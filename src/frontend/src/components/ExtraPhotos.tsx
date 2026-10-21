import { useEffect, useRef, useState } from 'react'
import type { ExtraPhoto, ExtraView } from '../types/api'
import { validateFile } from './ImageUpload'

export const MAX_EXTRA_PHOTOS = 5

const VIEWS: { value: ExtraView; label: string }[] = [
  { value: 'auto', label: 'Detect automatically' },
  { value: 'back', label: 'Back' },
  { value: 'left', label: 'Left side' },
  { value: 'right', label: 'Right side' },
  { value: 'top', label: 'Top' },
  { value: 'bottom', label: 'Bottom' },
  { value: 'front', label: 'Front (another angle)' },
]

interface Props {
  photos: ExtraPhoto[]
  disabled: boolean
  /** False when the connected engine ignores extra photos (TripoSR). */
  supported: boolean
  onChange: (photos: ExtraPhoto[]) => void
}

/**
 * Optional photos of the other sides of the same object. The shape still comes from the main photo;
 * these colour the sides it cannot see. Without them the AI guesses the hidden sides.
 */
export function ExtraPhotos({ photos, disabled, supported, onChange }: Props) {
  const input = useRef<HTMLInputElement>(null)
  const [error, setError] = useState<string | null>(null)

  const add = (files: FileList | null) => {
    if (!files) return
    const room = MAX_EXTRA_PHOTOS - photos.length
    const accepted: ExtraPhoto[] = []
    let problem: string | null = null
    for (const file of Array.from(files)) {
      const invalid = validateFile(file)
      if (invalid) problem = `${file.name}: ${invalid}`
      else if (accepted.length < room) accepted.push({ file, view: 'auto' })
      else problem = `At most ${MAX_EXTRA_PHOTOS} extra photos.`
    }
    setError(problem)
    if (accepted.length) onChange([...photos, ...accepted])
  }

  const setView = (index: number, view: ExtraView) => onChange(photos.map((p, i) => (i === index ? { ...p, view } : p)))
  const remove = (index: number) => onChange(photos.filter((_, i) => i !== index))

  return (
    <div className="extras">
      <div className="extras__header">
        <strong>Other sides (optional)</strong>
        <span className="muted small">
          {photos.length}/{MAX_EXTRA_PHOTOS}
        </span>
      </div>
      <p className="muted small extras__help">
        {supported
          ? 'Add photos of the back and sides of the same object to colour them from real photos. Without them the AI guesses the hidden sides.'
          : 'The connected engine (TripoSR) uses the main photo only. Start the SF3D or SPAR3D engine to use extra photos.'}
      </p>

      {photos.length > 0 && (
        <ul className="extras__list">
          {photos.map((photo, i) => (
            <li key={`${photo.file.name}-${photo.file.lastModified}-${i}`} className="extras__item">
              <Thumbnail file={photo.file} />
              <div className="extras__fields">
                <span className="extras__name" title={photo.file.name}>
                  {photo.file.name}
                </span>
                <select
                  value={photo.view}
                  disabled={disabled}
                  aria-label={`Where photo ${i + 1} was taken from`}
                  onChange={(e) => setView(i, e.target.value as ExtraView)}
                >
                  {VIEWS.map((v) => (
                    <option key={v.value} value={v.value}>
                      {v.label}
                    </option>
                  ))}
                </select>
              </div>
              <button type="button" className="btn btn--small" disabled={disabled} onClick={() => remove(i)} aria-label={`Remove photo ${i + 1}`}>
                ✕
              </button>
            </li>
          ))}
        </ul>
      )}

      <button
        type="button"
        className="btn btn--small"
        disabled={disabled || photos.length >= MAX_EXTRA_PHOTOS}
        onClick={() => input.current?.click()}
      >
        + Add photos of other sides
      </button>
      <input
        ref={input}
        type="file"
        accept="image/png,image/jpeg,image/webp"
        multiple
        hidden
        onChange={(e) => {
          add(e.target.files)
          e.target.value = ''
        }}
      />
      {error && <p className="field-error">{error}</p>}
    </div>
  )
}

function Thumbnail({ file }: { file: File }) {
  const [url, setUrl] = useState<string | null>(null)
  useEffect(() => {
    const u = URL.createObjectURL(file)
    setUrl(u)
    return () => URL.revokeObjectURL(u)
  }, [file])
  return url ? <img src={url} alt="" className="extras__thumb" /> : <span className="extras__thumb" />
}
