# Architecture

Status: Phase 0 audit of branch `v2` at commit `42a1745` (2026-10-23). The first part describes what
exists; the second part describes how the three reconstruction engines (single-image AI, Meshroom
photogrammetry, parametric geometry) fit into it. The step-by-step plan is in
[integration-plan.md](integration-plan.md), and tool installation is in
[environment-setup.md](environment-setup.md).

## 1. Current system (as built)

```
Browser ──► React 19 + Three.js 0.186 / React Three Fiber 9 (Vite 8, :5173, hash routes)
              │  /api (same origin through the Vite proxy or nginx)
              ▼
          ASP.NET Core API, .NET 10 (:5080)
              ├─ SQLite via EF Core (7 migrations, applied on start)
              ├─ local file stores under data/ (jobs, designs, references, reference cache)
              ├─ GenerationWorker: one background consumer of an in-memory Channel<Guid>
              ├─ optional CLIP ViT-B/32 ONNX in process (reference ranking)
              └─ HTTPS GET to public search APIs (Wikimedia, Openverse, Internet Archive, Sketchfab)
              │  HTTP on loopback, X-Internal-Api-Key when not on loopback
              ▼
          Inference service (one of, chosen at deployment time):
              ├─ src/inference          .NET + ONNX Runtime 1.31, TripoSR + U²-Net   (:5090)
              └─ src/inference-sf3d     Python 3.11 + FastAPI + PyTorch 2.5.1, SF3D (:5091) / SPAR3D (:5092)
```

Both inference services implement the same internal contract (`/health`, `/internal/jobs`), defined
in `src/shared/ImageTo3D.Core/Contracts/InferenceContracts.cs`.

### 1.1 Projects

| Project | Role |
|---|---|
| `src/shared/ImageTo3D.Core` | Pure library (static classes and records, no DI). `Geometry` (Mesh, GLB/STL read/write, marching cubes, topology, plane split, BVH, intersections, preview renderer), `PartDesign` (analysis, outlines, `PrimitiveBuilder`, fit check), `Drawing` (SVG, PDF, DXF R12 from a `PlanarOutline`), `Reconstruction` (route planner), `Embeddings` (CLIP tokenizer/preprocessing), `Imaging`, `Contracts`, `Hosting` |
| `src/backend/ImageTo3D.Api` | Public API. Controllers: `api/health`, `api/models` (generation jobs), `api/part-designs`, `api/references`. Services: Jobs (root), Inference client, PartDesign, References, Embeddings, Storage |
| `src/inference/ImageTo3D.Inference` | TripoSR ONNX service with a single-worker queue and a `generate` CLI |
| `src/inference-sf3d` | Optional Python service for Stable Fast 3D / SPAR3D (same contract). Pinned `requirements.txt`, uv-managed `.venv` / `.venv-spar3d` |
| `src/frontend` | Pages: Workspace (generate), References, Part design. Single API client `services/apiClient.ts` |
| `tests/shared`, `tests/backend`, `tests/inference` | xUnit. Backend uses `WebApplicationFactory` with `FakeInferenceClient`, `FakeReferenceWeb`, `FakeEmbedder` |
| `scripts/` | `setup.ps1`, `dev.ps1`, `test.ps1`, `gpu.ps1`, `download-models.ps1`, `setup-sf3d.ps1`, `run-sf3d.ps1` |

### 1.2 Data model (SQLite, `AppDbContext`)

| Entity | Notes relevant to the extension |
|---|---|
| `GenerationJob` | Single-image AI job. Has `Status` (Pending/Processing/Succeeded/Failed/Cancelled), `Stage`, `StageProgress`, `ErrorCode`, `ErrorMessage`, `CancelRequested`, `InferenceJobId`, required image fields (`OriginalFileName`, `OriginalFormat`, width/height), mesh stats, scale (`MmPerModelUnit`, `ScaleAxis`, `ScaleTargetMm`). **No job type, engine or provenance column.** |
| `PartDesignProject`, `DesignPart`, `PartOperation`, `PartRelation`, `DesignRelease` | Editable multi-part design with undo/redo, drawings, BOM, cut list, packages, immutable releases. `SourceKind` ∈ `generation_job`, `reference_model`, `none` (max 24 chars). `DesignPart.Origin` ∈ primitive, node, component, fragments, split, duplicate (max 16) |
| `ReferenceSession`, `ReferenceCandidate`, `ModelCandidate`, caches | Reference search, ranking, model candidates (Sketchfab listings, uploaded GLB/STL with licence), reconstruction route decision |

Migrations: `InitialCreate`, `AddPartDesign`, `AddDesignRelease`, `AddReferenceSearch`,
`AddReferenceRankingAndModels`, `AddReconstructionDecision`, `AddPartRelations`. A database created
by the old `EnsureCreated` is baselined in place by `DatabaseInitializer`.

