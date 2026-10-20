import { urls } from '../services/apiClient'
import { formatMm } from '../services/geometry'
import type { Job } from '../types/api'

export function DownloadPanel({ job }: { job: Job }) {
  const longest = job.dimensionsMm ? Math.max(job.dimensionsMm.x, job.dimensionsMm.y, job.dimensionsMm.z) : null

  return (
    <section className="card">
      <h2>Download</h2>
      <div className="downloads">
        <a className={`btn${job.downloads.glb ? '' : ' btn--disabled'}`} href={job.downloads.glb ? urls.download(job, 'glb') : undefined} download>
          GLB <span className="btn__sub">preview / colour · metres</span>
        </a>
        <a className={`btn${job.downloads.stl ? '' : ' btn--disabled'}`} href={job.downloads.stl ? urls.download(job, 'stl') : undefined} download>
          STL <span className="btn__sub">3D printing · millimetres</span>
        </a>
      </div>
      {job.downloads.stlError && <p className="error-text">{job.downloads.stlError}</p>}
      <ul className="muted small notes">
        <li>
          STL files do not store units. This file is written in millimetres{longest ? ` (longest side ${formatMm(longest)} mm)` : ''}; if
          your slicer shows it 25.4× too small or large, it assumed inches.
        </li>
        <li>GLB uses metres as required by the glTF standard and keeps vertex colours; STL has geometry only.</li>
        <li>Inspect the mesh in your slicer before printing: AI meshes can contain thin walls or floating fragments.</li>
      </ul>
    </section>
  )
}
