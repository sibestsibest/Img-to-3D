# API reference

All errors use RFC 7807 problem details with two extra fields:

```json
{ "status": 400, "title": "Bad Request", "detail": "Unsupported image format. Upload a PNG, JPEG or WebP image.",
  "code": "invalid_image", "correlationId": "74b326004e49410395453cc97417a3cb" }
```

`code` is stable and meant for programs; `detail` is safe to show to users. Every response carries
`X-Correlation-ID` (send your own 8–64 char `[A-Za-z0-9-]` value to correlate logs).

In development, the OpenAPI document is served at `GET /openapi/v1.json` (API only).

## Public API (ASP.NET Core, `http://localhost:5080`)

Jobs are private to the browser that created them (anonymous HttpOnly cookie `i3d_owner`).
Requests for another owner's job return **404**.

### `POST /api/models`

Multipart form with field `image` (PNG/JPEG/WebP, ≤ 20 MB, 64–8192 px per side, ≤ 40 MP).
Optional header `Idempotency-Key` (8–64 chars): repeating a request with the same key returns
the original job (`200`) instead of creating a new one.

Optional multi-photo fields (Stable Fast 3D / SPAR3D engines; TripoSR ignores them):

| Field | Repeat | Meaning |
|---|---|---|
| `extraImages` | up to 5 | Photos of other sides of the same object, same limits as `image` |
| `extraViews` | one per `extraImages`, in order | Where each was taken from: `auto` (default), `front`, `back`, `left`, `right`, `top`, `bottom` (relative to the main photo; `left` = the photographer moved to their left) |

The shape always comes from `image`. Extra photos are background-removed, their camera is
estimated against the generated mesh (silhouette match, narrowed by the view hint), and they
colour the surfaces the main photo cannot see. A photo whose camera cannot be matched
confidently is skipped (see the inference service log); the job still succeeds.

| Status | Meaning |
|---|---|
| 202 | Job created (`Location` header points to it), body = `Job` |
| 200 | Idempotent replay, body = existing `Job` |
| 400 | `missing_file`, `invalid_image`, `invalid_idempotency_key`, `too_many_images`, `invalid_extra_views` |
| 413 | `file_too_large` |
| 429 | `rate_limited` (uploads/minute) or `too_many_active_jobs` |

### `GET /api/models/{id}` → `Job`

```json
{
  "id": "d490aace-29c5-47c2-b5d0-e27a08c42ca1",
  "status": "Processing",            // Pending | Processing | Succeeded | Failed | Cancelled
  "stage": "EvaluatingDensity",      // see stages below; null when not running
  "stageProgress": 0.42,             // 0..1, only for stages where progress is measured
  "errorCode": null, "error": null,
  "createdAt": "...", "startedAt": "...", "completedAt": null,
  "originalFileName": "robot.jpg", "imageWidth": 512, "imageHeight": 512,
  "mesh": { "vertices": 81862, "triangles": 163712 },          // after success
  "dimensionsMm": { "x": 91.72, "y": 100.0, "z": 80.29 },     // bounding box, Y = up
  "scale": { "axis": "Longest", "targetMm": 100, "mmPerModelUnit": 101.05 },
  "downloads": { "glb": true, "stl": true, "stlError": null }
}
```

Stages, in order: `Submitting`, `Queued`, `LoadingModels`, `PreparingImage`, `RemovingBackground`,
`EncodingImage`, `EvaluatingDensity` (progress measured per chunk), `ExtractingSurface`,
`ColoringVertices`, `Exporting`, `Finalizing`.

Failure codes: `invalid_image`, `no_foreground`, `no_surface`, `out_of_memory`, `timeout`,
`inference_unavailable`, `inference_lost`, `invalid_mesh`, `model_unavailable`, `internal_error`.

### `GET /api/models?page=1&pageSize=20` → `{ items: Job[], page, pageSize, totalCount }`

Newest first. `page` ≥ 1, `pageSize` 1–100.

### `GET /api/models/{id}/preview`

Scaled GLB inline (`model/gltf-binary`) for the viewer. `409 not_ready` until the job succeeds.

### `GET /api/models/{id}/download?format=glb|stl`

* `glb`: glTF 2.0 binary, **metres**, Y-up, vertex colours.
* `stl`: binary STL, **millimetres**, Z-up, resting on Z = 0, header `ImageTo3D binary STL; units=mm`.

Errors: `400 invalid_format`, `404 not_found | stl_unavailable | artifact_missing`, `409 not_ready`.

### `GET /api/models/{id}/image?kind=source|preprocessed`

`source`: the upload, EXIF orientation applied, as PNG. `preprocessed`: background removed and
re-framed image exactly as TripoSR saw it (after success).

### `PUT /api/models/{id}/scale`

```json
{ "axis": "Y", "targetMm": 144 }
```

`axis`: `X` (width), `Y` (height), `Z` (depth) or `Longest`; `targetMm`: 0.1–10000. Applies a
uniform scale computed from the raw mesh (repeated calls never compound), regenerates GLB and STL,
returns the updated `Job`. `400` on validation errors, `409 not_ready` before success.

