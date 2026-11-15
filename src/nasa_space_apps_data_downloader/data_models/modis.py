from dataclasses import dataclass
from enum import Enum, StrEnum, auto
from typing import Any, NewType

lazy from .viirs import ViirsConfidence

from ..config import Config

lazy from .base_sensor_data import BaseSensorData, CommonBaseSensorData


class ModisSatellite(StrEnum):
    AQUA = "Aqua"
    TERRA = "Terra"

    @classmethod
    def from_short_str_enum(cls, string: str):
        match string.upper():
            case "A" | "AQUA":
                return cls.AQUA
            case "T" | "TERRA":
                return cls.TERRA
            case _:
                raise ValueError(
                    f"""Unknown short string enum {string}, expected either "A" or "T". """
                )


class ModisInferredHotSpotType(Enum):
    VEGETATION_FIRE = auto()
    ACTIVE_VOLCANO = auto()
    OTHER_STATIC_LAND_SOURCE = auto()
    OFFSHORE = auto()

    @classmethod
    def from_short_str_enum(cls, string: str):
        match string:
            case "0":
                return cls.VEGETATION_FIRE
            case "1":
                return cls.ACTIVE_VOLCANO
            case "2":
                return cls.OTHER_STATIC_LAND_SOURCE
            case "3":
                return cls.OFFSHORE
            case _:
                raise ValueError(
                    f"""Unknown short string enum {string}, expected either "0", "1", "2", or "3"."""
                )


Percentage = NewType("Percentage", int)


@dataclass(frozen=True, slots=True)
class ModisSensorData(BaseSensorData[ModisSatellite, Percentage]):
    @property
    def satellite(self) -> str:
        return self._satellite

    @property
    def confidence(self) -> ViirsConfidence:
        if self._confidence <= 33:
            return ViirsConfidence.LOW
        elif self._confidence <= 66:
            return ViirsConfidence.MEDIUM
        else:
            return ViirsConfidence.HIGH

    type: ModisInferredHotSpotType | None

    @classmethod
    def from_object(cls, object: dict[str, Any]):
        # assume object has been validated
        return cls(
            *CommonBaseSensorData.from_object(object),
            ModisSatellite.from_short_str_enum(object["satellite"]),
            Config.schema.modis["confidence"]
            .type_specifier.try_into_correct_type(object["confidence"])
            .unwrap(),
            (
                ModisInferredHotSpotType.from_short_str_enum(object["type"])
                if object.get("type") is not None
                else None
            ),
        )
