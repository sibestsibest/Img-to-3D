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
  "downloads": { "glb": true, "stl": true, "stlError": null },
  "engine": "TripoSR",               // engine that predicted the mesh (null for jobs before this field)
  "origin": "ai_predicted"           // geometry origin, always ai_predicted for jobs
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

### `GET /api/capabilities`

Which geometry engines can be used now, for the UI to offer only working workflows:

```json
{ "engines": [
  { "id": "parametric", "kind": "parametric", "origin": "parametric", "status": "ready", "version": "0.1.0", "reason": null },
  { "id": "TripoSR", "kind": "single_image_ai", "origin": "ai_predicted", "status": "ready", "version": null, "reason": null },
  { "id": "meshroom", "kind": "photogrammetry", "origin": "photogrammetry", "status": "unavailable", "version": null,
    "reason": "Multi-photo reconstruction (Meshroom) is not integrated yet." } ] }
```

`status`: `ready`, `missing` (not set up, e.g. no worker venv), `disabled`, `failed` or
`unavailable`; `reason` says what to do. Errors carry an optional `field` extension (e.g.
`"parameters.height"`) when they are about one request field.

## Part design (`/api/part-designs`)

A project breaks a model into derived, individually editable parts. The source is a succeeded
generation job, an existing model accepted in a reference search (created through
`POST /api/references/{id}/reconstruction`), or nothing at all (an empty project for stock
shapes). The source file is copied into the project (`data/designs/<project>/source.glb`, or
`source.stl` for a reused STL) and never modified; projects are not affected by job retention or
job deletion. Owner isolation is the same as for jobs (other browsers get `404`). Lengths are
millimetres; coordinates are the viewer's: Y-up, centred on X/Z, resting on Y = 0, at the job's
scale when the project was created (a reused GLB uses its declared metres, a reused STL is assumed
to be millimetres and converted from Z-up). Every mutating call returns the full project.

Project: `{ id, name, sourceJobId (null unless made from a job), sourceFileName, source { kind:
generation_job|reference_model|none|parametric, jobId, referenceSessionId, modelCandidateId, fileName,
license, attribution }, mmPerModelUnit, scaleStatus, coordinateSystem, fitAllowanceMinMm,
fitAllowanceMaxMm, createdAt, updatedAt, issues[], parts[], relations[], history[], canUndo, canRedo, units,
origin: ai_predicted|parametric|uploaded|manual, parametric (null unless generated from parameters) }`.
`scaleStatus` is `exact` for parametric projects, otherwise `estimated`.

