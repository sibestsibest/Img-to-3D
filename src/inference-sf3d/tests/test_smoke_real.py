"""Real-model smoke test. Runs only when SF3D can actually load (extensions built, weights accessible).

    $env:SF3D_SMOKE = "1"; .venv\\Scripts\\python -m pytest tests/test_smoke_real.py -s
"""

from __future__ import annotations

import io
import os
import struct

import pytest

from app.config import REPO_ROOT, Settings
from app.engine import Sf3dEngine

pytestmark = pytest.mark.skipif(os.environ.get("SF3D_SMOKE") != "1", reason="set SF3D_SMOKE=1 to run the real SF3D model")


def test_generates_textured_glb_from_sample():
    import trimesh

    sample = REPO_ROOT / "samples" / "chair.png"
    engine = Sf3dEngine(Settings())
    stages = []
    result = engine.generate(sample.read_bytes(), lambda s, f: stages.append(s), lambda: False)

    assert result.glb[:4] == b"glTF"
    assert struct.unpack("<I", result.glb[4:8])[0] == 2
    assert result.triangles > 1000
    sx, sy, sz = result.size
    assert sy > sx and sy > sz, f"chair should be taller than wide/deep, got {result.size}"

    scene = trimesh.load(io.BytesIO(result.glb), file_type="glb")
    mesh = next(iter(scene.geometry.values()))
    assert mesh.visual.kind == "texture", "SF3D output must carry a UV texture"
    assert mesh.visual.material.baseColorTexture is not None
    assert "GeneratingMesh" in stages
