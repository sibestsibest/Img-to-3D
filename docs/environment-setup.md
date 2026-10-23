# Environment setup

What each workflow needs, what was found on the development VM on 2026-10-23, and how to install
the optional tools. The existing quick start in the [README](../README.md) is unchanged; this file
adds the tools needed by the extension phases ([integration-plan.md](integration-plan.md)).

## 1. Requirements per workflow

| Workflow | Required | Optional | GPU | RAM |
|---|---|---|---|---|
| Geometry only (parametric engine, drawings, exports) | uv + Python 3.11 with `src/parametric-geometry/requirements.txt` (see its README); .NET 10 + Node.js for the app (Phase 2) | | none | 4 GB |
| Single-image AI, TripoSR (existing) | as above + models (`scripts/download-models.ps1`, 3.5 GB) | CUDA 13 + cuDNN 9 for `-Gpu` | optional | 16 GB |
| Single-image AI, SF3D/SPAR3D (existing) | Python 3.11 via uv, VS C++ Build Tools, gated Hugging Face weights | CUDA | optional | 16 GB+ |
| Meshroom reconstruction (Phase 3) | Meshroom 2025.1.0 (or a version whose CLI is verified) | | NVIDIA, compute capability ≥ 5.0, for the full pipeline; draft pipeline on CPU | 16–32 GB |
| Dataset generation (Phase 4) | Blender 5.2 LTS (or 4.5 LTS), uv + Python 3.11 (`tools/py`) | | optional (Cycles GPU), EEVEE/Workbench work on CPU | 8 GB+ |
| Asset ingestion (Phase 5) | uv + Python 3.11 | internet for online sources; api.data.gov key for Smithsonian | none | 4 GB |
| AI training (Phase 6) | uv + Python 3.11, PyTorch (pinned at Phase 6) | | strongly recommended; CPU only for tiny runs | 16 GB |
| AI inference (Phase 6) | .NET only (ONNX model in `models/`) | | none | 2 GB extra |

No paid service is needed for any of them.

## 2. Development VM inventory (2026-10-23)

| Item | Found |
|---|---|
| OS | Windows 10 IoT Enterprise LTSC 2021 (10.0.19044), QEMU VM |
| CPU / RAM | 16 logical CPUs / 32 GB |
| GPU | none (Microsoft Basic Display Adapter, Remote Display Adapter); no `nvidia-smi`, no `nvcc` |
| .NET SDK | 10.0.203, 10.0.400 |
| Node.js | 24.11.0 (nvm-noinstall) |
| uv | 0.13.3 |
| Python | uv-managed 3.11.17 and 3.15.0; nothing on PATH as `python` or `py` |
| SF3D venvs | `src/inference-sf3d/.venv` and `.venv-spar3d`: Python 3.11.17, torch 2.5.1+cpu, CUDA not available |
| Blender | not installed |
| Meshroom / AliceVision | not installed |
| Docker | not installed |
| Models | `models/triposr`, `models/u2net`, `models/clip`, `models/realesrgan` present (3.5 GB) |

Consequences: Phase 1 was fully verified here (worker venv `src/parametric-geometry/.venv`, Python 3.11.17). Phase 2 can be too. Phase 3 can be verified here only with fake
process tests (and the draft pipeline if Meshroom is installed). Phase 4 needs Blender installed.
Phase 6 training runs here on CPU only for small models; GPU training belongs on the home machine.

## 3. Installing the optional tools (Windows)

### Meshroom

1. Download the Windows release from <https://github.com/alicevision/meshroom/releases> (2025.1.0
   is the latest stable release found; nightly builds are marked unstable). Licence: MPL-2.0.
2. Extract to a folder without spaces, for example `C:\Tools\Meshroom-2025.1.0`.
3. Check the CLI before configuring the app:
   ```powershell
   & 'C:\Tools\Meshroom-2025.1.0\meshroom_batch.exe' --help
   ```
4. Configure (Phase 3): `Photogrammetry__MeshroomPath=C:\Tools\Meshroom-2025.1.0`.

The binaries are built with CUDA 12 and need an NVIDIA GPU with compute capability 5.0 or newer for
depth maps. Without one only the draft (CPU) pipeline is usable, with lower quality.

### Blender

1. Install Blender 5.2 LTS from <https://www.blender.org/download/> (GPL; the scripts run inside
   Blender and are not linked into the app), or `winget install BlenderFoundation.Blender`.
2. Check: `& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --version`.
3. Configure (Phase 4): `BLENDER_EXE` environment variable or `tools/py` config.

### Python tools (`tools/py`, Phase 4 onward)

Created in Phase 4, following the pattern of `scripts/setup-sf3d.ps1`:

```powershell
uv python install 3.11
uv venv tools\py\.venv --python 3.11
uv pip install --python tools\py\.venv\Scripts\python.exe -r tools\py\requirements.txt
```

`requirements.txt` will be fully pinned. A CUDA build of PyTorch is selected only when
`nvidia-smi` is found, as `setup-sf3d.ps1` already does.

### GPU machine

As in the README's [GPU section](../README.md#gpu-cuda): driver 580+, CUDA 13, cuDNN 9 for
ONNX Runtime GPU. Meshroom ships its own CUDA runtime and only needs a recent driver. The GPU model
of the home machine is not yet known; record it here once checked (`nvidia-smi`).

## 4. Checks

Until `scripts/check-env.ps1` exists (Phase 10), the checks used for this inventory were:

```powershell
dotnet --list-sdks; node --version; uv --version; uv python list --only-installed
Get-Command blender, meshroom_batch, nvidia-smi -ErrorAction SilentlyContinue
Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion
& src\inference-sf3d\.venv\Scripts\python.exe -c "import torch; print(torch.__version__, torch.cuda.is_available())"
```

## 5. Known issue

`scripts/test.ps1` fails with `MSB3027` (file locked) while the API is running from the same
`bin/Debug` folder. Stop the API first, or run the API tests with
`dotnet test tests\backend\ImageTo3D.Api.Tests --artifacts-path <other folder>`.
