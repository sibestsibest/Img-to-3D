"""Projects photos onto the base-colour texture of an SF3D/SPAR3D mesh.

The models predict albedo from a low-resolution triplane, so even the side shown in the photo comes
out much blurrier than the photo itself. Both always assume the same input camera (SF3D: distance
1.6, vertical FOV 40 degrees; SPAR3D: 2.2, ~33.9 degrees; square crop), so every visible surface
point maps to a known pixel of the preprocessed image. This module samples the photo there and
blends it over the predicted texture where:

* the texel is visible from the camera (z-buffer built from the texels themselves),
* the surface faces the camera (grazing angles stretch pixels and are faded out),
* the pixel is well inside the foreground mask (edges are feathered).

Extra photos (other sides of the same object) have unknown cameras. Each one's viewing direction
is found by matching the mesh silhouette against the photo's mask over candidate directions
(restricted by an optional hint such as "back"), re-ranked by colour agreement with what is already
known, then projected the same way. They only fill texels the main photo does not cover well.
Texels no photo sees keep the predicted colour, gain-matched to the photos so seams stay soft.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field

import numpy as np
from PIL import Image

# SF3D/SPAR3D output orientation: run_image rotates the native mesh by -90 deg about X, then
# +90 deg about Y. Inverting that brings vertices back into the input camera's frame, where the
# camera sits on +X looking at the origin, image right = +Y and image up = +Z.
_NATIVE_FROM_OUTPUT = (
    np.array([[1, 0, 0], [0, 0, -1], [0, 1, 0]], dtype=np.float64)  # Rx(+90)
    @ np.array([[0, 0, -1], [0, 1, 0], [1, 0, 0]], dtype=np.float64)  # Ry(-90)
)

# Camera directions (native frame) for view hints; "left" = the photographer moved to their left
# of the main photo's position.
VIEW_HINTS: dict[str, tuple[float, float, float] | None] = {
    "auto": None,
    "front": (1.0, 0.0, 0.0),
    "back": (-1.0, 0.0, 0.0),
    "left": (0.0, -1.0, 0.0),
    "right": (0.0, 1.0, 0.0),
    "top": (0.0, 0.0, 1.0),
    "bottom": (0.0, 0.0, -1.0),
}


@dataclass
class ExtraView:
    photo: Image.Image  # RGBA, background removed
    hint: str = "auto"


@dataclass
class ViewStats:
    label: str
    applied: bool
    coverage: float  # fraction of texels mostly coloured by this photo
    silhouette_iou: float
    direction: str = ""
    reason: str | None = None


@dataclass
class ProjectionStats:
    applied: bool
    coverage: float  # fraction of texels mostly coloured by the main photo
    silhouette_iou: float  # agreement between the mesh's silhouette and the main photo's mask
    resolution: int
    reason: str | None = None
    views: list[ViewStats] = field(default_factory=list)


@dataclass
class _Camera:
    rotation: np.ndarray  # native -> camera frame (camera on +X, right +Y, up +Z)
    distance: float
    fov_deg: float
    # Maps normalised camera coordinates to photo pixels: x = (u - cu) * k + cx (same for y).
    # None = the photo is exactly the model's input frame (the main photo).
    framing: tuple[float, float, float, float, float] | None = None


def project_photo(mesh, photo: Image.Image, baker, resolution: int, *, distance: float = 1.6, fov_deg: float = 40.0, min_silhouette_iou: float = 0.6) -> ProjectionStats:
    """Main photo only; see project_views."""
    return project_views(mesh, photo, [], baker, resolution, distance=distance, fov_deg=fov_deg, min_silhouette_iou=min_silhouette_iou)


def project_views(
    mesh,
    photo: Image.Image,
    extras: list[ExtraView],
    baker,
    resolution: int,
    *,
    distance: float = 1.6,
    fov_deg: float = 40.0,
    min_silhouette_iou: float = 0.6,
    min_extra_iou: float = 0.7,
) -> ProjectionStats:
    """Replaces mesh.visual.material.baseColorTexture in place (as a PNG-encoded image).

    `mesh` is the trimesh returned by run_image (before any further transform), `photo` the RGBA
    image passed to it (background removed, re-framed) and `extras` other photos of the object.
    """
    material = mesh.visual.material
    if material.baseColorTexture is None or mesh.visual.uv is None:
        return ProjectionStats(False, 0.0, 0.0, resolution, "mesh has no base-colour texture")
    surface = _Surface.from_mesh(mesh, baker, resolution)
    if surface is None:
        return ProjectionStats(False, 0.0, 0.0, resolution, "UV rasterization is empty")

    predicted = surface.predicted
    acc = np.zeros(len(surface.p), dtype=np.float32)  # summed photo weight per texel (<= 1)
    painted = np.zeros_like(predicted)  # sum of weight * photo colour

    # Main photo: the model's own input camera.
    main_cam = _Camera(np.eye(3), distance, fov_deg)
    w0, c0, iou0 = _view_weights(surface, photo, main_cam, min_facing=(0.15, 0.45))
    stats = ProjectionStats(False, 0.0, iou0, resolution)
    if iou0 >= min_silhouette_iou:
        sure = w0 > 0.8
        if sure.sum() > 500:
            # Match the predicted colours to the photo on the surface both describe.
            gain = (c0[sure].mean(axis=0) + 1e-3) / (predicted[sure].mean(axis=0) + 1e-3)
            predicted = (predicted * gain.clip(0.6, 1.6)).clip(0.0, 1.0)
        acc += w0
        painted += w0[:, None] * c0
        stats.applied, stats.coverage = True, float((w0 > 0.5).mean())
    else:
        stats.reason = f"silhouette IoU {iou0:.2f} too low; mesh and photo disagree"
    stats.views.append(ViewStats("main", stats.applied, stats.coverage, iou0, "front", stats.reason))

    for i, extra in enumerate(extras, start=1):
        label = f"extra {i} ({extra.hint})"
        current = predicted * (1.0 - acc[:, None]) + painted
        cam, iou, direction = _estimate_camera(surface, extra, distance, fov_deg, current, acc)
        if cam is None or iou < min_extra_iou:
            stats.views.append(ViewStats(label, False, 0.0, iou, direction, f"no confident camera match (silhouette IoU {iou:.2f})"))
            continue
        w, c, _ = _view_weights(surface, extra.photo, cam, min_facing=(0.25, 0.55))
        eff = w * (1.0 - acc)
        if (eff > 0.05).sum() < 100:
            stats.views.append(ViewStats(label, False, 0.0, iou, direction, "adds no surface the other photos do not cover"))
            continue
        # Bring this photo's exposure in line with what is already painted or predicted.
        ref = (w > 0.6) & (acc > 0.6)
        if ref.sum() < 300:
            ref = w > 0.6
        if ref.sum() > 300:
            gain = (current[ref].mean(axis=0) + 1e-3) / (c[ref].mean(axis=0) + 1e-3)
            c = (c * gain.clip(0.6, 1.6)).clip(0.0, 1.0)
        acc += eff
        painted += eff[:, None] * c
        stats.views.append(ViewStats(label, True, float((eff > 0.5).mean()), iou, direction))

    if not any(v.applied for v in stats.views):
        return stats

    blended = predicted * (1.0 - acc[:, None]) + painted
    texture = np.zeros((resolution, resolution, 3), dtype=np.float32)
    texture[surface.mask] = blended
    texture = _pad_from_nearest(texture, surface.mask)

    out = Image.fromarray((texture * 255.0 + 0.5).clip(0, 255).astype(np.uint8), "RGB")
    out.format = "PNG"  # trimesh keeps JPEG only for images tagged JPEG
    material.baseColorTexture = out
    return stats


@dataclass
class _Surface:
    mask: np.ndarray  # texel mask (resolution x resolution)
    p: np.ndarray  # texel positions, native frame (N x 3)
    n: np.ndarray  # unit normals, native frame (N x 3)
    predicted: np.ndarray  # predicted colour per texel (N x 3)

    @staticmethod
    def from_mesh(mesh, baker, resolution: int) -> "_Surface | None":
        import torch

        base = mesh.visual.material.baseColorTexture.convert("RGB")
        if base.size != (resolution, resolution):
            base = base.resize((resolution, resolution), Image.Resampling.BICUBIC)
        base = np.asarray(base, dtype=np.float32) / 255.0

        # trimesh.invert() (last step of run_image) flips the winding; rasterize with the original order.
        faces = torch.from_numpy(np.ascontiguousarray(np.fliplr(mesh.faces)).astype(np.int64))
        uv = torch.from_numpy(np.asarray(mesh.visual.uv, dtype=np.float32))
        positions = np.asarray(mesh.vertices, dtype=np.float64) @ _NATIVE_FROM_OUTPUT.T
        normals = np.asarray(mesh.vertex_normals, dtype=np.float64) @ _NATIVE_FROM_OUTPUT.T

        rast = baker.rasterize(uv, faces, resolution)
        mask = baker.get_mask(rast).numpy()
        if not mask.any():
            return None
        p = baker.interpolate(torch.from_numpy(positions.astype(np.float32)), rast, faces).numpy()[mask].astype(np.float64)
        n = baker.interpolate(torch.from_numpy(normals.astype(np.float32)), rast, faces).numpy()[mask].astype(np.float64)
        n /= np.linalg.norm(n, axis=1, keepdims=True).clip(1e-12)
        return _Surface(mask, p, n, base[mask])


def _project(p: np.ndarray, n: np.ndarray | None, cam: _Camera):
    """Normalised image coordinates (u right, v down, in [0,1] for the model frame), depth, facing."""
    pc = p @ cam.rotation.T
    depth = cam.distance - pc[:, 0]
    focal = 0.5 / math.tan(math.radians(cam.fov_deg) / 2)  # in image widths
    safe = np.where(np.abs(depth) < 1e-6, 1e-6, depth)
    u = 0.5 + focal * pc[:, 1] / safe
    v = 0.5 - focal * pc[:, 2] / safe
    facing = None
    if n is not None:
        nc = n @ cam.rotation.T
        view = np.stack([cam.distance - pc[:, 0], -pc[:, 1], -pc[:, 2]], axis=1)
        view /= np.linalg.norm(view, axis=1, keepdims=True).clip(1e-12)
        facing = np.einsum("ij,ij->i", nc, view)
    return u, v, depth, facing


def _to_pixels(u, v, cam: _Camera, pw: int, ph: int):
    if cam.framing is None:
        return u * pw - 0.5, v * ph - 0.5
    cu, cv, k, cx, cy = cam.framing
    return (u - cu) * k + cx - 0.5, (v - cv) * k + cy - 0.5


def _view_weights(surface: _Surface, photo: Image.Image, cam: _Camera, min_facing: tuple[float, float]):
    """Per-texel blend weight and colour for one photo, plus silhouette IoU against its mask."""
    import cv2

    rgba = np.asarray(photo.convert("RGBA"), dtype=np.float32) / 255.0
    ph, pw = rgba.shape[:2]
    alpha = rgba[:, :, 3]

    u, v, depth, facing = _project(surface.p, surface.n, cam)
    x, y = _to_pixels(u, v, cam, pw, ph)
    visible, covered_px = _visibility(x / pw, y / ph, depth, facing)
    iou = _iou(covered_px, alpha)

    # Feather the foreground edge so background colour never bleeds onto the mesh.
    fg = (alpha > 0.5).astype(np.uint8)
    inside = cv2.distanceTransform(fg, cv2.DIST_L2, 5)
    feather = max(2.0, 0.012 * max(ph, pw))
    edge_weight = np.clip((inside - 0.25 * feather) / feather, 0.0, 1.0).astype(np.float32)

    color = _sample(rgba[:, :, :3], x, y, cv2.BORDER_REPLICATE).reshape(-1, 3)
    edge = _sample(edge_weight, x, y, cv2.BORDER_CONSTANT).reshape(-1)
    weight = (visible * _smoothstep(facing, *min_facing) * edge).astype(np.float32)
    return weight, color, iou


def _visibility(u, v, depth, facing, grid: int = 1024):
    """Per-texel visibility from a z-buffer splatted by the camera-facing texels.

    `u`, `v` are in photo-normalised coordinates. Returns (visible 0/1 per texel, covered mask at
    `grid` resolution, i.e. the silhouette).
    """
    from scipy import ndimage

    front = facing > 0.0
    inside = (u >= 0) & (u < 1) & (v >= 0) & (v < 1)
    ix = np.clip((u * grid).astype(np.int64), 0, grid - 1)
    iy = np.clip((v * grid).astype(np.int64), 0, grid - 1)
    flat = iy * grid + ix

    zbuf = np.full(grid * grid, np.inf)
    sel = front & inside
    np.minimum.at(zbuf, flat[sel], depth[sel])
    zbuf = zbuf.reshape(grid, grid)
    # Texel splats leave pinholes; a small min-filter closes them (conservative near depth edges).
    zbuf = ndimage.minimum_filter(zbuf, size=3)

    tolerance = 0.01  # scene units (object fits in a radius-0.87 cube)
    visible = sel & (depth <= zbuf.reshape(-1)[flat] + tolerance)
    covered = ndimage.binary_closing(np.isfinite(zbuf), iterations=2)
    return visible.astype(np.float32), covered


def _iou(covered: np.ndarray, alpha: np.ndarray) -> float:
    """IoU between a covered mask (any resolution, same frame) and the photo's alpha."""
    ph, pw = alpha.shape
    silhouette = np.asarray(Image.fromarray(covered.astype(np.uint8) * 255).resize((pw, ph), Image.Resampling.NEAREST)) > 127
    fg = alpha > 0.5
    union = (silhouette | fg).sum()
    return float((silhouette & fg).sum() / union) if union else 0.0


