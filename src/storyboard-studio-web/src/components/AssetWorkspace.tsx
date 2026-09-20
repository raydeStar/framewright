import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Archive, ArrowLeft, ArrowRight, BadgeCheck, Box, Check, Clapperboard, Download, Folder, FolderOpen, GitCompare, Grid2X2, Image, ImagePlus, Library, List, LoaderCircle, Music2, Pause, PenLine, Play, Plus, Search, SlidersHorizontal, Sparkles, Upload, Video, WandSparkles, X } from 'lucide-react'
import { studioApi } from '../api'
import type { AssetCollectionSummary, AssetGenerationDraft, AssetGenerationReference, AssetPlacementSummary, AssetReviewNoteSummary, AssetSummary, JobSummary, ReferenceSummary, ShotSummary, StudioSnapshot } from '../types'
import Dialog from './Dialog'
import ModelInspectionWorkspace from './ModelInspectionWorkspace'
import AssetReviewPins from './AssetReviewPins'

type LibraryView = 'all' | 'images' | 'audio' | 'video' | 'models' | 'authorities' | 'archived' | `collection:${string}`
export default function AssetWorkspace({ studio, initialAssetId, onEditAuthority, onOpenGeneration, onOpenSequence, onJobQueued, onToast }: {
  studio: StudioSnapshot
  initialAssetId?: string
  onEditAuthority: (reference: ReferenceSummary) => void
  onOpenGeneration: (draft: AssetGenerationDraft) => void
  onOpenSequence: () => void
  onJobQueued: (job: JobSummary) => void
  onToast: (message: string) => void
}) {
  const [assets, setAssets] = useState<AssetSummary[]>([])
  const [collections, setCollections] = useState<AssetCollectionSummary[]>([])
  const [placements, setPlacements] = useState<AssetPlacementSummary[]>([])
  const [view, setView] = useState<LibraryView>('all')
  const [query, setQuery] = useState('')
  const [layout, setLayout] = useState<'grid' | 'list'>('grid')
  const [sort, setSort] = useState<'newest' | 'name' | 'type'>('newest')
  const [selectedId, setSelectedId] = useState<string | undefined>(initialAssetId)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [createOpen, setCreateOpen] = useState(false)
  const [collectionEditor, setCollectionEditor] = useState<AssetCollectionSummary | 'new'>()
  const fileRef = useRef<HTMLInputElement>(null)
  const observedTerminalJobs = useRef(new Set(studio.jobs
    .filter(job => job.state === 'Completed' || job.state === 'Failed' || job.state === 'Cancelled')
    .map(job => job.id)))

  const refresh = useCallback(async () => {
    const [nextAssets, nextCollections, nextPlacements] = await Promise.all([studioApi.assets(true), studioApi.assetCollections(), studioApi.assetPlacements()])
    setAssets(nextAssets); setCollections(nextCollections); setPlacements(nextPlacements)
  }, [])
  useEffect(() => { void refresh().catch(reason => setError(reason instanceof Error ? reason.message : 'Could not open the asset library.')).finally(() => setLoading(false)) }, [refresh])
  useEffect(() => {
    const finished = studio.jobs.filter(job =>
      Boolean(job.outputAssetId) &&
      (job.state === 'Completed' || job.state === 'Failed' || job.state === 'Cancelled') &&
      !observedTerminalJobs.current.has(job.id))
    if (finished.length === 0) return
    finished.forEach(job => observedTerminalJobs.current.add(job.id))
    void refresh().then(() => {
      const completed = finished.filter(job => job.state === 'Completed').length
      const failed = finished.length - completed
      if (completed > 0) onToast(`${completed} generated asset${completed === 1 ? '' : 's'} arrived in the library.`)
      if (failed > 0) setError(`${failed} generation job${failed === 1 ? '' : 's'} stopped. Open the jobs panel to inspect or retry.`)
    }).catch(reason => setError(reason instanceof Error ? reason.message : 'Could not refresh generated assets.'))
  }, [onToast, refresh, studio.jobs])

  const selected = assets.find(asset => asset.id === selectedId)
  const libraryAssets = useMemo(() => assets.filter(asset => !asset.revisionFamilyId || asset.isCurrentRevision), [assets])
  const normalized = query.trim().toLowerCase()
  const visible = useMemo(() => libraryAssets.filter(asset => {
    if (view === 'archived' ? !asset.isArchived : asset.isArchived) return false
    if (view === 'images' && asset.kind !== 'Image') return false
    if (view === 'audio' && asset.kind !== 'Audio') return false
    if (view === 'video' && asset.kind !== 'Video') return false
    if (view === 'models' && asset.kind !== 'Model') return false
    if (view === 'collection:unfiled' && asset.collectionId) return false
    if (view.startsWith('collection:') && view !== 'collection:unfiled' && asset.collectionId !== view.slice('collection:'.length)) return false
    if (normalized && ![asset.displayName, asset.originalFileName, asset.source, asset.notes, ...asset.tags].join(' ').toLowerCase().includes(normalized)) return false
    return true
  }).sort((a, b) => sort === 'name' ? a.displayName.localeCompare(b.displayName) : sort === 'type' ? a.kind.localeCompare(b.kind) || a.displayName.localeCompare(b.displayName) : b.createdAt.localeCompare(a.createdAt)), [libraryAssets, normalized, sort, view])

  const importFiles = async (files?: FileList | File[]) => {
    if (!files?.length || busy) return
    setBusy(true); setError(undefined)
    try {
      let imported = 0
      for (const file of Array.from(files)) {
        if (file.name.toLowerCase().endsWith('.glb')) await studioApi.uploadModel(file)
        else if (file.type.startsWith('image/')) await studioApi.uploadImage(file, file.name)
        else if (file.type.startsWith('audio/')) await studioApi.uploadMedia(file, 'Audio')
        else if (file.type.startsWith('video/')) await studioApi.uploadMedia(file, 'Video')
        else throw new Error(`${file.name} is not a supported image, audio, video, or GLB model file.`)
        imported++
      }
      await refresh(); onToast(`${imported} asset${imported === 1 ? '' : 's'} imported into the library.`)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not import that media.') }
    finally { setBusy(false); if (fileRef.current) fileRef.current.value = '' }
  }

  const moveToCollection = async (assetId: string, collectionId?: string) => {
    const asset = assets.find(item => item.id === assetId); if (!asset) return
    setBusy(true); setError(undefined)
    try {
      const updated = await studioApi.updateAsset(asset.id, { displayName: asset.displayName, collectionId, tags: asset.tags, notes: asset.notes })
      setAssets(current => current.map(item => item.id === updated.id ? updated : item)); await refresh()
      onToast(collectionId ? `Moved ${asset.displayName} to ${collections.find(item => item.id === collectionId)?.name}.` : `Moved ${asset.displayName} to Unfiled.`)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not move that asset.') }
    finally { setBusy(false) }
  }

  const smart = [
    { id: 'all' as const, label: 'All assets', icon: <Library />, count: libraryAssets.filter(x => !x.isArchived).length },
    { id: 'images' as const, label: 'Images', icon: <Image />, count: libraryAssets.filter(x => !x.isArchived && x.kind === 'Image').length },
    { id: 'audio' as const, label: 'Music & audio', icon: <Music2 />, count: libraryAssets.filter(x => !x.isArchived && x.kind === 'Audio').length },
    { id: 'video' as const, label: 'Video takes', icon: <Video />, count: libraryAssets.filter(x => !x.isArchived && x.kind === 'Video').length },
    { id: 'models' as const, label: 'Models', icon: <Box />, count: libraryAssets.filter(x => !x.isArchived && x.kind === 'Model').length },
    { id: 'authorities' as const, label: 'Authorities', icon: <BadgeCheck />, count: studio.references.length },
    { id: 'archived' as const, label: 'Archived', icon: <Archive />, count: assets.filter(x => x.isArchived).length },
  ]
  const activeCollection = view.startsWith('collection:') ? collections.find(item => item.id === view.slice('collection:'.length)) : undefined
  const rawGenerationReferences = useMemo<AssetGenerationReference[]>(() => [
    ...studio.shots.filter(shot => shot.currentAssetId && shot.currentAssetUrl).map(shot => ({ id: `shot:${shot.id}`, assetId: shot.currentAssetId!, label: `${shot.code} · ${shot.title}`, detail: `${shot.stage} v${shot.version}`, imageUrl: shot.currentAssetUrl!, source: 'Shot' as const })),
    ...studio.references.filter(reference => reference.imageAssetId && reference.imageUrl).map(reference => ({ id: `authority:${reference.id}`, assetId: reference.imageAssetId!, label: reference.name, detail: `${reference.category} · authority v${reference.version}`, imageUrl: reference.imageUrl!, source: 'Authority' as const })),
    ...libraryAssets.filter(asset => asset.kind === 'Image' && !asset.isArchived).map(asset => ({ id: `asset:${asset.id}`, assetId: asset.id, label: asset.displayName, detail: asset.source, imageUrl: asset.contentUrl, source: 'Asset' as const })),
  ], [libraryAssets, studio.references, studio.shots])
  const generationReferences = useMemo(() => {
    const seen = new Set<string>()
    return rawGenerationReferences.filter(reference => {
      if (seen.has(reference.assetId)) return false
      seen.add(reference.assetId)
      return true
    })
  }, [rawGenerationReferences])

  if (selected?.kind === 'Model') return <ModelInspectionWorkspace asset={selected} onBack={() => setSelectedId(undefined)} onError={setError} />
  if (selected?.kind === 'Image') return <ImageRevisionWorkspace asset={selected} collections={collections} shots={studio.shots} references={generationReferences} onBack={() => setSelectedId(undefined)} onOpenGeneration={onOpenGeneration} onChanged={async message => { await refresh(); onToast(message) }} onEditAuthority={onEditAuthority} />

  return <main className="workspace asset-workspace">
    <header className="asset-hero">
      <div><p className="eyebrow">Project media pool</p><h1>Asset library</h1><p>One searchable home for approved references, working images, music, audio, and video takes.</p></div>
      <div className="asset-hero-actions">
        <input ref={fileRef} className="sr-only" type="file" multiple accept="image/png,image/jpeg,audio/*,video/mp4,video/webm,.glb,model/gltf-binary" onChange={event => void importFiles(event.target.files ?? undefined)} />
        <button className="secondary" onClick={() => fileRef.current?.click()} disabled={busy}><Upload size={17} />Import media</button>
        <button className="primary" onClick={() => setCreateOpen(true)}><Plus size={18} />Create new</button>
      </div>
    </header>
    <div className="asset-layout">
      <aside className="asset-sidebar" aria-label="Asset collections">
        <section><span className="asset-sidebar-label">Library</span>{smart.map(item => <button key={item.id} className={view === item.id ? 'active' : ''} onClick={() => { setView(item.id); setSelectedId(undefined) }}>{item.icon}<span>{item.label}</span><small>{item.count}</small></button>)}</section>
        <section><div className="asset-sidebar-heading"><span className="asset-sidebar-label">Collections</span><button aria-label="Create collection" onClick={() => setCollectionEditor('new')}><Plus size={15} /></button></div>
          <button className={view === 'collection:unfiled' ? 'active' : ''} onDragOver={event => event.preventDefault()} onDrop={event => void moveToCollection(event.dataTransfer.getData('text/asset-id'), undefined)} onClick={() => { setView('collection:unfiled'); setSelectedId(undefined) }}><FolderOpen /><span>Unfiled</span><small>{assets.filter(x => !x.isArchived && !x.collectionId).length}</small></button>
          {collections.map(collection => <button key={collection.id} className={view === `collection:${collection.id}` ? 'active' : ''} onDragOver={event => event.preventDefault()} onDrop={event => void moveToCollection(event.dataTransfer.getData('text/asset-id'), collection.id)} onClick={() => { setView(`collection:${collection.id}`); setSelectedId(undefined) }}><i style={{ background: collection.color }} /><span>{collection.name}</span><small>{collection.assetCount}</small></button>)}
        </section>
        {activeCollection && <div className="asset-collection-actions"><button onClick={() => setCollectionEditor(activeCollection)}>Rename</button><button className="danger-text" onClick={async () => { if (!window.confirm(`Delete the ${activeCollection.name} collection? Its assets will move to Unfiled.`)) return; await studioApi.deleteAssetCollection(activeCollection.id); setView('all'); await refresh(); onToast('Collection removed; its assets are still safe in Unfiled.') }}>Delete</button></div>}
      </aside>
      <section className="asset-browser">
        <div className="asset-toolbar">
          <label className="asset-search"><Search size={16} /><input value={query} onChange={event => setQuery(event.target.value)} placeholder="Search names, tags, notes, or source" aria-label="Search assets" />{query && <button onClick={() => setQuery('')} aria-label="Clear asset search"><X size={14} /></button>}</label>
          <label className="asset-sort"><SlidersHorizontal size={15} /><select aria-label="Sort assets" value={sort} onChange={event => setSort(event.target.value as typeof sort)}><option value="newest">Newest</option><option value="name">Name</option><option value="type">Media type</option></select></label>
          <div className="asset-view-toggle" role="group" aria-label="Asset layout"><button aria-pressed={layout === 'grid'} onClick={() => setLayout('grid')}><Grid2X2 size={16} /></button><button aria-pressed={layout === 'list'} onClick={() => setLayout('list')}><List size={17} /></button></div>
        </div>
        {error && <p className="asset-error" role="alert"><span>{error}</span><button onClick={() => setError(undefined)}><X size={15} /></button></p>}
        {loading ? <div className="asset-loading"><LoaderCircle className="spin" /><span>Opening the media pool…</span></div> : view === 'authorities' ? <AuthorityLibrary references={studio.references} query={normalized} onEdit={onEditAuthority} /> : visible.length === 0 ? <AssetEmpty view={view} query={query} onCreate={() => setCreateOpen(true)} onImport={() => fileRef.current?.click()} /> : <div className={`asset-items ${layout}`}>
          {visible.map(asset => <AssetCard key={asset.id} asset={asset} placementCount={placements.filter(item => item.assetId === asset.id).length} selected={selectedId === asset.id} onSelect={() => setSelectedId(asset.id)} />)}
        </div>}
      </section>
      {selected && <AssetInspector asset={selected} collections={collections} placements={placements.filter(item => item.assetId === selected.id)} originJob={studio.jobs.find(job => job.outputAssetId === selected.id && job.workType === 'Shot')} shots={studio.shots} onClose={() => setSelectedId(undefined)} onSaved={updated => { setAssets(current => current.map(item => item.id === updated.id ? updated : item)); void refresh(); onToast(`${updated.displayName} metadata saved.`) }} onPlacementsChanged={() => void refresh()} onOpenGeneration={() => onOpenGeneration({ id: crypto.randomUUID(), name: `${selected.displayName} variation`, initialPrompt: `Create a new reusable variation of ${selected.displayName}. Preserve its defining identity and design while following my composition notes.`, route: 'fast', underlayAssetId: selected.id, references: generationReferences, revisionFamilyId: selected.revisionFamilyId, parentAssetId: selected.id })} onOpenSequence={onOpenSequence} />}
    </div>
    {createOpen && <CreateAssetLauncher references={generationReferences} onClose={() => setCreateOpen(false)} onImport={() => { setCreateOpen(false); fileRef.current?.click() }} onOpenGeneration={draft => { setCreateOpen(false); onOpenGeneration(draft) }} onJobQueued={onJobQueued} />}
    {collectionEditor && <CollectionDialog collection={collectionEditor === 'new' ? undefined : collectionEditor} onClose={() => setCollectionEditor(undefined)} onSaved={async saved => { setCollectionEditor(undefined); await refresh(); setView(`collection:${saved.id}`); onToast(`${saved.name} collection saved.`) }} />}
  </main>
}

function AssetCard({ asset, placementCount, selected, onSelect }: { asset: AssetSummary; placementCount: number; selected: boolean; onSelect: () => void }) {
  const [playing, setPlaying] = useState(false)
  return <article className={`asset-card ${selected ? 'selected' : ''}`} draggable={!asset.isArchived} onDragStart={event => { event.dataTransfer.setData('text/asset-id', asset.id); event.dataTransfer.effectAllowed = 'move' }}>
    <button className="asset-card-open" onClick={onSelect} aria-label={`Open ${asset.displayName}`}>
      <div className={`asset-thumbnail ${asset.kind.toLowerCase()}`}>{asset.kind === 'Image' ? <img src={asset.contentUrl} alt="" /> : asset.kind === 'Video' ? <video src={asset.contentUrl} muted preload="metadata" /> : asset.kind === 'Model' ? <Box size={26} /> : <><Music2 /><div className="asset-wave">{[2,5,3,7,4,8,3,6,2,5,7,3].map((height, index) => <i key={index} style={{ height: `${height * 8}%` }} />)}</div></>}<span>{asset.kind}</span></div>
      <div className="asset-card-copy"><strong>{asset.displayName}</strong><small>{asset.source} · {formatSize(asset.bytes)}</small><div>{asset.tags.slice(0, 2).map(tag => <em key={tag}>{tag}</em>)}{placementCount > 0 && <em className="used"><Clapperboard size={11} />{placementCount}</em>}</div></div>
    </button>
    {asset.kind === 'Audio' && <button className="asset-quick-play" onClick={event => { event.stopPropagation(); const audio = event.currentTarget.parentElement?.querySelector('audio'); if (!audio) return; if (playing) audio.pause(); else void audio.play(); setPlaying(!playing) }} aria-label={`${playing ? 'Pause' : 'Play'} ${asset.displayName}`}>{playing ? <Pause size={14} /> : <Play size={14} />}<audio src={asset.contentUrl} onEnded={() => setPlaying(false)} /></button>}
  </article>
}

function ImageRevisionWorkspace({ asset, collections, shots, references, onBack, onOpenGeneration, onChanged, onEditAuthority }: {
  asset: AssetSummary; collections: AssetCollectionSummary[]; shots: ShotSummary[]; references: AssetGenerationReference[]
  onBack: () => void; onOpenGeneration: (draft: AssetGenerationDraft) => void; onChanged: (message: string) => Promise<void>; onEditAuthority: (reference: ReferenceSummary) => void
}) {
  const [revisions, setRevisions] = useState<AssetSummary[]>([])
  const [activeId, setActiveId] = useState(asset.id)
  const [compare, setCompare] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const [name, setName] = useState(asset.displayName)
  const [collectionId, setCollectionId] = useState(asset.collectionId ?? '')
  const [tags, setTags] = useState(asset.tags.join(', '))
  const [notes, setNotes] = useState(asset.notes)
  const [shotId, setShotId] = useState(shots[0]?.id ?? '')
  const [reviewNotes, setReviewNotes] = useState<AssetReviewNoteSummary[]>([])
  const [authorityCategory, setAuthorityCategory] = useState('Character')
  const [authorityDescription, setAuthorityDescription] = useState(asset.notes || `Approved visual identity for ${asset.displayName}.`)
  const [authorityConstraint, setAuthorityConstraint] = useState('Preserve the approved identity, silhouette, materials, and defining details shown in this exact image.')
  const importRef = useRef<HTMLInputElement>(null)
  const syncedRevisionId = useRef<string | undefined>(undefined)
  const load = useCallback(async () => {
    const next = await studioApi.assetRevisions(asset.id)
    setRevisions(next)
    const current = next.find(item => item.isCurrentRevision) ?? next[0]
    setActiveId(existing => next.some(item => item.id === existing) ? existing : current?.id ?? asset.id)
  }, [asset.id])
  useEffect(() => { void load().catch(reason => setError(reason instanceof Error ? reason.message : 'Could not open image history.')) }, [load])
  const active = revisions.find(item => item.id === activeId) ?? asset
  const current = revisions.find(item => item.isCurrentRevision) ?? active
  const activeIsCurrent = active.id === current.id
  const openReviewNotes = reviewNotes.filter(note => note.state === 'Open')
  // A revision-history fetch can replace `active` with an equivalent object a
  // moment after the inspector opens. Sync only when the selected revision
  // changes so that late network data never erases text the artist has begun
  // entering in the form.
  useLayoutEffect(() => {
    if (syncedRevisionId.current === active.id) return
    syncedRevisionId.current = active.id
    setName(active.displayName)
    setCollectionId(active.collectionId ?? '')
    setTags(active.tags.join(', '))
    setNotes(active.notes)
  }, [active.collectionId, active.displayName, active.id, active.notes, active.tags])
  useEffect(() => setReviewNotes([]), [active.id])
  const generateRevision = () => {
    const base = active.revisionPrompt || `Create a deliberate revision of ${active.displayName}. Preserve everything not explicitly changed.`
    const pinned = openReviewNotes.length === 0 ? '' : `\n\nPINNED IMAGE REVISION NOTES (apply every note while preserving unmentioned content):\n${openReviewNotes.map((note, index) => `- Note ${index + 1} at ${Math.round(note.x * 100)}% across / ${Math.round(note.y * 100)}% down: ${note.body}`).join('\n')}`
    onOpenGeneration({ id: crypto.randomUUID(), name: active.displayName, initialPrompt: `${base}${pinned}`, route: 'fast', underlayAssetId: active.id, references, revisionFamilyId: active.revisionFamilyId, parentAssetId: active.id })
  }
  const save = async () => { setBusy(true); setError(undefined); try { await studioApi.updateAsset(active.id, { displayName: name.trim(), collectionId: collectionId || undefined, tags: tags.split(',').map(tag => tag.trim()).filter(Boolean), notes }); await load(); await onChanged(`${name.trim()} details saved.`) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not save image details.') } finally { setBusy(false) } }
  const makeCurrent = async () => { setBusy(true); setError(undefined); try { const chosen = await studioApi.makeAssetRevisionCurrent(active.id); await load(); setActiveId(chosen.id); await onChanged(`v${chosen.revisionNumber} is now the working image.`) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not choose that revision.') } finally { setBusy(false) } }
  const importRevision = async (file?: File) => { if (!file) return; setBusy(true); setError(undefined); try { const uploaded = await studioApi.uploadImage(file, file.name); const added = await studioApi.addAssetRevision(active.id, uploaded.id, 'Imported as a visual revision', 'Imported image'); await load(); setActiveId(added.id); await onChanged(`Imported v${added.revisionNumber} into ${active.displayName}.`) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not import that revision.') } finally { setBusy(false); if (importRef.current) importRef.current.value = '' } }
  const archive = async () => { if (revisions.length <= 1) { setError('Keep at least one image in the revision stack. Archive the whole asset from the library instead.'); return } setBusy(true); setError(undefined); try { if (activeIsCurrent) { const replacement = revisions.find(item => item.id !== active.id && !item.isArchived); if (replacement) await studioApi.makeAssetRevisionCurrent(replacement.id) } await studioApi.archiveAsset(active.id); const fallback = revisions.find(item => item.id !== active.id); await load(); if (fallback) setActiveId(fallback.id); await onChanged(`v${active.revisionNumber ?? 1} moved to the archive.`) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not archive that revision.') } finally { setBusy(false) } }
  const place = async () => { if (!shotId) return; setBusy(true); setError(undefined); try { await studioApi.placeAsset(active.id, shotId, 'Image guide'); await onChanged(`${active.displayName} added to ${shots.find(shot => shot.id === shotId)?.code}.`) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not add this image to the shot.') } finally { setBusy(false) } }
  const promote = async () => { setBusy(true); setError(undefined); try { const authority = await studioApi.createReference({ name: name.trim(), category: authorityCategory, description: authorityDescription.trim(), lockedConstraint: authorityConstraint.trim(), accent: '#73b7cf', imageAssetId: active.id }); await onChanged(`${authority.name} created as an immutable authority.`); onEditAuthority(authority) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not promote this image to authority.') } finally { setBusy(false) } }

  return <main className="workspace image-revision-workspace">
    <header className="image-revision-header"><button className="secondary compact" onClick={onBack}><ArrowLeft size={16} />Assets</button><div><p className="eyebrow">Still image studio</p><h1>{current.displayName}</h1><p>Iterate freely here. Only promotion creates immutable canon.</p></div><div className="image-revision-header-actions"><input ref={importRef} className="sr-only" type="file" accept="image/png,image/jpeg" onChange={event => void importRevision(event.target.files?.[0])} /><button className="secondary" onClick={() => importRef.current?.click()} disabled={busy}><Upload size={16} />Import revision</button><button className="primary" onClick={generateRevision}><WandSparkles size={17} />{openReviewNotes.length > 0 ? `Regenerate from ${openReviewNotes.length} note${openReviewNotes.length === 1 ? '' : 's'}` : 'Create revision'}</button></div></header>
    <div className="image-revision-layout">
      <aside className="image-version-rail"><div><small>REVISION STACK</small><strong>{revisions.length || 1} version{revisions.length === 1 ? '' : 's'}</strong></div>{(revisions.length ? revisions : [asset]).map(item => { const itemIsCurrent = item.id === current.id; return <button key={item.id} className={active.id === item.id ? 'active' : ''} onClick={() => { setActiveId(item.id); setCompare(false) }}><img src={item.contentUrl} alt="" /><span><strong>v{item.revisionNumber ?? 1}</strong><small>{itemIsCurrent ? 'Current working image' : item.isArchived ? 'Archived' : item.revisionEngine || item.source}</small></span>{itemIsCurrent && <BadgeCheck size={15} />}</button> })}</aside>
      <section className="image-revision-stage"><div className="image-stage-toolbar"><span>{activeIsCurrent ? <><BadgeCheck size={15} />Current working image</> : `Viewing v${active.revisionNumber ?? 1}`}</span>{revisions.length > 1 && <button className={compare ? 'active' : ''} onClick={() => setCompare(value => !value)}><GitCompare size={16} />{compare ? 'Single view' : 'Compare to current'}</button>}</div>{compare && active.id !== current.id ? <div className="image-compare"><figure><figcaption>Current · v{current.revisionNumber ?? 1}</figcaption><img src={current.contentUrl} alt={`${current.displayName} current`} /></figure><figure><figcaption>Selected · v{active.revisionNumber ?? 1}</figcaption><img src={active.contentUrl} alt={`${active.displayName} selected`} /></figure></div> : <div className="image-revision-canvas"><img src={active.contentUrl} alt={active.displayName} /><AssetReviewPins key={active.id} assetId={active.id} assetLabel={`${active.displayName} v${active.revisionNumber ?? 1}`} onNotesChanged={setReviewNotes} onError={setError} /><span>v{active.revisionNumber ?? 1} · {active.width} × {active.height}</span></div>}<div className="image-revision-actions">{!activeIsCurrent && <button className="primary" onClick={() => void makeCurrent()} disabled={busy}><Check size={16} />Choose this revision</button>}<button className="secondary" onClick={generateRevision}><PenLine size={16} />{openReviewNotes.length > 0 ? `Regenerate from ${openReviewNotes.length} note${openReviewNotes.length === 1 ? '' : 's'}` : 'Edit with directions'}</button><a className="secondary button-link" href={active.contentUrl} download={active.originalFileName}><Download size={16} />Download</a><button className="danger-text" onClick={() => void archive()} disabled={busy}><Archive size={15} />Archive revision</button></div></section>
      <aside className="image-revision-inspector" aria-label="Image asset details"><div className="image-inspector-heading"><p className="eyebrow">Image inspector</p><strong>Identity and use</strong></div><label>Name<input value={name} onChange={event => setName(event.target.value)} /></label><label>Collection<select value={collectionId} onChange={event => setCollectionId(event.target.value)}><option value="">Unfiled</option>{collections.map(collection => <option key={collection.id} value={collection.id}>{collection.name}</option>)}</select></label><label>Tags <small>comma separated</small><input value={tags} onChange={event => setTags(event.target.value)} /></label><label>Notes<textarea value={notes} onChange={event => setNotes(event.target.value)} placeholder="Usage, continuity, or editorial notes" /></label><button className="secondary asset-save" onClick={() => void save()} disabled={busy || !name.trim()}>Save details</button><section className="revision-provenance"><h3>Revision provenance</h3><dl><div><dt>Route</dt><dd>{active.revisionEngine || active.source}</dd></div><div><dt>Created</dt><dd>{new Date(active.createdAt).toLocaleDateString()}</dd></div><div><dt>Content ID</dt><dd>{active.contentHash.slice(0, 12)}…</dd></div></dl>{active.revisionPrompt && <p>{active.revisionPrompt}</p>}</section><section className="revision-production"><h3>Use in production</h3><div className="asset-shot-picker"><select value={shotId} onChange={event => setShotId(event.target.value)}>{shots.map(shot => <option key={shot.id} value={shot.id}>{shot.code} · {shot.title}</option>)}</select><button onClick={() => void place()} disabled={busy || !shotId}><Plus size={15} />Add</button></div><details className="authority-promotion-disclosure"><summary className="secondary promote-authority"><BadgeCheck size={16} />Promote to authority</summary><div className="authority-promotion"><p>Freeze this exact image and its rules as reusable canon.</p><label>What this reference controls<select value={authorityCategory} onChange={event => setAuthorityCategory(event.target.value)}><option>Character</option><option>Role</option><option>Wardrobe</option><option>Pose</option><option>Location</option><option>Architecture</option><option>Prop</option><option>Style</option><option>World</option></select></label><label>Identity and context<textarea value={authorityDescription} onChange={event => setAuthorityDescription(event.target.value)} /></label><label>Locked constraint<textarea value={authorityConstraint} onChange={event => setAuthorityConstraint(event.target.value)} /></label><button className="primary" onClick={() => void promote()} disabled={busy || !name.trim() || !authorityDescription.trim() || !authorityConstraint.trim()}>Create immutable authority</button></div></details></section>{error && <p className="form-error" role="alert">{error}</p>}</aside>
    </div>
  </main>
}

function AssetInspector({ asset, collections, placements, originJob, shots, onClose, onSaved, onPlacementsChanged, onOpenGeneration, onOpenSequence }: { asset: AssetSummary; collections: AssetCollectionSummary[]; placements: AssetPlacementSummary[]; originJob?: JobSummary; shots: ShotSummary[]; onClose: () => void; onSaved: (asset: AssetSummary) => void; onPlacementsChanged: () => void; onOpenGeneration: () => void; onOpenSequence: () => void }) {
  const [name, setName] = useState(asset.displayName); const [collectionId, setCollectionId] = useState(asset.collectionId ?? ''); const [tagText, setTagText] = useState(asset.tags.join(', ')); const [notes, setNotes] = useState(asset.notes); const [shotId, setShotId] = useState(shots[0]?.id ?? ''); const [busy, setBusy] = useState(false); const [error, setError] = useState<string>()
  useEffect(() => { setName(asset.displayName); setCollectionId(asset.collectionId ?? ''); setTagText(asset.tags.join(', ')); setNotes(asset.notes); setError(undefined) }, [asset])
  const tags = tagText.split(',').map(item => item.trim()).filter(Boolean)
  const save = async () => { setBusy(true); setError(undefined); try { onSaved(await studioApi.updateAsset(asset.id, { displayName: name.trim(), collectionId: collectionId || undefined, tags, notes })) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not save asset details.') } finally { setBusy(false) } }
  const place = async () => { if (!shotId) return; setBusy(true); setError(undefined); try { const role = asset.kind === 'Image' ? 'Image guide' : asset.kind === 'Video' ? 'Video take' : 'Audio cue'; await studioApi.placeAsset(asset.id, shotId, role); onPlacementsChanged() } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not add this asset to the shot.') } finally { setBusy(false) } }
  const archive = async () => { setBusy(true); try { onSaved(asset.isArchived ? await studioApi.restoreAsset(asset.id) : await studioApi.archiveAsset(asset.id)); onClose() } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not update archive state.') } finally { setBusy(false) } }
  return <aside className="asset-inspector" aria-label={`${asset.displayName} details`}>
    <header><span><small>{asset.kind} · {asset.source}</small><strong>Asset details</strong></span><button onClick={onClose} aria-label="Close asset details"><X /></button></header>
    <div className="asset-inspector-scroll">
      <div className={`asset-preview ${asset.kind.toLowerCase()}`}>{asset.kind === 'Image' ? <img src={asset.contentUrl} alt={asset.displayName} /> : asset.kind === 'Video' ? <video controls src={asset.contentUrl} /> : <audio controls src={asset.contentUrl} />}</div>
      <label>Name<input value={name} maxLength={120} onChange={event => setName(event.target.value)} /></label>
      <label>Collection<select value={collectionId} onChange={event => setCollectionId(event.target.value)}><option value="">Unfiled</option>{collections.map(collection => <option key={collection.id} value={collection.id}>{collection.name}</option>)}</select></label>
      <label>Tags <small>comma separated</small><input value={tagText} onChange={event => setTagText(event.target.value)} placeholder="character, exterior, tension" /></label>
      <label>Notes<textarea value={notes} maxLength={2000} onChange={event => setNotes(event.target.value)} placeholder="Usage, licensing, continuity, or editorial notes" /></label>
      <dl className="asset-facts"><div><dt>Source file</dt><dd>{asset.originalFileName}</dd></div><div><dt>Size</dt><dd>{formatSize(asset.bytes)}</dd></div>{asset.width && <div><dt>Dimensions</dt><dd>{asset.width} × {asset.height}</dd></div>}{asset.durationSeconds && <div><dt>Duration</dt><dd>{formatDuration(asset.durationSeconds)}</dd></div>}<div><dt>Content ID</dt><dd>{asset.contentHash.slice(0, 12)}…</dd></div></dl>
      <button className="primary asset-save" onClick={() => void save()} disabled={busy || !name.trim()}>{busy ? 'Saving…' : 'Save details'}</button>
      <section className="asset-use-section"><h3>Use in production</h3>{asset.kind === 'Image' && <button className="secondary" onClick={onOpenGeneration}><PenLine size={16} />Create variation in image lab</button>}{asset.kind === 'Audio' && <button className="secondary" onClick={onOpenSequence}><Music2 size={16} />Open audio timeline</button>}
        <div className="asset-shot-picker"><select aria-label="Choose shot for asset" value={shotId} onChange={event => setShotId(event.target.value)}>{shots.map(shot => <option key={shot.id} value={shot.id}>{shot.code} · {shot.title}</option>)}</select><button onClick={() => void place()} disabled={busy || !shotId}><Plus size={15} />Add</button></div>
        <div className="asset-placements">{placements.length === 0 ? originJob ? <div className="generated-asset-origin"><Clapperboard size={14} /><span><strong>{originJob.shotCode}</strong><small>Generated review take · {originJob.backend}</small></span></div> : <p>Not attached to a shot yet.</p> : placements.map(placement => <div key={placement.id}><Clapperboard size={14} /><span><strong>{placement.shotCode}</strong><small>{placement.role}</small></span><button aria-label={`Remove from ${placement.shotCode}`} onClick={async () => { await studioApi.removeAssetPlacement(placement.id); onPlacementsChanged() }}><X size={14} /></button></div>)}</div>
      </section>
      <button className="asset-archive" onClick={() => void archive()} disabled={busy}><Archive size={15} />{asset.isArchived ? 'Restore to library' : 'Archive asset'}</button>
      {error && <p className="form-error" role="alert">{error}</p>}
    </div>
  </aside>
}

function CreateAssetLauncher({ references, onClose, onImport, onOpenGeneration }: { references: AssetGenerationReference[]; onClose: () => void; onImport: () => void; onOpenGeneration: (draft: AssetGenerationDraft) => void; onJobQueued: (job: JobSummary) => void }) {
  const [mode, setMode] = useState<'image' | 'music'>('image')
  const [name, setName] = useState('New visual asset')
  const [prompt, setPrompt] = useState('')
  const [route, setRoute] = useState<'fast' | 'precision'>('fast')
  const openImageLab = () => onOpenGeneration({ id: crypto.randomUUID(), name: name.trim(), initialPrompt: prompt.trim(), route, references })
  return <Dialog className="edit-dialog create-asset-dialog" onClose={onClose} labelledBy="create-asset-title">
    <header><div><p className="eyebrow">Create into library</p><h2 id="create-asset-title">New asset</h2></div><button onClick={onClose} aria-label="Close asset creator">×</button></header>
    <div className="edit-dialog-body">
      <div className="create-asset-tabs" role="tablist"><button role="tab" aria-selected={mode === 'image'} onClick={() => setMode('image')}><ImagePlus />Image</button><button role="tab" aria-selected={mode === 'music'} onClick={() => setMode('music')}><Music2 />Music</button><button onClick={onImport}><Upload />Import</button></div>
      {mode === 'image' ? <div className="image-workshop">
        <div className="asset-lab-callout"><WandSparkles /><span><strong>Start a standalone still image</strong><small>Describe it, draw, add text, or bring an underlay. Every result returns to one tidy revision stack in Assets.</small></span></div>
        <label>Asset name<input value={name} maxLength={120} onChange={event => setName(event.target.value)} /></label>
        <label>Starting direction <small>optional</small><textarea value={prompt} onChange={event => setPrompt(event.target.value)} placeholder="Describe the character, location, prop, wardrobe, or reusable visual." /></label>
        <div className="provider-route"><button type="button" className={route === 'fast' ? 'active' : ''} onClick={() => setRoute('fast')}><span><strong>Fast local draft</strong><small>Use the selected ComfyUI workflow</small></span></button><button type="button" className={route === 'precision' ? 'active' : ''} onClick={() => setRoute('precision')}><span><strong>Precision image</strong><small>Choose Codex or direct OpenAI next</small></span></button></div>
        <p className="asset-creation-note"><BadgeCheck size={15} />Next: assign exact references, generate a working revision, compare it, then choose the version worth keeping.</p>
      </div> : <div className="music-workshop"><div className="music-engine"><span><Music2 size={15} /><strong>YuE2 composition studio <em className="preview-tag">Editable</em></strong></span><small>Intent → lyrics/structure → ABC score → render</small></div><p className="asset-creation-note"><BadgeCheck size={15} />Music is now composed and versioned in Sequence → Music, where every score edit creates a recoverable revision. Inspect lyrics, sections, tempo, key, chords, melody intent, and advanced ABC before rendering.</p><p className="music-protected">Close this dialog, open Sequence, then choose Music composition. Existing imported and legacy generated audio remains in Assets.</p></div>}
    </div>
    <footer><button className="secondary" onClick={onClose}>Cancel</button>{mode === 'image' ? <button className="primary" disabled={!name.trim()} onClick={openImageLab}>Open still-image studio<ArrowRight size={16} /></button> : <button className="primary" onClick={onClose}>Go to Sequence → Music<ArrowRight size={16} /></button>}</footer>
  </Dialog>
}

export function CreateAssetDialog({ shots, onClose, onImport, onOpenSketch }: { shots: ShotSummary[]; onClose: () => void; onImport: () => void; onOpenSketch: (shotId: string, launch: { prompt?: string; route?: 'fast' | 'precision'; underlayAssetId?: string }) => void }) {
  const [mode, setMode] = useState<'image' | 'music'>('image'); const [imagePath, setImagePath] = useState<'sketch' | 'generate'>('sketch'); const [shotId, setShotId] = useState(shots[0]?.id ?? ''); const [route, setRoute] = useState<'fast' | 'precision'>('fast'); const [prompt, setPrompt] = useState('')
  return <Dialog className="edit-dialog create-asset-dialog" onClose={onClose} labelledBy="create-asset-title">
    <header><div><p className="eyebrow">Create into library</p><h2 id="create-asset-title">New asset</h2></div><button onClick={onClose} aria-label="Close asset creator">×</button></header>
    <div className="edit-dialog-body"><div className="create-asset-tabs" role="tablist"><button role="tab" aria-selected={mode === 'image'} onClick={() => setMode('image')}><ImagePlus />Image</button><button role="tab" aria-selected={mode === 'music'} onClick={() => setMode('music')}><Music2 />Music</button><button onClick={onImport}><Upload />Import</button></div>
      {mode === 'image' ? <div className="image-workshop"><div className="image-path-cards"><button className={imagePath === 'sketch' ? 'active' : ''} onClick={() => setImagePath('sketch')}><PenLine /><span><strong>Start on canvas</strong><small>Draw blocking, add text, or bring an underlay.</small></span></button><button className={imagePath === 'generate' ? 'active' : ''} onClick={() => setImagePath('generate')}><WandSparkles /><span><strong>Start with a prompt</strong><small>Open the composition lab with a provider route ready.</small></span></button></div><label>Shot context<select value={shotId} onChange={event => setShotId(event.target.value)}>{shots.map(shot => <option key={shot.id} value={shot.id}>{shot.code} · {shot.title}</option>)}</select></label>{imagePath === 'generate' && <><label>Image direction<textarea value={prompt} onChange={event => setPrompt(event.target.value)} placeholder="Describe the composition, blocking, camera, and intended beat." /></label><div className="provider-route"><button className={route === 'fast' ? 'active' : ''} onClick={() => setRoute('fast')}><span><strong>ComfyUI fast pass</strong><small>Rapid composition iteration</small></span></button><button className={route === 'precision' ? 'active' : ''} onClick={() => setRoute('precision')}><span><strong>GPT Image precision</strong><small>Slower, higher-fidelity finaling</small></span></button></div></>}<p className="asset-creation-note"><BadgeCheck size={15} />The sketchboard remains the composition authority. Nothing enters an approval slot until you review it.</p></div> : <div className="music-workshop"><div className="music-engine"><span><Music2 size={15} /><strong>YuE2 composition studio <em className="preview-tag">Editable</em></strong></span><small>Compose → inspect/edit → render</small></div><p className="asset-creation-note"><BadgeCheck size={15} />Music has moved to Sequence → Music, where every score edit creates a recoverable revision and each YuE2 render stays attached to that revision.</p><p className="music-protected">Close this dialog and open the Sequence workspace to compose. Audio already in this project remains available.</p></div>}</div>
    <footer><button className="secondary" onClick={onClose}>Cancel</button>{mode === 'image' ? <button className="primary" disabled={!shotId || (imagePath === 'generate' && !prompt.trim())} onClick={() => onOpenSketch(shotId, { prompt: imagePath === 'generate' ? prompt.trim() : undefined, route })}>{imagePath === 'sketch' ? 'Open sketchboard' : `Continue with ${route === 'fast' ? 'ComfyUI' : 'GPT Image'}`}<ArrowRight size={16} /></button> : <button className="primary" onClick={onClose}>Go to Sequence → Music<ArrowRight size={16} /></button>}</footer>
  </Dialog>
}

function CollectionDialog({ collection, onClose, onSaved }: { collection?: AssetCollectionSummary; onClose: () => void; onSaved: (collection: AssetCollectionSummary) => void }) {
  const [name, setName] = useState(collection?.name ?? ''); const [color, setColor] = useState(collection?.color ?? '#73b7cf'); const [busy, setBusy] = useState(false); const [error, setError] = useState<string>()
  const save = async () => { setBusy(true); setError(undefined); try { onSaved(collection ? await studioApi.updateAssetCollection(collection.id, { name, color }) : await studioApi.createAssetCollection({ name, color })) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not save collection.') } finally { setBusy(false) } }
  return <Dialog className="edit-dialog collection-dialog" onClose={onClose} labelledBy="collection-title"><header><div><p className="eyebrow">Manual bin</p><h2 id="collection-title">{collection ? 'Edit collection' : 'New collection'}</h2></div><button onClick={onClose}>×</button></header><div className="edit-dialog-body"><label>Name<input value={name} maxLength={80} onChange={event => setName(event.target.value)} /></label><label>Color<input type="color" value={color} onChange={event => setColor(event.target.value)} /></label>{error && <p className="form-error">{error}</p>}</div><footer><button className="secondary" onClick={onClose}>Cancel</button><button className="primary" disabled={busy || !name.trim()} onClick={() => void save()}>{busy ? 'Saving…' : 'Save collection'}</button></footer></Dialog>
}

function AuthorityLibrary({ references, query, onEdit }: { references: ReferenceSummary[]; query: string; onEdit: (reference: ReferenceSummary) => void }) {
  const visible = references.filter(reference => !query || [reference.name, reference.category, reference.description, reference.lockedConstraint].join(' ').toLowerCase().includes(query))
  return <div className="authority-library"><div className="authority-library-note"><BadgeCheck /><span><strong>Immutable canon lives here too</strong><small>Authorities remain versioned and locked; opening one creates a new approved version instead of editing history.</small></span></div><div className="authority-library-grid">{visible.map(reference => <article key={reference.id}><div className="authority-library-image">{reference.imageUrl ? <img src={reference.imageUrl} alt="" /> : <BadgeCheck />}</div><span><small>{reference.category} · v{reference.version}</small><strong>{reference.name}</strong><p>{reference.description}</p><em>{reference.lockedConstraint}</em></span><button className="secondary compact" onClick={() => onEdit(reference)}>Open authority</button></article>)}</div></div>
}

function AssetEmpty({ view, query, onCreate, onImport }: { view: LibraryView; query: string; onCreate: () => void; onImport: () => void }) {
  return <div className="asset-empty"><div><Folder /><Sparkles /></div><h2>{query ? 'No matching assets' : view === 'archived' ? 'Archive is empty' : 'This bin is ready'}</h2><p>{query ? 'Try a broader name, tag, note, or source.' : 'Import existing media or create a new image or music cue without leaving Framewright.'}</p>{!query && <span><button className="primary" onClick={onCreate}><Plus size={16} />Create new</button><button className="secondary" onClick={onImport}><Upload size={16} />Import</button></span>}</div>
}

function formatSize(bytes: number) { return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(bytes >= 10 * 1024 * 1024 ? 0 : 1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB` }
function formatDuration(seconds: number) { const minutes = Math.floor(seconds / 60); const rest = Math.round(seconds % 60); return `${minutes}:${String(rest).padStart(2, '0')}` }
