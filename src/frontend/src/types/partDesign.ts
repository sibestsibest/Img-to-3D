// Mirrors ImageTo3D.Api part-design DTOs (src/backend/ImageTo3D.Api/DTOs/PartDesignDtos.cs).
// All lengths are millimetres; coordinates are the viewer's (Y-up, centred on X/Z, resting on Y = 0).

export type PartCategory = 'sheet' | 'rod' | 'tube' | 'stock_part' | 'custom_3d' | 'needs_review'

export type ReviewStatus = 'needs_review' | 'user_confirmed'

export type PartState = 'Active' | 'Removed' | 'Superseded'

export type ValidationStatus = 'valid' | 'warning' | 'invalid'

export interface Issue {
  code: string
  severity: 'info' | 'warning' | 'error'
  message: string
  source: 'geometry' | 'manufacturing' | 'analysis' | 'split'
}

export interface Vec3 {
  x: number
  y: number
  z: number
}

export interface Bounds {
  min: Vec3
  max: Vec3
  size: Vec3
}

export interface GeometryReport {
  vertices: number
  triangles: number
  closed: boolean
  boundaryEdges: number
  nonManifoldEdges: number
  inconsistentEdges: number
  degenerateTriangles: number
  components: number
  surfaceAreaMm2: number
  volumeMm3: number | null
  bounds: Bounds
  orientedExtentsMm: Vec3
  issues: Issue[]
}

export interface ManufacturingSpec {
  material: string | null
  manufacturingMethod: string | null
  quantity: number
  thicknessMm: number | null
  diameterMm: number | null
  outerDiameterMm: number | null
  innerDiameterMm: number | null
  cutLengthMm: number | null
  cutAngleDeg: number | null
  fitAllowanceMm: number | null
  stockDescription: string | null
  stockSource: string | null
  needsFurtherModelling: boolean
  notes: string | null
}

export interface DesignPart {
  id: string
  parentPartId: string | null
  sortOrder: number
  name: string
  origin: 'node' | 'component' | 'fragments' | 'split' | 'duplicate'
  sourceNodeName: string | null
  state: PartState
  category: PartCategory
  reviewStatus: ReviewStatus
  suggestion: { category: PartCategory; confidence: number; reasons: string[] }
  estimated: {
    lengthMm: number
    widthMm: number
    thicknessMm: number
    outerDiameterMm: number | null
    innerDiameterMm: number | null
    status: 'estimated'
  }
  confirmed: { lengthMm: number | null; widthMm: number | null; heightMm: number | null; status: 'user_confirmed' | null }
  spec: ManufacturingSpec
  geometry: GeometryReport
  validation: { status: ValidationStatus; issues: Issue[] }
  units: 'mm'
}

export interface Operation {
  sequence: number
  kind: string
  summary: string
  undone: boolean
  createdAt: string
}

export type DesignSourceKind = 'generation_job' | 'reference_model' | 'none'

/** Where a project's source came from; a reused model keeps its licence and attribution. */
export interface DesignSource {
  kind: DesignSourceKind
  jobId: string | null
  referenceSessionId: string | null
  modelCandidateId: string | null
  fileName: string
  license: string | null
  attribution: string | null
}

export interface PartDesignProject {
  id: string
  name: string
  sourceJobId: string | null
  sourceFileName: string
  source: DesignSource
  mmPerModelUnit: number
  scaleStatus: 'estimated'
  coordinateSystem: string
  fitAllowanceMinMm: number
  fitAllowanceMaxMm: number
  createdAt: string
  updatedAt: string
  issues: Issue[]
  parts: DesignPart[]
  history: Operation[]
  canUndo: boolean
  canRedo: boolean
  units: 'mm'
}

export interface PartDesignSummary {
  id: string
  name: string
  sourceJobId: string | null
  sourceKind: DesignSourceKind
  sourceFileName: string
  partCount: number
  createdAt: string
  updatedAt: string
}

