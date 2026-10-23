import { useCallback, useEffect, useState } from 'react'
import { referenceApi } from '../../services/apiClient'
import { blockers, canStart, isGenerating, routeHint, routeLabel, runsInference, runsPhotogrammetry, startLabel, stateLabel, stateTone } from '../../services/reconstructionState'
import type { Reconstruction, RouteOption } from '../../types/references'
import { ErrorBanner } from '../ErrorBanner'

interface Props {
  sessionId: string
  /** Changes whenever the session changes (accepting a model or picture changes the plan). */
  version: string
  onOpenDesign: (designId: string) => void
  onOpenJob: (jobId: string) => void
}

const POLL_MS = 3000

/**
 * The reconstruction decision: every way to get geometry in preference order (reuse first,
 * generation only when nothing suitable exists), why each is or is not possible, and the state of
 * whatever was started.
 */
export function ReconstructionPanel({ sessionId, version, onOpenDesign, onOpenJob }: Props) {
  const [plan, setPlan] = useState<Reconstruction | null>(null)
  const [starting, setStarting] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(
    (signal?: AbortSignal) =>
      referenceApi
        .reconstruction(sessionId, signal)
        .then(setPlan)
        .catch((e) => !signal?.aborted && setError(e instanceof Error ? e.message : 'Could not load the decision.')),
    [sessionId],
  )

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [load, version])

  const generating = plan != null && isGenerating(plan)
  useEffect(() => {
    if (!generating) return
    const timer = window.setInterval(() => void load(), POLL_MS)
    return () => window.clearInterval(timer)
  }, [generating, load])

  const start = async (option: RouteOption) => {
    setStarting(option.route)
    setError(null)
    try {
      const next = await referenceApi.startReconstruction(sessionId, option.route, option.modelId ? { modelId: option.modelId } : {})
      setPlan(next)
      if (next.designId && !runsInference(option.route)) onOpenDesign(next.designId)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not start.')
    } finally {
      setStarting(null)
    }
  }

  if (!plan) {
    return (
      <section className="card">
        <h2>Get the geometry</h2>
        <ErrorBanner message={error} onDismiss={() => setError(null)} />
        {!error && <p className="muted small">Checking what is possible…</p>}
      </section>
    )
  }

  return (
    <section className="card recon">
      <div className="part-tree__header">
        <h2>Get the geometry</h2>
        <span className={`status-pill status-pill--${stateTone(plan.state)}`}>{stateLabel(plan.state)}</span>
      </div>
      <p className="muted small">Retrieval first, reuse second, generation third: an accepted existing model is reused instead of generating a new one.</p>

      {plan.route && (
        <div className="recon__current small">
          <strong>{routeLabel(plan.route)}</strong>
          {plan.startedAt && <span className="muted"> · started {new Date(plan.startedAt).toLocaleString()}</span>}
          {plan.jobStatus && <span className="muted"> · job {plan.jobStatus.toLowerCase()}</span>}
          {plan.extraReferenceIds.length > 0 && <span className="muted"> · {plan.extraReferenceIds.length} extra photo(s)</span>}
          {plan.message && <p className="recon__message">{plan.message}</p>}
          <div className="recon__links">
            {plan.jobId && plan.route && runsPhotogrammetry(plan.route) && (
              <a className="btn btn--small" href={`#/photogrammetry/${plan.jobId}`}>
                Open the reconstruction
              </a>
            )}
            {plan.jobId && plan.route && !runsPhotogrammetry(plan.route) && (
              <button type="button" className="btn btn--small" onClick={() => onOpenJob(plan.jobId!)}>
                Open the generation
              </button>
            )}
            {plan.designId && (
              <button type="button" className="btn btn--small" onClick={() => onOpenDesign(plan.designId!)}>
                Open the part design
              </button>
            )}
          </div>
        </div>
      )}

      <ol className="recon__routes">
        {plan.routes.map((option) => (
          <li key={option.route} className={`recon__route recon__route--${option.availability}${option.recommended ? ' recon__route--recommended' : ''}`}>
            <div className="recon__route-head">
              <strong>{routeLabel(option.route)}</strong>
              {option.recommended && <span className="tag">recommended</span>}
              {option.availability !== 'available' && <span className="muted small">{option.availability === 'unsupported' ? 'not supported' : 'not possible yet'}</span>}
            </div>
            <p className="muted small">{routeHint(option.route)}</p>
            <ul className="recon__reasons small">
              {(option.availability === 'available' ? option.reasons : blockers(option)).map((r) => (
                <li key={r.code} className={`recon__reason recon__reason--${r.severity}`}>
                  {r.message}
                </li>
              ))}
            </ul>
            {option.availability === 'available' && (
              <button
                type="button"
                className={option.recommended ? 'btn btn--primary btn--small' : 'btn btn--small'}
                disabled={!canStart(plan, option) || starting !== null}
                onClick={() => void start(option)}
              >
                {starting === option.route ? 'Starting…' : startLabel(option.route)}
              </button>
            )}
          </li>
        ))}
      </ol>

      {plan.notes.length > 0 && (
        <ul className="recon__notes small muted">
          {plan.notes.map((n) => (
            <li key={n.code}>{n.message}</li>
          ))}
        </ul>
      )}
      {!plan.engineReachable && <p className="small muted">The inference service is not reachable right now.</p>}
      <ErrorBanner message={error} onDismiss={() => setError(null)} />
    </section>
  )
}
