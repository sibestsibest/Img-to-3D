import { useCallback, useEffect, useState } from 'react'
import { ErrorBanner } from '../components/ErrorBanner'
import { designApi } from '../services/apiClient'
import type { PartDesignSummary } from '../types/partDesign'

export function PartDesignListPage({ onOpen, onBack }: { onOpen: (id: string) => void; onBack: () => void }) {
  const [items, setItems] = useState<PartDesignSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      setItems((await designApi.list()).items)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load projects.')
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const remove = async (item: PartDesignSummary) => {
    if (!window.confirm(`Delete the part design "${item.name}"? Its parts and history are removed; the generated model is not affected.`)) return
    try {
      await designApi.remove(item.id)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Delete failed.')
    }
  }

  const [creating, setCreating] = useState(false)
  const createBlank = async () => {
    setCreating(true)
    try {
      onOpen((await designApi.createBlank()).id)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not create the project.')
    } finally {
      setCreating(false)
    }
  }

  return (
    <div className="workspace workspace--single">
      <section className="card">
        <div className="part-tree__header">
          <h2>Part design projects</h2>
          <div className="part-props__actions">
            <button type="button" className="btn btn--small" disabled={creating} onClick={createBlank} title="For parts built from plates, rods, tubes and blocks">
              {creating ? 'Creating…' : 'New empty project'}
            </button>
            <button type="button" className="btn btn--small" onClick={onBack}>
              Back to generator
            </button>
          </div>
        </div>
        <ErrorBanner message={error} onDismiss={() => setError(null)} />
        {items == null ? (
          <p className="muted">Loading…</p>
        ) : items.length === 0 ? (
          <p className="muted">No projects yet. Generate a model and choose “Open in part designer”, reuse a model from a reference search, or start an empty project.</p>
        ) : (
          <ul className="history">
            {items.map((item) => (
              <li key={item.id} className="history__item">
                <button type="button" className="history__main" onClick={() => onOpen(item.id)}>
                  <span className="history__text">
                    <span className="history__name">{item.name}</span>
                    <span className="muted small">
                      {item.partCount} part(s) · {item.sourceKind === 'none' ? 'built from stock shapes' : `from ${item.sourceFileName}`} · updated{' '}
                      {new Date(item.updatedAt).toLocaleString()}
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
