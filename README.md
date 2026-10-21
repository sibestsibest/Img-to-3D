# Image to 3D (TripoSR, .NET)

Upload one photo of a Gunpla part, plastic model component or mechanical accessory and get an
**estimated** 3D mesh you can preview in the browser, scale in millimetres and download as GLB or
STL for 3D printing.

> **Read this first: what the output is and is not.** TripoSR reconstructs a *plausible* shape from
> a single image. It cannot see the back, the inside, or anything hidden, so those parts are
> invented. Holes, slots, peg joints, snap fits, wall thickness and small mechanical details are
> frequently wrong, merged or missing. The mesh has no real-world scale; the millimetre size is
> whatever you set. **Do not expect a part that fits a kit, mates with other parts or matches
> tolerances.** Treat the output as a starting point for remodelling, not a scan.

## Architecture

```
Browser ──► React + Three.js (Vite, :5173)
              │  /api only (same origin via proxy)
              ▼
          ASP.NET Core API (:5080) ── SQLite (jobs) + local file store (artifacts)
              │  HTTP, loopback (or API key + private network)
              ▼
          Inference service, ASP.NET Core + ONNX Runtime (:5090, 127.0.0.1)
              │  loads models once, single worker queue
              ▼
          TripoSR (ONNX) + U²-Net background removal
```

### Engines

| Engine | Service | Port | Quality | Requirements |
|---|---|---|---|---|
| **TripoSR** (default) | `src/inference` (.NET + ONNX Runtime) | 5090 | baseline; vertex colours | nothing beyond .NET |
| **Stable Fast 3D** | `src/inference-sf3d` (Python + PyTorch) | 5091 | sharp: 384² triplane, denser surface grid, 4096² texture with the photo (Real-ESRGAN-upscaled when small) projected onto visible sides, normal map | Python 3.11 (uv), VS C++ Build Tools, gated HF weights, Stability AI Community License; NVIDIA GPU optional |
| **SPAR3D** | `src/inference-sf3d` (same code, `.venv-spar3d`) | 5092 | as SF3D, plus point-cloud diffusion for better hidden sides; slower | as SF3D, separate gated HF approval |

With SF3D or SPAR3D the web UI also accepts **up to 5 photos of other sides**: the shape still comes
from the main photo, and each extra photo (camera estimated automatically, optionally hinted as
back/left/right/top/bottom) colours the sides the main photo cannot see. With one photo the AI
guesses the hidden sides.

All implement the same internal contract; select one with `.\scripts\dev.ps1 -Engine TripoSR|SF3D|SPAR3D`
(or `InferenceService:BaseUrl`). Scaling keeps textures intact: the API wraps the generated GLB in a
scaled root node instead of re-encoding it. See [src/inference-sf3d/README.md](src/inference-sf3d/README.md).

### Why the TripoSR service is .NET, not Python

The original specification called for a Python service running the official TripoSR PyTorch code.
Python is not permitted in the target environment, so the inference service runs TripoSR through
**ONNX Runtime in C#** instead, using a community ONNX export of the official `stabilityai/TripoSR`
weights. Everything the Python reference does around the network was ported and checked against
the official source (`run.py`, `tsr/system.py`, `tsr/utils.py`, `tsr/models/isosurface.py`):

| Step | Official TripoSR | This project |
|---|---|---|
| Background removal | `rembg` (U²-Net) | U²-Net ONNX with rembg's exact pre/post-processing (`BackgroundRemover.cs`) |
| Re-framing | `resize_foreground(0.85)`, grey fill | Ported 1:1 (`ImagePreprocessor.cs`) |
| Resize to 512 | `F.interpolate(bilinear, antialias)` | Pillow/torch-equivalent resampler (`Resampler.cs`) |
| Encoder | `TSR.forward` | `triplane.onnx` |
| Density grid | `query_triplane` on 256³ grid in [-0.87, 0.87] | `nerf.onnx`, chunked, same grid |
| Surface | `torchmcubes` marching cubes, threshold 25 | C# marching cubes (Bourke tables), threshold 25 |
| Colour | NeRF colour at vertices | Same |
| Export | `trimesh.export` (GLB/OBJ) | Own GLB/STL writers, validated by tests and by SharpGLTF |

