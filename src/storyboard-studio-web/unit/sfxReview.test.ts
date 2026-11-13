import { test } from 'node:test'
import assert from 'node:assert/strict'
import { formatSeconds, listPosition, matchesSfxFilter, nextSfx, openingTake, rejectedSfxList, sfxCounts, sfxQueue, waveformPeaks } from '../src/sfxReview.ts'
import type { SfxSoundSummary, SfxTakeSummary } from '../src/types.ts'

const take = (sha256: string, overrides: Partial<SfxTakeSummary> = {}): SfxTakeSummary => ({
  sha256, runId: 'run', fileName: `SFX_${sha256}.wav`, takeNumber: 1, durationSeconds: 0.6, loop: false, prompt: '',
  audioUrl: `/api/sfx-review/takes/${sha256}/audio`, afterRejection: false, ...overrides,
})

const sound = (name: string, overrides: Partial<SfxSoundSummary> = {}): SfxSoundSummary => ({
  name, category: 'Player', prompt: `A ${name}.`, seconds: 1, loop: false, variations: 2, onList: true, generated: true,
  decision: 'Pending', storedDecision: 'Pending', note: '', answersNote: '', cueFileName: `SFX_${name}.wav`,
  takes: [take(`${name}-1`)], ...overrides,
})

test('filters separate waiting, decided and not-yet-generated sounds', () => {
  const unmade = sound('Wake', { generated: false, takes: [] })
  assert.equal(matchesSfxFilter(sound('Jump'), 'Pending'), true)
  assert.equal(matchesSfxFilter(unmade, 'Pending'), false)
  assert.equal(matchesSfxFilter(unmade, 'NotGenerated'), true)
  assert.equal(matchesSfxFilter(sound('Hit', { decision: 'ChangesRequested' }), 'ChangesRequested'), true)
  assert.equal(matchesSfxFilter(unmade, 'all'), true)
})

test('the walk keeps list order and what was decided in this sitting', () => {
  const sounds = [sound('Jump'), sound('Land', { decision: 'Approved' }), sound('Hit')]
  assert.deepEqual(sfxQueue(sounds, 'Pending').map(item => item.name), ['Jump', 'Hit'])
  assert.deepEqual(sfxQueue(sounds, 'Pending', new Set(['Land'])).map(item => item.name), ['Jump', 'Land', 'Hit'])
})

test('a decision moves on to the next sound still waiting, wrapping round', () => {
  const queue = [sound('A'), sound('B', { decision: 'Approved' }), sound('C')]
  assert.equal(nextSfx(queue, 'A', 'Pending'), 'C')
  assert.equal(nextSfx(queue, 'C', 'Pending'), 'A')
  assert.equal(nextSfx(queue, 'B', 'all'), 'C')
  assert.equal(nextSfx(queue, 'C', 'all'), undefined)
  assert.equal(nextSfx([sound('Only', { decision: 'Approved' })], 'Only', 'Pending'), undefined)
})

test('counts say accepted, rejected, pending and not generated out of the total', () => {
  assert.deepEqual(sfxCounts([
    sound('A'), sound('B', { decision: 'Approved' }), sound('C', { decision: 'ChangesRequested' }), sound('D', { generated: false, takes: [] }),
  ]), { total: 4, pending: 1, approved: 1, changesRequested: 1, notGenerated: 1 })
})

test('the first take heard is the accepted one, else the answer to a rejection', () => {
  const takes = [take('t1'), take('t2', { afterRejection: true }), take('t3')]
  assert.equal(openingTake(sound('A', { takes }))?.sha256, 't2')
  assert.equal(openingTake(sound('A', { takes, acceptedTakeSha256: 't3' }))?.sha256, 't3')
  assert.equal(openingTake(sound('A', { takes: [take('t1'), take('t2')] }))?.sha256, 't1')
  assert.equal(openingTake(sound('A', { takes: [] })), undefined)
})

test('the rejection list is one plain line per rejected sound', () => {
  assert.equal(rejectedSfxList([
    sound('Hit', { decision: 'ChangesRequested', category: 'Combat', note: 'Too thin;\nmore clank.' }),
    sound('Jump', { decision: 'Approved' }),
    sound('Wake', { decision: 'ChangesRequested', category: 'World' }),
  ]), 'Hit (Combat) — Too thin; more clank.\nWake (World) — no note')
})

test('a waveform outline is the loudest sample per slice', () => {
  assert.deepEqual(waveformPeaks(new Float32Array([0.25, -0.5, 0.25, 0.75, -1.5, 0]), 3), [0.5, 0.75, 1])
  assert.equal(waveformPeaks(new Float32Array(10), 4).length, 4)
  assert.equal(formatSeconds(0.6), '0.6 s')
  assert.equal(formatSeconds(19.5), '19.5 s')
  assert.equal(formatSeconds(undefined), '—')
})

test('a sound shows where it sits on the list, which is priority order', () => {
  const sounds = [sound('Jump'), sound('Stray', { onList: false }), sound('Hit'), sound('Land')]
  assert.deepEqual(listPosition(sounds, 'Hit'), { position: 2, total: 3 })
  assert.equal(listPosition(sounds, 'Stray'), undefined)
})
