import { describe, expect, it } from 'vitest'
import type { Collision, DesignPart } from '../types/partDesign'
import { emptyRelationForm, explodedOffsets, fitSummary, sortCollisions, toRelationBody, toRelationRequest } from './assemblyState'

const part = (id: string, min: [number, number, number], max: [number, number, number]) =>
  ({
    id,
    geometry: {
      bounds: {
        min: { x: min[0], y: min[1], z: min[2] },
        max: { x: max[0], y: max[1], z: max[2] },
        size: { x: max[0] - min[0], y: max[1] - min[1], z: max[2] - min[2] },
      },
    },
  }) as DesignPart

describe('relation form', () => {
  it('needs two different parts', () => {
    expect(toRelationRequest(emptyRelationForm('a'))).toEqual({ error: 'Choose both parts.' })
    expect(toRelationRequest(emptyRelationForm('a', 'a'))).toEqual({ error: 'Choose two different parts.' })
  })

  it('sends the opening only for kinds that use it, with decimal commas', () => {
    const peg = toRelationRequest({ ...emptyRelationForm('a', 'b'), kind: 'peg_in_hole', openingSizeMm: '3,1', note: '  pin  ' })
    expect(peg).toEqual({ request: { partAId: 'a', partBId: 'b', kind: 'peg_in_hole', allowOverlap: false, openingSizeMm: 3.1, note: 'pin' } })
    const glued = toRelationRequest({ ...emptyRelationForm('a', 'b'), openingSizeMm: '3' })
    expect('request' in glued && glued.request.openingSizeMm).toBe(null)
  })

  it('keeps the server limits', () => {
    expect(toRelationBody({ ...emptyRelationForm('a', 'b'), kind: 'slot', openingSizeMm: '0' })).toEqual({ error: 'Slot width must be between 0.01 and 1000 mm.' })
    expect(toRelationBody({ ...emptyRelationForm('a', 'b'), note: 'x'.repeat(501) })).toEqual({ error: 'Use at most 500 characters for the note.' })
  })
})

describe('fit summary', () => {
  it('describes clearance and interference', () => {
    expect(fitSummary({ status: 'ok', openingMm: 3.2, insertedMm: 3, clearanceMm: 0.2, basis: 'specified' })).toBe('0.2 mm clearance, OK')
    expect(fitSummary({ status: 'too_tight', openingMm: 3, insertedMm: 3.1, clearanceMm: -0.1, basis: 'estimated' })).toBe('0.1 mm interference, too tight (from estimates)')
    expect(fitSummary({ status: 'unknown', openingMm: null, insertedMm: 3, clearanceMm: null, basis: null })).toBe('Fit unknown: a size is missing')
  })
})

describe('collisions', () => {
  it('lists problems before info and plain touching last', () => {
    const c = (code: string, severity: Collision['severity']) => ({ code, severity }) as Collision
    const sorted = sortCollisions([c('parts_touch', 'info'), c('overlap_allowed', 'info'), c('parts_intersect', 'warning')])
    expect(sorted.map((x) => x.code)).toEqual(['parts_intersect', 'overlap_allowed', 'parts_touch'])
  })
})

describe('exploded view', () => {
  it('moves parts away from the assembly centre in metres, and not at all at zero', () => {
    const parts = [part('left', [-20, 0, 0], [-10, 10, 10]), part('right', [10, 0, 0], [20, 10, 10])]
    expect(explodedOffsets(parts, 0).get('left')).toEqual([0, 0, 0])
    const offsets = explodedOffsets(parts, 1)
    expect(offsets.get('left')).toEqual([-0.015, 0, 0])
    expect(offsets.get('right')).toEqual([0.015, 0, 0])
  })

  it('leaves a part at the centre in place', () => {
    const offsets = explodedOffsets([part('middle', [-5, -5, -5], [5, 5, 5]), part('outer', [-20, -20, -20], [20, 20, 20])], 1)
    expect(offsets.get('middle')).toEqual([0, 0, 0])
    expect(explodedOffsets([], 1).size).toBe(0)
  })
})
