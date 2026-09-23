import { expect, test, type Page } from '@playwright/test'
import AxeBuilder from '@axe-core/playwright'

function failOnConsoleErrors(page: Page, allowed: RegExp[] = []) {
  const errors: string[] = []
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  page.on('pageerror', error => errors.push(error.message))
  return () => {
    const unexpected = errors.filter(message => !allowed.some(pattern => pattern.test(message)))
    expect(unexpected, `Browser errors: ${unexpected.join('\n')}`).toEqual([])
  }
}

async function expectNoHorizontalPageOverflow(page: Page) {
  const dimensions = await page.evaluate(() => ({ width: document.documentElement.clientWidth, scrollWidth: document.documentElement.scrollWidth }))
  expect(dimensions.scrollWidth).toBeLessThanOrEqual(dimensions.width + 1)
}

async function expectNoSeriousAccessibilityViolations(page: Page) {
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag22aa']).analyze()
  const violations = results.violations.filter(violation => violation.impact === 'critical' || violation.impact === 'serious')
  expect(violations, violations.map(item => `${item.id}: ${item.help}`).join('\n')).toEqual([])
}

async function imageDataTransfer(page: Page, fileName = 'dropped-frame.png') {
  return page.evaluateHandle(({ name }) => {
    const binary = atob('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=')
    const bytes = Uint8Array.from(binary, character => character.charCodeAt(0))
    const transfer = new DataTransfer()
    transfer.items.add(new File([bytes], name, { type: 'image/png' }))
    return transfer
  }, { name: fileName })
}

// The one-click generate buttons are disabled while an engine reports it
// cannot run, and the sandboxed e2e service keeps every real engine off. Tests
// that exercise the button-to-request wiring (with the request itself mocked)
// report both engines ready first.
async function mockReadyOneClickEngines(page: Page) {
  await page.route('**/api/generation/adapters', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
    { id: 'comfyui-fast-draft', name: 'ComfyUI fast draft', kind: 'Local service', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['FastDraft'], purposes: ['Draft'] },
    { id: 'codex-imagegen', name: 'Codex ImageGen', kind: 'ChatGPT login', state: 'Connected', detail: 'Test adapter', canDispatch: true, routes: ['PrecisionDraft'], purposes: ['Draft', 'Final'] },
  ]) }))
}

async function mockReadyPreflight(page: Page) {
  await page.route('**/api/manifests/*/preflight?*', route => {
    const url = new URL(route.request().url())
    const adapterId = url.searchParams.get('adapterId') ?? 'test-adapter'
    const manifestId = url.pathname.split('/').at(-2) ?? crypto.randomUUID()
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
      manifestId, manifestHash: 'a'.repeat(64), adapterId, adapterName: adapterId,
      route: adapterId.includes('openai') || adapterId.includes('codex') ? 'PrecisionDraft' : 'FastDraft', purpose: 'Draft',
      workflowId: 'browser-proof', workflowName: 'Curated browser proof', workflowCapabilities: ['text-to-image', 'image-edit'],
      maxReferenceImages: 3, hasComposition: true, references: [], constraints: [],
      deliveryWidth: 2304, deliveryHeight: 960, framesPerSecond: 24, colorSpace: 'Rec.709', audioSampleRate: 48000,
      checks: [{ state: 'Pass', title: 'Browser contract', detail: 'The deterministic browser route is ready.' }], ready: true,
    }) })
  })
}

test('desktop shot-development path is complete and calm', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const pinNote = `Preserve the third chain silhouette through the haze. ${testInfo.project.name}-${Date.now()}`
  await page.goto('/')

  await expect(page).toHaveTitle('Framewright')
  const framewrightHome = page.getByRole('button', { name: 'Framewright home' })
  if (await framewrightHome.count()) await expect(framewrightHome).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Shot board' })).toBeVisible()
  expect(await page.locator('.shot-card').count()).toBeGreaterThanOrEqual(6)
  await expectNoHorizontalPageOverflow(page)

  await page.getByRole('button', { name: /Open SH-020, The chain court/ }).click()
  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  await expect(page.getByText('Shot references')).toBeHidden()
  await page.getByRole('tab', { name: /References/ }).click()
  await expect(page.getByText('Shot references')).toBeVisible()

  await page.getByRole('button', { name: 'Place comment' }).click()
  await page.getByTestId('shot-canvas').focus()
  await page.getByTestId('shot-canvas').press('Enter')
  await page.getByPlaceholder(/Be specific/).fill(pinNote)
  await page.getByRole('button', { name: 'Pin feedback' }).click()
  await expect(page.getByText('Feedback pinned to this exact version.')).toBeVisible()
  const movablePin = page.getByRole('button', { name: new RegExp(pinNote.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) })
  const pinBefore = await movablePin.boundingBox()
  expect(pinBefore).not.toBeNull()
  await page.mouse.move(pinBefore!.x + pinBefore!.width / 2, pinBefore!.y + pinBefore!.height / 2)
  await page.mouse.down()
  await page.mouse.move(pinBefore!.x + 85, pinBefore!.y + 42, { steps: 8 })
  await page.mouse.up()
  // The pointer target is 85 px from the pin's left edge; depending on the
  // viewport's fractional device scale, the resulting left-edge delta can be
  // exactly 35 px. React may also commit the pointer-move state one frame after
  // mouseup on the emulated tablet, so poll the visible pin instead of reading a
  // stale pre-commit box.
  await expect.poll(async () => (await movablePin.boundingBox())?.x ?? 0).toBeGreaterThanOrEqual(pinBefore!.x + 30)
  await expect.poll(async () => {
    const state = await (await page.request.get('/api/studio')).json()
    return state.comments.find((comment: { body: string }) => comment.body === pinNote)?.x
  }).toBeGreaterThan(.53)

  await page.getByRole('button', { name: 'Review', exact: true }).click()
  await expect(page.getByTestId('review-workspace')).toBeVisible()
  await expect(page.getByText('Continuity preflight')).toBeVisible()
  await expect(page.getByText(/never claims to have inspected pixels/i)).toBeVisible()
  const wipe = page.getByRole('slider', { name: 'Comparison wipe' })
  await expect(wipe).toHaveAttribute('aria-valuenow', '52')
  await wipe.press('ArrowRight')
  await expect(wipe).toHaveAttribute('aria-valuenow', '54')
  await page.getByRole('button', { name: 'Side by side' }).click()
  await expect(page.locator('.compare-side-by-side')).toBeVisible()
  await expectNoHorizontalPageOverflow(page)
  await expectNoSeriousAccessibilityViolations(page)
  const finalState = await (await page.request.get('/api/studio')).json()
  const createdComment = finalState.comments.find((comment: { body: string }) => comment.body === pinNote)
  if (createdComment) await page.request.post(`/api/comments/${createdComment.id}/resolve`)
  verifyConsole()
})

test('service-managed OpenAI credentials never expose unsupported browser controls', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.route('**/api/pairing/status', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    lanEnabled: false,
    isLoopback: true,
    isPaired: false,
    workstation: 'container-host',
    securityNote: 'Loopback-only service boundary.',
  }) }))
  await page.route('**/api/credentials/openai', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    isConfigured: false,
    source: 'Not configured',
    canManageHere: false,
    detail: 'Set OPENAI_API_KEY in the service environment.',
  }) }))

  await page.goto('/')
  await page.getByRole('button', { name: 'Local studio' }).click()
  const drawer = page.getByTestId('setup-drawer')
  await expect(drawer.getByText('Configure the service environment')).toBeVisible()
  await expect(drawer.getByText(/Set OPENAI_API_KEY in the service or container environment/)).toBeVisible()
  await expect(drawer.getByLabel('API key')).toHaveCount(0)
  await expect(drawer.getByRole('button', { name: 'Save encrypted' })).toHaveCount(0)
  await expect(drawer.getByRole('button', { name: 'Remove stored key' })).toHaveCount(0)
  verifyConsole()
})

test('global authorities are searchable and import into only the current project', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  const libraryId = '91422492-21f1-467c-86b2-4af43fbd56d9'
  let imports = 0
  await page.route('**/api/library', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
    { id: libraryId, slug: 'harbour-pilot', name: 'Harbour Pilot', category: 'Character', version: 3, description: 'Weathered airship pilot with a calm command presence.', lockedConstraint: 'Silver temple streak; brass compass pin.', accent: '#c98a61', visualVariant: 1, updatedAt: new Date().toISOString(), updateAvailable: false },
    { id: 'e968e2b2-324f-4444-a70e-4ef021d18c80', slug: 'observatory', name: 'Sky Observatory', category: 'Location', version: 2, description: 'Open observatory above the cloud deck.', lockedConstraint: 'Three lantern arches.', accent: '#6ab6cf', visualVariant: 2, updatedAt: new Date().toISOString(), importedAsReferenceId: 'observatory', importedVersion: 2, updateAvailable: false },
  ]) }))
  await page.route(`**/api/library/${libraryId}/import`, route => {
    imports += 1
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: 'harbour-pilot', name: 'Harbour Pilot', category: 'Character', version: 1, status: 'Approved', description: 'Weathered airship pilot with a calm command presence.', lockedConstraint: 'Silver temple streak; brass compass pin.', accent: '#c98a61', visualVariant: 1, openNotes: 0 }) })
  })

  await page.goto('/')
  await page.getByRole('tab', { name: /Authorities/ }).click()
  await page.getByRole('button', { name: 'Import authority' }).click()
  const dialog = page.getByRole('dialog', { name: 'Import authorities' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByText('Harbour Pilot')).toBeVisible()
  await expect(dialog.getByText('Sky Observatory')).toBeHidden()
  await dialog.getByRole('textbox', { name: 'Search global authorities' }).fill('pilot')
  await dialog.getByRole('button', { name: 'Add to project' }).click()
  await expect(page.getByText(/Harbour Pilot imported from the library as a project authority/)).toBeVisible()
  expect(imports).toBe(1)
  await expectNoHorizontalPageOverflow(page)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('open notes regenerate a placeholder revision from its clean storyboard layout', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'The shared seeded placeholder is exercised once before later candidate tests create an image asset.')
  const verifyConsole = failOnConsoleErrors(page)
  await mockReadyPreflight(page)
  let dispatchedAdapter = ''
  await page.route('**/api/generation/adapters', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
    { id: 'comfyui-fast-draft', name: 'ComfyUI fast draft', kind: 'Local service', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['FastDraft'], purposes: ['Draft'] },
    { id: 'openai-gpt-image', name: 'OpenAI GPT Image', kind: 'Cloud', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['PrecisionDraft'], purposes: ['Draft', 'Final'] },
  ]) }))
  await page.route('**/api/manifests/*/dispatch', async route => {
    dispatchedAdapter = (await route.request().postDataJSON()).adapterId
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: crypto.randomUUID(), state: 'Queued' }) })
  })

  await page.goto('/')
  await page.getByRole('button', { name: /Open SH-020, The chain court/ }).click()
  const regenerate = page.getByRole('button', { name: 'More options' })
  await expect(regenerate).toBeEnabled()
  await expect(page.getByText(/Open notes are revision instructions/)).toBeVisible()
  await regenerate.click()

  const dialog = page.getByRole('dialog', { name: 'Regenerate from feedback' })
  await expect(dialog.getByText(/send this clean storyboard layout/i)).toBeVisible()
  await expect(dialog.getByText(/Simplified figures guide blocking only/i)).toBeVisible()
  await expect(dialog.getByRole('radio', { name: /ComfyUI fast draft.*selected ComfyUI workflow/ })).toHaveAttribute('aria-checked', 'true')
  await dialog.getByRole('button', { name: 'Regenerate with ComfyUI fast draft' }).click()
  await expect(dialog).toBeHidden()
  expect(dispatchedAdapter).toBe('comfyui-fast-draft')

  const snapshot = await (await page.request.get('/api/studio')).json()
  const shot = snapshot.shots.find((item: { code: string }) => item.code === 'SH-020')
  const manifests = await (await page.request.get(`/api/shots/${shot.id}/manifests`)).json()
  expect(manifests[0].compositionAssetId).not.toBeNull()
  expect(manifests[0].creativeBrief).toContain('STORYBOARD COMPOSITION GUIDE')
  expect(manifests[0].creativeBrief).toContain('Keep the third chain readable')
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('Codex ImageGen starts directly from the shot panel without a handoff modal', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  let request: Record<string, unknown> | undefined
  await mockReadyOneClickEngines(page)
  await page.route('**/api/shots/*/generate-draft', async route => {
    request = await route.request().postDataJSON()
    await route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({ id: crypto.randomUUID(), state: 'Queued' }) })
  })

  await page.goto('/')
  await page.getByRole('button', { name: /Open SH-020,/ }).click()
  await page.getByRole('button', { name: /Regenerate with Codex/ }).click()
  await expect.poll(() => request?.adapterId).toBe('codex-imagegen')
  expect(request?.allowSketchCompositionFallback).toBe(false)
  expect(String(request?.creativeBriefOverride)).toContain('PINNED FEEDBACK ON THIS REVISION')
  await expect(page.getByRole('dialog', { name: 'Regenerate from feedback' })).toHaveCount(0)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('sketch lab persists composition and prepares an immutable route manifest', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const shotCode = testInfo.project.name === 'tablet' ? 'SH-050' : 'SH-030'
  const note = `${shotCode} profile blocking`
  await page.goto('/')
  await page.getByRole('button', { name: new RegExp(`Open ${shotCode},`) }).click()
  await page.locator('.shot-toolbar-actions').getByRole('button', { name: 'Sketch' }).click()

  await expect(page.getByTestId('sketch-workspace')).toBeVisible()
  await expect(page.getByRole('button', { name: /Fast Draft/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /Fast Draft/ })).toContainText('ComfyUI Text Draft · Text to image')
  await page.getByRole('button', { name: 'Text tool' }).click()
  await page.locator('.sketch-stage canvas').click({ position: { x: 300, y: 150 } })
  await page.getByRole('textbox', { name: 'Sketch text' }).fill(note)
  await page.getByRole('textbox', { name: 'Sketch text' }).press('Enter')
  await expect(page.getByText(note, { exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Pen tool' }).click()
  const box = await page.getByTestId('sketch-canvas').boundingBox()
  expect(box).not.toBeNull()
  await page.mouse.move(box!.x + box!.width * .2, box!.y + box!.height * .7)
  await page.mouse.down()
  await page.mouse.move(box!.x + box!.width * .35, box!.y + box!.height * .35, { steps: 6 })
  await page.mouse.move(box!.x + box!.width * .5, box!.y + box!.height * .68, { steps: 6 })
  await page.mouse.up()
  await expect(page.getByRole('button', { name: 'Undo sketch action' })).toBeEnabled()
  await expect(page.getByText(/Saved to studio/)).toBeVisible()
  await expect(page.getByRole('button', { name: /Fast Draft/ })).toContainText('ComfyUI Image Edit · Sketch guided')

  await page.getByRole('button', { name: /Precision image/ }).click()
  const engines = page.getByRole('radiogroup', { name: 'Precision image engine' })
  await expect(engines.getByRole('radio', { name: /Codex ImageGen.*Codex ChatGPT login/ })).toBeVisible()
  await engines.getByRole('radio', { name: /OpenAI GPT Image.*requires a project API key/ }).click()
  await page.getByRole('button', { name: 'Generate draft with OpenAI GPT Image' }).click()
  await expect(page.getByRole('alert')).toContainText(/project API key|not ready/i)
  const snapshot = await (await page.request.get('/api/studio')).json()
  const shot = snapshot.shots.find((item: { code: string }) => item.code === shotCode)
  const manifests = await (await page.request.get(`/api/shots/${shot.id}/manifests`)).json()
  expect(manifests[0].route).toBe('PrecisionDraft')
  expect(manifests[0].providerCallMade).toBe(false)
  expect(manifests[0].compositionAssetHash).toHaveLength(64)
  expect(manifests[0].manifestHash).toHaveLength(64)
  await expectNoHorizontalPageOverflow(page)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('blocking kit stages distinct full-body people with editable poses and durable authority assignments', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const snapshot = await (await page.request.get('/api/studio')).json()
  const guard = snapshot.references.find((item: { id: string }) => item.id === 'guards')
  expect(guard).toBeTruthy()
  const code = testInfo.project.name === 'tablet' ? 'SH-P-T' : 'SH-P-D'
  const created = await (await page.request.post('/api/shots', { data: {
    code, title: 'Pose blocker proof', description: 'Two ceremonial guards hold the entrance.', durationFrames: 72,
    camera: '35mm equivalent · full-body two-shot · locked', action: 'Both guards stand at attention.', referenceIds: [guard.id], constraints: ['Keep both guards fully visible.'],
  } })).json()

  await page.goto('/')
  await page.getByRole('button', { name: new RegExp(`Open ${code},`) }).click()
  await page.locator('.shot-toolbar-actions').getByRole('button', { name: 'Sketch' }).click()
  await page.getByRole('button', { name: 'Open blocking kit' }).click()
  const canvas = page.getByTestId('sketch-canvas')
  const canvasBox = await canvas.boundingBox()
  expect(canvasBox).not.toBeNull()
  if (testInfo.project.name === 'tablet') await page.getByRole('button', { name: 'Poseable person' }).click()
  else await page.getByRole('button', { name: 'Poseable person' }).dragTo(canvas, { targetPosition: { x: Math.round(canvasBox!.width * .28), y: Math.round(canvasBox!.height * .55) } })
  await expect(page.getByRole('region', { name: 'Selected blocking object' })).toBeVisible()
  await page.getByLabel('Name').fill('Left ceremonial guard')
  await page.getByLabel('Pose', { exact: true }).selectOption('Attention')
  await page.getByLabel('Appearance / role authority').selectOption(guard.id)
  await expect(page.getByText('Keep full body in frame')).toBeVisible()
  await page.getByRole('button', { name: 'Duplicate' }).click()
  await page.getByLabel('Name').fill('Right ceremonial guard')

  const wrist = page.locator('.joint-handle[data-joint="rightWrist"]')
  const wristBox = await wrist.boundingBox()
  expect(wristBox).not.toBeNull()
  await page.mouse.move(wristBox!.x + wristBox!.width / 2, wristBox!.y + wristBox!.height / 2)
  await page.mouse.down()
  await page.mouse.move(wristBox!.x + 28, wristBox!.y - 12, { steps: 4 })
  await page.mouse.up()
  await expect(page.getByLabel('Pose', { exact: true })).toHaveValue('Custom')
  const savedPoseName = `Ceremonial wrist ${testInfo.project.name}-${Date.now()}`
  await page.getByRole('button', { name: 'Save current' }).click()
  await page.getByLabel('New pose name').fill(savedPoseName)
  await page.getByRole('button', { name: 'Save pose' }).click()
  await expect(page.getByLabel('Saved poses')).toContainText(savedPoseName)
  const savedPresets = await (await page.request.get('/api/pose-presets')).json()
  expect(savedPresets).toContainEqual(expect.objectContaining({ name: savedPoseName }))
  const savedPreset = savedPresets.find((item: { name: string }) => item.name === savedPoseName)
  await page.getByLabel('Saved poses').selectOption(savedPreset.id)
  await page.getByRole('button', { name: 'Delete selected saved pose' }).click()
  await expect(page.getByLabel('Saved poses')).not.toContainText(savedPoseName)
  await expect(page.getByText(/Saved to studio/)).toBeVisible()

  const sketch = await (await page.request.get(`/api/shots/${created.id}/sketch`)).json()
  expect(sketch.content.objects).toHaveLength(2)
  if (testInfo.project.name === 'desktop') expect(sketch.content.objects[0].x).toBeCloseTo(.28, 1)
  expect(sketch.content.objects.every((item: { fullBody: boolean }) => item.fullBody)).toBe(true)
  expect(sketch.content.objects.every((item: { wardrobeReferenceId?: string }) => item.wardrobeReferenceId === guard.id)).toBe(true)
  expect(sketch.content.objects[1].pose).toBe('Custom')
  const manifest = await (await page.request.post(`/api/shots/${created.id}/manifests/prepare`, { data: { expectedShotVersion: created.version, expectedSketchRevision: sketch.revision, route: 'FastDraft', purpose: 'Draft' } })).json()
  expect(manifest.creativeBrief).toContain('BLOCKING OBJECT CONTRACT')
  expect(manifest.creativeBrief).toContain('Render exactly one subject or prop for every object')
  expect(manifest.creativeBrief).toContain('entire figure visible from head to toe')
  await expectNoHorizontalPageOverflow(page)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('one click generates a ComfyUI draft while manifest work stays behind the scenes', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  await mockReadyPreflight(page)
  let dispatchedAdapter = ''
  await page.route('**/api/generation/adapters', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
    { id: 'comfyui-fast-draft', name: 'ComfyUI fast draft', kind: 'Local service', state: 'Ready', detail: 'Curated workflow ready.', canDispatch: true, routes: ['FastDraft'], purposes: ['Draft'] },
    { id: 'openai-gpt-image', name: 'OpenAI GPT Image', kind: 'Cloud', state: 'Protected', detail: 'Not configured.', canDispatch: false, routes: ['PrecisionDraft'], purposes: ['Draft', 'Final'] },
  ]) }))
  await page.route('**/api/manifests/*/dispatch', async route => {
    dispatchedAdapter = (await route.request().postDataJSON()).adapterId
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: crypto.randomUUID(), shotId: crypto.randomUUID(), shotCode: 'SH-030', kind: 'Draft frame', state: 'Queued', progress: 0, phase: 'Frozen manifest queued', backend: 'ComfyUI fast draft', createdAt: new Date().toISOString() }) })
  })
  await page.goto('/')
  const code = testInfo.project.name === 'tablet' ? 'SH-050' : 'SH-030'
  await page.getByRole('button', { name: new RegExp(`Open ${code},`) }).click()
  await page.locator('.shot-toolbar-actions').getByRole('button', { name: 'Sketch' }).click()
  const brief = page.getByRole('textbox', { name: 'Creative brief' })
  await brief.fill(`${await brief.inputValue()} One-click ${testInfo.project.name} proof.`)
  await expect(page.getByText(/Saved to studio/)).toBeVisible()
  await page.getByRole('button', { name: 'Generate draft in ComfyUI' }).click()
  await expect.poll(() => dispatchedAdapter).toBe('comfyui-fast-draft')
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  verifyConsole()
})

