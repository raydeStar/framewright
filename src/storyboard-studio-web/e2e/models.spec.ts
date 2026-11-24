import { expect, test, type Locator, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { inflateSync } from 'node:zlib'

const block = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-block.glb', import.meta.url))
const post = fileURLToPath(new URL('../../../fixtures/glb/asymmetric-post.glb', import.meta.url))
const figure = fileURLToPath(new URL('../../../fixtures/glb/rigged-figure.glb', import.meta.url))
const animatedFigure = fileURLToPath(new URL('../../../fixtures/glb/clip-arm-raise.glb', import.meta.url))
const wrongProfile = fileURLToPath(new URL('../../../fixtures/glb/rigged-wrong-profile.glb', import.meta.url))

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

test('inspect two clips, play them, and export all, one, or no animations', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const name = `animation-preview-${testInfo.project.name}-${Date.now()}`
  await openAssets(page)
  await page.locator('input[type="file"]').setInputFiles({
    name: `${name}.glb`, mimeType: 'model/gltf-binary',
    buffer: ownFixture(animatedFigure, name),
  })
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open ${name}` }).click()
  await expect(page.getByTestId('model-viewer')).toHaveAttribute('data-state', 'ready')
  await expect(page.getByTestId('model-animation-select')).toHaveCount(1)
  await page.getByTestId('model-animation-select').selectOption('Arm raise')
  await page.getByTestId('model-animation-play').click()
  await expect.poll(async () => Number(await page.getByTestId('model-animation-time').inputValue()))
    .toBeGreaterThan(0.05)
  await page.getByTestId('model-animation-play').click()
  await page.getByRole('button', { name: 'Next animation' }).click()
  await expect(page.getByTestId('model-animation-select')).toHaveValue('Step forward')
  await page.getByTestId('model-animation-select').selectOption('')
  await expect(page.getByTestId('model-animation-play')).toBeDisabled()

  const exportedNames = async () => {
    const pending = page.waitForEvent('download')
    await page.getByTestId('model-export-button').click()
    const downloaded = await pending
    const bytes = readFileSync(await downloaded.path())
    const jsonLength = bytes.readUInt32LE(12)
    const document = JSON.parse(bytes.subarray(20, 20 + jsonLength).toString('utf8'))
    expect(document.meshes.length).toBeGreaterThan(0)
    expect(document.skins.length).toBeGreaterThan(0)
    return (document.animations ?? []).map((animation: { name: string }) => animation.name)
  }
  expect(await exportedNames()).toEqual(['Arm raise', 'Step forward'])
  await page.getByRole('button', { name: 'None', exact: true }).click()
  expect(await exportedNames()).toEqual([])
  await page.getByLabel('Arm raise 2.0s').check()
  expect(await exportedNames()).toEqual(['Arm raise'])
  verifyConsole()
})

test('a supported model imports, reports its real measurements, and inspects without moving', async ({ page }, testInfo) => {
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
  // The surface survives its parent re-rendering. Typing in the inspector
  // re-renders the workspace; the viewer used to be torn down and the model
  // loaded again on every such render, which with a job polling every 700 ms
  // made the model blink in and out for as long as anything was queued.
  await page.locator('.model-stage canvas').evaluate(canvas => { canvas.dataset.sameSurface = 'yes' })
  await page.getByLabel('Name').fill('Renamed while looking')
  await expect(page.locator('.model-stage canvas')).toHaveCount(1)
  await expect(page.locator('.model-stage canvas')).toHaveAttribute('data-same-surface', 'yes')
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
  await page.screenshot({ path: testInfo.outputPath('model-qc.png'), fullPage: true })

  // Sliding the view is not orbiting it: the camera moves without turning, so
  // a model taller than the frame can be read from top to bottom.
  const orbitBeforePan = await page.getByTestId('model-viewer-orbit').innerText()
  await stage.press('Shift+ArrowUp')
  await expect(page.getByTestId('model-viewer-orbit')).toHaveText(orbitBeforePan)

  // Paint and wire answer different questions, and a painted surface hides the
  // topology that a decimated mesh's faults live in.
  await expect(page.getByTestId('model-paint')).toHaveAttribute('aria-pressed', 'true')
  await page.getByTestId('model-paint').click()
  await expect(page.getByTestId('model-paint')).toHaveAttribute('aria-pressed', 'false')
  await page.getByTestId('model-wireframe').click()
  await expect(page.getByTestId('model-wireframe')).toHaveAttribute('aria-pressed', 'true')
  // Neither toggle edits the model: what the renderer loaded is unchanged.
  await expect(stage).toHaveAttribute('data-loaded-min', '0,0,0')
  await expect(stage).toHaveAttribute('data-loaded-max', '2,1,0.75')
  await page.getByTestId('model-paint').click()
  await page.getByTestId('model-wireframe').click()

  await page.getByRole('button', { name: 'Reset view' }).click()
  await expect(page.getByTestId('model-viewer-orbit')).toHaveText(openingOrbit)
  verifyConsole()
})

test('switching models releases the previous view instead of stacking contexts', async ({ page }) => {
  // A hosted runner can spend several seconds bringing up its second software
  // WebGL surface. The contract here is the released context and new measured
  // profile, so wait for that profile rather than treating render speed as one.
  test.setTimeout(60_000)
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
  const switchedDimensions = page.getByTestId('model-dimensions')
  await expect(switchedDimensions).toContainText('2.00 m', { timeout: 30_000 })
  await expect(switchedDimensions).toContainText('0.45 m')
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
  // GLB parsing and software WebGL can be slow on a contended hosted runner.
  // Keep the behavioral assertions strict while allowing the real viewer work.
  test.setTimeout(90_000)
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

  // Reopening the workspace shows exactly what was saved. A refresh comes
  // back to the model it left, by its address, rather than to the front page.
  await page.reload()
  await expect(page.getByTestId('model-workspace')).toBeVisible()
  await expect(page.getByRole('heading', { name })).toBeVisible()
  // And the library still lists it under the name it was given.
  await page.getByRole('button', { name: 'Asset library' }).click()
  await expect(page.getByTestId('model-workspace')).toHaveCount(0)
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open ${name}` }).click()
  await expect(page.getByTestId('model-workspace')).toBeVisible()
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

