import math

import numpy as np
import pytest

from parametric import bom
from parametric.errors import GEOMETRY_CONFLICT, ParametricError
from parametric.model import FLAT, SHEET, inspect
from parametric.params import PS_SHEET_THICKNESSES_MM
from parametric.templates import build

HULL_ROLES = {"hull_shell", "deck", "top_plate", "side_wall", "end_wall", "bulkhead", "rib"}


def hull_bounds(model):
    hull = [c for c in model.components if c.role in HULL_ROLES and not c.name.startswith("engine")]
    return np.min([c.mesh.bounds[0] for c in hull], axis=0), np.max([c.mesh.bounds[1] for c in hull], axis=0)


def by_name(model):
    return {c.name: c for c in model.components}


@pytest.mark.parametrize(
    "params",
    [
        {},
        {"length": 300, "width": 100, "height": 90, "sheet_thickness": 1.5},
        {"length": 120, "width": 40, "height": 30, "sheet_thickness": 0.5, "panel_thickness": 0.3, "engine_diameter": 24,
         "engine_length": 20, "exhaust_length": 10, "panel_rows": 1, "panel_margin": 2.5, "vent_slot_length": 10, "vent_slot_width": 1,
         "bolt_pitch": 8, "bolt_diameter": 1, "antenna_diameter": 1, "rib_depth": 3},
        {"length": 600, "width": 160, "height": 150, "sheet_thickness": 3.0, "panel_thickness": 2.0, "engine_diameter": 120,
         "engine_length": 100, "exhaust_length": 50, "panel_rows": 3, "panel_columns": 8, "bulkheads": 6, "panel_margin": 6},
    ],
)
def test_known_dimensions(params):
    m = build("spacecraft", params)
    p = m.parameters
    length, width, height, t = p["length"], p["width"], p["height"], p["sheet_thickness"]

    lo, hi = m.bounds()
    # Overall length is exact: engine and exhaust included.
    assert lo[0] == pytest.approx(-length / 2) and hi[0] == pytest.approx(length / 2)
    assert lo[1] == pytest.approx(0)

    # The structural hull is exactly width x height and starts behind the engine.
    hlo, hhi = hull_bounds(m)
    hull_length = length - p["engine_length"] - p["exhaust_length"]
    np.testing.assert_allclose(hhi - hlo, [hull_length, height, width], atol=1e-6)

    # Side armor adds its thickness on both sides; antennas stand on the frame.
    panels = p["panel_rows"] * p["panel_columns"] > 0
    assert hi[2] - lo[2] == pytest.approx(width + (2 * p["panel_thickness"] if panels else 0))
    assert hi[1] == pytest.approx(height + p["panel_thickness"] + p["antenna_length"])
    assert m.derived["overallSizeMm"] == pytest.approx(list(hi - lo))

    # The lower hull is a semicircle of radius width/2.
    shell = by_name(m)["lower_hull"].mesh
    radii = np.hypot(shell.vertices[:, 1] - width / 2, shell.vertices[:, 2])
    assert radii.max() == pytest.approx(width / 2) and radii.min() == pytest.approx(width / 2 - t)
    assert shell.bounds[1][1] == pytest.approx(width / 2)

    # Engine housing and exhaust.
    c = by_name(m)
    np.testing.assert_allclose(c["engine_housing"].mesh.extents, [p["engine_length"], p["engine_diameter"], p["engine_diameter"]], atol=1e-6)
    exit_d = p["engine_diameter"] * p["exhaust_exit_ratio"]
    np.testing.assert_allclose(c["exhaust"].mesh.extents, [p["exhaust_length"], exit_d, exit_d], atol=1e-6)
    assert c["exhaust"].mesh.bounds[0][0] == pytest.approx(-length / 2)


def test_every_component_is_a_valid_solid_with_its_quantity():
    for c in build("spacecraft", {}).components:
        r = inspect(c.mesh)
        assert r["valid"], (c.name, r)
        assert r["bodies"] == c.quantity, c.name


