import { urls } from '../../services/apiClient'
import { IMAGE_KINDS, RELATIONS, confidenceLabel, licenseTone, percent, suggestionLabel } from '../../services/referenceState'
import type { ReferenceCandidate, ReferenceDecision, UpdateCandidateRequest } from '../../types/references'

interface Props {
  sessionId: string
  candidate: ReferenceCandidate
  /** Title of the picture this one duplicates, when it is a duplicate. */
  duplicateOf: string | null
  busy: boolean
  onUpdate: (body: UpdateCandidateRequest) => void
  onRemove: () => void
}

/** One reference picture: preview, provenance and licence, and the review controls. */
export function CandidateCard({ sessionId, candidate: c, duplicateOf, busy, onUpdate, onRemove }: Props) {
  const decide = (decision: ReferenceDecision) => onUpdate({ decision, imageKind: c.imageKind, relation: c.relation, note: c.note })
  const tone = licenseTone(c.license)

  return (
    <li className={`ref-card ref-card--${c.decision}`}>
      <div className="ref-card__thumb">
        {c.hasThumbnail ? (
          <img src={urls.referenceThumbnail(sessionId, c.id)} alt={c.title} loading="lazy" width={c.thumbnailWidth ?? undefined} height={c.thumbnailHeight ?? undefined} />
        ) : (
          <span className="muted small">{c.downloadStatus === 'failed' ? 'Preview failed' : 'No preview'}</span>
        )}
        {c.duplicateKind && <span className="ref-card__flag">{c.duplicateKind === 'exact' ? 'Same file' : 'Near duplicate'}</span>}
        {c.matchesInput && <span className="ref-card__flag ref-card__flag--input">Your photo</span>}
        {c.ranking.position != null && (
          <span className="ref-card__rank" title={`Score ${percent(c.ranking.score)} (${confidenceLabel(c.ranking.confidence)})`}>
            #{c.ranking.position}
          </span>
        )}
      </div>

      <div className="ref-card__body">
        <div className="ref-card__title" title={c.title}>
          {c.pageUrl ? (
            <a href={c.pageUrl} target="_blank" rel="noopener noreferrer nofollow">
              {c.title}
            </a>
          ) : (
            c.title
          )}
        </div>
        <div className="muted small">
          {c.sourceName}
          {c.author ? ` · ${c.author}` : ''}
          {c.width && c.height ? ` · ${c.width}×${c.height}` : ''}
        </div>
        <div className="ref-card__tags">
          <span className={`tag tag--licence-${tone}`} title={c.license.raw ?? c.license.label}>
            {c.license.url ? (
              <a href={c.license.url} target="_blank" rel="noopener noreferrer nofollow">
                {c.license.label}
              </a>
            ) : (
              c.license.label
            )}
          </span>
          {c.decision !== 'pending' && <span className={`tag tag--decision-${c.decision}`}>{c.decision}</span>}
        </div>
        <RankingSummary candidate={c} />
        {duplicateOf && <p className="muted small">Duplicate of “{duplicateOf}”.</p>}
        {c.warnings
          .filter((w) => w.code !== 'duplicate')
          .map((w) => (
            <p key={w.code} className={`ref-card__warning ref-card__warning--${w.severity}`}>
              {w.message}
            </p>
          ))}

        <details className="ref-card__more">
          <summary className="small">Details</summary>
          <p className="small ref-card__attribution">{c.attribution}</p>
          {c.ranking.reasons.length > 0 && (
            <ul className="ref-reasons small">
              {c.ranking.reasons.map((r) => (
                <li key={r.code} className={`ref-reasons__item ref-reasons__item--${r.effect === '+' ? 'plus' : r.effect === '-' ? 'minus' : 'info'}`}>
                  {r.message}
                </li>
              ))}
            </ul>
          )}
          <p className="muted small">
            Retrieved {new Date(c.retrievedAt).toLocaleString()}
            {c.query ? ` for “${c.query}”` : ''}
            {c.imageUrl && (
              <>
                {' · '}
                <a href={c.imageUrl} target="_blank" rel="noopener noreferrer nofollow">
                  full image
                </a>
              </>
            )}
          </p>
          <div className="ref-card__selects">
            <select aria-label="Picture kind" value={c.imageKind} disabled={busy} onChange={(e) => onUpdate({ decision: c.decision, imageKind: e.target.value as typeof c.imageKind, relation: c.relation, note: c.note })}>
              {IMAGE_KINDS.map((k) => (
                <option key={k.value} value={k.value}>
                  {k.label}
                </option>
              ))}
            </select>
            <select aria-label="Relation to the object" value={c.relation} disabled={busy} onChange={(e) => onUpdate({ decision: c.decision, imageKind: c.imageKind, relation: e.target.value as typeof c.relation, note: c.note })}>
              {RELATIONS.map((r) => (
                <option key={r.value} value={r.value}>
                  {r.label}
                </option>
              ))}
            </select>
          </div>
          <button type="button" className="link-danger small" disabled={busy} onClick={onRemove}>
            Remove from session
          </button>
        </details>

        <div className="ref-card__actions">
          <button type="button" className="btn btn--small" aria-pressed={c.decision === 'accepted'} disabled={busy} onClick={() => decide(c.decision === 'accepted' ? 'pending' : 'accepted')}>
            {c.decision === 'accepted' ? '✓ Accepted' : 'Accept'}
          </button>
          <button type="button" className="btn btn--small" aria-pressed={c.decision === 'rejected'} disabled={busy} onClick={() => decide(c.decision === 'rejected' ? 'pending' : 'rejected')}>
            {c.decision === 'rejected' ? '✕ Rejected' : 'Reject'}
          </button>
        </div>
      </div>
    </li>
  )
}

/** Score, how much evidence it rests on, and the zero-shot suggestions (all advisory). */
function RankingSummary({ candidate: c }: { candidate: ReferenceCandidate }) {
  const r = c.ranking
  if (r.position == null) return null
  const suggestions = [r.viewpoint, r.kind, r.relation].filter((s): s is NonNullable<typeof s> => s != null)
  return (
    <div className="ref-rank small">
      <div className="ref-rank__bar" aria-label={`Score ${percent(r.score)}`}>
        <span style={{ width: percent(r.score) }} />
      </div>
      <span className="muted">
        {percent(r.score)} · {confidenceLabel(r.confidence)}
        {r.group != null ? ` · group ${r.group}` : ''}
      </span>
      {suggestions.length > 0 && (
        <div className="ref-card__tags" title="Suggestions from the image model: check them, they are often wrong.">
          {suggestions.map((s, i) => (
            <span key={i} className={`tag${s.label === 'uncertain' ? ' tag--uncertain' : ''}`}>
              {suggestionLabel(s.label)} {percent(s.confidence)}
            </span>
          ))}
        </div>
      )}
    </div>
  )
}
