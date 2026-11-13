import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ArrowLeft, ArrowRight, Check, ClipboardCopy, CornerUpLeft, FolderOpen, LoaderCircle, PackageCheck, Play, RefreshCw, Repeat, Square, Undo2, X } from 'lucide-react'
import { studioApi } from '../api'
import { formatSeconds, matchesSfxFilter, nextSfx, openingTake, rejectedSfxList, SFX_LABELS, sfxCounts, sfxQueue, waveformPeaks, type SfxFilter } from '../sfxReview'
import type { AssetReviewDecision, SfxReviewBoard, SfxReviewCounts, SfxSoundSummary, SfxTakeSummary } from '../types'

const FILTERS: { id: SfxFilter; label: string; count: (counts: SfxReviewCounts) => number }[] = [
  { id: 'Pending', label: 'Pending', count: counts => counts.pending },
  { id: 'ChangesRequested', label: 'Rejected', count: counts => counts.changesRequested },
  { id: 'Approved', label: 'Accepted', count: counts => counts.approved },
  { id: 'NotGenerated', label: 'Not generated', count: counts => counts.notGenerated },
  { id: 'all', label: 'All', count: counts => counts.total },
]

interface SfxUndo { name: string; made: AssetReviewDecision; decision: AssetReviewDecision; note: string; take?: string | null }

/**
 * Sound review: one sound at a time, heard as it arrives, decided from the
 * keyboard. A accepts the take that is playing, X rejects the sound, and the
 * next one still waiting plays at once. A sound can have several MOSS takes;
 * the up and down arrows (or 1-9) flip between them.
 *
 * The decisions go to Framewright's own store with the asset review values,
 * so whoever regenerates rejected sounds reads them over the same API.
 */
