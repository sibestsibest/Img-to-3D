"""Photo projection on synthetic geometry: a quad facing SF3D's fixed input camera.

Needs the texture_baker extension (built by scripts/setup-sf3d.ps1); skipped without it.
"""

from __future__ import annotations

import math

import numpy as np
import pytest
from PIL import Image

texture_baker = pytest.importorskip("texture_baker")
trimesh = pytest.importorskip("trimesh")

from app.projection import _NATIVE_FROM_OUTPUT, project_photo  # noqa: E402

DISTANCE, FOV = 1.6, 40.0
RES = 256


def _photo(size: int = 512) -> Image.Image:
    """Smooth RGB gradient (red = image x, green = image y) on an opaque square."""
    y, x = np.mgrid[0:size, 0:size].astype(np.float32) / (size - 1)
    rgba = np.stack([x, y, np.full_like(x, 0.5), np.ones_like(x)], axis=-1)
    return Image.fromarray((rgba * 255).astype(np.uint8), "RGBA")


def _quad(native_x: float, half: float, facing_camera: bool, base_rgb=(10, 200, 10)):
    """A square in the plane x = native_x (SF3D's camera frame), returned in SF3D output orientation."""
    native = np.array([[native_x, -half, -half], [native_x, half, -half], [native_x, half, half], [native_x, -half, half]])
    uv = np.array([[0.05, 0.05], [0.95, 0.05], [0.95, 0.95], [0.05, 0.95]])
    # Counter-clockwise seen from +x (the camera) gives a +x normal.
    faces = np.array([[0, 1, 2], [0, 2, 3]]) if facing_camera else np.array([[0, 2, 1], [0, 3, 2]])
    output = native @ np.linalg.inv(_NATIVE_FROM_OUTPUT).T
    texture = Image.new("RGB", (RES, RES), base_rgb)
    material = trimesh.visual.material.PBRMaterial(baseColorTexture=texture)
    mesh = trimesh.Trimesh(output, faces, visual=trimesh.visual.TextureVisuals(uv=uv, material=material), process=False)
    return mesh


def _expected_pixel(native_point, size):
    depth = DISTANCE - native_point[0]
    focal = 0.5 / math.tan(math.radians(FOV) / 2)
    u = 0.5 + focal * native_point[1] / depth
    v = 0.5 - focal * native_point[2] / depth
    return u * size, v * size


def test_camera_facing_quad_takes_photo_colours():
    # Half-size chosen so the quad fills most of the frame (silhouette must match the opaque photo).
    half = (DISTANCE - 0.3) * math.tan(math.radians(FOV) / 2) * 0.98
    mesh = _quad(0.3, half, facing_camera=True)
    photo = _photo()

    stats = project_photo(mesh, photo, texture_baker.TextureBaker(), RES, distance=DISTANCE, fov_deg=FOV, min_silhouette_iou=0.5)

    assert stats.applied, stats.reason
    assert stats.silhouette_iou > 0.85
    tex = np.asarray(mesh.visual.material.baseColorTexture, dtype=np.float32) / 255
    assert mesh.visual.material.baseColorTexture.format == "PNG"

    # The quad's centre maps to the photo centre, so red and green there are ~0.5.
    centre = tex[RES // 2, RES // 2]
    assert abs(centre[0] - 0.5) < 0.06 and abs(centre[1] - 0.5) < 0.06
    # The gradient runs across the texture: SF3D's uniform green base was replaced.
    assert tex[:, :, 0].std() > 0.15 and tex[:, :, 1].std() > 0.15


def test_quad_facing_away_keeps_predicted_texture():
    half = (DISTANCE - 0.3) * math.tan(math.radians(FOV) / 2) * 0.98
    mesh = _quad(0.3, half, facing_camera=False)

    project_photo(mesh, _photo(), texture_baker.TextureBaker(), RES, distance=DISTANCE, fov_deg=FOV, min_silhouette_iou=0.0)

    tex = np.asarray(mesh.visual.material.baseColorTexture.convert("RGB"), dtype=np.float32)
    inner = tex[RES // 4: 3 * RES // 4, RES // 4: 3 * RES // 4]
    assert np.abs(inner - np.array([10, 200, 10])).max() < 2


def test_mismatched_silhouette_is_not_projected():
    # A small quad cannot explain a photo that is opaque everywhere.
    mesh = _quad(0.3, 0.05, facing_camera=True)
    stats = project_photo(mesh, _photo(), texture_baker.TextureBaker(), RES, distance=DISTANCE, fov_deg=FOV)

    assert not stats.applied
    assert "IoU" in (stats.reason or "")
    tex = np.asarray(mesh.visual.material.baseColorTexture.convert("RGB"))
    assert (tex == np.array([10, 200, 10], dtype=np.uint8)).all()
