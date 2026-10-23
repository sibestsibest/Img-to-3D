import type { GeometryOrigin } from '../types/api'

/** How each geometry origin is named and what it means for accuracy; shown wherever a model is shown. */
export const ORIGINS: Record<GeometryOrigin, { label: string; description: string }> = {
  ai_predicted: {
    label: 'AI-predicted',
    description: 'Shape predicted from photos by an AI model: hidden sides are guessed, details may be wrong and sizes are estimates.',
  },
  parametric: {
    label: 'Parametric',
    description: 'Generated from the parameters you entered: sizes are exact, nothing is estimated or predicted.',
  },
  photogrammetry: {
    label: 'Photogrammetry',
    description: 'Reconstructed from several photos of the real object: scale is unknown unless calibrated, and the mesh may have holes.',
  },
  uploaded: {
    label: 'Existing model',
    description: 'A model file you supplied: its accuracy and units depend on where it came from.',
  },
  manual: {
    label: 'Built by hand',
    description: 'Made from stock shapes with the sizes you entered.',
  },
}

export const originOf = (value: string | null | undefined): GeometryOrigin =>
  value && Object.hasOwn(ORIGINS, value) ? (value as GeometryOrigin) : 'manual'