### 1.3 Jobs

* `JobService.CreateAsync` validates the image, enforces `Upload:MaxActiveJobsPerOwner`, stores the
  files and writes `JobQueue` (unbounded `Channel<Guid>`, single reader). The database is the source
  of truth; `GenerationWorker` re-enqueues Pending/Processing rows at start.
* `GenerationWorker` handles one job at a time: submit to the inference service, poll, download
  `mesh.glb`/`input.png`, build the scaled GLB and STL (`ArtifactProcessor`). Timeout 40 min.
* Cancellation sets `CancelRequested`; the worker forwards it to the inference service.
* `RetentionCleanupService` deletes terminal jobs and their folders after 7 days. Design and
  reference stores are never touched by retention.
* Engine selection is deployment-time (`InferenceService:BaseUrl`); the engine name is read from
  `/health` and is **not stored on the job**.

### 1.4 Storage

`IArtifactStore` is keyed by `(Guid id, fileName)`. `LocalFileArtifactStore` only accepts flat names
matching `^[a-z0-9]+\.[a-z0-9]{2,4}$` (no folders, underscores or hyphens). Job artifacts are a closed
set (`ArtifactKind`, `ArtifactNames`). Designs use `IDesignArtifactStore` (`source.glb`,
`p{guid}.glb`, `r{guid}.zip`); references use the same class under another root.

### 1.5 Geometry and drawing capabilities that already exist

| Capability | Where | Limits |
|---|---|---|
| Mesh type | `Geometry/Mesh.cs` | positions + indices + optional vertex colours; no normals/UVs/materials |
| GLB write/read | `GlbWriter`, `GlbReader`, `GlbContainer`, `GlbTransformer` | single mesh per write; `ReadNodes` keeps node transforms |
| STL write/read | `StlWriter` (binary, mm), `StlReader.ReadBinary` | ASCII STL refused |
| OBJ | none | tests assert that OBJ upload and `download?format=obj` are refused |
| Validation | `MeshValidator` (finite, index range, degenerate, empty bounds), `MeshTopology.Analyze` (boundary, non-manifold, inconsistent orientation, components), `PartAnalyzer.Measure` (bounds, OBB, area, volume, issues) | three separate reports, no single shared report |
| Primitives | `PartDesign/PrimitiveBuilder` | plate, box, rod, tube; axis-aligned; 0.05–5000 mm; closed meshes |
| Plane split | `Geometry/PlaneSplitter` | capped cuts, holes supported |
| Drawings | `SheetDrawingBuilder` → SVG/PDF; `DxfWriter` | sheet parts only, from a mid-thickness `PlanarOutline`; 1:1; A4/A3; no general orthographic views |
| Raster previews | `MeshPreviewRenderer` | shaded orthographic PNG, not dimensioned |
| BOM / cut list / package / releases | `PartDesignService.Exports` | CSV, zip with SHA-256 manifest |
| Assembly | `TriangleBvh`, `MeshIntersection`, `FitCheck` | |

### 1.6 Frontend

* No router library; `hooks/useHashRoute.ts` handles `#/`, `#/jobs/<id>`, `#/designs[/<id>]`,
  `#/references[/<id>]`.
* `components/ModelViewer.tsx` loads a **GLB URL only** (drei `useGLTF`), perspective camera,
  wireframe, fit, mm dimension overlay. `components/partdesign/PartViewport.tsx` shows one GLB per
  part, selection and an exploded slider.
* Job polling: `hooks/useJob.ts` (1.5 s, exponential back-off on errors).
* Engine shown from `/api/health` (`hooks/useEngineInfo.ts`).
* Existing "estimated" labels (scale, thickness, dimensions) are the closest thing to provenance.

### 1.7 Python today

Python exists only in the optional SF3D/SPAR3D service (Python 3.11.17 via uv, torch 2.5.1+cpu on
the development VM, trimesh 4.4.1, numpy 1.26.4, OpenCV 4.11, scikit-image 0.26, FastAPI 0.115.6,
pytest 8.3.4). The default TripoSR path is Python-free; the README states that Python was not
permitted in the original target environment (see open question Q1 in the integration plan).
`scripts/test.ps1` does not run pytest.

## 2. Target architecture

The extension keeps the stack above. Nothing is replaced; the API stays the only public entry
point and SQLite stays the only database.

