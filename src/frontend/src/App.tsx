import { engineLabel, useEngineInfo } from './hooks/useEngineInfo'
import { WorkspacePage } from './pages/WorkspacePage'

export default function App() {
  const { engine, status } = useEngineInfo()

  return (
    <div className="app">
      <header className="app__header">
        <span className="app__logo" aria-hidden>
          ◆
        </span>
        <h1>Image to 3D</h1>
        <span className="muted small">
          {engineLabel(engine)}
          {status !== 'ok' && status !== 'unknown' ? ` (${status})` : ''}
        </span>
        {/* Required attribution under the Stability AI Community License. */}
        {(engine === 'StableFast3D' || engine === 'SPAR3D') && <span className="app__attribution small">Powered by Stability AI</span>}
      </header>
      <WorkspacePage engine={engine} />
    </div>
  )
}
