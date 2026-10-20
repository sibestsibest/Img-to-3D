"""Stable Fast 3D / SPAR3D engine: loads the official model once and turns photos into a textured GLB.

Mirrors the official run.py of Stability-AI/stable-fast-3d (remove_background -> resize_foreground
-> SF3D.run_image) or Stability-AI/stable-point-aware-3d (remove_background -> foreground_crop ->
SPAR3D.run_image). Only the official classes and functions are used for the model itself; the
geometry always comes from the main photo. Afterwards the main photo, and any extra photos of other
sides, are projected onto the texture (app/projection.py).
"""

from __future__ import annotations

import io
import logging
import math
import os
import sys
import threading
import time
from dataclasses import dataclass
from typing import Callable, Protocol

import numpy as np
from PIL import Image, ImageOps

from .config import Settings
from .cpu import apply_cpu_limit

log = logging.getLogger("sf3d.engine")

ProgressFn = Callable[[str, float | None], None]

ENGINE_NAMES = {"sf3d": "StableFast3D", "spar3d": "SPAR3D"}


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


@dataclass
class ExtraImage:
    """Another photo of the same object; `view` hints where it was taken from (see projection.VIEW_HINTS)."""

    data: bytes
    view: str = "auto"


class Engine(Protocol):
    state: str
    failure_reason: str | None
    execution_provider: str
    name: str

    def ensure_loaded(self) -> None: ...

    def generate(
        self, image_bytes: bytes, progress: ProgressFn, is_cancelled: Callable[[], bool], extras: list[ExtraImage] | None = None
    ) -> GenerationResult: ...


