"""Bill of materials: identical components are grouped into one item with a quantity."""

from __future__ import annotations

import csv
import io
from dataclasses import dataclass

from .model import Component


@dataclass
class BomItem:
    number: int
    components: list[Component]

    @property
    def first(self) -> Component:
        return self.components[0]

    @property
    def quantity(self) -> int:
        return sum(c.quantity for c in self.components)

    @property
    def slug(self) -> str:
        return f"item{self.number:02d}_{self.first.role}"


def _key(c: Component) -> tuple:
    pattern = None
    if c.pattern is not None:
        minx, miny, _, _ = c.pattern.outline.bounds
        # Shape relative to its own corner, rounded: two panels cut from the same template group together.
        rings = [c.pattern.outline.exterior, *c.pattern.outline.interiors]
        pattern = (c.pattern.kind, tuple(tuple((round(x - minx, 4), round(y - miny, 4)) for x, y in r.coords) for r in rings))
    return (c.role, c.category, c.material, c.thickness_mm, c.forming, tuple(sorted(c.size_mm.items())), pattern)


def items(components: list[Component]) -> list[BomItem]:
    groups: dict[tuple, BomItem] = {}
    for c in components:
        k = _key(c)
        if k not in groups:
            groups[k] = BomItem(len(groups) + 1, [])
        groups[k].components.append(c)
    return list(groups.values())


def _text(value: str | None) -> str:
    # Spreadsheet formula injection guard, as in the application's CSV exports.
    if not value:
        return ""
    return "'" + value if value[0] in "=+-@\t\r" else value


def _num(value: float | None) -> str:
    return "" if value is None else f"{value:.4f}".rstrip("0").rstrip(".")


def to_csv(bom: list[BomItem], template_files: dict[int, str] | None = None) -> bytes:
    buf = io.StringIO()
    w = csv.writer(buf, lineterminator="\r\n")
    w.writerow(["item", "quantity", "role", "category", "material", "thickness_mm", "dimensions_mm", "pattern", "pattern_size_mm", "forming", "template", "components", "notes"])
    for it in bom:
        c = it.first
        dims = "; ".join(f"{k}={_num(v)}" for k, v in c.size_mm.items())
        if c.pattern is not None:
            minx, miny, maxx, maxy = c.pattern.outline.bounds
            psize = f"{_num(maxx - minx)} x {_num(maxy - miny)}"
        else:
            psize = ""
        notes = " ".join(dict.fromkeys(n for comp in it.components for n in comp.notes))
        if c.pattern is not None and c.pattern.note:
            notes = (c.pattern.note + " " + notes).strip()
        w.writerow([
            it.number, it.quantity, _text(c.role), _text(c.category), _text(c.material), _num(c.thickness_mm), _text(dims),
            c.pattern.kind if c.pattern else "", psize, c.forming or "", (template_files or {}).get(it.number, ""),
            _text(" ".join(x.name for x in it.components)), _text(notes),
        ])
    return ("﻿" + buf.getvalue()).encode("utf-8")
