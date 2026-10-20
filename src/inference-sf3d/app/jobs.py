"""In-memory job store and single-threaded worker (one generation at a time bounds memory)."""

from __future__ import annotations

import logging
import queue
import shutil
import threading
import time
import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path

from .engine import Engine, GenerationError

log = logging.getLogger("sf3d.jobs")

ARTIFACTS = {"mesh.glb": "model/gltf-binary", "input.png": "image/png"}
TERMINAL = {"Succeeded", "Failed", "Cancelled"}


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


@dataclass
class Job:
    id: str
    directory: Path
    image: bytes | None
    status: str = "Queued"
    stage: str = "Queued"
    stage_progress: float | None = None
    error_code: str | None = None
    error: str | None = None
    created_at: str = field(default_factory=_now)
    started_at: str | None = None
    completed_at: str | None = None
    completed_monotonic: float | None = None
    mesh: dict | None = None
    cancel_requested: bool = False

    def to_dto(self) -> dict:
        artifacts = []
        if self.status == "Succeeded":
            for name, content_type in ARTIFACTS.items():
                path = self.directory / name
                if path.exists():
                    artifacts.append({"name": name, "contentType": content_type, "sizeBytes": path.stat().st_size})
        return {
            "id": self.id,
            "status": self.status,
            "stage": self.stage,
            "stageProgress": self.stage_progress,
            "errorCode": self.error_code,
            "error": self.error,
            "createdAt": self.created_at,
            "startedAt": self.started_at,
            "completedAt": self.completed_at,
            "artifacts": artifacts,
            "mesh": self.mesh,
        }


class JobManager:
    def __init__(self, engine: Engine, work_dir: Path, max_queue: int, retention_seconds: int):
        self.engine = engine
        self.work_dir = work_dir
        self.retention_seconds = retention_seconds
        self._jobs: dict[str, Job] = {}
        self._lock = threading.Lock()
        self._queue: queue.Queue[str] = queue.Queue(maxsize=max_queue)
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, name="sf3d-worker", daemon=True)

    def start(self) -> None:
        self.work_dir.mkdir(parents=True, exist_ok=True)
        self._thread.start()

    def stop(self) -> None:
        self._stop.set()

    def submit(self, image: bytes) -> Job | None:
        job_id = str(uuid.uuid4())
        job = Job(id=job_id, directory=self.work_dir / job_id.replace("-", ""), image=image)
        with self._lock:
            self._jobs[job_id] = job
        try:
            self._queue.put_nowait(job_id)
        except queue.Full:
            with self._lock:
                self._jobs.pop(job_id, None)
            return None
        return job

    def get(self, job_id: str) -> Job | None:
        with self._lock:
            return self._jobs.get(job_id)

    def queued_count(self) -> int:
        with self._lock:
            return sum(1 for j in self._jobs.values() if j.status == "Queued")

    def cancel(self, job_id: str) -> Job | None:
        """Queued jobs are cancelled at once; a running job finishes its current model call, then is discarded."""
        with self._lock:
            job = self._jobs.get(job_id)
            if job is None or job.status in TERMINAL:
                return job
            job.cancel_requested = True
            if job.status == "Queued":
                self._finish(job, "Cancelled", "cancelled", "Cancelled before processing started.")
            return job

    def delete(self, job_id: str) -> bool:
        with self._lock:
            job = self._jobs.pop(job_id, None)
        if job is None:
            return False
        job.cancel_requested = True
        shutil.rmtree(job.directory, ignore_errors=True)
        return True

    def cleanup(self) -> int:
        cutoff = time.monotonic() - self.retention_seconds
        with self._lock:
            expired = [j for j in self._jobs.values() if j.completed_monotonic and j.completed_monotonic < cutoff]
            for job in expired:
                self._jobs.pop(job.id, None)
        for job in expired:
            shutil.rmtree(job.directory, ignore_errors=True)
        return len(expired)

    def _run(self) -> None:
        while not self._stop.is_set():
            try:
                job_id = self._queue.get(timeout=1)
            except queue.Empty:
                self.cleanup()
                continue
            job = self.get(job_id)
            if job is None:
                continue
            with self._lock:
                if job.status != "Queued":
                    continue
                job.status = "Running"
                job.started_at = _now()
                image, job.image = job.image, None
            self._process(job, image)

    def _process(self, job: Job, image: bytes) -> None:
        def progress(stage: str, fraction: float | None) -> None:
            with self._lock:
                if job.status not in TERMINAL:
                    job.stage, job.stage_progress = stage, fraction

        try:
            log.info("job %s started", job.id)
            result = self.engine.generate(image, progress, lambda: job.cancel_requested)
            job.directory.mkdir(parents=True, exist_ok=True)
            (job.directory / "mesh.glb").write_bytes(result.glb)
            (job.directory / "input.png").write_bytes(result.preprocessed_png)
            with self._lock:
                job.mesh = {
                    "vertices": result.vertices,
                    "triangles": result.triangles,
                    "sizeX": result.size[0],
                    "sizeY": result.size[1],
                    "sizeZ": result.size[2],
                }
                self._finish(job, "Succeeded")
            log.info("job %s succeeded: %d triangles, seconds %s", job.id, result.triangles, result.seconds)
        except GenerationError as exc:
            with self._lock:
                self._finish(job, "Cancelled" if exc.code == "cancelled" else "Failed", exc.code, str(exc))
            log.warning("job %s ended: %s", job.id, exc.code)
        except FileNotFoundError:
            with self._lock:
                self._finish(job, "Failed", "model_unavailable", "The 3D model files are not installed on the inference server.")
            log.exception("job %s: model files missing", job.id)
        except Exception:  # noqa: BLE001
            with self._lock:
                self._finish(job, "Failed", "internal_error", "Generation failed due to an internal error.")
            log.exception("job %s failed", job.id)

    @staticmethod
    def _finish(job: Job, status: str, code: str | None = None, message: str | None = None) -> None:
        if job.status in TERMINAL:
            return
        job.status = status
        job.error_code = code
        job.error = message
        job.stage_progress = None
        job.completed_at = _now()
        job.completed_monotonic = time.monotonic()
        job.image = None
