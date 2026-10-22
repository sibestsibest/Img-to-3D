import { useState } from 'react'
import { urls } from '../../services/apiClient'
import { MANUAL_LICENSES, confidenceLabel, describeOutcome, formatBytes, licenseTone, percent, sourceStatusLabel } from '../../services/referenceState'
import type { ModelCandidate, ModelUpload, ReferenceDecision, SourceSearchResult } from '../../types/references'

interface Props {
  sessionId: string
  models: ModelCandidate[]
  lastSearch: SourceSearchResult[]
  busy: boolean
  canSearch: boolean
  onSearch: () => void
  onUpload: (upload: ModelUpload) => Promise<boolean>
  onDecide: (model: ModelCandidate, decision: ReferenceDecision) => void
  onRemove: (model: ModelCandidate) => void
}

/** Existing 3D models: Sketchfab listings and uploaded GLB/STL files, with their evaluation. */
export function ModelCandidates({ sessionId, models, lastSearch, busy, canSearch, onSearch, onUpload, onDecide, onRemove }: Props) {
  return (
    <section className="card">
      <div className="part-tree__header">
        <h2>Existing 3D models</h2>
        <button type="button" className="btn btn--small" disabled={busy || !canSearch} onClick={onSearch}>
          Search Sketchfab
        </button>
      </div>
      <p className="muted small">
        Reuse beats regeneration when a suitable model exists. Listings are judged from their preview, licence and face count only;
        download a model yourself (Sketchfab needs a free account) and upload it below to have its mesh checked. The name is never used
        for scoring.
      </p>
      {lastSearch.map((r) => (
        <p key={r.source} className="small">
          <span className={`status-pill status-pill--${r.status === 'ok' ? 'valid' : r.status === 'empty' || r.status === 'disabled' ? 'warning' : 'invalid'}`}>
            {sourceStatusLabel(r.status)}
          </span>{' '}
          <strong>{r.name}</strong> <span className="muted">{describeOutcome(r)}</span>
        </p>
      ))}
      <ModelUploadForm busy={busy} onUpload={onUpload} />
      {models.length === 0 ? (
        <p className="muted">No model candidates yet.</p>
      ) : (
        <ul className="ref-grid">
          {models.map((m) => (
            <ModelCard key={m.id} sessionId={sessionId} model={m} busy={busy} onDecide={(d) => onDecide(m, d)} onRemove={() => onRemove(m)} />
          ))}
        </ul>
      )}
    </section>
  )
}

