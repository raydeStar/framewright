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