test('command palette supports keyboard and touch entry', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.goto('/')
  if (testInfo.project.name === 'tablet') await page.getByRole('button', { name: 'Open command palette' }).click()
  else await page.keyboard.press('Control+K')
  await expect(page.getByTestId('command-palette')).toBeVisible()
  await page.getByRole('combobox', { name: 'Find a shot or workspace' }).fill('Assets')
  await expect(page.getByRole('group', { name: 'Matching workspaces' }).getByRole('option', { name: /Assets/ })).toBeVisible()
  await page.keyboard.press('Enter')
  await expect(page.getByRole('heading', { name: 'Asset library' })).toBeVisible()

  if (testInfo.project.name === 'tablet') await page.getByRole('button', { name: 'Open command palette' }).click()
  else await page.keyboard.press('Control+K')
  await page.getByRole('combobox', { name: 'Find a shot or workspace' }).fill('SH-050')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  await expect(page.locator('.canvas-caption').getByText(/SH-050/)).toBeVisible()
  verifyConsole()
})

test('candidate decisions carry unresolved instructions forward without leaking newer notes backward', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  const studio = await (await page.request.get('/api/studio')).json() as { shots: Array<{ id: string; code: string }> }
  const shotId = studio.shots.find(shot => shot.code === 'SH-030')!.id
  const existingSketch = await page.request.get(`/api/shots/${shotId}/sketch`)
  const sketch = existingSketch.status() === 204
    ? await (await page.request.put(`/api/shots/${shotId}/sketch`, { data: { expectedRevision: 0, creativeBrief: 'Candidate decision browser proof.', content: { strokes: [], labels: [] } } })).json() as { revision: number }
    : await existingSketch.json() as { revision: number }
  for (let generation = 0; generation < 2; generation++) {
    const snapshot = await (await page.request.get('/api/studio')).json() as { shots: Array<{ id: string; version: number }> }
    const version = snapshot.shots.find(shot => shot.id === shotId)!.version
    const prepared = await page.request.post(`/api/shots/${shotId}/manifests/prepare`, { data: { expectedShotVersion: version, expectedSketchRevision: sketch.revision, route: 'FastDraft', purpose: 'Draft' } })
    const manifest = await prepared.json() as { id: string; manifestHash: string }
    await page.request.post(`/api/manifests/${manifest.id}/dispatch`, { data: { expectedManifestHash: manifest.manifestHash, adapterId: 'local-proof' } })
    await expect.poll(async () => {
      const current = await (await page.request.get('/api/studio')).json() as { shots: Array<{ id: string; version: number }> }
      return current.shots.find(shot => shot.id === shotId)!.version
    }).toBeGreaterThan(version)
  }

  const archivedSnapshot = await (await page.request.get('/api/studio')).json() as { shots: Array<{ id: string; version: number }> }
  const archivedVersion = archivedSnapshot.shots.find(shot => shot.id === shotId)!.version
  await page.request.post(`/api/shots/${shotId}/comments`, { data: { x: .28, y: .32, body: 'Archived-version-only note.' } })
  const archivedPrepared = await page.request.post(`/api/shots/${shotId}/manifests/prepare`, { data: { expectedShotVersion: archivedVersion, expectedSketchRevision: sketch.revision, route: 'FastDraft', purpose: 'Draft' } })
  const archivedManifest = await archivedPrepared.json() as { id: string; manifestHash: string }
  await page.request.post(`/api/manifests/${archivedManifest.id}/dispatch`, { data: { expectedManifestHash: archivedManifest.manifestHash, adapterId: 'local-proof' } })
  await expect.poll(async () => {
    const current = await (await page.request.get('/api/studio')).json() as { shots: Array<{ id: string; version: number }> }
    return current.shots.find(shot => shot.id === shotId)!.version
  }).toBeGreaterThan(archivedVersion)
  await page.request.post(`/api/shots/${shotId}/comments`, { data: { x: .68, y: .42, body: 'Live-version-only note.' } })

  await page.goto('/')
  await page.getByRole('button', { name: /Open SH-030,/ }).click()
  const before = await (await page.request.get(`/api/shots/${shotId}/candidates`)).json() as Array<{ id: string; version: number; assetId?: string; assetUrl?: string; isCurrent: boolean }>
  const earlier = before.find(candidate => candidate.version === archivedVersion && !candidate.isCurrent && candidate.assetUrl)
  const live = before.find(candidate => candidate.isCurrent)
  expect(earlier).toBeTruthy(); expect(live).toBeTruthy()
  await expect(page.getByRole('button', { name: /Live-version-only note/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /Archived-version-only note/ })).toBeVisible()

  await page.getByRole('listbox', { name: 'Candidate versions' }).getByRole('option', { name: new RegExp(`v${earlier!.version}`) }).click()
  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  await expect(page.locator('.shot-canvas .shot-asset')).toHaveAttribute('src', earlier!.assetUrl!)
  await expect(page.getByRole('button', { name: /Live-version-only note/ })).toBeHidden()
  await expect(page.getByRole('button', { name: /Archived-version-only note/ })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Draw annotation' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Place comment' })).toBeDisabled()
  await expect(page.getByRole('textbox', { name: 'What should change?' })).toBeDisabled()
  const compare = page.getByRole('button', { name: 'Compare to latest' })
  await expect(compare).toBeVisible()
  await expect(page.getByRole('button', { name: 'Delete draft' })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Promote draft' })).toBeVisible()
  const oldVersion = Math.max(...before.map(candidate => candidate.version))
  await page.getByRole('button', { name: 'Promote draft' }).click()
  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  await expect(page.getByText(new RegExp(`promoted as new working v${oldVersion + 1}`))).toBeVisible()

  const after = await (await page.request.get(`/api/shots/${shotId}/candidates`)).json() as Array<{ id: string; version: number; assetId?: string; isCurrent: boolean }>
  const copied = after.find(candidate => candidate.isCurrent)
  expect(copied?.version).toBe(oldVersion + 1)
  expect(copied?.assetId).toBe(earlier!.assetId)

  await expect(page.getByRole('listbox', { name: 'Candidate versions' }).getByRole('option').first()).toContainText(`v${oldVersion + 1}`)
  await page.getByRole('listbox', { name: 'Candidate versions' }).getByRole('option', { name: new RegExp(`v${live!.version}`) }).click()
  await expect(page.getByRole('button', { name: /Live-version-only note/ })).toBeVisible()
  await page.getByRole('button', { name: 'Delete draft' }).click()
  await expect.poll(async () => {
    const candidates = await (await page.request.get(`/api/shots/${shotId}/candidates`)).json() as Array<{ id: string }>
    return candidates.some(candidate => candidate.id === live!.id)
  }).toBe(false)
  verifyConsole()
})

test('drawn review markup persists on the exact shot version', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const shotCode = testInfo.project.name === 'tablet' ? 'SH-050' : 'SH-040'
  await page.goto('/')
  await page.getByRole('button', { name: new RegExp(`Open ${shotCode},`) }).click()
  await page.getByRole('button', { name: 'Draw annotation' }).click()
  const canvas = page.locator('.shot-canvas canvas')
  const box = await canvas.boundingBox()
  expect(box).not.toBeNull()
  await page.mouse.move(box!.x + box!.width * .25, box!.y + box!.height * .3)
  await page.mouse.down()
  await page.mouse.move(box!.x + box!.width * .55, box!.y + box!.height * .65, { steps: 8 })
  await page.mouse.up()
  await expect(page.getByText(/AI edit guide saved/)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Undo markup' })).toBeEnabled()
  await page.getByRole('button', { name: 'Undo markup' }).click()
  await expect(page.getByRole('button', { name: 'Redo markup' })).toBeEnabled()
  await page.getByRole('button', { name: 'Redo markup' }).click()
  await expect(page.getByText(/AI edit guide saved/)).toBeVisible()

  await page.getByRole('button', { name: 'Board', exact: true }).click()
  await page.getByRole('button', { name: new RegExp(`Open ${shotCode},`) }).click()
  await expect(page.getByText(/Apply & regenerate attaches this guide/)).toBeVisible()
  verifyConsole()
})