Relation: `{ id, partAId, partBId, kind, allowOverlap, openingSizeMm, note, fit { status:
ok|too_tight|too_loose|unknown|not_applicable, openingMm, insertedMm, clearanceMm, basis:
specified|estimated }, validation, units }`. `kind` and the roles of A and B: `glued` (glued face to
face), `peg_in_hole` (A is the peg; the hole is `openingSizeMm`, or B's bore when B is a tube),
`tube_over_rod` (A is the tube), `nested` (B sits inside A) and `slot` (B is the sheet; the slot
width is `openingSizeMm`). The clearance is opening minus inserted size (negative = interference)
and is compared with the project's `fitAllowanceMinMm`..`fitAllowanceMaxMm`; specified sizes are
used before the model's estimates (`basis: estimated`).

| Method | Path | Notes |
|---|---|---|
| POST | `/api/part-designs` | JSON `{ sourceJobId, name? }`. Splits the mesh into nodes and connected pieces (small pieces grouped as fragments) and suggests a category per part. `201`; `404 source_not_found`, `409 not_ready`, `410 artifact_missing`, `422 source_too_large` / `no_usable_geometry` |
| POST | `/api/part-designs/blank` | JSON `{ name? }`. Empty project without a source (`source.kind` `none`), for parts built from stock shapes. `201`; `409 too_many_projects` |
| GET | `/api/part-designs/parametric/templates` | Templates of the parametric geometry worker: parameters with `default`, `minimum`, `maximum`, `unit`, `description` and `allowed` (PS sheet thicknesses 0.3, 0.5, 0.8, 1, 1.5, 2, 3 mm); the assembly template also lists its `shapes`. `503 geometry_engine_unavailable` when the worker is not set up |
| POST | `/api/part-designs/parametric` | JSON `{ template, parameters?, name? }` (`template` `[a-z0-9_]{1,32}`; omitted parameters take their defaults). Runs the worker (a few seconds, rate limited like uploads) and creates a project with `source.kind` `parametric`, `origin` `parametric`, `scaleStatus` `exact`: one part per component (origin `parametric`, category, sheet thickness, material, rod/tube sizes from the generator, review `user_confirmed`; identical pieces in one component, e.g. 24 bolt heads, keep quantity 1 with a stock note). `parametric { template, engineVersion, parameters, files[] }` lists the generated files. `201`; `400 invalid_parameter` / `unsupported_thickness` / `unknown_template` / `invalid_request`, `413 request_too_large`, `422 geometry_conflict` / `limit_exceeded` (all with `field`), `503 geometry_engine_unavailable` / `geometry_engine_busy`, `504 geometry_engine_timeout`, `500 geometry_engine_failed` |
| GET | `/api/part-designs/{id}/parametric/files/{kind}?download=true` | A generated file as made with the project (later edits are not in it): `templates-pdf` (1:1 cutting templates), `views-pdf`, `views-svg` (orthographic views and section), `bom` (CSV, identical parts grouped), `stl` (mm, Z-up), `obj` (mm, Y-up), `package` (zip of everything, per-part SVG/DXF templates included). `404` unknown or not generated, `409 not_parametric` |
| POST | `/api/part-designs/{id}/parts/primitive` | JSON `{ kind: plate\|box\|rod\|tube, units: "mm", name?, material?, lengthMm, widthMm, heightMm, thicknessMm, diameterMm, outerDiameterMm, innerDiameterMm, axis?: x\|y\|z, center?: [x,y,z] }` (sizes 0.05–5000 mm; which ones are needed depends on the kind: plate = length, width, thickness with `axis` the thickness direction, default `y`; box = length X, height Y, width Z; rod = diameter, length with `axis` the length direction, default `x`; tube = outer and inner diameter, length). Adds a closed part (origin `primitive`, round shapes as 48-sided polygons on the nominal circle) whose category and specification follow the shape (plate → `sheet` with `thicknessMm`; rod/tube → `cutLengthMm` and diameters; box → `custom_3d`), review `user_confirmed`. Undoable (`add_primitive`). `400 invalid_primitive` (missing size, tube inner ≥ outer or wall < 0.05 mm) or validation errors |
| GET | `/api/part-designs?page&pageSize` | Summaries with active part count and `sourceKind` |
| GET | `/api/part-designs/{id}` | Project, parts (active and superseded), history (last 50), `canUndo` / `canRedo` |
| PUT | `/api/part-designs/{id}` | `{ name, fitAllowanceMinMm, fitAllowanceMaxMm }` (not part of the undo history) |
| DELETE | `/api/part-designs/{id}` | `204`; removes rows and files, never the source job |
| PUT | `/api/part-designs/{id}/parts/{partId}` | Full replacement of editable fields: `name, category, reviewStatus, units ("mm" only), material, manufacturingMethod, quantity, thicknessMm, diameterMm, outerDiameterMm, innerDiameterMm, cutLengthMm, cutAngleDeg, fitAllowanceMm, confirmedLengthMm/WidthMm/HeightMm, stockDescription, stockSource, needsFurtherModelling, notes`. Out-of-range values → `400`; inconsistent combinations (e.g. tube inner ≥ outer) are saved and reported in `validation` |
| POST | `/api/part-designs/{id}/parts/{partId}/duplicate` | Copy of the metadata; shares the immutable geometry |
| DELETE | `/api/part-designs/{id}/parts/{partId}` | Marks the part removed (undoable) |
| POST | `/api/part-designs/{id}/parts/{partId}/split/preview` | `{ point: [x,y,z], normal: [x,y,z] }` → contours, caps, per-side volume and whether both halves are closed. Nothing is saved |
| POST | `/api/part-designs/{id}/parts/{partId}/split` | Same body plus `allowOpenResult`. Two new parts; the original becomes `Superseded`. `400 plane_misses_part` / `invalid_plane`; `409 split_not_closed` unless `allowOpenResult` |
| POST | `/api/part-designs/{id}/relations` | JSON `{ partAId, partBId, kind, allowOverlap?, openingSizeMm? (0.01–1000), note? (≤ 500) }`. Undoable (`relate`). `400 relation_self` or validation errors, `404` unknown part, `409 part_not_active`, `409 relation_exists` (the pair, in either order, is already related), `409 too_many_relations` (`PartDesign:MaxRelationsPerProject`, default 1000) |
| PUT | `/api/part-designs/{id}/relations/{relationId}` | JSON `{ kind, allowOverlap, openingSizeMm, note }` (the parts cannot change). Undoable (`edit_relation`) |
| DELETE | `/api/part-designs/{id}/relations/{relationId}` | Marks the relation removed (undoable, `unrelate`); `409 relation_not_active` if already removed. Deleting or splitting a part removes its relations in the same history step |
| GET | `/api/part-designs/{id}/assembly` | Assembly check computed now (`no-store`): `{ status, checkedAt, historySequence, contactToleranceMm, pairsChecked, triangleTests, complete, relations[], collisions[], quality, issues[] }`. Every pair of active parts whose boxes meet is tested triangle against triangle (BVH); surfaces closer than `PartDesign:ContactToleranceMm` (0.05 mm) touch. Collision `contact`: `intersecting` (surfaces cross; `intersectionLengthMm` is the length of the intersection curve), `contained` (one closed solid wholly inside the other, by ray parity; open meshes are not tested), `coincident` (same geometry in the same place, e.g. an unmoved duplicate), `touching`, or `gap` for a glued relation whose parts do not meet. Severity is `warning` unless a relation explains it (`allowOverlap` or `nested` → `overlap_allowed`, glued faces touching → `glued_contact`, both `info`). Stops after `PartDesign:MaxCollisionTriangleTests` tests with `complete: false` and `collision_check_incomplete`. `quality`: per active part `{ status, closed, boundaryEdges, nonManifoldEdges, inconsistentEdges, degenerateTriangles, components, triangles, volumeMm3, solidExportReady, collisionCount, issueCodes }` plus project totals |
| POST | `/api/part-designs/{id}/undo`, `/redo` | `409 nothing_to_undo` / `nothing_to_redo`. Undoes part and relation changes alike. A new edit after undo discards the redo branch (relations it created are deleted) |
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
| GET | `/api/part-designs/{id}/releases/compare?from=&to=` | Compares two stored releases from their manifests: `{ fromRevision, toRevision, added[], removed[], changed[] { id, name, changes[] }, unchangedCount, specCompared, relationsCompared, relationsAdded, relationsRemoved, relationsChanged, warningsBefore, warningsAfter, notes[] }`. `changes` holds `name`, `category`, `quantity`, `reviewStatus`, `validationStatus`, `geometry` (STL hash) and `spec.<field>`; spec fields and relations are only compared when both packages carry them (releases made before Phase 7 do not). `404` for an unknown revision |
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
                         project { id, name, sourceJobId, sourceFileName, source (as above),
                         mmPerModelUnit, scaleStatus, historySequence, updatedAt }; parts[] { item, id, name, category, reviewStatus,
                         validationStatus, quantity, files[], spec, geometrySha256 }; files[] { path, kind, partId, bytes, sha256 };
                         warnings[] { code, severity, message, partId } (including relation, fit and collision warnings);
                         assembly { status, relationCount, collisionCount, collisionCheckComplete, qualityStatus,
                         solidExportReadyParts }
README.txt               contents and printing instructions
metadata/assembly.json   the assembly check (relations with fits, collisions, issues; quality totals)
metadata/quality.json    geometry quality per part
bom.csv, cut-list.csv    as the endpoints above
drawings/NN-<name>-<id8>.pdf|svg|dxf   sheet parts
stl/NN-<name>-<id8>.stl                every part, millimetres
```

`NN` is the item number (tree order). Parts with warnings are included and each warning is listed
(including `not_reviewed` for parts the user has not confirmed). A sheet part whose outline cannot
be extracted gets no drawing (`outline_failed`), and one larger than A3 gets only the DXF
(`part_too_large_for_page`).

## Reference search (`/api/references`)

A session collects reference pictures of one object: an optional input photo, the search query and
the candidates found or added. Sessions have their own storage (`data/references/<session>/`), are
not affected by job retention or deletion, and are isolated per owner like jobs (others get `404`).
Every mutating call returns the full session.

| Method | Path | Notes |
|---|---|---|
| GET | `/api/references/sources` | `[{ id, name, description, enabled, access: "free_no_account" }]`: `wikimedia`, `openverse`, `internet_archive` |
| POST | `/api/references` | Multipart, all optional: `image` (input photo, upload limits apply) **or** `sourceJobId` (copies the job's upright photo), `name`, `query`. `201`; `400 ambiguous_input` / `invalid_image`, `404 source_not_found`, `409 too_many_sessions`, `410 artifact_missing` |
| GET | `/api/references?page&pageSize` | Summaries with candidate and accepted counts |
| GET | `/api/references/{id}` | Session |
| PUT | `/api/references/{id}` | `{ name, query? }` |
| DELETE | `/api/references/{id}` | `204`; removes rows and files, never the source job |
| POST | `/api/references/{id}/search` | JSON `{ query?, sources? }` (query replaces the session's; sources default to all enabled). Always `200` for an existing session, each source reporting in `lastSearch`; `400 query_required` (under 2 characters) / `unknown_source`, `429 rate_limited` (`ReferenceSearch:SearchesPerMinute` per browser) |
| POST | `/api/references/{id}/candidates` | Multipart `image` plus optional `title`, `author`, `pageUrl`, `license` (one of `unknown, cc0, public_domain, cc-by, cc-by-sa, cc-by-nd, cc-by-nc, cc-by-nc-sa, cc-by-nc-nd, gfdl, own_work`), `licenseUrl`. Added as `accepted`. `400 invalid_image` / `invalid_license` / `invalid_page_url` / `invalid_license_url`, `409 already_added` / `session_full` |
| PUT | `/api/references/{id}/candidates/{candidateId}` | `{ decision: pending\|accepted\|rejected, imageKind?: unknown\|photo\|render\|drawing, relation?: unknown\|same_object\|variant\|similar_different, note? }` |
| DELETE | `/api/references/{id}/candidates/{candidateId}` | Removes the candidate and its files; duplicates are regrouped |
| GET | `/api/references/{id}/input?size=thumbnail\|full` | Input photo: JPEG preview or upright PNG |
| GET | `/api/references/{id}/candidates/{candidateId}/thumbnail` | Stored JPEG preview (longest side ≤ `ThumbnailMaxSide`), `404` when none |
| DELETE | `/api/references/cache` | Clears the shared, disposable caches: `{ searchEntries, files, bytes, embeddings }`. Sessions keep their pictures |
| GET | `/api/references/capabilities` | `{ embeddings: { available, model, reason }, imageSources[], modelSources[] }`; loads the CLIP model on first call |
| POST | `/api/references/{id}/rank` | Recompute ranking, groups and suggestions of pictures and models (uses accepted pictures). Also done automatically after a search, a manual picture, a removal and a model search or upload |
| POST | `/api/references/{id}/models/search` | JSON `{ query? }`. Sketchfab listings (anonymous); always `200`, the outcome is in `lastModelSearch`; `400 query_required` |
| POST | `/api/references/{id}/models` | Multipart `file` (binary GLB or binary STL, format detected from the content) plus optional `title`, `author`, `pageUrl`, `license`, `licenseUrl`. Checked geometrically. `400 unsupported_model` (ASCII STL, OBJ, others) / `invalid_license`, `409 already_added` / `session_full`, `413 file_too_large`, `422 unreadable_model` / `invalid_mesh` / `model_too_large` |
| PUT | `/api/references/{id}/models/{modelId}` | `{ decision: pending\|accepted\|rejected, note? }` |
| DELETE | `/api/references/{id}/models/{modelId}` | Removes the candidate, its preview and uploaded file |
| GET | `/api/references/{id}/models/{modelId}/thumbnail` | JPEG preview (the listing's image, or a shaded render of an uploaded mesh) |
| GET | `/api/references/{id}/models/{modelId}/file` | The uploaded GLB (`model/gltf-binary`) or STL (`model/stl`), byte for byte |
| GET | `/api/references/{id}/reconstruction` | The reconstruction decision (below). Brings the state of a started generation up to date with its job |
| POST | `/api/references/{id}/reconstruction` | JSON `{ route, modelId?, name? }` starts a route. `reuse_existing_model`: part design from the accepted uploaded model (`modelId` defaults to the plan's), no inference. `manual_geometry_required`: empty part design. `single_image_reconstruction` / `hybrid_reconstruction`: a generation job from the session's input photo (hybrid adds the accepted `same_object` pictures as extra photos, view `auto`, at most `Upload:MaxExtraImages`), with the usual upload limits and queue. Returns the decision. `400 invalid_route`, `404 not_found` (model), `409 route_unsupported` (multi-view) / `route_unavailable` (blockers in the message) / `reconstruction_in_progress` / `model_not_reusable`, `410 artifact_missing`, `429 too_many_active_jobs` / rate limit |

Session: `{ id, name, query, sourceJobId, hasInputImage, inputWidth, inputHeight, createdAt,
updatedAt, lastSearchedAt, lastSearch[], manualLinks[], candidates[], ranking, lastModelSearch[],
models[] }`. `ranking`: `{ mode: embeddings|basic|null, rankedAt, note, inputViewpoint, embeddingModel }`
(`note` says why ranking is basic or partial, e.g. the model is not installed).

`lastSearch[]`: `{ source, name, status, hits, added, fromCache, message }` with `status` ∈ `ok,
empty, failed, timeout, rate_limited, disabled`. Failures are not cached; results (including empty
ones) are cached per source and normalised query for `ReferenceSearch:CacheTtl`.

`manualLinks[]`: `{ name, url, kind }` (`kind` ∈ `images, reference, shop, models`), browser search
links for the current query. Nothing is fetched from them.

Candidate: `{ id, origin: search|manual, source, sourceName, title, pageUrl, imageUrl, author,
license { code, label, url, raw, commercialUse, derivatives, attributionRequired }, attribution,
query, retrievedAt, downloadStatus: downloaded|failed|skipped, downloadError, hasThumbnail,
thumbnailWidth, thumbnailHeight, width, height, duplicateOfId, duplicateKind: exact|near|null,
matchesInput, decision, imageKind, relation, note, warnings[] }`. `pageUrl` is a web page, `imageUrl`
a direct image address (null when the source gives none). Licence permissions are `null` when the
licence is unknown. `warnings[]` are `{ code, severity, message, source }` with codes
`licence_unknown` (personal reference only), `non_commercial`, `no_derivatives`, `preview_failed`,
`duplicate`, `matches_input`. Previews are only downloaded over HTTPS from the source's own image
hosts (`skipped` otherwise); a duplicate is a copy of the same picture, not another photo of the
same object.

Candidate `ranking`: `{ position (1 = best), score (0..1), confidence (0..1), group, querySimilarity,
inputSimilarity, viewpoint, kind, relation, reasons[] }`. `viewpoint`, `kind` and `relation` are
`{ label, confidence }` suggestions (`uncertain` when no label is clearly ahead; relation confidence
≤ 0.5), never assertions. `reasons[]`: `{ code, message, effect: +|-|0 }`, codes `query_match`,
`query_weak`, `looks_like_input`, `unlike_input`, `like_accepted`, `new_view`, `same_view`,
`low_resolution`, `high_resolution`, `licence_unknown`, `licence_restricted`, `duplicate`,
`is_input`, `no_visual`.

Model candidate: `{ id, origin: search|manual, source: sketchfab|upload, sourceName, title, pageUrl,
author, license, attribution, query, retrievedAt, isDownloadable, faceCount, vertexCount,
fileBytes, hasThumbnail, downloadStatus, format: glb|stl|null, evaluationStatus:
metadata_only|geometry_checked, score, confidence, visualSimilarity, reasons[], geometry, decision,
note, warnings[] }`; `geometry` (uploads): `{ triangles, components, closed, boundaryEdges,
nonManifoldEdges, degenerateTriangles, sizeXMm, sizeYMm, sizeZMm, scaleStatus:
plausible|unitless|suspicious }`. Model reason codes: `looks_like_object`, `looks_different`,
`looks_somewhat`, `query_weak`, `downloadable`, `not_downloadable`, `no_derivatives`,
`licence_unknown`, `very_coarse`, `dense_mesh`, `closed`, `open`, `degenerate`, `fragmented`,
`components`, `scale_plausible`, `scale_unitless`, `scale_suspicious`, `no_visual`, `listing_only`.
Models are listed best score first.

Reconstruction decision: `{ state, route, startedAt, jobId, jobStatus, designId, modelId,
extraReferenceIds[], message, recommended, routes[], notes[], engine, engineReachable }`. `routes[]`
is every route in preference order, `{ route, availability: available|unavailable|unsupported,
recommended, reasons[] { code, severity: info|warning|blocker, message }, modelId, extraPhotos }`:

1. `reuse_existing_model`: needs an accepted upload whose geometry was checked (closed meshes, then
   the highest score, are preferred). Search listings have no file (`listing_only`). Warnings:
   `model_open`, `model_unitless`, `model_scale_suspicious`, `model_licence_unknown`,
   `model_no_derivatives`. While it is available, both generation routes are blocked
   (`existing_model_accepted`): no heavy inference runs when a suitable model is accepted.
2. `multi_view_reconstruction`: always `unsupported` (`no_multiview_engine`); no installed engine
   reconstructs geometry from several photos.
3. `hybrid_reconstruction`: input photo, at least one accepted picture marked `same_object` (not a
   duplicate, not the input itself) and an engine that uses extra photos (`StableFast3D`, `SPAR3D`).
   The shape still comes from the input photo; the extra photos colour the hidden sides.
4. `single_image_reconstruction`: input photo; `engine_unreachable` is only a warning.
5. `manual_geometry_required`: always available.

`recommended` is the first available route. `state`: `reference_search` or
`model_candidate_review` (model candidates pending) before anything starts; the route while a
generation runs; `manual_geometry_required` after starting manual geometry; then `completed`,
`failed` (with `message`, also when a running job was deleted) or `cancelled`. A finished or failed
route can be started again.

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
