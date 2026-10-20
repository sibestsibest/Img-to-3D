import { useCallback, useEffect, useState } from 'react'
import { ApiLogPanel } from '../components/ApiLogPanel'
import { DimensionsPanel } from '../components/DimensionsPanel'
import { DownloadPanel } from '../components/DownloadPanel'
import { ErrorBanner } from '../components/ErrorBanner'
import { ExtraPhotos } from '../components/ExtraPhotos'
import { GenerationStatus } from '../components/GenerationStatus'
import { HistoryList } from '../components/HistoryList'
import { ImageUpload } from '../components/ImageUpload'
import { ModelViewer } from '../components/ModelViewer'
import { useGenerate } from '../hooks/useGenerate'
import { useJob } from '../hooks/useJob'
import { usesExtraPhotos } from '../hooks/useEngineInfo'
import { useJobHistory } from '../hooks/useJobHistory'
import { api, urls } from '../services/apiClient'
import type { ExtraPhoto, Job, ScaleAxis } from '../types/api'

export function WorkspacePage({ engine }: { engine: string | null }) {
  const [file, setFile] = useState<File | null>(null)
  const [extras, setExtras] = useState<ExtraPhoto[]>([])
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [cancelling, setCancelling] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)

  const history = useJobHistory()
  const { job, error: jobError, update } = useJob(selectedId)

  const onCreated = useCallback(
    (created: Job) => {
      setSelectedId(created.id)
      setFile(null)
      setExtras([])
      void history.refresh()
    },
    [history.refresh], // eslint-disable-line react-hooks/exhaustive-deps
  )
  const generate = useGenerate(onCreated)

  // Refresh history when the selected job reaches a final state.
  const status = job?.status
  useEffect(() => {
    if (status === 'Succeeded' || status === 'Failed' || status === 'Cancelled') void history.refresh()
  }, [status]) // eslint-disable-line react-hooks/exhaustive-deps

  const cancel = async () => {
    if (!job) return
    setCancelling(true)
    try {
      update(await api.cancel(job.id))
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Cancel failed.')
    } finally {
      setCancelling(false)
    }
  }

  const applyScale = async (axis: ScaleAxis, targetMm: number) => {
    if (!job) return
    update(await api.scale(job.id, axis, targetMm))
  }

  const deleteJob = async (target: Job) => {
    try {
      await history.remove(target.id)
      if (target.id === selectedId) setSelectedId(null)
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Delete failed.')
    }
  }

  const busy = job != null && (job.status === 'Pending' || job.status === 'Processing')

  return (
    <div className="workspace">
      <aside className="workspace__side">
        <section className="card">
          <h2>1. Upload an image</h2>
          <ImageUpload file={file} disabled={generate.submitting} onSelect={setFile} />
          <ExtraPhotos photos={extras} disabled={generate.submitting} supported={usesExtraPhotos(engine)} onChange={setExtras} />
          <button
            type="button"
            className="btn btn--primary btn--block"
            disabled={!file || generate.submitting}
            onClick={() => file && generate.submit(file, usesExtraPhotos(engine) ? extras : [])}
          >
            {generate.submitting ? 'Uploading…' : 'Generate 3D model'}
          </button>
          <ErrorBanner message={generate.error?.message} onDismiss={generate.clearError} />
        </section>

        {job && <GenerationStatus job={job} onCancel={cancel} cancelling={cancelling} />}
        <ErrorBanner message={jobError && jobError.status !== 404 ? jobError.message : null} />

        <HistoryList
          jobs={history.jobs}
          selectedId={selectedId}
          loading={history.loading}
          page={history.page}
          pageCount={history.pageCount}
          onPage={history.setPage}
          onSelect={(j) => setSelectedId(j.id)}
          onDelete={deleteJob}
        />
        <ErrorBanner message={history.error?.message} />
      </aside>

      <main className="workspace__main">
        <ErrorBanner message={actionError} onDismiss={() => setActionError(null)} />

        {job?.status === 'Succeeded' && job.downloads.glb ? (
          <>
            <ModelViewer url={urls.preview(job)} dimensionsMm={job.dimensionsMm} />
            <div className="panels">
              <DimensionsPanel job={job} onApply={applyScale} />
              <DownloadPanel job={job} />
            </div>
            <details className="card">
              <summary>What the model saw</summary>
              <div className="compare">
                <figure>
                  <img src={urls.image(job.id, 'source')} alt="Uploaded image" />
                  <figcaption>Uploaded</figcaption>
                </figure>
                <figure>
                  <img src={urls.image(job.id, 'preprocessed')} alt="Background removed and re-framed input" />
                  <figcaption>Background removed, re-framed (model input)</figcaption>
                </figure>
              </div>
            </details>
          </>
        ) : (
          <div className="empty-state">
            {busy ? (
              <p>Generating… the 3D preview appears here when the model is ready.</p>
            ) : job?.status === 'Failed' || job?.status === 'Cancelled' ? (
              <p>No model for this job. Adjust the image and try again.</p>
            ) : (
              <>
                <h2>Image to 3D for model parts</h2>
                <p>Upload a photo of a Gunpla part, plastic model component or mechanical accessory to get an estimated 3D mesh you can preview, scale in millimetres and export to STL.</p>
              </>
            )}
          </div>
        )}

        <footer className="disclaimer">
          <strong>AI-generated estimate, not a scan.</strong> The AI model reconstructs a plausible shape from one image. Hidden sides,
          internal cavities, holes, pegs and fine mechanical details are guessed and are often wrong. Do not rely on the output for
          precise fit or tolerances without checking it against the real part.
        </footer>

        <ApiLogPanel jobId={selectedId} />

      </main>
    </div>
  )
}
