import { useEffect, useMemo, useState } from 'react'
import { ErrorBanner } from '../components/ErrorBanner'
import { OriginBadge } from '../components/OriginBadge'
import { ApiError, designApi, parametricApi } from '../services/apiClient'
import { COMPONENTS_FIELD, fieldOf, initialValues, parameterLabel, toParameters, type ParametricFormErrors, type ParametricFormValues } from '../services/parametricForm'
import type { ParametricCatalog, ParametricTemplate } from '../types/partDesign'

interface Props {
  /** Project to make a variant of (its template and parameters prefill the form). */
  from: string | null
  onCreated: (projectId: string) => void
  onBack: () => void
}

/**
 * Generates a model from template parameters with the parametric geometry worker: exact sizes,
 * no AI. The result opens in the part designer with its cutting templates, views and BOM.
 */
export function ParametricPage({ from, onCreated, onBack }: Props) {
  const [catalog, setCatalog] = useState<ParametricCatalog | null>(null)
  const [unavailable, setUnavailable] = useState<string | null>(null)
  const [templateName, setTemplateName] = useState('')
  const [values, setValues] = useState<ParametricFormValues>({})
  const [errors, setErrors] = useState<ParametricFormErrors>({})
  const [name, setName] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    const controller = new AbortController()
    const load = async () => {
      try {
        const loaded = await parametricApi.templates(controller.signal)
        const source = from ? await designApi.get(from, controller.signal) : null
        const preset = source?.parametric
        const template = loaded.templates.find((t) => t.name === preset?.template) ?? loaded.templates[0]
        setCatalog(loaded)
        if (template) {
          setTemplateName(template.name)
          setValues(initialValues(template, preset?.parameters))
        }
        if (source) setName(`${source.name} (variant)`.slice(0, 100))
      } catch (e) {
        if (controller.signal.aborted) return
        if (e instanceof ApiError && e.status === 503) setUnavailable(e.message)
        else setError(e instanceof Error ? e.message : 'Could not load the templates.')
      }
    }
    void load()
    return () => controller.abort()
  }, [from])

  const template = useMemo<ParametricTemplate | undefined>(() => catalog?.templates.find((t) => t.name === templateName), [catalog, templateName])

  const chooseTemplate = (next: string) => {
    const t = catalog?.templates.find((x) => x.name === next)
    if (!t) return
    setTemplateName(next)
    setValues(initialValues(t))
    setErrors({})
  }

  const submit = async () => {
    if (!template) return
    const result = toParameters(template, values)
    if ('errors' in result) {
      setErrors(result.errors)
      setError('Some values are not valid; see the marked fields.')
      return
    }
    setErrors({})
    setError(null)
    setBusy(true)
    try {
      const project = await parametricApi.create({ template: template.name, parameters: result.parameters, name: name.trim() || undefined })
      onCreated(project.id)
    } catch (e) {
      const field = e instanceof ApiError ? fieldOf(e.field) : null
      if (field && e instanceof ApiError) setErrors({ [field]: e.message })
      setError(e instanceof Error ? e.message : 'The model could not be generated.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="workspace workspace--single">
      <section className="card">
        <div className="part-tree__header">
          <h2>
            New parametric model <OriginBadge origin="parametric" />
          </h2>
          <button type="button" className="btn btn--small" onClick={onBack}>
            All projects
          </button>
        </div>
        <p className="muted small">
          Geometry from exact sizes, no AI and no photos. Sheet parts are built from PS sheet of the thicknesses sold for
          scratch-building. The result opens in the part designer with 1:1 cutting templates, orthographic views and a BOM.
        </p>
        <ErrorBanner message={error} onDismiss={() => setError(null)} />

        {unavailable ? (
          <div className="parametric__unavailable">
            <p className="error-text">{unavailable}</p>
            <p className="muted small">
              Set up the geometry worker once with <code>.\scripts\setup.ps1</code> (needs <code>uv</code>), or follow
              src/parametric-geometry/README.md, then reload this page. Everything else in the application works without it.
            </p>
          </div>
        ) : !catalog ? (
          <p className="muted">Loading templates…</p>
        ) : (
          <form
            onSubmit={(e) => {
              e.preventDefault()
              void submit()
            }}
          >
            <div className="primitive-form__dims">
              <label className="field">
                <span>Template</span>
                <select value={templateName} disabled={busy} onChange={(e) => chooseTemplate(e.target.value)}>
                  {catalog.templates.map((t) => (
                    <option key={t.name} value={t.name}>
                      {parameterLabel(t.name)}
                    </option>
                  ))}
                </select>
              </label>
              <label className="field">
                <span>Project name (optional)</span>
                <input value={name} maxLength={100} disabled={busy} onChange={(e) => setName(e.target.value)} />
              </label>
            </div>
            {template && <p className="muted small">{template.description}</p>}

            <div className="parametric__grid">
              {template?.parameters.map((p) => (
                <label key={p.name} className="field" title={p.description}>
                  <span>
                    {parameterLabel(p.name)}
                    {p.unit ? ` (${p.unit})` : ''}
                  </span>
                  {p.allowed ? (
                    <select value={values[p.name] ?? ''} disabled={busy} onChange={(e) => setValues((v) => ({ ...v, [p.name]: e.target.value }))}>
                      {p.allowed.map((a) => (
                        <option key={a} value={String(a)}>
                          {a} {p.unit}
                        </option>
                      ))}
                    </select>
                  ) : (
                    <input
                      inputMode={p.type === 'integer' ? 'numeric' : 'decimal'}
                      value={values[p.name] ?? ''}
                      disabled={busy}
                      aria-invalid={errors[p.name] ? true : undefined}
                      onChange={(e) => setValues((v) => ({ ...v, [p.name]: e.target.value }))}
                    />
                  )}
                  <span className="muted small">{p.description}</span>
                  {errors[p.name] && <span className="field-error">{errors[p.name]}</span>}
                </label>
              ))}
            </div>

            {template?.shapes && (
              <label className="field">
                <span>Components (JSON)</span>
                <textarea
                  className="parametric__components"
                  rows={14}
                  spellCheck={false}
                  value={values[COMPONENTS_FIELD] ?? ''}
                  disabled={busy}
                  aria-invalid={errors[COMPONENTS_FIELD] ? true : undefined}
                  onChange={(e) => setValues((v) => ({ ...v, [COMPONENTS_FIELD]: e.target.value }))}
                />
                <span className="muted small">
                  Shapes: {Object.entries(template.shapes).map(([shape, s]) => `${shape} (${s.fields.join(', ')})`).join('; ')}. Each may have a
                  transform: translate [x, y, z] mm, rotate_deg [x, y, z], scale [x, y, z]. Plates and shells need a PS thickness.
                </span>
                {errors[COMPONENTS_FIELD] && <span className="field-error">{errors[COMPONENTS_FIELD]}</span>}
              </label>
            )}

            <div className="part-props__actions">
              <button type="submit" className="btn" disabled={busy || !template}>
                {busy ? 'Generating… (a few seconds)' : 'Generate and open in the part designer'}
              </button>
              <span className="muted small">
                Sheet thicknesses: {catalog.sheetThicknessesMm.join(', ')} mm · {catalog.engine.name} {catalog.engine.version}
              </span>
            </div>
          </form>
        )}
      </section>
    </div>
  )
}
