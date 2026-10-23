# Image to 3D (TripoSR, .NET)

Upload one photo of a Gunpla part, plastic model component or mechanical accessory and get an
**estimated** 3D mesh you can preview in the browser, scale in millimetres and download as GLB or
STL for 3D printing.

Around the generator the app follows *retrieval first, reuse second, reconstruction third*:

* **Generate** (`#/`): one photo (plus optional photos of other sides with SF3D/SPAR3D) → mesh,
  scaling, GLB/STL downloads, job history.
* **References** (`#/references`): search free sources (Wikimedia Commons, Openverse, Internet
  Archive) for pictures of the object, rank them with an optional local CLIP model, look for
  existing 3D models (Sketchfab listings, your own GLB/STL), then **Get the geometry**: reuse an
  accepted model, generate (with or without extra photos) or build by hand.
* **Photogrammetry** (`#/photogrammetry`): reconstruct a real object from many photos with Meshroom
  (optional, installed separately): photo checks, background run, scale calibration, GLB/STL and
  Meshroom's textured OBJ, opened in the part designer.
* **Parametric** (`#/parametric`): generate a model from exact sizes, with no AI and no photos. The
  spacecraft template is built from PS sheet, and the compound template from boxes, cylinders,
  cones, spheres, tubes, plates and shells. The result opens in the part designer with 1:1
  cutting templates, orthographic views and a BOM. It needs the Python worker in
  `src/parametric-geometry`, which `scripts/setup.ps1` installs when `uv` is present.
* **Part design** (`#/designs`): break a model into editable parts (sheet, rod, tube, stock,
  custom), split them with real capped plane cuts, add stock shapes with exact sizes, relate parts
  (glued, peg in hole, tube over rod, nested, slot) with fit and collision checks, and export 1:1
  drawings (SVG, PDF, DXF), BOM, cut list, STL and numbered release packages.

Everything runs locally; no paid API, account or cloud service is needed.

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
          ASP.NET Core API (:5080) ── SQLite (jobs, part designs, reference searches; EF Core migrations)
              │                     ── local file stores (data/storage, designs, references, reference-cache)
              │                     ── optional CLIP ViT-B/32 ONNX in process (reference ranking)
              │                     ── HTTPS GET to public search APIs (Wikimedia, Openverse, Internet Archive, Sketchfab)
              │  HTTP, loopback (or API key + private network)
              ▼
          Inference service, ASP.NET Core + ONNX Runtime (:5090, 127.0.0.1)
              │  loads models once, single worker queue
              ▼
          TripoSR (ONNX) + U²-Net background removal   (or SF3D / SPAR3D, see Engines)
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
                            pages: Workspace (generate), References, Part design
  backend/ImageTo3D.Api/    Controllers/ Services/ (Jobs, PartDesign, References, Embeddings, Storage)
                            Models/ DTOs/ Data/ (AppDbContext + Migrations) Configuration/
  inference/ImageTo3D.Inference/  Api/ Jobs/ Models/ (ONNX) Pipeline/ Cli/
  inference-sf3d/           optional Python service for Stable Fast 3D / SPAR3D
  shared/ImageTo3D.Core/    Geometry (marching cubes, GLB, STL, scaling, topology, plane split),
                            PartDesign (analysis, outlines, stock shapes), Drawing (SVG, PDF, DXF),
                            Reconstruction (route planner), Embeddings (CLIP tokenizer/preprocessing),
                            Imaging, Contracts
tests/
  shared/  inference/  backend/      xUnit
  (frontend tests live next to the code in src/frontend/src/**/*.test.ts so Vitest resolves node_modules)
