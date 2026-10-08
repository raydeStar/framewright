import { expect, test, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { deflateSync } from 'node:zlib'

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

test('handles in the view move, turn, and scale only the selected object, and orbiting moves no object', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.goto('/')
  const scene = await newScene(page)
  const objects = page.getByTestId('scene-objects')
  await objects.getByRole('button', { name: 'Add box stand-in' }).click()
  await objects.getByRole('button', { name: 'Add cylinder stand-in' }).click()
  await page.getByTestId('scene-save').click()
  await expect(page.getByTestId('scene-version')).toContainText('Version 2')
  const before = await readScene(page, scene.id)
  const cylinder = before.instances.find(instance => instance.name === 'Cylinder 1')!

  const stage = page.getByTestId('scene-stage')
  const placement = page.getByTestId('scene-placement')
  const field = (label: string) => placement.getByLabel(label, { exact: true })
  await objects.getByRole('button', { name: /Box 1/ }).click()
  const handles = page.getByRole('group', { name: 'Object handles' })
  await expect(handles.getByRole('button', { name: 'Move' })).toHaveAttribute('aria-pressed', 'true')

  // Grab the handle's centre and drag it, as an artist would.
  const dragHandle = async (dx: number, dy: number) => {
    await expect(stage).toHaveAttribute('data-handle', /\[/)
    const [x, y] = JSON.parse((await stage.getAttribute('data-handle'))!) as [number, number]
    const box = (await stage.boundingBox())!
    await page.mouse.move(box.x + x, box.y + y)
    await page.mouse.down()
    for (let step = 1; step <= 6; step += 1) await page.mouse.move(box.x + x + dx * step / 6, box.y + y + dy * step / 6)
    await page.mouse.up()
  }

  await dragHandle(70, -30)
  await expect(field('position X')).not.toHaveValue('0')
  await expect(page.getByTestId('scene-save')).toBeEnabled()
  const moved = [await field('position X').inputValue(), await field('position Y').inputValue(), await field('position Z').inputValue()].map(Number)

  await handles.getByRole('button', { name: 'Rotate' }).click()
  await dragHandle(40, 40)
  await expect.poll(async () => [await field('rotation X').inputValue(), await field('rotation Y').inputValue(), await field('rotation Z').inputValue()]
    .some(value => Number(value) !== 0)).toBe(true)

  await handles.getByRole('button', { name: 'Scale' }).click()
  await dragHandle(50, -50)
  await expect.poll(async () => Number(await field('scale X').inputValue())).not.toBe(1)
  const scaled = Number(await field('scale X').inputValue())
  // A 100 pixel drag up and to the right from the centre roughly doubles the
  // object, uniformly. It must not leap: the stock handle went to 50 times.
  expect(scaled).toBeGreaterThan(1.5)
  expect(scaled).toBeLessThan(3)
  expect(Number(await field('scale Y').inputValue())).toBe(scaled)
  expect(Number(await field('scale Z').inputValue())).toBe(scaled)

  // Lift it, then drop it: its lowest point lands on the floor, wherever the
  // turn and the new size put that point.
  await handles.getByRole('button', { name: 'Move' }).click()
  await field('position Y').fill('3')
  await handles.getByRole('button', { name: 'Drop to floor' }).click()
  await expect.poll(async () => Number(await field('position Y').inputValue())).toBeLessThan(3)
  const dropped = Number(await field('position Y').inputValue())
  // Half the scaled cube's height is the least it can rest at; tilted, more.
  expect(dropped).toBeGreaterThanOrEqual(scaled / 2 - 0.002)
  expect(dropped).toBeLessThanOrEqual(scaled * Math.sqrt(3) / 2 + 0.002)

  // An upright cylinder lands exactly on its base.
  await objects.getByRole('button', { name: /Cylinder 1/ }).click()
  await field('position Y').fill('4')
  await handles.getByRole('button', { name: 'Drop to floor' }).click()
  await expect(field('position Y')).toHaveValue('0.6')
  await field('position Y').fill(String(cylinder.position[1]))
  await objects.getByRole('button', { name: /Box 1/ }).click()

  // Dragging empty space orbits the inspection camera and moves no object.
  const workingBefore = [await field('position X').inputValue(), await field('position Z').inputValue()]
  const box = (await stage.boundingBox())!
  await page.mouse.move(box.x + 24, box.y + box.height - 24)
  await page.mouse.down()
  await page.mouse.move(box.x + 140, box.y + box.height - 60, { steps: 6 })
  await page.mouse.up()
  expect([await field('position X').inputValue(), await field('position Z').inputValue()]).toEqual(workingBefore)

  await page.getByTestId('scene-save').click()
  await expect(page.getByTestId('scene-version')).toContainText('Version 3')
  const after = await readScene(page, scene.id)
  const edited = after.instances.find(instance => instance.name === 'Box 1')!
  expect(edited.position[0]).toBe(moved[0])
  expect(edited.position[1]).toBe(dropped)
  expect(edited.rotation.some(value => value !== 0)).toBe(true)
  expect(edited.scale[0]).toBe(scaled)
  // The other object, and nothing but the camera, is as it was.
  expect(after.instances.find(instance => instance.id === cylinder.id)).toEqual(cylinder)
  expect(after.camera).not.toEqual(before.camera)

  await page.reload()
  await page.getByRole('button', { name: 'Scene', exact: true }).click()
  await page.getByTestId('scene-objects').getByRole('button', { name: /Box 1/ }).click()
  await expect(field('position Y')).toHaveValue(String(dropped))
  await expect(field('scale X')).toHaveValue(String(scaled))
  // The toolbar sits below the stage and inside its column at every size,
  // never over the view it controls or over the inspector.
  const [shell, view, bar] = await page.evaluate(() => ['.scene-stage-shell', '.scene-stage', '.scene-viewport-bar']
    .map(selector => { const rect = document.querySelector(selector)!.getBoundingClientRect(); return { top: rect.top, bottom: rect.bottom } }))
  expect(bar.top).toBeGreaterThanOrEqual(view.bottom)
  expect(bar.bottom).toBeLessThanOrEqual(shell.bottom)
  // Director Mode still hands the stage the screen.
  await page.getByRole('button', { name: 'Director Mode' }).click()
  const filled = await page.evaluate(() => document.querySelector('.scene-stage')!.getBoundingClientRect().height / window.innerHeight)
  expect(filled).toBeGreaterThan(0.6)
  // The full-screen canvas really draws the scene: its pixels hold the
  // scene background, not an empty buffer. (WebKit's screenshot of a canvas
  // this size can come back black even when the buffer is right, so the
  // buffer is what is checked.)
  await expect.poll(() => page.evaluate(() => {
    const canvas = document.querySelector('.scene-stage canvas') as HTMLCanvasElement
    const copy = document.createElement('canvas'); copy.width = canvas.width; copy.height = canvas.height
    const context = copy.getContext('2d')!; context.drawImage(canvas, 0, 0)
    return [...context.getImageData(Math.floor(canvas.width * 0.04), Math.floor(canvas.height * 0.04), 1, 1).data]
  })).toEqual([23, 27, 25, 255])
  await page.screenshot({ path: testInfo.outputPath('scene-handles-director.png') })
  await page.getByRole('button', { name: 'Exit Director' }).click()
  await page.screenshot({ path: testInfo.outputPath('scene-handles.png'), fullPage: true })
  verifyConsole()
})

test('looking through the shot camera frames the delivery picture and moves only the shot camera', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.goto('/')
  const studio = await (await page.request.get('/api/studio')).json() as {
    shots: { id: string; code: string; version: number }[]
    project: { deliveryWidth: number; deliveryHeight: number }
  }
  const targetShot = studio.shots[0]
  const aspect = studio.project.deliveryWidth / studio.project.deliveryHeight

  const scene = await newScene(page)
  await page.getByTestId('scene-objects').getByRole('button', { name: 'Add box stand-in' }).click()
  await page.getByTestId('scene-save').click()
  await expect(page.getByTestId('scene-version')).toContainText('Version 2')
  const saved = await readScene(page, scene.id)

  const shotSetup = page.getByTestId('scene-shot')
  await shotSetup.getByLabel('Scene shot').selectOption(targetShot.id)
  const stage = page.getByTestId('scene-stage')
  await expect(stage).toHaveAttribute('data-view', 'inspection')
  await expect(page.getByTestId('scene-shot-guide')).toHaveCount(0)
  const yawBefore = await shotSetup.getByLabel('Shot camera yaw').inputValue()

  await shotSetup.getByTestId('scene-look-through').click()
  await expect(stage).toHaveAttribute('data-view', 'shot')
  const guide = page.getByTestId('scene-shot-guide')
  await expect(guide).toBeVisible()
  await expect(guide).toContainText(`${targetShot.code} camera`)

  // The frame has the delivery shape and fits the stage on its long side.
  const frame = (await guide.boundingBox())!
  const view = (await stage.boundingBox())!
  expect(Math.abs(frame.width / frame.height - aspect)).toBeLessThan(0.02)
  if (aspect >= view.width / view.height) expect(Math.abs(frame.width - (view.width - 2))).toBeLessThan(2)
  else expect(Math.abs(frame.height - (view.height - 2))).toBeLessThan(2)
  expect(frame.x).toBeGreaterThanOrEqual(view.x)
  expect(frame.x + frame.width).toBeLessThanOrEqual(view.x + view.width + 0.5)

  // Orbiting here moves the shot camera only. The saved scene, inspection
  // camera included, is untouched and has nothing to save.
  await page.mouse.move(frame.x + 20, frame.y + 20)
  await page.mouse.down()
  await page.mouse.move(frame.x + 140, frame.y + 50, { steps: 6 })
  await page.mouse.up()
  await expect(shotSetup.getByLabel('Shot camera yaw')).not.toHaveValue(yawBefore)
  await expect(page.getByTestId('scene-save')).toBeDisabled()
  expect((await readScene(page, scene.id)).camera).toEqual(saved.camera)
  // Fields show the camera at their own precision, not floating-point noise.
  expect(await shotSetup.getByLabel('Shot camera yaw').inputValue()).toMatch(/^-?\d+(\.\d{1,4})?$/)
  const framedYaw = Number(await shotSetup.getByLabel('Shot camera yaw').inputValue())
  const framedPitch = Number(await shotSetup.getByLabel('Shot camera pitch').inputValue())
  await page.screenshot({ path: testInfo.outputPath('scene-look-through.png'), fullPage: true })

  await shotSetup.getByTestId('scene-look-through').click()
  await expect(stage).toHaveAttribute('data-view', 'inspection')
  await expect(page.getByTestId('scene-shot-guide')).toHaveCount(0)
  await expect(page.getByTestId('scene-save')).toBeDisabled()

  // The still is taken with the camera the artist framed.
  await shotSetup.getByRole('button', { name: 'Render still for review' }).click()
  await expect(page.getByTestId('review-workspace')).toBeVisible()
  const bindings = await (await page.request.get(`/api/scenes/${scene.id}/shot-stills`)).json() as { shotId: string; camera: { yaw: number; pitch: number } }[]
  const binding = bindings.find(item => item.shotId === targetShot.id)!
  expect(binding.camera.yaw).toBeCloseTo(framedYaw, 4)
  expect(binding.camera.pitch).toBeCloseTo(framedPitch, 4)
  verifyConsole()
})

