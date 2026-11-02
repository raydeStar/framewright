import { useEffect, useMemo, useState } from 'react'
import { ArrowRight, Check, LoaderCircle, LockKeyhole } from 'lucide-react'
import { studioApi } from '../api'
import { canvasToPngBlob } from '../canvasExport'
import { CAMERA_MOVES, cameraMoveBrief, deliveryWindow, describeTravel, planCameraMove, type CameraMove, type FrameWindow } from '../cameraMove'
import type { CandidateVersionSummary, ShotSummary } from '../types'

/**
 * Start and Last Frame as two windows of one image, so H3 performs the camera
 * move the artist chose. The browser crops the windows at the source's own
 * resolution and uploads them; the server binds both endpoints at once.
 * Nothing is generated and the live frame does not change.
 */
export default function CameraMoveEndpoints({ shot, source, aspect, onCancel, onCreated }: {
  shot: ShotSummary
  source: CandidateVersionSummary
  aspect: number
  onCancel: () => void
  onCreated: (saved: ShotSummary, brief: string) => Promise<void> | void
}) {
  const [move, setMove] = useState<CameraMove>('PushIn')
  const [amount, setAmount] = useState(15)
  const [focus, setFocus] = useState({ x: .5, y: .5 })
  const [image, setImage] = useState<HTMLImageElement>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()
  useEffect(() => {
    if (!source.assetUrl) return
    const next = new Image()
    next.onload = () => setImage(next)
    next.onerror = () => setError('The source image could not be loaded.')
    next.src = source.assetUrl
  }, [source.assetUrl])
  const width = image?.naturalWidth ?? 0
  const height = image?.naturalHeight ?? 0
  const plan = useMemo(() => width && height ? planCameraMove(width, height, aspect, move, amount, focus.x, focus.y) : undefined, [width, height, aspect, move, amount, focus.x, focus.y])
  const whole = width && height ? deliveryWindow(width, height, aspect) : undefined
  const startWindow = plan?.start ?? whole
  const endWindow = plan?.end ?? whole
  const aims = move === 'PushIn' || move === 'PullBack'

  const aimAt = (clientX: number, clientY: number, bounds: DOMRect) =>
    setFocus({ x: Math.min(1, Math.max(0, (clientX - bounds.left) / bounds.width)), y: Math.min(1, Math.max(0, (clientY - bounds.top) / bounds.height)) })
  const nudge = (event: React.KeyboardEvent) => {
    const step = ({ ArrowLeft: [-.05, 0], ArrowRight: [.05, 0], ArrowUp: [0, -.05], ArrowDown: [0, .05] } as Record<string, [number, number]>)[event.key]
    if (!step) return
    event.preventDefault()
    setFocus(current => ({ x: Math.min(1, Math.max(0, current.x + step[0])), y: Math.min(1, Math.max(0, current.y + step[1])) }))
  }

  const create = async () => {
    if (!image || !plan) return
    setBusy(true); setError(undefined)
    try {
      const crop = async (window: FrameWindow, role: string) => {
        const canvas = document.createElement('canvas')
        canvas.width = window.width; canvas.height = window.height
        const context = canvas.getContext('2d')
        if (!context) throw new Error('This browser could not prepare the camera window.')
        context.drawImage(image, window.x, window.y, window.width, window.height, 0, 0, window.width, window.height)
        const blob = await canvasToPngBlob(canvas, 'The browser could not export the camera window.')
        return (await studioApi.uploadImage(blob, `${shot.code}-${move}-${role}.png`)).id
      }
      const startAssetId = plan.start ? await crop(plan.start, 'start') : undefined
      const endAssetId = plan.end ? await crop(plan.end, 'last') : undefined
      const saved = await studioApi.createCameraMoveEndpoints(shot.id, { expectedUpdatedAt: shot.updatedAt, sourceCandidateId: source.id, move, amount, startAssetId, endAssetId })
      await onCreated(saved, cameraMoveBrief(move))
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not set the camera move.') }
    finally { setBusy(false) }
  }

  return <>
    <div className="route-modal-body camera-move-body">
      <div className="camera-move-picker" role="radiogroup" aria-label="Camera move">
        {CAMERA_MOVES.map(item => <button type="button" role="radio" key={item.id} aria-checked={move === item.id} className={move === item.id ? 'selected' : ''} onClick={() => setMove(item.id)}><strong>{item.label}</strong><small>{item.detail}</small></button>)}
      </div>
      {/* Keeps the source's own shape inside the height budget, so windows and clicks map without distortion. */}
      <div className="camera-move-stage" style={{ aspectRatio: width && height ? `${width} / ${height}` : '16 / 9', width: width && height ? `min(100%, ${Math.round(340 * width / height)}px)` : undefined }}
        role="group" tabIndex={0} aria-label={aims ? 'Click or use the arrow keys to choose where the move lands' : 'Click or use the arrow keys to choose the line the camera follows'}
        onClick={event => aimAt(event.clientX, event.clientY, event.currentTarget.getBoundingClientRect())} onKeyDown={nudge} data-testid="camera-move-stage">
        {source.assetUrl && <img src={source.assetUrl} alt={`Start source v${source.version}`} draggable={false} />}
        {startWindow && endWindow && <svg viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" aria-hidden="true">
          <defs><marker id="camera-move-arrow" viewBox="0 0 10 10" refX="8" refY="5" markerWidth="5" markerHeight="5" orient="auto-start-reverse"><path d="M0 0 L10 5 L0 10 z" /></marker></defs>
          <rect className="camera-window start" x={startWindow.x} y={startWindow.y} width={startWindow.width} height={startWindow.height} />
          <rect className="camera-window end" x={endWindow.x} y={endWindow.y} width={endWindow.width} height={endWindow.height} />
          <line className="camera-path" x1={startWindow.x + startWindow.width / 2} y1={startWindow.y + startWindow.height / 2} x2={endWindow.x + endWindow.width / 2} y2={endWindow.y + endWindow.height / 2} markerEnd="url(#camera-move-arrow)" />
        </svg>}
        <span className="camera-legend"><i className="start" />Start<i className="end" />Last</span>
        {!image && !error && <span className="camera-loading"><LoaderCircle className="spin" size={16} />Loading the source</span>}
      </div>
      <label className="camera-move-amount"><span>How far<em>{describeTravel(move, amount)}</em></span><input type="range" min={5} max={35} step={1} value={amount} onChange={event => setAmount(Number(event.target.value))} aria-label="How far the camera moves" /></label>
      <p className="feedback-one-click-note"><LockKeyhole size={14} /><span>No image is generated. Framewright crops {plan?.start && plan?.end ? 'two windows' : 'one window'} from v{source.version} at its own resolution, adds {plan?.start && plan?.end ? 'them' : 'it'} beside the live frame, and sets both video endpoints. Describe the same move in the video brief: “{cameraMoveBrief(move)}”</span></p>
      {error && <p className="form-error" role="alert">{error}</p>}
    </div>
    <footer><button className="secondary" onClick={onCancel}>Cancel</button><button className="primary" disabled={busy || !plan} onClick={() => void create()}>{busy ? <><LoaderCircle className="spin" size={16} />Setting endpoints…</> : <><Check size={16} />Set {CAMERA_MOVES.find(item => item.id === move)?.label.toLowerCase()} endpoints<ArrowRight size={16} /></>}</button></footer>
  </>
}
