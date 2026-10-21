import type { ExtraPhoto, Job, PagedResult, ProblemDetails, ScaleAxis } from '../types/api'
import type {
  DesignRelease,
  DrawingFormat,
  DrawingReport,
  ExportManifest,
  PartDesignProject,
  PartDesignSummary,
  SplitPlane,
  SplitPreview,
  UpdatePartRequest,
} from '../types/partDesign'
import type {
  ClearCacheResult,
  ManualPicture,
  ReferenceSession,
  ReferenceSessionSummary,
  ReferenceSource,
  UpdateCandidateRequest,
} from '../types/references'
import { apiLog, formatBody, summarize } from './apiLog'

/** Error carrying the backend's stable error code and a user-presentable message. */
export class ApiError extends Error {
  readonly status: number
  readonly code: string

  constructor(status: number, code: string, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
  }
}

const BASE = '/api'

export async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails = {}
  try {
    problem = (await response.json()) as ProblemDetails
  } catch {
    // Non-JSON error body (e.g. proxy error page): fall through to a generic message.
  }
  const validation = problem.errors ? Object.values(problem.errors).flat().join(' ') : ''
  const message =
    validation ||
    problem.detail ||
    (response.status === 502 || response.status === 504
      ? 'The server is unreachable. Is the API running?'
      : `Request failed (${response.status}).`)
  return new ApiError(response.status, problem.code ?? 'error', message)
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const url = `${BASE}${path}`
  const method = init?.method ?? 'GET'
  const started = performance.now()
  const log = (status: number, summary: string, body: unknown, response?: Response, bodyBytes?: number) =>
    apiLog.record({
      method,
      url,
      status,
      durationMs: Math.round(performance.now() - started),
      sizeBytes: (response && Number(response.headers.get('Content-Length'))) || bodyBytes || null,
      summary,
      correlationId: response?.headers.get('X-Correlation-ID') ?? null,
      body: formatBody(body),
      source: 'api',
    })

  apiLog.markClientRequest(url)
  let response: Response
  try {
    response = await fetch(url, { credentials: 'same-origin', ...init })
  } catch (e) {
    // A request cancelled by the page (e.g. switching jobs) is not a failure worth logging.
    if (!(e instanceof DOMException && e.name === 'AbortError')) log(0, 'network_error', undefined)
    throw new ApiError(0, 'network_error', 'Cannot reach the server. Check that the API is running.')
  }
  if (!response.ok) {
    const error = await toApiError(response.clone())
    const text = await response.text().catch(() => undefined)
    log(response.status, `${error.code}: ${error.message}`, text, response, text?.length)
    throw error
  }
  if (response.status === 204) {
    log(response.status, 'No Content', undefined, response)
    return undefined as T
  }
  const text = await response.text()
  const body = JSON.parse(text) as T
  log(response.status, summarize(body), body, response, new TextEncoder().encode(text).length)
  return body
}

export const api = {
  createJob(file: File, idempotencyKey: string, extras: ExtraPhoto[] = []): Promise<Job> {
    const body = new FormData()
    body.append('image', file)
    for (const extra of extras) {
      body.append('extraImages', extra.file)
      body.append('extraViews', extra.view)
    }
    return request<Job>('/models', { method: 'POST', body, headers: { 'Idempotency-Key': idempotencyKey } })
  },

  getJob: (id: string, signal?: AbortSignal) => request<Job>(`/models/${encodeURIComponent(id)}`, { signal }),

  listJobs: (page = 1, pageSize = 20) => request<PagedResult<Job>>(`/models?page=${page}&pageSize=${pageSize}`),

  scale: (id: string, axis: ScaleAxis, targetMm: number) =>
    request<Job>(`/models/${encodeURIComponent(id)}/scale`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ axis, targetMm }),
    }),

  cancel: (id: string) => request<Job>(`/models/${encodeURIComponent(id)}/cancel`, { method: 'POST' }),

  deleteJob: (id: string) => request<void>(`/models/${encodeURIComponent(id)}`, { method: 'DELETE' }),
}

const designPath = (id: string, rest = '') => `/part-designs/${encodeURIComponent(id)}${rest}`
const partPath = (id: string, partId: string, rest = '') => designPath(id, `/parts/${encodeURIComponent(partId)}${rest}`)
const json = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
})

export const designApi = {
  create: (sourceJobId: string, name?: string) => request<PartDesignProject>('/part-designs', json('POST', { sourceJobId, name })),
  list: (page = 1, pageSize = 50) => request<PagedResult<PartDesignSummary>>(`/part-designs?page=${page}&pageSize=${pageSize}`),
  get: (id: string, signal?: AbortSignal) => request<PartDesignProject>(designPath(id), { signal }),
  remove: (id: string) => request<void>(designPath(id), { method: 'DELETE' }),
  updatePart: (id: string, partId: string, body: UpdatePartRequest) => request<PartDesignProject>(partPath(id, partId), json('PUT', body)),
  duplicatePart: (id: string, partId: string) => request<PartDesignProject>(partPath(id, partId, '/duplicate'), { method: 'POST' }),
  removePart: (id: string, partId: string) => request<PartDesignProject>(partPath(id, partId), { method: 'DELETE' }),
  previewSplit: (id: string, partId: string, plane: SplitPlane) => request<SplitPreview>(partPath(id, partId, '/split/preview'), json('POST', plane)),
  split: (id: string, partId: string, plane: SplitPlane) => request<PartDesignProject>(partPath(id, partId, '/split'), json('POST', plane)),
  drawingReport: (id: string, partId: string, signal?: AbortSignal) => request<DrawingReport>(partPath(id, partId, '/drawing/report'), { signal }),
  exportManifest: (id: string, signal?: AbortSignal) => request<ExportManifest>(designPath(id, '/export/manifest'), { signal }),
  releases: (id: string, signal?: AbortSignal) => request<DesignRelease[]>(designPath(id, '/releases'), { signal }),
  createRelease: (id: string, note?: string) => request<DesignRelease>(designPath(id, '/releases'), json('POST', { note })),
  undo: (id: string) => request<PartDesignProject>(designPath(id, '/undo'), { method: 'POST' }),
  redo: (id: string) => request<PartDesignProject>(designPath(id, '/redo'), { method: 'POST' }),
}

