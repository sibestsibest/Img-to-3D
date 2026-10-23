import math

import numpy as np
import pytest

from parametric import components as parts
from parametric import shapes
from parametric.model import CONE_ROLL, DEVELOPED, ROLL, inspect


def ngon_area(r, n):
    return n / 2 * r * r * math.sin(2 * math.pi / n)


def assert_solid(mesh):
    r = inspect(mesh)
    assert r["valid"], r
    assert r["bodies"] == 1
    return r


@pytest.mark.parametrize(
    "mesh, size, volume",
    [
        (shapes.box(10, 20, 30), [10, 20, 30], 6000),
        (shapes.cylinder(10, 5, 48), [10, 10, 5], ngon_area(5, 48) * 5),
        (shapes.tube(10, 6, 7, 48), [10, 10, 7], (ngon_area(5, 48) - ngon_area(3, 48)) * 7),
        (shapes.extrude(shapes.polygon(shapes.chamfered_rect(20, 10, 2)), 4), [20, 10, 4], (200 - 2 * 4) * 4),
    ],
)
def test_prisms_have_exact_size_and_volume(mesh, size, volume):
    r = assert_solid(mesh)
    np.testing.assert_allclose(r["bounds"]["size"], size, atol=1e-4)
    assert r["volumeMm3"] == pytest.approx(volume, rel=1e-9)


def test_round_vertices_lie_on_the_nominal_circle():
    m = shapes.cylinder(12, 3, 48)
    radii = np.hypot(m.vertices[:, 0], m.vertices[:, 1])
    np.testing.assert_allclose(radii, 6, atol=1e-9)


@pytest.mark.parametrize(
    "mesh, size",
    [
        (shapes.sphere(10, 48), [10, 10, 10]),
        (shapes.hemisphere(10, 48), [10, 10, 5]),
        (shapes.frustum(10, 0, 8, 48), [10, 10, 8]),
        (shapes.frustum(10, 4, 8, 48), [10, 10, 8]),
        (shapes.frustum_tube(10, 14, 1, 6, 48), [14, 14, 6]),
    ],
)
def test_revolved_solids_are_closed_and_sized(mesh, size):
    r = assert_solid(mesh)
    np.testing.assert_allclose(r["bounds"]["size"], size, atol=1e-4)


def test_hemisphere_is_half_a_sphere():
    assert shapes.hemisphere(10, 48).volume == pytest.approx(shapes.sphere(10, 48).volume / 2, rel=0.02)


def test_frame_is_right_handed_and_refuses_mirrors():
    m = shapes.frame((1, 2, 3), (1, 0, 0), (0, 0, -1))
    np.testing.assert_allclose(m[:3, 2], [0, 1, 0])
    mirror = np.diag([-1.0, 1, 1, 1])
    with pytest.raises(ValueError):
        shapes.place(shapes.box(1, 1, 1), mirror)


def test_transform_rotates_then_translates():
    m = shapes.place(shapes.box(10, 2, 4), shapes.transform_matrix((5, 0, 0), (0, 0, 90)))
    np.testing.assert_allclose(m.extents, [2, 10, 4], atol=1e-9)
    np.testing.assert_allclose(m.bounds.mean(axis=0), [5, 0, 0], atol=1e-9)
    assert_solid(m)


def test_plate_volume_is_profile_area_times_thickness():
    profile = shapes.polygon(shapes.rect(0, 0, 40, 20), [shapes.circle(10, 10, 3, 32), shapes.rect(25, 5, 35, 15)])
    c = parts.plate("p", "panel", profile, 0.8, (0, 0, 0), (1, 0, 0), (0, 0, -1))
    r = assert_solid(c.mesh)
    assert r["volumeMm3"] == pytest.approx(profile.area * 0.8, rel=1e-9)
    np.testing.assert_allclose(c.mesh.extents, [40, 0.8, 20], atol=1e-9)
    assert c.pattern.outline.equals(profile)


def test_curved_shell_develops_at_mid_thickness():
    c = parts.curved_shell("s", "hull_shell", 20, 1, 0, math.pi, 50, (0, 0, 0), (1, 0, 0), (0, 1, 0), 48)
    assert_solid(c.mesh)
    minx, miny, maxx, maxy = c.pattern.outline.bounds
    assert (maxx - minx, maxy - miny) == pytest.approx((50, 19.5 * math.pi))
    assert c.pattern.kind == DEVELOPED and c.forming == ROLL


def test_rolled_tube_pattern_is_mid_wall_circumference():
    c = parts.rolled_tube("t", "engine_housing", 30, 1.5, 40, "x", (0, 0, 0), 48)
    assert_solid(c.mesh)
    np.testing.assert_allclose(c.mesh.extents, [40, 30, 30], atol=1e-9)
    _, _, w, h = c.pattern.outline.bounds
    assert (w, h) == pytest.approx((40, math.pi * 28.5))


def test_cone_pattern_edges_match_both_circumferences():
    d0, d1, t, length = 40.0, 60.0, 1.0, 25.0
    c = parts.cone_shell("e", "exhaust", d0, d1, t, length, np.eye(4), 96)
    assert_solid(c.mesh)
    assert c.forming == CONE_ROLL
    ring = np.asarray(c.pattern.outline.exterior.coords)[:-1]
    radii = np.hypot(ring[:, 0], ring[:, 1])
    inner, outer = radii.min(), radii.max()
    assert outer - inner == pytest.approx(math.hypot(length, (d1 - d0) / 2))
    angle = max(math.atan2(y, x) for x, y in ring)
    # Developed arc lengths equal the mid-wall circumferences of the two ends.
    assert inner * angle == pytest.approx(math.pi * (d0 - t), rel=1e-9)
    assert outer * angle == pytest.approx(math.pi * (d1 - t), rel=1e-9)


def test_hex_heads_are_separate_bodies():
    c = parts.hex_heads("b", "bolt", 2, 0.5, [(0, 0, 0), (5, 0, 0), (10, 0, 0)])
    r = inspect(c.mesh)
    assert r["valid"] and r["bodies"] == 3 and c.quantity == 3
    assert c.size_mm["acrossFlats"] == pytest.approx(math.sqrt(3), abs=1e-4)