function ModelCard({ sessionId, model: m, busy, onDecide, onRemove }: { sessionId: string; model: ModelCandidate; busy: boolean; onDecide: (d: ReferenceDecision) => void; onRemove: () => void }) {
  const g = m.geometry
  return (
    <li className={`ref-card ref-card--${m.decision}`}>
      <div className="ref-card__thumb">
        {m.hasThumbnail ? <img src={urls.modelThumbnail(sessionId, m.id)} alt={m.title} loading="lazy" /> : <span className="muted small">No preview</span>}
        <span className="ref-card__flag">{m.evaluationStatus === 'geometry_checked' ? 'Mesh checked' : 'Listing only'}</span>
      </div>
      <div className="ref-card__body">
        <div className="ref-card__title" title={m.title}>
          {m.pageUrl ? (
            <a href={m.pageUrl} target="_blank" rel="noopener noreferrer nofollow">
              {m.title}
            </a>
          ) : (
            m.title
          )}
        </div>
        <div className="muted small">
          {m.sourceName}
          {m.author ? ` · ${m.author}` : ''}
          {m.faceCount != null ? ` · ${m.faceCount.toLocaleString()} faces` : ''}
          {m.fileBytes != null ? ` · ${formatBytes(m.fileBytes)}` : ''}
        </div>
        <div className="ref-card__tags">
          <span className={`tag tag--licence-${licenseTone(m.license)}`} title={m.license.raw ?? m.license.label}>
            {m.license.label}
          </span>
          {m.isDownloadable === false && <span className="tag">not downloadable</span>}
          {m.decision !== 'pending' && <span className={`tag tag--decision-${m.decision}`}>{m.decision}</span>}
        </div>
        <div className="ref-rank small">
          <div className="ref-rank__bar" aria-label={`Score ${percent(m.score)}`}>
            <span style={{ width: percent(m.score) }} />
          </div>
          <span className="muted">
            {percent(m.score)} · {confidenceLabel(m.confidence)}
            {m.visualSimilarity != null ? ` · looks ${percent(m.visualSimilarity)} alike` : ''}
          </span>
        </div>
        {g && (
          <p className="small">
            {g.closed ? 'Closed surface' : 'Open surface'} · {g.components} piece(s) · {g.triangles.toLocaleString()} triangles ·{' '}
            {g.sizeXMm} × {g.sizeYMm} × {g.sizeZMm} mm{g.scaleStatus === 'unitless' ? ' (units assumed)' : g.scaleStatus === 'suspicious' ? ' (scale looks wrong)' : ''}
          </p>
        )}
        {m.warnings.map((w) => (
          <p key={w.code} className={`ref-card__warning ref-card__warning--${w.severity}`}>
            {w.message}
          </p>
        ))}
        <details className="ref-card__more">
          <summary className="small">Why this score</summary>
          <ul className="ref-reasons small">
            {m.reasons.map((r) => (
              <li key={r.code} className={`ref-reasons__item ref-reasons__item--${r.effect === '+' ? 'plus' : r.effect === '-' ? 'minus' : 'info'}`}>
                {r.message}
              </li>
            ))}
          </ul>
          <p className="small ref-card__attribution">{m.attribution}</p>
          {m.format && (
            <a className="small" href={urls.modelFile(sessionId, m.id)}>
              Download uploaded {m.format.toUpperCase()}
            </a>
          )}
          <button type="button" className="link-danger small" disabled={busy} onClick={onRemove}>
            Remove from session
          </button>
        </details>
        <div className="ref-card__actions">
          <button type="button" className="btn btn--small" aria-pressed={m.decision === 'accepted'} disabled={busy} onClick={() => onDecide(m.decision === 'accepted' ? 'pending' : 'accepted')}>
            {m.decision === 'accepted' ? '✓ Accepted' : 'Accept'}
          </button>
          <button type="button" className="btn btn--small" aria-pressed={m.decision === 'rejected'} disabled={busy} onClick={() => onDecide(m.decision === 'rejected' ? 'pending' : 'rejected')}>
            {m.decision === 'rejected' ? '✕ Rejected' : 'Reject'}
          </button>
        </div>
      </div>
    </li>
  )
}

function ModelUploadForm({ busy, onUpload }: { busy: boolean; onUpload: (upload: ModelUpload) => Promise<boolean> }) {
  const [file, setFile] = useState<File | null>(null)
  const [pageUrl, setPageUrl] = useState('')
  const [author, setAuthor] = useState('')
  const [license, setLicense] = useState('unknown')
  const [inputKey, setInputKey] = useState(0)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!file) return
    if (await onUpload({ file, pageUrl, author, license })) {
      setFile(null)
      setPageUrl('')
      setAuthor('')
      setLicense('unknown')
      setInputKey((k) => k + 1)
    }
  }

  return (
    <details className="ref-model-upload">
      <summary className="small">Check a model file (GLB or binary STL)</summary>
      <form className="ref-add" onSubmit={submit}>
        <input key={inputKey} type="file" accept=".glb,.stl,model/gltf-binary,model/stl" disabled={busy} onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
        <div className="part-props__grid">
          <label className="field">
            <span>Source page (link only)</span>
            <input type="url" value={pageUrl} maxLength={2000} disabled={busy} onChange={(e) => setPageUrl(e.target.value)} placeholder="https://…" />
          </label>
          <label className="field">
            <span>Author</span>
            <input value={author} maxLength={200} disabled={busy} onChange={(e) => setAuthor(e.target.value)} />
          </label>
          <label className="field">
            <span>Licence</span>
            <select value={license} disabled={busy} onChange={(e) => setLicense(e.target.value)}>
              {MANUAL_LICENSES.map((l) => (
                <option key={l.value} value={l.value}>
                  {l.label}
                </option>
              ))}
            </select>
          </label>
        </div>
        <button type="submit" className="btn" disabled={busy || !file}>
          Upload and check
        </button>
      </form>
    </details>
  )
}
