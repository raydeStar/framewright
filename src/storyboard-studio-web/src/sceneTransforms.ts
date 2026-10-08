/**
 * Transforms produced by direct manipulation in the scene view, in the same
 * terms the placement fields edit: metres, radians, and per-axis scale.
 */
export interface SceneInstanceTransform { position: number[]; rotation: number[]; scale: number[] }

export type HandleMode = 'move' | 'rotate' | 'scale'

/** The service's working volume and scale range (SceneService). */
const MAX_DISTANCE = 10_000
const MIN_SCALE = 0.001
const MAX_SCALE = 1000

/**
 * Turns a handle's raw transform into one the service accepts and the fields
 * show cleanly: millimetres, ten-thousandths of a radian, and scale inside the
 * supported range. A handle can drag scale through zero or below it; a stored
 * scale cannot be either.
 */
export function settleTransform(position: readonly number[], rotation: readonly number[], scale: readonly number[]): SceneInstanceTransform {
  // `+ 0` turns a rounded -0 into 0, so a field never shows "-0".
  const fixed = (value: number, places: number) => Number(value.toFixed(places)) + 0
  const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value))
  return {
    position: position.map(value => fixed(clamp(value, -MAX_DISTANCE, MAX_DISTANCE), 3)),
    rotation: rotation.map(value => fixed(value, 4)),
    scale: scale.map(value => fixed(clamp(Math.abs(value), MIN_SCALE, MAX_SCALE), 3)),
  }
}
