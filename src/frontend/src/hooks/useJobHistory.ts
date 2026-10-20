import { useCallback, useEffect, useState } from 'react'
import { api, ApiError } from '../services/apiClient'
import type { Job } from '../types/api'

const PAGE_SIZE = 10

export function useJobHistory() {
  const [jobs, setJobs] = useState<Job[]>([])
  const [page, setPage] = useState(1)
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<ApiError | null>(null)

  const refresh = useCallback(async () => {
    setLoading(true)
    try {
      const result = await api.listJobs(page, PAGE_SIZE)
      setJobs(result.items)
      setTotal(result.totalCount)
      setError(null)
    } catch (e) {
      setError(e instanceof ApiError ? e : new ApiError(0, 'error', 'Failed to load history.'))
    } finally {
      setLoading(false)
    }
  }, [page])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const remove = useCallback(
    async (id: string) => {
      await api.deleteJob(id)
      await refresh()
    },
    [refresh],
  )

  return {
    jobs,
    loading,
    error,
    refresh,
    remove,
    page,
    pageCount: Math.max(1, Math.ceil(total / PAGE_SIZE)),
    setPage,
  }
}
