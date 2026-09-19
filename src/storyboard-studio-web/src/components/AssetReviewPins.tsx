import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import type { KeyboardEvent as ReactKeyboardEvent, PointerEvent as ReactPointerEvent } from 'react'
import { Check, LoaderCircle, MessageCirclePlus, X } from 'lucide-react'
import { studioApi } from '../api'
import type { AssetReviewNoteSummary } from '../types'

interface AssetReviewPinsProps {
  assetId?: string
  assetLabel: string
  onNotesChanged?: (notes: AssetReviewNoteSummary[]) => void
  onError?: (message: string) => void
}

export default function AssetReviewPins({ assetId, assetLabel, onNotesChanged, onError }: AssetReviewPinsProps) {
  const [notes, setNotes] = useState<AssetReviewNoteSummary[]>([])
  const [placing, setPlacing] = useState(false)
  const [pending, setPending] = useState<{ x: number; y: number }>()
  const [text, setText] = useState('')
  const [activeId, setActiveId] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [imageBounds, setImageBounds] = useState<{ left: number; top: number; width: number; height: number }>()
  const layer = useRef<HTMLDivElement>(null)
  const drag = useRef<{ id: string; pointerId: number; moved: boolean } | undefined>(undefined)
  const suppressClick = useRef(false)

  const publish = (next: AssetReviewNoteSummary[]) => {
    setNotes(next)
    onNotesChanged?.(next)
  }

  useEffect(() => {
    let current = true
    setPlacing(false); setPending(undefined); setText(''); setActiveId(undefined)
    if (!assetId) { publish([]); return () => { current = false } }
    studioApi.assetReviewNotes(assetId)
      .then(items => { if (current) publish(items) })
      .catch(reason => { if (current) onError?.(reason instanceof Error ? reason.message : 'Could not load image review notes.') })
    return () => { current = false }
  // Callbacks deliberately do not restart a request when a parent renders.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [assetId])

  useLayoutEffect(() => {
    const overlay = layer.current
    const canvas = overlay?.parentElement
    const image = canvas?.querySelector(':scope > img') as HTMLImageElement | null
    if (!overlay || !canvas || !image) { setImageBounds(undefined); return }

    const measure = () => {
      const canvasRect = canvas.getBoundingClientRect()
      const imageRect = image.getBoundingClientRect()
      if (imageRect.width <= 0 || imageRect.height <= 0 || image.naturalWidth <= 0 || image.naturalHeight <= 0) return

      const sourceAspect = image.naturalWidth / image.naturalHeight
      const boxAspect = imageRect.width / imageRect.height
      const width = boxAspect > sourceAspect ? imageRect.height * sourceAspect : imageRect.width
      const height = boxAspect > sourceAspect ? imageRect.height : imageRect.width / sourceAspect
      setImageBounds({
        left: imageRect.left - canvasRect.left + (imageRect.width - width) / 2,
        top: imageRect.top - canvasRect.top + (imageRect.height - height) / 2,
        width,
        height,
      })
    }

    const observer = new ResizeObserver(measure)
    observer.observe(canvas); observer.observe(image)
    image.addEventListener('load', measure)
    measure()
    return () => { observer.disconnect(); image.removeEventListener('load', measure) }
  }, [assetId])

  useEffect(() => {
    if (!activeId) return
    const collapse = (event: globalThis.KeyboardEvent) => { if (event.key === 'Escape') setActiveId(undefined) }
    window.addEventListener('keydown', collapse)
    return () => window.removeEventListener('keydown', collapse)
  }, [activeId])

  const openNotes = notes.filter(note => note.state === 'Open')

  const place = (event: ReactPointerEvent<HTMLDivElement>) => {
    if (!placing || !assetId || !layer.current) return
    if ((event.target as HTMLElement).closest('button, textarea, form')) return
    const rect = layer.current.getBoundingClientRect()
    setPending({
      x: Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width)),
      y: Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height)),
    })
    setText('')
  }

  const save = async () => {
    if (!assetId || !pending || !text.trim()) return
    setBusy(true)
    try {
      const created = await studioApi.addAssetReviewNote(assetId, { ...pending, body: text.trim() })
      publish([...notes, created]); setPending(undefined); setText(''); setPlacing(false); setActiveId(created.id)
    } catch (reason) { onError?.(reason instanceof Error ? reason.message : 'Could not add that image note.') }
    finally { setBusy(false) }
  }

  const replace = (updated: AssetReviewNoteSummary) => publish(notes.map(note => note.id === updated.id ? updated : note))

  const beginMove = (event: ReactPointerEvent<HTMLButtonElement>, note: AssetReviewNoteSummary) => {
    event.stopPropagation(); event.currentTarget.setPointerCapture(event.pointerId)
    drag.current = { id: note.id, pointerId: event.pointerId, moved: false }
  }

  const move = (event: ReactPointerEvent<HTMLButtonElement>, note: AssetReviewNoteSummary) => {
    if (!drag.current || drag.current.id !== note.id || drag.current.pointerId !== event.pointerId || !layer.current) return
    const rect = layer.current.getBoundingClientRect()
    const x = Math.max(0, Math.min(1, (event.clientX - rect.left) / rect.width))
    const y = Math.max(0, Math.min(1, (event.clientY - rect.top) / rect.height))
    if (Math.abs(x - note.x) + Math.abs(y - note.y) > .004) drag.current.moved = true
    setNotes(current => current.map(item => item.id === note.id ? { ...item, x, y } : item))
  }

  const endMove = async (event: ReactPointerEvent<HTMLButtonElement>, note: AssetReviewNoteSummary) => {
    if (!drag.current || drag.current.id !== note.id || drag.current.pointerId !== event.pointerId) return
    const moved = drag.current.moved
    drag.current = undefined
    if (!moved) return
    suppressClick.current = true
    const positioned = notes.find(item => item.id === note.id) ?? note
    try { replace(await studioApi.moveAssetReviewNote(note.id, positioned.x, positioned.y)) }
    catch (reason) { replace(note); onError?.(reason instanceof Error ? reason.message : 'Could not move that image note.') }
  }

  const nudge = async (event: ReactKeyboardEvent<HTMLButtonElement>, note: AssetReviewNoteSummary) => {
    const delta = event.shiftKey ? .025 : .01
    const offset = event.key === 'ArrowLeft' ? [-delta, 0] : event.key === 'ArrowRight' ? [delta, 0] : event.key === 'ArrowUp' ? [0, -delta] : event.key === 'ArrowDown' ? [0, delta] : undefined
    if (!offset) return
    event.preventDefault(); event.stopPropagation()
    try { replace(await studioApi.moveAssetReviewNote(note.id, Math.max(0, Math.min(1, note.x + offset[0])), Math.max(0, Math.min(1, note.y + offset[1])))) }
    catch (reason) { onError?.(reason instanceof Error ? reason.message : 'Could not move that image note.') }
  }

  const resolve = async (note: AssetReviewNoteSummary) => {
    setBusy(true)
    try { replace(await studioApi.resolveAssetReviewNote(note.id)); setActiveId(undefined) }
    catch (reason) { onError?.(reason instanceof Error ? reason.message : 'Could not resolve that image note.') }
    finally { setBusy(false) }
  }

  return <div ref={layer} className={`asset-review-layer ${placing ? 'placing' : ''}`} style={imageBounds ? { ...imageBounds, right: 'auto', bottom: 'auto' } : undefined} onPointerDown={event => { if (event.target === event.currentTarget && !placing) setActiveId(undefined); place(event) }} aria-label={`${assetLabel} review layer`}>
    <div className="asset-review-toolbar">
      <button type="button" className={placing ? 'active' : ''} disabled={!assetId || busy} aria-pressed={placing} onPointerDown={event => event.stopPropagation()} onClick={() => { setPlacing(value => !value); setPending(undefined); setText('') }}>
        {placing ? <X size={15} /> : <MessageCirclePlus size={15} />}{placing ? 'Cancel note' : 'Add note'}
      </button>
      {openNotes.length > 0 && <span>{openNotes.length} open</span>}
    </div>
    {placing && !pending && <p className="asset-review-hint" role="status">Tap the exact place that needs work.</p>}
    {openNotes.map((note, index) => <div key={note.id} className="asset-review-anchor" style={{ left: `${note.x * 100}%`, top: `${note.y * 100}%` }}>
      <button type="button" className="comment-pin asset-review-pin" aria-label={`Image note ${index + 1}: ${note.body}. Drag to reposition; use arrow keys for fine adjustment.`} aria-expanded={activeId === note.id}
        onPointerDown={event => beginMove(event, note)} onPointerMove={event => move(event, note)} onPointerUp={event => void endMove(event, note)} onPointerCancel={event => void endMove(event, note)} onKeyDown={event => void nudge(event, note)}
        onClick={event => { event.stopPropagation(); if (suppressClick.current) { suppressClick.current = false; return } setActiveId(current => current === note.id ? undefined : note.id) }}><span>{index + 1}</span></button>
      {activeId === note.id && <div className="asset-review-popover" role="dialog" aria-label={`Revision note ${index + 1}`} onPointerDown={event => event.stopPropagation()}><div className="asset-review-popover-header"><strong>Revision note {index + 1}</strong><button type="button" aria-label={`Collapse revision note ${index + 1}`} onClick={() => setActiveId(undefined)}><X size={14} /></button></div><p>{note.body}</p><small>{Math.round(note.x * 100)}% across · {Math.round(note.y * 100)}% down</small><button type="button" disabled={busy} onClick={() => void resolve(note)}><Check size={14} />Mark resolved</button></div>}
    </div>)}
    {pending && <form className={`asset-review-composer ${pending.x > .62 ? 'opens-left' : ''}`} style={{ left: `${pending.x * 100}%`, top: `${pending.y * 100}%` }} onPointerDown={event => event.stopPropagation()} onSubmit={event => { event.preventDefault(); void save() }}>
      <strong>What went wrong here?</strong>
      <textarea autoFocus maxLength={2000} value={text} onChange={event => setText(event.target.value)} aria-label="Image revision note" placeholder="e.g. The eyebrow scar moved to the wrong side." />
      <div><button type="button" onClick={() => { setPending(undefined); setText('') }}>Cancel</button><button type="submit" className="primary" disabled={busy || !text.trim()}>{busy ? <LoaderCircle className="spin" size={14} /> : <MessageCirclePlus size={14} />}Save note</button></div>
    </form>}
  </div>
}
