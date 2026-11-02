import { expect, test } from '@playwright/test'

// The render itself is covered against real FFmpeg in the API suite; this
// journey checks what the artist sees and sends, so the renderer is stubbed.
test('export film shows each shot’s picture, sends the chosen options, and offers the finished film', async ({ page }) => {
  const jobId = '00000000-0000-0000-0000-00000000f11a'
  let requested: unknown
  let rendered = false
  await page.route('**/api/export/film/status', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    canRender: true, detail: 'Ready to render. 1 of 3 shots will hold a still or black instead of video.', totalFrames: 360, framesPerSecond: 24,
    deliveryWidth: 1920, deliveryHeight: 1080, audioClips: 2, captions: 1, approvedShots: 1,
    shots: [
      { shotId: 'a', code: 'SH-010', title: 'The Impossible Wind', durationFrames: 120, source: 'Production', detail: 'Approved Max take', assetId: 'x' },
      { shotId: 'b', code: 'SH-020', title: 'The Waiting Ships', durationFrames: 120, source: 'Take', detail: 'Latest Low video take', assetId: 'y' },
      { shotId: 'c', code: 'SH-030', title: 'Three Clicks', durationFrames: 120, source: 'Missing', detail: 'No picture yet; black for 5 s' },
    ],
  }) }))
  await page.route('**/api/export/film', async route => {
    requested = route.request().postDataJSON()
    rendered = true
    await route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({ id: jobId, shotId: '00000000-0000-0000-0000-000000000000', shotCode: 'SQ-01', kind: 'Film', workType: 'Film', state: 'Queued', progress: 0, phase: 'Film render queued', backend: 'FFmpeg film render', createdAt: new Date().toISOString(), attempt: 1 }) })
  })
  // Once rendered, the snapshot carries the finished film job.
  await page.route('**/api/studio', async route => {
    const response = await route.fetch()
    const body = await response.json()
    if (rendered) body.jobs.unshift({ id: jobId, shotId: '00000000-0000-0000-0000-000000000000', shotCode: 'SQ-01', kind: 'Film', workType: 'Film', state: 'Completed', progress: 100, phase: 'Film ready', backend: 'FFmpeg film render', createdAt: new Date().toISOString(), completedAt: new Date().toISOString(), outputAssetId: 'f', outputAssetUrl: '/api/assets/f/content', attempt: 1 })
    await route.fulfill({ response, json: body })
  })

  await page.goto('/')
  await page.getByRole('button', { name: 'Sequence' }).click()
  await page.getByRole('button', { name: 'Export film' }).click()
  const dialog = page.getByRole('dialog', { name: 'Export film' })
  const sources = dialog.getByRole('list', { name: 'Picture used for each shot' }).getByRole('listitem')
  await expect(sources).toHaveCount(3)
  await expect(sources.nth(0)).toContainText('Max take')
  await expect(sources.nth(1)).toContainText('Latest Low video take')
  await expect(sources.nth(2)).toContainText('No picture')
  await expect(dialog).toContainText('15.0 s · 1920 × 1080 · 24 fps · 2 audio clips · 1 caption')

  await dialog.getByRole('checkbox', { name: /Fade in and out/ }).uncheck()
  await dialog.getByRole('button', { name: 'Render film' }).click()
  expect(requested).toEqual({ fades: false, captions: true, normalizeLoudness: true })

  const ready = dialog.getByTestId('film-ready')
  await expect(ready).toContainText('Film ready')
  await expect(ready.getByRole('link', { name: 'Download film' })).toHaveAttribute('href', '/api/assets/f/content')
  await expect(dialog.getByRole('button', { name: 'Render again' })).toBeEnabled()
})
