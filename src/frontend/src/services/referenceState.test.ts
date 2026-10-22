import { afterEach, describe, expect, it, vi } from 'vitest'
import { parseRoute, routeHash } from '../hooks/useHashRoute'
import type { ReferenceCandidate, ReferenceLicense, SourceSearchResult } from '../types/references'
import { referenceApi, urls } from './apiClient'
import {
  allSourcesUnavailable,
  confidenceLabel,
  countByDecision,
  describeOutcome,
  filterCandidates,
  formatBytes,
  groupCandidates,
  licenseTone,
  percent,
  sortCandidates,
  suggestionLabel,
} from './referenceState'

afterEach(() => vi.restoreAllMocks())

const license = (code: string, commercialUse: boolean | null = true, derivatives: boolean | null = true): ReferenceLicense => ({
  code,
  label: code,
  url: null,
  raw: null,
  commercialUse,
  derivatives,
  attributionRequired: null,
})

const candidate = (id: string, decision: ReferenceCandidate['decision'], duplicateOfId: string | null = null): ReferenceCandidate =>
  ({ id, decision, duplicateOfId, title: id, license: license('cc0'), warnings: [] }) as unknown as ReferenceCandidate

const outcome = (status: SourceSearchResult['status'], extra: Partial<SourceSearchResult> = {}): SourceSearchResult => ({
  source: 's',
  name: 'S',
  status,
  hits: 0,
  added: 0,
  fromCache: false,
  message: null,
  ...extra,
})

describe('candidate filtering', () => {
  const list = [candidate('a', 'pending'), candidate('b', 'accepted'), candidate('c', 'accepted', 'b'), candidate('d', 'rejected')]

  it('filters by decision and hides duplicates on request', () => {
    expect(filterCandidates(list, 'all', false).map((c) => c.id)).toEqual(['a', 'b', 'c', 'd'])
    expect(filterCandidates(list, 'accepted', false).map((c) => c.id)).toEqual(['b', 'c'])
    expect(filterCandidates(list, 'accepted', true).map((c) => c.id)).toEqual(['b'])
    expect(filterCandidates(list, 'all', true).map((c) => c.id)).toEqual(['a', 'b', 'd'])
  })

  it('counts every decision', () => {
    expect(countByDecision(list)).toEqual({ all: 4, pending: 1, accepted: 2, rejected: 1 })
    expect(countByDecision([])).toEqual({ all: 0, pending: 0, accepted: 0, rejected: 0 })
  })
})

describe('licence tone', () => {
  it('separates free, restricted and unknown licences', () => {
    expect(licenseTone(license('cc-by-sa'))).toBe('free')
    expect(licenseTone(license('cc-by-nc', false, true))).toBe('restricted')
    expect(licenseTone(license('cc-by-nd', true, false))).toBe('restricted')
    expect(licenseTone(license('unknown', null, null))).toBe('unknown')
  })
})

describe('source outcomes', () => {
  it('describes results and failures', () => {
    expect(describeOutcome(outcome('ok', { hits: 20, added: 5, fromCache: true }))).toBe('20 found, 5 new (cached)')
    expect(describeOutcome(outcome('timeout', { message: 'No answer within 12 s.' }))).toBe('No answer within 12 s.')
    expect(describeOutcome(outcome('empty'))).toBe('No results')
  })

  it('knows when no source could be searched', () => {
    expect(allSourcesUnavailable([outcome('failed'), outcome('timeout'), outcome('disabled')])).toBe(true)
    expect(allSourcesUnavailable([outcome('failed'), outcome('empty')])).toBe(false)
    expect(allSourcesUnavailable([])).toBe(false)
  })
})

describe('reference routes', () => {
  it('parses and formats reference routes', () => {
    const id = '6f9619ff-8b86-d011-b42d-00c04fc964ff'
    expect(parseRoute('#/references')).toEqual({ view: 'references' })
    expect(parseRoute(`#/references/${id.toUpperCase()}`)).toEqual({ view: 'reference', id })
    expect(parseRoute('#/references/../designs')).toEqual({ view: 'references' })
    expect(routeHash({ view: 'reference', id })).toBe(`#/references/${id}`)
    expect(routeHash({ view: 'references' })).toBe('#/references')
    expect(parseRoute('#/designs')).toEqual({ view: 'designs' })
  })
})

