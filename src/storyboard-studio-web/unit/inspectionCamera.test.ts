import { test } from 'node:test'
import assert from 'node:assert/strict'
import { keepsCamera, type KeptCamera } from '../src/inspectionCamera.ts'

/**
 * When the inspection camera stays put, without a browser. The rule under test
 * is the reviewer's: flipping between revisions of one stack keeps their zoom
 * and angle, and anything else -- another asset, or a revision too different
 * in size for the old camera to make sense -- is framed afresh.
 */

const left = (overrides: Partial<KeptCamera> = {}): KeptCamera => ({
  stack: 'family-a',
  size: 2,
  yaw: 2.4,
  pitch: -0.3,
  distance: 0.9,
  target: [0.2, 1.1, -0.4],
  ...overrides,
})

test('another revision of the same stack keeps the camera', () => {
  assert.equal(keepsCamera(left(), 'family-a', 2), true)
})

test('before the model has loaded, the stack alone decides', () => {
  assert.equal(keepsCamera(left(), 'family-a'), true)
  assert.equal(keepsCamera(left(), 'family-b'), false)
})

test('a different asset starts from the opening framing, whatever its size', () => {
  assert.equal(keepsCamera(left(), 'family-b', 2), false)
})

test('nothing kept, or a model with no stack named, is framed afresh', () => {
  assert.equal(keepsCamera(undefined, 'family-a', 2), false)
  assert.equal(keepsCamera(left(), undefined, 2), false)
})

test('a revision within a quarter of the size keeps the camera', () => {
  assert.equal(keepsCamera(left(), 'family-a', 2.4), true)
  assert.equal(keepsCamera(left(), 'family-a', 2.5), true)
  assert.equal(keepsCamera(left(), 'family-a', 1.7), true)
})

test('a revision more than a quarter larger or smaller is framed afresh', () => {
  assert.equal(keepsCamera(left(), 'family-a', 2.6), false)
  assert.equal(keepsCamera(left(), 'family-a', 1.5), false)
  assert.equal(keepsCamera(left(), 'family-a', 8), false)
})

test('the answer is the same in both directions, so v1 to v2 and back agree', () => {
  for (const [one, other] of [[1, 1.2], [1, 1.25], [1, 1.3], [2, 2.6], [0.45, 2]]) {
    assert.equal(
      keepsCamera(left({ size: one }), 'family-a', other),
      keepsCamera(left({ size: other }), 'family-a', one),
      `${one} and ${other}`,
    )
  }
})

test('a size that cannot be compared never keeps the camera', () => {
  assert.equal(keepsCamera(left({ size: 0 }), 'family-a', 0), false)
  assert.equal(keepsCamera(left(), 'family-a', Number.NaN), false)
  assert.equal(keepsCamera(left(), 'family-a', Number.POSITIVE_INFINITY), false)
  assert.equal(keepsCamera(left({ size: Number.NaN }), 'family-a', 2), false)
})
