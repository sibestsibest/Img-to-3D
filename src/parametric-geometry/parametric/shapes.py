"""Closed, outward-facing solids and 2D profiles in millimetres.

Axial shapes are built around local +Z; :func:`place` and :func:`along_axis` move them into the
model. Round shapes are regular polygons whose vertices lie on the nominal circle (as in the
application's stock shapes), so a 48-sided rod of 3 mm is exactly 3 mm across its corners.
"""

from __future__ import annotations

import math

import numpy as np
import trimesh
from shapely.geometry import Polygon
from shapely.geometry.polygon import orient

DEFAULT_SEGMENTS = 48


# ---- 2D profiles ---------------------------------------------------------------------------


def rect(x0: float, y0: float, x1: float, y1: float) -> list[tuple[float, float]]:
    return [(x0, y0), (x1, y0), (x1, y1), (x0, y1)]


def circle(cx: float, cy: float, r: float, segments: int) -> list[tuple[float, float]]:
    return [(cx + r * math.cos(2 * math.pi * i / segments), cy + r * math.sin(2 * math.pi * i / segments)) for i in range(segments)]


def arc(cx: float, cy: float, r: float, a0: float, a1: float, segments: int) -> list[tuple[float, float]]:
    """Points from angle a0 to a1 (radians) inclusive, ``segments`` intervals."""
    return [(cx + r * math.cos(a0 + (a1 - a0) * i / segments), cy + r * math.sin(a0 + (a1 - a0) * i / segments)) for i in range(segments + 1)]


