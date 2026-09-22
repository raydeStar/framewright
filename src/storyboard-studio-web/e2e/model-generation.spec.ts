import { expect, test, type Page } from '@playwright/test'

/**
 * A reference image becomes a model candidate, in the real application, with a
 * real job queue and a real model import. The compiler is a controlled
 * stand-in rather than an hour of GPU work, which is exactly what M07 asks for:
 * what is being proved here is the studio's behaviour, not the compiler's.
 */
function failOnConsoleErrors(page: Page, allowed: RegExp[] = []) {
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('pageerror', error => errors.push(error.message))
  return () => {
    const unexpected = errors.filter(message => !allowed.some(pattern => pattern.test(message)))
    expect(unexpected, `Browser errors: ${unexpected.join('\n')}`).toEqual([])
  }
}

/** A reference nobody else in this run owns; the store is content addressed. */
function ownReference(label: string) {
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64')
  return Buffer.concat([png, Buffer.from(`model-generation-${label}`, 'utf8')])
}

test('a failed readiness check stops loading and can be retried without queuing work', async ({ page }, testInfo) => {
  const referenceName = `readiness-error-${testInfo.project.name}`
  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.locator('.asset-hero input[type="file"]').setInputFiles({
    name: `${referenceName}.png`, mimeType: 'image/png', buffer: ownReference(referenceName),
  })
  await expect(page.getByText('1 asset imported into the library.')).toBeVisible()
  await page.route('**/api/models/generation/readiness', route => route.fulfill({
    status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'QC readiness unavailable' }),
  }))
  await page.getByRole('button', { name: /^Images/ }).click()
  await page.getByRole('button', { name: `Open ${referenceName}` }).click()
  const panel = page.getByTestId('model-from-reference')
  await expect(panel.getByRole('alert')).toContainText('QC readiness unavailable')
  await expect(panel).not.toContainText('Asking the compiler')
  await page.unroute('**/api/models/generation/readiness')
  await panel.getByRole('button', { name: 'Retry readiness check' }).click()
  await expect(panel.getByTestId('model-generation-readiness')).toHaveAttribute('data-can-run', 'true')
  const snapshot = await (await page.request.get('/api/studio')).json()
  expect(snapshot.jobs.some((job: { shotCode: string }) => job.shotCode === referenceName)).toBe(false)
})

