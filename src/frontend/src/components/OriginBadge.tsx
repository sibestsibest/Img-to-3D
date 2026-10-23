import { ORIGINS, originOf } from '../services/origin'

/** Where a model's geometry comes from; the tooltip says what that means for its accuracy. */
export function OriginBadge({ origin, detail }: { origin: string | null | undefined; detail?: string | null }) {
  const o = originOf(origin)
  const { label, description } = ORIGINS[o]
  return (
    <span className={`tag tag--origin tag--origin-${o}`} title={description}>
      {label}
      {detail ? ` · ${detail}` : ''}
    </span>
  )
}
