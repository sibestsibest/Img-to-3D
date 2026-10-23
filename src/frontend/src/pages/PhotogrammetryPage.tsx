import { useEffect, useMemo, useState } from 'react'
import { ErrorBanner } from '../components/ErrorBanner'
import { ModelViewer } from '../components/ModelViewer'
import { OriginBadge } from '../components/OriginBadge'
import { usePhotogrammetryJob } from '../hooks/usePhotogrammetryJob'
import { photogrammetryApi, urls } from '../services/apiClient'
import { acceptedPhotos, AXIS_LABELS, batches, formatDuration, isActive, parseScale, progressText, splitFindings, statusLabel, statusTone } from '../services/photogrammetryState'
import type { PhotogrammetryEngine, ScaleAxisName } from '../types/photogrammetry'

interface Props {
  id: string
  onBack: () => void
  onOpenDesign: (id: string) => void
}

/** One photo set: collect and check photos, run Meshroom, follow it, calibrate and export the result. */
export function PhotogrammetryPage({ id, onBack, onOpenDesign }: Props) {
  const { job, loadError, actionError, setActionError, busy, run } = usePhotogrammetryJob(id)
  const [engine, setEngine] = useState<PhotogrammetryEngine | null>(null)
  const [pipeline, setPipeline] = useState('')
  const [upload, setUpload] = useState<string | null>(null)
  const [axis, setAxis] = useState<ScaleAxisName>('Longest')
  const [length, setLength] = useState('')
  const [scaleError, setScaleError] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    photogrammetryApi
      .engine(controller.signal)
      .then((e) => {
        setEngine(e)
        setPipeline(e.defaultPipeline ?? '')
      })
      .catch(() => undefined)
    return () => controller.abort()
  }, [])

  const findings = useMemo(() => splitFindings(job?.findings ?? []), [job])

  if (loadError) {
    return (
      <div className="workspace workspace--single">
        <ErrorBanner message={loadError.status === 404 ? 'This photo set does not exist or belongs to another browser.' : loadError.message} />
        <button type="button" className="btn" onClick={onBack}>
          All photo sets
        </button>
      </div>
    )
  }
  if (!job) return <div className="empty-state">Loading photo set…</div>

  const addPhotos = async (list: FileList | null) => {
    if (!list || list.length === 0) return
    const { accepted, skipped } = acceptedPhotos(Array.from(list))
    if (skipped.length) setActionError(`Skipped (only JPEG and PNG): ${skipped.join(', ')}`)
    const groups = batches(accepted)
    for (let i = 0; i < groups.length; i++) {
      setUpload(`Uploading ${i + 1}/${groups.length}…`)
      if (!(await run(() => photogrammetryApi.addPhotos(job.id, groups[i])))) break
    }
    setUpload(null)
  }

  const calibrate = async () => {
    const parsed = parseScale(axis, length)
    if ('error' in parsed) {
      setScaleError(parsed.error)
      return
    }
    setScaleError(null)
    await run(() => photogrammetryApi.scale(job.id, parsed.axis, parsed.targetMm))
  }

  const openDesign = async () => {
    try {
      onOpenDesign((await photogrammetryApi.createDesign(job.id)).id)
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Could not create the part design.')
    }
  }

  const draft = job.status === 'draft'
  const engineReady = engine?.status === 'ready'
  const version = job.scale.mmPerModelUnit

  return (
    <div className="workspace">
      <aside className="workspace__side">
        <section className="card">
          <div className="part-tree__header">
            <h2>{job.name}</h2>
            <button type="button" className="btn btn--small" onClick={onBack}>
              All photo sets
            </button>
          </div>
          <p className="small">
            <OriginBadge origin="photogrammetry" /> <span className={`status-pill status-pill--${statusTone(job.status)}`}>{statusLabel(job.status)}</span>
            {job.pipeline && <span className="muted"> · {job.pipeline}</span>}
            {job.engineVersion && <span className="muted"> · Meshroom {job.engineVersion}</span>}
          </p>
          {progressText(job) && <p className="small">{progressText(job)}</p>}
          {isActive(job.status) && (
            <button type="button" className="btn btn--small" disabled={busy} onClick={() => run(() => photogrammetryApi.cancel(job.id))}>
              Cancel reconstruction
            </button>
          )}
          {job.status === 'failed' && <p className="error-text">{job.error}</p>}
          {job.downloads.log && (
            <a className="small" href={urls.photogrammetryDownload(job.id, 'log', null)} target="_blank" rel="noreferrer">
              Meshroom log
            </a>
          )}
          {job.completedAt && job.progress.elapsedSeconds != null && <p className="muted small">Took {formatDuration(job.progress.elapsedSeconds)}.</p>}
        </section>

        <section className="card">
          <h2>Photo checks</h2>
          {findings.set.length === 0 ? (
            <p className="muted small">Add photos to check them.</p>
          ) : (
            <ul className="recon__reasons small">
              {findings.set.map((f) => (
                <li key={f.code} className={`recon__reason recon__reason--${f.severity}`}>
                  {f.message}
                </li>
              ))}
            </ul>
          )}
          {draft && (
            <>
              <label className="field">
                <span>Pipeline</span>
                <select value={pipeline} disabled={!engineReady || busy} onChange={(e) => setPipeline(e.target.value)}>
                  {(engine?.pipelines ?? []).map((p) => (
                    <option key={p} value={p}>
                      {p}
                      {p === engine?.defaultPipeline ? ' (default)' : ''}
                    </option>
                  ))}
                </select>
              </label>
              <button
                type="button"
                className="btn btn--primary"
                disabled={!job.canStart || !engineReady || busy}
                title={!engineReady ? (engine?.reason ?? 'Meshroom is not available') : undefined}
                onClick={() => run(() => photogrammetryApi.start(job.id, pipeline || undefined))}
              >
                Start reconstruction
              </button>
              {!engineReady && engine && <p className="muted small">{engine.reason}</p>}
              {engineReady && !engine.cuda && <p className="muted small">No CUDA GPU: only the draft pipeline is likely to work, and it is slow on large sets.</p>}
            </>
          )}
        </section>

        {job.status === 'succeeded' && (
          <>
            <section className="card">
              <h2>Scale</h2>
              <p className="muted small">
                {job.scale.status === 'calibrated'
                  ? `Calibrated: ${AXIS_LABELS[(job.scale.axis ?? 'Longest') as ScaleAxisName]} = ${job.scale.targetMm} mm.`
                  : 'Not calibrated: photogrammetry does not know the real size, so the longest side was set to a default. Measure one side of the real object and enter it.'}
              </p>
              <form
                className="primitive-form__dims"
                onSubmit={(e) => {
                  e.preventDefault()
                  void calibrate()
                }}
              >
                <label className="field">
                  <span>Side</span>
                  <select value={axis} onChange={(e) => setAxis(e.target.value as ScaleAxisName)}>
                    {(Object.keys(AXIS_LABELS) as ScaleAxisName[]).map((a) => (
                      <option key={a} value={a}>
                        {AXIS_LABELS[a]}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="field">
                  <span>Measured (mm)</span>
                  <input inputMode="decimal" value={length} onChange={(e) => setLength(e.target.value)} />
                </label>
                <button type="submit" className="btn btn--small" disabled={busy}>
                  Calibrate
                </button>
              </form>
              {scaleError && <p className="field-error">{scaleError}</p>}
            </section>

            <section className="card">
              <h2>Result</h2>
              {job.mesh && (
                <dl className="facts small">
                  <dt>Triangles</dt>
                  <dd>{job.mesh.triangles.toLocaleString()}</dd>
                  <dt>Closed surface</dt>
                  <dd>{job.mesh.closed ? 'yes' : `no (${job.mesh.boundaryEdges} open edges)`}</dd>
                  <dt>Separate pieces</dt>
                  <dd>{job.mesh.components}</dd>
                  <dt>Non-manifold edges</dt>
                  <dd>{job.mesh.nonManifoldEdges}</dd>
                </dl>
              )}
              <ul className="parametric__files small">
                <li>
                  <a href={urls.photogrammetryDownload(job.id, 'glb', version)}>GLB</a> <span className="muted">metres, untextured</span>
                </li>
                {job.downloads.stl ? (
                  <li>
                    <a href={urls.photogrammetryDownload(job.id, 'stl', version)}>STL</a> <span className="muted">millimetres, Z-up</span>
                  </li>
                ) : (
                  <li className="muted">{job.downloads.stlError}</li>
                )}
                <li>
                  <a href={urls.photogrammetryDownload(job.id, 'zip', null)}>Meshroom files</a> <span className="muted">textured OBJ, MTL and textures as produced</span>
                </li>
              </ul>
              <button type="button" className="btn btn--small" onClick={() => void openDesign()}>
                Open in part designer
              </button>
            </section>
          </>
        )}
      </aside>

      <main className="workspace__main">
        <ErrorBanner message={actionError} onDismiss={() => setActionError(null)} />
        {job.status === 'succeeded' ? (
          <ModelViewer url={urls.photogrammetryPreview(job.id, version)} dimensionsMm={job.dimensionsMm} />
        ) : (
          <section className="card">
            <h2>Photos ({job.photos.length})</h2>
            {draft && (
              <label className="photo__drop">
                <input type="file" accept="image/jpeg,image/png" multiple disabled={busy} onChange={(e) => void addPhotos(e.target.files).then(() => (e.target.value = ''))} />
                <span>{upload ?? 'Choose photos (JPEG or PNG, the camera originals)'}</span>
              </label>
            )}
            <ul className="photo__grid">
              {job.photos.map((p, i) => (
                <li key={p.id} className="photo__item">
                  <img src={p.thumbnailUrl} alt={p.originalName} loading="lazy" />
                  <span className="small">
                    {i + 1}. {p.originalName}
                  </span>
                  <span className="muted small">
                    {p.width} x {p.height}
                    {p.hasExif ? ' · EXIF' : ''}
                  </span>
                  {(findings.byPhoto.get(p.id) ?? []).map((f) => (
                    <span key={f.code} className={`recon__reason recon__reason--${f.severity} small`}>
                      {f.message}
                    </span>
                  ))}
                  {draft && (
                    <button type="button" className="link-danger small" disabled={busy} onClick={() => run(() => photogrammetryApi.removePhoto(job.id, p.id))}>
                      Remove
                    </button>
                  )}
                </li>
              ))}
            </ul>
          </section>
        )}
        <footer className="disclaimer">
          <strong>Reconstructed, not guessed, but not exact.</strong> Meshroom builds the surface the photos show: hidden, shiny, transparent or plain
          surfaces come out missing or noisy, and the mesh is often not closed or print-ready. The size is only real after calibration with a measured
          dimension.
        </footer>
      </main>
    </div>
  )
}
