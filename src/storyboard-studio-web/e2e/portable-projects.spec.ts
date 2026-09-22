import { expect, test } from '@playwright/test'

test('the project switcher imports a verified editable package', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByTestId('project-switcher')).toBeVisible()

  const exported = await page.request.get('/api/export/working-package')
  expect(exported.ok()).toBeTruthy()
  const bytes = await exported.body()

  await page.getByTestId('project-switcher').click()
  const projectRows = page.getByTestId('project-menu').getByRole('menuitemradio')
  await expect(projectRows.first()).toBeVisible()
  const before = await projectRows.count()
  const input = page.locator('[data-testid="project-menu"] input[type="file"]')
  await input.setInputFiles({ name: 'framewright-working-copy.zip', mimeType: 'application/zip', buffer: bytes })

  await expect(page.getByText(/imported with \d+ assets and \d+ scenes/i)).toBeVisible()
  await page.getByTestId('project-switcher').click()
  await expect(page.getByTestId('project-menu').getByRole('menuitemradio')).toHaveCount(before + 1)
  await expect(page.getByTestId('project-menu').getByText(/imported \d+/i).first()).toBeVisible()
})
