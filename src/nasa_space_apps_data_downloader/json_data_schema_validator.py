from dataclasses import dataclass
import datetime
from enum import Enum, StrEnum
import enum
from multiprocessing import Pool
import os
import sys
from typing import Any, Callable, cast
from python_linq import Enumerable
from pathlib import Path
from functools import cache, cached_property
from .streaming_json_parser import stream_parse_buffer_by_characters


class SchemaDataType(StrEnum):
    FLOAT = "float"  # not actually float, can be int too
    DATE = "date"
    TIME = "time"
    ENUM = "enum"
    PERCENTAGE = "percentage"
    VERSIONSPEC = "versionspec"


@dataclass(frozen=True)
class SchemaDataSpecifier:
    type: SchemaDataType
    type_args: str

    @cached_property
    def enum_values(self):
        return frozenset(self.type_args.split(","))

    def validate_data(self, data: Any, **context: Callable[[], str]) -> str | None:
        validation = self._validate_data(data)
        if validation is None:
            return validation

        context_string = "\n".join(
            f"\t{k}:\n{"\n".join(f"\t\t{l}" for l in v().splitlines())}"
            for k, v in context.items()
        )
        return f"{validation}\n{context_string}"

    def _validate_data(self, data: Any) -> str | None:
        match self.type:
            case SchemaDataType.FLOAT if not (
                isinstance(data, float) or isinstance(data, int)
            ):
                return f"Expected datatype of type float but got {data}"
            case SchemaDataType.DATE | SchemaDataType.TIME if not isinstance(data, str):
                return f"Expected datetime string but got {data} of type {type(data)}"
            case SchemaDataType.DATE:
                try:
                    datetime.date.strptime(cast(str, data), self.type_args)
                except ValueError as e:
                    return str(e)
            case SchemaDataType.TIME:
                try:
                    datetime.time.strptime(cast(str, data), self.type_args)
                except ValueError as e:
                    return str(e)
            case SchemaDataType.ENUM if (
                not isinstance(data, str) or data.strip() not in self.enum_values
            ):
                return f"Expected one of {self.enum_values} but got {data}"
            case SchemaDataType.PERCENTAGE:
                try:
                    x = int(data)
                    if not (0 <= x <= 100):
                        raise ValueError()
                except ValueError:
                    return (
                        f"Expected a stringified int-literal in [0..100] but got {data}"
                    )
            case SchemaDataType.VERSIONSPEC if not isinstance(data, str):
                return f"Expected string for data with type versionspec but got {data=}"
            case SchemaDataType.VERSIONSPEC if isinstance(
                (y := VersionSpec.try_parse(cast(str, data), self.enum_values)), str
            ):
                return y
            case z if z not in SchemaDataType:
                return f"Unexpected datatype specifier {z}, or code bug, {data=}"
            case _:
                return None


@cache
def cached_sort_specifiers(specifiers: frozenset[str]) -> list[str]:
    return sorted(specifiers, key=len, reverse=True)


@dataclass
class VersionSpec:
    version: float
    specifier: str | None

    @classmethod
    def try_parse(
        cls, version_spec: str, allowed_specifiers: frozenset[str]
    ) -> "VersionSpec | str":
        specifier: str | None = None
        for allowed_specifier in cached_sort_specifiers(allowed_specifiers):
            if version_spec.endswith(allowed_specifier):
                version_spec = version_spec[: -len(allowed_specifier)]
                specifier = allowed_specifier

        try:
            version = float(version_spec)
        except ValueError as e:
            return str(e)

        return VersionSpec(version, specifier)


@dataclass(frozen=True)
class SchemaRow:
    _name: str
    _alternatives: list[str]
    optional: bool
    type_specifier: SchemaDataSpecifier
    short_desc: str
    long_desc: str

    @cached_property
    def name(self) -> str:
        return self._name.lower()

    @cached_property
    def alternatives(self) -> list[str]:
        return Enumerable(self._alternatives).map(str.lower).to_list()

    @cached_property
    def all_names(self) -> list[str]:
        return [self.name] + self.alternatives

    @classmethod
    def from_config_row(cls, row: str):
        cells = Enumerable(row.split("\t")).map(str.strip)

        key_cell = cells.first()
        optional = False
        if key_cell.endswith("*"):
            optional = True
            key_cell = key_cell[:-1]

        names = key_cell.split("/")
        assert len(names) >= 1, f"expected one or more field names but got {key_cell}"
        name, *alternatives = names

        return SchemaRow(
            name,
            alternatives,
            optional,
            SchemaDataSpecifier(SchemaDataType(cells.first().strip()), cells.first()),
            cells.first_or_default(""),
            cells.first_or_default(""),
        )


class SensorType(Enum):
    MODIS = enum.auto()
    VIIRS = enum.auto()


@dataclass(frozen=True)
class Schema:
    sensor: SensorType
    rows: list[SchemaRow]

    @classmethod
    def from_path(cls, sensor: SensorType, path: Path):
        return Schema(
            sensor,
            Enumerable(path.read_text().splitlines())
            .filter(lambda x: not x.startswith("//"))
            .map(SchemaRow.from_config_row)
            .to_list(),
        )


cwd = os.getcwd()
os.chdir(Path(__file__).parent)
MODIS_SCHEMA = Schema.from_path(SensorType.MODIS, Path(r"config/fields/MODIS"))
VIIRS_SCHEMA = Schema.from_path(SensorType.VIIRS, Path(r"config/fields/VIIRS"))
os.chdir(cwd)


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
                schema = VIIRS_SCHEMA
            case "MODIS":
                schema = MODIS_SCHEMA
            case "OLI-2" | "SNPP":
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


def main():
    with Pool() as p:
        has_error = any(p.map(process_file, map(Path, sys.argv[1:])))
    if not has_error:
        print("No errors!!")


if __name__ == "__main__":
    main()