class Sf3dEngine:
    """Process-wide singleton; `generate` must be called from one worker thread at a time."""

    def __init__(self, settings: Settings):
        if settings.engine not in ENGINE_NAMES:
            raise ValueError(f"SF3D_ENGINE must be one of {sorted(ENGINE_NAMES)}")
        self.settings = settings
        self.name = ENGINE_NAMES[settings.engine]
        self.state = "NotLoaded"
        self.failure_reason: str | None = None
        self.execution_provider = "Cpu"
        self._lock = threading.Lock()
        self._model = None
        self._bg_remover = None  # rembg session (SF3D) or transparent_background.Remover (SPAR3D)
        self._superres = None  # SuperResolver, or False when its weights are missing
        self._device = "cpu"

    @property
    def _spar3d(self) -> bool:
        return self.settings.engine == "spar3d"

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
                self.failure_reason = _describe_load_error(exc, self.settings.engine)
                log.exception("%s model loading failed", self.name)
                raise
            self.state = "Ready"
            log.info("%s loaded on %s in %.1fs", self.name, self._device, time.perf_counter() - started)

    def _use_dense_grid(self, model, vertices_per_axis: int) -> None:
        import importlib

        import torch

        from .tetgrid import cached_grid

        helper_cls = importlib.import_module(f"{self.settings.engine}.models.isosurface").MarchingTetrahedraHelper
        path = cached_grid(self.settings.work_dir / "tets", vertices_per_axis)
        # The helper scales learned vertex offsets by 1/resolution ("half tet size"); a cubic grid
        # with (n-1) cells per axis has half-cell 1/(2(n-1)), hence resolution 2(n-1).
        helper = helper_cls(2 * (vertices_per_axis - 1), str(path))
        # Pre-fill the edge list: the helper would otherwise derive it with torch.unique over every
        # tet edge on first use, which runs out of memory on dense grids.
        helper._all_edges = torch.from_numpy(np.load(path)["edges"])
        model.isosurface_helper = helper
        log.info("Using generated cubic tet grid: %d vertices/axis", vertices_per_axis)

    def _load(self) -> None:
        s = self.settings
        repo, package = (s.spar3d_repo, "spar3d") if self._spar3d else (s.sf3d_repo, "sf3d")
        if not (repo / package).is_dir():
            raise FileNotFoundError(f"{self.name} repository not found at {repo}")
        if str(repo) not in sys.path:
            sys.path.insert(0, str(repo))
        os.environ.setdefault("U2NET_HOME", str(s.u2net_home))
        threads = apply_cpu_limit(s.cpu_limit)

        import torch

        torch.set_num_threads(threads)
        log.info("CPU threads: %d of %d", threads, os.cpu_count() or 1)
        if s.device == "auto":
            self._device = "cuda" if torch.cuda.is_available() else "cpu"
        else:
            self._device = s.device
        self.execution_provider = "Cuda" if self._device.startswith("cuda") else "Cpu"

        # Both raise ImportError if texture_baker/uv_unwrapper are not built.
        if self._spar3d:
            import spar3d.system as spar3d_system
            from spar3d.system import SPAR3D
            from transparent_background import Remover

            from .weights import load_into

            # safetensors' mmap loader crashes on SPAR3D's 7.3 GB checkpoint on Windows (see weights.py).
            spar3d_system.load_model = load_into
            model = SPAR3D.from_pretrained(s.spar3d_model, config_name="config.yaml", weight_name="model.safetensors", low_vram_mode=False)
            self._bg_remover = Remover(device=self._device)
        else:
            import rembg
            from sf3d.system import SF3D

            model = SF3D.from_pretrained(s.model, config_name="config.yaml", weight_name="model.safetensors")
            self._bg_remover = rembg.new_session()

        if s.isosurface_grid != "official":
            self._use_dense_grid(model, int(s.isosurface_grid))
        model.to(self._device)
        model.eval()
        self._model = model

    def _camera(self) -> tuple[float, float]:
        """(distance, vertical FOV in degrees) of the model's fixed input camera."""
        cfg = self._model.cfg
        fov = math.degrees(cfg.default_fovy_rad) if self._spar3d else cfg.default_fovy_deg
        return cfg.default_distance, fov

    def _remove_background(self, image: Image.Image) -> Image.Image:
        if self._spar3d:
            from spar3d.utils import remove_background
        else:
            from sf3d.utils import remove_background
        return remove_background(image, self._bg_remover)

    def _frame(self, image: Image.Image) -> Image.Image:
        """The model's own re-framing of the background-removed photo."""
        if self._spar3d:
            from spar3d.utils import foreground_crop

            return foreground_crop(image, self.settings.spar3d_crop_ratio)
        from sf3d.utils import resize_foreground

        return resize_foreground(image, self.settings.foreground_ratio)

    def generate(
        self, image_bytes: bytes, progress: ProgressFn, is_cancelled: Callable[[], bool], extras: list[ExtraImage] | None = None
    ) -> GenerationResult:
        import torch
        import trimesh

        self.ensure_loaded()  # also puts the model checkout on sys.path
        s = self.settings
        extras = (extras or [])[: s.max_extra_images]
        timings: dict[str, float] = {}
        t0 = time.perf_counter()

        def mark(name: str) -> None:
            nonlocal t0
            timings[name] = round(time.perf_counter() - t0, 2)
            t0 = time.perf_counter()

        def check_cancelled() -> None:
            if is_cancelled():
                raise GenerationError("cancelled", "Generation was cancelled.")

        progress("PreparingImage", None)
        image = _decode(image_bytes)

        progress("RemovingBackground", None)
        image = self._remove_background(image)
        if np.asarray(image)[:, :, 3].max() < 128:
            raise GenerationError("no_foreground", "No foreground object was detected in the image. Use a photo with a single, clearly visible object.")
        image = self._frame(image)
        preprocessed = io.BytesIO()
        image.save(preprocessed, format="PNG")
        extra_photos = self._prepare_extras(extras)
        mark("preprocess")
        check_cancelled()

        # The model runs encoder, (SPAR3D: point-cloud diffusion,) mesh extraction, UV unwrap and
        # texture baking in one call and exposes no intermediate progress, so none is reported.
        progress("GeneratingMesh", None)
        try:
            with torch.no_grad():
                autocast = torch.autocast(device_type="cuda", dtype=torch.bfloat16) if self._device.startswith("cuda") else _null()
                with autocast:
                    mesh, _ = self._model.run_image(image, bake_resolution=s.texture_resolution, remesh=s.remesh, vertex_count=-1)
        except (torch.cuda.OutOfMemoryError, MemoryError) as exc:
            raise GenerationError("out_of_memory", "Not enough memory to run the model. Try a lower texture resolution or a machine with more RAM/VRAM.") from exc
        mark("generate")
        check_cancelled()

        progress("Exporting", None)
        if not isinstance(mesh, trimesh.Trimesh) or len(mesh.faces) == 0:
            raise GenerationError("no_surface", "The model did not produce a surface for this image. Try a clearer photo of a single object on a plain background.")

        if s.photo_projection:
            self._project(mesh, image, extra_photos, mark)
        _store_textures_lossless(mesh)

        if s.material == "plastic" and getattr(mesh.visual, "material", None) is not None:
            mesh.visual.material.metallicFactor = 0.0
            mesh.visual.material.roughnessFactor = 0.7

        # The models apply Gradio's viewer rotation, which leaves the photographed side facing -Z.
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

    def _prepare_extras(self, extras: list[ExtraImage]):
        """Background-removed, tightly cropped extra photos; unreadable or empty ones are skipped."""
        from .projection import VIEW_HINTS, ExtraView

        prepared = []
        for i, extra in enumerate(extras, start=1):
            try:
                photo = self._remove_background(_decode(extra.data))
            except GenerationError:
                log.warning("Extra photo %d could not be decoded; skipped", i)
                continue
            alpha = np.asarray(photo)[:, :, 3]
            if alpha.max() < 128:
                log.warning("Extra photo %d has no foreground; skipped", i)
                continue
            prepared.append(ExtraView(_crop_to_foreground(photo), extra.view if extra.view in VIEW_HINTS else "auto"))
        return prepared

    def _project(self, mesh, image: Image.Image, extra_photos, mark) -> None:
        from .projection import project_views

        s = self.settings
        photo = self._sharpen_photo(image)
        for extra in extra_photos:
            extra.photo = self._sharpen_photo(extra.photo)
        mark("super_resolution")
        distance, fov = self._camera()
        stats = project_views(mesh, photo, extra_photos, self._model.baker, max(s.projection_texture_resolution, s.texture_resolution), distance=distance, fov_deg=fov)
        for view in stats.views:
            if view.applied:
                log.info("Photo %s projected: %.0f%% of texels, silhouette IoU %.2f, %s", view.label, view.coverage * 100, view.silhouette_iou, view.direction)
            else:
                log.warning("Photo %s skipped: %s", view.label, view.reason)
        mark("projection")

    def _sharpen_photo(self, photo: Image.Image) -> Image.Image:
        """Real-ESRGAN x4 of a photo when it is too small for the texture; else unchanged."""
        s = self.settings
        if not s.super_resolution or max(photo.size) >= s.super_resolution_below:
            return photo
        if self._superres is None:
            from .superres import SuperResolver

            try:
                self._superres = SuperResolver(s.realesrgan_weights, device=self._device)
            except FileNotFoundError as exc:
                log.warning("Super-resolution skipped: %s (run scripts/setup-sf3d.ps1)", exc)
                self._superres = False
        if self._superres is False:
            return photo
        started = time.perf_counter()
        sharpened = self._superres.upscale_photo(photo, max(s.projection_texture_resolution, s.texture_resolution))
        log.info("Super-resolution %dx%d -> %dx%d in %.1fs", *photo.size, *sharpened.size, time.perf_counter() - started)
        return sharpened


