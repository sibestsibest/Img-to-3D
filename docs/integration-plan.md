# Integration plan: local AI, Meshroom and parametric geometry

Status: Phase 0 (audit and plan). No code has been changed. Each later phase starts only after
approval, and decisions marked **D** below are proposals until the owner chooses.

Read with [architecture.md](architecture.md) (current and target structure) and
[environment-setup.md](environment-setup.md) (tools and versions).

## 1. Summary

The repository already has most of the platform the master prompt asks for: an ASP.NET Core API
with SQLite and background jobs, a React + Three.js viewer, single-image AI (TripoSR / SF3D /
SPAR3D), mesh validation pieces, 1:1 sheet drawings (SVG/PDF/DXF), BOM, cut lists and release
packages, reference and 3D-model search with licence tracking, and a reconstruction route planner
that already reserves a `multi_view_reconstruction` route (currently hard-coded as unsupported).

The plan therefore **extends** rather than builds:

| Master-prompt feature | Already there | To add |
|---|---|---|
| Parametric geometry (Phase 1) | `PrimitiveBuilder` (plate/box/rod/tube), GLB/STL writers, validators, sheet drawings, BOM, packages | done in Phase 1 as the Python worker `src/parametric-geometry` (D1-B): primitives with transforms, sheet/rolled components, PS thickness list, spacecraft template, GLB/STL/OBJ, templates, views, BOM |
| Shared backend/frontend (Phase 2) | jobs, health, downloads, viewer, part-design projects | capabilities endpoint, geometry origin + engine on results, parametric page |
| Meshroom (Phase 3) | route `multi_view_reconstruction` in the planner, extra-photo upload, job patterns | process runner, photogrammetry job + worker, OBJ import, scale calibration |
| Synthetic dataset (Phase 4) | parametric engine (after Phase 1) | Blender scripts, manifest, splits, integrity checks (Python, offline) |
| Asset ingestion (Phase 5) | licence normalisation, Sketchfab listing search, GLB/STL upload with mesh checks | offline ingestion tool with provenance manifest; optional source adapters |
| Single-image AI baseline (Phase 6) | ONNX Runtime in the API (CLIP), TripoSR pipeline | parameter-regression model (train offline, run as ONNX), predicted → parametric mesh |
| Advanced AI (Phase 7) | TripoSR, SF3D, SPAR3D engines as a comparison baseline | experiments and measurements only |
| Mesh validation (Phase 8) | `MeshValidator`, `MeshTopology`, `PartAnalyzer`, drawings refusal of non-flat parts | one shared `MeshInspection` report, optional logged repairs, forming/bending flags |
| Unified UI (Phase 9) | three pages, hash routing, viewer | Photogrammetry and Parametric workflows, origin badges |
| Packaging (Phase 10) | setup/dev/test scripts, README, tests | env check script, pytest in `test.ps1`, licence inventory, docs |

## 2. Mapping of existing modules to features

| Existing module | Used by (phase) | How |
|---|---|---|
| `Core/Geometry/Mesh`, `MeshOperations`, `GlbWriter`, `StlWriter` | 2, 3, 6, 8 | read and convert every engine's output (`GlbReader.ReadNodes` reads the Python worker's GLB, verified in Phase 1) |
| `Core/Geometry/MeshValidator`, `MeshTopology`, `PartDesign/PartAnalyzer` | 2, 3, 8 | combined into `MeshInspection` (Phase 8); the Python worker has its own equivalent report in its response |
| `Core/PartDesign/PrimitiveBuilder` | 1 | unchanged (the Python worker has its own shapes, decision D1-B) |
| `Core/Drawing/*` (`SheetDrawingBuilder`, `SvgDrawingWriter`, `PdfDrawingWriter`, `DxfWriter`) | 8 | unchanged; parametric templates and views come from the worker. Parts of a parametric project keep working with the existing sheet drawings |
| `Api/Services/PartDesign/*` (projects, parts, undo, exports, releases) | 1, 2, 3, 6, 9 | a parametric model **is** a part-design project with `SourceKind = parametric`; a photogrammetry mesh can be opened in the part designer like a generated one |
| `Api/Services/GenerationWorker`, `JobQueue`, `JobService` | 2, 3 | pattern copied for the photogrammetry worker (own queue); AI jobs untouched |
| `Api/Services/Storage/IArtifactStore` | 3 | photogrammetry store instance under its own root; Meshroom's working folder lives outside the store |
| `Core/Reconstruction/ReconstructionPlanner`, `Api/Services/References/ReconstructionService` | 3 | `multi_view_reconstruction` becomes available when Meshroom is installed and the session has enough same-object photos |
| `Api/Services/Embeddings/ClipEmbedder` (ONNX in process, optional) | 6 | pattern for the optional parameter-regression model |
| `Api/Services/References/ModelEvaluator`, licence normalisation, `ModelCandidate` | 5 | reused for licence/provenance fields and mesh checks of ingested assets |
| `Api/Controllers/HealthController` | 2 | unchanged; new `GET /api/capabilities` beside it |
| `frontend/components/ModelViewer.tsx`, `partdesign/PartViewport.tsx` | 1, 2, 3, 9 | reused; gain an orthographic view toggle (Phase 9) and origin badge |
| `frontend/hooks/useJob.ts` | 3 | generalised or copied for photogrammetry job polling |
| `src/inference-sf3d` uv/pinned-requirements setup | 4, 5, 6 | pattern for the new offline Python project; not shared (different dependency set) |

