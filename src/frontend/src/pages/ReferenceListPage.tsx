import { useCallback, useEffect, useState } from 'react'
import { ErrorBanner } from '../components/ErrorBanner'
import { referenceApi, urls } from '../services/apiClient'
import type { ReferenceSessionSummary } from '../types/references'

export function ReferenceListPage({ onOpen, onBack }: { onOpen: (id: string) => void; onBack: () => void }) {
  const [items, setItems] = useState<ReferenceSessionSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [image, setImage] = useState<File | null>(null)
  const [creating, setCreating] = useState(false)

  const load = useCallback(async () => {
    try {
      setItems((await referenceApi.list()).items)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load reference searches.')
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const create = async (e: React.FormEvent) => {
    e.preventDefault()
    setCreating(true)
    try {
      onOpen((await referenceApi.create({ query: query.trim() || undefined, image: image ?? undefined })).id)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not start the search.')
    } finally {
      setCreating(false)
    }
  }

  const remove = async (item: ReferenceSessionSummary) => {
    if (!window.confirm(`Delete the reference search "${item.name}" and its ${item.candidateCount} picture(s)?`)) return
    try {
      await referenceApi.remove(item.id)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Delete failed.')
    }
  }

  return (
    <div className="workspace workspace--single">
      <section className="card">
        <div className="part-tree__header">
          <h2>Reference search</h2>
          <button type="button" className="btn btn--small" onClick={onBack}>
            Back to generator
          </button>
        </div>
        <p className="muted small">
          Before generating a model, look for existing pictures of the object (other views, blueprints, box art) in free sources:
          Wikimedia Commons, Openverse and Internet Archive. No account or paid service is used.
        </p>
        <form className="ref-new" onSubmit={create}>
          <label className="field">
            <span>What are you looking for?</span>
            <input value={query} maxLength={200} onChange={(e) => setQuery(e.target.value)} placeholder="e.g. RX-78-2 Gundam backpack" disabled={creating} />
          </label>
          <label className="field">
            <span>Your photo of it (optional)</span>
            <input type="file" accept="image/png,image/jpeg,image/webp" disabled={creating} onChange={(e) => setImage(e.target.files?.[0] ?? null)} />
          </label>
          <button type="submit" className="btn btn--primary" disabled={creating || (!query.trim() && !image)}>
            {creating ? 'Creating…' : 'Start reference search'}
          </button>
        </form>
        <ErrorBanner message={error} onDismiss={() => setError(null)} />
      </section>

      <section className="card">
        <h2>Your searches</h2>
        {items == null ? (
          <p className="muted">Loading…</p>
        ) : items.length === 0 ? (
          <p className="muted">No reference searches yet.</p>
        ) : (
          <ul className="history">
            {items.map((item) => (
              <li key={item.id} className="history__item">
                <button type="button" className="history__main" onClick={() => onOpen(item.id)}>
                  {item.hasInputImage && <img className="history__thumb" src={urls.referenceInput(item.id)} alt="" />}
                  <span className="history__text">
                    <span className="history__name">{item.name}</span>
                    <span className="muted small">
                      {item.candidateCount} picture(s), {item.acceptedCount} accepted · updated {new Date(item.updatedAt).toLocaleString()}
                    </span>
                  </span>
                </button>
                <div className="history__actions">
                  <button type="button" className="link-danger" onClick={() => remove(item)}>
                    Delete
                  </button>
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  )
}
