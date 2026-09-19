import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react'
import { Aperture, ArrowLeft, ArrowRight, BadgeCheck, Check, CircleAlert, Clapperboard, Cpu, Download, Film, ImagePlus, Layers3, Library, LoaderCircle, LockKeyhole, MessageCircle, Music2, Pause, PenLine, Play, Plus, Redo2, RotateCcw, Scan, Sparkles, Trash2, Undo2, Volume2, WandSparkles, X } from 'lucide-react'
import { studioApi } from '../api'
import { getSupersededJobIds } from '../jobState'
import Artwork from './Artwork'
import Dialog from './Dialog'
import MusicCompositionWorkspace from './MusicCompositionWorkspace'
import type { AssetPlacementSummary, AssetSummary, AudioMasteringStatus, CandidateVersionSummary, CommentSummary, DraftWorkflowSummary, GenerationAdapterSummary, GenerationManifestSummary, GenerationPreflightSummary, JobSummary, ProductionExportReadiness, ReferenceSummary, ShotContinuityReport, ShotSummary, ShotVisualAuditSummary, SketchPoint, SketchStroke, StudioSnapshot, TimelineClipSummary, TimelineTrackKind, VideoQuality, VisualReconciliationAction, VoiceProfileKind, VoiceProfileSummary, VoiceSynthesisStatus } from '../types'

const SketchWorkspace = lazy(() => import('./SketchWorkspace'))

export function BoardWorkspace({ studio, selectedId, initialView, onViewChange, onSelect, onOpen, onCreateShot, onCreateAuthority, onEditAuthority, onOpenLibrary, onChanged }: { studio: StudioSnapshot; selectedId: string; initialView?: 'shots' | 'authorities'; onViewChange?: (view: 'shots' | 'authorities') => void; onSelect: (id: string) => void; onOpen: (id: string) => void; onCreateShot: (image?: File) => void; onCreateAuthority: () => void; onEditAuthority: (reference: ReferenceSummary) => void; onOpenLibrary: () => void; onChanged: (message?: string) => void }) {
  const [view, setView] = useState<'shots' | 'authorities'>(initialView ?? 'shots')
  const [fileDragging, setFileDragging] = useState(false)
  const fileDragDepth = useRef(0)
  const chooseView = (next: 'shots' | 'authorities') => { setView(next); onViewChange?.(next) }
  const ratified = studio.shots.filter(x => x.approval === 'Ratified').length
  const issues = studio.shots.filter(x => x.continuityState !== 'Clear').length
  const handleBoardTabs = (event: React.KeyboardEvent<HTMLDivElement>) => {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
    event.preventDefault()
    const next = event.key === 'ArrowLeft' || event.key === 'Home' ? 'shots' : 'authorities'
    chooseView(next)
    requestAnimationFrame(() => document.getElementById(`${next}-board-tab`)?.focus())
  }
  const acceptsFiles = (event: React.DragEvent) => view === 'shots' && event.dataTransfer.types.includes('Files')
  return <main className={`workspace board-workspace ${fileDragging ? 'board-file-drop-active' : ''}`} data-testid="board-workspace"
    onDragEnter={event => { if (!acceptsFiles(event)) return; event.preventDefault(); fileDragDepth.current += 1; setFileDragging(true) }}
    onDragOver={event => { if (!acceptsFiles(event)) return; event.preventDefault(); event.dataTransfer.dropEffect = 'copy' }}
    onDragLeave={event => { if (!fileDragging) return; event.preventDefault(); fileDragDepth.current = Math.max(0, fileDragDepth.current - 1); if (fileDragDepth.current === 0) setFileDragging(false) }}
    onDrop={event => { if (!acceptsFiles(event)) return; event.preventDefault(); fileDragDepth.current = 0; setFileDragging(false); const image = Array.from(event.dataTransfer.files).find(file => ['image/png', 'image/jpeg'].includes(file.type)); if (image) onCreateShot(image) }}>
    {fileDragging && <div className="board-drop-overlay" role="status"><ImagePlus size={34} /><strong>Drop image to add a shot</strong><span>The new-card form opens with this image attached.</span></div>}
    <section className="board-heading">
      <div><p className="eyebrow">{studio.project.sequenceCode} · {studio.project.sequenceName}</p><h1>{view === 'shots' ? 'Shot board' : 'Authority board'}</h1><p className="lede">{view === 'shots' ? 'The whole sequence at a glance. Select a frame to move from intent to approved production.' : 'Approved identity, world, wardrobe, prop, and style truth. Shots cite exact versions without changing canon.'}</p></div>
      <div className="board-heading-tools">
        <button className="primary board-create" onClick={() => view === 'shots' ? onCreateShot() : onCreateAuthority()}><Plus size={17} />{view === 'shots' ? 'Add card' : 'Add authority'}</button>
        {/* Only on the authority board: the library trades in authorities, so it
            would be noise above the shot wall. */}
        {view === 'authorities' && <button className="secondary board-library" onClick={onOpenLibrary}><Library size={16} />Import authority</button>}
        <div className="board-view-switch" role="tablist" aria-label="Board view" onKeyDown={handleBoardTabs}><button id="shots-board-tab" role="tab" aria-selected={view === 'shots'} aria-controls="shot-board-panel" tabIndex={view === 'shots' ? 0 : -1} className={view === 'shots' ? 'active' : ''} onClick={() => chooseView('shots')}>Shots <span>{studio.shots.length}</span></button><button id="authorities-board-tab" role="tab" aria-selected={view === 'authorities'} aria-controls="authority-board-panel" tabIndex={view === 'authorities' ? 0 : -1} className={view === 'authorities' ? 'active' : ''} onClick={() => chooseView('authorities')}>Authorities <span>{studio.references.length}</span></button></div>
        <div className="sequence-metrics" aria-label="Sequence summary">
          <Metric value={studio.shots.length.toString().padStart(2, '0')} label="Shots" />
          <Metric value={`${ratified}/${studio.shots.length}`} label="Ratified" />
          <Metric value={`${Math.round(studio.shots.reduce((n, x) => n + x.durationFrames, 0) / studio.project.framesPerSecond)}s`} label="Runtime" />
          <Metric value={issues.toString().padStart(2, '0')} label="To review" warning={issues > 0} />
        </div>
      </div>
    </section>
    {view === 'shots' && studio.shots.length === 0 ? <EmptyBoard
      icon={<Clapperboard />}
      title="No shots in this sequence yet"
      body="A sequence is built from named shot slots. Each slot holds one review authority and its archived versions."
      note="Use Add card to create the first named slot." />
    : view === 'authorities' && studio.references.length === 0 ? <EmptyBoard
      icon={<LockKeyhole />}
      title="No approved authorities yet"
      body="Authorities are the immutable truth for identity, wardrobe, location, props, and style. Shots cite exact versions of them."
      note="Create one with Add authority, or use Import authority to bring in a proven reference from the global library." />
    : view === 'shots' ? <ShotBoard studio={studio} selectedId={selectedId} onSelect={onSelect} onOpen={onOpen} onCreateShot={onCreateShot} onChanged={onChanged} />
    : <section id="authority-board-panel" className="authority-wall" role="tabpanel" aria-labelledby="authorities-board-tab" data-testid="reference-board">
      {studio.references.map(reference => <button type="button" className={`authority-card category-${reference.category.toLowerCase()}`} key={reference.id} style={{ '--authority-accent': reference.accent } as React.CSSProperties} onClick={() => onEditAuthority(reference)} aria-label={`Open ${reference.name}, ${reference.category}, approved version ${reference.version}`}>
        <div className="authority-image">{reference.imageUrl ? <img src={reference.imageUrl} alt={`${reference.name} approved authority`} /> : <Artwork variant={reference.visualVariant} label={`${reference.name} approved authority`} />}<span>{reference.category}</span><i><LockKeyhole size={13} />v{reference.version}</i></div>
        <div className="authority-body"><div><p>{reference.status}</p><h2>{reference.name}</h2></div><BadgeCheck size={18} /><p>{reference.description}</p><div className="locked-truth"><LockKeyhole size={14} /><span>{reference.lockedConstraint}</span></div></div>
      </button>)}
      <button type="button" className="new-shot-card new-authority-card" onClick={onCreateAuthority}><Plus size={26} /><span>Add authority</span><small>Ratify immutable visual truth</small></button>
    </section>}
  </main>
}

function Metric({ value, label, warning }: { value: string; label: string; warning?: boolean }) { return <div className={warning ? 'metric warning' : 'metric'}><strong>{value}</strong><span>{label}</span></div> }

type BoardFilter = 'all' | 'working' | 'review' | 'ratified'

const BOARD_FILTERS: { id: BoardFilter; label: string; hint: string; match: (shot: ShotSummary) => boolean }[] = [
  { id: 'all', label: 'All', hint: 'Every slot in the sequence', match: () => true },
  { id: 'working', label: 'In progress', hint: 'Not yet ratified', match: shot => shot.approval !== 'Ratified' },
  { id: 'review', label: 'Needs review', hint: 'Open notes or a continuity flag', match: shot => shot.openComments > 0 || shot.continuityState !== 'Clear' },
  { id: 'ratified', label: 'Ratified', hint: 'Approved authority', match: shot => shot.approval === 'Ratified' },
]

/**
 * The shot board.
 *
 * Everything an artist does to the *set* of shots happens here — ordering,
 * duplicating, deleting, triage — so none of it requires opening a shot first.
 *
 * Interaction model, chosen so the mouse path stays fast without stranding the
 * keyboard:
 *   click card      open it (the common case, unchanged)
 *   ← ↑ → ↓         move a roving focus across the grid without opening
 *   Enter / Space   open the focused card
 *   Alt + ← →       reorder the focused card, persisted immediately
 *   drag the grip   reorder with a live drop indicator
 *   ⋯ menu          duplicate, move, delete — without leaving the board
 */
function ShotBoard({ studio, selectedId, onSelect, onOpen, onCreateShot, onChanged }: {
  studio: StudioSnapshot; selectedId: string
  onSelect: (id: string) => void; onOpen: (id: string) => void
  onCreateShot: (image?: File) => void; onChanged: (message?: string) => void
}) {
  const [filter, setFilter] = useState<BoardFilter>('all')
  const [menuFor, setMenuFor] = useState<string>()
  const [confirmDelete, setConfirmDelete] = useState<ShotSummary>()
  const [busyId, setBusyId] = useState<string>()
  const [error, setError] = useState<string>()
  const [dragId, setDragId] = useState<string>()
  const [dropIndex, setDropIndex] = useState<number>()
  // Optimistic order so a drag lands instantly instead of after a round trip.
  const [order, setOrder] = useState<string[]>()
  const grid = useRef<HTMLDivElement>(null)

  const ordered = useMemo(() => {
    if (!order) return studio.shots
    const byId = new Map(studio.shots.map(shot => [shot.id, shot]))
    const sorted = order.map(id => byId.get(id)).filter((shot): shot is ShotSummary => Boolean(shot))
    // Anything the server knows about but the optimistic list does not (a shot
    // created elsewhere) still has to appear.
    return sorted.length === studio.shots.length ? sorted : studio.shots
  }, [order, studio.shots])
  useEffect(() => { if (!activeDrag.current) setOrder(undefined) }, [studio.shots])

  const visible = ordered.filter(BOARD_FILTERS.find(x => x.id === filter)!.match)
  const counts = Object.fromEntries(BOARD_FILTERS.map(x => [x.id, ordered.filter(x.match).length])) as Record<BoardFilter, number>
  const supersededJobs = getSupersededJobIds(studio.jobs)
  const jobFor = (shot: ShotSummary) => studio.jobs.find(job => job.shotId === shot.id && (job.state === 'Queued' || job.state === 'Running'))
  const failedFor = (shot: ShotSummary) => studio.jobs.find(job => job.shotId === shot.id && job.state === 'Failed' && !supersededJobs.has(job.id))

  const persistOrder = async (ids: string[]) => {
    setOrder(ids); setError(undefined)
    try { await studioApi.reorderShots(ids); onChanged(undefined) }
    catch (reason) { setOrder(undefined); setError(reason instanceof Error ? reason.message : 'Could not save the new order.') }
  }

  const move = (shot: ShotSummary, delta: -1 | 1) => {
    const ids = ordered.map(x => x.id)
    const from = ids.indexOf(shot.id)
    const to = from + delta
    if (to < 0 || to >= ids.length) return
    ids.splice(to, 0, ids.splice(from, 1)[0])
    void persistOrder(ids)
  }

  const duplicate = async (shot: ShotSummary) => {
    setBusyId(shot.id); setMenuFor(undefined); setError(undefined)
    try { const copy = await studioApi.duplicateShot(shot.id); onChanged(`${shot.code} duplicated as ${copy.code}.`) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not duplicate that shot.') }
    finally { setBusyId(undefined) }
  }

  const remove = async (shot: ShotSummary, acceptRatifiedLoss: boolean) => {
    setBusyId(shot.id); setError(undefined)
    try {
      const removed = await studioApi.deleteShot(shot.id, acceptRatifiedLoss)
      setConfirmDelete(undefined)
      onChanged(`${removed.code} deleted.`)
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Could not delete that shot.'
      // A refusal because of ratified evidence is not an error the artist should
      // have to decode — escalate straight to the confirming dialog.
      if (/ratified version/i.test(message) && !acceptRatifiedLoss) { setConfirmDelete(shot); setError(undefined) }
      else setError(message)
    } finally { setBusyId(undefined) }
  }

  const focusCard = (index: number) => {
    const cards = grid.current?.querySelectorAll<HTMLElement>('.shot-open-hit')
    if (!cards?.length) return
    const target = cards[Math.max(0, Math.min(cards.length - 1, index))]
    target?.focus()
  }

  const onGridKeys = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const card = (event.target as HTMLElement).closest<HTMLElement>('.shot-card')
    if (!card) return
    const cards = [...(grid.current?.querySelectorAll<HTMLElement>('.shot-open-hit') ?? [])]
    const index = cards.indexOf(card.querySelector<HTMLElement>('.shot-open-hit')!)
    if (index < 0) return
    const shot = visible[index]
    // Columns are driven by CSS, so measure rather than assume.
    const perRow = Math.max(1, Math.round((grid.current?.clientWidth ?? 0) / Math.max(1, card.offsetWidth + 20)))

    if (event.altKey && (event.key === 'ArrowLeft' || event.key === 'ArrowRight')) {
      event.preventDefault(); move(shot, event.key === 'ArrowLeft' ? -1 : 1)
      requestAnimationFrame(() => focusCard(index + (event.key === 'ArrowLeft' ? -1 : 1)))
      return
    }
    const jump: Partial<Record<string, number>> = {
      ArrowLeft: index - 1, ArrowRight: index + 1,
      ArrowUp: index - perRow, ArrowDown: index + perRow,
      Home: 0, End: cards.length - 1,
    }
    const next = jump[event.key]
    if (next !== undefined) { event.preventDefault(); focusCard(next); return }
    if (event.key === 'Delete' || event.key === 'Backspace') { event.preventDefault(); setConfirmDelete(shot) }
  }

  // ── Drag to reorder ─────────────────────────────────────────────────────
  // Any card can be dragged from anywhere on its surface, not just the grip:
  // a press only becomes a drag once the pointer travels past a threshold, so a
  // plain click still opens the shot. The grip stays purely as the visual hint
  // that a card is draggable.
  const DRAG_THRESHOLD = 5
  const dragFrom = useRef<number | undefined>(undefined)
  const pending = useRef<{ id: string; x: number; y: number } | undefined>(undefined)
  const dragged = useRef(false)
  const activeDrag = useRef<string | undefined>(undefined)
  const activeDrop = useRef<number | undefined>(undefined)

  const pressCard = (event: ReactPointerEvent<HTMLElement>, shot: ShotSummary) => {
    if (!reorderable || event.button !== 0) return
    // The ⋯ menu and its items are controls, not drag surfaces.
    if ((event.target as HTMLElement).closest('.shot-menu, .shot-menu-trigger')) return
    pending.current = { id: shot.id, x: event.clientX, y: event.clientY }
    dragged.current = false
  }

  const overDrag = (event: ReactPointerEvent<HTMLElement>) => {
    const start = pending.current
    if (start && !dragId) {
      if (Math.hypot(event.clientX - start.x, event.clientY - start.y) < DRAG_THRESHOLD) return
      dragFrom.current = ordered.findIndex(x => x.id === start.id)
      dragged.current = true
      activeDrag.current = start.id
      setDragId(start.id)
      // Capture keeps the drag alive if the pointer leaves the card, but a
      // failure here must not abandon the drag — the grid-level move handler
      // works without it.
      try { (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId) } catch { /* capture is an optimisation */ }
    }
    const active = activeDrag.current
    if (!active) return
    const under = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('.shot-card')
    if (!under) return
    const cards = [...(grid.current?.querySelectorAll<HTMLElement>('.shot-card') ?? [])]
    const target = cards.indexOf(under)
    if (target < 0) return

    // Reorder live rather than only on release. Watching the other cards move
    // aside is what tells you the drag is working — the previous build showed
    // nothing until drop, which read as a failure right up until it succeeded.
    const ids = ordered.map(x => x.id)
    const from = ids.indexOf(active)
    if (from === target) return
    measureCards()
    ids.splice(target, 0, ids.splice(from, 1)[0])
    dragFrom.current = target
    activeDrop.current = target
    setDropIndex(target)
    setOrder(ids)
  }

  /** FLIP: remember where every card is, so the next render can glide from here. */
  const lastRects = useRef(new Map<string, DOMRect>())
  const measureCards = () => {
    const map = new Map<string, DOMRect>()
    grid.current?.querySelectorAll<HTMLElement>('.shot-card').forEach(card => {
      const id = card.dataset.shotId
      if (id) map.set(id, card.getBoundingClientRect())
    })
    lastRects.current = map
  }
  useEffect(() => {
    if (!dragId) return
    const cards = grid.current?.querySelectorAll<HTMLElement>('.shot-card')
    cards?.forEach(card => {
      const id = card.dataset.shotId
      const before = id ? lastRects.current.get(id) : undefined
      if (!before || id === dragId) return
      const after = card.getBoundingClientRect()
      const dx = before.left - after.left
      const dy = before.top - after.top
      if (!dx && !dy) return
      card.animate(
        [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'translate(0, 0)' }],
        { duration: 160, easing: 'cubic-bezier(.2,.8,.3,1)' })
    })
  }, [order, dragId])

  const endDrag = () => {
    // The live reorder already produced the final arrangement; drop just saves it.
    if (activeDrag.current && order) void persistOrder(order)
    dragFrom.current = undefined; pending.current = undefined
    activeDrag.current = undefined; activeDrop.current = undefined
    setDragId(undefined); setDropIndex(undefined)
  }

  const reorderable = filter === 'all'

  return <section id="shot-board-panel" className="shot-board" role="tabpanel" aria-labelledby="shots-board-tab">
    <div className="board-filters" role="group" aria-label="Filter shots">
      {BOARD_FILTERS.map(item => <button key={item.id} type="button" className={filter === item.id ? 'active' : ''}
        aria-pressed={filter === item.id} title={item.hint} onClick={() => setFilter(item.id)}>
        {item.label}<span>{counts[item.id]}</span>
      </button>)}
      {!reorderable && <span className="board-filter-note">Reordering is available in the All view</span>}
    </div>

    {error && <p className="board-error" role="alert"><CircleAlert size={15} />{error}<button onClick={() => setError(undefined)} aria-label="Dismiss">×</button></p>}

    {visible.length === 0
      ? <p className="board-filter-empty">No shots match this filter. <button type="button" onClick={() => setFilter('all')}>Show all {counts.all}</button></p>
      : <div className="shot-wall" ref={grid} onKeyDown={onGridKeys} onPointerMove={overDrag} onPointerUp={endDrag} onPointerCancel={endDrag}>
        {visible.map((shot, index) => {
          const job = jobFor(shot)
          const failed = !job && failedFor(shot)
          return <article
            key={shot.id}
            className={`shot-card ${selectedId === shot.id ? 'selected' : ''} ${dragId === shot.id ? 'dragging' : ''} ${dropIndex === index && dragId && dragId !== shot.id ? 'drop-target' : ''} ${busyId === shot.id ? 'busy' : ''}`}
            data-shot-id={shot.id}
            onPointerDown={event => pressCard(event, shot)}
            >
            {/* The card carries its own controls, so it cannot itself be a button
                — nested buttons are invalid HTML and strip the semantics. A
                stretched primary action keeps a real button, a real accessible
                name, and native Enter/Space, while the grip and ⋯ menu sit above
                it in the stacking order. */}
            <button type="button" className="shot-open-hit"
              aria-label={`Open ${shot.code}, ${shot.title}. ${shot.stage} version ${shot.version}, ${shot.approval}.${shot.openComments ? ` ${shot.openComments} open notes.` : ''}${job ? ' Generating.' : ''}`}
              onFocus={() => onSelect(shot.id)}
              onClick={() => { if (dragged.current) { dragged.current = false; return } onOpen(shot.id) }} />

            <div className="shot-card-top">
              {reorderable && <span className="shot-grip" title="Drag to reorder" aria-hidden="true"
                onClick={event => event.stopPropagation()}>⋮⋮</span>}
              <span className="shot-code">{shot.code}</span>
              <span className="shot-index">{String(ordered.indexOf(shot) + 1).padStart(2, '0')}</span>
              <button type="button" className="shot-menu-trigger" aria-label={`Actions for ${shot.code}`} aria-expanded={menuFor === shot.id}
                onClick={event => { event.stopPropagation(); setMenuFor(current => current === shot.id ? undefined : shot.id) }}>⋯</button>
            </div>

            <div className="shot-thumb">
              <ShotArtwork shot={shot} label={`${shot.code} ${shot.title}`} />
              <div className="safe-frame" />
              {job && <div className="shot-job" role="status"><LoaderCircle className="spin" size={13} />{job.phase}</div>}
              {failed && <div className="shot-job failed"><CircleAlert size={13} />Generation failed</div>}
            </div>

            <div className="shot-card-body">
              <div className="shot-title-row"><h2>{shot.title}</h2><span className="open-shot">Open <ArrowRight size={14} aria-hidden="true" /></span></div>
              <p title={shot.description}>{shot.description}</p>
              <div className="shot-card-meta">
                <StageBadge shot={shot} />
                <span className="shot-card-signals">
                  {shot.openComments > 0 && <em title={`${shot.openComments} open notes`}><MessageCircle size={12} />{shot.openComments}</em>}
                  {shot.continuityState !== 'Clear' && <em className="warning" title={`Continuity: ${shot.continuityState}`}><CircleAlert size={12} />{shot.continuityState}</em>}
                  <span>{formatTime(shot.durationFrames, studio.project.framesPerSecond)}</span>
                </span>
              </div>
            </div>

            {menuFor === shot.id && <div className="shot-menu" role="menu" onClick={event => event.stopPropagation()}>
              <button role="menuitem" onClick={() => { setMenuFor(undefined); onOpen(shot.id) }}><ArrowRight size={14} />Open shot</button>
              <button role="menuitem" onClick={() => void duplicate(shot)}><Plus size={14} />Duplicate</button>
              <button role="menuitem" disabled={!reorderable || ordered.indexOf(shot) === 0} onClick={() => { setMenuFor(undefined); move(shot, -1) }}><ArrowLeft size={14} />Move earlier</button>
              <button role="menuitem" disabled={!reorderable || ordered.indexOf(shot) === ordered.length - 1} onClick={() => { setMenuFor(undefined); move(shot, 1) }}><ArrowRight size={14} />Move later</button>
              <button role="menuitem" className="danger" onClick={() => { setMenuFor(undefined); setConfirmDelete(shot) }}><Trash2 size={14} />Delete slot</button>
            </div>}
          </article>
        })}
        <button type="button" className="new-shot-card" onClick={() => onCreateShot()}><Plus size={26} /><span>Add card</span><small>Describe the shot, then generate or sketch</small></button>
      </div>}

    {menuFor && <div className="shot-menu-scrim" onClick={() => setMenuFor(undefined)} aria-hidden="true" />}

    {confirmDelete && <DeleteShotDialog
      shot={confirmDelete} busy={busyId === confirmDelete.id}
      onCancel={() => setConfirmDelete(undefined)}
      onConfirm={accept => void remove(confirmDelete, accept)} />}
  </section>
}

/** Deleting a slot is destructive, so it names exactly what goes with it. */
function DeleteShotDialog({ shot, busy, onCancel, onConfirm }: { shot: ShotSummary; busy: boolean; onCancel: () => void; onConfirm: (acceptRatifiedLoss: boolean) => void }) {
  const [accept, setAccept] = useState(false)
  const ratified = shot.approval === 'Ratified'
  return <Dialog className="modal-backdrop" onClose={onCancel} labelledBy="delete-shot-title" initialFocus="[data-dialog-focus]">
    <section className="edit-dialog delete-shot" data-dialog-focus tabIndex={-1}>
      <header><div><p className="eyebrow">Destructive</p><h2 id="delete-shot-title">Delete {shot.code}?</h2></div><button onClick={onCancel} aria-label="Cancel">×</button></header>
      <div className="edit-dialog-body">
        <p>This removes the slot <strong>{shot.title}</strong> and everything scoped to it — every candidate, its composition sketch, pinned notes and job history.</p>
        <p className="delete-shot-keep">Imported assets are content-addressed and shared, so they stay in the asset library.</p>
        {ratified && <label className="endpoint-confirm">
          <input type="checkbox" checked={accept} onChange={event => setAccept(event.target.checked)} />
          This shot carries ratified approval evidence. I understand deleting it destroys that record.
        </label>}
      </div>
      <footer>
        <span /><span />
        <button className="secondary" onClick={onCancel}>Keep it</button>
        <button className="primary danger" disabled={busy || (ratified && !accept)} onClick={() => onConfirm(accept)}>
          {busy ? 'Deleting…' : `Delete ${shot.code}`}
        </button>
      </footer>
    </section>
  </Dialog>
}

/** Empty states say what the surface is for and when it becomes usable, rather
 *  than leaving a blank grid that reads as a loading failure. */
function EmptyBoard({ icon, title, body, note }: { icon: React.ReactNode; title: string; body: string; note: string }) {
  return <section className="empty-board" data-testid="empty-board">
    <div className="empty-board-icon">{icon}</div>
    <h2>{title}</h2>
    <p>{body}</p>
    <p className="empty-board-note">{note}</p>
  </section>
}