# --- Camera search for extra photos -------------------------------------------------------------

_GRID = 96  # silhouette resolution used for matching
_SUBSAMPLE = 120_000


def _normalised_mask(points_uv: np.ndarray | None, alpha: np.ndarray | None) -> tuple[np.ndarray, tuple[float, float, float]]:
    """Square, bbox-normalised binary mask (_GRID^2) of projected points or of a photo's alpha.

    Returns the mask and (centre x, centre y, side) of the square in the input's own units, so
    the two framings can be related.
    """
    from scipy import ndimage

    if alpha is not None:
        ys, xs = np.nonzero(alpha > 0.5)
        x0, x1, y0, y1 = xs.min(), xs.max() + 1, ys.min(), ys.max() + 1
    else:
        xs, ys = points_uv[:, 0], points_uv[:, 1]
        x0, x1 = np.percentile(xs, [0.2, 99.8])
        y0, y1 = np.percentile(ys, [0.2, 99.8])
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    side = max(x1 - x0, y1 - y0) * 1.08 + 1e-9

    if alpha is not None:
        h, w = alpha.shape
        left, top = int(round(cx - side / 2)), int(round(cy - side / 2))
        s = int(round(side))
        canvas = np.zeros((s, s), dtype=np.uint8)
        sx0, sy0 = max(left, 0), max(top, 0)
        sx1, sy1 = min(left + s, w), min(top + s, h)
        canvas[sy0 - top:sy1 - top, sx0 - left:sx1 - left] = (alpha[sy0:sy1, sx0:sx1] > 0.5) * 255
        mask = np.asarray(Image.fromarray(canvas).resize((_GRID, _GRID), Image.Resampling.BILINEAR)) > 127
        return mask, (cx, cy, side)

    gx = ((xs - (cx - side / 2)) / side * _GRID).astype(np.int64)
    gy = ((ys - (cy - side / 2)) / side * _GRID).astype(np.int64)
    ok = (gx >= 0) & (gx < _GRID) & (gy >= 0) & (gy < _GRID)
    mask = np.zeros((_GRID, _GRID), dtype=bool)
    mask[gy[ok], gx[ok]] = True
    mask = ndimage.binary_closing(mask, iterations=1)
    return mask, (cx, cy, side)


