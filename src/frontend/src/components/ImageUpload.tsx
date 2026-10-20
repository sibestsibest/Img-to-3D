import { useEffect, useRef, useState, type DragEvent } from 'react'

const ACCEPTED = ['image/png', 'image/jpeg', 'image/webp']
export const MAX_UPLOAD_BYTES = 20 * 1024 * 1024

interface Props {
  file: File | null
  disabled: boolean
  onSelect: (file: File | null) => void
}

/** Quick client-side checks for fast feedback; the server re-validates the actual image content. */
export function validateFile(file: File): string | null {
  if (!ACCEPTED.includes(file.type)) return 'Unsupported file type. Use a PNG, JPEG or WebP image.'
  if (file.size > MAX_UPLOAD_BYTES) return 'The image is larger than 20 MB.'
  if (file.size === 0) return 'The file is empty.'
  return null
}

export function ImageUpload({ file, disabled, onSelect }: Props) {
  const input = useRef<HTMLInputElement>(null)
  const [preview, setPreview] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [dragging, setDragging] = useState(false)

  useEffect(() => {
    if (!file) {
      setPreview(null)
      return
    }
    const url = URL.createObjectURL(file)
    setPreview(url)
    return () => URL.revokeObjectURL(url)
  }, [file])

  const choose = (candidate: File | undefined) => {
    if (!candidate) return
    const problem = validateFile(candidate)
    setError(problem)
    onSelect(problem ? null : candidate)
  }

  const onDrop = (e: DragEvent) => {
    e.preventDefault()
    setDragging(false)
    if (!disabled) choose(e.dataTransfer.files[0])
  }

  return (
    <div className="upload">
      <div
        className={`dropzone${dragging ? ' dropzone--active' : ''}${disabled ? ' dropzone--disabled' : ''}`}
        role="button"
        tabIndex={0}
        aria-disabled={disabled}
        onClick={() => !disabled && input.current?.click()}
        onKeyDown={(e) => (e.key === 'Enter' || e.key === ' ') && !disabled && input.current?.click()}
        onDragOver={(e) => {
          e.preventDefault()
          setDragging(true)
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={onDrop}
      >
        {preview ? (
          <img src={preview} alt="Selected image preview" className="dropzone__preview" />
        ) : (
          <div className="dropzone__hint">
            <strong>Drop an image here</strong>
            <span>or click to choose — PNG, JPEG or WebP, up to 20 MB</span>
          </div>
        )}
        <input
          ref={input}
          type="file"
          accept={ACCEPTED.join(',')}
          hidden
          onChange={(e) => {
            choose(e.target.files?.[0])
            e.target.value = ''
          }}
        />
      </div>
      {file && (
        <div className="upload__meta">
          <span title={file.name}>{file.name}</span>
          <span>{(file.size / 1024 / 1024).toFixed(2)} MB</span>
        </div>
      )}
      {error && <p className="field-error">{error}</p>}
      <ul className="tips">
        <li>Use one part, centred, on a plain contrasting background.</li>
        <li>A single photo cannot show hidden sides; the back and interior are guessed by the AI.</li>
      </ul>
    </div>
  )
}