models/   ONNX weights (downloaded; models/clip optional)   scripts/   setup, model download, dev, test
docs/api.md                              docker-compose.yml, Dockerfiles
```

## Requirements

| | Minimum | Notes |
|---|---|---|
| OS | Windows 10/11 x64, Linux x64 | macOS untested |
| .NET SDK | 10.0 | `winget install Microsoft.DotNet.SDK.10` |
| Node.js | 20.19+ / 22.12+ | for Vite 8 |
| RAM | 16 GB | inference process peak working set measured at 5.75 GB (fp32 encoder ≈ 3.3 GB + activations) |
| Disk | 5 GB | 3.5 GB of models, plus ~155 MB for the optional CLIP model |
| GPU | optional | CPU works; see [GPU](#gpu-cuda) |
| Python | **not used** by the default TripoSR setup | the parametric geometry worker (`src/parametric-geometry`, Python 3.11 via `uv`, set up by `setup.ps1`) and the optional SF3D/SPAR3D engine (`src/inference-sf3d`) need it. Without it, only those features are unavailable |

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

On a machine with an NVIDIA GPU, add `-Gpu` to both commands so TripoSR runs on the GPU (needs
CUDA 13 and cuDNN 9; see [GPU (CUDA)](#gpu-cuda)).

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
| `tests/shared` | 223 | photogrammetry input (OBJ reading: quads, negative indices, colours, broken files with the line, limits; photo inspection from a reduced decode: size, sharpness falling with blur, EXIF segment, format and size refusals; photo-set checks: count blocker, few photos, low resolution, blur absolute and relative, near duplicates, featureless hashes, camera metadata, mixed sizes), multi-view route with and without Meshroom, assembly collisions (triangle pairs crossing, touching, coplanar, parallel within tolerance, shallow penetration; BVH pair search against brute force; rod through plate with intersection length, plate on block touching, separated, block inside block both ways, rod in a tube bore, oversize rod, open meshes, test budget, point-in-solid ray parity), fit checks (tube over rod, peg in hole too tight/too loose, tube bore as hole, slot, unknown opening, estimates marked, category mismatch, glued/non-round nested, unknown kind), reconstruction planner (preference order, reuse blocks generation, listing-only models, closed-mesh preference, licence/unit warnings, hybrid engine and same-object requirements, cap on extra photos, unreachable engine, manual fallback), stock shapes (plate/block/rod/tube closed and outward, exact sizes, axes, polygon volumes, tube bore, usable plate outline, input validation), CLIP tokenizer (BPE merges, reference token IDs, truncation), CLIP preprocessing (normalisation, alpha, centre crop), bicubic resampling, cosine/softmax/clustering, perceptual hash (resize/JPEG tolerance, distinct pictures, transparency) and thumbnails, DXF R12 export (header, mm units, CUT layer closed contours at 1:1, annotation layer, text encoding), drawing revision label, cutting outlines (rotated plate, rectangular and round holes, L shape, simplification tolerance, tapered edges, non-flat and open parts), 1:1 drawings (A4/A3 page choice, SVG mm scale and layers, escaping, PDF page size, millimetre scale and no embedded fonts, too-large parts, drawing checks),  plane splitting with capped cuts (cube, oblique, plate with hole both ways, cylinder, tube, cut through a vertex ring, disconnected boxes, open mesh, marching-cubes torus in three directions), polygon-with-holes triangulation, welding, connected components, boundary/non-manifold detection, oriented bounds, part classification (sheet, rod, tube, custom, needs review), GLB node reading, marching cubes watertightness/orientation/volume, GLB round-trip + independent SharpGLTF validation, STL binary format, normals, degenerate faces, GLB→STL conversion, scaling maths, image signature/decoding/limits, resampler |
| `tests/inference` | 23 | preprocessing ports, job state machine, internal API validation, artifact whitelist, API key, **real TripoSR smoke test** (chair → valid coloured mesh, Y-up proportions, density threshold sanity) and mid-inference cancellation |
| `tests/backend` | 261 (+1 real-Meshroom test, skipped unless configured) | photogrammetry with a scripted Meshroom (missing, ready with installed allowed pipelines, CUDA default, incompatible or broken CLI, wrong folder; photos stored byte for byte with warnings, thumbnails, removal, blocker, refused batches; a full run with the exact command line, input files, mesh checks, uncalibrated 100 mm default, GLB/STL/zip/log downloads and work-folder cleanup; calibration and part design; failure, timeout, no output, broken OBJ, mesh from the cache; progress from cache status files, cancellation and deletion while running; pipeline whitelist; isolation; restart recovery; the reference-search multi-view route end to end), parametric projects through the API (one part per component with exact specs, generated files kept and downloadable, refusals with code and field, missing worker as 503 and in capabilities, isolation, designer tools on parametric parts, job engine and origin), the worker process contract with a scripted runner (arguments, stdin request, refusals, crash, timeout, garbage, oversized request) and, when the worker venv exists, the real process runner (stdin, capped stderr, timeout kill, cancellation) and a real spacecraft generation, additive parametric migration, assembly through the API (relations with fit checks against the project limits, edit/undo/redo/remove, self/duplicate/inactive refusals, part deletion removes relations and undo restores them, discarded redo branch, collisions with intentional-overlap exceptions, glued contact and gaps, containment and nesting, unmoved duplicates, quality report of a generated model, package metadata and release comparison, owner isolation), reconstruction decision through the API (route list, reuse of an accepted GLB/STL without inference and with provenance, generation refused while a model is accepted, hybrid extra photos sent to the engine, engine without extra photos, single-image failure and retry, deleted running job, manual geometry, owner isolation, non-reusable models), stock shapes (empty projects, exact specs, drawing and cut list, undo/redo, invalid input, isolation), extra photos stored and sent with their views, migration of existing projects to source kinds, real CLIP model (caption matching on chair/robot, resize/JPEG stability, zero-shot, missing files, disabled), ranking (signals, re-normalisation without embeddings, duplicates, input photo, groups, new viewpoint, accepted evidence, capped relation), model evaluation (listing only, downloadability, licence, face count, geometry, scale), ranking through the API with a fake embedder (fallback, failure isolation, embedding cache), Sketchfab search (licence mapping, age-restricted skipped, preview host rules, cache, failures, timeout, disabled), GLB/STL upload checks (closed/open, unitless STL, ASCII STL/OBJ/broken/oversized refused, duplicates, file download, review, removal, isolation), reference search against a fake web (attribution and licence normalisation, previews and their size/host/redirect limits, cache and clearing, per-source failures with bounded retries, timeouts, rate limiting, empty and malformed answers, disabled sources, exact/near duplicates and input match, review decisions, manual pictures, sessions from jobs, owner isolation, no credentials sent, cancellation), exports (DXF and part STL endpoints, BOM with formula neutralisation, rod cut list grouping and estimate marking, draft package files and SHA-256 manifest, warnings, numbered immutable releases, export blocked by errors, owner isolation, release deletion with the project), drawing report/SVG/PDF endpoints (sheet suggestion, mm scale, headers, specified thickness, escaping, refusals, isolation),  migration baseline of a pre-migration database (and refusal of one with missing columns, WAL on new databases), part design: creation and source copy, survival of job deletion and retention, owner isolation, part editing and validation, undo/redo and redo-branch cleanup, split preview/apply/undo with real cut geometry, open-mesh refusal, upload validation, job transitions, inference failure/unavailable/timeout/lost, downloads (real GLB/STL, units, orientation), scale endpoint + DTO validation, ownership isolation, idempotency, active-job limit, delete, cleanup, health without secrets, correlation IDs, path-traversal protection, HTTP client error mapping |
| `src/frontend` | 101 | photogrammetry state (status labels, polling, progress without percentages, findings per photo, upload batches, JPEG/PNG filter, calibration input), multi-view route actions, parametric form (defaults, variants, ranges, sheet thicknesses, component JSON, server field mapping), geometry origin labels, parametric route, relation form parsing, fit summaries, collision order, exploded-view offsets, reconstruction states and route actions, stock-shape form parsing, job route, ranking order, visual/viewpoint grouping, suggestion and confidence labels, model API calls, reference filters, licence tones, source outcomes, reference API calls and routes, drawing URLs, part tree, selection/visibility/isolation state, cutting plane, part form parsing, hash routes, scale maths and validation, API client error mapping, idempotency header, upload validation, stage/progress text |
| `src/parametric-geometry` | 117 | pytest: primitives and their exact sizes and volumes, PS thickness whitelist, parameter validation, spacecraft dimensions and conflicts, sheet volume = template area x thickness, BOM grouping, determinism, GLB/STL/OBJ/SVG/PDF/DXF read back, CLI exit codes and refusals |

Backend tests replace the inference HTTP client with an in-memory fake, so they need no model, and
the reference-search HTTP handler with a fake web and CLIP with a deterministic fake, so they never
contact the internet; the real-model tests run only when the model files are present.

## Configuration

All settings live in each project's `appsettings.json` and can be overridden with environment
variables (`Section__Key`). Key settings:

| Setting | Default | |
|---|---|---|
| `Inference:ModelDirectory` | `../../../models` | relative to the inference project |
| `Inference:ExecutionProvider` | `Cpu` | `Cuda` needs the CUDA build (`dev.ps1 -Gpu` sets both) |
| `Inference:CpuLimit` | 0.8 | fraction of logical CPUs ONNX Runtime uses when `IntraOpThreads` is 0 (SF3D: `SF3D_CPU_LIMIT`) |
| `Inference:MarchingCubesResolution` | 256 | 32–320; lower = faster, coarser |
| `Inference:DensityThreshold` | 25 | official TripoSR iso-level |
| `Inference:KeepLargestComponentOnly` | false | drop floating fragments |
| `Inference:JobTimeout` | 30 min | |
| `Inference:ApiKey` | empty | required when not bound to loopback |
| `Upload:MaxBytes` | 20 MB | also min/max dimension, max pixels |
| `Upload:UploadsPerMinute` | 10 | per browser |
| `Upload:MaxActiveJobsPerOwner` | 3 | also applies to generations started from a reference search |
| `Upload:MaxExtraImages` | 5 | extra photos per job (also the cap for the hybrid route) |
| `InferenceService:BaseUrl` | `http://127.0.0.1:5090` | |
| `InferenceService:GenerationTimeout` | 40 min | |
| `Storage:RootPath` / `DatabasePath` | `data/` at repo root | outside any web root |
| `Storage:RetentionPeriod` | 7 days | finished jobs and files are deleted after this |
| `ModelOutput:DefaultLongestSideMm` | 100 | initial export size |
| `Storage:DesignRootPath` | `data/designs` | part-design projects; never touched by job retention |
| `PartDesign:MaxSourceTriangles` | 2,000,000 | larger meshes are refused (analysis runs in the request) |
| `PartDesign:MaxAnalysedParts` | 64 | further pieces are grouped as "fragments" |
| `PartDesign:MaxPartsPerProject` / `MaxProjectsPerOwner` | 500 / 100 | parts count history (split, removed) too |
| `PartDesign:MaxReleasesPerProject` | 50 | each release keeps its own zip |
| `PartDesign:MaxRelationsPerProject` | 1000 | including removed ones kept for undo |
| `PartDesign:ContactToleranceMm` | 0.05 | surfaces closer than this touch; shallower penetrations are not collisions |
| `PartDesign:MaxCollisionTriangleTests` | 50000000 | per assembly check; beyond it the check is marked incomplete |
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
| `ModelSearch:ResultsPerSearch` / `MaxModelsPerSession` | 24 / 100 | Sketchfab listings per search, model candidates per session |
| `ModelSearch:MaxUploadBytes` / `MaxTriangles` | 64 MB / 2,000,000 | uploaded GLB/STL limits |

