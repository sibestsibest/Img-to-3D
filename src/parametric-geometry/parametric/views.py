"""Orthographic front, side and top views plus one section, on one A3 sheet at a standard scale.

Each component is drawn as its projected outline (the union of its projected triangles), so the
outlines are exact for the generated geometry. Hidden lines are not removed: outlines of parts
behind others are drawn too.
"""

from __future__ import annotations

import numpy as np
import shapely
import trimesh
from shapely.geometry import MultiPolygon, Polygon
from trimesh.intersections import mesh_plane

from . import ENGINE_NAME, ENGINE_VERSION
from .drawing import ANNOTATION, VIEW, Drawing
from .model import ParametricModel

SCALES = [1, 2, 5, 10, 20, 50, 100]  # 1:N
PAGE = ("A3 landscape", 420.0, 297.0)
MARGIN, GAP, TITLE = 12.0, 22.0, 26.0

# view -> (page-x axis, page-y-down axis) as model directions (Y-up model, length along X).
# side: from +Z, X right, Y up.  top: from +Y, X right, Z down.  front: from +X, -Z right, Y up.
PROJECTIONS = {
    "side": (np.array([1.0, 0, 0]), np.array([0, -1.0, 0])),
    "top": (np.array([1.0, 0, 0]), np.array([0, 0, 1.0])),
    "front": (np.array([0, 0, -1.0]), np.array([0, -1.0, 0])),
}


def _outline(mesh: trimesh.Trimesh, ax: np.ndarray, ay: np.ndarray):
    tri = mesh.triangles
    p = np.stack([tri @ ax, tri @ ay], axis=-1)  # (n, 3, 2)
    area = 0.5 * np.abs((p[:, 1, 0] - p[:, 0, 0]) * (p[:, 2, 1] - p[:, 0, 1]) - (p[:, 2, 0] - p[:, 0, 0]) * (p[:, 1, 1] - p[:, 0, 1]))
    polys = [Polygon(t) for t in p[area > 1e-9]]
    if not polys:
        return []
    merged = shapely.union_all(polys, grid_size=1e-4)
    geoms = merged.geoms if isinstance(merged, MultiPolygon) else [merged]
    rings = []
    for g in geoms:
        if isinstance(g, Polygon) and not g.is_empty:
            g = g.simplify(1e-3)
            rings.append(list(g.exterior.coords))
            rings.extend(list(r.coords) for r in g.interiors)
    return rings


def _extent(model: ParametricModel, ax, ay):
    lo, hi = model.bounds()
    corners = np.array([[x, y, z] for x in (lo[0], hi[0]) for y in (lo[1], hi[1]) for z in (lo[2], hi[2])])
    px, py = corners @ ax, corners @ ay
    return px.min(), px.max(), py.min(), py.max()


def build(model: ParametricModel, section_x: float | None = None) -> tuple[Drawing, int]:
    """Returns the drawing and the scale denominator N (scale 1:N)."""
    lo, hi = model.bounds()
    size = hi - lo
    _, pw, ph = PAGE
    # side view (L x H) top-left, top view (L x W) below it, front view (W x H) right of side, section below front.
    need_w = lambda s: (size[0] + size[2]) / s + GAP + 2 * MARGIN  # noqa: E731
    need_h = lambda s: (size[1] + size[2]) / s + GAP + 2 * MARGIN + TITLE + 8  # noqa: E731
    n = next((s for s in SCALES if need_w(s) <= pw and need_h(s) <= ph), SCALES[-1])
    k = 1.0 / n

    d = Drawing(pw, ph, f"{model.template} - orthographic views 1:{n}", PAGE[0])
    x_left = MARGIN
    x_right = MARGIN + size[0] * k + GAP
    y_upper = MARGIN + 6
    y_lower = y_upper + size[1] * k + GAP

    def place(view: str, x0: float, y0: float, label: str, mesh_rings=None) -> None:
        ax, ay = PROJECTIONS[view]
        ex = _extent(model, ax, ay)
        to_page = lambda u, v: (x0 + (u - ex[0]) * k, y0 + (v - ex[2]) * k)  # noqa: E731
        rings = mesh_rings if mesh_rings is not None else [r for c in model.components for r in _outline(c.mesh, ax, ay)]
        for ring in rings:
            d.line([to_page(u, v) for u, v in ring], VIEW, 0.18)
        w, h = (ex[1] - ex[0]) * k, (ex[3] - ex[2]) * k
        d.text(label, x0, y0 - 2.5, 3.0)
        # Overall dimensions (real millimetres), below and to the right of the view.
        yb = y0 + h + 5
        d.line([(x0, yb), (x0 + w, yb)], ANNOTATION, 0.15)
        d.line([(x0, yb - 1.5), (x0, yb + 1.5)], ANNOTATION, 0.15)
        d.line([(x0 + w, yb - 1.5), (x0 + w, yb + 1.5)], ANNOTATION, 0.15)
        d.text(f"{ex[1] - ex[0]:.2f}", x0 + w / 2, yb + 3.5, 2.6, "middle")
        xr = x0 + w + 5
        d.line([(xr, y0), (xr, y0 + h)], ANNOTATION, 0.15)
        d.line([(xr - 1.5, y0), (xr + 1.5, y0)], ANNOTATION, 0.15)
        d.line([(xr - 1.5, y0 + h), (xr + 1.5, y0 + h)], ANNOTATION, 0.15)
        d.text(f"{ex[3] - ex[2]:.2f}", xr + 3.5, y0 + h / 2, 2.6, "middle", rotation=90)

    place("side", x_left, y_upper, "SIDE (from +Z)")
    place("top", x_left, y_lower, "TOP (from +Y)")
    place("front", x_right, y_upper, "FRONT (from +X)")

    sx = float((lo[0] + hi[0]) / 2) if section_x is None else section_x
    combined = model.combined()
    segments = mesh_plane(combined, plane_normal=[1.0, 0, 0], plane_origin=[sx, 0, 0])
    ax, ay = PROJECTIONS["front"]
    seg_rings = [[(float(a @ ax), float(a @ ay)), (float(b @ ax), float(b @ ay))] for a, b in segments]
    place("front", x_right, y_lower, f"SECTION A-A at x = {sx:.2f} (seen from +X)", seg_rings)

    ty = ph - MARGIN - TITLE + 8
    d.line([(MARGIN, ty - 6), (pw - MARGIN, ty - 6)], ANNOTATION, 0.25)
    d.text(f"Template '{model.template}'   Scale 1:{n}   Units mm   Overall {size[0]:.2f} x {size[1]:.2f} x {size[2]:.2f} (X x Y x Z)", MARGIN, ty, 3.2)
    d.text("Component outlines are exact; hidden lines are not removed. Use the 1:1 cutting templates for cutting, not this sheet.", MARGIN, ty + 5, 2.6)
    d.text(f"{ENGINE_NAME} {ENGINE_VERSION}. Parametrically generated geometry (not reconstructed, not AI-predicted).", MARGIN, ty + 10, 2.6)
    return d, n
