import { useEffect, useState } from 'react'
import { designApi } from '../../services/apiClient'
import { formatMm } from '../../services/geometry'
import { axisRange, formatVolume, planeFor, type Axis } from '../../services/partDesignState'
import type { DesignPart, SplitPreview } from '../../types/partDesign'
import { IssueList } from './PartPropertiesPanel'
import type { PlanePreview } from './PartViewport'

interface Props {
  projectId: string
  part: DesignPart
  busy: boolean
  onPlaneChange: (plane: PlanePreview | null) => void
  onSplit: (axis: Axis, offsetMm: number, allowOpen: boolean) => Promise<void>
}

/** Axis-aligned cutting plane through the selected part, with a server-computed preview before applying. */
export function SplitPanel({ projectId, part, busy, onPlaneChange, onSplit }: Props) {
  const [axis, setAxis] = useState<Axis>('y')
  const [offset, setOffset] = useState(0)
  const [preview, setPreview] = useState<SplitPreview | null>(null)
  const [previewing, setPreviewing] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [allowOpen, setAllowOpen] = useState(false)
  const [min, max] = axisRange(part.geometry.bounds, axis)

  // Start in the middle of the part whenever the part or axis changes.
  useEffect(() => {
    const [lo, hi] = axisRange(part.geometry.bounds, axis)
    setOffset(Number(((lo + hi) / 2).toFixed(2)))
  }, [part.id, axis]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    setPreview(null)
    setError(null)
    setAllowOpen(false)
    onPlaneChange({ partId: part.id, axis, offsetMm: offset })
  }, [part.id, axis, offset, onPlaneChange])

  useEffect(() => () => onPlaneChange(null), [onPlaneChange])

  const runPreview = async () => {
    setPreviewing(true)
    setError(null)
    try {
      setPreview(await designApi.previewSplit(projectId, part.id, planeFor(axis, offset)))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Preview failed.')
    } finally {
      setPreviewing(false)
    }
  }

  const step = Math.max((max - min) / 200, 0.01)
  const canApply = preview != null && preview.below != null && preview.above != null && (preview.completeSolidSplit || allowOpen)

  return (
    <section className="card">
      <h2>Split with a plane</h2>
      <div className="split-form">
        <label>
          Axis
          <select value={axis} onChange={(e) => setAxis(e.target.value as Axis)} disabled={busy}>
            <option value="x">X (width)</option>
            <option value="y">Y (height)</option>
            <option value="z">Z (depth)</option>
          </select>
        </label>
        <label>
          at
          <input type="number" step="any" value={offset} onChange={(e) => setOffset(Number(e.target.value))} disabled={busy} />
          mm
        </label>
      </div>
      <input
        type="range"
        className="split-form__slider"
        min={min}
        max={max}
        step={step}
        value={offset}
        onChange={(e) => setOffset(Number(e.target.value))}
        disabled={busy}
        aria-label="Plane position"
      />
      <p className="muted small">
        Part spans {formatMm(min)} to {formatMm(max)} mm on {axis.toUpperCase()}. The red plane in the viewport shows the cut.
      </p>

      <div className="part-props__actions">
        <button type="button" className="btn" onClick={runPreview} disabled={busy || previewing}>
          {previewing ? 'Computing…' : 'Preview split'}
        </button>
        <button type="button" className="btn btn--primary" disabled={busy || !canApply} onClick={() => onSplit(axis, offset, allowOpen)}>
          Apply split
        </button>
      </div>
      {error && <p className="field-error">{error}</p>}

      {preview && (
        <div className="split-preview">
          <p className={preview.completeSolidSplit ? 'success-text' : 'error-text'}>
            {preview.completeSolidSplit
              ? `Two closed, capped solids. ${preview.closedContours} contour(s), ${formatMm(preview.contourLengthMm)} mm of cut edge.`
              : 'The halves would not both be closed solids.'}
          </p>
          {preview.below && preview.above && (
            <table className="dims">
              <thead>
                <tr>
                  <th />
                  <th>Below (A)</th>
                  <th>Above (B)</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <th>Closed</th>
                  <td>{preview.below.closed ? 'yes' : 'no'}</td>
                  <td>{preview.above.closed ? 'yes' : 'no'}</td>
                </tr>
                <tr>
                  <th>Volume</th>
                  <td>{formatVolume(preview.below.volumeMm3)}</td>
                  <td>{formatVolume(preview.above.volumeMm3)}</td>
                </tr>
                <tr>
                  <th>Cap area</th>
                  <td>{formatMm(preview.below.capAreaMm2)} mm²</td>
                  <td>{formatMm(preview.above.capAreaMm2)} mm²</td>
                </tr>
              </tbody>
            </table>
          )}
          <IssueList issues={preview.issues} empty="" />
          {!preview.completeSolidSplit && preview.below && preview.above && (
            <label className="toggle">
              <input type="checkbox" checked={allowOpen} onChange={(e) => setAllowOpen(e.target.checked)} />
              Keep the open halves anyway (they will be marked as not closed)
            </label>
          )}
        </div>
      )}
    </section>
  )
}
