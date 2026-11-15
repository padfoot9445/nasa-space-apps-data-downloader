from multiprocessing import Pool
import sys
from typing import Any, Iterable
from python_linq import Enumerable
from pathlib import Path
from .schema import Schema
from .schema_row import SchemaRow
from ..streaming_json_parser import stream_parse_buffer_by_characters


def validate_row_name(row: SchemaRow, object: dict[Any, Any]):
    for name in row.all_names:
        x = object.get(name)
        if x is not None:
            error = row.type_specifier.validate_data(
                x, object=lambda: str(object), name=lambda: name
            )
            return error
    if not row.optional:
        return row.type_specifier.validate_data(
            None, object=lambda: str(object), name=lambda: ",".join(row.all_names)
        )
    return None


def validate_object(object: dict[Any, Any]) -> Enumerable[str]:
    try:
        match object["instrument"]:
            case "VIIRS":
                schema = Schema.schemas.viirs
            case "MODIS":
                schema = Schema.schemas.modis
            case "OLI-2" | "SNPP" | "OLI":
                return Enumerable[str].empty()
            case x:
                return Enumerable([f"Unknown instrument specifier {x}"])
    except KeyError:
        return Enumerable([f"Could not access 'instrument' key on object {object}"])

    return (
        Enumerable(schema.rows)
        .map(lambda row: validate_row_name(row, object))
        .where(lambda x: x is not None)
        .cast(str)
    )


def process_file(path: Path):
    has_error = False
    for error in Enumerable(stream_parse_buffer_by_characters(path)).flat_map(
        validate_object
    ):
        print(error)
        has_error = True
    print(f"finished {path}!")
    return has_error


def _validate_object(object: dict[Any, Any]) -> Iterable[str]:
    return list(validate_object(object))


def main():
    with Pool() as p:
        has_error = False
        for error in (
            Enumerable(sys.argv[1:])
            .map(Path)
            .map(lambda x: Enumerable(stream_parse_buffer_by_characters(x)))
            .flat_map(
                lambda x: p.imap_unordered(_validate_object, x, chunksize=100_000 // 16)
            )
            .flatten()
        ):
            has_error = True
            print(error)
    if not has_error:
        print("No errors!!")


if __name__ == "__main__":
    main()
