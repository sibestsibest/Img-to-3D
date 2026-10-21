import { Fragment, useEffect, useMemo, useState, useSyncExternalStore } from 'react'
import { apiLog, startResourceLogging, type ApiLogEntry } from '../services/apiLog'

interface Props {
  /** Job shown in the workspace; enables the "this job only" filter. */
  jobId: string | null
}

/** Table of the browser's /api calls (newest first) for checking against the API/inference logs. */
export function ApiLogPanel({ jobId }: Props) {
  const entries = useSyncExternalStore(apiLog.subscribe, apiLog.getEntries)
  const [jobOnly, setJobOnly] = useState(false)
  const [expanded, setExpanded] = useState<number | null>(null)

  useEffect(() => startResourceLogging(), [])

  const rows = useMemo(() => entries.filter((e) => !jobOnly || !jobId || e.url.includes(jobId)), [entries, jobOnly, jobId])

  return (
    <section className="card api-log">
      <div className="api-log__header">
        <h2>
          API calls <span className="api-log__count">{rows.length}</span>
        </h2>
        <div className="api-log__tools">
          <label className="toggle">
            <input type="checkbox" checked={jobOnly} disabled={!jobId} onChange={(e) => setJobOnly(e.target.checked)} />
            This job only
          </label>
          <button type="button" className="btn btn--small" onClick={() => apiLog.clear()} disabled={entries.length === 0}>
            Clear
          </button>
        </div>
      </div>

      {rows.length === 0 ? (
        <p className="api-log__empty">No API calls yet.</p>
      ) : (
        <div className="api-log__scroll">
          <table className="api-log__table">
            <thead>
              <tr>
                <th>Time</th>
                <th>Method</th>
                <th>URL</th>
                <th>Status</th>
                <th className="num">ms</th>
                <th className="num">Size</th>
                <th>Result</th>
                <th>Correlation ID</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((e) => (
                <Fragment key={e.id}>
                  <tr
                    className={`api-log__row ${statusClass(e.status)}${e.body ? ' api-log__row--clickable' : ''}`}
                    onClick={() => e.body && setExpanded(expanded === e.id ? null : e.id)}
                    title={e.body ? 'Click to show the response body' : undefined}
                  >
                    <td className="mono">
                      {formatTime(e.time)}
                      {e.count > 1 && (
                        <span className="api-log__repeat" title={`Repeated ${e.count} times since ${formatTime(e.firstTime)}`}>
                          ×{e.count}
                        </span>
                      )}
                    </td>
                    <td className="mono">{e.method}</td>
                    <td className="mono api-log__url" title={e.url}>
                      {e.url}
                    </td>
                    <td className="mono">{e.status === null ? '—' : e.status === 0 ? 'ERR' : e.status}</td>
                    <td className="mono num">{e.durationMs}</td>
                    <td className="mono num">{formatSize(e.sizeBytes)}</td>
                    <td className="api-log__summary" title={e.summary}>
                      {e.summary}
                    </td>
                    <td className="mono api-log__cid">
                      {e.correlationId ? (
                        <button
                          type="button"
                          className="api-log__copy"
                          title="Copy (search the API log for it)"
                          onClick={(ev) => {
                            ev.stopPropagation()
                            void navigator.clipboard?.writeText(e.correlationId!)
                          }}
                        >
                          {e.correlationId.slice(0, 8)}…
                        </button>
                      ) : (
                        '—'
                      )}
                    </td>
                  </tr>
                  {expanded === e.id && e.body && (
                    <tr className="api-log__detail">
                      <td colSpan={8}>
                        <pre>{e.body}</pre>
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}

function statusClass(status: ApiLogEntry['status']): string {
  if (status === null) return ''
  if (status === 0 || status >= 500) return 'api-log__row--error'
  if (status >= 400) return 'api-log__row--warn'
  return ''
}

function formatTime(d: Date): string {
  const pad = (n: number, w = 2) => String(n).padStart(w, '0')
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(), 3)}`
}

function formatSize(bytes: number | null): string {
  if (bytes == null) return '—'
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}
