import { expect, test, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const block = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-block.glb', import.meta.url))
const post = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-post.glb', import.meta.url))

/**
 * A per-run copy of a fixture. Both browser projects share one database, so a
 * journey that renames, revises, and archives a model has to own its own model
 * rather than inherit whatever the previous project left behind. Renaming the
 * scene changes the content hash, which is what makes it a distinct asset.
 */
function ownFixture(source: string, label: string) {
  const bytes = readFileSync(source)
  const jsonLength = bytes.readUInt32LE(12)
  const document = JSON.parse(bytes.subarray(20, 20 + jsonLength).toString('utf8'))
  document.scenes[0].name = `${document.scenes[0].name} ${label}`
  let json = Buffer.from(JSON.stringify(document), 'utf8')
  if (json.length % 4 !== 0) json = Buffer.concat([json, Buffer.alloc(4 - (json.length % 4), 0x20)])
  const binary = bytes.subarray(20 + jsonLength)
  const header = Buffer.alloc(20)
  header.writeUInt32LE(0x46546c67, 0)
  header.writeUInt32LE(2, 4)
  header.writeUInt32LE(12 + 8 + json.length + binary.length, 8)
  header.writeUInt32LE(json.length, 12)
  header.writeUInt32LE(0x4e4f534a, 16)
  return Buffer.concat([header, json, binary])
}

function failOnConsoleErrors(page: Page, allowed: RegExp[] = []) {
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('pageerror', error => errors.push(error.message))
  return () => {
    const unexpected = errors.filter(message => !allowed.some(pattern => pattern.test(message)))
    expect(unexpected, `Browser errors: ${unexpected.join('\n')}`).toEqual([])
  }
}

async function openAssets(page: Page) {
  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Asset library' })).toBeVisible()
}

test('a supported model imports, reports its real measurements, and inspects without moving', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await openAssets(page)

  // Both browser projects share one database, so this asserts the model is
  // there rather than how many models the run before it left behind.
  await page.locator('input[type="file"]').setInputFiles(block)
  await page.getByRole('button', { name: /^Models/ }).click()
  await expect(page.getByRole('button', { name: 'Open asymmetric-block' })).toBeVisible()
  await page.getByRole('button', { name: 'Open asymmetric-block' }).click()
  await expect(page.getByTestId('model-workspace')).toBeVisible()

  // The numbers come from the stored bytes, and they are the fixture's real
  // ones: a mirrored or transform-ignoring import would not produce these.
  const dimensions = page.getByTestId('model-dimensions')
  await expect(dimensions).toContainText('2.00 m')
  await expect(dimensions).toContainText('1.00 m')
  await expect(dimensions).toContainText('0.75 m')
  await expect(dimensions).toContainText('min (0.00, 0.00, 0.00)')
  await expect(dimensions).toContainText('max (2.00, 1.00, 0.75)')

  const statistics = page.getByTestId('model-statistics')
  await expect(statistics).toContainText('16')
  await expect(statistics).toContainText('24')
  const materials = page.getByTestId('model-materials')
  await expect(materials).toContainText('Block body')
  await expect(materials).toContainText('Corner tab')
  await expect(page.getByTestId('model-support')).toContainText('self-contained GLB')

  // The 3D view really opens, and orbiting moves the inspection camera only.
  await expect(page.getByTestId('model-viewer')).toHaveAttribute('data-state', 'ready')
  await expect(page.locator('.model-stage canvas')).toHaveCount(1)
  const openingOrbit = await page.getByTestId('model-viewer-orbit').innerText()
  const stage = page.getByTestId('model-stage')
  await stage.focus()
  await stage.press('ArrowRight')
  await stage.press('ArrowUp')
  await expect(page.getByTestId('model-viewer-orbit')).not.toHaveText(openingOrbit)

  const profile = await (await page.request.get(`/api/assets/${await currentAssetId(page)}/model-profile`)).json()
  expect(profile.dimensions).toEqual([2, 1, 0.75])
  expect(profile.boundsMin).toEqual([0, 0, 0])

  // The geometry the renderer loaded has to agree with the geometry the service
  // measured, axis for axis. A swapped or flipped axis would show up here even
  // though the picture would still look like a plausible block.
  await expect(stage).toHaveAttribute('data-loaded-min', '0,0,0')
  await expect(stage).toHaveAttribute('data-loaded-max', '2,1,0.75')

  await page.getByRole('button', { name: 'Reset view' }).click()
  await expect(page.getByTestId('model-viewer-orbit')).toHaveText(openingOrbit)
  verifyConsole()
})

