from dataclasses import dataclass
from functools import cache
from pathlib import Path

from python_linq import Enumerable, TakeEnumerableException


from ..sensor_type import SensorType
from .schema_row import SchemaRow


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

    @cache
    def __getitem__(self, key: str) -> SchemaRow:
        try:
            return Enumerable(self.rows).where(lambda x: x.name == key).single()
        except TakeEnumerableException as e:
            raise ValueError(
                f"{key} was not a valid property for schema {self.sensor}"
            ) from e
