import { expect, test, type Page } from '@playwright/test'

/**
 * A real-provider ComfyUI draft/revision/music canary, driven the way an artist
 * would drive those specific workflow legs.
 *
 * Unlike studio.spec.ts, this one DOES hit the real ComfyUI on 127.0.0.1:8188
 * and really renders. It proves this bounded path: author a shot and authority,
 * block it out, write a brief, render, revise, and place generated music. It is
 * not evidence for GPT Image, first/last-frame H3, voice, Max promotion,
 * ratification, restart recovery, or production export.
 *
 * It is excluded from the default gate because it needs a GPU and takes minutes.
 * Run it deliberately:
 *
 *   npx playwright test production-journey --project=desktop
 *
 * If ComfyUI is not reachable the test skips. A skipped canary is not release
 * evidence; the release matrix records which provider legs were actually run.
 */

const RUN = Date.now().toString().slice(-4)
const SHOT = `SH-9${RUN.slice(-2)}`
const AUTHORITY = `Journey Lantern ${RUN}`

test.describe.configure({ mode: 'serial' })
test.setTimeout(15 * 60 * 1000)

async function comfyReachable(page: Page) {
  const probe = await page.evaluate(async () => {
    try {
      const response = await fetch('/api/generation/adapters', { cache: 'no-store' })
      if (!response.ok) return { ok: false, why: `adapters returned ${response.status}` }
      console.info('ADAPTERS_ORIGIN', location.origin)
      const adapters = await response.json() as { id: string; canDispatch: boolean; state: string; detail: string }[]
      const target = adapters.find(x => x.id === 'comfyui-fast-draft')
      if (!target) return { ok: false, why: 'comfyui-fast-draft adapter is not installed' }
      return { ok: target.canDispatch === true, why: `${target.state}: ${target.detail}` }
    } catch (error) { return { ok: false, why: `probe threw: ${(error as Error).message}` } }
  })
  if (!probe.ok) console.log(`     probe says: ${probe.why}`)
  return probe.ok
}

/** Id of the shot's newest job, so a later wait can require a genuinely new one. */
async function latestJobId(page: Page, code: string): Promise<string | undefined> {
  return page.evaluate(async (shotCode) => {
    const snapshot = await (await fetch('/api/studio', { cache: 'no-store' })).json()
    return snapshot.jobs
      .filter((x: { shotCode: string }) => x.shotCode === shotCode)
      .sort((a: { createdAt: string }, b: { createdAt: string }) => b.createdAt.localeCompare(a.createdAt))[0]?.id
  }, code)
}

/**
 * Waits for a NEW job on this shot to reach a terminal state.
 *
 * `notJobId` matters: without it the helper sees the previous completed render
 * and returns instantly, so a second pass appears to succeed while nothing was
 * actually re-rendered. The first version of this test did exactly that.
 */
async function waitForRender(page: Page, code: string, notJobId?: string, minutes = 10) {
  const deadline = Date.now() + minutes * 60_000
  let last = ''
  while (Date.now() < deadline) {
    const job = await page.evaluate(async (shotCode) => {
      const snapshot = await (await fetch('/api/studio', { cache: 'no-store' })).json()
      return snapshot.jobs
        .filter((x: { shotCode: string }) => x.shotCode === shotCode)
        .sort((a: { createdAt: string }, b: { createdAt: string }) => b.createdAt.localeCompare(a.createdAt))[0] ?? null
    }, code)
    if (!job || job.id === notJobId) { await page.waitForTimeout(1_500); continue }
    if (job.phase !== last) { last = job.phase; console.log(`      · ${job.state} — ${job.phase}`) }
    if (job.state === 'Completed') return job
    if (job.state === 'Failed') throw new Error(`Render failed: ${job.error ?? 'no detail'}`)
    await page.waitForTimeout(2_000)
  }
  throw new Error('Render did not finish in time')
}

