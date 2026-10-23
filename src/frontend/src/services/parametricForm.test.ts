import { describe, expect, it } from 'vitest'
import type { ParametricTemplate } from '../types/partDesign'
import { COMPONENTS_FIELD, EXAMPLE_COMPONENTS, fieldOf, initialValues, parameterLabel, toParameters } from './parametricForm'

const spacecraft: ParametricTemplate = {
  name: 'spacecraft',
  description: '',
  parameters: [
    { name: 'length', type: 'number', default: 240, minimum: 40, maximum: 2000, unit: 'mm', description: '' },
    { name: 'sheet_thickness', type: 'number', default: 1, minimum: 0.3, maximum: 3, unit: 'mm', description: '', allowed: [0.3, 0.5, 0.8, 1, 1.5, 2, 3] },
    { name: 'panel_rows', type: 'integer', default: 2, minimum: 0, maximum: 10, unit: '', description: '' },
  ],
}

const assembly: ParametricTemplate = {
  name: 'assembly',
  description: '',
  parameters: [{ name: 'segments', type: 'integer', default: 48, minimum: 12, maximum: 256, unit: '', description: '' }],
  shapes: { box: { fields: ['size_x'], description: '' } },
}

describe('parametric form', () => {
  it('starts from the defaults', () => {
    expect(initialValues(spacecraft)).toEqual({ length: '240', sheet_thickness: '1', panel_rows: '2' })
  })

  it('prefills a variant from an earlier project', () => {
    expect(initialValues(spacecraft, { length: 300, sheet_thickness: 1.5, other: 'x' })).toEqual({ length: '300', sheet_thickness: '1.5', panel_rows: '2' })
    const values = initialValues(assembly, { segments: 16, components: [{ name: 'a', shape: 'box' }] })
    expect(values.segments).toBe('16')
    expect(JSON.parse(values[COMPONENTS_FIELD])).toEqual([{ name: 'a', shape: 'box' }])
    expect(JSON.parse(initialValues(assembly)[COMPONENTS_FIELD])).toEqual(EXAMPLE_COMPONENTS)
  })

  it('converts valid text to numbers, accepting a decimal comma', () => {
    expect(toParameters(spacecraft, { length: '250,5', sheet_thickness: '0.5', panel_rows: '3' })).toEqual({
      parameters: { length: 250.5, sheet_thickness: 0.5, panel_rows: 3 },
    })
  })

  it('refuses what the worker would refuse, per field', () => {
    const result = toParameters(spacecraft, { length: '20', sheet_thickness: '0.7', panel_rows: '1.5' })
    expect(result).toEqual({
      errors: {
        length: 'Between 40 and 2000 mm.',
        sheet_thickness: 'Use one of 0.3, 0.5, 0.8, 1, 1.5, 2, 3 mm.',
        panel_rows: 'Enter a whole number.',
      },
    })
    expect(toParameters(spacecraft, { length: '', sheet_thickness: '1', panel_rows: 'x' })).toEqual({
      errors: { length: 'Enter a number.', panel_rows: 'Enter a number.' },
    })
  })

  it('parses the assembly component list', () => {
    expect(toParameters(assembly, { segments: '24', components: '[{"name":"a","shape":"box"}]' })).toEqual({
      parameters: { segments: 24, components: [{ name: 'a', shape: 'box' }] },
    })
    expect(toParameters(assembly, { segments: '24', components: '{oops' })).toEqual({ errors: { components: 'The component list is not valid JSON.' } })
    expect(toParameters(assembly, { segments: '24', components: '[]' })).toEqual({ errors: { components: 'Enter a JSON list of components.' } })
  })

  it('maps server fields to form fields', () => {
    expect(fieldOf('parameters.height')).toBe('height')
    expect(fieldOf('parameters.components[2].shape')).toBe('components')
    expect(fieldOf('template')).toBeNull()
    expect(fieldOf(null)).toBeNull()
  })

  it('labels parameters', () => {
    expect(parameterLabel('sheet_thickness')).toBe('Sheet thickness')
  })
})
