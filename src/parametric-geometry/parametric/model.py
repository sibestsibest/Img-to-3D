"""Components, flat patterns and the generated model."""

from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np
import trimesh
from shapely.geometry import Polygon

# Fabrication categories, same values as the application's PartCategories.
SHEET = "sheet"
ROD = "rod"
TUBE = "tube"
STOCK_PART = "stock_part"
CUSTOM_3D = "custom_3d"

# Flat pattern kinds.
FLAT = "flat"  # cut and use as is
DEVELOPED = "developed"  # cut flat, then roll or bend (see Component.forming)

# Forming operations a part needs after cutting.
ROLL = "roll"  # cylindrical
CONE_ROLL = "cone_roll"  # conical


@dataclass(frozen=True)
class FlatPattern:
    """Exact cutting outline in pattern millimetres (holes are interiors of the polygon)."""

    outline: Polygon
    kind: str = FLAT
    note: str | None = None


@dataclass
class Component:
    name: str  # unique within a model: [a-z0-9_]
    role: str  # hull_shell, deck, side_wall, bulkhead, rib, armor_panel, vent_panel, ...
    category: str
    mesh: trimesh.Trimesh  # model millimetres, Y-up
    material: str = "PS"
    thickness_mm: float | None = None
    quantity: int = 1  # bodies in the mesh (several identical bolt heads are one component)
    pattern: FlatPattern | None = None
    forming: str | None = None
    size_mm: dict[str, float] = field(default_factory=dict)  # nominal sizes for the BOM
    notes: list[str] = field(default_factory=list)


@dataclass
class Issue:
    code: str
    severity: str  # info | warning | error
    message: str
    component: str | None = None

    def to_dict(self) -> dict:
        return {"code": self.code, "severity": self.severity, "message": self.message, "component": self.component}


@dataclass
class ParametricModel:
    template: str
    parameters: dict
    derived: dict
    components: list[Component]
    issues: list[Issue] = field(default_factory=list)

    def combined(self) -> trimesh.Trimesh:
        return trimesh.util.concatenate([c.mesh for c in self.components])

    def bounds(self) -> np.ndarray:
        lo = np.min([c.mesh.bounds[0] for c in self.components], axis=0)
        hi = np.max([c.mesh.bounds[1] for c in self.components], axis=0)
        return np.array([lo, hi])


# ---- inspection ------------------------------------------------------------------------------


def inspect(mesh: trimesh.Trimesh) -> dict:
    """Validation facts about one mesh; uses numpy only (no scipy/networkx needed)."""
    faces = mesh.faces
    edges = np.sort(mesh.edges, axis=1)
    _, counts = np.unique(edges, axis=0, return_counts=True)
    boundary = int((counts == 1).sum())
    non_manifold = int((counts > 2).sum())
    areas = mesh.area_faces
    degenerate = int((areas < 1e-12).sum())
    closed = boundary == 0 and non_manifold == 0
    bounds = mesh.bounds
    report = {
        "vertexCount": int(len(mesh.vertices)),
        "triangleCount": int(len(faces)),
        "degenerateTriangles": degenerate,
        "boundaryEdges": boundary,
        "nonManifoldEdges": non_manifold,
        "consistentWinding": bool(mesh.is_winding_consistent),
        "closed": bool(closed),
        "bodies": bodies(mesh),
        "volumeMm3": round(float(mesh.volume), 6) if closed else None,
        "areaMm2": round(float(mesh.area), 6),
        "bounds": box_dict(bounds),
    }
    report["valid"] = bool(closed and degenerate == 0 and report["consistentWinding"] and (report["volumeMm3"] or 0) > 0)
    return report


def bodies(mesh: trimesh.Trimesh) -> int:
    """Connected pieces (faces sharing a vertex), by union-find."""
    parent = np.arange(len(mesh.vertices))

    def find(i: int) -> int:
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    for a, b, c in mesh.faces:
        ra, rb, rc = find(a), find(b), find(c)
        parent[rb] = ra
        parent[find(rc)] = ra
    used = np.unique(mesh.faces)
    return len({find(int(i)) for i in used})


def box_dict(bounds: np.ndarray) -> dict:
    lo, hi = bounds
    return {"min": _r(lo), "max": _r(hi), "size": _r(hi - lo)}


def _r(v) -> list[float]:
    # Rounded to 0.1 µm so float noise never changes the JSON (and -0.0 becomes 0.0).
    return [round(float(x), 4) + 0.0 for x in v]