test('a rigged character reports its skeleton, poses deterministically, and refuses to flatter a wrong rig', async ({ page }, testInfo) => {
  // This opens three real GLBs and exercises their skeletons in software WebGL.
  test.setTimeout(90_000)
  const verifyConsole = failOnConsoleErrors(page)
  const label = testInfo.project.name
  await openAssets(page)

  await page.locator('input[type="file"]').setInputFiles([
    { name: `rigged-figure-${label}.glb`, mimeType: 'model/gltf-binary', buffer: ownFixture(figure, label) },
    { name: `rigged-wrong-${label}.glb`, mimeType: 'model/gltf-binary', buffer: ownFixture(wrongProfile, label) },
  ])
  // Wait for the import itself, so a refused upload fails here with its own
  // reason rather than as a missing card later.
  await expect(page.getByText('2 assets imported into the library.')).toBeVisible()
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open rigged-figure-${label}` }).click()
  await expect(page.getByTestId('model-workspace')).toBeVisible()

  // The skeleton is read from the stored file and named for what it is.
  const rig = page.getByTestId('model-rig')
  await expect(page.getByTestId('model-rig-state')).toHaveAttribute('data-ready', 'true')
  await expect(page.getByTestId('model-rig-state')).toContainText('Humanoid A')
  await expect(rig).toContainText('17')
  await expect(rig).toContainText('Valid')
  await expect(page.getByTestId('model-rig-findings')).toHaveCount(0)

  const bones = page.getByTestId('model-rig-bones')
  await expect(bones).toContainText('Hips')
  await expect(bones).toContainText('LeftHand')
  await expect(bones).toContainText('under Neck')

  // A pose moves the bones it names and nothing else, and it is calculated,
  // never saved: the model's own version and measurements do not move.
  const restingHand = (await bones.getByRole('listitem').filter({ hasText: 'LeftHand' }).textContent()) ?? ''
  await page.getByTestId('model-rig-pose').click()
  await expect(bones.getByRole('listitem').filter({ hasText: 'LeftHand' })).not.toHaveText(restingHand)
  const posedHand = (await bones.getByRole('listitem').filter({ hasText: 'LeftHand' }).textContent()) ?? ''
  await expect(bones.getByRole('listitem').filter({ hasText: 'RightHand' })).toContainText('rest (-0.67')

  // The same pose gives the same numbers every time.
  await page.getByRole('button', { name: 'Back to rest' }).click()
  await expect(bones.getByRole('listitem').filter({ hasText: 'LeftHand' })).toHaveText(restingHand)
  await page.getByTestId('model-rig-pose').click()
  await expect(bones.getByRole('listitem').filter({ hasText: 'LeftHand' })).toHaveText(posedHand)

  // A skeleton whose bones are named something else is an unknown skeleton,
  // and it is not posed at all.
  await page.getByRole('button', { name: 'Asset library' }).click()
  await page.getByRole('button', { name: `Open rigged-wrong-${label}` }).click()
  await expect(page.getByTestId('model-rig-state')).toHaveAttribute('data-ready', 'false')
  await expect(page.getByTestId('model-rig-state')).toContainText('Unknown skeleton')
  await expect(page.getByTestId('model-rig-findings')).toContainText('does not match')
  await expect(page.getByTestId('model-rig-pose')).toBeDisabled()

  // A static prop has no skeleton, and that is an ordinary answer.
  await page.getByRole('button', { name: 'Asset library' }).click()
  await page.locator('input[type="file"]').setInputFiles({
    name: `rig-prop-${label}.glb`, mimeType: 'model/gltf-binary', buffer: ownFixture(block, `rig-prop-${label}`),
  })
  await expect(page.getByText('1 asset imported into the library.')).toBeVisible()
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open rig-prop-${label}` }).click()
  await expect(page.getByTestId('model-rig-state')).toContainText('Static prop')
  await expect(page.getByTestId('model-rig-pose')).toHaveCount(0)
  verifyConsole()
})