type CameraNeed = 'All' | 'Action' | 'Dialogue' | 'Establishing' | 'Portrait' | 'Detail' | 'Suspense'
const CAMERA_NEEDS: CameraNeed[] = ['All', 'Action', 'Dialogue', 'Establishing', 'Portrait', 'Detail', 'Suspense']
const CAMERA_LENSES = [
  { mm: 18, family: 'Ultra-wide', uses: ['Action', 'Establishing'], character: 'Very broad spatial context and strong foreground-to-background scale.', caution: 'Faces and bodies near frame edges can stretch; close camera placement exaggerates proportions.' },
  { mm: 24, family: 'Wide', uses: ['Action', 'Establishing'], character: 'A common starting point for energetic movement, low approaches, and environment-led shots.', caution: 'Dynamic perspective comes from placing the camera close; the lens alone does not create action.' },
  { mm: 28, family: 'Wide naturalistic', uses: ['Action', 'Establishing', 'Dialogue'], character: 'Keeps useful environment while feeling less exaggerated than 18–24mm.', caution: 'Close facial coverage can still emphasize noses and near limbs.' },
  { mm: 35, family: 'Moderate wide', uses: ['Action', 'Dialogue', 'Establishing'], character: 'Flexible subject-plus-environment coverage for movement, groups, and conversational scenes.', caution: 'A close camera still changes facial perspective; back up when neutrality matters.' },
  { mm: 50, family: 'Normal', uses: ['Dialogue', 'Portrait'], character: 'Balanced general coverage with less spatial exaggeration than wider choices.', caution: 'It shows less environment than 35mm from the same subject size and may feel less kinetic.' },
  { mm: 70, family: 'Short telephoto', uses: ['Dialogue', 'Portrait', 'Suspense'], character: 'Useful for profiles, tighter dialogue, and isolating a subject from a calmer viewpoint.', caution: 'Usually requires more camera distance and can reduce the sense of spatial momentum.' },
  { mm: 85, family: 'Portrait telephoto', uses: ['Portrait', 'Dialogue', 'Detail', 'Suspense'], character: 'A familiar starting point for close portrait coverage and subject separation.', caution: 'Focus becomes less forgiving; perspective compression comes from the farther camera position it enables.' },
  { mm: 100, family: 'Telephoto detail', uses: ['Portrait', 'Detail', 'Suspense'], character: 'Tight faces, hands, props, and distant details with strong visual isolation.', caution: 'Not every 100mm lens focuses at macro distance; aperture and subject distance control depth of field.' },
  { mm: 135, family: 'Long telephoto', uses: ['Detail', 'Suspense'], character: 'Distant observation and strongly layered, visually compressed compositions.', caution: 'Harder to track and focus, with little environmental context and more working-distance demand.' },
] as const
const CAMERA_FRAMINGS = ['extreme wide / establishing', 'wide / full body', 'medium full / cowboy', 'medium shot', 'medium close-up', 'close-up', 'extreme close-up', 'over-the-shoulder', 'two-shot', 'profile'] as const
const CAMERA_MOVEMENTS = ['locked', 'handheld', 'slow push-in', 'dolly out', 'lateral tracking', 'stabilized follow', 'pan / tilt', 'crane rise'] as const

function CameraGuide({ value, disabled, onChange }: { value: string; disabled: boolean; onChange: (value: string) => void }) {
  const [need, setNeed] = useState<CameraNeed>('All')
  const lensMatch = value.match(/\b(18|24|28|35|50|70|85|100|135)mm\b/i)
  const selectedMm = lensMatch ? Number(lensMatch[1]) : 0
  const suffix = value.includes('·') ? value.split('·').slice(1).join('·').trim() : ''
  const normalizedSuffix = suffix.toLowerCase()
  const knownFraming = CAMERA_FRAMINGS.find(framing => normalizedSuffix.includes(framing) || (framing === 'profile' && normalizedSuffix.includes('profile')) || (framing === 'medium shot' && normalizedSuffix === 'eye-level medium'))
  const knownMovement = CAMERA_MOVEMENTS.find(movement => normalizedSuffix.includes(movement))
  const selectedLens = CAMERA_LENSES.find(lens => lens.mm === selectedMm)
  const update = (mm: number = selectedMm, framing: string = knownFraming ?? 'medium shot', movement: string = knownMovement ?? 'locked') => onChange(`${mm}mm equivalent · ${framing} · ${movement}`)
  const recommendations = CAMERA_LENSES.filter(lens => need !== 'All' && (lens.uses as readonly string[]).includes(need)).map(lens => `${lens.mm}mm`).join(', ')

  return <div className="camera-guide">
    <label className="camera-field"><span>Lens starting point</span><span className="camera-input"><Aperture size={17} /><select aria-label="Lens starting point" value={selectedMm || 'custom'} disabled={disabled} onChange={event => { const next = Number(event.target.value); if (next) update(next) }}><option value="custom">Custom / unclassified</option>{CAMERA_LENSES.map(lens => <option key={lens.mm} value={lens.mm}>{lens.mm}mm equivalent · {lens.family}</option>)}</select></span></label>
    <label className="camera-field"><span>Shot size / composition</span><span className="camera-input"><Scan size={17} /><select aria-label="Shot size and composition" value={knownFraming ?? 'custom'} disabled={disabled || !selectedMm} onChange={event => { if (event.target.value !== 'custom' && selectedMm) update(selectedMm, event.target.value, knownMovement ?? 'locked') }}><option value="custom">Custom framing</option>{CAMERA_FRAMINGS.map(framing => <option key={framing} value={framing}>{framing}</option>)}</select></span></label>
    <label className="camera-field"><span>Camera movement</span><span className="camera-input"><ArrowRight size={17} /><select aria-label="Camera movement" value={knownMovement ?? 'custom'} disabled={disabled || !selectedMm} onChange={event => { if (event.target.value !== 'custom' && selectedMm) update(selectedMm, knownFraming ?? 'medium shot', event.target.value) }}><option value="custom">Custom movement</option>{CAMERA_MOVEMENTS.map(movement => <option key={movement} value={movement}>{movement}</option>)}</select></span></label>
    {(!selectedLens || !knownFraming || !knownMovement) && <label className="camera-custom"><span>Current custom camera direction</span><input aria-label="Custom camera direction" value={value} disabled={disabled} onChange={event => onChange(event.target.value)} /></label>}
    <div className="camera-need-picker"><span>What does the shot need?</span><div>{CAMERA_NEEDS.map(item => <button type="button" key={item} className={need === item ? 'active' : ''} aria-pressed={need === item} onClick={() => setNeed(item)}>{item}</button>)}</div></div>
    <article className="camera-advice" aria-live="polite">
      {selectedLens ? <><strong>{selectedLens.mm}mm · {selectedLens.family}</strong><p>{selectedLens.character}</p><small><CircleAlert size={13} />{selectedLens.caution}</small></> : <><strong>Custom camera direction</strong><p>Keep the wording concrete: focal-length equivalent, framing, height, movement, and subject relationship.</p></>}
      {need !== 'All' && <em><Sparkles size={13} />For {need.toLowerCase()}, common starting points here are {recommendations}. Any focal length can work when camera position and blocking support the intent.</em>}
    </article>
    <p className="camera-truth-note">Guide assumes 35mm/full-frame-equivalent field of view. Actual framing also depends on sensor crop, camera distance, aspect ratio, and lens design. Perspective comes from camera position; aperture and subject distance influence depth of field. Guidance is not a rule or locked canon.</p>
  </div>
}

