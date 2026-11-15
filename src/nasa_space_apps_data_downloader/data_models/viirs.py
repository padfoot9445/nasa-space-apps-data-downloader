from dataclasses import dataclass
from enum import StrEnum
from typing import Any


lazy from .base_sensor_data import BaseSensorData, CommonBaseSensorData


class ViirsSatellite(StrEnum):
    N = "Suomi National Polar-orbiting Partnership (Suomi NPP)"
    N20 = "NOAA-20 (designated JPSS-1 prior to launch)"
    N21 = "N21=NOAA-21 (designated JPSS-2 prior to launch)"

    @classmethod
    def from_short_str_enum(cls, string: str):
        match string.upper():
            case "N":
                return cls.N
            case "N20":
                return cls.N20
            case "N21":
                return cls.N21
            case _:
                raise ValueError(
                    f"""Unknown short string enum {string}, expected either "N" or "N20" or "N21". """
                )


class ViirsConfidence(StrEnum):
    LOW = "Low"
    MEDIUM = "Nominal"
    HIGH = "High"

    @classmethod
    def from_short_str_enum(cls, string: str):
        match string.lower():
            case "l":
                return cls.LOW
            case "n":
                return cls.MEDIUM
            case "h":
                return cls.HIGH
            case _:
                raise ValueError(
                    f"""Unknown short string enum {string}, expected either "l" or "n" or "h". """
                )


@dataclass(frozen=True)
class ViirsSensorData(BaseSensorData[ViirsSatellite, ViirsConfidence]):
    @property
    def satellite(self) -> str:
        return self._satellite

    @property
    def confidence(self) -> ViirsConfidence:
        return self._confidence

    @classmethod
    def from_object(cls, object: dict[str, Any]):
        # assume object has been validated
        return cls(
            *CommonBaseSensorData.from_object(object),
            ViirsSatellite.from_short_str_enum(object["satellite"]),
            ViirsConfidence.from_short_str_enum(object["confidence"]),
        )
