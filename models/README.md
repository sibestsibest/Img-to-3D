# Models

The weights are not committed (3.5 GB). Download and verify them with:

```powershell
.\scripts\download-models.ps1        # Windows
./scripts/download-models.sh         # Linux / WSL
```

Expected layout:

```
models/
  triposr/
    triplane.onnx          image[1,3,512,512] in [0,1]  ->  triplane[1,3,40,64,64]
    triplane.onnx_data     external weights (3.35 GB), must sit next to triplane.onnx
    nerf.onnx              (triplane, xyz[K,3] in [-0.87, 0.87])  ->  density[K] (activated), color[K,3]
    config.yaml            TripoSR config (radius 0.87, exp density activation, bias -1)
    LICENSE.txt            MIT
  u2net/
    u2net.onnx             background removal (rembg default model)
```

## Provenance

| File | Source | License |
|---|---|---|
| `triposr/*` | [brodatech/triposr-onnx](https://huggingface.co/brodatech/triposr-onnx): ONNX export (opset 17) of [stabilityai/TripoSR](https://huggingface.co/stabilityai/TripoSR) using the architecture code from [VAST-AI-Research/TripoSR](https://github.com/VAST-AI-Research/TripoSR) commit `d26e331` | MIT |
| `u2net/u2net.onnx` | [danielgatis/rembg](https://github.com/danielgatis/rembg) release `v0.0.0` | Apache-2.0 (U²-Net) |

The ONNX export is **community-made, not an official TripoSR release**. Its model card
publishes no numerical comparison against the PyTorch model, and the project policy here forbids
running Python, so a PyTorch-vs-ONNX parity check could not be performed. What was verified:

* tensor names, shapes and value ranges match the documented interface (logged at startup and
  asserted in `TripoSrModel`);
* the density output is the *activated* density (observed range ≈ 0 … 3000–7000), so the official
  `extract_mesh` threshold of **25.0** is used. (The model card's example uses 0.0, which would
  produce no surface on activated density.)
* end-to-end generation on the official example images produces recognisable, correctly proportioned
  meshes (`tests/inference/.../RealModelSmokeTests.cs`).

SHA-256 checksums are pinned in the download scripts. If upstream files change, the scripts fail
rather than silently using different weights.
