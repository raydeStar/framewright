import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from 'react'
import { Box, Check, Copy, LoaderCircle, MessageCirclePlus, Plus, Save, Trash2, X } from 'lucide-react'
import { studioApi } from '../api'
import type { AssetSummary, SceneAnnotationSummary, SceneCameraSummary, SceneInstanceSummary, SceneListItem, SceneProposalSummary, SceneSummary } from '../types'

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
export default function SceneWorkspace({ onToast, proposalSignal, onDirectorView }: {
  onToast: (message: string) => void
  /** Bumped when a browser agent stages a scene proposal. */
  proposalSignal?: number
  /** Publishes what is open so browser tools describe this exact selection. */
  onDirectorView?: (view: { sceneId: string; instanceId?: string } | undefined) => void
}) {
  const [list, setList] = useState<SceneListItem[]>([])
  const [scene, setScene] = useState<SceneSummary>()
  const [models, setModels] = useState<AssetSummary[]>([])
  const [selectedId, setSelectedId] = useState<string>()
  const [dirty, setDirty] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [loading, setLoading] = useState(true)
  const [annotations, setAnnotations] = useState<SceneAnnotationSummary[]>([])
  const [proposals, setProposals] = useState<SceneProposalSummary[]>([])
  const [noteMode, setNoteMode] = useState(false)
  const [pendingNote, setPendingNote] = useState<{ instanceId: string; anchor: number[] }>()
  const [noteText, setNoteText] = useState('')

  const loadDirection = useCallback(async (sceneId: string) => {
    try {
      const [notes, staged] = await Promise.all([studioApi.sceneAnnotations(sceneId), studioApi.sceneProposals(sceneId)])
      setAnnotations(notes); setProposals(staged)
    } catch { setAnnotations([]); setProposals([]) }
  }, [])

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
          if (live) { setScene(opened); setDirty(false); await loadDirection(opened.id) }
        }
      } catch (reason) {
        if (live) setError(reason instanceof Error ? reason.message : 'The scene list could not be opened.')
      } finally { if (live) setLoading(false) }
    })()
    return () => { live = false }
  }, [loadDirection])

  const open = async (sceneId: string) => {
    setError(undefined)
    try {
      const opened = await studioApi.scene(sceneId)
      setScene(opened); setSelectedId(undefined); setDirty(false)
      await loadDirection(sceneId)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'That scene could not be opened.') }
  }

  const create = async () => {
    setBusy(true); setError(undefined)
    try {
      const created = await studioApi.createScene(`Scene ${list.length + 1}`)
      setScene(created); setSelectedId(undefined); setDirty(false)
      // A new scene starts with no notes and no proposals; keeping the previous
      // scene's would show one scene's direction against another's objects.
      await loadDirection(created.id)
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

  // Tell the shell what is open, so an agent reading context sees this object.
  useEffect(() => {
    onDirectorView?.(scene ? { sceneId: scene.id, instanceId: selectedId } : undefined)
    return () => onDirectorView?.(undefined)
  }, [onDirectorView, scene, selectedId])

  useEffect(() => {
    if (!scene || !proposalSignal) return
    void loadDirection(scene.id)
  }, [proposalSignal, scene, loadDirection])

  const selected = useMemo(() => scene?.instances.find(item => item.id === selectedId), [scene, selectedId])
  const selectedNotes = useMemo(
    () => annotations.filter(note => note.instanceId === selectedId && note.state === 'Open'),
    [annotations, selectedId])

  // A note is placed on the object under the pointer and anchored in that
  // object's own space, so it keeps meaning the same spot when the object moves.
  const placeNote = (instanceId: string, anchor: [number, number, number]) => {
    setSelectedId(instanceId)
    setPendingNote({ instanceId, anchor })
    setNoteText('')
    setNoteMode(false)
  }

  const saveNote = async () => {
    if (!scene || !pendingNote || !noteText.trim()) return
    setBusy(true); setError(undefined)
    try {
      await studioApi.addSceneAnnotation(scene.id, {
        instanceId: pendingNote.instanceId, anchor: pendingNote.anchor, camera: scene.camera, body: noteText.trim(),
      })
      setPendingNote(undefined); setNoteText('')
      await loadDirection(scene.id)
      onToast('Note pinned to that object.')
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'That note could not be saved.') }
    finally { setBusy(false) }
  }

  const resolveNote = async (annotationId: string) => {
    if (!scene) return
    setBusy(true)
    try { await studioApi.resolveSceneAnnotation(annotationId); await loadDirection(scene.id) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'That note could not be resolved.') }
    finally { setBusy(false) }
  }

  // Applying a proposal moves its one instance into working state. The artist
  // still presses Save, which is the only thing that writes to the scene.
  const applyProposal = async (proposal: SceneProposalSummary) => {
    if (!scene) return
    setBusy(true); setError(undefined)
    try {
      await studioApi.acceptSceneProposal(proposal.id)
      const applied = await studioApi.applySceneProposal(proposal.id)
      edit(current => ({
        ...current,
        instances: current.instances.map(instance => instance.id === applied.instanceId ? {
          ...instance,
          position: applied.position ?? instance.position,
          rotation: applied.rotation ?? instance.rotation,
          scale: applied.scale ?? instance.scale,
        } : instance),
      }))
      setSelectedId(applied.instanceId)
      await loadDirection(scene.id)
      onToast(`${applied.instanceName} updated in the working scene. Save to keep it.`)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'That proposal could not be applied.') }
    finally { setBusy(false) }
  }

  const rejectProposal = async (proposal: SceneProposalSummary) => {
    if (!scene) return
    setBusy(true)
    try { await studioApi.rejectSceneProposal(proposal.id); await loadDirection(scene.id) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'That proposal could not be rejected.') }
    finally { setBusy(false) }
  }

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
                noteMode={noteMode}
                onSelect={setSelectedId}
                onPlaceNote={placeNote}
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

            <section data-testid="scene-notes">
              <h2>Notes <span>{selectedNotes.length}</span></h2>
              {!selected && <p className="model-note">Select an object to see or add its notes.</p>}
              {selected && <>
                <button type="button" className={noteMode ? 'primary compact' : ''} data-testid="scene-note-mode"
                  onClick={() => { setNoteMode(value => !value); setPendingNote(undefined) }}>
                  {noteMode ? <X size={15} /> : <MessageCirclePlus size={15} />}{noteMode ? 'Cancel note' : 'Add note'}
                </button>
                {noteMode && <p className="model-note">Click the exact spot on the object in the view.</p>}
                {pendingNote && <div className="scene-note-composer">
                  <label>What needs work here?<textarea aria-label="Scene note" maxLength={2000} value={noteText}
                    onChange={event => setNoteText(event.target.value)} /></label>
                  <div>
                    <button type="button" onClick={() => { setPendingNote(undefined); setNoteText('') }}>Cancel</button>
                    <button type="button" className="primary compact" disabled={busy || !noteText.trim()} onClick={() => void saveNote()}>Pin note</button>
                  </div>
                </div>}
                <ul className="scene-note-list">
                  {selectedNotes.map(note => <li key={note.id} data-stale={note.stale ? 'true' : 'false'}>
                    <p>{note.body}</p>
                    <small>
                      anchored at ({note.anchor.map(value => value.toFixed(2)).join(', ')})
                      {note.stale ? ' · the model revision changed under this note' : ''}
                    </small>
                    <button type="button" disabled={busy} onClick={() => void resolveNote(note.id)}><Check size={14} />Resolve</button>
                  </li>)}
                </ul>
                {selectedNotes.length === 0 && !pendingNote && <p className="model-note">No open notes on this object.</p>}
              </>}
            </section>

            <section data-testid="scene-proposals">
              <h2>Agent proposals <span>{proposals.filter(item => item.state === 'Pending').length}</span></h2>
              {proposals.length === 0 && <p className="model-note">No proposals yet. Your scene is untouched.</p>}
              <ul className="scene-proposal-list">
                {proposals.slice(0, 8).map(proposal => <li key={proposal.id} data-testid="scene-proposal">
                  <strong>{proposal.instanceName}</strong>
                  <p>{proposal.direction}</p>
                  <small>{proposal.state} · base v{proposal.baseSceneVersion}</small>
                  {proposal.state === 'Pending' && <div>
                    <button type="button" disabled={busy} onClick={() => void rejectProposal(proposal)}>Reject</button>
                    <button type="button" className="primary compact" disabled={busy} onClick={() => void applyProposal(proposal)}>Apply to this object</button>
                  </div>}
                </li>)}
              </ul>
            </section>

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
