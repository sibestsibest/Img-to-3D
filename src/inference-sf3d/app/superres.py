"""Real-ESRGAN x4 upscaling of the input photo before it is projected onto the texture.

A 4096px texture needs far more photo pixels than a typical web image has (a 736px photo leaves
~600px for the object, stretched over ~2500 texels). Real-ESRGAN (xinntao/Real-ESRGAN, BSD-3-Clause)
restores edges and printed detail; fine texture it adds is synthesised, not recovered.

The network is the RRDBNet of the official RealESRGAN_x4plus.pth (basicsr/archs/rrdbnet_arch.py),
re-declared here so no extra package is needed. Runs tile by tile to bound memory.
"""

from __future__ import annotations

import logging
from pathlib import Path

import numpy as np
from PIL import Image

log = logging.getLogger("sf3d.superres")


def _build_rrdbnet(num_feat: int = 64, num_block: int = 23, num_grow_ch: int = 32):
    import torch
    from torch import nn
    from torch.nn import functional as F

    class ResidualDenseBlock(nn.Module):
        def __init__(self):
            super().__init__()
            nf, gc = num_feat, num_grow_ch
            self.conv1 = nn.Conv2d(nf, gc, 3, 1, 1)
            self.conv2 = nn.Conv2d(nf + gc, gc, 3, 1, 1)
            self.conv3 = nn.Conv2d(nf + 2 * gc, gc, 3, 1, 1)
            self.conv4 = nn.Conv2d(nf + 3 * gc, gc, 3, 1, 1)
            self.conv5 = nn.Conv2d(nf + 4 * gc, nf, 3, 1, 1)
            self.lrelu = nn.LeakyReLU(negative_slope=0.2, inplace=True)

        def forward(self, x):
            x1 = self.lrelu(self.conv1(x))
            x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
            x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
            x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
            x5 = self.conv5(torch.cat((x, x1, x2, x3, x4), 1))
            return x5 * 0.2 + x

    class RRDB(nn.Module):
        def __init__(self):
            super().__init__()
            self.rdb1, self.rdb2, self.rdb3 = ResidualDenseBlock(), ResidualDenseBlock(), ResidualDenseBlock()

        def forward(self, x):
            return self.rdb3(self.rdb2(self.rdb1(x))) * 0.2 + x

    class RRDBNet(nn.Module):
        def __init__(self):
            super().__init__()
            self.conv_first = nn.Conv2d(3, num_feat, 3, 1, 1)
            self.body = nn.Sequential(*[RRDB() for _ in range(num_block)])
            self.conv_body = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
            self.conv_up1 = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
            self.conv_up2 = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
            self.conv_hr = nn.Conv2d(num_feat, num_feat, 3, 1, 1)
            self.conv_last = nn.Conv2d(num_feat, 3, 3, 1, 1)
            self.lrelu = nn.LeakyReLU(negative_slope=0.2, inplace=True)

        def forward(self, x):
            feat = self.conv_first(x)
            feat = feat + self.conv_body(self.body(feat))
            feat = self.lrelu(self.conv_up1(F.interpolate(feat, scale_factor=2, mode="nearest")))
            feat = self.lrelu(self.conv_up2(F.interpolate(feat, scale_factor=2, mode="nearest")))
            return self.conv_last(self.lrelu(self.conv_hr(feat)))

    return RRDBNet()


class SuperResolver:
    SCALE = 4

    def __init__(self, weights: Path, device: str = "cpu", tile: int = 192, tile_pad: int = 12):
        import torch

        if not weights.is_file():
            raise FileNotFoundError(f"Real-ESRGAN weights not found: {weights}")
        state = torch.load(weights, map_location="cpu", weights_only=True)
        state = state.get("params_ema", state.get("params", state))
        net = _build_rrdbnet()
        net.load_state_dict(state, strict=True)
        self.net = net.eval().to(device)
        self.device = device
        self.tile = tile
        self.tile_pad = tile_pad

    def upscale_rgb(self, rgb: np.ndarray) -> np.ndarray:
        """float32 HxWx3 in [0, 1] -> float32 (4H)x(4W)x3."""
        import torch

        h, w = rgb.shape[:2]
        s, t, p = self.SCALE, self.tile, self.tile_pad
        src = torch.from_numpy(np.ascontiguousarray(rgb.transpose(2, 0, 1)))[None].to(self.device)
        out = np.zeros((h * s, w * s, 3), dtype=np.float32)
        with torch.no_grad():
            for y0 in range(0, h, t):
                for x0 in range(0, w, t):
                    y1, x1 = min(y0 + t, h), min(x0 + t, w)
                    # Pad each tile with context so seams do not show; crop the padding off afterwards.
                    py0, px0, py1, px1 = max(y0 - p, 0), max(x0 - p, 0), min(y1 + p, h), min(x1 + p, w)
                    result = self.net(src[:, :, py0:py1, px0:px1]).clamp_(0, 1)[0].permute(1, 2, 0).cpu().numpy()
                    oy, ox = (y0 - py0) * s, (x0 - px0) * s
                    out[y0 * s:y1 * s, x0 * s:x1 * s] = result[oy:oy + (y1 - y0) * s, ox:ox + (x1 - x0) * s]
        return out

    def upscale_photo(self, photo: Image.Image, max_side: int) -> Image.Image:
        """RGBA photo -> RGBA upscaled x4 (colour by the network, alpha bicubic), capped at max_side."""
        rgba = photo.convert("RGBA")
        rgb = np.asarray(rgba.convert("RGB"), dtype=np.float32) / 255.0
        up = Image.fromarray((self.upscale_rgb(rgb) * 255.0 + 0.5).clip(0, 255).astype(np.uint8), "RGB")
        alpha = rgba.getchannel("A").resize(up.size, Image.Resampling.BICUBIC)
        up.putalpha(alpha)
        if max(up.size) > max_side:
            k = max_side / max(up.size)
            up = up.resize((max(1, round(up.width * k)), max(1, round(up.height * k))), Image.Resampling.LANCZOS)
        return up
