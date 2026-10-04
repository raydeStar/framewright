import { test } from 'node:test'
import assert from 'node:assert/strict'
import { autoOffer, geometryBudget, manualBudgetProblem, preparationBudget } from '../src/modelBudget.ts'
import type { ModelTriangleBudget } from '../src/types.ts'

/**
 * How a runtime budget is offered and read, without a browser. The numbers in
 * these answers are the compiler's; the rules under test are only that the
 * studio shows them faithfully, defaults to Auto when Auto can work, flags an
 * over-budget model gently, and never fills in a number of its own.
 */

const answer = (overrides: Partial<ModelTriangleBudget> = {}): ModelTriangleBudget => ({
  assetId: '0f8fad5b-d9cb-469f-a165-70867728950e',
  triangleCount: 100_000,
  state: 'Decided',
  detail: null,
  role: 'prop',
  roleReason: "its name says 'chest'",
  sizeClass: 'medium',
  longestMetres: 1.2,
  triangleBudget: 5_000,
  maximumP99Metres: 0.0039,
  maximumMaxMetres: 0.0157,
  ladder: [5_000, 7_500, 11_300],
  summary: 'A medium prop: about 5,000 triangles.',
  ...overrides,
})

const n = (value: number) => value.toLocaleString()

test('Auto is offered in the compiler’s words, beside what the model has', () => {
  const offer = autoOffer(answer(), 100_000)
  assert.equal(offer.kind, 'ready')
  assert.ok(offer.kind === 'ready')
  assert.equal(offer.budget, 5_000)
  assert.equal(offer.text, `A medium prop: about 5,000 triangles. This model has ${n(100_000)}.`)
  assert.equal(offer.reason, "its name says 'chest'")
})

test('Auto is the default: with nothing typed, Prepare sends no number at all', () => {
  const offer = autoOffer(answer(), 100_000)
  // null is "auto" on the wire; the compiler decides when the stage runs.
  assert.equal(preparationBudget(false, offer, '', 100_000), null)
})

test('nothing can be sent while the compiler is still being asked', () => {
  const offer = autoOffer(undefined, 100_000)
  assert.equal(offer.kind, 'loading')
  assert.equal(preparationBudget(false, offer, '', 100_000), undefined)
})

test('a model already within its budget has nothing for Auto to reduce', () => {
  const offer = autoOffer(answer({ triangleBudget: 10_000, summary: 'A medium prop: about 10,000 triangles.' }), 4_000)
  assert.equal(offer.kind, 'within')
  assert.match(offer.kind === 'within' ? offer.text : '', /nothing to reduce/)
  assert.equal(preparationBudget(false, offer, '', 4_000), undefined)
})

test('a character gets no number from the compiler, and none is invented for it', () => {
  const offer = autoOffer(
    answer({
      role: 'character',
      triangleBudget: null,
      ladder: [],
      summary: 'A character takes the rig route; its skeleton profile sets its budget, not this table.',
    }),
    40_000,
  )
  assert.equal(offer.kind, 'no-number')
  assert.equal(preparationBudget(false, offer, '', 40_000), undefined)
})

test('an older compiler offers no Auto, and the artist’s number is the only way', () => {
  const outdated = answer({
    state: 'Outdated',
    detail: 'The installed Reference Asset Compiler is older than this: it has no budget command.',
    role: null,
    roleReason: null,
    sizeClass: null,
    longestMetres: null,
    triangleBudget: null,
    maximumP99Metres: null,
    maximumMaxMetres: null,
    ladder: null,
    summary: null,
  })
  const offer = autoOffer(outdated, 100_000)
  assert.equal(offer.kind, 'unavailable')
  assert.match(offer.kind === 'unavailable' ? offer.text : '', /older/)
  // No suggestion is turned into a number behind the artist's back ...
  assert.equal(preparationBudget(true, offer, '', 100_000), undefined)
  // ... and what they type is what is sent.
  assert.equal(preparationBudget(true, offer, '12000', 100_000), 12_000)
})

test('a request that failed outright is treated as no suggestion, not as a default', () => {
  const offer = autoOffer(null, 100_000)
  assert.equal(offer.kind, 'unavailable')
  assert.equal(preparationBudget(false, offer, '', 100_000), undefined)
})

test('a typed number replaces Auto only while the artist is choosing one', () => {
  const offer = autoOffer(answer(), 100_000)
  assert.equal(preparationBudget(true, offer, ' 7500 ', 100_000), 7_500)
  assert.equal(preparationBudget(false, offer, '7500', 100_000), null)
})

test('a typed number is held to what the reducer accepts', () => {
  assert.equal(manualBudgetProblem('', 100_000), 'Enter a triangle budget.')
  assert.match(manualBudgetProblem('2500.5', 100_000) ?? '', /whole number/)
  assert.match(manualBudgetProblem('999', 100_000) ?? '', /between/)
  assert.match(manualBudgetProblem('250000', 300_000) ?? '', /between/)
  assert.match(manualBudgetProblem('100000', 100_000) ?? '', /smaller/)
  assert.equal(manualBudgetProblem('99999', 100_000), null)
})

test('the Geometry panel shows the budget and its role beside the count', () => {
  const line = geometryBudget(answer({ role: 'hero', triangleBudget: 30_000 }), 78_000)
  assert.deepEqual(line, { text: `budget ${n(30_000)} (hero)`, over: true, budget: 30_000 })
})

test('under budget is not flagged', () => {
  const line = geometryBudget(answer(), 3_000)
  assert.equal(line?.over, false)
  assert.equal(line?.text, `budget ${n(5_000)} (prop)`)
})

test('exactly at the budget is within it', () => {
  assert.equal(geometryBudget(answer(), 5_000)?.over, false)
})

test('a character shows no reduction budget rather than a number', () => {
  const line = geometryBudget(answer({ role: 'character', triangleBudget: null }), 40_000)
  assert.deepEqual(line, { text: 'no reduction budget (character)', over: false, budget: null })
})

test('with no answer from the compiler the Geometry panel shows only the count', () => {
  assert.equal(geometryBudget(undefined, 40_000), null)
  assert.equal(geometryBudget(null, 40_000), null)
  assert.equal(geometryBudget(answer({ state: 'Refused', detail: 'No size.', triangleBudget: null }), 40_000), null)
})
