import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ArrowLeft, ArrowUpToLine, BadgeCheck, CopyCheck, LoaderCircle, LockKeyhole, Palette, RefreshCcw, Save, Sparkles, Trash2, Upload, Volume2, WandSparkles } from 'lucide-react'
import { studioApi } from '../api'
import type { AssetGenerationDraft, AssetGenerationReference, AssetReviewNoteSummary, AssetSummary, JobSummary, ReferenceSummary, ReferenceVersionSummary, StudioSnapshot, VoiceAuditionSummary, VoiceProfileSummary } from '../types'
import Artwork from './Artwork'
import AssetReviewPins from './AssetReviewPins'

const categories = ['Character', 'Role', 'Wardrobe', 'Pose', 'Location', 'Architecture', 'Prop', 'Style', 'World']

const categoryGuidance: Record<string, string> = {
  Character: 'Preserves this exact person: face, identity, and defining traits.',
  Role: 'Preserves outfit, equipment, and demeanor. Repeated pins generate distinct people.',
  Wardrobe: 'Transfers clothing and accessories without preserving the wearer.',
  Pose: 'Transfers blocking and body position without preserving appearance.',
  Location: 'Preserves the approved place and its defining geography.',
  Architecture: 'Preserves a structure and its defining construction details.',
  Prop: 'Preserves the approved object design.',
  Style: 'Applies the visual language globally; it is not placed in-frame.',
  World: 'Carries universal canon and world rules into every generation.',
}

