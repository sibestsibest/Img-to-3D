import { useHashRoute } from './hooks/useHashRoute'
import { engineLabel, useEngineInfo } from './hooks/useEngineInfo'
import { PartDesignListPage } from './pages/PartDesignListPage'
import { PartDesignPage } from './pages/PartDesignPage'
import { WorkspacePage } from './pages/WorkspacePage'

export default function App() {
  const { engine, status } = useEngineInfo()
  const { route, navigate } = useHashRoute()

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
        <nav className="app__nav">
          <a href="#/" aria-current={route.view === 'workspace' ? 'page' : undefined}>
            Generate
          </a>
          <a href="#/designs" aria-current={route.view !== 'workspace' ? 'page' : undefined}>
            Part design
          </a>
        </nav>
        {/* Required attribution under the Stability AI Community License. */}
        {(engine === 'StableFast3D' || engine === 'SPAR3D') && <span className="app__attribution small">Powered by Stability AI</span>}
      </header>
      {route.view === 'workspace' && <WorkspacePage engine={engine} onOpenDesign={(id) => navigate({ view: 'design', id })} />}
      {route.view === 'designs' && <PartDesignListPage onOpen={(id) => navigate({ view: 'design', id })} onBack={() => navigate({ view: 'workspace' })} />}
      {route.view === 'design' && <PartDesignPage key={route.id} id={route.id} onBack={() => navigate({ view: 'designs' })} />}
    </div>
  )
}