export default function SfxReviewWorkspace({ onToast }: { onToast: (message: string) => void }) {
  const [board, setBoard] = useState<SfxReviewBoard>()
  const [loading, setLoading] = useState(true)
  const [filter, setFilter] = useState<SfxFilter>('Pending')
  const [kept, setKept] = useState<Set<string>>(new Set())
  const [currentName, setCurrentName] = useState<string>()
  const [chosenTake, setChosenTake] = useState<Record<string, string>>({})
  const [finished, setFinished] = useState(false)
  const [note, setNote] = useState('')
  const [prefilled, setPrefilled] = useState('')
  const [busy, setBusy] = useState(false)
  const busyNow = useRef(false)
  const [error, setError] = useState<string>()
  const [status, setStatus] = useState<string>()
  const [undo, setUndo] = useState<SfxUndo[]>([])
  const [sourcesOpen, setSourcesOpen] = useState(false)
  const noteBox = useRef<HTMLTextAreaElement>(null)
  const player = usePlayer()

  const load = useCallback(async () => {
    const next = await studioApi.sfxReview()
    setBoard(next)
    if (!next.sources.listPath) setSourcesOpen(true)
    return next
  }, [])
  useEffect(() => {
    void load().catch(reason => setError(reason instanceof Error ? reason.message : 'Could not open sound review.')).finally(() => setLoading(false))
  }, [load])

  const sounds = useMemo(() => board?.sounds ?? [], [board])
  const counts = useMemo(() => sfxCounts(sounds), [sounds])
  const queue = useMemo(() => sfxQueue(sounds, filter, kept), [sounds, filter, kept])
  const index = useMemo(() => {
    if (queue.length === 0) return -1
    const at = currentName ? queue.findIndex(sound => sound.name === currentName) : -1
    if (at >= 0) return at
    const first = queue.findIndex(sound => matchesSfxFilter(sound, filter))
    return first >= 0 ? first : 0
  }, [currentName, filter, queue])
  const current = index >= 0 ? queue[index] : undefined
  const take = current ? current.takes.find(item => item.sha256 === chosenTake[current.name]) ?? openingTake(current) : undefined
  const showDone = finished || queue.length === 0

  // The note follows the sound on screen and its saved note; a refresh that
  // changes nothing about the decision leaves what is being typed alone.
  const shownNote = current?.note ?? ''
  useEffect(() => { setNote(shownNote); setPrefilled(shownNote) },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [current?.name, current?.decidedAt])

  // A sound plays as it arrives, and again whenever another take is chosen.
  const takeSha = take?.sha256
  useEffect(() => {
    if (!take || showDone) { player.stop(); return }
    void player.play(take)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [takeSha, showDone])

  const replace = (updated: SfxSoundSummary) => setBoard(current => current
    ? { ...current, sounds: current.sounds.map(sound => sound.name === updated.name ? updated : sound) }
    : current)

  const go = (step: number) => {
    if (busyNow.current || queue.length === 0 || index < 0) return
    const target = finished && step < 0 ? index : Math.max(0, Math.min(queue.length - 1, index + step))
    setError(undefined); setFinished(false); setStatus(undefined)
    setCurrentName(queue[target].name)
  }

  const chooseFilter = (next: SfxFilter) => {
    if (next === filter) return
    setError(undefined); setFinished(false); setStatus(undefined)
    setFilter(next); setKept(new Set()); setCurrentName(undefined)
  }

  const switchTake = (step: number | { number: number }) => {
    if (!current || current.takes.length === 0 || !take) return
    const at = current.takes.findIndex(item => item.sha256 === take.sha256)
    const target = typeof step === 'number' ? Math.max(0, Math.min(current.takes.length - 1, at + step)) : step.number - 1
    const chosen = current.takes[target]
    if (!chosen) return
    if (chosen.sha256 === take.sha256) { void player.play(chosen); return }
    setChosenTake(map => ({ ...map, [current.name]: chosen.sha256 }))
  }

  const advance = (list: SfxSoundSummary[], keptNow: Set<string>, from: string) => {
    const next = nextSfx(sfxQueue(list, filter, keptNow), from, filter)
    if (next) setCurrentName(next)
    else { setCurrentName(from); setFinished(true) }
  }

  const decide = async (decision: AssetReviewDecision, noteText: string | undefined) => {
    if (!current || busyNow.current || finished || !current.generated) return
    const target = current
    const takeSha256 = decision === 'Approved' ? take?.sha256 : undefined
    if (decision === 'Approved' && !takeSha256) return
    // The same decision, note and take again saves nothing; it just moves on.
    if (decision === target.decision && decision === target.storedDecision && (noteText ?? '') === target.note
      && (decision !== 'Approved' || takeSha256 === target.acceptedTakeSha256)) { advance(sounds, kept, target.name); return }
    busyNow.current = true; setBusy(true); setError(undefined)
    try {
      const updated = await studioApi.decideSfx(target.name, decision, noteText, takeSha256)
      const keptNow = new Set(kept).add(target.name)
      replace(updated)
      setKept(keptNow)
      setUndo(stack => [...stack.slice(-99), { name: target.name, made: decision, decision: target.storedDecision, note: target.note, take: target.acceptedTakeSha256 }])
      const chosen = target.takes.findIndex(item => item.sha256 === takeSha256)
      setStatus(decision === 'Approved'
        ? `Accepted take ${chosen + 1} of ${target.name}. Z undoes it.`
        : `Rejected ${target.name}. Z undoes it.`)
      advance(sounds.map(sound => sound.name === updated.name ? updated : sound), keptNow, target.name)
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'That decision could not be saved.')
    } finally { busyNow.current = false; setBusy(false) }
  }

  // Accept carries the note only when something was typed for it; a note the
  // sound arrived with (an old rejection) is not copied onto the acceptance.
  const accept = () => {
    const typed = note.trim()
    void decide('Approved', typed !== prefilled.trim() ? typed || undefined : current?.decision === 'Approved' ? current.note || undefined : undefined)
  }
  const reject = () => { void decide('ChangesRequested', note.trim()) }

  const undoLast = async () => {
    const last = undo[undo.length - 1]
    if (!last || busyNow.current) return
    busyNow.current = true; setBusy(true); setError(undefined)
    try {
      const restored = await studioApi.decideSfx(last.name, last.decision, last.decision === 'Pending' ? undefined : last.note, last.take ?? undefined)
      replace(restored)
      setKept(set => new Set(set).add(restored.name))
      setUndo(stack => stack.slice(0, -1))
      setFinished(false)
      setCurrentName(restored.name)
      setStatus(`Undone. ${restored.name} is back to ${SFX_LABELS[last.decision]}.`)
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'That decision could not be undone.')
    } finally { busyNow.current = false; setBusy(false) }
  }

  const refresh = () => { setError(undefined); void load().catch(reason => setError(reason instanceof Error ? reason.message : 'Could not refresh.')) }

  const copyRejected = async () => {
    const text = rejectedSfxList(sounds)
    if (!text) { setStatus('Nothing is rejected right now.'); return }
    const lines = text.split('\n').length
    try { await navigator.clipboard.writeText(text) } catch {
      const area = document.createElement('textarea')
      area.value = text; area.setAttribute('readonly', ''); area.style.position = 'fixed'; area.style.opacity = '0'
      document.body.appendChild(area); area.select()
      const done = document.execCommand('copy')
      area.remove()
      if (!done) { setStatus('The clipboard refused. Nothing was copied.'); return }
    }
    setStatus(`Copied ${lines} rejected sound${lines === 1 ? '' : 's'}.`)
  }

  const [exporting, setExporting] = useState(false)
  const exportAccepted = async () => {
    setExporting(true); setError(undefined)
    try {
      const result = await studioApi.exportSfx()
      const message = `Exported ${result.written.length} WAV${result.written.length === 1 ? '' : 's'} to ${result.exportPath}`
        + (result.missing.length ? `. Missing on disk: ${result.missing.join(', ')}.` : '.')
      setStatus(message); onToast(message)
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : 'Could not export the accepted sounds.')
    } finally { setExporting(false) }
  }

  // The keys. Listened for ahead of everything else on the page; held keys
  // never repeat a decision, and while the note box has focus they type.
  useEffect(() => {
    const keys = (event: KeyboardEvent) => {
      if (event.ctrlKey || event.metaKey || event.altKey) return
      const target = event.target as HTMLElement | null
      if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)) return
      if (document.querySelector('dialog[open]')) return
      const key = event.key
      const handled = () => { event.preventDefault(); event.stopPropagation() }
      if (key === 'ArrowLeft' || key === 'ArrowRight') { handled(); go(key === 'ArrowLeft' ? -1 : 1); return }
      if (key === 'ArrowUp' || key === 'ArrowDown') { handled(); switchTake(key === 'ArrowUp' ? -1 : 1); return }
      if (key === ' ') { handled(); if (take && !showDone) void player.play(take); return }
      if (event.repeat) return
      if (/^[1-9]$/.test(key)) { handled(); switchTake({ number: Number(key) }); return }
      const lower = key.toLowerCase()
      if (lower === 'a') { handled(); accept() }
      else if (lower === 'x') { handled(); reject() }
      else if (lower === 'n') { handled(); noteBox.current?.focus() }
      else if (lower === 's') { handled(); player.stop() }
      else if (lower === 'z') { handled(); void undoLast() }
    }
    window.addEventListener('keydown', keys, true)
    return () => window.removeEventListener('keydown', keys, true)
  })

  if (loading) return <main className="workspace sfx-review"><div className="asset-loading"><LoaderCircle className="spin" /><span>Opening sound review…</span></div></main>

  const last = undo[undo.length - 1]
  const decided = queue.length - queue.filter(sound => matchesSfxFilter(sound, filter)).length
  const filterLabel = FILTERS.find(item => item.id === filter)?.label.toLowerCase() ?? ''
  const canDecide = Boolean(current && current.generated && !showDone && !busy)

  return <main className="workspace quick-review sfx-review" data-testid="sfx-review">
    <header className="quick-review-header">
      <div className="quick-review-title"><p className="eyebrow">Sound review</p><h1>{showDone ? 'All caught up' : current?.name ?? 'Sounds'}</h1></div>
      <div className="quick-review-filters" role="group" aria-label="Show">
        {FILTERS.map(item => <button key={item.id} type="button" aria-pressed={filter === item.id} className={filter === item.id ? 'is-on' : undefined}
          data-testid={`sfx-filter-${item.id}`} onClick={() => chooseFilter(item.id)}>
          {item.label}<small>{item.count(counts)}</small>
        </button>)}
      </div>
      <div className="quick-review-progress" data-testid="sfx-progress">
        <strong>{queue.length === 0 ? `No ${filterLabel} sounds` : `${index + 1} of ${queue.length} ${filterLabel}`}</strong>
        <small>{counts.approved} accepted · {counts.changesRequested} rejected · {counts.pending} pending · {counts.notGenerated} not generated, of {counts.total}</small>
        <span className="quick-review-meter" aria-hidden="true"><i style={{ width: `${counts.total ? Math.round(((counts.approved + counts.changesRequested) / counts.total) * 100) : 0}%` }} /></span>
      </div>
      <button type="button" className="secondary compact" aria-label="Look for new takes" title="Read the list and the MOSS runs again" onClick={refresh}><RefreshCw size={15} /></button>
      <button type="button" className="secondary compact" aria-expanded={sourcesOpen} onClick={() => setSourcesOpen(open => !open)}><FolderOpen size={15} />Sources</button>
      <button type="button" className="secondary compact" data-testid="sfx-copy-rejected" disabled={counts.changesRequested === 0} onClick={() => void copyRejected()}><ClipboardCopy size={15} />Copy rejected</button>
      <button type="button" className="secondary compact" data-testid="sfx-export" disabled={exporting || counts.approved === 0 || !board?.sources.exportPath}
        title={board?.sources.exportPath ? `Copy each accepted take to ${board.sources.exportPath} as SFX_<Name>.wav` : 'Choose an export folder in Sources first'}
        onClick={() => void exportAccepted()}>
        {exporting ? <LoaderCircle className="spin" size={15} /> : <PackageCheck size={15} />}Export accepted ({counts.approved})
      </button>
    </header>

    <div className="quick-review-layout">
      <section className="quick-review-stage sfx-review-stage" aria-live="polite">
        {sourcesOpen && board && <SourcesForm board={board} onSaved={next => { setBoard(next); setSourcesOpen(false); setCurrentName(undefined); setKept(new Set()) }} />}
        {board?.listError && <p className="asset-error" role="alert">{board.listError}</p>}
        {board && board.manifestErrors.length > 0 && <p className="asset-error" role="alert">Some MOSS runs could not be read: {board.manifestErrors.join('; ')}</p>}
        {showDone
          ? <div className="quick-review-done" data-testid="sfx-done">
              <Check size={30} />
              <h2>{queue.length === 0 ? `No ${filterLabel} sounds` : `No ${filterLabel} sounds left`}</h2>
              <p>{counts.approved} accepted · {counts.changesRequested} rejected · {counts.pending} pending · {counts.notGenerated} not generated.</p>
              <div>
                {counts.changesRequested > 0 && <button type="button" className="secondary" onClick={() => void copyRejected()}><ClipboardCopy size={16} />Copy rejected list</button>}
                {counts.approved > 0 && board?.sources.exportPath && <button type="button" className="primary" onClick={() => void exportAccepted()}><PackageCheck size={16} />Export accepted</button>}
                {queue.length > 0 && <button type="button" className="secondary" onClick={() => go(-1)}><ArrowLeft size={16} />Back to the last one</button>}
              </div>
            </div>
          : current && (!current.generated
              ? <div className="quick-review-done sfx-not-generated" data-testid="sfx-not-generated">
                  <h2>Not generated yet</h2>
                  <p>MOSS has not written a take for {current.name}. Generate it from the list, then refresh.</p>
                </div>
              : take && <div className="sfx-player">
                  <Waveform take={take} player={player} />
                  <div className="sfx-transport">
                    <button type="button" className="secondary" data-testid="sfx-play" aria-keyshortcuts="Space" onClick={() => void player.play(take)}>
                      <Play size={16} />{player.state.playing && player.state.sha === take.sha256 ? 'Replay' : 'Play'}<kbd>Space</kbd>
                    </button>
                    <button type="button" className="secondary" aria-keyshortcuts="S" disabled={!player.state.playing} onClick={player.stop}><Square size={14} />Stop<kbd>S</kbd></button>
                    {take.loop && <span className="sfx-loop"><Repeat size={14} />Loops until stopped</span>}
                    {player.state.blocked && <span className="sfx-blocked">The browser held the sound back. Press Space to hear it.</span>}
                  </div>
                  <div className="sfx-takes" role="group" aria-label="Takes">
                    {current.takes.map((item, position) => <button key={item.sha256} type="button" aria-pressed={item.sha256 === take.sha256}
                      className={item.sha256 === take.sha256 ? 'is-on' : undefined} data-testid="sfx-take"
                      title={`${item.fileName} · run ${item.runId}${item.seed != null ? ` · seed ${item.seed}` : ''}`}
                      onClick={() => setChosenTake(map => ({ ...map, [current.name]: item.sha256 }))}>
                      {/* Numbered by position: MOSS starts each run at 1, so a
                          second run would otherwise show a second "Take 1". */}
                      <strong>{position < 9 ? <kbd>{position + 1}</kbd> : null}Take {position + 1}</strong>
                      <small>{formatSeconds(item.durationSeconds)}{item.createdAt ? ` · ${new Date(item.createdAt).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}` : ''}{item.afterRejection ? ' · new' : ''}{item.sha256 === current.acceptedTakeSha256 ? ' · accepted' : ''}</small>
                    </button>)}
                  </div>
                </div>)}
      </section>

      <aside className="quick-review-side" aria-label="About this sound">
        {current && !showDone && <>
          <section>
            <h2 data-testid="sfx-name">{current.name}</h2>
            <p className="quick-review-facts">{current.category || 'No category'} · {current.cueFileName}{current.onList ? '' : ' · not on the list'}</p>
            <p className="quick-review-decision" data-decision={current.decision}>
              <span className={`decision-chip decision-${current.decision}`}>{SFX_LABELS[current.decision]}</span>
              {current.decidedAt && <small>{new Date(current.decidedAt).toLocaleString()}</small>}
            </p>
            {current.note && current.decision !== 'Pending' && <p className="quick-review-quote">“{current.note}”</p>}
            {current.answersNote && <p className="quick-review-answers"><CornerUpLeft size={13} />New take answers a rejection: “{current.answersNote}”</p>}
          </section>
          <section>
            <h3>Prompt</h3>
            <p className="quick-review-text" data-testid="sfx-prompt">{current.prompt || 'No prompt recorded.'}</p>
          </section>
          <section>
            <h3>Length</h3>
            <p className="quick-review-text">
              {current.seconds != null ? `Asked for ${formatSeconds(current.seconds)}` : 'No length on the list'}
              {take ? ` · this take ${formatSeconds(take.durationSeconds)}` : ''}
              {current.loop ? ' · loop' : ''}
              {current.takes.length > 0 ? ` · ${current.takes.length} take${current.takes.length === 1 ? '' : 's'}` : ''}
            </p>
          </section>
        </>}
        <dl className="quick-review-keys">
          <div><dt><kbd>Space</kbd></dt><dd>Play it again</dd></div>
          <div><dt><kbd>A</kbd></dt><dd>Accept this take, then next</dd></div>
          <div><dt><kbd>X</kbd></dt><dd>Reject, then next</dd></div>
          <div><dt><kbd>N</kbd></dt><dd>Write a note; it saves with A or X</dd></div>
          <div><dt><kbd>↑</kbd> <kbd>↓</kbd> <kbd>1</kbd>–<kbd>9</kbd></dt><dd>Other takes</dd></div>
          <div><dt><kbd>←</kbd> <kbd>→</kbd></dt><dd>Previous / next sound</dd></div>
          <div><dt><kbd>S</kbd> <kbd>Z</kbd></dt><dd>Stop · undo the last decision</dd></div>
        </dl>
      </aside>
    </div>

    <footer className="quick-review-bar">
      {error && <p className="form-error" role="alert">{error}</p>}
      <div className="quick-review-actions">
        <button type="button" className="secondary" aria-keyshortcuts="ArrowLeft" disabled={busy || queue.length === 0 || (index <= 0 && !finished)} onClick={() => go(-1)}>
          <ArrowLeft size={18} />Previous<kbd>←</kbd>
        </button>
        <div className="quick-review-reject-group">
          <button type="button" className="quick-review-reject" data-testid="sfx-reject" aria-keyshortcuts="X" disabled={!canDecide} onClick={reject}>
            <X size={20} />Reject<kbd>X</kbd>
          </button>
          <textarea ref={noteBox} className="quick-review-feedback" rows={2} maxLength={1000} value={note}
            aria-label="Note for this decision (optional)" data-testid="sfx-note"
            placeholder="Note (optional) · N to write · Enter rejects with it"
            disabled={!current || showDone || busy}
            onChange={event => setNote(event.target.value)}
            onKeyDown={event => {
              if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); event.currentTarget.blur(); reject() }
              else if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); event.currentTarget.blur() }
            }} />
        </div>
        <button type="button" className="quick-review-approve" data-testid="sfx-accept" aria-keyshortcuts="A" disabled={!canDecide} onClick={accept}>
          {busy ? <LoaderCircle className="spin" size={20} /> : <Check size={20} />}Accept take{take && current ? ` ${current.takes.indexOf(take) + 1}` : ''}<kbd>A</kbd>
        </button>
        <button type="button" className="secondary" aria-keyshortcuts="ArrowRight" disabled={busy || queue.length === 0 || index >= queue.length - 1} onClick={() => go(1)}>
          Next<ArrowRight size={18} /><kbd>→</kbd>
        </button>
        <button type="button" className="secondary quick-review-undo" aria-keyshortcuts="Z" disabled={busy || !last} onClick={() => void undoLast()}
          title={last ? `Undo: ${SFX_LABELS[last.made].toLowerCase()} ${last.name}` : 'Nothing to undo'}>
          <Undo2 size={17} />Undo<kbd>Z</kbd>
        </button>
      </div>
      <p className="quick-review-status" role="status" data-testid="sfx-status">{status ?? (decided > 0 ? `${decided} decided in this sitting.` : '')}</p>
    </footer>
  </main>
}

