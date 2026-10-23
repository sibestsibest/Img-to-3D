import csv
import hashlib
import io
import json
import re
import xml.etree.ElementTree as ET

import ezdxf
import numpy as np
import pytest
import trimesh

from parametric import bom
from parametric.worker import EXPORTS, generate

SPACECRAFT = {"schemaVersion": 1, "template": "spacecraft", "parameters": {}}


@pytest.fixture(scope="module")
def result(tmp_path_factory):
    out = tmp_path_factory.mktemp("spacecraft") / "out"
    return generate(SPACECRAFT, out), out


def test_outputs_match_the_files_written(result):
    r, out = result
    on_disk = sorted(p.relative_to(out).as_posix() for p in out.rglob("*") if p.is_file())
    assert on_disk == sorted(o["path"] for o in r["outputs"])
    for o in r["outputs"]:
        data = (out / o["path"]).read_bytes()
        assert o["bytes"] == len(data) and o["sha256"] == hashlib.sha256(data).hexdigest()
    assert {o["kind"] for o in r["outputs"]} >= {"glb", "stl", "obj", "bom", "template_svg", "template_dxf", "templates_pdf", "views_svg", "views_pdf"}


def test_response_contract(result):
    r, _ = result
    assert (r["status"], r["origin"], r["engine"]["name"], r["units"], r["upAxis"]) == ("succeeded", "parametric", "Parametric", "mm", "y")
    assert r["scale"] == {"status": "exact", "mmPerUnit": 1.0}
    assert r["dimensions"]["size"] == [240.0, 95.5, 81.0]
    assert r["validation"]["valid"] and r["validation"]["closedComponents"] == r["validation"]["components"]
    assert r["error"] is None
    json.dumps(r)  # serialisable


def test_glb_has_one_node_per_component_in_metres(result):
    r, out = result
    scene = trimesh.load(out / "model.glb", file_type="glb")
    names = [c["name"] for c in r["components"]]
    assert sorted(scene.geometry) == sorted(names)
    np.testing.assert_allclose(scene.bounds, np.array([r["dimensions"]["min"], r["dimensions"]["max"]]) / 1000, atol=1e-7)
    for name in names:
        assert scene.geometry[name].is_watertight, name


def test_stl_is_millimetres_z_up(result):
    r, out = result
    mesh = trimesh.load(out / "model.stl", file_type="stl")
    x, y, z = r["dimensions"]["size"]
    np.testing.assert_allclose(mesh.extents, [x, z, y], atol=1e-4)
    assert mesh.bounds[0][2] == pytest.approx(0, abs=1e-6)


def test_obj_has_named_groups_in_millimetres(result):
    r, out = result
    text = (out / "model.obj").read_text("ascii")
    groups = re.findall(r"^o (\S+)$", text, re.M)
    assert groups == [c["name"] for c in r["components"]]
    vertices = sum(c["validation"]["vertexCount"] for c in r["components"])
    assert len(re.findall(r"^v ", text, re.M)) == vertices
    scene = trimesh.load(out / "model.obj", file_type="obj", group_material=False, split_object=True)
    np.testing.assert_allclose(scene.bounds, [r["dimensions"]["min"], r["dimensions"]["max"]], atol=1e-4)


def test_bom_rows_and_quantities(result):
    r, out = result
    rows = list(csv.DictReader(io.StringIO((out / "bom.csv").read_bytes().decode("utf-8-sig"))))
    assert len(rows) == len(r["bom"])
    assert sum(int(row["quantity"]) for row in rows) == sum(c["quantity"] for c in r["components"])
    armor = next(row for row in rows if row["role"] == "armor_panel")
    assert (armor["quantity"], armor["thickness_mm"], armor["pattern"]) == ("16", "0.5", "flat")
    assert armor["template"].endswith("armor_panel.svg")
    shell = next(row for row in rows if row["role"] == "hull_shell")
    assert shell["forming"] == "roll" and "Roll to 80 mm" in shell["notes"]


def test_bom_neutralises_formulas():
    assert bom._text("=SUM(A1)") == "'=SUM(A1)" and bom._text("-1") == "'-1" and bom._text("deck") == "deck"


def test_svg_template_is_one_to_one(result):
    r, out = result
    svg = next(o for o in r["outputs"] if o["kind"] == "template_svg" and "armor_panel" in o["path"])
    root = ET.parse(out / svg["path"]).getroot()
    ns = {"s": "http://www.w3.org/2000/svg"}
    assert root.get("width").endswith("mm")
    w, h = float(root.get("width")[:-2]), float(root.get("height")[:-2])
    assert root.get("viewBox") == f"0 0 {w:g} {h:g}"  # 1 user unit = 1 mm
    cut = root.find("s:g[@id='cut']", ns)
    paths = cut.findall("s:path", ns)
    assert len(paths) == 1
    xs = [float(x) for x, _ in re.findall(r"(-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?)", paths[0].get("d"))]
    panel = next(c for c in r["components"] if c["role"] == "armor_panel")
    assert max(xs) - min(xs) == pytest.approx(panel["sizeMm"]["length"], abs=1e-3)


