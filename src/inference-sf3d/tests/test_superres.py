"""Super-resolution plumbing with randomly initialised weights (no download needed)."""

from __future__ import annotations

import numpy as np
import pytest
from PIL import Image

torch = pytest.importorskip("torch")

from app.superres import SuperResolver, _build_rrdbnet  # noqa: E402


@pytest.fixture(scope="module")
def weights(tmp_path_factory):
    torch.manual_seed(0)
    path = tmp_path_factory.mktemp("esrgan") / "random_x4.pth"
    torch.save({"params_ema": _build_rrdbnet().state_dict()}, path)
    return path


def test_official_layer_names_load_strictly(weights):
    # Same keys as RealESRGAN_x4plus.pth's params_ema; strict loading would fail otherwise.
    SuperResolver(weights)


def test_tiles_are_stitched_into_place(weights):
    # Swap the network for a pixel-exact x4 nearest upscale: tiling must reproduce it exactly.
    sr = SuperResolver(weights, tile=16, tile_pad=4)
    sr.net = lambda x: torch.nn.functional.interpolate(x, scale_factor=4, mode="nearest")
    rgb = np.random.default_rng(1).random((37, 23, 3), dtype=np.float32)

    out = sr.upscale_rgb(rgb)

    assert out.shape == (148, 92, 3)
    np.testing.assert_array_equal(out, rgb.repeat(4, axis=0).repeat(4, axis=1))


def test_photo_keeps_alpha_and_respects_max_side(weights):
    sr = SuperResolver(weights, tile=32)
    rgba = np.zeros((40, 30, 4), dtype=np.uint8)
    rgba[10:30, 5:25] = (200, 100, 50, 255)
    up = sr.upscale_photo(Image.fromarray(rgba, "RGBA"), max_side=100)

    assert up.mode == "RGBA"
    assert up.size == (75, 100)  # 120x160 capped to 100 on the long side
    alpha = np.asarray(up.getchannel("A"))
    assert alpha[50, 37] > 250 and alpha[2, 2] < 5


def test_missing_weights_raise(tmp_path):
    with pytest.raises(FileNotFoundError):
        SuperResolver(tmp_path / "nope.pth")
