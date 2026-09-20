import { expect, test, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

const block = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-block.glb', import.meta.url))

function failOnConsoleErrors(page: Page, allowed: RegExp[] = []) {
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('pageerror', error => errors.push(error.message))
  return () => {
    const unexpected = errors.filter(message => !allowed.some(pattern => pattern.test(message)))
    expect(unexpected, `Browser errors: ${unexpected.join('\n')}`).toEqual([])
  }
}

/** A per-run copy of the fixture, so each browser project owns its own model. */
function ownFixture(source: string, label: string) {
  const bytes = readFileSync(source)
  const jsonLength = bytes.readUInt32LE(12)
  const document = JSON.parse(bytes.subarray(20, 20 + jsonLength).toString('utf8'))
  document.scenes[0].name = `${document.scenes[0].name} scene-${label}`
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

test('two instances of one model are placed, moved independently, and reopen after a reload', async ({ page }, testInfo) => {
  // Switching or removing objects remounts loaders, and an aborted model fetch
  // is reported by WebKit as a failed load. The viewport falls back to a
  // placeholder rather than showing the artist an error.
  const verifyConsole = failOnConsoleErrors(page, [/due to access control checks/, /TypeError: Load failed/])
  const label = testInfo.project.name
  const modelName = `scene-block-${label}`

  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.locator('input[type="file"]').setInputFiles({
    name: `${modelName}.glb`, mimeType: 'model/gltf-binary', buffer: ownFixture(block, label),
  })
  await expect(page.getByText('1 asset imported into the library.')).toBeVisible()

  await page.getByRole('button', { name: 'Scene', exact: true }).click()
  await expect(page.getByTestId('scene-workspace')).toBeVisible()
  await page.getByRole('button', { name: 'New scene' }).click()
  await expect(page.getByTestId('scene-version')).toContainText('Version 1')

  // Place the same model twice.
  const objects = page.getByTestId('scene-objects')
  await objects.getByLabel('Add model to scene').selectOption({ label: modelName })
  await objects.getByLabel('Add model to scene').selectOption({ label: modelName })
  await expect(objects.getByRole('button', { name: new RegExp(modelName) })).toHaveCount(2)

  // Move the second one only.
  const placement = page.getByTestId('scene-placement')
  await placement.getByLabel('Object name').fill('Right block')
  await placement.getByLabel('position X').fill('2.5')
  await placement.getByLabel('position Z').fill('1')
  await placement.getByLabel('scale X').fill('2')
  await placement.getByLabel('rotation Y').fill('0.8')

  await page.getByTestId('scene-save').click()
  await expect(page.getByTestId('scene-version')).toContainText('Version 2')
  await expect(page.getByTestId('scene-version')).toContainText('saved')

  // Reopening shows both objects with their own transforms, and one model
  // revision behind both of them.
  await page.reload()
  await page.getByRole('button', { name: 'Scene', exact: true }).click()
  await expect(page.getByTestId('scene-objects').getByRole('button', { name: /Right block/ })).toBeVisible()
  await page.getByTestId('scene-objects').getByRole('button', { name: /Right block/ }).click()
  const reopened = page.getByTestId('scene-placement')
  await expect(reopened.getByLabel('position X')).toHaveValue('2.5')
  await expect(reopened.getByLabel('scale X')).toHaveValue('2')

  const scenes = await (await page.request.get('/api/scenes')).json()
  const scene = await (await page.request.get(`/api/scenes/${scenes[0].id}`)).json()
  expect(scene.instances).toHaveLength(2)
  expect(scene.instances[0].assetId).toBe(scene.instances[1].assetId)
  expect(scene.instances[0].position).not.toEqual(scene.instances[1].position)
  expect(scene.camera.distance).toBeGreaterThan(0)
  await expect(page.getByTestId('scene-viewport')).toHaveAttribute('data-state', 'ready')

  // Both instances must really reach the scene graph, not just the API: an
  // object that never drew would otherwise pass every assertion above.
  await expect(page.getByTestId('scene-stage')).toHaveAttribute('data-objects', '2')
  await expect(page.getByTestId('scene-object-count')).toContainText('2 objects')

  // Framing pulls an object parked away from the origin back into view.
  await page.getByRole('button', { name: 'Frame all' }).click()
  const framed = await (await page.request.get(`/api/scenes/${scenes[0].id}`)).json()
  expect(framed.instances).toHaveLength(2)
  verifyConsole()
})

test('removing an object leaves the model in the library and a stale save is refused', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page, [/due to access control checks/, /TypeError: Load failed/, /409 \(Conflict\)/])
  const label = testInfo.project.name
  const modelName = `scene-removal-${label}`

  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.locator('input[type="file"]').setInputFiles({
    name: `${modelName}.glb`, mimeType: 'model/gltf-binary', buffer: ownFixture(block, `removal-${label}`),
  })
  await expect(page.getByText('1 asset imported into the library.')).toBeVisible()

  await page.getByRole('button', { name: 'Scene', exact: true }).click()
  await page.getByRole('button', { name: 'New scene' }).click()
  // Wait for the new scene to be the open one; adding a model before that
  // would edit the scene being replaced.
  await expect(page.getByTestId('scene-version')).toContainText('Version 1')
  await page.getByTestId('scene-objects').getByLabel('Add model to scene').selectOption({ label: modelName })
  await page.getByTestId('scene-save').click()
  await expect(page.getByTestId('scene-version')).toContainText('Version 2')

  const scenes = await (await page.request.get('/api/scenes')).json()
  const sceneId = scenes[0].id as string

  // Someone else saves the same scene while this view is open.
  const current = await (await page.request.get(`/api/scenes/${sceneId}`)).json()
  const elsewhere = await page.request.put(`/api/scenes/${sceneId}`, {
    data: { ...current, expectedVersion: current.version, instances: [] },
  })
  expect(elsewhere.ok()).toBe(true)

  // This view's save is refused rather than erasing that newer work.
  await page.getByTestId('scene-placement').getByLabel('position X').fill('3')
  await page.getByTestId('scene-save').click()
  await expect(page.getByTestId('scene-error')).toContainText(/now version/i)
  const afterConflict = await (await page.request.get(`/api/scenes/${sceneId}`)).json()
  expect(afterConflict.instances).toHaveLength(0)

  // The model itself is untouched by any of this.
  const assets = await (await page.request.get('/api/assets')).json()
  const model = assets.find((asset: { kind: string; displayName: string }) => asset.kind === 'Model' && asset.displayName === modelName)
  expect(model).toBeTruthy()
  expect(model.isArchived).toBe(false)
  const profile = await page.request.get(`/api/assets/${model.id}/model-profile`)
  expect(profile.ok()).toBe(true)
  verifyConsole()
})
