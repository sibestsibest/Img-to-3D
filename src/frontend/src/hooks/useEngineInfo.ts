import { useEffect, useState } from 'react'

export interface EngineInfo {
  engine: string | null
  status: string
}

const LABELS: Record<string, string> = {
  TripoSR: 'TripoSR · ONNX',
  StableFast3D: 'Stable Fast 3D',
}

export const engineLabel = (engine: string | null) => (engine ? (LABELS[engine] ?? engine) : 'engine unavailable')

/** Reads which inference engine the API is connected to (for the header and license attribution). */
export function useEngineInfo(): EngineInfo {
  const [info, setInfo] = useState<EngineInfo>({ engine: null, status: 'unknown' })

  useEffect(() => {
    let cancelled = false
    fetch('/api/health')
      .then((r) => r.json())
      .then((h: { inference?: { status: string; engine?: string | null } }) => {
        if (!cancelled) setInfo({ engine: h.inference?.engine ?? null, status: h.inference?.status ?? 'unknown' })
      })
      .catch(() => {
        if (!cancelled) setInfo({ engine: null, status: 'unreachable' })
      })
    return () => {
      cancelled = true
    }
  }, [])

  return info
}