The SQLite schema is managed by EF Core migrations (`src/backend/ImageTo3D.Api/Data/Migrations`),
applied on start. A database created by an older version (before migrations, by `EnsureCreated`)
is detected and baselined in place: its existing columns are checked and the first migration is
recorded as applied, so existing jobs are kept. Add a migration with
`dotnet ef migrations add <Name> --output-dir Data/Migrations` from the API project folder.

**Back up `data/imageto3d.db` before starting a new version for the first time.** Migrations never
delete data, but `AddReconstructionDecision` rebuilds the `PartDesignProjects` table (SQLite cannot
make a column nullable in place) and EF runs part of that rebuild outside a transaction, so an
interrupted start could leave it half applied. Existing projects become `source.kind =
generation_job`.

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
are not accepted yet.

### Getting the geometry (reconstruction decision)

**Get the geometry**, at the bottom of a reference search, lists every way to obtain geometry in
order of preference (retrieval first, reuse second, generation third), says why each one is or is
not possible, and recommends the first possible one:

1. **Reuse the accepted model**: an accepted, uploaded GLB/STL opens in the part designer as is
   (licence and attribution stay with the project and its export packages). No AI generation runs,
   and while such a model is accepted the generation routes are refused.
2. **Multi-view reconstruction**: always *not supported*. None of the installed engines builds
   geometry from several photos.
