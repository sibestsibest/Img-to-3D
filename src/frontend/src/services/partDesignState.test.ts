import { describe, expect, it } from 'vitest'
import { parseRoute, routeHash } from '../hooks/useHashRoute'
import type { DesignPart } from '../types/partDesign'
import {
  activeParts,
  buildTree,
  formFromPart,
  initialViewState,
  isVisible,
  parseForm,
  planeFor,
  viewReducer,
  type ViewState,
} from './partDesignState'

function part(id: string, overrides: Partial<DesignPart> = {}): DesignPart {
  return {
    id,
    parentPartId: null,
    sortOrder: 0,
    name: `Part ${id}`,
    origin: 'node',
    sourceNodeName: null,
    state: 'Active',
    category: 'needs_review',
    reviewStatus: 'needs_review',
    suggestion: { category: 'custom_3d', confidence: 0.5, reasons: [] },
    estimated: { lengthMm: 100, widthMm: 50, thicknessMm: 25, outerDiameterMm: null, innerDiameterMm: null, status: 'estimated' },
    confirmed: { lengthMm: null, widthMm: null, heightMm: null, status: null },
    spec: {
      material: null,
      manufacturingMethod: null,
      quantity: 1,
      thicknessMm: null,
      diameterMm: null,
      outerDiameterMm: null,
      innerDiameterMm: null,
      cutLengthMm: null,
      cutAngleDeg: null,
      fitAllowanceMm: null,
      stockDescription: null,
      stockSource: null,
      needsFurtherModelling: false,
      notes: null,
    },
    geometry: {
      vertices: 8,
      triangles: 12,
      closed: true,
      boundaryEdges: 0,
      nonManifoldEdges: 0,
      inconsistentEdges: 0,
      degenerateTriangles: 0,
      components: 1,
      surfaceAreaMm2: 1,
      volumeMm3: 1,
      bounds: { min: { x: 0, y: 0, z: 0 }, max: { x: 1, y: 1, z: 1 }, size: { x: 1, y: 1, z: 1 } },
      orientedExtentsMm: { x: 1, y: 1, z: 1 },
      issues: [],
    },
    validation: { status: 'valid', issues: [] },
    units: 'mm',
    ...overrides,
  }
}

describe('viewReducer', () => {
  it('selects, toggles visibility and isolates', () => {
    let s: ViewState = viewReducer(initialViewState, { type: 'select', id: 'a' })
    expect(s.selectedId).toBe('a')

    s = viewReducer(s, { type: 'toggleHidden', id: 'b' })
    expect(isVisible(s, 'b')).toBe(false)
    expect(isVisible(s, 'a')).toBe(true)

    s = viewReducer(s, { type: 'isolate', id: 'c' })
    expect(s.selectedId).toBe('c')
    expect(isVisible(s, 'c')).toBe(true)
    expect(isVisible(s, 'a')).toBe(false)

    // Isolating the same part again ends isolation.
    s = viewReducer(s, { type: 'isolate', id: 'c' })
    expect(s.isolatedId).toBeNull()
    expect(isVisible(s, 'a')).toBe(true)
  })

  it('hiding the isolated part ends isolation', () => {
    let s = viewReducer(initialViewState, { type: 'isolate', id: 'a' })
    s = viewReducer(s, { type: 'toggleHidden', id: 'a' })
    expect(s.isolatedId).toBeNull()
    expect(isVisible(s, 'a')).toBe(false)
  })

  it('sync drops selection, hidden and isolated ids that are no longer active', () => {
    let s = viewReducer(initialViewState, { type: 'select', id: 'gone' })
    s = viewReducer(s, { type: 'toggleHidden', id: 'gone' })
    s = viewReducer(s, { type: 'toggleHidden', id: 'kept' })
    s = viewReducer(s, { type: 'sync', activeIds: ['kept'] })
    expect(s.selectedId).toBeNull()
    expect([...s.hidden]).toEqual(['kept'])
  })

  it('sync returns the same object when nothing changes', () => {
    const s = viewReducer(initialViewState, { type: 'select', id: 'a' })
    expect(viewReducer(s, { type: 'sync', activeIds: ['a'] })).toBe(s)
  })

  it('showAll clears hidden parts and isolation', () => {
    let s = viewReducer(initialViewState, { type: 'toggleHidden', id: 'a' })
    s = viewReducer(s, { type: 'isolate', id: 'b' })
    s = viewReducer(s, { type: 'showAll' })
    expect(isVisible(s, 'a')).toBe(true)
    expect(s.isolatedId).toBeNull()
  })
})

