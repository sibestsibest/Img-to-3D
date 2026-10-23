"""Free-form compound model: a list of primitives, each with its own size, transform and material.

Every shape is defined around its local origin (axial shapes along local +Z, see the table in
``SHAPES``) and then moved with ``transform`` = {translate [x,y,z] mm, rotate_deg [x,y,z],
scale [x,y,z] > 0}. ``plate`` and ``shell`` are sheet parts: their thickness must be a PS sheet
thickness and they get a cutting pattern (a scaled sheet part would no longer have that
thickness, so they refuse ``scale``).
"""

from __future__ import annotations

import math
import re

from .. import components as parts
from .. import shapes
from ..errors import INVALID_PARAMETER, LIMIT_EXCEEDED, ParametricError
from ..model import CUSTOM_3D, ROD, SHEET, STOCK_PART, TUBE, ParametricModel
from ..params import Param, number, read, require, sheet_thickness

NAME = "assembly"
DESCRIPTION = "Compound model from boxes, cylinders, cones, spheres, tubes, extrusions, plates and shells."
MAX_COMPONENTS = 500
NAME_PATTERN = re.compile(r"^[a-z0-9_]{1,48}$")

PARAMS = [Param("segments", 48, 12, 256, unit="", integer=True, description="Polygon sides of round parts.")]

# shape -> (size fields, description)
SHAPES = {
    "box": (["size_x", "size_y", "size_z"], "Block centred on the origin."),
    "chamfered_box": (["size_x", "size_y", "size_z", "chamfer"], "Block centred on the origin with its four edges along Z chamfered."),
    "cylinder": (["diameter", "height"], "Solid cylinder centred on the origin, axis Z."),
    "cone": (["diameter", "height"], "Cone, base on z = 0, tip on +Z."),
    "frustum": (["bottom_diameter", "top_diameter", "height"], "Truncated cone, base on z = 0."),
    "sphere": (["diameter"], "Sphere centred on the origin."),
    "hemisphere": (["diameter"], "Half sphere, flat face on z = 0, dome towards +Z."),
    "tube": (["outer_diameter", "inner_diameter", "length"], "Round tube from z = 0 to length."),
    "rod": (["diameter", "length"], "Round rod from z = 0 to length."),
    "extrusion": (["height"], "Polygon 'outline' [[x, y], ...] with optional 'holes', extruded from z = 0 to height."),
    "plate": (["thickness"], "PS sheet: 'outline' (or size_x/size_y rectangle centred on the origin) with optional 'holes', thickness from z = 0."),
    "shell": (["outer_diameter", "thickness", "angle_deg", "length"], "PS sheet rolled to a cylinder sector starting at +X, axis Z, from z = 0 to length."),
}
CATEGORY = {"plate": SHEET, "shell": SHEET, "rod": ROD, "tube": TUBE}
MAX_SIZE_MM = 5000


def build(raw: dict) -> ParametricModel:
    if not isinstance(raw, dict):
        raise ParametricError(INVALID_PARAMETER, "parameters must be an object.", "parameters")
    items = raw.get("components")
    if not isinstance(items, list) or not items:
        raise ParametricError(INVALID_PARAMETER, "components must be a non-empty list.", "parameters.components")
    if len(items) > MAX_COMPONENTS:
        raise ParametricError(LIMIT_EXCEEDED, f"At most {MAX_COMPONENTS} components.", "parameters.components")
    p = read({k: v for k, v in raw.items() if k != "components"}, PARAMS, "parameters.")
    seg = p["segments"]

    comps, seen = [], set()
    for i, item in enumerate(items):
        f = f"parameters.components[{i}]"
        if not isinstance(item, dict):
            raise ParametricError(INVALID_PARAMETER, "Each component must be an object.", f)
        name = item.get("name", f"part_{i + 1}")
        require(isinstance(name, str) and bool(NAME_PATTERN.match(name)), "name must be 1-48 characters a-z, 0-9 or _.", f + ".name")
        require(name not in seen, f"Duplicate component name '{name}'.", f + ".name")
        seen.add(name)
        comps.append(_component(item, name, f, seg))

    return ParametricModel(NAME, {"segments": seg, "components": items}, {}, comps, [])


