import { useEffect, useState } from 'react'
import { Check, Crosshair, LoaderCircle, MessageCirclePlus } from 'lucide-react'
import { studioApi } from '../api'
import { formatMoment, noteAnchorLabel, orderNotes } from '../assetReview'
import type { AssetReviewNoteSummary } from '../types'

/** Where a new note would attach: a moment, a camera view, or nothing. */
export type NoteAnchor = { timeSeconds?: number; viewYaw?: number; viewPitch?: number }

/**
 * Review notes on an asset that is not a still image: a model, a video take,
 * a piece of audio. A note can be about the whole thing, or -- when the panel
 * is given a way to capture one -- attached to the moment that was playing or
 * the camera the model was being looked at from. Clicking an attached note
 * goes back there.
 */
export default function AssetNotesPanel({ assetId, capture, attachLabel, onFocus, duration }: {
  assetId: string
  /** Reads where a note would attach right now. */
  capture?: () => NoteAnchor | undefined
  /** What attaching means here, e.g. "Attach to this moment". */
  attachLabel?: string
  /** Goes back to where a note was attached. */
  onFocus?: (note: AssetReviewNoteSummary) => void
  /** Total length in seconds, for the markers along the timeline. */
  duration?: number
}) {
  const [notes, setNotes] = useState<AssetReviewNoteSummary[]>([])
  const [text, setText] = useState('')
  const [attach, setAttach] = useState(true)
  const [anchor, setAnchor] = useState<NoteAnchor>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  useEffect(() => {
    let live = true
    setNotes([]); setText(''); setAnchor(undefined); setError(undefined)
    studioApi.assetReviewNotes(assetId)
      .then(found => { if (live) setNotes(found) })
      .catch(failure => { if (live) setError(failure instanceof Error ? failure.message : 'Could not load review notes.') })
    return () => { live = false }
  }, [assetId])

  // The anchor is taken when the artist starts writing, not when they press
  // save: a take keeps playing and a camera keeps moving while they type.
  const remember = () => { if (capture && text.trim() === '') setAnchor(capture()) }
  const recapture = () => { if (capture) setAnchor(capture()) }

  const save = async () => {
    if (!text.trim()) return
    setBusy(true); setError(undefined)
    try {
      const attached = attach && capture ? anchor ?? capture() : undefined
      const created = await studioApi.addAssetReviewNote(assetId, { body: text.trim(), ...(attached ?? {}) })
      setNotes(current => [...current, created]); setText(''); setAnchor(undefined)
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Could not add that note.') }
    finally { setBusy(false) }
  }

  const resolve = async (note: AssetReviewNoteSummary) => {
    setBusy(true); setError(undefined)
    try {
      const resolved = await studioApi.resolveAssetReviewNote(note.id)
      setNotes(current => current.map(item => item.id === resolved.id ? resolved : item))
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Could not resolve that note.') }
    finally { setBusy(false) }
  }

  const ordered = orderNotes(notes)
  const open = notes.filter(note => note.state === 'Open')
  const timed = open.filter(note => note.timeSeconds != null)
  const anchorText = anchor?.timeSeconds != null
    ? `at ${formatMoment(anchor.timeSeconds)}`
    : anchor?.viewYaw != null ? `yaw ${anchor.viewYaw.toFixed(2)} · pitch ${(anchor.viewPitch ?? 0).toFixed(2)}` : undefined

  return <section className="asset-notes" data-testid="asset-notes">
    <h3>Review notes{open.length > 0 && <small> · {open.length} open</small>}</h3>
    {duration !== undefined && duration > 0 && timed.length > 0 && <div className="asset-notes-timeline" data-testid="asset-notes-timeline" aria-label="Notes along the timeline">
      {timed.map(note => <button key={note.id} type="button" className="asset-notes-marker"
        style={{ left: `${Math.min(100, (note.timeSeconds! / duration) * 100)}%` }}
        aria-label={`Note at ${formatMoment(note.timeSeconds!)}: ${note.body}`} title={note.body}
        onClick={() => onFocus?.(note)} />)}
    </div>}
    {ordered.length === 0
      ? <p className="model-note">No notes yet.</p>
      : <ul className="asset-notes-list">{ordered.map(note => <li key={note.id} data-testid="asset-note" data-state={note.state} data-anchor={note.anchor}>
          {note.anchor !== 'None' && onFocus
            ? <button type="button" className="asset-note-anchor" onClick={() => onFocus(note)}><Crosshair size={12} />{noteAnchorLabel(note)}</button>
            : <small className="asset-note-anchor">{noteAnchorLabel(note)}</small>}
          <p>{note.body}</p>
          {note.state === 'Open'
            ? <button type="button" className="secondary compact" disabled={busy} onClick={() => void resolve(note)}><Check size={13} />Resolve</button>
            : <small>Resolved</small>}
        </li>)}</ul>}
    <form className="asset-notes-composer" onSubmit={event => { event.preventDefault(); void save() }}>
      <textarea rows={2} maxLength={2000} value={text} data-testid="asset-note-text" aria-label="New review note"
        placeholder="What needs work?" onFocus={remember} onChange={event => setText(event.target.value)} />
      {capture && <div className="asset-notes-attach">
        <label><input type="checkbox" checked={attach} data-testid="asset-note-attach"
          onChange={event => setAttach(event.target.checked)} />{attachLabel ?? 'Attach'}{attach && anchorText && <em> {anchorText}</em>}</label>
        {attach && <button type="button" className="secondary compact" onClick={recapture}>Use current</button>}
      </div>}
      <button type="submit" className="secondary compact" data-testid="asset-note-add" disabled={busy || !text.trim()}>
        {busy ? <LoaderCircle className="spin" size={14} /> : <MessageCirclePlus size={14} />}Add note
      </button>
    </form>
    {error && <p className="form-error" role="alert">{error}</p>}
  </section>
}
