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
| `tests/shared` | 138 | CLIP tokenizer (BPE merges, reference token IDs, truncation), CLIP preprocessing (normalisation, alpha, centre crop), bicubic resampling, cosine/softmax/clustering, perceptual hash (resize/JPEG tolerance, distinct pictures, transparency) and thumbnails, DXF R12 export (header, mm units, CUT layer closed contours at 1:1, annotation layer, text encoding), drawing revision label, cutting outlines (rotated plate, rectangular and round holes, L shape, simplification tolerance, tapered edges, non-flat and open parts), 1:1 drawings (A4/A3 page choice, SVG mm scale and layers, escaping, PDF page size, millimetre scale and no embedded fonts, too-large parts, drawing checks),  plane splitting with capped cuts (cube, oblique, plate with hole both ways, cylinder, tube, cut through a vertex ring, disconnected boxes, open mesh, marching-cubes torus in three directions), polygon-with-holes triangulation, welding, connected components, boundary/non-manifold detection, oriented bounds, part classification (sheet, rod, tube, custom, needs review), GLB node reading, marching cubes watertightness/orientation/volume, GLB round-trip + independent SharpGLTF validation, STL binary format, normals, degenerate faces, GLB→STL conversion, scaling maths, image signature/decoding/limits, resampler |
| `tests/inference` | 23 | preprocessing ports, job state machine, internal API validation, artifact whitelist, API key, **real TripoSR smoke test** (chair → valid coloured mesh, Y-up proportions, density threshold sanity) and mid-inference cancellation |
| `tests/backend` | 179 | real CLIP model (caption matching on chair/robot, resize/JPEG stability, zero-shot, missing files, disabled), ranking (signals, re-normalisation without embeddings, duplicates, input photo, groups, new viewpoint, accepted evidence, capped relation), model evaluation (listing only, downloadability, licence, face count, geometry, scale), ranking through the API with a fake embedder (fallback, failure isolation, embedding cache), Sketchfab search (licence mapping, age-restricted skipped, preview host rules, cache, failures, timeout, disabled), GLB/STL upload checks (closed/open, unitless STL, ASCII STL/OBJ/broken/oversized refused, duplicates, file download, review, removal, isolation), reference search against a fake web (attribution and licence normalisation, previews and their size/host/redirect limits, cache and clearing, per-source failures with bounded retries, timeouts, rate limiting, empty and malformed answers, disabled sources, exact/near duplicates and input match, review decisions, manual pictures, sessions from jobs, owner isolation, no credentials sent, cancellation), exports (DXF and part STL endpoints, BOM with formula neutralisation, rod cut list grouping and estimate marking, draft package files and SHA-256 manifest, warnings, numbered immutable releases, export blocked by errors, owner isolation, release deletion with the project), drawing report/SVG/PDF endpoints (sheet suggestion, mm scale, headers, specified thickness, escaping, refusals, isolation),  migration baseline of a pre-migration database (and refusal of one with missing columns, WAL on new databases), part design: creation and source copy, survival of job deletion and retention, owner isolation, part editing and validation, undo/redo and redo-branch cleanup, split preview/apply/undo with real cut geometry, open-mesh refusal, upload validation, job transitions, inference failure/unavailable/timeout/lost, downloads (real GLB/STL, units, orientation), scale endpoint + DTO validation, ownership isolation, idempotency, active-job limit, delete, cleanup, health without secrets, correlation IDs, path-traversal protection, HTTP client error mapping |
| `src/frontend` | 68 | ranking order, visual/viewpoint grouping, suggestion and confidence labels, model API calls, reference filters, licence tones, source outcomes, reference API calls and routes, drawing URLs, part tree, selection/visibility/isolation state, cutting plane, part form parsing, hash routes, scale maths and validation, API client error mapping, idempotency header, upload validation, stage/progress text |