describe('buildTree', () => {
  it('nests split results under the superseded original and drops removed parts', () => {
    const parts = [
      part('a', { state: 'Superseded', sortOrder: 0 }),
      part('b', { sortOrder: 1 }),
      part('a1', { parentPartId: 'a', origin: 'split', sortOrder: 2 }),
      part('a2', { parentPartId: 'a', origin: 'split', sortOrder: 3 }),
      part('x', { state: 'Removed', sortOrder: 4 }),
    ]

    const tree = buildTree(parts)

    expect(tree.map((n) => n.part.id)).toEqual(['a', 'b'])
    expect(tree[0].children.map((n) => n.part.id)).toEqual(['a1', 'a2'])
    expect(activeParts(parts).map((p) => p.id)).toEqual(['b', 'a1', 'a2'])
  })

  it('treats a part whose parent is not shown as a root', () => {
    const tree = buildTree([part('child', { parentPartId: 'missing' })])
    expect(tree.map((n) => n.part.id)).toEqual(['child'])
  })
})

describe('planeFor', () => {
  it('builds an axis-aligned plane at a design coordinate', () => {
    expect(planeFor('y', 40)).toEqual({ point: [0, 40, 0], normal: [0, 1, 0] })
    expect(planeFor('x', -3, true)).toEqual({ point: [-3, 0, 0], normal: [-1, 0, 0] })
  })
})

describe('parseForm', () => {
  it('round-trips a part and sends millimetres with empty fields as null', () => {
    const form = formFromPart(part('a'))
    const { request, errors } = parseForm({ ...form, category: 'tube', outerDiameterMm: '4', innerDiameterMm: '3.2', cutLengthMm: '' })

    expect(errors).toEqual({})
    expect(request).toMatchObject({ category: 'tube', units: 'mm', outerDiameterMm: 4, innerDiameterMm: 3.2, cutLengthMm: null, quantity: 1 })
  })

  it('rejects out-of-range values, bad quantities and empty names', () => {
    const form = formFromPart(part('a'))
    const { request, errors } = parseForm({ ...form, name: ' ', quantity: '1.5', thicknessMm: '-1', cutAngleDeg: '120' })

    expect(request).toBeNull()
    expect(Object.keys(errors).sort()).toEqual(['cutAngleDeg', 'name', 'quantity', 'thicknessMm'])
  })

  it('does not block a tube whose inner diameter is too large; the server reports it', () => {
    const { request } = parseForm({ ...formFromPart(part('a')), category: 'tube', outerDiameterMm: '4', innerDiameterMm: '5' })
    expect(request).not.toBeNull()
  })
})

describe('hash routes', () => {
  it('parses and formats routes', () => {
    expect(parseRoute('')).toEqual({ view: 'workspace' })
    expect(parseRoute('#/designs')).toEqual({ view: 'designs' })
    const id = '6f9619ff-8b86-d011-b42d-00c04fc964ff'
    expect(parseRoute(`#/designs/${id.toUpperCase()}`)).toEqual({ view: 'design', id })
    expect(parseRoute('#/designs/not-a-guid')).toEqual({ view: 'designs' })
    expect(routeHash({ view: 'design', id })).toBe(`#/designs/${id}`)
  })

  it('opens the parametric page, optionally as a variant of a project', () => {
    const id = '6f9619ff-8b86-d011-b42d-00c04fc964ff'
    expect(parseRoute('#/parametric')).toEqual({ view: 'parametric', from: null })
    expect(parseRoute(`#/parametric/${id}`)).toEqual({ view: 'parametric', from: id })
    expect(parseRoute('#/parametric/../etc')).toEqual({ view: 'parametric', from: null })
    expect(routeHash({ view: 'parametric', from: id })).toBe(`#/parametric/${id}`)
    expect(routeHash({ view: 'parametric', from: null })).toBe('#/parametric')
    expect(parseRoute('#/photogrammetry')).toEqual({ view: 'photogrammetry' })
    expect(parseRoute(`#/photogrammetry/${id}`)).toEqual({ view: 'photoset', id })
    expect(parseRoute('#/photogrammetry/x')).toEqual({ view: 'photogrammetry' })
    expect(routeHash({ view: 'photoset', id })).toBe(`#/photogrammetry/${id}`)
  })
})

describe('drawing urls', () => {
  it('carry the format, an optional download flag and a cache-busting version', async () => {
    const { urls } = await import('./apiClient')
    const url = urls.drawing('p1', 'part 1', 'pdf', '2026-10-21T08:00:00Z', true)
    expect(url).toBe('/api/part-designs/p1/parts/part%201/drawing?format=pdf&download=true&v=2026-10-21T08%3A00%3A00Z')
    expect(urls.drawing('p1', 'x', 'svg', 'v')).toBe('/api/part-designs/p1/parts/x/drawing?format=svg&v=v')
  })
})
