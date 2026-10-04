import { test } from 'node:test'
import assert from 'node:assert/strict'
import { formatMoment, matchesReviewFilter, noteAnchorLabel, orderNotes, reviewBadge } from '../src/assetReview.ts'
import type { AssetReviewNoteSummary, AssetSummary } from '../src/types.ts'

const asset = (overrides: Partial<AssetSummary> = {}): AssetSummary => ({
  id: 'a1', projectId: 'p1', kind: 'Model', originalFileName: 'chest.glb', mimeType: 'model/gltf-binary', bytes: 10,
  contentHash: 'h', contentUrl: '/api/assets/a1/content', createdAt: '2026-10-04T00:00:00Z', displayName: 'Chest',
  tags: [], notes: '', source: 'Imported', isArchived: false, updatedAt: '2026-10-04T00:00:00Z', isCurrentRevision: true,
  revisionPrompt: '', revisionEngine: '', preparationAcceptedBy: '', preparationAcceptanceNote: '',
  preparationTopologyChanged: false, reviewDecision: 'Pending', reviewNote: '', reviewAnswersNote: '',
  ...overrides,
})

const note = (overrides: Partial<AssetReviewNoteSummary> = {}): AssetReviewNoteSummary => ({
  id: 'n1', assetId: 'a1', x: 0, y: 0, body: 'Fix it', state: 'Open', createdAt: '2026-10-04T00:00:00Z', anchor: 'None',
  ...overrides,
})

test('the library filter matches one decision, or every asset', () => {
  const approved = asset({ reviewDecision: 'Approved' })
  assert.equal(matchesReviewFilter(approved, 'all'), true)
  assert.equal(matchesReviewFilter(approved, 'Approved'), true)
  assert.equal(matchesReviewFilter(approved, 'Pending'), false)
  assert.equal(matchesReviewFilter(asset({ reviewDecision: 'ChangesRequested' }), 'ChangesRequested'), true)
})

test('a card says what was decided, and why on hover', () => {
  assert.deepEqual(reviewBadge(asset({ reviewDecision: 'ChangesRequested', reviewNote: 'Improve the lid texture.' })), {
    decision: 'ChangesRequested', label: 'Changes requested', title: 'Changes requested: “Improve the lid texture.”',
  })
  assert.equal(reviewBadge(asset({ reviewDecision: 'Approved' })).label, 'Approved')
  assert.equal(reviewBadge(asset()).label, 'Pending')
})

test('a pending revision that answers a send-back says so', () => {
  const badge = reviewBadge(asset({ reviewAnswersAssetId: 'a0', reviewAnswersNote: 'Weak effect.' }))
  assert.equal(badge.decision, 'Pending')
  assert.equal(badge.label, 'Answers send-back')
  assert.match(badge.title, /Weak effect\./)
})

test('moments read the way a player shows them', () => {
  assert.equal(formatMoment(0), '0:00.0')
  assert.equal(formatMoment(7.5), '0:07.5')
  assert.equal(formatMoment(62), '1:02.0')
})

test('each note says where it is attached', () => {
  assert.equal(noteAnchorLabel(note({ anchor: 'Time', timeSeconds: 12.25 })), 'At 0:12.3')
  assert.equal(noteAnchorLabel(note({ anchor: 'View', viewYaw: 0.9, viewPitch: 0.42 })), 'From the view at yaw 0.90 · pitch 0.42')
  assert.equal(noteAnchorLabel(note({ anchor: 'Point', x: 0.25, y: 0.5 })), '25% across · 50% down')
  assert.equal(noteAnchorLabel(note()), 'Whole asset')
})

test('open notes come first, timed ones in playback order', () => {
  const ordered = orderNotes([
    note({ id: 'resolved', state: 'Resolved', anchor: 'Time', timeSeconds: 0.1 }),
    note({ id: 'late', anchor: 'Time', timeSeconds: 9 }),
    note({ id: 'whole', createdAt: '2026-10-03T00:00:00Z' }),
    note({ id: 'early', anchor: 'Time', timeSeconds: 2 }),
  ])
  assert.deepEqual(ordered.map(item => item.id), ['early', 'late', 'whole', 'resolved'])
})
