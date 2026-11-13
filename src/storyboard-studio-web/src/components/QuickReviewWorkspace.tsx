import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ArrowLeft, ArrowRight, Check, ClipboardCopy, CornerUpLeft, LoaderCircle, RefreshCw, ScanSearch, Undo2, X } from 'lucide-react'
import { studioApi } from '../api'
import {
  matchesReviewFilter, nextInQueue, REVIEW_LABELS, reviewQueue, revisionLabel, sendBackNote, sentBackList, waitingCount,
  type QuickReviewSession, type QuickReviewUndo, type ReviewFilter,
} from '../assetReview'
import type { KeptCamera } from '../inspectionCamera'
import { openingLook, type SurfaceLook } from '../surfaceChannels'
import type { AssetReviewDecision, AssetSummary, ModelProfileSummary } from '../types'

const ModelViewer = lazy(() => import('./ModelViewer'))

const FILTERS: { id: ReviewFilter; label: string }[] = [
  { id: 'Pending', label: 'Pending' },
  { id: 'ChangesRequested', label: 'Changes requested' },
  { id: 'Approved', label: 'Approved' },
  { id: 'all', label: 'All' },
]

/**
 * Quick review: one asset at a time, decided from the keyboard.
 *
 * Approve or send back and the next asset still waiting opens at once, so a
 * library of pending revisions is gone through without hunting for cards. The
 * decisions go through the same review endpoint as the inspection panel, with
 * the same three values; nothing here decides anything on its own.
 *
 * What was decided in this sitting stays in the walk, so the previous arrow
 * finds it and Z puts it back.
 */
