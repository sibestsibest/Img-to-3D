import { useState } from 'react'
import { CONTACT_LABELS, sortCollisions } from '../../services/assemblyState'
import { designApi } from '../../services/apiClient'
import type { AssemblyReport, DesignPart } from '../../types/partDesign'
import { IssueList } from './PartPropertiesPanel'

interface Props {
  projectId: string
  parts: DesignPart[]
  /** Changes whenever the project changes; a report from an older version is not shown. */
  version: string
  onSelectPart: (partId: string) => void
}

/**
 * Assembly check on request: collisions between every pair of parts (exact triangle tests),
 * relation fits and the geometry quality of each part. It reads every part's geometry, so it
 * does not run on each edit.
 */
export function AssemblyPanel({ projectId, parts, version, onSelectPart }: Props) {
  const [checked, setChecked] = useState<{ version: string; report: AssemblyReport } | null>(null)
  const report = checked?.version === version ? checked.report : null
  const [checking, setChecking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const name = (id: string) => parts.find((p) => p.id === id)?.name ?? 'part'

  const check = async () => {
    setChecking(true)
    setError(null)
    try {
      setChecked({ version, report: await designApi.assembly(projectId) })
    } catch (e) {
      setChecked(null)
      setError(e instanceof Error ? e.message : 'The assembly could not be checked.')
    } finally {
      setChecking(false)
    }
  }

  const collisions = report ? sortCollisions(report.collisions) : []
  const q = report?.quality

  return (
    <section className="card assembly-panel">
      <div className="part-tree__header">
        <h2>Assembly and quality</h2>
        <button type="button" className="btn btn--small" disabled={checking || parts.length === 0} onClick={check}>
          {checking ? 'Checking…' : report ? 'Check again' : 'Check assembly'}
        </button>
      </div>
      {!report && !error && (
        <p className="muted small">
          Finds parts that intersect, sit inside each other or are duplicated in place, checks the fit of every relation, and reports whether each part is a
          closed solid.
        </p>
      )}
      {error && <p className="error-text">{error}</p>}
      {report && q && (
        <>
          <p className="small">
            <span className={`status-pill status-pill--${report.status}`}>{report.status}</span> {report.pairsChecked} close pair(s) checked
            {report.complete ? '' : ' (incomplete)'}, contact tolerance {report.contactToleranceMm} mm.
          </p>
          <IssueList issues={report.issues} empty="" />

          <h3>Collisions</h3>
          {collisions.length === 0 ? (
            <p className="success-text small">No parts touch or overlap.</p>
          ) : (
            <ul className="issues">
              {collisions.map((c, i) => (
                <li key={`${c.partAId}-${c.partBId}-${i}`} className={`issue issue--${c.severity}`}>
                  <span className="issue__severity">{CONTACT_LABELS[c.contact]}</span>{' '}
                  <button type="button" className="link-button" onClick={() => onSelectPart(c.partAId)}>
                    {name(c.partAId)}
                  </button>{' '}
                  ·{' '}
                  <button type="button" className="link-button" onClick={() => onSelectPart(c.partBId)}>
                    {name(c.partBId)}
                  </button>
                  : {c.message}
                </li>
              ))}
            </ul>
          )}

          <h3>Geometry quality</h3>
          <p className="small">
            {q.partCount} part(s): {q.validParts} valid, {q.warningParts} with warnings, {q.invalidParts} invalid. {q.solidExportReadyParts} of {q.partCount} are
            closed solids ready for STL. {q.totalTriangles.toLocaleString()} triangles
            {q.totalVolumeMm3 != null && `, ${q.totalVolumeMm3.toLocaleString(undefined, { maximumFractionDigits: 0 })} mm³ in total`}.
          </p>
          <table className="quality-table">
            <thead>
              <tr>
                <th>Part</th>
                <th>Status</th>
                <th>Solid</th>
                <th>Open / non-manifold edges</th>
                <th>Collisions</th>
              </tr>
            </thead>
            <tbody>
              {q.parts.map((p) => (
                <tr key={p.partId}>
                  <td>
                    <button type="button" className="link-button" onClick={() => onSelectPart(p.partId)}>
                      {p.name}
                    </button>
                  </td>
                  <td>
                    <span className={`status-pill status-pill--${p.status}`}>{p.status}</span>
                  </td>
                  <td>{p.solidExportReady ? 'yes' : 'no'}</td>
                  <td>
                    {p.boundaryEdges} / {p.nonManifoldEdges}
                  </td>
                  <td>{p.collisionCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="muted small">A closed solid can be printed or exported as STL; it is not a check that the part has the right size.</p>
        </>
      )}
    </section>
  )
}
