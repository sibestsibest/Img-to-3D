import type { Bounds, DesignPart, PartCategory, ReviewStatus, SplitPlane, UpdatePartRequest } from '../types/partDesign'

// ---- Viewport / tree state (selection, visibility, isolation) ----

export interface ViewState {
  selectedId: string | null
  hidden: ReadonlySet<string>
  isolatedId: string | null
}

export type ViewAction =
  | { type: 'select'; id: string | null }
  | { type: 'toggleHidden'; id: string }
  | { type: 'isolate'; id: string | null }
  | { type: 'showAll' }
  /** Drop references to parts that are no longer active (after split, delete, undo). */
  | { type: 'sync'; activeIds: readonly string[] }

export const initialViewState: ViewState = { selectedId: null, hidden: new Set(), isolatedId: null }

export function viewReducer(state: ViewState, action: ViewAction): ViewState {
  switch (action.type) {
    case 'select':
      return state.selectedId === action.id ? state : { ...state, selectedId: action.id }
    case 'toggleHidden': {
      const hidden = new Set(state.hidden)
      if (hidden.has(action.id)) hidden.delete(action.id)
      else hidden.add(action.id)
      // Hiding the isolated part ends isolation instead of leaving an empty viewport.
      return { ...state, hidden, isolatedId: state.isolatedId === action.id ? null : state.isolatedId }
    }
    case 'isolate':
      return { ...state, isolatedId: state.isolatedId === action.id ? null : action.id, selectedId: action.id ?? state.selectedId }
    case 'showAll':
      return { ...state, hidden: new Set(), isolatedId: null }
    case 'sync': {
      const active = new Set(action.activeIds)
      const hidden = new Set([...state.hidden].filter((id) => active.has(id)))
      const selectedId = state.selectedId && active.has(state.selectedId) ? state.selectedId : null
      const isolatedId = state.isolatedId && active.has(state.isolatedId) ? state.isolatedId : null
      if (hidden.size === state.hidden.size && selectedId === state.selectedId && isolatedId === state.isolatedId) return state
      return { selectedId, hidden, isolatedId }
    }
  }
}

export function isVisible(state: ViewState, id: string): boolean {
  if (state.isolatedId) return state.isolatedId === id
  return !state.hidden.has(id)
}

// ---- Part tree (provenance: splits and duplicates hang under the part they came from) ----

export interface TreeNode {
  part: DesignPart
  children: TreeNode[]
}

export function buildTree(parts: readonly DesignPart[]): TreeNode[] {
  const shown = parts.filter((p) => p.state !== 'Removed')
  const byId = new Map(shown.map((p) => [p.id, { part: p, children: [] as TreeNode[] }]))
  const roots: TreeNode[] = []
  for (const p of [...shown].sort((a, b) => a.sortOrder - b.sortOrder)) {
    const node = byId.get(p.id)!
    const parent = p.parentPartId ? byId.get(p.parentPartId) : undefined
    if (parent) parent.children.push(node)
    else roots.push(node)
  }
  return roots
}

export const activeParts = (parts: readonly DesignPart[]) => parts.filter((p) => p.state === 'Active')

// ---- Cutting plane ----

export type Axis = 'x' | 'y' | 'z'

const AXIS_INDEX: Record<Axis, 0 | 1 | 2> = { x: 0, y: 1, z: 2 }

export function axisRange(bounds: Bounds, axis: Axis): [number, number] {
  return [bounds.min[axis], bounds.max[axis]]
}

/** Axis-aligned plane at <offsetMm> (a design coordinate, not relative to the part). */
export function planeFor(axis: Axis, offsetMm: number, flipped = false): SplitPlane {
  const point: [number, number, number] = [0, 0, 0]
  const normal: [number, number, number] = [0, 0, 0]
  point[AXIS_INDEX[axis]] = offsetMm
  normal[AXIS_INDEX[axis]] = flipped ? -1 : 1
  return { point, normal }
}

// ---- Properties form ----

export const CATEGORY_LABELS: Record<PartCategory, string> = {
  needs_review: 'Needs review',
  sheet: 'Sheet (PS plate)',
  rod: 'Rod / strip',
  tube: 'Tube / pipe',
  stock_part: 'Stock part',
  custom_3d: 'Custom 3D',
}

export interface PartForm {
  name: string
  category: PartCategory
  reviewStatus: ReviewStatus
  material: string
  manufacturingMethod: string
  quantity: string
  thicknessMm: string
  diameterMm: string
  outerDiameterMm: string
  innerDiameterMm: string
  cutLengthMm: string
  cutAngleDeg: string
  fitAllowanceMm: string
  confirmedLengthMm: string
  confirmedWidthMm: string
  confirmedHeightMm: string
  stockDescription: string
  stockSource: string
  needsFurtherModelling: boolean
  notes: string
}

export type NumericField =
  | 'thicknessMm'
  | 'diameterMm'
  | 'outerDiameterMm'
  | 'innerDiameterMm'
  | 'cutLengthMm'
  | 'cutAngleDeg'
  | 'fitAllowanceMm'
  | 'confirmedLengthMm'
  | 'confirmedWidthMm'
  | 'confirmedHeightMm'

