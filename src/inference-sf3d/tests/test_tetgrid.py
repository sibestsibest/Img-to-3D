import numpy as np

from app.tetgrid import build_cubic_edges, build_cubic_grid


def test_grid_is_positive_and_fills_unit_cube():
    v, t = build_cubic_grid(5)
    assert v.shape == (125, 3) and t.shape == (6 * 64, 4)
    a, b, c, d = (v[t[:, i]] for i in range(4))
    vol = np.einsum("ij,ij->i", np.cross(b - a, c - a), d - a) / 6
    assert (vol > 0).all()
    assert np.isclose(vol.sum(), 1.0)


def test_grid_is_conforming():
    # Every interior triangle face is shared by exactly two tetrahedra.
    v, t = build_cubic_grid(4)
    faces = np.sort(np.concatenate([t[:, [0, 1, 2]], t[:, [0, 1, 3]], t[:, [0, 2, 3]], t[:, [1, 2, 3]]]), axis=1)
    _, counts = np.unique(faces, axis=0, return_counts=True)
    assert set(counts.tolist()) <= {1, 2}
    boundary = counts == 1
    # Boundary faces: 6 cube sides * 3x3 squares * 2 triangles.
    assert boundary.sum() == 6 * 9 * 2


def test_analytic_edges_match_unique_tet_edges():
    # Same set SF3D's MarchingTetrahedraHelper.all_edges computes with torch.unique.
    _, t = build_cubic_grid(5)
    pairs = t[:, [0, 1, 0, 2, 0, 3, 1, 2, 1, 3, 2, 3]].reshape(-1, 2)
    expected = np.unique(np.sort(pairs, axis=1), axis=0)
    edges = build_cubic_edges(5)
    assert (edges[:, 0] < edges[:, 1]).all()
    assert len(np.unique(edges, axis=0)) == len(edges)
    np.testing.assert_array_equal(np.unique(edges, axis=0), expected)