test('shot intent and camera can be edited directly in the inspector', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const snapshot = await (await page.request.get('/api/studio')).json()
  const code = `UX_${testInfo.project.name}_${Date.now().toString().slice(-6)}`.toUpperCase()
  const createdResponse = await page.request.post('/api/shots', { data: {
    code, title: 'Inline intent proof', description: 'A guard waits beneath the arch.', durationFrames: 72,
    camera: '50mm · eye level', action: 'Hold the beat.', referenceIds: snapshot.references.slice(0, 2).map((item: { id: string }) => item.id), constraints: ['Preserve the arch geometry'],
  } })
  expect(createdResponse.ok()).toBeTruthy()

  await page.goto('/')
  await page.getByRole('button', { name: new RegExp(`Open ${code},`) }).click()
  await page.getByRole('textbox', { name: 'Shot description' }).fill('A guard recognizes the crest and steps into profile.')
  await page.getByRole('textbox', { name: 'Shot action' }).fill('Turn on the dialogue beat; hold camera position.')
  await page.getByRole('combobox', { name: 'Lens starting point' }).selectOption('70')
  await page.getByRole('combobox', { name: 'Shot size and composition' }).selectOption('profile')
  await page.getByRole('combobox', { name: 'Camera movement' }).selectOption('locked')
  await page.getByRole('button', { name: 'Action', exact: true }).click()
  await expect(page.getByText(/common starting points here are 18mm, 24mm, 28mm, 35mm/i)).toBeVisible()
  await expect(page.getByText(/Perspective comes from camera position/i)).toBeVisible()
  await expect(page.getByText('Unsaved intent changes')).toBeVisible()
  await page.getByRole('button', { name: 'Save intent' }).click()
  await expect(page.getByText(`${code} intent saved.`)).toBeVisible()
  await expect(page.getByText('Intent is saved')).toBeVisible()
  const persisted = await (await page.request.get('/api/studio')).json()
  expect(persisted.shots.find((item: { code: string }) => item.code === code).camera).toBe('70mm equivalent · profile · locked')
  verifyConsole()
})

test('shot references and rules are editable and reference pins retain exact authority context', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const snapshot = await (await page.request.get('/api/studio')).json()
  const [initialReference] = snapshot.references
  const referencePng = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64')
  const referenceUpload = await page.request.post('/api/assets/images', {
    headers: { 'X-Storyboard-Studio': '1' },
    multipart: { file: { name: `pin-authority-${testInfo.project.name}.png`, mimeType: 'image/png', buffer: referencePng } },
  })
  expect(referenceUpload.ok()).toBeTruthy()
  const referenceAsset = await referenceUpload.json()
  const addedReferenceResponse = await page.request.post('/api/references', { data: {
    name: `Pin authority ${testInfo.project.name} ${Date.now()}`, category: 'Character',
    description: 'Approved face authority for spatial placement.', lockedConstraint: 'Preserve the exact face identity.',
    accent: '#8aa7b2', imageAssetId: referenceAsset.id,
  } })
  expect(addedReferenceResponse.ok()).toBeTruthy()
  const addedReference = await addedReferenceResponse.json()
  const code = `REF_${testInfo.project.name}_${Date.now().toString().slice(-6)}`.toUpperCase()
  const createdResponse = await page.request.post('/api/shots', { data: {
    code, title: 'Authority editing proof', description: 'A portrait for authority placement.', durationFrames: 48,
    camera: '50mm equivalent · medium close-up · locked', action: 'Hold still.', referenceIds: [initialReference.id], constraints: ['Keep the face centered'],
  } })
  expect(createdResponse.ok()).toBeTruthy()

  await page.goto('/')
  await page.getByRole('button', { name: new RegExp(`Open ${code},`) }).click()
  await page.getByRole('tab', { name: /References/ }).click()
  await page.getByRole('button', { name: 'Add', exact: true }).click()
  await page.getByRole('button', { name: new RegExp(addedReference.name) }).click()
  await expect(page.getByText(`${addedReference.name} added to ${code}.`)).toBeVisible()

  const referenceCard = page.locator('.reference-edit-card').filter({ hasText: addedReference.name })
  const frame = page.getByTestId('shot-canvas')
  const frameBox = await frame.boundingBox()
  expect(frameBox).not.toBeNull()
  const pinHandle = referenceCard.getByRole('button', { name: `Place ${addedReference.name} on frame` })
  if (testInfo.project.name === 'desktop') {
    // The inspector scrolls; bring the card into view as a person would before
    // reading raw coordinates for the drag.
    await pinHandle.scrollIntoViewIfNeeded()
    const pinHandleBox = await pinHandle.boundingBox()
    expect(pinHandleBox).not.toBeNull()
    await page.mouse.move(pinHandleBox!.x + pinHandleBox!.width / 2, pinHandleBox!.y + pinHandleBox!.height / 2)
    await page.mouse.down()
    await page.mouse.move(frameBox!.x + frameBox!.width * .37, frameBox!.y + frameBox!.height * .43, { steps: 8 })
    await expect(frame).toHaveClass(/reference-drop-ready/)
    await page.mouse.up()
  } else {
    await pinHandle.click()
    await expect(page.getByText(new RegExp(`Place ${addedReference.name} v${addedReference.version}`))).toBeVisible()
    await page.mouse.click(frameBox!.x + frameBox!.width * .37, frameBox!.y + frameBox!.height * .43)
  }
  const composer = page.getByRole('dialog', { name: new RegExp(`Place ${addedReference.name}`) })
  await composer.getByRole('textbox', { name: 'Reference instruction' }).fill('Use this approved image for the face in this region.')
  await composer.getByRole('button', { name: 'Pin reference' }).click()
  await expect(page.getByRole('button', { name: new RegExp(`${addedReference.name} v${addedReference.version}`) })).toBeVisible()
  await expect(referenceCard.getByText('Use this approved image for the face in this region.')).toBeVisible()
  await referenceCard.getByRole('button', { name: `Remove ${addedReference.name} placement note` }).click()
  await expect(referenceCard.getByText('Use this approved image for the face in this region.')).toHaveCount(0)

  await pinHandle.click()
  await expect(page.getByText(new RegExp(`Place ${addedReference.name} v${addedReference.version}`))).toBeVisible()
  await page.mouse.click(frameBox!.x + frameBox!.width * .41, frameBox!.y + frameBox!.height * .47)
  const secondComposer = page.getByRole('dialog', { name: new RegExp(`Place ${addedReference.name}`) })
  await secondComposer.getByRole('textbox', { name: 'Reference instruction' }).fill('Use this approved image for the face in the revised region.')
  await secondComposer.getByRole('button', { name: 'Pin reference' }).click()

  await referenceCard.getByRole('button', { name: `Remove ${addedReference.name} from shot`, exact: true }).click()
  await expect(page.getByText(`${addedReference.name} removed from ${code}.`)).toBeVisible()
  await expect(page.getByRole('button', { name: new RegExp(`${addedReference.name} v${addedReference.version}`) })).toHaveCount(0)

  await page.getByRole('tab', { name: /Rules/ }).click()
  await page.getByRole('textbox', { name: 'Constraint 1' }).fill('Keep the face centered and both eyes readable')
  await page.getByRole('textbox', { name: 'New constraint' }).fill('Preserve the approved wardrobe silhouette')
  await page.getByRole('button', { name: 'Add', exact: true }).click()
  await page.getByRole('button', { name: 'Save rules' }).click()
  await expect(page.getByText(`${code} rules updated.`)).toBeVisible()

  const persisted = await (await page.request.get('/api/studio')).json()
  const persistedShot = persisted.shots.find((item: { code: string }) => item.code === code)
  expect(persistedShot.referenceIds).toEqual([initialReference.id])
  expect(persistedShot.constraints).toEqual(['Keep the face centered and both eyes readable', 'Preserve the approved wardrobe silhouette'])
  const storedPin = persisted.comments.find((comment: { shotId: string; referenceId?: string }) => comment.shotId === persistedShot.id && comment.referenceId === addedReference.id)
  expect(storedPin.state).toBe('Resolved')
  expect(storedPin.referenceVersion).toBe(addedReference.version)
  verifyConsole()
})

test('long-running image jobs own the frame with durable progress', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.route('**/api/studio', async route => {
    const response = await route.fetch()
    const body = await response.json()
    const shot = body.shots.find((item: { code: string }) => item.code === 'SH-030')
    body.jobs.unshift({ id: '00000000-0000-0000-0000-000000000030', shotId: shot.id, shotCode: shot.code, kind: 'Final still', state: 'Running', progress: 42, phase: 'Waiting for GPT Image', backend: 'OpenAI GPT Image', adapterId: 'openai-gpt-image', createdAt: new Date().toISOString() })
    await route.fulfill({ response, json: body })
  })
  await page.goto('/')
  await page.getByRole('button', { name: /Open SH-030,/ }).click()
  await expect(page.getByTestId('frame-generation-state')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Building the production frame' })).toBeVisible()
  await expect(page.getByText(/can take several minutes/i)).toBeVisible()
  await expect(page.getByRole('progressbar', { name: 'Final still progress' })).toHaveAttribute('aria-valuenow', '42')
  verifyConsole()
})

test('visual audit exposes explicit reconciliation choices and optional context', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  const auditId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'
  let checked = false
  let reconciliation: { decisions?: { action: string; moreDetails: string }[] } | undefined
  const audit = {
    id: auditId, shotId: 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb', shotVersion: 4,
    assetId: 'cccccccc-cccc-4ccc-8ccc-cccccccccccc', assetHash: 'a'.repeat(64), contractHash: 'b'.repeat(64),
    state: 'Completed', gateState: 'Blocked', summary: 'The visible staging contradicts the shot contract.',
    findings: [
      { id: 'visual-1-subject-count', category: 'subject-count', severity: 'Block', title: 'Two wardens are missing', contractExpectation: 'Two distinct full-body wardens stand behind Mara.', observedImage: 'No wardens are visible.', confidence: .99, suggestedRoute: 'RebuildFromSketch', fixInstruction: 'Rebuild with two distinct full-body wardens behind Mara.' },
      { id: 'visual-2-lantern-placement', category: 'object-placement', severity: 'Review', title: 'Lantern is on the table', contractExpectation: 'The trial lantern sits on the floor beside Mara.', observedImage: 'The lantern sits on a table in front of Mara.', confidence: .96, suggestedRoute: 'RebuildFromSketch', fixInstruction: 'Place the trial lantern on the floor beside Mara.' },
    ],
    decisions: [],
    adoptedShotProposal: { description: 'Mara studies a lantern alone.', action: 'Mara leans toward the lantern.', constraints: ['Mara is the only visible person'], referenceIds: [], rationale: 'The wardens would be removed from this shot only.' },
    createdAt: new Date().toISOString(), completedAt: new Date().toISOString(),
    scopeNote: 'Codex inspected the exact current image against the versioned shot contract.',
  }
  await page.route('**/api/shots/*/visual-audit', async route => {
    if (route.request().method() === 'POST') { checked = true; return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(audit) }) }
    return checked ? route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(audit) }) : route.fulfill({ status: 204 })
  })
  await page.route('**/api/shots/*/visual-audits/*/reconcile', async route => {
    reconciliation = route.request().postDataJSON() as typeof reconciliation
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ audit: { ...audit, decisions: reconciliation?.decisions ?? [], gateState: 'Review' }, contractApplied: false, requiresImageGeneration: false, generationDirection: '', detail: 'The unresolved decisions remain visible in Review. Nothing was changed.' }) })
  })
  await page.route('**/api/generation/adapters', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
    { id: 'comfyui-fast-draft', name: 'Fast Draft', provider: 'ComfyUI', state: 'Ready', detail: 'Fast local composition loop', canDispatch: true, routes: ['FastDraft'], purposes: ['Draft'] },
    { id: 'codex-imagegen', name: 'Codex ImageGen', provider: 'ChatGPT login', state: 'Connected', detail: 'High-fidelity reconstruction', canDispatch: true, routes: ['PrecisionDraft'], purposes: ['Draft', 'Final'] },
  ]) }))
  const uploaded = await (await page.request.post('/api/assets/images', {
    headers: { 'X-Storyboard-Studio': '1' },
    multipart: { file: { name: 'visual-audit-source.png', mimeType: 'image/png', buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64') } },
  })).json()
  await page.request.post('/api/shots', { data: {
    code: 'QA-VIS', title: 'Visual reconciliation', description: 'Mara kneels beside a trial lantern while two wardens stand behind her.', durationFrames: 120,
    camera: '35mm centered wide', action: 'Mara studies the lantern.', referenceIds: [], constraints: ['Exactly two distinct full-body wardens stand behind Mara.'], initialImageAssetId: uploaded.id,
  } })
  await page.goto('/')
  await page.getByRole('button', { name: /Open QA-VIS, Visual reconciliation/ }).click()
  await page.getByRole('button', { name: 'Review', exact: true }).click()
  await page.getByRole('button', { name: 'Check visible frame' }).click()
  const dialog = page.getByRole('dialog', { name: 'Resolve image differences' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByText('Two wardens are missing')).toBeVisible()
  await expect(dialog.getByText(/Each difference starts unresolved/)).toBeVisible()
  const unresolvedChoices = dialog.getByRole('radio', { name: /Leave unresolved/ })
  await expect(unresolvedChoices).toHaveCount(2)
  expect(await unresolvedChoices.evaluateAll(nodes => nodes.every(node => node.getAttribute('aria-checked') === 'true'))).toBe(true)
  await dialog.getByLabel('More details for Two wardens are missing').fill('Check the staging with the director before changing either side.')
  await dialog.getByRole('button', { name: 'Save unresolved decisions' }).click()
  await expect(dialog).toBeHidden()
  expect(reconciliation?.decisions).toEqual([
    { findingId: 'visual-1-subject-count', action: 'DecideLater', moreDetails: 'Check the staging with the director before changing either side.' },
    { findingId: 'visual-2-lantern-placement', action: 'DecideLater', moreDetails: '' },
  ])
  await page.getByRole('button', { name: /Back to versions/ }).click()
  await page.getByRole('button', { name: 'Create production final' }).click()
  const finalDialog = page.getByRole('dialog', { name: 'Create production final' })
  await expect(finalDialog.getByText('Blocked: repair before final')).toBeVisible()
  const fixImage = finalDialog.getByRole('button', { name: 'Fix image', exact: true })
  await expect(fixImage).toBeEnabled()
  await fixImage.click()
  await expect(finalDialog).toBeHidden()
  const repairDialog = page.getByRole('dialog', { name: 'Resolve image differences' })
  await expect(repairDialog).toBeVisible()
  await expect(repairDialog.getByRole('radio', { name: /Leave unresolved/ })).toHaveCount(2)
  await repairDialog.getByRole('button', { name: 'Accept all shown' }).click()
  await expect(repairDialog.getByText('Stable scene + narrow overrides')).toBeVisible()
  await expect(repairDialog.getByText('Mara kneels beside a trial lantern while two wardens stand behind her.')).toBeVisible()
  await expect(repairDialog.getByRole('button', { name: 'Keep scene & accept selected' })).toBeEnabled()
  await repairDialog.getByRole('button', { name: 'Correct all' }).click()
  const correctChoices = repairDialog.getByRole('radio', { name: /Correct the image/ })
  await expect(correctChoices).toHaveCount(2)
  expect(await correctChoices.evaluateAll(nodes => nodes.every(node => node.getAttribute('aria-checked') === 'true'))).toBe(true)
  await expect(repairDialog.getByRole('button', { name: 'Generate rebuilt image' })).toBeEnabled()
  await repairDialog.getByRole('radio', { name: /Accept what’s shown/ }).nth(1).click()
  await expect(repairDialog.getByRole('button', { name: 'Apply choices & generate rebuilt image' })).toBeEnabled()
  verifyConsole()
})

test('a successful retry replaces the failed attempt in current status', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'One deterministic desktop proof covers shared job recovery state.')
  const verifyConsole = failOnConsoleErrors(page)
  const body = await (await page.request.get('/api/studio')).json()
  const shot = body.shots[0]
  const failedId = crypto.randomUUID()
  const now = new Date().toISOString()
  body.jobs.unshift(
    {
      id: crypto.randomUUID(), shotId: shot.id, shotCode: shot.code, kind: 'Video', state: 'Completed',
      progress: 100, phase: 'Candidate ready for review', backend: 'Browser H3 proof', createdAt: now,
      completedAt: now, adapterId: 'browser-h3', outputAssetId: crypto.randomUUID(), attempt: 2,
      retryOfJobId: failedId, workType: 'Shot',
    },
    {
      id: failedId, shotId: shot.id, shotCode: shot.code, kind: 'Video', state: 'Failed',
      progress: 100, phase: 'Generation failed', backend: 'Browser H3 proof', createdAt: now,
      completedAt: now, error: 'The first attempt returned the wrong frame count.',
      adapterId: 'browser-h3', attempt: 1, workType: 'Shot',
    },
  )
  await page.route('**/api/studio', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) }))

  await page.goto('/')
  await expect(page.getByTestId('jobs-dock-stalled')).toHaveCount(0)
  await expect(page.getByText('Generation failed', { exact: true })).toHaveCount(0)
  verifyConsole()
})

