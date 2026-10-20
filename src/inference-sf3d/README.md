# Stable Fast 3D inference service (Python)

Optional second engine. Implements the same `/internal/jobs` contract as the .NET TripoSR
service (`src/shared/ImageTo3D.Core/Contracts/InferenceContracts.cs`), so the API switches engines
by changing `InferenceService:BaseUrl` only.

Why: SF3D ([Stability-AI/stable-fast-3d](https://github.com/Stability-AI/stable-fast-3d)) builds on
TripoSR with a 384×384 triplane (TripoSR: 64×64), DINOv2 features, DMTet surface extraction and a
baked UV texture + normal map instead of vertex colours. Surface detail and colour sharpness are
noticeably better; it is still a single-image estimate (hidden sides are invented).

## Requirements

* Windows x64 (Linux works too); CPU is supported by SF3D (`SF3D_USE_CPU` is implied when no GPU).
* Python 3.11 via [uv](https://docs.astral.sh/uv/) (the script installs it; your system Python is not touched).
* **Visual Studio Build Tools 2022, C++ workload** to compile SF3D's `texture_baker` and
  `uv_unwrapper` extensions.
* **Hugging Face access** to the gated weights: accept the license at
  <https://huggingface.co/stabilityai/stable-fast-3d>, create a *read* token and set it as the
  `HF_TOKEN` user environment variable (or run `uvx hf auth login`).

## License (read before use)

SF3D is under the **Stability AI Community License**: free for research, non-commercial use and
for organisations with **under US $1M annual revenue**. Above that, an Enterprise license from
Stability AI is required. Distribution requires showing "Powered by Stability AI"; the web UI
does this automatically when the SF3D engine is active. See `third_party/stable-fast-3d/LICENSE.md`.

## Setup and run (PowerShell)

```powershell
.\scripts\setup-sf3d.ps1          # clone pinned SF3D, venv, deps, build C++ extensions
.\scripts\run-sf3d.ps1            # http://127.0.0.1:5091/health
.\scripts\dev.ps1 -Engine SF3D    # inference + API + web, API pointed at :5091
```

First start downloads the weights (`model.safetensors`, ~4 GB) into the Hugging Face cache.

## Configuration (environment variables)

| Variable | Default | |
|---|---|---|
| `SF3D_MODEL` | `stabilityai/stable-fast-3d` | HF id or local folder with `config.yaml` + `model.safetensors` |
| `SF3D_DEVICE` | `auto` | `cpu`, `cuda` |
| `SF3D_TEXTURE_RESOLUTION` | 1024 | baked texture size |
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

## Behaviour notes

* SF3D runs encoder, mesh extraction, UV unwrapping and texture baking in one call and reports no
  internal progress, so the UI shows the stage without a percentage.
* Cancelling a running job takes effect after the current model call finishes (PyTorch cannot be
  interrupted safely mid-call); queued jobs cancel immediately.
* The output is rotated 180° about Y relative to SF3D's Gradio orientation so the photographed side
  faces +Z (three.js default camera), matching the TripoSR engine.
