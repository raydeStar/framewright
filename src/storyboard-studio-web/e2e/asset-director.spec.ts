import { expect, test, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import AxeBuilder from '@axe-core/playwright'

type Packet = { ok: boolean; code: string; data?: { stateToken: string; subject: { assetId: string; displayedVersion: number }; view: { directorMode: boolean }; visual: { contentHash: string } } }
const call = (page: Page, name = 'get_director_context', input = {}) => page.evaluate(async ({ name, input }) => {
  const tools = (window as unknown as { directorTools: Map<string, { execute: (input: object, context: object) => Promise<Packet> }> }).directorTools
  return tools.get(name)!.execute(input, {})
}, { name, input })

test.beforeEach(async ({ page }) => {
  // A contract double for browsers without WebMCP, not evidence of host support.
  await page.addInitScript(() => {
    const tools = new Map()
    Object.defineProperty(window, 'directorTools', { value: tools })
    Object.defineProperty(document, 'modelContext', { value: {
      registerTool(tool: { name: string }, options: { signal: AbortSignal }) {
        tools.set(tool.name, tool)
        options.signal.addEventListener('abort', () => tools.delete(tool.name), { once: true })
      },
    } })
  })
  await page.goto('/')
})

async function importAsset(page: Page, kind: string, name: string, buffer: Buffer, mimeType: string) {
  const route = kind === 'Model' ? 'models' : kind === 'Image' ? 'images' : `media?kind=${kind}`
  const response = await page.request.post(`/api/assets/${route}`, {
    headers: { 'X-Storyboard-Studio': '1' }, multipart: { file: { name, mimeType, buffer } },
  })
  expect(response.ok()).toBe(true)
  return response.json() as Promise<{ id: string; displayName: string; contentHash: string }>
}

async function checkFullView(page: Page) {
  await expect(page.locator('.workspace-rail')).toBeHidden()
  const exit = page.getByRole('button', { name: 'Exit Director Mode', exact: true })
  await expect(exit).toBeVisible()
  expect((await exit.boundingBox())!.height).toBeGreaterThanOrEqual(44)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1)).toBe(true)
  const audit = await new AxeBuilder({ page }).analyze()
  expect(audit.violations.filter(item => ['serious', 'critical'].includes(item.impact ?? ''))).toEqual([])
}

test('model full view preserves the same renderer, orbit, revision and unsaved details', async ({ page }, info) => {
  const bytes = readFileSync(new URL('../../../fixtures/glb/asymmetric-block.glb', import.meta.url))
  const length = bytes.readUInt32LE(12)
  const doc = JSON.parse(bytes.subarray(20, 20 + length).toString())
  doc.scenes[0].name = `Director ${info.project.name} ${Date.now()}`
  let json = Buffer.from(JSON.stringify(doc))
  if (json.length % 4) json = Buffer.concat([json, Buffer.alloc(4 - json.length % 4, 32)])
  const header = Buffer.from(bytes.subarray(0, 20))
  const tail = bytes.subarray(20 + length)
  header.writeUInt32LE(20 + json.length + tail.length, 8); header.writeUInt32LE(json.length, 12)
  const asset = await importAsset(page, 'Model', `director-model-${info.project.name}.glb`, Buffer.concat([header, json, tail]), 'model/gltf-binary')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  expect((await call(page)).code).toBe('no_subject_open')
  await page.getByRole('button', { name: `Direct ${asset.displayName}`, exact: true }).click()
  await expect(page.getByTestId('model-viewer')).toHaveAttribute('data-state', 'ready')
  const canvas = page.getByTestId('model-stage').locator('canvas')
  await canvas.evaluate(node => { node.dataset.original = 'yes' })
  await page.getByTestId('model-stage').press('ArrowLeft')
  const orbit = await page.getByTestId('model-viewer-orbit').textContent()
  await checkFullView(page)
  const context = await call(page)
  expect(context.data?.subject.assetId).toBe(asset.id)
  expect(context.data?.view.directorMode).toBe(true)
  expect((await call(page, 'observe_current_frame', { stateToken: context.data!.stateToken })).code).toBe('observation_unavailable')
  await page.screenshot({ path: info.outputPath('model-director.png') })
  await page.getByRole('button', { name: 'Exit Director Mode' }).click()
  const name = page.getByTestId('model-library').getByLabel('Name', { exact: true })
  await name.fill('Unsaved model name')
  await page.getByRole('button', { name: 'Director Mode', exact: true }).click()
  await page.keyboard.press('Escape')
  await expect(name).toHaveValue('Unsaved model name')
  await expect(canvas).toHaveAttribute('data-original', 'yes')
  await expect(page.getByTestId('model-viewer-orbit')).toHaveText(orbit!)
  await page.getByRole('button', { name: 'Director Mode', exact: true }).click()
  await page.getByRole('button', { name: 'Asset library', exact: true }).click()
  await expect(page.locator('.workspace-rail')).toBeVisible()
  expect((await call(page)).code).toBe('no_subject_open')
})