export default function AuthorityWorkspace({ studio, authority, onBack, onOpenGeneration, onJobQueued, onChanged, onDeleted }: {
  studio: StudioSnapshot
  authority: ReferenceSummary
  onBack: () => void
  onOpenGeneration: (draft: AssetGenerationDraft, revision: { description: string; lockedConstraint: string }) => void
  onJobQueued: (job: JobSummary) => void
  onChanged: (reference: ReferenceSummary, message: string) => void
  onDeleted: (message: string) => void
}) {
  const [versions, setVersions] = useState<ReferenceVersionSummary[]>([])
  const [selectedVersion, setSelectedVersion] = useState(authority.version)
  const [name, setName] = useState(authority.name)
  const [category, setCategory] = useState(authority.category)
  const [accent, setAccent] = useState(authority.accent)
  const [description, setDescription] = useState(authority.description)
  const [lockedConstraint, setLockedConstraint] = useState(authority.lockedConstraint)
  const [assets, setAssets] = useState<AssetSummary[]>([])
  const [voiceProfiles, setVoiceProfiles] = useState<VoiceProfileSummary[]>([])
  const [castingOpen, setCastingOpen] = useState(false)
  const [voiceBusy, setVoiceBusy] = useState(false)
  const [voiceDirection, setVoiceDirection] = useState('')
  const [calibrationText, setCalibrationText] = useState('I keep my word when the sky is dark. Listen closely; every choice leaves a mark, and every silence has a cost.')
  const [auditions, setAuditions] = useState<VoiceAuditionSummary[]>([])
  const [voiceJob, setVoiceJob] = useState<JobSummary>()
  const [reviewNotes, setReviewNotes] = useState<AssetReviewNoteSummary[]>([])
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [fileDragging, setFileDragging] = useState(false)
  const [error, setError] = useState<string>()
  const file = useRef<HTMLInputElement>(null)
  const dragDepth = useRef(0)
  const loadEpoch = useRef(0)
  const observedAuthorityVersion = useRef(authority.version)
  const authorityIdentity = useRef({ id: authority.id, version: authority.version })
  const loadedVoiceResult = useRef<string | undefined>(undefined)
  if (authorityIdentity.current.id !== authority.id)
    authorityIdentity.current = { id: authority.id, version: authority.version }

  useEffect(() => {
    const latest = studio.jobs
      .filter(job => job.adapterId === `qwen3-tts.design:${authority.id}`)
      .sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0]
    if (latest && (!voiceJob || latest.id !== voiceJob.id || latest.state !== voiceJob.state || latest.progress !== voiceJob.progress))
      setVoiceJob(latest)
  }, [authority.id, studio.jobs, voiceJob])

  useEffect(() => {
    if (!voiceJob) return
    if (voiceJob.state === 'Queued' || voiceJob.state === 'Running') {
      setVoiceBusy(true)
      return
    }
    if (voiceJob.state === 'Completed' && loadedVoiceResult.current !== voiceJob.id) {
      loadedVoiceResult.current = voiceJob.id
      void studioApi.voiceAuditionResults(voiceJob.id)
        .then(result => { setAuditions(result); setVoiceBusy(false) })
        .catch(reason => { setError(reason instanceof Error ? reason.message : 'Could not load the completed voice auditions.'); setVoiceBusy(false) })
      return
    }
    if (voiceJob.state === 'Failed' || voiceJob.state === 'Cancelled') {
      setError(voiceJob.error || 'The voice audition job stopped. Retry it from the jobs panel.')
      setVoiceBusy(false)
    }
  }, [voiceJob])

  const refreshVersions = useCallback(async (prefer?: number) => {
    const epoch = ++loadEpoch.current
    const items = await studioApi.referenceVersions(authority.id)
    if (epoch !== loadEpoch.current) return
    setVersions(items)
    setSelectedVersion(prefer ?? authority.version)
    setLoading(false)
  }, [authority.id, authority.version])

  // Version-changing actions refresh this list themselves. Reloading again when
  // the parent receives the new head can race a later select/delete and restore
  // stale history over the artist's most recent action.
  useEffect(() => {
    const initialVersion = authorityIdentity.current.version
    observedAuthorityVersion.current = initialVersion
    const epoch = ++loadEpoch.current
    let current = true
    setLoading(true); setError(undefined)
    Promise.all([studioApi.referenceVersions(authority.id), studioApi.assets(), studioApi.voiceProfiles()])
      .then(([history, media, voices]) => { if (current && epoch === loadEpoch.current) { setVersions(history); setAssets(media); setVoiceProfiles(voices); setSelectedVersion(initialVersion) } })
      .catch(reason => { if (current && epoch === loadEpoch.current) setError(reason instanceof Error ? reason.message : 'Could not open this authority.') })
      .finally(() => { if (current && epoch === loadEpoch.current) setLoading(false) })
    return () => { current = false }
  }, [authority.id])

  useEffect(() => {
    const previousHead = observedAuthorityVersion.current
    if (previousHead === authority.version) return

    observedAuthorityVersion.current = authority.version
    const wasViewingLive = selectedVersion === previousHead
    // Background generators attach a new immutable authority version after the
    // workspace has already mounted. Refresh the stack when that head changes;
    // follow the new result only when the artist was viewing the former live
    // version, and preserve an intentional historical-version selection.
    void refreshVersions(wasViewingLive ? authority.version : selectedVersion)
  }, [authority.version, refreshVersions, selectedVersion])

  useEffect(() => {
    setName(authority.name); setCategory(authority.category); setAccent(authority.accent)
    setVoiceDirection(`Design an original synthetic production voice for ${authority.name}. ${authority.description} Controlled cinematic delivery, natural pacing, clear diction, and no imitation of a real person.`)
    setAuditions([]); setCastingOpen(false)
  }, [authority.accent, authority.category, authority.description, authority.id, authority.name])
  useEffect(() => {
    if (selectedVersion === authority.version) { setDescription(authority.description); setLockedConstraint(authority.lockedConstraint) }
  }, [authority.description, authority.lockedConstraint, authority.version, selectedVersion])

  const selected = versions.find(item => item.version === selectedVersion)
  const characterVoiceProfiles = authority.category === 'Character' ? voiceProfiles.filter(profile => profile.characterReferenceId === authority.id).sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt)) : []
  const voiceProfile = characterVoiceProfiles[0]
  const openReviewNotes = reviewNotes.filter(note => note.state === 'Open')
  const isLive = selectedVersion === authority.version
  const identityDirty = name.trim() !== authority.name || category !== authority.category || accent.toLowerCase() !== authority.accent.toLowerCase()
  const contentDirty = selected ? description.trim() !== selected.description || lockedConstraint.trim() !== selected.lockedConstraint : false

  const selectVersion = (version: ReferenceVersionSummary) => {
    setSelectedVersion(version.version); setDescription(version.description); setLockedConstraint(version.lockedConstraint); setError(undefined)
  }

  const run = async (action: () => Promise<void>) => {
    setBusy(true); setError(undefined)
    try { await action() } catch (reason) { setError(reason instanceof Error ? reason.message : 'That authority change could not be completed.') }
    finally { setBusy(false) }
  }

  const saveIdentity = () => run(async () => {
    const saved = await studioApi.updateReference(authority.id, { name: name.trim(), category, accent })
    onChanged(saved, `${saved.name} details saved.`)
  })

  const saveRevision = (imageAssetId?: string) => run(async () => {
    const saved = await studioApi.createReferenceVersion(authority.id, {
      expectedVersion: authority.version,
      description: description.trim(),
      lockedConstraint: lockedConstraint.trim(),
      imageAssetId: imageAssetId ?? selected?.imageAssetId,
    })
    await refreshVersions(saved.version)
    onChanged(saved, `${saved.name} v${saved.version} added to the top of the authority stack.`)
  })

  const importImage = async (picked?: File) => {
    if (!picked) return
    if (!['image/png', 'image/jpeg'].includes(picked.type)) { setError('Drop a PNG or JPEG image.'); return }
    await run(async () => {
      const asset = await studioApi.uploadImage(picked, picked.name)
      const saved = await studioApi.createReferenceVersion(authority.id, { expectedVersion: authority.version, description: description.trim(), lockedConstraint: lockedConstraint.trim(), imageAssetId: asset.id })
      await refreshVersions(saved.version)
      onChanged(saved, `${picked.name} imported as ${saved.name} v${saved.version}.`)
    })
    if (file.current) file.current.value = ''
  }

  const dragEnter = (event: React.DragEvent) => { if (!event.dataTransfer.types.includes('Files')) return; event.preventDefault(); dragDepth.current += 1; setFileDragging(true) }
  const dragLeave = (event: React.DragEvent) => { event.preventDefault(); dragDepth.current = Math.max(0, dragDepth.current - 1); if (dragDepth.current === 0) setFileDragging(false) }
  const dropImage = (event: React.DragEvent) => { event.preventDefault(); dragDepth.current = 0; setFileDragging(false); void importImage(event.dataTransfer.files[0]) }

  const promote = () => selected && run(async () => {
    const saved = await studioApi.promoteReferenceVersion(authority.id, selected.version)
    await refreshVersions(saved.version)
    onChanged(saved, `v${selected.version} copied forward as live v${saved.version}.`)
  })

  // Promotes the authority's current head into the cross-project library. The
  // library copy is independent from here: later edits on either side do not reach
  // the other, which is what keeps a delivered frame explainable.
  const promoteToLibrary = () => run(async () => {
    const entry = await studioApi.promoteToLibrary(authority.id)
    onChanged(authority, `${entry.name} is in the authority library at v${entry.version}. Other projects can import it as a copy.`)
  })

  const removeVersion = () => selected && run(async () => {
    if (!window.confirm(`Delete ${authority.name} v${selected.version}? Generated work and placement notes can block this.`)) return
    const saved = await studioApi.deleteReferenceVersion(authority.id, selected.version)
    await refreshVersions(saved.version)
    onChanged(saved, `v${selected.version} removed. v${saved.version} is now live.`)
  })

  const removeAuthority = () => run(async () => {
    if (!window.confirm(`Delete the entire ${authority.name} authority and all of its revisions? This cannot be undone.`)) return
    await studioApi.deleteReference(authority.id)
    onDeleted(`${authority.name} was deleted.`)
  })

  const generationReferences = useMemo<AssetGenerationReference[]>(() => {
    const candidates: AssetGenerationReference[] = [
      ...studio.references.filter(item => item.imageAssetId && item.imageUrl).map(item => ({ id: `authority:${item.id}`, assetId: item.imageAssetId!, label: item.name, detail: `${item.category} · authority v${item.version}`, imageUrl: item.imageUrl!, source: 'Authority' as const })),
      ...studio.shots.filter(shot => shot.currentAssetId && shot.currentAssetUrl).map(shot => ({ id: `shot:${shot.id}`, assetId: shot.currentAssetId!, label: `${shot.code} · ${shot.title}`, detail: `${shot.stage} v${shot.version}`, imageUrl: shot.currentAssetUrl!, source: 'Shot' as const })),
      ...assets.filter(asset => asset.kind === 'Image' && !asset.isArchived).map(asset => ({ id: `asset:${asset.id}`, assetId: asset.id, label: asset.displayName, detail: asset.source, imageUrl: asset.contentUrl, source: 'Asset' as const })),
    ]
    const seen = new Set<string>()
    return candidates.filter(item => !seen.has(item.assetId) && Boolean(seen.add(item.assetId)))
  }, [assets, studio.references, studio.shots])

  const generate = () => {
    const pinnedDirections = openReviewNotes.length === 0 ? '' : `\n\nPINNED IMAGE REVISION NOTES (apply every note while preserving unmentioned content):\n${openReviewNotes.map((note, index) => `- Note ${index + 1} at ${Math.round(note.x * 100)}% across / ${Math.round(note.y * 100)}% down: ${note.body}`).join('\n')}`
    onOpenGeneration({
    id: crypto.randomUUID(), name: `${authority.name} authority revision`,
    initialPrompt: `Revise the selected ${authority.category.toLowerCase()} authority for ${authority.name}. Preserve everything that is not explicitly changed.\n\nSELECTED AUTHORITY: ${authority.name} v${selected?.version ?? authority.version}\n\nIdentity and context: ${description.trim()}\n\nLocked constraint: ${lockedConstraint.trim()}${pinnedDirections}\n\nMake this a clear reusable authority image rather than a one-off dramatic shot.`,
    route: 'fast', underlayAssetId: selected?.imageAssetId, destination: 'authority',
    references: generationReferences,
    revisionSource: {
      label: authority.name,
      version: selected?.version ?? authority.version,
      description: description.trim(),
      lockedConstraint: lockedConstraint.trim(),
      imageAssetId: selected?.imageAssetId,
      imageUrl: selected?.imageUrl,
      visualVariant: authority.visualVariant,
    },
    }, { description: description.trim(), lockedConstraint: lockedConstraint.trim() })
  }

  const designVoice = async () => {
    setVoiceBusy(true); setError(undefined)
    try {
      const job = await studioApi.designVoiceAuditions(authority.id, { direction: voiceDirection.trim(), calibrationText: calibrationText.trim(), count: 3 })
      setVoiceJob(job)
      onJobQueued(job)
      onChanged(authority, `${authority.name} voice auditions queued safely.`)
    }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not queue the local voice auditions.'); setVoiceBusy(false) }
  }

  const approveVoice = async (audition: VoiceAuditionSummary) => {
    setVoiceBusy(true); setError(undefined)
    try {
      const profile = await studioApi.createVoiceProfile({
        name: `${authority.name} Qwen voice v${characterVoiceProfiles.length + 1}`,
        kind: 'ProviderPreset', provider: 'Qwen3-TTS local', providerVoiceId: audition.providerVoiceId,
        characterName: authority.name, characterReferenceId: authority.id, sampleAssetId: audition.assetId,
        consentConfirmed: false, consentAttestation: '',
      })
      setVoiceProfiles(await studioApi.voiceProfiles()); setAuditions([]); setCastingOpen(false)
      onChanged(authority, `${profile.name} approved as ${authority.name}'s voice authority.`)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not approve that voice audition.') }
    finally { setVoiceBusy(false) }
  }

  return <main className="workspace authority-workspace">
    <aside className="authority-version-rail">
      <button className="authority-back" onClick={onBack}><ArrowLeft size={16} />Authorities</button>
      <div className="authority-rail-heading"><small>Version stack</small><strong>{authority.name}</strong><span>The top revision is what new work uses.</span></div>
      <div className="authority-version-list" role="listbox" aria-label={`${authority.name} versions`}>
        {versions.map(version => <button key={version.id} role="option" aria-selected={selectedVersion === version.version} className={`${selectedVersion === version.version ? 'active' : ''} ${version.version === authority.version ? 'live' : ''}`} onClick={() => selectVersion(version)}>
          <span className="authority-version-thumb">{version.imageUrl ? <img src={version.imageUrl} alt="" /> : <Artwork variant={authority.visualVariant} muted />}</span>
          <span><strong>v{version.version}</strong><small>{version.version === authority.version ? 'Live authority' : new Date(version.ratifiedAt).toLocaleDateString()}</small></span>
          {version.version === authority.version && <BadgeCheck size={15} />}
        </button>)}
      </div>
      <button className="authority-delete-all" onClick={() => void removeAuthority()} disabled={busy}><Trash2 size={15} />Delete authority</button>
    </aside>

    <section className="authority-stage">
      <header><div><p className="eyebrow">{authority.category} authority · v{selectedVersion}</p><h1>{authority.name}</h1></div><div className={`authority-live-badge ${isLive ? 'live' : ''}`}>{isLive ? <BadgeCheck size={15} /> : <CopyCheck size={15} />}{isLive ? 'Live authority' : 'Historical revision'}</div></header>
      <div className={`authority-canvas ${fileDragging ? 'file-drop-active' : ''}`} onDragEnter={dragEnter} onDragOver={event => { if (event.dataTransfer.types.includes('Files')) { event.preventDefault(); event.dataTransfer.dropEffect = 'copy' } }} onDragLeave={dragLeave} onDrop={dropImage}>
        {loading ? <LoaderCircle className="spin" /> : selected?.imageUrl ? <img src={selected.imageUrl} alt={`${authority.name} version ${selected.version}`} /> : <div className="authority-artwork"><Artwork variant={authority.visualVariant} label={`${authority.name} v${selectedVersion}`} /></div>}
        <AssetReviewPins assetId={selected?.imageAssetId} assetLabel={`${authority.name} v${selectedVersion}`} onNotesChanged={setReviewNotes} onError={setError} />
        {fileDragging && <div className="authority-drop-overlay" role="status"><Upload size={30} /><strong>Drop to create a new revision</strong><span>The current identity and locked constraint will travel with this image.</span></div>}
        <div className="authority-canvas-label"><span>{authority.name.toUpperCase()} · AUTHORITY v{selectedVersion}</span><span>{selected?.contentHash.slice(0, 12)}</span></div>
      </div>
      <div className="authority-stage-actions">
        <input ref={file} className="sr-only" type="file" accept="image/png,image/jpeg" aria-label="Choose authority image" onChange={event => void importImage(event.target.files?.[0])} />
        <button className="secondary" onClick={() => file.current?.click()} disabled={busy}><Upload size={17} />Import new image</button>
        <button className="primary" onClick={generate} disabled={busy || !description.trim() || !lockedConstraint.trim()}><Sparkles size={17} />{openReviewNotes.length > 0 ? `Regenerate from ${openReviewNotes.length} note${openReviewNotes.length === 1 ? '' : 's'}` : 'Generate new revision'}</button>
        {!isLive && <button className="primary promote" onClick={() => void promote()} disabled={busy}><CopyCheck size={17} />Make this the live authority</button>}
        {/* Promote-up. Always available, because reusability is usually discovered
            after the fact — an authority that already came from the library adds a
            version to it rather than creating a duplicate. */}
        <button className="secondary" onClick={() => void promoteToLibrary()} disabled={busy} title="Make this authority reusable in other projects">
          <ArrowUpToLine size={17} />Promote to library
        </button>
        {versions.length > 1 && <button className="danger" onClick={() => void removeVersion()} disabled={busy}><Trash2 size={16} />Delete v{selectedVersion}</button>}
      </div>
      {openReviewNotes.length > 0 && <p className="authority-review-summary">{openReviewNotes.length} open image note{openReviewNotes.length === 1 ? '' : 's'} will travel with the selected revision into the next generation.</p>}
    </section>

    <aside className="authority-inspector">
      <header><Palette size={17} /><span><small>Authority inspector</small><strong>Identity and constraints</strong></span></header>
      <div className="authority-inspector-scroll">
        <section><h3>Library identity</h3><label>Name<input value={name} maxLength={120} onChange={event => setName(event.target.value)} /></label><label>What this reference controls<select value={category} onChange={event => setCategory(event.target.value)}>{categories.map(value => <option key={value}>{value}</option>)}</select><small className="authority-category-guidance">{categoryGuidance[category]}</small></label><label>Board accent<span className="authority-color"><input type="color" value={accent} onChange={event => setAccent(event.target.value)} /><code>{accent}</code></span></label><button className="secondary" onClick={() => void saveIdentity()} disabled={busy || !identityDirty || !name.trim()}><Save size={15} />Save identity</button></section>
        <section><h3>Revision content</h3><p className="authority-inspector-note">Edits create a new top revision. Existing versions are never overwritten.</p><label>Identity and context<textarea value={description} maxLength={1600} onChange={event => setDescription(event.target.value)} /></label><label>Locked constraint<textarea value={lockedConstraint} maxLength={800} onChange={event => setLockedConstraint(event.target.value)} /></label><p className="authority-inspector-note">For asymmetric details, name the subject's anatomical side: for example, "small cut through anatomical left eyebrow" or "right forearm wristguard only."</p><div className="authority-lock-note"><LockKeyhole size={15} /><span>This exact text and image travel together in frozen generation manifests.</span></div><button className="primary" onClick={() => void saveRevision()} disabled={busy || !contentDirty || !description.trim() || !lockedConstraint.trim()}><BadgeCheck size={16} />Save as new revision</button></section>
        {authority.category === 'Character' && <section className="character-voice-authority voice-casting-authority">
          <div className="voice-section-title"><h3>Voice authority</h3><button className="text-button" onClick={() => setCastingOpen(value => !value)}>{castingOpen ? 'Close casting' : voiceProfile ? 'Recast' : 'Design voice'}</button></div>
          {voiceProfile ? <><div className="voice-authority-heading"><Volume2 size={17} /><span><strong>{voiceProfile.name}</strong><small>{voiceProfile.provider} · approved sample</small></span><BadgeCheck size={16} /></div>{voiceProfile.sampleAssetUrl && <audio controls preload="metadata" src={voiceProfile.sampleAssetUrl}>Approved voice sample for {authority.name}</audio>}<p className="authority-inspector-note">New dialogue uses this exact sample and calibration text. Recasting creates another version; it never overwrites this proof.</p></> : <div className="voice-authority-empty"><Volume2 size={17} /><span><strong>No voice cast yet</strong><small>Generate three local auditions here, then approve the one that belongs to this character.</small></span></div>}
          {castingOpen && <div className="voice-casting-panel">
            <div className="voice-local-badge"><WandSparkles size={15} /><span><strong>Qwen3-TTS · local</strong><small>Synthetic voice design · no API key</small></span></div>
            <label>How should this character sound?<textarea value={voiceDirection} maxLength={1000} onChange={event => setVoiceDirection(event.target.value)} /></label>
            <label>Audition line<textarea value={calibrationText} maxLength={150} onChange={event => setCalibrationText(event.target.value)} /><small>{calibrationText.trim().length}/150 · stored with the approved sample for consistent dialogue</small></label>
            <button className="primary voice-generate" onClick={() => void designVoice()} disabled={voiceBusy || voiceDirection.trim().length < 20 || calibrationText.trim().length < 20}>{voiceBusy ? <><LoaderCircle className="spin" size={16} />Casting locally…</> : <><RefreshCcw size={16} />Generate 3 auditions</>}</button>
            {voiceBusy && <p className="authority-inspector-note" role="status">The first local run loads the model and can take several minutes. You may keep working; no cloud call is being made.</p>}
            {auditions.length > 0 && <div className="voice-audition-list" aria-label="Voice auditions">{auditions.map((audition, index) => <article key={audition.assetId}><div><strong>Take {index + 1}</strong><small>Local seed {audition.seed}</small></div><audio controls preload="metadata" src={audition.assetUrl}>Voice audition {index + 1}</audio><button className="secondary" onClick={() => void approveVoice(audition)} disabled={voiceBusy}><BadgeCheck size={15} />Use this voice</button></article>)}</div>}
            <p className="voice-safety"><LockKeyhole size={14} />Original synthetic voices only. A real-person clone belongs in the consented voice workflow.</p>
          </div>}
        </section>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
    </aside>
  </main>
}