test('connection surface reports safe workstation boundaries', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.goto('/')
  await page.getByRole('button', { name: /Local studio/ }).click()

  await expect(page.getByTestId('setup-drawer')).toBeVisible()
  // Generation comes first; workstation health is folded into Diagnostics.
  await expect(page.getByRole('heading', { name: 'Image and video generation' })).toBeVisible()
  await expect(page.getByText('Production guard is active')).toBeHidden()
  await page.getByTestId('setup-drawer').getByText('Diagnostics', { exact: true }).click()
  await expect(page.getByText('Production guard is active')).toBeVisible()
  await expect(page.getByTestId('build-identity')).toContainText('development')
  await expect(page.getByTestId('build-identity')).toContainText('commit unknown')
  const diagnosticLink = page.getByRole('link', { name: 'QA diagnostics' })
  await expect(diagnosticLink).toHaveAttribute('href', '/api/maintenance/diagnostics')
  const diagnosticDownload = page.waitForEvent('download')
  await diagnosticLink.click()
  expect((await diagnosticDownload).suggestedFilename()).toMatch(/framewright-diagnostics-.*\.zip/)
  await expect(page.getByRole('heading', { name: 'ComfyUI' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Codex', exact: true })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'OpenAI API' })).toBeVisible()
  await expect(page.getByRole('heading', { name: 'OpenAI API' }).locator('.preview-tag')).toHaveText('Preview')
  await expect(page.locator('.pairing-card .preview-tag')).toHaveText('Preview')
  await expect(page.getByText('Project contract', { exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: /Cinema scope/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /Widescreen 4K/ })).toBeVisible()
  await expect(page.getByRole('button', { name: /Widescreen HD/ })).toBeVisible()
  await expect(page.getByLabel('Color space')).toHaveValue('Rec.709')
  await expect(page.getByLabel('Audio master')).toHaveValue('48000')
  await expect(page.getByText('Project safety copies')).toBeVisible()
  const sequenceName = page.getByLabel('Sequence name')
  const originalSequenceName = await sequenceName.inputValue()
  const originalWidth = await page.getByLabel('Delivery width').inputValue()
  const originalHeight = await page.getByLabel('Delivery height').inputValue()
  const originalAspect = await page.getByLabel('Aspect ratio').inputValue()
  const originalFps = await page.getByLabel('Frame rate').inputValue()
  await expect(page.getByRole('button', { name: 'Save project contract' })).toBeDisabled()
  await sequenceName.fill(`${originalSequenceName} audit`)
  await expect(page.getByText('Unsaved project changes')).toBeVisible()
  await sequenceName.fill(originalSequenceName)
  await page.getByRole('button', { name: /Widescreen HD/ }).click()
  await expect(page.getByLabel('Delivery width')).toHaveValue('1920')
  await expect(page.getByLabel('Delivery height')).toHaveValue('1080')
  await page.getByLabel('Delivery width').fill(originalWidth)
  await page.getByLabel('Delivery height').fill(originalHeight)
  await page.getByLabel('Aspect ratio').fill(originalAspect)
  await page.getByLabel('Frame rate').fill(originalFps)
  await page.getByRole('button', { name: 'Save project contract' }).click()
  await expect(page.getByText('Project contract saved')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Save project contract' })).toBeDisabled()
  const credential = await (await page.request.get('/api/credentials/openai')).json()
  if (credential.canManageHere) {
    await expect(page.getByLabel('API key')).toHaveAttribute('type', 'password')
  } else {
    await expect(page.getByLabel('API key')).toHaveCount(0)
    await expect(page.getByText('Configure the service environment')).toBeVisible()
  }
  await expect(page.getByText(/cannot clear or interrupt jobs/i)).toBeVisible()
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('sequence slots can be reordered and restored without variant sprawl', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.goto('/')
  await page.getByRole('button', { name: 'Sequence' }).click()
  const overview = page.locator('.overview-track button')
  const originalFirst = await overview.first().textContent()
  const code = originalFirst?.trim() ?? ''
  await page.getByRole('button', { name: `Move ${code} later` }).click()
  await expect(overview.first()).not.toHaveText(code)
  await page.getByRole('button', { name: `Move ${code} earlier` }).click()
  await expect(overview.first()).toHaveText(code)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('tablet keeps the core board, shot, review, and timeline touch-usable', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Shot board' })).toBeVisible()
  await expectNoHorizontalPageOverflow(page)

  await page.getByRole('button', { name: /Open SH-010, Aerie approach/ }).click()
  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  await expectNoHorizontalPageOverflow(page)
  await expect(page.getByRole('button', { name: 'Create production final' })).toBeVisible()

  await page.getByRole('button', { name: 'Sequence' }).click()
  await expect(page.getByTestId('sequence-workspace')).toBeVisible()
  await expect(page.getByText('Dialogue', { exact: true })).toBeVisible()
  await expect(page.getByText('Music', { exact: true })).toBeVisible()
  await expect(page.getByText('Post only', { exact: true })).toBeVisible()
  const audioPolicy = page.locator('.sequence-policy')
  await expect(audioPolicy).toContainText('Audio stays separate')
  await expect(audioPolicy).toContainText('Never baked into generated video')
  const timelineTime = page.locator('.timeline-toolbar .timecode')
  await expect(timelineTime).toHaveText('00:00:00:00')
  await page.getByRole('button', { name: 'Play sequence preview' }).click()
  await expect(timelineTime).not.toHaveText('00:00:00:00')
  await page.getByRole('button', { name: 'Pause sequence preview' }).click()
  await expectNoHorizontalPageOverflow(page)
  const smallCoreTargets = await page.locator('.workspace-rail nav button, .timeline-toolbar > button').evaluateAll(elements => elements.filter(element => {
    const rect = element.getBoundingClientRect()
    return rect.width > 0 && rect.height > 0 && (rect.width < 44 || rect.height < 44)
  }).map(element => ({ label: element.getAttribute('aria-label') || element.textContent, rect: element.getBoundingClientRect().toJSON() })))
  expect(smallCoreTargets).toEqual([])
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('timeline clips and the volume slider persist real edits', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const suffix = testInfo.project.name === 'tablet' ? 'Tablet' : 'Desktop'
  await page.route('**/api/voice/synthesis', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({
      enabled: true, credentialConfigured: true, canSynthesize: true,
      model: 'browser-voice-proof', detail: 'Browser voice proof is ready.',
      localCanSynthesize: false, localDetail: 'Local route is not used by this profile.',
    }),
  }))
  await page.goto('/')
  await page.getByRole('button', { name: 'Sequence' }).click()
  await page.getByRole('button', { name: 'Voice profiles' }).click()
  const voices = page.getByRole('dialog', { name: 'Character voices' })
  await voices.getByLabel('Profile name').fill(`Ennix ${suffix}`)
  const characterAuthority = voices.getByRole('combobox', { name: 'Character authority', exact: true })
  const firstCharacterId = await characterAuthority.locator('option').nth(1).getAttribute('value')
  await characterAuthority.selectOption(firstCharacterId!)
  await voices.getByLabel('Provider', { exact: true }).fill('Editorial provider')
  await voices.getByLabel('Provider voice ID', { exact: true }).fill(`preset-${suffix.toLowerCase()}`)
  const wav = Buffer.alloc(46)
  wav.write('RIFF', 0); wav.writeUInt32LE(38, 4); wav.write('WAVE', 8); wav.write('fmt ', 12)
  wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(8000, 24)
  wav.writeUInt32LE(16000, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(2, 40)
  await voices.locator('input[type="file"]').setInputFiles({ name: `ennix-${suffix.toLowerCase()}.wav`, mimeType: 'audio/wav', buffer: wav })
  await expect(voices.getByText(/ready to attach/)).toBeVisible()
  await voices.getByRole('button', { name: 'Create voice authority' }).click()
  await expect(voices.getByText(`Ennix ${suffix}`, { exact: true })).toBeVisible()
  await voices.getByRole('button', { name: 'Done' }).click()

  await page.getByRole('button', { name: 'Add audio' }).click()
  const editor = page.getByRole('dialog', { name: 'Add audio clip' })
  await editor.getByLabel('Track').selectOption('Voice')
  await editor.getByLabel('Clip label').fill(`Opening line ${suffix}`)
  await editor.getByLabel('Dialogue / editorial note').fill(`Keep your eyes on the lantern, ${suffix.toLowerCase()}.`)
  const voiceOption = editor.getByLabel('Reusable voice profile').locator('option').filter({ hasText: `Ennix ${suffix}` })
  await editor.getByLabel('Reusable voice profile').selectOption((await voiceOption.getAttribute('value'))!)
  const slider = editor.getByRole('slider', { name: 'Clip volume' })
  await slider.fill('0.55')
  await expect(editor.getByText('55%', { exact: true })).toBeVisible()
  await editor.getByRole('button', { name: 'Save clip' }).click()

  const clip = page.getByRole('button', { name: new RegExp(`Edit voice clip Opening line ${suffix}`) })
  await expect(clip).toContainText('55%')
  await clip.focus()
  await clip.press('Enter')
  const savedEditor = page.getByRole('dialog', { name: 'Edit audio clip' })
  await expect(savedEditor.getByText('55%', { exact: true })).toBeVisible()

  const queuedAt = new Date().toISOString()
  const jobId = crypto.randomUUID()
  const outputAssetId = crypto.randomUUID()
  let jobPoll = 0
  const queuedJob = {
    id: jobId, shotId: `voice-clip-${suffix.toLowerCase()}`, shotCode: `Opening line ${suffix}`,
    kind: 'Voice synthesis', state: 'Queued', progress: 0, phase: 'Queued safely',
    backend: 'Browser proof', createdAt: queuedAt, adapterId: 'browser-voice-proof',
    attempt: 1, workType: 'Voice',
  }
  await page.route('**/api/timeline/clips/*/synthesize', route => route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify(queuedJob) }))
  await page.route('**/api/studio', async route => {
    const response = await route.fetch()
    const snapshot = await response.json()
    jobPoll += 1
    const completed = jobPoll >= 2
    snapshot.jobs = [{
      ...queuedJob,
      state: completed ? 'Completed' : 'Running',
      progress: completed ? 100 : 45,
      phase: completed ? 'Voice media attached' : 'Synthesizing speech',
      outputAssetId: completed ? outputAssetId : undefined,
      outputAssetUrl: completed ? `/api/assets/${outputAssetId}/content` : undefined,
      completedAt: completed ? new Date().toISOString() : undefined,
    }, ...snapshot.jobs.filter((job: { id: string }) => job.id !== jobId)]
    await route.fulfill({ response, json: snapshot })
  })
  await page.route(`**/api/assets/${outputAssetId}/content`, route => route.fulfill({
    status: 200, contentType: 'audio/wav', body: wav,
  }))
  await page.route('**/api/timeline/clips', async route => {
    const response = await route.fetch()
    const clips = await response.json()
    if (jobPoll >= 2) {
      const target = clips.find((item: { label: string }) => item.label === `Opening line ${suffix}`)
      if (target) { target.assetId = outputAssetId; target.assetUrl = `/api/assets/${outputAssetId}/content` }
    }
    await route.fulfill({ response, json: clips })
  })

  await savedEditor.getByRole('button', { name: 'Generate speech' }).click()
  await expect(savedEditor).toBeHidden()
  await expect(page.getByTestId('jobs-dock')).toContainText(`Opening line ${suffix}`)
  await expect(clip).toContainText('Media', { timeout: 8_000 })
  verifyConsole()
})

test('inline candidate review keeps versions, feedback, and tablet controls in place', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page, [/409 \(Conflict\)/])
  const shotCode = testInfo.project.name === 'tablet' ? 'SH-060' : 'SH-020'
  await page.goto('/')
  await page.getByRole('button', { name: new RegExp(`Open ${shotCode},`) }).click()
  await page.locator('.shot-toolbar-actions').getByRole('button', { name: 'Sketch' }).click()

  const brief = page.getByRole('textbox', { name: 'Creative brief' })
  await brief.fill(`${await brief.inputValue()} Review-loop proof.`)
  await expect(page.getByText(/Saved to studio/)).toBeVisible()
  await page.getByRole('button', { name: 'Generate draft in ComfyUI' }).click()
  await expect(page.getByRole('alert')).toContainText('ComfyUI image generation is turned off')
  const snapshot = await (await page.request.get('/api/studio')).json()
  const shot = snapshot.shots.find((item: { code: string }) => item.code === shotCode)
  const manifests = await (await page.request.get(`/api/shots/${shot.id}/manifests`)).json()
  const proof = await page.request.post(`/api/manifests/${manifests[0].id}/dispatch`, { data: { expectedManifestHash: manifests[0].manifestHash, adapterId: 'local-proof' } })
  expect(proof.ok()).toBe(true)
  await page.reload()
  await page.getByRole('button', { name: new RegExp(`Open ${shotCode},`) }).click()

  const options = page.getByRole('listbox', { name: 'Candidate versions' }).getByRole('option')
  await expect(options).toHaveCount(2)
  const archived = page.getByRole('listbox', { name: 'Candidate versions' }).locator('.review-chip:not(.is-current)').first()
  const archivedVersion = (await archived.innerText()).trim()
  await archived.click()
  await expect(archived).toHaveAttribute('aria-selected', 'true')
  await expect(page.locator('.preview-banner')).toContainText(`Reviewing ${archivedVersion}`)
  await expect(page.locator('.canvas-caption')).toContainText(archivedVersion)
  await expect(page.getByRole('textbox', { name: 'What should change?' })).toBeDisabled()
  const editIntent = page.getByRole('button', { name: 'Edit intent' })
  if (await editIntent.count()) await expect(editIntent).toBeDisabled()
  await expect(page.getByRole('textbox', { name: 'Shot description' })).toBeDisabled()
  await page.getByRole('tab', { name: /References/ }).click()
  await expect(page.getByRole('button', { name: 'Add', exact: true })).toBeDisabled()
  await expect(page.getByRole('button', { name: /Remove .* from shot/ }).first()).toBeDisabled()
  await page.getByRole('tab', { name: /Rules/ }).click()
  await expect(page.getByRole('textbox', { name: 'Constraint 1' })).toBeDisabled()
  await expect(page.getByRole('textbox', { name: 'New constraint' })).toBeDisabled()

  // Archived candidates are intentionally immutable. Return to the live head
  // for a change request, then revisit the archive to prove tab navigation
  // preserves the selected review context.
  await options.first().click()

  const change = `Keep ${shotCode} feedback available after a protected attempt.`
  const changeBox = page.getByRole('textbox', { name: 'What should change?' })
  await changeBox.fill(change)
  await page.getByRole('button', { name: 'Apply & regenerate' }).click()
  await expect(page.getByRole('alert')).toContainText('ComfyUI image generation is turned off')
  await expect(changeBox).toHaveValue(change)
  await archived.click()

  await page.getByRole('tab', { name: /References/ }).click()
  await page.getByRole('tab', { name: /References/ }).press('ArrowRight')
  await expect(page.getByRole('tab', { name: /Rules/ })).toHaveAttribute('aria-selected', 'true')
  await expect(archived).toHaveAttribute('aria-selected', 'true')

  const overlap = await page.evaluate(() => {
    if (innerWidth > 860) return false
    const review = document.querySelector('.review-bar')!.getBoundingClientRect()
    const inspector = document.querySelector('.shot-inspector')!.getBoundingClientRect()
    return review.bottom > inspector.top + 1
  })
  expect(overlap).toBe(false)
  await expectNoHorizontalPageOverflow(page)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('feedback regeneration freezes the current frame and pinned notes instead of restarting from the sketch', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await mockReadyPreflight(page)
  let dispatchedAdapter = ''
  await page.route('**/api/generation/adapters', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
    { id: 'local-proof', name: 'Local composition proof', kind: 'Local', state: 'Ready', detail: 'No AI test adapter', canDispatch: true, routes: ['FastDraft', 'PrecisionDraft'], purposes: ['Draft', 'Final'] },
    { id: 'comfyui-fast-draft', name: 'ComfyUI fast draft', kind: 'Local service', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['FastDraft'], purposes: ['Draft'] },
    { id: 'openai-gpt-image', name: 'OpenAI GPT Image', kind: 'Cloud', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['PrecisionDraft'], purposes: ['Draft', 'Final'] },
  ]) }))
  const feedbackStudioBefore = await (await page.request.get('/api/studio')).json()
  const feedbackShotId = feedbackStudioBefore.shots.find((item: { code: string }) => item.code === 'SH-030').id
  const sketchBeforeResponse = await page.request.get(`/api/shots/${feedbackShotId}/sketch`)
  const sketch = sketchBeforeResponse.status() === 204
    ? await (await page.request.put(`/api/shots/${feedbackShotId}/sketch`, { data: { expectedRevision: 0, creativeBrief: 'Feedback edit test composition.', content: { strokes: [], labels: [] } } })).json() as { revision: number }
    : await sketchBeforeResponse.json() as { revision: number }
  const versionBeforeProof = feedbackStudioBefore.shots.find((item: { code: string }) => item.code === 'SH-030').version
  const preparedProof = await page.request.post(`/api/shots/${feedbackShotId}/manifests/prepare`, { data: { expectedShotVersion: versionBeforeProof, expectedSketchRevision: sketch.revision, route: 'FastDraft', purpose: 'Draft' } })
  const proofManifest = await preparedProof.json() as { id: string; manifestHash: string }
  await page.request.post(`/api/manifests/${proofManifest.id}/dispatch`, { data: { expectedManifestHash: proofManifest.manifestHash, adapterId: 'local-proof' } })
  await page.route('**/api/manifests/*/dispatch', async route => {
    dispatchedAdapter = (await route.request().postDataJSON()).adapterId
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: crypto.randomUUID(), state: 'Queued' }) })
  })

  await expect.poll(async () => {
    const state = await (await page.request.get('/api/studio')).json()
    return state.shots.find((item: { code: string }) => item.code === 'SH-030')?.version
  }).toBeGreaterThan(versionBeforeProof)
  const before = await (await page.request.get('/api/studio')).json()
  const shot = before.shots.find((item: { code: string }) => item.code === 'SH-030')
  const note = 'Lower both raised arms and keep the current doorway composition.'
  await page.request.post(`/api/shots/${shot.id}/comments`, { data: { x: .42, y: .36, body: note } })
  await page.goto('/')
  await page.getByRole('button', { name: /Open SH-030,/ }).click()

  await page.getByRole('button', { name: 'More options' }).click()
  const dialog = page.getByRole('dialog', { name: 'Regenerate from feedback' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByText('The clean current frame—not the original sketch—will be sent as the composition image.')).toBeVisible()
  await expect(dialog.getByText(note)).toBeVisible()
  await expect(dialog.getByRole('img', { name: /current frame used for feedback regeneration/ })).toBeVisible()
  await expect(dialog.getByRole('radio', { name: /ComfyUI fast draft.*selected ComfyUI workflow/ })).toHaveAttribute('aria-checked', 'true')
  await dialog.getByRole('button', { name: 'Regenerate with ComfyUI fast draft' }).click()
  await expect(dialog).toBeHidden()
  expect(dispatchedAdapter).toBe('comfyui-fast-draft')

  const manifests = await (await page.request.get(`/api/shots/${shot.id}/manifests`)).json()
  const latest = manifests[0]
  expect(latest.compositionAssetId).toBe(shot.currentAssetId)
  expect(latest.creativeBrief).toContain('CURRENT FRAME EDIT')
  expect(latest.creativeBrief).toContain(note)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('working-frame pins can be cleared individually or all at once without deleting revision history', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const snapshot = await (await page.request.get('/api/studio')).json()
  const reference = snapshot.references[0]
  const upload = await page.request.post('/api/assets/images', {
    headers: { 'X-Storyboard-Studio': '1' },
    multipart: { file: { name: 'pin-clear-source.png', mimeType: 'image/png', buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64') } },
  })
  const source = await upload.json()
  const code = `PIN_${testInfo.project.name}_${Date.now().toString().slice(-6)}`.toUpperCase()
  const createdResponse = await page.request.post('/api/shots', { data: {
    code, title: 'Clear active pins', description: 'A working frame with disposable active notes.',
    durationFrames: 48, camera: '50mm equivalent · medium two-shot', action: 'Hold.',
    referenceIds: [reference.id], constraints: [], initialImageAssetId: source.id,
  } })
  const shot = await createdResponse.json()
  await page.request.post(`/api/shots/${shot.id}/comments`, { data: { x: .32, y: .35, body: 'Reference placement to clear.', referenceId: reference.id } })
  await page.request.post(`/api/shots/${shot.id}/comments`, { data: { x: .68, y: .62, body: 'Ordinary feedback to clear.' } })

  await page.goto('/')
  await page.getByRole('button', { name: new RegExp(`Open ${code},`) }).click()
  await expect(page.getByRole('button', { name: 'Clear all 2 pins' })).toBeVisible()

  await page.locator('.comment-pin').first().click()
  await page.getByRole('button', { name: 'Clear pin' }).click()
  await expect(page.getByRole('button', { name: 'Clear pin' })).toBeVisible()

  await page.getByRole('button', { name: 'Clear pin' }).click()
  await expect(page.locator('.comment-pin')).toHaveCount(0)
  const after = await (await page.request.get('/api/studio')).json()
  const comments = after.comments.filter((item: { shotId: string; version: number }) => item.shotId === shot.id && item.version === shot.version)
  expect(comments).toHaveLength(2)
  expect(comments.every((item: { state: string }) => item.state === 'Resolved')).toBeTruthy()
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('a ratified draft can regenerate from an exact reference note without hiding production final', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  await mockReadyPreflight(page)
  await page.route('**/api/generation/adapters', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
    { id: 'comfyui-fast-draft', name: 'ComfyUI fast draft', kind: 'Local service', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['FastDraft'], purposes: ['Draft'] },
    { id: 'openai-gpt-image', name: 'OpenAI GPT Image', kind: 'Cloud', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['PrecisionDraft'], purposes: ['Draft', 'Final'] },
  ]) }))
  let dispatchedAdapter = ''
  await page.route('**/api/manifests/*/dispatch', async route => {
    dispatchedAdapter = (await route.request().postDataJSON()).adapterId
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: crypto.randomUUID(), state: 'Queued' }) })
  })
  const snapshot = await (await page.request.get('/api/studio')).json()
  const sourceUpload = await page.request.post('/api/assets/images', {
    headers: { 'X-Storyboard-Studio': '1' },
    multipart: { file: { name: 'ratified-reference-source.png', mimeType: 'image/png', buffer: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64') } },
  })
  const sourceAsset = await sourceUpload.json()
  expect(sourceUpload.ok()).toBeTruthy()
  const reference = snapshot.references[0]
  const code = `RRF_${testInfo.project.name}_${Date.now().toString().slice(-6)}`.toUpperCase()
  const createdResponse = await page.request.post('/api/shots', { data: {
    code, title: 'Ratified reference replacement', description: 'A ratified source ready for a reference-guided replacement.',
    durationFrames: 48, camera: '50mm equivalent · close-up · locked', action: 'Hold the pose.',
    referenceIds: [reference.id], constraints: [], initialImageAssetId: sourceAsset.id,
  } })
  const created = await createdResponse.json()
  expect(createdResponse.ok()).toBeTruthy()
  const ratifyResponse = await page.request.post(`/api/shots/${created.id}/ratify`, { data: { expectedVersion: created.version, reason: 'Reference regeneration browser proof' } })
  expect(ratifyResponse.ok()).toBeTruthy()
  const ratifiedSnapshot = await (await page.request.get('/api/studio')).json()
  const shot = ratifiedSnapshot.shots.find((item: { id: string }) => item.id === created.id)
  expect(shot.approval).toBe('Ratified')
  const note = `Use this exact ${reference.name} authority image for the face. ${testInfo.project.name}-${Date.now()}`
  const comment = await page.request.post(`/api/shots/${shot.id}/comments`, { data: { x: .48, y: .31, body: note, referenceId: reference.id } })
  expect(comment.ok()).toBeTruthy()

  await page.goto('/')
  await page.getByRole('button', { name: new RegExp(`Open ${code},`) }).click()
  await expect(page.getByRole('button', { name: /Regenerate with Codex/ })).toBeVisible()
  await expect(page.getByRole('button', { name: 'Create production final' })).toBeVisible()
  await page.getByRole('button', { name: 'More options' }).click()

  const dialog = page.getByRole('dialog', { name: 'Regenerate from feedback' })
  await expect(dialog.getByText(`${reference.name} · authority v${reference.version}`)).toBeVisible()
  await expect(dialog.getByText(note)).toBeVisible()
  await expect(dialog.getByText('exact reference image included')).toBeVisible()
  await expect(dialog.getByText(new RegExp(`v${shot.version} stays preserved as the approved draft`))).toBeVisible()
  await dialog.getByRole('button', { name: 'Regenerate with ComfyUI' }).click()
  await expect.poll(() => dispatchedAdapter).toBe('comfyui-fast-draft')

  const manifests = await (await page.request.get(`/api/shots/${shot.id}/manifests`)).json()
  expect(manifests[0].purpose).toBe('Draft')
  expect(manifests[0].compositionAssetId).toBe(shot.currentAssetId)
  expect(manifests[0].creativeBrief).toContain(note)
  expect(manifests[0].creativeBrief).toContain(`REFERENCE PLACEMENTS`)
  expect(manifests[0].authorities).toContainEqual(expect.objectContaining({ id: reference.id, version: reference.version }))
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('a ratified final reopens as a ComfyUI working revision before precision promotion', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await mockReadyPreflight(page)
  await page.route('**/api/generation/adapters', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
    { id: 'comfyui-fast-draft', name: 'ComfyUI fast draft', kind: 'Local service', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['FastDraft'], purposes: ['Draft'] },
    { id: 'codex-imagegen', name: 'Codex ImageGen', kind: 'ChatGPT login', state: 'Connected', detail: 'Runs in Framewright with the Codex ChatGPT login.', canDispatch: true, routes: ['PrecisionDraft'], purposes: ['Draft', 'Final'] },
    { id: 'openai-gpt-image', name: 'OpenAI GPT Image', kind: 'Cloud', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['PrecisionDraft'], purposes: ['Draft', 'Final'] },
  ]) }))

  await page.goto('/')
  const snapshot = await (await page.request.get('/api/studio')).json()
  const shot = snapshot.shots.find((item: { code: string }) => item.code === 'SH-040')
  await page.getByRole('button', { name: /Open SH-040, Guard interruption/ }).click()
  await page.getByRole('button', { name: 'Regenerate this image' }).click()

  const dialog = page.getByRole('dialog', { name: 'Regenerate from feedback' })
  await expect(dialog.getByRole('radio', { name: /ComfyUI fast draft.*selected ComfyUI workflow/ })).toHaveAttribute('aria-checked', 'true')
  await expect(dialog.getByRole('radio', { name: /Codex ImageGen.*runs in Framewright/ })).toBeVisible()
  await expect(dialog.getByRole('radio', { name: /OpenAI GPT Image.*Direct (?:edit|draft)/ })).toBeVisible()
  await expect(dialog.getByText('1 · Iterate')).toBeVisible()
  await expect(dialog.getByText(new RegExp(`result becomes a new working draft; v${shot.version} stays preserved as the approved final`, 'i'))).toBeVisible()
  verifyConsole()
})

