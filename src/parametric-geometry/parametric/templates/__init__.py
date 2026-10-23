"""Template registry: name -> module with NAME, DESCRIPTION, PARAMS and build(parameters)."""

from __future__ import annotations

from ..errors import UNKNOWN_TEMPLATE, ParametricError
from ..model import ParametricModel
from . import assembly, spacecraft

TEMPLATES = {m.NAME: m for m in (spacecraft, assembly)}


def build(template: str, parameters) -> ParametricModel:
    module = TEMPLATES.get(template)
    if module is None:
        raise ParametricError(UNKNOWN_TEMPLATE, f"Unknown template '{template}'. Use one of: {', '.join(TEMPLATES)}.", "template")
    return module.build(parameters)


def describe() -> list[dict]:
    out = []
    for m in TEMPLATES.values():
        d = {"name": m.NAME, "description": m.DESCRIPTION, "parameters": [p.describe() for p in m.PARAMS]}
        if m is assembly:
            d["shapes"] = {k: {"fields": v[0], "description": v[1]} for k, v in assembly.SHAPES.items()}
        out.append(d)
    return out
