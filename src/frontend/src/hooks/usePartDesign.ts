import { useCallback, useEffect, useState } from 'react'
import { ApiError, designApi } from '../services/apiClient'
import type { PartDesignProject } from '../types/partDesign'

/** Loads one part-design project; every mutation returns the full project, which replaces local state. */
export function usePartDesign(id: string) {
  const [project, setProject] = useState<PartDesignProject | null>(null)
  const [loadError, setLoadError] = useState<ApiError | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    setProject(null)
    setLoadError(null)
    const controller = new AbortController()
    designApi
      .get(id, controller.signal)
      .then(setProject)
      .catch((e: unknown) => {
        if (!controller.signal.aborted) setLoadError(e instanceof ApiError ? e : new ApiError(0, 'error', 'Failed to load the project.'))
      })
    return () => controller.abort()
  }, [id])

  /** Runs a mutation; returns the new project, or null after showing the error. */
  const run = useCallback(async (action: () => Promise<PartDesignProject>): Promise<PartDesignProject | null> => {
    setBusy(true)
    setActionError(null)
    try {
      const next = await action()
      setProject(next)
      return next
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'The operation failed.')
      return null
    } finally {
      setBusy(false)
    }
  }, [])

  return { project, loadError, actionError, clearActionError: () => setActionError(null), busy, run }
}
