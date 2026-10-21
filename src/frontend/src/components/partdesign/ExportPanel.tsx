import { useEffect, useState } from 'react'
import { designApi, urls } from '../../services/apiClient'
import type { DesignPart, DesignRelease, ExportManifest } from '../../types/partDesign'

interface Props {
  projectId: string
  parts: DesignPart[]
  /** Changes whenever the project changes; a checked package is then out of date. */
  version: string
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

/**
 * Project-level exports: BOM and cut list CSVs, a draft package built on demand, and numbered
 * releases that are stored once and downloaded unchanged afterwards.
 */
export function ExportPanel({ projectId, parts, version }: Props) {
  // Building the manifest builds the whole package on the server, so it only runs on request,
  // and a result from an older version of the project is not shown.
  const [checked, setChecked] = useState<{ version: string; manifest: ExportManifest } | null>(null)
  const manifest = checked?.version === version ? checked.manifest : null
  const [checking, setChecking] = useState(false)
  const [releases, setReleases] = useState<DesignRelease[] | null>(null)
  const [note, setNote] = useState('')
  const [releasing, setReleasing] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    designApi
      .releases(projectId, controller.signal)
      .then(setReleases)
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : 'Releases could not be loaded.')
      })
    return () => controller.abort()
  }, [projectId])

  const check = async () => {
    setChecking(true)
    setError(null)
    try {
      setChecked({ version, manifest: await designApi.exportManifest(projectId) })
    } catch (e) {
      setChecked(null)
      setError(e instanceof Error ? e.message : 'The package could not be built.')
    } finally {
      setChecking(false)
    }
  }

  const release = async () => {
    setReleasing(true)
    setError(null)
    try {
      const created = await designApi.createRelease(projectId, note.trim() || undefined)
      setReleases((current) => [created, ...(current ?? [])])
      setNote('')
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The release could not be created.')
    } finally {
      setReleasing(false)
    }
  }

  const partName = (id: string | null) => (id ? (parts.find((p) => p.id === id)?.name ?? 'part') : 'project')
  const hasRodsOrTubes = parts.some((p) => p.state === 'Active' && (p.category === 'rod' || p.category === 'tube'))

  return (
    <section className="card export-panel">
      <h2>Export</h2>
      <div className="part-props__actions">
        <a className="btn btn--small" href={urls.bom(projectId, version)} download>
          BOM <span className="btn__sub">CSV</span>
        </a>
        <a className="btn btn--small" href={urls.cutList(projectId, version)} download>
          Cut list <span className="btn__sub">{hasRodsOrTubes ? 'rods & tubes' : 'no rods or tubes yet'}</span>
        </a>
      </div>

      <h3>Package</h3>
      <p className="muted small">
        Drawings (PDF, SVG, DXF), STL of every part, BOM, cut list and manifest.json in one zip. A draft is built now and marked “Draft”; a
        release is numbered and stored, and always downloads exactly as it was made.
      </p>
      <div className="part-props__actions">
        <button type="button" className="btn btn--small" disabled={checking} onClick={check}>
          {checking ? 'Checking…' : 'Check package'}
        </button>
        <a className="btn btn--small" href={urls.draftPackage(projectId, version)} download>
          Draft zip
        </a>
      </div>
      {error && <p className="error-text">{error}</p>}
      {manifest && (
        <div className="export-check">
          <p className="small">
            {manifest.parts.length} part(s), {manifest.files.length} file(s), {formatBytes(manifest.files.reduce((s, f) => s + f.bytes, 0))}{' '}
            before compression.
          </p>
          {manifest.warnings.length === 0 ? (
            <p className="success-text small">No warnings.</p>
          ) : (
            <ul className="issues">
              {manifest.warnings.map((w, i) => (
                <li key={`${w.code}-${w.partId ?? ''}-${i}`} className={`issue issue--${w.severity}`}>
                  <strong>{partName(w.partId)}:</strong> {w.message}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      <h3>Releases</h3>
      <div className="export-release">
        <input
          type="text"
          maxLength={500}
          placeholder="Note (optional), e.g. sent to workshop"
          value={note}
          onChange={(e) => setNote(e.target.value)}
          aria-label="Release note"
        />
        <button type="button" className="btn btn--small btn--primary" disabled={releasing} onClick={release}>
          {releasing ? 'Creating…' : 'Create release'}
        </button>
      </div>
      {releases === null ? (
        <p className="muted small">Loading releases…</p>
      ) : releases.length === 0 ? (
        <p className="muted small">No releases yet.</p>
      ) : (
        <ol className="op-history">
          {releases.map((r) => (
            <li key={r.id}>
              <a href={urls.releasePackage(projectId, r.revision)} download>
                Rev {r.revision}
              </a>{' '}
              <span className="muted small">
                {new Date(r.createdAt).toLocaleString()} · {r.partCount} part(s) · {formatBytes(r.sizeBytes)}
                {r.warningCount > 0 ? ` · ${r.warningCount} warning(s)` : ''}
              </span>
              {r.note && <div className="small">{r.note}</div>}
            </li>
          ))}
        </ol>
      )}
    </section>
  )
}