def _rotation_for(direction: np.ndarray, roll_deg: float) -> np.ndarray:
    d = direction / np.linalg.norm(direction)
    # Image up follows +Z; for near-vertical views, the main camera's side (+X) is "down" in the image.
    up0 = np.array([0.0, 0.0, 1.0]) if abs(d[2]) < 0.9 else np.array([-np.sign(d[2]), 0.0, 0.0])
    right = np.cross(up0, d)
    right /= np.linalg.norm(right)
    up = np.cross(d, right)
    r = math.radians(roll_deg)
    right, up = math.cos(r) * right + math.sin(r) * up, -math.sin(r) * right + math.cos(r) * up
    return np.stack([d, right, up])


def _candidate_directions(hint: str) -> list[np.ndarray]:
    count = 400
    i = np.arange(count) + 0.5
    phi = np.arccos(1 - 2 * i / count)
    theta = math.pi * (1 + 5 ** 0.5) * i
    dirs = np.stack([np.cos(theta) * np.sin(phi), np.sin(theta) * np.sin(phi), np.cos(phi)], axis=1)
    target = VIEW_HINTS.get(hint)
    if target is not None:
        dirs = dirs[dirs @ np.asarray(target) > math.cos(math.radians(55))]
    return list(dirs)


def _describe(direction: np.ndarray) -> str:
    az = math.degrees(math.atan2(direction[1], direction[0])) % 360
    el = math.degrees(math.asin(np.clip(direction[2], -1, 1)))
    return f"azimuth {az:.0f} deg, elevation {el:.0f} deg"


