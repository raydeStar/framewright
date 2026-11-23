import { test } from 'node:test'
import assert from 'node:assert/strict'
import { CLOSE_INSPECTION, keepsCamera, pinchZoom, zoomed, zoomLimits, ZOOM_IN, ZOOM_OUT, type KeptCamera } from '../src/inspectionCamera.ts'

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

/**
 * How close the camera comes. The rule is the reviewer's again: a part is
 * judged from where a person will stand, and that distance does not grow
 * with the part.
 */

const near = 0.01

test('a two-metre part can be looked at from thirty centimetres', () => {
  const limits = zoomLimits(2, near)
  assert.equal(limits.closest, 0.25)
  assert.equal(CLOSE_INSPECTION, 0.25)
  assert.ok(limits.closest <= 0.3)
  // It used to stop at 0.4 of the span: 80 cm, further than anyone stands.
  assert.ok(limits.closest < 2 * 0.4)
})

test('the closest distance is the same for a large part as for a very large one', () => {
  for (const span of [0.7, 1, 2, 4, 12, 60]) assert.equal(zoomLimits(span, near).closest, 0.25, `${span} m`)
})

test('a small part is still reached as closely as it was', () => {
  // Under 62.5 cm the old proportional limit was already nearer than 25 cm,
  // and a metre-based floor must not push the reviewer back from a fitting.
  assert.equal(zoomLimits(0.5, near).closest, 0.2)
  assert.ok(Math.abs(zoomLimits(0.1, near).closest - 0.04) < 1e-12)
  assert.equal(zoomLimits(0.625, near).closest, 0.25)
})

test('nothing comes nearer than the near plane allows', () => {
  // 0.4 of a two-centimetre part is 8 mm, inside a near plane at 1 cm: the
  // surface being looked at would be cut away as the camera reached it.
  assert.equal(zoomLimits(0.02, near).closest, 0.02)
  assert.equal(zoomLimits(0.001, near).closest, 0.02)
  assert.equal(zoomLimits(2, 0.2).closest, 0.4)
  for (const span of [0.001, 0.02, 0.1, 2, 60]) {
    const { closest } = zoomLimits(span, near)
    assert.ok(closest > near, `${span} m`)
  }
})

test('the far limit is forty spans, as it was', () => {
  for (const span of [0.1, 0.5, 2, 60]) assert.equal(zoomLimits(span, near).farthest, span * 40, `${span} m`)
})

test('the range is never inverted, however small the model', () => {
  for (const span of [0.0001, 0.001, 0.02, 0.5, 2, 400]) {
    const { closest, farthest } = zoomLimits(span, near)
    assert.ok(closest <= farthest, `${span} m`)
  }
})

test('zooming stops at the limits and nowhere before them', () => {
  const limits = zoomLimits(2, near)
  // From the opening distance of a two-metre part, steps in arrive at the floor and stay.
  let distance = 2 * 2.6
  const seen: number[] = []
  for (let step = 0; step < 40; step++) seen.push(distance = zoomed(distance, ZOOM_IN, limits))
  assert.equal(distance, 0.25)
  assert.ok(seen.some(value => value > 0.25 && value <= 0.3), 'a step lands at about thirty centimetres on the way in')
  assert.deepEqual(seen.filter((value, index) => index > 0 && value > seen[index - 1]), [], 'never back out')
  assert.equal(zoomed(distance, ZOOM_IN, limits), 0.25)
  // And out again, to the far limit and no further.
  for (let step = 0; step < 80; step++) distance = zoomed(distance, ZOOM_OUT, limits)
  assert.equal(distance, 80)
  assert.equal(zoomed(1, 1, limits), 1)
})

test('a camera already past a limit is never moved the wrong way by a zoom', () => {
  // The kept camera of a 60 cm revision, handed to a 70 cm one, sits a
  // centimetre inside the new floor. Asking to come closer must not push it
  // back out, and asking to back away backs away from where it is.
  const limits = zoomLimits(0.7, near)
  assert.equal(zoomed(0.24, ZOOM_IN, limits), 0.24)
  assert.equal(zoomed(0.24, ZOOM_OUT, limits), 0.24 * ZOOM_OUT)
  // And the far side: a camera beyond forty spans is not pulled in by a step out.
  assert.equal(zoomed(30, ZOOM_OUT, limits), 30)
  assert.equal(zoomed(30, ZOOM_IN, limits), 30 * ZOOM_IN)
  // A pinch that asks for nothing changes nothing, wherever the camera is.
  for (const distance of [0.1, 0.24, 1, 30]) assert.equal(zoomed(distance, 1, limits), distance)
})

test('a pinch zooms by how far the fingers moved apart', () => {
  assert.equal(pinchZoom(100, 200), 0.5)
  assert.equal(pinchZoom(200, 100), 2)
  assert.equal(pinchZoom(150, 150), 1)
  const limits = zoomLimits(2, near)
  assert.equal(zoomed(5.2, pinchZoom(100, 400), limits), 1.3)
  assert.equal(zoomed(0.4, pinchZoom(100, 400), limits), 0.25)
})

test('two fingers on one point ask for nothing', () => {
  assert.equal(pinchZoom(0, 120), 1)
  assert.equal(pinchZoom(120, 0), 1)
  assert.equal(pinchZoom(Number.NaN, 120), 1)
  assert.equal(pinchZoom(120, Number.NaN), 1)
})
