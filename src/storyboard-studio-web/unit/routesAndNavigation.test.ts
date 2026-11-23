import { test } from 'node:test'
import assert from 'node:assert/strict'
import { exactReference, madeFrom, neighbours, referenceSuggestions, referencesByName } from '../src/assetNavigation.ts'
import { formatAssetRoute, formatWorkspaceRoute, parseRoute } from '../src/routes.ts'
import type { AssetSummary } from '../src/types.ts'

const id = 'a1b2c3d4-0000-4000-8000-000000000001'

test('a refresh reads the library view, the open asset and the filters back from the address', () => {
  assert.deepEqual(parseRoute('/assets/models', '?review=Pending&sort=name&q=gear'), {
    workspace: 'assets', assets: { view: 'models', assetId: undefined, reviewing: undefined, review: 'Pending', sort: 'name', query: 'gear' },
  })
  assert.equal(parseRoute(`/assets/models/${id}`, '').assets?.assetId, id)
  assert.equal(parseRoute('/assets/models/review', '').assets?.reviewing, true)
  assert.equal(parseRoute(`/assets/collection/${id}`, '').assets?.view, `collection:${id}`)
  assert.equal(parseRoute('/assets', '?review=Nonsense').assets?.review, 'all')
  assert.deepEqual(parseRoute('/sounds', ''), { workspace: 'sounds' })
  assert.deepEqual(parseRoute(`/shot/${id}`, ''), { workspace: 'shot', shotId: id })
  assert.deepEqual(parseRoute('/somewhere/else', ''), { workspace: 'board' })
})

test('the address written is the one read back', () => {
  const route = { view: 'models' as const, assetId: id, review: 'Pending' as const, sort: 'name' as const, query: 'gear pot' }
  const path = formatAssetRoute(route)
  assert.equal(path, `/assets/models/${id}?review=Pending&sort=name&q=gear+pot`)
  const [pathname, search] = path.split('?')
  assert.deepEqual(parseRoute(pathname, `?${search}`).assets, { ...route, reviewing: undefined })
  assert.equal(formatAssetRoute({ view: 'all', review: 'all', sort: 'newest', query: '' }), '/assets')
  assert.equal(formatAssetRoute({ view: 'models', reviewing: true, review: 'all', sort: 'newest', query: '' }), '/assets/models/review')
  assert.equal(formatWorkspaceRoute('board'), '/')
  assert.equal(formatWorkspaceRoute('sounds'), '/sounds')
  assert.equal(formatWorkspaceRoute('shot', id), `/shot/${id}`)
})

test('next and previous step through the list the asset was opened from', () => {
  assert.deepEqual(neighbours(['a', 'b', 'c'], 'b'), { position: 2, total: 3, previous: 'a', next: 'c' })
  assert.deepEqual(neighbours(['a', 'b', 'c'], 'a'), { position: 1, total: 3, previous: undefined, next: 'b' })
  assert.equal(neighbours(['a'], 'z'), undefined)
})

const asset = (overrides: Partial<AssetSummary>): AssetSummary => ({
  id: 'x', projectId: 'p', kind: 'Image', originalFileName: 'x.png', mimeType: 'image/png', bytes: 1, contentHash: 'h',
  contentUrl: '/x', createdAt: '2026-11-20T00:00:00Z', displayName: 'x', tags: [], notes: '', source: 'Imported', isArchived: false,
  updatedAt: '2026-11-20T00:00:00Z', isCurrentRevision: true, revisionPrompt: '', revisionEngine: '', preparationAcceptedBy: '',
  preparationAcceptanceNote: '', preparationTopologyChanged: false, reviewDecision: 'Pending', reviewNote: '', reviewAnswersNote: '',
  ...overrides,
})

test('a model is offered the image with its own name first', () => {
  const model = asset({ id: 'm', kind: 'Model', displayName: 'SM_Gear (req 1.2)' })
  const library = [
    model,
    asset({ id: 'other', displayName: 'SM_Pot (req 2.0)', createdAt: '2026-11-21T00:00:00Z' }),
    asset({ id: 'loose', displayName: 'SM_Gear sketch' }),
    asset({ id: 'older', displayName: 'SM_Gear (req 1.1)' }),
    asset({ id: 'exact', displayName: 'SM_Gear (req 1.2)' }),
    asset({ id: 'gone', displayName: 'SM_Gear (req 1.2)', isArchived: true }),
  ]
  assert.deepEqual(referenceSuggestions(model, library).map(item => item.id), ['exact', 'older', 'loose', 'other'])
  assert.deepEqual(referenceSuggestions(model, library, 'pot').map(item => item.id), ['other'])
  assert.equal(exactReference(model, library)?.id, 'exact')
  assert.equal(exactReference(asset({ kind: 'Model', displayName: 'SM_Nothing' }), library), undefined)
})

test('an image lists the models made from it', () => {
  const library = [
    asset({ id: 'm1', kind: 'Model', displayName: 'B', referenceAssetId: 'img' }),
    asset({ id: 'm2', kind: 'Model', displayName: 'A', referenceAssetId: 'img-v2' }),
    asset({ id: 'm3', kind: 'Model', displayName: 'C', referenceAssetId: 'elsewhere' }),
    asset({ id: 'old', kind: 'Model', displayName: 'D', referenceAssetId: 'img', revisionFamilyId: 'f', isCurrentRevision: false }),
  ]
  assert.deepEqual(madeFrom(['img', 'img-v2'], library).map(item => item.id), ['m2', 'm1'])
})

test('the library names a concept image "<model> - concept", and that still matches', () => {
  const model = asset({ id: 'm', kind: 'Model', displayName: 'SM_Part_RecallSpool (req 3.5)' })
  const concept = asset({ id: 'c', displayName: 'SM_Part_RecallSpool (req 3.5) - concept' })
  const linked = asset({ id: 'l', kind: 'Model', displayName: 'SM_Done (req 1)', referenceAssetId: 'x' })
  const twin = asset({ id: 't', kind: 'Model', displayName: 'SM_Twin (req 1)' })
  const library = [model, concept, linked, asset({ id: 'lc', displayName: 'SM_Done (req 1) - concept' }), twin,
    asset({ id: 't1', displayName: 'SM_Twin (req 1) - concept' }), asset({ id: 't2', displayName: 'SM_Twin (req 1)' })]
  assert.equal(exactReference(model, library)?.id, 'c')
  // Linked ones are left alone, and two equally good images are left to a person.
  assert.deepEqual(referencesByName(library).map(pair => [pair.model.id, pair.image.id]), [['m', 'c']])
})
