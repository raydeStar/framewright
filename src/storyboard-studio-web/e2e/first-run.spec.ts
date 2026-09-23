import { expect, test, type Page } from '@playwright/test'

type ProjectRow = { id: string; name: string; production: string; sequenceCode: string; sequenceName: string; isActive: boolean }

// These journeys switch the active project in the shared e2e database, so each
// one puts the original project back and removes what it created.
async function restoreProject(page: Page, originalId: string, createdName: string) {
  await page.request.post(`/api/projects/${originalId}/activate`)
  const projects = await (await page.request.get('/api/projects')).json() as ProjectRow[]
  const created = projects.find(project => project.name === createdName)
  if (created) await page.request.delete(`/api/projects/${created.id}?acceptRatifiedLoss=true`)
}

test('a new project needs only a name and opens once created', async ({ page }) => {
  const before = await (await page.request.get('/api/projects')).json() as ProjectRow[]
  const original = before.find(project => project.isActive)!
  const name = `Lighthouse ${Date.now()}`
  try {
    await page.goto('/')
    await page.getByTestId('project-switcher').click()
    await page.getByRole('menuitem', { name: 'New project' }).click()
    const dialog = page.getByTestId('create-project')
    await expect(dialog).toBeVisible()

    // The fields are styled like every other dialog, not left as bare inline
    // browser controls with labels running into them.
    const nameField = dialog.getByLabel('Project name')
    await expect(nameField).toBeFocused()
    expect(await nameField.evaluate(input => getComputedStyle(input.closest('label')!).display)).toBe('grid')
    expect(Number.parseFloat(await nameField.evaluate(input => getComputedStyle(input).minHeight))).toBeGreaterThanOrEqual(40)

    // Pressing Create without a name explains itself instead of doing nothing.
    const create = dialog.getByRole('button', { name: 'Create project', exact: true })
    await expect(create).toBeEnabled()
    await create.click()
    await expect(dialog.getByRole('alert')).toHaveText(/Give the project a name/)
    await expect(nameField).toBeFocused()

    // A named format replaces the pixel fields; exact numbers stay under Custom.
    await expect(dialog.getByLabel('Width in pixels')).toHaveCount(0)
    await expect(dialog.getByRole('radio', { name: /Widescreen HD/ })).toBeChecked()
    await dialog.getByText('Vertical', { exact: true }).click()
    await expect(dialog.getByRole('radio', { name: /Vertical/ })).toBeChecked()

    await nameField.fill(name)
    await expect(dialog.getByRole('alert')).toHaveCount(0)
    await create.click()

    await expect(dialog).toHaveCount(0)
    await expect(page.getByText(`${name} is ready`)).toBeVisible()
    await expect(page.getByTestId('project-switcher')).toContainText(name)
    await expect(page.getByText('No shots in this sequence yet')).toBeVisible()
    await expect(page.getByTestId('sample-welcome')).toHaveCount(0)
    await expect(page.getByTestId('project-switcher').getByText('Sample', { exact: true })).toHaveCount(0)

    // The defaults the artist skipped are real, valid values on the server, and
    // the choice survives a reload.
    const projects = await (await page.request.get('/api/projects')).json() as ProjectRow[]
    const created = projects.find(project => project.name === name)!
    expect(created).toMatchObject({ isActive: true, production: name, sequenceCode: 'SQ-01', sequenceName: 'Sequence 1' })
    const { project: contract } = await (await page.request.get('/api/studio')).json()
    expect(contract).toMatchObject({ aspectRatio: '9:16', deliveryWidth: 1080, deliveryHeight: 1920, framesPerSecond: 24 })
    await page.reload()
    await expect(page.getByTestId('project-switcher')).toContainText(name)
  } finally {
    await restoreProject(page, original.id, name)
  }
})

test('on a phone-width screen the create dialog keeps its explanations', async ({ page }) => {
  // The dialog renders inside the project header, whose compact-width rule once
  // hid every plain paragraph under it (and the wide rule shouted them in caps).
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/')
  await page.getByTestId('project-switcher').click()
  await page.getByRole('menuitem', { name: 'New project' }).click()
  const dialog = page.getByTestId('create-project')
  await dialog.getByRole('button', { name: 'Create project', exact: true }).click()
  await expect(dialog.getByRole('alert')).toBeVisible()
  await dialog.getByText('Custom', { exact: true }).click()
  const note = dialog.getByText('Width and height must be even numbers that match the aspect ratio.')
  await expect(note).toBeVisible()
  await expect(note).toHaveCSS('text-transform', 'none')
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(dialog).toHaveCount(0)
})

