import { describe, expect, it } from 'vitest'
import { parseRoute, routeHash } from '../hooks/useHashRoute'
import { emptyPrimitiveForm, toPrimitiveRequest } from './primitiveForm'

describe('stock shape form', () => {
  it('builds a plate request with only the plate sizes', () => {
    const result = toPrimitiveRequest({ ...emptyPrimitiveForm('plate'), lengthMm: '40', widthMm: '20,5', thicknessMm: '0.5', heightMm: '99', centerY: '10' })
    expect(result).toEqual({
      request: { kind: 'plate', units: 'mm', lengthMm: 40, widthMm: 20.5, thicknessMm: 0.5, axis: 'y', center: [0, 10, 0] },
    })
  })

  it('requires every size of the shape and keeps the server limits', () => {
    expect(toPrimitiveRequest({ ...emptyPrimitiveForm('rod'), lengthMm: '30' })).toEqual({ error: 'Diameter is required.' })
    expect(toPrimitiveRequest({ ...emptyPrimitiveForm('rod'), lengthMm: '30', diameterMm: '0.01' })).toEqual({ error: 'Diameter must be between 0.05 and 5000 mm.' })
  })

  it('refuses a tube whose bore is not smaller than its outside', () => {
    const form = { ...emptyPrimitiveForm('tube'), outerDiameterMm: '3', innerDiameterMm: '3', lengthMm: '10' }
    expect(toPrimitiveRequest(form)).toEqual({ error: 'The inner diameter must be smaller than the outer diameter.' })
    const ok = toPrimitiveRequest({ ...form, innerDiameterMm: '2' })
    expect('request' in ok && ok.request.axis).toBe('x')
  })

  it('sends no axis for a block and rejects a bad position', () => {
    const block = toPrimitiveRequest({ ...emptyPrimitiveForm('box'), lengthMm: '1', widthMm: '2', heightMm: '3', name: '  Hatch  ' })
    expect(block).toEqual({ request: { kind: 'box', units: 'mm', lengthMm: 1, heightMm: 3, widthMm: 2, center: [0, 0, 0], name: 'Hatch' } })
    expect(toPrimitiveRequest({ ...emptyPrimitiveForm('box'), lengthMm: '1', widthMm: '2', heightMm: '3', centerX: 'left' })).toEqual({
      error: 'The position must be numbers (mm).',
    })
  })
})

describe('job route', () => {
  it('opens the workspace on one job', () => {
    const id = '0f8fad5b-d9cb-469f-a165-70867728950e'
    expect(parseRoute(`#/jobs/${id.toUpperCase()}`)).toEqual({ view: 'job', id })
    expect(routeHash({ view: 'job', id })).toBe(`#/jobs/${id}`)
    expect(parseRoute('#/jobs/not-a-guid')).toEqual({ view: 'workspace' })
  })
})
