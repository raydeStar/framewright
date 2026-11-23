/**
 * Whether the inspection camera stays where a reviewer left it when the model
 * on screen changes.
 *
 * Flipping between revisions of one stack is how two surfaces are compared up
 * close, and a camera that jumped back to its opening view on every flip would
 * make the reviewer find the same spot again each time. So a camera left on
 * one revision is handed to the next revision of the same stack.
 *
 * It is not handed to a different asset, and not to a revision of a very
 * different size: a camera placed for a one-metre prop looks at nothing useful
 * on a four-metre one, so that model is framed afresh instead.
 */

/** The inspection camera as it was left, and what it was looking at. */
export interface KeptCamera {
  /** The revision stack the model belonged to. */
  stack: string
  /** The longest side of that model's bounds, in scene units. */
  size: number
  yaw: number
  pitch: number
  distance: number
  target: [number, number, number]
}

/**
 * How close the camera may come to the point it looks at, in metres.
 *
 * A part bound for a headset is seen from thirty to fifty centimetres, and a
 * surface that holds up at arm's length can fall apart there. The limit used
 * to be a share of the model's size -- 0.4 of its longest side, 80 cm on a
 * two-metre part -- which kept the reviewer further away the larger the part
 * was. A distance a person stands at does not grow with the part, so the limit
 * is a distance.
 */
export const CLOSE_INSPECTION = 0.25

/** One step of the wheel or of a key towards the model, and one step away. */
export const ZOOM_IN = 0.89
export const ZOOM_OUT = 1.12

/** The nearest and farthest the camera may sit from the point it looks at. */
export interface ZoomLimits {
  closest: number
  farthest: number
}

/**
 * How near and how far the camera may go, for a model whose longest side is
 * `span` and a camera whose near plane is at `nearPlane`, all in metres.
 *
 * The nearest is `CLOSE_INSPECTION`, or the old 0.4 of the span where that is
 * nearer still: a ten-centimetre fitting was already inspected from four, and
 * holding it off at twenty-five would have taken that away. Nothing comes
 * nearer than twice the near plane, where the surface being looked at would
 * start to be cut away. The farthest is forty spans, as it always was.
 */
export function zoomLimits(span: number, nearPlane: number): ZoomLimits {
  const farthest = span * 40
  const closest = Math.max(Math.min(CLOSE_INSPECTION, span * 0.4), nearPlane * 2)
  // A model too small for both keeps a usable range rather than an inverted one.
  return { closest: Math.min(closest, farthest), farthest }
}

/**
 * The distance after a zoom by `factor`, stopped at the limits.
 *
 * A camera can already be past a limit: one kept from a slightly smaller
 * revision, or the opening framing of a part a few millimetres long. Zooming
 * never corrects that by moving the other way. A step in never takes the
 * camera further off, and a step out never brings it nearer.
 */
export function zoomed(distance: number, factor: number, limits: ZoomLimits): number {
  const asked = distance * factor
  return factor < 1
    ? Math.max(asked, Math.min(distance, limits.closest))
    : Math.min(asked, Math.max(distance, limits.farthest))
}

/**
 * The zoom a pinch asks for: fingers that move apart bring the camera in by
 * the same ratio. A gesture with nothing to measure -- two fingers on one
 * point -- asks for no change rather than for a division by zero.
 */
export function pinchZoom(separationBefore: number, separationNow: number): number {
  return separationBefore > 0 && separationNow > 0 ? separationBefore / separationNow : 1
}

/** How far apart two models' longest sides may be before a kept camera no longer suits. */
export const SIZE_TOLERANCE = 0.25

/**
 * True when a kept camera should be used for the model now opening. Without a
 * size the model has not loaded yet and only the stack can be compared; the
 * question is asked again, with the size, once it has.
 *
 * The sizes are compared the same way in both directions, so going from v1 to
 * v2 and back again never keeps the camera one way and resets it the other.
 */
export function keepsCamera(kept: KeptCamera | undefined, stack: string | undefined, size?: number): kept is KeptCamera {
  if (!kept || !stack || kept.stack !== stack) return false
  if (size === undefined) return true
  const [smaller, larger] = kept.size < size ? [kept.size, size] : [size, kept.size]
  return smaller > 0 && larger <= smaller * (1 + SIZE_TOLERANCE)
}