test('production final stays on the ratified image and sends that exact frame to GPT Image', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await mockReadyPreflight(page)
  const snapshotBefore = await (await page.request.get('/api/studio')).json()
  const initial = snapshotBefore.shots.find((item: { code: string }) => item.code === 'SH-010')
  const sketchResponse = await page.request.get(`/api/shots/${initial.id}/sketch`)
  const sketch = sketchResponse.status() === 204
    ? await (await page.request.put(`/api/shots/${initial.id}/sketch`, { data: { expectedRevision: 0, creativeBrief: 'Production final source proof.', content: { strokes: [], labels: [] } } })).json() as { revision: number }
    : await sketchResponse.json() as { revision: number }
  const preparedDraft = await page.request.post(`/api/shots/${initial.id}/manifests/prepare`, { data: { expectedShotVersion: initial.version, expectedSketchRevision: sketch.revision, route: 'FastDraft', purpose: 'Draft' } })
  const draftManifest = await preparedDraft.json() as { id: string; manifestHash: string }
  await page.request.post(`/api/manifests/${draftManifest.id}/dispatch`, { data: { expectedManifestHash: draftManifest.manifestHash, adapterId: 'local-proof' } })
  await expect.poll(async () => {
    const current = await (await page.request.get('/api/studio')).json()
    return current.shots.find((item: { code: string }) => item.code === 'SH-010')?.version
  }).toBeGreaterThan(initial.version)
  const generatedSnapshot = await (await page.request.get('/api/studio')).json()
  const generated = generatedSnapshot.shots.find((item: { code: string }) => item.code === 'SH-010')
  await page.request.post(`/api/shots/${generated.id}/ratify`, { data: { expectedVersion: generated.version, reason: 'Production final route test' } })
  const ratifiedSnapshot = await (await page.request.get('/api/studio')).json()
  const ratified = ratifiedSnapshot.shots.find((item: { code: string }) => item.code === 'SH-010')
  expect(ratified.currentAssetId).toBeTruthy()

  let dispatchedAdapter = ''
  await page.route(`**/api/shots/${ratified.id}/visual-audit`, route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
    id: crypto.randomUUID(), shotId: ratified.id, shotVersion: ratified.version, assetId: ratified.currentAssetId,
    assetHash: 'a'.repeat(64), contractHash: 'b'.repeat(64), state: 'Completed', gateState: 'Clear',
    summary: 'The exact selected image matches its observable shot contract.', findings: [], decisions: [],
    createdAt: new Date().toISOString(), completedAt: new Date().toISOString(), scopeNote: 'Image-bound browser proof.',
  }) }))
  await page.route('**/api/generation/adapters', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
    { id: 'openai-gpt-image', name: 'OpenAI GPT Image', kind: 'Cloud', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['PrecisionDraft'], purposes: ['Draft', 'Final'] },
  ]) }))
  await page.route('**/api/manifests/*/dispatch', async route => {
    dispatchedAdapter = (await route.request().postDataJSON()).adapterId
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: crypto.randomUUID(), state: 'Queued' }) })
  })
  await page.goto('/')
  await page.getByRole('button', { name: /Open SH-010,/ }).click()
  const sourceUrl = ratified.currentAssetUrl
  await expect(page.locator('.shot-canvas .shot-asset')).toHaveAttribute('src', sourceUrl)
  await page.getByRole('button', { name: 'Create production final' }).click()

  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  await expect(page.getByTestId('sketch-workspace')).toHaveCount(0)
  const dialog = page.getByRole('dialog', { name: 'Create production final' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('img', { name: /selected image version .* production source/ })).toHaveAttribute('src', sourceUrl)
  await expect(dialog.getByText('This image stays the visual base')).toBeVisible()
  await expect(dialog.getByText(/will not replace it with the original sketch/)).toBeVisible()
  await dialog.getByRole('button', { name: 'Generate with OpenAI GPT Image' }).click()
  await expect(dialog).toBeHidden()
  expect(dispatchedAdapter).toBe('openai-gpt-image')

  const manifests = await (await page.request.get(`/api/shots/${ratified.id}/manifests`)).json()
  expect(manifests[0].purpose).toBe('Final')
  expect(manifests[0].compositionAssetId).toBe(ratified.currentAssetId)
  expect(manifests[0].creativeBrief).toContain('Do not use or substitute the saved sketch')
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('Codex directing advice can become a reviewable current-frame edit', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.route('**/api/codex/assist', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      mode: 'Directing pass',
      headline: 'Hold the recognition in profile',
      message: 'Make the turn more deliberate while preserving the locked profile.',
      findings: ['Keep both collar clasps readable.'],
      suggestedActions: ['Hold the final pose for a dramatic count.'],
      live: true,
      completedAt: new Date().toISOString(),
    }),
  }))
  await page.goto('/')
  await page.getByRole('button', { name: /Open SH-030,/ }).click()
  await page.getByRole('button', { name: /Ask Codex to refine the shot/ }).click()
  const assistant = page.getByRole('dialog', { name: /SH-030 directing pass/ })
  await expect(assistant.getByText('Hold the recognition in profile')).toBeVisible()
  await assistant.getByRole('button', { name: 'Make these changes' }).click()

  const dialog = page.getByRole('dialog', { name: 'Regenerate from feedback' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByRole('textbox', { name: 'Feedback regeneration instructions' })).toContainText('Make the turn more deliberate')
  // Which source the dialog names depends on whether an earlier journey in the
  // shared database already gave SH-030 an image; each branch must explain it.
  await expect(dialog.getByText(/current frame—not the original sketch|clean storyboard layout|No source image or storyboard layout/i)).toBeVisible()
  await dialog.getByRole('radio', { name: /OpenAI GPT Image.*Direct (?:edit|draft)/ }).click()
  await expect(dialog.getByRole('radio', { name: /OpenAI GPT Image.*Direct (?:edit|draft)/ })).toHaveAttribute('aria-checked', 'true')
  await expect(dialog.getByRole('button', { name: /OpenAI GPT Image needs setup|Regenerate with OpenAI GPT Image/ })).toBeVisible()
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('music composition is a usable protected editorial surface', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.goto('/')
  await page.getByRole('button', { name: 'Sequence' }).click()
  await page.getByRole('button', { name: 'Music library' }).click()
  const dialog = page.getByRole('dialog', { name: 'Music composition' })
  await expect(dialog).toBeVisible()
  await expect(dialog.locator('.preview-tag')).toHaveText('YuE2')
  await expect(dialog).toHaveCSS('background-color', 'rgb(27, 31, 29)')

  const box = await dialog.boundingBox()
  const viewport = page.viewportSize()!
  expect(box).not.toBeNull()
  expect(box!.x).toBeGreaterThanOrEqual(0)
  expect(box!.y).toBeGreaterThanOrEqual(0)
  expect(box!.x + box!.width).toBeLessThanOrEqual(viewport.width)
  expect(box!.y + box!.height).toBeLessThanOrEqual(viewport.height)

  await expect(dialog.getByRole('textbox', { name: 'Song description' })).toContainText('Dark cinematic synthwave')
  await expect(dialog.getByRole('button', { name: 'Compose editable song' })).toBeDisabled()
  await expect(dialog).toContainText('remains disabled')
  await expectNoSeriousAccessibilityViolations(page)
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
  verifyConsole()
})

