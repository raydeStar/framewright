import { test } from 'node:test'
import assert from 'node:assert/strict'
import { laneTemplate, stackClips } from '../src/timelineLanes.ts'

const clip = (id: string, startFrame: number, durationFrames: number) => ({ id, startFrame, durationFrames })

test('clips that follow one another share a single row', () => {
  const stack = stackClips([clip('a', 0, 48), clip('b', 48, 24), clip('c', 100, 20)], 672)
  assert.equal(stack.rows, 1)
  assert.deepEqual(stack.rowOf, { a: 0, b: 0, c: 0 })
})

test('a bed under spot effects puts each overlap on the first row that is free', () => {
  // The Bill Comes Due: an air bed, sails over its start, then cues that overlap each other.
  const stack = stackClips([
    clip('air', 0, 480), clip('sails', 0, 150), clip('wire', 146, 116), clip('twist', 268, 84), clip('glow', 334, 42),
    clip('whoomph', 480, 86), clip('fire', 482, 190), clip('ship', 536, 136),
  ], 672)
  assert.equal(stack.rows, 3)
  assert.deepEqual(stack.rowOf, { air: 0, sails: 1, wire: 2, twist: 1, glow: 2, whoomph: 0, fire: 1, ship: 2 })
})

test('a short clip is stacked by the width it is drawn at, not its length', () => {
  // 3% of 672 frames is 21 frames: a 4-frame tick at 100 still covers 100-121 on screen.
  assert.equal(stackClips([clip('tick', 100, 4), clip('next', 110, 30)], 672).rows, 2)
  assert.equal(stackClips([clip('tick', 100, 4), clip('next', 121, 30)], 672).rows, 1)
})

test('the order clips arrive in does not change the rows', () => {
  const clips = [clip('air', 0, 480), clip('sails', 0, 150), clip('wire', 146, 116)]
  assert.deepEqual(stackClips([...clips].reverse(), 672), stackClips(clips, 672))
})

test('an empty lane keeps one row, and the grid gives stacked lanes room in proportion', () => {
  assert.deepEqual(stackClips([], 672), { rows: 1, rowOf: {} })
  assert.equal(laneTemplate([1, 1, 1, 1]), 'minmax(64px, 1.35fr) minmax(46px, 1fr) minmax(46px, 1fr) minmax(46px, 1fr) minmax(46px, 1fr)')
  assert.equal(laneTemplate([1, 1, 3, 1]), 'minmax(64px, 1.35fr) minmax(46px, 1fr) minmax(46px, 1fr) minmax(82px, 3fr) minmax(46px, 1fr)')
})
