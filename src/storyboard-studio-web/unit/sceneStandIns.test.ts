import { test } from 'node:test'
import assert from 'node:assert/strict'
import { STAND_IN_KIT, createImageCard, createStandIn, nextStandInName, resizeStandIn, restingHeight } from '../src/sceneStandIns.ts'

const kind = (key: string) => STAND_IN_KIT.find(item => item.key === key)!

test('a new stand-in is placeholder geometry with no asset and no plan', () => {
  const box = createStandIn(kind('box'), [], [0, 0.5, 0], 'id-1')
  assert.equal(box.assetId, null)
  assert.equal(box.planId, null)
  assert.equal(box.role, null)
  assert.deepEqual(box.placeholder, { shape: 'Box', size: [1, 1, 1] })
  assert.equal(box.name, 'Box 1')
})

test('a stand-in rests on the floor where the camera is looking', () => {
  const box = createStandIn(kind('box'), [], [2, 7, -3])
  assert.deepEqual(box.position, [2, 0.5, -3])
  const floor = createStandIn(kind('floor'), [], [0, 0, 0])
  assert.equal(floor.position[1], 0.005)
  const wall = createStandIn(kind('wall'), [], [1, 0, 1])
  assert.deepEqual(wall.position, [1, 1.25, -1])
})

test('names skip ordinals that are already used, including gaps', () => {
  assert.equal(nextStandInName('Box', [{ name: 'Box 1' }, { name: 'Box 3' }, { name: 'Boxer 2' }]), 'Box 2')
  assert.equal(nextStandInName('Box', [{ name: 'Box 1' }, { name: 'Box 2' }]), 'Box 3')
})

test('the size kept is the one the service accepts', () => {
  const box = { shape: 'Box' as const, size: [1, 1, 1] }
  assert.deepEqual(resizeStandIn(box, 0, 2.5).size, [2.5, 1, 1])
  assert.deepEqual(resizeStandIn(box, 1, 0).size, [1, 0.01, 1])
  assert.deepEqual(resizeStandIn(box, 2, 5000).size, [1, 1, 1000])
  assert.deepEqual(resizeStandIn(box, 0, Number.NaN), box)
})

test('a sphere keeps one diameter on every axis', () => {
  assert.deepEqual(resizeStandIn({ shape: 'Sphere', size: [1, 1, 1] }, 1, 3).size, [3, 3, 3])
})

test('resting height matches how each shape is drawn', () => {
  assert.equal(restingHeight('Box', [1, 2, 1]), 1)
  assert.equal(restingHeight('Cylinder', [0.5, 1.2, 0.5]), 0.6)
  assert.equal(restingHeight('Sphere', [0.4, 0.8, 0.4]), 0.4)
  assert.equal(restingHeight('Plane', [10, 0, 10]), 0.005)
})

test('an image card takes its picture shape, stands on the floor, and cites the picture', () => {
  const card = createImageCard({ id: 'img-1', displayName: 'Harbour', contentUrl: '/api/assets/img-1/content', width: 1920, height: 1080 }, [1, 0, 2], 'card-1')
  assert.deepEqual(card.placeholder, { shape: 'Card', size: [3.556, 2, 0.01], imageAssetId: 'img-1', imageUrl: '/api/assets/img-1/content' })
  assert.equal(card.assetId, null)
  assert.deepEqual(card.position, [1, 1, 0.5])
  assert.equal(card.name, 'Harbour')
  const unknown = createImageCard({ id: 'img-2', displayName: '', contentUrl: '/x' }, [0, 0, 0])
  assert.deepEqual(unknown.placeholder!.size, [2, 2, 0.01])
  assert.equal(unknown.name, 'Image card')
})

test('resizing a card keeps the picture shape', () => {
  const card = { shape: 'Card' as const, size: [4, 2, 0.01], imageAssetId: 'img-1' }
  assert.deepEqual(resizeStandIn(card, 0, 6).size, [6, 3, 0.01])
  assert.deepEqual(resizeStandIn(card, 1, 1).size, [2, 1, 0.01])
  assert.equal(resizeStandIn(card, 0, 6).imageAssetId, 'img-1')
})
