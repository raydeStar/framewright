import { expect, test, type Page } from '@playwright/test'

type ProjectRow = { id: string; name: string; isActive: boolean }
type Candidate = { id: string; version: number; isCurrent: boolean; approval: string; assetId?: string; width?: number; height?: number }

async function restoreProject(page: Page, originalId: string, createdName: string) {
  await page.request.post(`/api/projects/${originalId}/activate`)
  const projects = await (await page.request.get('/api/projects')).json() as ProjectRow[]
  const created = projects.find(project => project.name === createdName)
  if (created) await page.request.delete(`/api/projects/${created.id}?acceptRatifiedLoss=true`)
}

test('a camera move sets both video endpoints from one image without generating anything', async ({ page }) => {
  const before = await (await page.request.get('/api/projects')).json() as ProjectRow[]
  const original = before.find(project => project.isActive)!
  const name = `Camera Move ${Date.now()}`
  try {
    const created = await (await page.request.post('/api/projects', { data: {
      name, production: name, sequenceCode: 'SQ-01', sequenceName: 'Sequence 1',
      framesPerSecond: 24, aspectRatio: '16:9', deliveryWidth: 1920, deliveryHeight: 1080,
    } })).json()
    await page.request.post(`/api/projects/${created.id}/activate`)
    await page.goto('/')
    // A real, decodable 1280x720 picture, drawn and uploaded by the page itself.
    const imageId = await page.evaluate(async () => {
      const canvas = document.createElement('canvas'); canvas.width = 1280; canvas.height = 720
      const context = canvas.getContext('2d')!
      const gradient = context.createLinearGradient(0, 0, 1280, 0); gradient.addColorStop(0, '#203040'); gradient.addColorStop(1, '#e0a050')
      context.fillStyle = gradient; context.fillRect(0, 0, 1280, 720)
      const blob = await new Promise<Blob>(resolve => canvas.toBlob(value => resolve(value!), 'image/png'))
      const form = new FormData(); form.append('file', blob, 'deck.png')
      const response = await fetch('/api/assets/images', { method: 'POST', headers: { 'X-Storyboard-Studio': '1' }, body: form })
      return (await response.json()).id as string
    })
    const shot = await (await page.request.post('/api/shots', { data: {
      code: 'SH-040', title: 'Go in', description: 'Two sailors on a deck at dusk.', durationFrames: 48,
      camera: '35mm', action: 'She speaks once; he looks past the bow.', referenceIds: [], constraints: [], initialImageAssetId: imageId,
    } })).json()
    await page.reload()
    await page.getByRole('button', { name: /Open SH-040,/ }).click()
    const jobsBefore = (await (await page.request.get('/api/studio')).json()).jobs.length

    await page.getByRole('button', { name: 'Create Last Frame from Start Frame' }).click()
    const dialog = page.getByRole('dialog', { name: 'Create Last Frame' })
    await dialog.getByRole('radio', { name: /Move the camera/ }).click()
    await dialog.getByRole('radio', { name: /Truck right/ }).click()
    await expect(dialog.getByText('Travels 13% of the frame')).toBeVisible()
    const slider = dialog.getByLabel('How far the camera moves')
    await slider.focus()
    await page.keyboard.press('ArrowRight', { delay: 10 })
    for (let step = 1; step < 10; step++) await page.keyboard.press('ArrowRight')
    await expect(slider).toHaveValue('25')
    await expect(dialog.getByText('Travels 20% of the frame')).toBeVisible()
    await expect(dialog.getByTestId('camera-move-stage').locator('rect.camera-window')).toHaveCount(2)
    await dialog.getByRole('button', { name: /Set truck right endpoints/ }).click()
    await expect(dialog).toHaveCount(0)

    // Both endpoints are new working windows; the live frame is untouched and
    // no job was queued.
    const studio = await (await page.request.get('/api/studio')).json()
    const saved = studio.shots.find((item: { id: string }) => item.id === shot.id)
    expect(saved.currentAssetId).toBe(imageId)
    expect(studio.jobs.length).toBe(jobsBefore)
    const candidates = await (await page.request.get(`/api/shots/${shot.id}/candidates`)).json() as Candidate[]
    const start = candidates.find(item => item.id === saved.videoFirstFrameCandidateId)!
    const last = candidates.find(item => item.id === saved.videoLastFrameCandidateId)!
    for (const window of [start, last]) {
      expect(window.isCurrent).toBe(false)
      expect(window.assetId).not.toBe(imageId)
      // 25% travel crops each window to 80% of the 1280x720 source.
      expect([window.width, window.height]).toEqual([1024, 576])
    }
    const strip = page.getByRole('region', { name: 'Video endpoints' })
    await expect(strip.getByLabel('Shot Start Frame')).toHaveValue(start.id)
    await expect(strip.getByLabel('Shot Last Frame')).toHaveValue(last.id)

    // The next video brief describes the same move.
    await page.getByRole('button', { name: 'Generate video from this image' }).click()
    await expect(page.getByRole('dialog').getByRole('textbox', { name: 'Motion and directing brief' })).toHaveValue(/Slow lateral truck right at constant framing/)
  } finally {
    await restoreProject(page, original.id, name)
  }
})