test('real ComfyUI canary renders and revises a draft, then places generated music', async ({ page }) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })

  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Shot board' })).toBeVisible()

  // ── 1. Establish the authority the shot will cite ──────────────────────
  console.log('  1. authoring the authority packet')
  await page.getByRole('tab', { name: /Authorities/ }).click()
  await page.getByRole('button', { name: 'Add authority' }).first().click()
  const authorityDialog = page.getByRole('dialog', { name: 'Add authority' })
  await authorityDialog.getByLabel('Name').fill(AUTHORITY)
  await authorityDialog.getByLabel('Category').selectOption('Prop')
  await authorityDialog.getByLabel('Identity and context').fill(
    'A hand-carried brass signal lantern with an amber lens and a worn leather strap.')
  await authorityDialog.getByLabel('Locked constraint').fill(
    'Exactly three brass ribs around the lens. The strap is always on the left shoulder.')
  await authorityDialog.getByRole('button', { name: 'Create authority' }).click()
  await expect(page.getByRole('button', { name: new RegExp(`Open ${AUTHORITY}`) })).toBeVisible()

  // ── 2. Author the shot ─────────────────────────────────────────────────
  console.log('  2. authoring the shot')
  await page.getByRole('tab', { name: /Shots/ }).click()
  await page.getByRole('button', { name: 'Add card' }).first().click()
  const shotDialog = page.getByRole('dialog', { name: 'Add shot card' })
  await shotDialog.getByLabel('Shot code').fill(SHOT)
  await shotDialog.getByLabel('Title').fill('Lantern at the gate')
  await shotDialog.getByLabel('Description').fill(
    'A lone figure stops at a tall stone gate at dusk, holding a lit signal lantern low at their side.')
  await shotDialog.getByLabel('Action').fill(
    'The figure halts, lifts the lantern just enough to read the gate, and holds.')
  await shotDialog.getByLabel('Camera').fill('Wide · eye level · 35 mm')
  await shotDialog.getByLabel(/Locked shot constraints/).fill(
    'Lantern stays in the right hand\nStrap remains on the left shoulder\nGate is stone, never timber')
  await shotDialog.getByRole('button', { name: 'Add card' }).click()
  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  await expect(page.locator('.canvas-caption').getByText(new RegExp(SHOT))).toBeVisible()

  // ── 3. Block the composition in the sketch lab ─────────────────────────
  console.log('  3. blocking the composition')
  await page.locator('.shot-toolbar-actions').getByRole('button', { name: 'Sketch' }).click()
  await expect(page.getByTestId('sketch-workspace')).toBeVisible()
  const stage = page.getByTestId('sketch-canvas')
  const box = await stage.boundingBox()
  expect(box).not.toBeNull()
  // A figure on the left third, and a gate rectangle on the right.
  const draw = async (points: [number, number][]) => {
    await page.mouse.move(box!.x + box!.width * points[0][0], box!.y + box!.height * points[0][1])
    await page.mouse.down()
    for (const [x, y] of points.slice(1)) await page.mouse.move(box!.x + box!.width * x, box!.y + box!.height * y, { steps: 8 })
    await page.mouse.up()
  }
  await draw([[0.28, 0.42], [0.28, 0.66]])                    // torso
  await draw([[0.28, 0.50], [0.34, 0.60]])                    // lantern arm
  await draw([[0.28, 0.66], [0.23, 0.82]])                    // left leg
  await draw([[0.28, 0.66], [0.33, 0.82]])                    // right leg
  await draw([[0.62, 0.28], [0.62, 0.80], [0.86, 0.80], [0.86, 0.28], [0.62, 0.28]]) // gate
  await expect(page.getByRole('button', { name: 'Undo sketch action' })).toBeEnabled()

  // ── 4. Write the brief in my own words ─────────────────────────────────
  console.log('  4. writing the creative brief')
  const brief = page.getByRole('textbox', { name: 'Creative brief' })
  await brief.fill(
    'Dusk exterior. A lone traveller halts at a tall stone gate, holding a lit brass signal lantern low in the right hand, '
    + 'leather strap over the left shoulder. Cold blue evening air against warm lantern light. Wet flagstones. '
    + 'Wide 35 mm at eye level, the gate filling the right of frame. Quiet, held, unhurried.')

  if (!(await comfyReachable(page))) {
    console.log('  ⚠ ComfyUI is not dispatchable — skipping the render half of the journey.')
    test.skip()
    return
  }

  // ── 5. One click freezes the manifest and renders for real ─────────────
  console.log('  5. generating directly in ComfyUI')
  await page.getByRole('button', { name: /Generate draft in ComfyUI/ }).click()

  const job = await waitForRender(page, SHOT)
  expect(job.state).toBe('Completed')
  console.log(`     rendered → ${job.outputAssetUrl}`)

  // ── 6. Judge it, then ask for a change ─────────────────────────────────
  console.log('  6. reviewing the candidate')
  await page.goto('/')
  await page.getByRole('button', { name: new RegExp(`Open ${SHOT}`) }).click()
  const reviewBar = page.getByTestId('candidate-review-bar')
  await expect(reviewBar).toBeVisible()
  await expect(reviewBar.locator('.review-chip')).not.toHaveCount(0)

  // Flip and peek — this must never navigate away from the shot.
  await page.keyboard.press('ArrowRight')
  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  await page.keyboard.down('\\')
  await page.keyboard.up('\\')
  await expect(page.getByTestId('shot-workspace')).toBeVisible()

  console.log('  7. sending a directed change back to ComfyUI')
  await reviewBar.getByRole('textbox', { name: 'What should change?' }).fill(
    'Raise the lantern higher so it lights the gate face, and turn the traveller a little more toward camera.')
  await reviewBar.getByRole('button', { name: /Send change/ }).click()
  const second = await waitForRender(page, SHOT, job.id)
  expect(second.state).toBe('Completed')
  expect(second.id).not.toBe(job.id)
  expect(second.outputAssetUrl).not.toBe(job.outputAssetUrl)
  console.log(`     re-rendered → ${second.outputAssetUrl}`)

  // ── 7. Score a music bed and place it ──────────────────────────────────
  console.log('  8. scoring the sequence')
  await page.getByRole('button', { name: 'Sequence', exact: true }).click()
  await page.getByRole('button', { name: 'Music library' }).click()
  const library = page.getByTestId('music-library')
  await expect(library).toBeVisible()
  await library.getByRole('button', { name: 'Aerie reveal' }).click()
  await library.getByRole('button', { name: /Generate track/ }).click()
  await expect(library.locator('.music-track').first()).toBeVisible({ timeout: 10 * 60 * 1000 })
  const placed = library.locator('.music-track').first()
  await placed.getByRole('button', { name: /Add to Music lane/ }).click()
  await expect(library).toBeHidden()

  const musicClips = await page.evaluate(async () => {
    const clips = await (await fetch('/api/timeline/clips')).json()
    return clips.filter((x: { track: string; assetId?: string }) => x.track === 'Music' && x.assetId).length
  })
  expect(musicClips).toBeGreaterThan(0)
  console.log(`     ${musicClips} music clip(s) on the lane with real audio`)

  expect(errors, `Browser errors:\n${errors.join('\n')}`).toEqual([])
  console.log('  ✔ journey complete')
})