## 3. Decisions to make (proposals)

| # | Decision | Options | Recommendation |
|---|---|---|---|
| D1 | Language of the parametric engine | A. C# in `ImageTo3D.Core.Parametric`. B. Python (trimesh) worker. | Recommended A; **owner chose B (2026-10-23)**: `src/parametric-geometry`, a one-shot worker process with a JSON contract (see Phase 1) |
| D2 | Where photogrammetry jobs live | A. New `PhotogrammetryJob` table, own queue and worker. B. Add `JobType`/`Engine` columns to `GenerationJob`, make image fields nullable (SQLite table rebuild) and route by type inside `GenerationWorker`. C. Generic `ProcessingJob` table that later replaces `GenerationJob`. | **A**: additive migration, AI generation is never blocked by a multi-hour run, no change to existing job contracts. C is a rewrite of working code |
| D3 | What a parametric model is | A. Part-design project, one part per component (hull plates, panels, engine). B. Standalone parametric job/result. | **A**: drawings, BOM, cut list, packages, releases, undo, assembly checks come for free; parameters stored on the project for regeneration |
| D4 | Python integration | A. Offline CLI tools only; runtime AI as ONNX in .NET. B. Local Python worker process called by the API. C. Python HTTP service. | Follows from D1-B: the parametric engine is a **local worker process (B)**, so the API needs Python at runtime for parametric generation. Runtime AI (Phase 6) can still be ONNX in .NET; to be confirmed at Phase 6 |
| D5 | OBJ export | A. Add OBJ to parametric and photogrammetry downloads only. B. Also `api/models/{id}/download?format=obj` (changes a test that asserts 400). | **A** in Phase 1; B is a behaviour change, ask separately |
| D6 | Route prefix | A. Keep `/api/...` style. B. Introduce `/api/v1`. | **A**: one style in one API |
| D7 | Hull geometry for scratch-building | A. Build every hull face from sheets of the chosen thickness (true PS construction, inner faces offset). B. Solid hull plus separate sheet "skins". | **A, chosen**. The rectangular upper hull is flat sheets. The semicircular lower hull is a rolled sheet flagged `requires_forming`; as implemented it also gets a developed (flat) pattern. Bulkheads and ribs are flat sheet parts |

Other Phase 1 choices made by the owner: full export scope (GLB, STL, OBJ, BOM, 1:1 SVG/PDF/DXF
templates, orthographic views with a section); verification by automated tests plus an
independent check of the files with the application's own C# readers; no extra CLI project.

## 4. Phase plans

Conventions for every phase: only the files listed are expected to change (more are reported before
being touched); tests are run with `.\scripts\test.ps1` plus the phase's own command; nothing is
committed unless asked.

Test baseline measured on 2026-10-23 (CPU-only VM, `-SkipRealModel` / real models absent):
Core 201 passed; Inference 21 passed; Api 211 total: one run had 1 failure, the rerun had 208
passed, 3 skipped (real CLIP tests) and 0 failed (a timing-dependent test,
`ReconstructionTests.Hybrid_SendsAcceptedSameObjectPicturesAsExtraPhotos`, is known to be flaky;
the failing test of the first run was not captured); frontend 84 passed. Python SF3D tests are not
part of `test.ps1` and were not run. Note: `test.ps1` fails with a file lock if the API is running
from the same `bin/` folder; stop it or build to another output path.

