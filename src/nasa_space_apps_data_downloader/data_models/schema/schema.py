from dataclasses import dataclass
from functools import cached_property
from pathlib import Path
from typing import ClassVar

from python_linq import Enumerable

from ..._definitions import PROJECT_ROOT

from ..sensor_type import SensorType
from .schema_row import SchemaRow


class _SchemaAccessor:
    @cached_property
    def modis(self):
        return Schema.from_path(SensorType.MODIS, PROJECT_ROOT / r"config/schema/MODIS")

    @cached_property
    def viirs(self):
        return Schema.from_path(
            SensorType.VIIRS, PROJECT_ROOT / (r"config/schema/VIIRS")
        )


@dataclass(frozen=True)
class Schema:
    sensor: SensorType
    rows: list[SchemaRow]
    schemas: ClassVar[_SchemaAccessor] = _SchemaAccessor()

    @classmethod
    def from_path(cls, sensor: SensorType, path: Path):
        return Schema(
            sensor,
            Enumerable(path.read_text().splitlines())
            .filter(lambda x: not x.startswith("//"))
            .map(SchemaRow.from_config_row)
            .to_list(),
        )
