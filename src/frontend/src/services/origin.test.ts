import { describe, expect, it } from 'vitest'
import { ORIGINS, originOf } from './origin'

describe('geometry origin', () => {
  it('names every origin and says what it means', () => {
    expect(ORIGINS.ai_predicted.label).toBe('AI-predicted')
    expect(ORIGINS.parametric.label).toBe('Parametric')
    expect(ORIGINS.photogrammetry.label).toBe('Photogrammetry')
    for (const o of Object.values(ORIGINS)) expect(o.description.length).toBeGreaterThan(20)
  })

  it('never claims more than it knows for unknown values', () => {
    expect(originOf('parametric')).toBe('parametric')
    expect(originOf(undefined)).toBe('manual')
    expect(originOf('toString')).toBe('manual')
  })
})