Limitations of this choice are listed under [Verification status](#verification-status).

Project structure (adapted from the requested layout because `inference/` is a .NET project and
geometry code is shared by the API and the inference service):

```
src/
  frontend/                 React + TS + Vite (components/ hooks/ services/ pages/ types/)
  backend/ImageTo3D.Api/    Controllers/ Services/ Models/ DTOs/ Data/ Configuration/
  inference/ImageTo3D.Inference/  Api/ Jobs/ Models/ (ONNX) Pipeline/ Cli/
  shared/ImageTo3D.Core/    Geometry (marching cubes, GLB, STL, scaling), Imaging, Contracts
tests/
  shared/  inference/  backend/      xUnit
  (frontend tests live next to the code in src/frontend/src/**/*.test.ts so Vitest resolves node_modules)
models/   ONNX weights (downloaded)      scripts/   setup, model download, dev, test
docs/api.md                              docker-compose.yml, Dockerfiles
```

## Requirements

| | Minimum | Notes |
|---|---|---|
| OS | Windows 10/11 x64, Linux x64 | macOS untested |
| .NET SDK | 10.0 | `winget install Microsoft.DotNet.SDK.10` |
| Node.js | 20.19+ / 22.12+ | for Vite 8 |
| RAM | 16 GB | inference process peak working set measured at 5.75 GB (fp32 encoder ≈ 3.3 GB + activations) |
| Disk | 5 GB | 3.5 GB of models |
| GPU | optional | CPU works; see [GPU](#gpu-cuda) |
| Python | **not used** | |

Measured on the development machine (16 vCPU QEMU VM, 32 GB RAM, no GPU, CPU execution provider):
model load ≈ 7 s; per image ≈ 30–35 s (encoder ≈ 8–10 s, 256³ density grid ≈ 21 s, background
removal ≈ 1 s, marching cubes < 1 s).

## Quick start (Windows PowerShell)

```powershell
git clone <this repo> image-to-3d
cd image-to-3d
.\scripts\setup.ps1        # checks tools, restores packages, downloads + verifies models (~3.5 GB)
.\scripts\dev.ps1          # opens three windows: inference, API, web
```

Open <http://localhost:5173>, drop an image, click **Generate 3D model**.

Manual start, one terminal each:

```powershell
# 1. Inference service (Release is much faster for the numeric code)
cd src\inference\ImageTo3D.Inference; dotnet run -c Release
# wait for "Models loaded" or: curl http://127.0.0.1:5090/health

# 2. API
cd src\backend\ImageTo3D.Api; dotnet run

# 3. Frontend
cd src\frontend; npm run dev
```

### Generate a model without the UI

```powershell
dotnet run --project src\inference\ImageTo3D.Inference -c Release -- `
  generate "$PWD\samples\chair.png" "$PWD\out\chair" --Inference:ModelDirectory="$PWD\models"
```

Writes `input.png` (what the model saw), `mesh.glb` (model units), `mesh_100mm.stl` and
`preview_*.png` (software-rendered views, handy on machines without a GPU or browser).

Through the API with curl:

```powershell
curl.exe -c cj.txt -b cj.txt -F "image=@samples\robot.png" http://localhost:5080/api/models
curl.exe -b cj.txt http://localhost:5080/api/models/<id>
curl.exe -b cj.txt -o part.stl "http://localhost:5080/api/models/<id>/download?format=stl"
```

## Using the result for 3D printing

1. After generation, check the dimensions panel. By default the **longest side is 100 mm**.
2. Choose *Width*, *Height*, *Depth* or *Longest side*, enter the real length in mm (measure the
   physical part with calipers) and click **Apply uniform scale**. All sides change by the same
   factor; the preview row shows the result before applying.
3. Download **STL**. It is binary, in **millimetres**, **Z-up** (the viewer's height becomes Z),
   centred on X/Y and resting on Z = 0.
4. Open it in your slicer (PrusaSlicer, Bambu Studio, Cura, OrcaSlicer: *File → Import*). STL has
   no unit field. Slicers assume millimetres, which is what this file uses. If the part looks
   25.4× too small or too large, the slicer assumed inches; rescale to the size shown in the app.
5. Inspect before printing: thin walls, fused gaps, floating fragments and an invented back side
   are common. Repair or remodel in Blender, Meshmixer or your CAD tool as needed.

GLB downloads are in metres (as the glTF standard requires) and keep vertex colours; use them for
viewing or as a base for remodelling.

STL carries geometry only. The SF3D engine's photo projection makes the GLB's colours sharp on the
photographed side; the printable shape comes from the surface grid (`SF3D_ISOSURFACE_GRID`).

Changing scale only changes size. **It does not fix wrong holes, slots, joints or hidden geometry**,
and a correctly scaled model is still not dimensionally identical to the original part.

## Tests

```powershell
.\scripts\test.ps1                 # everything (real-model tests run if ./models is present)
.\scripts\test.ps1 -SkipRealModel  # no model files needed
```

| Suite | Count | What it covers |
|---|---|---|
| `tests/shared` | 115 | cutting outlines (rotated plate, rectangular and round holes, L shape, simplification tolerance, tapered edges, non-flat and open parts), 1:1 drawings (A4/A3 page choice, SVG mm scale and layers, escaping, PDF page size, millimetre scale and no embedded fonts, too-large parts, drawing checks),  plane splitting with capped cuts (cube, oblique, plate with hole both ways, cylinder, tube, cut through a vertex ring, disconnected boxes, open mesh, marching-cubes torus in three directions), polygon-with-holes triangulation, welding, connected components, boundary/non-manifold detection, oriented bounds, part classification (sheet, rod, tube, custom, needs review), GLB node reading, marching cubes watertightness/orientation/volume, GLB round-trip + independent SharpGLTF validation, STL binary format, normals, degenerate faces, GLB→STL conversion, scaling maths, image signature/decoding/limits, resampler |
| `tests/inference` | 23 | preprocessing ports, job state machine, internal API validation, artifact whitelist, API key, **real TripoSR smoke test** (chair → valid coloured mesh, Y-up proportions, density threshold sanity) and mid-inference cancellation |
| `tests/backend` | 95 | drawing report/SVG/PDF endpoints (sheet suggestion, mm scale, headers, specified thickness, escaping, refusals, isolation),  migration baseline of a pre-migration database (and refusal of one with missing columns, WAL on new databases), part design: creation and source copy, survival of job deletion and retention, owner isolation, part editing and validation, undo/redo and redo-branch cleanup, split preview/apply/undo with real cut geometry, open-mesh refusal, upload validation, job transitions, inference failure/unavailable/timeout/lost, downloads (real GLB/STL, units, orientation), scale endpoint + DTO validation, ownership isolation, idempotency, active-job limit, delete, cleanup, health without secrets, correlation IDs, path-traversal protection, HTTP client error mapping |
| `src/frontend` | 54 | drawing URLs, part tree, selection/visibility/isolation state, cutting plane, part form parsing, hash routes, scale maths and validation, API client error mapping, idempotency header, upload validation, stage/progress text |

Backend tests replace the inference HTTP client with an in-memory fake, so they need no model.

## Configuration

All settings live in each project's `appsettings.json` and can be overridden with environment
variables (`Section__Key`). Key settings:

| Setting | Default | |
|---|---|---|
| `Inference:ModelDirectory` | `../../../models` | relative to the inference project |
| `Inference:ExecutionProvider` | `Cpu` | `Cuda` needs the CUDA build |
| `Inference:CpuLimit` | 0.8 | fraction of logical CPUs ONNX Runtime uses when `IntraOpThreads` is 0 (SF3D: `SF3D_CPU_LIMIT`) |
| `Inference:MarchingCubesResolution` | 256 | 32–320; lower = faster, coarser |
| `Inference:DensityThreshold` | 25 | official TripoSR iso-level |
| `Inference:KeepLargestComponentOnly` | false | drop floating fragments |
| `Inference:JobTimeout` | 30 min | |
| `Inference:ApiKey` | empty | required when not bound to loopback |
| `Upload:MaxBytes` | 20 MB | also min/max dimension, max pixels |
| `Upload:UploadsPerMinute` | 10 | per browser |
| `Upload:MaxActiveJobsPerOwner` | 3 | |
| `InferenceService:BaseUrl` | `http://127.0.0.1:5090` | |
| `InferenceService:GenerationTimeout` | 40 min | |
| `Storage:RootPath` / `DatabasePath` | `data/` at repo root | outside any web root |
| `Storage:RetentionPeriod` | 7 days | finished jobs and files are deleted after this |
| `ModelOutput:DefaultLongestSideMm` | 100 | initial export size |
| `Storage:DesignRootPath` | `data/designs` | part-design projects; never touched by job retention |
| `PartDesign:MaxSourceTriangles` | 2,000,000 | larger meshes are refused (analysis runs in the request) |
| `PartDesign:MaxAnalysedParts` | 64 | further pieces are grouped as "fragments" |

The SQLite schema is managed by EF Core migrations (`src/backend/ImageTo3D.Api/Data/Migrations`),
applied on start. A database created by an older version (before migrations, by `EnsureCreated`)
is detected and baselined in place: its existing columns are checked and the first migration is
recorded as applied, so existing jobs are kept. Add a migration with
`dotnet ef migrations add <Name> --output-dir Data/Migrations` from the API project folder.

## Part design

After a model is generated, **Open in part designer** copies it into a project and breaks it into
parts (one per glTF node, then per connected piece; tiny floating pieces are grouped). Each part
gets a suggested fabrication category (sheet, rod, tube, custom 3D, or *needs review*), estimated
dimensions from an oriented bounding box, and geometry checks (closed, manifold, degenerate faces).
You can rename, recategorise, enter caliper measurements and manufacturing properties (material,
thickness, diameters, cut length and angle, fit allowance, stock part details), duplicate, delete,
and **split a part with a plane**: the mesh is really cut, both halves are capped (including cuts
with holes, such as through a tube or a frame) and checked to be closed solids. Every edit can be
undone and redone; projects persist and reopen at `#/designs/<id>`.

Limitations of this first version:

* Dimensions are estimates: they come from the AI model at the scale you set, not from the part.
  Category suggestions are simple shape rules, always pending your review.
* Planes are axis-aligned in the UI (the API accepts any orientation). Each half stays one part
  even if it has several separate pieces.
* Parts keep geometry and vertex colours only; SF3D/SPAR3D textures are not carried into parts.
* Splitting needs a closed surface where the plane crosses it. On an open mesh the cut is not
  capped; the split is refused unless you explicitly keep the open halves, which are then flagged.
* Not yet available: DXF, drawings for rods/tubes/curved parts, part STL export, bill of materials,
  import of external GLB/OBJ/STL files, reference search.

### Fabrication drawings (sheet parts)

For a part whose category is **Sheet**, the part designer shows a 1:1 cutting sheet and offers
**PDF** (print) and **SVG** (cutter/laser software) downloads. The outline is the exact cross-section
at mid-thickness, simplified within 0.05 mm, with holes kept. The sheet has a top view with overall
dimensions, an edge view with the thickness, a hole table, a 100 mm verification bar and a title
block (material, thickness and whether it is specified or estimated, quantity, scale 1:1, units mm).
The smallest A4/A3 page that fits the part at full size is used; nothing is ever scaled down.

* **Print at 100 % / actual size, never "fit to page"**, then measure the 100 mm bar.
* The SVG uses millimetre units (1 user unit = 1 mm); the layer `cut` holds only cutting contours.
* Parts that are not flat (curved, bulky) are refused instead of drawn: an orthographic outline is
  not a cutting pattern. Bevelled or tapered edges are drawn at mid-thickness with a warning.
* Parts larger than A3 at 1:1 are refused (split them first); tiled printing is not supported.
* Dimensions are still those of the generated model at its estimated scale unless you confirmed them.

## GPU (CUDA)

Not verified: the development machine has no NVIDIA GPU. The code path exists:

```powershell
# CUDA 12.x + cuDNN 9 installed and on PATH, NVIDIA driver recent enough for CUDA 12
dotnet run --project src\inference\ImageTo3D.Inference -c Release -p:OrtFlavor=Cuda -- --Inference:ExecutionProvider=Cuda
```

`-p:OrtFlavor=Cuda` swaps `Microsoft.ML.OnnxRuntime` for `Microsoft.ML.OnnxRuntime.Gpu` 1.31.0. If
the CUDA provider cannot load, startup logs the error and `/health` reports `unavailable`. VRAM need
is roughly the official 6 GB guidance plus the fp32 encoder; lower `ChunkSize` if you hit
out-of-memory (reported to the user as `out_of_memory`). On Windows without CUDA, CPU is the
supported path; WSL2 with CUDA is an option for Linux-style GPU deployment.

## Docker

`docker-compose.yml` builds CPU images for all three services. **Not verified here (Docker is not
installed on the development machine).**

```powershell
Copy-Item .env.example .env   # set INFERENCE_API_KEY
.\scripts\download-models.ps1
docker compose up --build     # http://localhost:8080
```

Only the web container publishes a port (on 127.0.0.1). Models are mounted read-only.

## Security notes

Implemented: size/dimension/pixel limits checked before decoding; format detected from file
signatures and fully decoded (client content type and file name are ignored); generated IDs and a
fixed set of artifact file names (no user input reaches a path, plus a resolved-path check);
artifacts stored outside any served directory; per-browser job isolation; rate limiting and an
active-job cap; configurable timeouts; RFC 7807 errors without stack traces; correlation IDs in
structured JSON logs; inference bound to loopback, refusing non-loopback binding without an API key;
no shell execution; no secrets in source control.

### Before exposing to the internet

* Replace the anonymous owner cookie with real authentication (accounts or an identity provider).
  The cookie only separates browsers; anyone who obtains it can access that browser's jobs.
* Serve over HTTPS (the cookie becomes `Secure` automatically) and add HSTS and a CSP.
* Put the API behind a reverse proxy that forwards client IPs and configure
  `ForwardedHeaders`, otherwise IP-based rate limiting sees only the proxy.
* Use a shared rate-limit store if you run more than one API instance.
* Run the inference service on a private network with `Inference:ApiKey` and TLS between services.
* Scan or re-encode uploads in an isolated process if you accept images from untrusted users at scale
  (image decoders are a common attack surface).
* Move storage to blob storage with private access (implement `IArtifactStore`) and back up SQLite
  or move to a server database.
* Add monitoring and alerting on job failures and queue length.

## Verification status

Verified on the development machine (Windows 10, CPU only):

* Real TripoSR ONNX inference on the official `chair.png`, `robot.png` and an opaque JPEG (which
  exercises background removal), producing recognisable meshes with correct orientation.
* Full flow through the browser in headless Chrome: upload → status → viewer → dimensions →
  downloads → history; plus curl tests of every API endpoint, cancellation during the encoder,
  idempotent resubmits and path-traversal attempts.
* All automated test suites listed above.

Not verified:

* Numerical parity between the ONNX export and the PyTorch model (needs Python, not allowed).
* CUDA execution, Docker images and docker-compose.
* Linux and macOS hosts (code is cross-platform; SkiaSharp Linux native assets are referenced).
* Opening the STL in a specific slicer (the file is validated programmatically: binary layout,
  outward normals, positive volume, millimetre extents).
