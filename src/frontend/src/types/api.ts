// Mirrors ImageTo3D.Api DTOs (src/backend/ImageTo3D.Api/DTOs/JobDtos.cs).

export type JobStatus = 'Pending' | 'Processing' | 'Succeeded' | 'Failed' | 'Cancelled'

export type ScaleAxis = 'X' | 'Y' | 'Z' | 'Longest'

/** Where an extra photo was taken from, relative to the main photo (the server re-validates). */
export type ExtraView = 'auto' | 'front' | 'back' | 'left' | 'right' | 'top' | 'bottom'

export interface ExtraPhoto {
  file: File
  view: ExtraView
}

export interface Dimensions {
  x: number
  y: number
  z: number
}

export interface Job {
  id: string
  status: JobStatus
  stage: string | null
  /** 0..1, only reported where the backend actually measures it. */
  stageProgress: number | null
  errorCode: string | null
  error: string | null
  createdAt: string
  startedAt: string | null
  completedAt: string | null
  originalFileName: string
  imageWidth: number
  imageHeight: number
  mesh: { vertices: number; triangles: number } | null
  /** Bounding box in millimetres: x = width, y = height (up), z = depth. */
  dimensionsMm: Dimensions | null
  scale: { axis: ScaleAxis; targetMm: number; mmPerModelUnit: number } | null
  downloads: { glb: boolean; stl: boolean; stlError: string | null }
}

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
}

export interface ProblemDetails {
  title?: string
  detail?: string
  status?: number
  code?: string
  errors?: Record<string, string[]>
}

export const TERMINAL_STATUSES: readonly JobStatus[] = ['Succeeded', 'Failed', 'Cancelled']

export const isTerminal = (status: JobStatus) => TERMINAL_STATUSES.includes(status)
