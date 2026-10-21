import { useCallback, useEffect, useState } from 'react'

/**
 * Minimal hash routing (the app has no router): the workspace is the default view, part design
 * lives under #/designs and #/designs/<id>, so a project survives a reload and can be bookmarked.
 */
export type Route = { view: 'workspace' } | { view: 'designs' } | { view: 'design'; id: string }

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function parseRoute(hash: string): Route {
  const parts = hash.replace(/^#\/?/, '').split('/').filter(Boolean)
  if (parts[0] !== 'designs') return { view: 'workspace' }
  if (parts.length === 1) return { view: 'designs' }
  return GUID.test(parts[1]) ? { view: 'design', id: parts[1].toLowerCase() } : { view: 'designs' }
}

export function routeHash(route: Route): string {
  switch (route.view) {
    case 'workspace':
      return '#/'
    case 'designs':
      return '#/designs'
    case 'design':
      return `#/designs/${route.id}`
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
