import { describe, expect, it } from 'vitest'
import { MAX_UPLOAD_BYTES, validateFile } from './ImageUpload'
import { describeJob } from '../services/stages'
import type { Job } from '../types/api'

describe('validateFile', () => {
  it('accepts PNG, JPEG and WebP', () => {
    for (const type of ['image/png', 'image/jpeg', 'image/webp'])
      expect(validateFile(new File(['x'], 'f', { type }))).toBeNull()
  })

  it('rejects other types', () => {
    expect(validateFile(new File(['x'], 'f.gif', { type: 'image/gif' }))).toMatch(/Unsupported/)
    expect(validateFile(new File(['x'], 'f.stl', { type: 'model/stl' }))).toMatch(/Unsupported/)
  })

  it('rejects empty and oversized files', () => {
    expect(validateFile(new File([], 'f.png', { type: 'image/png' }))).toMatch(/empty/)
    const big = new File([new Uint8Array(MAX_UPLOAD_BYTES + 1)], 'f.png', { type: 'image/png' })
    expect(validateFile(big)).toMatch(/20 MB/)
  })
})

describe('describeJob', () => {
  const base = { status: 'Processing', stage: 'EncodingImage', stageProgress: null } as Job

  it('shows no percentage when progress is not measured', () => {
    expect(describeJob(base)).toBe('Running TripoSR image encoder')
  })

  it('shows a percentage only when measured', () => {
    expect(describeJob({ ...base, stage: 'EvaluatingDensity', stageProgress: 0.42 })).toBe('Evaluating 3D density grid — 42%')
  })

  it('shows the server error for failures', () => {
    expect(describeJob({ ...base, status: 'Failed', error: 'No foreground object was detected.' })).toBe('No foreground object was detected.')
  })
})