def _decode(data: bytes) -> Image.Image:
    try:
        image = Image.open(io.BytesIO(data))
        return ImageOps.exif_transpose(image).convert("RGBA")
    except Exception as exc:  # noqa: BLE001
        raise GenerationError("invalid_image", "The file could not be decoded as an image.") from exc


def _crop_to_foreground(photo: Image.Image, margin: float = 0.06) -> Image.Image:
    alpha = np.asarray(photo)[:, :, 3]
    ys, xs = np.nonzero(alpha > 127)
    pad = int(max(np.ptp(xs), np.ptp(ys)) * margin) + 2
    return photo.crop((max(xs.min() - pad, 0), max(ys.min() - pad, 0), min(xs.max() + pad + 1, photo.width), min(ys.max() + pad + 1, photo.height)))


def _store_textures_lossless(mesh) -> None:
    """The models tag their textures as JPEG, which trimesh then writes as JPEG (blocky colour, banded normals)."""
    material = getattr(mesh.visual, "material", None)
    for name in ("baseColorTexture", "normalTexture"):
        texture = getattr(material, name, None)
        if texture is not None and texture.format == "JPEG":
            texture.format = "PNG"


class _null:
    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def _describe_load_error(exc: Exception, engine: str = "sf3d") -> str:
    hf_repo = "stabilityai/stable-point-aware-3d" if engine == "spar3d" else "stabilityai/stable-fast-3d"
    flag = " -Engine SPAR3D" if engine == "spar3d" else ""
    text = str(exc)
    if isinstance(exc, ImportError) and ("texture_baker" in text or "uv_unwrapper" in text):
        return f"Native extensions are not built. Install Visual Studio Build Tools (C++) and run scripts/setup-sf3d.ps1{flag}."
    lowered = text.lower()
    if "401" in text or "403" in text or "gated" in lowered or ("access" in lowered and "repo" in lowered):
        return f"Access to {hf_repo} is denied. Accept the license on Hugging Face and log in with a read token."
    if isinstance(exc, FileNotFoundError):
        return "Model code or files are missing. See src/inference-sf3d/README.md."
    return f"Model loading failed: {type(exc).__name__}"
