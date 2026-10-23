import { Bounds, Grid, OrbitControls, useBounds, useGLTF } from '@react-three/drei'
import { Canvas, type ThreeEvent } from '@react-three/fiber'
import { Suspense, useEffect, useMemo, useState } from 'react'
import { DoubleSide, Mesh, MeshStandardMaterial, type Object3D } from 'three'
import { urls } from '../../services/apiClient'
import { explodedOffsets } from '../../services/assemblyState'
import type { Axis } from '../../services/partDesignState'
import type { DesignPart } from '../../types/partDesign'
import { StudioEnvironment, ViewerErrorBoundary } from '../ModelViewer'

export interface PlanePreview {
  partId: string
  axis: Axis
  offsetMm: number
}

interface Props {
  projectId: string
  parts: DesignPart[]
  selectedId: string | null
  plane: PlanePreview | null
  onSelect: (id: string | null) => void
}

/** Derived parts only (never the source mesh), each loaded from its own immutable GLB in metres. */
export function PartViewport({ projectId, parts, selectedId, plane, onSelect }: Props) {
  const [wireframe, setWireframe] = useState(false)
  const [fitToken, setFitToken] = useState(0)
  const [explode, setExplode] = useState(0)
  const offsets = useMemo(() => explodedOffsets(parts, explode), [parts, explode])

  // Refit when the set of parts changes (split, delete, isolate), not on every selection.
  const partKey = parts.map((p) => p.id).join(',')
  useEffect(() => setFitToken((t) => t + 1), [partKey])

  const extent = useMemo(() => {
    if (parts.length === 0) return 0.1
    const max = Math.max(...parts.map((p) => Math.max(p.geometry.bounds.max.x, p.geometry.bounds.max.z, -p.geometry.bounds.min.x, -p.geometry.bounds.min.z, p.geometry.bounds.max.y)))
    return Math.max(max / 1000, 0.01) * 2
  }, [parts])

  const planePart = plane ? parts.find((p) => p.id === plane.partId) : undefined

  return (
    <div className="viewer">
      <div className="viewer__toolbar">
        <button type="button" className="btn btn--small" onClick={() => setFitToken((t) => t + 1)}>
          Fit to parts
        </button>
        <label className="toggle">
          <input type="checkbox" checked={wireframe} onChange={(e) => setWireframe(e.target.checked)} />
          Wireframe
        </label>
        <label className="viewer__explode" title="Display only: moves parts apart from the centre of the assembly; geometry and exports do not change.">
          Exploded
          <input type="range" min={0} max={1.5} step={0.05} value={explode} onChange={(e) => setExplode(Number(e.target.value))} aria-label="Exploded view" />
        </label>
      </div>

      <ViewerErrorBoundary key={projectId}>
        <Canvas camera={{ position: [0, 0.08, 0.25], fov: 40, near: 0.0005, far: 50 }} dpr={[1, 2]} onPointerMissed={() => onSelect(null)}>
          <color attach="background" args={['#eef0f3']} />
          <StudioEnvironment />
          <hemisphereLight args={['#ffffff', '#8a8f99', 1.1]} />
          <directionalLight position={[0.5, 1, 0.8]} intensity={1.6} />
          <directionalLight position={[-0.6, 0.3, -0.7]} intensity={0.5} />
          <Suspense fallback={null}>
            <Bounds fit clip observe margin={1.4}>
              <Refit token={fitToken} />
              {parts.map((p) => (
                <PartMesh key={p.id} url={urls.partGeometry(projectId, p.id)} offset={offsets.get(p.id)} selected={p.id === selectedId} wireframe={wireframe} onSelect={() => onSelect(p.id)} />
              ))}
            </Bounds>
            {planePart && plane && <CutPlane part={planePart} axis={plane.axis} offsetMm={plane.offsetMm} shift={offsets.get(planePart.id)} />}
          </Suspense>
          <Grid args={[extent * 3, extent * 3]} cellSize={extent / 20} sectionSize={extent / 4} cellColor="#c5cad3" sectionColor="#9aa1ad" fadeDistance={extent * 6} />
          <OrbitControls makeDefault enableDamping />
        </Canvas>
      </ViewerErrorBoundary>
      {parts.length === 0 && <div className="viewer__loading">No visible parts</div>}
      <div className="viewer__dims">Derived parts · millimetres in the panels, estimated scale{explode > 0 ? ' · exploded view (display only)' : ''}</div>
    </div>
  )
}

function PartMesh({ url, offset, selected, wireframe, onSelect }: { url: string; offset?: [number, number, number]; selected: boolean; wireframe: boolean; onSelect: () => void }) {
  const { scene } = useGLTF(url)
  const object = useMemo(() => scene.clone(true), [scene])

  useEffect(() => {
    object.traverse((child: Object3D) => {
      if (!(child instanceof Mesh)) return
      const hasColors = child.geometry.getAttribute('color') != null
      child.material = new MeshStandardMaterial({
        vertexColors: hasColors,
        color: hasColors ? '#ffffff' : '#c9ced6',
        roughness: 0.75,
        metalness: 0,
        emissive: selected ? '#2f6fde' : '#000000',
        emissiveIntensity: selected ? 0.45 : 0,
        wireframe,
      })
    })
  }, [object, selected, wireframe])

  const click = (e: ThreeEvent<MouseEvent>) => {
    e.stopPropagation()
    onSelect()
  }

  return <primitive object={object} position={offset ?? [0, 0, 0]} onClick={click} />
}

/** Translucent preview of the cutting plane, sized to the part it will cut. Visual only: the cut runs on the server. */
function CutPlane({ part, axis, offsetMm, shift }: { part: DesignPart; axis: Axis; offsetMm: number; shift?: [number, number, number] }) {
  const b = part.geometry.bounds
  const pad = 1.15 / 1000
  const center: [number, number, number] = [((b.min.x + b.max.x) / 2) / 1000, ((b.min.y + b.max.y) / 2) / 1000, ((b.min.z + b.max.z) / 2) / 1000]
  const position: [number, number, number] = [...center]
  position[axis === 'x' ? 0 : axis === 'y' ? 1 : 2] = offsetMm / 1000
  // Follow the part when the view is exploded.
  if (shift) for (let i = 0; i < 3; i++) position[i] += shift[i]
  // PlaneGeometry lies in XY (normal +Z); rotate it so its normal follows the cutting axis.
  const rotation: [number, number, number] = axis === 'x' ? [0, Math.PI / 2, 0] : axis === 'y' ? [-Math.PI / 2, 0, 0] : [0, 0, 0]
  const size: [number, number] =
    axis === 'x' ? [b.size.z * pad, b.size.y * pad] : axis === 'y' ? [b.size.x * pad, b.size.z * pad] : [b.size.x * pad, b.size.y * pad]

  return (
    <mesh position={position} rotation={rotation} renderOrder={1}>
      <planeGeometry args={size} />
      <meshBasicMaterial color="#e5484d" transparent opacity={0.28} side={DoubleSide} depthWrite={false} />
    </mesh>
  )
}

function Refit({ token }: { token: number }) {
  const bounds = useBounds()
  useEffect(() => {
    if (token > 0) bounds.refresh().clip().fit()
  }, [token, bounds])
  return null
}