test('a fresh studio says it is a sample and offers the way to start your own', async ({ page }) => {
  await page.goto('/')
  const snapshot = await (await page.request.get('/api/studio')).json()
  expect(snapshot.project.isSample).toBe(true)

  await expect(page.getByTestId('project-switcher').getByText('Sample', { exact: true })).toBeVisible()
  const welcome = page.getByTestId('sample-welcome')
  await expect(welcome).toBeVisible()
  await expect(welcome.getByRole('heading', { name: 'Welcome to Framewright' })).toBeVisible()

  // The welcome opens the same create dialog as the project menu.
  await welcome.getByRole('button', { name: 'Start your own project' }).click()
  const dialog = page.getByTestId('create-project')
  await expect(dialog).toBeVisible()
  await expect(dialog.getByLabel('Project name')).toBeFocused()
  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(dialog).toHaveCount(0)

  await page.getByTestId('project-switcher').click()
  await expect(page.getByTestId('project-menu').getByRole('menuitemradio', { checked: true })).toContainText('Sample')
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('project-menu')).toHaveCount(0)
  await expect(page.getByTestId('project-switcher')).toBeFocused()

  // Keep exploring hides the welcome and it stays hidden; the tag remains.
  await welcome.getByRole('button', { name: 'Keep exploring' }).click()
  await expect(welcome).toHaveCount(0)
  await page.reload()
  await expect(page.getByTestId('board-workspace')).toBeVisible()
  await expect(page.getByTestId('sample-welcome')).toHaveCount(0)
  await expect(page.getByTestId('project-switcher').getByText('Sample', { exact: true })).toBeVisible()
})

test('a first shot needs only a sentence and starts from an honest empty frame', async ({ page }) => {
  const before = await (await page.request.get('/api/projects')).json() as ProjectRow[]
  const original = before.find(project => project.isActive)!
  const name = `First Shot ${Date.now()}`
  try {
    const created = await (await page.request.post('/api/projects', { data: {
      name, production: name, sequenceCode: 'SQ-01', sequenceName: 'Sequence 1',
      framesPerSecond: 24, aspectRatio: '16:9', deliveryWidth: 1920, deliveryHeight: 1080,
    } })).json()
    await page.request.post(`/api/projects/${created.id}/activate`)
    await page.goto('/')

    // The empty board offers the action itself rather than pointing elsewhere.
    await page.getByTestId('empty-board').getByRole('button', { name: 'Add your first shot' }).click()
    const dialog = page.getByRole('dialog', { name: 'Add a shot' })
    const description = dialog.getByLabel('Describe the shot')
    await expect(description).toBeFocused()
    // Everything beyond the sentence is folded away.
    await expect(dialog.getByLabel('Camera')).toBeHidden()
    await expect(dialog.getByLabel('Length in seconds')).toHaveValue('3')

    await dialog.getByRole('button', { name: 'Add shot', exact: true }).click()
    await expect(dialog.getByRole('alert')).toHaveText('Describe the shot to add it.')

    await description.fill('An old lighthouse on a cliff in a storm. The lamp sweeps across the waves.')
    await dialog.getByLabel('Length in seconds').fill('4.5')
    await dialog.getByRole('button', { name: 'Add shot', exact: true }).click()
    await expect(page.getByTestId('shot-workspace')).toBeVisible()

    const snapshot = await (await page.request.get('/api/studio')).json()
    expect(snapshot.shots).toHaveLength(1)
    expect(snapshot.shots[0]).toMatchObject({
      code: 'SH-010',
      title: 'An old lighthouse on a cliff in a storm',
      durationFrames: 108,
      camera: '',
      action: '',
    })

    // Not the sample's illustrated demo art, and no video controls before there
    // is a picture to start a video from.
    await expect(page.getByTestId('shot-workspace').getByTestId('empty-frame').first()).toBeVisible()
    await expect(page.getByTestId('shot-workspace').locator('.artwork:not(.artwork-empty)')).toHaveCount(0)
    await expect(page.getByRole('region', { name: 'Video endpoints' })).toHaveCount(0)
  } finally {
    await restoreProject(page, original.id, name)
  }
})

