"""Worker contract: a JSON request in, files plus a JSON response out.

Request (schemaVersion 1)::

    {"schemaVersion": 1, "template": "spacecraft", "parameters": {...},
     "exports": ["glb", "stl", "obj", "bom", "templates", "views"]}   # exports optional, default all

The response lists every written file by path relative to the output directory, with its size and
SHA-256, plus dimensions, per-component validation and issues. Large data never goes through JSON.
Only file names built here are written, and only inside the output directory, which must be new or
empty. Nothing is executed.
"""

from __future__ import annotations

import hashlib
from pathlib import Path

from . import ENGINE_NAME, ENGINE_VERSION, SCHEMA_VERSION, bom, cutting, drawing, meshio, views
from .errors import INTERNAL_ERROR, INVALID_OUTPUT_DIRECTORY, INVALID_REQUEST, LIMIT_EXCEEDED, ParametricError
from .model import ParametricModel, box_dict, inspect
from .params import PS_SHEET_THICKNESSES_MM
from .templates import build as build_template
from .templates import describe as describe_templates

EXPORTS = ("glb", "stl", "obj", "bom", "templates", "views")
MAX_TRIANGLES = 2_000_000
ORIGIN = "parametric"  # geometry origin in the shared result contract


def describe() -> dict:
    return {
        "schemaVersion": SCHEMA_VERSION,
        "engine": {"name": ENGINE_NAME, "version": ENGINE_VERSION},
        "origin": ORIGIN,
        "units": "mm",
        "upAxis": "y",
        "sheetThicknessesMm": list(PS_SHEET_THICKNESSES_MM),
        "exports": list(EXPORTS),
        "templates": describe_templates(),
    }


def _request(req) -> tuple[str, dict, list[str]]:
    if not isinstance(req, dict):
        raise ParametricError(INVALID_REQUEST, "The request must be a JSON object.", None)
    for key in req:
        if key not in ("schemaVersion", "template", "parameters", "exports"):
            raise ParametricError(INVALID_REQUEST, f"Unknown request field '{key}'.", key)
    if req.get("schemaVersion") != SCHEMA_VERSION:
        raise ParametricError(INVALID_REQUEST, f"schemaVersion must be {SCHEMA_VERSION}.", "schemaVersion")
    template = req.get("template")
    if not isinstance(template, str):
        raise ParametricError(INVALID_REQUEST, "template is required.", "template")
    exports = req.get("exports", list(EXPORTS))
    if not isinstance(exports, list) or any(e not in EXPORTS for e in exports):
        raise ParametricError(INVALID_REQUEST, f"exports must be a list of: {', '.join(EXPORTS)}.", "exports")
    return template, req.get("parameters") or {}, [e for e in EXPORTS if e in exports]


def _output_dir(path: Path) -> Path:
    path = path.resolve()
    if path.exists() and (not path.is_dir() or any(path.iterdir())):
        raise ParametricError(INVALID_OUTPUT_DIRECTORY, "The output directory must not exist yet or be empty.", "output")
    path.mkdir(parents=True, exist_ok=True)
    return path


class _Writer:
    def __init__(self, root: Path):
        self.root = root
        self.outputs: list[dict] = []

    def write(self, rel: str, kind: str, data: bytes | str, **extra) -> None:
        data = data.encode("utf-8") if isinstance(data, str) else data
        target = (self.root / rel).resolve()
        if self.root not in target.parents:
            raise RuntimeError("output path escapes the output directory")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        self.outputs.append({"kind": kind, "path": rel, "bytes": len(data), "sha256": hashlib.sha256(data).hexdigest(), **extra})