test('a person stand-in takes every pose on the floor, keeps its pose and height, and reopens', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.goto('/')
  const scene = await newScene(page)
  const objects = page.getByTestId('scene-objects')
  const placement = page.getByTestId('scene-placement')
  const handles = page.getByRole('group', { name: 'Object handles' })
  await objects.getByRole('button', { name: 'Add person stand-in' }).click()
  await expect(placement.getByLabel('Object name')).toHaveValue('Person 1')
  await expect(placement.getByLabel('Stand-in pose')).toHaveValue('Neutral')
  await expect(page.getByTestId('scene-stage')).toHaveAttribute('data-blockouts', '1')

  // Standing, seated, kneeling, or lying, the figure's lowest point is on the
  // floor at the same resting height, so dropping it moves nothing.
  const poses = await placement.getByLabel('Stand-in pose').locator('option').allTextContents()
  expect(poses).toEqual(['Neutral', 'Walking', 'Running', 'Seated', 'Kneeling', 'Pointing', 'Reaching', 'Conversation', 'Prone'])
  for (const pose of poses) {
    await placement.getByLabel('Stand-in pose').selectOption(pose)
    await placement.getByLabel('position Y', { exact: true }).fill('2')
    await handles.getByRole('button', { name: 'Drop to floor' }).click()
    await expect(placement.getByLabel('position Y', { exact: true }), pose).toHaveValue('0.875')
  }

  await placement.getByLabel('Stand-in pose').selectOption('Seated')
  await placement.getByLabel('Stand-in size height').fill('1.6')
  await objects.getByRole('button', { name: 'Add person stand-in' }).click()
  await placement.getByLabel('Stand-in pose').selectOption('Walking')
  await placement.getByLabel('position X', { exact: true }).fill('1.2')
  await objects.getByRole('button', { name: 'Add person stand-in' }).click()
  await placement.getByLabel('Stand-in pose').selectOption('Pointing')
  await placement.getByLabel('position X', { exact: true }).fill('-1.2')
  await page.getByTestId('scene-save').click()
  await expect(page.getByTestId('scene-version')).toContainText('Version 2')

  await page.reload()
  await page.getByRole('button', { name: 'Scene', exact: true }).click()
  await expect(page.getByLabel('Scene name', { exact: true })).toHaveValue(scene.name)
  await expect(page.getByTestId('scene-stage')).toHaveAttribute('data-blockouts', '3')
  const saved = await readScene(page, scene.id)
  const person = saved.instances.find(instance => instance.name === 'Person 1')!
  expect(person.placeholder).toEqual({ shape: 'Person', size: [0.5, 1.6, 0.3], pose: 'Seated' })
  expect(saved.instances.map(instance => (instance.placeholder as { pose?: string }).pose)).toEqual(['Seated', 'Walking', 'Pointing'])
  await page.getByTestId('scene-objects').getByRole('button', { name: /Person 1/ }).click()
  await expect(page.getByTestId('scene-placement').getByLabel('Stand-in pose')).toHaveValue('Seated')
  await page.getByRole('button', { name: 'Frame all' }).click()
  await page.screenshot({ path: testInfo.outputPath('scene-people.png'), fullPage: true })
  verifyConsole()
})

