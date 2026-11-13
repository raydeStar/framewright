import { test } from 'node:test'
import assert from 'node:assert/strict'
import { formatMoment, matchesReviewFilter, nextInQueue, noteAnchorLabel, orderNotes, reviewBadge, reviewQueue, revisionLabel, SENT_BACK_WITHOUT_NOTE, sendBackNote, sentBackList, waitingCount } from '../src/assetReview.ts'
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

test('quick review walks current, unarchived revisions in name order', () => {
  const library = [
    asset({ id: 'c', displayName: 'Gong' }),
    asset({ id: 'a', displayName: 'Drum', reviewDecision: 'Approved' }),
    asset({ id: 'old', displayName: 'Bell', revisionFamilyId: 'f', revisionNumber: 1, isCurrentRevision: false }),
    asset({ id: 'new', displayName: 'Bell', revisionFamilyId: 'f', revisionNumber: 2, isCurrentRevision: true }),
    asset({ id: 'gone', displayName: 'Anvil', isArchived: true }),
  ]
  assert.deepEqual(reviewQueue(library, 'all').map(item => item.id), ['new', 'a', 'c'])
  assert.deepEqual(reviewQueue(library, 'Pending').map(item => item.id), ['new', 'c'])
  // Decided in this sitting: still walked, so going back finds it.
  assert.deepEqual(reviewQueue(library, 'Pending', new Set(['a'])).map(item => item.id), ['new', 'a', 'c'])
})

test('a decision moves on to the next one still waiting, wrapping round', () => {
  const queue = [
    asset({ id: '1', reviewDecision: 'Pending' }),
    asset({ id: '2', reviewDecision: 'Approved' }),
    asset({ id: '3', reviewDecision: 'Approved' }),
    asset({ id: '4', reviewDecision: 'Pending' }),
  ]
  assert.equal(nextInQueue(queue, '1', 'Pending'), '4')
  assert.equal(nextInQueue(queue, '4', 'Pending'), '1')
  assert.equal(nextInQueue(queue, '2', 'all'), '3')
  assert.equal(nextInQueue(queue, '4', 'all'), undefined)
  assert.equal(nextInQueue([asset({ id: 'only', reviewDecision: 'Approved' })], 'only', 'Pending'), undefined)
  assert.equal(waitingCount(queue, 'Pending'), 2)
  assert.equal(waitingCount(queue, 'all'), 4)
})

test('the revision reads as its place in the stack', () => {
  const v1 = asset({ id: 'v1', revisionFamilyId: 'f', revisionNumber: 1, isCurrentRevision: false })
  const v2 = asset({ id: 'v2', revisionFamilyId: 'f', revisionNumber: 2, isCurrentRevision: true })
  assert.equal(revisionLabel(v2, [v1, v2]), 'rev 2 of 2')
  assert.equal(revisionLabel(asset(), [asset()]), 'rev 1 of 1')
})

test('an empty feedback box still sends a reason the server accepts', () => {
  assert.equal(sendBackNote('  Lid is muddy. '), 'Lid is muddy.')
  assert.equal(sendBackNote('   '), SENT_BACK_WITHOUT_NOTE)
})

test('the send-back list is one plain line per asset sent back', () => {
  const library = [
    asset({ id: 'b', displayName: 'SM_Kettle', reviewDecision: 'ChangesRequested', reviewNote: 'Lid is\nmuddy.' }),
    asset({ id: 'a', displayName: 'SM_Drum', reviewDecision: 'Approved' }),
    asset({ id: 'v1', displayName: 'SM_Bell', revisionFamilyId: 'f', revisionNumber: 1, isCurrentRevision: false, reviewDecision: 'ChangesRequested', reviewNote: 'Old.' }),
    asset({ id: 'v2', displayName: 'SM_Bell', revisionFamilyId: 'f', revisionNumber: 2, isCurrentRevision: true, reviewDecision: 'ChangesRequested', reviewNote: 'Too shiny.' }),
  ]
  assert.equal(sentBackList(library), 'SM_Bell — rev 2 of 2 — Too shiny.\nSM_Kettle — rev 1 of 1 — Lid is muddy.')
})
