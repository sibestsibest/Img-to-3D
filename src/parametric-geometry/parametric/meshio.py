"""Mesh files: GLB (metres, one node per component), STL (millimetres, Z-up), OBJ (millimetres, Y-up)."""

from __future__ import annotations

import numpy as np
import trimesh
from trimesh.visual import TextureVisuals
from trimesh.visual.material import PBRMaterial

from .model import ParametricModel

# Display colours by role (RGBA 0-255); unknown roles are light grey like unpainted styrene.
ROLE_COLOURS = {
    "hull_shell": (176, 184, 196, 255),
    "deck": (160, 168, 180, 255),
    "top_plate": (170, 178, 190, 255),
    "side_wall": (170, 178, 190, 255),
    "end_wall": (150, 158, 170, 255),
    "bulkhead": (120, 128, 140, 255),
    "rib": (120, 128, 140, 255),
    "armor_panel": (96, 112, 136, 255),
    "recess_frame": (110, 122, 140, 255),
    "vent_panel": (70, 76, 86, 255),
    "bolt": (60, 60, 64, 255),
    "antenna": (200, 170, 90, 255),
    "engine_housing": (90, 96, 106, 255),
    "exhaust": (60, 52, 48, 255),
}
DEFAULT_COLOUR = (215, 215, 210, 255)

Y_UP_TO_Z_UP = np.array([[1, 0, 0, 0], [0, 0, -1, 0], [0, 1, 0, 0], [0, 0, 0, 1]], dtype=float)


def to_glb(model: ParametricModel) -> bytes:
    """glTF positions are metres; each component is its own named node and mesh."""
    scene = trimesh.Scene()
    materials: dict[tuple, PBRMaterial] = {}
    for c in model.components:
        mesh = c.mesh.copy()
        mesh.apply_scale(0.001)
        colour = ROLE_COLOURS.get(c.role, DEFAULT_COLOUR)
        if colour not in materials:
            materials[colour] = PBRMaterial(name=f"styrene_{len(materials)}", baseColorFactor=list(colour), metallicFactor=0.0, roughnessFactor=0.8)
        mesh.visual = TextureVisuals(material=materials[colour])
        mesh.metadata.clear()
        scene.add_geometry(mesh, node_name=c.name, geom_name=c.name)
    return scene.export(file_type="glb")


def to_stl(model: ParametricModel) -> bytes:
    """Binary STL of all components in millimetres, Z-up (slicer convention), as the application exports."""
    mesh = model.combined()
    mesh.apply_transform(Y_UP_TO_Z_UP)
    return mesh.export(file_type="stl")


def to_obj(model: ParametricModel) -> bytes:
    """Wavefront OBJ in millimetres, Y-up, one ``o`` group per component, no materials."""
    lines = ["# ImageTo3D parametric geometry, units: millimetres, Y-up"]
    offset = 1
    for c in model.components:
        lines.append(f"o {c.name}")
        lines.extend(f"v {_f(x)} {_f(y)} {_f(z)}" for x, y, z in c.mesh.vertices)
        lines.extend(f"f {a + offset} {b + offset} {d + offset}" for a, b, d in c.mesh.faces)
        offset += len(c.mesh.vertices)
    return ("\n".join(lines) + "\n").encode("ascii")


def _f(v: float) -> str:
    s = f"{v:.6f}".rstrip("0").rstrip(".")
    return "0" if s in ("-0", "") else s
