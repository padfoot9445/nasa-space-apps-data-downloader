from abc import ABC, abstractmethod
from dataclasses import dataclass
import datetime
from functools import cached_property
from typing import Any, NamedTuple, Self, cast


lazy from .modis import ModisSensorData

lazy from .sensor_type import SensorType

lazy from .viirs import ViirsConfidence, ViirsSensorData

from ..config import Config
from .day_night import DayNight
from .coordinate import Coordinate
from .version_spec import VersionSpec


def tee[T](x: T) -> T:
    print(str(x))
    print(type(x))
    print(hash(x))
    return x


class CommonBaseSensorData(NamedTuple):
    coordinate: Coordinate
    brightness: float
    scan: float
    track: float
    acq_date: datetime.date
    acq_time: datetime.time
    version: VersionSpec
    bright_t31: float
    frp: float
    daynight: DayNight
    VALID_ROWS = {
        "latitude",
        "longitude",
        "brightness",
        "scan",
        "track",
        "acq_date",
        "acq_time",
        "version",
        "bright_t31",
        "frp",
        "daynight",
    }

    @classmethod
    def from_object(cls, object: dict[str, Any]) -> Self:
        args: dict[str, Any] = {
            row.name: Config.schema.common[row.name]
            .type_specifier.try_into_correct_type(object[row.name])
            .unwrap()
            for row in Config.schema.common.rows
            if row.name in cls.VALID_ROWS
        }
        coordinate = Coordinate(
            cast(float, args["latitude"]), cast(float, args["longitude"])
        )
        del args["latitude"]
        del args["longitude"]

        args["coordinate"] = coordinate
        return cls(**args)


@dataclass(frozen=True, slots=True)
class BaseSensorData[TSatellite = Any, TConfidence = Any](ABC):
    coordinate: Coordinate

    @property
    def latitude(self):
        return self.coordinate.latitude

    @property
    def longitude(self):
        return self.coordinate.longitude

    brightness: float
    scan: float
    track: float
    acq_date: datetime.date
    acq_time: datetime.time

    @cached_property
    def acquired_datetime(self):
        return datetime.datetime.combine(self.acq_date, self.acq_time)

    version: VersionSpec
    brightness_t31: float
    fire_radiative_power: float

    @property
    def frp(self):
        return self.fire_radiative_power

    day_night: DayNight
    _satellite: TSatellite

    @property
    @abstractmethod
    def satellite(self) -> str: ...

    _confidence: TConfidence

    @property
    @abstractmethod
    def confidence(self) -> ViirsConfidence: ...

    @classmethod
    def try_from_object(cls, object: dict[str, Any]):
        match SensorType.try_from_string(object["instrument"]):
            case SensorType.MODIS:
                return ModisSensorData.from_object(object)
            case SensorType.VIIRS:
                return ViirsSensorData.from_object(object)
            case x:
                return None
