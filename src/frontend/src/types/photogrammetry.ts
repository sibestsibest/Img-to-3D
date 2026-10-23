// Mirrors ImageTo3D.Api photogrammetry DTOs (src/backend/ImageTo3D.Api/DTOs/PhotogrammetryDtos.cs).

export type PhotogrammetryStatus = 'draft' | 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled'

export interface PhotoFinding {
  code: string
  severity: 'info' | 'warning' | 'blocker'
  message: string
  /** The photo it is about; null for the whole set. */
  photoId: string | null
}

export interface PhotogrammetryPhoto {
  id: string
  sortOrder: number
  originalName: string
  format: 'jpg' | 'png'
  width: number
  height: number
  sizeBytes: number
  sharpness: number
  hasExif: boolean
  thumbnailUrl: string
}

export interface MeshInspection {
  vertices: number
  triangles: number
  closed: boolean
  boundaryEdges: number
  nonManifoldEdges: number
  degenerateTriangles: number
  components: number
}

export interface PhotogrammetryJob {
  id: string
  name: string
  status: PhotogrammetryStatus
  pipeline: string | null
  /** Meshroom does not announce a total, so only finished nodes and the current one are known. */
  progress: { nodesDone: number | null; currentNode: string | null; elapsedSeconds: number | null }
  errorCode: string | null
  error: string | null
  createdAt: string
  startedAt: string | null
  completedAt: string | null
  engineVersion: string | null
  sourceReferenceSessionId: string | null
  photos: PhotogrammetryPhoto[]
  findings: PhotoFinding[]
  canStart: boolean
  mesh: MeshInspection | null
  dimensionsMm: { x: number; y: number; z: number } | null
  scale: { status: 'uncalibrated' | 'calibrated'; axis: string | null; targetMm: number | null; mmPerModelUnit: number | null }
  downloads: { glb: boolean; stl: boolean; zip: boolean; log: boolean; stlError: string | null }
  outputs: { path: string; bytes: number }[]
  origin: 'photogrammetry'
  units: 'mm'
}

export interface PhotogrammetrySummary {
  id: string
  name: string
  status: PhotogrammetryStatus
  photoCount: number
  pipeline: string | null
  createdAt: string
  completedAt: string | null
  origin: 'photogrammetry'
}

export interface PhotogrammetryEngine {
  status: 'ready' | 'missing' | 'disabled' | 'failed'
  reason: string | null
  version: string | null
  pipelines: string[]
  defaultPipeline: string | null
  cuda: boolean
  gpu: string | null
  notes: string[]
  maxPhotos: number
  maxPhotoBytes: number
}

export type ScaleAxisName = 'X' | 'Y' | 'Z' | 'Longest'
