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

  test('registers exactly six closed-schema tools without duplicates', async ({ page }) => {
    await page.goto('/')
    await expect(page.getByTestId('agent-activity-button')).toBeVisible()
    const registration = await page.evaluate(() => {
      const tools = [...(window as unknown as { __framewrightTools: Map<string, { inputSchema: { additionalProperties: boolean } }> }).__framewrightTools]
      const signals = (window as unknown as { __framewrightRegistrationSignals: AbortSignal[] }).__framewrightRegistrationSignals
      return { names: tools.map(([name]) => name), closed: tools.every(([, tool]) => tool.inputSchema.additionalProperties === false), oneLifecycle: new Set(signals).size === 1, signalActive: signals.every(signal => !signal.aborted) }
    })
    expect(registration.names).toEqual(['get_storyboard_context', 'list_storyboard_shots', 'get_shot_details', 'inspect_shot_continuity', 'propose_shot_revision', 'get_generation_status'])
    expect(registration.closed).toBe(true)
    expect(registration.oneLifecycle).toBe(true)
    expect(registration.signalActive).toBe(true)
    await page.getByTestId('agent-activity-button').click()
    await page.getByTestId('agent-tools-toggle').click()
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(0)
    await expect(page.getByTestId('agent-activity-panel')).toContainText('WebMCP paused')
    await page.getByTestId('agent-tools-toggle').click()
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(6)
  })

  test('tool calls select the shot and synchronize a durable visible proposal', async ({ page }) => {
    await page.goto('/')
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(6)
    const context = await page.evaluate(async () => {
      const tools = (window as unknown as { __framewrightTools: Map<string, { execute: (input: object, context: object) => Promise<{ data?: { selectedShot?: { id: string; version: number } } }> }> }).__framewrightTools
      return tools.get('get_storyboard_context')!.execute({}, {})
    })
    const selected = context.data?.selectedShot
    expect(selected).toBeTruthy()
    await page.evaluate(async ({ shotId, expectedVersion }) => {
      const tools = (window as unknown as { __framewrightTools: Map<string, { execute: (input: object, context: object) => Promise<unknown> }> }).__framewrightTools
      await tools.get('get_shot_details')!.execute({ shotId }, {})
      await tools.get('propose_shot_revision')!.execute({
        shotId, expectedVersion, creativeDirection: 'Keep the blocking; lower the lantern to the floor beside Mara.',
        rationale: 'Human review should precede every provider call.', desiredMediaType: 'image', authorityIds: [], noteIds: [],
        idempotencyKey: `browser-${crypto.randomUUID()}`,
      }, {})
    }, { shotId: selected!.id, expectedVersion: selected!.version })
    await expect(page.getByTestId('agent-activity-panel')).toBeVisible()
    await expect(page.getByTestId('agent-proposal-card').first()).toContainText('lower the lantern')
    await expect(page.getByTestId('agent-proposal-card').first()).toContainText('Pending')
  })

  test('passes AbortSignal cancellation through fetch', async ({ page }) => {
    await page.goto('/')
    await expect.poll(() => page.evaluate(() => (window as unknown as { __framewrightTools: Map<string, unknown> }).__framewrightTools.size)).toBe(6)
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