### `POST /api/models/{id}/cancel`

`202` with the job; the final `Cancelled` status follows within a poll interval. Cancellation
reaches the running ONNX Runtime call (`RunOptions.Terminate`), so CPU/GPU work stops promptly.
`409 already_finished` for finished jobs.

### `DELETE /api/models/{id}` → `204`

Cancels running work and deletes the database row and every file of the job.

### `GET /api/health`

```json
{ "status": "ok", "database": "ok", "inference": { "status": "ok", "modelState": "Ready" }, "version": "1.0.0.0" }
```

`status` is `degraded` when the inference service is unreachable or still loading; HTTP 503 only
when the database is unavailable. No configuration, paths or secrets are exposed.

## Part design (`/api/part-designs`)

A project breaks a succeeded model into derived, individually editable parts. The job's raw mesh
is copied into the project (`data/designs/<project>/source.glb`) and never modified; projects are
not affected by job retention or job deletion. Owner isolation is the same as for jobs (other
browsers get `404`). Lengths are millimetres; coordinates are the viewer's: Y-up, centred on X/Z,
resting on Y = 0, at the job's scale when the project was created. Every mutating call returns the
full project.

| Method | Path | Notes |
|---|---|---|
| POST | `/api/part-designs` | JSON `{ sourceJobId, name? }`. Splits the mesh into nodes and connected pieces (small pieces grouped as fragments) and suggests a category per part. `201`; `404 source_not_found`, `409 not_ready`, `410 artifact_missing`, `422 source_too_large` / `no_usable_geometry` |
| GET | `/api/part-designs?page&pageSize` | Summaries with active part count |
| GET | `/api/part-designs/{id}` | Project, parts (active and superseded), history (last 50), `canUndo` / `canRedo` |
| PUT | `/api/part-designs/{id}` | `{ name, fitAllowanceMinMm, fitAllowanceMaxMm }` (not part of the undo history) |
| DELETE | `/api/part-designs/{id}` | `204`; removes rows and files, never the source job |
| PUT | `/api/part-designs/{id}/parts/{partId}` | Full replacement of editable fields: `name, category, reviewStatus, units ("mm" only), material, manufacturingMethod, quantity, thicknessMm, diameterMm, outerDiameterMm, innerDiameterMm, cutLengthMm, cutAngleDeg, fitAllowanceMm, confirmedLengthMm/WidthMm/HeightMm, stockDescription, stockSource, needsFurtherModelling, notes`. Out-of-range values → `400`; inconsistent combinations (e.g. tube inner ≥ outer) are saved and reported in `validation` |
| POST | `/api/part-designs/{id}/parts/{partId}/duplicate` | Copy of the metadata; shares the immutable geometry |
| DELETE | `/api/part-designs/{id}/parts/{partId}` | Marks the part removed (undoable) |
| POST | `/api/part-designs/{id}/parts/{partId}/split/preview` | `{ point: [x,y,z], normal: [x,y,z] }` → contours, caps, per-side volume and whether both halves are closed. Nothing is saved |
| POST | `/api/part-designs/{id}/parts/{partId}/split` | Same body plus `allowOpenResult`. Two new parts; the original becomes `Superseded`. `400 plane_misses_part` / `invalid_plane`; `409 split_not_closed` unless `allowOpenResult` |
| POST | `/api/part-designs/{id}/undo`, `/redo` | `409 nothing_to_undo` / `nothing_to_redo`. A new edit after undo discards the redo branch |
| GET | `/api/part-designs/{id}/parts/{partId}/geometry` | GLB in metres, design coordinates; immutable per part ID |
| GET | `/api/part-designs/{id}/parts/{partId}/drawing/report` | Sheet parts only. Page, outline size, thickness (`specified` or `estimated`), holes (centre from the outline's lower-left corner, diameter when round) and every outline and drawing check. `409 drawing_unsupported` (not a sheet), `422 outline_failed` (not flat, open surface) or `422 part_too_large_for_page` (larger than A3 at 1:1) |
| GET | `/api/part-designs/{id}/parts/{partId}/drawing?format=svg\|pdf\|dxf&download=` | 1:1 cutting sheet. SVG: `width`/`height` in mm equal to the `viewBox` (1 unit = 1 mm), groups `cut` (cutting contours only), `annotation`, `reference` (100 mm bar). PDF: vector, page in whole points (A4 = 595 x 842), text as outlines, no embedded fonts. DXF (`image/vnd.dxf`): ASCII R12, `$INSUNITS` 4 (mm), model space with the origin at the outline's lower-left corner; layer `CUT` = closed polylines of the outer contours and holes only, layer `ANNOTATION` = hole labels and a description under the part. The DXF does not depend on a page, so it is also served for parts larger than A3. Title block / DXF text say `Draft`. `Cache-Control: no-store`, `nosniff`, restrictive CSP. Same errors as the report, plus `400 invalid_format` |
| GET | `/api/part-designs/{id}/parts/{partId}/stl` | Binary STL of the part in millimetres (design coordinates), any category. `422 stl_failed` for unusable geometry |
| GET | `/api/part-designs/{id}/export/bom` | Bill of materials, CSV (UTF-8 with BOM, CRLF, invariant numbers), one row per active part in tree order: item, part ID, name, category, review and validation status, quantity, material, method, every manufacturing value, estimated and confirmed dimensions, stock details, issue codes, notes. Text cells starting with `= + - @` are prefixed with `'` |
| GET | `/api/part-designs/{id}/export/cut-list` | Rod and tube cuts, CSV: one row per distinct cut (same category, material, section, length, angle, fit allowance) with summed quantity, `total_length_mm` and the part names; rows of the same stock share a `stock_group` (`S1`, `S2`…), longest cut first. A missing diameter or cut length falls back to the model estimate and is marked `estimated` in `size_status` / `length_status`. No stock-length nesting |
| GET | `/api/part-designs/{id}/export/manifest` | Builds a draft package and returns only its `manifest.json` (see below) — a check before downloading or releasing |
| GET | `/api/part-designs/{id}/export/package` | Draft package (zip), built now and not stored. `409 nothing_to_export` (no active part) or `409 export_blocked` (a part has a validation error, e.g. tube inner ≥ outer) |
| GET | `/api/part-designs/{id}/releases` | Stored releases, newest first: `{ id, revision, note, sizeBytes, sha256, historySequence, partCount, fileCount, warningCount, createdAt }` |
| POST | `/api/part-designs/{id}/releases` | JSON `{ note? }` (≤ 500 chars). Builds the next numbered package (`revision` 1, 2, 3… per project), stores the zip and returns `201` with the release. Drawings and DXFs inside say `Rev N`. Same `409` errors as the draft package, plus `409 too_many_releases` (`PartDesign:MaxReleasesPerProject`, default 50) and `409 release_conflict` (concurrent release; retry) |
| GET | `/api/part-designs/{id}/releases/{revision}/package` | The stored zip, byte for byte (its SHA-256 equals the release's `sha256`), whatever was edited or undone since. `404` for an unknown revision |

Part fields worth knowing: `category` ∈ `sheet, rod, tube, stock_part, custom_3d, needs_review`
(the analysis never suggests `stock_part`, and uses `needs_review` when its confidence is below
0.5); `reviewStatus` ∈ `needs_review, user_confirmed`; `estimated.status` is always `estimated`;
`confirmed.status` is `user_confirmed` once a measured dimension is entered; `validation.status` ∈
`valid, warning, invalid` with `issues[]` of `{ code, severity, message, source }`.

Export package layout (draft and release are identical apart from `kind`, `revision` and the
revision label on drawings):

```
manifest.json            schemaVersion 1; kind draft|release; revision; note; createdAt (UTC); units "mm";
                         project { id, name, sourceJobId, sourceFileName, mmPerModelUnit, scaleStatus,
                         historySequence, updatedAt }; parts[] { item, id, name, category, reviewStatus,
                         validationStatus, quantity, files[] }; files[] { path, kind, partId, bytes, sha256 };
                         warnings[] { code, severity, message, partId }
README.txt               contents and printing instructions
bom.csv, cut-list.csv    as the endpoints above
drawings/NN-<name>-<id8>.pdf|svg|dxf   sheet parts
stl/NN-<name>-<id8>.stl                every part, millimetres
```

`NN` is the item number (tree order). Parts with warnings are included and each warning is listed
(including `not_reviewed` for parts the user has not confirmed). A sheet part whose outline cannot
be extracted gets no drawing (`outline_failed`), and one larger than A3 gets only the DXF
(`part_too_large_for_page`).

## Internal inference API (`http://127.0.0.1:5090`)

Not for browsers. Bound to loopback by default; if bound to any other address the service
refuses to start unless `Inference:ApiKey` is set, and then every `/internal/*` call needs
`X-Internal-Api-Key`.

| Method | Path | Notes |
|---|---|---|
| GET | `/health` | `200` when models are loaded, `503` while loading/failed. Body `{ status, modelState, reason, executionProvider, queuedJobs }` |
| POST | `/internal/jobs/` | Multipart `image`; optional `resolution` (32–320), `foregroundRatio` (0.5–1.0), `removeBackground` (bool) (TripoSR). SF3D/SPAR3D service: optional repeated `extraImages` + `extraViews` (see `POST /api/models`). `202` + job, `400`, `413`, `503 queue_full` |
| GET | `/internal/jobs/{id}` | Job status, stage, progress, artifacts, mesh stats (model units) |
| GET | `/internal/jobs/{id}/artifacts/{name}` | `name` ∈ {`mesh.glb`, `input.png`} only |
| POST | `/internal/jobs/{id}/cancel` | `202`, `409` if finished |
| DELETE | `/internal/jobs/{id}` | Removes the job and its files |

Jobs live in memory; a restart loses them (the API then fails the job with `inference_lost`).