export function ShotWorkspace({ studio, shot, comments, references, tool, setTool, onAddComment, onMoveComment, onResolveReferencePin, onRatify, onOpenReview, onOpenAssets, onEdit, onShotSaved, onShotContractSaved, onCandidatesChanged, onJobQueued, onAskCodex, codexBusy, generationBusy, activeJob, implementationRequest, onImplementationConsumed, sketchLaunch, onSketchLaunchConsumed }: {
  studio: StudioSnapshot; shot: ShotSummary; comments: CommentSummary[]; references: ReferenceSummary[];
  tool: 'select' | 'draw' | 'comment'; setTool: (tool: 'select' | 'draw' | 'comment') => void;
  onAddComment: (x: number, y: number, reference?: ReferenceSummary) => void; onMoveComment: (id: string, x: number, y: number) => Promise<void>; onResolveReferencePin: (id: string) => void; onRatify: () => void; onOpenReview: (candidateId?: string) => void; onOpenAssets: () => void; onEdit: () => void; onShotSaved: (shot: ShotSummary) => void; onShotContractSaved: (shot: ShotSummary, message: string) => void; onCandidatesChanged: (message: string) => Promise<void>; onJobQueued: () => void; onAskCodex: () => void; codexBusy: boolean; generationBusy: boolean; activeJob?: JobSummary; implementationRequest?: { id: number; direction: string }; onImplementationConsumed: () => void; sketchLaunch?: { id: number; prompt?: string; route?: 'fast' | 'precision'; underlayAssetId?: string }; onSketchLaunchConsumed: () => void
}) {
  const [tab, setTab] = useState<'intent' | 'refs' | 'rules' | 'media'>('intent')
  const [canvasMode, setCanvasMode] = useState<'frame' | 'sketch'>('frame')
  const [sketchPurpose, setSketchPurpose] = useState<'Draft' | 'Final'>('Draft')
  const [candidates, setCandidates] = useState<CandidateVersionSummary[]>([])
  const [videoOpen, setVideoOpen] = useState(false)
  const [finalOpen, setFinalOpen] = useState(false)
  const [repairAudit, setRepairAudit] = useState<ShotVisualAuditSummary>()
  const [lastFrameOpen, setLastFrameOpen] = useState(false)
  const [markupReady, setMarkupReady] = useState(false)
  const [intentDraft, setIntentDraft] = useState({ description: shot.description, action: shot.action, camera: shot.camera })
  const [intentSaving, setIntentSaving] = useState(false)
  const [intentError, setIntentError] = useState<string>()
  const [pinningReference, setPinningReference] = useState<ReferenceSummary>()
  const [draggingReference, setDraggingReference] = useState<ReferenceSummary>()
  const [showReferencePicker, setShowReferencePicker] = useState(false)
  const [contractBusy, setContractBusy] = useState(false)
  const [clearingPins, setClearingPins] = useState(false)
  const [contractError, setContractError] = useState<string>()
  const [constraintsDraft, setConstraintsDraft] = useState<string[]>(shot.constraints)
  const [newConstraint, setNewConstraint] = useState('')
  const [feedbackRequest, setFeedbackRequest] = useState<string>()
  const [quickGenerating, setQuickGenerating] = useState<'comfyui-fast-draft' | 'codex-imagegen'>()
  const [directRegenerating, setDirectRegenerating] = useState<'comfyui-fast-draft' | 'codex-imagegen'>()
  const [draftWorkflow, setDraftWorkflow] = useState<DraftWorkflowSummary>()
  const [quickGenerationError, setQuickGenerationError] = useState<string>()
  const [mediaAssets, setMediaAssets] = useState<AssetSummary[]>([])
  const [mediaPlacements, setMediaPlacements] = useState<AssetPlacementSummary[]>([])
  const [manifests, setManifests] = useState<GenerationManifestSummary[]>([])
  const [videoPromoting, setVideoPromoting] = useState(false)
  const [endpointBusy, setEndpointBusy] = useState(false)
  const [endpointError, setEndpointError] = useState<string>()
  const videoTakes = useMemo(() => studio.jobs.filter(job => job.shotId === shot.id && job.kind === 'Video' && job.state === 'Completed' && job.outputAssetUrl).sort((a, b) => b.createdAt.localeCompare(a.createdAt)), [shot.id, studio.jobs])
  const latestVideoTake = videoTakes[0]
  const latestVideoManifest = manifests.find(item => item.id === latestVideoTake?.manifestId)
  const currentVideoUrl = latestVideoTake?.outputAssetUrl
  const currentVideoQuality = latestVideoManifest?.videoQuality ?? (latestVideoTake ? 'Low' : undefined)
  useEffect(() => {
    let cancelled = false
    void studioApi.draftWorkflow(shot.id, undefined, Boolean(shot.currentAssetId)).then(value => { if (!cancelled) setDraftWorkflow(value) }).catch(() => { if (!cancelled) setDraftWorkflow(undefined) })
    return () => { cancelled = true }
  }, [canvasMode, shot.currentAssetId, shot.id, shot.version])
  useEffect(() => { void studioApi.manifests(shot.id).then(setManifests).catch(() => setManifests([])) }, [shot.id, shot.version, studio.jobs.length])
  const openSketch = (purpose: 'Draft' | 'Final' = 'Draft') => { setSketchPurpose(purpose); setCanvasMode('sketch') }
  const intentDirty = intentDraft.description !== shot.description || intentDraft.action !== shot.action || intentDraft.camera !== shot.camera
  const resetIntent = useCallback(() => { setIntentDraft({ description: shot.description, action: shot.action, camera: shot.camera }); setIntentError(undefined) }, [shot.action, shot.camera, shot.description])
  const saveIntent = async () => {
    if (!intentDirty || intentSaving || generationBusy || archivedPreview) return
    setIntentSaving(true); setIntentError(undefined)
    try {
      const saved = await studioApi.updateShot(shot.id, {
        expectedUpdatedAt: shot.updatedAt, title: shot.title, description: intentDraft.description,
        durationFrames: shot.durationFrames, camera: intentDraft.camera, action: intentDraft.action,
        referenceIds: shot.referenceIds, constraints: shot.constraints,
      })
      onShotSaved(saved)
    } catch (error) { setIntentError(error instanceof Error ? error.message : 'Could not save the shot intent.') }
    finally { setIntentSaving(false) }
  }
  const saveContract = async (referenceIds: string[], constraints: string[], message: string) => {
    if (contractBusy || generationBusy) return
    setContractBusy(true); setContractError(undefined)
    try {
      const saved = await studioApi.updateShot(shot.id, {
        expectedUpdatedAt: shot.updatedAt, title: shot.title, description: shot.description,
        durationFrames: shot.durationFrames, camera: shot.camera, action: shot.action,
        referenceIds, constraints: constraints.map(value => value.trim()).filter(Boolean),
      })
      setConstraintsDraft(saved.constraints); setShowReferencePicker(false); setPinningReference(undefined)
      onShotContractSaved(saved, message)
    } catch (error) { setContractError(error instanceof Error ? error.message : 'Could not update this shot contract.') }
    finally { setContractBusy(false) }
  }
  const handleInspectorTabs = (event: React.KeyboardEvent<HTMLDivElement>) => {
    const tabs = ['intent', 'refs', 'rules', 'media'] as const
    const index = tabs.indexOf(tab)
    let next: typeof tab | undefined
    if (event.key === 'ArrowLeft') next = tabs[(index + tabs.length - 1) % tabs.length]
    if (event.key === 'ArrowRight') next = tabs[(index + 1) % tabs.length]
    if (event.key === 'Home') next = tabs[0]
    if (event.key === 'End') next = tabs[tabs.length - 1]
    if (!next) return
    event.preventDefault()
    setTab(next)
    requestAnimationFrame(() => document.getElementById(`${next}-tab`)?.focus())
  }
  // ── Candidate review loop ────────────────────────────────────────────────
  // Flipping candidates used to jump to the Review workspace, which loses the
  // inspector, the notes and your place. Everything below keeps you here.
  const [previewId, setPreviewId] = useState<string>()
  const [peeking, setPeeking] = useState(false)
  const [redirectBusy, setRedirectBusy] = useState(false)
  const [candidateBusy, setCandidateBusy] = useState(false)
  const [reviewError, setReviewError] = useState<string>()
  const markupGuide = useRef<{ revision: number; exportAsset: () => Promise<string> } | undefined>(undefined)
  const compositionGuide = useRef<{ exportAsset: () => Promise<string> } | undefined>(undefined)
  const rememberMarkupGuide = useCallback((guide?: { revision: number; exportAsset: () => Promise<string> }) => { markupGuide.current = guide }, [])
  const rememberCompositionGuide = useCallback((guide?: { exportAsset: () => Promise<string> }) => { compositionGuide.current = guide }, [])
  const preview = candidates.find(candidate => candidate.id === previewId && !candidate.isCurrent)
  const displayedVersion = preview && !peeking ? preview.version : shot.version
  const displayedComments = useMemo(() => studio.comments.filter(comment => comment.shotId === shot.id && comment.version === displayedVersion && comment.state === 'Open'), [displayedVersion, shot.id, studio.comments])
  const archivedPreview = Boolean(preview && !peeking)

  const clearDisplayedPins = async () => {
    if (archivedPreview || clearingPins || displayedComments.length === 0) return
    setClearingPins(true); setQuickGenerationError(undefined)
    try {
      // Resolve instead of deleting: the clean working frame loses the pins,
      // while the revision history still remembers what the artist asked for.
      for (const comment of displayedComments) await studioApi.resolveComment(comment.id)
      await onCandidatesChanged(`${displayedComments.length} pin${displayedComments.length === 1 ? '' : 's'} cleared from v${displayedVersion}. Their revision history is preserved.`)
    } catch (error) { setQuickGenerationError(error instanceof Error ? error.message : 'Could not clear the pins.') }
    finally { setClearingPins(false) }
  }

  useEffect(() => { setCanvasMode('frame'); setMarkupReady(false) }, [shot.id, shot.version])
  useEffect(() => { if (!implementationRequest) return; setFeedbackRequest(implementationRequest.direction); onImplementationConsumed() }, [implementationRequest, onImplementationConsumed])
  useEffect(() => { if (!sketchLaunch) return; setSketchPurpose('Draft'); setCanvasMode('sketch') }, [sketchLaunch])
  useEffect(() => { resetIntent() }, [resetIntent, shot.id, shot.updatedAt])
  useEffect(() => { if (!archivedPreview) return; resetIntent(); setConstraintsDraft(shot.constraints); setNewConstraint(''); setPinningReference(undefined); setShowReferencePicker(false); setTool('select') }, [archivedPreview, resetIntent, setTool, shot.constraints])
  useEffect(() => { setConstraintsDraft(shot.constraints); setNewConstraint(''); setContractError(undefined) }, [shot.id, shot.updatedAt, shot.constraints])
  useEffect(() => { void studioApi.candidates(shot.id).then(setCandidates) }, [shot.id, shot.version])
  const refreshMedia = useCallback(() => { void Promise.all([studioApi.assets(), studioApi.assetPlacements({ shotId: shot.id })]).then(([all, linked]) => { setMediaAssets(all); setMediaPlacements(linked) }) }, [shot.id])
  useEffect(() => { refreshMedia() }, [refreshMedia])
  // Snap back to the live frame whenever the shot moves on.
  useEffect(() => { setPreviewId(undefined); setPeeking(false) }, [shot.id, shot.version])
  // Warm every candidate image so flipping is instant rather than a flash of empty.
  useEffect(() => { for (const candidate of candidates) if (candidate.assetUrl) { const img = new Image(); img.src = candidate.assetUrl } }, [candidates])

  const redirect = async (direction: string) => {
    setRedirectBusy(true); setReviewError(undefined)
    try {
      const guide = markupGuide.current
      const guidanceAssetId = guide ? await guide.exportAsset() : undefined
      const trimmedDirection = direction.trim().slice(0, 2000)
      const feedbackHeading = '\n\nPINNED FEEDBACK ON THIS FRAME:\n'
      const feedbackBudget = Math.max(0, 2000 - trimmedDirection.length - feedbackHeading.length)
      const pinnedFeedback = comments
        .map((comment, index) => `- Pin ${index + 1} at ${Math.round(comment.x * 100)}% across / ${Math.round(comment.y * 100)}% down: ${comment.body}`)
        .join('\n')
        .slice(0, feedbackBudget)
      const completeDirection = pinnedFeedback ? `${trimmedDirection}${feedbackHeading}${pinnedFeedback}` : trimmedDirection
      await studioApi.redirectShot(shot.id, completeDirection, undefined, guidanceAssetId, guide?.revision)
      onJobQueued(); return true
    }
    catch (error) { setReviewError(error instanceof Error ? error.message : 'Could not queue that change.'); return false }
    finally { setRedirectBusy(false) }
  }
  const deleteCandidate = async (candidateId: string) => {
    setCandidateBusy(true); setReviewError(undefined)
    try {
      const candidate = candidates.find(item => item.id === candidateId)
      await studioApi.deleteCandidate(shot.id, candidateId)
      setPreviewId(current => (current === candidateId ? undefined : current))
      setCandidates(await studioApi.candidates(shot.id))
      await onCandidatesChanged(`Draft v${candidate?.version ?? ''} deleted.`)
    } catch (error) { setReviewError(error instanceof Error ? error.message : 'Could not delete that draft.') }
    finally { setCandidateBusy(false) }
  }
  const promoteCandidate = async (candidateId: string) => {
    const candidate = candidates.find(item => item.id === candidateId)
    if (!candidate || candidate.isCurrent || !candidate.assetId) return
    setCandidateBusy(true); setReviewError(undefined)
    try {
      const next = await studioApi.copyCandidateForward(shot.id, candidate.id, shot.version)
      setPreviewId(undefined)
      await onCandidatesChanged(`Draft v${candidate.version} promoted as new working v${next.version}.`)
    } catch (error) { setReviewError(error instanceof Error ? error.message : 'Could not promote that draft.') }
    finally { setCandidateBusy(false) }
  }
  const generateInitialDraft = async (adapterId: 'comfyui-fast-draft' | 'codex-imagegen') => {
    if (quickGenerating || generationBusy) return
    setQuickGenerating(adapterId); setQuickGenerationError(undefined)
    try {
      await studioApi.generateDraft(shot.id, shot.version, adapterId)
      onJobQueued()
    } catch (error) { setQuickGenerationError(error instanceof Error ? error.message : `Could not start the ${adapterId === 'codex-imagegen' ? 'Codex ImageGen' : 'ComfyUI'} draft.`) }
    finally { setQuickGenerating(undefined) }
  }
  const regenerateDirectly = async (adapterId: 'comfyui-fast-draft' | 'codex-imagegen') => {
    if (directRegenerating || generationBusy) return
    setDirectRegenerating(adapterId); setQuickGenerationError(undefined)
    try {
      const guide = markupGuide.current
      const guidanceAssetId = guide && shot.currentAssetId ? await guide.exportAsset() : undefined
      const storyboardGuideId = !guidanceAssetId && !shot.currentAssetId ? await compositionGuide.current?.exportAsset() : undefined
      const sourceAssetId = guidanceAssetId ?? shot.currentAssetId ?? storyboardGuideId
      const sourceContract = guidanceAssetId || shot.currentAssetId
        ? `CURRENT FRAME EDIT: Use the attached current generated frame v${shot.version} as the visual starting point. Preserve all content not named by the requested change; do not restart from the original sketch.`
        : storyboardGuideId
          ? 'STORYBOARD COMPOSITION GUIDE: Use the attached clean storyboard frame as the binding layout. Replace simplified figures and shapes with finished artwork while preserving subject count, placement, camera, horizon, and negative space.'
          : 'FEEDBACK-GUIDED DRAFT: Generate from shot intent, approved authorities, locked constraints, and the pinned feedback.'
      const pinned = comments.map((comment, index) => { const reference = references.find(item => item.id === comment.referenceId); const label = reference ? `Reference ${reference.name} v${comment.referenceVersion ?? reference.version}` : `Pin ${index + 1}`; return `- ${label} at ${Math.round(comment.x * 100)}% across / ${Math.round(comment.y * 100)}% down: ${comment.body}` }).join('\n').slice(0, 2500)
      let brief = `${shot.description.slice(0, 800)}\nCamera: ${shot.camera.slice(0, 300)}.\nAction: ${shot.action.slice(0, 800)}.\n\nREQUESTED CHANGE: Apply every pinned note to this revision while preserving all unmentioned content.\n\n${sourceContract}`
      if (pinned) brief += `\n\nPINNED FEEDBACK ON THIS REVISION:\n${pinned}`
      if (guidanceAssetId) brief += '\n\nFRAME EDIT GUIDE: The attached current frame contains temporary gold markup. Use it only to locate edits, then remove every trace of the markup.'
      await studioApi.generateDraft(shot.id, shot.version, adapterId, {
        compositionAssetId: sourceAssetId,
        creativeBriefOverride: brief,
        markupRevision: guidanceAssetId ? guide?.revision : undefined,
        allowSketchCompositionFallback: false,
      })
      onJobQueued()
    } catch (error) { setQuickGenerationError(error instanceof Error ? error.message : `Could not start ${adapterId === 'codex-imagegen' ? 'Codex ImageGen' : 'the fast local pass'}.`) }
    finally { setDirectRegenerating(undefined) }
  }
  const promoteLatestVideo = async () => {
    if (!latestVideoTake || currentVideoQuality === 'Max' || videoPromoting || generationBusy) return
    setVideoPromoting(true); setQuickGenerationError(undefined)
    try {
      await studioApi.promoteVideoTake(shot.id, latestVideoTake.id, shot.version)
      onJobQueued()
    } catch (error) { setQuickGenerationError(error instanceof Error ? error.message : 'Could not promote this take to max quality.') }
    finally { setVideoPromoting(false) }
  }
  const saveVideoEndpoints = async (firstFrameCandidateId?: string, lastFrameCandidateId?: string) => {
    if (endpointBusy || generationBusy) return
    setEndpointBusy(true); setEndpointError(undefined)
    try {
      const saved = await studioApi.updateVideoEndpoints(shot.id, {
        expectedUpdatedAt: shot.updatedAt,
        firstFrameCandidateId,
        lastFrameCandidateId,
      })
      onShotSaved(saved)
    } catch (error) {
      setEndpointError(error instanceof Error ? error.message : 'Could not save the video endpoints.')
      throw error
    } finally { setEndpointBusy(false) }
  }
  if (canvasMode === 'sketch') return <Suspense fallback={<main className="workspace workspace-loading" role="status"><LoaderCircle className="spin" /><p>Opening composition lab</p></main>}><SketchWorkspace key={`${shot.id}-${sketchPurpose}`} shot={shot} references={references} purpose={sketchPurpose} launch={sketchLaunch} onLaunchConsumed={onSketchLaunchConsumed} onOpenFrame={() => setCanvasMode('frame')} onJobQueued={onJobQueued} /></Suspense>
  return <main className="workspace shot-workspace" data-testid="shot-workspace" style={{ '--project-aspect': `${studio.project.deliveryWidth} / ${studio.project.deliveryHeight}` } as React.CSSProperties}>
    <aside className="version-rail">
      <div className="rail-label">Versions</div>
      {(['Sketch', 'Draft', 'Final', 'Video'] as const).map((stage, index) => {
        const active = shot.stage === stage
        const available = index <= ['Sketch', 'Draft', 'Final', 'Video'].indexOf(shot.stage)
        const interactive = stage === 'Sketch' || active || candidates.some(candidate => candidate.stage === stage)
        const note = active ? `Current · v${shot.version}`
          : stage === 'Sketch' ? 'Open the sketch lab'
          : candidates.some(candidate => candidate.stage === stage) ? 'Open version compare'
          : 'Not started'
        return <button type="button" key={stage} disabled={!interactive} onClick={() => stage === 'Sketch' ? openSketch('Draft') : active ? setCanvasMode('frame') : onOpenReview()} className={`version-slot ${active ? 'active' : ''} ${available ? 'available' : ''}`} aria-current={active ? 'step' : undefined}>
          <span className="version-marker">{available ? (active ? shot.version : '✓') : '—'}</span><span><strong>{stage}</strong><small>{note}</small></span>
        </button>
      })}
      <div className="rail-history"><span>Candidate stack</span>{candidates.slice(0, 7).map(candidate => <button type="button" key={candidate.id} aria-pressed={candidate.id === (preview?.id ?? candidates.find(item => item.isCurrent)?.id)} className={`${candidate.isCurrent ? 'current' : ''} ${preview?.id === candidate.id ? 'selected' : ''}`} onClick={() => { setPreviewId(candidate.isCurrent ? undefined : candidate.id); setTool('select') }}><strong>v{candidate.version}</strong><small>{candidate.isCurrent ? 'Live' : candidate.stage} · {candidate.approval}{candidate.assetId ? ' · image' : ' · intent'}</small></button>)}</div>
    </aside>
    <section className="shot-center">
      <div className="canvas-toolbar">
        <div className="segmented tools" role="group" aria-label="Canvas tools">
          <button aria-label="Select and pan" aria-pressed={tool === 'select'} className={tool === 'select' ? 'active' : ''} onClick={() => setTool('select')}><Scan size={17} /></button>
          <button aria-label="Draw annotation" aria-pressed={tool === 'draw'} className={tool === 'draw' ? 'active' : ''} disabled={!markupReady || archivedPreview} title={archivedPreview ? 'Promote this draft before adding new markup' : markupReady ? 'Draw durable version-bound markup' : 'Loading saved markup'} onClick={() => setTool('draw')}><PenLine size={17} /></button>
          <button aria-label="Place comment" aria-pressed={tool === 'comment' && !pinningReference} className={tool === 'comment' && !pinningReference ? 'active' : ''} disabled={archivedPreview} title={archivedPreview ? 'Promote this draft before adding new feedback' : 'Pin feedback to this version'} onClick={() => { setPinningReference(undefined); setTool('comment') }}><MessageCircle size={17} /></button>
        </div>
        <div className="canvas-title"><span>{shot.code}</span><strong>{shot.title}</strong><em>{tool === 'draw' ? 'Markup' : pinningReference ? `Place ${pinningReference.name}` : tool === 'comment' ? 'Place a pin' : 'Review'}</em></div>
        <div className="toolbar-actions shot-toolbar-actions"><button onClick={() => openSketch('Draft')}><PenLine size={17} />Sketch</button><button onClick={onEdit} disabled={archivedPreview} title={archivedPreview ? 'Promote this draft before editing the live shot contract' : 'Edit shot intent'}><Clapperboard size={17} />Edit intent</button><button onClick={() => onOpenReview(preview?.id)}><Layers3 size={17} />{preview ? `Compare v${preview.version} to latest` : 'Compare'}</button></div>
      </div>
      <ShotCanvas shot={shot} comments={displayedComments} references={studio.references} displayVersion={displayedVersion} readOnly={archivedPreview} tool={tool} placingReference={pinningReference} referenceDrop={draggingReference} onAddComment={(x, y) => { onAddComment(x, y, pinningReference); setPinningReference(undefined) }} onMoveComment={onMoveComment} onResolveComment={onResolveReferencePin} onMarkupReady={setMarkupReady} onMarkupGuideReady={rememberMarkupGuide} onCompositionGuideReady={rememberCompositionGuide} deliveryWidth={studio.project.deliveryWidth} deliveryHeight={studio.project.deliveryHeight} videoUrl={currentVideoUrl}
        previewCandidate={preview} peeking={peeking} activeJob={activeJob} />
      <VideoEndpointStrip shot={shot} candidates={candidates} busy={endpointBusy || generationBusy} error={endpointError} onChange={saveVideoEndpoints}
        onView={candidate => { setPreviewId(candidate.isCurrent ? undefined : candidate.id); setPeeking(false); setTool('select') }}
        onCreateLastFrame={() => setLastFrameOpen(true)} />
      <CandidateReviewBar
        shot={shot} candidates={candidates} selectedId={previewId} onSelect={setPreviewId}
        onPeek={setPeeking} generationBusy={generationBusy}
        onRedirect={redirect} onDelete={deleteCandidate} onPromote={promoteCandidate} onCompare={candidateId => onOpenReview(candidateId)} onComment={() => { setPinningReference(undefined); setTool('comment') }}
        busy={redirectBusy || candidateBusy} error={reviewError} onDismissError={() => setReviewError(undefined)} />
    </section>
    <aside className="shot-inspector">
      <div className="inspector-tabs" role="tablist" aria-label="Shot details" onKeyDown={handleInspectorTabs}><button id="intent-tab" role="tab" aria-selected={tab === 'intent'} aria-controls="shot-details-panel" tabIndex={tab === 'intent' ? 0 : -1} onClick={() => setTab('intent')} className={tab === 'intent' ? 'active' : ''}>Intent</button><button id="refs-tab" role="tab" aria-selected={tab === 'refs'} aria-controls="shot-details-panel" tabIndex={tab === 'refs' ? 0 : -1} onClick={() => setTab('refs')} className={tab === 'refs' ? 'active' : ''}>References <span>{references.length}</span></button><button id="rules-tab" role="tab" aria-selected={tab === 'rules'} aria-controls="shot-details-panel" tabIndex={tab === 'rules' ? 0 : -1} onClick={() => setTab('rules')} className={tab === 'rules' ? 'active' : ''}>Rules <span>{shot.constraints.length}</span></button><button id="media-tab" role="tab" aria-selected={tab === 'media'} aria-controls="shot-details-panel" tabIndex={tab === 'media' ? 0 : -1} onClick={() => setTab('media')} className={tab === 'media' ? 'active' : ''}>Media <span>{mediaPlacements.length}</span></button></div>
      <div id="shot-details-panel" className="inspector-scroll" role="tabpanel" aria-labelledby={`${tab}-tab`} tabIndex={0}>
        {tab === 'intent' && <>
          {archivedPreview && <ArchivedContractNotice version={displayedVersion} />}
          <InspectorSection title="Shot intent"><label>Description<textarea aria-label="Shot description" value={intentDraft.description} disabled={intentSaving || generationBusy || archivedPreview} onChange={event => setIntentDraft(current => ({ ...current, description: event.target.value }))} /></label><label>Action<textarea aria-label="Shot action" value={intentDraft.action} disabled={intentSaving || generationBusy || archivedPreview} onChange={event => setIntentDraft(current => ({ ...current, action: event.target.value }))} /></label></InspectorSection>
          <InspectorSection title="Camera"><CameraGuide value={intentDraft.camera} disabled={intentSaving || generationBusy || archivedPreview} onChange={camera => setIntentDraft(current => ({ ...current, camera }))} /></InspectorSection>
          {shot.approval === 'Ratified' && intentDirty && <p className="intent-authority-warning"><LockKeyhole size={14} />Saving preserves the ratified authority and opens a new working sketch version.</p>}
          {intentError && <p className="form-error" role="alert">{intentError}</p>}
          <div className={`intent-edit-actions ${intentDirty ? 'visible' : ''}`} aria-live="polite"><span>{archivedPreview ? 'Archived contract is read-only' : intentDirty ? 'Unsaved intent changes' : 'Intent is saved'}</span><button className="secondary compact" onClick={resetIntent} disabled={!intentDirty || intentSaving || archivedPreview}>Reset</button><button className="primary compact" onClick={() => void saveIntent()} disabled={!intentDirty || intentSaving || generationBusy || archivedPreview}>{intentSaving ? <LoaderCircle className="spin" size={14} /> : <Check size={14} />}{intentSaving ? 'Saving…' : 'Save intent'}</button></div>
          <button className="codex-note" onClick={onAskCodex} disabled={codexBusy || archivedPreview}><Sparkles size={17} /><span><strong>{codexBusy ? 'Codex is reviewing…' : archivedPreview ? 'Promote before refining' : 'Ask Codex to refine the shot'}</strong><small>{archivedPreview ? 'Codex actions apply only to the live working head' : 'Directing, pacing, camera language'}</small></span><ArrowRight size={16} /></button>
        </>}
        {tab === 'refs' && <>
          {archivedPreview && <ArchivedContractNotice version={displayedVersion} />}
          <div className="packet-summary"><LockKeyhole size={16} /><span><strong>Shot references</strong><small>{references.length} approved {references.length === 1 ? 'version' : 'versions'} · used in the next generation</small></span><button type="button" className="secondary compact" onClick={() => setShowReferencePicker(value => !value)} disabled={contractBusy || generationBusy || archivedPreview}><Plus size={14} />Add</button></div>
          {pinningReference && <div className="reference-pin-mode" role="status"><BadgeCheck size={16} /><span><strong>Place {pinningReference.name} v{pinningReference.version}</strong><small>Now tap the exact face, object, or region on the frame.</small></span><button type="button" onClick={() => { setPinningReference(undefined); setTool('select') }}>Cancel</button></div>}
          {showReferencePicker && !archivedPreview && <div className="reference-picker"><strong>Available authorities</strong>{studio.references.filter(reference => !shot.referenceIds.includes(reference.id)).map(reference => <button type="button" key={reference.id} disabled={contractBusy} onClick={() => void saveContract([...shot.referenceIds, reference.id], shot.constraints, `${reference.name} added to ${shot.code}.`)}><span><b>{reference.name}</b><small>{reference.category} · v{reference.version}</small></span><Plus size={15} /></button>)}{studio.references.every(reference => shot.referenceIds.includes(reference.id)) && <small>Every approved authority is already attached.</small>}</div>}
          <div className="reference-stack editable">{references.map(ref => <ShotReferenceCard key={ref.id} reference={ref} pins={displayedComments.filter(comment => comment.referenceId === ref.id)} busy={contractBusy || generationBusy} readOnly={archivedPreview} onPin={() => { setPinningReference(ref); setTool('comment') }} onDropPin={(x, y) => { setPinningReference(undefined); setTool('select'); onAddComment(x, y, ref) }} onDragState={active => setDraggingReference(active ? ref : undefined)} onRemove={() => void saveContract(shot.referenceIds.filter(id => id !== ref.id), shot.constraints, `${ref.name} removed from ${shot.code}.`)} onResolvePin={onResolveReferencePin} />)}</div>
          {references.length === 0 && <p className="inspector-empty">No references attached. Add an approved authority to guide this shot.</p>}
          {shot.approval === 'Ratified' && !archivedPreview && <p className="intent-authority-warning"><LockKeyhole size={14} />Changing the references preserves the approved version and opens a new working sketch.</p>}
          {contractError && <p className="form-error" role="alert">{contractError}</p>}
          <p className="inspector-footnote">The selected ComfyUI workflow can use a limited number of reference images alongside the current frame or sketch. Character identities are chosen first; pinned references take priority. Any remaining authority still guides the result through its approved description and rules.</p>
          <p className="inspector-footnote">Drag the placement button onto the exact target, or tap it and then tap the frame. The next generation will use that authority version at this location. If a reference image conflicts with a locked rule, the rule wins.</p>
        </>}
        {tab === 'rules' && <>{archivedPreview && <ArchivedContractNotice version={displayedVersion} />}<div className="rule-heading"><span>Shot constraints</span><small>World truth stays separate from crop</small></div><div className="rule-editor">{constraintsDraft.map((rule, i) => <div className="rule-edit-row" key={`${i}-${shot.id}`}><span>{i + 1}</span><input aria-label={`Constraint ${i + 1}`} value={rule} disabled={contractBusy || generationBusy || archivedPreview} onChange={event => setConstraintsDraft(current => current.map((item, index) => index === i ? event.target.value : item))} /><button type="button" aria-label={`Delete constraint ${i + 1}`} disabled={contractBusy || generationBusy || archivedPreview} onClick={() => setConstraintsDraft(current => current.filter((_, index) => index !== i))}><Trash2 size={14} /></button></div>)}<form className="rule-add" onSubmit={event => { event.preventDefault(); if (!newConstraint.trim() || archivedPreview) return; setConstraintsDraft(current => [...current, newConstraint.trim()]); setNewConstraint('') }}><input aria-label="New constraint" value={newConstraint} disabled={contractBusy || generationBusy || archivedPreview} onChange={event => setNewConstraint(event.target.value)} placeholder={archivedPreview ? 'Promote this draft to edit rules' : 'Add a concrete visual rule…'} /><button type="submit" className="secondary compact" disabled={!newConstraint.trim() || contractBusy || archivedPreview}><Plus size={14} />Add</button></form></div>{shot.approval === 'Ratified' && !archivedPreview && constraintsDraft.join('\n') !== shot.constraints.join('\n') && <p className="intent-authority-warning"><LockKeyhole size={14} />Saving preserves the ratified version and opens a new working sketch.</p>}{contractError && <p className="form-error" role="alert">{contractError}</p>}<div className={`contract-edit-actions ${constraintsDraft.join('\n') !== shot.constraints.join('\n') ? 'visible' : ''}`}><span>{archivedPreview ? 'Archived rules are read-only' : constraintsDraft.join('\n') !== shot.constraints.join('\n') ? 'Unsaved rule changes' : 'Rules are saved'}</span><button type="button" className="secondary compact" disabled={contractBusy || archivedPreview || constraintsDraft.join('\n') === shot.constraints.join('\n')} onClick={() => setConstraintsDraft(shot.constraints)}>Reset</button><button type="button" className="primary compact" disabled={contractBusy || generationBusy || archivedPreview || constraintsDraft.join('\n') === shot.constraints.join('\n')} onClick={() => void saveContract(shot.referenceIds, constraintsDraft, `${shot.code} rules updated.`)}>{contractBusy ? <LoaderCircle className="spin" size={14} /> : <Check size={14} />}{contractBusy ? 'Saving…' : 'Save rules'}</button></div><p className="inspector-footnote">Rules are literal production constraints. Off-camera world facts stay true even when they are not visible in this crop.</p></>}
        {tab === 'media' && <><div className="packet-summary"><Film size={16} /><span><strong>Working media</strong><small>Guides and takes · never silent authority replacement</small></span><button className="secondary compact" onClick={onOpenAssets}><Plus size={14} />Add from library</button></div><div className="shot-media-stack">{mediaPlacements.map(placement => { const asset = mediaAssets.find(item => item.id === placement.assetId); if (!asset) return null; return <article key={placement.id}><div>{asset.kind === 'Image' ? <img src={asset.contentUrl} alt="" /> : asset.kind === 'Video' ? <video src={asset.contentUrl} muted preload="metadata" /> : <Music2 />}</div><span><strong>{asset.displayName}</strong><small>{placement.role} · {asset.source}</small></span><button aria-label={`Remove ${asset.displayName} from ${shot.code}`} onClick={async () => { await studioApi.removeAssetPlacement(placement.id); refreshMedia() }}><X size={14} /></button></article>})}</div>{mediaPlacements.length === 0 && <div className="inspector-empty media-empty"><Library size={24} /><p>No working media attached. Add an image guide, video take, or audio cue from the Asset library.</p><button className="secondary compact" onClick={onOpenAssets}>Open Asset library</button></div>}<p className="inspector-footnote">Attached media is contextual material. It does not alter the shot’s ratified image, approved references, or locked constraints.</p></>}
      </div>
      <div className="generation-panel">
        {archivedPreview ? <div className="archived-preview-note"><Layers3 size={17} /><span><strong>Viewing archived v{preview?.version}</strong><small>Its feedback is shown read-only. Use Promote draft below to make it the new working head.</small></span></div> : <>
          <div className="generation-state"><StageBadge shot={shot} /><span>v{shot.version} · {shot.openComments} open notes</span></div>
          {draftWorkflow && ((!activeJob && shot.stage === 'Sketch') || activeJob?.adapterId === 'comfyui-fast-draft') && <div className={`workflow-badge ${activeJob ? 'running' : ''}`} title={draftWorkflow.summary}><Cpu size={15} /><span><small>Fast local workflow</small><strong>{draftWorkflow.name}</strong></span><em>{draftWorkflow.mode}</em></div>}
          {activeJob?.adapterId === 'codex-imagegen' && <div className="workflow-badge running" title="High-fidelity image generation through your Codex ChatGPT login"><Sparkles size={15} /><span><small>Precision image engine</small><strong>Codex ImageGen</strong></span><em>ChatGPT login</em></div>}
          {activeJob && <div className="active-job-note" role="status"><LoaderCircle className="spin" size={14} />{activeJob.phase}</div>}
          {shot.stage === 'Sketch' && <p className="inspector-footnote">Generate from the description now, or add a sketch when composition needs tighter control.</p>}
          {shot.stage === 'Sketch' ? <div className="generation-action-stack initial-draft-actions"><button className="primary" onClick={() => void generateInitialDraft('comfyui-fast-draft')} disabled={generationBusy || Boolean(quickGenerating)}><WandSparkles size={18} />{quickGenerating === 'comfyui-fast-draft' ? 'Starting ComfyUI…' : 'Generate fast draft'}</button><button className="secondary" onClick={() => void generateInitialDraft('codex-imagegen')} disabled={generationBusy || Boolean(quickGenerating)}><Sparkles size={17} />{quickGenerating === 'codex-imagegen' ? 'Starting Codex ImageGen…' : 'Generate with Codex'}</button></div>
            : shot.stage === 'Video' && latestVideoTake ? <div className="video-take-actions"><div className="video-quality-status"><Film size={16} /><span><strong>{currentVideoQuality ?? 'Low'} quality take</strong><small>{currentVideoQuality === 'Max' ? shot.approval === 'Ratified' ? 'Immutable production video · approved' : 'Maximum production pass ready for approval' : 'Review motion, then promote this exact take or try another pass'}</small></span></div>{currentVideoQuality === 'Max' ? shot.approval === 'Ratified' ? <button className="primary" disabled><BadgeCheck size={18} />Max video ratified</button> : <button className="primary" onClick={onRatify} disabled={generationBusy}><BadgeCheck size={18} />Ratify max video</button> : <button className="primary" onClick={() => void promoteLatestVideo()} disabled={generationBusy || videoPromoting}><WandSparkles size={18} />{videoPromoting ? 'Promoting to max…' : `Promote ${currentVideoQuality ?? 'Low'} take to Max`}</button>}<button className="secondary" onClick={() => setVideoOpen(true)} disabled={generationBusy || videoPromoting}><Film size={17} />Generate another pass</button></div>
            : <div className="generation-action-stack">
              {comments.length > 0
                ? <><button className="primary" onClick={() => void regenerateDirectly('codex-imagegen')} disabled={generationBusy || Boolean(directRegenerating)}><Sparkles size={18} />{directRegenerating === 'codex-imagegen' ? 'Starting Codex…' : `Regenerate with Codex · ${comments.length} note${comments.length === 1 ? '' : 's'}`}</button><div className="quick-revision-options"><button className="secondary" onClick={() => void regenerateDirectly('comfyui-fast-draft')} disabled={generationBusy || Boolean(directRegenerating)}><RotateCcw size={16} />{directRegenerating === 'comfyui-fast-draft' ? 'Starting fast pass…' : 'Fast local pass'}</button><button className="secondary" onClick={() => setFeedbackRequest('Apply every pinned note to this revision while preserving all unmentioned content.')} disabled={generationBusy || Boolean(directRegenerating)}>More options</button></div></>
                : shot.stage === 'Draft'
                  ? <button className="primary" onClick={() => setFinalOpen(true)} disabled={generationBusy}><WandSparkles size={18} />Create production final</button>
                  : <button className="primary" onClick={() => setVideoOpen(true)} disabled={generationBusy || !shot.currentAssetId}><Film size={18} />Generate video</button>}
              {comments.length === 0 && <button className="secondary" onClick={() => setFeedbackRequest('Describe the requested change for the next revision.')} disabled={generationBusy}><RotateCcw size={17} />Regenerate this image</button>}
              {shot.stage === 'Draft' && comments.length > 0 && <button className="secondary" onClick={() => setFinalOpen(true)} disabled={generationBusy}><WandSparkles size={17} />Create production final</button>}
              {shot.currentAssetId && (shot.stage === 'Draft' || comments.length > 0 && shot.stage === 'Final') && <button className="secondary" onClick={() => setVideoOpen(true)} disabled={generationBusy}><Film size={17} />Generate video from this image</button>}
              {shot.approval !== 'Ratified' && <button className="secondary" onClick={onRatify} disabled={generationBusy}><BadgeCheck size={17} />Mark this version approved</button>}
              {displayedComments.length > 0 && <button className="secondary clear-pins-action" onClick={() => void clearDisplayedPins()} disabled={generationBusy || clearingPins}><X size={17} />{clearingPins ? 'Clearing pins…' : displayedComments.length === 1 ? 'Clear pin' : `Clear all ${displayedComments.length} pins`}</button>}
            </div>}
          {comments.length > 0 && <p className="feedback-gate-copy"><MessageCircle size={14} /><span>Open notes are revision instructions. Regenerate first; mark them resolved only after the new frame passes review.</span></p>}
          {quickGenerationError && <p className="generation-quick-error" role="alert"><CircleAlert size={14} />{quickGenerationError}</p>}
        </>}
      </div>
    </aside>
    {finalOpen && <ProductionFinalDialog shot={shot} references={references} onClose={() => setFinalOpen(false)} onRepairRequested={audit => { setFinalOpen(false); setRepairAudit(audit) }} onQueued={() => { setFinalOpen(false); onJobQueued() }} />}
    {repairAudit && <VisualReconciliationDialog shot={shot} audit={repairAudit} startInRepairMode onClose={() => setRepairAudit(undefined)} onChanged={onCandidatesChanged} onReturnToShot={() => setRepairAudit(undefined)} />}
    {videoOpen && <VideoGenerationDialog shot={shot} project={studio.project} onSaveEndpoints={saveVideoEndpoints} onClose={() => setVideoOpen(false)} onQueued={() => { setVideoOpen(false); onJobQueued() }} />}
    {lastFrameOpen && <LastFrameCreationDialog shot={shot} candidates={candidates} referenceCount={references.length} onClose={() => setLastFrameOpen(false)} onQueued={() => { setLastFrameOpen(false); onJobQueued() }} />}
    {feedbackRequest !== undefined && <FeedbackRegenerationDialog shot={shot} comments={comments} references={references} initialDirection={feedbackRequest} markupGuide={markupGuide.current} compositionGuide={compositionGuide.current} onClose={() => setFeedbackRequest(undefined)} onQueued={() => { setFeedbackRequest(undefined); onJobQueued() }} />}
  </main>
}

function MusicLibraryDialog({ jobs, framesPerSecond, onClose, onPlaced, onJobQueued }: { jobs: JobSummary[]; framesPerSecond: number; onClose: () => void; onPlaced: () => void; onJobQueued: (job: JobSummary) => void }) {
  return <Dialog className="edit-dialog music-library music-composition-dialog" onClose={onClose} labelledBy="music-library-title">
      <header><div><p className="eyebrow">Editorial audio</p><h2 id="music-library-title">Music composition <em className="preview-tag">YuE2</em></h2></div><button onClick={onClose} aria-label="Close music composition">×</button></header>
      <div className="edit-dialog-body music-library-body" data-testid="music-library">
        <MusicCompositionWorkspace jobs={jobs} framesPerSecond={framesPerSecond} onPlaced={onPlaced} onJobQueued={onJobQueued} />
      </div>
  </Dialog>
}

/**
 * The candidate review loop. Everything an artist does between "a render came
 * back" and "try again" lives here, directly under the canvas:
 *
 *   ← / →   flip through candidates without leaving the page
 *   Hold \  peek at the live frame, release to return — the fastest A/B there is
 *   C       drop a pin on the image
 *   Delete  bin a bad draft
 *   type    say what should change and send it straight back to the renderer
 *
 * The whole point is that giving an opinion costs one keystroke. Anything that
 * makes you navigate, wait, or re-find your place defeats it.
 */