/** Body of PUT /api/part-designs/{id}/parts/{partId}: a full replacement of the editable fields. */
export interface UpdatePartRequest {
  name: string
  category: PartCategory
  reviewStatus: ReviewStatus
  units: 'mm'
  material: string | null
  manufacturingMethod: string | null
  quantity: number
  thicknessMm: number | null
  diameterMm: number | null
  outerDiameterMm: number | null
  innerDiameterMm: number | null
  cutLengthMm: number | null
  cutAngleDeg: number | null
  fitAllowanceMm: number | null
  confirmedLengthMm: number | null
  confirmedWidthMm: number | null
  confirmedHeightMm: number | null
  stockDescription: string | null
  stockSource: string | null
  needsFurtherModelling: boolean
  notes: string | null
}

export interface DrawingHole {
  label: string
  centreXMm: number
  centreYMm: number
  diameterMm: number | null
  widthMm: number
  heightMm: number
}

/** GET .../parts/{partId}/drawing/report: what the 1:1 drawing contains and the checks run on it. */
export interface DrawingReport {
  page: string
  pageWidthMm: number
  pageHeightMm: number
  scale: '1:1'
  outlineWidthMm: number
  outlineHeightMm: number
  thicknessMm: number
  thicknessStatus: 'estimated' | 'specified'
  measuredThicknessMm: number
  outlineCount: number
  holes: DrawingHole[]
  issues: Issue[]
  formats: DrawingFormat[]
  units: 'mm'
}

export type PrimitiveKind = 'plate' | 'box' | 'rod' | 'tube'

/** Body of POST /api/part-designs/{id}/parts/primitive; sizes in mm, fields used depend on the kind. */
export interface AddPrimitiveRequest {
  kind: PrimitiveKind
  units: 'mm'
  name?: string
  material?: string
  lengthMm?: number
  widthMm?: number
  heightMm?: number
  thicknessMm?: number
  diameterMm?: number
  outerDiameterMm?: number
  innerDiameterMm?: number
  axis?: 'x' | 'y' | 'z'
  center?: [number, number, number]
}

export interface SplitPlane {
  point: [number, number, number]
  normal: [number, number, number]
  allowOpenResult?: boolean
}

export interface SplitSide {
  triangles: number
  capped: boolean
  closed: boolean
  capAreaMm2: number
  volumeMm3: number | null
  bounds: Bounds
}

export interface SplitPreview {
  completeSolidSplit: boolean
  closedContours: number
  openContours: number
  contourLengthMm: number
  below: SplitSide | null
  above: SplitSide | null
  issues: Issue[]
}

export type DrawingFormat = 'svg' | 'pdf' | 'dxf'

export interface ManifestFile {
  path: string
  kind: 'drawing-pdf' | 'drawing-svg' | 'drawing-dxf' | 'stl' | 'bom' | 'cut-list' | 'readme'
  partId: string | null
  bytes: number
  sha256: string
}

export interface ManifestPart {
  item: number
  id: string
  name: string
  category: string
  reviewStatus: string
  validationStatus: 'valid' | 'warning' | 'invalid'
  quantity: number
  files: string[]
}

export interface ManifestWarning {
  code: string
  severity: 'warning' | 'error'
  message: string
  partId: string | null
}

/** manifest.json of an export package; lengths in mm. */
export interface ExportManifest {
  schemaVersion: number
  generator: string
  kind: 'draft' | 'release'
  revision: number | null
  note: string | null
  createdAt: string
  units: 'mm'
  coordinateSystem: string
  project: { id: string; name: string; historySequence: number; updatedAt: string }
  parts: ManifestPart[]
  files: ManifestFile[]
  warnings: ManifestWarning[]
}

/** A stored, numbered export package; its zip never changes. */
export interface DesignRelease {
  id: string
  revision: number
  note: string | null
  sizeBytes: number
  sha256: string
  historySequence: number
  partCount: number
  fileCount: number
  warningCount: number
  createdAt: string
}
