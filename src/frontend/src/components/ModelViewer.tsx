import { Bounds, Grid, OrbitControls, useBounds, useGLTF, useProgress } from '@react-three/drei'
import { Canvas, useThree } from '@react-three/fiber'
import { Component, Suspense, useEffect, useMemo, useState, type ReactNode } from 'react'
import { Box3, Mesh, PMREMGenerator, Vector3, type Material } from 'three'
import { RoomEnvironment } from 'three/examples/jsm/environments/RoomEnvironment.js'
import { formatMm } from '../services/geometry'
import type { Dimensions } from '../types/api'

interface Props {
  url: string
  dimensionsMm: Dimensions | null
}

export function ModelViewer({ url, dimensionsMm }: Props) {
  const [wireframe, setWireframe] = useState(false)
  const [resetToken, setResetToken] = useState(0)
  const [fitToken, setFitToken] = useState(0)

  return (
    <div className="viewer">
      <div className="viewer__toolbar">
        <button type="button" className="btn btn--small" onClick={() => setResetToken((t) => t + 1)}>
          Reset camera
        </button>
        <button type="button" className="btn btn--small" onClick={() => setFitToken((t) => t + 1)}>
          Fit to model
        </button>
        <label className="toggle">
          <input type="checkbox" checked={wireframe} onChange={(e) => setWireframe(e.target.checked)} />
          Wireframe
        </label>
      </div>

      <ViewerErrorBoundary key={url}>
        <Canvas camera={{ position: [0, 0.08, 0.25], fov: 40, near: 0.0005, far: 50 }} dpr={[1, 2]}>
          <color attach="background" args={['#eef0f3']} />
          <StudioEnvironment />
          <hemisphereLight args={['#ffffff', '#8a8f99', 1.1]} />
          <directionalLight position={[0.5, 1, 0.8]} intensity={1.6} />
          <directionalLight position={[-0.6, 0.3, -0.7]} intensity={0.5} />
          {/* No drei <Html> fallback: it mounts a nested React root that breaks on Suspense unmount. */}
          <Suspense fallback={null}>
            <Bounds fit clip observe margin={1.4}>
              <CameraControls resetToken={resetToken} fitToken={fitToken} />
              <Model url={url} wireframe={wireframe} />
            </Bounds>
            {/* Outside <Bounds> so the grid does not count when fitting the camera. */}
            <Ground url={url} />
          </Suspense>
          <OrbitControls makeDefault enableDamping />
        </Canvas>
      </ViewerErrorBoundary>
      <LoadingOverlay />

      {dimensionsMm && (
        <div className="viewer__dims" title="Bounding box of the exported model">
          W {formatMm(dimensionsMm.x)} × H {formatMm(dimensionsMm.y)} × D {formatMm(dimensionsMm.z)} mm
        </div>
      )}
    </div>
  )
}

function Model({ url, wireframe }: { url: string; wireframe: boolean }) {
  const { scene } = useGLTF(url)

  useEffect(() => {
    scene.traverse((object) => {
      if (object instanceof Mesh) {
        const materials: Material[] = Array.isArray(object.material) ? object.material : [object.material]
        for (const material of materials) {
          if ('wireframe' in material) (material as Material & { wireframe: boolean }).wireframe = wireframe
        }
      }
    })
  }, [scene, wireframe])

  // The backend already centres the model and rests it on y = 0.
  return <primitive object={scene} />
}

function Ground({ url }: { url: string }) {
  const { scene } = useGLTF(url)
  const [ground, size] = useMemo(() => {
    const box = new Box3().setFromObject(scene)
    const s = box.getSize(new Vector3())
    return [box.min.y, Math.max(s.x, s.y, s.z)]
  }, [scene])

  return (
    <Grid
      position={[0, ground - size * 0.002, 0]}
      args={[size * 3, size * 3]}
      cellSize={size / 20}
      sectionSize={size / 4}
      cellColor="#c5cad3"
      sectionColor="#9aa1ad"
      fadeDistance={size * 6}
    />
  )
}

/**
 * Procedural studio reflections (no network). Needed for PBR materials: Stable Fast 3D often
 * estimates metallic surfaces, which render almost black with direct lights alone.
 */
export function StudioEnvironment() {
  const { gl, scene } = useThree()
  useEffect(() => {
    const pmrem = new PMREMGenerator(gl)
    const environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture
    scene.environment = environment
    return () => {
      scene.environment = null
      environment.dispose()
      pmrem.dispose()
    }
  }, [gl, scene])
  return null
}

function LoadingOverlay() {
  const { active, progress } = useProgress()
  if (!active) return null
  return <div className="viewer__loading">Loading model… {progress > 0 ? `${Math.round(progress)}%` : ''}</div>
}

/** Bridges toolbar buttons to drei's Bounds API. */
function CameraControls({ resetToken, fitToken }: { resetToken: number; fitToken: number }) {
  const bounds = useBounds()
  useEffect(() => {
    if (resetToken > 0) bounds.refresh().reset().fit()
  }, [resetToken, bounds])
  useEffect(() => {
    if (fitToken > 0) bounds.refresh().clip().fit()
  }, [fitToken, bounds])
  return null
}

export class ViewerErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  render() {
    if (this.state.failed)
      return (
        <div className="viewer__error" role="alert">
          The 3D model could not be displayed. The file may be missing or invalid; try downloading it or regenerating.
        </div>
      )
    return this.props.children
  }
}
