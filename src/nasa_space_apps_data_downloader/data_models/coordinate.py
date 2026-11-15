from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class Coordinate:
    latitude: float
    longitude: float
