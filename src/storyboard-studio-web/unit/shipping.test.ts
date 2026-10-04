import { test } from 'node:test'
import assert from 'node:assert/strict'
import { defaultDestination, shipSummary } from '../src/shipping.ts'
import type { ShipmentItemPreview, ShipmentPreview } from '../src/types.ts'

const item = (overrides: Partial<ShipmentItemPreview>): ShipmentItemPreview => ({
  assetId: 'a', revisionId: 'r', displayName: 'Chest', kind: 'Model', decision: 'Approved', ships: true, reason: null,
  ...overrides,
})

const preview = (items: ShipmentItemPreview[]): ShipmentPreview => ({
  sourceName: 'Props', includePending: false, items,
  shipCount: items.filter(entry => entry.ships).length,
  skippedCount: items.filter(entry => !entry.ships).length,
})

test('a shipment says what goes and, grouped, why the rest stays', () => {
  assert.equal(shipSummary(preview([item({}), item({ revisionId: 'b' })])), '2 will ship')
  assert.equal(shipSummary(preview([
    item({}),
    item({ revisionId: 'p', decision: 'Pending', ships: false, reason: 'Pending review' }),
    item({ revisionId: 's', decision: 'ChangesRequested', ships: false, reason: 'Sent back: The glow reads weak.' }),
    item({ revisionId: 't', decision: 'ChangesRequested', ships: false, reason: 'Sent back' }),
    item({ revisionId: 'x', ships: false, reason: 'Archived' }),
  ])), '1 will ship · 4 left out: 1 pending, 2 sent back, 1 archived')
})

test('nothing approved reads as nothing shipping', () => {
  assert.equal(shipSummary(preview([item({ ships: false, decision: 'Pending', reason: 'Pending review' })])),
    '0 will ship · 1 left out: 1 pending')
})

test('the first destination that can be written is offered first, else a zip', () => {
  assert.equal(defaultDestination([]), 'zip')
  assert.equal(defaultDestination([
    { name: 'Broken', path: 'X:/missing', available: false, problem: 'Its folder does not exist.' },
    { name: 'Game', path: '/srv/game/import', available: true, problem: null },
  ]), 'Game')
  assert.equal(defaultDestination([{ name: 'Broken', path: 'X:/missing', available: false, problem: 'No.' }]), 'zip')
})
