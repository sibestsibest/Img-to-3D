import { useHashRoute } from './hooks/useHashRoute'
import { engineLabel, useEngineInfo } from './hooks/useEngineInfo'
import { PartDesignListPage } from './pages/PartDesignListPage'
import { ParametricPage } from './pages/ParametricPage'
import { PhotogrammetryListPage } from './pages/PhotogrammetryListPage'
import { PhotogrammetryPage } from './pages/PhotogrammetryPage'
import { PartDesignPage } from './pages/PartDesignPage'
import { ReferenceListPage } from './pages/ReferenceListPage'
import { ReferenceSessionPage } from './pages/ReferenceSessionPage'
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
          <a href="#/" aria-current={route.view === 'workspace' || route.view === 'job' ? 'page' : undefined}>
            Generate
          </a>
          <a href="#/references" aria-current={route.view === 'references' || route.view === 'reference' ? 'page' : undefined}>
            References
          </a>
          <a href="#/photogrammetry" aria-current={route.view === 'photogrammetry' || route.view === 'photoset' ? 'page' : undefined}>
            Photogrammetry
          </a>
          <a href="#/parametric" aria-current={route.view === 'parametric' ? 'page' : undefined}>
            Parametric
          </a>
          <a href="#/designs" aria-current={route.view === 'designs' || route.view === 'design' ? 'page' : undefined}>
            Part design
          </a>
        </nav>
        {/* Required attribution under the Stability AI Community License. */}
        {(engine === 'StableFast3D' || engine === 'SPAR3D') && <span className="app__attribution small">Powered by Stability AI</span>}
      </header>
      {(route.view === 'workspace' || route.view === 'job') && (
        <WorkspacePage
          key={route.view === 'job' ? route.id : 'workspace'}
          engine={engine}
          initialJobId={route.view === 'job' ? route.id : null}
          onOpenDesign={(id) => navigate({ view: 'design', id })}
          onOpenReferences={(id) => navigate({ view: 'reference', id })}
        />
      )}
      {route.view === 'designs' && <PartDesignListPage onOpen={(id) => navigate({ view: 'design', id })} onBack={() => navigate({ view: 'workspace' })} />}
      {route.view === 'design' && <PartDesignPage key={route.id} id={route.id} onBack={() => navigate({ view: 'designs' })} />}
      {route.view === 'photogrammetry' && <PhotogrammetryListPage onOpen={(id) => navigate({ view: 'photoset', id })} />}
      {route.view === 'photoset' && (
        <PhotogrammetryPage key={route.id} id={route.id} onBack={() => navigate({ view: 'photogrammetry' })} onOpenDesign={(id) => navigate({ view: 'design', id })} />
      )}
      {route.view === 'parametric' && (
        <ParametricPage key={route.from ?? 'new'} from={route.from} onCreated={(id) => navigate({ view: 'design', id })} onBack={() => navigate({ view: 'designs' })} />
      )}
      {route.view === 'references' && <ReferenceListPage onOpen={(id) => navigate({ view: 'reference', id })} onBack={() => navigate({ view: 'workspace' })} />}
      {route.view === 'reference' && (
        <ReferenceSessionPage
          key={route.id}
          id={route.id}
          onBack={() => navigate({ view: 'references' })}
          onOpenDesign={(id) => navigate({ view: 'design', id })}
          onOpenJob={(id) => navigate({ view: 'job', id })}
        />
      )}
    </div>
  )
}
