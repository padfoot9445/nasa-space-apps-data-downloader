from dataclasses import dataclass
from functools import cached_property

from python_linq import Enumerable

from .schema_data_type import SchemaDataSpecifier, SchemaDataType


@dataclass(frozen=True)
class SchemaRow:
    _name: str
    _alternatives: list[str]
    optional: bool
    type_specifier: SchemaDataSpecifier
    short_desc: str
    long_desc: str

    @cached_property
    def name(self) -> str:
        return self._name.lower()

    @cached_property
    def alternatives(self) -> list[str]:
        return Enumerable(self._alternatives).map(str.lower).to_list()

    @cached_property
    def all_names(self) -> list[str]:
        return [self.name] + self.alternatives

    @classmethod
    def from_config_row(cls, row: str):
        cells = Enumerable(row.split("\t")).map(str.strip)

        key_cell = cells.first()
        optional = False
        if key_cell.endswith("*"):
            optional = True
            key_cell = key_cell[:-1]

        names = key_cell.split("/")
        assert len(names) >= 1, f"expected one or more field names but got {key_cell}"
        name, *alternatives = names

        return SchemaRow(
            name,
            alternatives,
            optional,
            SchemaDataSpecifier(SchemaDataType(cells.first().strip()), cells.first()),
            cells.first_or_default(""),
            cells.first_or_default(""),
        )