/** The rigged fixture as an unrigged humanoid: the same triangles, no skin, no skeleton. */
function withoutSkin(source: string, label: string) {
  const bytes = ownFixture(source, label)
  const jsonLength = bytes.readUInt32LE(12)
  const document = JSON.parse(bytes.subarray(20, 20 + jsonLength).toString('utf8'))
  delete document.skins
  delete document.animations
  for (const node of document.nodes ?? []) delete node.skin
  for (const mesh of document.meshes ?? []) for (const primitive of mesh.primitives ?? []) {
    delete primitive.attributes.JOINTS_0
    delete primitive.attributes.WEIGHTS_0
  }
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

test('an unrigged humanoid is rigged as a candidate, shown bending, and accepted by a person', async ({ page }, testInfo) => {
  test.setTimeout(90_000)
  const verifyConsole = failOnConsoleErrors(page)
  const label = testInfo.project.name
  await openAssets(page)
  await page.locator('input[type="file"]').setInputFiles([
    { name: `plain-figure-${label}.glb`, mimeType: 'model/gltf-binary', buffer: withoutSkin(figure, label) },
  ])
  await expect(page.getByText(/imported into the library/)).toBeVisible()
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open plain-figure-${label}` }).click()
  await expect(page.getByTestId('model-workspace')).toBeVisible()

  // No skeleton yet, so the rig action is offered; nothing has been sent.
  const create = page.getByTestId('model-rig-create')
  await expect(page.getByTestId('model-rig-create-section')).toContainText('UE5 Manny skeleton')
  await create.click()
  await expect(page.getByText(/queued\. It keeps going if you leave this screen/)).toBeVisible()

  // The candidate comes back with its pose suite as the thing to judge.
  const suite = page.getByTestId('model-pose-suite')
  await expect(suite).toBeVisible({ timeout: 45_000 })
  await expect(suite.getByRole('img', { name: 'elbows bent front' })).toBeVisible()
  await expect(suite.getByRole('img', { name: 'Landmarks front' })).toBeVisible()
  // The pictures load after the suite appears, and lazily: bring the first
  // into view, as a person would, and wait for it to decode. A broken image
  // never gets a width, so this still fails on one.
  const firstPose = suite.getByRole('img').first()
  await firstPose.scrollIntoViewIfNeeded()
  await expect.poll(() => firstPose.evaluate(image => (image as HTMLImageElement).naturalWidth > 0)).toBe(true)

  // Unaccepted until a person says so; accepting makes the rig current.
  const assets = await (await page.request.get('/api/assets')).json() as Array<{ id: string; displayName: string; revisionPrompt: string; isCurrentRevision: boolean; preparationAcceptedAt: string | null }>
  const rigged = assets.find(asset => asset.revisionPrompt.startsWith(`Rigged from plain-figure-${label}`))
  expect(rigged).toBeTruthy()
  expect(rigged!.isCurrentRevision).toBe(false)
  expect(rigged!.preparationAcceptedAt).toBeNull()
  await page.getByTestId('model-preparation-accept').click()
  await expect(page.getByText(/Accepted\. Revision \d+ is now the current model/)).toBeVisible()
  const after = (await (await page.request.get('/api/assets')).json() as Array<{ id: string; isCurrentRevision: boolean; preparationAcceptedAt: string | null }>)
    .find(asset => asset.id === rigged!.id)
  expect(after?.isCurrentRevision).toBe(true)
  expect(after?.preparationAcceptedAt).toBeTruthy()
  verifyConsole()
})

/**
 * The block as a part worth judging up close: two metres long, a few
 * centimetres deep, and painted one matte 18% grey. The depth puts the middle
 * of the part just behind its face, as it is on a railing or a panel, so the
 * closest view looks at a surface rather than out from inside a solid. The
 * grey is the card a light is exposed against: its stored base colour is
 * sRGB 118, and how far a light shows it from 118 is how far that light is
 * from showing paint as it was authored.
 */
function greyPanel(label: string) {
  const bytes = ownFixture(block, label)
  const jsonLength = bytes.readUInt32LE(12)
  const document = JSON.parse(bytes.subarray(20, 20 + jsonLength).toString('utf8'))
  for (const material of document.materials)
    material.pbrMetallicRoughness = { baseColorFactor: [0.18, 0.18, 0.18, 1], metallicFactor: 0, roughnessFactor: 1 }
  document.nodes.push({ name: 'Panel', children: document.scenes[0].nodes, scale: [1, 1, 0.1] })
  document.scenes[0].nodes = [document.nodes.length - 1]
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

/**
 * The picture inside the stage's frame. The frame itself is left out: its
 * rounded corners show the focus ring drawn around the stage, which says
 * where the keyboard is and nothing about the model.
 */
async function pictureOf(stage: Locator) {
  await stage.scrollIntoViewIfNeeded()
  const box = (await stage.boundingBox())!
  const frame = 14
  return stage.page().screenshot({
    clip: { x: box.x + frame, y: box.y + frame, width: box.width - frame * 2, height: box.height - frame * 2 },
  })
}

/** That picture once it has stopped changing: this viewer draws on demand, so a finished picture holds still. */
async function settled(stage: Locator) {
  let previous: Buffer | undefined
  for (let attempt = 0; attempt < 40; attempt++) {
    const shot = await pictureOf(stage)
    if (previous && differenceBetween(shot, previous) === 'nothing') return shot
    previous = shot
    await stage.page().waitForTimeout(150)
  }
  throw new Error('The model view never came to rest.')
}

/**
 * The pixels of a screenshot, read here rather than in a browser page. A
 * second page opened beside the one under test puts that one behind it, and a
 * page behind another answers every action slowly. Playwright writes plain
 * 8-bit RGB or RGBA; anything else is refused rather than guessed at.
 */
function pixelsOf(png: Buffer) {
  let width = 0
  let height = 0
  let channels = 0
  const packed: Buffer[] = []
  for (let at = 8; at < png.length;) {
    const length = png.readUInt32BE(at)
    const kind = png.toString('latin1', at + 4, at + 8)
    const body = png.subarray(at + 8, at + 8 + length)
    if (kind === 'IHDR') {
      width = body.readUInt32BE(0)
      height = body.readUInt32BE(4)
      if (body[8] !== 8 || (body[9] !== 2 && body[9] !== 6) || body[12] !== 0)
        throw new Error(`A screenshot in a form this test does not read: depth ${body[8]}, colour type ${body[9]}, interlace ${body[12]}.`)
      channels = body[9] === 6 ? 4 : 3
    } else if (kind === 'IDAT') packed.push(body)
    at += 12 + length
  }
  const lines = inflateSync(Buffer.concat(packed))
  const stride = width * channels
  const data = Buffer.alloc(height * stride)
  for (let y = 0; y < height; y++) {
    const filter = lines[y * (stride + 1)]
    const line = y * (stride + 1) + 1
    const row = y * stride
    for (let x = 0; x < stride; x++) {
      const left = x >= channels ? data[row + x - channels] : 0
      const up = y > 0 ? data[row - stride + x] : 0
      const corner = x >= channels && y > 0 ? data[row - stride + x - channels] : 0
      let predicted = 0
      if (filter === 1) predicted = left
      else if (filter === 2) predicted = up
      else if (filter === 3) predicted = (left + up) >> 1
      else if (filter === 4) {
        const guess = left + up - corner
        const [byLeft, byUp, byCorner] = [Math.abs(guess - left), Math.abs(guess - up), Math.abs(guess - corner)]
        predicted = byLeft <= byUp && byLeft <= byCorner ? left : byUp <= byCorner ? up : corner
      }
      data[row + x] = (lines[line + x] + predicted) & 255
    }
  }
  return { width, height, channels, data }
}

/**
 * What a picture of the stage shows: how much of it is the model's surface
 * rather than the dark floor behind it, and the middle value of that surface.
 */
function surfaceOf(picture: Buffer) {
  const { width, height, channels, data } = pixelsOf(picture)
  const seen: number[] = []
  let sampled = 0
  for (let y = 0; y < height; y += 3) {
    for (let x = 0; x < width; x += 3) {
      sampled++
      const green = data[(y * width + x) * channels + 1]
      // The floor and its grid are darker than any lit face of this part.
      if (green > 85) seen.push(green)
    }
  }
  seen.sort((a, b) => a - b)
  return { coverage: seen.length / sampled, value: seen[Math.floor(seen.length / 2)] ?? 0 }
}

/**
 * Where two pictures of the stage differ, in words, or 'nothing'. Two
 * pictures that are not the same should say where, so a failure on a machine
 * nobody is sitting at still names the corner of the stage it happened in.
 */
function differenceBetween(seen: Buffer, expected: Buffer) {
  if (seen.equals(expected)) return 'nothing'
  const [one, other] = [pixelsOf(seen), pixelsOf(expected)]
  if (one.width !== other.width || one.height !== other.height) return `a ${one.width}x${one.height} picture, not ${other.width}x${other.height}`
  let differing = 0
  let largest = 0
  let [left, top, right, bottom] = [one.width, one.height, -1, -1]
  for (let y = 0; y < one.height; y++) {
    for (let x = 0; x < one.width; x++) {
      const [here, there] = [(y * one.width + x) * one.channels, (y * other.width + x) * other.channels]
      const apart = Math.max(Math.abs(one.data[here] - other.data[there]), Math.abs(one.data[here + 1] - other.data[there + 1]), Math.abs(one.data[here + 2] - other.data[there + 2]))
      if (apart === 0) continue
      differing++
      largest = Math.max(largest, apart)
      left = Math.min(left, x); top = Math.min(top, y); right = Math.max(right, x); bottom = Math.max(bottom, y)
    }
  }
  return differing === 0 ? 'nothing'
    : `${differing} pixels differ by up to ${largest}, inside (${left},${top})-(${right},${bottom}) of ${one.width}x${one.height}`
}

/**
 * Where the 3D view and the controls under it have ended up, in pixels.
 * `overStage` is how far the control bar reaches up over the view, and
 * `pastPanel` how far the camera readout runs out of the bottom of the view's
 * own panel, into whatever is next. Each is nought or less when the layout
 * holds.
 */
const controlsOutOfPlace = (page: Page) => page.evaluate(() => {
  const box = (selector: string) => document.querySelector(selector)!.getBoundingClientRect()
  const panel = document.querySelector('.model-stage-shell')!
  const inside = panel.getBoundingClientRect().bottom - parseFloat(getComputedStyle(panel).paddingBottom)
  const tenths = (value: number) => Math.round(value * 10) / 10
  return {
    stageHeight: tenths(box('.model-stage').height),
    overStage: tenths(box('.model-stage').bottom - box('.model-viewer-bar').top),
    pastPanel: tenths(box('.model-viewer-orbit').bottom - inside),
  }
})

const distanceShown = (metres: string) => new RegExp(`distance ${metres.replace('.', '\\.')} m$`)

test('a part is judged under a look-dev light and from as close as it will be seen, by pointer, keys and touch', async ({ page }, testInfo) => {
  test.setTimeout(150_000)
  // Switching revisions aborts a GLB still in flight; WebKit reports the abort.
  const verifyConsole = failOnConsoleErrors(page, [/due to access control checks/, /TypeError: Load failed/])
  const touchScreen = testInfo.project.name === 'tablet'
  const name = `lookdev-panel-${testInfo.project.name}-${Date.now()}`
  await openAssets(page)
  await page.locator('input[type="file"]').setInputFiles({ name: `${name}.glb`, mimeType: 'model/gltf-binary', buffer: greyPanel(name) })
  await expect(page.getByText('1 asset imported into the library.')).toBeVisible()
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open ${name}` }).click()
  const viewer = page.getByTestId('model-viewer')
  const stage = page.getByTestId('model-stage')
  const orbit = page.getByTestId('model-viewer-orbit')
  const light = page.getByTestId('model-light')
  await expect(viewer).toHaveAttribute('data-state', 'ready')
  await expect(stage).toHaveAttribute('data-loaded-max', '2,1,0.075')

  // It opens under the even light and at the framing it always had.
  await expect(viewer).toHaveAttribute('data-light', 'inspection')
  await expect(light).toHaveAttribute('aria-pressed', 'false')
  await expect(light).toHaveText('Look-dev off')
  await expect(orbit).toHaveText(distanceShown('5.20'))
  const opening = await settled(stage)
  await testInfo.attach('opening-inspection', { body: opening, contentType: 'image/png' })
  expect(surfaceOf(opening).coverage, 'the whole part is in view, with floor around it').toBeLessThan(0.3)
  // The controls sit under the 3D view, never over it, and inside its panel,
  // however many rows they wrap to on this screen and in this machine's fonts.
  const controls = await controlsOutOfPlace(page)
  expect(controls.overStage, 'the control bar starts below the 3D view').toBeLessThanOrEqual(0)
  expect(controls.pastPanel, 'the camera readout stays inside the view’s panel').toBeLessThanOrEqual(0)
  // Where the page is longer than the screen, the view keeps its minimum
  // height. It does not take a height from the frame it last drew.
  if (touchScreen) expect(controls.stageHeight, 'the 3D view is its minimum height on a tablet').toBe(180)

  // The keys bring the camera in to a quarter of a metre and no nearer. On a
  // two-metre part the limit used to be 80 cm.
  await stage.focus()
  for (let step = 0; step < 25; step++) await stage.press('+')
  await expect(orbit).toHaveText(distanceShown('0.28'))
  for (let step = 0; step < 6; step++) await stage.press('+')
  await expect(orbit).toHaveText(distanceShown('0.25'))
  await stage.press('-')
  await expect(orbit).toHaveText(distanceShown('0.28'))
  await stage.press('+')
  await expect(orbit).toHaveText(distanceShown('0.25'))
  // The numbers are not the proof: at that distance the surface fills the view.
  const closeInspection = await settled(stage)
  await testInfo.attach('close-inspection', { body: closeInspection, contentType: 'image/png' })
  const even = surfaceOf(closeInspection)
  // A tablet's stage is a wide strip, and its far end looks past the part.
  expect(even.coverage, 'the face of the part fills most of the stage').toBeGreaterThan(0.6)
  // The even light shows an 18% grey well above the 118 it is stored as.
  expect(even.value).toBeGreaterThan(140)

  // The look-dev light, from the keyboard. The viewer draws only when asked,
  // so the picture changing at all is the proof that the switch redrew it.
  await light.focus()
  await light.press('Enter')
  await expect(light).toHaveAttribute('aria-pressed', 'true')
  await expect(light).toHaveText('Look-dev on')
  await expect(viewer).toHaveAttribute('data-light', 'lookdev')
  await expect.poll(async () => differenceBetween(await pictureOf(stage), closeInspection)).not.toBe('nothing')
  const closeLookdev = await settled(stage)
  await testInfo.attach('close-lookdev', { body: closeLookdev, contentType: 'image/png' })
  const filmic = surfaceOf(closeLookdev)
  expect(filmic.coverage).toBeGreaterThan(0.6)
  // Mid-grey reads as mid-grey: within a sixth of a stop of the stored 118 on
  // a face that is turned a little away from the key.
  expect(filmic.value).toBeGreaterThan(100)
  expect(filmic.value).toBeLessThan(130)

  // And back, by pointer or by touch: the default view is the picture it was, to the pixel.
  if (touchScreen) await light.tap()
  else await light.click()
  await expect(light).toHaveAttribute('aria-pressed', 'false')
  await expect.poll(async () => differenceBetween(await pictureOf(stage), closeInspection), 'the default view is the picture it was').toBe('nothing')

  // A single channel is unlit and is not tone-mapped, so it shows the stored
  // numbers under either light: base colour 0.18 is sRGB 118 and roughness 1
  // is white, and the two pictures are the same picture.
  const channelUnder = async (channel: string) => {
    await page.getByTestId(`model-channel-${channel}`).click()
    await expect(page.getByTestId(`model-channel-${channel}`)).toHaveAttribute('aria-pressed', 'true')
    await expect(light).toBeDisabled()
    return settled(stage)
  }
  const colourEven = await channelUnder('colour')
  const roughnessEven = await channelUnder('roughness')
  await page.getByTestId('model-channel-lit').click()
  await expect(light).toBeEnabled()
  await light.click()
  await expect(viewer).toHaveAttribute('data-light', 'lookdev')
  const colourFilmic = await channelUnder('colour')
  const roughnessFilmic = await channelUnder('roughness')
  expect(differenceBetween(colourFilmic, colourEven), 'base colour is the same picture under both lights').toBe('nothing')
  expect(differenceBetween(roughnessFilmic, roughnessEven), 'roughness is the same picture under both lights').toBe('nothing')
  expect(differenceBetween(colourFilmic, roughnessFilmic)).not.toBe('nothing')
  const storedColour = surfaceOf(colourFilmic).value
  expect(storedColour).toBeGreaterThanOrEqual(117)
  expect(storedColour).toBeLessThanOrEqual(119)
  expect(surfaceOf(roughnessFilmic).value).toBe(255)
  await page.getByTestId('model-channel-lit').click()
  await expect.poll(async () => differenceBetween(await pictureOf(stage), closeLookdev), 'look-dev is the picture it was').toBe('nothing')

  // Sliding the view at this distance moves the camera without turning it or
  // changing how far away it is.
  const beforeSlide = await orbit.innerText()
  await stage.press('Shift+ArrowUp')
  await stage.press('Shift+ArrowLeft')
  await expect(orbit).toHaveText(beforeSlide)
  const leftAt = await settled(stage)

  // Another revision of the part opens under the same light and from the same
  // spot, and so does the first when it is opened again. The two revisions are
  // the same panel, so the same light from the same spot is the same picture:
  // a control that only said look-dev would not pass.
  const revisions = page.getByTestId('model-revisions')
  await revisions.locator('input[type="file"]').setInputFiles({
    name: `${name}-v2.glb`, mimeType: 'model/gltf-binary', buffer: greyPanel(`${name}-v2`),
  })
  await expect(revisions.getByRole('button', { name: /^v2/ })).toBeVisible()
  for (const revision of [/^v1/, /^v2/, /^v1/]) {
    await revisions.getByRole('button', { name: revision }).click()
    await expect(revisions.getByRole('button', { name: revision })).toHaveAttribute('aria-pressed', 'true')
    await expect(viewer).toHaveAttribute('data-state', 'ready')
    await expect(viewer).toHaveAttribute('data-light', 'lookdev')
    await expect(light).toHaveAttribute('aria-pressed', 'true')
    await expect(orbit).toHaveText(beforeSlide)
    await expect.poll(async () => differenceBetween(await pictureOf(stage), leftAt), 'this revision is the same picture from the same spot').toBe('nothing')
  }
  // Seen whole again, it is not the picture it opened with: the light stayed.
  await page.getByRole('button', { name: 'Frame', exact: true }).click()
  await expect(orbit).toHaveText(distanceShown('5.20'))
  const openingLookdev = await settled(stage)
  await testInfo.attach('opening-lookdev', { body: openingLookdev, contentType: 'image/png' })
  expect(differenceBetween(openingLookdev, opening)).not.toBe('nothing')

  // Two fingers: moving them apart brings the camera in by the same ratio.
  // These are touch pointers handed to the stage, not a finger on glass.
  const box = (await stage.boundingBox())!
  const [centreX, centreY] = [box.x + box.width / 2, box.y + box.height / 2]
  const finger = (pointerId: number, isPrimary: boolean, x: number, y = centreY) =>
    ({ pointerId, pointerType: 'touch', isPrimary, clientX: x, clientY: y, button: 0, buttons: 1 })
  const pinch = async (from: number, to: number) => {
    await stage.dispatchEvent('pointerdown', finger(11, true, centreX - from / 2))
    await stage.dispatchEvent('pointerdown', finger(12, false, centreX + from / 2))
    await stage.dispatchEvent('pointermove', finger(11, true, centreX - to / 2))
    await stage.dispatchEvent('pointermove', finger(12, false, centreX + to / 2))
    await stage.dispatchEvent('pointerup', finger(12, false, centreX + to / 2))
    await stage.dispatchEvent('pointerup', finger(11, true, centreX - to / 2))
  }
  await pinch(100, 200)
  await expect(orbit).toHaveText(distanceShown('2.60'))
  await pinch(200, 100)
  await expect(orbit).toHaveText(distanceShown('5.20'))
  for (let gesture = 0; gesture < 4; gesture++) await pinch(60, 240)
  await expect(orbit).toHaveText(distanceShown('0.25'))
  // Two fingers moved together slide the view: the picture moves, the camera does not turn.
  await page.getByRole('button', { name: 'Reset view' }).click()
  const beforeTwoFingers = await orbit.innerText()
  const framed = await settled(stage)
  await stage.dispatchEvent('pointerdown', finger(11, true, centreX - 50))
  await stage.dispatchEvent('pointerdown', finger(12, false, centreX + 50))
  await stage.dispatchEvent('pointermove', finger(11, true, centreX - 50, centreY + 60))
  await stage.dispatchEvent('pointermove', finger(12, false, centreX + 50, centreY + 60))
  await expect(orbit).toHaveText(beforeTwoFingers)
  expect(differenceBetween(await settled(stage), framed)).not.toBe('nothing')
  // The finger left down when the other lifts carries on as an orbit.
  await stage.dispatchEvent('pointerup', finger(12, false, centreX + 50, centreY + 60))
  await stage.dispatchEvent('pointermove', finger(11, true, centreX + 30, centreY + 60))
  await expect(orbit).not.toHaveText(beforeTwoFingers)
  await expect(orbit).toHaveText(distanceShown('5.20'))
  await stage.dispatchEvent('pointerup', finger(11, true, centreX + 30, centreY + 60))

  // The wheel, where the device has one, stops at the same quarter of a metre.
  if (!touchScreen) {
    await page.mouse.move(centreX, centreY)
    for (let step = 0; step < 31; step++) await page.mouse.wheel(0, -120)
    await expect(orbit).toHaveText(distanceShown('0.25'))
    for (let step = 0; step < 80; step++) await page.mouse.wheel(0, 120)
    await expect(orbit).toHaveText(distanceShown('80.00'))
  }

  // The light chosen here is not saved anywhere. A refresh reopens the part
  // from its address, afresh: under the even light, at its opening framing.
  await expect(viewer).toHaveAttribute('data-light', 'lookdev')
  await page.reload()
  await expect(viewer).toHaveAttribute('data-state', 'ready')
  await expect(viewer).toHaveAttribute('data-light', 'inspection')
  await expect(orbit).toHaveText(distanceShown('5.20'))
  verifyConsole()
})