def _component(item: dict, name: str, f: str, seg: int):
    shape = item.get("shape")
    if shape not in SHAPES:
        raise ParametricError(INVALID_PARAMETER, f"shape must be one of {', '.join(SHAPES)}.", f + ".shape")
    allowed = set(SHAPES[shape][0]) | {"name", "shape", "transform", "material", "role", "outline", "holes", "size_x", "size_y"}
    for key in item:
        if key not in allowed:
            raise ParametricError(INVALID_PARAMETER, f"Unknown field '{key}' for shape '{shape}'.", f"{f}.{key}")

    def size(key: str, minimum: float = 0.05) -> float:
        field = f"{f}.{key}"
        if key not in item:
            raise ParametricError(INVALID_PARAMETER, f"{key} is required for shape '{shape}'.", field)
        v = number(item[key], field)
        require(minimum <= v <= MAX_SIZE_MM, f"{key} must be between {minimum:g} and {MAX_SIZE_MM} mm.", field)
        return v

    matrix, scaled = _transform(item.get("transform"), f + ".transform")
    material = item.get("material", "PS")
    require(isinstance(material, str) and 0 < len(material) <= 40, "material must be a short text.", f + ".material")
    role = item.get("role", shape)
    require(isinstance(role, str) and bool(NAME_PATTERN.match(role)), "role must be 1-48 characters a-z, 0-9 or _.", f + ".role")

    if shape in ("plate", "shell"):
        require(not scaled, "Sheet parts cannot be scaled (the sheet thickness would change).", f + ".transform.scale")
        t = sheet_thickness(item.get("thickness"), f + ".thickness")
        if shape == "plate":
            profile = _profile(item, f, rectangle_ok=True)
            c = parts.plate(name, role, profile, t, (0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), material)
        else:
            od, angle, length = size("outer_diameter"), number(item.get("angle_deg"), f + ".angle_deg"), size("length")
            require(1 <= angle <= 359, "angle_deg must be between 1 and 359 (use a rolled tube for a full circle).", f + ".angle_deg")
            require(od / 2 > 2 * t, "outer_diameter must be more than 4 sheet thicknesses.", f + ".outer_diameter")
            c = parts.curved_shell(name, role, od / 2, t, 0.0, math.radians(angle), length, (0.0, 0.0, 0.0), (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), seg)
        c.material = material
        c.mesh = shapes.place(c.mesh, matrix)
        return c

    if shape == "box":
        mesh = shapes.box(size("size_x"), size("size_y"), size("size_z"))
    elif shape == "chamfered_box":
        sx, sy, sz, ch = size("size_x"), size("size_y"), size("size_z"), size("chamfer", 0.01)
        require(ch < min(sx, sy) / 2, "chamfer must be smaller than half the smaller of size_x and size_y.", f + ".chamfer")
        mesh = shapes.extrude(shapes.polygon(shapes.chamfered_rect(sx, sy, ch)), sz).apply_translation([0, 0, -sz / 2])
    elif shape == "cylinder":
        mesh = shapes.cylinder(size("diameter"), size("height"), seg)
    elif shape == "cone":
        mesh = shapes.frustum(size("diameter"), 0.0, size("height"), seg)
    elif shape == "frustum":
        mesh = shapes.frustum(size("bottom_diameter"), size("top_diameter"), size("height"), seg)
    elif shape == "sphere":
        mesh = shapes.sphere(size("diameter"), seg)
    elif shape == "hemisphere":
        mesh = shapes.hemisphere(size("diameter"), seg)
    elif shape in ("tube", "rod"):
        if shape == "tube":
            od, idia, length = size("outer_diameter"), size("inner_diameter"), size("length")
            require(idia < od and (od - idia) / 2 >= 0.05, "inner_diameter must be smaller than outer_diameter with a wall of at least 0.05 mm.", f + ".inner_diameter")
            c = parts.stock_tube(name, role, od, idia, length, "z", (0.0, 0.0, 0.0), seg, material)
        else:
            c = parts.rod(name, role, size("diameter"), size("length"), "z", (0.0, 0.0, 0.0), seg, material)
        c.mesh = shapes.place(c.mesh, matrix)
        if scaled:
            c.category, c.size_mm = CUSTOM_3D, {}
        return c
    else:  # extrusion
        mesh = shapes.extrude(_profile(item, f, rectangle_ok=False), size("height"))

    c = parts.solid(name, role, shapes.place(mesh, matrix), material, CATEGORY.get(shape, CUSTOM_3D))
    if c.category == CUSTOM_3D and material.lower().startswith("stock"):
        c.category = STOCK_PART
    return c


