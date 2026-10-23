"""Reading and validating parameter dictionaries.

Every template declares its parameters as :class:`Param` entries. Unknown keys are refused, so a
typo never silently falls back to a default.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any

from .errors import INVALID_PARAMETER, UNSUPPORTED_THICKNESS, ParametricError

# Polystyrene (HIPS) sheet thicknesses sold for scratch-building.
PS_SHEET_THICKNESSES_MM: tuple[float, ...] = (0.3, 0.5, 0.8, 1.0, 1.5, 2.0, 3.0)


@dataclass(frozen=True)
class Param:
    name: str
    default: float | int
    minimum: float | int
    maximum: float | int
    unit: str = "mm"
    integer: bool = False
    sheet: bool = False  # value must be one of PS_SHEET_THICKNESSES_MM
    description: str = ""

    def describe(self) -> dict:
        d = {
            "name": self.name,
            "type": "integer" if self.integer else "number",
            "default": self.default,
            "minimum": self.minimum,
            "maximum": self.maximum,
            "unit": self.unit,
            "description": self.description,
        }
        if self.sheet:
            d["allowed"] = list(PS_SHEET_THICKNESSES_MM)
        return d


def sheet_thickness(value: Any, field: str) -> float:
    v = number(value, field)
    for allowed in PS_SHEET_THICKNESSES_MM:
        if abs(v - allowed) < 1e-9:
            return allowed
    raise ParametricError(
        UNSUPPORTED_THICKNESS,
        f"{field} must be one of the PS sheet thicknesses {', '.join(f'{t:g}' for t in PS_SHEET_THICKNESSES_MM)} mm (got {v:g}).",
        field,
    )


def number(value: Any, field: str) -> float:
    # bool is an int subclass in Python; true/false is never a size.
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise ParametricError(INVALID_PARAMETER, f"{field} must be a number.", field)
    v = float(value)
    if not math.isfinite(v):
        raise ParametricError(INVALID_PARAMETER, f"{field} must be finite.", field)
    return v


def read(params: Any, spec: list[Param], prefix: str = "") -> dict[str, float | int]:
    """Validates ``params`` against ``spec``; returns every parameter with defaults filled in."""
    if params is None:
        params = {}
    if not isinstance(params, dict):
        raise ParametricError(INVALID_PARAMETER, "parameters must be an object.", prefix.rstrip(".") or "parameters")
    known = {p.name for p in spec}
    for key in params:
        if key not in known:
            raise ParametricError(INVALID_PARAMETER, f"Unknown parameter '{key}'.", prefix + str(key))

    out: dict[str, float | int] = {}
    for p in spec:
        field = prefix + p.name
        raw = params.get(p.name, p.default)
        if p.sheet:
            out[p.name] = sheet_thickness(raw, field)
            continue
        v = number(raw, field)
        if p.integer:
            if v != int(v):
                raise ParametricError(INVALID_PARAMETER, f"{field} must be a whole number.", field)
            v = int(v)
        if v < p.minimum or v > p.maximum:
            raise ParametricError(INVALID_PARAMETER, f"{field} must be between {p.minimum:g} and {p.maximum:g} {p.unit}.".rstrip(), field)
        out[p.name] = v
    return out


def require(condition: bool, message: str, field: str, code: str = INVALID_PARAMETER) -> None:
    if not condition:
        raise ParametricError(code, message, field)