```
                     ┌──────────────────────── React app (same pages + new ones) ────────────────────────┐
                     │ Generate (AI) │ Photogrammetry (new) │ Parametric (new) │ References │ Part design │
                     └───────────────────────────────┬───────────────────────────────────────────────────┘
                                                     │ /api
                     ┌───────────────────────────────▼───────────────────────────────────────────────────┐
                     │ ASP.NET Core API                                                                  │
                     │  GET /api/capabilities (new) ─ what is installed / usable                         │
                     │  Parametric: python -m parametric worker process (src/parametric-geometry)        │
                     │  Single-image AI: existing GenerationWorker → inference service (unchanged)       │
                     │                   + parameter-regression ONNX model (Phase 6, in process)         │
                     │  Photogrammetry: PhotogrammetryWorker (new, own queue) → meshroom_batch process   │
                     │  Shared: GeometryOrigin + MeshInspection report (Core) on every result            │
                     │  Results land in: GenerationJob (AI) / PhotogrammetryJob (new) / PartDesignProject │
                     └───────────────────────────────────────────────────────────────────────────────────┘
          offline, developer-run (never called by the API):
             tools/py (new uv project): Blender --background dataset generation, asset ingestion,
             PyTorch training, ONNX export, evaluation  →  writes models/param-regressor/*.onnx
```

### 2.1 Engines

| Engine | Implementation | Runs where | Output | Geometry origin |
|---|---|---|---|---|
| Parametric | Python worker `src/parametric-geometry` (trimesh, shapely, ezdxf, reportlab), one process per request, JSON request/response, files in an output folder | child process of the API, synchronous (about 1-4 s per model) | part-design project, one part per component; GLB/STL/OBJ; 1:1 templates; views; BOM | `parametric` |
| Meshroom | `meshroom_batch` launched with `ProcessStartInfo.ArgumentList` (no shell) by a new hosted worker | API host, background job, separate queue | mesh (OBJ from Meshroom → normalised GLB/STL), log, metadata | `photogrammetry` |
| Single-image AI (existing) | TripoSR / SF3D / SPAR3D inference service | inference service | mesh | `ai_predicted` |
| Single-image AI baseline (Phase 6) | PyTorch model trained offline, exported to ONNX, run with ONNX Runtime in the API process (as CLIP is today) | API process | predicted parameters → Parametric engine → mesh | `ai_predicted` (parameters predicted, mesh generated) |

### 2.2 Why these boundaries

* **Parametric as a Python worker process (owner's choice, D1-B).** The engine uses trimesh and
  shapely and is called as a short-lived child process. Only a request on stdin, file paths and a
  response file cross the boundary: no port and no service, and the process is killed on timeout.
  The cost is a runtime dependency on Python 3.11 and its pinned packages for parametric
  generation, plus some overlap with C# Core (its own writers and validation). The API reads the
  worker's GLB with the existing `GlbReader`, so the part designer and its exports work on the result
  unchanged.
* **Meshroom from .NET directly.** Meshroom is itself a CLI (`meshroom_batch`). Calling it through a
  Python worker would add a hop without adding a capability. The API already owns jobs, storage,
  cancellation and retention.
* **Other Python work offline.** Dataset generation (Blender), asset ingestion and training are
  batch tools a developer runs from `scripts/`. Their product is files (dataset manifests, ONNX
  checkpoints). Runtime AI loads an ONNX file the way CLIP is loaded (to be confirmed at Phase 6).
  The parametric worker is the only Python the running application starts.
* **New photogrammetry job table, not new columns on `GenerationJob`.** `GenerationJob` requires
  single-image fields and an inference job id, and its worker processes one job at a time; a
  multi-hour Meshroom run in that queue would block AI generation. Options are compared in the
  integration plan (decision D2).

### 2.3 Shared result contract

Added to Core (Phase 1/2) and exposed on each DTO rather than as a new parallel API:

```text
GeometryOrigin   = ai_predicted | photogrammetry | parametric | uploaded | mixed
EngineInfo       = { name, version }                    e.g. { "Meshroom", "2025.1.0" }, { "Parametric", "1" }
ScaleCalibration = { status: none|estimated|calibrated|exact, mmPerUnit, reference: { kind, valueMm } }
MeshInspection   = { bounds, vertexCount, triangleCount, degenerateCount, boundaryEdges,
                     nonManifoldEdges, inconsistentEdges, components, closed, volume, issues[] }
```

`GenerationJob` DTOs get `engine` and `origin`; `PhotogrammetryJob` and parametric projects carry the
same fields. `PartDesignProject.SourceKind` gains `parametric` and `photogrammetry`, so every part
design keeps its origin. The UI shows the origin as a badge everywhere a model is shown.

### 2.4 Things that stay as they are

Hash routing, the single API client, the owner cookie, RFC 7807 errors, correlation IDs, the
artifact name whitelist (extended, not removed), retention rules for generation jobs, the existing
`/api/...` route style (no `/api/v1` prefix is introduced; suggested `v1` routes are mapped to the
existing style in the integration plan).
