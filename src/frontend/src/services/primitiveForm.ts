import type { AddPrimitiveRequest, PrimitiveKind } from '../types/partDesign'

/** Raw text of the stock-shape form; every size is millimetres. */
export interface PrimitiveForm {
  kind: PrimitiveKind
  name: string
  material: string
  lengthMm: string
  widthMm: string
  heightMm: string
  thicknessMm: string
  diameterMm: string
  outerDiameterMm: string
  innerDiameterMm: string
  axis: 'x' | 'y' | 'z'
  centerX: string
  centerY: string
  centerZ: string
}

type SizeField = 'lengthMm' | 'widthMm' | 'heightMm' | 'thicknessMm' | 'diameterMm' | 'outerDiameterMm' | 'innerDiameterMm'

/** Which sizes each shape needs, with their labels. */
export const PRIMITIVE_FIELDS: Record<PrimitiveKind, { field: SizeField; label: string }[]> = {
  plate: [
    { field: 'lengthMm', label: 'Length' },
    { field: 'widthMm', label: 'Width' },
    { field: 'thicknessMm', label: 'Thickness' },
  ],
  box: [
    { field: 'lengthMm', label: 'Length (X)' },
    { field: 'heightMm', label: 'Height (Y)' },
    { field: 'widthMm', label: 'Width (Z)' },
  ],
  rod: [
    { field: 'diameterMm', label: 'Diameter' },
    { field: 'lengthMm', label: 'Length' },
  ],
  tube: [
    { field: 'outerDiameterMm', label: 'Outer Ø' },
    { field: 'innerDiameterMm', label: 'Inner Ø' },
    { field: 'lengthMm', label: 'Length' },
  ],
}

export const PRIMITIVE_LABELS: Record<PrimitiveKind, string> = {
  plate: 'Plate (styrene sheet)',
  rod: 'Rod',
  tube: 'Tube',
  box: 'Block (custom)',
}

/** Default axis: the plate's thickness direction, the rod's or tube's length direction. */
export const defaultAxis = (kind: PrimitiveKind): 'x' | 'y' | 'z' => (kind === 'plate' ? 'y' : 'x')

export const emptyPrimitiveForm = (kind: PrimitiveKind = 'plate'): PrimitiveForm => ({
  kind,
  name: '',
  material: '',
  lengthMm: '',
  widthMm: '',
  heightMm: '',
  thicknessMm: '',
  diameterMm: '',
  outerDiameterMm: '',
  innerDiameterMm: '',
  axis: defaultAxis(kind),
  centerX: '0',
  centerY: '0',
  centerZ: '0',
})

const number = (text: string) => (text.trim() === '' ? Number.NaN : Number(text.replace(',', '.')))

/** Builds the request, or returns the first problem in words. Same limits as the server (0.05..5000 mm). */
export function toPrimitiveRequest(form: PrimitiveForm): { request: AddPrimitiveRequest } | { error: string } {
  const request: AddPrimitiveRequest = { kind: form.kind, units: 'mm' }
  for (const { field, label } of PRIMITIVE_FIELDS[form.kind]) {
    const value = number(form[field])
    if (!Number.isFinite(value)) return { error: `${label} is required.` }
    if (value < 0.05 || value > 5000) return { error: `${label} must be between 0.05 and 5000 mm.` }
    request[field] = value
  }
  if (form.kind === 'tube' && request.innerDiameterMm! >= request.outerDiameterMm!) return { error: 'The inner diameter must be smaller than the outer diameter.' }

  const center = [form.centerX, form.centerY, form.centerZ].map((t) => (t.trim() === '' ? 0 : number(t)))
  if (center.some((c) => !Number.isFinite(c))) return { error: 'The position must be numbers (mm).' }
  request.center = center as [number, number, number]
  if (form.kind !== 'box') request.axis = form.axis
  if (form.name.trim()) request.name = form.name.trim()
  if (form.material.trim()) request.material = form.material.trim()
  return { request }
}