@pytest.mark.parametrize("t", PS_SHEET_THICKNESSES_MM)
def test_flat_sheet_parts_have_the_selected_thickness(t):
    # Panels use the thinnest sheet that still fits; with 3 mm structure the default margins are fine.
    m = build("spacecraft", {"sheet_thickness": t, "panel_thickness": t})
    flat = [c for c in m.components if c.category == SHEET and c.pattern.kind == FLAT]
    assert len(flat) > 10
    for c in flat:
        assert c.thickness_mm == t
        # volume = cutting outline area x thickness: the template is exactly the part.
        assert c.mesh.volume == pytest.approx(c.pattern.outline.area * t, rel=1e-9), c.name
        assert min(c.mesh.extents) == pytest.approx(t), c.name


def test_armor_panel_grid():
    m = build("spacecraft", {"panel_rows": 3, "panel_columns": 5, "panel_margin": 4, "panel_gap": 2})
    panels = [c for c in m.components if c.role == "armor_panel"]
    assert len(panels) == 2 * 3 * 5
    hull_length = m.derived["hullLengthMm"]
    expected_w = (hull_length - 8 - 4 * 2) / 5
    expected_h = (m.derived["upperHullHeightMm"] - 8 - 2 * 2) / 3
    for c in panels:
        assert c.size_mm["length"] == pytest.approx(expected_w) and c.size_mm["width"] == pytest.approx(expected_h)


def test_bom_groups_identical_parts():
    m = build("spacecraft", {})
    items = {it.first.role + ("_engine" if it.first.name == "engine_mount" else ""): it for it in bom.items(m.components)}
    assert items["armor_panel"].quantity == 16
    assert items["rib"].quantity == 3
    assert items["bulkhead"].quantity == 2 and items["bulkhead_engine"].quantity == 1
    assert items["side_wall"].quantity == 2
    bolts = items["bolt"]
    span = m.derived["hullLengthMm"] - 8
    assert bolts.quantity == 2 * (math.floor(span / 12) + 1)


def test_rolled_parts_are_reported():
    m = build("spacecraft", {})
    forming = {i.component for i in m.issues if i.code == "requires_forming"}
    assert forming == {"lower_hull", "engine_housing", "exhaust"}


def test_building_twice_gives_identical_geometry():
    a, b = build("spacecraft", {"length": 333.3}), build("spacecraft", {"length": 333.3})
    for x, y in zip(a.components, b.components):
        assert x.name == y.name
        assert np.array_equal(x.mesh.vertices, y.mesh.vertices) and np.array_equal(x.mesh.faces, y.mesh.faces)


def test_optional_details_can_be_switched_off():
    m = build("spacecraft", {"panel_rows": 0, "bulkheads": 0, "top_recesses": 0, "vent_slots": 0, "bolt_pitch": 0, "antennas": 0})
    roles = {c.role for c in m.components}
    assert roles.isdisjoint({"armor_panel", "rib", "recess_frame", "vent_panel", "bolt", "antenna"})
    lo, hi = m.bounds()
    assert hi[1] - lo[1] == pytest.approx(70) and hi[2] - lo[2] == pytest.approx(80)


@pytest.mark.parametrize(
    "params, field",
    [
        ({"height": 41}, "parameters.height"),  # 40 (semicircle) + 2 x 1 mm sheet leaves no upper hull
        ({"engine_diameter": 90}, "parameters.engine_diameter"),
        ({"exhaust_exit_ratio": 2.0, "engine_diameter": 48}, "parameters.engine_diameter"),
        ({"engine_axis_height": 10}, "parameters.engine_axis_height"),
        ({"engine_length": 150, "exhaust_length": 80}, "parameters.length"),
        ({"panel_columns": 30, "panel_gap": 10}, "parameters.panel_columns"),
        ({"vent_slots": 20}, "parameters.vent_slots"),
        ({"bolt_diameter": 5}, "parameters.bolt_diameter"),
        ({"top_recesses": 0}, "parameters.antennas"),
        ({"rib_depth": 50}, "parameters.rib_depth"),
    ],
)
def test_impossible_geometry_is_refused_with_the_field(params, field):
    with pytest.raises(ParametricError) as e:
        build("spacecraft", params)
    assert e.value.code == GEOMETRY_CONFLICT and e.value.field == field
    assert e.value.message


def test_unknown_template():
    with pytest.raises(ParametricError) as e:
        build("rocket", {})
    assert e.value.code == "unknown_template"
