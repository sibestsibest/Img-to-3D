import { useCallback, useEffect, useMemo, useReducer, useState } from 'react'
import { ErrorBanner } from '../components/ErrorBanner'
import { AssemblyPanel } from '../components/partdesign/AssemblyPanel'
import { ExportPanel } from '../components/partdesign/ExportPanel'
import { IssueList, PartPropertiesPanel } from '../components/partdesign/PartPropertiesPanel'
import { PartDrawingPanel } from '../components/partdesign/PartDrawingPanel'
import { PartTree } from '../components/partdesign/PartTree'
import { PartViewport, type PlanePreview } from '../components/partdesign/PartViewport'
import { PrimitivePanel } from '../components/partdesign/PrimitivePanel'
import { RelationsPanel } from '../components/partdesign/RelationsPanel'
import { SplitPanel } from '../components/partdesign/SplitPanel'
import { usePartDesign } from '../hooks/usePartDesign'
import { designApi } from '../services/apiClient'
import { activeParts, buildTree, initialViewState, isVisible, planeFor, viewReducer, type Axis } from '../services/partDesignState'
import type { AddPrimitiveRequest, CreateRelationRequest, RelationRequest, UpdatePartRequest } from '../types/partDesign'

export function PartDesignPage({ id, onBack }: { id: string; onBack: () => void }) {
  const { project, loadError, actionError, clearActionError, busy, run } = usePartDesign(id)
  const [view, dispatch] = useReducer(viewReducer, initialViewState)
  const [plane, setPlane] = useState<PlanePreview | null>(null)

  const parts = useMemo(() => project?.parts ?? [], [project])
  const active = useMemo(() => activeParts(parts), [parts])
  const tree = useMemo(() => buildTree(parts), [parts])
  const visible = active.filter((p) => isVisible(view, p.id))
  const selected = active.find((p) => p.id === view.selectedId) ?? null

  const activeKey = active.map((p) => p.id).join(',')
  useEffect(() => dispatch({ type: 'sync', activeIds: activeKey ? activeKey.split(',') : [] }), [activeKey])

  const onPlaneChange = useCallback((p: PlanePreview | null) => setPlane(p), [])

  if (loadError) {
    return (
      <div className="workspace workspace--single">
        <ErrorBanner message={loadError.status === 404 ? 'This part-design project does not exist or belongs to another browser.' : loadError.message} />
        <button type="button" className="btn" onClick={onBack}>
          Back to projects
        </button>
      </div>
    )
  }
  if (!project) return <div className="empty-state">Loading project…</div>

  const save = async (request: UpdatePartRequest) => {
    if (selected) await run(() => designApi.updatePart(project.id, selected.id, request))
  }

  const addPrimitive = async (request: AddPrimitiveRequest) => {
    const before = new Set(project.parts.map((p) => p.id))
    const next = await run(() => designApi.addPrimitive(project.id, request))
    const added = next?.parts.find((p) => !before.has(p.id))
    if (added) dispatch({ type: 'select', id: added.id })
    return next !== null
  }

  const addRelation = async (request: CreateRelationRequest) => (await run(() => designApi.addRelation(project.id, request))) !== null
  const updateRelation = async (relationId: string, request: RelationRequest) =>
    (await run(() => designApi.updateRelation(project.id, relationId, request))) !== null
  const selectPart = (partId: string) => dispatch({ type: 'select', id: partId })

  const split = async (axis: Axis, offsetMm: number, allowOpen: boolean) => {
    if (!selected) return
    const parentId = selected.id
    const next = await run(() => designApi.split(project.id, parentId, { ...planeFor(axis, offsetMm), allowOpenResult: allowOpen }))
    const firstChild = next?.parts.find((p) => p.parentPartId === parentId && p.state === 'Active')
    if (firstChild) dispatch({ type: 'select', id: firstChild.id })
  }

  return (
    <div className="workspace">
      <aside className="workspace__side">
        <section className="card">
          <div className="part-tree__header">
            <h2>{project.name}</h2>
            <button type="button" className="btn btn--small" onClick={onBack}>
              All projects
            </button>
          </div>
          <p className="muted small">
            {project.source.kind === 'none' ? (
              'No source model: built from stock shapes.'
            ) : (
              <>
                From <strong>{project.sourceFileName}</strong>
                {project.source.kind === 'reference_model' && ' (reused existing model)'}.
              </>
            )}{' '}
            Units: mm. Scale: <span className="tag tag--needs_review">estimated</span>
          </p>
          {project.source.attribution && <p className="muted small">Source: {project.source.attribution}</p>}
          <IssueList issues={project.issues} empty="" />
          <div className="part-props__actions">
            <button type="button" className="btn btn--small" disabled={busy || !project.canUndo} onClick={() => run(() => designApi.undo(project.id))}>
              ↶ Undo
            </button>
            <button type="button" className="btn btn--small" disabled={busy || !project.canRedo} onClick={() => run(() => designApi.redo(project.id))}>
              ↷ Redo
            </button>
          </div>
        </section>

        <PartTree
          nodes={tree}
          view={view}
          onSelect={(partId) => dispatch({ type: 'select', id: partId })}
          onToggleHidden={(partId) => dispatch({ type: 'toggleHidden', id: partId })}
          onIsolate={(partId) => dispatch({ type: 'isolate', id: partId })}
          onShowAll={() => dispatch({ type: 'showAll' })}
        />

        {selected && <SplitPanel projectId={project.id} part={selected} busy={busy} onPlaneChange={onPlaneChange} onSplit={split} />}

        <PrimitivePanel busy={busy} defaultOpen={project.source.kind === 'none'} onAdd={addPrimitive} />

        <RelationsPanel
          relations={project.relations}
          parts={active}
          selectedId={view.selectedId}
          busy={busy}
          onAdd={addRelation}
          onUpdate={updateRelation}
          onRemove={(relationId) => void run(() => designApi.removeRelation(project.id, relationId))}
          onSelectPart={selectPart}
        />

        <ExportPanel projectId={project.id} parts={parts} version={project.updatedAt} />

        <section className="card">
          <h2>History</h2>
          {project.history.length === 0 ? (
            <p className="muted small">No edits yet.</p>
          ) : (
            <ol className="op-history">
              {project.history.map((o) => (
                <li key={o.sequence} className={o.undone ? 'op-history__item--undone' : undefined}>
                  {o.summary}
                  {o.undone && <span className="muted small"> (undone)</span>}
                </li>
              ))}
            </ol>
          )}
        </section>
      </aside>

      <main className="workspace__main">
        <ErrorBanner message={actionError} onDismiss={clearActionError} />
        <PartViewport projectId={project.id} parts={visible} selectedId={view.selectedId} plane={selected ? plane : null} onSelect={(partId) => dispatch({ type: 'select', id: partId })} />
        {selected ? (
          <>
            <PartPropertiesPanel
              part={selected}
              busy={busy}
              onSave={save}
              onDuplicate={() => run(() => designApi.duplicatePart(project.id, selected.id))}
              onDelete={() => run(() => designApi.removePart(project.id, selected.id))}
            />
            <PartDrawingPanel projectId={project.id} part={selected} version={project.updatedAt} />
          </>
        ) : (
          <div className="empty-state">
            <p>Select a part in the list or the viewport to edit its fabrication properties or split it.</p>
          </div>
        )}
        <AssemblyPanel projectId={project.id} parts={active} version={project.updatedAt} onSelectPart={selectPart} />
        <footer className="disclaimer">
          <strong>Estimates, not measurements.</strong> Categories are suggestions from simple shape rules, and dimensions measured on a
          model (AI-generated or reused) are at an assumed scale; only stock shapes have the sizes you entered. Confirm measurements on the
          real part before cutting styrene.
        </footer>
      </main>
    </div>
  )
}
