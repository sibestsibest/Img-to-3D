import { useState } from 'react'
import {
  emptyRelationForm,
  fitSummary,
  formFromRelation,
  openingLabel,
  RELATION_LABELS,
  RELATION_ROLES,
  toRelationBody,
  toRelationRequest,
  type RelationForm,
} from '../../services/assemblyState'
import type { CreateRelationRequest, DesignPart, PartRelation, RelationKind, RelationRequest } from '../../types/partDesign'
import { IssueList } from './PartPropertiesPanel'

interface Props {
  relations: PartRelation[]
  /** Active parts, to choose from and to name the related ones. */
  parts: DesignPart[]
  selectedId: string | null
  busy: boolean
  onAdd: (request: CreateRelationRequest) => Promise<boolean>
  onUpdate: (relationId: string, request: RelationRequest) => Promise<boolean>
  onRemove: (relationId: string) => void
  onSelectPart: (partId: string) => void
}

/** How parts are joined, with the fit each joint gets. Edits go through undo/redo like part edits. */
export function RelationsPanel({ relations, parts, selectedId, busy, onAdd, onUpdate, onRemove, onSelectPart }: Props) {
  const [form, setForm] = useState<RelationForm | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const name = (id: string) => parts.find((p) => p.id === id)?.name ?? '(deleted part)'

  const startAdd = () => {
    setEditing(null)
    setError(null)
    const other = parts.find((p) => p.id !== selectedId)
    setForm(emptyRelationForm(selectedId ?? parts[0]?.id ?? '', other?.id ?? ''))
  }

  const submit = async () => {
    if (!form) return
    if (editing) {
      const body = toRelationBody(form)
      if ('error' in body) return setError(body.error)
      if (await onUpdate(editing, body.request)) close()
      return
    }
    const result = toRelationRequest(form)
    if ('error' in result) return setError(result.error)
    if (await onAdd(result.request)) close()
  }

  const close = () => {
    setForm(null)
    setEditing(null)
    setError(null)
  }

  const set = (patch: Partial<RelationForm>) => setForm((f) => (f ? { ...f, ...patch } : f))
  const roles = form ? RELATION_ROLES[form.kind] : null
  const opening = form ? openingLabel(form.kind) : null

  return (
    <section className="card">
      <div className="part-tree__header">
        <h2>Relations</h2>
        {!form && (
          <button type="button" className="btn btn--small" disabled={busy || parts.length < 2} onClick={startAdd}>
            Add relation
          </button>
        )}
      </div>
      {relations.length === 0 && !form && (
        <p className="muted small">{parts.length < 2 ? 'Relations join two parts; add a second part first.' : 'No relations yet. Relate parts to check fits and allow intended overlaps.'}</p>
      )}
      {relations.length > 0 && (
        <ul className="relation-list">
          {relations.map((r) => (
            <li key={r.id} className={`relation relation--${r.validation.status}`}>
              <div>
                <button type="button" className="link-button" onClick={() => onSelectPart(r.partAId)}>
                  {name(r.partAId)}
                </button>{' '}
                <span className="tag">{RELATION_LABELS[r.kind]}</span>{' '}
                <button type="button" className="link-button" onClick={() => onSelectPart(r.partBId)}>
                  {name(r.partBId)}
                </button>
                {r.allowOverlap && <span className="muted small"> · overlap allowed</span>}
              </div>
              <div className="small">{fitSummary(r.fit)}</div>
              {r.note && <div className="muted small">{r.note}</div>}
              <IssueList issues={r.validation.issues.filter((i) => i.severity !== 'info')} empty="" />
              <div className="part-props__actions">
                <button
                  type="button"
                  className="btn btn--small"
                  disabled={busy}
                  onClick={() => {
                    setEditing(r.id)
                    setError(null)
                    setForm(formFromRelation(r))
                  }}
                >
                  Edit
                </button>
                <button type="button" className="btn btn--small" disabled={busy} onClick={() => onRemove(r.id)}>
                  Remove
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}

      {form && roles && (
        <form
          className="relation-form"
          onSubmit={(e) => {
            e.preventDefault()
            void submit()
          }}
        >
          <h3>{editing ? 'Edit relation' : 'New relation'}</h3>
          <label className="field">
            <span>Kind</span>
            <select value={form.kind} disabled={busy} onChange={(e) => set({ kind: e.target.value as RelationKind })}>
              {(Object.keys(RELATION_LABELS) as RelationKind[]).map((k) => (
                <option key={k} value={k}>
                  {RELATION_LABELS[k]}
                </option>
              ))}
            </select>
          </label>
          {(['partAId', 'partBId'] as const).map((key) => (
            <label key={key} className="field">
              <span>{key === 'partAId' ? roles.a : roles.b}</span>
              <select value={form[key]} disabled={busy || editing !== null} onChange={(e) => set({ [key]: e.target.value })}>
                <option value="">Choose a part…</option>
                {parts.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
            </label>
          ))}
          {opening && (
            <label className="field">
              <span>{opening} (mm, optional)</span>
              <input inputMode="decimal" value={form.openingSizeMm} disabled={busy} onChange={(e) => set({ openingSizeMm: e.target.value })} />
            </label>
          )}
          <label className="toggle">
            <input type="checkbox" checked={form.allowOverlap} disabled={busy} onChange={(e) => set({ allowOverlap: e.target.checked })} />
            The parts are meant to overlap (e.g. the hole is not modelled)
          </label>
          <label className="field">
            <span>Note (optional)</span>
            <input value={form.note} maxLength={500} disabled={busy} onChange={(e) => set({ note: e.target.value })} />
          </label>
          {error && <p className="error-text small">{error}</p>}
          <div className="part-props__actions">
            <button type="submit" className="btn btn--small btn--primary" disabled={busy}>
              {editing ? 'Save' : 'Add'}
            </button>
            <button type="button" className="btn btn--small" disabled={busy} onClick={close}>
              Cancel
            </button>
          </div>
        </form>
      )}
    </section>
  )
}