def test_vent_template_has_its_slots(result):
    r, out = result
    svg = next(o for o in r["outputs"] if o["kind"] == "template_svg" and "vent_panel" in o["path"])
    root = ET.parse(out / svg["path"]).getroot()
    cut = root.find("{http://www.w3.org/2000/svg}g[@id='cut']")
    assert len(cut) == 1 + 5


def test_dxf_template_is_millimetres_and_closed(result):
    r, out = result
    dxf = next(o for o in r["outputs"] if o["kind"] == "template_dxf" and "recess_frame" in o["path"])
    doc = ezdxf.readfile(out / dxf["path"])
    assert doc.units == ezdxf.units.MM
    cuts = [e for e in doc.modelspace() if e.dxf.layer == "CUT"]
    assert len(cuts) == 1 + 2 and all(e.closed for e in cuts)
    pts = np.array([p[:2] for e in cuts for p in e.get_points()])
    frame = next(c for c in r["components"] if c["role"] == "recess_frame")
    assert pts[:, 0].max() - pts[:, 0].min() == pytest.approx(frame["sizeMm"]["length"], abs=1e-5)


def test_pdfs_have_expected_pages(result):
    r, out = result
    pdf = (out / "templates/templates.pdf").read_bytes()
    assert pdf.startswith(b"%PDF")
    templates = [o for o in r["outputs"] if o["kind"] == "template_svg"]
    assert next(o for o in r["outputs"] if o["kind"] == "templates_pdf")["pages"] == len(templates)
    views = (out / "views/views.pdf").read_bytes()
    assert b"/MediaBox [ 0 0 1190.551 841.8898 ]" in views  # A3 landscape


def test_views_sheet_states_its_scale(result):
    r, out = result
    v = next(o for o in r["outputs"] if o["kind"] == "views_svg")
    assert v["scale"] == "1:1"
    text = (out / v["path"]).read_text("utf-8")
    for label in ("SIDE (from +Z)", "TOP (from +Y)", "FRONT (from +X)", "SECTION A-A at x = 30.00", "240.00", "95.50", "81.00", "Scale 1:1"):
        assert label in text


def test_large_model_views_use_a_reduced_standard_scale(tmp_path):
    r = generate({**SPACECRAFT, "parameters": {"length": 1500, "width": 400, "height": 350, "engine_diameter": 300, "engine_length": 200, "exhaust_length": 100, "exhaust_exit_ratio": 1.1}, "exports": ["views"]}, tmp_path / "o")
    # 1901 x 776 mm of views do not fit A3 at 1:5; 1:10 is the next standard scale.
    assert r["outputs"][0]["scale"] == "1:10"


def test_identical_requests_give_identical_files(tmp_path):
    a = generate(SPACECRAFT, tmp_path / "a")
    b = generate(SPACECRAFT, tmp_path / "b")
    assert [(o["path"], o["sha256"]) for o in a["outputs"]] == [(o["path"], o["sha256"]) for o in b["outputs"]]


def test_only_requested_exports_are_written(tmp_path):
    r = generate({**SPACECRAFT, "exports": ["stl"]}, tmp_path / "o")
    assert [o["path"] for o in r["outputs"]] == ["model.stl"]
    assert sorted(p.name for p in (tmp_path / "o").iterdir()) == ["model.stl"]
    assert set(EXPORTS) == {"glb", "stl", "obj", "bom", "templates", "views"}


def test_plate_larger_than_a3_gets_a_warning_and_a_custom_page(tmp_path):
    req = {"schemaVersion": 1, "template": "assembly", "parameters": {"components": [{"name": "big", "shape": "plate", "size_x": 500, "size_y": 300, "thickness": 2}]}, "exports": ["templates"]}
    r = generate(req, tmp_path / "o")
    assert [i["code"] for i in r["issues"]] == ["template_too_large"]
    svg = next(o for o in r["outputs"] if o["kind"] == "template_svg")
    assert svg["page"].startswith("custom")
    doc = ezdxf.readfile(tmp_path / "o" / next(o for o in r["outputs"] if o["kind"] == "template_dxf")["path"])
    assert len([e for e in doc.modelspace() if e.dxf.layer == "CUT"]) == 1