function CandidateReviewBar({ shot, candidates, selectedId, onSelect, onPeek, onRedirect, onDelete, onPromote, onCompare, onComment, busy, generationBusy, error, onDismissError }: {
  shot: ShotSummary; candidates: CandidateVersionSummary[]; selectedId?: string
  onSelect: (id: string | undefined) => void; onPeek: (peeking: boolean) => void
  onRedirect: (direction: string) => Promise<boolean>; onDelete: (id: string) => void; onPromote: (id: string) => void; onCompare: (id: string) => void; onComment: () => void
  busy: boolean; generationBusy: boolean; error?: string; onDismissError: () => void
}) {
  const [direction, setDirection] = useState('')
  const input = useRef<HTMLTextAreaElement>(null)
  const strip = useRef<HTMLDivElement>(null)
  // Newest first: the thing you just rendered is the thing you are judging.
  const ordered = useMemo(() => [...candidates].sort((a, b) => b.version - a.version), [candidates])
  const index = selectedId ? ordered.findIndex(x => x.id === selectedId) : ordered.findIndex(x => x.isCurrent)
  const selected = ordered[index] ?? ordered.find(x => x.isCurrent)
  const reviewingArchived = Boolean(selected && !selected.isCurrent)

  const step = useCallback((delta: number) => {
    if (ordered.length === 0) return
    const from = index < 0 ? 0 : index
    const next = ordered[Math.min(ordered.length - 1, Math.max(0, from + delta))]
    if (!next) return
    onSelect(next.isCurrent ? undefined : next.id)
    document.getElementById(`candidate-${next.id}`)?.scrollIntoView({ block: 'nearest', inline: 'center' })
  }, [index, onSelect, ordered])

  useEffect(() => {
    const keydown = (event: KeyboardEvent) => {
      // Guard on the element type: a keydown can be dispatched with a non-element
      // target, and calling .closest() on it would throw inside a global listener.
      const target = event.target
      if (target instanceof Element && target.closest('input, textarea, select, button, a, [contenteditable], [role="tab"], [role="slider"]')) return
      if (event.metaKey || event.ctrlKey || event.altKey) return
      if (event.key === 'ArrowLeft') { event.preventDefault(); step(-1) }
      else if (event.key === 'ArrowRight') { event.preventDefault(); step(1) }
      else if (event.key === '\\') { event.preventDefault(); onPeek(true) }
      else if (event.key.toLowerCase() === 'c') { event.preventDefault(); onComment() }
      else if (event.key.toLowerCase() === 'n') { event.preventDefault(); input.current?.focus() }
      else if (event.key === 'Delete' || event.key === 'Backspace') {
        if (!selected || selected.isCurrent) return
        event.preventDefault(); onDelete(selected.id)
      }
    }
    const keyup = (event: KeyboardEvent) => { if (event.key === '\\') onPeek(false) }
    window.addEventListener('keydown', keydown); window.addEventListener('keyup', keyup)
    return () => { window.removeEventListener('keydown', keydown); window.removeEventListener('keyup', keyup) }
  }, [onComment, onDelete, onPeek, selected, step])

  const send = async () => {
    const text = direction.trim()
    if (!text || busy) return
    if (await onRedirect(text)) setDirection('')
  }

  return <div className="review-bar" data-testid="candidate-review-bar">
    <div className="review-strip-row">
      <div className="review-strip" ref={strip} role={ordered.length > 0 ? 'listbox' : 'status'} aria-label="Candidate versions" aria-orientation={ordered.length > 0 ? 'horizontal' : undefined}>
        {ordered.map(candidate => {
          const active = candidate.id === selected?.id
          return <button
            key={candidate.id} id={`candidate-${candidate.id}`} role="option" aria-selected={active}
            className={`review-chip ${active ? 'active' : ''} ${candidate.isCurrent ? 'is-current' : ''}`}
            onClick={() => onSelect(candidate.isCurrent ? undefined : candidate.id)}
            title={`v${candidate.version} · ${candidate.stage} · ${candidate.approval}`}>
            {candidate.assetUrl
              ? <img src={candidate.assetUrl} alt="" loading="lazy" />
              : <span className="review-chip-empty"><Film size={14} /></span>}
            <em>v{candidate.version}</em>
            {candidate.isCurrent && <i className="review-chip-live" aria-label="Live version"><BadgeCheck size={12} /></i>}
          </button>
        })}
        {ordered.length === 0 && <span className="review-strip-empty">No candidates yet — generate one to start reviewing.</span>}
      </div>
      <div className={`review-strip-meta ${selected && !selected.isCurrent ? 'has-decision' : ''}`}>
        {selected && <><strong>v{selected.version}</strong><small>{selected.isCurrent ? 'Live' : 'Superseded'} · {selected.stage}</small></>}
        <kbd>←</kbd><kbd>→</kbd><span>flip</span><kbd>\</kbd><span>peek live</span>
        {selected && !selected.isCurrent && <div className="selected-draft-actions"><button type="button" className="compare-selected" onClick={() => onCompare(selected.id)} disabled={busy || generationBusy}><Layers3 size={14} />Compare to latest</button><button type="button" className="promote-selected" onClick={() => onPromote(selected.id)} disabled={busy || generationBusy || !selected.assetId}><BadgeCheck size={14} />Promote draft</button><button type="button" className="delete-selected" onClick={() => onDelete(selected.id)} disabled={busy || generationBusy || selected.approval === 'Ratified'} title={selected.approval === 'Ratified' ? 'Ratified history cannot be deleted' : `Delete draft v${selected.version}`}><Trash2 size={14} />Delete draft</button></div>}
      </div>
    </div>

    <form className="review-direction" onSubmit={event => { event.preventDefault(); void send() }}>
      <label htmlFor="redirect-input" className="sr-only">What should change?</label>
      <textarea
        id="redirect-input" ref={input} rows={1} value={direction} disabled={busy || generationBusy || reviewingArchived}
        placeholder={reviewingArchived ? 'Promote this draft to make changes' : generationBusy ? 'A render is already running…' : 'Describe one change, then apply it to generate the next revision — press N'}
        onChange={event => setDirection(event.target.value)}
        onKeyDown={event => { if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); void send() } }} />
      <div className="review-actions">
        <button type="button" className="ghost" disabled={reviewingArchived} onClick={onComment} title={reviewingArchived ? 'Promote this draft before adding new feedback' : 'Pin a note to a point on the frame (C)'}><MessageCircle size={16} />Pin</button>
        <button type="submit" className="primary" disabled={!direction.trim() || busy || generationBusy || reviewingArchived}>
          {busy ? <LoaderCircle className="spin" size={16} /> : <RotateCcw size={16} />}
          {busy ? 'Starting revision…' : 'Apply & regenerate'}
        </button>
      </div>
    </form>
    {error && <p className="review-error" role="alert"><CircleAlert size={14} />{error}<button onClick={onDismissError} aria-label="Dismiss">×</button></p>}
    <p className="sr-only" aria-live="polite">{selected ? `Showing version ${selected.version}, ${selected.isCurrent ? 'live' : 'superseded'}` : `Showing ${shot.code} live frame`}</p>
  </div>
}

function InspectorSection({ title, children }: { title: string; children: React.ReactNode }) { return <section className="inspector-section"><h3>{title}</h3>{children}</section> }
function ReferenceRow({ reference }: { reference: ReferenceSummary }) { return <div className="reference-row"><div className="reference-swatch" style={{ '--accent': reference.accent } as React.CSSProperties}>{reference.imageUrl ? <img src={reference.imageUrl} alt="" /> : <Artwork variant={reference.visualVariant} muted />}</div><span><strong>{reference.name}</strong><small>{reference.category} · v{reference.version} · {reference.imageAssetId ? 'visual authority' : 'text only'}</small></span>{reference.imageAssetId ? <BadgeCheck size={16} /> : <CircleAlert size={16} />}</div> }
function ArchivedContractNotice({ version }: { version: number }) { return <p className="archived-contract-notice"><Layers3 size={14} /><span><strong>Archived image and notes · v{version}</strong>The inspector below shows the live shot contract for context. Promote this draft before changing that contract.</span></p> }
function ShotReferenceCard({ reference, pins, busy, readOnly, onPin, onDropPin, onDragState, onRemove, onResolvePin }: { reference: ReferenceSummary; pins: CommentSummary[]; busy: boolean; readOnly: boolean; onPin: () => void; onDropPin: (x: number, y: number) => void; onDragState: (active: boolean) => void; onRemove: () => void; onResolvePin: (id: string) => void }) {
  const drag = useRef<{ pointerId: number; startX: number; startY: number; moved: boolean } | null>(null)
  const suppressClick = useRef(false)
  const [ghost, setGhost] = useState<{ x: number; y: number }>()
  const spatialRole = !['Style', 'World'].includes(reference.category)
  const canPlace = Boolean(reference.imageAssetId) && spatialRole
  const move = (event: ReactPointerEvent<HTMLButtonElement>) => {
    if (readOnly || busy) return
    if (event.type === 'pointerdown') {
      if (event.button !== 0) return
      event.currentTarget.setPointerCapture(event.pointerId)
      drag.current = { pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, moved: false }
      suppressClick.current = false
      return
    }
    if (!drag.current || drag.current.pointerId !== event.pointerId) return
    if (event.type === 'pointermove') {
      if (!drag.current.moved && Math.hypot(event.clientX - drag.current.startX, event.clientY - drag.current.startY) < 7) return
      if (!drag.current.moved) { drag.current.moved = true; onDragState(true) }
      setGhost({ x: event.clientX, y: event.clientY })
      return
    }
    const completed = drag.current
    drag.current = null
    suppressClick.current = completed.moved
    setGhost(undefined)
    onDragState(false)
    if (event.type !== 'pointerup' || !completed.moved) return
    const frame = document.querySelector<HTMLElement>('[data-reference-drop-target="true"]')
    if (!frame) return
    const rect = frame.getBoundingClientRect()
    if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) return
    onDropPin(Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)), Math.min(1, Math.max(0, (event.clientY - rect.top) / rect.height)))
  }
  if (!canPlace) return <div className="reference-edit-card reference-nonspatial"><ReferenceRow reference={reference} /><p>{reference.description}</p><small className="reference-lock"><LockKeyhole size={12} />{reference.lockedConstraint}</small><p className="reference-availability-note"><CircleAlert size={14} />{reference.imageAssetId ? `${reference.category} authorities apply globally and are never placed as image layers.` : 'Text authority only. Add an approved image on the authority card before using spatial placement.'}</p>{pins.length > 0 && <div className="reference-card-pins"><strong>{pins.length} old placement {pins.length === 1 ? 'note' : 'notes'} now treated as semantic context</strong>{pins.map(pin => <div key={pin.id}><MessageCircle size={13} /><span><p>{pin.body}</p><small>v{pin.referenceVersion ?? reference.version}</small></span><button type="button" aria-label={`Remove ${reference.name} placement note`} title="Resolve this obsolete placement" disabled={busy || readOnly} onClick={() => onResolvePin(pin.id)}><X size={13} /></button></div>)}</div>}<div className="reference-actions"><button type="button" className="secondary compact" disabled title={reference.imageAssetId ? 'This authority applies to the whole frame' : 'Attach an approved image to enable placement'}><LockKeyhole size={14} />{reference.imageAssetId ? 'Global authority' : 'No visual to place'}</button><button type="button" className="danger compact" aria-label={`Remove ${reference.name} from shot`} disabled={busy || readOnly} onClick={onRemove}><Trash2 size={14} />Remove</button></div></div>
  return <div className="reference-edit-card"><ReferenceRow reference={reference} /><p>{reference.description}</p><small className="reference-lock"><LockKeyhole size={12} />{reference.lockedConstraint}</small>{pins.length > 0 && <div className="reference-card-pins"><strong>{pins.length} placement {pins.length === 1 ? 'note' : 'notes'}</strong>{pins.map(pin => <div key={pin.id}><MessageCircle size={13} /><span><p>{pin.body}</p><small>v{pin.referenceVersion ?? reference.version} · {Math.round(pin.x * 100)}% across · {Math.round(pin.y * 100)}% down</small></span><button type="button" aria-label={`Remove ${reference.name} placement note`} title="Resolve this reference placement" disabled={busy || readOnly} onClick={() => onResolvePin(pin.id)}><X size={13} /></button></div>)}</div>}<div className="reference-actions"><button type="button" className="secondary compact reference-pin-handle" disabled={busy || readOnly} aria-label={`Place ${reference.name} on frame`} title="Drag onto the frame, or tap this button and then tap the target" onPointerDown={move} onPointerMove={move} onPointerUp={move} onPointerCancel={move} onClick={() => { if (suppressClick.current) { suppressClick.current = false; return }; onPin() }}><BadgeCheck size={14} /><span>Place on frame<small>drag or tap</small></span></button><button type="button" className="danger compact" aria-label={`Remove ${reference.name} from shot`} disabled={busy || readOnly} onClick={onRemove}><Trash2 size={14} />Remove</button></div>{ghost && <div className="reference-drag-ghost" style={{ left: ghost.x, top: ghost.y }} aria-hidden="true"><BadgeCheck size={15} /><span>{reference.name}<small>Drop on frame</small></span></div>}</div>
}
function ShotArtwork({ shot, label, muted = false }: { shot: ShotSummary; label?: string; muted?: boolean }) { return shot.currentAssetUrl ? <img className={`shot-asset ${muted ? 'muted' : ''}`} src={shot.currentAssetUrl} alt={label ?? `${shot.code} frame`} /> : <Artwork variant={shot.visualVariant} label={label} muted={muted} /> }
function CandidateArtwork({ candidate, fallbackVariant, muted = false }: { candidate?: CandidateVersionSummary; fallbackVariant: number; muted?: boolean }) { return candidate?.assetUrl ? <img className={`shot-asset ${muted ? 'muted' : ''}`} src={candidate.assetUrl} alt={`Archived ${candidate.stage} version ${candidate.version}`} /> : <Artwork variant={fallbackVariant} muted={muted} label={candidate ? `Archived ${candidate.stage} version ${candidate.version}` : 'No earlier image candidate'} /> }

function VideoEndpointStrip({ shot, candidates, busy, error, onChange, onView, onCreateLastFrame }: {
  shot: ShotSummary
  candidates: CandidateVersionSummary[]
  busy: boolean
  error?: string
  onChange: (firstFrameCandidateId?: string, lastFrameCandidateId?: string) => Promise<void>
  onView: (candidate: CandidateVersionSummary) => void
  onCreateLastFrame: () => void
}) {
  const imageCandidates = candidates.filter(candidate => candidate.assetId && candidate.assetUrl && candidate.stage !== 'Video')
  const defaultStart = imageCandidates.find(candidate => candidate.isCurrent) ?? imageCandidates.find(candidate => candidate.approval === 'Ratified')
  const start = imageCandidates.find(candidate => candidate.id === shot.videoFirstFrameCandidateId) ?? defaultStart
  const last = imageCandidates.find(candidate => candidate.id === shot.videoLastFrameCandidateId)
  const startValue = start?.id ?? ''
  const startOptions = imageCandidates.filter(candidate => candidate.id !== last?.id)
  const lastOptions = imageCandidates.filter(candidate => candidate.id !== start?.id && candidate.assetId !== start?.assetId)
  const status = (candidate?: CandidateVersionSummary) => candidate?.approval === 'Ratified' ? 'Approved' : candidate ? 'Working' : 'Optional'
  const saveStart = async (candidateId: string) => {
    const next = imageCandidates.find(candidate => candidate.id === candidateId)
    const nextLast = last && next?.assetId === last.assetId ? undefined : last?.id
    try { await onChange(candidateId || undefined, nextLast) } catch { /* The inline error owns the explanation. */ }
  }
  const saveLast = async (candidateId: string) => {
    try { await onChange(start?.id, candidateId || undefined) } catch { /* A failed save must not create an unhandled UI promise. */ }
  }
  return <section className="shot-endpoint-strip" aria-label="Video endpoints">
    <header><Film size={16} /><span><strong>Video endpoints</strong><small>Visible motion anchors · Last Frame is optional and editable</small></span></header>
    <div className="shot-endpoint-card start">
      <button type="button" className="shot-endpoint-thumb endpoint-preview-button" disabled={!start} onClick={() => start && onView(start)} aria-label={start ? `View Start Frame v${start.version}` : 'No Start Frame available'}>{start ? <CandidateArtwork candidate={start} fallbackVariant={shot.visualVariant} /> : <ShotArtwork shot={shot} muted />}<span>View</span></button>
      <label><span>Start Frame <em>{status(start)}</em></span><select aria-label="Shot Start Frame" value={startValue} disabled={busy || imageCandidates.length === 0} onChange={event => void saveStart(event.target.value)}>{startOptions.map(candidate => <option key={candidate.id} value={candidate.id}>v{candidate.version} · {candidate.stage}{candidate.approval === 'Ratified' ? ' · ratified' : ' · working'}</option>)}</select></label>
    </div>
    <ArrowRight className="endpoint-direction" size={16} />
    <div className={`shot-endpoint-card end ${last ? '' : 'empty'}`}>
      <button type="button" className="shot-endpoint-thumb endpoint-preview-button" disabled={!start || busy} onClick={() => last ? onView(last) : onCreateLastFrame()} aria-label={last ? `View Last Frame v${last.version}` : 'Create Last Frame from Start Frame'}>{last ? <CandidateArtwork candidate={last} fallbackVariant={shot.visualVariant} /> : <div><WandSparkles size={17} /><span>Create Last Frame</span></div>}<span>{last ? 'View' : 'Create'}</span></button>
      <label><span>Last Frame <em>{status(last)}</em></span><select aria-label="Shot Last Frame" value={last?.id ?? ''} disabled={busy || imageCandidates.length === 0} onChange={event => void saveLast(event.target.value)}><option value="">No fixed Last Frame</option>{lastOptions.map(candidate => <option key={candidate.id} value={candidate.id}>v{candidate.version} · {candidate.stage}{candidate.approval === 'Ratified' ? ' · ratified' : ' · working'}</option>)}</select></label>
    </div>
    <div className="shot-endpoint-guidance"><small>{last ? `H3 will freeze Start v${start?.version ?? '?'} → Last v${last.version}. Click either image to inspect the actual endpoint; compatibility review is still required before generation.` : 'Leave this empty for natural motion, or click Create Last Frame to derive a compatible landing image.'}</small>{last && <button type="button" className="secondary compact" disabled={!start || busy} onClick={onCreateLastFrame}><WandSparkles size={13} />Create another Last Frame</button>}</div>
    {busy && <LoaderCircle className="spin endpoint-saving" size={15} aria-label="Saving video endpoints" />}
    {error && <p className="form-error" role="alert">{error}</p>}
  </section>
}

