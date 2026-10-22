import { useState } from 'react'
import { defaultAxis, emptyPrimitiveForm, PRIMITIVE_FIELDS, PRIMITIVE_LABELS, toPrimitiveRequest, type PrimitiveForm } from '../../services/primitiveForm'
import type { AddPrimitiveRequest, PrimitiveKind } from '../../types/partDesign'

interface Props {
  busy: boolean
  /** Open by default in a project without a source model. */
  defaultOpen: boolean
  onAdd: (request: AddPrimitiveRequest) => Promise<boolean>
}

/** Adds a plate, rod, tube or block with exact sizes: for parts better built than reconstructed. */
export function PrimitivePanel({ busy, defaultOpen, onAdd }: Props) {
  const [form, setForm] = useState<PrimitiveForm>(emptyPrimitiveForm())
  const [error, setError] = useState<string | null>(null)
  const set = (patch: Partial<PrimitiveForm>) => setForm((f) => ({ ...f, ...patch }))

  const submit = async () => {
    const result = toPrimitiveRequest(form)
    if ('error' in result) {
      setError(result.error)
      return
    }
    setError(null)
    if (await onAdd(result.request)) setForm(emptyPrimitiveForm(form.kind))
  }

  return (
    <details className="card" open={defaultOpen}>
      <summary>Add a stock shape</summary>
      <p className="muted small">Exact sizes in mm, placed in the same coordinates as the viewer (Y up). Undo removes it again.</p>
      <form
        onSubmit={(e) => {
          e.preventDefault()
          void submit()
        }}
      >
        <label className="field">
          <span>Shape</span>
          <select value={form.kind} disabled={busy} onChange={(e) => set({ kind: e.target.value as PrimitiveKind, axis: defaultAxis(e.target.value as PrimitiveKind) })}>
            {(Object.keys(PRIMITIVE_LABELS) as PrimitiveKind[]).map((k) => (
              <option key={k} value={k}>
                {PRIMITIVE_LABELS[k]}
              </option>
            ))}
          </select>
        </label>
        <div className="primitive-form__dims">
          {PRIMITIVE_FIELDS[form.kind].map(({ field, label }) => (
            <label key={field} className="field">
              <span>{label} (mm)</span>
              <input inputMode="decimal" value={form[field]} disabled={busy} onChange={(e) => set({ [field]: e.target.value })} />
            </label>
          ))}
          {form.kind !== 'box' && (
            <label className="field">
              <span>{form.kind === 'plate' ? 'Thickness along' : 'Length along'}</span>
              <select value={form.axis} disabled={busy} onChange={(e) => set({ axis: e.target.value as 'x' | 'y' | 'z' })}>
                <option value="x">X</option>
                <option value="y">Y (up)</option>
                <option value="z">Z</option>
              </select>
            </label>
          )}
        </div>
        <div className="primitive-form__dims">
          {(['centerX', 'centerY', 'centerZ'] as const).map((key) => (
            <label key={key} className="field">
              <span>Centre {key.slice(-1)} (mm)</span>
              <input inputMode="decimal" value={form[key]} disabled={busy} onChange={(e) => set({ [key]: e.target.value })} />
            </label>
          ))}
        </div>
        <div className="primitive-form__dims">
          <label className="field">
            <span>Name (optional)</span>
            <input value={form.name} maxLength={100} disabled={busy} onChange={(e) => set({ name: e.target.value })} />
          </label>
          <label className="field">
            <span>Material (optional)</span>
            <input value={form.material} maxLength={100} placeholder={form.kind === 'box' ? '' : 'Polystyrene (PS)'} disabled={busy} onChange={(e) => set({ material: e.target.value })} />
          </label>
        </div>
        {error && <p className="error-text small">{error}</p>}
        <button type="submit" className="btn btn--small" disabled={busy}>
          Add shape
        </button>
      </form>
    </details>
  )
}
