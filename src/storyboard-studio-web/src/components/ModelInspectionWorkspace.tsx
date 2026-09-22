import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react'
import { Archive, ArrowLeft, Bone, Box, Check, Layers3, LoaderCircle, LockKeyhole, Ruler, TriangleAlert, Upload } from 'lucide-react'
import { studioApi } from '../api'
import { DirectorModeButton, type AssetDirectorControls } from './AssetDirectorMode'
import { useAssetDirectorView } from './useAssetDirectorView'
import type { AssetSummary, ModelProfileSummary, RigPoseSummary } from '../types'

// three.js only loads when an artist actually opens a model, so the ordinary
// image and audio workflows keep their current start-up cost.
const ModelViewer = lazy(() => import('./ModelViewer'))
// Only a model worth reducing ever opens this, and it pulls its own
// evidence images, so it loads with the model rather than with the app.
const ModelPreparation = lazy(() => import('./ModelPreparation'))

/**
 * The isolated inspection surface for one model: its revision stack, the
 * geometry itself, the numbers measured from the stored bytes, and the library
 * organisation that can be edited without touching any of those facts.
 */
export default function ModelInspectionWorkspace({ asset, onBack, onError, onChanged, ...director }: AssetDirectorControls & {
  asset: AssetSummary
  onBack: () => void
  onError: (message: string) => void
  onChanged?: (message: string) => void
}) {
  const [revisions, setRevisions] = useState<AssetSummary[]>([asset])
  const [activeId, setActiveId] = useState(asset.id)
  const [profile, setProfile] = useState<ModelProfileSummary>()
  const [failure, setFailure] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [name, setName] = useState(asset.displayName)
  const [tagText, setTagText] = useState(asset.tags.join(', '))
  const [notes, setNotes] = useState(asset.notes)
  const revisionFile = useRef<HTMLInputElement>(null)
  const [pose, setPose] = useState<RigPoseSummary>()

  const active = revisions.find(item => item.id === activeId) ?? asset
  const current = revisions.find(item => item.isCurrentRevision) ?? revisions[0]
  useAssetDirectorView(active, director)

  // The library hands down a fresh asset object on every refresh. Keying the
  // reset on its identity would throw the artist back to the revision they
  // opened with each time they saved something, so only a genuinely different
  // asset resets the selection.
  const opened = useRef(asset)
  opened.current = asset
  const loadRevisions = useCallback(async () => {
    try {
      const stack = await studioApi.assetRevisions(opened.current.id)
      setRevisions(stack.length > 0 ? stack : [opened.current])
    } catch { setRevisions([opened.current]) }
  }, [])

  useEffect(() => { setActiveId(asset.id); void loadRevisions() }, [asset.id, loadRevisions])
  // The form follows the artist's choice of revision, not every refresh of the
  // revision list. Reacting to the list itself would wipe whatever they were
  // part-way through typing the moment a reload landed.
  useEffect(() => {
    const revision = revisions.find(item => item.id === activeId)
    if (!revision) return
    setName(revision.displayName); setTagText(revision.tags.join(', ')); setNotes(revision.notes)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeId])

  // A model being looked at has already been fetched to be drawn, so making
  // its thumbnail here costs a cached read rather than a download. Rendering
  // every card in the library speculatively was the other option, and it put
  // a hundred and thirty-nine megabytes of geometry ahead of everything the
  // artist was actually waiting for.
  useEffect(() => {
    let live = true
    void (async () => {
      const existing = await studioApi.assetPosters().catch(() => [] as string[])
      if (!live || existing.includes(activeId)) return
      const revision = revisions.find(item => item.id === activeId) ?? asset
      const { renderModelPoster } = await import('./modelPoster')
      await renderModelPoster(activeId, revision.contentUrl, revision.bytes)
    })()
    return () => { live = false }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeId])

  useEffect(() => {
    let live = true
    // A pose belongs to the revision it was calculated from, so switching
    // revisions drops it rather than showing one model's bones against
    // another's mesh.
    setProfile(undefined); setFailure(undefined); setPose(undefined)
    studioApi.modelProfile(activeId)
      .then(loaded => { if (live) setProfile(loaded) })
      .catch(reason => { if (live) setFailure(reason instanceof Error ? reason.message : 'This model could not be inspected.') })
    return () => { live = false }
  }, [activeId])

  const act = async (operation: () => Promise<string>) => {
    setBusy(true); setFailure(undefined)
    try { onChanged?.(await operation()) }
    catch (reason) { setFailure(reason instanceof Error ? reason.message : 'That change could not be saved.') }
    finally { setBusy(false) }
  }

  const saveDetails = () => act(async () => {
    const saved = await studioApi.updateAsset(active.id, {
      displayName: name.trim(), collectionId: active.collectionId, notes,
      tags: tagText.split(',').map(tag => tag.trim()).filter(Boolean),
    })
    await loadRevisions()
    return `${saved.displayName} details saved.`
  })

  const makeCurrent = () => act(async () => {
    const promoted = await studioApi.makeAssetRevisionCurrent(active.id)
    await loadRevisions()
    return `Revision ${promoted.revisionNumber ?? 1} is now the current model.`
  })

  const toggleArchive = () => act(async () => {
    const saved = active.isArchived ? await studioApi.restoreAsset(active.id) : await studioApi.archiveAsset(active.id)
    await loadRevisions()
    return saved.isArchived
      ? `${saved.displayName} archived. It stays resolvable for anything that cites it.`
      : `${saved.displayName} restored to the library.`
  })

  // A new revision is an ordinary validated import that is then added to this
  // stack, so a refused model never becomes a revision of anything.
  const addRevision = (files?: FileList | null) => {
    const file = files?.[0]
    if (!file) return
    void act(async () => {
      const imported = await studioApi.uploadModel(file)
      const added = await studioApi.addAssetRevision(asset.id, imported.id, `Imported ${file.name}`, 'Imported')
      await loadRevisions()
      setActiveId(added.id)
      return `Revision ${added.revisionNumber ?? 2} added. The earlier revision is untouched.`
    })
  }

  const metres = (value: number) => `${value.toFixed(value < 10 ? 2 : 1)} m`

  const rig = profile?.rig
  // One documented pose, so the same press gives the same numbers every time
  // and an artist can see plainly that the skeleton really drives the mesh.
  const testPose = () => act(async () => {
    const posed = await studioApi.rigPose(activeId, [{ bone: 'LeftUpperArm', rotation: [0, 0, 1.5708] }])
    setPose(posed)
    return 'Test pose applied to the skeleton. Nothing was saved.'
  })

  return <main className={`workspace model-workspace${director.directorMode ? ' director-mode' : ''}`} data-testid="model-workspace">
    <header className="model-header">
      <button className="secondary compact" onClick={onBack}><ArrowLeft size={16} />Asset library</button>
      <div><p className="eyebrow">Model inspection</p><h1>{active.displayName}</h1></div>
      <span className="model-format"><Box size={15} />{profile?.container ?? 'GLB'}</span>
      <DirectorModeButton active={director.directorMode} onClick={() => director.onDirectorMode(!director.directorMode)} />
    </header>

    {failure && <p className="asset-error" role="alert" data-testid="model-failure">{failure}</p>}

    <div className="model-layout">
      <section className="model-stage-shell">
        {profile
          ? <Suspense fallback={<div className="asset-loading"><LoaderCircle className="spin" /><span>Opening the 3D view…</span></div>}>
              <ModelViewer
                key={profile.assetId}
                contentUrl={profile.contentUrl}
                label={profile.displayName}
                dimensions={[profile.dimensions[0], profile.dimensions[1], profile.dimensions[2]]}
                onError={message => { setFailure(message); onError(message) }}
              />
            </Suspense>
          : !failure && <div className="asset-loading"><LoaderCircle className="spin" /><span>Measuring the model…</span></div>}
      </section>

      <aside className="model-inspector" aria-label="Model details">
        <section data-testid="model-revisions">
          <h2><Layers3 size={15} />Revisions</h2>
          <ul className="model-revision-stack">
            {revisions.map(revision => <li key={revision.id}>
              <button
                type="button"
                className={revision.id === activeId ? 'active' : ''}
                aria-pressed={revision.id === activeId}
                onClick={() => setActiveId(revision.id)}
              >
                <strong>v{revision.revisionNumber ?? 1}</strong>
                <small>{revision.isCurrentRevision ? 'Current' : 'Earlier'} · {revision.revisionEngine || revision.source}{revision.isArchived ? ' · archived' : ''}</small>
              </button>
            </li>)}
          </ul>
          {active.revisionPrompt && <p className="model-note" data-testid="model-revision-note">{active.revisionPrompt}</p>}
          <div className="model-revision-actions">
            <input ref={revisionFile} className="sr-only" type="file" accept=".glb,model/gltf-binary" onChange={event => { addRevision(event.target.files); event.target.value = '' }} />
            <button type="button" disabled={busy} onClick={() => revisionFile.current?.click()}><Upload size={15} />Add revision</button>
            {active.id !== current?.id && <button type="button" className="primary compact" disabled={busy} onClick={() => void makeCurrent()}><Check size={15} />Make current</button>}
          </div>
          <p className="model-note">A new revision never overwrites an earlier one. Every revision keeps its own file, measurements, and materials.</p>
        </section>

        {profile && <>
          <section data-testid="model-dimensions">
            <h2><Ruler size={15} />Dimensions</h2>
            <dl>
              <div><dt>Width (X)</dt><dd>{metres(profile.dimensions[0])}</dd></div>
              <div><dt>Height (Y)</dt><dd>{metres(profile.dimensions[1])}</dd></div>
              <div><dt>Depth (Z)</dt><dd>{metres(profile.dimensions[2])}</dd></div>
            </dl>
            <p className="model-note">Scene space, +Y up, metres. Measured from the stored file, not from the view.</p>
            <p className="model-bounds">min ({profile.boundsMin.map(value => value.toFixed(2)).join(', ')}) · max ({profile.boundsMax.map(value => value.toFixed(2)).join(', ')})</p>
          </section>

          <Suspense fallback={null}>
            <ModelPreparation
              asset={active}
              profile={profile}
              onQueued={message => onChanged?.(message)}
              onDecided={message => { onChanged?.(message); void loadRevisions() }}
            />
          </Suspense>

          <section data-testid="model-statistics">
            <h2>Geometry</h2>
            <dl>
              <div><dt>Vertices</dt><dd>{profile.vertexCount.toLocaleString()}</dd></div>
              <div><dt>Triangles</dt><dd>{profile.triangleCount.toLocaleString()}</dd></div>
              <div><dt>Meshes</dt><dd>{profile.meshCount.toLocaleString()}</dd></div>
              <div><dt>Nodes</dt><dd>{profile.nodeCount.toLocaleString()}</dd></div>
              <div><dt>File</dt><dd>{(profile.bytes / 1024).toFixed(0)} KB</dd></div>
              <div><dt>Embedded textures</dt><dd>{profile.imageCount === 0 ? 'None' : `${profile.imageCount} · ${(profile.embeddedTextureBytes / 1024).toFixed(0)} KB`}</dd></div>
            </dl>
          </section>

          <section data-testid="model-materials">
            <h2>Materials</h2>
            {profile.materials.length === 0 && <p className="model-note">This model declares no materials.</p>}
            <ul>{profile.materials.map(material => <li key={material.name}>
              <strong>{material.name}</strong>
              <small>
                {material.textured ? 'Textured' : 'Untextured'}
                {' · '}
                {material.transmission > 0
                  ? `transmits ${Math.round(material.transmission * 100)}%`
                  : material.alphaMode.toLowerCase()}
                {' · '}
                {material.doubleSided ? 'double sided' : 'single sided'}
              </small>
            </li>)}</ul>
          </section>
        </>}

        {rig && <section data-testid="model-rig">
          <h2><Bone size={15} />Rig</h2>
          {!rig.hasSkeleton
            ? <p className="model-note" data-testid="model-rig-state">
                Static prop: no skeleton. Props do not need one, and this is not a fault.
              </p>
            : <>
                <p className="model-rig-state" data-testid="model-rig-state" data-ready={rig.animationReady ? 'true' : 'false'}>
                  {rig.animationReady
                    ? `Animation-ready · ${rig.profileName}`
                    : `Not animation-ready · ${rig.profileName}`}
                </p>
                <dl>
                  <div><dt>Bones</dt><dd>{rig.boneCount}</dd></div>
                  <div><dt>Skinned vertices</dt><dd>{rig.skinnedVertexCount.toLocaleString()}</dd></div>
                  <div><dt>Rest transforms</dt><dd>{rig.transformsFinite ? 'Finite' : 'Not finite'}</dd></div>
                  <div><dt>Bind pose</dt><dd>{rig.bindPoseValid ? 'Valid' : 'Not confirmed'}</dd></div>
                  <div><dt>Skin weights</dt><dd>{rig.skinWeightsValid ? `Valid · ${rig.skinWeightsChecked.toLocaleString()} checked` : 'Not valid'}</dd></div>
                </dl>
                {rig.findings.length > 0 && <ul className="model-rig-findings" data-testid="model-rig-findings">
                  {rig.findings.map(finding => <li key={finding}><TriangleAlert size={14} />{finding}</li>)}
                </ul>}
                <ul className="model-rig-bones" data-testid="model-rig-bones">
                  {rig.bones.slice(0, 40).map(bone => <li key={bone.name} style={{ paddingLeft: `${bone.depth * 10}px` }}>
                    <strong>{bone.name}</strong>
                    <small>
                      {bone.parent ? `under ${bone.parent}` : 'root'} · rest ({bone.restWorldPosition.map(value => value.toFixed(2)).join(', ')})
                      {pose ? ` · posed (${(pose.joints.find(joint => joint.bone === bone.name)?.position ?? bone.restWorldPosition).map(value => value.toFixed(2)).join(', ')})` : ''}
                    </small>
                  </li>)}
                </ul>
                <div className="model-rig-actions">
                  <button type="button" data-testid="model-rig-pose" disabled={busy || !rig.animationReady} onClick={() => void testPose()}>
                    Test pose
                  </button>
                  {pose && <button type="button" onClick={() => setPose(undefined)}>Back to rest</button>}
                </div>
                <p className="model-note">
                  {rig.animationReady
                    ? 'A test pose is calculated from the stored file and saved nowhere. The model on screen is unchanged.'
                    : 'This rig is not posed until every check passes. Bones named something else are an unknown skeleton, not a nearly-humanoid one.'}
                </p>
              </>}
        </section>}

        <section data-testid="model-library">
          <h2>Library</h2>
          <label>Name<input value={name} maxLength={120} onChange={event => setName(event.target.value)} /></label>
          <label>Tags <small>comma separated</small><input value={tagText} onChange={event => setTagText(event.target.value)} placeholder="set-dressing, exterior" /></label>
          <label>Notes<textarea value={notes} maxLength={2000} onChange={event => setNotes(event.target.value)} placeholder="Provenance, licensing, or continuity notes" /></label>
          <button type="button" className="primary compact" disabled={busy || !name.trim()} onClick={() => void saveDetails()}>{busy ? 'Saving…' : 'Save details'}</button>
          <button type="button" className="asset-archive" disabled={busy} onClick={() => void toggleArchive()}>
            <Archive size={15} />{active.isArchived ? 'Restore to library' : 'Archive model'}
          </button>
          <p className="model-note">Archiving hides a model from the library without deleting it. Anything that already cites this revision keeps resolving.</p>
        </section>

        {profile && <section data-testid="model-support">
          <h2><LockKeyhole size={15} />Provenance and supported subset</h2>
          <dl>
            <div><dt>Source</dt><dd>{active.source}</dd></div>
            <div><dt>Original file</dt><dd>{active.originalFileName}</dd></div>
          </dl>
          <p className="model-note">
            glTF {profile.specificationVersion} in a self-contained GLB. Accepted up to {(profile.limits.maxBytes / (1024 * 1024)).toFixed(0)} MB,
            {' '}{profile.limits.maxVertices.toLocaleString()} vertices and {profile.limits.maxTriangles.toLocaleString()} triangles.
            {profile.limits.supportedRequiredExtensions.length === 0
              ? ' No required glTF extensions are supported yet.'
              : ` Supported required extensions: ${profile.limits.supportedRequiredExtensions.join(', ')}.`}
          </p>
          <p className="model-hash">sha256 {profile.contentHash.slice(0, 16)}…</p>
        </section>}
      </aside>
    </div>
  </main>
}
