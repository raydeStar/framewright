import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Aperture, ArrowLeft, ArrowRight, BadgeCheck, Blocks, Bot, Boxes, Check, ChevronDown, CircleAlert, CircleDot, CircleSlash, Clapperboard, Command, Download, Film, GalleryHorizontalEnd, Globe2, Grid2X2, Library, LoaderCircle, LockKeyhole, MessageCircle, RefreshCw, Search, Settings2, Sparkles, Unplug, WandSparkles, X } from 'lucide-react'
import { ApiError, studioApi } from './api'
import { getSupersededJobIds } from './jobState'
import Dialog from './components/Dialog'
import { AuthorityEditorDialog, ShotEditorDialog } from './components/ManageDialogs'
import AssetWorkspace from './components/AssetWorkspace'
import AuthorityWorkspace from './components/AuthorityWorkspace'
import ProjectSwitcher from './components/ProjectSwitcher'
import AuthorityLibraryDialog from './components/AuthorityLibraryDialog'
import WorldWorkspace from './components/WorldWorkspace'
const SceneWorkspace = lazy(() => import('./components/SceneWorkspace'))
import { BoardWorkspace, ReviewWorkspace, SequenceWorkspace, ShotWorkspace } from './components/StudioWorkspaces'
import { FRAMEWRIGHT_WEBMCP_TOOL_NAMES, registerFramewrightWebMcp } from './webmcp'
import type { AgentActivityEntry, AssetGenerationDraft, BackupStatus, CodexAssistResponse, CredentialStatus, DirectorViewQuery, IntegrationSummary, JobSummary, PairingStatusSummary, ProjectSummary, ReferenceSummary, RuntimeReadinessSummary, ShotRevisionProposalSummary, ShotSummary, StudioSnapshot, Workspace } from './types'

const SketchWorkspace = lazy(() => import('./components/SketchWorkspace'))

const workspaceItems: { id: Workspace; label: string; icon: React.ReactNode }[] = [
  { id: 'board', label: 'Board', icon: <Grid2X2 /> },
  { id: 'assets', label: 'Assets', icon: <Library /> },
  { id: 'world', label: 'World', icon: <Globe2 /> },
  { id: 'scene', label: 'Scene', icon: <Boxes /> },
  { id: 'shot', label: 'Shot', icon: <Aperture /> },
  { id: 'review', label: 'Review', icon: <GalleryHorizontalEnd /> },
  { id: 'sequence', label: 'Sequence', icon: <Clapperboard /> },
]

