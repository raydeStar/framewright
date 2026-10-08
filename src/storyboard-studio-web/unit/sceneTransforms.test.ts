import { test } from 'node:test'
import assert from 'node:assert/strict'
import { settleTransform } from '../src/sceneTransforms.ts'

test('handle transforms are rounded to what the fields show', () => {
  const settled = settleTransform([1.23456, -0.00001, 2], [0.123456, 0, -0.00001], [1.00049, 2, 3])
  assert.deepEqual(settled.position, [1.235, 0, 2])
  assert.deepEqual(settled.rotation, [0.1235, 0, 0])
  assert.deepEqual(settled.scale, [1, 2, 3])
  assert.ok(!Object.is(settled.position[1], -0))
})

test('a scale dragged through zero is held inside the supported range', () => {
  assert.deepEqual(settleTransform([0, 0, 0], [0, 0, 0], [0, -2, 5000]).scale, [0.001, 2, 1000])
})

test('a position is held inside the working volume', () => {
  assert.deepEqual(settleTransform([20_000, -20_000, 0], [0, 0, 0], [1, 1, 1]).position, [10_000, -10_000, 0])
})
