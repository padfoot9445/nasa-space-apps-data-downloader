from dataclasses import dataclass
import datetime
from enum import StrEnum
from functools import cached_property
from typing import Any, Callable, cast
from ..version_spec import VersionSpec


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
