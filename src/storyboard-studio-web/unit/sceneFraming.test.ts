import { test } from 'node:test'
import assert from 'node:assert/strict'
import { frameGuide, framedFieldOfView } from '../src/sceneFraming.ts'

const close = (actual: number, expected: number) => assert.ok(Math.abs(actual - expected) < 1e-9, `${actual} != ${expected}`)
const tan = (degrees: number) => Math.tan(degrees * Math.PI / 360)

test('a frame narrower than the stage keeps the shot field of view and full height', () => {
  assert.equal(framedFieldOfView(38, 16 / 9, 2.4), 38)
  const guide = frameGuide(1200, 500, 16 / 9)
  close(guide.width, 500 * 16 / 9)
  close(guide.height, 500)
  close(guide.left, (1200 - 500 * 16 / 9) / 2)
  close(guide.top, 0)
})

test('a frame wider than the stage widens the stage field until the full shot width fits', () => {
  const stageAspect = 834 / 1080
  const fov = framedFieldOfView(38, 16 / 9, stageAspect)
  // The stage's horizontal extent equals the render's.
  close(tan(fov) * stageAspect, tan(38) * 16 / 9)
  const guide = frameGuide(834, 1080, 16 / 9)
  close(guide.width, 834)
  close(guide.height, 834 * 9 / 16)
  // And the frame's share of the stage height is the render's share of the widened field.
  close(guide.height / 1080, tan(38) / tan(fov))
})

test('nonsense sizes draw no frame and change nothing', () => {
  assert.deepEqual(frameGuide(0, 100, 1.5), { left: 0, top: 0, width: 0, height: 0 })
  assert.equal(framedFieldOfView(40, Number.NaN, 1), 40)
})
