import { useCallback, useEffect, useState } from 'react'
import { ApiError, photogrammetryApi } from '../services/apiClient'
import { isActive } from '../services/photogrammetryState'
import type { PhotogrammetryJob } from '../types/photogrammetry'

const POLL_MS = 2000

/** Loads one photogrammetry job and polls it while Meshroom is queued or running. */
export function usePhotogrammetryJob(id: string) {
  const [job, setJob] = useState<PhotogrammetryJob | null>(null)
  const [loadError, setLoadError] = useState<ApiError | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(
    async (signal?: AbortSignal) => {
      try {
        setJob(await photogrammetryApi.get(id, signal))
      } catch (e) {
        if (!signal?.aborted) setLoadError(e instanceof ApiError ? e : new ApiError(0, 'error', 'Failed to load the photo set.'))
      }
    },
    [id],
  )

  useEffect(() => {
    const controller = new AbortController()
    void load(controller.signal)
    return () => controller.abort()
  }, [load])

  const active = job != null && isActive(job.status)
  useEffect(() => {
    if (!active) return
    const timer = window.setInterval(() => void load(), POLL_MS)
    return () => window.clearInterval(timer)
  }, [active, load])

  /** Runs an action that returns the job; shows its error instead of throwing. */
  const run = useCallback(async (action: () => Promise<PhotogrammetryJob>): Promise<PhotogrammetryJob | null> => {
    setBusy(true)
    setActionError(null)
    try {
      const next = await action()
      setJob(next)
      return next
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'The operation failed.')
      return null
    } finally {
      setBusy(false)
    }
  }, [])

  return { job, loadError, actionError, setActionError, busy, run }
}
