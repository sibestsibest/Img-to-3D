import type { Collision, CreateRelationRequest, DesignPart, Fit, PartRelation, RelationKind, RelationRequest } from '../types/partDesign'

// ---- Relations ----

export const RELATION_LABELS: Record<RelationKind, string> = {
  glued: 'Glued',
  peg_in_hole: 'Peg in hole',
  tube_over_rod: 'Tube over rod',
  nested: 'Nested',
  slot: 'Slot',
}

/** What part A and part B are for each kind (the server checks fits with these roles). */
export const RELATION_ROLES: Record<RelationKind, { a: string; b: string }> = {
  glued: { a: 'Part', b: 'Glued to' },
  peg_in_hole: { a: 'Peg (rod or tube)', b: 'Part with the hole' },
  tube_over_rod: { a: 'Tube (outside)', b: 'Rod or tube inside' },
  nested: { a: 'Outer part', b: 'Inner part' },
  slot: { a: 'Part with the slot', b: 'Sheet in the slot' },
}

/** Kinds whose fit needs an opening the part sizes do not give: hole diameter or slot width. */
export function openingLabel(kind: RelationKind): string | null {
  return kind === 'peg_in_hole' ? 'Hole diameter' : kind === 'slot' ? 'Slot width' : null
}

export interface RelationForm {
  partAId: string
  partBId: string
  kind: RelationKind
  allowOverlap: boolean
  openingSizeMm: string
  note: string
}

export const emptyRelationForm = (partAId = '', partBId = ''): RelationForm => ({
  partAId,
  partBId,
  kind: 'glued',
  allowOverlap: false,
  openingSizeMm: '',
  note: '',
})

export function formFromRelation(r: PartRelation): RelationForm {
  return { partAId: r.partAId, partBId: r.partBId, kind: r.kind, allowOverlap: r.allowOverlap, openingSizeMm: r.openingSizeMm == null ? '' : String(r.openingSizeMm), note: r.note ?? '' }
}

/** Same limits as the API (opening 0.01–1000 mm, note up to 500 characters). Decimal commas are accepted. */
export function toRelationRequest(form: RelationForm): { request: CreateRelationRequest } | { error: string } {
  if (!form.partAId || !form.partBId) return { error: 'Choose both parts.' }
  if (form.partAId === form.partBId) return { error: 'Choose two different parts.' }
  const body = toRelationBody(form)
  return 'error' in body ? body : { request: { partAId: form.partAId, partBId: form.partBId, ...body.request } }
}

export function toRelationBody(form: RelationForm): { request: RelationRequest } | { error: string } {
  let openingSizeMm: number | null = null
  const raw = form.openingSizeMm.trim().replace(',', '.')
  if (raw !== '' && openingLabel(form.kind)) {
    const value = Number(raw)
    if (!Number.isFinite(value) || value < 0.01 || value > 1000) return { error: `${openingLabel(form.kind)} must be between 0.01 and 1000 mm.` }
    openingSizeMm = value
  }
  const note = form.note.trim()
  if (note.length > 500) return { error: 'Use at most 500 characters for the note.' }
  return { request: { kind: form.kind, allowOverlap: form.allowOverlap, openingSizeMm, note: note || null } }
}

export function fitSummary(fit: Fit): string {
  switch (fit.status) {
    case 'not_applicable':
      return 'No fit to check'
    case 'unknown':
      return 'Fit unknown: a size is missing'
    default: {
      const c = fit.clearanceMm ?? 0
      const what = c < 0 ? `${formatMm(-c)} mm interference` : `${formatMm(c)} mm clearance`
      const verdict = fit.status === 'ok' ? 'OK' : fit.status === 'too_tight' ? 'too tight' : 'too loose'
      return `${what}, ${verdict}${fit.basis === 'estimated' ? ' (from estimates)' : ''}`
    }
  }
}

const formatMm = (v: number) => v.toLocaleString(undefined, { maximumFractionDigits: 3 })

// ---- Collisions ----

export const CONTACT_LABELS: Record<Collision['contact'], string> = {
  intersecting: 'Intersect',
  contained: 'Inside',
  coincident: 'Same place',
  touching: 'Touch',
  gap: 'Not touching',
}

/** Problems first (error, warning), then info; touching pairs without a relation last. */
export function sortCollisions(collisions: readonly Collision[]): Collision[] {
  const rank = (c: Collision) => (c.severity === 'error' ? 0 : c.severity === 'warning' ? 1 : c.code === 'parts_touch' ? 3 : 2)
  return [...collisions].sort((a, b) => rank(a) - rank(b))
}

// ---- Exploded view ----

/**
 * Display-only offsets (metres, for the viewer) that move each part away from the centre of the
 * assembly along the line from that centre to the part's own centre. <factor> 0 is the assembled
 * position; 1 doubles every part's distance from the centre. Geometry never changes.
 */
export function explodedOffsets(parts: readonly DesignPart[], factor: number): Map<string, [number, number, number]> {
  const offsets = new Map<string, [number, number, number]>()
  if (parts.length === 0) return offsets
  const min = [Infinity, Infinity, Infinity]
  const max = [-Infinity, -Infinity, -Infinity]
  const centres = parts.map((p) => {
    const b = p.geometry.bounds
    const c = [(b.min.x + b.max.x) / 2, (b.min.y + b.max.y) / 2, (b.min.z + b.max.z) / 2]
    ;[b.min.x, b.min.y, b.min.z].forEach((v, i) => (min[i] = Math.min(min[i], v)))
    ;[b.max.x, b.max.y, b.max.z].forEach((v, i) => (max[i] = Math.max(max[i], v)))
    return c
  })
  const centre = [0, 1, 2].map((i) => (min[i] + max[i]) / 2)
  parts.forEach((p, n) => {
    const c = centres[n]
    offsets.set(p.id, [0, 1, 2].map((i) => ((c[i] - centre[i]) * factor) / 1000 + 0) as [number, number, number])
  })
  return offsets
}
