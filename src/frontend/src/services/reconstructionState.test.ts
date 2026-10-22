import { describe, expect, it } from 'vitest'
import type { Reconstruction, RouteOption } from '../types/references'
import { blockers, canStart, isGenerating, routeLabel, runsInference, stateLabel, stateTone } from './reconstructionState'

const option = (over: Partial<RouteOption> = {}): RouteOption => ({
  route: 'single_image_reconstruction',
  availability: 'available',
  recommended: false,
  reasons: [],
  modelId: null,
  extraPhotos: 0,
  ...over,
})

const plan = (over: Partial<Reconstruction> = {}): Reconstruction => ({
  state: 'reference_search',
  route: null,
  startedAt: null,
  jobId: null,
  jobStatus: null,
  designId: null,
  modelId: null,
  extraReferenceIds: [],
  message: null,
  recommended: 'single_image_reconstruction',
  routes: [option()],
  notes: [],
  engine: 'TripoSR',
  engineReachable: true,
  ...over,
})

describe('reconstruction state', () => {
  it('treats a generation as running until its job is final', () => {
    expect(isGenerating(plan({ state: 'single_image_reconstruction', jobStatus: 'Processing' }))).toBe(true)
    expect(isGenerating(plan({ state: 'hybrid_reconstruction', jobStatus: 'Pending' }))).toBe(true)
    expect(isGenerating(plan({ state: 'completed', jobStatus: 'Succeeded' }))).toBe(false)
    expect(isGenerating(plan({ state: 'manual_geometry_required' }))).toBe(false)
    expect(isGenerating(plan({ state: 'failed', jobStatus: 'Failed' }))).toBe(false)
  })

  it('only offers available routes, and none while generating', () => {
    expect(canStart(plan(), option())).toBe(true)
    expect(canStart(plan(), option({ availability: 'unavailable' }))).toBe(false)
    expect(canStart(plan(), option({ route: 'multi_view_reconstruction', availability: 'unsupported' }))).toBe(false)
    expect(canStart(plan({ state: 'single_image_reconstruction', jobStatus: 'Processing' }), option())).toBe(false)
  })

  it('lists only blockers for an unavailable route', () => {
    const o = option({
      availability: 'unavailable',
      reasons: [
        { code: 'a', severity: 'blocker', message: 'no photo' },
        { code: 'b', severity: 'warning', message: 'guessed' },
      ],
    })
    expect(blockers(o).map((r) => r.code)).toEqual(['a'])
  })

  it('labels routes and states and knows which routes run inference', () => {
    expect(routeLabel('reuse_existing_model')).toBe('Reuse the accepted model')
    expect(stateLabel('model_candidate_review')).toBe('Reviewing model candidates')
    expect(stateTone('completed')).toBe('valid')
    expect(stateTone('failed')).toBe('invalid')
    expect(stateTone('reference_search')).toBe('neutral')
    expect(runsInference('hybrid_reconstruction')).toBe(true)
    expect(runsInference('reuse_existing_model')).toBe(false)
    expect(runsInference('manual_geometry_required')).toBe(false)
  })
})
