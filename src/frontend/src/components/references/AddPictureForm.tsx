import { useState } from 'react'
import { MANUAL_LICENSES } from '../../services/referenceState'
import type { ManualPicture } from '../../types/references'

interface Props {
  busy: boolean
  onAdd: (picture: ManualPicture) => Promise<boolean>
}

/** Add a picture you have (own photo, scan, saved web image); the server validates and re-encodes it. */
export function AddPictureForm({ busy, onAdd }: Props) {
  const [file, setFile] = useState<File | null>(null)
  const [title, setTitle] = useState('')
  const [author, setAuthor] = useState('')
  const [pageUrl, setPageUrl] = useState('')
  const [license, setLicense] = useState('unknown')
  const [inputKey, setInputKey] = useState(0)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!file) return
    if (await onAdd({ file, title, author, pageUrl, license })) {
      setFile(null)
      setTitle('')
      setAuthor('')
      setPageUrl('')
      setLicense('unknown')
      setInputKey((k) => k + 1)
    }
  }

  return (
    <form className="ref-add" onSubmit={submit}>
      <label className="field">
        <span>Picture (PNG, JPEG or WebP)</span>
        <input key={inputKey} type="file" accept="image/png,image/jpeg,image/webp" disabled={busy} onChange={(e) => setFile(e.target.files?.[0] ?? null)} />
      </label>
      <div className="part-props__grid">
        <label className="field">
          <span>Title</span>
          <input value={title} maxLength={300} disabled={busy} onChange={(e) => setTitle(e.target.value)} placeholder="File name if empty" />
        </label>
        <label className="field">
          <span>Author</span>
          <input value={author} maxLength={200} disabled={busy} onChange={(e) => setAuthor(e.target.value)} />
        </label>
        <label className="field">
          <span>Source page (link only)</span>
          <input type="url" value={pageUrl} maxLength={2000} disabled={busy} onChange={(e) => setPageUrl(e.target.value)} placeholder="https://…" />
        </label>
        <label className="field">
          <span>Licence</span>
          <select value={license} disabled={busy} onChange={(e) => setLicense(e.target.value)}>
            {MANUAL_LICENSES.map((l) => (
              <option key={l.value} value={l.value}>
                {l.label}
              </option>
            ))}
          </select>
        </label>
      </div>
      <button type="submit" className="btn" disabled={busy || !file}>
        Add picture
      </button>
    </form>
  )
}