def _estimate_camera(surface: _Surface, extra: ExtraView, distance: float, fov_deg: float, current: np.ndarray, acc: np.ndarray):
    """Best camera for an extra photo: silhouette IoU search, then colour re-ranking of the top few."""
    alpha = np.asarray(extra.photo.convert("RGBA"), dtype=np.float32)[:, :, 3] / 255.0
    if (alpha > 0.5).sum() < 100:
        return None, 0.0, ""
    target, (pcx, pcy, pside) = _normalised_mask(None, alpha)

    rng = np.random.default_rng(0)
    idx = rng.choice(len(surface.p), size=min(_SUBSAMPLE, len(surface.p)), replace=False)
    p = surface.p[idx]

    def iou_for(rotation: np.ndarray) -> float:
        u, v, _, _ = _project(p, None, _Camera(rotation, distance, fov_deg))
        mask, _ = _normalised_mask(np.stack([u, v], axis=1), None)
        union = (mask | target).sum()
        return float((mask & target).sum() / union) if union else 0.0

    candidates = []
    for d in _candidate_directions(extra.hint):
        rolls = range(0, 360, 30) if abs(d[2]) > 0.7 else (-30, -15, 0, 15, 30)
        for roll in rolls:
            rot = _rotation_for(d, roll)
            candidates.append((iou_for(rot), rot, d))
    candidates.sort(key=lambda c: c[0], reverse=True)

    # Local refinement of the best few with small rotations about the camera axes.
    refined = []
    for score, rot, d in candidates[:8]:
        best = (score, rot)
        for step in (6.0, 2.5):
            for axis in range(3):
                for sign in (-1, 1):
                    trial = _axis_rotation(axis, sign * step) @ best[1]
                    s = iou_for(trial)
                    if s > best[0]:
                        best = (s, trial)
        refined.append(best)

    def framing_for(rotation: np.ndarray):
        u, v, _, _ = _project(p, None, _Camera(rotation, distance, fov_deg))
        _, (cu, cv, side) = _normalised_mask(np.stack([u, v], axis=1), None)
        return (cu, cv, pside / side, pcx, pcy)

    # Colour agreement with what the main photo (or the prediction) already says breaks ties
    # between mirror-image poses that share a silhouette.
    photo_rgb = np.asarray(extra.photo.convert("RGB"), dtype=np.float32) / 255.0
    sub = _Surface(surface.mask, p, surface.n[idx], current[idx])
    best_score, best_cam = -1.0, None
    best_iou = 0.0
    for score, rot in sorted(refined, key=lambda r: r[0], reverse=True)[:6]:
        cam = _Camera(rot, distance, fov_deg, framing_for(rot))
        u, v, depth, facing = _project(sub.p, sub.n, cam)
        h, w = photo_rgb.shape[:2]
        x, y = _to_pixels(u, v, cam, w, h)
        visible, _ = _visibility(x / w, y / h, depth, facing, grid=256)
        sel = (visible > 0) & (facing > 0.4)
        if sel.sum() < 200:
            continue
        import cv2

        col = _sample(photo_rgb, x[sel], y[sel], cv2.BORDER_REPLICATE).reshape(-1, 3)
        ref = sub.predicted[sel]
        col = col * ((ref.mean(axis=0) + 1e-3) / (col.mean(axis=0) + 1e-3))
        diff = float(np.abs(col - ref).mean())
        combined = score - 0.6 * diff
        if combined > best_score:
            best_score, best_cam, best_iou = combined, cam, score
    if best_cam is None:
        return None, refined[0][0] if refined else 0.0, ""
    return best_cam, best_iou, _describe(best_cam.rotation[0])


