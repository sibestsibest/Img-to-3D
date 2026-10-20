"""Regular tetrahedral grids for SF3D's marching-tetrahedra surface extraction.

SF3D ships one grid (load/tets/160_tets.npz: a BCC lattice, ~80 cells per axis), which is far
coarser than its 384x384 triplane, so fine geometry is lost at extraction. This module builds a
denser cubic grid split into 6 tetrahedra per cube (Kuhn/Freudenthal subdivision). Adjacent cubes
share face diagonals, so the grid is conforming and extracted surfaces stay watertight.

The grid uses the same conventions as the official file: vertices in [0, 1]^3 (float64),
int64 indices, every tetrahedron positively oriented.
"""

from __future__ import annotations

from itertools import permutations
from pathlib import Path

import numpy as np


def build_cubic_grid(vertices_per_axis: int) -> tuple[np.ndarray, np.ndarray]:
    n = vertices_per_axis
    if n < 3:
        raise ValueError("Need at least 3 vertices per axis.")
    lin = np.linspace(0.0, 1.0, n)
    xs, ys, zs = np.meshgrid(lin, lin, lin, indexing="ij")
    vertices = np.stack([xs, ys, zs], axis=-1).reshape(-1, 3)

    def vid(i, j, k):
        return (i * n + j) * n + k

    c = np.arange(n - 1)
    ci, cj, ck = np.meshgrid(c, c, c, indexing="ij")
    ci, cj, ck = ci.ravel(), cj.ravel(), ck.ravel()

    tets = []
    for perm in permutations(range(3)):
        # Path from the cube's min corner to its max corner, stepping one axis at a time.
        corner = [ci.copy(), cj.copy(), ck.copy()]
        path = [vid(*corner)]
        for axis in perm:
            corner[axis] = corner[axis] + 1
            path.append(vid(*corner))
        tets.append(np.stack(path, axis=-1))
    indices = np.concatenate(tets, axis=0).astype(np.int64)

    # Make every tetrahedron positively oriented, like the official grid.
    a, b, cc, d = (vertices[indices[:, i]] for i in range(4))
    negative = np.einsum("ij,ij->i", np.cross(b - a, cc - a), d - a) < 0
    indices[negative, 1], indices[negative, 2] = indices[negative, 2].copy(), indices[negative, 1].copy()
    return vertices, indices


def build_cubic_edges(vertices_per_axis: int) -> np.ndarray:
    """Unique edges of build_cubic_grid, without deduplicating 6 edges x 6 tets per cube.

    Every tetrahedron is a monotone path from a cube's min to its max corner, so each of its edges
    joins a vertex to one at a non-negative offset: an axis step, a face diagonal or the body
    diagonal. Listing those 7 offsets per vertex gives each grid edge exactly once. (SF3D's own
    torch.unique over all tet edges needs tens of GB at 129 vertices per axis.)
    """
    n = vertices_per_axis
    lin = np.arange(n)
    i, j, k = (a.ravel() for a in np.meshgrid(lin, lin, lin, indexing="ij"))
    edges = []
    for di, dj, dk in [(1, 0, 0), (0, 1, 0), (0, 0, 1), (1, 1, 0), (1, 0, 1), (0, 1, 1), (1, 1, 1)]:
        ok = (i + di < n) & (j + dj < n) & (k + dk < n)
        a = (i[ok] * n + j[ok]) * n + k[ok]
        b = ((i[ok] + di) * n + j[ok] + dj) * n + k[ok] + dk
        edges.append(np.stack([a, b], axis=-1))
    return np.concatenate(edges, axis=0).astype(np.int64)


def cached_grid(directory: Path, vertices_per_axis: int) -> Path:
    path = directory / f"cubic_{vertices_per_axis}_tets_v2.npz"
    if not path.exists():
        directory.mkdir(parents=True, exist_ok=True)
        vertices, indices = build_cubic_grid(vertices_per_axis)
        edges = build_cubic_edges(vertices_per_axis)
        tmp = path.with_suffix(".tmp.npz")
        np.savez(tmp, vertices=vertices, indices=indices, edges=edges)
        tmp.replace(path)
    return path
