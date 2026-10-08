import type { SceneInstanceSummary, ScenePlaceholderSummary } from './types'

/**
 * Hand-placed stand-ins: simple geometry the artist blocks a scene out with
 * before any model exists. They are ordinary scene instances with placeholder
 * geometry and no asset, the same thing an approved blockout plan builds, so
 * replacing one with a library model, saving, and rendering all work through
 * the existing paths. A hand-placed stand-in has no plan provenance.
 *
 * Sizes are in metres, +Y up, matching docs/3D_CONVENTIONS.md.
 */
export interface StandInKind {
  key: 'person' | 'box' | 'cylinder' | 'sphere' | 'floor' | 'wall'
  label: string
  shape: ScenePlaceholderSummary['shape']
  size: [number, number, number]
}

export const STAND_IN_KIT: readonly StandInKind[] = [
  // A person first: most shots are about one, and a figure gives every other
  // stand-in its scale.
  { key: 'person', label: 'Person', shape: 'Person', size: [0.5, 1.75, 0.3] },
  { key: 'box', label: 'Box', shape: 'Box', size: [1, 1, 1] },
  { key: 'cylinder', label: 'Cylinder', shape: 'Cylinder', size: [0.6, 1.2, 0.6] },
  { key: 'sphere', label: 'Sphere', shape: 'Sphere', size: [0.8, 0.8, 0.8] },
  { key: 'floor', label: 'Floor', shape: 'Plane', size: [10, 0.01, 10] },
  { key: 'wall', label: 'Wall', shape: 'Box', size: [4, 2.5, 0.15] },
]

/** The service's own limits for stand-in size, so the form never offers a value it would refuse. */
export const STAND_IN_MIN_SIZE = 0.01
export const STAND_IN_MAX_SIZE = 1000

/** The next unused "Box 3"-style name, so two stand-ins are never confused in the object list. */
export function nextStandInName(label: string, instances: readonly Pick<SceneInstanceSummary, 'name'>[]) {
  const pattern = new RegExp(`^${label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')} (\\d+)$`)
  const taken = new Set(instances.map(instance => pattern.exec(instance.name)?.[1]).filter(Boolean).map(Number))
  let ordinal = 1
  while (taken.has(ordinal)) ordinal += 1
  return `${label} ${ordinal}`
}

/**
 * A new stand-in resting on the floor where the inspection camera is looking,
 * so it appears in view rather than at a far-off origin.
 */
export function createStandIn(
  kind: StandInKind,
  instances: readonly SceneInstanceSummary[],
  lookingAt: readonly number[],
  id: string = crypto.randomUUID(),
): SceneInstanceSummary {
  const size: number[] = [...kind.size]
  const x = Number.isFinite(lookingAt[0]) ? round(lookingAt[0]) : 0
  const z = Number.isFinite(lookingAt[2]) ? round(lookingAt[2]) : 0
  // A wall stands at the back of what the camera sees, not through its middle.
  const back = kind.key === 'wall' ? -2 : 0
  return {
    id,
    assetId: null,
    name: nextStandInName(kind.label, instances),
    position: [x, round(restingHeight(kind.shape, size)), round(z + back)],
    rotation: [0, 0, 0],
    scale: [1, 1, 1],
    assetName: 'Placeholder',
    revisionNumber: 1,
    contentUrl: null,
    available: true,
    archived: false,
    dimensions: size,
    placeholder: kind.shape === 'Person' ? { shape: kind.shape, size, pose: 'Neutral' } : { shape: kind.shape, size },
    role: null,
    planId: null,
    clip: null,
    motion: null,
  }
}

/** How far above its origin a stand-in's lowest point is, matching how the viewport draws it. */
export function restingHeight(shape: ScenePlaceholderSummary['shape'], size: readonly number[]) {
  if (shape === 'Sphere') return Math.max(size[0], size[1], size[2]) / 2
  if (shape === 'Plane') return Math.max(size[1], 0.01) / 2
  return size[1] / 2
}

/**
 * One axis of a stand-in's size, set from a form value. A value the service
 * would refuse is held at the nearest one it accepts; a non-number changes
 * nothing.
 */
export function resizeStandIn(placeholder: ScenePlaceholderSummary, axis: 0 | 1 | 2, value: number): ScenePlaceholderSummary {
  if (!Number.isFinite(value)) return placeholder
  const clamped = Math.min(STAND_IN_MAX_SIZE, Math.max(STAND_IN_MIN_SIZE, value))
  // A sphere has one size; the viewport draws its largest axis, so all three move together.
  if (placeholder.shape === 'Sphere') return { ...placeholder, size: [clamped, clamped, clamped] }
  return { ...placeholder, size: placeholder.size.map((existing, index) => index === axis ? clamped : existing) }
}

function round(value: number) { return Math.round(value * 1000) / 1000 }
