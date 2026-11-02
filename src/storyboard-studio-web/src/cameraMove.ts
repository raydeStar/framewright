/**
 * Video endpoints from a camera move across one image. H3 interpolates
 * between a Start and a Last Frame; when both are windows of the same picture,
 * the motion between them is a camera move the artist chose (a push-in, a
 * truck, a crane) instead of whatever move the model would invent.
 *
 * Windows are in source pixels and always have the project delivery aspect,
 * so H3's center crop leaves them untouched.
 */
export type CameraMove = 'PushIn' | 'PullBack' | 'TruckLeft' | 'TruckRight' | 'CraneUp' | 'CraneDown'

export interface FrameWindow { x: number; y: number; width: number; height: number }

/** A missing window means that endpoint is the source image itself. */
export interface CameraMovePlan { start?: FrameWindow; end?: FrameWindow }

export const CAMERA_MOVES: readonly { id: CameraMove; label: string; detail: string }[] = [
  { id: 'PushIn', label: 'Push in', detail: 'Ends closer on the point you choose' },
  { id: 'PullBack', label: 'Pull back', detail: 'Starts close and opens to the full frame' },
  { id: 'TruckLeft', label: 'Truck left', detail: 'Slides sideways, same framing' },
  { id: 'TruckRight', label: 'Truck right', detail: 'Slides sideways, same framing' },
  { id: 'CraneUp', label: 'Crane up', detail: 'Rises, same framing' },
  { id: 'CraneDown', label: 'Crane down', detail: 'Lowers, same framing' },
]

const clamp = (value: number, low: number, high: number) => Math.min(Math.max(value, low), Math.max(low, high))

/** The delivery-shaped centre of the source: what H3 sees of an uncropped image. */
export function deliveryWindow(sourceWidth: number, sourceHeight: number, aspect: number): FrameWindow {
  const width = sourceWidth / sourceHeight > aspect ? sourceHeight * aspect : sourceWidth
  const height = sourceWidth / sourceHeight > aspect ? sourceHeight : sourceWidth / aspect
  return { x: Math.round((sourceWidth - width) / 2), y: Math.round((sourceHeight - height) / 2), width: Math.round(width), height: Math.round(height) }
}

/**
 * @param amount how far the camera travels, in percent: a push-in ends that
 *   much closer; a truck or crane crops by that much and uses the margin to move.
 * @param focusX where a push-in lands, or which line a truck or crane follows,
 *   as a fraction of the source width (focusY likewise for height).
 */
export function planCameraMove(sourceWidth: number, sourceHeight: number, aspect: number, move: CameraMove, amount: number, focusX = 0.5, focusY = 0.5): CameraMovePlan {
  const base = sourceWidth / sourceHeight > aspect
    ? { width: sourceHeight * aspect, height: sourceHeight }
    : { width: sourceWidth, height: sourceWidth / aspect }
  const baseX = (sourceWidth - base.width) / 2
  const baseY = (sourceHeight - base.height) / 2
  const zoom = 1 + clamp(amount, 3, 50) / 100
  const width = base.width / zoom
  const height = base.height / zoom
  const at = (x: number, y: number): FrameWindow => ({ x: Math.round(x), y: Math.round(y), width: Math.round(width), height: Math.round(height) })
  // A closer window centred on the focus point, kept inside the source.
  const focused = () => at(clamp(focusX * sourceWidth - width / 2, 0, sourceWidth - width), clamp(focusY * sourceHeight - height / 2, 0, sourceHeight - height))
  // Truck and crane travel across the delivery-shaped part of the source; the
  // other axis follows the focus line.
  const left = baseX, right = baseX + base.width - width
  const top = baseY, bottom = baseY + base.height - height
  const lineY = clamp(focusY * sourceHeight - height / 2, top, bottom)
  const lineX = clamp(focusX * sourceWidth - width / 2, left, right)
  switch (move) {
    case 'PushIn': return { end: focused() }
    case 'PullBack': return { start: focused() }
    case 'TruckRight': return { start: at(left, lineY), end: at(right, lineY) }
    case 'TruckLeft': return { start: at(right, lineY), end: at(left, lineY) }
    case 'CraneUp': return { start: at(lineX, bottom), end: at(lineX, top) }
    case 'CraneDown': return { start: at(lineX, top), end: at(lineX, bottom) }
  }
}

/** How far the camera travels, as an artist reads it. */
export function describeTravel(move: CameraMove, amount: number) {
  if (move === 'PushIn') return `Ends ${amount}% closer`
  if (move === 'PullBack') return `Starts ${amount}% closer`
  return `Travels ${Math.round((1 - 1 / (1 + amount / 100)) * 100)}% of the frame`
}

/** The motion words for the video brief, so the prompt and the endpoints agree. */
export function cameraMoveBrief(move: CameraMove) {
  switch (move) {
    case 'PushIn': return 'Slow, steady push-in toward the subject; no cut, no other camera move.'
    case 'PullBack': return 'Slow, steady pull-back revealing the full frame; no cut, no other camera move.'
    case 'TruckLeft': return 'Slow lateral truck left at constant framing; no zoom, no push-in, no pan.'
    case 'TruckRight': return 'Slow lateral truck right at constant framing; no zoom, no push-in, no pan.'
    case 'CraneUp': return 'Slow crane up at constant framing; no zoom, no tilt.'
    case 'CraneDown': return 'Slow crane down at constant framing; no zoom, no tilt.'
  }
}