export default function QuickReviewWorkspace({ assets, session, onSession, onExit, onInspect, onDecided, onRefresh }: {
  /** The whole library as the asset workspace holds it, archived and earlier revisions included. */
  assets: AssetSummary[]
  session: QuickReviewSession
  onSession: (session: QuickReviewSession) => void
  onExit: () => void
  onInspect: (assetId: string) => void
  onDecided: (updated: AssetSummary) => void
  onRefresh: () => Promise<void>
}) {
  const [filter, setFilter] = useState<ReviewFilter>(session.filter)
  const [kept, setKept] = useState<Set<string>>(session.kept)
  const [undo, setUndo] = useState<QuickReviewUndo[]>(session.undo)
  const [currentId, setCurrentId] = useState<string | undefined>(session.currentId)
  const [finished, setFinished] = useState(false)
  // The optional feedback under Send back, and what it opened with: an asset
  // already carrying a note shows it here to be edited.
  const [reason, setReason] = useState('')
  const [prefilled, setPrefilled] = useState('')
  const feedback = useRef<HTMLTextAreaElement>(null)
  const [busy, setBusy] = useState(false)
  const busyNow = useRef(false)
  const [error, setError] = useState<string>()
  const [status, setStatus] = useState<string>()
  // A decision made here is held over the library's copy until a refresh that
  // already knows about it arrives, so a refresh that left before the save
  // cannot put the old decision back on screen.
  const [overrides, setOverrides] = useState<Map<string, AssetSummary>>(new Map())
  const profiles = useRef(new Map<string, ModelProfileSummary>())
  const keptCamera = useRef<KeptCamera | undefined>(undefined)
  const keptLook = useRef<SurfaceLook>(openingLook())

  useEffect(() => { onSession({ filter, kept, undo, currentId }) }, [filter, kept, undo, currentId, onSession])

  const merged = useMemo(() => assets.map(asset => {
    const mine = overrides.get(asset.id)
    return mine && Date.parse(mine.updatedAt) >= Date.parse(asset.updatedAt) ? mine : asset
  }), [assets, overrides])
  const queue = useMemo(() => reviewQueue(merged, filter, kept), [merged, filter, kept])
  const waiting = waitingCount(queue, filter)
  const counts = useMemo(() => Object.fromEntries(FILTERS.map(item => [item.id, reviewQueue(merged, item.id).length])) as Record<ReviewFilter, number>, [merged])

  // The asset on screen. One that was replaced by a newer revision while it
  // was open is followed to that revision; otherwise the first still waiting.
  const index = useMemo(() => {
    if (queue.length === 0) return -1
    const at = currentId ? queue.findIndex(asset => asset.id === currentId) : -1
    if (at >= 0) return at
    const was = currentId ? merged.find(asset => asset.id === currentId) : undefined
    const successor = was?.revisionFamilyId ? queue.findIndex(asset => asset.revisionFamilyId === was.revisionFamilyId) : -1
    if (successor >= 0) return successor
    const first = queue.findIndex(asset => matchesReviewFilter(asset, filter))
    return first >= 0 ? first : 0
  }, [currentId, filter, merged, queue])
  const current = index >= 0 ? queue[index] : undefined

  // The box follows the asset on screen and its saved note: moving on clears
  // it, coming back to a sent-back asset shows what was said. A refresh that
  // changes nothing about the decision leaves whatever is being typed alone.
  const shownNote = current?.reviewNote ?? ''
  useEffect(() => { setReason(shownNote); setPrefilled(shownNote) },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [current?.id, current?.reviewDecidedAt])

  // Codex keeps importing while this is open, so the library is asked again
  // on the way in and shortly after decisions stop landing.
  const refreshTimer = useRef<number | undefined>(undefined)
  const scheduleRefresh = useCallback(() => {
    window.clearTimeout(refreshTimer.current)
    refreshTimer.current = window.setTimeout(() => { void onRefresh().catch(() => undefined) }, 1500)
  }, [onRefresh])
  useEffect(() => { void onRefresh().catch(() => undefined) }, [onRefresh])
  useEffect(() => () => window.clearTimeout(refreshTimer.current), [])

  const reset = () => { setError(undefined) }

  const go = (step: number) => {
    if (busyNow.current || queue.length === 0 || index < 0) return
    const target = finished && step < 0 ? index : Math.max(0, Math.min(queue.length - 1, index + step))
    reset(); setFinished(false); setStatus(undefined)
    setCurrentId(queue[target].id)
  }

  const chooseFilter = (next: ReviewFilter) => {
    if (next === filter) return
    reset(); setFinished(false); setStatus(undefined)
    setFilter(next); setKept(new Set()); setCurrentId(undefined)
  }

  const decide = async (decision: AssetReviewDecision, note?: string) => {
    if (!current || busyNow.current || finished) return
    const target = current
    const advance = (list: AssetSummary[], keptNow: Set<string>) => {
      const next = nextInQueue(reviewQueue(list, filter, keptNow), target.id, filter)
      if (next) setCurrentId(next)
      else { setCurrentId(target.id); setFinished(true) }
    }
    // Deciding again what is already decided, with the same note, saves
    // nothing; it just moves on.
    if (decision === target.reviewDecision && (note ?? '') === target.reviewNote) { reset(); advance(merged, kept); return }
    busyNow.current = true; setBusy(true); setError(undefined)
    try {
      const updated = await studioApi.setAssetReview(target.id, decision, note)
      const keptNow = new Set(kept).add(target.id)
      setOverrides(map => new Map(map).set(updated.id, updated))
      setKept(keptNow)
      setUndo(stack => [...stack.slice(-99), {
        assetId: target.id, displayName: target.displayName, made: decision,
        decision: target.reviewDecision, note: target.reviewNote,
      }])
      onDecided(updated)
      setStatus(`${decision === 'Approved' ? 'Approved' : 'Sent back'} ${updated.displayName}. Z undoes it.`)
      advance(merged.map(asset => asset.id === updated.id ? updated : asset), keptNow)
      scheduleRefresh()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'That decision could not be saved.')
    } finally { busyNow.current = false; setBusy(false) }
  }

  const undoLast = async () => {
    const last = undo[undo.length - 1]
    if (!last || busyNow.current) return
    busyNow.current = true; setBusy(true); setError(undefined)
    try {
      const restored = await studioApi.setAssetReview(last.assetId, last.decision, last.decision === 'Pending' ? undefined : last.note || undefined)
      setOverrides(map => new Map(map).set(restored.id, restored))
      setKept(set => new Set(set).add(restored.id))
      setUndo(stack => stack.slice(0, -1))
      onDecided(restored)
      reset(); setFinished(false)
      setCurrentId(restored.id)
      setStatus(`Undone. ${restored.displayName} is back to ${REVIEW_LABELS[last.decision]}.`)
      scheduleRefresh()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'That decision could not be undone.')
    } finally { busyNow.current = false; setBusy(false) }
  }

  // Send back takes whatever is in the box, or nothing. Approve ignores the
  // box unless something was typed into it, and then keeps it with the
  // approval rather than dropping it; a note it only opened with stays put.
  const sendBack = () => { void decide('ChangesRequested', sendBackNote(reason)) }
  const approve = () => {
    const typed = reason.trim()
    const edited = typed !== prefilled.trim()
    void decide('Approved', edited ? typed || undefined : current?.reviewDecision === 'Approved' ? current.reviewNote || undefined : undefined)
  }

  const [copied, setCopied] = useState<string>()
  const copyList = async () => {
    const text = sentBackList(merged)
    if (!text) { setCopied('Nothing is sent back right now.'); return }
    const lines = text.split('\n').length
    try {
      await navigator.clipboard.writeText(text)
    } catch {
      // The clipboard API can be refused; a selected text area copies anyway.
      const area = document.createElement('textarea')
      area.value = text; area.setAttribute('readonly', ''); area.style.position = 'fixed'; area.style.opacity = '0'
      document.body.appendChild(area); area.select()
      const done = document.execCommand('copy')
      area.remove()
      if (!done) { setCopied('The clipboard refused. Nothing was copied.'); return }
    }
    setCopied(`Copied ${lines} line${lines === 1 ? '' : 's'}.`)
  }
  useEffect(() => { if (!copied) return; const timer = window.setTimeout(() => setCopied(undefined), 3000); return () => window.clearTimeout(timer) }, [copied])

  // The keys. Listened for before anything else on the page so the left and
  // right arrows move through the list rather than turning the model; up and
  // down still tilt it. Held keys never repeat a decision.
  useEffect(() => {
    const keys = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey) return
      const target = event.target as HTMLElement | null
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)) return
      if (document.querySelector('dialog[open]')) return
      if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
        event.preventDefault(); event.stopPropagation()
        go(event.key === 'ArrowLeft' ? -1 : 1)
        return
      }
      if (event.repeat) return
      const key = event.key.toLowerCase()
      if (key === 'a') { event.preventDefault(); approve() }
      else if (key === 'x') { event.preventDefault(); sendBack() }
      else if (key === 'z') { event.preventDefault(); void undoLast() }
    }
    window.addEventListener('keydown', keys, true)
    return () => window.removeEventListener('keydown', keys, true)
  })

  // The next model is fetched while this one is looked at, so moving on does
  // not wait for a download. Only one ahead, and only once this one is up.
  const upNext = index >= 0 ? queue[index + 1] : undefined
  useEffect(() => {
    if (!upNext || upNext.kind !== 'Model') return
    const timer = window.setTimeout(() => {
      if (!profiles.current.has(upNext.id))
        studioApi.modelProfile(upNext.id).then(profile => { profiles.current.set(upNext.id, profile) }).catch(() => undefined)
      void fetch(upNext.contentUrl).then(response => response.blob()).catch(() => undefined)
    }, 1500)
    return () => window.clearTimeout(timer)
  }, [upNext])

  const last = undo[undo.length - 1]
  const decided = queue.length - waiting
  const filterLabel = filter === 'all' ? 'assets' : REVIEW_LABELS[filter].toLowerCase()
  const showDone = finished || queue.length === 0

  return <main className="workspace quick-review" data-testid="quick-review">
    <header className="quick-review-header">
      <button type="button" className="secondary compact" onClick={onExit}><ArrowLeft size={16} />Asset library</button>
      <div className="quick-review-title"><p className="eyebrow">Quick review</p><h1>{showDone ? 'All caught up' : current?.displayName}</h1></div>
      <div className="quick-review-filters" role="group" aria-label="Show">
        {FILTERS.map(item => <button key={item.id} type="button" aria-pressed={filter === item.id} className={filter === item.id ? 'is-on' : undefined}
          data-testid={`quick-review-filter-${item.id}`} onClick={() => chooseFilter(item.id)}>
          {item.label}<small>{counts[item.id]}</small>
        </button>)}
      </div>
      <div className="quick-review-progress" data-testid="quick-review-progress">
        <strong>{queue.length === 0 ? `No ${filterLabel}` : `${index + 1} of ${queue.length} ${filterLabel}`}</strong>
        <small>{filter === 'all' ? `${counts.Pending} still pending` : `${waiting} left${decided > 0 ? ` · ${decided} decided here` : ''}`}</small>
        <span className="quick-review-meter" aria-hidden="true"><i style={{ width: `${queue.length ? Math.round((decided / queue.length) * 100) : 100}%` }} /></span>
      </div>
      <button type="button" className="secondary compact" aria-label="Refresh from the library" title="Ask the library again for new revisions"
        onClick={() => void onRefresh().catch(reason => setError(reason instanceof Error ? reason.message : 'Could not refresh.'))}><RefreshCw size={15} /></button>
      {filter === 'ChangesRequested' && <button type="button" className="secondary compact" data-testid="quick-review-copy" onClick={() => void copyList()}>
        <ClipboardCopy size={15} />Copy list
      </button>}
    </header>

    <div className="quick-review-layout">
      <section className="quick-review-stage" aria-live="polite">
        {showDone
          ? <div className="quick-review-done" data-testid="quick-review-done">
              <Check size={30} />
              <h2>{queue.length === 0 ? `Nothing ${filter === 'all' ? 'in the library' : filterLabel}` : `No ${filterLabel} left`}</h2>
              <p>{counts.Approved} approved · {counts.ChangesRequested} sent back · {counts.Pending} pending in this project.</p>
              <div>
                {counts.ChangesRequested > 0 && <button type="button" className="primary" onClick={() => void copyList()}><ClipboardCopy size={16} />Copy sent-back list</button>}
                {queue.length > 0 && <button type="button" className="secondary" onClick={() => go(-1)}><ArrowLeft size={16} />Back to the last one</button>}
                <button type="button" className="secondary" onClick={onExit}>Asset library</button>
              </div>
            </div>
          : current && <ReviewStage key={current.id} asset={current} profiles={profiles.current} kept={keptCamera} look={keptLook} />}
      </section>

      <aside className="quick-review-side" aria-label="About this asset">
        {current && !showDone && <>
          <section>
            <h2 data-testid="quick-review-name">{current.displayName}</h2>
            <p className="quick-review-facts" data-testid="quick-review-revision">
              {revisionLabel(current, merged)} · {current.kind} · {formatBytes(current.bytes)}
            </p>
            <p className="quick-review-decision" data-testid="quick-review-decision" data-decision={current.reviewDecision}>
              <span className={`decision-chip decision-${current.reviewDecision}`}>{REVIEW_LABELS[current.reviewDecision]}</span>
              {current.reviewDecidedAt && <small>{new Date(current.reviewDecidedAt).toLocaleString()}</small>}
            </p>
            {current.reviewNote && <p className="quick-review-quote">“{current.reviewNote}”</p>}
            {current.reviewAnswersNote && <p className="quick-review-answers"><CornerUpLeft size={13} />Answers a send-back: “{current.reviewAnswersNote}”</p>}
          </section>
          {current.revisionPrompt && <section>
            <h3>This revision</h3>
            <p className="quick-review-text">{current.revisionPrompt}</p>
          </section>}
          <section>
            <h3>Notes</h3>
            <p className="quick-review-text" data-testid="quick-review-notes">{current.notes || 'No notes.'}</p>
          </section>
          {current.tags.length > 0 && <section className="quick-review-tags">{current.tags.map(tag => <em key={tag}>{tag}</em>)}</section>}
          {(current.kind === 'Model' || current.kind === 'Image') && <button type="button" className="secondary compact" data-testid="quick-review-inspect"
            onClick={() => onInspect(current.id)}><ScanSearch size={15} />Full inspection</button>}
        </>}
        <dl className="quick-review-keys">
          <div><dt><kbd>A</kbd></dt><dd>Approve, then next</dd></div>
          <div><dt><kbd>X</kbd></dt><dd>Send back, then next. The feedback box is optional</dd></div>
          <div><dt><kbd>Enter</kbd></dt><dd>In the box: send back with it</dd></div>
          <div><dt><kbd>Esc</kbd></dt><dd>Leave the box; the keys work again</dd></div>
          <div><dt><kbd>←</kbd> <kbd>→</kbd></dt><dd>Previous / next</dd></div>
          <div><dt><kbd>Z</kbd></dt><dd>Undo the last decision</dd></div>
        </dl>
      </aside>
    </div>

    <footer className="quick-review-bar">
      {error && <p className="form-error" role="alert">{error}</p>}
      <div className="quick-review-actions">
        <button type="button" className="secondary" data-testid="quick-review-prev" aria-keyshortcuts="ArrowLeft"
          disabled={busy || queue.length === 0 || (index <= 0 && !finished)} onClick={() => go(-1)}>
          <ArrowLeft size={18} />Previous<kbd>←</kbd>
        </button>
        <div className="quick-review-reject-group">
          <button type="button" className="quick-review-reject" data-testid="quick-review-send-back" aria-keyshortcuts="X"
            disabled={busy || !current || showDone || current.isArchived} onClick={sendBack}>
            <X size={20} />Send back<kbd>X</kbd>
          </button>
          {/* Optional. While it has focus the single keys type into it;
              Enter sends back with it, Shift+Enter is a new line, Esc leaves. */}
          <textarea ref={feedback} className="quick-review-feedback" rows={2} maxLength={1000} value={reason}
            aria-label="Feedback for the send-back (optional)" data-testid="quick-review-feedback"
            placeholder="Feedback (optional) · Enter sends back"
            disabled={busy || !current || showDone || current.isArchived}
            onChange={event => setReason(event.target.value)}
            onKeyDown={event => {
              if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); event.currentTarget.blur(); sendBack() }
              else if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); event.currentTarget.blur() }
            }} />
        </div>
        <button type="button" className="quick-review-approve" data-testid="quick-review-approve" aria-keyshortcuts="A"
          disabled={busy || !current || showDone || current.isArchived} onClick={approve}>
          {busy ? <LoaderCircle className="spin" size={20} /> : <Check size={20} />}Approve<kbd>A</kbd>
        </button>
        <button type="button" className="secondary" data-testid="quick-review-next" aria-keyshortcuts="ArrowRight"
          disabled={busy || queue.length === 0 || index >= queue.length - 1} onClick={() => go(1)}>
          Next<ArrowRight size={18} /><kbd>→</kbd>
        </button>
        <button type="button" className="secondary quick-review-undo" data-testid="quick-review-undo" aria-keyshortcuts="Z"
          disabled={busy || !last} onClick={() => void undoLast()}
          title={last ? `Undo: ${last.made === 'Approved' ? 'approved' : 'sent back'} ${last.displayName}` : 'Nothing to undo'}>
          <Undo2 size={17} />Undo<kbd>Z</kbd>
        </button>
      </div>
      <p className="quick-review-status" role="status" data-testid="quick-review-status">
        {copied ?? status ?? ''}
      </p>
    </footer>
  </main>
}