### Phase 1: Parametric Geometry Engine MVP (done 2026-10-23)

Implemented as the Python worker `src/parametric-geometry` (decision D1-B); see its
[README](../src/parametric-geometry/README.md) for the contract, files and limitations.

* One-shot process: `python -m parametric generate --request <json|-> --output <new dir>` writes
  the files and `response.json`, prints the response, exits 0 / 2 (refused, coded error with the
  field) / 1 (internal). `python -m parametric describe` returns templates, parameters with
  defaults and ranges, and the PS thicknesses, for the Phase 2 form. Request schema version 1;
  unknown fields and parameters are refused; requests are capped at 1 MB, models at 500
  components and 2,000,000 triangles. The output directory must be new or empty, and only fixed
  file names are written inside it.
* Geometry: box, chamfered box, cylinder, cone, frustum, sphere, hemisphere, tube, rod, extrusion
  with holes, PS plate with holes and rolled PS shell, each with translate/rotate/scale
  (`assembly` template); flat sheet parts built from their profile; rolled parts (half-shell,
  tube, cone) with developed patterns; hex bolt heads; `spacecraft` template (rectangular upper
  hull, semicircular lower hull with bulkheads and ribs, engine housing, mount, exhaust, armor
  panels, recessed top frame, vent, bolts, antennas).
* PS thickness whitelist 0.3/0.5/0.8/1/1.5/2/3 mm (`unsupported_thickness` otherwise).
* Exports: GLB (m, node per component), STL (mm, Z-up), OBJ (mm, groups), BOM CSV (grouped
  identical parts), 1:1 templates SVG + DXF per item and one PDF, orthographic front/side/top +
  section A-A on A3 (SVG + PDF) at a standard scale.
* Validation per component (closed, boundary/non-manifold edges, degenerate faces, winding,
  bodies, volume, bounds) in the response; an invalid generated solid is an internal error.
