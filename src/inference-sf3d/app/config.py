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
    # Official Stability-AI/stable-fast-3d checkout (provides the `sf3d` package).
    sf3d_repo: Path = field(default_factory=lambda: _path("SF3D_REPO", "third_party/stable-fast-3d"))
    # Hugging Face model id (gated; needs `hf auth login` or HF_TOKEN) or a local directory
    # containing config.yaml and model.safetensors.
    model: str = field(default_factory=lambda: os.environ.get("SF3D_MODEL", "stabilityai/stable-fast-3d"))
    # Directory holding u2net.onnx for rembg (reuses the file downloaded for the .NET service).
    u2net_home: Path = field(default_factory=lambda: _path("SF3D_U2NET_HOME", "models/u2net"))
    work_dir: Path = field(default_factory=lambda: _path("SF3D_WORK_DIR", "data/inference-sf3d"))
    device: str = field(default_factory=lambda: os.environ.get("SF3D_DEVICE", "auto"))  # auto | cpu | cuda
    texture_resolution: int = field(default_factory=lambda: int(os.environ.get("SF3D_TEXTURE_RESOLUTION", "2048")))
    # "plastic": metallic 0 / roughness 0.7 (painted kits are not metal; SF3D often estimates metallic=1,
    # which hides the texture under reflections). "estimated": keep SF3D's predicted material.
    material: str = field(default_factory=lambda: os.environ.get("SF3D_MATERIAL", "plastic"))
    # Surface extraction grid: "official" (bundled 160 BCC grid, ~80 cells/axis) or the number of
    # vertices per axis of a generated cubic grid (e.g. 129). Denser = more geometric detail, slower.
    isosurface_grid: str = field(default_factory=lambda: os.environ.get("SF3D_ISOSURFACE_GRID", "official"))
    foreground_ratio: float = field(default_factory=lambda: float(os.environ.get("SF3D_FOREGROUND_RATIO", "0.85")))
    remesh: str = field(default_factory=lambda: os.environ.get("SF3D_REMESH", "none"))  # none | triangle | quad
    max_queue: int = field(default_factory=lambda: int(os.environ.get("SF3D_MAX_QUEUE", "8")))
    max_image_bytes: int = field(default_factory=lambda: int(os.environ.get("SF3D_MAX_IMAGE_BYTES", str(20 * 1024 * 1024))))
    retention_seconds: int = field(default_factory=lambda: int(os.environ.get("SF3D_RETENTION_SECONDS", str(6 * 3600))))
    # Shared secret for X-Internal-Api-Key. Required when binding to a non-loopback address.
    api_key: str = field(default_factory=lambda: os.environ.get("SF3D_API_KEY", ""))
    load_on_startup: bool = field(default_factory=lambda: os.environ.get("SF3D_LOAD_ON_STARTUP", "1") == "1")
