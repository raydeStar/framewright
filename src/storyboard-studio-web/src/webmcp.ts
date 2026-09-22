import { studioApi } from './api'
import type { DirectorViewQuery, ShotRevisionProposalSummary, WebMcpEnvelope } from './types'

type JsonSchema = Record<string, unknown>
type ToolExecution = (input: Record<string, unknown>, context: { signal?: AbortSignal }) => Promise<WebMcpEnvelope>
type BrowserTool = { name: string; title: string; description: string; inputSchema: JsonSchema; annotations: Record<string, boolean>; execute: ToolExecution }
type ModelContext = { registerTool: (tool: BrowserTool, options: { signal: AbortSignal }) => void }

declare global { interface Document { modelContext?: ModelContext } }

const emptySchema = { type: 'object', properties: {}, required: [], additionalProperties: false } as const
const noShotOpen: WebMcpEnvelope = {
  ok: false, status: 'error', code: 'no_shot_open',
  message: 'No shot is open in the workspace. Ask the artist to open one, then read the context again.', retryable: true,
}
const noSceneOpen: WebMcpEnvelope = {
  ok: false, status: 'error', code: 'no_scene_open',
  message: 'No scene is open in the workspace. Ask the artist to open one, then read the context again.', retryable: true,
}
const noReferenceSelected: WebMcpEnvelope = {
  ok: false, status: 'error', code: 'no_reference_selected',
  message: 'No reference is selected in the scene workspace. Ask the artist to choose one, then read the context again.', retryable: true,
}
const unsavedScene: WebMcpEnvelope = {
  ok: false, status: 'conflict', code: 'unsaved_scene',
  message: 'This scene has unsaved camera or object changes. Save or reopen it before asking an agent to read or propose against it.', retryable: true,
}
const wrongSurface: WebMcpEnvelope = {
  ok: false, status: 'error', code: 'wrong_surface',
  message: 'That tool applies to a different workspace than the one the artist has open. Read the director context again.', retryable: true,
}
const shotIdSchema = {
  type: 'object', properties: { shotId: { type: 'string', format: 'uuid', maxLength: 36 } }, required: ['shotId'], additionalProperties: false,
} as const

export const FRAMEWRIGHT_WEBMCP_TOOL_NAMES = [
  'get_storyboard_context', 'list_storyboard_shots', 'get_shot_details',
  'inspect_shot_continuity', 'get_director_context', 'search_scene_assets', 'observe_current_frame',
  'propose_shot_revision', 'propose_scene_edit', 'propose_scene_blockout', 'get_generation_status',
] as const

