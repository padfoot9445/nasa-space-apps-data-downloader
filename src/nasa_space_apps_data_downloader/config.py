from functools import cache, cached_property
from pathlib import Path
from typing import Self

lazy from .data_models.schema import Schema
lazy from .data_models import SensorType

PROJECT_ROOT = Path(__file__).resolve().parent


class Config:
    class _SchemaAccessor:
        _instance: "Self | None" = None

        def __new__(cls):
            if cls._instance is None:
                cls._instance = super(cls, cls).__new__(cls)
            return cls._instance

        @cached_property
        def modis(self):
            return Schema.from_path(
                SensorType.MODIS, PROJECT_ROOT / r"config/schema/MODIS"
            )

        @cached_property
        def viirs(self):
            return Schema.from_path(
                SensorType.VIIRS, PROJECT_ROOT / (r"config/schema/VIIRS")
            )

        @cached_property
        def common(self):
            return Schema.common(self.modis, self.viirs)

        @cached_property
        def ignored(self):
            return frozenset(
                (PROJECT_ROOT / r"config/IGNORED_SCHEMA").read_text().splitlines()
            )

        @cache
        def __getitem__(self, key: SensorType):
            match key:
                case SensorType.MODIS:
                    return self.modis
                case SensorType.VIIRS:
                    return self.viirs
                case SensorType.COMBINED:
                    return self.common
                case _:
                    raise ValueError(f"Unknown key {key}")

        @cache
        def get(self, key: str):
            if key in self.ignored:
                return None
            else:
                try:
                    return self[SensorType.from_string(key)]
                except ValueError:
                    return None

    schema = _SchemaAccessor()
