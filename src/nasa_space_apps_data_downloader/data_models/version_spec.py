from dataclasses import dataclass
from functools import cache

from rustshed import Err, Ok, Result


@dataclass
class VersionSpec:
    version: float
    specifier: str | None

    @classmethod
    def try_parse(
        cls, version_spec: str, allowed_specifiers: frozenset[str]
    ) -> Result["VersionSpec", str]:
        specifier: str | None = None
        for allowed_specifier in cached_sort_specifiers(allowed_specifiers):
            if version_spec.endswith(allowed_specifier):
                version_spec = version_spec[: -len(allowed_specifier)]
                specifier = allowed_specifier

        try:
            version = float(version_spec)
        except ValueError as e:
            return Err(str(e))

        return Ok(VersionSpec(version, specifier))


@cache
def cached_sort_specifiers(specifiers: frozenset[str]) -> list[str]:
    return sorted(specifiers, key=len, reverse=True)