def slot(cx: float, cy: float, length: float, width: float, segments: int) -> list[tuple[float, float]]:
    """Rounded slot along X: total ``length`` including the round ends."""
    r = width / 2
    half = max(length / 2 - r, 0.0)
    n = max(segments // 4, 4)
    right = arc(cx + half, cy, r, -math.pi / 2, math.pi / 2, n)
    left = arc(cx - half, cy, r, math.pi / 2, 3 * math.pi / 2, n)
    return right + left


def chamfered_rect(w: float, h: float, c: float) -> list[tuple[float, float]]:
    x, y = w / 2, h / 2
    return [(-x + c, -y), (x - c, -y), (x, -y + c), (x, y - c), (x - c, y), (-x + c, y), (-x, y - c), (-x, -y + c)]


def polygon(outline, holes=()) -> Polygon:
    """Counter-clockwise outline with clockwise holes (shapely's orientation for extrusion)."""
    return orient(Polygon(outline, list(holes)), sign=1.0)


# ---- solids ----------------------------------------------------------------------------------


def _solid(mesh: trimesh.Trimesh) -> trimesh.Trimesh:
    mesh.metadata.clear()  # trimesh stores creation arguments; keep exports free of them
    if mesh.volume < 0:
        mesh.invert()
    return mesh


def box(sx: float, sy: float, sz: float) -> trimesh.Trimesh:
    return _solid(trimesh.creation.box(extents=[sx, sy, sz]))


def extrude(profile: Polygon, height: float) -> trimesh.Trimesh:
    """Profile in local XY extruded along +Z from z = 0 to z = height."""
    return _solid(trimesh.creation.extrude_polygon(profile, height))


def revolve(profile: list[tuple[float, float]], segments: int) -> trimesh.Trimesh:
    """Closed (radius, z) profile revolved around local Z."""
    return _solid(trimesh.creation.revolve(np.asarray(profile, dtype=float), sections=segments))


def cylinder(diameter: float, height: float, segments: int) -> trimesh.Trimesh:
    """Centred on the origin, axis Z."""
    return extrude(polygon(circle(0, 0, diameter / 2, segments)), height).apply_translation([0, 0, -height / 2])


def frustum(bottom_diameter: float, top_diameter: float, height: float, segments: int) -> trimesh.Trimesh:
    """Cone (top diameter 0) or truncated cone, base at z = 0."""
    rb, rt = bottom_diameter / 2, top_diameter / 2
    profile = [(0.0, 0.0), (rb, 0.0), (rt, height), (0.0, height)] if rt > 0 else [(0.0, 0.0), (rb, 0.0), (0.0, height)]
    return revolve(profile, segments)


def tube(outer_diameter: float, inner_diameter: float, length: float, segments: int) -> trimesh.Trimesh:
    """Round tube from z = 0 to z = length."""
    return extrude(polygon(circle(0, 0, outer_diameter / 2, segments), [circle(0, 0, inner_diameter / 2, segments)]), length)


def frustum_tube(d0: float, d1: float, wall: float, length: float, segments: int) -> trimesh.Trimesh:
    """Flared tube (nozzle): outer diameter d0 at z = 0 to d1 at z = length, radial wall ``wall``."""
    r0, r1 = d0 / 2, d1 / 2
    # The profile does not touch the axis, so it is closed explicitly (first point repeated).
    return revolve([(r0 - wall, 0.0), (r0, 0.0), (r1, length), (r1 - wall, length), (r0 - wall, 0.0)], segments)


def sphere(diameter: float, segments: int) -> trimesh.Trimesh:
    r = diameter / 2
    n = max(segments // 2, 4)
    return revolve([(0.0, -r)] + [(r * math.sin(math.pi * i / n), -r * math.cos(math.pi * i / n)) for i in range(1, n)] + [(0.0, r)], segments)


def hemisphere(diameter: float, segments: int) -> trimesh.Trimesh:
    """Flat face on z = 0, dome towards +Z."""
    r = diameter / 2
    n = max(segments // 4, 3)
    return revolve([(0.0, 0.0), (r, 0.0)] + [(r * math.cos(math.pi / 2 * i / n), r * math.sin(math.pi / 2 * i / n)) for i in range(1, n)] + [(0.0, r)], segments)


# ---- placement -------------------------------------------------------------------------------


def frame(origin, u, v) -> np.ndarray:
    """4x4 matrix mapping local X->u, Y->v, Z->u×v, local origin -> origin (right-handed)."""
    u, v = np.asarray(u, dtype=float), np.asarray(v, dtype=float)
    n = np.cross(u, v)
    m = np.eye(4)
    m[:3, 0], m[:3, 1], m[:3, 2], m[:3, 3] = u, v, n, origin
    return m


def place(mesh: trimesh.Trimesh, matrix: np.ndarray) -> trimesh.Trimesh:
    if np.linalg.det(matrix[:3, :3]) <= 0:
        raise ValueError("placement must not mirror the solid")
    return mesh.copy().apply_transform(matrix)


AXIS_FRAMES = {
    # local Z onto the given world axis by a cyclic permutation (keeps the winding).
    "x": ((0, 1, 0), (0, 0, 1)),
    "y": ((0, 0, 1), (1, 0, 0)),
    "z": ((1, 0, 0), (0, 1, 0)),
}


def along_axis(mesh: trimesh.Trimesh, axis: str, origin=(0.0, 0.0, 0.0)) -> trimesh.Trimesh:
    u, v = AXIS_FRAMES[axis]
    return place(mesh, frame(origin, u, v))


def transform_matrix(translate=(0.0, 0.0, 0.0), rotate_deg=(0.0, 0.0, 0.0), scale=(1.0, 1.0, 1.0)) -> np.ndarray:
    """T · Rz · Ry · Rx · S (rotations about the fixed world axes, X first)."""
    rx, ry, rz = (math.radians(a) for a in rotate_deg)
    r = (
        trimesh.transformations.rotation_matrix(rz, [0, 0, 1])
        @ trimesh.transformations.rotation_matrix(ry, [0, 1, 0])
        @ trimesh.transformations.rotation_matrix(rx, [1, 0, 0])
    )
    s = np.diag([scale[0], scale[1], scale[2], 1.0])
    t = trimesh.transformations.translation_matrix(translate)
    return t @ r @ s