const referencePath = (id: string, rest = '') => `/references/${encodeURIComponent(id)}${rest}`
const candidatePath = (id: string, candidateId: string, rest = '') => referencePath(id, `/candidates/${encodeURIComponent(candidateId)}${rest}`)

export const referenceApi = {
  sources: () => request<ReferenceSource[]>('/references/sources'),
  /** Start a session from an uploaded photo, from a generation job's photo, or from a query alone. */
  create(input: { name?: string; query?: string; image?: File; sourceJobId?: string }): Promise<ReferenceSession> {
    const body = new FormData()
    if (input.name) body.append('name', input.name)
    if (input.query) body.append('query', input.query)
    if (input.image) body.append('image', input.image)
    if (input.sourceJobId) body.append('sourceJobId', input.sourceJobId)
    // A multipart body needs at least one part.
    if (![...body.keys()].length) body.append('name', '')
    return request<ReferenceSession>('/references', { method: 'POST', body })
  },
  list: (page = 1, pageSize = 50) => request<PagedResult<ReferenceSessionSummary>>(`/references?page=${page}&pageSize=${pageSize}`),
  get: (id: string, signal?: AbortSignal) => request<ReferenceSession>(referencePath(id), { signal }),
  update: (id: string, name: string, query: string) => request<ReferenceSession>(referencePath(id), json('PUT', { name, query })),
  remove: (id: string) => request<void>(referencePath(id), { method: 'DELETE' }),
  /** Searches the given sources (all enabled ones when omitted); individual source failures come back as statuses. */
  search: (id: string, query: string, sources?: string[], signal?: AbortSignal) =>
    request<ReferenceSession>(referencePath(id, '/search'), { ...json('POST', { query, sources }), signal }),
  addPicture(id: string, picture: ManualPicture): Promise<ReferenceSession> {
    const body = new FormData()
    body.append('image', picture.file)
    for (const key of ['title', 'author', 'pageUrl', 'license', 'licenseUrl'] as const) {
      const value = picture[key]?.trim()
      if (value) body.append(key, value)
    }
    return request<ReferenceSession>(referencePath(id, '/candidates'), { method: 'POST', body })
  },
  updateCandidate: (id: string, candidateId: string, body: UpdateCandidateRequest) =>
    request<ReferenceSession>(candidatePath(id, candidateId), json('PUT', body)),
  removeCandidate: (id: string, candidateId: string) => request<ReferenceSession>(candidatePath(id, candidateId), { method: 'DELETE' }),
  clearCache: () => request<ClearCacheResult>('/references/cache', { method: 'DELETE' }),
}

/** Relative URLs only: the browser never learns internal service addresses or file paths. */
export const urls = {
  /** Part geometry is immutable per part ID, so the URL needs no cache-busting. */
  partGeometry: (projectId: string, partId: string) => `${BASE}${partPath(projectId, partId, '/geometry')}`,
  /** Drawings depend on editable metadata, so <version> (the project's updatedAt) busts the browser cache. */
  drawing: (projectId: string, partId: string, format: DrawingFormat, version: string, download = false) =>
    `${BASE}${partPath(projectId, partId, '/drawing')}?format=${format}${download ? '&download=true' : ''}&v=${encodeURIComponent(version)}`,
  /** Exports depend on editable metadata too, so <version> busts the cache. */
  partStl: (projectId: string, partId: string, version: string) => `${BASE}${partPath(projectId, partId, '/stl')}?v=${encodeURIComponent(version)}`,
  bom: (projectId: string, version: string) => `${BASE}${designPath(projectId, '/export/bom')}?v=${encodeURIComponent(version)}`,
  cutList: (projectId: string, version: string) => `${BASE}${designPath(projectId, '/export/cut-list')}?v=${encodeURIComponent(version)}`,
  draftPackage: (projectId: string, version: string) => `${BASE}${designPath(projectId, '/export/package')}?v=${encodeURIComponent(version)}`,
  /** Release packages never change, so the URL needs no cache-busting. */
  releasePackage: (projectId: string, revision: number) => `${BASE}${designPath(projectId, `/releases/${revision}/package`)}`,
  preview: (job: Job) => `${BASE}/models/${job.id}/preview?v=${job.scale?.mmPerModelUnit ?? 0}`,
  download: (job: Job, format: 'glb' | 'stl') => `${BASE}/models/${job.id}/download?format=${format}`,
  image: (id: string, kind: 'source' | 'preprocessed') => `${BASE}/models/${id}/image?kind=${kind}`,
  /** Stored previews never change for a given ID. */
  referenceThumbnail: (sessionId: string, candidateId: string) => `${BASE}${candidatePath(sessionId, candidateId, '/thumbnail')}`,
  referenceInput: (sessionId: string, size: 'thumbnail' | 'full' = 'thumbnail') => `${BASE}${referencePath(sessionId, '/input')}?size=${size}`,
}
