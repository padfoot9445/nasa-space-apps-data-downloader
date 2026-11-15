from enum import Enum
import enum


class SensorType(Enum):
    MODIS = enum.auto()
    VIIRS = enum.auto()
