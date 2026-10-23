"""1:1 cutting templates (SVG/PDF pages and DXF) for every sheet item with a pattern."""

from __future__ import annotations

import io

import ezdxf
from shapely.geometry import Polygon

from . import ENGINE_NAME, ENGINE_VERSION
from .bom import BomItem
from .drawing import ANNOTATION, CUT, Drawing, smallest_page
from .model import DEVELOPED, Issue

MARGIN = 10.0
TITLE_HEIGHT = 30.0
BAR_SPACE = 14.0
CUT_STROKE = 0.2


def _rings(outline: Polygon):
    return [list(outline.exterior.coords)[:-1]] + [list(r.coords)[:-1] for r in outline.interiors]


def template_page(item: BomItem, template: str, issues: list[Issue]) -> Drawing:
    c = item.first
    outline = c.pattern.outline
    minx, miny, maxx, maxy = outline.bounds
    w, h = maxx - minx, maxy - miny
    need_w = max(w, 110.0) + 2 * MARGIN
    need_h = h + 2 * MARGIN + BAR_SPACE + TITLE_HEIGHT
    page = smallest_page(need_w, need_h)
    if page is None:
        name, pw, ph = "custom (larger than A3)", need_w, need_h
        issues.append(Issue("template_too_large", "warning", f"Item {item.number} ({c.role}) is larger than A3 at 1:1; its page is {pw:.0f} x {ph:.0f} mm (plotter or tiled print needed). The DXF is unaffected.", c.name))
    else:
        name, pw, ph = page
    d = Drawing(pw, ph, f"Item {item.number} {c.role} - cutting template 1:1", name)

    # Pattern (u, v) with v up -> page (x, y) with y down; not mirrored.
    ox, oy = MARGIN - minx, MARGIN + maxy
    for ring in _rings(outline):
        d.line([(ox + u, oy - v) for u, v in ring], CUT, CUT_STROKE, closed=True)

    y = MARGIN + h + 8
    d.check_bar(MARGIN, y)
    y = ph - MARGIN - TITLE_HEIGHT + 6
    d.line([(MARGIN, y - 5), (pw - MARGIN, y - 5)], ANNOTATION, 0.25)
    holes = len(outline.interiors)
    lines = [
        f"Item {item.number}: {c.role}   Qty {item.quantity}   {c.material} {c.thickness_mm:g} mm sheet",
        f"Pattern {w:.2f} x {h:.2f} mm" + (f", {holes} opening(s)" if holes else "") + "   Scale 1:1   Units mm",
        c.pattern.note if c.pattern.kind == DEVELOPED else "Cut on the red lines. Sand to the line.",
        f"Parts: {' '.join(x.name for x in item.components)}"[:150],
        f"{ENGINE_NAME} {ENGINE_VERSION}, template '{template}'. Parametrically generated: exact to the entered dimensions.",
    ]
    for i, text in enumerate(lines):
        d.text(text, MARGIN, y + i * 4.6, 3.2 if i == 0 else 2.6)
    return d


def template_dxf(item: BomItem) -> str:
    """DXF R2000, millimetres, 1:1: layer CUT holds only closed contours, ANNOTATION the label."""
    c = item.first
    previous = ezdxf.options.write_fixed_meta_data_for_testing
    ezdxf.options.write_fixed_meta_data_for_testing = True  # fixed dates and GUIDs: reproducible files
    try:
        doc = ezdxf.new("R2000", setup=False)
        doc.units = ezdxf.units.MM
        doc.header["$MEASUREMENT"] = 1
        doc.layers.add("CUT", color=1)
        doc.layers.add("ANNOTATION", color=7)
        msp = doc.modelspace()
        for ring in _rings(c.pattern.outline):
            msp.add_lwpolyline([(round(u, 6), round(v, 6)) for u, v in ring], close=True, dxfattribs={"layer": "CUT"})
        minx, miny, _, _ = c.pattern.outline.bounds
        label = f"Item {item.number} {c.role} Qty {item.quantity} {c.material} {c.thickness_mm:g} mm"
        msp.add_text(label, height=2.5, dxfattribs={"layer": "ANNOTATION", "insert": (round(minx, 6), round(miny - 5, 6))})
        out = io.StringIO()
        doc.write(out)
        return out.getvalue()
    finally:
        ezdxf.options.write_fixed_meta_data_for_testing = previous
