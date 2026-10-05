import { expect, test, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

/**
 * Library review as the artist does it, on every kind of asset: approve or
 * send back with a reason from the panel the asset opens in, see the decision
 * on its card and filter by it, write notes attached the way that kind is
 * looked at, and see a new revision say which send-back it answers.
 */

const block = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-block.glb', import.meta.url))
const pixel = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='

function png(label: string) { return Buffer.concat([Buffer.from(pixel, 'base64'), Buffer.from(label)]) }

/** A per-run copy of a GLB fixture, so each journey owns its own model. */
function ownModel(label: string) {
  const bytes = readFileSync(block)
  const jsonLength = bytes.readUInt32LE(12)
  const document = JSON.parse(bytes.subarray(20, 20 + jsonLength).toString('utf8'))
  document.scenes[0].name = `${document.scenes[0].name} ${label}`
  let json = Buffer.from(JSON.stringify(document), 'utf8')
  if (json.length % 4 !== 0) json = Buffer.concat([json, Buffer.alloc(4 - (json.length % 4), 0x20)])
  const binary = bytes.subarray(20 + jsonLength)
  const header = Buffer.alloc(20)
  header.writeUInt32LE(0x46546c67, 0); header.writeUInt32LE(2, 4)
  header.writeUInt32LE(12 + 8 + json.length + binary.length, 8)
  header.writeUInt32LE(json.length, 12); header.writeUInt32LE(0x4e4f534a, 16)
  return Buffer.concat([header, json, binary])
}

/** Two seconds of quiet 8 kHz mono PCM: real audio both browsers can play and seek. */
function wav(label: string) {
  const samples = 16_000
  const buffer = Buffer.alloc(44 + samples * 2)
  buffer.write('RIFF', 0); buffer.writeUInt32LE(36 + samples * 2, 4); buffer.write('WAVE', 8); buffer.write('fmt ', 12)
  buffer.writeUInt32LE(16, 16); buffer.writeUInt16LE(1, 20); buffer.writeUInt16LE(1, 22); buffer.writeUInt32LE(8000, 24)
  buffer.writeUInt32LE(16000, 28); buffer.writeUInt16LE(2, 32); buffer.writeUInt16LE(16, 34)
  buffer.write('data', 36); buffer.writeUInt32LE(samples * 2, 40)
  // A few samples of the label keep each run's file distinct.
  Buffer.from(label).copy(buffer, 44)
  return buffer
}

function failOnConsoleErrors(page: Page) {
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('pageerror', error => errors.push(error.message))
  return () => expect(errors, `Browser errors: ${errors.join('\n')}`).toEqual([])
}

async function openLibrary(page: Page) {
  // A new model renders its library thumbnail, which headless WebKit cannot
  // do faithfully; that is a thumbnail matter, not a review one.
  await page.route('**/api/assets/*/poster', route =>
    route.request().method() === 'PUT' ? route.fulfill({ status: 204 }) : route.fallback())
  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Asset library' })).toBeVisible()
}

async function importInto(page: Page, file: { name: string; mimeType: string; buffer: Buffer }) {
  await page.locator('.asset-hero input[type="file"]').setInputFiles(file)
  await expect(page.getByText(/asset imported into the library/i)).toBeVisible()
}

test('an image is sent back with a reason, and its next revision says what it answers', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const name = `review-plate-${testInfo.project.name}-${Date.now()}`
  await openLibrary(page)
  await importInto(page, { name: `${name}.png`, mimeType: 'image/png', buffer: png(name) })
  await page.getByRole('button', { name: `Open ${name}` }).click()

  const panel = page.getByTestId('asset-review-decision')
  await expect(panel).toHaveAttribute('data-decision', 'Pending')
  await panel.getByTestId('asset-review-approve').click()
  await expect(panel).toHaveAttribute('data-decision', 'Approved')

  // A send-back waits for its reason.
  await panel.getByTestId('asset-review-send-back').click()
  await expect(panel.getByTestId('asset-review-send-back-confirm')).toBeDisabled()
  await panel.getByTestId('asset-review-reason').fill('Warmer light on the left pane.')
  // The library refresh that announces the send-back is held until the next
  // revision has been announced, so it lands last, as a slow one once did and
  // replaced "Imported v2" with the send-back.
  let releaseSendBack = () => {}
  const sendBackHeld = new Promise<void>(resolve => { releaseSendBack = resolve })
  await page.route('**/api/asset-placements', async route => { await sendBackHeld; await route.continue() }, { times: 1 })
  await panel.getByTestId('asset-review-send-back-confirm').click()
  await expect(panel).toHaveAttribute('data-decision', 'ChangesRequested')
  await expect(panel.getByTestId('asset-review-reason-shown')).toContainText('Warmer light on the left pane.')

  // The answer arrives as a new revision: pending, and saying what it answers.
  await page.getByLabel('Import image revision').setInputFiles({ name: `${name}-v2.png`, mimeType: 'image/png', buffer: png(`${name}-v2`) })
  await expect(page.getByText(/Imported v2/)).toBeVisible()
  // The older announcement, arriving late, does not replace the newer one.
  const sendBackRefreshed = page.waitForResponse('**/api/asset-placements')
  releaseSendBack()
  await (await sendBackRefreshed).finished()
  await page.waitForTimeout(500)
  await expect(page.locator('.toast.success')).toContainText('Imported v2')
  await expect(panel).toHaveAttribute('data-decision', 'Pending')
  await expect(panel.getByTestId('asset-review-answers')).toContainText('Warmer light on the left pane.')

  await page.locator('.image-revision-header').getByRole('button', { name: 'Assets', exact: true }).click()
  const card = page.locator('.asset-card').filter({ has: page.getByRole('button', { name: `Open ${name}` }) })
  await expect(card.getByTestId('asset-review-badge')).toHaveText('Answers send-back')
  verifyConsole()
})