Backend tests replace the inference HTTP client with an in-memory fake, so they need no model, and
the reference-search HTTP handler with a fake web and CLIP with a deterministic fake, so they never
contact the internet; the real-model tests run only when the model files are present.

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
| `Storage:ReferenceRootPath` | `data/references` | reference searches (input photo, previews); never touched by job retention |
| `Storage:ReferenceCachePath` | `data/reference-cache` | disposable download cache; safe to delete at any time |
| `ReferenceSearch:WikimediaEnabled` / `OpenverseEnabled` / `InternetArchiveEnabled` | `true` | turn individual sources off |
| `ReferenceSearch:UserAgent` | `ImageTo3D/0.4 (…)` | add your contact details if you search a lot (Wikimedia policy) |
| `ReferenceSearch:SourceTimeout` / `MaxRetries` | 12 s / 1 | per source; 429 is never retried |
| `ReferenceSearch:ResultsPerSource` | 20 | Openverse allows at most 20 without an account |
| `ReferenceSearch:CacheTtl` | 7 days | cached search responses |
| `ReferenceSearch:ThumbnailMaxSide` / `ThumbnailMaxBytes` | 640 px / 4 MB | only previews are downloaded |
| `ReferenceSearch:NearDuplicateDistance` | 6 | dHash bits (of 64) for a near duplicate |
| `Embeddings:Enabled` / `ModelDirectory` | `true` / `models/clip` | optional CLIP ranking; missing files mean basic ranking |
| `Embeddings:IntraOpThreads` | 2 | CPU threads per embedding call |
| `Embeddings:GroupThreshold` | 0.86 | mean cosine for pictures to share a visual group |
| `ModelSearch:SketchfabEnabled` | `true` | anonymous Sketchfab listing search |
| `ModelSearch:MaxUploadBytes` / `MaxTriangles` | 64 MB / 2,000,000 | uploaded GLB/STL limits |

The SQLite schema is managed by EF Core migrations (`src/backend/ImageTo3D.Api/Data/Migrations`),
applied on start. A database created by an older version (before migrations, by `EnsureCreated`)
is detected and baselined in place: its existing columns are checked and the first migration is
recorded as applied, so existing jobs are kept. Add a migration with
`dotnet ef migrations add <Name> --output-dir Data/Migrations` from the API project folder.

## Reference search

**References** (`#/references`), or **Find references for this photo** next to a job, starts a
search for existing pictures of the object before you rely on a generated model. Enter what it is
(kit or part name; there is no automatic image recognition yet) and search:

| Source | Access | Licence data |
|---|---|---|
| Wikimedia Commons (MediaWiki API) | free, no account | per file (`extmetadata`) |
| Openverse (`api.openverse.org`) | free, no account; anonymous requests are rate limited | per image (Creative Commons) |
| Internet Archive (advanced search, images only) | free, no account | often missing, then shown as unknown |

