import { useEffect, useState } from 'react'
import { designApi, urls } from '../../services/apiClient'
import { formatMm } from '../../services/geometry'
import type { DesignPart, DrawingReport } from '../../types/partDesign'
import { IssueList } from './PartPropertiesPanel'

interface Props {
  projectId: string
  part: DesignPart
  /** Changes whenever the project changes, so the preview and report are refetched after edits. */
  version: string
}

/** 1:1 cutting sheet for flat parts: checks, preview and SVG/PDF/DXF downloads; STL for every part. */
export function PartDrawingPanel({ projectId, part, version }: Props) {
  const [report, setReport] = useState<DrawingReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  const isSheet = part.category === 'sheet'

  useEffect(() => {
    setReport(null)
    setError(null)
    if (!isSheet) return
    const controller = new AbortController()
    designApi
      .drawingReport(projectId, part.id, controller.signal)
      .then(setReport)
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setError(e instanceof Error ? e.message : 'The drawing could not be prepared.')
      })
    return () => controller.abort()
  }, [projectId, part.id, version, isSheet])

  return (
    <section className="card">
      <h2>Fabrication drawing</h2>
      {!isSheet ? (
        <p className="muted small">
          Drawings are made for flat parts. Set the category to <strong>Sheet (PS plate)</strong> and save if this part is cut from styrene
          sheet. Curved parts need a developed pattern, segmented construction or custom modelling instead.
        </p>
      ) : error ? (
        <p className="error-text">{error}</p>
      ) : !report ? (
        <p className="muted small">Preparing the drawing…</p>
      ) : (
        <>
          <dl className="facts">
            <dt>Page</dt>
            <dd>
              {report.page}, scale {report.scale}
            </dd>
            <dt>Outline</dt>
            <dd>
              {formatMm(report.outlineWidthMm)} × {formatMm(report.outlineHeightMm)} mm
              {report.outlineCount > 1 ? ` (${report.outlineCount} pieces)` : ''}
            </dd>
            <dt>Thickness</dt>
            <dd>
              {formatMm(report.thicknessMm)} mm{' '}
              <span className={`tag${report.thicknessStatus === 'estimated' ? ' tag--needs_review' : ''}`}>{report.thicknessStatus}</span>
            </dd>
            <dt>Holes</dt>
            <dd>{report.holes.length}</dd>
          </dl>
          <IssueList issues={report.issues} empty="All drawing checks passed." />
          <div className="part-props__actions">
            <a className="btn btn--primary" href={urls.drawing(projectId, part.id, 'pdf', version, true)} download>
              PDF <span className="btn__sub">print at 100%</span>
            </a>
            <a className="btn" href={urls.drawing(projectId, part.id, 'svg', version, true)} download>
              SVG <span className="btn__sub">mm units · cutter / laser</span>
            </a>
            <a className="btn" href={urls.drawing(projectId, part.id, 'dxf', version, true)} download>
              DXF <span className="btn__sub">R12 · layer CUT</span>
            </a>
          </div>
          <p className="note">
            Print at <strong>100% / actual size</strong>, never “fit to page”, then check the 100 mm bar with a ruler before cutting. Layer
            “cut” in the SVG holds only the cutting contours.
          </p>
          <img className="drawing-preview" src={urls.drawing(projectId, part.id, 'svg', version)} alt={`Cutting sheet for ${part.name}`} />
        </>
      )}
      <div className="part-props__actions">
        <a className="btn btn--small" href={urls.partStl(projectId, part.id, version)} download>
          STL <span className="btn__sub">this part · mm</span>
        </a>
      </div>
    </section>
  )
}