function LastFrameCreationDialog({ shot, candidates, referenceCount, onClose, onQueued }: { shot: ShotSummary; candidates: CandidateVersionSummary[]; referenceCount: number; onClose: () => void; onQueued: () => void }) {
  const imageCandidates = candidates.filter(candidate => candidate.assetId && candidate.assetUrl && candidate.stage !== 'Video')
  const start = imageCandidates.find(candidate => candidate.id === shot.videoFirstFrameCandidateId)
    ?? imageCandidates.find(candidate => candidate.isCurrent)
    ?? imageCandidates.find(candidate => candidate.approval === 'Ratified')
  const [direction, setDirection] = useState(`By the end of the shot: ${shot.action} Keep the same camera setup and visual continuity unless the action requires a deliberate change.`)
  const [adapterId, setAdapterId] = useState('comfyui-fast-draft')
  const [sketchRevision, setSketchRevision] = useState<number>()
  const [adapters, setAdapters] = useState<GenerationAdapterSummary[]>([])
  const [autoRefine, setAutoRefine] = useState(() => localStorage.getItem('framewright.codex-auto-refine') !== 'false')
  const [refining, setRefining] = useState(false)
  const [refinement, setRefinement] = useState<{ before: string; after: string; summary: string }>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  useEffect(() => {
    void Promise.all([studioApi.sketch(shot.id), studioApi.generationAdapters()]).then(([sketch, available]) => {
      setSketchRevision(sketch?.revision ?? 0)
      const compatible = available.filter(item => item.purposes.includes('Draft') && item.id !== 'local-proof')
      setAdapters(compatible)
      setAdapterId(compatible.find(item => item.id === 'comfyui-fast-draft' && item.canDispatch)?.id
        ?? compatible.find(item => item.id === 'codex-imagegen' && item.state === 'Connected')?.id
        ?? compatible.find(item => item.canDispatch)?.id ?? compatible[0]?.id ?? 'codex-imagegen')
    }).catch(reason => setError(reason instanceof Error ? reason.message : 'Could not prepare the Last Frame route.'))
  }, [shot.id])
  const selected = adapters.find(adapter => adapter.id === adapterId)
  const route = selected?.routes.includes('PrecisionDraft') ? 'PrecisionDraft' : 'FastDraft'
  const ready = Boolean(start?.assetId && selected?.canDispatch && direction.trim() && sketchRevision !== undefined)
  const requestCodexRefinement = async (text: string) => {
    setRefining(true)
    try {
      const suggestion = await studioApi.improveGenerationDirection(shot.id, text, 'Last Frame')
      if (!suggestion.live) throw new Error(suggestion.summary)
      setDirection(suggestion.improvedText)
      setRefinement({ before: text, after: suggestion.improvedText, summary: suggestion.summary })
      return suggestion.improvedText
    } finally { setRefining(false) }
  }
  const improveNow = async () => {
    if (!direction.trim() || refining || busy) return
    setError(undefined)
    try { await requestCodexRefinement(direction.trim()) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Codex could not refine this direction.') }
  }
  const generate = async () => {
    if (!start?.assetId || !selected || sketchRevision === undefined || !direction.trim()) return
    setBusy(true); setError(undefined)
    try {
      let finalDirection = direction.trim()
      if (autoRefine && refinement?.after !== finalDirection) finalDirection = await requestCodexRefinement(finalDirection)
      const brief = `CREATE THE LAST FRAME FOR THIS SHOT.\n\nEND-STATE DIRECTION: ${finalDirection.slice(0, 2500)}\n\nSHOT INTENT: ${shot.description.slice(0, 1000)}\nACTION: ${shot.action.slice(0, 1000)}\nCAMERA: ${shot.camera.slice(0, 500)}\n\nUse Start Frame v${start.version} as the exact visual source. This is a later moment in the same continuous shot, not a second panel or a redesign. Preserve continuity rather than identical pixels: the requested end-state must be plainly readable at full-frame review, rebuilding pose, expression, hands, fabric, and nearby anatomy wherever the action requires it.`
      const prepared = await studioApi.prepareManifest(shot.id, {
        expectedShotVersion: shot.version,
        expectedSketchRevision: sketchRevision,
        route,
        purpose: 'Draft',
        compositionAssetId: start.assetId,
        creativeBriefOverride: brief,
        allowSketchCompositionFallback: false,
        videoEndpointRole: 'LastFrame',
        videoEndpointSourceCandidateId: start.id,
      })
      await studioApi.dispatchManifest(prepared.id, prepared.manifestHash, selected.id)
      onQueued()
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not create the Last Frame packet.') }
    finally { setBusy(false) }
  }
  return <Dialog className="modal-backdrop route-backdrop" onClose={onClose} labelledBy="last-frame-title" initialFocus="textarea">
    <section className="route-modal last-frame-modal">
      <header><div><p className="eyebrow">Copy with directions</p><h2 id="last-frame-title">Create Last Frame</h2></div><button onClick={onClose} aria-label="Close Last Frame creator">×</button></header>
      <div className="route-modal-body">
        <div className="feedback-source last-frame-source"><div>{start ? <CandidateArtwork candidate={start} fallbackVariant={shot.visualVariant} /> : <ShotArtwork shot={shot} muted />}<span>Start Frame · {start ? `v${start.version}` : 'unavailable'}</span></div><div><strong>Describe only what changes by the end</strong><p>Framewright carries the source image, style, camera, crop, {referenceCount} {referenceCount === 1 ? 'authority' : 'authorities'}, and every locked rule. The result becomes the selected working Last Frame automatically.</p></div></div>
        <label><span>What is different at the end?</span><textarea aria-label="Last Frame directions" maxLength={2500} value={direction} onChange={event => { setDirection(event.target.value); setRefinement(undefined) }} /></label>
        <div className="codex-prompt-pass">
          <div className="codex-prompt-flow"><span>Your text</span><ArrowRight size={13} /><span>Codex improves</span><ArrowRight size={13} /><span>{selected?.id === 'comfyui-fast-draft' ? 'ComfyUI generates' : selected?.name ?? 'Image engine'}</span></div>
          <div className="codex-prompt-actions"><button type="button" className="secondary compact" disabled={!direction.trim() || refining || busy} onClick={() => void improveNow()}>{refining ? <LoaderCircle className="spin" size={14} /> : <Sparkles size={14} />}{refining ? 'Codex is refining…' : 'Improve with Codex'}</button><label className="codex-auto-refine"><input type="checkbox" checked={autoRefine} onChange={event => { setAutoRefine(event.target.checked); localStorage.setItem('framewright.codex-auto-refine', String(event.target.checked)) }} /><span><strong>Auto-improve before generating</strong><small>Uses Codex sign-in, then calls the selected engine</small></span></label></div>
          {refinement && <div className="codex-refinement-result" role="status"><Check size={15} /><span><strong>Codex refinement applied</strong><small>{refinement.summary}</small></span><button type="button" className="secondary compact" onClick={() => { setDirection(refinement.before); setRefinement(undefined) }}>Undo</button></div>}
        </div>
        <div className="continuity-transfer" aria-label="Continuity transferred automatically"><span><Check size={13} />Style</span><span><Check size={13} />Camera &amp; crop</span><span><Check size={13} />Characters &amp; wardrobe</span><span><Check size={13} />World &amp; props</span><span><Check size={13} />Locked rules</span></div>
        <div className="adapter-picker feedback-adapter-picker" role="radiogroup" aria-label="Last Frame generation engine">{adapters.map(item => <button type="button" role="radio" aria-checked={adapterId === item.id} key={item.id} className={adapterId === item.id ? 'selected' : ''} onClick={() => { setAdapterId(item.id); setError(undefined) }}><i>{item.id === 'comfyui-fast-draft' ? <Film size={17} /> : item.id === 'codex-imagegen' ? <Cpu size={17} /> : <Sparkles size={17} />}</i><span><strong>{item.name}{item.id === 'openai-gpt-image' && <em className="preview-tag">Preview</em>}</strong><small>{item.id === 'comfyui-fast-draft' ? 'Fast local Start-to-Last edit · selected ComfyUI workflow' : item.id === 'codex-imagegen' ? 'High-fidelity Start-to-Last edit · runs in Framewright' : item.detail}</small></span><em className={item.canDispatch ? 'ready' : 'protected'}>{item.canDispatch ? 'Ready' : 'Setup'}</em></button>)}</div>
        <p className="feedback-one-click-note"><LockKeyhole size={14} /><span>The Start Frame stays unchanged. The result becomes a new working Last Frame; approve it before generating video.</span></p>
        {!start && <p className="form-error" role="alert">Choose a Start Frame with an image before creating a Last Frame.</p>}
        {sketchRevision === undefined && !error && <p className="route-error" role="status"><LoaderCircle className="spin" size={14} /><span><strong>Loading shot details</strong>Gathering the source frame, references, and continuity rules.</span></p>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
      <footer><button className="secondary" onClick={onClose}>Cancel</button><button className="primary feedback-regenerate-action" disabled={busy || refining || !ready} onClick={() => void generate()}>{refining ? 'Codex is refining…' : busy ? 'Creating Last Frame…' : ready ? `${autoRefine && !refinement ? 'Refine & generate' : 'Generate Last Frame'} with ${selected?.name ?? 'selected engine'}` : sketchRevision === undefined ? 'Loading continuity…' : `${selected?.name ?? 'Image engine'} needs setup`}<ArrowRight size={16} /></button></footer>
    </section>
  </Dialog>
}

function GenerationPacketProof({ summary }: { summary?: GenerationPreflightSummary }) {
  if (!summary) return null
  const visual = summary.references.filter(reference => reference.willBindVisually).length
  const review = summary.checks.filter(check => check.state === 'Review').length
  return <details className={`generation-packet-proof ${summary.ready ? 'ready' : 'blocked'}`} open={!summary.ready}>
    <summary><span><strong>{summary.ready ? 'Generation packet verified' : 'Generation packet needs attention'}</strong><small>{summary.workflowName} · {visual}/{summary.references.length} visual references · {summary.deliveryWidth} × {summary.deliveryHeight} · {summary.colorSpace}</small></span><em>{summary.ready ? review ? `${review} review` : 'Ready' : 'Blocked'}</em></summary>
    <div>{summary.checks.map(check => <p key={`${check.state}-${check.title}`} className={`state-${check.state.toLowerCase()}`}><span>{check.state === 'Pass' ? <Check size={14} /> : <CircleAlert size={14} />}</span><span><strong>{check.title}</strong><small>{check.detail}</small></span></p>)}</div>
  </details>
}

function ProductionFinalDialog({ shot, references, onClose, onRepairRequested, onQueued }: { shot: ShotSummary; references: ReferenceSummary[]; onClose: () => void; onRepairRequested: (audit: ShotVisualAuditSummary) => void; onQueued: () => void }) {
  const [brief, setBrief] = useState(`Create the production-quality final from selected image v${shot.version}. Preserve the exact composition, camera, subject placement, identity, wardrobe, architecture, props, and all unmentioned details from the source image. Improve only rendering fidelity, natural anatomy, material detail, lighting coherence, and photographic finish. Camera: ${shot.camera}. Action: ${shot.action}`)
  const [sketchRevision, setSketchRevision] = useState<number>()
  const [adapters, setAdapters] = useState<GenerationAdapterSummary[]>([])
  const [adapterId, setAdapterId] = useState<string>()
  const [preflight, setPreflight] = useState<GenerationPreflightSummary>()
  const [visualAudit, setVisualAudit] = useState<ShotVisualAuditSummary | null>()
  const [auditBusy, setAuditBusy] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  useEffect(() => {
    void Promise.all([studioApi.sketch(shot.id), studioApi.generationAdapters(), studioApi.visualAudit(shot.id)]).then(([sketch, adapters, audit]) => {
      setSketchRevision(sketch?.revision ?? 0)
      setVisualAudit(audit)
      const compatible = adapters.filter(item => item.routes.includes('PrecisionDraft') && item.purposes.includes('Final'))
      setAdapters(compatible)
      setAdapterId(compatible.find(item => item.id === 'codex-imagegen' && item.state === 'Connected')?.id
        ?? compatible.find(item => item.canDispatch)?.id ?? compatible[0]?.id)
    }).catch(reason => setError(reason instanceof Error ? reason.message : 'Could not prepare the production-final route.'))
  }, [shot.id])
  const checkVisibleFrame = async () => {
    setAuditBusy(true); setError(undefined)
    try { setVisualAudit(await studioApi.runVisualAudit(shot.id, Boolean(visualAudit))) }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Codex could not inspect this frame.') }
    finally { setAuditBusy(false) }
  }
  const generate = async () => {
    const adapter = adapters.find(item => item.id === adapterId)
    if (!shot.currentAssetId || sketchRevision === undefined || !brief.trim() || !adapter?.canDispatch || visualAudit?.gateState !== 'Clear') return
    setBusy(true); setError(undefined)
    try {
      const creativeBrief = `${brief.trim().slice(0, 5000)}\n\nPRODUCTION FINAL SOURCE: The attached selected image v${shot.version} is the visual base for this pass. Perform an image edit from this exact frame. Do not use or substitute the saved sketch.\n\nAPPROVED CONTEXT: ${references.map(reference => `${reference.name} v${reference.version}`).join(', ') || 'No visual authorities attached'}.\nLOCKED RULES:\n${shot.constraints.map(rule => `- ${rule}`).join('\n')}`
      const prepared = await studioApi.prepareManifest(shot.id, {
        expectedShotVersion: shot.version,
        expectedSketchRevision: sketchRevision,
        route: 'PrecisionDraft',
        purpose: 'Final',
        compositionAssetId: shot.currentAssetId,
        creativeBriefOverride: creativeBrief,
        allowSketchCompositionFallback: false,
      })
      const proof = await studioApi.generationPreflight(prepared.id, adapter.id)
      setPreflight(proof)
      if (!proof.ready) throw new Error(proof.checks.filter(check => check.state === 'Block').map(check => check.detail).join(' '))
      await studioApi.dispatchManifest(prepared.id, prepared.manifestHash, adapter.id)
      onQueued()
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not start the production final.') }
    finally { setBusy(false) }
  }
  const adapter = adapters.find(item => item.id === adapterId)
  const repairRequired = Boolean(visualAudit && visualAudit.gateState !== 'Clear')
  const canOpenRepair = Boolean(repairRequired && visualAudit?.findings.length)
  const openRepair = () => { if (canOpenRepair && visualAudit) onRepairRequested(visualAudit) }
  const ready = Boolean(shot.currentAssetId && sketchRevision !== undefined && brief.trim() && adapter?.canDispatch && visualAudit?.gateState === 'Clear')
  return <Dialog className="modal-backdrop route-backdrop" onClose={onClose} labelledBy="production-final-title" initialFocus="textarea">
    <section className="route-modal production-final-modal">
      <header><div><p className="eyebrow">Selected image to final</p><h2 id="production-final-title">Create production final</h2></div><button onClick={onClose} aria-label="Close production final">×</button></header>
      <div className="route-modal-body">
        <div className="feedback-source production-final-source"><div><ShotArtwork shot={shot} label={`${shot.code} selected image version ${shot.version} production source`} /><span>Selected source · v{shot.version}</span></div><div><strong>This image stays the visual base</strong><p>The selected precision engine edits this exact image. Approval is optional and Framewright will not replace it with the original sketch.</p></div></div>
        <label><span>Production finish direction</span><textarea aria-label="Production final direction" maxLength={5000} value={brief} onChange={event => setBrief(event.target.value)} /></label>
        <div className="route-hero precision"><i><Sparkles size={20} /></i><span><strong>Choose the production engine</strong><small>Both precision routes preserve this exact visual base and may take several minutes</small></span></div>
      <div className="adapter-picker precision-adapter-picker" role="radiogroup" aria-label="Production image engine">{adapters.map(item => <button type="button" role="radio" aria-checked={adapterId === item.id} key={item.id} className={adapterId === item.id ? 'selected' : ''} onClick={() => { setAdapterId(item.id); setError(undefined) }}><i>{item.id === 'codex-imagegen' ? <Cpu size={17} /> : <Sparkles size={17} />}</i><span><strong>{item.name}{item.id === 'openai-gpt-image' && <em className="preview-tag">Preview</em>}</strong><small>{item.id === 'codex-imagegen' ? 'Uses your Codex ChatGPT login · runs here · no API key' : item.detail}</small></span><em className={item.canDispatch ? 'ready' : 'protected'}>{item.canDispatch ? 'Ready' : 'Setup'}</em></button>)}</div>
        <div className="manifest-grid"><div><span>Visual source</span><strong>Selected image v{shot.version}</strong></div><div><span>Included context</span><strong>{references.length} references · {shot.constraints.length} locked rules</strong></div></div>
        <div className={`production-visual-gate ${visualAudit ? `gate-${visualAudit.gateState.toLowerCase()}` : ''}`}><Scan size={18} /><span><strong>{visualAudit?.gateState === 'Clear' ? 'Visible frame matches the shot contract' : visualAudit ? `${visualAudit.gateState}: repair before final` : 'Visual consistency check required'}</strong><small>{visualAudit?.summary ?? 'Codex checks this exact image for subject count, pose, props, location, style, and visible anatomy before a production pass can preserve it.'}</small></span><button className="secondary compact" onClick={() => repairRequired ? openRepair() : void checkVisibleFrame()} disabled={auditBusy || (repairRequired && !canOpenRepair)}>{auditBusy ? <LoaderCircle className="spin" size={14} /> : repairRequired ? <WandSparkles size={14} /> : <Scan size={14} />}{auditBusy ? 'Inspecting…' : repairRequired ? 'Open repair' : visualAudit ? 'Check again' : 'Check frame'}</button></div>
        {repairRequired && <p className="visual-mixed-note" role="status"><CircleAlert size={14} />The current image does not match the shot. Continue with Fix image to review the discrepancies and launch a replacement without leaving this flow.</p>}
        {!shot.currentAssetId && <p className="form-error" role="alert">This draft only has a preview, not a saved source image. Import or generate an image before creating the production final.</p>}
        {adapter && !adapter.canDispatch && <div className="wiring-notice adapter-blocked"><LockKeyhole size={17} /><p><strong>{adapter.name} needs setup</strong>{adapter.detail}</p></div>}
        <GenerationPacketProof summary={preflight} />
        {sketchRevision === undefined && !error && <p className="route-error" role="status"><LoaderCircle className="spin" size={14} /><span><strong>Loading shot details</strong>Gathering the selected source, references, and locked rules.</span></p>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
      <footer><button className="secondary" onClick={onClose}>Keep reviewing</button><button className="primary" disabled={busy || (repairRequired ? !canOpenRepair : !ready)} onClick={() => repairRequired ? openRepair() : void generate()}>{busy ? 'Starting production final…' : repairRequired ? <><WandSparkles size={16} />Fix image</> : ready ? `Generate with ${adapter?.name ?? 'selected engine'}` : sketchRevision === undefined ? 'Loading source…' : `${adapter?.name ?? 'Image engine'} needs setup`}<ArrowRight size={16} /></button></footer>
    </section>
  </Dialog>
}

function FeedbackRegenerationDialog({ shot, comments, references, initialDirection, markupGuide, compositionGuide, onClose, onQueued }: { shot: ShotSummary; comments: CommentSummary[]; references: ReferenceSummary[]; initialDirection: string; markupGuide?: { revision: number; exportAsset: () => Promise<string> }; compositionGuide?: { exportAsset: () => Promise<string> }; onClose: () => void; onQueued: () => void }) {
  // Feedback regeneration is always an exploratory working pass. A ratified
  // final remains immutable; the artist deliberately promotes a chosen draft
  // through the separate production-final flow.
  const outputPurpose = 'Draft' as const
  const [direction, setDirection] = useState(initialDirection)
  const [adapterId, setAdapterId] = useState('comfyui-fast-draft')
  const [sketchRevision, setSketchRevision] = useState<number>()
  const [adapters, setAdapters] = useState<GenerationAdapterSummary[]>([])
  const [preflight, setPreflight] = useState<GenerationPreflightSummary>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  useEffect(() => {
    void Promise.all([studioApi.sketch(shot.id), studioApi.generationAdapters()]).then(([sketch, available]) => {
      setSketchRevision(sketch?.revision ?? 0)
      setAdapters(available)
      const compatible = available.filter(item => item.purposes.includes(outputPurpose) && item.id !== 'local-proof')
      const preferred = compatible.find(item => item.id === 'comfyui-fast-draft' && item.canDispatch)
      setAdapterId(preferred?.id ?? compatible.find(item => item.canDispatch || item.id === 'codex-imagegen' && item.state === 'Connected')?.id ?? compatible[0]?.id ?? 'codex-imagegen')
    }).catch(reason => setError(reason instanceof Error ? reason.message : 'Could not prepare feedback regeneration.'))
  }, [shot.id, outputPurpose])
  const pinned = comments.map((comment, index) => { const reference = references.find(item => item.id === comment.referenceId); const label = reference ? `Reference ${reference.name} v${comment.referenceVersion ?? reference.version}` : `Pin ${index + 1}`; return `- ${label} at ${Math.round(comment.x * 100)}% across / ${Math.round(comment.y * 100)}% down: ${comment.body}` }).join('\n').slice(0, 2500)
  const selected = adapters.find(adapter => adapter.id === adapterId)
  const route = selected?.routes.includes('PrecisionDraft') ? 'PrecisionDraft' : 'FastDraft'
  const regenerate = async () => {
    if (!direction.trim() || sketchRevision === undefined) return
    setBusy(true); setError(undefined)
    try {
      const guidanceAssetId = markupGuide && shot.currentAssetId ? await markupGuide.exportAsset() : undefined
      const storyboardGuideId = !guidanceAssetId && !shot.currentAssetId ? await compositionGuide?.exportAsset() : undefined
      const sourceAssetId = guidanceAssetId ?? shot.currentAssetId ?? storyboardGuideId
      const sourceContract = guidanceAssetId || shot.currentAssetId
        ? `CURRENT FRAME EDIT: Use the attached current generated frame v${shot.version} as the visual starting point. Preserve all content not named by the requested change; do not restart from the original sketch.`
        : storyboardGuideId
          ? 'STORYBOARD COMPOSITION GUIDE: Use the attached clean storyboard frame as the binding layout. Preserve its subject count, full-body scale, left-to-right placement, camera, horizon, and negative space. Replace every simplified figure and shape with finished artwork; do not crop into a portrait or preserve guide geometry.'
          : 'FEEDBACK-GUIDED DRAFT: This revision has no stored source image or storyboard layout. Generate a new frame from the shot intent, approved authorities, locked constraints, and feedback below.'
      let brief = `${shot.description.slice(0, 800)}\nCamera: ${shot.camera.slice(0, 300)}.\nAction: ${shot.action.slice(0, 800)}.\n\nREQUESTED CHANGE: ${direction.trim().slice(0, 2500)}\n\n${sourceContract}`
      if (pinned) brief += `\n\nPINNED FEEDBACK ON THIS REVISION:\n${pinned}`
      if (guidanceAssetId) brief += '\n\nFRAME EDIT GUIDE: The attached current frame contains temporary gold markup. Use it only to locate edits, then remove every trace of the markup.'
      const prepared = await studioApi.prepareManifest(shot.id, { expectedShotVersion: shot.version, expectedSketchRevision: sketchRevision, route, purpose: outputPurpose, compositionAssetId: sourceAssetId, creativeBriefOverride: brief, markupRevision: guidanceAssetId ? markupGuide?.revision : undefined, allowSketchCompositionFallback: false })
      const proof = await studioApi.generationPreflight(prepared.id, adapterId)
      setPreflight(proof)
      if (!proof.ready) throw new Error(proof.checks.filter(check => check.state === 'Block').map(check => check.detail).join(' '))
      await studioApi.dispatchManifest(prepared.id, prepared.manifestHash, adapterId)
      onQueued()
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not start this revision.') }
    finally { setBusy(false) }
  }
  const ready = Boolean(selected?.canDispatch && direction.trim() && sketchRevision !== undefined)
  const hasStoredSource = Boolean(shot.currentAssetId)
  const hasStoryboardGuide = !hasStoredSource && Boolean(compositionGuide)
  return <Dialog className="modal-backdrop route-backdrop" onClose={onClose} labelledBy="feedback-route-title" initialFocus="textarea">
    <section className="route-modal feedback-route-modal">
      <header><div><p className="eyebrow">Quick revision</p><h2 id="feedback-route-title">Regenerate from feedback</h2></div><button onClick={onClose} aria-label="Close feedback regeneration">×</button></header>
      <div className="route-modal-body">
        <div className="feedback-source"><div><ShotArtwork shot={shot} label={hasStoredSource ? `${shot.code} current frame used for feedback regeneration` : `${shot.code} storyboard composition guide`} /><span>{hasStoredSource ? 'Visual base' : 'Storyboard layout'} · v{shot.version}</span></div><div><strong>{comments.length} pinned note{comments.length === 1 ? '' : 's'} included</strong><p>{hasStoredSource ? (markupGuide ? 'Current-frame markup will be exported as a temporary edit guide.' : 'The clean current frame—not the original sketch—will be sent as the composition image.') : hasStoryboardGuide ? 'Framewright will send this clean storyboard layout with the pins, references, rules, and shot intent. Simplified figures guide blocking only and will be replaced by finished artwork.' : 'No source image or storyboard layout is available. Framewright will compose a new draft from the shot intent, references, rules, and these notes.'}</p></div></div>
        <label><span>What should change?</span><textarea aria-label="Feedback regeneration instructions" maxLength={2500} value={direction} onChange={event => setDirection(event.target.value)} /></label>
        {comments.length > 0 && <div className="feedback-packet-notes">{comments.map((comment, index) => { const reference = references.find(item => item.id === comment.referenceId); return <p key={comment.id} className={reference ? 'reference-feedback' : ''}><strong>{reference ? `${reference.name} · authority v${comment.referenceVersion ?? reference.version}` : `Pin ${index + 1}`}</strong>{comment.body}<small>{Math.round(comment.x * 100)}% across · {Math.round(comment.y * 100)}% down{reference ? ' · exact reference image included' : ''}</small></p> })}</div>}
        <div className="revision-route-guide" aria-label="Recommended revision path"><span><strong>1 · Iterate</strong><small>Fast ComfyUI working passes</small></span><ArrowRight size={15} /><span><strong>2 · Choose</strong><small>Compare and approve the winner</small></span><ArrowRight size={15} /><span><strong>3 · Finish</strong><small>Create the precision final</small></span></div>
        <div className="route-heading revision-engine-heading"><span>Choose this pass</span><small>ComfyUI is the recommended fast loop; precision engines can be used at any time</small></div>
      <div className="adapter-picker feedback-adapter-picker" role="radiogroup" aria-label="Revision engine">{adapters.filter(item => item.purposes.includes(outputPurpose) && item.id !== 'local-proof').map(item => <button type="button" role="radio" aria-checked={adapterId === item.id} key={item.id} className={adapterId === item.id ? 'selected' : ''} onClick={() => { setAdapterId(item.id); setError(undefined) }}><i>{item.id === 'comfyui-fast-draft' ? <Film size={17} /> : item.id === 'codex-imagegen' ? <Cpu size={17} /> : <Sparkles size={17} />}</i><span><strong>{item.name}{item.id === 'openai-gpt-image' && <em className="preview-tag">Preview</em>}</strong><small>{item.id === 'comfyui-fast-draft' ? `Fast local ${hasStoredSource ? 'edit' : 'draft'} · selected ComfyUI workflow` : item.id === 'codex-imagegen' ? `High-fidelity ${hasStoredSource ? 'edit' : 'draft'} · runs in Framewright` : `Direct ${hasStoredSource ? 'edit' : 'draft'} · requires an OpenAI project key`}</small></span><em className={item.canDispatch ? 'ready' : 'protected'}>{item.canDispatch ? 'Ready' : 'Setup'}</em></button>)}</div>
        <p className="feedback-one-click-note"><LockKeyhole size={14} /><span>Framewright includes the current frame, your notes, references, and locked rules automatically. The result becomes a new working draft; v{shot.version}{shot.approval === 'Ratified' ? ` stays preserved as the approved ${shot.stage.toLowerCase()}` : ' stays in history'}. Open notes remain attached until you approve the replacement.</span></p>
        <GenerationPacketProof summary={preflight} />
        {selected && !selected.canDispatch && selected.id !== 'codex-imagegen' && <div className="wiring-notice adapter-blocked"><LockKeyhole size={17} /><p><strong>{selected.name} needs setup</strong>{selected.detail}</p></div>}
        {sketchRevision === undefined && !error && <p className="route-error" role="status"><CircleAlert size={14} /><span><strong>Loading shot details</strong>Gathering the current frame, references, notes, and rules.</span></p>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
      <footer><button className="secondary" onClick={onClose}>Cancel</button><button className="primary feedback-regenerate-action" disabled={busy || !ready} onClick={() => void regenerate()}>{busy ? 'Starting revision…' : ready ? `Regenerate with ${selected?.name ?? 'selected engine'}` : sketchRevision === undefined ? 'Loading revision…' : `${selected?.name ?? 'Image engine'} needs setup`}<ArrowRight size={16} /></button></footer>
    </section>
  </Dialog>
}

function VideoGenerationDialog({ shot, project, onSaveEndpoints, onClose, onQueued }: { shot: ShotSummary; project: StudioSnapshot['project']; onSaveEndpoints: (firstFrameCandidateId?: string, lastFrameCandidateId?: string) => Promise<void>; onClose: () => void; onQueued: () => void }) {
  const [brief, setBrief] = useState(`${shot.action} Preserve the selected composition and camera: ${shot.camera}. Design a stable opening and a clean landing; prioritize believable motion over forcing an incompatible endpoint.`)
  const [quality, setQuality] = useState<Exclude<VideoQuality, 'Max'>>('Low')
  const [takeId] = useState(() => crypto.randomUUID())
  const [candidates, setCandidates] = useState<CandidateVersionSummary[]>([])
  const [firstFrameCandidateId, setFirstFrameCandidateId] = useState<string | undefined>(shot.videoFirstFrameCandidateId)
  const [lastFrameCandidateId, setLastFrameCandidateId] = useState<string | undefined>(shot.videoLastFrameCandidateId)
  const [confirmed, setConfirmed] = useState(false)
  const [manifest, setManifest] = useState<GenerationManifestSummary>()
  const [preflight, setPreflight] = useState<GenerationPreflightSummary>()
  const [adapters, setAdapters] = useState<GenerationAdapterSummary[]>([])
  const [selectedAdapter, setSelectedAdapter] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  useEffect(() => {
    void Promise.all([studioApi.generationAdapters(), studioApi.candidates(shot.id)])
      .then(([items, versions]) => {
        const compatible = items.filter(item => item.purposes.includes('Video'))
        setAdapters(compatible)
        setSelectedAdapter(compatible.find(item => item.canDispatch)?.id ?? compatible[0]?.id)
        const imageVersions = versions.filter(item => item.assetId && item.assetUrl && item.stage !== 'Video')
        setCandidates(imageVersions)
        setFirstFrameCandidateId(current => current ?? imageVersions.find(item => item.isCurrent)?.id ?? imageVersions.find(item => item.approval === 'Ratified')?.id)
      })
      .catch(reason => setError(reason instanceof Error ? reason.message : 'Could not load the shot endpoints.'))
  }, [shot.id])
  const firstCandidate = candidates.find(item => item.id === firstFrameCandidateId)
  const firstAssetId = firstCandidate?.assetId ?? shot.currentAssetId
  const lastCandidates = candidates.filter(item => item.assetId !== firstAssetId)
  const lastCandidate = lastCandidates.find(item => item.id === lastFrameCandidateId)
  const chooseFirst = (candidateId: string) => {
    const nextId = candidateId || undefined
    const nextAssetId = candidates.find(item => item.id === nextId)?.assetId ?? shot.currentAssetId
    setFirstFrameCandidateId(nextId)
    if (lastCandidate?.assetId === nextAssetId) setLastFrameCandidateId(undefined)
    setConfirmed(false)
    setManifest(undefined)
    setPreflight(undefined)
  }
  const chooseLast = (candidateId: string) => {
    setLastFrameCandidateId(candidateId || undefined)
    setConfirmed(false)
    setManifest(undefined)
    setPreflight(undefined)
  }
  const prepare = async () => { setBusy(true); setError(undefined); try { await onSaveEndpoints(firstFrameCandidateId, lastFrameCandidateId); const prepared = await studioApi.prepareVideoManifest(shot.id, { expectedShotVersion: shot.version, motionBrief: brief, firstFrameCandidateId, lastFrameCandidateId, confirmEndpointCompatibility: confirmed, quality, takeId }); setManifest(prepared); if (selectedAdapter) setPreflight(await studioApi.generationPreflight(prepared.id, selectedAdapter)) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not prepare the video packet.') } finally { setBusy(false) } }
  const dispatch = async () => { if (!manifest || !selectedAdapter) return; setBusy(true); setError(undefined); try { const proof = await studioApi.generationPreflight(manifest.id, selectedAdapter); setPreflight(proof); if (!proof.ready) throw new Error(proof.checks.filter(check => check.state === 'Block').map(check => check.detail).join(' ')); await studioApi.dispatchManifest(manifest.id, manifest.manifestHash, selectedAdapter); onQueued() } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not dispatch the video packet.'); setBusy(false) } }
  const selected = adapters.find(item => item.id === selectedAdapter)
  const hasFirstFrame = Boolean(firstAssetId)
  const qualityProfile = VIDEO_QUALITY_PROFILES[quality]
  const proxyCanvas = videoProxyCanvas(project.deliveryWidth, project.deliveryHeight, quality)
  return <Dialog className="edit-dialog video-generation-dialog" onClose={onClose} labelledBy="video-generation-title">
    <header><div><p className="eyebrow">Shot endpoints to motion</p><h2 id="video-generation-title">Generate {shot.code} video</h2></div><button onClick={onClose} aria-label="Close video generator">×</button></header>
    <div className="edit-dialog-body">
      <div className="video-endpoint-intro"><Film size={17} /><span><strong>MiniMax H3 · {qualityProfile.label} · {Math.max(1, Math.round(shot.durationFrames / project.framesPerSecond))} seconds</strong><small>{qualityProfile.detail} Any selected image revision can start the shot; an ending frame remains optional.</small></span></div>
      <div className="project-format-banner"><LockKeyhole size={16} /><span><strong>Project delivery · {project.deliveryWidth} × {project.deliveryHeight}</strong><small>{project.aspectRatio} · {project.framesPerSecond}fps · every Max take uses this exact canvas</small></span></div>
      <div className="video-quality-picker" role="radiogroup" aria-label="Video quality pass">{(Object.keys(VIDEO_QUALITY_PROFILES) as Exclude<VideoQuality, 'Max'>[]).map(value => { const item = VIDEO_QUALITY_PROFILES[value]; const canvas = videoProxyCanvas(project.deliveryWidth, project.deliveryHeight, value); return <button type="button" role="radio" aria-checked={quality === value} className={quality === value ? 'selected' : ''} key={value} onClick={() => { setQuality(value); setManifest(undefined) }}><span><strong>{value}</strong><small>{canvas.width} × {canvas.height}{value === 'High' ? ` → ${project.deliveryWidth} × ${project.deliveryHeight}` : ' proxy'}</small></span><em>{item.cost}</em></button> })}</div>
      <div className="video-anchor-row">
        <div className="video-anchor-card">
          <span>START FRAME · REQUIRED</span>
          {firstCandidate ? <CandidateArtwork candidate={firstCandidate} fallbackVariant={shot.visualVariant} /> : shot.currentAssetId ? <ShotArtwork shot={shot} /> : <div className="endpoint-empty"><CircleAlert size={24} /><strong>No source image</strong><small>Import or generate a still before creating video.</small></div>}
          <small className="endpoint-resolution">{firstCandidate?.width && firstCandidate.height ? `Source ${firstCandidate.width} × ${firstCandidate.height}` : 'Current image source'} · center-cropped to {proxyCanvas.width} × {proxyCanvas.height}</small>
          <label>Start on<select aria-label="Start frame" value={firstFrameCandidateId ?? ''} onChange={event => chooseFirst(event.target.value)}><option value="" disabled={!shot.currentAssetId}>{shot.currentAssetId ? `Current frame · v${shot.version}${shot.approval === 'Ratified' ? ' · approved' : ' · working'}` : `Current frame unavailable · v${shot.version}`}</option>{candidates.filter(item => !item.isCurrent).map(item => <option key={item.id} value={item.id}>Shot revision v{item.version} · {item.stage}{item.approval === 'Ratified' ? ' · approved' : ' · working'}</option>)}</select></label>
        </div>
        <div className={`video-anchor-card ${lastCandidate ? '' : 'optional-anchor'}`}>
          <span>END FRAME · OPTIONAL</span>
          {lastCandidate ? <CandidateArtwork candidate={lastCandidate} fallbackVariant={shot.visualVariant} /> : <div className="endpoint-empty"><Film size={24} /><strong>Motion-only ending</strong><small>Let motion settle naturally without forcing a second image.</small></div>}
          {lastCandidate && <small className="endpoint-resolution">Source {lastCandidate.width ?? '?'} × {lastCandidate.height ?? '?'} · center-cropped to {proxyCanvas.width} × {proxyCanvas.height}</small>}
          <label>Land on<select aria-label="End frame" value={lastFrameCandidateId ?? ''} onChange={event => chooseLast(event.target.value)}><option value="">No fixed end frame</option>{lastCandidates.map(item => <option key={item.id} value={item.id}>Shot revision v{item.version} · {item.stage}{item.approval === 'Ratified' ? ' · approved' : ' · working'}</option>)}</select></label>
        </div>
      </div>
      {lastCandidates.length === 0 && <p className="endpoint-help"><CircleAlert size={14} />To use a fixed ending, create another still revision on {shot.code}. It will appear here automatically; approval is optional.</p>}
      <label>Motion and directing brief<textarea value={brief} onChange={event => { setBrief(event.target.value); setManifest(undefined) }} /></label>
      {lastFrameCandidateId && <label className="endpoint-confirm"><input type="checkbox" checked={confirmed} onChange={event => { setConfirmed(event.target.checked); setManifest(undefined) }} />I reviewed scale, composition, subject identity, and camera compatibility. This endpoint is a target, not a command to leap.</label>}
      <p className="safety-copy"><Music2 size={15} />The frozen video prompt explicitly excludes music, dialogue, voices, narration, and sound effects. Add them later on separate timeline tracks.</p>
      {manifest && <div className="video-manifest-proof"><LockKeyhole size={16} /><span><strong>{manifest.videoQuality ?? quality} video settings ready</strong><small>{lastCandidate ? `v${firstCandidate?.version ?? shot.version} → v${lastCandidate.version}` : `v${firstCandidate?.version ?? shot.version} + natural motion`} · generation has not started</small></span></div>}
      <GenerationPacketProof summary={preflight} />
      <div className="adapter-picker" role="radiogroup" aria-label="Video generation engine">{adapters.map(adapter => <button type="button" role="radio" aria-checked={selectedAdapter === adapter.id} key={adapter.id} className={selectedAdapter === adapter.id ? 'selected' : ''} onClick={() => { setSelectedAdapter(adapter.id); setPreflight(undefined) }}><i><Film size={17} /></i><span><strong>{adapter.name}</strong><small>{adapter.detail}</small></span><em className={adapter.canDispatch ? 'ready' : 'protected'}>{adapter.canDispatch ? adapter.state : 'Setup'}</em></button>)}</div>
      {error && <p className="form-error" role="alert">{error}</p>}
    </div>
    <footer><span /><span /><button className="secondary" onClick={onClose}>Cancel</button>{manifest ? <button className="primary" onClick={() => void dispatch()} disabled={busy || !selected?.canDispatch}>{busy ? 'Starting...' : selected?.canDispatch ? `Generate ${quality} pass` : 'Set up this engine'}</button> : <button className="primary" onClick={() => void prepare()} disabled={busy || !hasFirstFrame || !brief.trim() || Boolean(lastFrameCandidateId && !confirmed)}>{busy ? 'Preparing...' : !hasFirstFrame ? 'Choose a Start Frame' : `Review ${quality} settings`}</button>}</footer>
  </Dialog>
}

const VIDEO_QUALITY_PROFILES: Record<Exclude<VideoQuality, 'Max'>, { label: string; short: string; detail: string; cost: string }> = {
  Low: { label: '0.4 MP motion proof', short: 'Project-ratio proxy', detail: 'Fastest route for testing motion and timing.', cost: 'Fast' },
  Medium: { label: '0.56 MP review pass', short: 'Project-ratio proxy', detail: 'Balanced detail for ordinary review.', cost: 'Balanced' },
  High: { label: '0.75 MP finish pass + delivery scale', short: 'Project-ratio finish', detail: 'Higher-detail render scaled to the project delivery canvas.', cost: 'Slower' },
}

function videoProxyCanvas(deliveryWidth: number, deliveryHeight: number, quality: Exclude<VideoQuality, 'Max'>) {
  const targetPixels = quality === 'Medium' ? 560_000 : quality === 'High' ? 750_000 : 400_000
  const aspect = deliveryWidth / deliveryHeight
  const height = Math.max(32, Math.round(Math.sqrt(targetPixels / aspect) / 32) * 32)
  const width = Math.max(32, Math.round(height * aspect / 32) * 32)
  return { width, height }
}

function canvasToPngBlob(canvas: HTMLCanvasElement, failureMessage: string) {
  return new Promise<Blob>((resolve, reject) => canvas.toBlob(value => {
    if (value && value.size > 0) { resolve(value); return }
    // WebKit can occasionally return a non-null zero-byte Blob for an
    // otherwise valid canvas. Re-encode through its data URL before surfacing
    // an error; a zero-byte guide must never reach the immutable manifest.
    try {
      const encoded = canvas.toDataURL('image/png')
      const separator = encoded.indexOf(',')
      if (separator < 0) throw new Error(failureMessage)
      const binary = window.atob(encoded.slice(separator + 1))
      const bytes = new Uint8Array(binary.length)
      for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index)
      const fallback = new Blob([bytes], { type: 'image/png' })
      if (fallback.size === 0) throw new Error(failureMessage)
      resolve(fallback)
    } catch { reject(new Error(failureMessage)) }
  }, 'image/png'))
}

function ShotCanvas({ shot, comments, references, displayVersion, readOnly, tool, placingReference, referenceDrop, onAddComment, onMoveComment, onResolveComment, onMarkupReady, onMarkupGuideReady, onCompositionGuideReady, deliveryWidth, deliveryHeight, videoUrl, previewCandidate, peeking = false, activeJob }: { shot: ShotSummary; comments: CommentSummary[]; references: ReferenceSummary[]; displayVersion: number; readOnly: boolean; tool: 'select' | 'draw' | 'comment'; placingReference?: ReferenceSummary; referenceDrop?: ReferenceSummary; onAddComment: (x: number, y: number) => void; onMoveComment: (id: string, x: number, y: number) => Promise<void>; onResolveComment: (id: string) => void; onMarkupReady: (ready: boolean) => void; onMarkupGuideReady: (guide?: { revision: number; exportAsset: () => Promise<string> }) => void; onCompositionGuideReady: (guide?: { exportAsset: () => Promise<string> }) => void; deliveryWidth: number; deliveryHeight: number; videoUrl?: string; previewCandidate?: CandidateVersionSummary; peeking?: boolean; activeJob?: JobSummary }) {
  const stageRef = useRef<HTMLDivElement>(null)
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const [activeComment, setActiveComment] = useState<string>()
  const [markupHistory, setMarkupHistory] = useState<SketchStroke[][]>([[]])
  const [markupIndex, setMarkupIndex] = useState(0)
  const [revision, setRevision] = useState(0)
  const [saveState, setSaveState] = useState<'loading' | 'saved' | 'saving' | 'error'>('loading')
  const [pinPositions, setPinPositions] = useState<Record<string, { x: number; y: number }>>({})
  const activeStroke = useRef<SketchStroke | null>(null)
  const pinDrag = useRef<{ id: string; x: number; y: number; moved: boolean } | undefined>(undefined)
  const suppressPinClick = useRef(false)
  const strokes = useMemo(() => markupHistory[markupIndex] ?? [], [markupHistory, markupIndex])
  const renderMarkup = useCallback((items: SketchStroke[]) => {
    const canvas = canvasRef.current; if (!canvas) return
    const rect = canvas.getBoundingClientRect(); const scale = window.devicePixelRatio || 1
    const width = Math.max(1, Math.round(rect.width * scale)); const height = Math.max(1, Math.round(rect.height * scale))
    if (canvas.width !== width || canvas.height !== height) { canvas.width = width; canvas.height = height }
    const context = canvas.getContext('2d'); if (!context) return
    context.setTransform(scale, 0, 0, scale, 0, 0); context.clearRect(0, 0, rect.width, rect.height)
    for (const stroke of items) {
      if (!stroke.points.length) continue
      context.strokeStyle = stroke.color; context.lineCap = 'round'; context.lineJoin = 'round'; context.lineWidth = stroke.width
      context.beginPath(); context.moveTo(stroke.points[0].x * rect.width, stroke.points[0].y * rect.height)
      for (const point of stroke.points.slice(1)) context.lineTo(point.x * rect.width, point.y * rect.height)
      if (stroke.points.length === 1) context.lineTo(stroke.points[0].x * rect.width + .01, stroke.points[0].y * rect.height + .01)
      context.stroke()
    }
  }, [])
  const persist = useCallback(async (next: SketchStroke[], expectedRevision: number) => {
    if (readOnly) return
    setSaveState('saving')
    try {
      const saved = await studioApi.saveFrameMarkup(shot.id, displayVersion, expectedRevision, next)
      setRevision(saved.revision); setSaveState('saved')
    } catch { setSaveState('error') }
  }, [displayVersion, readOnly, shot.id])
  const commitMarkup = useCallback((next: SketchStroke[]) => {
    if (saveState === 'saving') return
    setMarkupHistory(current => [...current.slice(0, markupIndex + 1), next])
    setMarkupIndex(markupIndex + 1)
    renderMarkup(next)
    void persist(next, revision)
  }, [markupIndex, persist, renderMarkup, revision, saveState])
  const stepMarkup = useCallback((nextIndex: number) => {
    if (saveState === 'saving' || nextIndex < 0 || nextIndex >= markupHistory.length) return
    const next = markupHistory[nextIndex]
    setMarkupIndex(nextIndex); renderMarkup(next); void persist(next, revision)
  }, [markupHistory, persist, renderMarkup, revision, saveState])
  const clearMarkup = useCallback(() => commitMarkup([]), [commitMarkup])
  const pointer = (event: ReactPointerEvent<HTMLCanvasElement>) => {
    const canvas = canvasRef.current; if (!canvas || readOnly || tool !== 'draw' || saveState === 'saving' || saveState === 'loading') return
    const rect = canvas.getBoundingClientRect(); const point: SketchPoint = { x: Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)), y: Math.min(1, Math.max(0, (event.clientY - rect.top) / rect.height)), pressure: event.pressure || .6 }
    const context = canvas.getContext('2d'); if (!context) return
    if (event.type === 'pointerdown') { activeStroke.current = { id: crypto.randomUUID(), points: [point], color: '#f2d4a7', width: 4 }; canvas.setPointerCapture(event.pointerId) }
    if (event.type === 'pointermove' && activeStroke.current) {
      const prior = activeStroke.current.points.at(-1)!; activeStroke.current.points.push(point)
      context.strokeStyle = activeStroke.current.color; context.lineCap = 'round'; context.lineJoin = 'round'; context.lineWidth = Math.max(2, activeStroke.current.width * point.pressure); context.beginPath(); context.moveTo(prior.x * rect.width, prior.y * rect.height); context.lineTo(point.x * rect.width, point.y * rect.height); context.stroke()
    }
    if ((event.type === 'pointerup' || event.type === 'pointercancel') && activeStroke.current) {
      const completed = activeStroke.current; activeStroke.current = null
      commitMarkup([...strokes, completed])
    }
  }
  useEffect(() => {
    let cancelled = false; setSaveState('loading'); setMarkupHistory([[]]); setMarkupIndex(0); setRevision(0)
    onMarkupReady(false)
    void studioApi.frameMarkup(shot.id, displayVersion).then(markup => { if (cancelled) return; setMarkupHistory([markup?.strokes ?? []]); setMarkupIndex(0); setRevision(markup?.revision ?? 0); setSaveState('saved'); onMarkupReady(!readOnly) }).catch(() => { if (!cancelled) { setSaveState('error'); onMarkupReady(false) } })
    return () => { cancelled = true }
  }, [displayVersion, onMarkupReady, readOnly, shot.id])
  // Selecting a different shot from the command palette keeps this component
  // mounted, and the resize handler copies the existing bitmap forward — so
  // without this, markup drawn on one shot reappears over the next one.
  useEffect(() => {
    renderMarkup(strokes); const canvas = canvasRef.current; if (!canvas) return
    const observer = new ResizeObserver(() => renderMarkup(strokes)); observer.observe(canvas); return () => observer.disconnect()
  }, [renderMarkup, strokes])
  const exportMarkupGuide = useCallback(async () => {
    const markup = canvasRef.current
    if (saveState !== 'saved') throw new Error('Wait for the edit guide to finish saving, then send the change again.')
    if (!markup || !shot.currentAssetUrl || strokes.length === 0) throw new Error('Draw markup on an imported frame before sending it as edit guidance.')
    const output = document.createElement('canvas'); output.width = markup.width; output.height = markup.height
    const context = output.getContext('2d'); if (!context) throw new Error('The browser could not prepare the annotation guide.')
    const image = new Image(); image.crossOrigin = 'anonymous'
    await new Promise<void>((resolve, reject) => { image.onload = () => resolve(); image.onerror = () => reject(new Error('The current frame could not be loaded for annotation guidance.')); image.src = shot.currentAssetUrl! })
    const scale = Math.max(output.width / image.naturalWidth, output.height / image.naturalHeight)
    const width = image.naturalWidth * scale; const height = image.naturalHeight * scale
    context.drawImage(image, (output.width - width) / 2, (output.height - height) / 2, width, height)
    context.drawImage(markup, 0, 0, output.width, output.height)
    const blob = await canvasToPngBlob(output, 'The annotation guide could not be encoded.')
    const asset = await studioApi.uploadImage(blob, `${shot.code.toLowerCase()}-v${shot.version}-markup-r${revision}.png`)
    return asset.id
  }, [revision, saveState, shot.code, shot.currentAssetUrl, shot.version, strokes.length])
  const exportCompositionGuide = useCallback(async () => {
    const svg = stageRef.current?.querySelector('.artwork svg')
    if (!svg) throw new Error('The storyboard layout is unavailable. Open the live storyboard frame and try again.')
    const clean = svg.cloneNode(true) as SVGSVGElement
    clean.setAttribute('xmlns', 'http://www.w3.org/2000/svg')
    clean.setAttribute('width', '1200')
    clean.setAttribute('height', '502')
    const source = new Blob([new XMLSerializer().serializeToString(clean)], { type: 'image/svg+xml;charset=utf-8' })
    const sourceUrl = URL.createObjectURL(source)
    try {
      const image = new Image()
      await new Promise<void>((resolve, reject) => { image.onload = () => resolve(); image.onerror = () => reject(new Error('The storyboard layout could not be rendered.')); image.src = sourceUrl })
      const output = document.createElement('canvas'); output.width = 1200; output.height = Math.max(1, Math.round(1200 * deliveryHeight / deliveryWidth))
      const context = output.getContext('2d'); if (!context) throw new Error('The browser could not prepare the storyboard layout.')
      const scale = Math.max(output.width / image.naturalWidth, output.height / image.naturalHeight)
      const width = image.naturalWidth * scale; const height = image.naturalHeight * scale
      context.drawImage(image, (output.width - width) / 2, (output.height - height) / 2, width, height)
      const blob = await canvasToPngBlob(output, 'The storyboard layout could not be encoded.')
      const asset = await studioApi.uploadImage(blob, `${shot.code.toLowerCase()}-v${displayVersion}-storyboard-layout.png`)
      return asset.id
    } finally { URL.revokeObjectURL(sourceUrl) }
  }, [deliveryHeight, deliveryWidth, displayVersion, shot.code])
  useEffect(() => {
    onMarkupGuideReady(!readOnly && strokes.length > 0 && shot.currentAssetUrl ? { revision, exportAsset: exportMarkupGuide } : undefined)
    return () => onMarkupGuideReady(undefined)
  }, [exportMarkupGuide, onMarkupGuideReady, readOnly, revision, shot.currentAssetUrl, strokes.length])
  useEffect(() => {
    onCompositionGuideReady(!readOnly && !shot.currentAssetUrl ? { exportAsset: exportCompositionGuide } : undefined)
    return () => onCompositionGuideReady(undefined)
  }, [exportCompositionGuide, onCompositionGuideReady, readOnly, shot.currentAssetUrl])
  useEffect(() => { setPinPositions(Object.fromEntries(comments.map(comment => [comment.id, { x: comment.x, y: comment.y }]))) }, [comments])
  const pinPoint = (event: ReactPointerEvent<HTMLButtonElement>) => {
    const rect = stageRef.current!.getBoundingClientRect()
    return { x: Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)), y: Math.min(1, Math.max(0, (event.clientY - rect.top) / rect.height)) }
  }
  const movePin = (event: ReactPointerEvent<HTMLButtonElement>, comment: CommentSummary) => {
    event.stopPropagation()
    if (readOnly) return
    if (event.type === 'pointerdown') {
      if (event.button !== 0) return
      event.currentTarget.setPointerCapture(event.pointerId)
      const point = pinPoint(event); pinDrag.current = { id: comment.id, ...point, moved: false }; suppressPinClick.current = false
    } else if (event.type === 'pointermove' && pinDrag.current?.id === comment.id) {
      const point = pinPoint(event)
      if (Math.hypot(point.x - pinDrag.current.x, point.y - pinDrag.current.y) > .004) pinDrag.current.moved = true
      pinDrag.current.x = point.x; pinDrag.current.y = point.y
      setPinPositions(current => ({ ...current, [comment.id]: point }))
    } else if ((event.type === 'pointerup' || event.type === 'pointercancel') && pinDrag.current?.id === comment.id) {
      const finished = pinDrag.current; pinDrag.current = undefined; suppressPinClick.current = finished.moved
      if (finished.moved) void onMoveComment(comment.id, finished.x, finished.y)
    }
  }
  const nudgePin = (event: React.KeyboardEvent<HTMLButtonElement>, comment: CommentSummary) => {
    if (readOnly) return
    const directions: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }
    const direction = directions[event.key]; if (!direction) return
    event.preventDefault(); event.stopPropagation()
    const current = pinPositions[comment.id] ?? { x: comment.x, y: comment.y }; const amount = event.shiftKey ? .05 : .01
    const next = { x: Math.min(1, Math.max(0, current.x + direction[0] * amount)), y: Math.min(1, Math.max(0, current.y + direction[1] * amount)) }
    setPinPositions(value => ({ ...value, [comment.id]: next })); void onMoveComment(comment.id, next.x, next.y)
  }
  const click = (event: React.MouseEvent<HTMLDivElement>) => { if (readOnly || tool !== 'comment') return; const rect = event.currentTarget.getBoundingClientRect(); onAddComment((event.clientX - rect.left) / rect.width, (event.clientY - rect.top) / rect.height) }
  const placementLabel = placingReference
    ? `Storyboard frame. Tap a location to place ${placingReference.name}, or press Enter to place it in the center.`
    : 'Storyboard frame. Tap a location to place feedback, or press Enter to place it in the center.'
  return <div ref={stageRef} className={`shot-canvas tool-${tool} ${referenceDrop ? 'reference-drop-ready' : ''} ${activeJob ? 'is-generating' : ''}`} onClick={click} onKeyDown={event => { if (!readOnly && event.key === 'Enter' && tool === 'comment') onAddComment(.5, .5) }} tabIndex={!readOnly && tool === 'comment' ? 0 : -1} aria-label={!readOnly && tool === 'comment' ? placementLabel : 'Storyboard frame'} data-reference-drop-target={!readOnly ? 'true' : undefined} data-testid="shot-canvas">
    {/* Holding \ peeks past the previewed candidate to the live frame — the
        cheapest A/B available, and it never leaves the page. */}
    {previewCandidate && !peeking
      ? <CandidateArtwork candidate={previewCandidate} fallbackVariant={shot.visualVariant} />
      : videoUrl && shot.stage === 'Video'
        ? <video className="shot-video" src={videoUrl} controls playsInline aria-label={`${shot.code} current video version ${shot.version}`} />
        : <ShotArtwork shot={shot} label={`${shot.code} current ${shot.stage} version ${shot.version}`} />}
    {activeJob && <div className="frame-generation-state" role="status" aria-live="polite" data-testid="frame-generation-state">
      <div className="generation-orbit"><LoaderCircle className="spin" /><Sparkles /></div>
      <p className="eyebrow">{activeJob.backend}</p><h2>{activeJob.kind === 'Final still' ? 'Building the production frame' : activeJob.kind === 'Video' ? 'Rendering motion' : 'Generating a new draft'}</h2>
      <p>{activeJob.phase}</p><div className="frame-job-progress" role="progressbar" aria-label={`${activeJob.kind} progress`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={activeJob.progress}><i style={{ width: `${activeJob.progress}%` }} /></div>
      <small>{activeJob.adapterId && ['openai-gpt-image', 'codex-imagegen'].includes(activeJob.adapterId) ? 'Precision image generation can take several minutes. This durable job keeps running if you leave the shot.' : 'This durable job keeps running if you leave the shot.'}</small>
    </div>}
    {previewCandidate && <div className={`preview-banner ${peeking ? 'peeking' : ''}`}>{peeking ? `Live · v${shot.version}` : `Reviewing v${previewCandidate.version} · hold \\ for live`}</div>}
    <canvas ref={canvasRef} onPointerDown={pointer} onPointerMove={pointer} onPointerUp={pointer} onPointerCancel={pointer} />
    <div className="composition-grid"><i /><i /><i /><i /></div>
    {referenceDrop && <div className="reference-drop-overlay" aria-hidden="true"><BadgeCheck size={22} /><span><strong>Drop {referenceDrop.name} here</strong><small>Place it on the exact face, object, or region</small></span></div>}
    {comments.map((comment, index) => { const position = pinPositions[comment.id] ?? { x: comment.x, y: comment.y }; const reference = references.find(item => item.id === comment.referenceId); const label = reference ? `${reference.name} v${comment.referenceVersion ?? reference.version}` : `Feedback ${index + 1}`; return <div key={comment.id} className="comment-anchor" style={{ left: `${position.x * 100}%`, top: `${position.y * 100}%` }}><button className={`comment-pin ${reference ? 'reference-pin' : ''}`} aria-label={`${label}: ${comment.body}.${readOnly ? ' Bound to this archived version.' : ' Drag to reposition; use arrow keys for fine adjustment.'}`} aria-expanded={activeComment === comment.id} title={readOnly ? `${label} bound to this version` : `Drag ${label} to reposition`} onPointerDown={event => movePin(event, comment)} onPointerMove={event => movePin(event, comment)} onPointerUp={event => movePin(event, comment)} onPointerCancel={event => movePin(event, comment)} onKeyDown={event => nudgePin(event, comment)} onClick={event => { event.stopPropagation(); if (suppressPinClick.current) { suppressPinClick.current = false; return } setActiveComment(current => current === comment.id ? undefined : comment.id) }}><span>{reference ? 'R' : index + 1}</span></button>{activeComment === comment.id && <div className={`comment-popover ${reference ? 'reference-popover' : ''}`} role="status"><strong>{label}</strong><p>{comment.body}</p><small>{readOnly ? `Saved with version ${displayVersion}. Promote this draft to add new feedback.` : reference ? 'Drag to move. This exact authority version and location are included in the next manifest.' : 'Drag to move. This note and its location are included when you apply and regenerate.'}</small>{!readOnly && <div className="comment-popover-actions"><button type="button" onClick={event => { event.stopPropagation(); setActiveComment(undefined); onResolveComment(comment.id) }}><X size={13} />Clear pin</button></div>}</div>}</div> })}
    {(strokes.length > 0 || markupHistory.length > 1 || saveState === 'error') && <div className={`markup-notice ${saveState === 'error' ? 'error' : ''}`} role="status">
      <PenLine size={14} /><span><strong>{saveState === 'saving' ? 'Saving edit guide…' : saveState === 'error' ? 'Markup needs attention' : strokes.length === 0 ? 'Edit guide cleared' : `AI edit guide saved · r${revision}`}</strong><small>{saveState === 'error' ? 'Could not save. Change shots to reload the studio copy.' : strokes.length === 0 ? 'The frame is clean. Redo restores the previous markup.' : 'Apply & regenerate attaches this guide to the current frame. Gold ink is never baked into the output.'}</small></span>
      <div className="markup-history-actions"><button type="button" aria-label="Undo markup" title="Undo markup" disabled={readOnly || saveState === 'saving' || markupIndex === 0} onClick={event => { event.stopPropagation(); stepMarkup(markupIndex - 1) }}><Undo2 size={14} /></button><button type="button" aria-label="Redo markup" title="Redo markup" disabled={readOnly || saveState === 'saving' || markupIndex >= markupHistory.length - 1} onClick={event => { event.stopPropagation(); stepMarkup(markupIndex + 1) }}><Redo2 size={14} /></button>{strokes.length > 0 && <button type="button" className="markup-clear" disabled={readOnly || saveState === 'saving'} onClick={event => { event.stopPropagation(); clearMarkup() }}>Clear</button>}</div>
    </div>}
    <div className="canvas-caption"><span>{shot.code} · {(previewCandidate && !peeking ? previewCandidate.stage : shot.stage).toUpperCase()} v{previewCandidate && !peeking ? previewCandidate.version : shot.version}</span><span>{shot.camera}</span></div>
  </div>
}

function VisualReconciliationDialog({ shot, audit, startInRepairMode = false, onClose, onChanged, onReturnToShot }: { shot: ShotSummary; audit: ShotVisualAuditSummary; startInRepairMode?: boolean; onClose: () => void; onChanged: (message: string) => Promise<void>; onReturnToShot: () => void }) {
  const existing = new Map(audit.decisions.map(item => [item.findingId, item]))
  const [decisions, setDecisions] = useState<Record<string, { action?: VisualReconciliationAction; moreDetails: string }>>(() => Object.fromEntries(audit.findings.map(finding => {
    const saved = existing.get(finding.id)
    return [finding.id, saved ?? { action: 'DecideLater', moreDetails: '' }]
  })))
  const [adapters, setAdapters] = useState<GenerationAdapterSummary[]>([])
  const [adapterId, setAdapterId] = useState('comfyui-fast-draft')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const allChosen = audit.findings.every(finding => decisions[finding.id]?.action)
  const actions = audit.findings.map(finding => decisions[finding.id]?.action)
  const hasFix = actions.includes('FixImage')
  const hasAdopt = actions.includes('AdoptImage')
  const hasStructuralFix = audit.findings.some(finding => decisions[finding.id]?.action === 'FixImage' && finding.suggestedRoute === 'RebuildFromSketch')
  const chooseAll = (action: VisualReconciliationAction) => setDecisions(current => Object.fromEntries(
    audit.findings.map(finding => [finding.id, { ...current[finding.id], action }]),
  ))
  useEffect(() => {
    void studioApi.generationAdapters().then(available => {
      const compatible = available.filter(item => item.purposes.includes('Draft') && item.id !== 'local-proof')
      setAdapters(compatible)
      const preferred = compatible.find(item => item.id === (hasStructuralFix ? 'comfyui-fast-draft' : 'codex-imagegen') && item.canDispatch)
      setAdapterId(preferred?.id ?? compatible.find(item => item.canDispatch)?.id ?? compatible[0]?.id ?? 'codex-imagegen')
    }).catch(reason => setError(reason instanceof Error ? reason.message : 'Could not load image engines.'))
  }, [hasStructuralFix])
  const save = async (applyContract: boolean) => {
    if (!allChosen) return
    setBusy(true); setError(undefined)
    try {
      const body = audit.findings.map(finding => ({ findingId: finding.id, action: decisions[finding.id].action!, moreDetails: decisions[finding.id].moreDetails.trim() }))
      const plan = await studioApi.reconcileVisualAudit(shot.id, audit.id, body, applyContract)
      if (plan.contractApplied && !plan.requiresImageGeneration) {
        await onChanged(plan.detail); onClose(); onReturnToShot(); return
      }
      if (!plan.requiresImageGeneration) {
        await onChanged(plan.detail); onClose(); return
      }
      const selected = adapters.find(item => item.id === adapterId)
      if (!selected?.canDispatch) throw new Error(`${selected?.name ?? 'The selected image engine'} needs setup.`)
      const sketch = await studioApi.sketch(shot.id)
      const generationShot = plan.updatedShot ?? shot
      if (plan.imageRoute === 'RebuildFromSketch' && !sketch?.compositionAssetId)
        throw new Error('This structural repair needs the saved storyboard composition. Open Sketch and save the composition before rebuilding.')
      const compositionAssetId = plan.imageRoute === 'RebuildFromSketch' ? sketch?.compositionAssetId : audit.assetId
      const sourceContract = plan.imageRoute === 'RebuildFromSketch'
        ? 'STRUCTURAL REBUILD: Use the attached storyboard composition as the binding layout. Reconstruct the entire finished frame from the shot contract and immutable authorities. Do not preserve, imitate, or use the rejected current image.'
        : 'TARGETED RECONSTRUCTION: Use the current frame only for unaffected composition and camera placement. Fully redraw the named anatomy, wardrobe, or rendering defects; do not trace or preserve malformed pixels.'
      const brief = `${generationShot.description}\nCamera: ${generationShot.camera}.\nAction: ${generationShot.action}.\n\nVISUAL AUDIT REPAIRS:\n${plan.generationDirection}\n\n${sourceContract}`
      const prepared = await studioApi.prepareManifest(shot.id, {
        expectedShotVersion: generationShot.version,
        expectedSketchRevision: sketch?.revision ?? 0,
        route: selected.routes.includes('PrecisionDraft') ? 'PrecisionDraft' : 'FastDraft',
        purpose: 'Draft',
        compositionAssetId,
        creativeBriefOverride: brief,
        allowSketchCompositionFallback: plan.imageRoute === 'RebuildFromSketch',
      })
      const proof = await studioApi.generationPreflight(prepared.id, selected.id)
      if (!proof.ready) throw new Error(proof.checks.filter(check => check.state === 'Block').map(check => check.detail).join(' '))
      await studioApi.dispatchManifest(prepared.id, prepared.manifestHash, selected.id)
      await onChanged(`${plan.imageRoute === 'RebuildFromSketch' ? 'Structural rebuild' : 'Targeted repair'} queued with ${selected.name}.`)
      onClose(); onReturnToShot()
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not apply the reconciliation plan.') }
    finally { setBusy(false) }
  }
  const selected = adapters.find(item => item.id === adapterId)
  return <Dialog className="modal-backdrop route-backdrop" onClose={onClose} labelledBy="visual-reconcile-title" initialFocus="[data-reconcile-choice]">
    <section className="route-modal visual-reconcile-modal">
      <header><div><p className="eyebrow">{startInRepairMode ? 'Repair before production' : 'Image ↔ shot contract'}</p><h2 id="visual-reconcile-title">Resolve image differences</h2></div><button onClick={onClose} aria-label="Close visual reconciliation">×</button></header>
      <div className="route-modal-body">
        <div className={`visual-audit-verdict gate-${audit.gateState.toLowerCase()}`}><CircleAlert size={20} /><span><strong>{audit.gateState} · {audit.findings.length} visible discrepanc{audit.findings.length === 1 ? 'y' : 'ies'}</strong><small>{audit.summary}</small></span></div>
        <p className="visual-lock-note"><LockKeyhole size={15} />Each difference starts unresolved, so opening this screen never commits a change. Character, world, style, and reusable authority records stay locked.</p>
        <div className="visual-bulk-actions" role="group" aria-label="Apply one decision to every difference"><span><strong>Choose for all</strong><small>Set every row now, then adjust exceptions below.</small></span><div><button type="button" onClick={() => chooseAll('FixImage')}><WandSparkles size={14} />Correct all</button><button type="button" onClick={() => chooseAll('AdoptImage')}><BadgeCheck size={14} />Accept all shown</button><button type="button" onClick={() => chooseAll('DecideLater')}><Pause size={14} />Leave all unresolved</button></div></div>
        <div className="visual-finding-list">{audit.findings.map((finding, index) => {
          const value = decisions[finding.id] ?? { moreDetails: '' }
          return <article className={`visual-finding severity-${finding.severity.toLowerCase()}`} key={finding.id}>
            <header><span>{index + 1}</span><div><strong>{finding.title}</strong><small>{finding.category} · {Math.round(finding.confidence * 100)}% confidence · {finding.suggestedRoute === 'RebuildFromSketch' ? 'structural rebuild' : 'targeted repair'}</small></div><em>{finding.severity}</em></header>
            <div className="visual-compare-copy"><p><span>Shot says</span>{finding.contractExpectation}</p><ArrowRight size={16} /><p><span>Image shows</span>{finding.observedImage}</p></div>
            <div className="visual-decision-buttons" role="radiogroup" aria-label={`Decision for ${finding.title}`}>
              {([['FixImage', 'Correct the image', 'Keep this shot requirement'], ['AdoptImage', 'Accept what’s shown', 'Change this shot to match the image'], ['DecideLater', 'Leave unresolved', 'Make no change yet']] as [VisualReconciliationAction, string, string][]).map(([action, label, detail]) => <button data-reconcile-choice={index === 0 && action === 'DecideLater' ? '' : undefined} type="button" role="radio" aria-checked={value.action === action} className={value.action === action ? 'selected' : ''} key={action} onClick={() => setDecisions(current => ({ ...current, [finding.id]: { ...value, action } }))}>{action === 'FixImage' ? <WandSparkles size={15} /> : action === 'AdoptImage' ? <BadgeCheck size={15} /> : <Pause size={15} />}<span><strong>{label}</strong><small>{detail}</small></span></button>)}
            </div>
            <label><span>More details <small>optional</small></span><textarea aria-label={`More details for ${finding.title}`} value={value.moreDetails} maxLength={1200} placeholder="Add context for this specific choice…" onChange={event => setDecisions(current => ({ ...current, [finding.id]: { ...value, moreDetails: event.target.value } }))} /></label>
          </article>
        })}</div>
        {hasAdopt && <details className="contract-proposal" open><summary><span><strong>Stable scene + narrow overrides</strong><small>The overall scene remains intact; only accepted differences take precedence</small></span></summary><p><b>Scene description stays</b>{shot.description}</p><p><b>Action stays</b>{shot.action}</p>{audit.findings.filter(finding => decisions[finding.id]?.action === 'AdoptImage').map(finding => <p key={finding.id}><b>Accepted difference</b>{finding.observedImage}</p>)}</details>}
        {hasFix && <><div className="route-heading"><span>Repair engine</span><small>{hasStructuralFix ? 'The incorrect image will be bypassed; the saved sketch becomes composition authority.' : 'The current frame remains a layout guide, not pixel authority for the defect.'}</small></div><div className="adapter-picker" role="radiogroup" aria-label="Visual repair engine">{adapters.map(item => <button type="button" role="radio" aria-checked={adapterId === item.id} className={adapterId === item.id ? 'selected' : ''} key={item.id} onClick={() => setAdapterId(item.id)}><i>{item.id === 'codex-imagegen' ? <Sparkles size={17} /> : <Cpu size={17} />}</i><span><strong>{item.name}</strong><small>{item.id === 'codex-imagegen' ? 'High-fidelity reconstruction with Codex ImageGen' : item.detail}</small></span><em className={item.canDispatch ? 'ready' : 'protected'}>{item.canDispatch ? 'Ready' : 'Setup'}</em></button>)}</div></>}
        {hasAdopt && hasFix && <p className="visual-mixed-note"><CircleAlert size={14} />Accepted differences become narrow shot overrides. Every selected correction is then combined into one generation using the full scene intent plus those overrides.</p>}
        {error && <p className="form-error" role="alert">{error}</p>}
      </div>
      <footer><button className="secondary" onClick={onClose}>Cancel</button><button className="primary" disabled={!allChosen || busy || (hasFix && !selected?.canDispatch)} onClick={() => void save(hasAdopt)}>{busy ? <LoaderCircle className="spin" size={16} /> : hasAdopt && !hasFix ? <BadgeCheck size={16} /> : hasFix ? <WandSparkles size={16} /> : <Pause size={16} />}{busy ? 'Applying decisions…' : hasAdopt && !hasFix ? 'Keep scene & accept selected' : hasFix ? `${hasAdopt ? 'Apply choices & generate' : 'Generate'} ${hasStructuralFix ? 'rebuilt' : 'corrected'} image` : 'Save unresolved decisions'}</button></footer>
    </section>
  </Dialog>
}

export function ReviewWorkspace({ studio, shot, comments, initialCandidateId, onReturnToShot, onResolveComment, onCandidatesChanged, generationBusy }: { studio: StudioSnapshot; shot: ShotSummary; comments: CommentSummary[]; initialCandidateId?: string; onReturnToShot: () => void; onResolveComment: (id: string) => void; onCandidatesChanged: (message: string) => Promise<void>; generationBusy: boolean }) {
  const [wipe, setWipe] = useState(52)
  const [mode, setMode] = useState<'Wipe' | 'Side by side' | 'Overlay'>('Wipe')
  const [candidates, setCandidates] = useState<CandidateVersionSummary[]>([])
  const [targetId, setTargetId] = useState<string>()
  const [continuity, setContinuity] = useState<ShotContinuityReport>()
  const [continuityError, setContinuityError] = useState<string>()
  const [visualAudit, setVisualAudit] = useState<ShotVisualAuditSummary | null>()
  const [visualAuditBusy, setVisualAuditBusy] = useState(false)
  const [visualAuditError, setVisualAuditError] = useState<string>()
  const [reconcileOpen, setReconcileOpen] = useState(false)
  const [decisionBusy, setDecisionBusy] = useState(false)
  const [decisionError, setDecisionError] = useState<string>()
  const wipeDragging = useRef(false)
  useEffect(() => { void studioApi.candidates(shot.id).then(next => { setCandidates(next); setTargetId(current => next.some(candidate => candidate.id === current && !candidate.isCurrent) ? current : next.some(candidate => candidate.id === initialCandidateId && !candidate.isCurrent) ? initialCandidateId : next.find(candidate => !candidate.isCurrent)?.id) }) }, [initialCandidateId, shot.id, shot.version])
  useEffect(() => {
    let current = true
    setContinuity(undefined)
    setContinuityError(undefined)
    void studioApi.continuity(shot.id)
      .then(next => { if (current) setContinuity(next) })
      .catch(error => { if (current) setContinuityError(error instanceof Error ? error.message : 'Continuity preflight could not be loaded.') })
    return () => { current = false }
  }, [shot.id, shot.version, comments.length])
  useEffect(() => {
    let current = true
    setVisualAudit(undefined); setVisualAuditError(undefined)
    void studioApi.visualAudit(shot.id)
      .then(next => { if (current) setVisualAudit(next) })
      .catch(error => { if (current) setVisualAuditError(error instanceof Error ? error.message : 'Visual check could not be loaded.') })
    return () => { current = false }
  }, [shot.id, shot.version, shot.currentAssetId])
  const runVisualAudit = async (force = false) => {
    if (!shot.currentAssetId || visualAuditBusy) return
    setVisualAuditBusy(true); setVisualAuditError(undefined)
    try {
      const next = await studioApi.runVisualAudit(shot.id, force)
      setVisualAudit(next)
      if (next.findings.length > 0) setReconcileOpen(true)
      setContinuity(await studioApi.continuity(shot.id))
    } catch (error) { setVisualAuditError(error instanceof Error ? error.message : 'Codex could not inspect this frame.') }
    finally { setVisualAuditBusy(false) }
  }
  const target = candidates.find(candidate => candidate.id === targetId)
  const chooseTarget = async () => {
    if (!target || decisionBusy || generationBusy) return
    setDecisionBusy(true); setDecisionError(undefined)
    try {
      const next = await studioApi.copyCandidateForward(shot.id, target.id, shot.version)
      await onCandidatesChanged(`v${target.version} copied forward as the new working v${next.version}.`)
      onReturnToShot()
    } catch (error) { setDecisionError(error instanceof Error ? error.message : 'Could not choose that revision.') }
    finally { setDecisionBusy(false) }
  }
  const discardTarget = async () => {
    if (!target || target.isCurrent || target.approval === 'Ratified' || decisionBusy || generationBusy) return
    setDecisionBusy(true); setDecisionError(undefined)
    try {
      const discardedVersion = target.version
      await studioApi.deleteCandidate(shot.id, target.id)
      const refreshed = await studioApi.candidates(shot.id)
      setCandidates(refreshed); setTargetId(refreshed.find(candidate => !candidate.isCurrent)?.id)
      await onCandidatesChanged(`Draft v${discardedVersion} discarded.`)
    } catch (error) { setDecisionError(error instanceof Error ? error.message : 'Could not discard that draft.') }
    finally { setDecisionBusy(false) }
  }
  const moveWipe = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (!wipeDragging.current && event.type !== 'pointerdown') return
    const rect = event.currentTarget.getBoundingClientRect()
    setWipe(Math.round(Math.min(100, Math.max(0, ((event.clientX - rect.left) / rect.width) * 100))))
  }
  return <main className="workspace review-workspace" data-testid="review-workspace">
    <section className="review-header"><div><p className="eyebrow">Review room</p><h1>{shot.code} · {shot.title}</h1></div><div className="segmented" role="group" aria-label="Comparison mode"><button aria-pressed={mode === 'Wipe'} className={mode === 'Wipe' ? 'active' : ''} onClick={() => setMode('Wipe')}>Wipe</button><button aria-pressed={mode === 'Side by side'} className={mode === 'Side by side' ? 'active' : ''} onClick={() => setMode('Side by side')}>Side by side</button><button aria-pressed={mode === 'Overlay'} className={mode === 'Overlay' ? 'active' : ''} onClick={() => setMode('Overlay')}>Overlay</button></div><label className="compare-target"><Layers3 size={14} /><span className="sr-only">Compare against</span><select aria-label="Compare against" value={targetId ?? ''} onChange={event => setTargetId(event.target.value)} disabled={!candidates.some(candidate => !candidate.isCurrent)}><option value="">No earlier candidate</option>{candidates.filter(candidate => !candidate.isCurrent).map(candidate => <option key={candidate.id} value={candidate.id}>v{candidate.version} · {candidate.stage} · {candidate.approval}</option>)}</select></label></section>
    <section className={`compare-view compare-${mode.toLowerCase().replaceAll(' ', '-')}`}>
      <div className="compare-label left">CURRENT · v{shot.version}</div><div className="compare-label right">{target ? `${target.stage.toUpperCase()} · v${target.version}` : 'NO EARLIER CANDIDATE'}</div>
      {mode === 'Side by side' ? <><ShotArtwork shot={shot} /><CandidateArtwork candidate={target} fallbackVariant={Math.max(1, shot.visualVariant - 1)} /></> : <div className={`compare-stack ${mode === 'Wipe' ? 'wipe-interactive' : ''}`} role={mode === 'Wipe' ? 'slider' : undefined} aria-label={mode === 'Wipe' ? 'Comparison wipe' : undefined} aria-valuemin={mode === 'Wipe' ? 0 : undefined} aria-valuemax={mode === 'Wipe' ? 100 : undefined} aria-valuenow={mode === 'Wipe' ? wipe : undefined} aria-valuetext={mode === 'Wipe' ? `${wipe}% current frame revealed` : undefined} tabIndex={mode === 'Wipe' ? 0 : -1} onPointerDown={event => { if (mode !== 'Wipe') return; wipeDragging.current = true; event.currentTarget.setPointerCapture(event.pointerId); moveWipe(event) }} onPointerMove={moveWipe} onPointerUp={() => { wipeDragging.current = false }} onPointerCancel={() => { wipeDragging.current = false }} onKeyDown={event => { if (mode !== 'Wipe') return; const keyValues: Partial<Record<string, number>> = { Home: 0, End: 100, PageDown: Math.max(0, wipe - 10), PageUp: Math.min(100, wipe + 10), ArrowLeft: Math.max(0, wipe - 2), ArrowDown: Math.max(0, wipe - 2), ArrowRight: Math.min(100, wipe + 2), ArrowUp: Math.min(100, wipe + 2) }; const next = keyValues[event.key]; if (next !== undefined) { event.preventDefault(); setWipe(next) } }}><CandidateArtwork candidate={target} fallbackVariant={Math.max(1, shot.visualVariant - 1)} muted={mode === 'Overlay'} /><div className="compare-current" style={{ clipPath: mode === 'Wipe' ? `inset(0 ${100 - wipe}% 0 0)` : undefined, opacity: mode === 'Overlay' ? .52 : 1 }}><ShotArtwork shot={shot} /></div>{mode === 'Wipe' && <div className="wipe-line" style={{ left: `${wipe}%` }}><span><ArrowRight className="flip" size={14} /><ArrowRight size={14} /></span></div>}</div>}
    </section>
    <section className="review-bottom">
      <div className="feedback-panel"><div className="panel-title"><span>Feedback</span><small>{comments.length} open on v{shot.version}</small></div>{comments.length === 0 ? <div className="empty-notes"><Check size={18} /><span>No open feedback on this version</span></div> : comments.map((comment, index) => { const reference = studio.references.find(item => item.id === comment.referenceId); return <div className={`feedback-row ${reference ? 'reference-feedback' : ''}`} key={comment.id}><span className="feedback-index">{reference ? 'R' : index + 1}</span><div>{reference && <strong>{reference.name} · v{comment.referenceVersion ?? reference.version}</strong>}<p>{comment.body}</p><small>{reference ? `${Math.round(comment.x * 100)}% across · ${Math.round(comment.y * 100)}% down` : `Frame ${shot.durationFrames}`} · Open</small></div><button onClick={() => onResolveComment(comment.id)} title={reference ? 'Resolve reference placement' : 'Resolve feedback'} aria-label={`Resolve feedback ${index + 1}`}><Check size={16} /></button></div> })}</div>
      <div className="continuity-panel" tabIndex={0} aria-label="Continuity preflight results"><div className="panel-title"><span>Continuity preflight</span><small>{continuity?.gateState ?? (continuityError ? 'Unavailable' : 'Checking')}</small></div>{continuityError && <div className="check-row state-block" role="alert"><CircleAlert className="red" size={17} /><span><strong>Preflight unavailable</strong><small>{continuityError}</small></span></div>}{continuity?.checks.map(check => <div className={`check-row state-${check.state.toLowerCase()}`} key={check.id}>{check.state === 'Pass' ? <Check size={17} /> : <CircleAlert className={check.state === 'Block' ? 'red' : check.state === 'Review' ? 'amber' : 'muted'} size={17} />}<span><strong>{check.title}</strong><small>{check.detail}</small></span></div>)}<div className={`visual-audit-card ${visualAudit ? `gate-${visualAudit.gateState.toLowerCase()}` : ''}`}><div><Scan size={17} /><span><strong>Codex visual check</strong><small>{visualAudit === undefined ? 'Loading image-bound evidence…' : visualAudit ? `${visualAudit.gateState} · ${visualAudit.summary}` : 'Not run for this exact image and shot contract'}</small></span></div>{visualAuditError && <p className="form-error" role="alert">{visualAuditError}</p>}<div className="visual-audit-actions"><button className="secondary compact" onClick={() => void runVisualAudit(Boolean(visualAudit))} disabled={!shot.currentAssetId || visualAuditBusy || generationBusy}>{visualAuditBusy ? <LoaderCircle className="spin" size={14} /> : <Scan size={14} />}{visualAuditBusy ? 'Codex is inspecting…' : visualAudit ? 'Check again' : 'Check visible frame'}</button>{visualAudit && visualAudit.findings.length > 0 && <button className="primary compact" onClick={() => setReconcileOpen(true)} disabled={visualAuditBusy || generationBusy}>Reconcile {visualAudit.findings.length}</button>}</div></div>{continuity && <p className="continuity-scope">{continuity.scopeNote}</p>}</div>
      <div className="decision-panel"><div><span className="authority-lock"><LockKeyhole size={15} />Revision decision</span><h2>{target ? `Prefer v${target.version}?` : 'Choose a comparison draft'}</h2><p>{target ? `Choosing it makes a new working copy above v${shot.version}; neither historical revision is overwritten.` : 'Select an earlier candidate from the menu to compare it with the latest working revision.'}</p>{decisionError && <p className="form-error" role="alert">{decisionError}</p>}</div><div className="decision-actions revision-actions"><button className="secondary" onClick={onReturnToShot} disabled={decisionBusy || generationBusy}><ArrowLeft size={16} />Back to versions</button>{target && <button className="danger" onClick={() => void discardTarget()} disabled={decisionBusy || generationBusy || target.approval === 'Ratified'} title={target.approval === 'Ratified' ? 'Ratified history cannot be deleted' : `Discard draft v${target.version}`}><Trash2 size={16} />Discard v{target.version}</button>}<button className="primary" onClick={() => void chooseTarget()} disabled={!target?.assetId || decisionBusy || generationBusy}><BadgeCheck size={18} />{decisionBusy ? 'Working…' : target ? `Choose v${target.version} — copy forward` : 'Choose this revision'}</button></div></div>
    </section>
    {reconcileOpen && visualAudit && <VisualReconciliationDialog shot={shot} audit={visualAudit} onClose={() => setReconcileOpen(false)} onChanged={onCandidatesChanged} onReturnToShot={onReturnToShot} />}
  </main>
}

export function SequenceWorkspace({ studio, selectedId, onSelect, onReordered, onJobQueued }: { studio: StudioSnapshot; selectedId: string; onSelect: (id: string) => void; onReordered: () => void; onJobQueued: (job: JobSummary) => void }) {
  const totalFrames = studio.shots.reduce((n, x) => n + x.durationFrames, 0)
  const [playing, setPlaying] = useState(false)
  const [playheadFrame, setPlayheadFrame] = useState(0)
  const [clips, setClips] = useState<TimelineClipSummary[]>([])
  const [profiles, setProfiles] = useState<VoiceProfileSummary[]>([])
  const [clipEditor, setClipEditor] = useState<TimelineClipSummary | 'new' | null>(null)
  const [voicesOpen, setVoicesOpen] = useState(false)
  const [musicOpen, setMusicOpen] = useState(false)
  const [synthesis, setSynthesis] = useState<VoiceSynthesisStatus>()
  const [mastering, setMastering] = useState<AudioMasteringStatus>()
  const [exportReadiness, setExportReadiness] = useState<ProductionExportReadiness>()
  const [exportError, setExportError] = useState<string>()
  const [timelineError, setTimelineError] = useState<string>()
  const observedVoiceJobs = useRef(new Set(studio.jobs
    .filter(job => job.workType === 'Voice' && (job.state === 'Completed' || job.state === 'Failed' || job.state === 'Cancelled'))
    .map(job => job.id)))
  const refreshTimeline = useCallback(async () => {
    try { const [nextClips, nextProfiles, nextSynthesis, nextMastering] = await Promise.all([studioApi.timelineClips(), studioApi.voiceProfiles(), studioApi.voiceSynthesisStatus(), studioApi.audioMasteringStatus()]); setClips(nextClips); setProfiles(nextProfiles); setSynthesis(nextSynthesis); setMastering(nextMastering); setTimelineError(undefined) }
    catch (error) { setTimelineError(error instanceof Error ? error.message : 'Could not load editorial tracks.') }
  }, [])
  useEffect(() => { void refreshTimeline() }, [refreshTimeline])
  useEffect(() => {
    const finished = studio.jobs.filter(job => job.workType === 'Voice' &&
      (job.state === 'Completed' || job.state === 'Failed' || job.state === 'Cancelled') &&
      !observedVoiceJobs.current.has(job.id))
    if (finished.length === 0) return
    finished.forEach(job => observedVoiceJobs.current.add(job.id))
    // Voice completion attaches generated media to the saved clip. Reload the
    // timeline in place so Guide becomes playable Media without navigating.
    void refreshTimeline()
  }, [refreshTimeline, studio.jobs])
  const refreshExportReadiness = useCallback(async () => {
    try { setExportReadiness(await studioApi.productionExportStatus()); setExportError(undefined) }
    catch (error) { setExportError(error instanceof Error ? error.message : 'Could not check production readiness.') }
  }, [])
  useEffect(() => { void refreshExportReadiness() }, [refreshExportReadiness, studio.shots])
  useEffect(() => {
    if (!exportReadiness?.activeJobCount) return
    const timer = window.setInterval(() => void refreshExportReadiness(), 5000)
    return () => window.clearInterval(timer)
  }, [exportReadiness?.activeJobCount, refreshExportReadiness])
  useEffect(() => {
    if (!playing) return
    const timer = window.setInterval(() => setPlayheadFrame(frame => {
      const next = frame + Math.max(1, Math.round(studio.project.framesPerSecond / 10)); if (next >= totalFrames) { setPlaying(false); return 0 }
      let cursor = 0; const active = studio.shots.find(candidate => { const hit = next >= cursor && next < cursor + candidate.durationFrames; cursor += candidate.durationFrames; return hit }); if (active && active.id !== selectedId) onSelect(active.id); return next
    }), 100)
    return () => window.clearInterval(timer)
  }, [playing, selectedId, studio.project.framesPerSecond, studio.shots, totalFrames, onSelect])
  const jumpToShot = (id: string) => { let start = 0; for (const shot of studio.shots) { if (shot.id === id) break; start += shot.durationFrames } setPlayheadFrame(start); onSelect(id) }
  const scrub = (event: ReactPointerEvent<HTMLDivElement>) => { const rect = event.currentTarget.getBoundingClientRect(); setPlayheadFrame(Math.round(Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width)) * totalFrames)) }
  const timecode = formatFrameTime(playheadFrame, studio.project.framesPerSecond)
  const currentShot = studio.shots.find(x => x.id === selectedId) ?? studio.shots[0]
  const currentShotStartFrame = currentShot
    ? studio.shots.slice(0, studio.shots.findIndex(shot => shot.id === currentShot.id)).reduce((frames, shot) => frames + shot.durationFrames, 0)
    : 0
  const moveShot = async (direction: -1 | 1) => { if (!currentShot) return; const ids = studio.shots.map(shot => shot.id); const index = ids.indexOf(currentShot.id); const target = index + direction; if (target < 0 || target >= ids.length) return; [ids[index], ids[target]] = [ids[target], ids[index]]; try { await studioApi.reorderShots(ids); onReordered() } catch (reason) { setTimelineError(reason instanceof Error ? reason.message : 'Could not reorder the sequence.') } }
  return <main className="workspace sequence-workspace" data-testid="sequence-workspace">
    <section className="sequence-header"><div><p className="eyebrow">Editorial assembly</p><h1>{studio.project.sequenceCode} · {studio.project.sequenceName}</h1></div><div className="sequence-header-actions"><div className="sequence-policy"><Music2 size={15} /><span><strong>Audio stays separate</strong><small>Never baked into generated video</small></span></div><SequenceExportMenu readiness={exportReadiness} error={exportError} onRefresh={refreshExportReadiness} />{mastering?.canMix ? <a className="secondary compact export-link" href="/api/export/audio-mix" download title={mastering.detail}><Volume2 size={15} />Mix WAV</a> : <button className="secondary compact" disabled title={mastering?.detail ?? 'Checking audio mastering readiness'}><Volume2 size={15} />Mix protected</button>}<button className="secondary compact" onClick={() => setVoicesOpen(true)}><Volume2 size={15} />Voice profiles</button><button className="secondary compact" onClick={() => setMusicOpen(true)}><Music2 size={15} />Music library</button><button className="primary compact" onClick={() => setClipEditor('new')}><Plus size={16} />Add audio</button></div></section>
    <section className="viewer-row"><div className="sequence-viewer">{currentShot && <ShotArtwork shot={currentShot} />}<div className="viewer-timecode">{timecode}</div></div><div className="sequence-notes"><h3>Sequence intent</h3><p>A restrained approach that accelerates only when the guards break the court's axis.</p><dl><div><dt>Runtime</dt><dd>{formatTime(totalFrames, studio.project.framesPerSecond)}</dd></div><div><dt>Format</dt><dd>{studio.project.aspectRatio} · {studio.project.framesPerSecond}fps</dd></div><div><dt>Audio</dt><dd>{clips.length} editable clips · {mastering?.clipsWithMedia ?? 0} with media</dd></div></dl></div></section>
    <section className="timeline-shell">
      <div className="timeline-toolbar"><button onClick={() => setPlaying(!playing)} className="play" aria-label={playing ? 'Pause sequence preview' : 'Play sequence preview'} aria-pressed={playing}>{playing ? <Pause size={17} fill="currentColor" /> : <Play size={17} fill="currentColor" />}</button><span className="timecode">{timecode}</span>{currentShot && <div className="timeline-order-actions"><button className="secondary compact" aria-label={`Move ${currentShot.code} earlier`} title="Move selected shot earlier" disabled={studio.shots[0]?.id === currentShot.id} onClick={() => void moveShot(-1)}><ArrowLeft size={14} /><span>Earlier</span></button><button className="secondary compact" aria-label={`Move ${currentShot.code} later`} title="Move selected shot later" disabled={studio.shots.at(-1)?.id === currentShot.id} onClick={() => void moveShot(1)}><span>Later</span><ArrowRight size={14} /></button></div>}<div className="timeline-status"><Scan size={15} />Local timing preview<em className="preview-tag">{clips.length} durable audio clips</em></div></div>
      <div className="overview-track" aria-label="Shot overview" onPointerDown={scrub}>{studio.shots.map(shot => <button key={shot.id} aria-label={`Select ${shot.code}, ${shot.title}`} aria-pressed={shot.id === selectedId} style={{ flex: shot.durationFrames }} onClick={() => jumpToShot(shot.id)} className={shot.id === selectedId ? 'active' : ''}><span>{shot.code}</span></button>)}</div>
      <div className="timeline-grid">
        <TrackHeader icon={<Film size={15} />} title="Shots" subtitle="Picture" />
        <div className="clip-lane shot-lane">{studio.shots.map(shot => <button key={shot.id} aria-label={`Select ${shot.code}, ${shot.title}`} aria-pressed={shot.id === selectedId} style={{ flex: shot.durationFrames }} onClick={() => jumpToShot(shot.id)} className={`timeline-clip ${shot.id === selectedId ? 'active' : ''}`}><ShotArtwork shot={shot} muted /><span>{shot.code}</span></button>)}</div>
        {(['Dialogue', 'Voice', 'Music'] as TimelineTrackKind[]).map(track => <TimelineTrack key={track} track={track} clips={clips.filter(clip => clip.track === track)} totalFrames={totalFrames} onEdit={setClipEditor} />)}
        <div className="playhead" style={{ '--playhead-ratio': playheadFrame / Math.max(1, totalFrames) } as React.CSSProperties}><span /></div>
      </div>
    </section>
    {timelineError && <div className="timeline-error" role="alert">{timelineError}</div>}
    <TimelineAudioPlayback clips={clips} playheadFrame={playheadFrame} fps={studio.project.framesPerSecond} playing={playing} />
    {clipEditor && <TimelineClipDialog clip={clipEditor === 'new' ? undefined : clipEditor} profiles={profiles} synthesis={synthesis} fps={studio.project.framesPerSecond} totalFrames={totalFrames} defaultStartFrame={currentShotStartFrame} currentShotCode={currentShot?.code} onClose={() => setClipEditor(null)} onJobQueued={onJobQueued} onSaved={() => { setClipEditor(null); void refreshTimeline() }} />}
    {voicesOpen && <VoiceProfilesDialog profiles={profiles} characters={studio.references.filter(reference => reference.category === 'Character')} onClose={() => setVoicesOpen(false)} onSaved={() => void refreshTimeline()} />}
    {musicOpen && <MusicLibraryDialog jobs={studio.jobs} framesPerSecond={studio.project.framesPerSecond} onClose={() => setMusicOpen(false)} onPlaced={() => void refreshTimeline()} onJobQueued={onJobQueued} />}
  </main>
}

function SequenceExportMenu({ readiness, error, onRefresh }: { readiness?: ProductionExportReadiness; error?: string; onRefresh: () => Promise<void> }) {
  const ready = readiness?.canExportProduction === true
  const status = error ? 'unavailable' : readiness ? ready ? 'ready' : 'blocked' : 'checking'
  const statusLabel = error ? 'Readiness unavailable' : readiness ? ready ? 'Production ready' : `${readiness.blockers.length} production ${readiness.blockers.length === 1 ? 'blocker' : 'blockers'}` : 'Checking package'
  const statusIcon = error || (readiness && !ready)
    ? <CircleAlert size={15} />
    : ready
      ? <Check size={15} />
      : <LoaderCircle className="spin" size={15} />
  return <details className={`sequence-export state-${status}`} onToggle={event => { if (event.currentTarget.open) void onRefresh() }}>
    <summary aria-label={`${statusLabel}. Open export packages.`}>{statusIcon}<span>{statusLabel}</span></summary>
    <div className="sequence-export-panel">
      <header><span className={`export-readiness-mark state-${status}`}>{statusIcon}</span><div><strong>{ready ? 'Production package is ready' : 'Export packages'}</strong><small>{readiness ? `${readiness.ratifiedShotCount} of ${readiness.shotCount} shots ratified · ${readiness.openNoteCount} open notes` : 'Checking the sequence against the production contract.'}</small></div><button type="button" className="export-refresh" onClick={() => void onRefresh()} aria-label="Refresh export readiness" title="Refresh export readiness"><RotateCcw size={15} /></button></header>
      <div className="export-package-options">
        <article className={ready ? 'is-ready' : 'is-blocked'}><span><strong>Production package</strong><small>Verified, ratified delivery with provenance and checksums.</small></span>{ready ? <a className="export-package-action primary" href="/api/export/production-package" download><Download size={15} />Download</a> : <button type="button" className="export-package-action" disabled title="Resolve every production blocker before delivery"><LockKeyhole size={15} />Protected</button>}</article>
        <article><span><strong>Working package</strong><small>Review copy of the current project, including unfinished work.</small></span><a className="export-package-action" href="/api/export/working-package" download><Download size={15} />Download</a></article>
      </div>
      {error && <p className="export-status-error" role="alert"><CircleAlert size={14} /><span>{error} The working package remains available.</span></p>}
      {readiness && readiness.blockers.length > 0 && <section className="export-findings blockers" aria-labelledby="production-blockers-title"><h3 id="production-blockers-title">Resolve before production</h3><ul>{readiness.blockers.map(blocker => <li key={blocker}><CircleAlert size={14} /><span>{blocker}</span></li>)}</ul></section>}
      {readiness && readiness.warnings.length > 0 && <section className="export-findings warnings" aria-labelledby="production-warnings-title"><h3 id="production-warnings-title">Package warnings</h3><ul>{readiness.warnings.map(warning => <li key={warning}><CircleAlert size={14} /><span>{warning}</span></li>)}</ul></section>}
      {readiness && ready && readiness.warnings.length === 0 && <p className="export-all-clear"><BadgeCheck size={15} />All production checks passed.</p>}
      <p className="export-working-note"><LockKeyhole size={13} />A working package is clearly labeled inside the archive and cannot be mistaken for production delivery.</p>
    </div>
  </details>
}

function TimelineAudioPlayback({ clips, playheadFrame, fps, playing }: { clips: TimelineClipSummary[]; playheadFrame: number; fps: number; playing: boolean }) {
  return <div className="sr-only" aria-hidden="true">{clips.filter(clip => clip.assetUrl).map(clip => <ClipAudio key={clip.id} clip={clip} playheadFrame={playheadFrame} fps={fps} playing={playing} />)}</div>
}

function ClipAudio({ clip, playheadFrame, fps, playing }: { clip: TimelineClipSummary; playheadFrame: number; fps: number; playing: boolean }) {
  const ref = useRef<HTMLAudioElement>(null); const active = playheadFrame >= clip.startFrame && playheadFrame < clip.startFrame + clip.durationFrames
  useEffect(() => {
    const audio = ref.current; if (!audio) return; audio.volume = Math.min(1, Math.max(0, clip.volume)); const expected = clip.trimStartSeconds + Math.max(0, playheadFrame - clip.startFrame) / fps; if (active && Math.abs(audio.currentTime - expected) > .35) audio.currentTime = expected
    if (playing && active) void audio.play().catch(() => { /* Browser gesture policy can still refuse programmatic preview. */ }); else audio.pause()
  }, [active, clip.startFrame, clip.trimStartSeconds, clip.volume, fps, playheadFrame, playing])
  return <audio ref={ref} src={clip.assetUrl} preload="metadata" />
}

function TimelineTrack({ track, clips, totalFrames, onEdit }: { track: TimelineTrackKind; clips: TimelineClipSummary[]; totalFrames: number; onEdit: (clip: TimelineClipSummary) => void }) {
  const icon = track === 'Dialogue' ? <MessageCircle size={15} /> : track === 'Voice' ? <Volume2 size={15} /> : <Music2 size={15} />
  return <><TrackHeader icon={icon} title={track === 'Voice' ? 'Voices' : track} subtitle={track === 'Music' ? 'Post only' : track === 'Voice' ? 'Consented profiles' : 'Guide / recorded'} /><div className="clip-lane">{clips.map(clip => <button key={clip.id} className={`audio-clip ${track.toLowerCase()}`} style={{ left: `${Math.min(100, clip.startFrame / Math.max(1, totalFrames) * 100)}%`, width: `${Math.max(3, Math.min(100, clip.durationFrames / Math.max(1, totalFrames) * 100))}%` }} onClick={() => onEdit(clip)} aria-label={`Edit ${track.toLowerCase()} clip ${clip.label}`}><span>{clip.label}<small>{clip.assetId ? 'Media' : 'Guide'} · {Math.round(clip.volume * 100)}%</small></span><Wave assetUrl={clip.assetUrl} seed={clip.id.length + clip.startFrame} /></button>)}</div></>
}

function TimelineClipDialog({ clip, profiles, synthesis, fps, totalFrames, defaultStartFrame, currentShotCode, onClose, onJobQueued, onSaved }: { clip?: TimelineClipSummary; profiles: VoiceProfileSummary[]; synthesis?: VoiceSynthesisStatus; fps: number; totalFrames: number; defaultStartFrame: number; currentShotCode?: string; onClose: () => void; onJobQueued: (job: JobSummary) => void; onSaved: () => void }) {
  const [track, setTrack] = useState<TimelineTrackKind>(clip?.track ?? 'Dialogue'); const [label, setLabel] = useState(clip?.label ?? ''); const [text, setText] = useState(clip?.text ?? ''); const [startFrame, setStartFrame] = useState(clip?.startFrame ?? defaultStartFrame); const [durationFrames, setDurationFrames] = useState(clip?.durationFrames ?? fps * 3); const [trim, setTrim] = useState(clip?.trimStartSeconds ?? 0); const [volume, setVolume] = useState(clip?.volume ?? 1); const [voiceProfileId, setVoiceProfileId] = useState(clip?.voiceProfileId ?? ''); const [assetId, setAssetId] = useState(clip?.assetId); const [busy, setBusy] = useState(false); const [error, setError] = useState<string>()
  const selectedVoiceProfile = profiles.find(profile => profile.id === voiceProfileId)
  const usesLocalQwen = selectedVoiceProfile?.provider.toLowerCase().includes('qwen3-tts') ?? false
  const voiceRouteReady = usesLocalQwen ? synthesis?.localCanSynthesize : synthesis?.canSynthesize
  const voiceRouteLabel = usesLocalQwen ? 'Local Qwen character voice' : 'OpenAI preset voice'
  const voiceRouteDetail = usesLocalQwen ? synthesis?.localDetail : synthesis?.detail
  const save = async () => { setBusy(true); setError(undefined); try { await studioApi.saveTimelineClip({ track, label, text, startFrame, durationFrames, trimStartSeconds: trim, volume, assetId, voiceProfileId: track === 'Voice' ? voiceProfileId || undefined : undefined, expectedUpdatedAt: clip?.updatedAt }, clip?.id); onSaved() } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not save the clip.') } finally { setBusy(false) } }
  const upload = async (file?: File) => { if (!file) return; setBusy(true); setError(undefined); try { const asset = await studioApi.uploadMedia(file, 'Audio'); setAssetId(asset.id) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not import audio.') } finally { setBusy(false) } }
  const remove = async () => { if (!clip) return; setBusy(true); try { await studioApi.deleteTimelineClip(clip.id); onSaved() } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not delete the clip.'); setBusy(false) } }
  const dirty = Boolean(clip && (clip.text !== text || clip.label !== label || clip.voiceProfileId !== voiceProfileId || clip.startFrame !== startFrame || clip.durationFrames !== durationFrames || clip.trimStartSeconds !== trim || clip.volume !== volume))
  const synthesize = async () => { if (!clip) return; setBusy(true); setError(undefined); try { const job = await studioApi.synthesizeVoice(clip.id, clip.updatedAt); onJobQueued(job); onSaved() } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not queue the saved voice clip.') } finally { setBusy(false) } }
  return <Dialog className="edit-dialog timeline-edit-dialog" onClose={onClose} labelledBy="timeline-clip-title"><header><div><p className="eyebrow">Separate editorial track</p><h2 id="timeline-clip-title">{clip ? 'Edit audio clip' : 'Add audio clip'}</h2></div><button onClick={onClose} aria-label="Close audio editor">×</button></header><div className="edit-dialog-body">{!clip && currentShotCode && <p className="safety-copy"><Check size={15} />Starts on selected shot {currentShotCode}. Adjust only when placing audio elsewhere in the sequence.</p>}<div className="form-grid"><label>Track<select value={track} onChange={event => setTrack(event.target.value as TimelineTrackKind)}><option>Dialogue</option><option>Voice</option><option>Music</option></select></label><label>Clip label<input value={label} onChange={event => setLabel(event.target.value)} placeholder="Line, performance, or cue" /></label></div>{track === 'Voice' && <label>Reusable voice profile<select value={voiceProfileId} onChange={event => setVoiceProfileId(event.target.value)}><option value="">Choose a consented or preset profile</option>{profiles.map(profile => <option key={profile.id} value={profile.id}>{profile.name} · {profile.characterName}</option>)}</select></label>}<label>Dialogue / editorial note<textarea value={text} onChange={event => setText(event.target.value)} /></label><div className="form-grid three"><label>Start frame<input type="number" min={0} max={totalFrames} value={startFrame} onChange={event => setStartFrame(Number(event.target.value))} /></label><label>Duration frames<input type="number" min={1} max={totalFrames} value={durationFrames} onChange={event => setDurationFrames(Number(event.target.value))} /></label><label>Source trim (sec)<input type="number" min={0} step="0.1" value={trim} onChange={event => setTrim(Number(event.target.value))} /></label></div><label className="range-field"><span>Volume <output>{Math.round(volume * 100)}%</output></span><input aria-label="Clip volume" type="range" min={0} max={2} step="0.01" value={volume} onChange={event => setVolume(Number(event.target.value))} /></label><label className="media-drop">Audio file<input type="file" accept="audio/*,.wav,.mp3,.flac,.ogg,.m4a,.webm" onChange={event => void upload(event.target.files?.[0])} /><small>{assetId ? 'Audio imported into the content-addressed studio library.' : 'Optional for dialogue guides. Required when the clip should play media.'}</small></label>{track === 'Voice' && <div className="voice-synthesis-action"><span><strong>{voiceRouteLabel}</strong><small>{voiceRouteDetail ?? 'Choose a voice profile to inspect its synthesis route.'}</small></span><button className="secondary" onClick={() => void synthesize()} disabled={busy || !clip || dirty || !text.trim() || !voiceRouteReady}>{dirty ? 'Save changes first' : clip ? assetId ? 'Regenerate speech' : 'Generate speech' : 'Create clip to generate'}</button></div>}{track === 'Music' && <p className="safety-copy"><Music2 size={15} />Music stays on this post-production lane and is never inserted into image or video prompts.</p>}{error && <p className="form-error" role="alert">{error}</p>}</div><footer>{clip && <button className="danger" onClick={() => void remove()} disabled={busy}>Delete clip</button>}<span /><button className="secondary" onClick={onClose}>Cancel</button><button className="primary" onClick={() => void save()} disabled={busy || !label.trim() || (track === 'Voice' && !voiceProfileId)}>{busy ? 'Saving...' : 'Save clip'}</button></footer></Dialog>
}

function VoiceProfilesDialog({ profiles, characters, onClose, onSaved }: { profiles: VoiceProfileSummary[]; characters: ReferenceSummary[]; onClose: () => void; onSaved: () => void }) {
  const [kind, setKind] = useState<VoiceProfileKind>('ProviderPreset'); const [name, setName] = useState(''); const [characterId, setCharacterId] = useState(''); const [provider, setProvider] = useState('OpenAI'); const [voiceId, setVoiceId] = useState(''); const [sampleAssetId, setSampleAssetId] = useState(''); const [sampleName, setSampleName] = useState(''); const [consent, setConsent] = useState(false); const [attestation, setAttestation] = useState(''); const [busy, setBusy] = useState(false); const [error, setError] = useState<string>()
  const selectedCharacter = characters.find(character => character.id === characterId)
  const uploadSample = async (file?: File) => { if (!file) return; setBusy(true); setError(undefined); try { const asset = await studioApi.uploadMedia(file, 'Audio'); setSampleAssetId(asset.id); setSampleName(file.name) } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not import the voice sample.') } finally { setBusy(false) } }
  const create = async () => { if (!selectedCharacter) return; setBusy(true); setError(undefined); try { await studioApi.createVoiceProfile({ name, kind, provider, providerVoiceId: voiceId, characterName: selectedCharacter.name, characterReferenceId: selectedCharacter.id, sampleAssetId, consentConfirmed: consent, consentAttestation: attestation }); setName(''); setVoiceId(''); setSampleAssetId(''); setSampleName(''); setAttestation(''); setConsent(false); onSaved() } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not create the profile.') } finally { setBusy(false) } }
  return <Dialog className="edit-dialog voice-dialog" onClose={onClose} labelledBy="voice-profiles-title"><header><div><p className="eyebrow">Reusable voice authority</p><h2 id="voice-profiles-title">Character voices</h2></div><button onClick={onClose} aria-label="Close voice profiles">×</button></header><div className="edit-dialog-body"><div className="voice-profile-list">{profiles.length ? profiles.map(profile => <div className="voice-profile-card" key={profile.id}><Volume2 size={16} /><span><strong>{profile.name}</strong><small>{profile.characterName} · {profile.provider} · {profile.kind === 'ConsentedClone' ? 'Consent recorded' : 'Provider preset'}</small>{profile.sampleAssetUrl && <audio controls preload="metadata" src={profile.sampleAssetUrl}>Voice sample for {profile.characterName}</audio>}</span><BadgeCheck size={16} /></div>) : <p>No reusable voices yet.</p>}</div><h3>Create voice authority</h3><div className="form-grid"><label>Profile type<select value={kind} onChange={event => setKind(event.target.value as VoiceProfileKind)}><option value="ProviderPreset">Provider preset</option><option value="ConsentedClone">Consented clone</option></select></label><label>Profile name<input value={name} onChange={event => setName(event.target.value)} placeholder="Ennix production voice" /></label><label>Character authority<select value={characterId} onChange={event => setCharacterId(event.target.value)}><option value="">Choose an approved character</option>{characters.map(character => <option key={character.id} value={character.id}>{character.name} · v{character.version}</option>)}</select></label><label>Provider<input value={provider} onChange={event => setProvider(event.target.value)} /></label></div><label>Provider voice ID<input value={voiceId} onChange={event => setVoiceId(event.target.value)} /></label><label className="media-drop">Approved voice sample<input type="file" accept="audio/*,.wav,.mp3,.flac,.ogg,.m4a,.webm" onChange={event => void uploadSample(event.target.files?.[0])} /><small>{sampleAssetId ? `${sampleName} · ready to attach` : 'Required review proof. This playable sample appears with the character authority.'}</small></label>{kind === 'ConsentedClone' && <div className="consent-block"><label><input type="checkbox" checked={consent} onChange={event => setConsent(event.target.checked)} />I confirm the speaker explicitly consented to this reusable cloned voice.</label><label>Consent record<textarea value={attestation} onChange={event => setAttestation(event.target.value)} placeholder="Who consented, what they consented to, when, and where the record is held." /></label></div>}<p className="safety-copy"><LockKeyhole size={15} />The character link, provider identity, playable sample, and consent record travel as one voice authority. Secrets remain outside the profile.</p>{error && <p className="form-error" role="alert">{error}</p>}</div><footer><span /><button className="secondary" onClick={onClose}>Done</button><button className="primary" onClick={() => void create()} disabled={busy || !name.trim() || !characterId || !provider.trim() || !voiceId.trim() || !sampleAssetId || (kind === 'ConsentedClone' && (!consent || attestation.trim().length < 20))}>{busy ? 'Saving...' : 'Create voice authority'}</button></footer></Dialog>
}

function TrackHeader({ icon, title, subtitle }: { icon: React.ReactNode; title: string; subtitle: string }) {
  return <div className="track-header">{icon}<span><strong>{title}</strong><small>{subtitle}</small></span></div>
}
const waveformCache = new Map<string, number[]>()
const waveformGuideCache = new Set<string>()
const maxDecodedWaveformBytes = 8 * 1024 * 1024
const guidePeaks = (seed: number) => Array.from({ length: 42 }, (_, i) => 18 + (((i + seed) * 17) % 68))
function Wave({ assetUrl, seed = 0 }: { assetUrl?: string; seed?: number }) {
  const [peaks, setPeaks] = useState<number[]>(() => assetUrl ? waveformCache.get(assetUrl) ?? [] : [])
  useEffect(() => {
    if (!assetUrl || waveformCache.has(assetUrl)) return
    let active = true
    const controller = new AbortController()
    let context: AudioContext | undefined
    const closeContext = () => { if (context && context.state !== 'closed') void context.close().catch(() => undefined) }
    const fallback = guidePeaks(seed)
    void (async () => {
      const response = await fetch(assetUrl, { signal: controller.signal })
      if (!response.ok) throw new Error(`Waveform source returned ${response.status}.`)
      const declaredBytes = Number(response.headers.get('content-length'))
      if (!Number.isFinite(declaredBytes) || declaredBytes <= 0 || declaredBytes > maxDecodedWaveformBytes) {
        await response.body?.cancel()
        waveformCache.set(assetUrl, fallback)
        waveformGuideCache.add(assetUrl)
        if (active) setPeaks(fallback)
        return
      }
      const bytes = await response.arrayBuffer()
      context = new AudioContext()
      const buffer = await context.decodeAudioData(bytes)
      const channel = buffer.getChannelData(0)
      const next = Array.from({ length: 42 }, (_, index) => {
        const start = Math.floor(index * channel.length / 42)
        const end = Math.max(start + 1, Math.floor((index + 1) * channel.length / 42))
        let peak = 0
        for (let offset = start; offset < end; offset++) peak = Math.max(peak, Math.abs(channel[offset]))
        return Math.max(12, Math.round(peak * 100))
      })
      waveformCache.set(assetUrl, next)
      waveformGuideCache.delete(assetUrl)
      if (active) setPeaks(next)
    })().catch(() => undefined).finally(closeContext)
    return () => { active = false; controller.abort(); closeContext() }
  }, [assetUrl, seed])
  const values = peaks.length ? peaks : guidePeaks(seed)
  return <div className="wave" aria-hidden="true" data-waveform={peaks.length && (!assetUrl || !waveformGuideCache.has(assetUrl)) ? 'decoded' : 'guide'}>{values.map((height, i) => <i key={i} style={{ height: `${height}%` }} />)}</div>
}

export function StageBadge({ shot }: { shot: ShotSummary }) {
  const label = shot.approval === 'Ratified' ? `${shot.stage} ratified` : shot.approval === 'NeedsWork' ? 'Needs work' : `${shot.stage} v${shot.version}`
  return <span className={`stage-badge stage-${shot.stage.toLowerCase()} approval-${shot.approval.toLowerCase()}`}>{shot.approval === 'Ratified' && <BadgeCheck size={13} />}{label}</span>
}

function formatTime(frames: number, fps: number) { const seconds = frames / fps; return seconds < 60 ? `${seconds.toFixed(seconds % 1 ? 1 : 0)}s` : `${Math.floor(seconds / 60)}:${String(Math.round(seconds % 60)).padStart(2, '0')}` }
function formatFrameTime(frames: number, fps: number) { const seconds = Math.floor(frames / fps); const frame = frames % fps; return `${String(Math.floor(seconds / 3600)).padStart(2, '0')}:${String(Math.floor((seconds % 3600) / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}:${String(frame).padStart(2, '0')}` }