test('the arrow keys turn a focused 3D view, and step through the library only from outside it', async ({ page }, testInfo) => {
  test.setTimeout(90_000)
  // Stepping to another model aborts a GLB still in flight; WebKit reports the abort.
  const verifyConsole = failOnConsoleErrors(page, [/due to access control checks/, /TypeError: Load failed/])
  const label = `${testInfo.project.name}-${Date.now()}`
  const mine = `arrow-keys-block-${label}`
  const other = `arrow-keys-post-${label}`
  await openAssets(page)
  // Two models, so the one opened always has a neighbour to be stepped to.
  await page.locator('input[type="file"]').setInputFiles([
    { name: `${mine}.glb`, mimeType: 'model/gltf-binary', buffer: ownFixture(block, mine) },
    { name: `${other}.glb`, mimeType: 'model/gltf-binary', buffer: ownFixture(post, other) },
  ])
  await expect(page.getByText('2 assets imported into the library.')).toBeVisible()
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open ${mine}` }).click()
  const viewer = page.getByTestId('model-viewer')
  const stage = page.getByTestId('model-stage')
  const orbit = page.getByTestId('model-viewer-orbit')
  const position = page.getByTestId('asset-step-position')
  const heading = page.getByTestId('model-workspace').getByRole('heading', { level: 1 })
  await expect(viewer).toHaveAttribute('data-state', 'ready')
  await expect(heading).toHaveText(mine)
  const place = await position.innerText()
  const hasNext = await page.getByTestId('asset-step-next').isEnabled()
  const hasPrevious = await page.getByTestId('asset-step-previous').isEnabled()
  expect(hasNext || hasPrevious, 'the model has a neighbour in the list').toBe(true)

  // On the focused view, left and right turn the inspection camera. The model
  // on screen stays the model on screen: same name, same place in the list,
  // same drawing surface.
  await stage.locator('canvas').evaluate(canvas => { canvas.dataset.sameSurface = 'yes' })
  await expect(orbit).toContainText('yaw 0.90')
  await stage.focus()
  await stage.press('ArrowRight')
  await expect(orbit).toContainText('yaw 0.98')
  await stage.press('ArrowLeft')
  await stage.press('ArrowLeft')
  await expect(orbit).toContainText('yaw 0.82')
  await expect(heading).toHaveText(mine)
  await expect(position).toHaveText(place)
  await expect(stage.locator('canvas')).toHaveAttribute('data-same-surface', 'yes')
  await expect(stage).toHaveAttribute('data-loaded-max', '2,1,0.75')

  // A field being typed in keeps its arrows too.
  const nameField = page.getByTestId('model-library').getByLabel('Name', { exact: true })
  await nameField.focus()
  await nameField.press('ArrowLeft')
  await nameField.press('ArrowRight')
  await expect(heading).toHaveText(mine)
  await expect(position).toHaveText(place)

  // From a plain button the same keys step through the list, and step back.
  const [away, back] = hasNext ? ['ArrowRight', 'ArrowLeft'] : ['ArrowLeft', 'ArrowRight']
  await page.getByRole('button', { name: 'Frame', exact: true }).focus()
  await page.keyboard.press(away)
  await expect(heading).not.toHaveText(mine)
  await expect(position).not.toHaveText(place)
  await expect(viewer).toHaveAttribute('data-state', 'ready')
  await page.getByRole('button', { name: 'Frame', exact: true }).focus()
  await page.keyboard.press(back)
  await expect(heading).toHaveText(mine)
  await expect(position).toHaveText(place)
  await expect(viewer).toHaveAttribute('data-state', 'ready')
  await expect(stage).toHaveAttribute('data-loaded-max', '2,1,0.75')
  verifyConsole()
})
