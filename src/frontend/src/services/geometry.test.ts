import { describe, expect, it } from 'vitest'
import { axisLength, formatMm, scaleDimensions, uniformScaleFactor, validateTargetMm } from './geometry'

const dims = { x: 50, y: 100, z: 25 }

describe('uniform scaling', () => {
  it.each([
    ['X', 100, 2],
    ['Y', 100, 1],
    ['Z', 100, 4],
    ['Longest', 50, 0.5],
  ] as const)('axis %s to %d mm gives factor %d', (axis, target, factor) => {
    expect(uniformScaleFactor(dims, axis, target)).toBeCloseTo(factor, 10)
  })

  it('scales all dimensions by the same factor', () => {
    const f = uniformScaleFactor(dims, 'Y', 150)
    expect(scaleDimensions(dims, f)).toEqual({ x: 75, y: 150, z: 37.5 })
  })

  it('reports the longest side', () => {
    expect(axisLength(dims, 'Longest')).toBe(100)
  })

  it('rejects zero-length dimensions', () => {
    expect(() => uniformScaleFactor({ x: 0, y: 1, z: 1 }, 'X', 10)).toThrow()
  })
})

describe('validateTargetMm', () => {
  it.each(['', 'abc', '0', '-5', '0.05', '20000'])('rejects %j', (value) => {
    expect(validateTargetMm(value)).not.toBeNull()
  })

  it.each(['0.1', '25.4', '144', '10000'])('accepts %j', (value) => {
    expect(validateTargetMm(value)).toBeNull()
  })
})

describe('formatMm', () => {
  it('uses fewer decimals for larger values', () => {
    expect(formatMm(144.123)).toBe('144.1')
    expect(formatMm(12.3456)).toBe('12.35')
    expect(formatMm(1.23456)).toBe('1.235')
  })
})
