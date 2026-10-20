import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, ApiError, toApiError, urls } from './apiClient'
import type { Job } from '../types/api'

afterEach(() => vi.restoreAllMocks())

const json = (status: number, body: unknown) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('toApiError', () => {
  it('uses problem detail and code', async () => {
    const err = await toApiError(json(400, { detail: 'Unsupported image format.', code: 'invalid_image' }))
    expect(err.status).toBe(400)
    expect(err.code).toBe('invalid_image')
    expect(err.message).toBe('Unsupported image format.')
  })

  it('flattens validation errors', async () => {
    const err = await toApiError(json(400, { errors: { TargetMm: ['Must be between 0.1 and 10000.'] } }))
    expect(err.message).toBe('Must be between 0.1 and 10000.')
  })

  it('handles non-JSON gateway errors', async () => {
    const err = await toApiError(new Response('<html>Bad gateway</html>', { status: 502 }))
    expect(err.message).toMatch(/unreachable/)
  })
})

describe('api', () => {
  it('sends the Idempotency-Key header on upload', async () => {
    const fetchMock = vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(202, { id: '1' }))
    await api.createJob(new File(['x'], 'a.png', { type: 'image/png' }), 'key-12345678')
    const init = fetchMock.mock.calls[0][1]!
    expect(init.method).toBe('POST')
    expect((init.headers as Record<string, string>)['Idempotency-Key']).toBe('key-12345678')
    expect(init.body).toBeInstanceOf(FormData)
  })

  it('maps network failures to a readable error', async () => {
    vi.spyOn(globalThis, 'fetch').mockRejectedValue(new TypeError('Failed to fetch'))
    await expect(api.getJob('abc')).rejects.toMatchObject({ code: 'network_error' })
  })

  it('throws ApiError for 404', async () => {
    vi.spyOn(globalThis, 'fetch').mockResolvedValue(json(404, { detail: 'Model job not found.', code: 'not_found' }))
    await expect(api.getJob('abc')).rejects.toBeInstanceOf(ApiError)
  })
})

describe('urls', () => {
  it('builds relative URLs and busts the preview cache after scaling', () => {
    const job = { id: 'abc', scale: { axis: 'Y', targetMm: 150, mmPerModelUnit: 151.5 } } as Job
    expect(urls.preview(job)).toBe('/api/models/abc/preview?v=151.5')
    expect(urls.download(job, 'stl')).toBe('/api/models/abc/download?format=stl')
  })
})
