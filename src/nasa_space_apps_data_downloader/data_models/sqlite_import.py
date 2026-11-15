import argparse
from contextlib import closing
from datetime import date, datetime, time, timezone
import hashlib
import pickle
from pathlib import Path
import sqlite3
import sys
from typing import Iterable

from ..config import Config
from .base_sensor_data import BaseSensorData
from .load_all_data import DataAccessor
from .modis import ModisSensorData
from .viirs import ViirsSensorData

SCHEMA_VERSION = 1
IMPORTER_VERSION = "1"
BATCH_SIZE = 10_000
EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)
INSERT_OBSERVATION = """
INSERT INTO observations (
    source_file_id,
    source_record_number,
    instrument,
    satellite,
    acquired_at_utc_us,
    latitude,
    longitude,
    brightness,
    scan,
    track,
    version_number,
    version_specifier,
    brightness_t31,
    fire_radiative_power,
    day_night,
    modis_confidence_percent,
    modis_hotspot_type,
    viirs_confidence
) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
"""


def epoch_microseconds(acq_date: date, acq_time: time) -> int:
    value = datetime.combine(acq_date, acq_time).replace(tzinfo=timezone.utc)
    delta = value - EPOCH
    return ((delta.days * 86_400 + delta.seconds) * 1_000_000) + delta.microseconds


def observation_values(
    observation: BaseSensorData, source_file_id: int, record_number: int
) -> tuple[object, ...]:
    if isinstance(observation, ModisSensorData):
        instrument = "MODIS"
        satellite = observation._satellite.name
        modis_confidence = int(observation._confidence)
        modis_type = observation.type.name if observation.type is not None else None
        viirs_confidence = None
    elif isinstance(observation, ViirsSensorData):
        instrument = "VIIRS"
        satellite = observation._satellite.name
        modis_confidence = None
        modis_type = None
        viirs_confidence = observation._confidence.name
    else:
        raise TypeError(f"Unsupported observation model: {type(observation).__qualname__}")

    version = observation.version
    day_night = getattr(observation.day_night, "value", observation.day_night)
    if day_night not in ("D", "N"):
        raise ValueError(f"Unsupported day/night value: {day_night!r}")

    return (
        source_file_id,
        record_number,
        instrument,
        satellite,
        epoch_microseconds(observation.acq_date, observation.acq_time),
        observation.coordinate.latitude,
        observation.coordinate.longitude,
        observation.brightness,
        observation.scan,
        observation.track,
        version.version,
        version.specifier,
        observation.brightness_t31,
        observation.fire_radiative_power,
        day_night,
        modis_confidence,
        modis_type,
        viirs_confidence,
    )


def load_pickle(path: Path) -> list[BaseSensorData]:
    resolved = path.resolve()
    if resolved == Config.pickle_data_path.resolve():
        records = DataAccessor.pickled_data()
    else:
        with resolved.open("rb") as file:
            records = pickle.load(file)
    if not isinstance(records, list):
        raise TypeError(f"Expected a list in pickle {resolved}, got {type(records).__name__}")
    return records


def initialize_schema(connection: sqlite3.Connection) -> None:
    current_version = connection.execute("PRAGMA user_version").fetchone()[0]
    if current_version > SCHEMA_VERSION:
        raise RuntimeError(
            f"Database schema version {current_version} is newer than importer "
            f"version {SCHEMA_VERSION}."
        )
    schema_path = Path(__file__).resolve().parents[3] / "sqlite" / "schema.sql"
    connection.executescript(schema_path.read_text(encoding="utf-8"))
    connection.execute("PRAGMA foreign_keys = ON")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as file:
        for chunk in iter(lambda: file.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def import_pickle(
    pickle_path: Path,
    database_path: Path,
    *,
    batch_size: int = BATCH_SIZE,
) -> int:
    if batch_size < 1:
        raise ValueError("batch_size must be greater than zero.")

    source = pickle_path.resolve()
    target = database_path.resolve()
    target.parent.mkdir(parents=True, exist_ok=True)
    records = load_pickle(source)
    source_hash = sha256_file(source)
    imported_at_time = datetime.now(timezone.utc)
    imported_at = epoch_microseconds(
        imported_at_time.date(), imported_at_time.time()
    )

    with closing(sqlite3.connect(target)) as connection:
        connection.execute("PRAGMA foreign_keys = ON")
        connection.execute("PRAGMA journal_mode = WAL")
        initialize_schema(connection)
        existing_count = connection.execute(
            "SELECT COUNT(*) FROM observations"
        ).fetchone()[0]
        if existing_count:
            raise RuntimeError(
                f"Database already contains {existing_count:,} observations; "
                "refusing to import duplicates."
            )

        with connection:
            cursor = connection.execute(
                """
                INSERT INTO source_files (
                    source_path, source_name, product, sha256, imported_at_utc_us,
                    importer_version, schema_version, record_count, accepted_count
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    str(source),
                    source.name,
                    "python-model-pickle",
                    source_hash,
                    imported_at,
                    IMPORTER_VERSION,
                    SCHEMA_VERSION,
                    len(records),
                    len(records),
                ),
            )
            source_file_id = cursor.lastrowid
            batch: list[tuple[object, ...]] = []
            for record_number, record in enumerate(records, start=1):
                if not isinstance(record, BaseSensorData):
                    raise TypeError(
                        f"Record {record_number} is not a BaseSensorData model: "
                        f"{type(record).__qualname__}"
                    )
                batch.append(
                    observation_values(record, source_file_id, record_number)
                )
                if len(batch) >= batch_size:
                    connection.executemany(INSERT_OBSERVATION, batch)
                    batch.clear()

            if batch:
                connection.executemany(INSERT_OBSERVATION, batch)

    return len(records)


def parse_args(argv: Iterable[str] | None = None) -> argparse.Namespace:
    repo_root = Path(__file__).resolve().parents[3]
    parser = argparse.ArgumentParser(
        description="Import the established Python model pickle into SQLite."
    )
    parser.add_argument(
        "--pickle",
        type=Path,
        default=Config.pickle_data_path,
        help="Pickle containing the existing list of typed sensor models.",
    )
    parser.add_argument(
        "--database",
        type=Path,
        default=repo_root / "data" / "fire-observations.sqlite",
        help="Output SQLite database path.",
    )
    return parser.parse_args(argv)


def main(argv: Iterable[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        count = import_pickle(args.pickle, args.database)
    except (OSError, sqlite3.Error, TypeError, ValueError, RuntimeError) as error:
        print(f"SQLite import failed: {error}", file=sys.stderr)
        return 1

    print(
        f"Imported {count:,} model records from {args.pickle} into {args.database}."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
