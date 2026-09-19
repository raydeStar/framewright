import { studioApi } from './api'
import type { ShotRevisionProposalSummary, WebMcpEnvelope } from './types'

type JsonSchema = Record<string, unknown>
type ToolExecution = (input: Record<string, unknown>, context: { signal?: AbortSignal }) => Promise<WebMcpEnvelope>
type BrowserTool = { name: string; title: string; description: string; inputSchema: JsonSchema; annotations: Record<string, boolean>; execute: ToolExecution }
type ModelContext = { registerTool: (tool: BrowserTool, options: { signal: AbortSignal }) => void }

declare global { interface Document { modelContext?: ModelContext } }

const emptySchema = { type: 'object', properties: {}, required: [], additionalProperties: false } as const
const shotIdSchema = {
  type: 'object', properties: { shotId: { type: 'string', format: 'uuid', maxLength: 36 } }, required: ['shotId'], additionalProperties: false,
} as const

export const FRAMEWRIGHT_WEBMCP_TOOL_NAMES = [
  'get_storyboard_context', 'list_storyboard_shots', 'get_shot_details',
  'inspect_shot_continuity', 'propose_shot_revision', 'get_generation_status',
] as const

export interface WebMcpBridgeOptions {
  getSelectedShotId: () => string | undefined
  getSelectedShotVersion: () => number | undefined
  onAvailability: (available: boolean, detail: string) => void
  onActivity: (tool: string, state: 'Running' | 'Succeeded' | 'Failed' | 'Cancelled', message: string) => void
  onInspectShot: (shotId: string, continuity: boolean) => void
  onProposal: (proposal?: ShotRevisionProposalSummary) => void
  onJobStatus: (jobId: string) => void
}

let activeController: AbortController | undefined

export function registerFramewrightWebMcp(options: WebMcpBridgeOptions): () => void {
  activeController?.abort()
  const controller = new AbortController()
  activeController = controller
  const modelContext = document.modelContext
  if (typeof modelContext?.registerTool !== 'function') {
    options.onAvailability(false, 'This browser does not expose WebMCP. Framewright remains fully usable.')
    return () => controller.abort()
  }

  const read = { readOnlyHint: true, untrustedContentHint: true, consequentialHint: false }
  const run = (name: string, work: (input: Record<string, unknown>, signal?: AbortSignal) => Promise<WebMcpEnvelope>): ToolExecution => async (input, context) => {
    options.onActivity(name, 'Running', 'Agent request received.')
    try {
      const result = await work(input, context.signal)
      options.onActivity(name, result.ok ? 'Succeeded' : 'Failed', result.message)
      return result
    } catch (error) {
      const cancelled = error instanceof DOMException && error.name === 'AbortError'
      const message = cancelled ? 'Request cancelled.' : 'The browser request could not be completed.'
      options.onActivity(name, cancelled ? 'Cancelled' : 'Failed', message)
      return { ok: false, status: cancelled ? 'cancelled' : 'error', code: cancelled ? 'cancelled' : 'browser_request_failed', message, retryable: !cancelled }
    }
  }

  const tools: BrowserTool[] = [
    {
      name: 'get_storyboard_context', title: 'Get storyboard context',
      description: 'Read the active Framewright project and currently selected shot summary.', inputSchema: emptySchema, annotations: read,
      execute: run('get_storyboard_context', (_input, signal) => studioApi.webMcpContext(options.getSelectedShotId(), signal)),
    },
    {
      name: 'list_storyboard_shots', title: 'List storyboard shots',
      description: 'List a bounded page of shots in the active Framewright project.',
      inputSchema: { type: 'object', properties: { offset: { type: 'integer', minimum: 0, maximum: 10000 }, limit: { type: 'integer', minimum: 1, maximum: 20 } }, required: [], additionalProperties: false }, annotations: read,
      execute: run('list_storyboard_shots', (input, signal) => studioApi.webMcpShots(Number(input.offset ?? 0), Number(input.limit ?? 10), signal)),
    },
    {
      name: 'get_shot_details', title: 'Get shot details',
      description: 'Read one active-project shot, its authorities, notes, revision, and media state.', inputSchema: shotIdSchema, annotations: read,
      execute: run('get_shot_details', async (input, signal) => { const id = String(input.shotId); const result = await studioApi.webMcpShot(id, signal); if (result.ok) options.onInspectShot(id, false); return result }),
    },
    {
      name: 'inspect_shot_continuity', title: 'Inspect shot continuity',
      description: 'Evaluate continuity for one active-project shot and open its review surface.', inputSchema: shotIdSchema, annotations: read,
      execute: run('inspect_shot_continuity', async (input, signal) => { const id = String(input.shotId); const result = await studioApi.webMcpContinuity(id, signal); if (result.ok) options.onInspectShot(id, true); return result }),
    },
    {
      name: 'propose_shot_revision', title: 'Propose shot revision',
      description: 'Stage reversible creative direction for human review without changing the shot or dispatching generation.',
      inputSchema: {
        type: 'object', properties: {
          shotId: { type: 'string', format: 'uuid', maxLength: 36 }, expectedVersion: { type: 'integer', minimum: 1, maximum: 1000000 },
          creativeDirection: { type: 'string', minLength: 1, maxLength: 1000 }, rationale: { type: 'string', maxLength: 600 },
          desiredMediaType: { type: 'string', enum: ['image', 'video'] }, authorityIds: { type: 'array', maxItems: 12, items: { type: 'string', minLength: 1, maxLength: 100 } },
          noteIds: { type: 'array', maxItems: 20, items: { type: 'string', format: 'uuid', maxLength: 36 } }, idempotencyKey: { type: 'string', minLength: 1, maxLength: 128 },
        }, required: ['shotId', 'expectedVersion', 'creativeDirection', 'rationale', 'desiredMediaType', 'authorityIds', 'noteIds', 'idempotencyKey'], additionalProperties: false,
      }, annotations: { readOnlyHint: false, untrustedContentHint: true, consequentialHint: false },
      execute: run('propose_shot_revision', async (input, signal) => {
        const result = await studioApi.webMcpPropose(input as Parameters<typeof studioApi.webMcpPropose>[0], signal)
        if (result.ok) options.onProposal(result.data)
        return result
      }),
    },
    {
      name: 'get_generation_status', title: 'Get generation status',
      description: 'Read bounded status for a server-minted Framewright generation job.',
      inputSchema: { type: 'object', properties: { jobId: { type: 'string', format: 'uuid', maxLength: 36 } }, required: ['jobId'], additionalProperties: false }, annotations: read,
      execute: run('get_generation_status', async (input, signal) => { const id = String(input.jobId); const result = await studioApi.webMcpJob(id, signal); if (result.ok) options.onJobStatus(id); return result }),
    },
  ]

  try {
    for (const tool of tools) modelContext.registerTool(tool, { signal: controller.signal })
    options.onAvailability(true, `${tools.length} tools registered for this tab.`)
  } catch (error) {
    controller.abort()
    const name = error instanceof DOMException ? error.name : 'RegistrationError'
    options.onAvailability(false, `WebMCP registration was refused (${name}). The ordinary app is unaffected.`)
  }

  return () => { if (activeController === controller) activeController = undefined; controller.abort() }
}
