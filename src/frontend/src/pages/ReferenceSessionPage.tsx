import { useCallback, useEffect, useMemo, useState } from 'react'
import { ErrorBanner } from '../components/ErrorBanner'
import { AddPictureForm } from '../components/references/AddPictureForm'
import { CandidateCard } from '../components/references/CandidateCard'
import { referenceApi, urls } from '../services/apiClient'
import {
  allSourcesUnavailable,
  countByDecision,
  describeOutcome,
  filterCandidates,
  sourceStatusLabel,
  type CandidateFilter,
} from '../services/referenceState'
import type { ManualPicture, ReferenceSession, ReferenceSource, UpdateCandidateRequest } from '../types/references'

const FILTERS: { value: CandidateFilter; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'pending', label: 'To review' },
  { value: 'accepted', label: 'Accepted' },
  { value: 'rejected', label: 'Rejected' },
]

export function ReferenceSessionPage({ id, onBack }: { id: string; onBack: () => void }) {
  const [session, setSession] = useState<ReferenceSession | null>(null)
  const [sources, setSources] = useState<ReferenceSource[]>([])
  const [selected, setSelected] = useState<string[] | null>(null)
  const [query, setQuery] = useState('')
  const [searching, setSearching] = useState(false)
  const [busy, setBusy] = useState(false)
  const [filter, setFilter] = useState<CandidateFilter>('all')
  const [hideDuplicates, setHideDuplicates] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const fail = (e: unknown, fallback: string) => setError(e instanceof Error ? e.message : fallback)

  useEffect(() => {
    const controller = new AbortController()
    referenceApi
      .get(id, controller.signal)
      .then((s) => {
        setSession(s)
        setQuery(s.query)
      })
      .catch((e) => !controller.signal.aborted && fail(e, 'Failed to load the search.'))
    referenceApi
      .sources()
      .then((list) => {
        setSources(list)
        setSelected(list.filter((s) => s.enabled).map((s) => s.id))
      })
      .catch(() => setSources([]))
    return () => controller.abort()
  }, [id])

  const search = async () => {
    setSearching(true)
    setError(null)
    try {
      setSession(await referenceApi.search(id, query, selected ?? undefined))
    } catch (e) {
      fail(e, 'Search failed.')
    } finally {
      setSearching(false)
    }
  }

  const run = useCallback(async (action: () => Promise<ReferenceSession>, fallback: string) => {
    setBusy(true)
    try {
      setSession(await action())
      return true
    } catch (e) {
      setError(e instanceof Error ? e.message : fallback)
      return false
    } finally {
      setBusy(false)
    }
  }, [])

  const update = (candidateId: string, body: UpdateCandidateRequest) => run(() => referenceApi.updateCandidate(id, candidateId, body), 'Update failed.')
  const remove = (candidateId: string) => run(() => referenceApi.removeCandidate(id, candidateId), 'Remove failed.')
  const add = (picture: ManualPicture) => run(() => referenceApi.addPicture(id, picture), 'Could not add the picture.')

  const clearCache = async () => {
    try {
      const result = await referenceApi.clearCache()
      setNotice(`Cleared ${result.searchEntries} cached searches and ${result.files} downloads (${Math.round(result.bytes / 1024)} KB). Your pictures are kept.`)
    } catch (e) {
      fail(e, 'Could not clear the cache.')
    }
  }

  const candidates = useMemo(() => session?.candidates ?? [], [session])
  const counts = useMemo(() => countByDecision(candidates), [candidates])
  const visible = useMemo(() => filterCandidates(candidates, filter, hideDuplicates), [candidates, filter, hideDuplicates])
  const titles = useMemo(() => new Map(candidates.map((c) => [c.id, c.title])), [candidates])
  const hiddenDuplicates = filterCandidates(candidates, filter, false).length - visible.length

  if (!session) {
    return (
      <div className="workspace workspace--single">
        <section className="card">
          <ErrorBanner message={error} />
          <p className="muted">{error ? '' : 'Loading…'}</p>
          <button type="button" className="btn btn--small" onClick={onBack}>
            Back to searches
          </button>
        </section>
      </div>
    )
  }

  const toggleSource = (sourceId: string, on: boolean) =>
    setSelected((current) => {
      const set = new Set(current ?? [])
      if (on) set.add(sourceId)
      else set.delete(sourceId)
      return sources.filter((s) => set.has(s.id)).map((s) => s.id)
    })

  return (
    <div className="workspace">
      <aside className="workspace__side">
        <section className="card">
          <div className="part-tree__header">
            <h2 title={session.name}>{session.name}</h2>
            <button type="button" className="btn btn--small" onClick={onBack}>
              All searches
            </button>
          </div>
          {session.hasInputImage && (
            <figure className="ref-input">
              <img src={urls.referenceInput(session.id)} alt="Input photo" />
              <figcaption className="muted small">
                Input photo{session.inputWidth ? ` · ${session.inputWidth}×${session.inputHeight}` : ''}
              </figcaption>
            </figure>
          )}
          <form
            onSubmit={(e) => {
              e.preventDefault()
              void search()
            }}
          >
            <label className="field">
              <span>Search for</span>
              <input value={query} maxLength={200} onChange={(e) => setQuery(e.target.value)} placeholder="Kit or part name, e.g. RX-78-2 Gundam" disabled={searching} />
            </label>
            <fieldset className="part-props__section">
              <legend className="small">Sources (free, no account)</legend>
              {sources.map((s) => (
                <label key={s.id} className="toggle" title={s.description}>
                  <input type="checkbox" checked={selected?.includes(s.id) ?? false} disabled={!s.enabled || searching} onChange={(e) => toggleSource(s.id, e.target.checked)} />
                  <span>
                    {s.name}
                    {!s.enabled && <span className="muted small"> (disabled on server)</span>}
                  </span>
                </label>
              ))}
            </fieldset>
            <button type="submit" className="btn btn--primary btn--block" disabled={searching || query.trim().length < 2 || selected?.length === 0}>
              {searching ? 'Searching…' : 'Search references'}
            </button>
          </form>
          {session.lastSearch.length > 0 && (
            <ul className="ref-outcomes small">
              {session.lastSearch.map((r) => (
                <li key={r.source}>
                  <span className={`status-pill status-pill--${r.status === 'ok' ? 'valid' : r.status === 'empty' || r.status === 'disabled' ? 'warning' : 'invalid'}`}>
                    {sourceStatusLabel(r.status)}
                  </span>{' '}
                  <strong>{r.name}</strong> <span className="muted">{describeOutcome(r)}</span>
                </li>
              ))}
            </ul>
          )}
          <ErrorBanner message={error} onDismiss={() => setError(null)} />
        </section>

        {session.manualLinks.length > 0 && (
          <section className="card">
            <h2>Search by hand</h2>
            <p className="muted small">
              {allSourcesUnavailable(session.lastSearch)
                ? 'No source could be searched automatically. These links open the same query in your browser:'
                : 'Image search engines, shops and wikis cannot be searched automatically. Open them in your browser and add useful pictures below.'}
            </p>
            <ul className="ref-links">
              {session.manualLinks.map((l) => (
                <li key={l.url}>
                  <a href={l.url} target="_blank" rel="noopener noreferrer nofollow">
                    {l.name}
                  </a>
                </li>
              ))}
            </ul>
          </section>
        )}

        <section className="card">
          <h2>Add your own picture</h2>
          <AddPictureForm busy={busy} onAdd={add} />
        </section>

        <p className="muted small">
          Search results and downloads are cached for a week.{' '}
          <button type="button" className="link-button" onClick={clearCache}>
            Clear cache
          </button>
          {notice && <span className="ref-notice"> {notice}</span>}
        </p>
      </aside>

      <main className="workspace__main">
        <section className="card">
          <div className="part-tree__header">
            <h2>Reference pictures</h2>
            <label className="toggle small">
              <input type="checkbox" checked={hideDuplicates} onChange={(e) => setHideDuplicates(e.target.checked)} />
              <span>Hide duplicates</span>
            </label>
          </div>
          <div className="ref-filters" role="tablist">
            {FILTERS.map((f) => (
              <button key={f.value} type="button" role="tab" aria-selected={filter === f.value} className="btn btn--small" onClick={() => setFilter(f.value)}>
                {f.label} ({counts[f.value]})
              </button>
            ))}
          </div>
          <p className="muted small">
            Similar-looking pictures are candidates, not proof that they show the same object. Check the source page and licence before relying on a picture.
          </p>
          {visible.length === 0 ? (
            <p className="muted">
              {candidates.length === 0
                ? 'No pictures yet. Search the free sources, use the manual links or add a picture of your own.'
                : 'No pictures match this filter.'}
            </p>
          ) : (
            <ul className="ref-grid">
              {visible.map((c) => (
                <CandidateCard
                  key={c.id}
                  sessionId={session.id}
                  candidate={c}
                  duplicateOf={c.duplicateOfId ? (titles.get(c.duplicateOfId) ?? null) : null}
                  busy={busy}
                  onUpdate={(body) => void update(c.id, body)}
                  onRemove={() => void remove(c.id)}
                />
              ))}
            </ul>
          )}
          {hiddenDuplicates > 0 && <p className="muted small">{hiddenDuplicates} duplicate(s) hidden.</p>}
        </section>
      </main>
    </div>
  )
}
