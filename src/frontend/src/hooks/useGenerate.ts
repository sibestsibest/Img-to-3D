import { useCallback, useRef, useState } from 'react'
import { api, ApiError } from '../services/apiClient'
import type { Job } from '../types/api'

/**
 * Submits an image once. A ref guards against double clicks before React re-renders, and a
 * per-file Idempotency-Key makes a retried request return the same job instead of a new one.
 */
export function useGenerate(onCreated: (job: Job) => void) {
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)
  const inFlight = useRef(false)
  const keys = useRef(new WeakMap<File, string>())

  const submit = useCallback(
    async (file: File) => {
      if (inFlight.current) return
      inFlight.current = true
      setSubmitting(true)
      setError(null)

      let key = keys.current.get(file)
      if (!key) {
        key = crypto.randomUUID()
        keys.current.set(file, key)
      }

      try {
        onCreated(await api.createJob(file, key))
      } catch (e) {
        setError(e instanceof ApiError ? e : new ApiError(0, 'error', 'Upload failed.'))
      } finally {
        inFlight.current = false
        setSubmitting(false)
      }
    },
    [onCreated],
  )

  return { submit, submitting, error, clearError: () => setError(null) }
}
