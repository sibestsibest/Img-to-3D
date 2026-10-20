import { useEffect, useMemo, useState, type FormEvent } from 'react'
import {
  AXIS_LABELS,
  axisLength,
  formatMm,
  scaleDimensions,
  uniformScaleFactor,
  validateTargetMm,
} from '../services/geometry'
import type { Job, ScaleAxis } from '../types/api'

interface Props {
  job: Job
  onApply: (axis: ScaleAxis, targetMm: number) => Promise<void>
}

export function DimensionsPanel({ job, onApply }: Props) {
  const dims = job.dimensionsMm
  const [axis, setAxis] = useState<ScaleAxis>(job.scale?.axis ?? 'Longest')
  const [target, setTarget] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (dims) setTarget(formatMm(axisLength(dims, axis)))
    // Reset the input only when the job or axis changes, not on every poll.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [job.id, axis, job.scale?.mmPerModelUnit])

  const validation = validateTargetMm(target)
  const previewDims = useMemo(() => {
    if (!dims || validation) return null
    return scaleDimensions(dims, uniformScaleFactor(dims, axis, Number(target)))
  }, [dims, axis, target, validation])

  if (!dims) return null

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    if (validation) return
    setBusy(true)
    setError(null)
    try {
      await onApply(axis, Number(target))
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Scaling failed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section className="card">
      <h2>Dimensions &amp; scale</h2>
      <table className="dims">
        <thead>
          <tr>
            <th />
            <th>Width (X)</th>
            <th>Height (Y)</th>
            <th>Depth (Z)</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <th>Current</th>
            <td>{formatMm(dims.x)}</td>
            <td>{formatMm(dims.y)}</td>
            <td>{formatMm(dims.z)}</td>
          </tr>
          {previewDims && (
            <tr className="dims__preview">
              <th>After scaling</th>
              <td>{formatMm(previewDims.x)}</td>
              <td>{formatMm(previewDims.y)}</td>
              <td>{formatMm(previewDims.z)}</td>
            </tr>
          )}
        </tbody>
      </table>
      <p className="muted small">All values in millimetres (bounding box). STL export is Z-up, so Height becomes Z in your slicer.</p>

      <form className="scale-form" onSubmit={submit}>
        <label>
          Set
          <select value={axis} onChange={(e) => setAxis(e.target.value as ScaleAxis)} disabled={busy}>
            {(Object.keys(AXIS_LABELS) as ScaleAxis[]).map((a) => (
              <option key={a} value={a}>
                {AXIS_LABELS[a]}
              </option>
            ))}
          </select>
        </label>
        <label>
          to
          <input
            type="number"
            inputMode="decimal"
            min={0.1}
            max={10000}
            step="any"
            value={target}
            onChange={(e) => setTarget(e.target.value)}
            disabled={busy}
            aria-invalid={validation != null}
          />
          mm
        </label>
        <button type="submit" className="btn" disabled={busy || validation != null}>
          {busy ? 'Applying…' : 'Apply uniform scale'}
        </button>
      </form>
      {validation && target !== '' && <p className="field-error">{validation}</p>}
      {error && <p className="field-error">{error}</p>}
      <p className="note">
        Scaling is uniform: it changes overall size only. It cannot fix holes, slots, peg joints or hidden geometry that the AI
        estimated incorrectly, and the result is not dimensionally identical to the real part.
      </p>
    </section>
  )
}
