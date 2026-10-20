import { useCallback, useRef, useState } from 'react'
import { api, ApiError } from '../services/apiClient'
import type { ExtraPhoto, Job } from '../types/api'

/**
 * Submits an image (plus optional extra photos) once. A ref guards against double clicks before
 * React re-renders, and an Idempotency-Key per main file and extra-photo set makes a retried
 * request return the same job instead of a new one.
 */
export function useGenerate(onCreated: (job: Job) => void) {
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)
  const inFlight = useRef(false)
  const keys = useRef(new WeakMap<File, { signature: string; key: string }>())

  const submit = useCallback(
    async (file: File, extras: ExtraPhoto[] = []) => {
      if (inFlight.current) return
      inFlight.current = true
      setSubmitting(true)
      setError(null)

      // Changing the extra photos or their views is a different request, so it gets a new key.
      const signature = extras.map((e) => `${e.file.name}:${e.file.size}:${e.file.lastModified}:${e.view}`).join('|')
      let entry = keys.current.get(file)
      if (!entry || entry.signature !== signature) {
        entry = { signature, key: crypto.randomUUID() }
        keys.current.set(file, entry)
      }

      try {
        onCreated(await api.createJob(file, entry.key, extras))
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