export interface WebMcpBridgeOptions {
  getSelectedShotId: () => string | undefined
  getSelectedShotVersion: () => number | undefined
  /** What the artist actually has on screen, including an archived preview. */
  getDirectorView: () => DirectorViewQuery | undefined
  onAvailability: (available: boolean, detail: string) => void
  onActivity: (tool: string, state: 'Running' | 'Succeeded' | 'Failed' | 'Cancelled', message: string) => void
  onInspectShot: (shotId: string, continuity: boolean) => void
  onProposal: (proposal?: ShotRevisionProposalSummary) => void
  /** A scene proposal landed; the open scene should show it. */
  onSceneProposal: () => void
  /** A blockout plan landed; the open scene workspace should show it for review. */
  onSceneBlockout: () => void
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
      name: 'get_director_context', title: 'Get director context',
      description: 'Read the exact saved shot or scene state on screen and a token that binds later proposals to it. Unsaved scene drafts are refused rather than described as older state.',
      inputSchema: emptySchema, annotations: read,
      execute: run('get_director_context', (_input, signal) => {
        const view = options.getDirectorView()
        if (!view) return Promise.resolve(noShotOpen)
        // One tool, two surfaces: the packet describes whichever the artist has
        // open, so an agent never has to guess which workspace it is looking at.
        if (view.kind === 'scene' && view.dirty) return Promise.resolve(unsavedScene)
        return view.kind === 'scene'
          ? studioApi.webMcpSceneContext(view.sceneId, view.instanceId, view.referenceAssetId, view.directorMode ?? false, view.time, signal)
          : studioApi.webMcpDirectorContext(view, signal)
      }),
    },
    {
      name: 'search_scene_assets', title: 'Search reusable scene assets',
      description: 'Search current, non-archived model revisions in the active project before proposing generation. Returned asset ids may be used as matchAssetId in a scene blockout proposal. This places and generates nothing.',
      inputSchema: {
        type: 'object', properties: {
          search: { type: 'string', maxLength: 120 },
          offset: { type: 'integer', minimum: 0, maximum: 10000 },
          limit: { type: 'integer', minimum: 1, maximum: 20 },
        }, required: [], additionalProperties: false,
      }, annotations: read,
      execute: run('search_scene_assets', (input, signal) => studioApi.webMcpSceneAssets(
        String(input.search ?? ''), Number(input.offset ?? 0), Number(input.limit ?? 10), signal)),
    },
    {
      name: 'observe_current_frame', title: 'Observe the current frame',
      description: 'Resolve the picture for a previously read director context. A view that changed since then is refused so structured and visual context can never disagree.',
      inputSchema: {
        type: 'object', properties: { stateToken: { type: 'string', minLength: 64, maxLength: 64 } }, required: ['stateToken'], additionalProperties: false,
      }, annotations: read,
      execute: run('observe_current_frame', (input, signal) => {
        const view = options.getDirectorView()
        if (!view) return Promise.resolve(noShotOpen)
        if (view.kind !== 'shot') return Promise.resolve(wrongSurface)
        return studioApi.webMcpDirectorObservation(view, String(input.stateToken), signal)
      }),
    },
    {
      name: 'propose_shot_revision', title: 'Propose shot revision',
      description: 'Stage reversible creative direction for human review, bound to a director context you have read, naming the notes it targets and the constraints it preserves. It changes no shot and dispatches nothing.',
      inputSchema: {
        type: 'object', properties: {
          shotId: { type: 'string', format: 'uuid', maxLength: 36 }, expectedVersion: { type: 'integer', minimum: 1, maximum: 1000000 },
          creativeDirection: { type: 'string', minLength: 1, maxLength: 1000 }, rationale: { type: 'string', maxLength: 600 },
          desiredMediaType: { type: 'string', enum: ['image', 'video'] }, authorityIds: { type: 'array', maxItems: 12, items: { type: 'string', minLength: 1, maxLength: 100 } },
          noteIds: { type: 'array', maxItems: 20, items: { type: 'string', format: 'uuid', maxLength: 36 } },
          preservedConstraints: { type: 'array', maxItems: 20, items: { type: 'string', minLength: 1, maxLength: 400 } },
          observedStateToken: { type: 'string', minLength: 64, maxLength: 64 },
          idempotencyKey: { type: 'string', minLength: 1, maxLength: 128 },
        }, required: ['shotId', 'expectedVersion', 'creativeDirection', 'rationale', 'desiredMediaType', 'authorityIds', 'noteIds', 'preservedConstraints', 'observedStateToken', 'idempotencyKey'], additionalProperties: false,
      }, annotations: { readOnlyHint: false, untrustedContentHint: true, consequentialHint: false },
      execute: run('propose_shot_revision', async (input, signal) => {
        const result = await studioApi.webMcpPropose(input as Parameters<typeof studioApi.webMcpPropose>[0], signal)
        if (result.ok) options.onProposal(result.data)
        return result
      }),
    },
    {
      name: 'propose_scene_edit', title: 'Propose a scene edit',
      description: 'Stage a reversible change to one named scene object, bound to a scene director context you have read. It moves nothing until the artist applies it and saves.',
      inputSchema: {
        type: 'object', properties: {
          instanceId: { type: 'string', format: 'uuid', maxLength: 36 },
          expectedSceneVersion: { type: 'integer', minimum: 1, maximum: 1000000 },
          observedStateToken: { type: 'string', minLength: 64, maxLength: 64 },
          direction: { type: 'string', minLength: 1, maxLength: 1000 },
          rationale: { type: 'string', maxLength: 600 },
          position: { type: 'array', minItems: 3, maxItems: 3, items: { type: 'number' } },
          rotation: { type: 'array', minItems: 3, maxItems: 3, items: { type: 'number' } },
          scale: { type: 'array', minItems: 3, maxItems: 3, items: { type: 'number' } },
          idempotencyKey: { type: 'string', minLength: 1, maxLength: 128 },
        },
        required: ['instanceId', 'expectedSceneVersion', 'observedStateToken', 'direction', 'rationale', 'idempotencyKey'],
        additionalProperties: false,
      },
      annotations: { readOnlyHint: false, untrustedContentHint: true, consequentialHint: false },
      execute: run('propose_scene_edit', async (input, signal) => {
        const view = options.getDirectorView()
        if (!view) return noSceneOpen
        if (view.kind !== 'scene') return wrongSurface
        if (view.dirty) return unsavedScene
        const result = await studioApi.webMcpProposeSceneEdit({ ...input, sceneId: view.sceneId, observedSceneTime: view.time }, signal)
        if (result.ok) options.onSceneProposal()
        return result
      }),
    },
    {
      name: 'propose_scene_blockout', title: 'Propose a scene blockout',
      description: 'Read the reference the artist has selected and stage a construction plan for it: what objects the scene needs, which of them the library already has, what stands in for the rest, and what the reference does not show. It builds nothing until the artist approves it.',
      inputSchema: {
        type: 'object', properties: {
          observedReferenceHash: { type: 'string', minLength: 1, maxLength: 128 },
          title: { type: 'string', minLength: 1, maxLength: 120 },
          summary: { type: 'string', minLength: 1, maxLength: 1000 },
          camera: {
            type: 'object', properties: {
              yaw: { type: 'number' }, pitch: { type: 'number' }, distance: { type: 'number' },
              target: { type: 'array', minItems: 3, maxItems: 3, items: { type: 'number' } },
              fieldOfView: { type: 'number' },
            },
            required: ['yaw', 'pitch', 'distance', 'target', 'fieldOfView'], additionalProperties: false,
          },
          assumptions: { type: 'array', maxItems: 10, items: { type: 'string', maxLength: 300 } },
          uncertainties: { type: 'array', maxItems: 10, items: { type: 'string', maxLength: 300 } },
          items: {
            type: 'array', minItems: 1, maxItems: 12,
            items: {
              type: 'object', properties: {
                role: { type: 'string', minLength: 1, maxLength: 80 },
                matchAssetId: { type: 'string', format: 'uuid', maxLength: 36 },
                shape: { type: 'string', enum: ['Box', 'Cylinder', 'Sphere', 'Plane'] },
                size: { type: 'array', minItems: 3, maxItems: 3, items: { type: 'number' } },
                position: { type: 'array', minItems: 3, maxItems: 3, items: { type: 'number' } },
                rotation: { type: 'array', minItems: 3, maxItems: 3, items: { type: 'number' } },
                scale: { type: 'array', minItems: 3, maxItems: 3, items: { type: 'number' } },
                motionIntent: { type: 'string', maxLength: 200 },
                confidence: { type: 'string', enum: ['Certain', 'Approximate', 'Occluded'] },
                note: { type: 'string', maxLength: 300 },
              },
              required: ['role'], additionalProperties: false,
            },
          },
          idempotencyKey: { type: 'string', minLength: 1, maxLength: 128 },
        },
        required: ['observedReferenceHash', 'title', 'summary', 'items', 'idempotencyKey'],
        additionalProperties: false,
      },
      annotations: { readOnlyHint: false, untrustedContentHint: true, consequentialHint: false },
      execute: run('propose_scene_blockout', async (input, signal) => {
        const view = options.getDirectorView()
        if (!view) return noSceneOpen
        if (view.kind !== 'scene') return wrongSurface
        // The plan is always about the reference the artist has selected, never
        // one the agent chose for itself.
        if (!view.referenceAssetId) return noReferenceSelected
        const result = await studioApi.webMcpProposeSceneBlockout({ ...input, referenceAssetId: view.referenceAssetId }, signal)
        if (result.ok) options.onSceneBlockout()
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
