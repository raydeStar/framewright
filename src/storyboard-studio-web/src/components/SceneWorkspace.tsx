import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Box, Check, Copy, Image, LoaderCircle, Maximize2, MessageCirclePlus, Minimize2, Pause, Play, Plus, Save, Trash2, X } from 'lucide-react'
import { studioApi } from '../api'
import type { AssetSummary, DirectorSceneView, ModelClipSummary, SceneAnnotationSummary, SceneBlockoutPlanSummary, SceneCameraSummary, SceneInstanceSummary, SceneListItem, SceneProposalSummary, SceneShotBindingSummary, SceneSummary, StudioSnapshot } from '../types'

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
export default function SceneWorkspace({ studio, onToast, proposalSignal, blockoutSignal, directorMode, onDirectorMode, onDirectorView, onShotRendered }: {
  studio: StudioSnapshot
  onToast: (message: string) => void
  /** Bumped when a browser agent stages a scene proposal. */
  proposalSignal?: number
  /** Bumped when a browser agent stages a blockout plan. */
  blockoutSignal?: number
  directorMode: boolean
  onDirectorMode: (active: boolean) => void
  /** Publishes what is open so browser tools describe this exact selection. */
  onDirectorView?: (view: DirectorSceneView | undefined) => void
  onShotRendered?: (shotId: string) => void
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
  const [references, setReferences] = useState<AssetSummary[]>([])
  const [referenceId, setReferenceId] = useState<string>()
  const [plans, setPlans] = useState<SceneBlockoutPlanSummary[]>([])
  /** The plan this scene was built from, when it was built from one. */
  const [builtFrom, setBuiltFrom] = useState<SceneBlockoutPlanSummary>()
  /** Playback is one clock the whole scene reads; every object reads it through its own settings. */
  const [playhead, setPlayhead] = useState(0)
  const [playing, setPlaying] = useState(false)
  const [clipSourceId, setClipSourceId] = useState<string>()
  const [sourceClips, setSourceClips] = useState<ModelClipSummary[]>([])
  const [replacementId, setReplacementId] = useState<string>()
  const [shotId, setShotId] = useState(studio.shots[0]?.id ?? '')
  const [shotCamera, setShotCamera] = useState<SceneCameraSummary>()
  const [shotStart, setShotStart] = useState(0)
  const [shotStill, setShotStill] = useState(0)
  const [shotBindings, setShotBindings] = useState<SceneShotBindingSummary[]>([])
  const [captureStill, setCaptureStill] = useState<((request: { camera: SceneCameraSummary; time: number; width: number; height: number }) => Promise<Blob>)>()
  const shotSceneId = useRef<string | undefined>(undefined)
  // Opening a scene is asynchronous, and the artist can create or open another
  // one while the first is still arriving. Without a ticket the slower answer
  // lands second and puts them back in a scene they have already left.
  const openSequence = useRef(0)
  const editSequence = useRef(0)

  const loadDirection = useCallback(async (sceneId: string) => {
    try {
      const [notes, staged] = await Promise.all([studioApi.sceneAnnotations(sceneId), studioApi.sceneProposals(sceneId)])
      setAnnotations(notes); setProposals(staged)
    } catch { setAnnotations([]); setProposals([]) }
  }, [])

  // Plans are read per reference, because a plan only means anything against
  // the picture it was read from.
  const loadPlans = useCallback(async (assetId: string | undefined) => {
    if (!assetId) { setPlans([]); return }
    try { setPlans(await studioApi.sceneBlockouts(assetId)) } catch { setPlans([]) }
  }, [])

  // Only a scene whose objects came from a plan has one to read, so an ordinary
  // scene never asks the service for something that is not there.
  const loadProvenance = useCallback(async (opened: SceneSummary) => {
    if (!opened.instances.some(instance => instance.planId)) { setBuiltFrom(undefined); return }
    try { setBuiltFrom(await studioApi.sceneBlockoutForScene(opened.id)) } catch { setBuiltFrom(undefined) }
  }, [])

  // A clip source is an ordinary library model; its clips are read from its own
  // file rather than declared here.
  useEffect(() => {
    if (!clipSourceId) { setSourceClips([]); return }
    let live = true
    studioApi.modelProfile(clipSourceId)
      .then(profile => { if (live) setSourceClips(profile.clips ?? []) })
      .catch(() => { if (live) setSourceClips([]) })
    return () => { live = false }
  }, [clipSourceId])

  const refreshList = useCallback(async () => {
    try { setList(await studioApi.scenes()) } catch { setList([]) }
  }, [])

  useEffect(() => {
    let live = true
    // The ticket is taken before anything is awaited, so a scene the artist
    // creates or opens while this is still loading always wins.
    const ticket = ++openSequence.current
    void (async () => {
      try {
        const [scenes, assets] = await Promise.all([studioApi.scenes(), studioApi.assets()])
        if (!live) return
        setList(scenes)
        setModels(assets.filter(asset => asset.kind === 'Model' && !asset.isArchived))
        setReferences(assets.filter(asset => asset.kind === 'Image' && !asset.isArchived))
        if (scenes.length > 0 && ticket === openSequence.current) {
          const opened = await studioApi.scene(scenes[0].id)
          if (live && ticket === openSequence.current) {
            setScene(opened); setDirty(false); await loadDirection(opened.id); await loadProvenance(opened)
          }
        }
      } catch (reason) {
        if (live) setError(reason instanceof Error ? reason.message : 'The scene list could not be opened.')
      } finally { if (live) setLoading(false) }
    })()
    return () => { live = false }
  }, [loadDirection, loadProvenance])

  const open = async (sceneId: string) => {
    setError(undefined)
    const ticket = ++openSequence.current
    try {
      const opened = await studioApi.scene(sceneId)
      if (ticket !== openSequence.current) return
      setScene(opened); setSelectedId(undefined); setDirty(false)
      await loadDirection(sceneId)
      await loadProvenance(opened)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'That scene could not be opened.') }
  }

  const create = async () => {
    setBusy(true); setError(undefined)
    const ticket = ++openSequence.current
    try {
      const created = await studioApi.createScene(`Scene ${list.length + 1}`)
      if (ticket !== openSequence.current) return
      setScene(created); setSelectedId(undefined); setDirty(false)
      // A new scene starts with no notes, proposals, or provenance; keeping the
      // previous scene's would show one scene's direction against another's
      // objects.
      await loadDirection(created.id)
      setBuiltFrom(undefined)
      await refreshList()
      onToast(`${created.name} created.`)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'That scene could not be created.') }
    finally { setBusy(false) }
  }

  const edit = (change: (current: SceneSummary) => SceneSummary) => {
    editSequence.current += 1
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
    const savedEdit = editSequence.current
    setBusy(true); setError(undefined)
    try {
      const saved = await studioApi.saveScene(scene.id, {
        expectedVersion: scene.version, name: scene.name, camera: scene.camera, environment: scene.environment,
        instances: scene.instances.map(instance => ({
          id: instance.id, assetId: instance.assetId, name: instance.name,
          position: instance.position, rotation: instance.rotation, scale: instance.scale,
          // A stand-in is saved as itself; dropping this would leave an object
          // with a transform and nothing under it.
          placeholder: instance.placeholder ?? null,
          clip: instance.clip ?? null,
          motion: instance.motion ?? null,
        })),
      })
      const editedWhileSaving = editSequence.current !== savedEdit
      setScene(current => editedWhileSaving && current?.id === saved.id ? { ...current, version: saved.version } : saved)
      setDirty(editedWhileSaving)
      await refreshList()
      onToast(`${saved.name} saved as version ${saved.version}.`)
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'That scene could not be saved.')
    } finally { setBusy(false) }
  }

  // Tell the shell what is open, so an agent reading context sees this object.
  // Dirty state is part of the contract: the service cannot validate a draft
  // camera or transform until the artist saves it, so browser tools refuse to
  // describe the older persisted scene in its place.
  useEffect(() => {
    onDirectorView?.(scene ? { kind: 'scene', sceneId: scene.id, instanceId: selectedId, referenceAssetId: referenceId, directorMode, dirty, time: playhead } : undefined)
    return () => onDirectorView?.(undefined)
  }, [onDirectorView, scene, selectedId, referenceId, directorMode, dirty, playhead])

  useEffect(() => { void loadPlans(referenceId) }, [referenceId, loadPlans])

  useEffect(() => {
    if (studio.shots.some(shot => shot.id === shotId)) return
    setShotId(studio.shots[0]?.id ?? '')
  }, [shotId, studio.shots])

  useEffect(() => {
    if (!scene) {
      shotSceneId.current = undefined
      setShotBindings([])
      setShotCamera(undefined)
      return
    }
    if (shotSceneId.current === scene.id) return
    shotSceneId.current = scene.id
    setShotCamera({ ...scene.camera, target: [...scene.camera.target] })
    let live = true
    void studioApi.sceneShotStills(scene.id)
      .then(bindings => { if (live) setShotBindings(bindings) })
      .catch(() => { if (live) setShotBindings([]) })
    return () => { live = false }
  }, [scene])

  // How long this scene's motion runs: the longest any one object plays for at
  // its own speed, so the transport covers everything in it.
  const runtime = useMemo(() => Math.max(2, ...(scene?.instances ?? []).map(instance => {
    if (instance.motion) return instance.motion.seconds
    if (!instance.clip) return 0
    const span = instance.clip.end > instance.clip.start ? instance.clip.end - instance.clip.start : 0
    return span / (instance.clip.speed > 0 ? instance.clip.speed : 1)
  })), [scene])

  // One clock, advanced only while the artist is playing. Scrubbing sets it
  // directly, which is the same thing at a different time.
  useEffect(() => {
    if (!playing) return
    let frame = 0
    let last = performance.now()
    const step = (now: number) => {
      const elapsed = (now - last) / 1000
      last = now
      setPlayhead(current => {
        const next = current + elapsed
        return next > runtime ? next % runtime : next
      })
      frame = requestAnimationFrame(step)
    }
    frame = requestAnimationFrame(step)
    return () => cancelAnimationFrame(frame)
  }, [playing, runtime])

  useEffect(() => {
    if (!blockoutSignal) return
    void loadPlans(referenceId)
  }, [blockoutSignal, referenceId, loadPlans])

  useEffect(() => {
    if (!scene || !proposalSignal) return
    void loadDirection(scene.id)
  }, [proposalSignal, scene, loadDirection])

  const selected = useMemo(() => scene?.instances.find(item => item.id === selectedId), [scene, selectedId])
  const reference = useMemo(() => references.find(asset => asset.id === referenceId), [references, referenceId])
  const shot = useMemo(() => studio.shots.find(candidate => candidate.id === shotId), [shotId, studio.shots])
  const shotEnd = shot ? shotStart + shot.durationFrames / studio.project.framesPerSecond : shotStart
  const selectedNotes = useMemo(
    () => annotations.filter(note => note.instanceId === selectedId && note.state === 'Open'),
    [annotations, selectedId])

  useEffect(() => { setReplacementId(undefined) }, [selectedId])

  useEffect(() => {
    setShotStill(current => Math.min(Math.max(current, shotStart), shotEnd))
  }, [shotStart, shotEnd])

  const renderStill = async () => {
    if (!scene || !shot || !shotCamera || !captureStill) return
    if (dirty) { setError('Save the scene before rendering, so the still can cite an exact scene version.'); return }
    setBusy(true); setError(undefined); setPlaying(false)
    try {
      const file = await captureStill({
        camera: shotCamera,
        time: shotStill,
        width: studio.project.deliveryWidth,
        height: studio.project.deliveryHeight,
      })
      const binding = await studioApi.renderSceneStill(scene.id, {
        shotId: shot.id,
        expectedSceneVersion: scene.version,
        expectedShotVersion: shot.version,
        camera: shotCamera,
        startTime: shotStart,
        endTime: shotEnd,
        stillTime: shotStill,
        file,
      })
      setShotBindings(current => [binding, ...current.filter(item => item.id !== binding.id)])
      onToast(`${binding.shotCode} v${binding.shotVersion} is ready in Review from ${scene.name} v${scene.version}.`)
      onShotRendered?.(binding.shotId)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'The scene still could not be rendered.') }
    finally { setBusy(false) }
  }

  // Replacing a stand-in changes only what its existing scene object draws.
  // The instance identity, transform, plan provenance, annotations, and rigid
  // pivot remain on the object; the ordinary versioned Save is still the only
  // write. GLB coordinates are already +Y-up metres, so there is no hidden unit
  // conversion here and the artist can adjust the explicit scale if needed.
  const replacePlaceholder = () => {
    if (!selected?.placeholder || !replacementId) return
    const model = models.find(candidate => candidate.id === replacementId)
    if (!model) return
    editInstance(selected.id, instance => ({
      ...instance,
      assetId: model.id,
      assetName: model.displayName,
      revisionNumber: model.revisionNumber ?? 1,
      contentUrl: model.contentUrl,
      available: true,
      archived: model.isArchived,
      dimensions: [0, 0, 0],
      placeholder: null,
    }))
    setReplacementId(undefined)
    onToast(`${selected.name} now uses ${model.displayName} in the working scene. Save to keep it.`)
  }

  const bindClip = (clipName: string) => {
    if (!selected || !clipSourceId) return
    const clip = sourceClips.find(candidate => candidate.name === clipName)
    if (!clip) return
    editInstance(selected.id, instance => ({
      ...instance,
      motion: null,
      clip: {
        clipAssetId: clipSourceId, clipName: clip.name, clipAssetName: null,
        start: 0, end: clip.duration, speed: 1, time: 0, loop: false, rootMotion: 'Hold',
      },
    }))
  }

  const editClip = (change: Partial<NonNullable<SceneInstanceSummary['clip']>>) => {
    if (!selected?.clip) return
    editInstance(selected.id, instance => {
      const clip = { ...instance.clip!, ...change }
      // Trimming a clip cannot leave the playback position outside it, which
      // would be a binding the service is right to refuse.
      return { ...instance, clip: { ...clip, time: Math.min(Math.max(clip.time, clip.start), clip.end) } }
    })
  }

  const bindMotion = () => {
    if (!selected) return
    editInstance(selected.id, instance => ({
      ...instance,
      clip: null,
      motion: { pivot: [0, 0, 0], axis: 'Y', fromRadians: 0, toRadians: 1.5708, seconds: 2, pingPong: false },
    }))
  }

  const editMotion = (change: Partial<NonNullable<SceneInstanceSummary['motion']>>) => {
    if (!selected?.motion) return
    editInstance(selected.id, instance => ({ ...instance, motion: { ...instance.motion!, ...change } }))
  }

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

  // Approving a plan is the artist's move and the only thing that builds. It
  // creates a new scene out of placeholders and library models, so nothing that
  // already exists can be overwritten, and no generation is started.
  const buildBlockout = async (plan: SceneBlockoutPlanSummary) => {
    setBusy(true); setError(undefined)
    try {
      const ticket = ++openSequence.current
      const built = await studioApi.applySceneBlockout(plan.id, plan.title)
      if (ticket !== openSequence.current) return
      setScene(built); setSelectedId(undefined); setDirty(false)
      await loadDirection(built.id)
      await loadProvenance(built)
      await loadPlans(referenceId)
      await refreshList()
      onToast(`${built.name} built from the plan. Nothing was generated.`)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'That plan could not be built.') }
    finally { setBusy(false) }
  }

  const rejectBlockout = async (plan: SceneBlockoutPlanSummary) => {
    setBusy(true)
    try { await studioApi.rejectSceneBlockout(plan.id); await loadPlans(referenceId) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'That plan could not be rejected.') }
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

  return <main className={`workspace scene-workspace${directorMode ? ' director-mode' : ''}`} data-testid="scene-workspace">
    <header className="scene-header">
      <div>
        <p className="eyebrow">Scene</p>
        {scene
          ? <input aria-label="Scene name" className="scene-name" value={scene.name} maxLength={120}
              onChange={event => edit(current => ({ ...current, name: event.target.value }))} />
          : <h1>No scene yet</h1>}
      </div>
      <div className="scene-header-actions">
        {scene && <button type="button" className={`secondary director-mode-toggle${directorMode ? ' active' : ''}`}
          aria-pressed={directorMode} onClick={() => onDirectorMode(!directorMode)}>
          {directorMode ? <Minimize2 size={16} /> : <Maximize2 size={16} />}{directorMode ? 'Exit Director' : 'Director Mode'}
        </button>}
        {list.length > 1 && <select aria-label="Open scene" value={scene?.id ?? ''} disabled={busy} onChange={event => void open(event.target.value)}>
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
                playhead={playhead}
                playing={playing}
                onSelect={setSelectedId}
                onPlaceNote={placeNote}
                onCaptureReady={capture => setCaptureStill(() => capture)}
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
                    <small>{instance.placeholder
                      ? `${instance.placeholder.shape} stand-in`
                      : `${instance.assetName} · v${instance.revisionNumber}${instance.available ? '' : ' · unavailable'}`}
                      {instance.clip ? ` · ${instance.clip.clipName}` : instance.motion ? ' · turns on a pivot' : ''}</small>
                  </button>
                </li>)}
              </ul>
              {scene.instances.length === 0 && <p className="model-note">No objects yet. Add a model below.</p>}
              <div className="scene-add">
                {/* Editing the scene on screen while another is being opened or
                    created would be editing one that is about to be replaced. */}
                <label>Add model<select aria-label="Add model to scene" value="" disabled={busy || models.length === 0}
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
              <p className="model-note">{selected.placeholder
                ? `${selected.placeholder.shape} stand-in · ${selected.placeholder.size.map(value => value.toFixed(2)).join(' × ')} m${selected.role ? ` · planned as ${selected.role}` : ''}`
                : `${selected.assetName} · revision ${selected.revisionNumber}${selected.available ? '' : ' · model unavailable, shown as a placeholder'}`}</p>

              {selected.placeholder && <div className="scene-replacement" data-testid="scene-replacement">
                <label>Replace stand-in with<select aria-label="Replacement model" value={replacementId ?? ''}
                  disabled={busy || models.length === 0}
                  onChange={event => setReplacementId(event.target.value || undefined)}>
                  <option value="">{models.length === 0 ? 'No models in the library yet' : 'Choose a library model…'}</option>
                  {models.map(model => <option key={model.id} value={model.id}>{model.displayName} · v{model.revisionNumber ?? 1}</option>)}
                </select></label>
                <button type="button" className="primary compact" disabled={busy || !replacementId} onClick={replacePlaceholder}>
                  Replace this object
                </button>
                <p className="model-note">Keeps this object's identity, placement, scale, pivot motion, notes, and plan lineage. Models use +Y-up metres; adjust the visible scale only if the source needs it.</p>
              </div>}

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

            <section data-testid="scene-motion">
              <h2>Motion</h2>
              <div className="scene-transport">
                <button type="button" data-testid="scene-play" onClick={() => setPlaying(value => !value)}>
                  {playing ? <Pause size={15} /> : <Play size={15} />}{playing ? 'Pause' : 'Play'}
                </button>
                <label>Time <small data-testid="scene-playhead">{playhead.toFixed(2)} s</small>
                  <input type="range" min={0} max={runtime} step={0.05} aria-label="Playback time" value={Math.min(playhead, runtime)}
                    onChange={event => { setPlaying(false); setPlayhead(Number(event.target.value)) }} /></label>
              </div>
              <p className="model-note">Playback draws only. Nothing here changes an object until you save it.</p>

              {!selected && <p className="model-note">Select an object to give it a clip or a pivot.</p>}
              {selected && !selected.placeholder && <>
                <label>Clip source<select aria-label="Clip source" value={clipSourceId ?? ''}
                  onChange={event => setClipSourceId(event.target.value || undefined)}>
                  <option value="">Choose a model that carries clips…</option>
                  {models.map(model => <option key={model.id} value={model.id}>{model.displayName}</option>)}
                </select></label>
                {clipSourceId && <label>Clip<select aria-label="Clip" value={selected.clip?.clipName ?? ''}
                  onChange={event => bindClip(event.target.value)}>
                  <option value="">{sourceClips.length === 0 ? 'This model carries no clips' : 'Choose a clip…'}</option>
                  {sourceClips.filter(clip => clip.supported).map(clip => <option key={clip.name} value={clip.name}>
                    {clip.name} · {clip.duration.toFixed(2)} s
                  </option>)}
                </select></label>}
                {sourceClips.some(clip => !clip.supported) && <p className="model-note">
                  {sourceClips.filter(clip => !clip.supported).length} clip(s) in that file are not supported and are not offered.
                </p>}

                {selected.clip && <div className="scene-clip" data-testid="scene-clip">
                  <p className="model-note">{selected.clip.clipName}{selected.clip.clipAssetName ? ` · from ${selected.clip.clipAssetName}` : ''}</p>
                  <div className="scene-vector">
                    <span>Trim (seconds)</span>
                    <div>
                      <label>From<input type="number" step={0.1} min={0} aria-label="Clip start" value={selected.clip.start}
                        onChange={event => editClip({ start: Number(event.target.value) })} /></label>
                      <label>To<input type="number" step={0.1} min={0} aria-label="Clip end" value={selected.clip.end}
                        onChange={event => editClip({ end: Number(event.target.value) })} /></label>
                      <label>Speed<input type="number" step={0.1} min={0.1} max={4} aria-label="Clip speed" value={selected.clip.speed}
                        onChange={event => editClip({ speed: Number(event.target.value) })} /></label>
                    </div>
                  </div>
                  <label className="scene-check"><input type="checkbox" checked={selected.clip.loop}
                    onChange={event => editClip({ loop: event.target.checked })} />Loop</label>
                  <label>Root motion<select aria-label="Root motion" value={selected.clip.rootMotion}
                    onChange={event => editClip({ rootMotion: event.target.value as 'Hold' | 'Offset' })}>
                    <option value="Hold">Hold · the character stays where you put it</option>
                    <option value="Offset">Offset · the clip moves the object once</option>
                  </select></label>
                  <button type="button" onClick={() => editInstance(selected.id, instance => ({ ...instance, clip: null }))}>Clear clip</button>
                </div>}
              </>}

              {selected && !selected.clip && <div className="scene-rigid">
                {!selected.motion
                  ? <button type="button" data-testid="scene-add-motion" onClick={bindMotion}>Turn about a pivot</button>
                  : <div data-testid="scene-motion-track">
                      <div className="scene-vector">
                        <span>Pivot (metres, in this object's own space)</span>
                        <div>
                          {axes.map((axis, index) => <label key={axis}>{axis}
                            <input type="number" step={0.1} aria-label={`pivot ${axis}`} value={selected.motion!.pivot[index]}
                              onChange={event => editMotion({ pivot: selected.motion!.pivot.map((value, position) => position === index ? Number(event.target.value) : value) })} />
                          </label>)}
                        </div>
                      </div>
                      <div className="scene-vector">
                        <span>Swing</span>
                        <div>
                          <label>Axis<select aria-label="Motion axis" value={selected.motion!.axis}
                            onChange={event => editMotion({ axis: event.target.value as 'X' | 'Y' | 'Z' })}>
                            {axes.map(axis => <option key={axis} value={axis}>{axis}</option>)}
                          </select></label>
                          <label>From<input type="number" step={0.05} aria-label="Motion from" value={selected.motion!.fromRadians}
                            onChange={event => editMotion({ fromRadians: Number(event.target.value) })} /></label>
                          <label>To<input type="number" step={0.05} aria-label="Motion to" value={selected.motion!.toRadians}
                            onChange={event => editMotion({ toRadians: Number(event.target.value) })} /></label>
                          <label>Seconds<input type="number" step={0.1} min={0.1} aria-label="Motion seconds" value={selected.motion!.seconds}
                            onChange={event => editMotion({ seconds: Number(event.target.value) })} /></label>
                        </div>
                      </div>
                      <label className="scene-check"><input type="checkbox" checked={selected.motion!.pingPong}
                        onChange={event => editMotion({ pingPong: event.target.checked })} />Swing back again</label>
                      <button type="button" onClick={() => editInstance(selected.id, instance => ({ ...instance, motion: null }))}>Clear motion</button>
                    </div>}
                <p className="model-note">A rigid part needs no skeleton. The pivot is the one point the swing leaves where it is.</p>
              </div>}
            </section>

            <section data-testid="scene-shot">
              <h2>Shot setup</h2>
              {studio.shots.length === 0
                ? <p className="model-note">Create a shot slot before rendering a scene frame for review.</p>
                : <>
                    <label>Send still to<select aria-label="Scene shot" value={shotId} disabled={busy}
                      onChange={event => setShotId(event.target.value)}>
                      {studio.shots.map(candidate => <option key={candidate.id} value={candidate.id}>{candidate.code} · {candidate.title}</option>)}
                    </select></label>
                    <div className="scene-vector">
                      <span>Scene time (seconds)</span>
                      <div>
                        <label>From<input type="number" min={0} step={0.05} aria-label="Shot start time" value={shotStart}
                          onChange={event => setShotStart(Math.max(0, Number(event.target.value) || 0))} /></label>
                        <label>To<input type="number" aria-label="Shot end time" value={Number(shotEnd.toFixed(3))} disabled /></label>
                        <label>Still<input type="number" min={shotStart} max={shotEnd} step={0.05} aria-label="Shot still time" value={shotStill}
                          onChange={event => setShotStill(Number(event.target.value) || 0)} /></label>
                      </div>
                    </div>
                    <p className="model-note">The range is fixed to {shot?.durationFrames ?? 0} frames at {studio.project.framesPerSecond} fps.</p>
                    {shotCamera && <div className="scene-shot-camera">
                      <div className="scene-section-heading"><strong>Shot camera</strong><button type="button" disabled={busy}
                        onClick={() => setShotCamera({ ...scene.camera, target: [...scene.camera.target] })}>Use inspection view</button></div>
                      <div className="scene-vector"><span>Orbit</span><div>
                        <label>Yaw<input type="number" step={0.05} aria-label="Shot camera yaw" value={shotCamera.yaw}
                          onChange={event => setShotCamera(current => current && ({ ...current, yaw: Number(event.target.value) }))} /></label>
                        <label>Pitch<input type="number" step={0.05} aria-label="Shot camera pitch" value={shotCamera.pitch}
                          onChange={event => setShotCamera(current => current && ({ ...current, pitch: Number(event.target.value) }))} /></label>
                        <label>Distance<input type="number" min={0.01} step={0.1} aria-label="Shot camera distance" value={shotCamera.distance}
                          onChange={event => setShotCamera(current => current && ({ ...current, distance: Number(event.target.value) }))} /></label>
                      </div></div>
                      <div className="scene-vector"><span>Frame</span><div>
                        {axes.map((axis, index) => <label key={axis}>{axis}<input type="number" step={0.1} aria-label={`Shot camera target ${axis}`} value={shotCamera.target[index]}
                          onChange={event => setShotCamera(current => current && ({ ...current, target: current.target.map((value, position) => position === index ? Number(event.target.value) : value) }))} /></label>)}
                        <label>FOV<input type="number" min={10} max={120} step={1} aria-label="Shot camera field of view" value={shotCamera.fieldOfView}
                          onChange={event => setShotCamera(current => current && ({ ...current, fieldOfView: Number(event.target.value) }))} /></label>
                      </div></div>
                    </div>}
                    <button type="button" className="primary scene-render-still" disabled={busy || dirty || !shotCamera || !captureStill}
                      onClick={() => void renderStill()}>{busy ? 'Rendering…' : 'Render still for review'}</button>
                    <p className="model-note">This explicit action freezes the saved scene, this separate shot camera, timing, and the {studio.project.deliveryWidth} × {studio.project.deliveryHeight} {studio.project.colorSpace} delivery canvas. It creates a working candidate; approval still happens in Review.</p>
                  </>}
              {shotBindings.length > 0 && <ul className="scene-shot-bindings">
                {shotBindings.slice(0, 3).map(binding => <li key={binding.id}>
                  <img src={binding.stillAssetUrl} alt={`${binding.shotCode} scene still`} />
                  <span><strong>{binding.shotCode} v{binding.shotVersion}</strong><small>Scene v{binding.sceneVersion} · {binding.stillTime.toFixed(2)} s · {binding.deliveryWidth} × {binding.deliveryHeight}</small><code>{binding.snapshotHash.slice(0, 12)}</code></span>
                </li>)}
              </ul>}
            </section>

            <section data-testid="scene-reference">
              <h2>Reference</h2>
              <label>Read a blockout from<select aria-label="Scene reference" value={referenceId ?? ''}
                disabled={references.length === 0}
                onChange={event => setReferenceId(event.target.value || undefined)}>
                <option value="">{references.length === 0 ? 'No reference images yet' : 'No reference selected'}</option>
                {references.map(asset => <option key={asset.id} value={asset.id}>{asset.displayName}</option>)}
              </select></label>
              {reference && <figure className="scene-reference-frame">
                <img src={reference.contentUrl} alt={`Reference ${reference.displayName}`} />
                <figcaption>An agent can read this reference and propose a plan. Nothing is built until you approve it.</figcaption>
              </figure>}
              {!reference && <p className="model-note"><Image size={14} /> Choose a reference to read a blockout from.</p>}
              {builtFrom && <p className="model-note" data-testid="scene-built-from">
                Built from “{builtFrom.title}”, read from {builtFrom.referenceName}
                {builtFrom.referenceChanged ? ' · that reference has changed since' : ''}.
              </p>}
            </section>

            {reference && <section data-testid="scene-blockouts">
              <h2>Blockout plans <span>{plans.filter(plan => plan.state === 'Pending').length}</span></h2>
              {plans.length === 0 && <p className="model-note">No plans for this reference yet.</p>}
              <ul className="scene-blockout-list">
                {plans.slice(0, 6).map(plan => <li key={plan.id} data-testid="scene-blockout" data-state={plan.state}>
                  <strong>{plan.title}</strong>
                  <p>{plan.summary}</p>
                  <ol className="scene-blockout-items">
                    {plan.items.map(item => <li key={item.id}>
                      <span>{item.role}</span>
                      <small>
                        {item.matchAssetName
                          ? `matches ${item.matchAssetName}`
                          : `${item.placeholder?.shape ?? 'Box'} stand-in ${(item.placeholder?.size ?? []).map(value => value.toFixed(2)).join(' × ')} m`}
                        {' · '}{item.confidence.toLowerCase()}
                        {item.motionIntent ? ` · ${item.motionIntent}` : ''}
                      </small>
                    </li>)}
                  </ol>
                  {plan.assumptions.length > 0 && <p className="model-note">Assumes: {plan.assumptions.join(' ')}</p>}
                  {plan.uncertainties.length > 0 && <p className="model-note" data-testid="scene-blockout-uncertainty">
                    Could not see: {plan.uncertainties.join(' ')}
                  </p>}
                  {plan.referenceChanged && <p className="model-note">This reference changed after the plan was read.</p>}
                  {plan.state === 'Pending'
                    ? <div>
                        <button type="button" disabled={busy} onClick={() => void rejectBlockout(plan)}>Reject</button>
                        <button type="button" className="primary compact" disabled={busy} onClick={() => void buildBlockout(plan)}>Build blockout scene</button>
                      </div>
                    : <small>{plan.state === 'Applied' ? `Built as ${plan.sceneName ?? 'a scene'}` : 'Rejected'}</small>}
                </li>)}
              </ul>
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
