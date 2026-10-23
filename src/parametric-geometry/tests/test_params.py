import math

import pytest

from parametric.errors import INVALID_PARAMETER, UNSUPPORTED_THICKNESS, ParametricError
from parametric.params import PS_SHEET_THICKNESSES_MM, Param, read, sheet_thickness

SPEC = [Param("length", 10, 1, 100), Param("count", 2, 0, 5, unit="", integer=True), Param("t", 1.0, 0.3, 3, sheet=True)]


def test_ps_sheet_thicknesses_are_the_scratch_building_set():
    assert PS_SHEET_THICKNESSES_MM == (0.3, 0.5, 0.8, 1.0, 1.5, 2.0, 3.0)


@pytest.mark.parametrize("t", PS_SHEET_THICKNESSES_MM)
def test_every_ps_thickness_is_accepted(t):
    assert sheet_thickness(t, "t") == t


@pytest.mark.parametrize("t", [0.4, 1.2, 0, -1, 2.5, 4])
def test_other_thicknesses_are_refused(t):
    with pytest.raises(ParametricError) as e:
        sheet_thickness(t, "parameters.t")
    assert e.value.code == UNSUPPORTED_THICKNESS and e.value.field == "parameters.t"
    assert "0.3, 0.5, 0.8, 1, 1.5, 2, 3" in e.value.message


def test_defaults_are_filled_in():
    assert read({}, SPEC) == {"length": 10.0, "count": 2, "t": 1.0}


@pytest.mark.parametrize(
    "params, field",
    [
        ({"lenght": 5}, "p.lenght"),  # typo is never silently ignored
        ({"length": 0.5}, "p.length"),
        ({"length": 101}, "p.length"),
        ({"length": "10"}, "p.length"),
        ({"length": True}, "p.length"),
        ({"length": math.nan}, "p.length"),
        ({"length": math.inf}, "p.length"),
        ({"count": 1.5}, "p.count"),
        ({"count": 6}, "p.count"),
    ],
)
def test_invalid_values_name_the_field(params, field):
    with pytest.raises(ParametricError) as e:
        read(params, SPEC, "p.")
    assert e.value.code == INVALID_PARAMETER and e.value.field == field


def test_parameters_must_be_an_object():
    with pytest.raises(ParametricError):
        read([1, 2], SPEC)
