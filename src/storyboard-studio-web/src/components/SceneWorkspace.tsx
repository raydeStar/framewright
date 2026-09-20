import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from 'react'
import { Box, Copy, LoaderCircle, Plus, Save, Trash2 } from 'lucide-react'
import { studioApi } from '../api'
import type { AssetSummary, SceneCameraSummary, SceneInstanceSummary, SceneListItem, SceneSummary } from '../types'

// three.js loads only when a scene is actually opened.
const SceneViewport = lazy(() => import('./SceneViewport'))

const axes = ['X', 'Y', 'Z'] as const

/**
 * A small editable scene: distinct instances of exact model revisions, an
 * orbit camera, and basic lighting.
 *
 * Every edit happens in local working state and is written by one explicit
 * Save against the version the scene was read at. A save built on a stale view
 * is refused by the service and surfaced here, so newer work is never silently
 * overwritten.
 */
export default function SceneWorkspace({ onToast }: { onToast: (message: string) => void }) {
  const [list, setList] = useState<SceneListItem[]>([])
  const [scene, setScene] = useState<SceneSummary>()
  const [models, setModels] = useState<AssetSummary[]>([])
  const [selectedId, setSelectedId] = useState<string>()
  const [dirty, setDirty] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [loading, setLoading] = useState(true)

  const refreshList = useCallback(async () => {
    try { setList(await studioApi.scenes()) } catch { setList([]) }
  }, [])

  useEffect(() => {
    let live = true
    void (async () => {
      try {
        const [scenes, assets] = await Promise.all([studioApi.scenes(), studioApi.assets()])
        if (!live) return
        setList(scenes)
        setModels(assets.filter(asset => asset.kind === 'Model' && !asset.isArchived))
        if (scenes.length > 0) {
          const opened = await studioApi.scene(scenes[0].id)
          if (live) { setScene(opened); setDirty(false) }
        }
      } catch (reason) {
        if (live) setError(reason instanceof Error ? reason.message : 'The scene list could not be opened.')
      } finally { if (live) setLoading(false) }
    })()
    return () => { live = false }
  }, [])

  const open = async (sceneId: string) => {
    setError(undefined)
    try {
      const opened = await studioApi.scene(sceneId)
      setScene(opened); setSelectedId(undefined); setDirty(false)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'That scene could not be opened.') }
  }

  const create = async () => {
    setBusy(true); setError(undefined)
    try {
      const created = await studioApi.createScene(`Scene ${list.length + 1}`)
      setScene(created); setSelectedId(undefined); setDirty(false)
      await refreshList()
      onToast(`${created.name} created.`)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'That scene could not be created.') }
    finally { setBusy(false) }
  }

  const edit = (change: (current: SceneSummary) => SceneSummary) => {
    setScene(current => current ? change(current) : current)
    setDirty(true)
  }

  const editInstance = (instanceId: string, change: (instance: SceneInstanceSummary) => SceneInstanceSummary) =>
    edit(current => ({ ...current, instances: current.instances.map(item => item.id === instanceId ? change(item) : item) }))

  const addInstance = (model: AssetSummary) => {
    const instance: SceneInstanceSummary = {
      id: crypto.randomUUID(), assetId: model.id, name: model.displayName,
      position: [0, 0, 0], rotation: [0, 0, 0], scale: [1, 1, 1],
      assetName: model.displayName, revisionNumber: model.revisionNumber ?? 1,
      contentUrl: model.contentUrl, available: true, archived: model.isArchived, dimensions: [1, 1, 1],
    }
    edit(current => ({ ...current, instances: [...current.instances, instance] }))
    setSelectedId(instance.id)
  }

  // Duplicating keeps the same model revision and gives the copy its own
  // identity and transform, which is the whole point of instances.
  const duplicate = (instance: SceneInstanceSummary) => {
    const copy: SceneInstanceSummary = {
      ...instance, id: crypto.randomUUID(), name: `${instance.name} copy`,
      position: [instance.position[0] + 1, instance.position[1], instance.position[2]],
    }
    edit(current => ({ ...current, instances: [...current.instances, copy] }))
    setSelectedId(copy.id)
  }

  const remove = (instanceId: string) => {
    edit(current => ({ ...current, instances: current.instances.filter(item => item.id !== instanceId) }))
    setSelectedId(undefined)
  }

  const save = async () => {
    if (!scene) return
    setBusy(true); setError(undefined)
    try {
      const saved = await studioApi.saveScene(scene.id, {
        expectedVersion: scene.version, name: scene.name, camera: scene.camera, environment: scene.environment,
        instances: scene.instances.map(instance => ({
          id: instance.id, assetId: instance.assetId, name: instance.name,
          position: instance.position, rotation: instance.rotation, scale: instance.scale,
        })),
      })
      setScene(saved); setDirty(false)
      await refreshList()
      onToast(`${saved.name} saved as version ${saved.version}.`)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'That scene could not be saved.')
    } finally { setBusy(false) }
  }

  const selected = useMemo(() => scene?.instances.find(item => item.id === selectedId), [scene, selectedId])

  if (loading) return <main className="workspace workspace-loading" role="status"><LoaderCircle className="spin" /><p>Opening scenes</p></main>

  return <main className="workspace scene-workspace" data-testid="scene-workspace">
    <header className="scene-header">
      <div>
        <p className="eyebrow">Scene</p>
        {scene
          ? <input aria-label="Scene name" className="scene-name" value={scene.name} maxLength={120}
              onChange={event => edit(current => ({ ...current, name: event.target.value }))} />
          : <h1>No scene yet</h1>}
      </div>
      <div className="scene-header-actions">
        {list.length > 1 && <select aria-label="Open scene" value={scene?.id ?? ''} onChange={event => void open(event.target.value)}>
          {list.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
        </select>}
        <button className="secondary" disabled={busy} onClick={() => void create()}><Plus size={16} />New scene</button>
        {scene && <button className="primary" data-testid="scene-save" disabled={busy || !dirty} onClick={() => void save()}>
          <Save size={16} />{busy ? 'Saving…' : dirty ? 'Save scene' : `Saved · v${scene.version}`}
        </button>}
      </div>
    </header>

    {error && <p className="asset-error" role="alert" data-testid="scene-error">{error}</p>}

    {!scene
      ? <div className="scene-empty"><Box size={28} /><p>Create a scene, then place models from the library into it.</p></div>
      : <div className="scene-layout">
          <section className="scene-stage-shell">
            <Suspense fallback={<div className="asset-loading"><LoaderCircle className="spin" /><span>Opening the scene view…</span></div>}>
              <SceneViewport
                instances={scene.instances}
                camera={scene.camera}
                environment={scene.environment}
                selectedId={selectedId}
                onSelect={setSelectedId}
                onCameraChange={(camera: SceneCameraSummary) => edit(current => ({ ...current, camera }))}
              />
            </Suspense>
          </section>

          <aside className="scene-inspector" aria-label="Scene contents">
            <section data-testid="scene-objects">
              <h2>Objects <span>{scene.instances.length}</span></h2>
              <ul className="scene-object-list">
                {scene.instances.map(instance => <li key={instance.id}>
                  <button type="button" aria-pressed={instance.id === selectedId}
                    className={instance.id === selectedId ? 'active' : ''}
                    onClick={() => setSelectedId(instance.id)}>
                    <strong>{instance.name}</strong>
                    <small>{instance.assetName} · v{instance.revisionNumber}{instance.available ? '' : ' · unavailable'}</small>
                  </button>
                </li>)}
              </ul>
              {scene.instances.length === 0 && <p className="model-note">No objects yet. Add a model below.</p>}
              <div className="scene-add">
                <label>Add model<select aria-label="Add model to scene" value="" disabled={models.length === 0}
                  onChange={event => { const model = models.find(item => item.id === event.target.value); if (model) addInstance(model) }}>
                  <option value="">{models.length === 0 ? 'No models in the library yet' : 'Choose a model…'}</option>
                  {models.map(model => <option key={model.id} value={model.id}>{model.displayName}</option>)}
                </select></label>
              </div>
            </section>

            {selected && <section data-testid="scene-placement">
              <h2>Placement</h2>
              <label>Name<input aria-label="Object name" value={selected.name} maxLength={120}
                onChange={event => editInstance(selected.id, item => ({ ...item, name: event.target.value }))} /></label>
              <p className="model-note">{selected.assetName} · revision {selected.revisionNumber}{selected.available ? '' : ' · model unavailable, shown as a placeholder'}</p>

              {(['position', 'rotation', 'scale'] as const).map(field => <div className="scene-vector" key={field}>
                <span>{field === 'rotation' ? 'Rotation (radians)' : field === 'scale' ? 'Scale' : 'Position (metres)'}</span>
                <div>
                  {axes.map((axis, index) => <label key={axis}>{axis}
                    <input
                      type="number"
                      step={field === 'rotation' ? 0.05 : 0.1}
                      aria-label={`${field} ${axis}`}
                      value={selected[field][index]}
                      onChange={event => {
                        const value = Number(event.target.value)
                        if (!Number.isFinite(value)) return
                        editInstance(selected.id, item => ({
                          ...item,
                          [field]: item[field].map((existing, position) => position === index ? value : existing),
                        }))
                      }} />
                  </label>)}
                </div>
              </div>)}

              <div className="scene-placement-actions">
                <button type="button" onClick={() => duplicate(selected)}><Copy size={15} />Duplicate</button>
                <button type="button" className="danger-text" onClick={() => remove(selected.id)}><Trash2 size={15} />Remove from scene</button>
              </div>
              <p className="model-note">Removing an object takes it out of this scene only. The model stays in the library.</p>
            </section>}

            <section data-testid="scene-environment">
              <h2>Lighting</h2>
              <label>Key light <small>{scene.environment.keyIntensity.toFixed(1)}</small>
                <input type="range" min={0} max={8} step={0.1} aria-label="Key light intensity" value={scene.environment.keyIntensity}
                  onChange={event => edit(current => ({ ...current, environment: { ...current.environment, keyIntensity: Number(event.target.value) } }))} /></label>
              <label>Ambient <small>{scene.environment.ambientIntensity.toFixed(1)}</small>
                <input type="range" min={0} max={8} step={0.1} aria-label="Ambient light intensity" value={scene.environment.ambientIntensity}
                  onChange={event => edit(current => ({ ...current, environment: { ...current.environment, ambientIntensity: Number(event.target.value) } }))} /></label>
              <p className="model-note" data-testid="scene-version">Version {scene.version}{dirty ? ' · unsaved changes' : ' · saved'}</p>
            </section>
          </aside>
        </div>}
  </main>
}
