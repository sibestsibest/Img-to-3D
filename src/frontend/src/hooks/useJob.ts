import { useCallback, useEffect, useRef, useState } from 'react'
import { api, ApiError } from '../services/apiClient'
import { isTerminal, type Job } from '../types/api'

const POLL_MS = 1500

/** Tracks one job, polling until it reaches a terminal status. */
export function useJob(jobId: string | null) {
  const [job, setJob] = useState<Job | null>(null)
  const [error, setError] = useState<ApiError | null>(null)
  const timer = useRef<number | undefined>(undefined)

  useEffect(() => {
    setJob(null)
    setError(null)
    if (!jobId) return

    const controller = new AbortController()
    let failures = 0

    const tick = async () => {
      try {
        const next = await api.getJob(jobId, controller.signal)
        failures = 0
        setJob(next)
        setError(null)
        if (!isTerminal(next.status)) timer.current = window.setTimeout(tick, POLL_MS)
      } catch (e) {
        if (controller.signal.aborted) return
        const err = e instanceof ApiError ? e : new ApiError(0, 'error', 'Failed to load job status.')
        if (err.status === 404) {
          setError(err)
          return
        }
        // Transient errors: back off and keep polling, surfacing the error meanwhile.
        failures += 1
        setError(err)
        timer.current = window.setTimeout(tick, Math.min(POLL_MS * 2 ** failures, 15000))
      }
    }

    void tick()
    return () => {
      controller.abort()
      window.clearTimeout(timer.current)
    }
  }, [jobId])

  /** Replace local state with a fresh server response (after scale/cancel). */
  const update = useCallback((next: Job) => setJob(next), [])

  return { job, error, update }
}
