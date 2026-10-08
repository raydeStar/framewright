import { expect, test, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

/**
 * Scene setup kit (docs/goals/scene-setup-kit.md): blocking a scene out by hand
 * before anything is generated. Every journey here runs without a browser agent.
 */

const block = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-block.glb', import.meta.url))

type SceneInstance = {
  id: string; name: string; assetId: string | null; planId: string | null
  position: number[]; rotation: number[]; scale: number[]
  placeholder: { shape: string; size: number[] } | null
}
type Scene = { id: string; name: string; version: number; camera: { yaw: number; pitch: number; distance: number; target: number[]; fieldOfView: number }; environment: unknown; instances: SceneInstance[] }

test('stand-ins are blocked out by hand, resized, saved, reopened, and replaced with a library model', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page, [/due to access control checks/, /TypeError: Load failed/])
  const label = testInfo.project.name
  const modelName = `standin-replacement-${label}`

  await page.goto('/')
  // The replacement model is in the library before the scene opens, so the
  // inspector offers it.
  const imported = await page.request.post('/api/assets/models', {
    headers: { 'X-Storyboard-Studio': '1' },
    multipart: { file: { name: `${modelName}.glb`, mimeType: 'model/gltf-binary', buffer: ownFixture(block, modelName) } },
  })
  expect(imported.ok()).toBe(true)
  const model = await imported.json() as { id: string; displayName: string }

  const scene = await newScene(page)
  const objects = page.getByTestId('scene-objects')
  await objects.getByRole('button', { name: 'Add box stand-in' }).click()
  await objects.getByRole('button', { name: 'Add wall stand-in' }).click()
  await expect(objects.getByRole('button', { name: /Box 1/ })).toBeVisible()
  await expect(objects.getByRole('button', { name: /Wall 1/ })).toBeVisible()
  await expect(page.getByTestId('scene-stage')).toHaveAttribute('data-blockouts', '2')
  await expect(page.getByTestId('scene-save')).toBeEnabled()

  // The wall was added last, so it is the one selected.
  const placement = page.getByTestId('scene-placement')
  await expect(placement.getByLabel('Object name')).toHaveValue('Wall 1')
  await placement.getByLabel('Stand-in size width').fill('6')
  await placement.getByLabel('Stand-in size height').fill('3')

  await page.getByTestId('scene-save').click()
  await expect(page.getByTestId('scene-version')).toContainText('Version 2')

  // Reopen from storage: both stand-ins are still themselves, with no model
  // and no plan behind them, at the sizes the artist set.
  await page.reload()
  await page.getByRole('button', { name: 'Scene', exact: true }).click()
  await expect(page.getByLabel('Scene name', { exact: true })).toHaveValue(scene.name)
  await expect(page.getByTestId('scene-stage')).toHaveAttribute('data-blockouts', '2')
  const saved = await readScene(page, scene.id)
  const wall = saved.instances.find(instance => instance.name === 'Wall 1')!
  const box = saved.instances.find(instance => instance.name === 'Box 1')!
  expect(wall.assetId).toBeNull()
  expect(wall.planId).toBeNull()
  expect(wall.placeholder).toEqual({ shape: 'Box', size: [6, 3, 0.15] })
  expect(box.placeholder).toEqual({ shape: 'Box', size: [1, 1, 1] })
  expect(box.position[1]).toBe(0.5)

  // The service refuses a size it does not support, whatever the form allows,
  // and the stored scene stays exactly as it was.
  const refused = await page.request.put(`/api/scenes/${scene.id}`, {
    data: {
      expectedVersion: saved.version, name: saved.name, camera: saved.camera, environment: saved.environment,
      instances: saved.instances.map(instance => ({
        id: instance.id, assetId: instance.assetId, name: instance.name,
        position: instance.position, rotation: instance.rotation, scale: instance.scale,
        placeholder: instance.id === box.id ? { shape: 'Box', size: [0, 1, 1] } : instance.placeholder,
      })),
    },
  })
  expect(refused.status()).toBe(400)
  expect((await readScene(page, scene.id)).version).toBe(saved.version)

  // A hand-placed stand-in is replaced exactly like a planned one: same
  // object, same placement, a real model under it.
  await page.reload()
  await page.getByRole('button', { name: 'Scene', exact: true }).click()
  await page.getByTestId('scene-objects').getByRole('button', { name: /Box 1/ }).click()
  await page.getByTestId('scene-replacement').getByLabel('Replacement model').selectOption({ label: `${model.displayName} · v1` })
  await page.getByRole('button', { name: 'Replace this object' }).click()
  await page.getByTestId('scene-save').click()
  await expect(page.getByTestId('scene-version')).toContainText('Version 3')
  const replaced = await readScene(page, scene.id)
  const object = replaced.instances.find(instance => instance.id === box.id)!
  expect(object.assetId).toBe(model.id)
  expect(object.placeholder).toBeNull()
  expect(object.position).toEqual(box.position)
  expect(replaced.instances.find(instance => instance.id === wall.id)!.placeholder).toEqual(wall.placeholder)
  await expect(page.getByTestId('scene-stage')).toHaveAttribute('data-blockouts', '1')
  await page.screenshot({ path: testInfo.outputPath('scene-standins.png'), fullPage: true })
  verifyConsole()
})

async function newScene(page: Page) {
  await page.getByRole('button', { name: 'Scene', exact: true }).click()
  const createdResponse = page.waitForResponse(response => response.url().endsWith('/api/scenes') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'New scene' }).click()
  const created = await (await createdResponse).json() as Scene
  await expect(page.getByLabel('Scene name', { exact: true })).toHaveValue(created.name)
  await expect(page.getByTestId('scene-viewport')).toHaveAttribute('data-state', 'ready')
  return created
}

async function readScene(page: Page, sceneId: string) {
  return await (await page.request.get(`/api/scenes/${sceneId}`)).json() as Scene
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

/** A per-run copy of the fixture, so each browser project owns its own model. */
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