test('switching models releases the previous view instead of stacking contexts', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await openAssets(page)
  await page.locator('input[type="file"]').setInputFiles([block, post])
  await page.getByRole('button', { name: /^Models/ }).click()
  await expect(page.getByRole('button', { name: 'Open asymmetric-post' })).toBeVisible()

  await page.getByRole('button', { name: 'Open asymmetric-block' }).click()
  await expect(page.getByTestId('model-dimensions')).toContainText('2.00 m')
  await expect(page.locator('canvas')).toHaveCount(1)

  await page.getByRole('button', { name: 'Asset library' }).click()
  await expect(page.locator('canvas')).toHaveCount(0)

  // A second model with different extents: the surface shows the new numbers
  // and still holds exactly one drawing context.
  await page.getByRole('button', { name: 'Open asymmetric-post' }).click()
  await expect(page.getByTestId('model-dimensions')).toContainText('2.00 m')
  await expect(page.getByTestId('model-dimensions')).toContainText('0.45 m')
  await expect(page.getByTestId('model-viewer')).toHaveAttribute('data-state', 'ready')
  await expect(page.locator('canvas')).toHaveCount(1)
  verifyConsole()
})

test('an unsupported model is refused with a reason and leaves nothing importable', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page, [/Failed to load resource/])
  await openAssets(page)

  const before = (await (await page.request.get('/api/assets')).json()).filter((asset: { kind: string }) => asset.kind === 'Model').length
  await page.locator('input[type="file"]').setInputFiles({
    name: 'broken.glb', mimeType: 'model/gltf-binary', buffer: Buffer.from('These are ordinary notes that someone renamed to .glb by mistake.'),
  })
  await expect(page.getByRole('alert')).toContainText(/not a GLB container/i)

  const after = (await (await page.request.get('/api/assets')).json()).filter((asset: { kind: string }) => asset.kind === 'Model').length
  expect(after).toBe(before)
  verifyConsole()
})

async function currentAssetId(page: Page) {
  const assets = await (await page.request.get('/api/assets')).json()
  return assets.find((asset: { kind: string; displayName: string }) => asset.kind === 'Model' && asset.displayName === 'asymmetric-block').id as string
}

