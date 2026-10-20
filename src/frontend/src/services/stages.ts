import type { Job } from '../types/api'

/** Pipeline stages reported by the inference service, in execution order. */
export const STAGES: { key: string; label: string }[] = [
  { key: 'Submitting', label: 'Sending image to the generator' },
  { key: 'Queued', label: 'Waiting in the generation queue' },
  { key: 'LoadingModels', label: 'Loading the AI model' },
  { key: 'PreparingImage', label: 'Preparing image' },
  { key: 'RemovingBackground', label: 'Removing background' },
  { key: 'EncodingImage', label: 'Running TripoSR image encoder' },
  // Stable Fast 3D / SPAR3D run encoder, mesh extraction and texture baking as one model call.
  { key: 'GeneratingMesh', label: 'Generating textured mesh (Stable Fast 3D / SPAR3D)' },
  { key: 'EvaluatingDensity', label: 'Evaluating 3D density grid' },
  { key: 'ExtractingSurface', label: 'Extracting surface (marching cubes)' },
  { key: 'ColoringVertices', label: 'Colouring vertices' },
  { key: 'Exporting', label: 'Projecting photos onto the texture, exporting mesh' },
  { key: 'Finalizing', label: 'Creating GLB and STL files' },
]

export function stageLabel(stage: string | null): string {
  return STAGES.find((s) => s.key === stage)?.label ?? 'Starting'
}

/** Human description of where a job is. Progress is shown only when the backend measured it. */
export function describeJob(job: Job): string {
  switch (job.status) {
    case 'Pending':
      return 'Waiting to start'
    case 'Processing': {
      const label = stageLabel(job.stage)
      return job.stageProgress != null ? `${label} — ${Math.round(job.stageProgress * 100)}%` : label
    }
    case 'Succeeded':
      return 'Model ready'
    case 'Cancelled':
      return 'Cancelled'
    case 'Failed':
      return job.error ?? 'Generation failed'
  }
}