test('music placement uses the project frame rate rather than a hidden 24fps assumption', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'One deterministic desktop proof covers project-rate conversion.')
  const verifyConsole = failOnConsoleErrors(page)
  let placed: { durationFrames: number } | undefined
  const compositionId = crypto.randomUUID()
  const revisionId = crypto.randomUUID()
  const assetId = crypto.randomUUID()
  const composition = {
    id: compositionId, title: 'Ten second proof', description: 'Placement proof', currentRevisionId: revisionId, currentRevisionNumber: 1,
    createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(),
    revisions: [{ id: revisionId, revisionNumber: 1, editSummary: 'Original composition', contentHash: 'b'.repeat(64), createdAt: new Date().toISOString(), abcNotation: 'X:1\nM:4/4\nK:C\nV:Vocal\nV:Ins\n', composition: { title: 'Ten second proof', description: 'Placement proof', style: 'cinematic', tempo: 100, meter: '4/4', key: 'C', performancePrompt: 'strings', sections: [{ id: 'verse', type: 'verse', bars: 8, lyrics: 'Proof', chords: ['C'], melody: 'rising' }] }, renders: [{ id: crypto.randomUUID(), jobId: crypto.randomUUID(), assetId, assetUrl: '/api/assets/ten-second-proof/content', renderer: 'YuE2', settingsJson: '{}', createdAt: new Date().toISOString() }] }],
  }
  await page.route('**/api/studio', async route => {
    const response = await route.fetch()
    const snapshot = await response.json()
    snapshot.project.framesPerSecond = 25
    await route.fulfill({ response, json: snapshot })
  })
  await page.route('**/api/assets', async route => {
    const response = await route.fetch()
    const assets = await response.json()
    assets.unshift({
      id: assetId, kind: 'Audio', originalFileName: 'ten-second-proof.wav',
      mimeType: 'audio/wav', bytes: 1024, durationSeconds: 10,
      contentHash: 'a'.repeat(64), contentUrl: '/api/assets/ten-second-proof/content',
      createdAt: new Date().toISOString(), displayName: 'Ten second proof', source: 'Browser proof',
      revisionFamilyId: crypto.randomUUID(), revisionNumber: 1, isCurrentRevision: true,
    })
    await route.fulfill({ response, json: assets })
  })
  await page.route('**/api/timeline/clips', async route => {
    if (route.request().method() !== 'POST') return route.fallback()
    placed = await route.request().postDataJSON()
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ id: crypto.randomUUID(), ...placed, updatedAt: new Date().toISOString() }) })
  })
  await page.route('**/api/music/status', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ enabled: true, endpointAllowed: true, serviceReachable: true, canCompose: true, canRender: true, detail: 'YuE2 ready', model: 'm-a-p/YuE2-3B', device: 'cuda' }) }))
  await page.route('**/api/music/compositions', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([composition]) }))
  await page.route(`**/api/music/compositions/${compositionId}`, route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(composition) }))

  await page.goto('/')
  await page.getByRole('button', { name: 'Sequence' }).click()
  await page.getByRole('button', { name: 'Music library' }).click()
  const dialog = page.getByRole('dialog', { name: 'Music composition' })
  await dialog.getByRole('button', { name: 'Add to Music lane' }).first().click()
  await expect.poll(() => placed?.durationFrames).toBe(250)
  verifyConsole()
})

test('asset creator directs music to the versioned composition studio', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  let releaseAssetLibrary!: () => void
  const assetLibraryReady = new Promise<void>(resolve => { releaseAssetLibrary = resolve })
  await page.route('**/api/assets*', async route => {
    if (new URL(route.request().url()).pathname !== '/api/assets') return route.fallback()
    await assetLibraryReady
    await route.continue()
  })
  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  const createNew = page.locator('.asset-hero-actions').getByRole('button', { name: 'Create new' })
  await expect(createNew).toBeDisabled()
  releaseAssetLibrary()
  await expect(createNew).toBeEnabled()
  await createNew.click()
  const creator = page.getByRole('dialog', { name: 'New asset' })
  await creator.getByRole('tab', { name: 'Music' }).click()
  await expect(creator.locator('.preview-tag')).toHaveText('Editable')
  await expect(creator).toContainText('every score edit creates a recoverable revision')
  await creator.getByRole('button', { name: 'Go to Sequence → Music' }).click()

  await expect(creator).toBeHidden()
  verifyConsole()
})

test('guided setup launch opens the visible production setup drawer', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.goto('/?setup=1')

  await expect(page.getByTestId('setup-drawer')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Production setup' })).toBeVisible()
  await expect(page.getByTestId('generation-settings')).toBeVisible()
  verifyConsole()
})

test('generated shot media names its owning shot in the asset inspector', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'One deterministic desktop proof covers generated-media provenance.')
  const verifyConsole = failOnConsoleErrors(page)
  const seeded = await (await page.request.get('/api/studio')).json()
  const shot = seeded.shots[0]
  const assetId = crypto.randomUUID()
  const createdAt = new Date().toISOString()
  await page.route('**/api/studio', async route => {
    const response = await route.fetch()
    const snapshot = await response.json()
    snapshot.jobs.unshift({
      id: crypto.randomUUID(), shotId: shot.id, shotCode: shot.code, kind: 'Video', state: 'Completed',
      progress: 100, phase: 'Candidate ready for review', backend: 'Browser H3 proof', createdAt,
      completedAt: createdAt, adapterId: 'browser-h3', outputAssetId: assetId,
      outputAssetUrl: `/api/assets/${assetId}/content`, attempt: 1, workType: 'Shot',
    })
    await route.fulfill({ response, json: snapshot })
  })
  await page.route(/\/api\/assets(?:\?.*)?$/, async route => {
    const response = await route.fetch()
    const assets = await response.json()
    assets.unshift({
      id: assetId, kind: 'Video', originalFileName: 'generated-review-proof.mp4', mimeType: 'video/mp4',
      bytes: 1, width: 864, height: 480, durationSeconds: 5, contentHash: 'b'.repeat(64),
      contentUrl: `/api/assets/${assetId}/content`, createdAt, displayName: 'Generated review proof',
      source: 'Generated media', tags: [], notes: '', isArchived: false,
      revisionFamilyId: crypto.randomUUID(), revisionNumber: 1, isCurrentRevision: true,
    })
    await route.fulfill({ response, json: assets })
  })
  await page.route(`**/api/assets/${assetId}/content`, route => route.fulfill({
    status: 200, contentType: 'video/mp4', body: Buffer.alloc(1),
  }))

  await page.goto('/')
  await page.getByRole('button', { name: 'Assets', exact: true }).click()
  await page.getByRole('button', { name: 'Open Generated review proof' }).click()
  const inspector = page.getByRole('complementary', { name: 'Generated review proof details' })
  await expect(inspector.getByText(shot.code, { exact: true })).toBeVisible()
  await expect(inspector.getByText('Generated review take · Browser H3 proof')).toBeVisible()
  await expect(inspector.getByText('Not attached to a shot yet.')).toHaveCount(0)
  verifyConsole()
})

test('ratified finals expose a visual-only video handoff', async ({ page }) => {
  const verifyConsole = failOnConsoleErrors(page)
  const seeded = await (await page.request.get('/api/studio')).json()
  let shot = seeded.shots.find((item: { code: string }) => item.code === 'SH-040')
  if (!shot.currentAssetId || shot.stage !== 'Final') {
    const sketchResponse = await page.request.get(`/api/shots/${shot.id}/sketch`)
    const sketch = sketchResponse.status() === 204
      ? await (await page.request.put(`/api/shots/${shot.id}/sketch`, { data: { expectedRevision: 0, creativeBrief: 'Video handoff proof.', content: { strokes: [], labels: [] } } })).json()
      : await sketchResponse.json()
    const prepared = await (await page.request.post(`/api/shots/${shot.id}/manifests/prepare`, { data: { expectedShotVersion: shot.version, expectedSketchRevision: sketch.revision, route: 'PrecisionDraft', purpose: 'Final' } })).json()
    await page.request.post(`/api/manifests/${prepared.id}/dispatch`, { data: { expectedManifestHash: prepared.manifestHash, adapterId: 'local-proof' } })
    await expect.poll(async () => {
      const current = await (await page.request.get('/api/studio')).json()
      shot = current.shots.find((item: { code: string }) => item.code === 'SH-040')
      return Boolean(shot.currentAssetId && shot.stage === 'Final')
    }).toBe(true)
  }
  if (shot.approval !== 'Ratified') await page.request.post(`/api/shots/${shot.id}/ratify`, { data: { expectedVersion: shot.version, reason: 'Video handoff browser proof' } })
  await page.goto('/')
  await page.getByRole('button', { name: /Open SH-040, Guard interruption/ }).click()
  await page.getByRole('button', { name: 'Generate video' }).click()
  const dialog = page.getByRole('dialog', { name: 'Generate SH-040 video' })
  await expect(dialog.getByText('START FRAME · REQUIRED', { exact: true })).toBeVisible()
  await expect(dialog.getByText('END FRAME · OPTIONAL', { exact: true })).toBeVisible()
  await expect(dialog.getByText(/Any selected image revision can start the shot; an ending frame remains optional/i)).toBeVisible()
  await expect(dialog.getByRole('combobox', { name: 'Start frame' })).toBeVisible()
  await expect(dialog.getByRole('combobox', { name: 'End frame' })).toHaveValue('')
  await expect(dialog.locator('input[type="file"]')).toHaveCount(0)
  await expect(dialog.getByText(/explicitly excludes music, dialogue, voices/i)).toBeVisible()
  // The real adapter is wired but remains protected in the browser sandbox.
  // The invariant above also proves the frozen brief excludes audio, and a
  // final must be imported before anything can be dispatched.
  await expect(dialog.getByRole('radio', { name: /ComfyUI H3 video/ })).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Review Low settings' })).toBeEnabled()
  verifyConsole()
})