* Determinism: identical requests give byte-identical files (PDF via reportlab `invariant`,
  DXF with ezdxf's fixed metadata).

No C# file was changed. `PrimitiveBuilder`, the sheet drawings and the part-design exports remain
as they are; the worker does not use them.

Verification (2026-10-23): `pytest` 117 passed. Independently, the application's own
`GlbReader.ReadNodes`, `MeshTopology`, `MeshValidator`, `PartAnalyzer` and `StlReader` (a scratch
console app referencing `ImageTo3D.Core`) read the default spacecraft: 36 nodes, all closed,
bounds -120..120 × 0..95.5 × -40.5..40.5 mm, plates classified `sheet`; STL 240 × 81 × 95.5 mm,
min Z 0. PDFs read back with pypdf: 13 A4 pages at 1:1, views on A3 landscape. SVG outputs were
checked visually in Edge.

Acceptance: reproducible geometry ✓; tests verify known dimensions ✓; meaningful coded errors ✓;
exported GLB/STL/OBJ/DXF/PDF load in independent readers ✓; works without AI weights or Meshroom ✓
(needs Python 3.11 and the pinned packages).

### Phase 2: Shared backend and frontend integration

Endpoint mapping (suggested → this project):

| Suggested | Here |
|---|---|
| `GET /api/v1/capabilities` | new `GET /api/capabilities` (engines with status installed/missing/unusable + reason, versions, GPU, optional models); `/api/health` unchanged |
| `POST /api/v1/projects` | existing `POST /api/part-designs[/blank]`; new `POST /api/part-designs/parametric` (template + parameters → project) |
| `POST /api/v1/geometry/generate` | `POST /api/part-designs/parametric/preview` (validate and return dimensions/issues without saving) and `PUT /api/part-designs/{id}/parameters` (regenerate, one undo step) |
| `GET /api/v1/jobs/{id}` | existing `GET /api/models/{id}`; Phase 3 adds `GET /api/photogrammetry/{id}` |
| `GET /api/v1/models/{id}[/download]` | existing `GET /api/models/{id}/download`, `GET /api/part-designs/{id}/...` exports; new `GET /api/part-designs/{id}/export/model?format=glb|stl|obj` and `.../export/views?format=svg|pdf` |

Worker integration (follows from D1-B): `Api/Services/Parametric/ParametricWorkerClient` starts
`<venv>\Scripts\python.exe -m parametric generate --request - --output <new folder under
data/parametric-work>` with `ProcessStartInfo.ArgumentList` (no shell), writes the request to
stdin, applies a timeout (`Parametric:Timeout`, e.g. 60 s), kills the process tree on timeout or
cancel, keeps stderr as the log (size-capped), and reads `response.json`. The exit code maps to the
HTTP status: 2 → 400/422 with the worker's code and field, 1 → 500 `geometry_engine_failed`. On
success, `model.glb` is read with `GlbReader.ReadNodes` (verified compatible in Phase 1); each node
becomes a part (`Origin = primitive`, category/thickness/material from the response), and templates,
views, BOM and response are copied into the design store under flat names. The work folder is
deleted afterwards. Python path and worker folder are configuration (`Parametric:PythonPath`,
`Parametric:WorkerDirectory`). A missing venv makes the capability `missing` and the endpoints
503; the rest of the app is unaffected. Config validation refuses paths outside the repository or
an explicitly configured folder. `describe` is called once at start, and again on demand, for the
form schema.

Data: migration `AddGeometryOrigin` (additive): `PartDesignProjects.ParametricTemplate`,
`ParametricParametersJson`, `ParametricEngineVersion`; `SourceKind` values `parametric` (later
`photogrammetry`); `GenerationJobs.Engine` (nullable, filled from `/health` at submit). DTOs gain
`origin` and `engine` (additive JSON fields).

Frontend: new page `#/parametric` (template form with sheet-thickness select, live validation,
"Create project"), origin badge component used in Workspace, part-design list/page and history;
capabilities hook. The part designer shows the parametric parameters panel for parametric projects.

Files: `Api/Controllers/CapabilitiesController.cs`, `Api/Services/Capabilities/*`,
`Api/Services/PartDesign/PartDesignService.Parametric.cs` (partial), `Api/DTOs/*` (modified),
`Api/Models/PartDesign.cs`, `Api/Models/GenerationJob.cs` (modified), migration,
`Api/Services/GenerationWorker.cs` (record engine), frontend `pages/ParametricPage.tsx`,
`components/OriginBadge.tsx`, `services/apiClient.ts`, `types/*`, `hooks/useHashRoute.ts`,
`App.tsx` (modified), tests in `tests/backend/.../ParametricTests.cs`, `CapabilitiesTests.cs`,
frontend `*.test.ts`, `docs/api.md`.

Acceptance: existing workflows and tests unchanged; a spacecraft project can be created, displayed,
regenerated, undone and exported; capability report matches what is installed; errors are RFC 7807
with codes; real DB backed up before the migration.

### Phase 3: Meshroom integration

* Configuration `Photogrammetry:MeshroomPath` (folder or `meshroom_batch.exe`), `WorkRoot`,
  `Timeout` (default 6 h), `MaxConcurrentJobs` = 1, `Pipeline` ∈ {`photogrammetry`,
  `photogrammetryDraft`} (draft = no CUDA depth maps).
* Detection: executable exists, `--help` output parsed for supported options, version from the
  install folder / `--version` if supported; result cached in capabilities. CUDA check through
  `nvidia-smi` (reuse logic of `scripts/gpu.ps1` in C#).
* Input: upload of 3–300 JPEG/PNG photos (existing `ImageDecoder` limits per image, total size cap),
  or a reference session's accepted same-object pictures. Warnings: fewer than ~20 photos, low
  resolution, duplicates (existing dHash), blur (variance of Laplacian), mixed cameras, missing EXIF
  focal length. Warnings never block; < 3 images blocks.
* Execution: `ProcessStartInfo` with `ArgumentList` (`--input`, `--output`, `--cache`, `--pipeline`
  only; no user-supplied arguments), working folder under `data/photogrammetry/<id>/work`,
  stdout/stderr to a size-capped log, kill process tree on cancel/timeout, application-owned
  folders deleted on cleanup.
* Progress: parsed from node names in the log only when recognised; otherwise "running" with
  elapsed time (no invented percentages).
* Output discovery: search the output folder for `*.obj` (then `*.ply`/`*.stl` if any) and pick the
  textured or largest mesh; record every found file; convert OBJ → `Mesh` (new `ObjReader`, geometry
  only; texture kept as a download if present) → GLB/STL with `MeshInspection`.
* Scale calibration: user gives a known distance (two picked points or bounding-box axis) → stored
  `ScaleCalibration`; without it the result is `scale: unknown`.
* Planner: `multi_view_reconstruction` becomes executable when Meshroom is available and the
  session has enough same-object photos; otherwise unsupported with the real reason.

Endpoints (mapped): `GET /api/capabilities` (engines), `POST /api/photogrammetry` (multipart),
`GET /api/photogrammetry[/{id}]`, `POST /api/photogrammetry/{id}/cancel`,
`GET /api/photogrammetry/{id}/outputs`, `GET /api/photogrammetry/{id}/download?format=glb|stl|obj`,
`GET /api/photogrammetry/{id}/log`, `PUT /api/photogrammetry/{id}/scale`,
`POST /api/part-designs/from-photogrammetry/{id}`.

Files: `Api/Models/PhotogrammetryJob.cs`, migration `AddPhotogrammetry`,
`Api/Services/Photogrammetry/` (`MeshroomLocator`, `IProcessRunner` + `ProcessRunner`,
`PhotogrammetryService`, `PhotogrammetryWorker`, `PhotoSetInspector`, `MeshroomOutputFinder`),
`Core/Geometry/ObjReader.cs`, `Core/Imaging/BlurMetric.cs`, controller, planner and
`ReconstructionService` (modified), frontend page, tests with a fake `IProcessRunner` (missing
executable, invalid input, non-zero exit, timeout, cancel, no outputs, malformed OBJ), optional
`[Trait("Category","Meshroom")]` integration test skipped when Meshroom is absent.

Acceptance: app runs without Meshroom (capability `missing`, route unsupported with reason);
with a compatible install a job runs and its log/exit code are shown; outputs inspected,
calibrated and exported. **Not verifiable on the current VM** (no Meshroom, no GPU): the real run
needs the GPU machine or the CPU draft pipeline.

### Phase 4: Synthetic dataset generation (offline, Python + Blender)

* New uv project `tools/py` (Python 3.11, pinned `requirements.txt`, pytest), separate from
  `src/inference-sf3d`. Commands: `dataset generate`, `dataset verify`, `dataset split`.
* Geometry source: the C# parametric engine through a CLI verb that writes GLB + parameters JSON
  for a seed (deterministic, no geometry duplicated in Python).
* Rendering: `blender --background --factory-startup --python tools/py/blender/render_views.py --
  <job.json>` (the only script Blender runs; arguments are a JSON file path). Outputs per view: RGB,
  mask, depth (EXR or 16-bit PNG), normals, camera intrinsics/extrinsics JSON.
* Manifest (JSON Lines + schema version): sample id, object id, source type, model path, parameters,
  view files, camera, split, seed, dataset version, licence/provenance (`generated`, own licence).
* Splits by object id (no view leakage), seeded; integrity (file exists, decodes, sizes match,
  camera matrix orthonormal, mask non-empty), SHA-256 duplicates, failed-sample report.

Acceptance: a small dataset (e.g. 50 objects × 8 views) generated offline; every sample linked to
its model; checks pass. Requires Blender (not installed here).

### Phase 5: Open 3D asset ingestion (offline, optional)

* Same `tools/py` project: `assets import-local <folder>` (always available) and source adapters
  only where a documented API exists: Smithsonian Open Access (api.data.gov key, CC0 filter), NASA
  3D Resources (public GitHub repository), Objaverse (via its published package/annotations,
  per-object licence), Sketchfab only for assets the user downloaded with a verified licence.
* Per asset: source id/URL, creator, licence + URL, download date, allowed uses, restrictions, mesh
  inspection (via the C# CLI), `training_allowed` flag (false unless licence is in an allow-list).
* Rendering of ingested assets reuses Phase 4's Blender script, only for `training_allowed`.
* The in-app reference search stays as it is (it is for reference, not training data).

Acceptance: small auditable import with licence metadata; unverified assets excluded; no network
needed for the local path.

### Phase 6: Single-image AI baseline (parameter regression)

Justification for a new model next to TripoSR/SF3D: those output an unstructured mesh; this one
outputs template parameters so the result is editable, dimensioned and buildable. The existing
engines stay as they are.

* Training (offline, `tools/py`): small CNN (e.g. ResNet-18 from torchvision, weights licence
  checked) on masks/RGB from Phase 4, regression of normalised parameters + classification of
  discrete ones (engine count, cross-section). Seeded config file, checkpoints with metadata
  (dataset version, git commit, metrics), evaluation on held-out **objects**: per-parameter MAE,
  bounding-box IoU of the regenerated mesh, Chamfer distance.
* Export to ONNX + JSON sidecar (parameter names, normalisation, training metadata).
* Inference in the API (`Api/Services/ParameterPrediction/`, ONNX Runtime 1.31 already referenced):
  validate image → existing U²-Net mask or uploaded mask → predict → parametric engine → project
  with `origin = ai_predicted` and per-parameter `predicted` flags. Missing model = capability
  `missing`, endpoint returns 503 `model_unavailable`. No confidence is shown unless the model
  outputs a calibrated one.

Acceptance: small model trained locally (CPU possible on this VM, slow), inference uses the saved
checkpoint, metrics computed, mesh previewed/exported.

### Phase 7: Advanced AI (research only)

One experiment at a time, each with a short report in `docs/experiments/`: licence and weight
provenance, hardware, inference time and memory, comparison with the Phase 6 baseline and with
TripoSR/SF3D on held-out synthetic and real photos. No code merged into the app without evidence.

### Phase 8: Mesh validation and print preparation

* `MeshInspection` (from Phase 1) on every result type, shown in the UI; units/scale status.
* Optional repairs, each a recorded operation with the original kept: weld, remove degenerate faces,
  drop tiny components (existing `KeepLargestComponent`), fix orientation, fill simple holes (planar
  loops only). Reversible by keeping the original file.
* Scratch-building: thickness/tolerance aware templates (kerf/tolerance offset parameter),
  `requires_forming` / `requires_segmentation` classification for curved parts (from `PartAnalyzer`
  flatness), dimensioned orthographic views for any part.

### Phase 9: Unified frontend workflows

Workflow A (Generate page, plus "Predict parameters" when the Phase 6 model is present), Workflow B
(Photogrammetry page), Workflow C (Parametric page + part designer). Origin badge and limitations
text on every model view; capabilities drive disabled states with the reason; orthographic camera
toggle in `ModelViewer`. Same application, same routes style.

### Phase 10: Integration testing, packaging, documentation

`scripts/check-env.ps1` (tools, versions, GPU, Meshroom, Blender, models; exit code per workflow),
`test.ps1` runs pytest for `tools/py` when its venv exists, scripts for geometry CLI, dataset,
training, inference; dependency and licence inventory (`docs/licenses.md`); setup per workflow;
backup/recovery of `data/` and the SQLite DB.

## 5. Files expected to change per phase (summary)

| Phase | Core | Api | Frontend | Python / scripts | Docs |
|---|---|---|---|---|---|
| 1 (done) | none | none | none | new `src/parametric-geometry` (Python worker, tests) | package README, this plan, architecture, environment |
| 2 | small DTO-facing types | controllers, services, DTOs, models, 1 migration, worker (engine field) | new page, badge, client, types, routes | none | `api.md`, README |
| 3 | `ObjReader`, `BlurMetric`, planner | photogrammetry services/worker/controller, 1 migration, `ReconstructionService` | new page, planner UI | `check-env` hook for Meshroom | `photogrammetry.md` |
| 4 | none (CLI verb if not added in 1) | none | none | new `tools/py`, Blender script, setup script | `dataset.md` |
| 5 | none | none | none | `tools/py/assets` | `assets.md` |
| 6 | parameter normalisation types | prediction service + endpoint, options | Generate page option | `tools/py/train`, export | `ai-baseline.md` |
| 7 | none | none | none | experiments | `experiments/*` |
| 8 | `MeshInspection`, repairs | inspection/repair endpoints | validation panel | none | README |
| 9 | none | small | pages/viewer | none | README |
| 10 | none | none | none | scripts | all |

## 6. Dependency and compatibility assessment

| Dependency | Status on this VM | Version to target | Notes |
|---|---|---|---|
| .NET SDK | 10.0.203 and 10.0.400 | net10.0 (existing) | |
| Node.js | 24.11.0 | existing Vite 8 range (20.19+/22.12+) | |
| ONNX Runtime | 1.31.0 (NuGet, existing) | same | reused for Phase 6 |
| Python | 3.11.17 and 3.15.0 via uv 0.13.3; no `python` on PATH | 3.11 for `tools/py` | 3.11 matches the existing SF3D venv and has wheels for torch/Blender-independent libs; 3.15 too new for PyTorch wheels (not checked, avoid) |
| PyTorch | 2.5.1+cpu inside SF3D venvs only | pin when Phase 6 starts; CPU and CUDA builds | CUDA build must match the GPU machine's driver |
| trimesh / numpy / OpenCV / Pillow / scikit-image | 4.4.1 / 1.26.4 / 4.11 / 12.3 / 0.26 in SF3D venv | re-pin in `tools/py` | |
| Blender | **not installed** | 5.2 LTS (released 2026-07-14, supported to 2028-07) or 4.5 LTS | runs its own bundled Python; scripts use only `bpy` + stdlib |
| Meshroom / AliceVision | **not installed** | 2025.1.0 (latest stable release found, Aug 2025; CUDA 12 builds, compute capability ≥ 5.0) | CLI options to be confirmed from the installed `meshroom_batch --help`; nightly builds exist but are marked unstable |
| NVIDIA GPU / CUDA | **none** (QEMU VM, basic display adapter) | home machine: CUDA 13 + cuDNN 9 for ONNX Runtime GPU; Meshroom bundles its own CUDA 12 runtime | Meshroom's full pipeline needs CUDA for depth maps; draft pipeline runs on CPU |
| Docker | not installed | n/a | unchanged |
| RAM / CPU / disk | 32 GB, 16 vCPU | | Meshroom wants 16–32 GB for 50–200 photos; models already use 3.5 GB disk |

## 7. Open questions, risks and assumptions

Questions for the owner:

* **Q1 Python policy.** Answered in part by D1-B: the running app may start the Python parametric
  worker. Still open: should the API's default setup (`scripts/setup.ps1`) create the worker's venv,
  or stay optional (parametric capability `missing` until set up)?
* **Q2 Where heavy work runs.** This VM has no GPU, Blender or Meshroom. Should Phases 3, 4 and 6
  be verified on the home GPU machine (whose GPU model is still unknown), with CPU-only fallbacks
  verified here?
* **Q3 Decisions D1–D7.** Confirm or change the recommendations before Phase 1/2.
* **Q4 OBJ on existing job downloads (D5-B).** Changes current behaviour and one test.
* **Q5 Spacecraft template details.** Proportions, panel layout and engine placement are not
  specified beyond the prompt; the first version will use documented defaults. Is a reference
  image or sketch available?
* **Q6 Real photo sets for Meshroom.** Is there a photo set of a real model part to use as the
  integration test?
* **Q7 Ticket key.** Commits so far use `CIR-XXXXX`; is there a real key for this work?

Risks:

* Meshroom CLI and output layout differ between versions; mitigated by `--help` probing, output
  discovery and fake-process tests, but only a real install verifies it.
* Long Meshroom runs on the API host compete for CPU/RAM with TripoSR on CPU machines;
  `MaxConcurrentJobs = 1` and a refusal while an AI job runs on CPU may be needed.
* SQLite migrations: Phase 2 and 3 migrations are additive (no table rebuild expected); back up the
  real DB before each.
* `LocalFileArtifactStore` name rule forbids Meshroom's file names; Meshroom works in its own folder
  and only normalised results enter the store.
* Synthetic-to-real gap: a model trained only on rendered procedural spacecraft will not generalise
  to arbitrary photos; Phase 6 is scoped to the procedural domain.
* Licences: Objaverse and Sketchfab assets carry mixed licences (some non-commercial or
  no-derivatives); the allow-list must exclude them from training by default.
* No booleans in the parametric engine; designs that need true CSG (curved cutouts through curved
  hulls) are out of scope for the MVP.

Assumptions:

* The repository at `C:\Users\hung.pt\projects\general\Img-to-3D`, branch `v2`, is the project to
  extend; the earlier 7-phase "retrieval-first + part design" work is complete and is the base.
* Units: millimetres in the parametric engine and all exports except GLB (metres, glTF rule).
* Single local user per browser cookie as today; no authentication changes.
