"""FastAPI app implementing the same /internal/jobs contract as the .NET TripoSR service
(see src/shared/ImageTo3D.Core/Contracts/InferenceContracts.cs), so the ASP.NET Core API can
switch engines by changing InferenceService:BaseUrl only.

Run: uvicorn app.main:app --host 127.0.0.1 --port 5091
"""

from __future__ import annotations

import hmac
import logging
import re
import threading
import uuid
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, Header, Request, UploadFile
from fastapi.responses import FileResponse, JSONResponse

from .config import Settings
from .engine import Engine, Sf3dEngine
from .jobs import ARTIFACTS, JobManager

logging.basicConfig(level=logging.INFO, format='{"time":"%(asctime)s","level":"%(levelname)s","logger":"%(name)s","message":"%(message)s"}')
log = logging.getLogger("sf3d.api")

SIGNATURES = (b"\x89PNG\r\n\x1a\n", b"\xff\xd8\xff")
CORRELATION = re.compile(r"^[A-Za-z0-9-]{8,64}$")


def problem(status: int, title: str, detail: str, code: str | None = None) -> JSONResponse:
    body = {"type": "about:blank", "title": title, "status": status, "detail": detail}
    if code:
        body["code"] = code
    return JSONResponse(body, status_code=status, media_type="application/problem+json")


def is_supported_image(data: bytes) -> bool:
    return data.startswith(SIGNATURES) or (len(data) >= 12 and data[:4] == b"RIFF" and data[8:12] == b"WEBP")


def create_app(settings: Settings | None = None, engine: Engine | None = None) -> FastAPI:
    settings = settings or Settings()
    engine = engine or Sf3dEngine(settings)
    manager = JobManager(engine, settings.work_dir, settings.max_queue, settings.retention_seconds)

    @asynccontextmanager
    async def lifespan(_: FastAPI):
        manager.start()
        if settings.load_on_startup:
            threading.Thread(target=_load_quietly, args=(engine,), daemon=True).start()
        yield
        manager.stop()

    app = FastAPI(title="ImageTo3D SF3D inference", lifespan=lifespan, docs_url=None, redoc_url=None, openapi_url=None)
    app.state.manager = manager

    @app.middleware("http")
    async def correlation(request: Request, call_next):
        incoming = request.headers.get("x-correlation-id", "")
        cid = incoming if CORRELATION.match(incoming) else uuid.uuid4().hex
        response = await call_next(request)
        response.headers["X-Correlation-ID"] = cid
        return response

    def require_key(x_internal_api_key: str = Header(default="")) -> None:
        if settings.api_key and not hmac.compare_digest(x_internal_api_key.encode(), settings.api_key.encode()):
            raise _Unauthorized()

    @app.exception_handler(_Unauthorized)
    async def unauthorized(_: Request, __: Exception):
        return problem(401, "Unauthorized", "Missing or invalid internal API key.")

    @app.exception_handler(Exception)
    async def unhandled(_: Request, exc: Exception):
        log.exception("unhandled error")
        return problem(500, "Internal server error", "An unexpected error occurred.", "internal_error")

    @app.get("/health")
    def health():
        status = {"Ready": "ok", "Loading": "loading", "NotLoaded": "loading"}.get(engine.state, "unavailable")
        body = {
            "status": status,
            "modelState": engine.state,
            "reason": engine.failure_reason,
            "executionProvider": engine.execution_provider,
            "engine": "StableFast3D",
            "queuedJobs": manager.queued_count(),
        }
        return JSONResponse(body, status_code=200 if engine.state == "Ready" else 503)

    @app.post("/internal/jobs/", dependencies=[Depends(require_key)], status_code=202)
    async def create_job(image: UploadFile | None = None):
        if image is None:
            return problem(400, "Invalid request", "The 'image' file is required.")
        data = await image.read(settings.max_image_bytes + 1)
        if not data:
            return problem(400, "Invalid request", "The 'image' file is required.")
        if len(data) > settings.max_image_bytes:
            return problem(413, "Image too large", f"Images must be at most {settings.max_image_bytes // (1024 * 1024)} MB.")
        if not is_supported_image(data):
            return problem(400, "Unsupported image", "Upload a PNG, JPEG or WebP image.", "invalid_image")
        job = manager.submit(data)
        if job is None:
            return problem(503, "Queue full", "The inference queue is full. Try again later.", "queue_full")
        log.info("job %s queued (%d bytes)", job.id, len(data))
        return JSONResponse(job.to_dto(), status_code=202, headers={"Location": f"/internal/jobs/{job.id}"})

    @app.get("/internal/jobs/{job_id}", dependencies=[Depends(require_key)])
    def get_job(job_id: str):
        job = manager.get(job_id)
        return job.to_dto() if job else problem(404, "Not found", "Job or artifact not found.")

    @app.get("/internal/jobs/{job_id}/artifacts/{name}", dependencies=[Depends(require_key)])
    def get_artifact(job_id: str, name: str):
        # Whitelisted names only: no request data reaches the filesystem path.
        job = manager.get(job_id)
        if name not in ARTIFACTS or job is None or job.status != "Succeeded":
            return problem(404, "Not found", "Job or artifact not found.")
        path = job.directory / name
        return FileResponse(path, media_type=ARTIFACTS[name]) if path.exists() else problem(404, "Not found", "Job or artifact not found.")

    @app.post("/internal/jobs/{job_id}/cancel", dependencies=[Depends(require_key)], status_code=202)
    def cancel_job(job_id: str):
        job = manager.get(job_id)
        if job is None:
            return problem(404, "Not found", "Job or artifact not found.")
        if job.status in ("Succeeded", "Failed", "Cancelled"):
            return problem(409, "Job already finished", "The job has already reached a final state.")
        manager.cancel(job_id)
        return JSONResponse(job.to_dto(), status_code=202)

    @app.delete("/internal/jobs/{job_id}", dependencies=[Depends(require_key)], status_code=204)
    def delete_job(job_id: str):
        if not manager.delete(job_id):
            return problem(404, "Not found", "Job or artifact not found.")
        return JSONResponse(None, status_code=204)

    return app


class _Unauthorized(Exception):
    pass


def _load_quietly(engine: Engine) -> None:
    try:
        engine.ensure_loaded()
    except Exception:  # noqa: BLE001 - surfaced through /health
        log.warning("startup model load failed; /health reports the reason")


app = create_app()