export default function App() {
  const [studio, setStudio] = useState<StudioSnapshot | null>(null)
  const [workspace, setWorkspace] = useState<Workspace>('board')
  const [boardView, setBoardView] = useState<'shots' | 'authorities'>('shots')
  const [selectedId, setSelectedId] = useState('')
  const [reviewCandidateId, setReviewCandidateId] = useState<string>()
  const [tool, setTool] = useState<'select' | 'draw' | 'comment'>('select')
  const [setupOpen, setSetupOpen] = useState(() => new URLSearchParams(window.location.search).get('setup') === '1')
  const [assistantOpen, setAssistantOpen] = useState(false)
  const [commandOpen, setCommandOpen] = useState(false)
  const [integrations, setIntegrations] = useState<IntegrationSummary[]>([])
  const [integrationBusy, setIntegrationBusy] = useState(false)
  const [codexBusy, setCodexBusy] = useState(false)
  const [codexResult, setCodexResult] = useState<CodexAssistResponse | null>(null)
  const [codexImplementation, setCodexImplementation] = useState<{ id: number; direction: string }>()
  const [assetGenerationDraft, setAssetGenerationDraft] = useState<AssetGenerationDraft>()
  const [assetLandingId, setAssetLandingId] = useState<string>()
  const [authorityGeneration, setAuthorityGeneration] = useState<{ authorityId: string; expectedVersion: number; description: string; lockedConstraint: string }>()
  const [selectedAuthorityId, setSelectedAuthorityId] = useState<string>()
  const [directorMode, setDirectorMode] = useState(false)
  const [displayedRevision, setDisplayedRevision] = useState<{ shotId: string; version: number; archived: boolean }>()
  const [commentPin, setCommentPin] = useState<{ x: number; y: number } | null>(null)
  const [commentText, setCommentText] = useState('')
  const [commentReference, setCommentReference] = useState<ReferenceSummary>()
  const [dismissedJobs, setDismissedJobs] = useState<string[]>([])
  const observedJobStates = useRef<Map<string, JobSummary['state']> | undefined>(undefined)
  const [toast, setToast] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [actionBusy, setActionBusy] = useState(false)
  const [entityEditor, setEntityEditor] = useState<{ kind: 'shot'; shot?: ShotSummary; initialImage?: File } | { kind: 'authority'; authority?: ReferenceSummary } | null>(null)
  const [pairing, setPairing] = useState<PairingStatusSummary>()
  const [needsPairing, setNeedsPairing] = useState(false)
  const [libraryOpen, setLibraryOpen] = useState(false)
  const [agentOpen, setAgentOpen] = useState(false)
  const [agentToolsEnabled, setAgentToolsEnabled] = useState(true)
  const [webMcpAvailable, setWebMcpAvailable] = useState(false)
  const [webMcpDetail, setWebMcpDetail] = useState('Checking browser support…')
  const [agentActivity, setAgentActivity] = useState<AgentActivityEntry[]>([])
  const [agentProposals, setAgentProposals] = useState<ShotRevisionProposalSummary[]>([])
  const selectedShotRef = useRef<{ id?: string; version?: number }>({})
  const directorViewRef = useRef<DirectorViewQuery | undefined>(undefined)
  const activitySequence = useRef(0)
  const proposalLoadSequence = useRef(0)
  const loadAgentProposalsRef = useRef<(() => Promise<void>) | undefined>(undefined)

  const refresh = useCallback(async (quiet = false) => {
    try {
      const next = await studioApi.snapshot(); setStudio(next); setSelectedId(current => current || next.shots[0]?.id || ''); if (!quiet) setError(null)
    } catch (e) { if (e instanceof ApiError && e.status === 401) setNeedsPairing(true); else if (!quiet) setError(e instanceof Error ? e.message : 'Could not load the studio.') }
  }, [])
  const acceptQueuedJob = useCallback((job: JobSummary) => {
    // The enqueue response is the first durable proof that work exists. Put it
    // into shared state now so JobsDock and the global poller follow it even if
    // the originating dialog closes before the next snapshot refresh.
    setStudio(current => current ? {
      ...current,
      jobs: [job, ...current.jobs.filter(existing => existing.id !== job.id)],
    } : current)
    setDismissedJobs(current => current.filter(id => id !== job.id))
  }, [])
  const followGeneration = useCallback(async (shotId: string, startingVersion: number) => {
    for (let attempt = 0; attempt < 80; attempt++) {
      await new Promise(resolve => window.setTimeout(resolve, 250))
      try {
        const next = await studioApi.snapshot(); setStudio(next)
        const shot = next.shots.find(item => item.id === shotId); const related = next.jobs.find(job => job.shotId === shotId)
        if ((shot && shot.version > startingVersion) || related?.state === 'Failed' || related?.state === 'Cancelled') return
      } catch { /* The ordinary refresh path owns the visible connection error. */ }
    }
  }, [])

  useEffect(() => { void refresh() }, [refresh])
  useEffect(() => {
    const running = studio?.jobs.some(x => x.state === 'Queued' || x.state === 'Running')
    if (!running) return
    const timer = window.setInterval(() => void refresh(true), 700)
    return () => window.clearInterval(timer)
  }, [studio?.jobs, refresh])
  useEffect(() => {
    if (!studio) return
    if (!observedJobStates.current) {
      observedJobStates.current = new Map(studio.jobs.map(job => [job.id, job.state]))
      return
    }
    for (const job of studio.jobs) {
      const previous = observedJobStates.current.get(job.id)
      if ((previous === 'Queued' || previous === 'Running') && job.state === 'Completed') {
        if (job.error) setError(`${job.shotCode}: ${job.error}`)
        else setToast(job.workType === 'Asset' ? `${job.shotCode} is ready in Assets.` : `${job.shotCode} ${job.kind.toLowerCase()} is ready to review.`)
        if (job.workType === 'Asset' && job.outputAssetId) setAssetLandingId(job.outputAssetId)
      }
      if ((previous === 'Queued' || previous === 'Running') && job.state === 'Failed') setError(`${job.shotCode}: ${job.error ?? 'Generation failed.'}`)
      observedJobStates.current.set(job.id, job.state)
    }
  }, [studio])
  useEffect(() => { if (!toast) return; const timer = window.setTimeout(() => setToast(null), 3200); return () => window.clearTimeout(timer) }, [toast])
  useEffect(() => {
    const keydown = (event: KeyboardEvent) => {
      const typing = event.target instanceof HTMLInputElement || event.target instanceof HTMLTextAreaElement
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') { event.preventDefault(); setCommandOpen(true); return }
      if (event.key === '/' && !typing) { event.preventDefault(); setCommandOpen(true); return }
      // Modal dialogs handle their own Escape via the native `cancel` event, so
      // only the non-modal assistant panel is dismissed here. Handling Escape
      // for all of them would close two layers on a single press.
      if (event.key === 'Escape' && assistantOpen) { setAssistantOpen(false); return }
      // Director Mode hides the rails, so Escape must lead back out — but only
      // once the modal on top of it has had its own press.
      if (event.key === 'Escape' && directorMode && !document.querySelector('dialog[open]')) setDirectorMode(false)
    }
    window.addEventListener('keydown', keydown)
    return () => window.removeEventListener('keydown', keydown)
  }, [assistantOpen, directorMode])
  // Leaving the shot would strand the artist in a full-view canvas that no
  // longer matches the workspace, so full view never outlives its subject.
  useEffect(() => { if (workspace !== 'shot') setDirectorMode(false) }, [workspace])

  const selected = studio?.shots.find(x => x.id === selectedId) ?? studio?.shots[0]
  selectedShotRef.current = { id: selected?.id, version: selected?.version }
  // Browser tools must describe the revision on screen, which is the archived
  // candidate whenever one is being previewed rather than the live head.
  const previewing = displayedRevision && displayedRevision.shotId === selected?.id && workspace === 'shot' ? displayedRevision : undefined
  directorViewRef.current = selected ? {
    shotId: selected.id,
    displayedVersion: previewing?.version ?? selected.version,
    archived: previewing?.archived ?? false,
    directorMode: directorMode && workspace === 'shot',
    tool,
  } : undefined
  const selectedAuthority = studio?.references.find(x => x.id === selectedAuthorityId)
  const comments = useMemo(() => studio?.comments.filter(x => x.shotId === selected?.id && x.version === selected.version && x.state === 'Open') ?? [], [studio, selected])
  const references = useMemo(() => studio?.references.filter(x => selected?.referenceIds.includes(x.id)) ?? [], [studio, selected])
  const webMcpReady = studio !== null
  const noteDisplayedRevision = useCallback((revision: { shotId: string; version: number; archived: boolean }) => setDisplayedRevision(current =>
    current && current.shotId === revision.shotId && current.version === revision.version && current.archived === revision.archived ? current : revision), [])
  // Applying is a validated server operation, not a UI shortcut: it refuses a
  // stale or undecided proposal, records the one moment it was applied, and
  // hands back frozen instructions for the ordinary revision surface. Nothing
  // here authorizes a provider; the artist's Generate action still does that.
  const applyProposal = useCallback(async (proposal: ShotRevisionProposalSummary) => {
    const result = await studioApi.webMcpApplyProposal(proposal.id)
    void loadAgentProposalsRef.current?.()
    if (!result.ok || !result.data) return result.message
    const instructions = result.data.instructions
    const preserved = instructions.preservedConstraints.length > 0
      ? `\n\nPRESERVE EXACTLY: ${instructions.preservedConstraints.join('; ')}.`
      : ''
    const targeted = instructions.targetedNotes.length > 0
      ? `\n\nTARGETED NOTES:\n${instructions.targetedNotes.map(note => `- at ${Math.round(note.x * 100)}% across / ${Math.round(note.y * 100)}% down: ${note.body}`).join('\n')}`
      : ''
    setSelectedId(instructions.shotId)
    setCodexImplementation({ id: Date.now(), direction: `${instructions.direction}${preserved}${targeted}` })
    setWorkspace('shot')
    setAgentOpen(false)
    setToast(`${instructions.code} direction applied. Nothing has been generated yet.`)
    return undefined
  }, [])
  const loadAgentProposals = useCallback(async () => {
    const sequence = ++proposalLoadSequence.current
    const result = await studioApi.webMcpProposals()
    if (sequence === proposalLoadSequence.current && result.ok && result.data) setAgentProposals(result.data)
  }, [])
  loadAgentProposalsRef.current = loadAgentProposals
  useEffect(() => {
    if (!webMcpReady) return
    if (!agentToolsEnabled) {
      setWebMcpAvailable(false)
      setWebMcpDetail('Browser tools are paused for this tab. Your storyboard remains fully usable.')
      return
    }
    const unregister = registerFramewrightWebMcp({
      getSelectedShotId: () => selectedShotRef.current.id,
      getSelectedShotVersion: () => selectedShotRef.current.version,
      getDirectorView: () => directorViewRef.current,
      onAvailability: (available, detail) => { setWebMcpAvailable(available); setWebMcpDetail(detail) },
      onActivity: (toolName, state, message) => setAgentActivity(current => [{ id: ++activitySequence.current, tool: toolName, state, message, at: new Date().toISOString() }, ...current].slice(0, 12)),
      onInspectShot: (shotId, continuity) => { setSelectedId(shotId); setWorkspace(continuity ? 'review' : 'shot'); setAgentOpen(true) },
      onProposal: proposal => { setAgentOpen(true); if (proposal) setAgentProposals(current => [proposal, ...current.filter(item => item.id !== proposal.id)]); else void loadAgentProposals() },
      onJobStatus: () => { setAgentOpen(true); void refresh(true) },
    })
    void loadAgentProposals()
    return unregister
  }, [webMcpReady, agentToolsEnabled, loadAgentProposals, refresh])

  const inspectIntegrations = async () => {
    setIntegrationBusy(true); setError(null)
    try { setIntegrations(await studioApi.integrations()) } catch (e) { setError(e instanceof Error ? e.message : 'Integration discovery failed.') }
    finally { setIntegrationBusy(false) }
  }
  const inspectPairing = async () => { try { setPairing(await studioApi.pairingStatus()) } catch { /* Setup can still show provider status. */ } }
  useEffect(() => { if (setupOpen && integrations.length === 0) void inspectIntegrations() }, [integrations.length, setupOpen])
  useEffect(() => { if (setupOpen) void inspectPairing() }, [setupOpen])

  const ratify = async () => {
    if (!selected || actionBusy) return
    setActionBusy(true)
    try { await studioApi.ratify(selected.id, selected.version); setToast(`${selected.code} v${selected.version} is approved and protected.`); await refresh() }
    catch (e) { setError(e instanceof Error ? e.message : 'Could not ratify this version.') }
    finally { setActionBusy(false) }
  }
  const addComment = async () => {
    if (!selected || !commentPin || !commentText.trim()) return
    try {
      const reference = commentReference
      await studioApi.comment(selected.id, { ...commentPin, body: commentText, referenceId: reference?.id })
      setCommentPin(null); setCommentText(''); setCommentReference(undefined); setTool('select')
      setToast(reference ? `${reference.name} v${reference.version} pinned to this frame.` : 'Feedback pinned to this exact version.')
      await refresh()
    }
    catch (e) { setError(e instanceof Error ? e.message : 'Could not save the comment.') }
  }
  const resolveComment = async (commentId: string) => {
    try { await studioApi.resolveComment(commentId); setToast('Feedback resolved. The approval gate has been updated.'); await refresh() }
    catch (e) { setError(e instanceof Error ? e.message : 'Could not resolve the comment.') }
  }
  const moveComment = async (commentId: string, x: number, y: number) => {
    try {
      const moved = await studioApi.moveComment(commentId, x, y)
      setStudio(current => current ? { ...current, comments: current.comments.map(comment => comment.id === moved.id ? moved : comment) } : current)
    } catch (e) { setError(e instanceof Error ? e.message : 'Could not move the feedback pin.') }
  }
  const askCodex = async (mode = 'Directing pass', message = 'Review this shot for camera language, continuity, pacing, and a clean first/last frame strategy.') => {
    if (!selected) return
    setCodexBusy(true); setAssistantOpen(true); setCodexResult(null)
    try { setCodexResult(await studioApi.codex(mode, message, selected.id)) }
    catch (e) { setError(e instanceof Error ? e.message : 'Codex assistance failed.') }
    finally { setCodexBusy(false) }
  }

  if (!studio && needsPairing) return <PairingGate onPaired={() => { setNeedsPairing(false); void refresh() }} />
  if (!studio && error) return <main className="load-failure"><div className="studio-mark"><Film /><span>F</span></div><p className="eyebrow">Local service unavailable</p><h1>Framewright could not open</h1><p>{error}</p><button className="primary" onClick={() => void refresh()}><RefreshCw size={17} />Try again</button></main>
  // Only an absent snapshot means "still loading". A snapshot with no shots is a
  // real, loaded, empty project — treating it as loading left it on the
  // skeleton forever, with no way to reach the empty state that explains it.
  if (!studio) return <StudioSkeleton />

  const activeShotJob = selected && studio.jobs.find(job => job.shotId === selected.id && (job.state === 'Queued' || job.state === 'Running'))
  const generationBusy = actionBusy || Boolean(activeShotJob)
  // Board, Assets, and World are project-level workspaces; the others need a shot.
  const active: Workspace = selected || workspace === 'assets' || workspace === 'world' || workspace === 'scene' || (workspace === 'authority' && selectedAuthority) ? workspace : 'board'
  const shotScoped = active === 'shot' || active === 'review' || active === 'sequence'

  return <div className={`app-shell${directorMode && active === 'shot' ? ' director-mode' : ''}`}>
    <a className="skip-link" href="#studio-workspace">Skip to workspace</a>
    <aside className="workspace-rail" aria-label="Workspaces">
      <button className="brand" onClick={() => setWorkspace('board')} aria-label="Framewright home" title="Framewright · Build every shot with intention"><div className="brand-glyph"><Film size={20} /></div><span>FRAME</span></button>
      <nav aria-label="Production workspaces">{workspaceItems.map(item => <button key={item.id} aria-current={active === item.id ? 'page' : undefined} disabled={!selected && !['board', 'assets', 'world', 'scene'].includes(item.id)} title={!selected && !['board', 'assets', 'world', 'scene'].includes(item.id) ? 'Add a shot to this sequence first.' : undefined} className={active === item.id ? 'active' : ''} onClick={() => setWorkspace(item.id)}>{item.icon}<span>{item.label}</span></button>)}</nav>
      <div className="rail-bottom"><button onClick={() => { setSetupOpen(true); void inspectIntegrations() }}><Blocks /><span>Connect</span><i className="online-dot" /></button><button onClick={() => { setSetupOpen(true); void inspectIntegrations() }}><Settings2 /><span>Settings</span></button></div>
    </aside>
    <section className={`app-main workspace-${active}`}>
      <header className="context-bar">
        <div className="project-lead"><button className="project-back" onClick={() => setWorkspace('board')} aria-label="Return to shot board"><ArrowLeft size={17} /></button><ProjectSwitcher project={studio.project} onError={setError} onSwitched={message => {
          // Switching replaces every entity on screen, so shot-scoped state is
          // dropped rather than carried into a project that does not contain it.
          setSelectedId(''); setSelectedAuthorityId(undefined); setReviewCandidateId(undefined)
          setAssetGenerationDraft(undefined); setAssetLandingId(undefined); setAuthorityGeneration(undefined)
          setCommentPin(null); setCommentReference(undefined); setCodexResult(null)
          setWorkspace('board'); setBoardView('shots')
          setToast(message); void refresh()
        }} /></div>
        <div className="context-center"><span>{studio.project.sequenceCode}</span><i />{shotScoped && selected && <><span>{selected.code}</span><i /></>}{active === 'authority' && selectedAuthority && <><span>{selectedAuthority.name}</span><i /></>}<strong>{active === 'authority' ? 'Authority' : workspaceItems.find(x => x.id === active)?.label}</strong></div>
        <div className="context-actions"><button className="agent-button" data-testid="agent-activity-button" onClick={() => { setAgentOpen(true); void loadAgentProposals() }}><Bot size={15} /><i className={webMcpAvailable ? 'online-dot' : 'offline-dot'} />Agent</button><button className="search-button" onClick={() => setCommandOpen(true)}><Search size={16} /><span>Find shot or view</span><kbd>Ctrl K</kbd></button><button className="icon-button" onClick={() => setCommandOpen(true)} aria-label="Open command palette"><Command size={17} /></button><button className="connection-button" onClick={() => { setSetupOpen(true); void inspectIntegrations() }}><CircleDot size={15} />Local studio</button><div className="avatar" aria-label="Signed in as AR">AR</div></div>
      </header>

      <div id="studio-workspace" className="workspace-host" tabIndex={-1}>
        {active === 'board' && <BoardWorkspace studio={studio} selectedId={selected?.id ?? ''} initialView={boardView} onViewChange={setBoardView} onSelect={setSelectedId} onOpen={id => { setSelectedId(id); setWorkspace('shot') }} onCreateShot={initialImage => setEntityEditor({ kind: 'shot', initialImage })} onCreateAuthority={() => setEntityEditor({ kind: 'authority' })} onEditAuthority={authority => { setBoardView('authorities'); setSelectedAuthorityId(authority.id); setWorkspace('authority') }} onOpenLibrary={() => setLibraryOpen(true)} onChanged={message => { if (message) setToast(message); void refresh() }} />}
        {active === 'assets' && !assetGenerationDraft && <AssetWorkspace studio={studio} initialAssetId={assetLandingId} onEditAuthority={authority => { setSelectedAuthorityId(authority.id); setWorkspace('authority') }} onOpenGeneration={setAssetGenerationDraft} onOpenSequence={() => setWorkspace('sequence')} onJobQueued={acceptQueuedJob} onToast={setToast} />}
        {active === 'assets' && assetGenerationDraft && <Suspense fallback={<WorkspaceLoading label="Opening image workspace" />}><SketchWorkspace key={assetGenerationDraft.id} shot={selected ?? assetFallbackShot(assetGenerationDraft)} references={studio.references} assetDraft={assetGenerationDraft} onOpenFrame={() => setAssetGenerationDraft(undefined)} onJobQueued={() => undefined} onAssetJobQueued={job => { acceptQueuedJob(job); setAssetGenerationDraft(undefined); setToast(`${job.shotCode} queued. You can keep working while Framewright renders it.`) }} /></Suspense>}
        {active === 'scene' && <Suspense fallback={<WorkspaceLoading label="Opening scenes" />}><SceneWorkspace onToast={setToast} /></Suspense>}
        {active === 'world' && <WorldWorkspace project={studio.project} onSaved={(saved, message) => { setStudio(current => current ? { ...current, project: saved } : current); setToast(message) }} />}
        {active === 'authority' && selectedAuthority && !assetGenerationDraft && <AuthorityWorkspace studio={studio} authority={selectedAuthority} onBack={() => { setBoardView('authorities'); setWorkspace('board') }} onOpenGeneration={(draft, revision) => { setAuthorityGeneration({ authorityId: selectedAuthority.id, expectedVersion: selectedAuthority.version, ...revision }); setAssetGenerationDraft(draft) }} onJobQueued={acceptQueuedJob} onChanged={(saved, message) => { setSelectedAuthorityId(saved.id); setToast(message); void refresh() }} onDeleted={message => { setSelectedAuthorityId(undefined); setBoardView('authorities'); setWorkspace('board'); setToast(message); void refresh() }} />}
        {active === 'authority' && selectedAuthority && assetGenerationDraft && authorityGeneration && <Suspense fallback={<WorkspaceLoading label="Opening authority workspace" />}><SketchWorkspace key={assetGenerationDraft.id} shot={selected ?? assetFallbackShot(assetGenerationDraft)} references={studio.references} assetDraft={assetGenerationDraft} authorityTarget={{ referenceId: authorityGeneration.authorityId, expectedVersion: authorityGeneration.expectedVersion, description: authorityGeneration.description, lockedConstraint: authorityGeneration.lockedConstraint }} onOpenFrame={() => { setAssetGenerationDraft(undefined); setAuthorityGeneration(undefined) }} onJobQueued={() => undefined} onAssetJobQueued={job => { acceptQueuedJob(job); setAssetGenerationDraft(undefined); setAuthorityGeneration(undefined); setToast(`${job.shotCode} queued. Its new authority version will attach automatically when ready.`) }} /></Suspense>}
        {active === 'shot' && selected && <ShotWorkspace studio={studio} shot={selected} comments={comments} references={references} tool={tool} setTool={setTool} directorMode={directorMode} onDirectorMode={setDirectorMode} onDisplayedRevision={noteDisplayedRevision} onAddComment={(x, y, reference) => { setCommentPin({ x, y }); setCommentReference(reference); setCommentText(reference ? `Use this approved reference for the subject or region marked here.` : '') }} onMoveComment={moveComment} onResolveReferencePin={resolveComment} onRatify={ratify} onOpenReview={candidateId => { setReviewCandidateId(candidateId); setWorkspace('review') }} onOpenAssets={() => setWorkspace('assets')} onEdit={() => setEntityEditor({ kind: 'shot', shot: selected })} onShotSaved={saved => { setStudio(current => current ? { ...current, shots: current.shots.map(item => item.id === saved.id ? saved : item) } : current); setToast(`${saved.code} intent saved${saved.version !== selected.version ? ` as working v${saved.version}` : ''}.`); void refresh(true) }} onShotContractSaved={(saved, message) => { setStudio(current => current ? { ...current, shots: current.shots.map(item => item.id === saved.id ? saved : item) } : current); setToast(message); void refresh(true) }} onCandidatesChanged={async message => { setToast(message); await refresh() }} onJobQueued={() => { setToast('Generation started. It will keep running if you leave this shot.'); void followGeneration(selected.id, selected.version) }} onAskCodex={() => void askCodex()} codexBusy={codexBusy} generationBusy={generationBusy} activeJob={activeShotJob} implementationRequest={codexImplementation} onImplementationConsumed={() => setCodexImplementation(undefined)} onSketchLaunchConsumed={() => undefined} />}
        {active === 'review' && selected && <ReviewWorkspace studio={studio} shot={selected} comments={comments} initialCandidateId={reviewCandidateId} onReturnToShot={() => setWorkspace('shot')} onResolveComment={id => void resolveComment(id)} onCandidatesChanged={async message => { setToast(message); await refresh() }} generationBusy={generationBusy} />}
        {active === 'sequence' && selected && <SequenceWorkspace studio={studio} selectedId={selected.id} onSelect={setSelectedId} onReordered={() => void refresh()} onJobQueued={acceptQueuedJob} />}
      </div>

      <JobsDock jobs={studio.jobs} scopeShotId={active === 'board' ? undefined : shotScoped ? selected?.id : null} onOpen={() => void refresh()} onRetry={async job => { try { const retry = await studioApi.retryJob(job.id); setToast(`${retry.shotCode} retry ${retry.attempt} queued.`); setDismissedJobs(current => current.filter(id => id !== job.id)); if (retry.workType === 'Shot') void followGeneration(retry.shotId, studio.shots.find(shot => shot.id === retry.shotId)?.version ?? 0); await refresh(true) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not retry this generation.') } }} dismissed={dismissedJobs} onDismiss={id => setDismissedJobs(current => [...current, id])} />
    </section>

    {setupOpen && <SetupDrawer project={studio.project} integrations={integrations} pairing={pairing} busy={integrationBusy} onProjectSaved={() => void refresh()} onRefresh={() => { void inspectIntegrations(); void inspectPairing() }} onRotatePairing={async () => setPairing(await studioApi.rotatePairing())} onRevokePairing={async () => setPairing(await studioApi.revokePairing())} onClose={() => setSetupOpen(false)} onAskCodex={() => { setSetupOpen(false); void askCodex('Integration setup', 'Inspect the configured integration status shown by Framewright and recommend the next safe setup steps. Do not change files or submit jobs.') }} />}
    {assistantOpen && selected && <AssistantDrawer shotCode={selected.code} busy={codexBusy} result={codexResult} onClose={() => setAssistantOpen(false)} onAsk={(message) => void askCodex('Directing pass', message)} onImplement={direction => { setCodexImplementation({ id: Date.now(), direction }); setAssistantOpen(false); setWorkspace('shot') }} />}
    {commentPin && selected && <CommentComposer code={selected.code} reference={commentReference} text={commentText} setText={setCommentText} onSave={() => void addComment()} onClose={() => { setCommentPin(null); setCommentReference(undefined); setTool('select') }} />}
    {commandOpen && <CommandPalette studio={studio} onClose={() => setCommandOpen(false)} onWorkspace={next => { setWorkspace(next); setCommandOpen(false) }} onShot={id => { setSelectedId(id); setWorkspace('shot'); setCommandOpen(false) }} />}
    {entityEditor?.kind === 'shot' && <ShotEditorDialog shot={entityEditor.shot} initialImage={entityEditor.initialImage} references={studio.references} suggestedCode={nextShotCode(studio.shots)} onClose={() => setEntityEditor(null)} onSaved={saved => { setEntityEditor(null); setSelectedId(saved.id); setWorkspace('shot'); setToast(`${saved.code} ${saved.currentAssetId ? 'image draft created' : 'intent saved'}.`); void refresh() }} />}
    {entityEditor?.kind === 'authority' && <AuthorityEditorDialog onClose={() => setEntityEditor(null)} onSaved={saved => { setEntityEditor(null); setBoardView('authorities'); setSelectedAuthorityId(saved.id); setWorkspace('authority'); setToast(`${saved.name} v${saved.version} created as authority.`); void refresh() }} />}
    {libraryOpen && <AuthorityLibraryDialog onClose={() => setLibraryOpen(false)} onError={setError} onChanged={message => { setToast(message); void refresh() }} />}
    {agentOpen && <AgentActivityPanel available={webMcpAvailable} enabled={agentToolsEnabled} detail={webMcpDetail} activity={agentActivity} proposals={agentProposals} shots={studio.shots} onToggleEnabled={() => setAgentToolsEnabled(value => !value)} onClose={() => setAgentOpen(false)} onRefresh={() => void loadAgentProposals()} onUpdated={() => void loadAgentProposals()} onApply={applyProposal} />}
    {toast && <div className="toast success" role="status" aria-live="polite"><Check size={17} />{toast}</div>}
    {error && <div className="toast error" role="alert"><CircleAlert size={17} /><span>{error}</span><button onClick={() => setError(null)} aria-label="Dismiss error"><X size={16} /></button></div>}
  </div>
}

function nextShotCode(shots: ShotSummary[]) {
  const next = Math.max(0, ...shots.map(shot => Number.parseInt(shot.code.match(/\d+/)?.[0] ?? '0', 10))) + 10
  return `SH-${String(next).padStart(3, '0')}`
}

function WorkspaceLoading({ label }: { label: string }) {
  return <main className="workspace workspace-loading" role="status"><LoaderCircle className="spin" /><p>{label}</p></main>
}

// Asset generation shares the composition workspace without pretending the
// result belongs to a shot. The fallback only supplies layout metadata to the
// shared editor when a project does not have any shots yet.
function assetFallbackShot(draft: AssetGenerationDraft): ShotSummary {
  return {
    id: draft.id,
    code: 'ASSET',
    title: draft.name,
    description: draft.initialPrompt,
    stage: 'Sketch',
    approval: 'Working',
    version: 1,
    durationFrames: 0,
    sortOrder: 0,
    visualVariant: 0,
    openComments: 0,
    continuityState: 'Not applicable',
    camera: 'Asset composition',
    action: '',
    referenceIds: [],
    constraints: [],
    updatedAt: new Date(0).toISOString(),
  }
}

/**
 * Shown while the first snapshot loads. Replaces a centred spinner: the shell
 * chrome is static and can be drawn immediately, so drawing it makes the app
 * feel like it is opening rather than stalling, and stops the whole layout
 * shifting into place when data lands.
 */
function StudioSkeleton() {
  return <div className="app-shell is-skeleton" role="status" aria-live="polite" data-testid="studio-skeleton">
    <span className="sr-only">Opening the studio</span>
    <aside className="workspace-rail" aria-hidden="true">
      <div className="brand"><div className="brand-glyph"><Film size={20} /></div><span>FRAME</span></div>
      <nav>{workspaceItems.map(item => <div key={item.id} className="skeleton-rail-item">{item.icon}<span>{item.label}</span></div>)}</nav>
    </aside>
    <section className="app-main">
      <header className="context-bar"><div className="project-identity"><div><span className="skeleton-line w-60" /><span className="skeleton-line w-120" /></div></div><div /><div /></header>
      <div className="workspace-host">
        <main className="workspace board-workspace" aria-hidden="true">
          <section className="board-heading"><div><span className="skeleton-line w-120" /><span className="skeleton-line w-260 tall" /></div></section>
          <section className="shot-wall">{Array.from({ length: 6 }, (_, index) => <div key={index} className="skeleton-card"><div className="skeleton-thumb" /><div className="skeleton-card-body"><span className="skeleton-line w-140" /><span className="skeleton-line w-full" /><span className="skeleton-line w-80" /></div></div>)}</section>
        </main>
      </div>
    </section>
  </div>
}

function JobsDock({ jobs, scopeShotId, onOpen, onRetry, dismissed, onDismiss }: { jobs: StudioSnapshot['jobs']; scopeShotId?: string | null; onOpen: () => void; onRetry: (job: JobSummary) => Promise<void>; dismissed: string[]; onDismiss: (id: string) => void }) {
  const [retrying, setRetrying] = useState(false)
  const underway = jobs.filter(x => x.state === 'Queued' || x.state === 'Running')
  const active = underway.find(x => x.state === 'Running') ?? underway[0]
  if (active) return <button className="jobs-dock active" onClick={onOpen} data-testid="jobs-dock" aria-label={`${active.shotCode} ${active.kind}: ${active.phase}, ${active.progress}%`}>
    <LoaderCircle className="spin" size={15} /><span><strong>{active.shotCode} · {active.kind}</strong><small>{active.phase}{underway.length > 1 ? ` · ${underway.length - 1} waiting safely` : ''}</small></span><div className="job-progress" role="progressbar" aria-label="Generation progress" aria-valuemin={0} aria-valuemax={100} aria-valuenow={active.progress}><i style={{ width: `${active.progress}%` }} /></div><em>{active.progress}%</em><ChevronDown size={14} />
  </button>

  // Terminal failures used to be invisible: this dock only ever rendered
  // Queued and Running, so a job that failed simply vanished and the artist was
  // left waiting for a render that was never coming. §18 of the handoff names
  // Blocked and Failed as first-class job states — this is where they surface.
  const superseded = getSupersededJobIds(jobs)
  const stalled = jobs
    .filter(job => (job.state === 'Failed' || job.state === 'Cancelled') && !superseded.has(job.id) && !dismissed.includes(job.id)
      && (scopeShotId === null ? job.workType !== 'Shot' : !scopeShotId || job.shotId === scopeShotId))
    .sort((a, b) => (b.completedAt ?? b.createdAt).localeCompare(a.completedAt ?? a.createdAt))[0]
  if (!stalled) return null
  const failed = stalled.state === 'Failed'
  return <div className={`jobs-dock stalled ${failed ? 'is-failed' : 'is-cancelled'}`} role="alert" data-testid="jobs-dock-stalled">
    {failed ? <CircleAlert size={15} /> : <CircleSlash size={15} />}
    <span>
      <strong>{stalled.shotCode} · {stalled.kind} {failed ? 'failed' : 'cancelled'}</strong>
      <small>{stalled.error || (failed ? 'The renderer did not return a candidate.' : 'This job was stopped before it finished.')}</small>
    </span>
    <span className="jobs-dock-attempt">Attempt {stalled.attempt}</span>
    <button onClick={() => { setRetrying(true); void onRetry(stalled).finally(() => setRetrying(false)) }} disabled={retrying} className="jobs-dock-action">{retrying ? 'Queuing…' : 'Retry'}</button>
    <button onClick={onOpen} className="jobs-dock-action secondary">Refresh</button>
    <button onClick={() => onDismiss(stalled.id)} aria-label="Dismiss job failure"><X size={15} /></button>
  </div>
}

function PairingGate({ onPaired }: { onPaired: () => void }) {
  const [code, setCode] = useState(''); const [busy, setBusy] = useState(false); const [error, setError] = useState<string>()
  const claim = async () => { setBusy(true); setError(undefined); try { await studioApi.claimPairing(code); onPaired() } catch (reason) { setError(reason instanceof Error ? reason.message : 'Pairing failed.') } finally { setBusy(false) } }
  return <main className="pairing-gate"><div className="studio-mark"><Film /><span>F</span></div><p className="eyebrow">Framewright tablet pairing</p><h1>Connect to this workstation</h1><p>Enter the eight-digit code shown in Framewright on the Windows workstation. Provider keys never leave that machine.</p><form onSubmit={event => { event.preventDefault(); void claim() }}><label>Pairing code<input autoFocus inputMode="numeric" autoComplete="one-time-code" maxLength={8} pattern="[0-9]{8}" value={code} onChange={event => setCode(event.target.value.replace(/\D/g, '').slice(0, 8))} /></label>{error && <span role="alert">{error}</span>}<button className="primary" disabled={busy || code.length !== 8}>{busy ? 'Pairing...' : 'Pair tablet'}</button></form></main>
}

const DELIVERY_PRESETS = [
  { id: 'scope-uhd', label: 'Cinema scope', detail: '3840 × 1608 · 2.39:1 · 24fps', aspectRatio: '2.39:1', deliveryWidth: 3840, deliveryHeight: 1608, framesPerSecond: 24 },
  { id: 'streaming-uhd', label: 'Streaming UHD', detail: '3840 × 2160 · 16:9 · 24fps', aspectRatio: '16:9', deliveryWidth: 3840, deliveryHeight: 2160, framesPerSecond: 24 },
  { id: 'streaming-hd', label: 'Streaming HD', detail: '1920 × 1080 · 16:9 · 24fps', aspectRatio: '16:9', deliveryWidth: 1920, deliveryHeight: 1080, framesPerSecond: 24 },
] as const

function SetupDrawer({ project, integrations, pairing, busy, onProjectSaved, onRefresh, onRotatePairing, onRevokePairing, onClose, onAskCodex }: { project: ProjectSummary; integrations: IntegrationSummary[]; pairing?: PairingStatusSummary; busy: boolean; onProjectSaved: () => void; onRefresh: () => void; onRotatePairing: () => void; onRevokePairing: () => void; onClose: () => void; onAskCodex: () => void }) {
  const [draft, setDraft] = useState(project)
  const [credential, setCredential] = useState<CredentialStatus>()
  const [backup, setBackup] = useState<BackupStatus>()
  const [runtime, setRuntime] = useState<RuntimeReadinessSummary>()
  const [apiKey, setApiKey] = useState('')
  const [saving, setSaving] = useState(false)
  const [setupError, setSetupError] = useState<string>()
  const [projectStatus, setProjectStatus] = useState<'saved' | 'dirty'>('saved')
  const projectDirty = projectStatus === 'dirty'
  const projectValid = Boolean(draft.name.trim() && draft.production.trim() && draft.sequenceCode.trim() && draft.sequenceName.trim() && draft.aspectRatio.trim() && draft.framesPerSecond >= 1 && draft.framesPerSecond <= 120 && draft.deliveryWidth >= 320 && draft.deliveryHeight >= 180 && draft.deliveryWidth % 2 === 0 && draft.deliveryHeight % 2 === 0 && ['Rec.709', 'Display P3 D65', 'Rec.2020'].includes(draft.colorSpace) && [44100, 48000, 96000].includes(draft.audioSampleRate))
  useEffect(() => { setDraft(project); setProjectStatus('saved') }, [project])
  useEffect(() => { if (pairing?.isLoopback) void studioApi.openAiCredentialStatus().then(setCredential).catch(() => undefined) }, [pairing?.isLoopback])
  const refreshOperationalStatus = useCallback(() => {
    void Promise.all([
      studioApi.backupStatus().then(setBackup),
      studioApi.runtimeStatus().then(setRuntime),
    ]).catch(() => undefined)
  }, [])
  useEffect(() => { refreshOperationalStatus() }, [refreshOperationalStatus])
  const saveProject = async () => { if (!projectDirty || !projectValid) return; setSaving(true); setSetupError(undefined); try { const saved = await studioApi.updateProject(draft); setDraft(saved); setProjectStatus('saved'); onProjectSaved() } catch (reason) { setSetupError(reason instanceof Error ? reason.message : 'Could not save project settings.') } finally { setSaving(false) } }
  const changeProject = (next: ProjectSummary) => { setDraft(next); setProjectStatus('dirty') }
  const saveCredential = async () => { setSaving(true); setSetupError(undefined); try { setCredential(await studioApi.saveOpenAiCredential(apiKey)); setApiKey(''); onRefresh() } catch (reason) { setSetupError(reason instanceof Error ? reason.message : 'Could not save the credential.') } finally { setSaving(false) } }
  const removeCredential = async () => { setSaving(true); setSetupError(undefined); try { setCredential(await studioApi.deleteOpenAiCredential()); onRefresh() } catch (reason) { setSetupError(reason instanceof Error ? reason.message : 'Could not remove the stored credential.') } finally { setSaving(false) } }
  // Focus the panel, not the first tabbable control — otherwise the first thing
  // a screen reader announces on open is "Close production setup".
  return <Dialog className="drawer-backdrop" onClose={onClose} labelledBy="setup-title" initialFocus="[data-dialog-focus]"><aside className="setup-drawer" data-testid="setup-drawer" data-dialog-focus tabIndex={-1}>
    <header><div><p className="eyebrow">Workstation connections</p><h2 id="setup-title">Production setup</h2><p>Credentials and render systems stay behind the local service.</p></div><button onClick={onClose} aria-label="Close production setup"><X /></button></header>
    <div className="setup-scroll"><div className="safety-banner"><BadgeCheck size={19} /><div><strong>Production guard is active</strong><p>Discovery is read-only. Studio cannot clear or interrupt jobs it does not own.</p></div></div>
    {runtime && <section className={`settings-card runtime-status-card state-${runtime.status.toLowerCase()}`}><div className="settings-card-title"><strong>Workstation readiness</strong><small>{runtime.status === 'Ready' ? 'Ready to accept durable generation work' : runtime.status === 'Degraded' ? 'Available with an operational warning' : 'New production work is blocked'}</small></div><div className="build-identity" data-testid="build-identity"><span><strong>{runtime.build.version}</strong><small>{runtime.build.channel.replaceAll('-', ' ')}</small></span><code title={runtime.build.commit}>{runtime.build.commit === 'unknown' ? 'commit unknown' : runtime.build.commit.slice(0, 12)}</code><time dateTime={runtime.build.builtAtUtc === 'unknown' ? undefined : runtime.build.builtAtUtc}>{runtime.build.builtAtUtc === 'unknown' ? 'unstamped development build' : `built ${new Date(runtime.build.builtAtUtc).toLocaleString()}`}</time></div><div className="runtime-queue"><span><strong>{runtime.queue.running}</strong><small>Running</small></span><span><strong>{runtime.queue.queued}</strong><small>Queued</small></span><span><strong>{runtime.queue.failed}</strong><small>Failed history</small></span></div><ul className="runtime-checks" tabIndex={0} aria-label="Runtime readiness checks">{runtime.checks.map(check => <li key={check.id} className={`state-${check.state.toLowerCase()}`}><span>{check.state === 'Ready' ? <Check size={14} /> : <Unplug size={14} />}</span><div><strong>{check.id.replaceAll('-', ' ')}</strong><small>{check.detail}</small></div></li>)}</ul></section>}
    {backup && <section className="settings-card backup-status-card"><div className="settings-card-title"><strong>Project safety copies</strong><small>{backup.scheduled ? 'Automatic daily protection is on' : 'Automatic protection is off'}</small></div><p>{backup.policy}</p><div className="backup-facts"><span><strong>{backup.databaseIntegrity === 'ok' ? 'Healthy' : backup.databaseIntegrity}</strong><small>Database check</small></span><span><strong>{backup.retainedBackups}</strong><small>Retained backups</small></span><span><strong>{backup.latestBackupAt ? new Date(backup.latestBackupAt).toLocaleString() : 'Not yet'}</strong><small>Latest automatic copy</small></span></div></section>}
    <section className="settings-card"><div className="settings-card-title"><strong>Project contract</strong><small>One delivery standard for every approved shot</small></div><div className="delivery-presets" role="group" aria-label="Delivery presets">{DELIVERY_PRESETS.map(preset => <button type="button" key={preset.id} className={draft.deliveryWidth === preset.deliveryWidth && draft.deliveryHeight === preset.deliveryHeight && draft.framesPerSecond === preset.framesPerSecond ? 'selected' : ''} onClick={() => changeProject({ ...draft, aspectRatio: preset.aspectRatio, deliveryWidth: preset.deliveryWidth, deliveryHeight: preset.deliveryHeight, framesPerSecond: preset.framesPerSecond, colorSpace: 'Rec.709', audioSampleRate: 48000 })}><strong>{preset.label}</strong><small>{preset.detail}</small></button>)}</div><div className="form-grid"><label>Project name<input value={draft.name} onChange={event => changeProject({ ...draft, name: event.target.value })} /></label><label>Production<input value={draft.production} onChange={event => changeProject({ ...draft, production: event.target.value })} /></label><label>Sequence code<input value={draft.sequenceCode} onChange={event => changeProject({ ...draft, sequenceCode: event.target.value })} /></label><label>Sequence name<input value={draft.sequenceName} onChange={event => changeProject({ ...draft, sequenceName: event.target.value })} /></label><label>Frame rate<input type="number" min={1} max={120} value={draft.framesPerSecond} onChange={event => changeProject({ ...draft, framesPerSecond: Number(event.target.value) })} /></label><label>Aspect ratio<input value={draft.aspectRatio} onChange={event => changeProject({ ...draft, aspectRatio: event.target.value })} /></label><label>Delivery width<input type="number" min={320} max={7680} step={2} value={draft.deliveryWidth} onChange={event => changeProject({ ...draft, deliveryWidth: Number(event.target.value) })} /></label><label>Delivery height<input type="number" min={180} max={4320} step={2} value={draft.deliveryHeight} onChange={event => changeProject({ ...draft, deliveryHeight: Number(event.target.value) })} /></label><label>Color space<select value={draft.colorSpace} onChange={event => changeProject({ ...draft, colorSpace: event.target.value as ProjectSummary['colorSpace'] })}><option>Rec.709</option><option>Display P3 D65</option><option>Rec.2020</option></select></label><label>Audio master<select value={draft.audioSampleRate} onChange={event => changeProject({ ...draft, audioSampleRate: Number(event.target.value) as ProjectSummary['audioSampleRate'] })}><option value={44100}>44.1 kHz</option><option value={48000}>48 kHz</option><option value={96000}>96 kHz</option></select></label></div><p className="settings-contract-note">Still authorities may be larger. Review passes use lower-cost proxy canvases; approved output returns to {draft.deliveryWidth} × {draft.deliveryHeight} · {draft.framesPerSecond}fps · {draft.colorSpace} · {draft.audioSampleRate / 1000} kHz.</p><div className="settings-save-row"><span className={projectDirty ? 'dirty' : ''}>{projectDirty ? 'Unsaved project changes' : 'Project contract saved'}</span><button className="secondary compact" disabled={saving || !projectDirty || !projectValid} onClick={() => void saveProject()}>{saving ? 'Saving…' : 'Save project contract'}</button></div></section>
    {pairing?.isLoopback && <section className="settings-card credential-card"><div className="settings-card-title"><strong>OpenAI project credential</strong><small>{credential?.detail ?? 'Checking the credential boundary...'}</small></div>{credential?.canManageHere ? <><label>API key<input type="password" autoComplete="off" value={apiKey} onChange={event => setApiKey(event.target.value)} placeholder={credential.isConfigured ? 'Replace configured key' : 'Paste a dedicated project key'} /></label><div className="pairing-actions"><button className="secondary compact" disabled={saving || apiKey.trim().length < 20} onClick={() => void saveCredential()}>Save encrypted</button>{credential.isConfigured && credential.source === 'Windows user credential' && <button className="secondary compact" disabled={saving} onClick={() => void removeCredential()}>Remove stored key</button>}</div></> : <div className="service-credential-status" role="status"><LockKeyhole size={16} /><span><strong>{credential?.isConfigured ? 'Credential supplied by the service environment' : credential ? 'Configure the service environment' : 'Checking service credential status…'}</strong><small>{credential ? credential.isConfigured ? `${credential.source} is active. Change or remove it where the Framewright service is launched.` : 'Set OPENAI_API_KEY in the service or container environment, then restart Framewright.' : 'Interactive credential controls appear only when this host can store them safely.'}</small></span></div>}</section>}
    <section className={`pairing-card ${pairing?.lanEnabled ? 'enabled' : ''}`}><div><span><strong>Tablet access <em className="preview-tag">Preview</em></strong><small>{pairing?.lanEnabled ? 'Short-lived, in-person pairing' : 'Loopback only - safest default'}</small></span>{pairing?.pairingCode && <code>{pairing.pairingCode.slice(0, 4)} {pairing.pairingCode.slice(4)}</code>}</div><p>{pairing?.securityNote ?? 'Checking the local access boundary...'}</p>{pairing?.lanEnabled && pairing.isLoopback && <div className="pairing-actions"><button className="secondary" onClick={onRotatePairing}>Rotate code</button><button className="secondary" onClick={onRevokePairing}>End tablet sessions</button></div>}</section>
    {setupError && <p className="form-error" role="alert">{setupError}</p>}
    <div className="integration-list">{busy && integrations.length === 0 ? <div className="integration-loading"><LoaderCircle className="spin" />Inspecting local services…</div> : integrations.map(integration => <IntegrationCard key={integration.id} integration={integration} />)}</div>
    <section className="setup-codex"><div className="codex-orb"><Sparkles /></div><div><h3>Let Codex handle the setup work</h3><p>Codex can inspect configuration, prepare adapter files, and propose safe workflow bindings. Every production action remains reviewable.</p><button onClick={onAskCodex}><Bot size={17} />Open a read-only setup pass</button></div></section></div>
    <footer><div className="setup-downloads"><a className="secondary backup-link" href="/api/maintenance/backup" download><Download size={16} />Backup</a>{pairing?.isLoopback && <a className="secondary backup-link diagnostic-link" href="/api/maintenance/diagnostics" download><Download size={16} />QA diagnostics</a>}</div><button className="secondary" onClick={() => { onRefresh(); refreshOperationalStatus() }} disabled={busy}><RefreshCw className={busy ? 'spin' : ''} size={16} />Refresh status</button><button className="primary" onClick={onClose}>Done</button></footer>
  </aside></Dialog>
}

function IntegrationCard({ integration }: { integration: IntegrationSummary }) {
  const stateIcon = integration.state === 'Connected' || integration.state === 'Ready' ? <Check size={15} /> : integration.state === 'Protected' ? <BadgeCheck size={15} /> : <Unplug size={15} />
  return <article className={`integration-card state-${integration.state.toLowerCase()}`}><div className="integration-icon">{integration.id === 'codex' ? <Bot /> : integration.id === 'comfyui' ? <Blocks /> : <WandSparkles />}</div><div className="integration-copy"><div><h3>{integration.name}{integration.id === 'openai' && <em className="preview-tag">Preview</em>}</h3><span>{stateIcon}{integration.headline}</span></div><p>{integration.detail}</p>{integration.endpoint && <code>{integration.endpoint}</code>}</div></article>
}

function AgentActivityPanel({ available, enabled, detail, activity, proposals, shots, onToggleEnabled, onClose, onRefresh, onUpdated, onApply }: { available: boolean; enabled: boolean; detail: string; activity: AgentActivityEntry[]; proposals: ShotRevisionProposalSummary[]; shots: ShotSummary[]; onToggleEnabled: () => void; onClose: () => void; onRefresh: () => void; onUpdated: () => void; onApply: (proposal: ShotRevisionProposalSummary) => Promise<string | undefined> }) {
  const [editing, setEditing] = useState<ShotRevisionProposalSummary>()
  const [busy, setBusy] = useState(false)
  const [failure, setFailure] = useState<string>()
  const act = async (operation: () => Promise<unknown>) => { setBusy(true); setFailure(undefined); try { await operation(); onUpdated() } finally { setBusy(false) } }
  // Applying is the artist's move, so its refusal belongs on the card rather
  // than in a toast that disappears before it is read.
  const apply = async (proposal: ShotRevisionProposalSummary) => { setBusy(true); setFailure(undefined); try { setFailure(await onApply(proposal)) } finally { setBusy(false) } }
  return <aside className="agent-activity-panel" data-testid="agent-activity-panel" role="dialog" aria-labelledby="agent-activity-title">
    <header><div><p className="eyebrow">Shared human-agent workspace</p><h2 id="agent-activity-title">Agent activity</h2></div><button onClick={onClose} aria-label="Close agent activity"><X /></button></header>
    <section className={`agent-availability ${available ? 'available' : ''}`}><CircleDot size={17} /><div><strong>{available ? 'WebMCP available' : enabled ? 'WebMCP unavailable' : 'WebMCP paused'}</strong><p>{detail}</p></div><button data-testid="agent-tools-toggle" onClick={onToggleEnabled}>{enabled ? 'Pause tools' : 'Enable tools'}</button></section>
    <section><div className="agent-section-title"><h3>Registered tools</h3><span>{available ? FRAMEWRIGHT_WEBMCP_TOOL_NAMES.length : 0}</span></div><div className="agent-tools">{FRAMEWRIGHT_WEBMCP_TOOL_NAMES.map(name => <code key={name}>{name}</code>)}</div></section>
    <section><div className="agent-section-title"><h3>Revision proposals</h3><button onClick={onRefresh}><RefreshCw size={14} />Refresh</button></div>
      {proposals.length === 0 && <p className="agent-empty">No agent proposals yet. Your canonical shots remain untouched.</p>}
      {proposals.map(proposal => { const shot = shots.find(item => item.id === proposal.shotId); const draft = editing?.id === proposal.id ? editing : proposal; return <article className="proposal-card" key={proposal.id} data-testid="agent-proposal-card">
        <div className="proposal-heading"><div><span>{shot?.code ?? 'Shot'} · base v{proposal.baseVersion}</span><strong>{proposal.state}</strong></div><em>{proposal.desiredMediaType}</em></div>
        <div className="proposal-diff"><div><small>Before · shot intent</small><p>{shot?.description ?? 'Shot no longer available.'}</p><p>{shot?.action}</p></div><div><small>After · proposed direction</small>{proposal.state === 'Pending' ? <><textarea aria-label="Proposed creative direction" value={draft.creativeDirection} maxLength={1000} onChange={event => setEditing({ ...draft, creativeDirection: event.target.value })} /><textarea aria-label="Proposal rationale" value={draft.rationale} maxLength={600} onChange={event => setEditing({ ...draft, rationale: event.target.value })} /></> : <><p>{proposal.creativeDirection}</p><p>{proposal.rationale}</p></>}</div></div>
        <div className="proposal-scope" data-testid="agent-proposal-scope"><span><strong>{proposal.noteIds.length}</strong> targeted {proposal.noteIds.length === 1 ? 'note' : 'notes'}</span><span><strong>{proposal.preservedConstraints.length}</strong> preserved</span>{proposal.preservedConstraints.map(rule => <em key={rule} title={rule}><LockKeyhole size={12} />{rule}</em>)}</div>
        <footer>{proposal.state === 'Pending' ? <><button disabled={busy || !editing || editing.id !== proposal.id} onClick={() => void act(() => studioApi.webMcpEditProposal(proposal.id, { creativeDirection: draft.creativeDirection, rationale: draft.rationale, desiredMediaType: draft.desiredMediaType }))}>Save edit</button><button className="secondary" disabled={busy} onClick={() => void act(() => studioApi.webMcpRejectProposal(proposal.id))}>Reject</button><button className="primary" disabled={busy} onClick={() => void act(() => studioApi.webMcpAcceptProposal(proposal.id))}><Check size={15} />Accept direction</button></>
          : proposal.state === 'Accepted' ? <><span>Nothing is generated by applying</span><button className="primary" disabled={busy} onClick={() => void apply(proposal)}><WandSparkles size={15} />Apply to revision</button></>
          : proposal.state === 'Applied' ? <><span>Applied · no provider was called</span><button disabled={busy} onClick={() => void apply(proposal)}><WandSparkles size={15} />Reopen instructions</button></>
          : <span>Rejected · no shot changes</span>}</footer>
        {failure && <p className="proposal-failure" role="alert" data-testid="agent-proposal-failure">{failure}</p>}
      </article> })}
    </section>
    <section><div className="agent-section-title"><h3>Recent calls</h3><span>{activity.length}</span></div>{activity.length === 0 ? <p className="agent-empty">Waiting for the first browser tool call.</p> : <ol className="agent-log">{activity.map(item => <li key={item.id}><i className={item.state.toLowerCase()} /><div><strong>{item.tool}</strong><span>{item.message}</span></div><time>{new Date(item.at).toLocaleTimeString()}</time></li>)}</ol>}</section>
  </aside>
}

function AssistantDrawer({ shotCode, busy, result, onClose, onAsk, onImplement }: { shotCode: string; busy: boolean; result: CodexAssistResponse | null; onClose: () => void; onAsk: (message: string) => void; onImplement: (direction: string) => void }) {
  const [message, setMessage] = useState('Make this shot more deliberate without breaking continuity.')
  const panel = useRef<HTMLElement>(null)
  // Deliberately NOT a modal: Codex advice is meant to be read alongside the
  // shot, so focus is moved in but not trapped. It is still restored on close —
  // otherwise dismissing the panel drops the user back at the top of the page.
  useEffect(() => {
    const restoreTo = document.activeElement as HTMLElement | null
    panel.current?.focus()
    return () => { if (restoreTo && document.contains(restoreTo)) restoreTo.focus() }
  }, [])
  return <aside ref={panel} tabIndex={-1} className="assistant-drawer" data-testid="assistant-drawer" role="dialog" aria-labelledby="assistant-title"><header><div className="codex-orb"><Sparkles /></div><div><p>Codex production partner</p><h2 id="assistant-title">{shotCode} directing pass</h2></div><button onClick={onClose} aria-label="Close Codex assistant"><X /></button></header>
    <div className="assistant-body">{busy && <div className="assistant-thinking"><LoaderCircle className="spin" /><strong>Reviewing the shot</strong><span>No files or generation jobs will be changed.</span></div>}{result && <article className="assistant-result"><div className="assistant-result-label"><BadgeCheck size={15} />{result.live ? 'Live Codex note' : 'Local fallback note'}</div><h3>{result.headline}</h3><div className="assistant-message">{result.message}</div>{result.findings.length > 0 && <><h4>Findings</h4><ul>{result.findings.map(item => <li key={item}>{item}</li>)}</ul></>}{result.suggestedActions.length > 0 && <><h4>Suggested next moves</h4><ul>{result.suggestedActions.map(item => <li key={item}>{item}</li>)}</ul></>}<div className="assistant-implementation"><strong>Ready to make it real?</strong><p>Use the current frame and this direction to create a new revision. You will choose the image engine before anything starts.</p><button className="primary" onClick={() => onImplement([result.message, ...result.findings, ...result.suggestedActions].filter(Boolean).join('\n'))}><WandSparkles size={16} />Make these changes</button></div></article>}{!busy && !result && <div className="assistant-empty"><Bot /><h3>Directing help, inside the shot</h3><p>Ask about composition, pacing, camera language, continuity, or first/last-frame strategy.</p></div>}</div>
    <form onSubmit={e => { e.preventDefault(); if (message.trim()) onAsk(message) }}><textarea value={message} onChange={e => setMessage(e.target.value)} aria-label="Ask Codex" /><button disabled={busy || !message.trim()}><Sparkles size={17} />Ask Codex</button></form>
  </aside>
}

function CommentComposer({ code, reference, text, setText, onSave, onClose }: { code: string; reference?: ReferenceSummary; text: string; setText: (value: string) => void; onSave: () => void; onClose: () => void }) {
  return <Dialog className="modal-backdrop" onClose={onClose} labelledBy="comment-title" initialFocus="textarea"><form className="comment-composer" onSubmit={e => { e.preventDefault(); onSave() }}><header><div><p className="eyebrow">{reference ? `Reference placement · ${code}` : `Pinned feedback · ${code}`}</p><h2 id="comment-title">{reference ? `Place ${reference.name}` : 'What should change?'}</h2></div><button type="button" onClick={onClose} aria-label="Close feedback composer"><X /></button></header>{reference && <div className="comment-reference-context"><div className="reference-swatch" style={{ '--accent': reference.accent } as React.CSSProperties}><span>{reference.name.slice(0, 1)}</span></div><span><strong>{reference.name} · v{reference.version}</strong><small>{reference.category} authority · {reference.lockedConstraint}</small></span><BadgeCheck size={17} /></div>}<textarea aria-label={reference ? 'Reference instruction' : 'Feedback'} value={text} onChange={e => setText(e.target.value)} placeholder={reference ? 'Example: use this image reference for this face…' : 'Be specific: subject, region, intended correction…'} /><p>{reference ? `The next generation will use ${reference.name} v${reference.version} at this exact location with your instruction.` : 'This note stays with the current version and is included when you regenerate.'}</p><footer><button type="button" className="secondary" onClick={onClose}>Cancel</button><button className="primary" disabled={!text.trim()}>{reference ? <BadgeCheck size={17} /> : <MessageCircle size={17} />}{reference ? 'Pin reference' : 'Pin feedback'}</button></footer></form></Dialog>
}

function CommandPalette({ studio, onClose, onWorkspace, onShot }: { studio: StudioSnapshot; onClose: () => void; onWorkspace: (workspace: Workspace) => void; onShot: (id: string) => void }) {
  const [query, setQuery] = useState('')
  const [activeIndex, setActiveIndex] = useState(0)
  const normalized = query.trim().toLowerCase()
  const workspaces = workspaceItems.filter(item => !normalized || `${item.label} ${item.id}`.toLowerCase().includes(normalized))
  const shots = studio.shots.filter(shot => !normalized || `${shot.code} ${shot.title} ${shot.description}`.toLowerCase().includes(normalized))
  const workspaceCount = workspaces.length
  const actionCount = workspaceCount + Math.min(6, shots.length)
  useEffect(() => setActiveIndex(0), [normalized])
  const activate = (index: number) => {
    if (index < workspaceCount) onWorkspace(workspaces[index].id)
    else { const shot = shots[index - workspaceCount]; if (shot) onShot(shot.id) }
  }
  const handleKeys = (event: React.KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      if (actionCount > 0) setActiveIndex(index => (index + (event.key === 'ArrowDown' ? 1 : -1) + actionCount) % actionCount)
    }
    if (event.key === 'Enter' && actionCount > 0) { event.preventDefault(); activate(activeIndex) }
  }
  // The arrow-key highlight used to be purely visual: no combobox role, no
  // aria-activedescendant, no option ids. A screen-reader user pressing ↑/↓
  // heard nothing at all. Every option now carries an id that the input points
  // at, which is what actually gets announced.
  const optionId = (index: number) => `command-option-${index}`
  return <Dialog className="modal-backdrop command-backdrop" onClose={onClose} labelledBy="command-title" initialFocus="input"><section className="command-palette" data-testid="command-palette">
    <header><Search size={19} /><input value={query} onChange={event => setQuery(event.target.value)} onKeyDown={handleKeys} placeholder="Find a shot or open a workspace" aria-label="Find a shot or workspace" role="combobox" aria-expanded={actionCount > 0} aria-controls="command-results" aria-autocomplete="list" aria-activedescendant={actionCount > 0 ? optionId(activeIndex) : undefined} /><kbd>Esc</kbd></header>
    <h2 id="command-title" className="sr-only">Find a shot or workspace</h2>
    <div id="command-results" className="command-results" role="listbox" aria-label="Shots and workspaces">
      {workspaces.length > 0 && <div className="command-group" role="group" aria-label={normalized ? 'Matching workspaces' : 'Workspaces'}><p>{normalized ? 'Matching workspaces' : 'Workspaces'}</p>{workspaces.map((item, index) => <button key={item.id} id={optionId(index)} role="option" aria-selected={activeIndex === index} className={activeIndex === index ? 'active' : ''} onMouseEnter={() => setActiveIndex(index)} onClick={() => onWorkspace(item.id)}>{item.icon}<span><strong>{item.label}</strong><small>{item.id === 'board' ? 'Shots and authorities' : item.id === 'assets' ? 'Images, music, audio, and video' : item.id === 'world' ? 'Universal style and canon laws' : item.id === 'shot' ? 'Frame, feedback, and constraints' : item.id === 'review' ? 'Compare and ratify' : 'Picture and audio assembly'}</small></span><ArrowRight size={16} /></button>)}</div>}
      <div className="command-group" role="group" aria-label={normalized ? 'Matching shots' : 'Shots'}><p>{normalized ? 'Matching shots' : 'Shots'}</p>{shots.slice(0, 6).map((shot, index) => { const actionIndex = workspaceCount + index; return <button key={shot.id} id={optionId(actionIndex)} role="option" aria-selected={activeIndex === actionIndex} className={activeIndex === actionIndex ? 'active' : ''} onMouseEnter={() => setActiveIndex(actionIndex)} onClick={() => onShot(shot.id)}><span className="command-code">{shot.code}</span><span><strong>{shot.title}</strong><small>{shot.stage} v{shot.version} · {shot.approval}</small></span><ArrowRight size={16} /></button> })}{shots.length === 0 && <div className="command-empty">No shots match “{query}”.</div>}</div>
    </div>
    <footer><span><kbd>↑↓</kbd> Browse</span><span><kbd>Enter</kbd> Open</span><span><kbd>Esc</kbd> Close</span></footer>
  </section></Dialog>
}