def generate(req, output: Path) -> dict:
    """Builds and exports the model; raises ParametricError for anything the caller must fix."""
    template, parameters, exports = _request(req)
    model = build_template(template, parameters)
    triangles = sum(len(c.mesh.faces) for c in model.components)
    if triangles > MAX_TRIANGLES:
        raise ParametricError(LIMIT_EXCEEDED, f"The model would have {triangles} triangles (limit {MAX_TRIANGLES}); use fewer segments or components.", "parameters")

    reports = [inspect(c.mesh) for c in model.components]
    invalid = [c.name for c, r in zip(model.components, reports) if not r["valid"] or r["bodies"] != c.quantity]
    if invalid:
        # Generated geometry must always be closed; this is a bug, never a user error.
        raise RuntimeError(f"generated components are not valid solids: {', '.join(invalid)}")

    root = _output_dir(output)
    w = _Writer(root)
    items = bom.items(model.components)
    template_files: dict[int, str] = {}

    if "glb" in exports:
        w.write("model.glb", "glb", meshio.to_glb(model), units="m", upAxis="y")
    if "stl" in exports:
        w.write("model.stl", "stl", meshio.to_stl(model), units="mm", upAxis="z")
    if "obj" in exports:
        w.write("model.obj", "obj", meshio.to_obj(model), units="mm", upAxis="y")
    if "templates" in exports:
        pages = []
        for it in (i for i in items if i.first.pattern is not None):
            page = cutting.template_page(it, model.template, model.issues)
            pages.append(page)
            w.write(f"templates/{it.slug}.svg", "template_svg", drawing.to_svg(page), item=it.number, page=page.page_name, scale="1:1")
            w.write(f"templates/{it.slug}.dxf", "template_dxf", cutting.template_dxf(it), item=it.number, scale="1:1")
            template_files[it.number] = f"templates/{it.slug}.svg"
        if pages:
            w.write("templates/templates.pdf", "templates_pdf", drawing.to_pdf(pages), pages=len(pages), scale="1:1")
    if "views" in exports:
        sheet, n = views.build(model, model.derived.get("sectionXMm"))
        w.write("views/views.svg", "views_svg", drawing.to_svg(sheet), scale=f"1:{n}")
        w.write("views/views.pdf", "views_pdf", drawing.to_pdf([sheet]), scale=f"1:{n}")
    if "bom" in exports:
        w.write("bom.csv", "bom", bom.to_csv(items, template_files))

    return _response(model, reports, items, w.outputs)


def _response(model: ParametricModel, reports, items, outputs) -> dict:
    item_of = {c.name: it.number for it in items for c in it.components}
    comps = []
    for c, r in zip(model.components, reports):
        comps.append({
            "name": c.name, "role": c.role, "category": c.category, "material": c.material,
            "thicknessMm": c.thickness_mm, "quantity": c.quantity, "bomItem": item_of[c.name],
            "pattern": c.pattern.kind if c.pattern else None, "forming": c.forming,
            "sizeMm": c.size_mm, "validation": r,
        })
    lo, hi = model.bounds()
    return {
        "schemaVersion": SCHEMA_VERSION,
        "status": "succeeded",
        "engine": {"name": ENGINE_NAME, "version": ENGINE_VERSION},
        "origin": ORIGIN,
        "template": model.template,
        "units": "mm",
        "upAxis": "y",
        "scale": {"status": "exact", "mmPerUnit": 1.0},
        "parameters": model.parameters,
        "derived": model.derived,
        "dimensions": box_dict([lo, hi]),
        "validation": {
            "valid": all(r["valid"] for r in reports),
            "components": len(reports),
            "triangles": sum(r["triangleCount"] for r in reports),
            "closedComponents": sum(1 for r in reports if r["closed"]),
        },
        "components": comps,
        "bom": [{"item": it.number, "role": it.first.role, "quantity": it.quantity, "components": [c.name for c in it.components]} for it in items],
        "outputs": outputs,
        "issues": [i.to_dict() for i in model.issues],
        "error": None,
    }


def failure(error: ParametricError | Exception) -> dict:
    if isinstance(error, ParametricError):
        err = error.to_dict()
    else:
        err = {"code": INTERNAL_ERROR, "message": "The geometry engine failed; see the worker log.", "field": None}
    return {
        "schemaVersion": SCHEMA_VERSION,
        "status": "failed",
        "engine": {"name": ENGINE_NAME, "version": ENGINE_VERSION},
        "origin": ORIGIN,
        "outputs": [],
        "issues": [],
        "error": err,
    }
