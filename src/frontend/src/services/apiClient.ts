import type { ExtraPhoto, Job, PagedResult, ProblemDetails, ScaleAxis } from '../types/api'
import type { DrawingReport, PartDesignProject, PartDesignSummary, SplitPlane, SplitPreview, UpdatePartRequest } from '../types/partDesign'
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
  undo: (id: string) => request<PartDesignProject>(designPath(id, '/undo'), { method: 'POST' }),
  redo: (id: string) => request<PartDesignProject>(designPath(id, '/redo'), { method: 'POST' }),
}

/** Relative URLs only: the browser never learns internal service addresses or file paths. */
export const urls = {
  /** Part geometry is immutable per part ID, so the URL needs no cache-busting. */
  partGeometry: (projectId: string, partId: string) => `${BASE}${partPath(projectId, partId, '/geometry')}`,
  /** Drawings depend on editable metadata, so <version> (the project's updatedAt) busts the browser cache. */
  drawing: (projectId: string, partId: string, format: 'svg' | 'pdf', version: string, download = false) =>
    `${BASE}${partPath(projectId, partId, '/drawing')}?format=${format}${download ? '&download=true' : ''}&v=${encodeURIComponent(version)}`,
  preview: (job: Job) => `${BASE}/models/${job.id}/preview?v=${job.scale?.mmPerModelUnit ?? 0}`,
  download: (job: Job, format: 'glb' | 'stl') => `${BASE}/models/${job.id}/download?format=${format}`,
  image: (id: string, kind: 'source' | 'preprocessed') => `${BASE}/models/${id}/image?kind=${kind}`,
}
