"""Scratch-building components: sheet parts with exact cutting patterns, rolled parts with their
developed patterns, rods and stock parts.

A sheet component is built from its 2D profile, so the cutting template is the profile itself and
never has to be recovered from the mesh.
"""

from __future__ import annotations

import math

import trimesh
from shapely.geometry import Polygon

from . import shapes
from .model import CONE_ROLL, CUSTOM_3D, DEVELOPED, FLAT, ROD, ROLL, SHEET, STOCK_PART, TUBE, Component, FlatPattern


def plate(name: str, role: str, profile: Polygon, thickness: float, origin, u, v, material: str = "PS", notes=()) -> Component:
    """Flat sheet part: ``profile`` in (u, v) millimetres, thickness along u×v starting at ``origin``."""
    mesh = shapes.place(shapes.extrude(profile, thickness), shapes.frame(origin, u, v))
    minx, miny, maxx, maxy = profile.bounds
    return Component(
        name=name,
        role=role,
        category=SHEET,
        mesh=mesh,
        material=material,
        thickness_mm=thickness,
        pattern=FlatPattern(profile, FLAT),
        size_mm={"length": round(maxx - minx, 4), "width": round(maxy - miny, 4)},
        notes=list(notes),
    )


def curved_shell(name: str, role: str, outer_radius: float, thickness: float, a0: float, a1: float, length: float, origin, u, v, segments: int) -> Component:
    """Sheet bent to a cylinder sector: profile in the (u, v) plane around ``origin``, extruded ``length`` along u×v.

    The developed pattern is a rectangle: ``length`` by the arc length at mid-thickness.
    """
    inner = outer_radius - thickness
    n = max(int(segments * abs(a1 - a0) / (2 * math.pi)), 4)
    outline = shapes.arc(0, 0, outer_radius, a0, a1, n) + list(reversed(shapes.arc(0, 0, inner, a0, a1, n)))
    mesh = shapes.place(shapes.extrude(shapes.polygon(outline), length), shapes.frame(origin, u, v))
    mid = outer_radius - thickness / 2
    developed = mid * abs(a1 - a0)
    return Component(
        name=name,
        role=role,
        category=SHEET,
        mesh=mesh,
        thickness_mm=thickness,
        pattern=FlatPattern(
            shapes.polygon(shapes.rect(0, 0, length, developed)),
            DEVELOPED,
            f"Roll to {2 * outer_radius:g} mm outside diameter ({math.degrees(abs(a1 - a0)):g}° arc); pattern length at mid-thickness.",
        ),
        forming=ROLL,
        size_mm={"length": round(length, 4), "developedWidth": round(developed, 4), "outerRadius": round(outer_radius, 4)},
    )


def rolled_tube(name: str, role: str, outer_diameter: float, thickness: float, length: float, axis: str, origin, segments: int) -> Component:
    """Tube rolled from sheet; developed pattern = length × circumference at mid-thickness."""
    mesh = shapes.along_axis(shapes.tube(outer_diameter, outer_diameter - 2 * thickness, length, segments), axis, origin)
    circumference = math.pi * (outer_diameter - thickness)
    return Component(
        name=name,
        role=role,
        category=SHEET,
        mesh=mesh,
        thickness_mm=thickness,
        pattern=FlatPattern(
            shapes.polygon(shapes.rect(0, 0, length, circumference)),
            DEVELOPED,
            f"Roll to {outer_diameter:g} mm outside diameter and butt-join the long edges.",
        ),
        forming=ROLL,
        size_mm={"length": round(length, 4), "outerDiameter": outer_diameter, "developedWidth": round(circumference, 4)},
    )


def cone_shell(name: str, role: str, d0: float, d1: float, thickness: float, length: float, matrix, segments: int) -> Component:
    """Flared tube rolled from sheet; developed pattern = annular sector at mid-thickness."""
    mesh = shapes.place(shapes.frustum_tube(d0, d1, thickness, length, segments), matrix)
    r0, r1 = d0 / 2 - thickness / 2, d1 / 2 - thickness / 2
    if abs(r1 - r0) < 1e-9:
        outline = shapes.rect(0, 0, length, 2 * math.pi * r0)
        forming = ROLL
    else:
        slant = math.hypot(length, r1 - r0)
        small, large = min(r0, r1), max(r0, r1)
        inner = small * slant / (large - small)  # apex to the small edge along the slant
        angle = 2 * math.pi * small / inner
        n = max(segments, 8)
        outline = shapes.arc(0, 0, inner + slant, 0, angle, n) + list(reversed(shapes.arc(0, 0, inner, 0, angle, n)))
        forming = CONE_ROLL
    return Component(
        name=name,
        role=role,
        category=SHEET,
        mesh=mesh,
        thickness_mm=thickness,
        pattern=FlatPattern(shapes.polygon(outline), DEVELOPED, f"Roll into a cone from {d0:g} to {d1:g} mm outside diameter and butt-join the straight edges."),
        forming=forming,
        size_mm={"length": round(length, 4), "startDiameter": d0, "endDiameter": d1},
    )


def rod(name: str, role: str, diameter: float, length: float, axis: str, origin, segments: int, material: str = "PS rod") -> Component:
    """Round rod from z = 0 to length along ``axis`` starting at ``origin``."""
    mesh = shapes.along_axis(shapes.extrude(shapes.polygon(shapes.circle(0, 0, diameter / 2, segments)), length), axis, origin)
    return Component(name=name, role=role, category=ROD, mesh=mesh, material=material, size_mm={"diameter": diameter, "length": round(length, 4)})


def stock_tube(name: str, role: str, outer_diameter: float, inner_diameter: float, length: float, axis: str, origin, segments: int, material: str = "PS tube") -> Component:
    mesh = shapes.along_axis(shapes.tube(outer_diameter, inner_diameter, length, segments), axis, origin)
    return Component(
        name=name, role=role, category=TUBE, mesh=mesh, material=material,
        size_mm={"outerDiameter": outer_diameter, "innerDiameter": inner_diameter, "length": round(length, 4)},
    )


def hex_heads(name: str, role: str, across_corners: float, height: float, positions, axis: str = "y", material: str = "PS hex rod") -> Component:
    """Identical hexagonal bolt heads standing on ``positions`` (one body each), cut from hex rod."""
    unit = shapes.extrude(shapes.polygon(shapes.circle(0, 0, across_corners / 2, 6)), height)
    meshes = [shapes.along_axis(unit, axis, p) for p in positions]
    return Component(
        name=name,
        role=role,
        category=STOCK_PART,
        mesh=trimesh.util.concatenate(meshes),
        material=material,
        quantity=len(meshes),
        size_mm={"acrossCorners": across_corners, "acrossFlats": round(across_corners * math.sqrt(3) / 2, 4), "height": height},
        notes=["Slice from hex rod; quantity is the number of heads."],
    )


def solid(name: str, role: str, mesh: trimesh.Trimesh, material: str = "PS", category: str = CUSTOM_3D, size_mm=None) -> Component:
    return Component(name=name, role=role, category=category, mesh=mesh, material=material, size_mm=dict(size_mm or {}))