test('a model is approved or sent back from its own panel, with a note from the view it was seen in', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const name = `review-block-${testInfo.project.name}-${Date.now()}`
  await openLibrary(page)
  await importInto(page, { name: `${name}.glb`, mimeType: 'model/gltf-binary', buffer: ownModel(name) })
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open ${name}` }).click()
  await expect(page.getByTestId('model-viewer')).toHaveAttribute('data-state', 'ready')

  // A note written from a view goes back to it.
  const notes = page.getByTestId('asset-notes')
  const stage = page.getByTestId('model-stage')
  await stage.focus(); await stage.press('ArrowRight'); await stage.press('ArrowRight')
  const orbitWhenWritten = await page.getByTestId('model-viewer-orbit').innerText()
  await notes.getByTestId('asset-note-text').click()
  await notes.getByTestId('asset-note-text').fill('The back panel tears at this angle.')
  await expect(notes.getByTestId('asset-note-attach')).toBeChecked()
  await notes.getByTestId('asset-note-add').click()
  const written = notes.getByTestId('asset-note').filter({ hasText: 'The back panel tears at this angle.' })
  await expect(written).toHaveAttribute('data-anchor', 'View')
  await expect(written).toContainText('From the view at yaw')
  await stage.focus(); await stage.press('ArrowLeft'); await stage.press('ArrowUp')
  await expect(page.getByTestId('model-viewer-orbit')).not.toHaveText(orbitWhenWritten)
  await written.getByRole('button', { name: /From the view/ }).click()
  await expect(page.getByTestId('model-viewer-orbit')).toHaveText(orbitWhenWritten)

  // A note about the whole model needs no view.
  await notes.getByTestId('asset-note-text').fill('Too glossy overall.')
  await notes.getByTestId('asset-note-attach').uncheck()
  await notes.getByTestId('asset-note-add').click()
  await expect(notes.getByTestId('asset-note').filter({ hasText: 'Too glossy overall.' })).toHaveAttribute('data-anchor', 'None')

  const panel = page.getByTestId('asset-review-decision')
  await panel.getByTestId('asset-review-send-back').click()
  await panel.getByTestId('asset-review-reason').fill('Improve the texture on the lid.')
  await panel.getByTestId('asset-review-send-back-confirm').click()
  await expect(panel).toHaveAttribute('data-decision', 'ChangesRequested')

  // The library shows it, and the review filter finds it.
  await page.getByRole('button', { name: 'Asset library' }).click()
  const card = page.locator('.asset-card').filter({ has: page.getByRole('button', { name: `Open ${name}` }) })
  await expect(card.getByTestId('asset-review-badge')).toHaveText('Changes requested')
  await page.getByTestId('asset-review-filter').selectOption('Approved')
  await expect(card).toHaveCount(0)
  await page.getByTestId('asset-review-filter').selectOption('ChangesRequested')
  await expect(card).toHaveCount(1)
  await page.getByTestId('asset-review-filter').selectOption('all')
  verifyConsole()
})

test('audio is approved from the library, with a note pinned to the moment that was playing', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const name = `review-cue-${testInfo.project.name}-${Date.now()}`
  await openLibrary(page)
  await importInto(page, { name: `${name}.wav`, mimeType: 'audio/wav', buffer: wav(name) })
  await page.getByRole('button', { name: `Open ${name}` }).click()

  const inspector = page.getByRole('complementary', { name: new RegExp(`${name} details`) })
  const player = inspector.getByTestId('asset-player')
  await expect.poll(() => player.evaluate(element => (element as HTMLMediaElement).duration)).toBeGreaterThan(1.9)
  await player.evaluate(element => { (element as HTMLMediaElement).currentTime = 1.5 })
  await expect.poll(() => player.evaluate(element => (element as HTMLMediaElement).currentTime)).toBeGreaterThan(1.4)

  const notes = inspector.getByTestId('asset-notes')
  await notes.getByTestId('asset-note-text').click()
  await notes.getByTestId('asset-note-text').fill('The swell clips here.')
  await notes.getByTestId('asset-note-add').click()
  const written = notes.getByTestId('asset-note').filter({ hasText: 'The swell clips here.' })
  await expect(written).toHaveAttribute('data-anchor', 'Time')
  await expect(written).toContainText('At 0:01.5')
  // Shown where it happens, along the timeline, and it goes back there.
  await expect(notes.getByTestId('asset-notes-timeline').getByRole('button')).toHaveCount(1)
  await player.evaluate(element => { (element as HTMLMediaElement).currentTime = 0 })
  await written.getByRole('button', { name: /At 0:01.5/ }).click()
  await expect.poll(() => player.evaluate(element => (element as HTMLMediaElement).currentTime)).toBeGreaterThan(1.4)

  const panel = inspector.getByTestId('asset-review-decision')
  await panel.getByTestId('asset-review-approve').click()
  await expect(panel).toHaveAttribute('data-decision', 'Approved')
  const card = page.locator('.asset-card').filter({ has: page.getByRole('button', { name: `Open ${name}` }) })
  await expect(card.getByTestId('asset-review-badge')).toHaveText('Approved')
  verifyConsole()
})