test('an image card shows a library picture as a cutout, survives a reload, and a still waits for its picture', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const label = testInfo.project.name
  await page.goto('/')
  const uploaded = await page.request.post('/api/assets/images', {
    headers: { 'X-Storyboard-Studio': '1' },
    multipart: { file: { name: `cutout-${label}.png`, mimeType: 'image/png', buffer: halfCutoutPng(64, 32, label) } },
  })
  expect(uploaded.ok()).toBe(true)
  const picture = await uploaded.json() as { id: string; displayName: string; contentHash: string }
  const studio = await (await page.request.get('/api/studio')).json() as { shots: { id: string }[] }

  const scene = await newScene(page)
  const objects = page.getByTestId('scene-objects')
  await objects.getByLabel('Add image card').selectOption({ label: picture.displayName })
  const placement = page.getByTestId('scene-placement')
  await expect(placement.getByLabel('Object name')).toHaveValue(picture.displayName)
  await expect(objects.getByRole('button', { name: new RegExp(picture.displayName) })).toContainText('Image card')
  // The card takes the picture's 2:1 shape and keeps it when resized.
  await expect(placement.getByLabel('Stand-in size width')).toHaveValue('4')
  await placement.getByLabel('Stand-in size height').fill('3')
  await expect(placement.getByLabel('Stand-in size width')).toHaveValue('6')
  const stage = page.getByTestId('scene-stage')
  await expect(stage).toHaveAttribute('data-cards-ready', '1')

  // Frame the card, then let go of it so the handles are out of the way.
  await page.getByRole('group', { name: 'Object handles' }).getByRole('button', { name: 'Focus' }).click()
  const box = (await stage.boundingBox())!
  await page.mouse.click(box.x + 8, box.y + 8)
  await expect(page.getByTestId('scene-placement')).toHaveCount(0)
  const pixel = (x: number, y: number) => page.evaluate(([fx, fy]) => {
    const canvas = document.querySelector('.scene-stage canvas') as HTMLCanvasElement
    const copy = document.createElement('canvas'); copy.width = canvas.width; copy.height = canvas.height
    const context = copy.getContext('2d')!; context.drawImage(canvas, 0, 0)
    return [...context.getImageData(Math.floor(canvas.width * fx), Math.floor(canvas.height * fy), 1, 1).data]
  }, [x, y])
  // The opaque half shows the picture's red, unlit; the clear half shows the
  // scene behind it, not a filled card.
  await expect.poll(async () => (await pixel(0.56, 0.5)).slice(0, 3)).toEqual([255, 0, 0])
  const clear = await pixel(0.44, 0.5)
  expect(clear[0]).toBeLessThan(100)
  await page.screenshot({ path: testInfo.outputPath('scene-image-card.png'), fullPage: true })

  await page.getByTestId('scene-save').click()
  await expect(page.getByTestId('scene-version')).toContainText('Version 2')
  const saved = await readScene(page, scene.id)
  const placeholder = saved.instances[0].placeholder as { shape: string; size: number[]; imageAssetId: string; imageUrl: string; imageContentHash: string }
  expect(placeholder).toMatchObject({ shape: 'Card', size: [6, 3, 0.01], imageAssetId: picture.id, imageContentHash: picture.contentHash })

  // Reopen with the picture held back. A still asked for now waits for it
  // rather than freezing a blank card.
  let release!: () => void
  const held = new Promise<void>(resolve => { release = resolve })
  await page.route(`**/api/assets/${picture.id}/content`, async route => { const response = await route.fetch(); await held; await route.fulfill({ response }) })
  const stills: string[] = []
  page.on('request', request => { if (request.method() === 'POST' && request.url().includes('/shot-stills')) stills.push(request.url()) })
  await page.reload()
  await page.getByRole('button', { name: 'Scene', exact: true }).click()
  await expect(page.getByLabel('Scene name', { exact: true })).toHaveValue(scene.name)
  await expect(stage).toHaveAttribute('data-cards-ready', '0')
  const shotSetup = page.getByTestId('scene-shot')
  await shotSetup.getByLabel('Scene shot').selectOption(studio.shots[0].id)
  await shotSetup.getByRole('button', { name: 'Render still for review' }).click()
  await page.waitForTimeout(1000)
  expect(stills).toEqual([])
  release()
  await expect(page.getByTestId('review-workspace')).toBeVisible()
  expect(stills).toHaveLength(1)
  await page.unroute(`**/api/assets/${picture.id}/content`)
  verifyConsole()
})

