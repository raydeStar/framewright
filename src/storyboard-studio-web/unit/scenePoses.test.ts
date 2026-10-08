import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { PERSON_POSES, POSE_ANGLES, poseAngles } from '../src/scenePoses.ts'

test('the studio offers exactly the poses the service accepts, in the same order', () => {
  const service = readFileSync(fileURLToPath(new URL('../../StoryboardStudio.Api/Services/SceneService.cs', import.meta.url)), 'utf8')
  const declared = /SupportedPoses = \[([^\]]+)\]/.exec(service)
  assert.ok(declared, 'SceneService declares SupportedPoses')
  const names = [...declared[1].matchAll(/"([^"]+)"/g)].map(match => match[1])
  assert.deepEqual(names, [...PERSON_POSES])
})

test('every pose turns only finite amounts at known joints', () => {
  for (const pose of PERSON_POSES) {
    for (const turn of Object.values(POSE_ANGLES[pose])) {
      assert.equal(turn.length, 3)
      assert.ok(turn.every(Number.isFinite), pose)
    }
  }
})

test('an unknown or missing pose stands in Neutral', () => {
  assert.equal(poseAngles('Dancing'), POSE_ANGLES.Neutral)
  assert.equal(poseAngles(null), POSE_ANGLES.Neutral)
  assert.equal(poseAngles('Seated'), POSE_ANGLES.Seated)
})