test('dropping an image on the board creates a real draft and Codex can structure the editable form', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const snapshot = await (await page.request.get('/api/studio')).json()
  const reference = snapshot.references[0]
  const code = `DROP_${testInfo.project.name}_${Date.now().toString().slice(-6)}`.toUpperCase()
  await page.route('**/api/codex/shot-intent', route => route.fulfill({
    status: 200, contentType: 'application/json', body: JSON.stringify({
      title: 'Dropped frame arrival', description: 'Ennix arrives beneath the Aerie bridge.', durationFrames: 64,
      camera: '35mm equivalent · medium wide · locked', action: 'Ennix enters frame and holds at the threshold.',
      referenceIds: [reference.id], constraints: ['Keep the bridge visible'], live: true,
      detail: 'Codex filled the editable fields.',
    }),
  }))
  await page.goto('/')

  const transfer = await imageDataTransfer(page, `${code.toLowerCase()}.png`)
  const board = page.getByTestId('board-workspace')
  await board.dispatchEvent('dragenter', { dataTransfer: transfer })
  await expect(page.getByText('Drop image to add a shot')).toBeVisible()
  await board.dispatchEvent('drop', { dataTransfer: transfer })

  const dialog = page.getByRole('dialog', { name: 'Add a shot' })
  await expect(dialog.getByAltText('New shot draft preview')).toBeVisible()
  await expect(dialog.getByText('This becomes the first draft of the shot.')).toBeVisible()
  const artistWords = 'Ennix arrives under the bridge. Use the approved authority and keep the bridge visible.'
  await dialog.getByLabel('Describe the shot').fill(artistWords)
  await dialog.getByRole('button', { name: 'Suggest details with Codex' }).click()
  await expect(dialog.getByLabel('Title')).toHaveValue('Dropped frame arrival')
  // Codex fills the folded-away fields and opens them; the sentence stays the artist's.
  await expect(dialog.getByLabel('Camera')).toHaveValue('35mm equivalent · medium wide · locked')
  await expect(dialog.getByLabel('Describe the shot')).toHaveValue(artistWords)
  await expect(dialog.getByText(reference.name, { exact: true })).toBeVisible()
  await expect(dialog.getByText(/Review the details below/)).toBeVisible()
  await dialog.getByLabel('Shot code').fill(code)
  await dialog.getByRole('button', { name: 'Add shot with image' }).click()

  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  await expect.poll(async () => {
    const state = await (await page.request.get('/api/studio')).json()
    return state.shots.find((item: { code: string }) => item.code === code)
  }).toMatchObject({ code, stage: 'Draft' })
  const saved = (await (await page.request.get('/api/studio')).json()).shots.find((item: { code: string }) => item.code === code)
  expect(saved.currentAssetId).toBeTruthy()
  const candidates = await (await page.request.get(`/api/shots/${saved.id}/candidates`)).json()
  expect(candidates.find((item: { isCurrent: boolean }) => item.isCurrent)?.assetId).toBe(saved.currentAssetId)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('dropping an image on an authority creates a new immutable top revision', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'The immutable authority drop contract only needs one shared-database proof.')
  const verifyConsole = failOnConsoleErrors(page)
  const name = `Drop Authority ${Date.now().toString().slice(-6)}`
  const created = await page.request.post('/api/references', { data: {
    name, category: 'Prop', description: 'A compact authority made for the drop test.',
    lockedConstraint: 'The circular mark remains centered.', accent: '#9b7653',
  } })
  expect(created.ok()).toBeTruthy()
  await page.goto('/')
  await page.getByRole('tab', { name: /Authorities/ }).click()
  await page.getByRole('button', { name: new RegExp(`Open ${name}.*version 1`) }).click()

  const canvas = page.locator('.authority-canvas')
  const transfer = await imageDataTransfer(page, 'authority-drop.png')
  await canvas.dispatchEvent('dragenter', { dataTransfer: transfer })
  await expect(page.getByText('Drop to create a new revision')).toBeVisible()
  await canvas.dispatchEvent('drop', { dataTransfer: transfer })
  const history = page.getByRole('listbox', { name: `${name} versions` })
  await expect(history.getByRole('option')).toHaveCount(2)
  await expect(history.getByRole('option').first()).toContainText('v2')
  await expect(history.getByRole('option').first()).toContainText('Live authority')
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('shot and authority editors persist real production entities', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const tablet = testInfo.project.name === 'tablet'
  const code = tablet ? 'SH-890' : 'SH-880'
  const authorityName = tablet ? 'Signal Lantern Tablet' : 'Signal Lantern Desktop'
  await page.goto('/')

  await page.getByRole('button', { name: 'Add card' }).first().click()
  const shotDialog = page.getByRole('dialog', { name: 'Add a shot' })
  await shotDialog.getByText('Camera, action and references').click()
  await shotDialog.getByLabel('Shot code').fill(code)
  await shotDialog.getByLabel('Title').fill('Lantern handoff')
  await shotDialog.getByLabel('Describe the shot').fill('Ennix receives the signal lantern at the bridge threshold.')
  await shotDialog.getByLabel('Action').fill('The lantern crosses frame left to right and settles in Ennix’s hand.')
  await shotDialog.getByLabel('Camera').fill('Medium close · eye level · 50 mm')
  await shotDialog.getByLabel(/Must stay true/).fill('Lantern has three brass ribs\nScreen direction remains left to right')
  await shotDialog.getByRole('button', { name: 'Add shot', exact: true }).click()
  await expect(page.getByTestId('shot-workspace')).toBeVisible()
  await expect(page.locator('.canvas-caption').getByText(new RegExp(code))).toBeVisible()

  await page.getByRole('button', { name: 'Board', exact: true }).click()
  await page.getByRole('tab', { name: /Authorities/ }).click()
  await page.getByRole('button', { name: 'Add authority' }).first().click()
  const authorityDialog = page.getByRole('dialog', { name: 'Add authority' })
  await authorityDialog.getByLabel('Name').fill(authorityName)
  await authorityDialog.getByLabel('Category').selectOption('Prop')
  await authorityDialog.getByLabel('Identity and context').fill('A hand-sized amber signal lantern with a weathered black grip.')
  await authorityDialog.getByLabel('Locked constraint').fill('Exactly three brass ribs surround the amber lens.')
  await authorityDialog.getByRole('button', { name: 'Create authority' }).click()
  await expect(page.getByRole('heading', { name: authorityName })).toBeVisible()
  await expect(page.getByRole('listbox', { name: `${authorityName} versions` }).getByRole('option')).toHaveCount(1)
  await page.getByLabel('Identity and context').fill('A hand-sized amber signal lantern with a weathered black grip and a dim pilot glow.')
  await page.getByRole('button', { name: 'Save as new revision' }).click()
  const history = page.getByRole('listbox', { name: `${authorityName} versions` })
  await expect(history.getByRole('option')).toHaveCount(2)
  await expect(history.getByRole('option').first()).toContainText('v2')
  await expect(history.getByRole('option').first()).toContainText('Live authority')
  await expect(history.getByRole('option').last()).toContainText('v1')
  await page.getByRole('button', { name: 'Authorities' }).click()
  await expect(page.getByRole('button', { name: new RegExp(`Open ${authorityName}.*version 2`) })).toBeVisible()
  await expectNoHorizontalPageOverflow(page)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('a shot card can generate directly without visiting the sketchboard', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const code = testInfo.project.name === 'tablet' ? 'SH-891' : 'SH-881'
  let generationBody: { expectedShotVersion?: number; adapterId?: string } | undefined
  let sketchRequested = false
  await mockReadyOneClickEngines(page)
  await page.route('**/api/shots/*/sketch', async route => {
    sketchRequested = true
    await route.continue()
  })
  await page.route('**/api/shots/*/generate-draft', async route => {
    generationBody = route.request().postDataJSON() as { expectedShotVersion?: number; adapterId?: string }
    await route.fulfill({
      status: 202,
      contentType: 'application/json',
      body: JSON.stringify({ id: crypto.randomUUID(), state: 'Queued', progress: 0, phase: 'Queued', shotCode: code }),
    })
  })
  await page.goto('/')

  await page.getByRole('button', { name: 'Add card' }).first().click()
  const dialog = page.getByRole('dialog', { name: 'Add a shot' })
  await dialog.getByText('Camera, action and references').click()
  await dialog.getByLabel('Shot code').fill(code)
  await dialog.getByLabel('Title').fill('Direct generation')
  await dialog.getByLabel('Describe the shot').fill('Ennix waits alone under the Aerie bridge at blue hour.')
  await dialog.getByLabel('Action').fill('She turns toward an approaching light.')
  await dialog.getByRole('button', { name: 'Add shot', exact: true }).click()

  const generate = page.getByRole('button', { name: 'Generate fast draft' })
  const generateWithCodex = page.getByRole('button', { name: 'Generate with Codex' })
  await expect(generate).toBeVisible()
  await expect(generateWithCodex).toBeVisible()
  await expect(page.locator('.workflow-badge')).toContainText('ComfyUI Text Draft')
  await expect(page.locator('.workflow-badge')).toContainText('Text to image')
  await generate.click()
  await expect.poll(() => generationBody).toEqual({ expectedShotVersion: 1, adapterId: 'comfyui-fast-draft' })
  await generateWithCodex.click()
  await expect.poll(() => generationBody).toEqual({ expectedShotVersion: 1, adapterId: 'codex-imagegen' })
  expect(sketchRequested).toBe(false)
  verifyConsole()
})

test('authority workspace promotes, edits, and removes uncited revisions in place', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const suffix = testInfo.project.name === 'tablet' ? 'Tablet' : 'Desktop'
  const name = `Continuity Token ${suffix}`
  const created = await (await page.request.post('/api/references', { data: { name, category: 'Prop', description: 'A square continuity token with a centered circular cutout.', lockedConstraint: 'The cutout remains exactly centered.', accent: '#7a8e9c' } })).json()
  const v2 = await (await page.request.post(`/api/references/${created.id}/versions`, { data: { expectedVersion: 1, description: 'A weathered square continuity token with a centered circular cutout.', lockedConstraint: 'The cutout remains exactly centered.' } })).json()
  await page.request.post(`/api/references/${created.id}/versions`, { data: { expectedVersion: v2.version, description: 'A graphite square continuity token with a centered circular cutout.', lockedConstraint: 'The cutout remains exactly centered.' } })

  await page.goto('/')
  await page.getByRole('tab', { name: /Authorities/ }).click()
  await page.getByRole('button', { name: new RegExp(`Open ${name}`) }).click()
  const history = page.getByRole('listbox', { name: `${name} versions` })
  await history.getByRole('option', { name: /v1/ }).click()
  await page.getByRole('button', { name: 'Make this the live authority' }).click()
  await expect(history.getByRole('option').first()).toContainText('v4')
  await expect(history.getByRole('option').first()).toContainText('Live authority')

  await history.getByRole('option', { name: /v2/ }).click()
  page.once('dialog', dialog => dialog.accept())
  await page.getByRole('button', { name: 'Delete v2' }).click()
  await expect(history.getByRole('option', { name: /v2/ })).toHaveCount(0)

  await page.getByLabel('Name').fill(`${name} Renamed`)
  await page.getByRole('button', { name: 'Save identity' }).click()
  await expect(page.getByRole('heading', { name: `${name} Renamed` })).toBeVisible()
  await expectNoHorizontalPageOverflow(page)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('generate authority revision uses the exact selected revision as its visible provider input', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const suffix = testInfo.project.name === 'tablet' ? 'Tablet' : 'Desktop'
  const name = `Revision Source ${suffix}`
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64')
  const upload = await page.request.post('/api/assets/images', {
    headers: { 'X-Storyboard-Studio': '1' },
    multipart: { file: { name: `revision-source-${suffix}.png`, mimeType: 'image/png', buffer: png } },
  })
  expect(upload.ok()).toBeTruthy()
  const sourceAsset = await upload.json()
  const created = await (await page.request.post('/api/references', { data: {
    name, category: 'Location', description: 'A limestone chamber with a single circular threshold.',
    lockedConstraint: 'The circular threshold stays centered.', accent: '#75909a', imageAssetId: sourceAsset.id,
  } })).json()
  await page.request.post(`/api/references/${created.id}/versions`, { data: {
    expectedVersion: 1, description: 'A darker limestone chamber with a single circular threshold.',
    lockedConstraint: 'The circular threshold stays centered.', imageAssetId: sourceAsset.id,
  } })

  await page.route('**/api/generation/adapters', route => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([
    { id: 'comfyui-fast-draft', name: 'ComfyUI fast draft', kind: 'Local service', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['FastDraft'], purposes: ['Draft'] },
    { id: 'openai-gpt-image', name: 'OpenAI GPT Image', kind: 'Cloud', state: 'Ready', detail: 'Test adapter', canDispatch: true, routes: ['PrecisionDraft'], purposes: ['Draft', 'Final'] },
  ]) }))
  let generationBody: { compositionAssetId?: string; referenceAssetIds?: string[]; adapterId?: string; authorityTarget?: { referenceId: string; expectedVersion: number } } | undefined
  await page.route('**/api/assets/generate-image', async route => {
    generationBody = route.request().postDataJSON()
    await route.fulfill({ status: 202, contentType: 'application/json', body: JSON.stringify({ id: crypto.randomUUID(), shotId: '00000000-0000-0000-0000-000000000000', shotCode: `${name} generated revision`, kind: 'Authority image', state: 'Queued', progress: 0, phase: 'Frozen image request queued', backend: 'ComfyUI fast draft', createdAt: new Date().toISOString(), attempt: 1, workType: 'Asset' }) })
  })

  await page.goto('/')
  await page.getByRole('tab', { name: /Authorities/ }).click()
  await page.getByRole('button', { name: new RegExp(`Open ${name}`) }).click()
  const history = page.getByRole('listbox', { name: `${name} versions` })
  await history.getByRole('option', { name: /v1/ }).click()
  await page.getByRole('button', { name: 'Generate new revision' }).click()

  await expect(page.getByRole('region', { name: 'Selected revision source' })).toContainText(`${name} · v1`)
  await expect(page.getByRole('img', { name: `${name} version 1 selected as revision source` })).toBeVisible()
  await expect.poll(async () => {
    const box = await page.getByTestId('sketch-canvas').boundingBox()
    return box ? box.width / box.height : 0
  }).toBeCloseTo(1, 1)
  await expect(page.getByText(`${name} v1 is the base`)).toBeVisible()
  await expect(page.getByText('Its approved image is sent directly to the selected image-edit provider.')).toBeVisible()
  await expect(page.getByText('ComfyUI Current Frame Edit · Current frame edit')).toBeVisible()
  await expect(page.getByText('Text to image')).toHaveCount(0)
  await page.getByRole('button', { name: 'Precision image' }).click()
  await expect(page.getByText('High-fidelity image-guided pass')).toBeVisible()
  await page.getByRole('button', { name: 'Fast Draft' }).click()
  await page.getByRole('button', { name: 'Generate revision in ComfyUI' }).click()
  await expect.poll(() => generationBody).toMatchObject({ compositionAssetId: sourceAsset.id, adapterId: 'comfyui-fast-draft', authorityTarget: { referenceId: created.id, expectedVersion: 2 } })
  expect(generationBody?.referenceAssetIds).not.toContain(sourceAsset.id)
  await expect(page.getByText(/queued.*attach automatically/i)).toBeVisible()
  await expectNoHorizontalPageOverflow(page)
  await expectNoSeriousAccessibilityViolations(page)
  verifyConsole()
})

test('a completed background authority render appears without leaving the authority', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'The shared background-refresh contract only needs one browser proof.')
  const verifyConsole = failOnConsoleErrors(page)
  const name = `Background Authority ${Date.now().toString().slice(-6)}`
  const created = await (await page.request.post('/api/references', { data: {
    name, category: 'Prop', description: 'A compact brass direction marker.',
    lockedConstraint: 'The triangular face remains centered.', accent: '#9b7653',
  } })).json()
  const initialSnapshot = await (await page.request.get('/api/studio')).json()
  const jobId = crypto.randomUUID()
  const startedAt = new Date().toISOString()
  let releaseCompletion = false
  let completedSnapshot = initialSnapshot

  await page.route('**/api/studio', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      ...(releaseCompletion ? completedSnapshot : initialSnapshot),
      jobs: [{
        id: jobId, shotId: '00000000-0000-0000-0000-000000000000',
        shotCode: `${name} revision`, kind: 'Authority image',
        state: releaseCompletion ? 'Completed' : 'Running', progress: releaseCompletion ? 100 : 30,
        phase: releaseCompletion ? `${name} v2 ready` : 'Generating authority image',
        backend: 'Codex ImageGen', createdAt: startedAt,
        completedAt: releaseCompletion ? new Date().toISOString() : undefined,
        attempt: 1, workType: 'Asset',
      }],
    }),
  }))

  await page.goto('/')
  await page.getByRole('tab', { name: /Authorities/ }).click()
  await page.getByRole('button', { name: new RegExp(`Open ${name}.*version 1`) }).click()
  const history = page.getByRole('listbox', { name: `${name} versions` })
  await expect(history.getByRole('option')).toHaveCount(1)

  const generated = await page.request.post(`/api/references/${created.id}/versions`, { data: {
    expectedVersion: 1,
    description: 'A refined brass direction marker with a softly weathered surface.',
    lockedConstraint: 'The triangular face remains centered.',
  } })
  expect(generated.ok()).toBeTruthy()
  completedSnapshot = await (await page.request.get('/api/studio')).json()
  releaseCompletion = true

  await expect(history.getByRole('option')).toHaveCount(2)
  await expect(history.getByRole('option').first()).toContainText('v2')
  await expect(history.getByRole('option').first()).toContainText('Live authority')
  await expect(page.locator('.authority-stage .eyebrow')).toContainText('v2')
  verifyConsole()
})

