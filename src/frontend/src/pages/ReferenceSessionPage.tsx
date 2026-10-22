import { useCallback, useEffect, useMemo, useState } from 'react'
import { ErrorBanner } from '../components/ErrorBanner'
import { AddPictureForm } from '../components/references/AddPictureForm'
import { CandidateCard } from '../components/references/CandidateCard'
import { ModelCandidates } from '../components/references/ModelCandidates'
import { ReconstructionPanel } from '../components/references/ReconstructionPanel'
import { referenceApi, urls } from '../services/apiClient'
import {
  allSourcesUnavailable,
  countByDecision,
  describeOutcome,
  filterCandidates,
  groupCandidates,
  percent,
  sortCandidates,
  sourceStatusLabel,
  viewpointLabel,
  type CandidateFilter,
  type CandidateGrouping,
  type CandidateOrder,
} from '../services/referenceState'
import type { ManualPicture, ModelUpload, ReferenceSession, ReferenceSource, UpdateCandidateRequest } from '../types/references'

const FILTERS: { value: CandidateFilter; label: string }[] = [
  { value: 'all', label: 'All' },
  { value: 'pending', label: 'To review' },
  { value: 'accepted', label: 'Accepted' },
  { value: 'rejected', label: 'Rejected' },
]

export function ReferenceSessionPage({
  id,
  onBack,
  onOpenDesign,
  onOpenJob,
}: {
  id: string
  onBack: () => void
  onOpenDesign: (designId: string) => void
  onOpenJob: (jobId: string) => void
}) {
  const [session, setSession] = useState<ReferenceSession | null>(null)
  const [sources, setSources] = useState<ReferenceSource[]>([])
  const [selected, setSelected] = useState<string[] | null>(null)
  const [query, setQuery] = useState('')
  const [searching, setSearching] = useState(false)
  const [busy, setBusy] = useState(false)
  const [filter, setFilter] = useState<CandidateFilter>('all')
  const [hideDuplicates, setHideDuplicates] = useState(true)
  const [order, setOrder] = useState<CandidateOrder>('rank')
  const [grouping, setGrouping] = useState<CandidateGrouping>('none')
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
  const rerank = () => run(() => referenceApi.rank(id), 'Ranking failed.')
  const searchModels = () => run(() => referenceApi.searchModels(id, query), 'Model search failed.')
  const addModel = (upload: ModelUpload) => run(() => referenceApi.addModel(id, upload), 'Could not check the model.')

  const clearCache = async () => {
    try {
      const result = await referenceApi.clearCache()
      setNotice(
        `Cleared ${result.searchEntries} cached searches, ${result.embeddings} embeddings and ${result.files} downloads (${Math.round(result.bytes / 1024)} KB). Your pictures are kept.`,
      )
    } catch (e) {
      fail(e, 'Could not clear the cache.')
    }
  }

  const candidates = useMemo(() => session?.candidates ?? [], [session])
  const counts = useMemo(() => countByDecision(candidates), [candidates])
  const visible = useMemo(() => sortCandidates(filterCandidates(candidates, filter, hideDuplicates), order), [candidates, filter, hideDuplicates, order])
  const sections = useMemo(() => groupCandidates(visible, grouping), [visible, grouping])
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
          <div className="ref-filters">
            <label className="toggle small">
              <span>Order</span>
              <select value={order} onChange={(e) => setOrder(e.target.value as CandidateOrder)}>
                <option value="rank">Best match first</option>
                <option value="found">As found</option>
              </select>
            </label>
            <label className="toggle small">
              <span>Group</span>
              <select value={grouping} onChange={(e) => setGrouping(e.target.value as CandidateGrouping)}>
                <option value="none">No grouping</option>
                <option value="group">Visual groups</option>
                <option value="viewpoint">Viewpoint</option>
              </select>
            </label>
            <button type="button" className="btn btn--small" disabled={busy || candidates.length === 0} onClick={() => void rerank()} title="Use the pictures accepted so far as extra evidence">
              Re-rank
            </button>
          </div>
          <RankingStatus session={session} />
          <p className="muted small">
            Similar-looking pictures are candidates, not proof that they show the same object. Scores and suggestions are estimates; check the
            source page and licence before relying on a picture.
          </p>
          {visible.length === 0 ? (
            <p className="muted">
              {candidates.length === 0
                ? 'No pictures yet. Search the free sources, use the manual links or add a picture of your own.'
                : 'No pictures match this filter.'}
            </p>
          ) : (
            sections.map((section) => (
              <div key={section.key} className="ref-section">
                {section.title && (
                  <h3 className="ref-section__title">
                    {section.title} <span className="muted small">({section.items.length})</span>
                  </h3>
                )}
                <ul className="ref-grid">
                  {section.items.map((c) => (
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
              </div>
            ))
          )}
          {hiddenDuplicates > 0 && <p className="muted small">{hiddenDuplicates} duplicate(s) hidden.</p>}
        </section>

        <ModelCandidates
          sessionId={session.id}
          models={session.models}
          lastSearch={session.lastModelSearch}
          busy={busy}
          canSearch={query.trim().length >= 2}
          onSearch={() => void searchModels()}
          onUpload={addModel}
          onDecide={(m, decision) => void run(() => referenceApi.updateModel(id, m.id, decision, m.note), 'Update failed.')}
          onRemove={(m) => void run(() => referenceApi.removeModel(id, m.id), 'Remove failed.')}
        />

        <ReconstructionPanel sessionId={session.id} version={session.updatedAt} onOpenDesign={onOpenDesign} onOpenJob={onOpenJob} />
      </main>
    </div>
  )
}

/** Whether visual ranking was used, and why not when it was not. */
function RankingStatus({ session }: { session: ReferenceSession }) {
  const r = session.ranking
  if (!r.mode) return null
  return (
    <p className={`small ref-ranking ref-ranking--${r.mode}`}>
      {r.mode === 'embeddings' ? `Ranked with visual similarity (${r.embeddingModel}).` : 'Ranked without visual similarity.'}
      {r.inputViewpoint && ` Your photo: ${viewpointLabel(r.inputViewpoint.label).toLowerCase()} (${percent(r.inputViewpoint.confidence)}).`}
      {r.note && <span className="muted"> {r.note}</span>}
    </p>
  )
}
