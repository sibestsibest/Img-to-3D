import type { PhotoFinding, PhotogrammetryJob, PhotogrammetryStatus, ScaleAxisName } from '../types/photogrammetry'

const STATUS_LABELS: Record<PhotogrammetryStatus, string> = {
  draft: 'Collecting photos',
  queued: 'Waiting to start',
  running: 'Reconstructing',
  succeeded: 'Reconstructed',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

export const statusLabel = (status: PhotogrammetryStatus) => STATUS_LABELS[status] ?? status

export function statusTone(status: PhotogrammetryStatus): 'valid' | 'warning' | 'invalid' | 'neutral' {
  if (status === 'succeeded') return 'valid'
  if (status === 'failed') return 'invalid'
  if (status === 'cancelled') return 'warning'
  return 'neutral'
}

/** The worker still has to do something: the page polls while this is true. */
export const isActive = (status: PhotogrammetryStatus) => status === 'queued' || status === 'running'

export function formatDuration(seconds: number | null): string {
  if (seconds == null) return ''
  const s = Math.round(seconds)
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const rest = s % 60
  return h > 0 ? `${h} h ${m} min` : m > 0 ? `${m} min ${rest} s` : `${rest} s`
}

/** What is known about a run: Meshroom announces no total, so no percentage is shown. */
export function progressText(job: PhotogrammetryJob): string {
  if (job.status === 'queued') return 'Waiting: Meshroom runs one reconstruction at a time.'
  if (job.status !== 'running') return ''
  const parts = [`${job.progress.nodesDone ?? 0} step(s) finished`]
  if (job.progress.currentNode && job.progress.currentNode !== 'Starting') parts.push(`now: ${job.progress.currentNode}`)
  if (job.progress.elapsedSeconds != null) parts.push(`running for ${formatDuration(job.progress.elapsedSeconds)}`)
  return parts.join(' · ')
}

/** Findings per photo id, and those about the whole set. */
export function splitFindings(findings: PhotoFinding[]): { byPhoto: Map<string, PhotoFinding[]>; set: PhotoFinding[] } {
  const byPhoto = new Map<string, PhotoFinding[]>()
  const set: PhotoFinding[] = []
  for (const f of findings) {
    if (f.photoId == null) set.push(f)
    else byPhoto.set(f.photoId, [...(byPhoto.get(f.photoId) ?? []), f])
  }
  return { byPhoto, set }
}

/** Upload batches: at most <maxFiles> files and <maxBytes> per request, files kept in order. */
export function batches<T extends { size: number }>(files: T[], maxFiles = 10, maxBytes = 200 * 1024 * 1024): T[][] {
  const out: T[][] = []
  let current: T[] = []
  let bytes = 0
  for (const file of files) {
    if (current.length > 0 && (current.length >= maxFiles || bytes + file.size > maxBytes)) {
      out.push(current)
      current = []
      bytes = 0
    }
    current.push(file)
    bytes += file.size
  }
  if (current.length > 0) out.push(current)
  return out
}

/** Only JPEG and PNG reach the server; other files are named so the user knows what was skipped. */
export function acceptedPhotos<T extends { name: string; type: string }>(files: T[]): { accepted: T[]; skipped: string[] } {
  const accepted: T[] = []
  const skipped: string[] = []
  for (const f of files) {
    if (f.type === 'image/jpeg' || f.type === 'image/png' || /\.(jpe?g|png)$/i.test(f.name)) accepted.push(f)
    else skipped.push(f.name)
  }
  return { accepted, skipped }
}

export function parseScale(axis: ScaleAxisName, text: string): { axis: ScaleAxisName; targetMm: number } | { error: string } {
  const value = Number(text.trim().replace(',', '.'))
  if (text.trim() === '' || !Number.isFinite(value)) return { error: 'Enter the measured length in mm.' }
  if (value < 0.1 || value > 10000) return { error: 'The length must be between 0.1 and 10000 mm.' }
  return { axis, targetMm: value }
}

export const AXIS_LABELS: Record<ScaleAxisName, string> = {
  Longest: 'Longest side',
  X: 'Width (X)',
  Y: 'Height (Y)',
  Z: 'Depth (Z)',
}
