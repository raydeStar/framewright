import { expect, test, type Page, type Route } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

/**
 * Painting a large prop again in sections, as the artist meets it: in the
 * generation panel, where a hero is asked whether it is a figure, an animal or
 * a prop and a prop is told what sections cost; and on a model already in the
 * library, where Paint in sections either states its cost or refuses in one
 * sentence before anything is queued.
 *
 * The library journeys run the real API, gateway, process boundary, queue and
 * import against the controlled compiler stand-in, which lists the section
 * pass. The generation journey answers its one POST at the browser, because
 * what is being proved there is what the panel says and sends, not a hero run.
 */

const denseProp = fileURLToPath(new URL('../../../fixtures/glb/dense-prop.glb', import.meta.url))
const block = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-block.glb', import.meta.url))

/**
 * A per-run copy of a fixture, renamed so each journey owns its own library
 * asset. `scale` resizes it through its node, which is how the studio measures
 * it; `painted` gives it an image, as a painted model has.
 */
function ownFixture(source: string, label: string, options: { scale?: number; painted?: boolean } = {}) {
  const bytes = readFileSync(source)
  const jsonLength = bytes.readUInt32LE(12)
  const document = JSON.parse(bytes.subarray(20, 20 + jsonLength).toString('utf8'))
  document.scenes[0].name = `${document.scenes[0].name} ${label}`
  if (options.scale) document.nodes[0].scale = [options.scale, options.scale, options.scale]
  if (options.painted) document.images = [{ bufferView: 0, mimeType: 'image/png' }]
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

/** A picture nobody else in this run owns; the store is content addressed. */
function ownPicture(label: string) {
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64')
  return Buffer.concat([png, Buffer.from(`paint-sections-${label}`, 'utf8')])
}

function failOnConsoleErrors(page: Page) {
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('pageerror', error => errors.push(error.message))
  return () => expect(errors, `Browser errors: ${errors.join('\n')}`).toEqual([])
}

async function importAsset(page: Page, route: 'models' | 'images', name: string, buffer: Buffer, mimeType: string) {
  const response = await page.request.post(`/api/assets/${route}`, {
    headers: { 'X-Storyboard-Studio': '1' }, multipart: { file: { name, mimeType, buffer } },
  })
  expect(response.ok()).toBe(true)
  return response.json() as Promise<{ id: string; displayName: string }>
}

/** A model in the library, linked to its own picture unless told otherwise. */
async function libraryModel(page: Page, name: string, model: Buffer, link = true) {
  const asset = await importAsset(page, 'models', `${name}.glb`, model, 'model/gltf-binary')
  if (link) {
    const picture = await importAsset(page, 'images', `${name}-concept.png`, ownPicture(name), 'image/png')
    const linked = await page.request.put(`/api/assets/${asset.id}/reference`, {
      headers: { 'X-Storyboard-Studio': '1' }, data: { referenceAssetId: picture.id },
    })
    expect(linked.ok()).toBe(true)
  }
  return asset
}

async function openModel(page: Page, name: string) {
  // Opening a new model also renders its library thumbnail. Headless WebKit
  // hands back an image the server rightly refuses, which is a thumbnail
  // matter and not what these journeys are about.
  await page.route('**/api/assets/*/poster', route =>
    route.request().method() === 'PUT' ? route.fulfill({ status: 204 }) : route.fallback())
  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Asset library' })).toBeVisible()
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open ${name}` }).click()
  await expect(page.getByTestId('model-workspace')).toBeVisible()
}

test('Paint in sections refuses in one sentence, before anything is queued', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const run = `${testInfo.project.name}-${Date.now()}`
  const unlinked = `sections-unlinked-${run}`
  const surfaced = `sections-surfaced-${run}`
  const lantern = `sections-lantern-${run}`
  await page.goto('/')
  await libraryModel(page, unlinked, ownFixture(denseProp, unlinked, { scale: 2, painted: true }), false)
  await libraryModel(page, surfaced, ownFixture(block, surfaced))
  await libraryModel(page, lantern, ownFixture(denseProp, lantern, { scale: 0.5, painted: true }))

  const refusals: Array<[string, RegExp]> = [
    [unlinked, /no linked picture\. Link the picture it was made from first/],
    [surfaced, /carries 2 materials\. .*paint sections before surfacing: pick an earlier revision/],
    [lantern, /0\.6 m at its longest, under 1\.2 m/],
  ]
  for (const [name, refusal] of refusals) {
    await openModel(page, name)
    const section = page.getByTestId('model-sections-section')
    await expect(section).toHaveAttribute('data-can-queue', 'false')
    await expect(section.getByTestId('model-sections-refusal')).toHaveText(refusal)
    // It says what it is for before it is ever offered.
    await expect(section).toContainText('This is for props.')
    await expect(section.getByTestId('model-sections-cost')).toHaveCount(0)
    await expect(section.getByTestId('model-paint-sections')).toBeDisabled()
    if (name === surfaced) {
      await testInfo.attach('paint-sections-refused-surfaced', { body: await section.screenshot(), contentType: 'image/png' })
    }
  }

  // Nothing was queued for any of them. Other journeys share this server, so
  // only this run's models are looked for.
  const snapshot = await (await page.request.get('/api/studio')).json()
  expect(snapshot.jobs.some((job: { shotCode: string }) =>
    [unlinked, surfaced, lantern].some(name => job.shotCode.startsWith(name)))).toBe(false)
  verifyConsole()
})

test('Paint in sections states its cost, then delivers a revision beside the model through the real queue', async ({ page }, testInfo) => {
  test.setTimeout(90_000)
  const verifyConsole = failOnConsoleErrors(page)
  const name = `sections-skiff-${testInfo.project.name}-${Date.now()}`
  await page.goto('/')
  const model = await libraryModel(page, name, ownFixture(denseProp, name, { scale: 2, painted: true }))
  await openModel(page, name)

  const section = page.getByTestId('model-sections-section')
  await expect(section).toHaveAttribute('data-can-queue', 'true')
  // The cost, for this model, before anything is queued.
  const cost = section.getByTestId('model-sections-cost')
  await expect(cost).toContainText('This model is 2.4 m at its longest.')
  await expect(cost).toContainText('one painter run a section, roughly 3 minutes each, on a GPU with 21 GiB free')
  await expect(cost).toContainText(`${name}-concept`)
  await expect(section).toContainText('that hard band goes along with the soft paint below it')
  await testInfo.attach('paint-sections-ready', { body: await section.screenshot(), contentType: 'image/png' })

  const button = section.getByTestId('model-paint-sections')
  await expect(button).toBeEnabled()
  await button.click()
  await expect(page.getByText(/painted in sections\) queued\. It keeps going if you leave this screen/)).toBeVisible()

  // The queue carries it through the stand-in: views, sections, export, views.
  await expect.poll(async () => {
    const snapshot = await (await page.request.get('/api/studio')).json()
    return snapshot.jobs.find((job: { shotCode: string }) => job.shotCode === `${name} (painted in sections)`)?.state
  }, { timeout: 60_000, intervals: [250] }).toBe('Completed')

  // A revision beside the source, not instead of it, quoting the receipt.
  const assets = await (await page.request.get('/api/assets')).json() as Array<{
    id: string; parentAssetId?: string; revisionPrompt: string; isCurrentRevision: boolean; referenceAssetId?: string
  }>
  const derivative = assets.find(asset => asset.parentAssetId === model.id)
  expect(derivative).toBeTruthy()
  expect(derivative!.isCurrentRevision).toBe(false)
  expect(derivative!.revisionPrompt).toContain('Painted in sections from')
  expect(derivative!.revisionPrompt).toContain(
    '4 sections, at about 0.9 mm of surface a painted pixel, down from 3.1 mm for the whole model')
  expect(assets.find(asset => asset.id === model.id)!.isCurrentRevision).toBe(true)

  // The before-and-after views arrive in the same panel, to be judged.
  await expect(page.getByTestId('model-comparison')).toBeVisible({ timeout: 15_000 })
  verifyConsole()
})

const queuedJob = {
  id: '9d3c1f40-1c2e-4a55-8a0e-6b2b7a1c3e01', shotId: '00000000-0000-0000-0000-000000000000',
  shotCode: 'Stand-in mast', kind: 'Model from reference', state: 'Queued', progress: 0,
  phase: 'Frozen reference queued', backend: 'Reference Asset Compiler', createdAt: new Date().toISOString(),
  completedAt: null, error: null, manifestId: null, adapterId: null, outputAssetId: null, outputUrl: null,
  providerRequestId: null, attempt: 1, retryOfJobId: null, lastHeartbeatAt: null, workType: 'Model',
}

test('the generation panel asks what a hero is, and tells a prop what sections cost', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const name = `sections-mast-${testInfo.project.name}-${Date.now()}`
  const sent: Array<Record<string, unknown>> = []
  await page.route('**/api/models/generation', async (route: Route) => {
    if (route.request().method() !== 'POST') return route.fallback()
    sent.push(route.request().postDataJSON() as Record<string, unknown>)
    await route.fulfill({ json: queuedJob })
  })
  await page.goto('/')
  await importAsset(page, 'images', `${name}.png`, ownPicture(name), 'image/png')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.getByRole('button', { name: /^Images/ }).click()
  await page.getByRole('button', { name: `Open ${name}` }).click()

  const panel = page.getByTestId('model-from-reference')
  await expect(panel.getByTestId('model-generation-readiness')).toHaveAttribute('data-can-run', 'true')
  await panel.getByTestId('model-size').selectOption('head')
  await panel.getByTestId('model-detail').selectOption('hero')

  // Asked, never guessed: a hero waits for the answer.
  const subject = panel.getByTestId('model-head-end')
  await expect(subject).toHaveValue('')
  await expect(panel.getByTestId('model-generate')).toBeDisabled()
  await expect(panel.getByTestId('model-paint-sections')).toHaveCount(0)

  // A figure keeps its head pass and is never offered sections.
  await subject.selectOption('top')
  await expect(panel.getByTestId('model-head-note')).toContainText('The head is painted a second time on its own')
  await expect(panel.getByTestId('model-paint-sections')).toHaveCount(0)

  // A prop gets no head pass, and is told what sections cost, on by default.
  await subject.selectOption('none')
  await expect(panel.getByTestId('model-head-note')).toContainText('leaves a hard line across it')
  const sections = panel.getByTestId('model-paint-sections')
  await expect(sections).toBeChecked()
  const cost = panel.getByTestId('model-paint-sections-cost')
  await expect(cost).toContainText('A prop longer than 1.2 m is cut into sections of about 0.45 m')
  await expect(cost).toContainText('One painter run a section, roughly 3 minutes each, up to 48, on a GPU with 10.5 GiB free')
  await expect(cost).toContainText('passes straight through at no cost')
  // Read beside its checkbox, not squeezed out of the column by it: the image
  // inspector stretches every input it holds across the full width.
  await expect(cost).toBeVisible()
  const [box, tick] = [await cost.boundingBox(), await sections.boundingBox()]
  expect(box!.width).toBeGreaterThan(150)
  expect(tick!.width).toBeLessThan(40)
  for (const [name, part] of [['generation-hero-subject', subject.locator('xpath=..')], ['generation-hero-prop-sections', panel.locator('label.model-paint-sections')]] as const) {
    await part.scrollIntoViewIfNeeded()
    await testInfo.attach(name, { body: await part.screenshot(), contentType: 'image/png' })
  }

  await panel.getByTestId('model-generate').click()
  await expect(page.getByText(/Stand-in mast queued/)).toBeVisible()
  // And it can be switched off, which is sent as such.
  await sections.uncheck()
  await panel.getByTestId('model-generate').click()
  await expect.poll(() => sent.length).toBe(2)
  expect(sent[0]).toEqual(expect.objectContaining({ detail: 'hero', headEnd: 'none', paintSections: true }))
  expect(sent[1]).toEqual(expect.objectContaining({ detail: 'hero', headEnd: 'none', paintSections: false }))

  // Set dressing is never asked about either.
  await panel.getByTestId('model-detail').selectOption('set')
  await expect(subject).toHaveCount(0)
  await expect(panel.getByTestId('model-paint-sections')).toHaveCount(0)
  verifyConsole()
})
