import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from './apiClient'
import { apiLog, formatBody, summarize, type ApiLogInput } from './apiLog'

afterEach(() => {
  apiLog.clear()
  vi.restoreAllMocks()
})

const call = (overrides: Partial<ApiLogInput> = {}): ApiLogInput => ({
  method: 'GET',
  url: '/api/models/abc',
  status: 200,
  durationMs: 12,
  sizeBytes: 100,
  summary: 'Processing / GeneratingMesh',
  correlationId: 'cid-1',
  body: null,
  source: 'api',
  ...overrides,
})

describe('apiLog', () => {
  it('lists newest first', () => {
    apiLog.record(call({ url: '/api/models' }))
    apiLog.record(call({ url: '/api/health' }))
    expect(apiLog.getEntries().map((e) => e.url)).toEqual(['/api/health', '/api/models'])
  })

  it('collapses identical consecutive polls into one row with a count', () => {
    const t1 = new Date(2026, 0, 1, 10, 0, 0)
    const t2 = new Date(2026, 0, 1, 10, 0, 3)
    apiLog.record(call({ correlationId: 'a' }), t1)
    apiLog.record(call({ correlationId: 'b', durationMs: 30 }), t2)

    const [row] = apiLog.getEntries()
    expect(apiLog.getEntries()).toHaveLength(1)
    expect(row.count).toBe(2)
    expect(row.firstTime).toBe(t1)
    expect(row.time).toBe(t2)
    expect(row.correlationId).toBe('b') // latest call's details
    expect(row.durationMs).toBe(30)
  })

  it('starts a new row when the outcome changes', () => {
    apiLog.record(call())
    apiLog.record(call({ summary: 'Processing / Exporting' }))
    apiLog.record(call({ summary: 'Succeeded' }))
    expect(apiLog.getEntries().map((e) => e.summary)).toEqual(['Succeeded', 'Processing / Exporting', 'Processing / GeneratingMesh'])
  })

  it('keeps the same array instance until something changes', () => {
    apiLog.record(call())
    const first = apiLog.getEntries()
    expect(apiLog.getEntries()).toBe(first)
    apiLog.record(call({ url: '/api/models' }))
    expect(apiLog.getEntries()).not.toBe(first)
  })

  it('reports client-fetched URLs once so resource timing does not duplicate them', () => {
    apiLog.markClientRequest('/api/models/x')
    expect(apiLog.consumeClientLogged('/api/models/x')).toBe(true)
    expect(apiLog.consumeClientLogged('/api/models/x')).toBe(false)
  })
})

describe('summarize', () => {
  it('describes jobs, problems and pages', () => {
    expect(summarize({ id: '1', status: 'Processing', stage: 'EvaluatingDensity', stageProgress: 0.42 })).toBe(
      'Processing / EvaluatingDensity 42%',
    )
    expect(summarize({ code: 'invalid_image', detail: 'Bad file.' })).toBe('invalid_image: Bad file.')
    expect(summarize({ items: [1, 2], totalCount: 7 })).toBe('2 item(s) of 7')
    expect(summarize({ status: 'ok' })).toBe('ok')
  })

  it('truncates long bodies', () => {
    expect(formatBody('x'.repeat(5000))).toMatch(/truncated\)$/)
    expect(formatBody(undefined)).toBeNull()
  })
})

describe('api client logging', () => {
  it('records method, status, outcome, size and correlation ID', async () => {
    const body = JSON.stringify({ id: 'abc', status: 'Succeeded', stage: null })
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(body, { status: 200, headers: { 'Content-Type': 'application/json', 'X-Correlation-ID': 'corr-123456' } }),
    )

    await api.getJob('abc')

    const [entry] = apiLog.getEntries()
    expect(entry).toMatchObject({
      method: 'GET',
      url: '/api/models/abc',
      status: 200,
      summary: 'Succeeded',
      correlationId: 'corr-123456',
      sizeBytes: body.length,
      source: 'api',
    })
    expect(entry.body).toContain('"Succeeded"')
  })

  it('records errors with their code', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify({ code: 'not_found', detail: 'Model job not found.' }), { status: 404 }),
    )
    await expect(api.getJob('zzz')).rejects.toThrow()
    expect(apiLog.getEntries()[0]).toMatchObject({ status: 404, summary: 'not_found: Model job not found.' })
  })

  it('records network failures but not aborted requests', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValueOnce(new TypeError('Failed to fetch'))
    await expect(api.getJob('n1')).rejects.toMatchObject({ code: 'network_error' })
    expect(apiLog.getEntries()[0]).toMatchObject({ status: 0, summary: 'network_error' })

    apiLog.clear()
    vi.spyOn(globalThis, 'fetch').mockRejectedValueOnce(new DOMException('aborted', 'AbortError'))
    await expect(api.getJob('n2')).rejects.toThrow()
    expect(apiLog.getEntries()).toHaveLength(0)
  })
})