/** Where the list, the takes and the export live. */
function SourcesForm({ board, onSaved }: { board: SfxReviewBoard; onSaved: (board: SfxReviewBoard) => void }) {
  const [listPath, setListPath] = useState(board.sources.listPath)
  const [roots, setRoots] = useState(board.sources.takeRoots.join('\n'))
  const [exportPath, setExportPath] = useState(board.sources.exportPath)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  const save = async () => {
    setBusy(true); setError(undefined)
    try {
      onSaved(await studioApi.setSfxReviewSources({
        listPath: listPath.trim(), exportPath: exportPath.trim(),
        takeRoots: roots.split('\n').map(line => line.trim()).filter(Boolean),
      }))
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not save those folders.') } finally { setBusy(false) }
  }
  return <form className="sfx-sources" data-testid="sfx-sources" onSubmit={event => { event.preventDefault(); void save() }}>
    <label>Sound list <small>the MOSS .json list, in priority order</small>
      <input value={listPath} onChange={event => setListPath(event.target.value)} placeholder="C:\…\sfx-list.json" />
    </label>
    <label>Take folders <small>one per line; every sfx-manifest.json under them is read. Empty means the list's own folder.</small>
      <textarea rows={2} value={roots} onChange={event => setRoots(event.target.value)} />
    </label>
    <label>Export folder <small>accepted takes are copied here as SFX_&lt;Name&gt;.wav; never inside the Unreal project</small>
      <input value={exportPath} onChange={event => setExportPath(event.target.value)} placeholder="C:\…\audio\approved" />
    </label>
    {error && <p className="form-error" role="alert">{error}</p>}
    <div><button type="submit" className="primary compact" disabled={busy}>{busy ? 'Saving…' : 'Save sources'}</button></div>
  </form>
}

/** The take's outline, with a playhead while it plays. */
function Waveform({ take, player }: { take: SfxTakeSummary; player: Player }) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const head = useRef<HTMLDivElement>(null)
  const [peaks, setPeaks] = useState<number[]>()
  const [failure, setFailure] = useState<string>()

  useEffect(() => {
    let live = true
    setPeaks(undefined); setFailure(undefined)
    player.buffer(take)
      .then(buffer => { if (live) setPeaks(waveformPeaks(buffer.getChannelData(0), 480)) })
      .catch(reason => { if (live) setFailure(reason instanceof Error ? reason.message : 'This take could not be read.') })
    return () => { live = false }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [take.sha256])

  useEffect(() => {
    const element = canvas.current
    if (!element || !peaks) return
    const draw = () => {
      const ratio = window.devicePixelRatio || 1
      const width = element.clientWidth, height = element.clientHeight
      element.width = Math.max(1, Math.round(width * ratio)); element.height = Math.max(1, Math.round(height * ratio))
      const context = element.getContext('2d')
      if (!context) return
      context.setTransform(ratio, 0, 0, ratio, 0, 0)
      context.clearRect(0, 0, width, height)
      context.fillStyle = getComputedStyle(element).getPropertyValue('--accent').trim() || '#6fb2c9'
      const bar = width / peaks.length
      peaks.forEach((peak, index) => {
        const tall = Math.max(1, peak * (height * 0.92))
        context.fillRect(index * bar, (height - tall) / 2, Math.max(1, bar - 0.5), tall)
      })
    }
    draw()
    const observer = new ResizeObserver(draw)
    observer.observe(element)
    return () => observer.disconnect()
  }, [peaks])

  useEffect(() => {
    let frame = 0
    const tick = () => {
      const progress = player.progress(take.sha256)
      if (head.current) {
        head.current.style.display = progress == null ? 'none' : 'block'
        if (progress != null) head.current.style.left = `${progress * 100}%`
      }
      frame = requestAnimationFrame(tick)
    }
    frame = requestAnimationFrame(tick)
    return () => cancelAnimationFrame(frame)
  }, [player, take.sha256])

  return <div className="sfx-waveform" data-testid="sfx-waveform" data-ready={peaks ? 'true' : 'false'}>
    <canvas ref={canvas} aria-label={`Waveform of ${take.fileName}`} />
    <div ref={head} className="sfx-playhead" />
    {!peaks && !failure && <span className="sfx-waveform-note"><LoaderCircle className="spin" size={16} />Reading the take…</span>}
    {failure && <span className="sfx-waveform-note">{failure}</span>}
  </div>
}

interface Player {
  play: (take: SfxTakeSummary) => Promise<void>
  stop: () => void
  buffer: (take: SfxTakeSummary) => Promise<AudioBuffer>
  progress: (sha256: string) => number | null
  state: { sha?: string; playing: boolean; blocked: boolean }
}

/**
 * One Web Audio player for the page. A take is fetched and decoded once, then
 * kept, so flipping between takes and back is instant; only one sound plays
 * at a time, and a later request always wins over an earlier one still loading.
 */
function usePlayer(): Player {
  const context = useRef<AudioContext | undefined>(undefined)
  const buffers = useRef(new Map<string, Promise<AudioBuffer>>())
  const playing = useRef<{ source: AudioBufferSourceNode; sha: string; started: number; duration: number; loop: boolean } | undefined>(undefined)
  const ticket = useRef(0)
  const [state, setState] = useState<Player['state']>({ playing: false, blocked: false })

  const audio = useCallback(() => (context.current ??= new AudioContext()), [])
  const buffer = useCallback((take: SfxTakeSummary) => {
    let pending = buffers.current.get(take.sha256)
    if (!pending) {
      pending = fetch(take.audioUrl)
        .then(response => { if (!response.ok) throw new Error(`The take could not be loaded (${response.status}).`); return response.arrayBuffer() })
        .then(bytes => audio().decodeAudioData(bytes))
      pending.catch(() => buffers.current.delete(take.sha256))
      buffers.current.set(take.sha256, pending)
    }
    return pending
  }, [audio])
  const halt = useCallback(() => {
    const current = playing.current
    playing.current = undefined
    if (current) { current.source.onended = null; try { current.source.stop() } catch { /* already ended */ } }
  }, [])
  const stop = useCallback(() => { ticket.current++; halt(); setState(value => ({ ...value, playing: false })) }, [halt])
  const play = useCallback(async (take: SfxTakeSummary) => {
    const mine = ++ticket.current
    halt()
    const output = audio()
    if (output.state === 'suspended') void output.resume().catch(() => undefined)
    let decoded: AudioBuffer
    try { decoded = await buffer(take) } catch { if (mine === ticket.current) setState({ sha: take.sha256, playing: false, blocked: false }); return }
    if (mine !== ticket.current) return
    // A browser that has not seen a key or click yet keeps resume() waiting
    // for one, so the wait is bounded and the page asks for Space instead.
    if (output.state !== 'running')
      await Promise.race([output.resume().catch(() => undefined), new Promise(resolve => window.setTimeout(resolve, 300))])
    if (mine !== ticket.current) return
    if (output.state !== 'running') { setState({ sha: take.sha256, playing: false, blocked: true }); return }
    const source = output.createBufferSource()
    source.buffer = decoded
    source.loop = take.loop
    source.connect(output.destination)
    source.onended = () => { if (playing.current?.source === source) { playing.current = undefined; setState(value => ({ ...value, playing: false })) } }
    source.start()
    playing.current = { source, sha: take.sha256, started: output.currentTime, duration: decoded.duration, loop: take.loop }
    setState({ sha: take.sha256, playing: true, blocked: false })
  }, [audio, buffer, halt])
  const progress = useCallback((sha256: string) => {
    const current = playing.current
    if (!current || current.sha !== sha256 || !context.current || current.duration <= 0) return null
    const elapsed = context.current.currentTime - current.started
    return current.loop ? (elapsed % current.duration) / current.duration : Math.min(1, elapsed / current.duration)
  }, [])
  useEffect(() => () => { halt(); void context.current?.close() }, [halt])
  return useMemo(() => ({ play, stop, buffer, progress, state }), [play, stop, buffer, progress, state])
}