def _axis_rotation(axis: int, degrees: float) -> np.ndarray:
    """Rotation of the camera frame about one of its own axes (0 = view, 1 = right, 2 = up)."""
    c, s = math.cos(math.radians(degrees)), math.sin(math.radians(degrees))
    if axis == 0:
        return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])
    if axis == 1:
        return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]])


# --- Helpers ------------------------------------------------------------------------------------


def _sample(image: np.ndarray, x: np.ndarray, y: np.ndarray, border: int) -> np.ndarray:
    """Bilinear lookups at pixel coordinates; cv2.remap caps maps below 32767 rows, so lay them out 2D."""
    import cv2

    width = 4096
    count = len(x)
    pad = (-count) % width
    map_x = np.pad(x.astype(np.float32), (0, pad)).reshape(-1, width)
    map_y = np.pad(y.astype(np.float32), (0, pad)).reshape(-1, width)
    out = cv2.remap(image, map_x, map_y, cv2.INTER_LINEAR, borderMode=border, borderValue=0)
    channels = image.shape[2] if image.ndim == 3 else 1
    return out.reshape(-1, channels)[:count]


def _smoothstep(x, lo, hi):
    t = np.clip((x - lo) / (hi - lo), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def _pad_from_nearest(texture: np.ndarray, mask: np.ndarray) -> np.ndarray:
    """Fills texels outside the UV islands with the nearest island colour (avoids seams under mipmapping)."""
    from scipy import ndimage

    _, (iy, ix) = ndimage.distance_transform_edt(~mask, return_indices=True)
    return texture[iy, ix]
