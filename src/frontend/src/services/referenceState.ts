import type {
  ReferenceCandidate,
  ReferenceDecision,
  ReferenceImageKind,
  ReferenceLicense,
  ReferenceRelation,
  SourceSearchResult,
} from '../types/references'

export type CandidateFilter = 'all' | ReferenceDecision

export function filterCandidates(candidates: ReferenceCandidate[], filter: CandidateFilter, hideDuplicates: boolean): ReferenceCandidate[] {
  return candidates.filter((c) => (filter === 'all' || c.decision === filter) && !(hideDuplicates && c.duplicateOfId != null))
}

export function countByDecision(candidates: ReferenceCandidate[]): Record<CandidateFilter, number> {
  const counts: Record<CandidateFilter, number> = { all: candidates.length, pending: 0, accepted: 0, rejected: 0 }
  for (const c of candidates) counts[c.decision]++
  return counts
}

/**
 * Badge tone for a licence: `free` (commercial use and changes allowed), `restricted`
 * (non-commercial or no derivatives) or `unknown` (personal reference only).
 */
export function licenseTone(license: ReferenceLicense): 'free' | 'restricted' | 'unknown' {
  if (license.code === 'unknown') return 'unknown'
  return license.commercialUse === false || license.derivatives === false ? 'restricted' : 'free'
}

const STATUS_LABELS: Record<SourceSearchResult['status'], string> = {
  ok: 'OK',
  empty: 'No results',
  failed: 'Failed',
  timeout: 'Timed out',
  rate_limited: 'Rate limited',
  disabled: 'Disabled',
}

export function sourceStatusLabel(status: SourceSearchResult['status']): string {
  return STATUS_LABELS[status] ?? status
}

/** One line per source, e.g. "20 found, 5 new (cached)". */
export function describeOutcome(result: SourceSearchResult): string {
  if (result.status !== 'ok') return result.message ?? sourceStatusLabel(result.status)
  const parts = [`${result.hits} found`, `${result.added} new`]
  return parts.join(', ') + (result.fromCache ? ' (cached)' : '') + (result.message ? ` · ${result.message}` : '')
}

/** True when no source returned anything usable, so the manual links are the way forward. */
export function allSourcesUnavailable(results: SourceSearchResult[]): boolean {
  return results.length > 0 && results.every((r) => r.status !== 'ok' && r.status !== 'empty')
}

export const IMAGE_KINDS: { value: ReferenceImageKind; label: string }[] = [
  { value: 'unknown', label: 'Kind: not set' },
  { value: 'photo', label: 'Real photo' },
  { value: 'render', label: 'Product render' },
  { value: 'drawing', label: 'Drawing / blueprint' },
]

export const RELATIONS: { value: ReferenceRelation; label: string }[] = [
  { value: 'unknown', label: 'Relation: not set' },
  { value: 'same_object', label: 'Same object, other view' },
  { value: 'variant', label: 'Closely related variant' },
  { value: 'similar_different', label: 'Similar but different' },
]

/** Licence codes the server accepts for a manually added picture. */
export const MANUAL_LICENSES: { value: string; label: string }[] = [
  { value: 'unknown', label: 'Unknown (personal reference only)' },
  { value: 'own_work', label: 'My own photo' },
  { value: 'cc0', label: 'CC0' },
  { value: 'public_domain', label: 'Public domain' },
  { value: 'cc-by', label: 'CC BY' },
  { value: 'cc-by-sa', label: 'CC BY-SA' },
  { value: 'cc-by-nd', label: 'CC BY-ND' },
  { value: 'cc-by-nc', label: 'CC BY-NC' },
  { value: 'cc-by-nc-sa', label: 'CC BY-NC-SA' },
  { value: 'cc-by-nc-nd', label: 'CC BY-NC-ND' },
  { value: 'gfdl', label: 'GNU FDL' },
]
