import { expect, test, type Page } from '@playwright/test'
import { fileURLToPath } from 'node:url'

const block = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-block.glb', import.meta.url))
const post = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-post.glb', import.meta.url))

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
