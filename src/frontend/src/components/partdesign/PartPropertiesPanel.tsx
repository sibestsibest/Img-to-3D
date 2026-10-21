import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { formatMm } from '../../services/geometry'
import {
  CATEGORY_FIELDS,
  CATEGORY_LABELS,
  formatArea,
  formatVolume,
  formFromPart,
  NUMERIC_LIMITS,
  parseForm,
  type NumericField,
  type PartForm,
} from '../../services/partDesignState'
import type { DesignPart, Issue, PartCategory, UpdatePartRequest } from '../../types/partDesign'

interface Props {
  part: DesignPart
  busy: boolean
  onSave: (request: UpdatePartRequest) => Promise<void>
  onDuplicate: () => void
  onDelete: () => void
}

export function PartPropertiesPanel({ part, busy, onSave, onDuplicate, onDelete }: Props) {
  const [form, setForm] = useState<PartForm>(() => formFromPart(part))
  const [errors, setErrors] = useState<ReturnType<typeof parseForm>['errors']>({})

  // Reset when another part is selected or the server returns a new version (save, undo, redo).
  useEffect(() => {
    setForm(formFromPart(part))
    setErrors({})
  }, [part])

  const set = <K extends keyof PartForm>(key: K, value: PartForm[K]) => setForm((f) => ({ ...f, [key]: value }))
  const dirty = JSON.stringify(form) !== JSON.stringify(formFromPart(part))

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    const parsed = parseForm(form)
    setErrors(parsed.errors)
    if (parsed.request) await onSave(parsed.request)
  }

  const numberField = (field: NumericField, estimate?: number | null) => (
    <label key={field} className="field">
      <span>
        {NUMERIC_LIMITS[field].label} ({field === 'cutAngleDeg' ? '°' : 'mm'})
      </span>
      <span className="field__row">
        <input type="number" inputMode="decimal" step="any" value={form[field]} onChange={(e) => set(field, e.target.value)} aria-invalid={errors[field] != null} disabled={busy} />
        {estimate != null && (
          <button type="button" className="btn btn--small" title="Copy the estimate measured on the model" onClick={() => set(field, String(estimate))} disabled={busy}>
            use estimate {formatMm(estimate)}
          </button>
        )}
      </span>
      {errors[field] && <span className="field-error">{errors[field]}</span>}
    </label>
  )

  const estimateFor = (field: NumericField): number | null => {
    const e = part.estimated
    switch (field) {
      case 'thicknessMm':
        return e.thicknessMm
      case 'diameterMm':
      case 'outerDiameterMm':
        return e.outerDiameterMm
      case 'innerDiameterMm':
        return e.innerDiameterMm
      case 'cutLengthMm':
        return e.lengthMm
      default:
        return null
    }
  }

  const g = part.geometry
  return (
    <section className="card part-props">
      <div className="part-props__header">
        <h2>{part.name}</h2>
        <span className={`status-pill status-pill--${part.validation.status}`}>{part.validation.status}</span>
      </div>

      <form onSubmit={submit}>
        <div className="part-props__grid">
          <label className="field">
            <span>Name</span>
            <input value={form.name} onChange={(e) => set('name', e.target.value)} maxLength={100} disabled={busy} aria-invalid={errors.name != null} />
            {errors.name && <span className="field-error">{errors.name}</span>}
          </label>
          <label className="field">
            <span>Fabrication category</span>
            <select value={form.category} onChange={(e) => set('category', e.target.value as PartCategory)} disabled={busy}>
              {(Object.keys(CATEGORY_LABELS) as PartCategory[]).map((c) => (
                <option key={c} value={c}>
                  {CATEGORY_LABELS[c]}
                </option>
              ))}
            </select>
          </label>
        </div>

        <p className="muted small">
          Suggestion: <strong>{CATEGORY_LABELS[part.suggestion.category]}</strong> (confidence {part.suggestion.confidence.toFixed(2)}, advisory).{' '}
          {part.suggestion.reasons.join(' ')}
        </p>
        <label className="toggle">
          <input type="checkbox" checked={form.reviewStatus === 'user_confirmed'} onChange={(e) => set('reviewStatus', e.target.checked ? 'user_confirmed' : 'needs_review')} disabled={busy} />
          I have reviewed the category and fabrication method
        </label>

        <Section title="Estimated dimensions" note="Measured on the generated model at its current scale: an estimate, not a measurement of the real part.">
          <dl className="facts">
            <dt>Length</dt>
            <dd>{formatMm(part.estimated.lengthMm)} mm</dd>
            <dt>Width</dt>
            <dd>{formatMm(part.estimated.widthMm)} mm</dd>
            <dt>Thickness</dt>
            <dd>{formatMm(part.estimated.thicknessMm)} mm</dd>
            {part.estimated.outerDiameterMm != null && (
              <>
                <dt>Outer Ø</dt>
                <dd>{formatMm(part.estimated.outerDiameterMm)} mm</dd>
              </>
            )}
            {part.estimated.innerDiameterMm != null && (
              <>
                <dt>Inner Ø</dt>
                <dd>{formatMm(part.estimated.innerDiameterMm)} mm</dd>
              </>
            )}
          </dl>
        </Section>

        <Section title="Measured on the real part" note="Enter caliper measurements; these are marked user-confirmed.">
          <div className="part-props__grid">{(['confirmedLengthMm', 'confirmedWidthMm', 'confirmedHeightMm'] as NumericField[]).map((f) => numberField(f))}</div>
        </Section>

        <Section title="Manufacturing">
          <div className="part-props__grid">
            <label className="field">
              <span>Material</span>
              <input value={form.material} onChange={(e) => set('material', e.target.value)} maxLength={100} placeholder="e.g. Styrene (PS)" disabled={busy} />
            </label>
            <label className="field">
              <span>Method</span>
              <input value={form.manufacturingMethod} onChange={(e) => set('manufacturingMethod', e.target.value)} maxLength={100} placeholder="e.g. scribe and snap" disabled={busy} />
            </label>
            <label className="field">
              <span>Quantity</span>
              <input type="number" min={1} step={1} value={form.quantity} onChange={(e) => set('quantity', e.target.value)} disabled={busy} aria-invalid={errors.quantity != null} />
              {errors.quantity && <span className="field-error">{errors.quantity}</span>}
            </label>
            {CATEGORY_FIELDS[form.category].map((f) => numberField(f, estimateFor(f)))}
            {numberField('fitAllowanceMm')}
          </div>
          {form.category === 'stock_part' && (
            <div className="part-props__grid">
              <label className="field">
                <span>Commercial part</span>
                <input value={form.stockDescription} onChange={(e) => set('stockDescription', e.target.value)} maxLength={200} placeholder="type, size, maker" disabled={busy} />
              </label>
              <label className="field">
                <span>Source</span>
                <input value={form.stockSource} onChange={(e) => set('stockSource', e.target.value)} maxLength={200} placeholder="shop or catalogue" disabled={busy} />
              </label>
            </div>
          )}
          {form.category === 'custom_3d' && (
            <label className="toggle">
              <input type="checkbox" checked={form.needsFurtherModelling} onChange={(e) => set('needsFurtherModelling', e.target.checked)} disabled={busy} />
              Needs further manual modelling or 3D printing
            </label>
          )}
          <label className="field">
            <span>Notes</span>
            <textarea rows={2} value={form.notes} onChange={(e) => set('notes', e.target.value)} maxLength={2000} disabled={busy} />
          </label>
        </Section>

        <div className="part-props__actions">
          <button type="submit" className="btn btn--primary" disabled={busy || !dirty}>
            Save changes
          </button>
          <button type="button" className="btn" onClick={() => setForm(formFromPart(part))} disabled={busy || !dirty}>
            Discard
          </button>
          <span className="spacer" />
          <button type="button" className="btn" onClick={onDuplicate} disabled={busy}>
            Duplicate
          </button>
          <button type="button" className="btn link-danger" onClick={onDelete} disabled={busy}>
            Delete part
          </button>
        </div>
      </form>

      <Section title="Validation">
        <IssueList issues={part.validation.issues} empty="No problems found." />
      </Section>

      <Section title="Geometry" note="Rendering correctly does not make a part manufacturable; closed and manifold is required for solid exports.">
        <dl className="facts">
          <dt>Closed solid</dt>
          <dd>{g.closed ? 'yes' : `no (${g.boundaryEdges} open, ${g.nonManifoldEdges} non-manifold edges)`}</dd>
          <dt>Triangles</dt>
          <dd>{g.triangles.toLocaleString()}</dd>
          <dt>Pieces</dt>
          <dd>{g.components}</dd>
          <dt>Surface</dt>
          <dd>{formatArea(g.surfaceAreaMm2)}</dd>
          <dt>Volume</dt>
          <dd>{formatVolume(g.volumeMm3)}</dd>
          <dt>Box (X×Y×Z)</dt>
          <dd>
            {formatMm(g.bounds.size.x)} × {formatMm(g.bounds.size.y)} × {formatMm(g.bounds.size.z)} mm
          </dd>
        </dl>
      </Section>
    </section>
  )
}

function Section({ title, note, children }: { title: string; note?: string; children: ReactNode }) {
  return (
    <fieldset className="part-props__section">
      <legend>{title}</legend>
      {note && <p className="muted small">{note}</p>}
      {children}
    </fieldset>
  )
}

export function IssueList({ issues, empty }: { issues: Issue[]; empty: string }) {
  if (issues.length === 0) return <p className="muted small">{empty}</p>
  return (
    <ul className="issues">
      {issues.map((i, n) => (
        <li key={`${i.code}-${n}`} className={`issue issue--${i.severity}`}>
          <span className="issue__severity">{i.severity}</span> {i.message}
        </li>
      ))}
    </ul>
  )
}
