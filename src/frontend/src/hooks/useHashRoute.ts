import { useCallback, useEffect, useState } from 'react'

/**
 * Minimal hash routing (the app has no router): the workspace is the default view (#/jobs/<id> opens it
 * on one generation job), part design
 * lives under #/designs and #/designs/<id>, reference searches under #/references and
 * #/references/<id>, and new parametric models under #/parametric (#/parametric/<project id> for a
 * variant of an earlier one), so a project or search survives a reload and can be bookmarked.
 */
export type Route =
  | { view: 'workspace' }
  | { view: 'job'; id: string }
  | { view: 'designs' }
  | { view: 'design'; id: string }
  | { view: 'references' }
  | { view: 'reference'; id: string }
  /** New parametric model; <from> prefills it from an earlier parametric project (a variant). */
  | { view: 'parametric'; from: string | null }

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function parseRoute(hash: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean)
  if (parts[0] === 'jobs' && parts.length > 1 && GUID.test(parts[1])) return { view: 'job', id: parts[1].toLowerCase() }
  if (parts[0] === 'designs') {
    if (parts.length === 1) return { view: 'designs' }
    return GUID.test(parts[1]) ? { view: 'design', id: parts[1].toLowerCase() } : { view: 'designs' }
  }
  if (parts[0] === 'references') {
    if (parts.length === 1) return { view: 'references' }
    return GUID.test(parts[1]) ? { view: 'reference', id: parts[1].toLowerCase() } : { view: 'references' }
  }
  if (parts[0] === 'parametric') return { view: 'parametric', from: parts.length > 1 && GUID.test(parts[1]) ? parts[1].toLowerCase() : null }
  return { view: 'workspace' }
}

export function routeHash(route: Route): string {
  switch (route.view) {
    case 'workspace':
      return '#/'
    case 'job':
      return `#/jobs/${route.id}`
    case 'designs':
      return '#/designs'
    case 'design':
      return `#/designs/${route.id}`
    case 'references':
      return '#/references'
    case 'reference':
      return `#/references/${route.id}`
    case 'parametric':
      return route.from ? `#/parametric/${route.from}` : '#/parametric'
  }
}

export function useHashRoute() {
  const [route, setRoute] = useState<Route>(() => parseRoute(window.location.hash))

  useEffect(() => {
    const onChange = () => setRoute(parseRoute(window.location.hash))
    window.addEventListener('hashchange', onChange)
    return () => window.removeEventListener('hashchange', onChange)
  }, [])

  const navigate = useCallback((next: Route) => {
    window.location.hash = routeHash(next)
  }, [])

  return { route, navigate }
}
