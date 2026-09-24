import { expect, test } from '@playwright/test'

test('a view that fails to render says so and the rest of the studio keeps working', async ({ page }) => {
  // One malformed shot makes the board throw while rendering. Before the
  // boundary this unmounted everything and left a blank page.
  const real = await (await page.request.get('/api/studio')).json()
  const broken = { ...real, shots: real.shots.map((shot: Record<string, unknown>, index: number) => index === 0 ? { ...shot, approval: null } : shot) }
  await page.route('**/api/studio', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(broken) }))
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })

  await page.goto('/')
  const failure = page.getByTestId('view-failure')
  await expect(failure).toBeVisible()
  await expect(failure).toContainText('This view stopped working')
  await expect(failure).toContainText('Your saved work is safe')
  await expect(failure.getByRole('button', { name: 'Reload Framewright' })).toBeVisible()
  // The shell is still there, and another workspace renders normally.
  await expect(page.getByTestId('project-switcher')).toBeVisible()
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await expect(failure).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Create new' })).toBeVisible()
  expect(errors.some(text => text.includes('Framewright view failed to render'))).toBe(true)
})