/** The asset itself: the inspection viewer for a model, the plain player for anything else. */
function ReviewStage({ asset, profiles, kept, look }: {
  asset: AssetSummary
  profiles: Map<string, ModelProfileSummary>
  kept: { current: KeptCamera | undefined }
  look: { current: SurfaceLook }
}) {
  const [profile, setProfile] = useState<ModelProfileSummary | undefined>(() => profiles.get(asset.id))
  const [failure, setFailure] = useState<string>()

  useEffect(() => {
    if (asset.kind !== 'Model' || profiles.has(asset.id)) return
    let live = true
    studioApi.modelProfile(asset.id)
      .then(loaded => { profiles.set(asset.id, loaded); if (live) setProfile(loaded) })
      .catch(reason => { if (live) setFailure(reason instanceof Error ? reason.message : 'This model could not be inspected.') })
    return () => { live = false }
  }, [asset.id, asset.kind, profiles])

  if (asset.kind === 'Image') return <div className="quick-review-media"><img src={asset.contentUrl} alt={asset.displayName} /></div>
  if (asset.kind === 'Video') return <div className="quick-review-media"><video src={asset.contentUrl} controls /></div>
  if (asset.kind === 'Audio') return <div className="quick-review-media"><audio src={asset.contentUrl} controls /></div>
  if (failure) return <p className="asset-error" role="alert">{failure}</p>
  if (!profile) return <div className="asset-loading"><LoaderCircle className="spin" /><span>Measuring the model…</span></div>
  return <Suspense fallback={<div className="asset-loading"><LoaderCircle className="spin" /><span>Opening the 3D view…</span></div>}>
    <ModelViewer
      key={profile.assetId}
      contentUrl={profile.contentUrl}
      label={profile.displayName}
      dimensions={[profile.dimensions[0], profile.dimensions[1], profile.dimensions[2]]}
      supportedClipNames={(profile.clips ?? []).filter(clip => clip.supported).map(clip => clip.name)}
      onError={setFailure}
      stack={asset.revisionFamilyId ?? asset.id}
      kept={kept}
      look={look}
    />
  </Suspense>
}

function formatBytes(bytes: number) {
  return bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`
}