/** A PNG whose left half is fully transparent and right half opaque red, unique to this run. */
function halfCutoutPng(width: number, height: number, label: string) {
  const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k += 1) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0 })
  const crc = (bytes: Buffer) => { let c = 0xffffffff; for (const byte of bytes) c = crcTable[(c ^ byte) & 0xff] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0 }
  const chunk = (type: string, data: Buffer) => {
    const length = Buffer.alloc(4); length.writeUInt32BE(data.length)
    const body = Buffer.concat([Buffer.from(type, 'ascii'), data])
    const check = Buffer.alloc(4); check.writeUInt32BE(crc(body))
    return Buffer.concat([length, body, check])
  }
  const header = Buffer.alloc(13)
  header.writeUInt32BE(width, 0); header.writeUInt32BE(height, 4)
  header[8] = 8; header[9] = 6 // 8-bit RGBA
  const rows = Buffer.alloc((width * 4 + 1) * height)
  for (let y = 0; y < height; y += 1) {
    for (let x = 0; x < width; x += 1) {
      const at = y * (width * 4 + 1) + 1 + x * 4
      if (x >= width / 2) { rows[at] = 255; rows[at + 3] = 255 }
    }
  }
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', header),
    chunk('tEXt', Buffer.from(`Comment\0scene-setup-${label}`, 'latin1')),
    chunk('IDAT', deflateSync(rows)),
    chunk('IEND', Buffer.alloc(0)),
  ])
}

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
