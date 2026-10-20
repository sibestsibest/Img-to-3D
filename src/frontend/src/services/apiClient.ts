import type { Job, PagedResult, ProblemDetails, ScaleAxis } from '../types/api'

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
  let response: Response
  try {
    response = await fetch(`${BASE}${path}`, { credentials: 'same-origin', ...init })
  } catch {
    throw new ApiError(0, 'network_error', 'Cannot reach the server. Check that the API is running.')
  }
  if (!response.ok) throw await toApiError(response)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export const api = {
  createJob(file: File, idempotencyKey: string): Promise<Job> {
    const body = new FormData()
    body.append('image', file)
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

/** Relative URLs only: the browser never learns internal service addresses or file paths. */
export const urls = {
  preview: (job: Job) => `${BASE}/models/${job.id}/preview?v=${job.scale?.mmPerModelUnit ?? 0}`,
  download: (job: Job, format: 'glb' | 'stl') => `${BASE}/models/${job.id}/download?format=${format}`,
  image: (id: string, kind: 'source' | 'preprocessed') => `${BASE}/models/${id}/image?kind=${kind}`,
}
