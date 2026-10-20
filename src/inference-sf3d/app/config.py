"""Settings from environment variables (prefix SF3D_). Paths are resolved against the repo root."""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]


def _path(name: str, default: str) -> Path:
    value = Path(os.environ.get(name, default))
    return value if value.is_absolute() else (REPO_ROOT / value).resolve()


@dataclass(frozen=True)
class Settings:
    # "sf3d" (Stable Fast 3D) or "spar3d" (Stable Point-Aware 3D: predicts a point cloud of the
    # hidden sides first, so backs and sides come out better; slower). Each needs its own venv.
    engine: str = field(default_factory=lambda: os.environ.get("SF3D_ENGINE", "sf3d").lower())
    # Official Stability-AI/stable-fast-3d checkout (provides the `sf3d` package).
    sf3d_repo: Path = field(default_factory=lambda: _path("SF3D_REPO", "third_party/stable-fast-3d"))
    # Official Stability-AI/stable-point-aware-3d checkout (provides the `spar3d` package).
    spar3d_repo: Path = field(default_factory=lambda: _path("SPAR3D_REPO", "third_party/stable-point-aware-3d"))
    # Gated like SF3D: accept the license at https://huggingface.co/stabilityai/stable-point-aware-3d.
    spar3d_model: str = field(default_factory=lambda: os.environ.get("SPAR3D_MODEL", "stabilityai/stable-point-aware-3d"))
    # SPAR3D's own framing: crop side = object size x this ratio (official default 1.3).
    spar3d_crop_ratio: float = field(default_factory=lambda: float(os.environ.get("SPAR3D_CROP_RATIO", "1.3")))
    # Hugging Face model id (gated; needs `hf auth login` or HF_TOKEN) or a local directory
    # containing config.yaml and model.safetensors.
    model: str = field(default_factory=lambda: os.environ.get("SF3D_MODEL", "stabilityai/stable-fast-3d"))
    # Directory holding u2net.onnx for rembg (reuses the file downloaded for the .NET service).
    u2net_home: Path = field(default_factory=lambda: _path("SF3D_U2NET_HOME", "models/u2net"))
    work_dir: Path = field(default_factory=lambda: _path("SF3D_WORK_DIR", "data/inference-sf3d"))
    device: str = field(default_factory=lambda: os.environ.get("SF3D_DEVICE", "auto"))  # auto | cpu | cuda
    # Fraction of logical CPUs the model may use (0.8 = 12 threads on 16). OMP_NUM_THREADS, if set, wins.
    cpu_limit: float = field(default_factory=lambda: float(os.environ.get("SF3D_CPU_LIMIT", "0.8")))
    texture_resolution: int = field(default_factory=lambda: int(os.environ.get("SF3D_TEXTURE_RESOLUTION", "2048")))
    # "plastic": metallic 0 / roughness 0.7 (painted kits are not metal; SF3D often estimates metallic=1,
    # which hides the texture under reflections). "estimated": keep SF3D's predicted material.
    material: str = field(default_factory=lambda: os.environ.get("SF3D_MATERIAL", "plastic"))
    # Surface extraction grid: "official" (bundled 160 BCC grid, ~80 cells/axis) or the number of
    # vertices per axis of a generated cubic grid (e.g. 129). Denser = more geometric detail, slower.
    isosurface_grid: str = field(default_factory=lambda: os.environ.get("SF3D_ISOSURFACE_GRID", "129"))
    # Sample the input photo onto the camera-facing surfaces (sharp colour where the photo shows the
    # object); hidden sides keep SF3D's predicted texture. See app/projection.py.
    photo_projection: bool = field(default_factory=lambda: os.environ.get("SF3D_PHOTO_PROJECTION", "1") == "1")
    # Size of the final base-colour texture when photo projection is on (SF3D's bake is upscaled to it).
    projection_texture_resolution: int = field(default_factory=lambda: int(os.environ.get("SF3D_PROJECTION_TEXTURE_RESOLUTION", "4096")))
    # Upscale the photo x4 with Real-ESRGAN before projecting it, when its re-framed crop is smaller
    # than super_resolution_below pixels on the longest side. Skipped (logged) if the weights are missing.
    super_resolution: bool = field(default_factory=lambda: os.environ.get("SF3D_SUPER_RESOLUTION", "1") == "1")
    super_resolution_below: int = field(default_factory=lambda: int(os.environ.get("SF3D_SUPER_RESOLUTION_BELOW", "2048")))
    realesrgan_weights: Path = field(default_factory=lambda: _path("SF3D_REALESRGAN_WEIGHTS", "models/realesrgan/RealESRGAN_x4plus.pth"))
    foreground_ratio: float = field(default_factory=lambda: float(os.environ.get("SF3D_FOREGROUND_RATIO", "0.85")))
    remesh: str = field(default_factory=lambda: os.environ.get("SF3D_REMESH", "none"))  # none | triangle | quad
    # Extra photos of other sides (multi-photo jobs): at most this many are used.
    max_extra_images: int = field(default_factory=lambda: int(os.environ.get("SF3D_MAX_EXTRA_IMAGES", "5")))
    max_queue: int = field(default_factory=lambda: int(os.environ.get("SF3D_MAX_QUEUE", "8")))
    max_image_bytes: int = field(default_factory=lambda: int(os.environ.get("SF3D_MAX_IMAGE_BYTES", str(20 * 1024 * 1024))))
    retention_seconds: int = field(default_factory=lambda: int(os.environ.get("SF3D_RETENTION_SECONDS", str(6 * 3600))))
    # Shared secret for X-Internal-Api-Key. Required when binding to a non-loopback address.
    api_key: str = field(default_factory=lambda: os.environ.get("SF3D_API_KEY", ""))
    load_on_startup: bool = field(default_factory=lambda: os.environ.get("SF3D_LOAD_ON_STARTUP", "1") == "1")
