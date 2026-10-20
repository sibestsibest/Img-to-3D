"""Contract tests for the SF3D service using a fake engine (no model, no GPU)."""

from __future__ import annotations

import io
import time
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from PIL import Image

from app.config import Settings
from app.engine import GenerationError, GenerationResult
from app.main import create_app


def png_bytes() -> bytes:
    buf = io.BytesIO()
    Image.new("RGB", (64, 64), "white").save(buf, format="PNG")
    return buf.getvalue()


class FakeEngine:
    execution_provider = "Cpu"
    failure_reason = None

    def __init__(self, mode: str = "ok", delay: float = 0.0):
        self.state = "Ready"
        self.mode = mode
        self.delay = delay

    def ensure_loaded(self):
        pass

    def generate(self, image_bytes, progress, is_cancelled, extras=None):
        self.last_extras = extras
        progress("GeneratingMesh", None)
        deadline = time.time() + self.delay
        while time.time() < deadline:
            time.sleep(0.01)
        if is_cancelled():
            raise GenerationError("cancelled", "Generation was cancelled.")
        if self.mode == "fail":
            raise GenerationError("no_foreground", "No foreground object was detected in the image.")
        if self.mode == "crash":
            raise RuntimeError("boom at C:/secret/path")
        return GenerationResult(b"glTF-fake", png_bytes(), 8, 12, (0.5, 1.0, 0.25), {"generate": 0.1})


def make_client(tmp_path: Path, engine: FakeEngine, **overrides) -> TestClient:
    settings = Settings(work_dir=tmp_path, load_on_startup=False, **overrides)
    return TestClient(create_app(settings, engine))


def wait_terminal(client: TestClient, job_id: str, headers=None) -> dict:
    for _ in range(500):
        body = client.get(f"/internal/jobs/{job_id}", headers=headers).json()
        if body["status"] in ("Succeeded", "Failed", "Cancelled"):
            return body
        time.sleep(0.01)
    raise AssertionError("job did not finish")


def submit(client: TestClient, data: bytes | None = None, headers=None):
    return client.post("/internal/jobs/", files={"image": ("x.png", data or png_bytes(), "image/png")}, headers=headers)


def test_health_reports_ready(tmp_path):
    with make_client(tmp_path, FakeEngine()) as client:
        response = client.get("/health")
    assert response.status_code == 200
    assert response.json() == {"status": "ok", "modelState": "Ready", "reason": None, "executionProvider": "Cpu", "engine": "StableFast3D", "queuedJobs": 0}


def test_health_not_ready_is_503(tmp_path):
    engine = FakeEngine()
    engine.state = "Failed"
    with make_client(tmp_path, engine) as client:
        assert client.get("/health").status_code == 503


def test_successful_job_matches_dotnet_contract(tmp_path):
    with make_client(tmp_path, FakeEngine()) as client:
        created = submit(client)
        assert created.status_code == 202
        job = wait_terminal(client, created.json()["id"])
        assert job["status"] == "Succeeded"
        assert job["mesh"] == {"vertices": 8, "triangles": 12, "sizeX": 0.5, "sizeY": 1.0, "sizeZ": 0.25}
        assert {a["name"] for a in job["artifacts"]} == {"mesh.glb", "input.png"}
        glb = client.get(f"/internal/jobs/{job['id']}/artifacts/mesh.glb")
        assert glb.status_code == 200 and glb.content == b"glTF-fake"
        assert glb.headers["content-type"] == "model/gltf-binary"


def test_rejects_non_image_and_missing_file(tmp_path):
    with make_client(tmp_path, FakeEngine()) as client:
        bad = submit(client, b"GIF89a....")
        assert bad.status_code == 400 and bad.json()["code"] == "invalid_image"
        assert client.post("/internal/jobs/").status_code == 400


def test_rejects_oversized_upload(tmp_path):
    with make_client(tmp_path, FakeEngine(), max_image_bytes=100) as client:
        assert submit(client, png_bytes() + b"\0" * 200).status_code == 413


def test_generation_error_keeps_code(tmp_path):
    with make_client(tmp_path, FakeEngine("fail")) as client:
        job = wait_terminal(client, submit(client).json()["id"])
    assert job["status"] == "Failed" and job["errorCode"] == "no_foreground"


def test_unexpected_error_does_not_leak_details(tmp_path):
    with make_client(tmp_path, FakeEngine("crash")) as client:
        job = wait_terminal(client, submit(client).json()["id"])
    assert job["errorCode"] == "internal_error"
    assert "secret" not in job["error"]


def test_cancel_running_job(tmp_path):
    with make_client(tmp_path, FakeEngine(delay=0.5)) as client:
        job_id = submit(client).json()["id"]
        time.sleep(0.1)
        assert client.post(f"/internal/jobs/{job_id}/cancel").status_code == 202
        job = wait_terminal(client, job_id)
        assert job["status"] == "Cancelled"
        assert client.post(f"/internal/jobs/{job_id}/cancel").status_code == 409


def test_queue_full_returns_503(tmp_path):
    with make_client(tmp_path, FakeEngine(delay=1.0), max_queue=1) as client:
        codes = [submit(client).status_code for _ in range(4)]
    assert 503 in codes


@pytest.mark.parametrize("name", ["..%2F..%2Fsecret.txt", "secret.txt", "MESH.GLB"])
def test_artifact_whitelist(tmp_path, name):
    with make_client(tmp_path, FakeEngine()) as client:
        job = wait_terminal(client, submit(client).json()["id"])
        assert client.get(f"/internal/jobs/{job['id']}/artifacts/{name}").status_code == 404


def test_delete_removes_job(tmp_path):
    with make_client(tmp_path, FakeEngine()) as client:
        job = wait_terminal(client, submit(client).json()["id"])
        assert client.delete(f"/internal/jobs/{job['id']}").status_code == 204
        assert client.get(f"/internal/jobs/{job['id']}").status_code == 404
        assert not any(tmp_path.iterdir())


def test_api_key_required_when_configured(tmp_path):
    with make_client(tmp_path, FakeEngine(), api_key="secret-key-123") as client:
        assert submit(client).status_code == 401
        assert submit(client, headers={"X-Internal-Api-Key": "wrong"}).status_code == 401
        assert submit(client, headers={"X-Internal-Api-Key": "secret-key-123"}).status_code == 202
        assert client.get("/health").status_code == 200  # probes stay open


def test_correlation_id_is_echoed(tmp_path):
    with make_client(tmp_path, FakeEngine()) as client:
        response = client.get("/health", headers={"X-Correlation-ID": "corr-12345678"})
    assert response.headers["x-correlation-id"] == "corr-12345678"