No paid API, key, login cookie or scraping is used. Each source runs in parallel with its own
timeout and one retry, reports its own status (ok, no results, failed, timed out, rate limited,
disabled), and never fails the others; with every source down the page still offers **search by
hand** links (Google/Bing/DuckDuckGo images, Commons, Openverse, Gundam Wiki, HobbyLink Japan,
Sketchfab, Printables) and **Add your own picture**. Every result keeps its source page, direct
image address when known, title, author, licence (normalised, with the source's own wording),
search query and retrieval time; the attribution line is built from them.

Only previews are downloaded (at most 640 px, 4 MB), over HTTPS and only from the hosts each source
serves images from (redirects are checked hop by hop), then re-encoded as JPEG. Pictures are
fingerprinted with SHA-256 (same file) and a 64-bit difference hash (same picture re-encoded or
resized) to flag duplicates and pictures that are your input photo itself. Search responses and
downloads are cached (7 days, shared; `DELETE /api/references/cache` or **Clear cache** removes them
without touching sessions). You accept or reject each picture and can note what it is (photo,
render, drawing) and how it relates to the object (same object, variant, similar but different).

### Ranking and grouping

Pictures are ranked after every search (and on **Re-rank**, which also uses the pictures you have
accepted). With the optional CLIP model (`.\scripts\download-models.ps1 -Embeddings`, ~155 MB,
MIT; see [models/README.md](models/README.md)) the score mixes: match with the search text,
similarity to your input photo and to accepted pictures, whether it probably shows a side your
photo does not, resolution, licence and source. Without the model only the non-visual signals are
used and the page says so (`ranking.mode = basic`). Every picture shows its score, how much
evidence it rests on (less without embeddings or an input photo) and the reasons. CLIP also
suggests a viewpoint (front, back, side, top, three-quarter, or *uncertain*), a kind (photo,
render, drawing) and a relation to your photo (same object?, variant?, similar but different?):
these are advisory, the relation's confidence is capped at 50 %, and your own fields stay
separate. Pictures whose embeddings are close form *visual groups* (same or closely related
object); the grid can be grouped by visual group or by viewpoint. Embeddings are cached by the
SHA-256 of the exact preview bytes and cleared with the other caches.

### Existing 3D models

**Search Sketchfab** lists models for the query (anonymous, no account; only listings and preview
images are read). Downloading from Sketchfab needs a free account, so it is not done here: download
a model yourself and **Check a model file** (binary GLB or binary STL, up to 64 MB) to have the mesh
read and checked: triangles, separate pieces, closed or open surface, degenerate faces, declared
size (glTF is metres; STL has no unit, so millimetres are assumed and flagged). Each candidate gets
a score with reasons: visual similarity of its preview (or of a shaded render of the uploaded mesh)
to your photo and accepted pictures, availability, whether the licence allows modified versions,
and mesh workability. The model's name is never used. Listing-only candidates are marked
`metadata_only` and get a low confidence.

Limitations: duplicate detection finds copies of the same picture, not other photos of the same
object (visual groups do that, approximately); nearly featureless pictures are compared by exact
file only. Similarity scores and zero-shot suggestions are estimates from a general-purpose model,
not identification: two different kits can look alike and the same kit can look different under
other lighting. The calibration constants (`ReferenceRanker`) were chosen from observed CLIP
cosines, not measured against labelled data. A picture or model with an unknown licence is for
personal reference only; check the source page before using it. Previews that are not PNG, JPEG or
WebP (for example GIF) are listed without a preview. OBJ, glTF with separate files and ASCII STL
are not accepted yet. Reusing an accepted model in place of generation comes in phase 6.

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
* Not yet available: drawings for rods/tubes/curved parts, stock-length nesting for the cut list,
  import of external GLB/OBJ/STL files.

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
* **DXF** (R12, millimetres, 1:1) is offered too: layer `CUT` holds only the closed cutting contours,
  layer `ANNOTATION` the hole labels and a description you can switch off.

### Exports and releases

The **Export** card of a project offers:

* **BOM** (CSV): one row per part with category, quantity, material, every manufacturing value,
  estimated and confirmed dimensions and open issues.
* **Cut list** (CSV): rod and tube cuts grouped by stock (material and section) with summed
  quantities and total length. Values not entered by you come from the model and are marked
  `estimated`. Stock-length nesting is not done yet.
* **STL** of a single part (millimetres), from the drawing card.
* **Draft package** (zip): drawings (PDF, SVG, DXF) of every sheet part, STL of every part, BOM, cut
  list, README and `manifest.json` (every file with its SHA-256, parts, warnings). **Check package**
  shows what it would contain and its warnings first. Drawings in a draft say *Draft*.
* **Releases**: *Create release* builds the same package once, numbers it (Rev 1, 2, …) and stores
  it; drawings inside say *Rev N*. A release always downloads exactly as it was made, whatever you
  edit or undo afterwards. A part with a validation error (for example a tube whose inner diameter
  is not smaller than its outer) blocks packages and releases until it is fixed.

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
no shell execution; no secrets in source control. Reference search only makes anonymous GET
requests to the configured public APIs and their image hosts (HTTPS, redirects checked per hop,
size-capped downloads re-encoded before storage); addresses entered by users are stored as links
and never fetched.

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
* Reference search against the live Wikimedia Commons, Openverse and Internet Archive APIs
  (2026-10-21, query "RX-78-2 gundam", temporary database): all three sources answered, 41 of 41
  previews downloaded, the repeated search was served from the cache.
* Ranking with the real CLIP model on live results (2026-10-22, same query, temporary database):
  Gundam statue and kit photos ranked first, venue photos (entrance, pavilion) last; query cosines
  0.18–0.32. With one of the results as input photo it was recognised as the input itself (cosine
  0.98, moved down) and other RX-78-2 pictures were suggested as variants. Sketchfab search
  returned 24 listings (12 without a licence, shown as unknown); an uploaded unrelated TripoSR mesh
  was checked (closed, 78,012 triangles, 100 × 68.5 × 77.5 mm) and ranked low as visually different.
  Timings on this CPU-only machine: first search 11 s (model load, 41 previews), cached repeat 1.5 s.

Not verified:

* Numerical parity between the ONNX export and the PyTorch model (needs Python, not allowed).
* CUDA execution, Docker images and docker-compose.
* Linux and macOS hosts (code is cross-platform; SkiaSharp Linux native assets are referenced).
* Opening the STL in a specific slicer (the file is validated programmatically: binary layout,
  outward normals, positive volume, millimetre extents).