test('image review pins stay on the exact authority asset and feed the next revision', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  const suffix = testInfo.project.name === 'tablet' ? 'Tablet' : 'Desktop'
  const name = `Pinned Asset Review ${suffix}`
  const note = 'Restore the small notch through the anatomical left eyebrow.'
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64')
  const upload = await page.request.post('/api/assets/images', {
    headers: { 'X-Storyboard-Studio': '1' },
    multipart: { file: { name: `pinned-asset-${suffix}.png`, mimeType: 'image/png', buffer: png } },
  })
  const source = await upload.json()
  const inherited = await (await page.request.get(`/api/assets/${source.id}/review-notes`)).json()
  for (const existing of inherited.filter((item: { state: string }) => item.state === 'Open'))
    await page.request.post(`/api/asset-review-notes/${existing.id}/resolve`)
  await page.request.post('/api/references', { data: {
    name, category: 'Character', description: 'A calm observatory keeper with short dark curls.',
    lockedConstraint: 'Keep the small notch through the anatomical left eyebrow.', accent: '#8f6f55', imageAssetId: source.id,
  } })

  await page.goto('/')
  await page.getByRole('tab', { name: /Authorities/ }).click()
  await page.getByRole('button', { name: new RegExp(`Open ${name}`) }).click()
  await page.getByRole('button', { name: 'Add note' }).click()
  const layer = page.getByLabel(`${name} v1 review layer`)
  const box = await layer.boundingBox()
  expect(box).not.toBeNull()
  await layer.click({ position: { x: box!.width * .44, y: box!.height * .31 } })
  await page.getByLabel('Image revision note').fill(note)
  await page.getByRole('button', { name: 'Save note' }).click()

  const notePin = page.getByRole('button', { name: /Image note 1: Restore the small notch/ })
  await expect(notePin).toBeVisible()
  const noteDialog = page.getByRole('dialog', { name: 'Revision note 1' })
  await expect(noteDialog).toBeVisible()
  await page.getByRole('button', { name: 'Collapse revision note 1' }).click()
  await expect(noteDialog).toBeHidden()
  await notePin.click()
  await expect(noteDialog).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(noteDialog).toBeHidden()
  await expect(page.getByRole('button', { name: 'Regenerate from 1 note' })).toBeVisible()
  const stored = await page.request.get(`/api/assets/${source.id}/review-notes`)
  const notes = (await stored.json()).filter((item: { state: string }) => item.state === 'Open')
  expect(notes).toEqual([expect.objectContaining({ assetId: source.id, state: 'Open', body: note })])

  await page.getByRole('button', { name: 'Regenerate from 1 note' }).click()
  await expect(page.getByLabel('Creative brief')).toContainText(note)
  await expect(page.getByLabel('Creative brief')).toContainText('44% across / 31% down')
  await page.request.post(`/api/asset-review-notes/${notes[0].id}/resolve`)
  await expectNoHorizontalPageOverflow(page)
  verifyConsole()
})

test('asset library organizes media and carries it into shot work', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page)
  await page.goto('/')
  await page.getByRole('button', { name: 'Assets' }).click()
  await expect(page.getByRole('heading', { name: 'Asset library' })).toBeVisible()
  await expect(page.getByText('Project media pool')).toBeVisible()
  await page.getByRole('button', { name: /^Authorities \d+$/ }).click()
  await expect(page.getByText('Immutable canon lives here too')).toBeVisible()
  await expectNoHorizontalPageOverflow(page)
  await page.getByRole('button', { name: /All assets/ }).click()

  const collectionName = `Shot plates ${testInfo.project.name}`
  await page.getByRole('button', { name: 'Create collection' }).click()
  const collectionDialog = page.getByRole('dialog', { name: 'New collection' })
  await collectionDialog.getByLabel('Name').fill(collectionName)
  await collectionDialog.getByRole('button', { name: 'Save collection' }).click()
  await expect(page.getByRole('button', { name: new RegExp(collectionName) })).toBeVisible()

  const png = Buffer.concat([Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64'), Buffer.from(testInfo.project.name)])
  const fileName = `library-${testInfo.project.name}.png`
  await page.locator('.asset-hero input[type="file"]').setInputFiles({ name: fileName, mimeType: 'image/png', buffer: png })
  await expect(page.getByText(/asset imported into the library/i)).toBeVisible()
  await page.getByRole('button', { name: /All assets/ }).click()
  await expectNoHorizontalPageOverflow(page)
  await page.getByRole('button', { name: `Open ${fileName.replace('.png', '')}` }).click()
  const inspector = page.getByRole('complementary', { name: /details/ })
  await inspector.getByLabel('Name').fill(`Aerie plate ${testInfo.project.name}`)
  await inspector.getByLabel('Collection').selectOption({ label: collectionName })
  await inspector.getByLabel(/Tags/).fill('architecture, threshold')
  await inspector.getByLabel('Notes').fill('Working plate for threshold proportions; not an approved authority.')
  await inspector.getByRole('button', { name: 'Save details' }).click()
  await expect(page.getByText(/details saved/i)).toBeVisible()
  const promotion = page.locator('details.authority-promotion-disclosure')
  await promotion.locator('summary').click()
  await expect(promotion.getByText('Freeze this exact image and its rules as reusable canon.')).toBeVisible()
  await expect(promotion.getByLabel('What this reference controls')).toBeVisible()
  await expect(promotion.getByRole('button', { name: 'Create immutable authority' })).toBeEnabled()
  await promotion.locator('summary').click()
  await expect(promotion.getByRole('button', { name: 'Create immutable authority' })).toBeHidden()
  await inspector.getByRole('button', { name: 'Add', exact: true }).click()
  await expect(page.getByText(/added to SH-010/i)).toBeVisible()
  await page.locator('.image-revision-header').getByRole('button', { name: 'Assets', exact: true }).click()

  await page.getByRole('button', { name: 'Shot', exact: true }).click()
  await page.getByRole('tab', { name: /Media/ }).click()
  const shotWorkspace = page.getByTestId('shot-workspace')
  await expect(shotWorkspace.getByText(`Aerie plate ${testInfo.project.name}`)).toBeVisible()
  await expect(shotWorkspace.getByText(/Image guide/).first()).toBeVisible()

  await page.getByRole('button', { name: 'Assets' }).click()
  await page.getByRole('button', { name: 'Create new' }).click()
  await page.getByLabel(/Starting direction/).fill('A severe doorway composition with two figures held at opposite thirds.')
  await page.getByRole('button', { name: /Precision image Choose Codex or direct OpenAI next/ }).click()
  await page.getByRole('button', { name: 'Open still-image studio' }).click()
  await expect(page.getByTestId('sketch-canvas')).toBeVisible()
  await expect(page.getByLabel('Creative brief')).toHaveValue(/severe doorway composition/)
  await expectNoHorizontalPageOverflow(page)
  verifyConsole()
})

test('director mode gives the frame the whole workstation without forking shot state', async ({ page }, testInfo) => {
  const verifyConsole = failOnConsoleErrors(page, [/409 \(Conflict\)/])
  const note = `Full-view direction note ${testInfo.project.name}-${Date.now()}`
  const draftSuffix = ` Director-mode draft ${testInfo.project.name}.`
  const code = testInfo.project.name === 'tablet' ? 'SH-DM-T' : 'SH-DM-D'
  const opener = new RegExp(`Open ${code},`)
  await page.request.post('/api/shots', { data: {
    code, title: 'Full-view direction proof', description: 'Remora holds the seal at the chain court gate.', durationFrames: 72,
    camera: '50mm equivalent · medium · locked', action: 'Remora waits for the gate.', referenceIds: [], constraints: ['Keep the seal in the anatomical left hand.'],
  } })

  await page.goto('/')
  await page.getByRole('button', { name: opener }).click()
  await expect(page.getByTestId('shot-workspace')).toBeVisible()

  // One extra candidate through the no-network proof route so full view has a
  // live head and an archived revision to move between.
  await page.locator('.shot-toolbar-actions').getByRole('button', { name: 'Sketch' }).click()
  const brief = page.getByRole('textbox', { name: 'Creative brief' })
  await brief.fill(`${await brief.inputValue()} Director mode proof.`)
  await expect(page.getByText(/Saved to studio/)).toBeVisible()
  await page.getByRole('button', { name: 'Generate draft in ComfyUI' }).click()
  await expect(page.getByRole('alert')).toContainText('ComfyUI image generation is turned off')
  const snapshot = await (await page.request.get('/api/studio')).json()
  const shot = snapshot.shots.find((item: { code: string }) => item.code === code)
  const manifests = await (await page.request.get(`/api/shots/${shot.id}/manifests`)).json()
  const proof = await page.request.post(`/api/manifests/${manifests[0].id}/dispatch`, { data: { expectedManifestHash: manifests[0].manifestHash, adapterId: 'local-proof' } })
  expect(proof.ok()).toBe(true)
  await page.reload()
  await page.getByRole('button', { name: opener }).click()

  // An unsaved inspector draft is the strictest proof that full view restyles
  // the shell rather than remounting the workspace.
  const description = page.getByRole('textbox', { name: 'Shot description' })
  await description.fill(`${await description.inputValue()}${draftSuffix}`)
  await expect(page.getByText('Unsaved intent changes')).toBeVisible()

  await page.getByRole('button', { name: 'Place comment' }).click()
  await page.getByTestId('shot-canvas').focus()
  await page.getByTestId('shot-canvas').press('Enter')
  await page.getByPlaceholder(/Be specific/).fill(note)
  await page.getByRole('button', { name: 'Pin feedback' }).click()
  await expect(page.getByText('Feedback pinned to this exact version.')).toBeVisible()
  const pin = page.getByRole('button', { name: new RegExp(note.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')) })
  const savedPin = async () => {
    const state = await (await page.request.get('/api/studio')).json()
    return state.comments.find((comment: { body: string }) => comment.body === note) as { id: string; x: number; y: number; version: number }
  }
  const pinned = await savedPin()
  expect(pinned).toBeTruthy()

  // Where the pin sits inside the frame, as a fraction of the stage. This must
  // survive every size the stage takes, or notes drift off their subject.
  const placement = async () => {
    const stage = (await page.getByTestId('shot-canvas').boundingBox())!
    const marker = (await pin.boundingBox())!
    return { stage, x: (marker.x + marker.width / 2 - stage.x) / stage.width, y: (marker.y + marker.height / 2 - stage.y) / stage.height }
  }
  const windowed = await placement()
  expect(windowed.x).toBeCloseTo(pinned.x, 2)
  expect(windowed.y).toBeCloseTo(pinned.y, 2)

  // Full view is reachable by keyboard and, on a coarse pointer, by a target
  // that meets the production minimum.
  const toggle = page.getByRole('button', { name: 'Director mode' })
  if (testInfo.project.name === 'tablet') expect((await toggle.boundingBox())!.height).toBeGreaterThanOrEqual(44)
  await toggle.focus()
  await toggle.press('Enter')

  await expect(page.locator('.workspace-rail')).toBeHidden()
  await expect(page.locator('.context-bar')).toBeHidden()
  await expect(page.locator('.version-rail')).toBeHidden()
  await expect(page.locator('.shot-inspector')).toBeHidden()
  await expect(page.getByRole('button', { name: 'Exit full view' })).toBeVisible()
  await expect(page.locator('.canvas-caption')).toContainText(code)

  // A wide workstation gives the frame more room. A tablet frame is already
  // full width, so there full view buys chrome-free height rather than pixels.
  const full = await placement()
  const area = (box: { width: number; height: number }) => box.width * box.height
  expect(area(full.stage)).toBeGreaterThanOrEqual(area(windowed.stage))
  if (testInfo.project.name === 'desktop') expect(full.stage.width).toBeGreaterThan(windowed.stage.width)
  expect(full.stage.width / full.stage.height).toBeCloseTo(snapshot.project.deliveryWidth / snapshot.project.deliveryHeight, 1)
  expect(full.stage.width).toBeLessThanOrEqual(page.viewportSize()!.width + 1)
  expect(full.stage.height).toBeLessThanOrEqual(page.viewportSize()!.height + 1)
  expect(full.x).toBeCloseTo(pinned.x, 2)
  expect(full.y).toBeCloseTo(pinned.y, 2)
  await expectNoHorizontalPageOverflow(page)
  await expectNoSeriousAccessibilityViolations(page)

  // A modal opened inside full view gets its own Escape before the shell does.
  await page.getByRole('button', { name: 'Place comment' }).click()
  await page.getByTestId('shot-canvas').press('Enter')
  await expect(page.getByPlaceholder(/Be specific/)).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByPlaceholder(/Be specific/)).toBeHidden()
  await expect(page.getByRole('button', { name: 'Exit full view' })).toBeVisible()

  // Drawing is a first-class full-view action, not a windowed-only one.
  await page.getByRole('button', { name: 'Draw annotation' }).click()
  const markup = page.locator('.shot-canvas canvas')
  const ink = (await markup.boundingBox())!
  await page.mouse.move(ink.x + ink.width * .3, ink.y + ink.height * .35)
  await page.mouse.down()
  await page.mouse.move(ink.x + ink.width * .6, ink.y + ink.height * .6, { steps: 8 })
  await page.mouse.up()
  await expect(page.getByText(/AI edit guide saved/)).toBeVisible()

  // A narrower workstation must not move the note off its subject.
  const viewport = page.viewportSize()!
  await page.setViewportSize({ width: Math.round(viewport.width * .72), height: Math.round(viewport.height * .82) })
  await expect.poll(async () => Math.abs((await placement()).x - pinned.x)).toBeLessThan(.015)
  expect((await placement()).y).toBeCloseTo(pinned.y, 2)
  await page.setViewportSize(viewport)

  await page.keyboard.press('Escape')
  await expect(page.locator('.shot-inspector')).toBeVisible()
  await expect(page.locator('.workspace-rail')).toBeVisible()
  await expect(description).toHaveValue(new RegExp(draftSuffix.trim().replace(/[.*+?^${}()|[\]\\]/g, '\\$&')))
  await expect(page.getByText('Unsaved intent changes')).toBeVisible()
  await expect(page.getByText(/AI edit guide saved/)).toBeVisible()
  await expect(pin).toBeVisible()

  // The archived revision keeps its own locks in full view, and the live
  // head note never migrates onto it.
  const archived = page.getByRole('listbox', { name: 'Candidate versions' }).locator('.review-chip:not(.is-current)').first()
  const archivedVersion = (await archived.innerText()).trim()
  await archived.click()
  await page.getByRole('button', { name: 'Director mode' }).click()
  await expect(page.locator('.preview-banner')).toContainText(`Reviewing ${archivedVersion}`)
  await expect(page.getByRole('button', { name: 'Draw annotation' })).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Place comment' })).toBeDisabled()
  await expect(pin).toBeHidden()
  await page.getByRole('button', { name: 'Exit full view' }).click()
  await page.getByRole('listbox', { name: 'Candidate versions' }).getByRole('option').first().click()

  // Reopening the workspace shows exactly what was saved, and nothing that was
  // only ever a draft.
  await page.reload()
  await page.getByRole('button', { name: opener }).click()
  await expect(pin).toBeVisible()
  await expect(page.getByText(/Apply & regenerate attaches this guide/)).toBeVisible()
  await expect(description).not.toHaveValue(new RegExp(draftSuffix.trim().replace(/[.*+?^${}()|[\]\\]/g, '\\$&')))
  const reopened = await savedPin()
  expect(reopened.x).toBeCloseTo(pinned.x, 3)
  expect(reopened.version).toBe(pinned.version)

  await page.request.post(`/api/comments/${pinned.id}/resolve`)
  verifyConsole()
})
