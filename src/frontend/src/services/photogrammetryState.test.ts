import { describe, expect, it } from 'vitest'
import type { PhotogrammetryJob } from '../types/photogrammetry'
import { acceptedPhotos, batches, formatDuration, isActive, parseScale, progressText, splitFindings, statusLabel, statusTone } from './photogrammetryState'

const job = (over: Partial<PhotogrammetryJob> = {}): PhotogrammetryJob => ({
  id: 'j',
  name: 'Set',
  status: 'draft',
  pipeline: null,
  progress: { nodesDone: null, currentNode: null, elapsedSeconds: null },
  errorCode: null,
  error: null,
  createdAt: '',
  startedAt: null,
  completedAt: null,
  engineVersion: null,
  sourceReferenceSessionId: null,
  photos: [],
  findings: [],
  canStart: false,
  mesh: null,
  dimensionsMm: null,
  scale: { status: 'uncalibrated', axis: null, targetMm: null, mmPerModelUnit: null },
  downloads: { glb: false, stl: false, zip: false, log: false, stlError: null },
  outputs: [],
  origin: 'photogrammetry',
  units: 'mm',
  ...over,
})

describe('photogrammetry state', () => {
  it('labels statuses and knows when to poll', () => {
    expect(statusLabel('running')).toBe('Reconstructing')
    expect(statusTone('succeeded')).toBe('valid')
    expect(statusTone('failed')).toBe('invalid')
    expect(isActive('queued')).toBe(true)
    expect(isActive('running')).toBe(true)
    expect(isActive('draft')).toBe(false)
    expect(isActive('succeeded')).toBe(false)
  })

  it('reports progress without inventing a percentage', () => {
    expect(progressText(job({ status: 'running', progress: { nodesDone: 4, currentNode: 'DepthMap', elapsedSeconds: 125 } }))).toBe(
      '4 step(s) finished · now: DepthMap · running for 2 min 5 s',
    )
    expect(progressText(job({ status: 'running', progress: { nodesDone: 0, currentNode: 'Starting', elapsedSeconds: null } }))).toBe('0 step(s) finished')
    expect(progressText(job({ status: 'queued' }))).toContain('one reconstruction at a time')
    expect(progressText(job({ status: 'succeeded' }))).toBe('')
    expect(formatDuration(3720)).toBe('1 h 2 min')
    expect(formatDuration(null)).toBe('')
  })

  it('groups findings by photo', () => {
    const { byPhoto, set } = splitFindings([
      { code: 'few_photos', severity: 'warning', message: '', photoId: null },
      { code: 'blurry', severity: 'warning', message: '', photoId: 'p1' },
      { code: 'low_resolution', severity: 'warning', message: '', photoId: 'p1' },
    ])
    expect(set.map((f) => f.code)).toEqual(['few_photos'])
    expect(byPhoto.get('p1')?.map((f) => f.code)).toEqual(['blurry', 'low_resolution'])
  })

  it('splits uploads into batches by count and size', () => {
    const files = [5, 5, 5, 50, 5].map((mb, i) => ({ name: `f${i}`, size: mb * 1024 * 1024 }))
    expect(batches(files, 2).map((b) => b.map((f) => f.name))).toEqual([['f0', 'f1'], ['f2', 'f3'], ['f4']])
    expect(batches(files, 10, 20 * 1024 * 1024).map((b) => b.length)).toEqual([3, 1, 1])
    expect(batches([])).toEqual([])
  })

  it('accepts only JPEG and PNG photos', () => {
    const { accepted, skipped } = acceptedPhotos([
      { name: 'a.JPG', type: '' },
      { name: 'b.png', type: 'image/png' },
      { name: 'c.heic', type: 'image/heic' },
      { name: 'd.webp', type: 'image/webp' },
    ])
    expect(accepted.map((f) => f.name)).toEqual(['a.JPG', 'b.png'])
    expect(skipped).toEqual(['c.heic', 'd.webp'])
  })

  it('validates the measured length', () => {
    expect(parseScale('Y', '42,5')).toEqual({ axis: 'Y', targetMm: 42.5 })
    expect(parseScale('X', '')).toEqual({ error: 'Enter the measured length in mm.' })
    expect(parseScale('X', '0')).toEqual({ error: 'The length must be between 0.1 and 10000 mm.' })
  })
})
