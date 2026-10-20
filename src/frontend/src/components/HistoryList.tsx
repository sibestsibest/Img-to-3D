import { useState } from 'react'
import { urls } from '../services/apiClient'
import { describeJob } from '../services/stages'
import type { Job } from '../types/api'

interface Props {
  jobs: Job[]
  selectedId: string | null
  loading: boolean
  page: number
  pageCount: number
  onPage: (page: number) => void
  onSelect: (job: Job) => void
  onDelete: (job: Job) => Promise<void>
}

export function HistoryList({ jobs, selectedId, loading, page, pageCount, onPage, onSelect, onDelete }: Props) {
  const [deleting, setDeleting] = useState<string | null>(null)

  const remove = async (job: Job) => {
    if (!window.confirm(`Delete "${job.originalFileName}" and all its files?`)) return
    setDeleting(job.id)
    try {
      await onDelete(job)
    } finally {
      setDeleting(null)
    }
  }

  return (
    <section className="card">
      <h2>Recent generations</h2>
      {jobs.length === 0 && !loading && <p className="muted">No generations yet. Upload an image to get started.</p>}
      <ul className="history">
        {jobs.map((job) => (
          <li key={job.id} className={`history__item${job.id === selectedId ? ' history__item--selected' : ''}`}>
            <button type="button" className="history__main" onClick={() => onSelect(job)}>
              <img src={urls.image(job.id, 'source')} alt="" className="history__thumb" loading="lazy" />
              <span className="history__text">
                <span className="history__name" title={job.originalFileName}>
                  {job.originalFileName}
                </span>
                <span className="muted small">
                  {new Date(job.createdAt).toLocaleString()} · <span className={`status-dot status-dot--${job.status.toLowerCase()}`} />
                  {job.status === 'Failed' ? 'Failed' : describeJob(job)}
                </span>
              </span>
            </button>
            <span className="history__actions">
              {job.downloads.glb && (
                <a href={urls.download(job, 'glb')} download title="Download GLB">
                  GLB
                </a>
              )}
              {job.downloads.stl && (
                <a href={urls.download(job, 'stl')} download title="Download STL">
                  STL
                </a>
              )}
              <button type="button" className="link-danger" onClick={() => remove(job)} disabled={deleting === job.id}>
                {deleting === job.id ? '…' : 'Delete'}
              </button>
            </span>
          </li>
        ))}
      </ul>
      {pageCount > 1 && (
        <div className="pager">
          <button type="button" className="btn btn--small" disabled={page <= 1} onClick={() => onPage(page - 1)}>
            Previous
          </button>
          <span className="muted small">
            Page {page} of {pageCount}
          </span>
          <button type="button" className="btn btn--small" disabled={page >= pageCount} onClick={() => onPage(page + 1)}>
            Next
          </button>
        </div>
      )}
    </section>
  )
}
