import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { existsSync, readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'

/**
 * Handing approved assets to a game, as the artist does it: from a collection
 * to the workstation's configured game folder, and from a picked selection as
 * a zip. What is written is read back from disk, not taken from a toast.
 */

const pixel = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='
const png = (label: string) => Buffer.concat([Buffer.from(pixel, 'base64'), Buffer.from(label)])

function failOnConsoleErrors(page: Page) {
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('pageerror', error => errors.push(error.message))
  return () => expect(errors, `Browser errors: ${errors.join('\n')}`).toEqual([])
}

async function importImage(request: APIRequestContext, name: string) {
  const response = await request.post('/api/assets/images', {
    headers: { 'X-Storyboard-Studio': '1' },
    multipart: { file: { name: `${name}.png`, mimeType: 'image/png', buffer: png(name) } },
  })
  expect(response.ok()).toBe(true)
  return await response.json() as { id: string; displayName: string }
}

async function file(request: APIRequestContext, assetId: string, name: string, collectionId?: string) {
  const response = await request.put(`/api/assets/${assetId}`, { data: { displayName: name, collectionId, tags: [], notes: '' } })
  expect(response.ok()).toBe(true)
}

async function approve(request: APIRequestContext, assetId: string) {
  const response = await request.post(`/api/assets/${assetId}/review`, { data: { decision: 'Approved', note: 'Ready.' } })
  expect(response.ok()).toBe(true)
}

async function openLibrary(page: Page) {
  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Asset library' })).toBeVisible()
}

test('a collection ships its approved assets to the game folder as a new bundle', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const run = `${testInfo.project.name}-${Date.now()}`
  const collectionName = `Ship props ${run}`
  const created = await page.request.post('/api/asset-collections', { data: { name: collectionName, color: '#73b7cf' } })
  expect(created.ok()).toBe(true)
  const collection = await created.json() as { id: string }
  const approved = await importImage(page.request, `ship-sign-${run}`)
  const pending = await importImage(page.request, `ship-draft-${run}`)
  await file(page.request, approved.id, `Tavern sign ${run}`, collection.id)
  await file(page.request, pending.id, `Draft banner ${run}`, collection.id)
  await approve(page.request, approved.id)

  await openLibrary(page)
  await page.getByRole('button', { name: new RegExp(collectionName) }).click()
  await page.getByTestId('collection-ship').click()
  const dialog = page.getByRole('dialog', { name: collectionName })

  // What goes and what stays, before anything is written.
  await expect(dialog.getByTestId('ship-summary')).toHaveText('1 will ship · 1 left out: 1 pending')
  await dialog.getByTestId('ship-include-pending').check()
  await expect(dialog.getByTestId('ship-summary')).toHaveText('2 will ship')
  await dialog.getByTestId('ship-include-pending').uncheck()
  await expect(dialog.getByTestId('ship-summary')).toHaveText('1 will ship · 1 left out: 1 pending')

  // The configured game folder is offered first.
  await expect(dialog.getByRole('radio', { name: /E2E game/ })).toBeChecked()
  await dialog.getByTestId('ship-confirm').click()
  await expect(dialog.getByTestId('ship-receipt')).toContainText('Earlier shipments are untouched')
  const folder = await dialog.getByTestId('ship-receipt-folder').innerText()

  // The bundle on disk is what was promised: the approved file and a manifest.
  const manifest = JSON.parse(readFileSync(join(folder, 'framewright-bundle.json'), 'utf8'))
  expect(manifest.schema).toBe('framewright.bundle.v1')
  expect(manifest.source.name).toBe(collectionName)
  expect(manifest.items).toHaveLength(1)
  expect(manifest.items[0].displayName).toBe(`Tavern sign ${run}`)
  expect(manifest.items[0].review.decision).toBe('Approved')
  expect(existsSync(join(folder, manifest.items[0].file))).toBe(true)
  expect(manifest.skipped).toHaveLength(1)
  expect(manifest.skipped[0].reason).toBe('Pending review')

  // Shipping again is a second folder beside the first, never on top of it.
  await dialog.getByRole('button', { name: 'Done' }).click()
  await page.getByTestId('collection-ship').click()
  const again = page.getByRole('dialog', { name: collectionName })
  await expect(again.getByTestId('ship-summary')).toHaveText('1 will ship · 1 left out: 1 pending')
  await again.getByTestId('ship-confirm').click()
  const second = await again.getByTestId('ship-receipt-folder').innerText()
  expect(second).not.toBe(folder)
  expect(existsSync(join(folder, 'framewright-bundle.json'))).toBe(true)
  const shipments = readdirSync(join(folder, '..')).filter(name => !name.startsWith('.'))
  expect(shipments.length).toBeGreaterThanOrEqual(2)
  verifyConsole()
})

test('picked assets download as a zip bundle', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const run = `${testInfo.project.name}-${Date.now()}`
  const one = await importImage(page.request, `zip-one-${run}`)
  const two = await importImage(page.request, `zip-two-${run}`)
  await approve(page.request, one.id)
  await approve(page.request, two.id)

  await openLibrary(page)
  await page.getByTestId('asset-select-mode').click()
  await expect(page.getByTestId('selection-ship')).toBeDisabled()
  await page.getByLabel(`Select ${one.displayName}`).check()
  await page.getByLabel(`Select ${two.displayName}`).check()
  await expect(page.getByTestId('asset-selection-bar')).toContainText('2 selected')
  await page.getByTestId('selection-ship').click()

  const dialog = page.getByRole('dialog', { name: '2 selected assets' })
  await expect(dialog.getByTestId('ship-summary')).toHaveText('2 will ship')
  await dialog.getByTestId('ship-destination-zip').check()
  const pending = page.waitForEvent('download')
  await dialog.getByTestId('ship-confirm').click()
  const download = await pending
  expect(download.suggestedFilename()).toMatch(/^selection-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}Z\.zip$/)
  const bytes = readFileSync(await download.path())
  // A zip (PK) whose directory names the manifest and both files.
  expect(bytes.subarray(0, 2).toString()).toBe('PK')
  expect(bytes.includes(Buffer.from('framewright-bundle.json'))).toBe(true)
  expect(bytes.includes(Buffer.from(`files/images/zip-one-${run.toLowerCase()}`))).toBe(true)
  await expect(dialog.getByTestId('ship-receipt')).toContainText('Bundled 2 assets')
  verifyConsole()
})
