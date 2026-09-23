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