def _profile(item: dict, f: str, rectangle_ok: bool):
    from shapely.geometry import Polygon  # local import keeps module import light

    if "outline" in item:
        outline = _points(item["outline"], f + ".outline")
        holes = item.get("holes", [])
        require(isinstance(holes, list), "holes must be a list of polygons.", f + ".holes")
        hole_pts = [_points(h, f"{f}.holes[{j}]") for j, h in enumerate(holes)]
        poly = Polygon(outline, hole_pts)
        require(poly.is_valid and poly.area > 0, "outline and holes must form a simple polygon with the holes inside it and not touching each other.", f + ".outline")
        return shapes.polygon(outline, hole_pts)
    require(rectangle_ok, "outline is required.", f + ".outline")
    sx, sy = number(item.get("size_x"), f + ".size_x"), number(item.get("size_y"), f + ".size_y")
    require(0.05 <= sx <= MAX_SIZE_MM and 0.05 <= sy <= MAX_SIZE_MM, "size_x and size_y must be between 0.05 and 5000 mm.", f + ".size_x")
    return shapes.polygon(shapes.rect(-sx / 2, -sy / 2, sx / 2, sy / 2))


def _points(value, field: str) -> list[tuple[float, float]]:
    require(isinstance(value, list) and 3 <= len(value) <= 2000, "A polygon needs 3 to 2000 [x, y] points.", field)
    pts = []
    for j, pt in enumerate(value):
        require(isinstance(pt, list) and len(pt) == 2, "Each point must be [x, y].", f"{field}[{j}]")
        x, y = number(pt[0], f"{field}[{j}]"), number(pt[1], f"{field}[{j}]")
        require(abs(x) <= MAX_SIZE_MM and abs(y) <= MAX_SIZE_MM, "Points must be within 5000 mm of the origin.", f"{field}[{j}]")
        pts.append((x, y))
    return pts


def _transform(value, field: str):
    if value is None:
        value = {}
    require(isinstance(value, dict), "transform must be an object.", field)
    for key in value:
        require(key in ("translate", "rotate_deg", "scale"), f"Unknown transform field '{key}'.", f"{field}.{key}")

    def vec(key: str, default, lo: float, hi: float):
        v = value.get(key, default)
        require(isinstance(v, list) and len(v) == 3, f"{key} must be [x, y, z].", f"{field}.{key}")
        out = [number(c, f"{field}.{key}") for c in v]
        require(all(lo <= c <= hi for c in out), f"{key} values must be between {lo:g} and {hi:g}.", f"{field}.{key}")
        return out

    t = vec("translate", [0, 0, 0], -100_000, 100_000)
    r = vec("rotate_deg", [0, 0, 0], -360, 360)
    s = vec("scale", [1, 1, 1], 0.001, 1000)
    return shapes.transform_matrix(t, r, s), any(abs(c - 1) > 1e-12 for c in s)
