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

| Status | Meaning |
|---|---|
| 202 | Job created (`Location` header points to it), body = `Job` |
| 200 | Idempotent replay, body = existing `Job` |
| 400 | `missing_file`, `invalid_image`, `invalid_idempotency_key` |
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

## Internal inference API (`http://127.0.0.1:5090`)

Not for browsers. Bound to loopback by default; if bound to any other address the service
refuses to start unless `Inference:ApiKey` is set, and then every `/internal/*` call needs
`X-Internal-Api-Key`.

| Method | Path | Notes |
|---|---|---|
| GET | `/health` | `200` when models are loaded, `503` while loading/failed. Body `{ status, modelState, reason, executionProvider, queuedJobs }` |
| POST | `/internal/jobs/` | Multipart `image`; optional `resolution` (32–320), `foregroundRatio` (0.5–1.0), `removeBackground` (bool). `202` + job, `400`, `413`, `503 queue_full` |
| GET | `/internal/jobs/{id}` | Job status, stage, progress, artifacts, mesh stats (model units) |
| GET | `/internal/jobs/{id}/artifacts/{name}` | `name` ∈ {`mesh.glb`, `input.png`} only |
| POST | `/internal/jobs/{id}/cancel` | `202`, `409` if finished |
| DELETE | `/internal/jobs/{id}` | Removes the job and its files |

Jobs live in memory; a restart loses them (the API then fails the job with `inference_lost`).
