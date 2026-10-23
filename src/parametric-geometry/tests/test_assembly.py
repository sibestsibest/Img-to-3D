import math

import numpy as np
import pytest

from parametric.errors import INVALID_PARAMETER, UNSUPPORTED_THICKNESS, ParametricError
from parametric.model import DEVELOPED, FLAT, ROD, SHEET, TUBE, inspect
from parametric.templates import build

ALL_SHAPES = [
    {"name": "block", "shape": "box", "size_x": 10, "size_y": 20, "size_z": 30},
    {"name": "bevel", "shape": "chamfered_box", "size_x": 10, "size_y": 8, "size_z": 4, "chamfer": 1},
    {"name": "cyl", "shape": "cylinder", "diameter": 10, "height": 5},
    {"name": "cone", "shape": "cone", "diameter": 10, "height": 8},
    {"name": "frus", "shape": "frustum", "bottom_diameter": 10, "top_diameter": 6, "height": 8},
    {"name": "ball", "shape": "sphere", "diameter": 12},
    {"name": "dome", "shape": "hemisphere", "diameter": 12},
    {"name": "pipe", "shape": "tube", "outer_diameter": 6, "inner_diameter": 4, "length": 30},
    {"name": "pin", "shape": "rod", "diameter": 2, "length": 15},
    {"name": "rib", "shape": "extrusion", "outline": [[0, 0], [20, 0], [20, 5], [0, 5]], "height": 1},
    {"name": "panel", "shape": "plate", "size_x": 30, "size_y": 20, "thickness": 0.5, "holes": []},
    {"name": "skin", "shape": "shell", "outer_diameter": 40, "thickness": 0.8, "angle_deg": 90, "length": 25},
]


def model(components, **extra):
    return build("assembly", {"components": components, **extra})


def test_every_shape_builds_a_valid_solid():
    m = model(ALL_SHAPES)
    assert [c.name for c in m.components] == [c["name"] for c in ALL_SHAPES]
    for c in m.components:
        assert inspect(c.mesh)["valid"], c.name
    cat = {c.name: c.category for c in m.components}
    assert (cat["panel"], cat["skin"], cat["pin"], cat["pipe"]) == (SHEET, SHEET, ROD, TUBE)
    pat = {c.name: c.pattern.kind if c.pattern else None for c in m.components}
    assert (pat["panel"], pat["skin"], pat["block"]) == (FLAT, DEVELOPED, None)


def test_transform_moves_and_turns_a_part():
    m = model([{"name": "a", "shape": "box", "size_x": 10, "size_y": 2, "size_z": 4,
                "transform": {"translate": [100, 0, 0], "rotate_deg": [0, 0, 90], "scale": [1, 1, 2]}}])
    mesh = m.components[0].mesh
    np.testing.assert_allclose(mesh.extents, [2, 10, 8], atol=1e-9)
    np.testing.assert_allclose(mesh.bounds.mean(axis=0), [100, 0, 0], atol=1e-9)


def test_plate_with_holes_keeps_its_pattern():
    outline = [[0, 0], [40, 0], [40, 30], [0, 30]]
    holes = [[[5, 5], [10, 5], [10, 10], [5, 10]], [[20, 20], [30, 20], [30, 25], [20, 25]]]
    c = model([{"name": "p", "shape": "plate", "outline": outline, "holes": holes, "thickness": 1.5}]).components[0]
    assert len(c.pattern.outline.interiors) == 2
    assert c.mesh.volume == pytest.approx((1200 - 25 - 50) * 1.5)


def test_shell_pattern_is_the_arc_length():
    c = model([{"name": "s", "shape": "shell", "outer_diameter": 40, "thickness": 1, "angle_deg": 120, "length": 25}]).components[0]
    _, _, w, h = c.pattern.outline.bounds
    assert (w, h) == pytest.approx((25, 19.5 * math.radians(120)))


@pytest.mark.parametrize(
    "components, code, field",
    [
        ([], INVALID_PARAMETER, "parameters.components"),
        ([{"shape": "torus"}], INVALID_PARAMETER, "parameters.components[0].shape"),
        ([{"name": "a", "shape": "box", "size_x": 1, "size_y": 1}], INVALID_PARAMETER, "parameters.components[0].size_z"),
        ([{"name": "a", "shape": "box", "size_x": 1, "size_y": 1, "size_z": 0}], INVALID_PARAMETER, "parameters.components[0].size_z"),
        ([{"name": "a", "shape": "box", "size_x": 1, "size_y": 1, "size_z": 1, "colour": "red"}], INVALID_PARAMETER, "parameters.components[0].colour"),
        ([{"name": "a", "shape": "box", "size_x": 1, "size_y": 1, "size_z": 1}] * 2, INVALID_PARAMETER, "parameters.components[1].name"),
        ([{"name": "A B", "shape": "box", "size_x": 1, "size_y": 1, "size_z": 1}], INVALID_PARAMETER, "parameters.components[0].name"),
        ([{"name": "p", "shape": "plate", "size_x": 10, "size_y": 10, "thickness": 0.6}], UNSUPPORTED_THICKNESS, "parameters.components[0].thickness"),
        ([{"name": "p", "shape": "plate", "size_x": 10, "size_y": 10, "thickness": 1, "transform": {"scale": [2, 1, 1]}}], INVALID_PARAMETER, "parameters.components[0].transform.scale"),
        ([{"name": "p", "shape": "box", "size_x": 1, "size_y": 1, "size_z": 1, "transform": {"scale": [-1, 1, 1]}}], INVALID_PARAMETER, "parameters.components[0].transform.scale"),
        ([{"name": "e", "shape": "extrusion", "outline": [[0, 0], [10, 0], [10, 10], [0, 10]], "holes": [[[20, 20], [25, 20], [25, 25]]], "height": 1}], INVALID_PARAMETER, "parameters.components[0].outline"),
        ([{"name": "e", "shape": "extrusion", "outline": [[0, 0], [10, 10], [10, 0], [0, 10]], "height": 1}], INVALID_PARAMETER, "parameters.components[0].outline"),
        ([{"name": "t", "shape": "tube", "outer_diameter": 4, "inner_diameter": 4, "length": 3}], INVALID_PARAMETER, "parameters.components[0].inner_diameter"),
        ([{"name": "c", "shape": "chamfered_box", "size_x": 4, "size_y": 4, "size_z": 1, "chamfer": 2}], INVALID_PARAMETER, "parameters.components[0].chamfer"),
    ],
)
def test_invalid_components_are_refused_with_the_field(components, code, field):
    with pytest.raises(ParametricError) as e:
        model(components)
    assert (e.value.code, e.value.field) == (code, field)


def test_component_limit():
    with pytest.raises(ParametricError) as e:
        model([{"name": f"b{i}", "shape": "box", "size_x": 1, "size_y": 1, "size_z": 1} for i in range(501)])
    assert e.value.code == "limit_exceeded"
