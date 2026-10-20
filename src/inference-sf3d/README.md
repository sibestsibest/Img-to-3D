# Stable Fast 3D / SPAR3D inference service (Python)

Optional engines. Implements the same `/internal/jobs` contract as the .NET TripoSR
service (`src/shared/ImageTo3D.Core/Contracts/InferenceContracts.cs`), so the API switches engines
by changing `InferenceService:BaseUrl` only. One code base, two models (`SF3D_ENGINE`):

* **SF3D** ([Stability-AI/stable-fast-3d](https://github.com/Stability-AI/stable-fast-3d)), port 5091:
  builds on TripoSR with a 384×384 triplane (TripoSR: 64×64), DINOv2 features, DMTet surface
  extraction and a baked UV texture + normal map instead of vertex colours.
* **SPAR3D** ([Stability-AI/stable-point-aware-3d](https://github.com/Stability-AI/stable-point-aware-3d)),
  port 5092: SF3D's successor. It first samples a point cloud of the whole object (point diffusion),
  then reconstructs conditioned on it, so the hidden sides are noticeably better. Slower on CPU.

Both are single-image estimates: the shape comes from the main photo, hidden sides are invented.
Extra photos of other sides (multi-photo jobs) replace the invented colour where they see the object.

## Requirements

* Windows x64 (Linux works too). Runs on CPU; an NVIDIA GPU is used automatically when the setup
  script found one together with the CUDA 12.x toolkit (see below).
* Python 3.11 via [uv](https://docs.astral.sh/uv/) (the script installs it; your system Python is not touched).
* **Visual Studio Build Tools 2022, C++ workload** to compile SF3D's `texture_baker` and
  `uv_unwrapper` extensions.
* **Hugging Face access** to the gated weights: accept the license at
  <https://huggingface.co/stabilityai/stable-fast-3d> (and, for SPAR3D,
  <https://huggingface.co/stabilityai/stable-point-aware-3d> — a separate approval), create a *read*
  token and set it as the `HF_TOKEN` user environment variable (or run `uvx hf auth login`).

## License (read before use)

SF3D and SPAR3D are under the **Stability AI Community License**: free for research, non-commercial use and
for organisations with **under US $1M annual revenue**. Above that, an Enterprise license from
Stability AI is required. Distribution requires showing "Powered by Stability AI"; the web UI
does this automatically when the SF3D engine is active. See `third_party/stable-fast-3d/LICENSE.md`.

## Setup and run (PowerShell)

```powershell
.\scripts\setup-sf3d.ps1                   # SF3D: clone pinned repo, .venv, deps, build C++ extensions, Real-ESRGAN
.\scripts\run-sf3d.ps1                     # http://127.0.0.1:5091/health
.\scripts\dev.ps1 -Engine SF3D             # inference + API + web, API pointed at :5091

.\scripts\setup-sf3d.ps1 -Engine SPAR3D    # SPAR3D: separate .venv-spar3d (its extensions share SF3D's package names)
.\scripts\run-sf3d.ps1 -Engine SPAR3D      # http://127.0.0.1:5092/health
.\scripts\dev.ps1 -Engine SPAR3D
```

First start downloads the weights (`model.safetensors`, ~4 GB for SF3D) into the Hugging Face cache;
SPAR3D's background remover (`transparent-background`) downloads its own checkpoint on first use.

### GPU

`setup-sf3d.ps1 -Device Auto` (default) installs the CUDA build of PyTorch (cu124) and compiles the
extensions with CUDA kernels when both `nvidia-smi` and `nvcc` (CUDA 12.x toolkit) are present;
otherwise it installs the CPU build. `-Device Cuda` / `-Device Cpu` force a mode; re-run the script
to switch. At run time `SF3D_DEVICE=auto` picks the GPU when PyTorch sees one (`/health` reports the
execution provider). **The GPU path is not verified here** (the development machine has no NVIDIA GPU).

## Configuration (environment variables)

| Variable | Default | |
|---|---|---|
| `SF3D_ENGINE` | `sf3d` | `spar3d` (set by `run-sf3d.ps1 -Engine SPAR3D`; needs `.venv-spar3d`) |
| `SF3D_MODEL` | `stabilityai/stable-fast-3d` | HF id or local folder with `config.yaml` + `model.safetensors` |
| `SPAR3D_MODEL` | `stabilityai/stable-point-aware-3d` | as above, for SPAR3D |
| `SPAR3D_CROP_RATIO` | 1.3 | SPAR3D's framing (official default) |
| `SF3D_SUPER_RESOLUTION` | 1 | Real-ESRGAN x4 of photos whose re-framed crop is below `SF3D_SUPER_RESOLUTION_BELOW` (2048) px |
| `SF3D_REALESRGAN_WEIGHTS` | `models/realesrgan/RealESRGAN_x4plus.pth` | downloaded by the setup script |
| `SF3D_MAX_EXTRA_IMAGES` | 5 | extra photos used per job |
| `SF3D_DEVICE` | `auto` | `cpu`, `cuda` |
| `SF3D_CPU_LIMIT` | 0.8 | fraction of logical CPUs used (12 threads on 16); `OMP_NUM_THREADS` overrides |
| `SF3D_TEXTURE_RESOLUTION` | 2048 | SF3D's baked texture size (base colour before projection, normal map) |
| `SF3D_ISOSURFACE_GRID` | 129 | surface grid: vertices per axis of a generated cubic grid, or `official` (SF3D's ~80 cells/axis) |
| `SF3D_PHOTO_PROJECTION` | 1 | `0` keeps SF3D's predicted colours everywhere |
| `SF3D_PROJECTION_TEXTURE_RESOLUTION` | 4096 | final base-colour texture size when projecting |
| `SF3D_MATERIAL` | `plastic` | `estimated` keeps SF3D's metallic/roughness guess |
| `SF3D_REMESH` | `none` | `triangle` / `quad` remeshing |
| `SF3D_FOREGROUND_RATIO` | 0.85 | |
| `SF3D_MAX_QUEUE` | 8 | |
| `SF3D_API_KEY` | empty | required for non-loopback binding |
| `SF3D_U2NET_HOME` | `models/u2net` | reuses the downloaded `u2net.onnx` |

## Tests

```powershell
cd src\inference-sf3d
.venv\Scripts\python -m pytest              # contract tests with a fake engine
$env:SF3D_SMOKE = "1"; .venv\Scripts\python -m pytest tests/test_smoke_real.py -s   # real model
```

## Quality settings

Defaults favour detail over speed. Measured on the development machine (16 vCPU, CPU only,
12 threads) with a 736×562 photo of a detailed terrain model:

| | Official SF3D settings | Defaults here |
|---|---|---|
| Surface grid | ~80 cells/axis → 23.7k triangles | 128 cells/axis → 78k triangles |
| Base colour | 1024² predicted, JPEG | 4096² PNG, photo projected on visible sides |
| Normal map | 1024² JPEG | 2048² PNG |
| Generation time | ~57 s | ~130 s + ~10 s projection |
| GLB size | ~1 MB | ~9 MB |

**Photo projection.** SF3D predicts colour from a low-resolution triplane, so even the side shown
in the photo comes out blurry. SF3D always assumes the same input camera (distance 1.6, 40° FOV),
so `app/projection.py` maps every visible, camera-facing texel to its pixel in the background-removed
photo and blends the photo in (feathered at grazing angles and at the silhouette edge). Hidden sides
keep SF3D's prediction, colour-matched to the photo. The step is skipped (and logged) when the
mesh silhouette and the photo mask disagree (IoU < 0.6). Only colour changes; geometry, and with it
the STL, is unaffected.

**Super-resolution.** A 4096² texture needs more photo pixels than a typical web photo has, so
photos whose re-framed crop is under 2048 px are upscaled x4 with Real-ESRGAN (BSD-3-Clause,
`app/superres.py`) before projection (~90 s for an 844 px crop on CPU). Edges, rivets and printed
markings get crisper; fine surface grain is synthesised, not recovered. A large original photo
(3000+ px, not re-compressed by a chat app) skips this step and gives real detail.

**Extra photos (multi-photo jobs).** Up to 5 photos of other sides, each with an optional view hint
(`auto`, `back`, `left`, `right`, `top`, `bottom`, `front`). Each is background-removed and its
camera is estimated against the generated mesh: silhouette IoU over ~400 viewing directions × rolls
(narrowed to ±55° around the hint), refined locally, then re-ranked by colour agreement with what is
already known (resolves mirror-symmetric poses). Matches below IoU 0.7 are skipped and logged.
Accepted photos only fill texels the main photo does not cover well, exposure-matched to it. The
shape is unchanged: extra photos colour hidden sides, they do not reconstruct them.

**Dense grid.** The generated cubic grid ships its edge list (`app/tetgrid.py`); SF3D would otherwise
derive it with `torch.unique` over ~75M tet edges, which exhausts memory at 129 vertices per axis.

## Behaviour notes

* SF3D runs encoder, mesh extraction, UV unwrapping and texture baking in one call and reports no
  internal progress, so the UI shows the stage without a percentage.
* Cancelling a running job takes effect after the current model call finishes (PyTorch cannot be
  interrupted safely mid-call); queued jobs cancel immediately.
* The output is rotated 180° about Y relative to SF3D's Gradio orientation so the photographed side
  faces +Z (three.js default camera), matching the TripoSR engine.
