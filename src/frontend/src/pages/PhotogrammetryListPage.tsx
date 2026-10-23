import { useCallback, useEffect, useState } from 'react'
import { ErrorBanner } from '../components/ErrorBanner'
import { OriginBadge } from '../components/OriginBadge'
import { photogrammetryApi } from '../services/apiClient'
import { statusLabel, statusTone } from '../services/photogrammetryState'
import type { PhotogrammetryEngine, PhotogrammetrySummary } from '../types/photogrammetry'

/** Photo sets for Meshroom photogrammetry, and whether Meshroom is usable here. */
export function PhotogrammetryListPage({ onOpen }: { onOpen: (id: string) => void }) {
  const [engine, setEngine] = useState<PhotogrammetryEngine | null>(null)
  const [items, setItems] = useState<PhotogrammetrySummary[] | null>(null)
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  const load = useCallback(async () => {
    try {
      const [e, list] = await Promise.all([photogrammetryApi.engine(), photogrammetryApi.list()])
      setEngine(e)
      setItems(list.items)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to load.')
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const create = async () => {
    setCreating(true)
    try {
      onOpen((await photogrammetryApi.create(name.trim() || undefined)).id)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not create the photo set.')
    } finally {
      setCreating(false)
    }
  }

  const remove = async (item: PhotogrammetrySummary) => {
    if (!window.confirm(`Delete "${item.name}" with its ${item.photoCount} photo(s) and results?`)) return
    try {
      await photogrammetryApi.remove(item.id)
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Delete failed.')
    }
  }

  return (
    <div className="workspace workspace--single">
      <section className="card">
        <h2>
          Photogrammetry <OriginBadge origin="photogrammetry" />
        </h2>
        <p className="muted small">
          Reconstructs a real object from many overlapping photos with Meshroom (AliceVision), locally. Unlike the AI generator nothing is guessed: what the
          photos do not show is missing. Walk around the object (20–200 photos, each part visible in at least three), keep the focus sharp and the
          lighting even; matt, textured surfaces work best.
        </p>
        <ErrorBanner message={error} onDismiss={() => setError(null)} />
        {engine && (
          <div className={`photo__engine photo__engine--${engine.status}`}>
            {engine.status === 'ready' ? (
              <p className="small">
                Meshroom {engine.version ?? '(version unknown)'} ready · {engine.cuda ? `CUDA GPU ${engine.gpu ?? ''}` : 'no CUDA GPU'} · default pipeline{' '}
                <strong>{engine.defaultPipeline}</strong>
              </p>
            ) : (
              <>
                <p className="error-text">{engine.reason}</p>
                <p className="muted small">
                  Install Meshroom 2025.1 or newer from github.com/alicevision/meshroom/releases and set <code>Photogrammetry:MeshroomPath</code> (for
                  example the environment variable <code>Photogrammetry__MeshroomPath</code>) to its folder, then restart the API. You can still collect
                  photos now.
                </p>
              </>
            )}
            {engine.notes.map((n) => (
              <p key={n} className="muted small">
                {n}
              </p>
            ))}
          </div>
        )}
        <form
          className="part-props__actions"
          onSubmit={(e) => {
            e.preventDefault()
            void create()
          }}
        >
          <input value={name} maxLength={100} placeholder="Name of the object" onChange={(e) => setName(e.target.value)} aria-label="Name" />
          <button type="submit" className="btn btn--small" disabled={creating}>
            {creating ? 'Creating…' : 'New photo set'}
          </button>
        </form>
      </section>

      <section className="card">
        <h2>Photo sets</h2>
        {items == null ? (
          <p className="muted">Loading…</p>
        ) : items.length === 0 ? (
          <p className="muted">No photo sets yet.</p>
        ) : (
          <ul className="history">
            {items.map((item) => (
              <li key={item.id} className="history__item">
                <button type="button" className="history__main" onClick={() => onOpen(item.id)}>
                  <span className="history__text">
                    <span className="history__name">
                      {item.name} <span className={`status-pill status-pill--${statusTone(item.status)}`}>{statusLabel(item.status)}</span>
                    </span>
                    <span className="muted small">
                      {item.photoCount} photo(s){item.pipeline ? ` · ${item.pipeline}` : ''} · created {new Date(item.createdAt).toLocaleString()}
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
