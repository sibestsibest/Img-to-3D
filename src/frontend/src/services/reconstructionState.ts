import type { Reconstruction, ReconstructionRoute, ReconstructionState, RouteOption } from '../types/references'

const ROUTE_LABELS: Record<ReconstructionRoute, string> = {
  reuse_existing_model: 'Reuse the accepted model',
  multi_view_reconstruction: 'Multi-view reconstruction',
  hybrid_reconstruction: 'Generate with extra photos',
  single_image_reconstruction: 'Generate from the input photo',
  manual_geometry_required: 'Build by hand from stock shapes',
}

const ROUTE_HINTS: Record<ReconstructionRoute, string> = {
  reuse_existing_model: 'Opens the model in the part designer as is. No AI generation runs.',
  multi_view_reconstruction:
    'Meshroom reconstructs the shape from the views themselves (photogrammetry). Needs Meshroom and at least 8 same-object pictures; your own photos on the Photogrammetry page work much better.',
  hybrid_reconstruction: 'Shape from the input photo; accepted same-object pictures colour the hidden sides.',
  single_image_reconstruction: 'Shape from the input photo alone; hidden sides are guessed.',
  manual_geometry_required: 'Empty part design: add plates, rods, tubes and blocks with exact sizes.',
}

const STATE_LABELS: Record<ReconstructionState, string> = {
  reference_search: 'Collecting references',
  model_candidate_review: 'Reviewing model candidates',
  reuse_existing_model: 'Reusing a model',
  multi_view_reconstruction: 'Multi-view reconstruction',
  hybrid_reconstruction: 'Generating (with extra photos)',
  single_image_reconstruction: 'Generating (input photo)',
  manual_geometry_required: 'Manual geometry required',
  completed: 'Completed',
  failed: 'Failed',
  cancelled: 'Cancelled',
}

export const routeLabel = (route: ReconstructionRoute) => ROUTE_LABELS[route] ?? route

export const routeHint = (route: ReconstructionRoute) => ROUTE_HINTS[route] ?? ''

export const stateLabel = (state: ReconstructionState) => STATE_LABELS[state] ?? state

/** Status pill tone for a state. */
export function stateTone(state: ReconstructionState): 'valid' | 'warning' | 'invalid' | 'neutral' {
  if (state === 'completed') return 'valid'
  if (state === 'failed') return 'invalid'
  if (state === 'cancelled' || state === 'manual_geometry_required') return 'warning'
  return 'neutral'
}

// Generation jobs report "Succeeded"; photogrammetry jobs "succeeded".
const FINISHED = new Set(['succeeded', 'failed', 'cancelled'])

/** A generation or photogrammetry run started from this search is still going (the panel polls until it is not). */
export const isGenerating = (r: Reconstruction) =>
  (r.state === 'single_image_reconstruction' || r.state === 'hybrid_reconstruction' || r.state === 'multi_view_reconstruction') &&
  r.jobStatus !== null &&
  !FINISHED.has(r.jobStatus.toLowerCase())

export const blockers = (option: RouteOption) => option.reasons.filter((r) => r.severity === 'blocker')

/** Only routes that can run, and not while a generation is still going. */
export const canStart = (r: Reconstruction, option: RouteOption) => option.availability === 'available' && !isGenerating(r)

export const runsInference = (route: ReconstructionRoute) => route === 'single_image_reconstruction' || route === 'hybrid_reconstruction'

/** The multi-view route runs a Meshroom photogrammetry job (followed on the Photogrammetry page). */
export const runsPhotogrammetry = (route: ReconstructionRoute) => route === 'multi_view_reconstruction'

export function startLabel(route: ReconstructionRoute): string {
  if (runsPhotogrammetry(route)) return 'Start reconstruction'
  return runsInference(route) ? 'Start generation' : 'Create part design'
}
