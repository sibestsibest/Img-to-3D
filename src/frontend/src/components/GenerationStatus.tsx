import { describeJob, STAGES } from '../services/stages'
import type { Job } from '../types/api'

interface Props {
  job: Job
  onCancel: () => void
  cancelling: boolean
}

export function GenerationStatus({ job, onCancel, cancelling }: Props) {
  const active = job.status === 'Pending' || job.status === 'Processing'
  const currentIndex = STAGES.findIndex((s) => s.key === job.stage)
  const elapsed = job.startedAt ? Math.max(0, ((job.completedAt ? Date.parse(job.completedAt) : Date.now()) - Date.parse(job.startedAt)) / 1000) : null

  return (
    <section className="card" aria-live="polite">
      <div className="status-header">
        <span className={`badge badge--${job.status.toLowerCase()}`}>{job.status}</span>
        <span className="status-text">{describeJob(job)}</span>
      </div>

      {active && (
        <>
          {job.stageProgress != null ? (
            <progress className="progress" value={job.stageProgress} max={1} aria-label="Stage progress" />
          ) : (
            <div className="progress progress--indeterminate" role="progressbar" aria-label="Working" />
          )}
          <ol className="stages">
            {STAGES.filter((s) => s.key !== 'Submitting' || job.stage === 'Submitting').map((stage) => {
              const index = STAGES.indexOf(stage)
              const state = currentIndex < 0 ? 'todo' : index < currentIndex ? 'done' : index === currentIndex ? 'current' : 'todo'
              return (
                <li key={stage.key} className={`stage stage--${state}`}>
                  {stage.label}
                </li>
              )
            })}
          </ol>
          <p className="muted small">
            Progress is only shown as a percentage for the density-grid step, where it is actually measured. On CPU a
            generation typically takes under a minute; the first run also loads the model.
          </p>
          <button type="button" className="btn btn--secondary" onClick={onCancel} disabled={cancelling}>
            {cancelling ? 'Cancelling…' : 'Cancel generation'}
          </button>
        </>
      )}

      {job.status === 'Failed' && (
        <p className="error-text">
          {job.error}
          {job.errorCode === 'no_foreground' || job.errorCode === 'no_surface'
            ? ' Tip: crop to a single part and use a plain background.'
            : job.errorCode === 'inference_unavailable'
              ? ' Check that the inference service is running, then try again.'
              : ''}
        </p>
      )}

      {elapsed != null && !active && <p className="muted small">Took {elapsed.toFixed(0)} s</p>}
    </section>
  )
}
