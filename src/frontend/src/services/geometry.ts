import type { Dimensions, ScaleAxis } from '../types/api'

export const MIN_TARGET_MM = 0.1
export const MAX_TARGET_MM = 10_000

export function axisLength(dims: Dimensions, axis: ScaleAxis): number {
  switch (axis) {
    case 'X':
      return dims.x
    case 'Y':
      return dims.y
    case 'Z':
      return dims.z
    case 'Longest':
      return Math.max(dims.x, dims.y, dims.z)
  }
}

/** Uniform factor that makes the chosen side equal to targetMm (same rule as the backend ScaleCalculator). */
export function uniformScaleFactor(dims: Dimensions, axis: ScaleAxis, targetMm: number): number {
  const current = axisLength(dims, axis)
  if (!(current > 0)) throw new Error('The selected dimension is zero.')
  return targetMm / current
}

export function scaleDimensions(dims: Dimensions, factor: number): Dimensions {
  return { x: dims.x * factor, y: dims.y * factor, z: dims.z * factor }
}

/** Returns an error message, or null if the target is acceptable. */
export function validateTargetMm(raw: string): string | null {
  if (raw.trim() === '') return 'Enter a length in millimetres.'
  const value = Number(raw)
  if (!Number.isFinite(value)) return 'Enter a number.'
  if (value < MIN_TARGET_MM || value > MAX_TARGET_MM) return `Length must be between ${MIN_TARGET_MM} and ${MAX_TARGET_MM} mm.`
  return null
}

export function formatMm(value: number): string {
  return value >= 100 ? value.toFixed(1) : value >= 10 ? value.toFixed(2) : value.toFixed(3)
}

export const AXIS_LABELS: Record<ScaleAxis, string> = {
  X: 'Width (X)',
  Y: 'Height (Y)',
  Z: 'Depth (Z)',
  Longest: 'Longest side',
}