test('with no generator ready, generating explains itself and leads to working switches', async ({ page }) => {
  const before = await (await page.request.get('/api/projects')).json() as ProjectRow[]
  const original = before.find(project => project.isActive)!
  const name = `No Engine ${Date.now()}`
  const setupBefore = await (await page.request.get('/api/setup/generation')).json()
  const restoreSetup = () => page.request.put('/api/setup/generation', { headers: { 'X-Storyboard-Studio': '1' }, data: {
    comfyUiEndpoint: setupBefore.comfyUi.endpoint, comfyUiImagesEnabled: setupBefore.comfyUi.imagesEnabled,
    comfyUiVideoEnabled: setupBefore.comfyUi.videoEnabled, codexOneClickImages: setupBefore.codex.oneClickImages,
  } })
  try {
    const created = await (await page.request.post('/api/projects', { data: {
      name, production: name, sequenceCode: 'SQ-01', sequenceName: 'Sequence 1',
      framesPerSecond: 24, aspectRatio: '16:9', deliveryWidth: 1920, deliveryHeight: 1080,
    } })).json()
    await page.request.post(`/api/projects/${created.id}/activate`)
    await page.request.post('/api/shots', { data: {
      code: 'SH-010', title: 'Lighthouse', description: 'A lighthouse in a storm.', durationFrames: 72,
      camera: '', action: '', referenceIds: [], constraints: [],
    } })
    await page.goto('/')
    await page.getByRole('button', { name: /Open SH-010,/ }).click()

    // The sandboxed service keeps ComfyUI pinned off and has no Codex, so the
    // one-click buttons say so up front instead of failing after a click.
    const notice = page.getByTestId('generation-setup-notice')
    await expect(notice).toContainText('No image generator is set up yet')
    await expect(notice).toContainText('You can still sketch this shot')
    await expect(page.getByRole('button', { name: 'Generate fast draft' })).toBeDisabled()
    await expect(page.getByRole('button', { name: 'Generate with Codex' })).toBeDisabled()

    await notice.getByRole('button', { name: 'Set up image generation' }).click()
    const drawer = page.getByTestId('setup-drawer')
    await expect(drawer.getByTestId('generation-settings')).toBeVisible()
    // Pinned by the launcher: shown, locked, and explained.
    const comfySwitch = drawer.getByRole('switch', { name: /Make draft images with ComfyUI/ })
    await expect(comfySwitch).toBeDisabled()
    await expect(drawer.getByText(/Set outside Framewright/).first()).toBeVisible()

    // Not pinned: the Codex switch saves and the server reports it.
    const codexSwitch = drawer.getByRole('switch', { name: /One-click Codex images/ })
    await expect(codexSwitch).toBeEnabled()
    await expect(codexSwitch).not.toBeChecked()
    await codexSwitch.check()
    await expect(drawer.getByText('Unsaved generation changes')).toBeVisible()
    await drawer.getByRole('button', { name: 'Save generation settings' }).click()
    await expect(drawer.getByText('Generation settings saved')).toBeVisible()
    const saved = await (await page.request.get('/api/setup/generation')).json()
    expect(saved.codex.oneClickImages).toBe(true)

    // A connection test that fails says so in plain words.
    await drawer.getByRole('button', { name: 'Test connection' }).click()
    await expect(drawer.getByText(/Nothing answered at this address|must run on this computer/)).toBeVisible()
  } finally {
    await restoreSetup()
    await restoreProject(page, original.id, name)
  }
})

test('the studio page is revalidated so an update never opens on stale assets', async ({ page }) => {
  // index.html names the build's hashed script and stylesheet. A cached copy
  // from the previous build points at files that no longer exist.
  for (const path of ['/', '/shots/any-client-route']) {
    const response = await page.request.get(path)
    expect(response.ok()).toBeTruthy()
    expect(response.headers()['cache-control']).toContain('no-cache')
  }
})