test('a model is a reusable library record with revisions that never overwrite each other', async ({ page }, testInfo) => {
  // Switching revisions remounts the viewer, which aborts any GLB fetch still
  // in flight. WebKit reports that abort as a failed load. The viewer already
  // ignores aborted loads rather than showing the artist an error, so this is
  // browser noise about a cancelled request, not a real failure.
  const verifyConsole = failOnConsoleErrors(page, [/due to access control checks/, /TypeError: Load failed/])
  const label = testInfo.project.name
  const name = `Chain court block ${label}`
  const fileName = `library-block-${label}.glb`
  await openAssets(page)

  await page.locator('input[type="file"]').setInputFiles({
    name: fileName, mimeType: 'model/gltf-binary', buffer: ownFixture(block, label),
  })
  // Wait for the import itself rather than for a card to appear, so a refused
  // or dropped upload fails here with its own reason instead of as a silent
  // missing-card timeout later.
  await expect(page.getByText('1 asset imported into the library.')).toBeVisible()
  // Separate "the service never stored it" from "the library list did not show
  // it", so a failure here names which half is at fault.
  const stored = await (await page.request.get('/api/assets')).json()
  const mine = stored.filter((item: { kind: string; displayName: string }) => item.kind === 'Model' && item.displayName === `library-block-${label}`)
  expect(mine, `service holds ${stored.filter((i: { kind: string }) => i.kind === 'Model').length} models`).toHaveLength(1)
  expect(mine[0].isArchived, 'a freshly imported model must not arrive archived').toBe(false)
  expect(mine[0].revisionFamilyId ?? null, 'a freshly imported model must not arrive inside a revision family').toBeNull()
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open library-block-${label}` }).click()
  await expect(page.getByTestId('model-workspace')).toBeVisible()

  // Organisation is editable without touching the measured facts.
  const library = page.getByTestId('model-library')
  await library.getByLabel('Name').fill(name)
  await library.getByLabel(/^Tags/).fill('set-dressing, chain-court')
  await library.getByLabel('Notes').fill('Blocking stand-in for the gate approach.')
  await library.getByRole('button', { name: 'Save details' }).click()
  await expect(page.getByRole('heading', { name })).toBeVisible()
  await expect(page.getByTestId('model-dimensions')).toContainText('2.00 m')

  // A second model becomes revision 2; revision 1 keeps its own geometry.
  const revisions = page.getByTestId('model-revisions')
  await revisions.locator('input[type="file"]').setInputFiles({
    name: `library-post-${label}.glb`, mimeType: 'model/gltf-binary', buffer: ownFixture(post, label),
  })
  await expect(revisions.getByRole('button', { name: /^v2/ })).toBeVisible()
  await expect(page.getByTestId('model-dimensions')).toContainText('0.45 m')

  await revisions.getByRole('button', { name: /^v1/ }).click()
  await expect(page.getByTestId('model-dimensions')).toContainText('2.00 m')
  await expect(page.getByTestId('model-dimensions')).toContainText('max (2.00, 1.00, 0.75)')
  await expect(page.getByTestId('model-viewer')).toHaveAttribute('data-state', 'ready')

  // Selecting the earlier revision is an ordinary, reversible choice.
  await revisions.getByRole('button', { name: 'Make current' }).click()
  await expect(revisions.getByRole('button', { name: /^v1 Current/ })).toBeVisible()

  // Reopening the workspace shows exactly what was saved.
  await page.reload()
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open ${name}` }).click()
  await expect(page.getByTestId('model-revisions').getByRole('button', { name: /^v2/ })).toBeVisible()
  await expect(page.getByTestId('model-library').getByLabel(/^Tags/)).toHaveValue(/chain-court/)
  await expect(page.getByTestId('model-dimensions')).toContainText('2.00 m')

  // Archiving hides it without breaking anything that cites it.
  const assets = await (await page.request.get('/api/assets')).json()
  const archivedId = assets.find((asset: { kind: string; displayName: string; revisionNumber?: number }) =>
    asset.kind === 'Model' && asset.displayName === name && asset.revisionNumber === 1).id as string
  await page.getByTestId('model-library').getByRole('button', { name: 'Archive model' }).click()
  await expect(page.getByTestId('model-library').getByRole('button', { name: 'Restore to library' })).toBeVisible()
  const stillThere = await page.request.get(`/api/assets/${archivedId}/model-profile`)
  expect(stillThere.ok()).toBe(true)
  expect((await stillThere.json()).dimensions).toEqual([2, 1, 0.75])

  // Leave the library as it was found. Both browser projects share one
  // database, so a journey that archives something has to put it back.
  await page.getByTestId('model-library').getByRole('button', { name: 'Restore to library' }).click()
  await expect(page.getByTestId('model-library').getByRole('button', { name: 'Archive model' })).toBeVisible()
  verifyConsole()
})
