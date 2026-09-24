import { expect, test } from '@playwright/test'

// R07: nothing the artist made disappears without a question, and no button
// leads nowhere.

test('switching away from a scene with unsaved changes asks first', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('button', { name: 'Scene', exact: true }).click()
  let creates = 0
  page.on('request', request => { if (request.url().endsWith('/api/scenes') && request.method() === 'POST') creates += 1 })
  const firstCreated = page.waitForResponse(response => response.url().endsWith('/api/scenes') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'New scene' }).click()
  const first = await (await firstCreated).json()
  const name = page.getByLabel('Scene name', { exact: true })
  await expect(name).toHaveValue(first.name)

  await name.fill('Unsaved lighthouse blocking')
  await page.getByRole('button', { name: 'New scene' }).click()
  const confirm = page.getByTestId('confirm-dialog')
  await expect(confirm).toContainText('Discard unsaved scene changes?')
  await expect(confirm.getByRole('button', { name: 'Keep editing' })).toBeFocused()
  await confirm.getByRole('button', { name: 'Keep editing' }).click()
  await expect(confirm).toHaveCount(0)
  await expect(name).toHaveValue('Unsaved lighthouse blocking')
  expect(creates).toBe(1)

  const secondCreated = page.waitForResponse(response => response.url().endsWith('/api/scenes') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'New scene' }).click()
  await confirm.getByRole('button', { name: 'Discard changes' }).click()
  const second = await (await secondCreated).json()
  await expect(name).toHaveValue(second.name)
  expect(creates).toBe(2)
})

test('the sequence describes this project, not the demo', async ({ page }) => {
  const snapshot = await (await page.request.get('/api/studio')).json()
  await page.goto('/')
  await page.getByRole('button', { name: new RegExp(`Open ${snapshot.shots[0].code},`) }).click()
  await page.getByRole('button', { name: 'Sequence', exact: true }).click()
  const sequence = page.getByTestId('sequence-workspace')
  // The notes panel is folded away on narrow screens; its text must still be this project's.
  await expect(sequence.locator('.sequence-notes h3')).toHaveText(`${snapshot.project.sequenceCode} · ${snapshot.project.sequenceName}`)
  await expect(sequence.getByText(/restrained approach/)).toHaveCount(0)
})
