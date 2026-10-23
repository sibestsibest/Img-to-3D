import type { ParametricParameter, ParametricTemplate } from '../types/partDesign'

/** Raw text per parameter (and "components" JSON for the assembly template). */
export type ParametricFormValues = Record<string, string>

export type ParametricFormErrors = Record<string, string>

/** Field holding the assembly template's component list as JSON text. */
export const COMPONENTS_FIELD = 'components'

export const EXAMPLE_COMPONENTS = [
  { name: 'base', shape: 'plate', size_x: 60, size_y: 40, thickness: 1.5, transform: { rotate_deg: [-90, 0, 0] } },
  { name: 'post', shape: 'rod', diameter: 3, length: 25, transform: { rotate_deg: [-90, 0, 0], translate: [0, 1.5, 0] } },
]

const usesComponents = (template: ParametricTemplate) => template.shapes !== undefined

/** "sheet_thickness" -> "Sheet thickness". */
export function parameterLabel(name: string): string {
  const words = name.replace(/_/g, ' ')
  return words.charAt(0).toUpperCase() + words.slice(1)
}

const text = (value: unknown) => (typeof value === 'number' && Number.isFinite(value) ? String(value) : '')

/** Defaults, or the values of an earlier project when making a variant of it. */
export function initialValues(template: ParametricTemplate, from?: Record<string, unknown> | null): ParametricFormValues {
  const values: ParametricFormValues = {}
  for (const p of template.parameters) values[p.name] = text(from?.[p.name]) || String(p.default)
  if (usesComponents(template)) {
    const components = Array.isArray(from?.[COMPONENTS_FIELD]) ? from[COMPONENTS_FIELD] : EXAMPLE_COMPONENTS
    values[COMPONENTS_FIELD] = JSON.stringify(components, null, 2)
  }
  return values
}

function checkNumber(p: ParametricParameter, raw: string): number | string {
  const value = Number(raw.trim().replace(',', '.'))
  if (raw.trim() === '' || !Number.isFinite(value)) return 'Enter a number.'
  if (p.type === 'integer' && !Number.isInteger(value)) return 'Enter a whole number.'
  if (p.allowed) {
    const match = p.allowed.find((a) => Math.abs(a - value) < 1e-9)
    return match ?? `Use one of ${p.allowed.join(', ')} ${p.unit}.`.trimEnd()
  }
  if (value < p.minimum || value > p.maximum) return `Between ${p.minimum} and ${p.maximum} ${p.unit}`.trimEnd() + '.'
  return value
}

/** Validates like the worker does (ranges, whole numbers, sheet thicknesses) so most mistakes never leave the browser. */
export function toParameters(template: ParametricTemplate, values: ParametricFormValues): { parameters: Record<string, unknown> } | { errors: ParametricFormErrors } {
  const parameters: Record<string, unknown> = {}
  const errors: ParametricFormErrors = {}
  for (const p of template.parameters) {
    const checked = checkNumber(p, values[p.name] ?? '')
    if (typeof checked === 'string') errors[p.name] = checked
    else parameters[p.name] = checked
  }
  if (usesComponents(template)) {
    try {
      const components: unknown = JSON.parse(values[COMPONENTS_FIELD] ?? '')
      if (!Array.isArray(components) || components.length === 0) errors[COMPONENTS_FIELD] = 'Enter a JSON list of components.'
      else parameters[COMPONENTS_FIELD] = components
    } catch {
      errors[COMPONENTS_FIELD] = 'The component list is not valid JSON.'
    }
  }
  return Object.keys(errors).length ? { errors } : { parameters }
}

/** The form field a server error points at: "parameters.height" -> "height", "parameters.components[2].shape" -> "components". */
export function fieldOf(serverField: string | null | undefined): string | null {
  if (!serverField?.startsWith('parameters.')) return null
  const rest = serverField.slice('parameters.'.length)
  return rest.split(/[.[]/)[0] || null
}