describe('reference api', () => {
  const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })

  it('creates a session from a job as multipart form data', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json({ id: 's1' }))
    await referenceApi.create({ sourceJobId: 'job-1' })
    const body = fetchMock.mock.calls[0][1]!.body as FormData
    expect(fetchMock.mock.calls[0][0]).toBe('/api/references')
    expect(body.get('sourceJobId')).toBe('job-1')
    expect(body.has('image')).toBe(false)
  })

  it('sends the query and chosen sources when searching', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json({ id: 's1' }))
    await referenceApi.search('s1', 'zaku', ['wikimedia'])
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/references/s1/search')
    expect(JSON.parse(init!.body as string)).toEqual({ query: 'zaku', sources: ['wikimedia'] })
  })

  it('sends only filled-in fields with a manual picture', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json({ id: 's1' }))
    await referenceApi.addPicture('s1', { file: new File(['x'], 'a.png', { type: 'image/png' }), title: ' Side ', author: '  ', license: 'cc-by' })
    const body = fetchMock.mock.calls[0][1]!.body as FormData
    expect(body.get('title')).toBe('Side')
    expect(body.has('author')).toBe(false)
    expect(body.get('license')).toBe('cc-by')
  })

  it('builds relative preview URLs', () => {
    expect(urls.referenceThumbnail('s 1', 'c1')).toBe('/api/references/s%201/candidates/c1/thumbnail')
    expect(urls.referenceInput('s1', 'full')).toBe('/api/references/s1/input?size=full')
  })
})

describe('ranking display', () => {
  const ranked = (id: string, position: number | null, group: number | null, viewpoint: string | null): ReferenceCandidate =>
    ({
      id,
      decision: 'pending',
      duplicateOfId: null,
      ranking: { position, group, viewpoint: viewpoint ? { label: viewpoint, confidence: 0.6 } : null, reasons: [] },
    }) as unknown as ReferenceCandidate
  const list = [ranked('a', 3, 2, 'back'), ranked('b', 1, 1, 'front'), ranked('c', null, null, null), ranked('d', 2, 2, 'front')]

  it('sorts by rank with unranked pictures last, or keeps discovery order', () => {
    expect(sortCandidates(list, 'rank').map((c) => c.id)).toEqual(['b', 'd', 'a', 'c'])
    expect(sortCandidates(list, 'found').map((c) => c.id)).toEqual(['a', 'b', 'c', 'd'])
  })

  it('groups by visual group or viewpoint in display order', () => {
    const byGroup = groupCandidates(sortCandidates(list, 'rank'), 'group')
    expect(byGroup.map((s) => [s.title, s.items.map((c) => c.id)])).toEqual([
      ['Visual group 1', ['b']],
      ['Visual group 2', ['d', 'a']],
      ['Not compared visually', ['c']],
    ])
    expect(groupCandidates(list, 'viewpoint').map((s) => s.title)).toEqual(['Back', 'Front', 'Viewpoint unknown'])
    expect(groupCandidates(list, 'none')).toHaveLength(1)
  })

  it('labels suggestions and confidence without overclaiming', () => {
    expect(suggestionLabel('same_object')).toBe('same object?')
    expect(suggestionLabel('three_quarter')).toBe('three-quarter')
    expect(confidenceLabel(1)).toBe('more evidence')
    expect(confidenceLabel(0.25)).toBe('little evidence')
    expect(confidenceLabel(null)).toBe('not ranked')
    expect(percent(0.456)).toBe('46%')
    expect(percent(null)).toBe('–')
    expect(formatBytes(1234567)).toBe('1.2 MB')
    expect(formatBytes(2048)).toBe('2 KB')
  })
})

describe('model api', () => {
  const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })

  it('uploads a model file with its fields and calls rank and model search', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockImplementation(async () => json({ id: 's1' }))
    await referenceApi.addModel('s1', { file: new File(['glTF'], 'a.glb'), license: 'cc-by', author: ' ' })
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/api/references/s1/models')
    const body = init!.body as FormData
    expect(body.get('license')).toBe('cc-by')
    expect(body.has('author')).toBe(false)
    expect(body.get('file')).toBeInstanceOf(File)

    await referenceApi.rank('s1')
    expect(fetchMock.mock.calls[1][0]).toBe('/api/references/s1/rank')
    await referenceApi.searchModels('s1', 'zaku')
    expect(JSON.parse(fetchMock.mock.calls[2][1]!.body as string)).toEqual({ query: 'zaku' })
    expect(urls.modelFile('s1', 'm1')).toBe('/api/references/s1/models/m1/file')
  })
})
