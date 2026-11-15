from functools import cached_property
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

    schema = _SchemaAccessor()
