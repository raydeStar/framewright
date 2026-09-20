import { expect, test } from '@playwright/test'

test.describe('browser WebMCP collaboration', () => {
  test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => {
      const tools = new Map<string, unknown>()
      const signals: AbortSignal[] = []
      Object.defineProperty(window, '__framewrightTools', { value: tools })
      Object.defineProperty(window, '__framewrightRegistrationSignals', { value: signals })
      Object.defineProperty(document, 'modelContext', { configurable: true, value: {
        registerTool(tool: { name: string }, options: { signal: AbortSignal }) {
          if (tools.has(tool.name)) throw new DOMException('Duplicate tool', 'InvalidStateError')
          tools.set(tool.name, tool)
          signals.push(options.signal)
          options.signal.addEventListener('abort', () => tools.delete(tool.name), { once: true })
        },
      } })
    })
  })

  test('registers exactly the nine closed-schema tools without duplicates', async ({ page }) => {
    await page.goto('/')
    await expect(page.getByTestId('agent-activity-button')).toBeVisible()
    const registration = await page.evaluate(() => {
      const tools = [...(window as unknown as { __framewrightTools: Map<string, { inputSchema: { additionalProperties: boolean } }> }).__framewrightTools]
      const signals = (window as unknown as { __framewrightRegistrationSignals: AbortSignal[] }).__framewrightRegistrationSignals
      return { names: tools.map(([name]) => name), closed: tools.every(([, tool]) => tool.inputSchema.additionalProperties === false), oneLifecycle: new Set(signals).size === 1, signalActive: signals.every(signal => !signal.aborted) }
    })
    expect(registration.names).toEqual(['get_storyboard_context', 'list_storyboard_shots', 'get_shot_details', 'inspect_shot_continuity', 'get_director_context', 'observe_current_frame', 'propose_shot_revision', 'propose_scene_edit', 'get_generation_status'])
    expect(registration.closed).toBe(true)
    expect(registration.oneLifecycle).toBe(true)
    expect(registration.signalActive).toBe(true)
    await page.getByTestId('agent-activity-button').click()
    await page.getByTestId('agent-tools-toggle').click()
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(0)
    await expect(page.getByTestId('agent-activity-panel')).toContainText('WebMCP paused')
    await page.getByTestId('agent-tools-toggle').click()
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(9)
  })

  test('tool calls select the shot and synchronize a durable visible proposal', async ({ page }) => {
    await page.goto('/')
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(9)
    const context = await page.evaluate(async () => {
      const tools = (window as unknown as { __framewrightTools: Map<string, { execute: (input: object, context: object) => Promise<{ data?: { selectedShot?: { id: string; version: number } } }> }> }).__framewrightTools
      return tools.get('get_storyboard_context')!.execute({}, {})
    })
    const selected = context.data?.selectedShot
    expect(selected).toBeTruthy()
    await page.evaluate(async ({ shotId, expectedVersion }) => {
      const tools = (window as unknown as { __framewrightTools: Map<string, { execute: (input: object, context: object) => Promise<{ data?: { stateToken?: string } }> }> }).__framewrightTools
      await tools.get('get_shot_details')!.execute({ shotId }, {})
      // Proposing requires a director context the agent actually read.
      const director = await tools.get('get_director_context')!.execute({}, {})
      await tools.get('propose_shot_revision')!.execute({
        shotId, expectedVersion, creativeDirection: 'Keep the blocking; lower the lantern to the floor beside Mara.',
        rationale: 'Human review should precede every provider call.', desiredMediaType: 'image', authorityIds: [], noteIds: [],
        preservedConstraints: [], observedStateToken: director.data!.stateToken,
        idempotencyKey: `browser-${crypto.randomUUID()}`,
      }, {})
    }, { shotId: selected!.id, expectedVersion: selected!.version })
    await expect(page.getByTestId('agent-activity-panel')).toBeVisible()
    await expect(page.getByTestId('agent-proposal-card').first()).toContainText('lower the lantern')
    await expect(page.getByTestId('agent-proposal-card').first()).toContainText('Pending')
  })

  test('director context names the revision on screen and refuses a view that moved under it', async ({ page }) => {
    await page.goto('/')
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(9)
    await page.getByRole('button', { name: /Open SH-010,/ }).click()
    await expect(page.getByTestId('shot-workspace')).toBeVisible()
    await page.getByRole('button', { name: 'Director mode' }).click()
    await expect(page.getByRole('button', { name: 'Exit full view' })).toBeVisible()

    type Envelope = { ok: boolean; code: string; data?: Record<string, never> & { stateToken?: string; subject?: { code: string; displayedVersion: number; archived: boolean }; view?: { directorMode: boolean; tool: string }; visual?: { kind: string; contentUrl: string | null }; annotations?: unknown[] } }
    const call = (name: string, input: object) => page.evaluate(({ name, input }) => {
      const tools = (window as unknown as { __framewrightTools: Map<string, { execute: (input: object, context: object) => Promise<unknown> }> }).__framewrightTools
      return tools.get(name)!.execute(input, {}) as Promise<unknown>
    }, { name, input }) as Promise<Envelope>

    const context = await call('get_director_context', {})
    expect(context.ok).toBe(true)
    expect(context.data!.subject!.code).toBe('SH-010')
    expect(context.data!.view!.directorMode).toBe(true)
    const token = context.data!.stateToken!
    expect(token).toHaveLength(64)

    // The packet and the picture must agree before an agent acts on either.
    const observation = await call('observe_current_frame', { stateToken: token })
    expect(observation.ok).toBe(true)
    expect(observation.code).toBe('frame_observation')

    // The artist pins a note. That is a different view, so the old pairing dies.
    await page.getByRole('button', { name: 'Place comment' }).click()
    await page.getByTestId('shot-canvas').focus()
    await page.getByTestId('shot-canvas').press('Enter')
    const note = `Director context staleness proof ${Date.now()}`
    await page.getByPlaceholder(/Be specific/).fill(note)
    await page.getByRole('button', { name: 'Pin feedback' }).click()
    await expect(page.getByText('Feedback pinned to this exact version.')).toBeVisible()

    const stale = await call('observe_current_frame', { stateToken: token })
    expect(stale.ok).toBe(false)
    expect(stale.code).toBe('stale_context')

    const refreshed = await call('get_director_context', {})
    expect(refreshed.data!.stateToken).not.toBe(token)
    expect(refreshed.data!.annotations!.length).toBeGreaterThan(0)
    const reobserved = await call('observe_current_frame', { stateToken: refreshed.data!.stateToken! })
    expect(reobserved.ok).toBe(true)

    // Reading context is not an interruption, so it never opens a panel by
    // itself; the artist can still see exactly what the agent read.
    await expect(page.getByTestId('agent-activity-panel')).toBeHidden()
    await page.getByRole('button', { name: 'Exit full view' }).click()
    await page.getByTestId('agent-activity-button').click()
    await expect(page.getByTestId('agent-activity-panel')).toContainText('observe_current_frame')
    const state = await (await page.request.get('/api/studio')).json()
    const created = state.comments.find((comment: { body: string }) => comment.body === note)
    if (created) await page.request.post(`/api/comments/${created.id}/resolve`)
  })

  test('director context follows an archived preview rather than the live head', async ({ page }, testInfo) => {
    const code = testInfo.project.name === 'tablet' ? 'SH-AGT-T' : 'SH-AGT-D'
    await page.request.post('/api/shots', { data: {
      code, title: 'Agent preview proof', description: 'Remora waits under the chain court arch.', durationFrames: 72,
      camera: '50mm equivalent · medium · locked', action: 'Remora waits.', referenceIds: [], constraints: ['Keep the arch centred.'],
    } })
    await page.goto('/')
    await page.getByRole('button', { name: new RegExp(`Open ${code},`) }).click()
    await page.locator('.shot-toolbar-actions').getByRole('button', { name: 'Sketch' }).click()
    const brief = page.getByRole('textbox', { name: 'Creative brief' })
    await brief.fill(`${await brief.inputValue()} Agent preview proof.`)
    await expect(page.getByText(/Saved to studio/)).toBeVisible()
    await page.getByRole('button', { name: 'Generate draft in ComfyUI' }).click()
    await expect(page.getByRole('alert')).toContainText('Submission is off')
    const snapshot = await (await page.request.get('/api/studio')).json()
    const shot = snapshot.shots.find((item: { code: string }) => item.code === code)
    const manifests = await (await page.request.get(`/api/shots/${shot.id}/manifests`)).json()
    const proof = await page.request.post(`/api/manifests/${manifests[0].id}/dispatch`, { data: { expectedManifestHash: manifests[0].manifestHash, adapterId: 'local-proof' } })
    expect(proof.ok()).toBe(true)
    await page.reload()
    await page.getByRole('button', { name: new RegExp(`Open ${code},`) }).click()
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(9)
    // The proof job lands a new head asynchronously; read context only once the
    // artist would actually see both revisions.
    await expect(page.getByRole('listbox', { name: 'Candidate versions' }).getByRole('option')).toHaveCount(2)

    type Envelope = { ok: boolean; code: string; data?: { stateToken?: string; subject?: { displayedVersion: number; liveVersion: number; archived: boolean; editable: boolean }; availableActions?: string[] } }
    const call = (name: string, input: object) => page.evaluate(({ name, input }) => {
      const tools = (window as unknown as { __framewrightTools: Map<string, { execute: (input: object, context: object) => Promise<unknown> }> }).__framewrightTools
      return tools.get(name)!.execute(input, {}) as Promise<unknown>
    }, { name, input }) as Promise<Envelope>

    const live = await call('get_director_context', {})
    expect(live.code).toBe('director_context')
    expect(live.data!.subject!.archived).toBe(false)
    expect(live.data!.availableActions).toContain('propose_shot_revision')

    const archived = page.getByRole('listbox', { name: 'Candidate versions' }).locator('.review-chip:not(.is-current)').first()
    const archivedVersion = Number((await archived.innerText()).trim().replace(/[^0-9]/g, ''))
    await archived.click()
    await expect(page.locator('.preview-banner')).toBeVisible()

    // The agent must see the revision the artist is looking at, and must not be
    // offered a proposal against an archived one. The workspace publishes the
    // previewed revision a frame after the click, so settle before reading.
    await expect.poll(async () => (await call('get_director_context', {})).data?.subject?.archived).toBe(true)
    const previewed = await call('get_director_context', {})
    expect(previewed.data!.subject!.displayedVersion).toBe(archivedVersion)
    expect(previewed.data!.subject!.liveVersion).toBe(live.data!.subject!.liveVersion)
    expect(previewed.data!.subject!.editable).toBe(false)
    expect(previewed.data!.availableActions).not.toContain('propose_shot_revision')
    expect(previewed.data!.stateToken).not.toBe(live.data!.stateToken)

    const observed = await call('observe_current_frame', { stateToken: previewed.data!.stateToken! })
    expect(observed.ok).toBe(true)

    // A token read while previewing the archive is not valid for the live head.
    await page.getByRole('listbox', { name: 'Candidate versions' }).getByRole('option').first().click()
    await expect(page.locator('.preview-banner')).toBeHidden()
    // The workspace settles a frame after the click, so poll rather than assume
    // the agent and the artist are in lockstep to the millisecond.
    await expect.poll(async () => (await call('observe_current_frame', { stateToken: previewed.data!.stateToken! })).code).toBe('stale_context')
  })

  test('a proposal is reviewed, applied exactly once, and generates nothing by itself', async ({ page }, testInfo) => {
    const code = testInfo.project.name === 'tablet' ? 'SH-PRO-T' : 'SH-PRO-D'
    const rule = 'Keep the seal in the anatomical left hand.'
    await page.request.post('/api/shots', { data: {
      code, title: 'Proposal review proof', description: 'Remora presents the seal at the gate.', durationFrames: 72,
      camera: '50mm equivalent · medium · locked', action: 'Remora presents the seal.', referenceIds: [], constraints: [rule],
    } })
    await page.goto('/')
    await page.getByRole('button', { name: new RegExp(`Open ${code},`) }).click()
    await expect(page.getByTestId('shot-workspace')).toBeVisible()
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(9)

    const note = `The seal edge is lost against the gate. ${testInfo.project.name}`
    await page.getByRole('button', { name: 'Place comment' }).click()
    await page.getByTestId('shot-canvas').focus()
    await page.getByTestId('shot-canvas').press('Enter')
    await page.getByPlaceholder(/Be specific/).fill(note)
    await page.getByRole('button', { name: 'Pin feedback' }).click()
    await expect(page.getByText('Feedback pinned to this exact version.')).toBeVisible()

    type Envelope = { ok: boolean; code: string; message: string; data?: { id?: string; stateToken?: string; subject?: { shotId: string; liveVersion: number }; annotations?: { id: string; body: string }[] } }
    const call = (name: string, input: object) => page.evaluate(({ name, input }) => {
      const tools = (window as unknown as { __framewrightTools: Map<string, { execute: (input: object, context: object) => Promise<unknown> }> }).__framewrightTools
      return tools.get(name)!.execute(input, {}) as Promise<unknown>
    }, { name, input }) as Promise<Envelope>

    const propose = async (direction: string, noteIds: string[], preserved: string[], token: string, shotId: string, version: number) =>
      call('propose_shot_revision', {
        shotId, expectedVersion: version, creativeDirection: direction, rationale: 'Aimed at the marked region.',
        desiredMediaType: 'image', authorityIds: [], noteIds, preservedConstraints: preserved,
        observedStateToken: token, idempotencyKey: `journey-${Date.now()}-${Math.random()}`,
      })

    await expect.poll(async () => (await call('get_director_context', {})).data?.annotations?.length ?? 0).toBeGreaterThan(0)
    const context = await call('get_director_context', {})
    const targeted = context.data!.annotations!.find(item => item.body === note)!
    expect(targeted).toBeTruthy()
    const shotId = context.data!.subject!.shotId
    const version = context.data!.subject!.liveVersion

    // Both projects share one database, so each proposal names its own run.
    const rejectedText = `Wash the gate in cold light for ${testInfo.project.name}.`
    const appliedText = `Lift the seal edge against the gate light for ${testInfo.project.name}.`
    const rejectedProposal = await propose(rejectedText, [], [], context.data!.stateToken!, shotId, version)
    expect(rejectedProposal.ok).toBe(true)
    const appliedProposal = await propose(appliedText, [targeted.id], [rule], context.data!.stateToken!, shotId, version)
    expect(appliedProposal.ok).toBe(true)

    // A proposal that invents a rule the shot does not hold is refused outright.
    const invented = await propose('Soften the face.', [], ['Never change the protagonist face.'], context.data!.stateToken!, shotId, version)
    expect(invented.ok).toBe(false)
    expect(invented.code).toBe('invalid_preserved_constraints')

    const before = await (await page.request.get('/api/studio')).json()
    const jobsBefore = before.jobs.length
    const versionBefore = before.shots.find((item: { code: string }) => item.code === code).version

    await page.getByTestId('agent-activity-button').click()
    const cards = page.getByTestId('agent-proposal-card')
    const applied = cards.filter({ hasText: appliedText })
    const rejected = cards.filter({ hasText: rejectedText })
    await expect(applied.getByTestId('agent-proposal-scope')).toContainText('1 targeted note')
    await expect(applied.getByTestId('agent-proposal-scope')).toContainText(rule)

    // Rejecting changes nothing at all.
    await rejected.getByRole('button', { name: 'Reject' }).click()
    await expect(rejected).toContainText('Rejected · no shot changes')
    await expect(applied).toContainText('Pending')

    await applied.getByRole('button', { name: 'Accept direction' }).click()
    await expect(applied.getByRole('button', { name: 'Apply to revision' })).toBeVisible()
    await applied.getByRole('button', { name: 'Apply to revision' }).click()

    // Applying opens the ordinary revision surface, carrying the preserved rule
    // and the marked region, and starts nothing.
    const dialog = page.getByRole('dialog', { name: 'Regenerate from feedback' })
    await expect(dialog).toBeVisible()
    const instructions = dialog.getByRole('textbox', { name: 'Feedback regeneration instructions' })
    await expect(instructions).toContainText(appliedText)
    await expect(instructions).toContainText(`PRESERVE EXACTLY: ${rule}`)
    await expect(instructions).toContainText(note)
    await dialog.getByRole('button', { name: 'Cancel' }).click()

    // The one application is recorded; reopening replays it rather than
    // applying a second time.
    await page.getByTestId('agent-activity-button').click()
    await expect(applied).toContainText('Applied · no provider was called')
    await applied.getByRole('button', { name: 'Reopen instructions' }).click()
    await expect(dialog).toBeVisible()
    await dialog.getByRole('button', { name: 'Cancel' }).click()

    const after = await (await page.request.get('/api/studio')).json()
    expect(after.jobs.length).toBe(jobsBefore)
    expect(after.shots.find((item: { code: string }) => item.code === code).version).toBe(versionBefore)
    const proposals = await (await page.request.get(`/api/webmcp/proposals?shotId=${shotId}`)).json()
    expect(proposals.data.filter((item: { state: string }) => item.state === 'Applied')).toHaveLength(1)
  })

  test('passes AbortSignal cancellation through fetch', async ({ page }) => {
    await page.goto('/')
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(9)
    await page.route('**/api/webmcp/shots?*', async route => { await new Promise(resolve => setTimeout(resolve, 1500)); await route.continue() })
    const result = await page.evaluate(async () => {
      const tool = (window as unknown as { __framewrightTools: Map<string, { execute: (input: object, context: object) => Promise<{ code: string }> }> }).__framewrightTools.get('list_storyboard_shots')!
      const controller = new AbortController()
      const pending = tool.execute({ limit: 10 }, { signal: controller.signal })
      controller.abort()
      return pending
    })
    expect(result.code).toBe('cancelled')
  })
})

test('ordinary app remains functional without WebMCP', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('button', { name: 'Board', exact: true })).toBeVisible()
  await page.getByTestId('agent-activity-button').click()
  await expect(page.getByTestId('agent-activity-panel')).toContainText('WebMCP unavailable')
})
