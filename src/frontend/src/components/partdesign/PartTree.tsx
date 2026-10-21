import { CATEGORY_LABELS, isVisible, type TreeNode, type ViewState } from '../../services/partDesignState'

interface Props {
  nodes: TreeNode[]
  view: ViewState
  onSelect: (id: string) => void
  onToggleHidden: (id: string) => void
  onIsolate: (id: string) => void
  onShowAll: () => void
}

/** Parts with their provenance: split results and duplicates sit under the part they came from. */
export function PartTree({ nodes, view, onSelect, onToggleHidden, onIsolate, onShowAll }: Props) {
  return (
    <section className="card">
      <div className="part-tree__header">
        <h2>Parts</h2>
        {(view.hidden.size > 0 || view.isolatedId) && (
          <button type="button" className="btn btn--small" onClick={onShowAll}>
            Show all
          </button>
        )}
      </div>
      <ul className="part-tree" role="tree">
        {nodes.map((n) => (
          <Node key={n.part.id} node={n} depth={0} view={view} onSelect={onSelect} onToggleHidden={onToggleHidden} onIsolate={onIsolate} />
        ))}
      </ul>
      <p className="muted small">Greyed entries were replaced by a split; undo restores them. The source model is never changed.</p>
    </section>
  )
}

function Node({ node, depth, view, onSelect, onToggleHidden, onIsolate }: { node: TreeNode; depth: number } & Omit<Props, 'nodes' | 'onShowAll'>) {
  const p = node.part
  const active = p.state === 'Active'
  const selected = view.selectedId === p.id
  const visible = active && isVisible(view, p.id)
  return (
    <li role="treeitem" aria-selected={selected}>
      <div className={`part-tree__row${selected ? ' part-tree__row--selected' : ''}${active ? '' : ' part-tree__row--inactive'}`} style={{ paddingLeft: `${0.4 + depth * 1.1}rem` }}>
        {active ? (
          <button type="button" className="part-tree__icon" aria-label={visible ? `Hide ${p.name}` : `Show ${p.name}`} title={visible ? 'Hide' : 'Show'} onClick={() => onToggleHidden(p.id)}>
            {visible ? '●' : '○'}
          </button>
        ) : (
          <span className="part-tree__icon" aria-hidden>
            ◌
          </span>
        )}
        <button type="button" className="part-tree__name" disabled={!active} onClick={() => onSelect(p.id)} title={p.name}>
          {p.name}
        </button>
        {active && (
          <>
            <span className={`tag tag--${p.category}`}>{CATEGORY_LABELS[p.category]}</span>
            <span className={`status-pill status-pill--${p.validation.status}`} title={`Validation: ${p.validation.status}`}>
              {p.validation.status}
            </span>
            <button type="button" className={`part-tree__icon${view.isolatedId === p.id ? ' part-tree__icon--on' : ''}`} title="Isolate" aria-label={`Isolate ${p.name}`} onClick={() => onIsolate(p.id)}>
              ◎
            </button>
          </>
        )}
      </div>
      {node.children.length > 0 && (
        <ul role="group">
          {node.children.map((c) => (
            <Node key={c.part.id} node={c} depth={depth + 1} view={view} onSelect={onSelect} onToggleHidden={onToggleHidden} onIsolate={onIsolate} />
          ))}
        </ul>
      )}
    </li>
  )
}
