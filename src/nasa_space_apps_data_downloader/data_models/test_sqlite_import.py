from datetime import date, time
import pickle
import sqlite3

import pytest

from .coordinate import Coordinate
from .modis import ModisInferredHotSpotType, ModisSatellite, ModisSensorData
from .sqlite_import import epoch_microseconds, import_pickle, observation_values
from .version_spec import VersionSpec
from .viirs import ViirsConfidence, ViirsSatellite, ViirsSensorData


def _models() -> list[ModisSensorData | ViirsSensorData]:
    shared = (
        Coordinate(38.1, -122.2),
        310.5,
        0.4,
        0.5,
        date(2024, 4, 5),
        time(12, 34),
        VersionSpec(2.0, "NRT"),
        290.0,
        1.25,
        "D",
    )
    return [
        ModisSensorData(
            *shared,
            ModisSatellite.TERRA,
            67,
            ModisInferredHotSpotType.VEGETATION_FIRE,
        ),
        ViirsSensorData(
            *shared,
            ViirsSatellite.N20,
            ViirsConfidence.MEDIUM,
        ),
        ModisSensorData(
            *shared,
            ModisSatellite.AQUA,
            10,
            None,
        ),
    ]


def test_epoch_microseconds_uses_exact_utc_arithmetic():
    assert epoch_microseconds(date(1970, 1, 1), time()) == 0
    assert epoch_microseconds(date(1970, 1, 1), time(0, 0, 1, 123456)) == 1_123_456


def test_observation_values_preserve_discriminated_model_state():
    modis, viirs, _ = _models()
    modis_values = observation_values(modis, 3, 5)
    viirs_values = observation_values(viirs, 3, 6)

    assert modis_values[1:4] == (5, "MODIS", "TERRA")
    assert modis_values[15:] == (67, "VEGETATION_FIRE", None)
    assert viirs_values[1:4] == (6, "VIIRS", "N20")
    assert viirs_values[15:] == (None, None, "MEDIUM")


def test_import_creates_typed_rows_rtree_and_generated_confidence(tmp_path):
    pickle_path = tmp_path / "models.pickle"
    database_path = tmp_path / "observations.sqlite"
    with pickle_path.open("wb") as file:
        pickle.dump(_models(), file)

    assert import_pickle(pickle_path, database_path, batch_size=2) == 3

    with sqlite3.connect(database_path) as connection:
        rows = connection.execute(
            """
            SELECT instrument, satellite, modis_confidence_percent, modis_hotspot_type,
                   viirs_confidence, confidence_class
            FROM observations
            ORDER BY source_record_number
            """
        ).fetchall()
        rtree_count = connection.execute(
            "SELECT COUNT(*) FROM observation_rtree"
        ).fetchone()[0]
        source = connection.execute(
            "SELECT record_count, accepted_count FROM source_files"
        ).fetchone()

    assert rows == [
        ("MODIS", "TERRA", 67, "VEGETATION_FIRE", None, "High"),
        ("VIIRS", "N20", None, None, "MEDIUM", "Nominal"),
        ("MODIS", "AQUA", 10, None, None, "Low"),
    ]
    assert rtree_count == 3
    assert source == (3, 3)

    with sqlite3.connect(database_path) as connection:
        observation_id = connection.execute(
            "SELECT observation_id FROM observations ORDER BY observation_id LIMIT 1"
        ).fetchone()[0]
        connection.execute(
            "UPDATE observations SET latitude = 38.2, longitude = -122.3 "
            "WHERE observation_id = ?",
            (observation_id,),
        )
        bounds = connection.execute(
            """
            SELECT min_longitude, max_longitude, min_latitude, max_latitude
            FROM observation_rtree WHERE observation_id = ?
            """,
            (observation_id,),
        ).fetchone()
        assert bounds[0] <= -122.3 <= bounds[1]
        assert bounds[2] <= 38.2 <= bounds[3]

        connection.execute(
            "DELETE FROM observations WHERE observation_id = ?", (observation_id,)
        )
        assert connection.execute(
            "SELECT 1 FROM observation_rtree WHERE observation_id = ?",
            (observation_id,),
        ).fetchone() is None

        try:
            connection.execute(
                "UPDATE observations SET instrument = 'MODIS' "
                "WHERE observation_id = 2"
            )
        except sqlite3.IntegrityError:
            pass
        else:
            raise AssertionError("Discriminator constraints accepted invalid fields.")


def test_import_rejects_nonpositive_batch_size(tmp_path):
    with pytest.raises(ValueError, match="batch_size"):
        import_pickle(
            tmp_path / "source.pickle",
            tmp_path / "database.sqlite",
            batch_size=0,
        )
