/**
 * In-memory log of the browser's calls to /api, shown in the API calls panel for debugging.
 * Nothing is persisted or sent anywhere; reloading the page clears it.
 */

export interface ApiLogEntry {
  id: number
  /** Last time this call (or an identical repeat of it) happened. */
  time: Date
  firstTime: Date
  method: string
  /** Path and query, e.g. /api/models/abc */
  url: string
  /** HTTP status; 0 = network error; null = unknown (resource timing without status). */
  status: number | null
  durationMs: number
  sizeBytes: number | null
  /** Short outcome: job status/stage, error code, or content type. */
  summary: string
  correlationId: string | null
  /** Response body (JSON, pretty-printed and truncated) when available. */
  body: string | null
  source: 'api' | 'resource'
  /** How many identical consecutive calls this row stands for (polling). */
  count: number
}

export type ApiLogInput = Omit<ApiLogEntry, 'id' | 'time' | 'firstTime' | 'count'>

const MAX_ENTRIES = 300
const MAX_BODY_CHARS = 4000

let entries: ApiLogEntry[] = []
let nextId = 1
const listeners = new Set<() => void>()
/** URLs just logged by the API client, so resource timing does not log them twice. */
const loggedByClient = new Map<string, number>()

function emit() {
  for (const listener of listeners) listener()
}

export const apiLog = {
  subscribe(listener: () => void): () => void {
    listeners.add(listener)
    return () => listeners.delete(listener)
  },

  /** Newest first. Returns the same array instance until something changes (for useSyncExternalStore). */
  getEntries: (): ApiLogEntry[] => entries,

  /** Call before the API client fetches `url`: its resource-timing entry may arrive before record(). */
  markClientRequest(url: string): void {
    loggedByClient.set(url, (loggedByClient.get(url) ?? 0) + 1)
  },

  record(input: ApiLogInput, now = new Date()): void {
    const latest = entries.find((e) => e.method === input.method && e.url === input.url)
    // Collapse polling: same call, same outcome as the latest call to that URL.
    if (latest && latest === entries[0] && latest.status === input.status && latest.summary === input.summary) {
      const merged: ApiLogEntry = { ...latest, ...input, time: now, count: latest.count + 1 }
      entries = [merged, ...entries.slice(1)]
    } else {
      const entry: ApiLogEntry = { ...input, id: nextId++, time: now, firstTime: now, count: 1 }
      entries = [entry, ...entries].slice(0, MAX_ENTRIES)
    }
    emit()
  },

  clear(): void {
    entries = []
    emit()
  },

  /** True (once) when the API client already logged this URL; used to skip duplicate resource entries. */
  consumeClientLogged(url: string): boolean {
    const count = loggedByClient.get(url) ?? 0
    if (count === 0) return false
    if (count === 1) loggedByClient.delete(url)
    else loggedByClient.set(url, count - 1)
    return true
  },
}

export function formatBody(value: unknown): string | null {
  if (value === undefined) return null
  const text = typeof value === 'string' ? value : JSON.stringify(value, null, 2)
  return text.length > MAX_BODY_CHARS ? `${text.slice(0, MAX_BODY_CHARS)}\n… (truncated)` : text
}

/** One-line outcome for a response body: job status/stage, paged list size, or problem code. */
export function summarize(body: unknown): string {
  if (body && typeof body === 'object') {
    const b = body as Record<string, unknown>
    if (typeof b.code === 'string') return typeof b.detail === 'string' ? `${b.code}: ${b.detail}` : b.code
    if (typeof b.status === 'string' && typeof b.id === 'string') {
      const stage = typeof b.stage === 'string' && b.stage ? ` / ${b.stage}` : ''
      const progress = typeof b.stageProgress === 'number' ? ` ${Math.round(b.stageProgress * 100)}%` : ''
      return `${b.status}${stage}${progress}`
    }
    if (Array.isArray(b.items)) return `${b.items.length} item(s)${typeof b.totalCount === 'number' ? ` of ${b.totalCount}` : ''}`
    if (typeof b.status === 'string') return b.status
  }
  return ''
}

function pathOf(url: string): string {
  try {
    const u = new URL(url, location.origin)
    return u.pathname + u.search
  } catch {
    return url
  }
}

let observer: PerformanceObserver | null = null

/**
 * Logs /api resources the API client does not fetch itself (3D preview, images, downloads).
 * Status comes from PerformanceResourceTiming.responseStatus where the browser provides it.
 */
export function startResourceLogging(): () => void {
  if (observer || typeof PerformanceObserver === 'undefined') return () => {}
  observer = new PerformanceObserver((list) => {
    for (const item of list.getEntries() as PerformanceResourceTiming[]) {
      const url = pathOf(item.name)
      if (!url.startsWith('/api/') || apiLog.consumeClientLogged(url)) continue
      const status = (item as PerformanceResourceTiming & { responseStatus?: number }).responseStatus
      apiLog.record({
        method: 'GET',
        url,
        status: status && status > 0 ? status : null,
        durationMs: Math.round(item.duration),
        sizeBytes: item.encodedBodySize || item.transferSize || null,
        summary: item.initiatorType,
        correlationId: null,
        body: null,
        source: 'resource',
      })
    }
  })
  observer.observe({ type: 'resource', buffered: true })
  return () => {
    observer?.disconnect()
    observer = null
  }
}
