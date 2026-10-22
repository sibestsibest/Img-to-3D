// Mirrors ImageTo3D.Api reference-search DTOs (src/backend/ImageTo3D.Api/DTOs/ReferenceDtos.cs).

import type { Issue } from './partDesign'

export type ReferenceDecision = 'pending' | 'accepted' | 'rejected'

export type ReferenceImageKind = 'unknown' | 'photo' | 'render' | 'drawing'

/** How a picture relates to the object being modelled; a similar look is not proof of the same object. */
export type ReferenceRelation = 'unknown' | 'same_object' | 'variant' | 'similar_different'

export type SourceStatus = 'ok' | 'empty' | 'failed' | 'timeout' | 'rate_limited' | 'disabled'

export type DownloadStatus = 'downloaded' | 'failed' | 'skipped'

export interface ReferenceSource {
  id: string
  name: string
  description: string
  enabled: boolean
  access: 'free_no_account'
}

/** Permissions are null when the licence is unknown: then the picture is for personal reference only. */
export interface ReferenceLicense {
  code: string
  label: string
  url: string | null
  raw: string | null
  commercialUse: boolean | null
  derivatives: boolean | null
  attributionRequired: boolean | null
}

export interface ReferenceIssue extends Omit<Issue, 'source'> {
  source: 'licence' | 'download' | 'duplicates'
}

export interface ReferenceCandidate {
  id: string
  origin: 'search' | 'manual'
  source: string
  sourceName: string
  title: string
  /** Web page describing the item (not an image). */
  pageUrl: string | null
  /** Direct address of the full image, when the source gives one. */
  imageUrl: string | null
  author: string | null
  license: ReferenceLicense
  attribution: string
  query: string | null
  retrievedAt: string
  downloadStatus: DownloadStatus
  downloadError: string | null
  hasThumbnail: boolean
  thumbnailWidth: number | null
  thumbnailHeight: number | null
  width: number | null
  height: number | null
  duplicateOfId: string | null
  duplicateKind: 'exact' | 'near' | null
  matchesInput: boolean
  decision: ReferenceDecision
  imageKind: ReferenceImageKind
  relation: ReferenceRelation
  note: string | null
  warnings: ReferenceIssue[]
  ranking: CandidateRanking
}

export interface SourceSearchResult {
  source: string
  name: string
  status: SourceStatus
  hits: number
  added: number
  fromCache: boolean
  message: string | null
}

export interface ManualSearchLink {
  name: string
  url: string
  kind: 'images' | 'reference' | 'shop' | 'models'
}

export interface ReferenceSession {
  id: string
  name: string
  query: string
  sourceJobId: string | null
  hasInputImage: boolean
  inputWidth: number | null
  inputHeight: number | null
  createdAt: string
  updatedAt: string
  lastSearchedAt: string | null
  lastSearch: SourceSearchResult[]
  manualLinks: ManualSearchLink[]
  candidates: ReferenceCandidate[]
  ranking: SessionRanking
  lastModelSearch: SourceSearchResult[]
  models: ModelCandidate[]
}

export interface ReferenceSessionSummary {
  id: string
  name: string
  query: string
  hasInputImage: boolean
  candidateCount: number
  acceptedCount: number
  createdAt: string
  updatedAt: string
}

export interface UpdateCandidateRequest {
  decision: ReferenceDecision
  imageKind?: ReferenceImageKind
  relation?: ReferenceRelation
  note?: string | null
}

export interface ManualPicture {
  file: File
  title?: string
  author?: string
  pageUrl?: string
  license?: string
  licenseUrl?: string
}

export interface ClearCacheResult {
  searchEntries: number
  files: number
  bytes: number
  embeddings: number
}

/** Advisory label with its probability or capped confidence; `uncertain` when no label is clearly ahead. */
export interface ZeroShot {
  label: string
  confidence: number
}

export interface RankReason {
  code: string
  message: string
  effect: '+' | '-' | '0'
}

/** Ranking of one picture: all values are advisory, never a claim that two pictures show the same object. */
export interface CandidateRanking {
  position: number | null
  score: number | null
  confidence: number | null
  group: number | null
  querySimilarity: number | null
  inputSimilarity: number | null
  viewpoint: ZeroShot | null
  kind: ZeroShot | null
  relation: ZeroShot | null
  reasons: RankReason[]
}

export interface SessionRanking {
  /** `embeddings` (visual and query similarity used) or `basic`; null before the first ranking. */
  mode: 'embeddings' | 'basic' | null
  rankedAt: string | null
  note: string | null
  inputViewpoint: ZeroShot | null
  embeddingModel: string
}

export interface ModelGeometry {
  triangles: number
  components: number
  closed: boolean
  boundaryEdges: number
  nonManifoldEdges: number
  degenerateTriangles: number
  sizeXMm: number
  sizeYMm: number
  sizeZMm: number
  scaleStatus: 'plausible' | 'unitless' | 'suspicious'
}

export interface ModelCandidate {
  id: string
  origin: 'search' | 'manual'
  source: string
  sourceName: string
  title: string
  pageUrl: string | null
  author: string | null
  license: ReferenceLicense
  attribution: string
  query: string | null
  retrievedAt: string
  isDownloadable: boolean | null
  faceCount: number | null
  vertexCount: number | null
  fileBytes: number | null
  hasThumbnail: boolean
  downloadStatus: DownloadStatus
  format: 'glb' | 'stl' | null
  /** `metadata_only`: judged from the listing; `geometry_checked`: the uploaded mesh was analysed. */
  evaluationStatus: 'metadata_only' | 'geometry_checked'
  score: number | null
  confidence: number | null
  visualSimilarity: number | null
  reasons: RankReason[]
  geometry: ModelGeometry | null
  decision: ReferenceDecision
  note: string | null
  warnings: ReferenceIssue[]
}

export interface ReferenceCapabilities {
  embeddings: { available: boolean; model: string; reason: string | null }
  imageSources: ReferenceSource[]
  modelSources: ReferenceSource[]
}

export interface ModelUpload {
  file: File
  title?: string
  author?: string
  pageUrl?: string
  license?: string
}

export type ReconstructionRoute =
  | 'reuse_existing_model'
  | 'multi_view_reconstruction'
  | 'hybrid_reconstruction'
  | 'single_image_reconstruction'
  | 'manual_geometry_required'

export type ReconstructionState = 'reference_search' | 'model_candidate_review' | ReconstructionRoute | 'completed' | 'failed' | 'cancelled'

export interface RouteReason {
  code: string
  /** `blocker`: the route cannot start because of it. */
  severity: 'info' | 'warning' | 'blocker'
  message: string
}

export interface RouteOption {
  route: ReconstructionRoute
  /** `unsupported`: no installed engine can do it; `unavailable`: not with what the search has now. */
  availability: 'available' | 'unavailable' | 'unsupported'
  recommended: boolean
  reasons: RouteReason[]
  modelId: string | null
  extraPhotos: number
}

/** GET/POST /api/references/{id}/reconstruction: routes in preference order and what was started. */
export interface Reconstruction {
  state: ReconstructionState
  route: ReconstructionRoute | null
  startedAt: string | null
  jobId: string | null
  jobStatus: 'Pending' | 'Processing' | 'Succeeded' | 'Failed' | 'Cancelled' | null
  designId: string | null
  modelId: string | null
  extraReferenceIds: string[]
  message: string | null
  recommended: ReconstructionRoute
  routes: RouteOption[]
  notes: RouteReason[]
  engine: string | null
  engineReachable: boolean
}
