from enum import Flag
import enum


class SensorType(Flag):
    MODIS = enum.auto()
    VIIRS = enum.auto()
    COMBINED = enum.auto()

    @classmethod
    def try_from_string(cls, string: str):
        match string.upper():
            case "MODIS":
                return cls.MODIS
            case "VIIRS":
                return cls.VIIRS
            case _:
                return None
