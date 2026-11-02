import { test } from 'node:test'
import assert from 'node:assert/strict'
import { describeTravel, planCameraMove } from '../src/cameraMove.ts'

const aspect = 16 / 9

test('a push-in keeps the source as its start and ends on a closer window around the focus', () => {
  const plan = planCameraMove(1920, 1080, aspect, 'PushIn', 20, 0.5, 0.5)
  assert.equal(plan.start, undefined)
  assert.deepEqual(plan.end, { x: 160, y: 90, width: 1600, height: 900 })
})

test('a closer window never leaves the source, wherever the focus is', () => {
  const plan = planCameraMove(1920, 1080, aspect, 'PullBack', 20, 0.98, 0.02)
  assert.deepEqual(plan.start, { x: 320, y: 0, width: 1600, height: 900 })
  assert.equal(plan.end, undefined)
})

test('a truck right slides one window from the left edge to the right at constant framing', () => {
  const plan = planCameraMove(1920, 1080, aspect, 'TruckRight', 20, 0.5, 0.5)
  assert.deepEqual(plan.start, { x: 0, y: 90, width: 1600, height: 900 })
  assert.deepEqual(plan.end, { x: 320, y: 90, width: 1600, height: 900 })
  const back = planCameraMove(1920, 1080, aspect, 'TruckLeft', 20, 0.5, 0.5)
  assert.deepEqual(back.start, plan.end)
  assert.deepEqual(back.end, plan.start)
})

test('a crane rises from the bottom of the delivery frame to its top', () => {
  const plan = planCameraMove(1920, 1080, aspect, 'CraneUp', 20, 0.5, 0.5)
  assert.deepEqual(plan.start, { x: 160, y: 180, width: 1600, height: 900 })
  assert.deepEqual(plan.end, { x: 160, y: 0, width: 1600, height: 900 })
})

test('a source wider than the delivery shape travels only inside its delivery-shaped centre', () => {
  // 2400x1080 holds a 1920x1080 delivery frame from x=240 to x=2160.
  const plan = planCameraMove(2400, 1080, aspect, 'TruckRight', 20, 0.5, 0.5)
  assert.equal(plan.start!.x, 240)
  assert.equal(plan.end!.x + plan.end!.width, 2160)
  assert.equal(plan.start!.width / plan.start!.height, 16 / 9)
})

test('travel reads the way an artist judges it', () => {
  assert.equal(describeTravel('PushIn', 20), 'Ends 20% closer')
  assert.equal(describeTravel('TruckRight', 25), 'Travels 20% of the frame')
})
