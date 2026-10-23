"""The worker as the application will call it: a separate process, JSON in, JSON out."""

import json
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]


def run(*args, stdin: bytes | None = None):
    p = subprocess.run([sys.executable, "-m", "parametric", *args], cwd=ROOT, input=stdin, capture_output=True, timeout=120)
    return p.returncode, json.loads(p.stdout), p.stderr.decode("utf-8", "replace")


def write(tmp_path, obj) -> str:
    f = tmp_path / "request.json"
    f.write_text(json.dumps(obj), encoding="utf-8")
    return str(f)


def test_describe_lists_templates_and_sheet_thicknesses():
    code, out, _ = run("describe")
    assert code == 0
    assert out["sheetThicknessesMm"] == [0.3, 0.5, 0.8, 1.0, 1.5, 2.0, 3.0]
    names = {t["name"]: t for t in out["templates"]}
    assert set(names) == {"spacecraft", "assembly"}
    sheet = next(p for p in names["spacecraft"]["parameters"] if p["name"] == "sheet_thickness")
    assert sheet["allowed"] == out["sheetThicknessesMm"] and sheet["default"] == 1.0
    assert "plate" in names["assembly"]["shapes"]


def test_generate_writes_files_and_response(tmp_path):
    out = tmp_path / "out"
    code, resp, _ = run("generate", "--request", write(tmp_path, {"schemaVersion": 1, "template": "spacecraft", "parameters": {"length": 200}, "exports": ["glb", "bom"]}), "--output", str(out))
    assert code == 0 and resp["status"] == "succeeded"
    assert json.loads((out / "response.json").read_text("utf-8")) == resp
    assert (out / "model.glb").is_file() and (out / "bom.csv").is_file()
    assert resp["dimensions"]["size"][0] == 200


def test_request_from_stdin(tmp_path):
    req = json.dumps({"schemaVersion": 1, "template": "spacecraft", "exports": ["stl"]}).encode()
    code, resp, _ = run("generate", "--request", "-", "--output", str(tmp_path / "o"), stdin=req)
    assert code == 0 and [o["path"] for o in resp["outputs"]] == ["model.stl"]


@pytest.mark.parametrize(
    "request_obj, code, field",
    [
        ({"schemaVersion": 2, "template": "spacecraft"}, "invalid_request", "schemaVersion"),
        ({"schemaVersion": 1, "template": "rocket"}, "unknown_template", "template"),
        ({"schemaVersion": 1, "template": "spacecraft", "parameters": {"sheet_thickness": 0.7}}, "unsupported_thickness", "parameters.sheet_thickness"),
        ({"schemaVersion": 1, "template": "spacecraft", "parameters": {"height": 30}}, "geometry_conflict", "parameters.height"),
        ({"schemaVersion": 1, "template": "spacecraft", "exports": ["fbx"]}, "invalid_request", "exports"),
        ({"schemaVersion": 1, "template": "spacecraft", "command": "rm"}, "invalid_request", "command"),
    ],
)
def test_refusals_exit_2_with_a_coded_error(tmp_path, request_obj, code, field):
    out = tmp_path / "out"
    exit_code, resp, _ = run("generate", "--request", write(tmp_path, request_obj), "--output", str(out))
    assert exit_code == 2
    assert resp["status"] == "failed" and resp["error"]["code"] == code and resp["error"]["field"] == field
    assert resp["outputs"] == []


def test_invalid_json_is_refused(tmp_path):
    f = tmp_path / "bad.json"
    f.write_text("{not json", encoding="utf-8")
    code, resp, _ = run("generate", "--request", str(f), "--output", str(tmp_path / "o"))
    assert code == 2 and resp["error"]["code"] == "invalid_request"


def test_non_empty_output_directory_is_left_untouched(tmp_path):
    out = tmp_path / "existing"
    out.mkdir()
    (out / "keep.txt").write_text("user file", encoding="utf-8")
    code, resp, _ = run("generate", "--request", write(tmp_path, {"schemaVersion": 1, "template": "spacecraft"}), "--output", str(out))
    assert code == 2 and resp["error"]["code"] == "invalid_output_directory"
    assert sorted(p.name for p in out.iterdir()) == ["keep.txt"]
