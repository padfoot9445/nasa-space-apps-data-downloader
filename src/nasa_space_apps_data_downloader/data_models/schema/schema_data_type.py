from dataclasses import dataclass
import datetime
from enum import StrEnum
from functools import cached_property
from typing import Any, Callable, cast

from rustshed import Ok, Err, Result
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
        validation = self.try_into_correct_type(data)
        if validation.is_ok():
            return None

        context_string = "\n".join(
            f"\t{k}:\n{"\n".join(f"\t\t{l}" for l in v().splitlines())}"
            for k, v in context.items()
        )
        return f"{validation.unwrap_err()}\n{context_string}"

    def try_into_correct_type(
        self, data: Any
    ) -> Result[float | datetime.date | datetime.time | str | int | VersionSpec, str]:
        match self.type:
            case SchemaDataType.FLOAT:
                if not (isinstance(data, float) or isinstance(data, int)):
                    return Err(f"Expected datatype of type float but got {data}")
                else:
                    return Ok(float(data))

            case SchemaDataType.DATE | SchemaDataType.TIME if not isinstance(data, str):
                return Err(
                    f"Expected datetime string but got {data} of type {type(data)}"
                )
            case SchemaDataType.DATE:
                try:
                    return Ok(datetime.date.strptime(cast(str, data), self.type_args))
                except ValueError as e:
                    return Err(str(e))
            case SchemaDataType.TIME:
                try:
                    return Ok(datetime.time.strptime(cast(str, data), self.type_args))
                except ValueError as e:
                    return Err(str(e))
            case SchemaDataType.ENUM:
                if not isinstance(data, str) or data.strip() not in self.enum_values:
                    return Err(f"Expected one of {self.enum_values} but got {data}")
                else:
                    return Ok(data)
            case SchemaDataType.PERCENTAGE:
                try:
                    x = int(data)
                    if not (0 <= x <= 100):
                        raise ValueError()
                except ValueError:
                    return Err(
                        f"Expected a stringified int-literal in [0..100] but got {data}"
                    )
                else:
                    return Ok(x)
            case SchemaDataType.VERSIONSPEC:
                if not isinstance(data, str):
                    return Err(
                        f"Expected string for data with type versionspec but got {data=}"
                    )

                return VersionSpec.try_parse(data, self.enum_values)
