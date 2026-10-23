import type { ParametricInfo } from '../../types/partDesign'
import { parameterLabel } from '../../services/parametricForm'

/** How a parametric project was generated, its generated files, and a way to make a variant. */
export function ParametricPanel({ projectId, info }: { projectId: string; info: ParametricInfo }) {
  const scalars = Object.entries(info.parameters).filter(([, v]) => typeof v === 'number')
  const components = Array.isArray(info.parameters.components) ? info.parameters.components.length : null
  return (
    <section className="card">
      <h2>Parametric model</h2>
      <p className="muted small">
        Template <strong>{parameterLabel(info.template)}</strong>, geometry engine {info.engineVersion}. These files show the model as generated;
        edits made in the designer afterwards are not in them.
      </p>
      <ul className="parametric__files">
        {info.files.map((f) => (
          <li key={f.kind}>
            <a href={f.url} download>
              {f.kind}
            </a>{' '}
            <span className="muted small">{f.description}</span>
          </li>
        ))}
      </ul>
      <details>
        <summary className="small">Parameters ({scalars.length + (components != null ? 1 : 0)})</summary>
        <dl className="parametric__params small">
          {scalars.map(([k, v]) => (
            <div key={k}>
              <dt>{parameterLabel(k)}</dt>
              <dd>{String(v)}</dd>
            </div>
          ))}
          {components != null && (
            <div>
              <dt>Components</dt>
              <dd>{components}</dd>
            </div>
          )}
        </dl>
      </details>
      <div className="part-props__actions">
        <a className="btn btn--small" href={`#/parametric/${projectId}`}>
          Create a variant with other parameters
        </a>
      </div>
    </section>
  )
}