3. **Generate with extra photos** (hybrid): the input photo plus the accepted pictures you marked as
   *same object* go to the existing engine as extra photos. Only Stable Fast 3D and SPAR3D use them,
   and only to colour the hidden sides; the shape still comes from the one input photo.
4. **Generate from the input photo**: the existing single-image pipeline, unchanged.
5. **Build by hand from stock shapes**: an empty part design (also **New empty project** on the
   part-design list). **Add a stock shape** creates plates, blocks, rods and tubes with exact
   millimetre sizes; this works in any project, and undo removes them again.

A started generation is a normal job (open it with **Open the generation**, `#/jobs/<id>`); the
search follows its state (`completed`, `failed` with the reason, `cancelled`) and a failed route can
be started again. Limitations: a reused STL is assumed to be millimetres; reused models are split
into parts like generated ones (node, then connected piece) and their textures are not carried over;
suggested viewpoints are not passed to the engine (it detects the side itself).

## Photogrammetry (Meshroom)

**Photogrammetry** (`#/photogrammetry`) reconstructs a real object from many overlapping photos
using [Meshroom](https://github.com/alicevision/meshroom) (AliceVision, MPL-2.0), which runs locally
and is installed separately. Unlike the AI generator it guesses nothing: surfaces the photos show are
reconstructed, and surfaces they do not show are missing.

1. Install Meshroom 2025.1 or newer and point the API at it:
   `$env:Photogrammetry__MeshroomPath = 'C:\Tools\Meshroom-2025.1.0'` (or `Photogrammetry:MeshroomPath`
   in `appsettings.json`), then start the API. **Photogrammetry** says whether it is ready, which
   pipelines are installed, and whether a CUDA GPU was found.
2. Create a photo set and add the camera's original JPEG or PNG files. 20 to 200 photos all around
   the object work best. The checks warn about too few photos, low resolution, blur, near-duplicates
   and missing EXIF camera data. Only fewer than 3 photos blocks the start.
3. Start the reconstruction. One runs at a time in the background; the page shows the Meshroom step
   that is running and how many are done (Meshroom gives no total, so there is no percentage). You
   can cancel it, and the log is kept.
4. Calibrate the scale. Meshroom does not know the real size, so the longest side starts at 100 mm.
   Measure one side of the real object and enter it. Then download GLB (metres), STL (mm, Z-up) or
   Meshroom's own textured OBJ, MTL and textures (zip), or **Open in part designer**.

Pipelines offered (only those the installation has):

* `photogrammetry`: the full pipeline. Its depth maps need an NVIDIA GPU with CUDA compute capability
  5.0 or higher, so it is the default only when `nvidia-smi` finds one.
* `photogrammetryDraft`: runs on the CPU, coarser. It is the default without a GPU.
* `photogrammetryObject` and `photogrammetryObjectTurntable`: for objects on a turntable.

The API starts `meshroom_batch --input --pipeline --output --cache` with generated paths only, in a
per-job folder under `data/photogrammetry-work` that is deleted afterwards (`KeepWorkFiles` keeps
it). It kills the process tree on cancel, delete or `Photogrammetry:Timeout` (6 h). It looks for the
mesh in the output folder, then in the cache, rather than assuming a layout. A restart fails a
running job as `interrupted`.

A reference search can also start it: the *Multi-view reconstruction* route becomes available with
at least 8 pictures of the same object. Those pictures are 640 px previews, so your own photos work
much better.

Limitations:

* Shiny, transparent, very dark or plain single-colour surfaces reconstruct poorly.
* The mesh is often open, so it is not print-ready without repair.
* The size is only as good as the one measured dimension.
* GLB, STL and part designs carry geometry only; the texture is in the zip.
* No real Meshroom run was possible on the development machine: no GPU, and Meshroom was not
  installed. The integration is tested with a scripted Meshroom and a fake `meshroom_batch.cmd`.
  `MeshroomRealTests` runs against a real installation when `MESHROOM_TEST_PATH` and
  `MESHROOM_TEST_PHOTOS` are set.

## Parametric models

**Parametric** (`#/parametric`), or **New parametric model** on the part-design list, generates a
model from exact sizes. Pick a template, enter the sizes and the PS sheet thickness (0.3, 0.5, 0.8,
1, 1.5, 2 or 3 mm), then click **Generate**. The model is built in a few seconds and opens in the
part designer as a project marked **Parametric**, with scale *exact*. Each component becomes one
part, with its category, sheet thickness and material already filled in. The **Parametric model**
card has the generated files: 1:1 cutting templates (PDF, plus a zip with SVG and DXF per part),
front, side and top views with a section, a BOM with identical parts grouped, and STL and OBJ.
These files show the model as it was generated; edits made in the designer afterwards are not in
them. **Create a variant** opens the form with the same parameters.

Templates:

* **Spacecraft**: a rectangular upper hull built from sheets, a semicircular lower hull rolled
  from sheet with bulkheads and ribs, an engine housing with a mount and a flared exhaust, armor
  panels on both sides, a top frame with recesses, a vent, hex bolt heads and antennas. Rolled
  parts get developed (flat) templates and a *requires forming* note.
* **Assembly**: a list of boxes, chamfered boxes, cylinders, cones, frustums, spheres,
  hemispheres, tubes, rods, extrusions, PS plates and rolled shells, each placed with translate,
  rotate and scale.

The geometry comes from a Python worker (`src/parametric-geometry`, see its
[README](src/parametric-geometry/README.md)), which the API starts as a short-lived process for
each request. The request goes over stdin and the files go to a temporary folder that is deleted
afterwards. There is no port and no shell, and the process is killed after `Parametric:Timeout`.
`GET /api/capabilities` reports whether it is ready. When the worker's venv is missing,
parametric generation answers 503 and says how to set it up; everything else keeps working.
Settings: `Parametric:Enabled`, `WorkerDirectory`, `PythonPath` (default: the worker's `.venv`),
`WorkRoot`, `Timeout` (2 min), `MaxConcurrentRuns` (2).

Every model also shows where its geometry comes from: **AI-predicted** (a generation job, with its
engine), **Parametric**, **Existing model** (a reused file) or **Built by hand**.

## Part design

A project starts from a generated model (**Open in part designer** next to a job), from an existing
model reused through a reference search (its licence and attribution stay with the project), or
empty (**New empty project**, or *Build by hand* in a reference search). The source is copied into
the project and broken into parts (one per glTF node, then per connected piece; tiny floating
pieces are grouped). Each part
gets a suggested fabrication category (sheet, rod, tube, custom 3D, or *needs review*), estimated
dimensions from an oriented bounding box, and geometry checks (closed, manifold, degenerate faces).
You can rename, recategorise, enter caliper measurements and manufacturing properties (material,
thickness, diameters, cut length and angle, fit allowance, stock part details), duplicate, delete,
and **split a part with a plane**: the mesh is really cut, both halves are capped (including cuts
with holes, such as through a tube or a frame) and checked to be closed solids. **Add a stock
shape** adds a plate, block, rod or tube with exact millimetre sizes and position (closed solid,
category and specification filled in: a plate is a sheet with its thickness, rods and tubes get
their diameters and cut length). Every edit can be undone and redone; projects persist and reopen
at `#/designs/<id>`.

Limitations:

* Dimensions measured on a model are estimates: they come from the AI model at the scale you set,
  or from a reused file's declared units, not from the part. Only stock shapes have the sizes you
  entered. Category suggestions are simple shape rules, always pending your review.
* Planes are axis-aligned in the UI (the API accepts any orientation). Each half stays one part
  even if it has several separate pieces.
* Parts keep geometry and vertex colours only; SF3D/SPAR3D textures are not carried into parts.
* Splitting needs a closed surface where the plane crosses it. On an open mesh the cut is not
  capped; the split is refused unless you explicitly keep the open halves, which are then flagged.
* Round stock shapes are 48-sided polygons whose corners lie on the nominal circle.
* Not yet available: drawings for rods/tubes/curved parts, stock-length nesting for the cut list,
  OBJ import, moving or rotating parts in the designer (parts stay where the model or the stock
  shape put them).
  External binary GLB/STL files enter through a reference search (see *Getting the geometry*).

### Assembly: relations, collisions, quality

**Relations** say how two parts are joined: *glued*, *peg in hole* (part A is the peg), *tube over
rod* (A is the tube), *nested* (B sits inside A) or *slot* (B is the sheet). For pegs, tubes and
slots the clearance of the fit (opening minus inserted size, from your sizes or else the model's
estimates) is checked against the project's fit-allowance range and reported as OK, too tight or
too loose. Relations go through undo/redo; deleting or splitting a part removes its relations in
the same step.

**Check assembly** tests every pair of parts that are close, triangle against triangle: parts
whose surfaces cross (*intersect*), a closed part wholly inside another, the same geometry twice in
the same place (an unmoved duplicate), glued parts that do not touch. Surfaces within 0.05 mm
count as touching. An overlap is reported as info instead of a warning when a relation allows it
(*overlap allowed*, or *nested*). The same card lists the geometry quality of each part (closed,
open and non-manifold edges) and how many are closed solids ready for STL. The **Exploded** slider
in the viewer moves parts apart from the centre for viewing only.

Packages and releases include `metadata/assembly.json` and `metadata/quality.json`, the manifest
lists relation, fit and collision warnings, and **Compare releases** shows which parts were added,
removed or changed (name, quantity, geometry, each specification field) between two releases.

Limits: the check is exact for the triangles, but a generated model's pieces often overlap or float
by design of the generator, so expect warnings there; containment is only tested for closed parts;
large projects stop after `PartDesign:MaxCollisionTriangleTests` and say the check is incomplete.
Fits use diameters and thicknesses as entered or estimated; they do not measure holes in the mesh.

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
* Dimensions are still those of the model at its estimated scale unless you confirmed them (a
  plate added as a stock shape has exactly the sizes you entered).
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

TripoSR runs on an NVIDIA GPU through ONNX Runtime's CUDA provider. ONNX Runtime 1.31
(`Microsoft.ML.OnnxRuntime.Gpu`) is built with **CUDA 13** and needs **cuDNN 9**; the package does
not include them. Not verified on real hardware: the development machine has no GPU (the GPU build
compiles, and without CUDA it reports `unavailable` instead of falling back to the CPU).

Install once on the GPU machine:

1. NVIDIA driver 580 or newer: <https://www.nvidia.com/Download/index.aspx>
2. CUDA Toolkit 13 (Windows, x86_64): <https://developer.nvidia.com/cuda-downloads>
3. cuDNN 9 for CUDA 13 (Windows, x86_64): <https://developer.nvidia.com/cudnn-downloads>
4. .NET 10 SDK, Node.js LTS and PowerShell 7 (`winget install Microsoft.DotNet.SDK.10`,
   `winget install OpenJS.NodeJS.LTS`, `winget install Microsoft.PowerShell`)

Then, in a new terminal (VS Code's terminal works):

```powershell
.\scripts\setup.ps1 -Gpu   # checks GPU, driver, CUDA 13 and cuDNN 9; restores the GPU build; downloads models
.\scripts\dev.ps1 -Gpu     # starts everything; prints "TripoSR ready on GPU (CUDA)" when loaded
```

`-Gpu` refuses to start when the GPU, driver, CUDA or cuDNN is missing and says what to install;
it never falls back to the CPU silently. CUDA or cuDNN folders found outside PATH (under `CUDA_PATH`
or `C:\Program Files\NVIDIA\CUDNN`) are added for the inference service. Manual equivalent:

```powershell
$env:Inference__ExecutionProvider = 'Cuda'
dotnet run --project src\inference\ImageTo3D.Inference -c Release -p:OrtFlavor=Cuda
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
* Reconstruction decision against the real API on a copy of the development database
  (2026-10-22): the migration kept all jobs and searches; with the inference service down the plan
  recommended single-image generation (with a warning) and refused multi-view; reusing a real
  TripoSR GLB (23,744 triangles) created a part design in about 5 s without starting a job;
  manual geometry plus a 5/3 × 30 mm tube gave a closed, valid part and a package manifest.

* Assembly checks against the real API on a copy of the development database (2026-10-23): a
  reused TripoSR mesh (78,010 triangles, closed) with a 3 mm rod through it and a 5/3.2 mm tube on
  the rod: the check took 0.5 s (15,948 triangle tests), found the rod–mesh intersection, reported
  it as info once related as a peg with the overlap allowed, computed both fits (0.1 and 0.2 mm
  clearance, OK), and a release (8 files, 3.2 MB) plus a second one compared as one changed part.
  The development database was then backed up and migrated (7 jobs, 2 searches kept).

* Parametric models through the real API and the real Python worker, on a copy of the development
  database (2026-10-23). The `AddParametricDesigns` migration kept all 8 jobs. A 260 mm spacecraft
  with 1.5 mm sheet was created in 4.3 s, and the templates PDF downloaded. A height of 30 mm was
  refused with `422 geometry_conflict`, field `parameters.height`. The temporary work folder was
  empty afterwards, and `/api/capabilities` reported parametric `ready`. The `#/parametric` form
  was checked in a browser (headless Edge).

Not verified:

* A parametric project opened in the part designer in a browser, which includes the Parametric
  card and the badges. It is covered by type checking, unit tests and the API tests.
* Parametric generation in Docker: the images have no Python, so it reports `missing` there.
* A real Meshroom run. Meshroom is not installed on the development machine and it has no GPU. The
  live check (2026-10-23, real API on a copy of the development database, `AddPhotogrammetry` applied)
  used a `meshroom_batch.cmd` stand-in, which exercised the real process start, arguments and
  cleanup. Detection reported 2025.1.0 with the draft default. Three sample photos gave the expected
  warnings. The run found the mesh, produced a closed 100 x 50 x 25 mm result and an STL, and left
  the work folder empty. Calibration to X = 64 mm and a part design both worked.
* The photogrammetry job page in a browser; it is covered by type checking, unit tests and API tests.
  The photo-set list page was checked in headless Edge.

* The Phase 7 screens (relations, assembly check, exploded view, release comparison) in a
  browser; they are covered by type checking, unit tests and the API tests only.
* The Phase 6 screens (*Get the geometry*, *Add a stock shape*) in a browser; they are covered by
  type checking, unit tests and the API tests only.
* A hybrid generation with the real SF3D/SPAR3D engine (tested with the fake inference client).

* Numerical parity between the ONNX export and the PyTorch model (needs Python, not allowed).
* CUDA execution, Docker images and docker-compose.
* Linux and macOS hosts (code is cross-platform; SkiaSharp Linux native assets are referenced).
* Opening the STL in a specific slicer (the file is validated programmatically: binary layout,
  outward normals, positive volume, millimetre extents).