/** Same limits as the API's request validation; cross-field checks are reported by the server. */
export const NUMERIC_LIMITS: Record<NumericField, { min: number; max: number; label: string }> = {
  thicknessMm: { min: 0.01, max: 1000, label: 'Thickness' },
  diameterMm: { min: 0.01, max: 1000, label: 'Diameter' },
  outerDiameterMm: { min: 0.01, max: 1000, label: 'Outer diameter' },
  innerDiameterMm: { min: 0, max: 1000, label: 'Inner diameter' },
  cutLengthMm: { min: 0.01, max: 10000, label: 'Cut length' },
  cutAngleDeg: { min: -89, max: 89, label: 'Cut angle' },
  fitAllowanceMm: { min: -10, max: 10, label: 'Fit allowance' },
  confirmedLengthMm: { min: 0.01, max: 10000, label: 'Measured length' },
  confirmedWidthMm: { min: 0.01, max: 10000, label: 'Measured width' },
  confirmedHeightMm: { min: 0.01, max: 10000, label: 'Measured height' },
}

/** Manufacturing fields relevant to each category (the rest stay stored but hidden). */
export const CATEGORY_FIELDS: Record<PartCategory, NumericField[]> = {
  needs_review: [],
  sheet: ['thicknessMm'],
  rod: ['diameterMm', 'cutLengthMm', 'cutAngleDeg'],
  tube: ['outerDiameterMm', 'innerDiameterMm', 'cutLengthMm', 'cutAngleDeg'],
  stock_part: [],
  custom_3d: [],
}

const text = (v: number | null) => (v == null ? '' : String(v))

export function formFromPart(part: DesignPart): PartForm {
  const s = part.spec
  return {
    name: part.name,
    category: part.category,
    reviewStatus: part.reviewStatus,
    material: s.material ?? '',
    manufacturingMethod: s.manufacturingMethod ?? '',
    quantity: String(s.quantity),
    thicknessMm: text(s.thicknessMm),
    diameterMm: text(s.diameterMm),
    outerDiameterMm: text(s.outerDiameterMm),
    innerDiameterMm: text(s.innerDiameterMm),
    cutLengthMm: text(s.cutLengthMm),
    cutAngleDeg: text(s.cutAngleDeg),
    fitAllowanceMm: text(s.fitAllowanceMm),
    confirmedLengthMm: text(part.confirmed.lengthMm),
    confirmedWidthMm: text(part.confirmed.widthMm),
    confirmedHeightMm: text(part.confirmed.heightMm),
    stockDescription: s.stockDescription ?? '',
    stockSource: s.stockSource ?? '',
    needsFurtherModelling: s.needsFurtherModelling,
    notes: s.notes ?? '',
  }
}

export interface ParsedForm {
  request: UpdatePartRequest | null
  errors: Partial<Record<keyof PartForm, string>>
}

export function parseForm(form: PartForm): ParsedForm {
  const errors: ParsedForm['errors'] = {}
  const name = form.name.trim()
  if (!name) errors.name = 'Enter a name.'
  else if (name.length > 100) errors.name = 'Use at most 100 characters.'

  const quantity = Number(form.quantity)
  if (!Number.isInteger(quantity) || quantity < 1 || quantity > 10000) errors.quantity = 'Quantity must be a whole number from 1 to 10000.'

  const numbers = {} as Record<NumericField, number | null>
  for (const field of Object.keys(NUMERIC_LIMITS) as NumericField[]) {
    const raw = form[field].trim()
    if (raw === '') {
      numbers[field] = null
      continue
    }
    const value = Number(raw)
    const { min, max, label } = NUMERIC_LIMITS[field]
    if (!Number.isFinite(value) || value < min || value > max) {
      errors[field] = `${label} must be between ${min} and ${max}.`
      numbers[field] = null
    } else {
      numbers[field] = value
    }
  }

  if (Object.keys(errors).length > 0) return { request: null, errors }
  const optional = (v: string) => (v.trim() === '' ? null : v.trim())
  return {
    errors,
    request: {
      name,
      category: form.category,
      reviewStatus: form.reviewStatus,
      units: 'mm',
      material: optional(form.material),
      manufacturingMethod: optional(form.manufacturingMethod),
      quantity,
      ...numbers,
      stockDescription: optional(form.stockDescription),
      stockSource: optional(form.stockSource),
      needsFurtherModelling: form.needsFurtherModelling,
      notes: optional(form.notes),
    },
  }
}

/** "1 234.5 mm²"-style figures for geometry facts. */
export function formatArea(mm2: number): string {
  return `${mm2.toLocaleString(undefined, { maximumFractionDigits: 1 })} mm²`
}

export function formatVolume(mm3: number | null): string {
  return mm3 == null ? 'n/a (open surface)' : `${mm3.toLocaleString(undefined, { maximumFractionDigits: 1 })} mm³`
}