test('a reference becomes a model candidate that names where it came from', async ({ page }, testInfo) => {
  // This journey waits for a deliberately external-looking queue. The poll's
  // 60-second contract must remain reachable on a contended hosted runner.
  test.setTimeout(90_000)
  const verifyConsole = failOnConsoleErrors(page, [/due to access control checks/, /TypeError: Load failed/])
  const label = testInfo.project.name
  const referenceName = `model-source-${label}`

  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.locator('.asset-hero input[type="file"]').setInputFiles({
    name: `${referenceName}.png`, mimeType: 'image/png', buffer: ownReference(label),
  })
  await expect(page.getByText('1 asset imported into the library.')).toBeVisible()

  await page.getByRole('button', { name: /^Images/ }).click()
  await page.getByRole('button', { name: `Open ${referenceName}` }).click()

  // The studio asks the compiler what it can do before offering the work.
  const panel = page.getByTestId('model-from-reference')
  await expect(panel.getByTestId('model-generation-readiness')).toHaveAttribute('data-can-run', 'true')
  await expect(panel).toContainText('The compiler does this work, not this studio')

  // A generator normalises, so it has to be told how big the thing is — in
  // the only terms most people can answer, not in metres.
  await expect(panel.getByTestId('model-generate')).toBeDisabled()
  await panel.getByTestId('model-size').selectOption('knee')
  await expect(panel.getByTestId('model-generate')).toBeEnabled()
  // Glass is offered and only offered: nothing in a mesh says which faces are
  // panes, so the default answer is that there are none.
  await expect(panel.getByTestId('model-glass')).toHaveValue('')
  await panel.getByTestId('model-glass').selectOption('teal')
  // How close the camera gets is asked in those terms, and set dressing is
  // the answer unless somebody says otherwise: a hero costs minutes more.
  await expect(panel.getByTestId('model-detail')).toHaveValue('set')
  await expect(panel.getByTestId('model-detail').locator('option')).toHaveCount(2)
  await panel.getByTestId('model-generate').click()
  await expect(page.getByText(/keeps going if you leave this screen/)).toBeVisible()

  // The queue carries it. Nobody watches: the artist can be anywhere.
  await page.getByRole('button', { name: 'Board', exact: true }).click()

  const delivered = await expect.poll(async () => {
    const jobs = await (await page.request.get('/api/studio')).json()
    const job = jobs.jobs.find((candidate: { workType: string; shotCode: string }) =>
      candidate.workType === 'Model' && candidate.shotCode === referenceName)
    return job?.state
  }, { timeout: 60_000, intervals: [250] }).toBe('Completed')
  expect(delivered).toBeUndefined()

  const snapshot = await (await page.request.get('/api/studio')).json()
  const job = snapshot.jobs.find((candidate: { workType: string; shotCode: string }) =>
    candidate.workType === 'Model' && candidate.shotCode === referenceName)
  expect(job.progress).toBe(100)
  expect(job.outputAssetId).toBeTruthy()

  // What arrived is a real model: it inspects, and it carries its rig.
  const profile = await (await page.request.get(`/api/assets/${job.outputAssetId}/model-profile`)).json()
  expect(profile.rig.boneCount).toBe(17)
  expect(profile.rig.fingerprint).toBe('123be375436770f873a157e54ca00299d417bbb08a79a0abe6d39e0ace223ea7')

  // And it says which reference it came from, in the library, in words.
  const assets = await (await page.request.get('/api/assets')).json()
  const model = assets.find((asset: { id: string }) => asset.id === job.outputAssetId)
  expect(model.kind).toBe('Model')
  expect(model.notes).toContain(referenceName)
  expect(model.notes).toContain('Reference Asset Compiler')

  // The artist can open it and read the same lineage on screen. It is opened by
  // the name the library actually gave it: the store is content addressed, so a
  // second run delivering the same bytes delivers the same asset rather than a
  // second copy of it, and that asset keeps the name it was first given.
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.getByRole('button', { name: /^Models/ }).click()
  await page.getByRole('button', { name: `Open ${model.displayName}` }).click()
  await expect(page.getByTestId('model-workspace')).toBeVisible()
  await expect(page.getByTestId('model-rig-state')).toHaveAttribute('data-ready', 'true')
  verifyConsole()
})

test('the work queue shows what is underway and survives leaving the screen', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page, [/due to access control checks/, /TypeError: Load failed/])
  const label = testInfo.project.name
  const referenceName = `queue-source-${label}`

  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.locator('.asset-hero input[type="file"]').setInputFiles({
    name: `${referenceName}.png`, mimeType: 'image/png', buffer: ownReference(`queue-${label}`),
  })
  await expect(page.getByText('1 asset imported into the library.')).toBeVisible()
  await page.getByRole('button', { name: /^Images/ }).click()
  await page.getByRole('button', { name: `Open ${referenceName}` }).click()
  await page.getByTestId('model-from-reference').getByTestId('model-size').selectOption('knee')
  await page.getByTestId('model-from-reference').getByTestId('model-generate').click()

  // Whatever the artist does next, the queue holds the work and says so.
  await page.getByRole('button', { name: 'Board', exact: true }).click()
  await expect.poll(async () => {
    const snapshot = await (await page.request.get('/api/studio')).json()
    return snapshot.jobs.some((job: { workType: string; shotCode: string; state: string }) =>
      job.workType === 'Model' && job.shotCode === referenceName && job.state === 'Completed')
  }, { timeout: 60_000, intervals: [250] }).toBe(true)

  // Reloading the studio does not lose it: the job is the service's, not the
  // page's.
  await page.reload()
  const snapshot = await (await page.request.get('/api/studio')).json()
  const job = snapshot.jobs.find((candidate: { workType: string; shotCode: string }) =>
    candidate.workType === 'Model' && candidate.shotCode === referenceName)
  expect(job.state).toBe('Completed')
  expect(job.outputAssetId).toBeTruthy()
  verifyConsole()
})
