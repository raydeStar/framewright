import { expect, test, type Page } from '@playwright/test'
import { spawn } from 'node:child_process'
import { once } from 'node:events'

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

test('a measured crate carries its actual width and closed shell from the model panel', async ({ page }, testInfo) => {
  const name = `crate-check-${testInfo.project.name}`
  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.locator('.asset-hero input[type="file"]').setInputFiles({
    name: `${name}.png`, mimeType: 'image/png', buffer: ownReference(name),
  })
  await expect(page.getByText('1 asset imported into the library.')).toBeVisible()
  await page.route('**/api/models/generation/readiness', async route => {
    const response = await route.fetch()
    await route.fulfill({ json: { ...(await response.json()), canRun: true, pixalGeometryChecks: true, pixalSolidExport: true, pixalMounts: true,
      engines: [{ engine: 'pixal3d', description: 'Pixal3D', cost: '' }] } })
  })
  await page.getByRole('button', { name: /^Images/ }).click()
  await page.getByRole('button', { name: `Open ${name}` }).click()
  const panel = page.getByTestId('model-from-reference')
  await panel.getByLabel('Measure by').selectOption('width')
  await expect(panel.getByTestId('model-generate')).toBeDisabled()
  await panel.getByLabel('Length in metres').fill('0.55')
  await expect(panel.getByLabel('Shell construction')).toHaveValue('closed')
  await panel.getByLabel('Shell construction').selectOption('solid')
  let submitted: unknown
  await page.route('**/api/models/generation', async route => {
    submitted = route.request().postDataJSON()
    await route.fulfill({ status: 422, json: { error: 'Fixture stops before generation.' } })
  })
  await panel.getByTestId('model-generate').click()
  await expect(panel.getByRole('alert')).toContainText('Fixture stops')
  expect(submitted).toMatchObject({ engine: 'pixal3d', topology: 'closed', gameExport: 'solid', sizeIs: 'width', sizeM: 0.55, size: '' })
  await panel.getByLabel('Shell construction').selectOption('open')
  await panel.getByLabel('Align to mounting feet').check()
  await panel.getByLabel('Horizontal run between feet in metres').fill('3')
  await panel.getByLabel('Rise between feet in metres').fill('2')
  await expect(panel.getByTestId('model-generate')).toBeDisabled()
  const picture = panel.getByAltText('Reference for mounting points')
  const box = await picture.boundingBox()
  if (!box) throw new Error('The mounting picture has no visible bounds')
  for (const [x, y] of [[.1, .9], [.9, .4], [.1, .6]]) {
    await picture.click({ position: { x: box.width * x, y: box.height * y } })
  }
  await expect(panel.getByText('Three mounting points marked.')).toBeVisible()
  const sent = page.waitForRequest(req => req.method() === 'POST' && req.url().endsWith('/api/models/generation'))
  await panel.getByTestId('model-generate').click()
  const frame = (await sent).postDataJSON()
  expect(frame).toMatchObject({ topology: 'open', sizeIs: 'width', sizeM: 3, mounts: { riseM: 2 } })
  for (const [name, x, y] of [['lowerFoot', .1, .9], ['upperFoot', .9, .4], ['postTop', .1, .6]] as const) {
    expect(frame.mounts[name][0]).toBeCloseTo(x, 1)
    expect(frame.mounts[name][1]).toBeCloseTo(y, 1)
  }
})

test('a direct WSL RAC worker holds a Framewright model job until it releases the GPU', async ({ page }) => {
  const guard = process.env.FRAMEWRIGHT_TEST_RAC_GUARD
  test.skip(process.platform !== 'win32' || !guard || !process.env.FRAMEWRIGHT_TEST_PYTHON
    || !process.env.RAC_GPU_LOCK_PATH, 'Opt-in Windows and Pixal WSL host test with CPU-only workers')
  test.setTimeout(100_000)
  const path = guard!.replaceAll('\\', '/').replace(/^([a-z]):/i, (_, drive: string) => `/mnt/${drive.toLowerCase()}`)
  const owner = spawn('wsl.exe', ['-d', process.env.RAC_WSL_DISTRO ?? 'Ubuntu-22.04', '--',
    'env', `RAC_GPU_LOCK_PATH=${process.env.RAC_GPU_LOCK_PATH}`, 'python3', path, '0',
    'python3', '-u', '-c', "import sys; print('reserved'); sys.stdin.readline()"], { windowsHide: true })
  const exited = once(owner, 'exit')
  try {
    expect(String((await once(owner.stdout!, 'data'))[0])).toContain('reserved')
    const name = 'local-gpu-overlap-test'
    await page.goto('/')
    await page.getByRole('button', { name: 'Assets', exact: true }).click()
    await page.locator('.asset-hero input[type="file"]').setInputFiles({
      name: `${name}.png`, mimeType: 'image/png', buffer: ownReference(name),
    })
    await page.getByRole('button', { name: /^Images/ }).click()
    await page.getByRole('button', { name: `Open ${name}` }).click()
    await page.getByTestId('model-size').selectOption('knee')
    await page.getByTestId('model-generate').click()
    const job = async () => {
      const studio = await (await page.request.get('/api/studio')).json()
      return studio.jobs.find((entry: { shotCode: string }) => entry.shotCode === name)
    }
    await expect.poll(async () => (await job())?.phase, {timeout: 40_000}).toContain('GPU reservation')
    expect((await job()).outputAssetId).toBeFalsy()
    owner.stdin!.end()
    expect((await exited)[0]).toBe(0)
    await expect.poll(async () => (await job())?.state, {timeout: 50_000}).toBe('Completed')
  } finally {
    owner.stdin!.end()
  }
})

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
