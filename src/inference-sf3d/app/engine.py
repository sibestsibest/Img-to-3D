"""Stable Fast 3D engine: loads the official model once and turns one image into a textured GLB.

Mirrors Stability-AI/stable-fast-3d run.py (remove_background -> resize_foreground ->
SF3D.run_image -> trimesh export). Only the official classes and functions are used.
"""

from __future__ import annotations

import io
import logging
import os
import sys
import threading
import time
from dataclasses import dataclass
from typing import Callable, Protocol

import numpy as np
from PIL import Image, ImageOps

from .config import Settings

log = logging.getLogger("sf3d.engine")

ProgressFn = Callable[[str, float | None], None]


class GenerationError(Exception):
    """User-presentable failure with a stable error code (matches InferenceErrorCodes in the .NET contract)."""

    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


@dataclass
class GenerationResult:
    glb: bytes
    preprocessed_png: bytes
    vertices: int
    triangles: int
    size: tuple[float, float, float]
    seconds: dict[str, float]


class Engine(Protocol):
    state: str
    failure_reason: str | None
    execution_provider: str

    def ensure_loaded(self) -> None: ...

    def generate(self, image_bytes: bytes, progress: ProgressFn, is_cancelled: Callable[[], bool]) -> GenerationResult: ...


class Sf3dEngine:
    """Process-wide singleton; `generate` must be called from one worker thread at a time."""

    def __init__(self, settings: Settings):
        self.settings = settings
        self.state = "NotLoaded"
        self.failure_reason: str | None = None
        self.execution_provider = "Cpu"
        self._lock = threading.Lock()
        self._model = None
        self._rembg_session = None
        self._device = "cpu"

    def ensure_loaded(self) -> None:
        with self._lock:
            if self.state == "Ready":
                return
            self.state = "Loading"
            self.failure_reason = None
            started = time.perf_counter()
            try:
                self._load()
            except Exception as exc:  # noqa: BLE001 - reported via /health
                self.state = "Failed"
                self.failure_reason = _describe_load_error(exc)
                log.exception("SF3D model loading failed")
                raise
            self.state = "Ready"
            log.info("SF3D loaded on %s in %.1fs", self._device, time.perf_counter() - started)

    def _use_dense_grid(self, model, vertices_per_axis: int) -> None:
        from sf3d.models.isosurface import MarchingTetrahedraHelper

        from .tetgrid import cached_grid

        path = cached_grid(self.settings.work_dir / "tets", vertices_per_axis)
        # The helper scales learned vertex offsets by 1/resolution ("half tet size"); a cubic grid
        # with (n-1) cells per axis has half-cell 1/(2(n-1)), hence resolution 2(n-1).
        model.isosurface_helper = MarchingTetrahedraHelper(2 * (vertices_per_axis - 1), str(path))
        log.info("Using generated cubic tet grid: %d vertices/axis", vertices_per_axis)

    def _load(self) -> None:
        s = self.settings
        if not (s.sf3d_repo / "sf3d").is_dir():
            raise FileNotFoundError(f"SF3D repository not found at {s.sf3d_repo}")
        if str(s.sf3d_repo) not in sys.path:
            sys.path.insert(0, str(s.sf3d_repo))
        os.environ.setdefault("U2NET_HOME", str(s.u2net_home))

        import rembg
        import torch
        from sf3d.system import SF3D  # raises ImportError if texture_baker/uv_unwrapper are not built

        if s.device == "auto":
            self._device = "cuda" if torch.cuda.is_available() else "cpu"
        else:
            self._device = s.device
        self.execution_provider = "Cuda" if self._device.startswith("cuda") else "Cpu"

        model = SF3D.from_pretrained(s.model, config_name="config.yaml", weight_name="model.safetensors")
        if s.isosurface_grid != "official":
            self._use_dense_grid(model, int(s.isosurface_grid))
        model.to(self._device)
        model.eval()
        self._model = model
        self._rembg_session = rembg.new_session()

    def generate(self, image_bytes: bytes, progress: ProgressFn, is_cancelled: Callable[[], bool]) -> GenerationResult:
        import torch
        import trimesh

        self.ensure_loaded()  # also puts the SF3D checkout on sys.path
        from sf3d.utils import remove_background, resize_foreground
        s = self.settings
        timings: dict[str, float] = {}
        t0 = time.perf_counter()

        def mark(name: str) -> None:
            nonlocal t0
            timings[name] = round(time.perf_counter() - t0, 2)
            t0 = time.perf_counter()

        progress("PreparingImage", None)
        try:
            image = Image.open(io.BytesIO(image_bytes))
            image = ImageOps.exif_transpose(image).convert("RGBA")
        except Exception as exc:  # noqa: BLE001
            raise GenerationError("invalid_image", "The file could not be decoded as an image.") from exc

        progress("RemovingBackground", None)
        image = remove_background(image, self._rembg_session)
        if np.asarray(image)[:, :, 3].max() < 128:
            raise GenerationError("no_foreground", "No foreground object was detected in the image. Use a photo with a single, clearly visible object.")
        image = resize_foreground(image, s.foreground_ratio)
        preprocessed = io.BytesIO()
        image.save(preprocessed, format="PNG")
        mark("preprocess")
        if is_cancelled():
            raise GenerationError("cancelled", "Generation was cancelled.")

        # SF3D runs encoder, mesh extraction, UV unwrap and texture baking in one call; it
        # exposes no intermediate progress, so none is reported (no fabricated percentages).
        progress("GeneratingMesh", None)
        try:
            with torch.no_grad():
                autocast = torch.autocast(device_type="cuda", dtype=torch.bfloat16) if self._device.startswith("cuda") else _null()
                with autocast:
                    mesh, _ = self._model.run_image(image, bake_resolution=s.texture_resolution, remesh=s.remesh, vertex_count=-1)
        except (torch.cuda.OutOfMemoryError, MemoryError) as exc:
            raise GenerationError("out_of_memory", "Not enough memory to run the model. Try a lower texture resolution or a machine with more RAM/VRAM.") from exc
        mark("generate")
        if is_cancelled():
            raise GenerationError("cancelled", "Generation was cancelled.")

        progress("Exporting", None)
        if not isinstance(mesh, trimesh.Trimesh) or len(mesh.faces) == 0:
            raise GenerationError("no_surface", "The model did not produce a surface for this image. Try a clearer photo of a single object on a plain background.")

        if s.material == "plastic" and getattr(mesh.visual, "material", None) is not None:
            mesh.visual.material.metallicFactor = 0.0
            mesh.visual.material.roughnessFactor = 0.7

        # SF3D applies Gradio's viewer rotation, which leaves the photographed side facing -Z.
        # Turn 180 degrees about Y so it faces +Z, the default camera side in three.js/glTF viewers.
        mesh.apply_transform(trimesh.transformations.rotation_matrix(np.pi, [0, 1, 0]))

        glb = mesh.export(file_type="glb", include_normals=True)
        extents = mesh.bounding_box.extents
        mark("export")
        if self._device.startswith("cuda"):
            torch.cuda.empty_cache()

        return GenerationResult(
            glb=glb,
            preprocessed_png=preprocessed.getvalue(),
            vertices=int(len(mesh.vertices)),
            triangles=int(len(mesh.faces)),
            size=(float(extents[0]), float(extents[1]), float(extents[2])),
            seconds=timings,
        )


class _null:
    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def _describe_load_error(exc: Exception) -> str:
    text = str(exc)
    if isinstance(exc, ImportError) and ("texture_baker" in text or "uv_unwrapper" in text):
        return "SF3D native extensions are not built. Install Visual Studio Build Tools (C++) and run scripts/setup-sf3d.ps1."
    lowered = text.lower()
    if "401" in text or "gated" in lowered or ("access" in lowered and "repo" in lowered):
        return "Access to stabilityai/stable-fast-3d is denied. Accept the license on Hugging Face and log in with a read token."
    if isinstance(exc, FileNotFoundError):
        return "SF3D code or model files are missing. See src/inference-sf3d/README.md."
    return f"Model loading failed: {type(exc).__name__}"