test('image full view follows an older selected revision and keeps inspector drafts', async ({ page }, info) => {
  const image = async (label: string) => {
    const base64 = await page.evaluate(label => {
      const canvas = document.createElement('canvas'); canvas.width = 640; canvas.height = 360
      const ctx = canvas.getContext('2d')!; ctx.fillStyle = '#326773'; ctx.fillRect(0, 0, 640, 360)
      ctx.fillStyle = '#fff'; ctx.font = '24px sans-serif'; ctx.fillText(label, 24, 80)
      return canvas.toDataURL('image/png').split(',')[1]
    }, label)
    return importAsset(page, 'Image', `${label}.png`, Buffer.from(base64, 'base64'), 'image/png')
  }
  const first = await image(`Director still ${info.project.name} ${Date.now()}`)
  const second = await image(`Second still ${info.project.name} ${Date.now()}`)
  expect((await page.request.post(`/api/assets/${first.id}/revisions`, { data: { assetId: second.id, prompt: 'Second', engine: 'Imported' } })).ok()).toBe(true)
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.getByRole('button', { name: `Open ${first.displayName}`, exact: true }).click()
  await page.locator('.image-version-rail').getByRole('button', { name: /^v1/ }).click()
  const draft = page.getByRole('complementary', { name: 'Image asset details' }).getByLabel('Name', { exact: true })
  await draft.fill('Unsaved still name')
  await page.getByRole('button', { name: 'Director Mode', exact: true }).click()
  await checkFullView(page)
  const packet = await call(page)
  expect(packet.data?.subject.assetId).toBe(first.id)
  expect(packet.data?.subject.displayedVersion).toBe(1)
  expect(packet.data?.visual.contentHash).toBe(first.contentHash)
  expect((await call(page, 'observe_current_frame', { stateToken: packet.data!.stateToken })).code).toBe('asset_observation')
  await page.screenshot({ path: info.outputPath('image-director.png') })
  await page.getByRole('button', { name: 'Compare to current' }).click()
  expect((await call(page)).code).toBe('comparison_view')
  await page.getByRole('button', { name: 'Single view' }).click()
  await page.keyboard.press('Escape')
  await expect(draft).toHaveValue('Unsaved still name')
  await page.getByRole('button', { name: 'Director Mode', exact: true }).click()
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await expect(page.locator('.workspace-rail')).toBeVisible()
  expect((await call(page)).code).toBe('no_subject_open')
})

for (const kind of ['Audio', 'Video']) {
  test(`${kind} full view preserves playback position and unsaved metadata`, async ({ page }, info) => {
    const wav = Buffer.alloc(64044)
    wav.write('RIFF'); wav.writeUInt32LE(64036, 4); wav.write('WAVEfmt ', 8)
    wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(8000, 24)
    wav.writeUInt32LE(16000, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(64000, 40)
    const buffer = kind === 'Audio' ? wav : readFileSync(new URL('../../../fixtures/media/director-preview.mp4', import.meta.url))
    const asset = await importAsset(page, kind, `director-${kind.toLowerCase()}.${kind === 'Audio' ? 'wav' : 'mp4'}`, buffer, kind === 'Audio' ? 'audio/wav' : 'video/mp4')
    await page.getByRole('button', { name: 'Assets', exact: true }).click()
    await page.getByRole('button', { name: `Open ${asset.displayName}`, exact: true }).click()
    const inspector = page.getByRole('complementary', { name: `${asset.displayName} details` })
    await inspector.getByLabel('Name', { exact: true }).fill('Unsaved media name')
    const media = inspector.locator(`.asset-preview ${kind === 'Audio' ? 'audio' : 'video'}`)
    await expect.poll(() => media.evaluate(node => (node as HTMLMediaElement).readyState)).toBeGreaterThanOrEqual(1)
    await media.evaluate(node => { node.dataset.original = 'yes'; (node as HTMLMediaElement).currentTime = 1 })
    await inspector.getByRole('button', { name: 'Director Mode', exact: true }).click()
    await checkFullView(page)
    expect((await call(page)).data?.subject.assetId).toBe(asset.id)
    await page.screenshot({ path: info.outputPath(`${kind.toLowerCase()}-director.png`) })
    await page.getByRole('button', { name: 'Exit Director Mode' }).click()
    await expect(inspector.getByLabel('Name', { exact: true })).toHaveValue('Unsaved media name')
    await expect(media).toHaveAttribute('data-original', 'yes')
    expect(await media.evaluate(node => (node as HTMLMediaElement).currentTime)).toBeCloseTo(1, 1)
    await inspector.getByRole('button', { name: 'Director Mode', exact: true }).click()
    await page.getByRole('button', { name: 'Close asset details' }).click()
    await expect(page.locator('.workspace-rail')).toBeVisible()
    expect((await call(page)).code).toBe('no_subject_open')
  })
}
