# Framewright WebMCP Challenge Slice

Implementation date: 2026-09-03  
Baseline commit: `eae84a0` (`fix: independently verify Last Frame criteria`)  
Branch: `challenge/webmcp-storyboard`

## What changed

Before this slice, Framewright exposed its own workstation-local server MCP endpoint at `/mcp`, but a browser agent could not discover the open storyboard, inspect the selected shot, or collaborate through the visible React application.

After this slice, a supported browser can discover eight narrowly scoped tools through the proposed browser WebMCP imperative API. An agent can inspect the active project, shots, authorities, notes, and continuity; stage a durable revision proposal; and see that proposal appear immediately in the ordinary application. The artist can edit, accept, or reject the proposal. Creating or accepting the proposal does not submit generation work. An accepted direction opens Framewright's existing revision choices, where the human deliberately chooses the next provider-agnostic step.

Unsupported browsers keep the complete ordinary application and show an honest unavailable state in **Agent activity**.

## Tool surface

| Tool | Purpose | `readOnlyHint` | `untrustedContentHint` | `consequentialHint` | Consequence model |
|---|---|---:|---:|---:|---|
| `get_storyboard_context` | Active project and selected-shot summary | true | true | false | Read only |
| `list_storyboard_shots` | Paginated active-project shot summaries, maximum 20 | true | true | false | Read only |
| `get_shot_details` | Bounded shot, authority, note, revision, and media context | true | true | false | Read only; selects the shot visibly |
| `inspect_shot_continuity` | Existing continuity evaluation | true | true | false | Read only; opens Review visibly |
| `get_director_context` | The exact revision on screen, its open notes, constraints, authorities, available actions, and a state token | true | true | false | Read only |
| `observe_current_frame` | The picture for a previously read context, refused once that view changes | true | true | false | Read only |
| `propose_shot_revision` | Durable, reversible pending direction, bound to a director context the agent read, naming its targeted notes and preserved constraints | false | true | false | Adds a review proposal only; no canonical edit or provider call |
| `get_generation_status` | Bounded durable-ledger job status | true | true | false | Read only |

All schemas have object roots, explicit bounds, required fields, enums where appropriate, and `additionalProperties: false`. Tool metadata is static source code. Storyboard content is always treated as untrusted data.

## Director context and its picture

`get_director_context` returns one bounded packet describing the revision the
artist actually has on screen - the archived candidate whenever one is being
previewed, not the live head - together with a `stateToken`.

The token is a SHA-256 digest of persisted state only: project, shot, live and
displayed version, the shot's update time, the frame asset and its content hash,
the markup revision, camera, constraints, authorities, and every open note with
its normalized position and text. A browser cannot talk a stale view into
looking current, because it does not contribute anything the token is made of.
The active tool and the full-view flag are reported but deliberately excluded:
they change what the artist is doing, not which revision is on screen.

`observe_current_frame` resolves the picture for a token, and only while that
token still matches. Once the artist pins a note, edits the shot, or moves to a
different revision, the pairing is refused with `stale_context` and the agent is
told to read the context again. The refusal carries no replacement token, so it
cannot be used to skip the re-read it is asking for. Structured context and
visual context therefore can never describe two different revisions.

Proposals remain unavailable while an archived revision is previewed:
`availableActions` omits `propose_shot_revision`, matching the read-only lock the
artist sees in the workspace.

## Proposal scope, and what applying one does

A proposal cannot be made blind. `propose_shot_revision` requires the state
token from a director context the agent has read, so the sequence is always
inspect, then propose; a token from a view that has since moved is refused with
`stale_context` and nothing is written.

A proposal states its own scope. `noteIds` are the marked regions it is aimed
at, and `preservedConstraints` are the rules it promises not to touch. The
service validates that every preserved rule is one the shot or one of its
attached authorities actually holds, so an agent cannot reassure the artist with
an invented constraint that nothing enforces. Both appear on the proposal card
before the artist decides.

The states are distinct and ordered: Pending, then Accepted or Rejected, then
Applied. Rejecting changes nothing. Applying is the artist's action and the only
step that turns a direction into working instructions: the service refuses a
pending or rejected proposal, refuses one whose shot has moved on, records the
single moment it was applied, and returns frozen instructions - direction,
rationale, preserved constraints, and the targeted notes with their normalized
coordinates - which open in the ordinary revision surface. Applying again
replays that same application rather than doubling it.

Applying still authorizes no provider. The instructions carry
`generationAuthorized: false`, and the existing explicit Generate action in the
revision surface remains the only thing that spends GPU time or money.

## Security and threat model

- Browser tools call only same-origin `/api/webmcp/*` routes; no arbitrary URLs, paths, SQL, prompt execution, deletion, reset, or provider selection is exposed.
- Existing loopback/pairing middleware remains authoritative. Project query filters and server-side binding validation prevent cross-project reads or references.
- The existing `/mcp` endpoint is unchanged and remains a distinct workstation-local interface.
- `Permissions-Policy` now adds `tools=(self)` while retaining `camera=()`, `microphone=()`, and `geolocation=()`.
- Provider credentials remain server-side. No browser tool returns them.
- Proposal input is revalidated by ASP.NET. Unknown properties are rejected. Creative text, rationales, authority IDs, and note IDs are bounded.
- Proposals capture the shot version and update timestamp. Editing or deciding a stale proposal returns `stale_shot` rather than overwriting current work.
- Project-scoped idempotency keys prevent duplicate proposal creation. Accept/reject decisions are replay-safe and immutable after the first decision.
- Both proposal creation and acceptance leave the generation ledger unchanged. Spending remains in the existing human-visible revision workflow.
- `AbortSignal` is passed into same-origin requests. A registration `AbortController` owns all six tools and is aborted on React teardown; registration failures degrade to ordinary UI.

Annotations are advisory discovery metadata, never authorization.

## 90-second demo

1. Open Framewright in a WebMCP-capable Chrome build and select a shot (10 seconds).
2. Open **Agent** to show six registered tools and no hidden provider action (10 seconds).
3. Ask the browser agent to list shots and inspect the selected shot and continuity (15 seconds). The app selects that shot and opens Review.
4. Say: “Propose an image revision that keeps the current composition, places the lantern beside Mara, and preserves every pinned authority” (15 seconds).
5. The pending before/after card appears in **Agent activity**. Edit one phrase, save it, and point out that the shot is still unchanged (15 seconds).
6. Accept the direction. Show the explicit message that no provider was called, then click **Open revision options** (15 seconds).
7. If a prerecorded safe job exists, ask for its generation status and show the durable-ledger progress in Agent activity. Otherwise state that generation is intentionally outside this GPU-free viability run (10 seconds).

## Verification evidence

The focused tests cover browser fallback, exact static tool names, closed schemas, one registration lifecycle without duplicate names, cancellation propagation, visible tool/UI synchronization, bounded and unknown input handling, injection-shaped text as inert data, project/shot isolation, proposal persistence, edit/accept/replay behavior, stale revisions, no provider dispatch, generation-ledger status, compact output, and the security header.

Final commands and results:

- `npm run check` — passed TypeScript, ESLint with zero warnings, and Prettier checks.
- `dotnet test tests/StoryboardStudio.Api.Tests/StoryboardStudio.Api.Tests.csproj --no-restore --filter FullyQualifiedName~WebMcpApiTests` — 5 passed, 0 failed, 0 skipped.
- `dotnet test Framewright.slnx --configuration Release --no-restore` — 142 passed, 0 failed, 0 skipped.
- `npm run test:e2e` — 83 passed, 0 failed, 7 intentionally skipped tablet-only exclusions; 90 discovered across desktop and tablet projects.
- The repository verifier also passed `npm ci`, npm and NuGet vulnerability inspection (0 known vulnerabilities), production frontend build, Release build with zero warnings, backup/rollback, Docker fallback/environment, release-candidate safety, voice PID ownership, and 5 local voice bootstrap contract tests.

The first full browser run exposed a SQLite limitation when sorting `DateTimeOffset` values in the new proposal list. The query was corrected to sort the already bounded, project-scoped result in memory; the full 90-test browser matrix then passed. No GPU provider, paid API, or creative generation was invoked.

## Public deployment blockers

This slice is production-shaped for a trusted local workstation; it is not yet a public multi-tenant service.

- Hosted authentication and tenant-scoped authorization must replace process-wide active-project selection.
- Durable hosted object storage, backup, retention, and deletion policies are required for media.
- Provider adapters need hosted secret management, quotas, cost controls, abuse controls, cancellation, and observability.
- WebMCP browser availability and competition runtime behavior must be validated in the exact target Chrome build.
- HTTPS, origin policy, CSP, rate limiting, audit review, privacy terms, and operational monitoring require deployment-specific configuration.
- A public demo should use pre-generated media or explicitly user-triggered, budget-capped generation; it must never imply that a staged proposal generated media.

Repository visibility and licensing are intentionally unchanged. Both remain explicit user decisions before challenge submission.

## Source baseline

Implementation follows the current browser WebMCP proposal and guidance reviewed on 2026-09-03:

- [WebMCP Community Group draft](https://webmachinelearning.github.io/webmcp/)
- [ChatGPT WebMCP documentation](https://learn.chatgpt.com/docs/webmcp)
- [Chrome imperative API](https://developer.chrome.com/docs/ai/webmcp/imperative-api)
- [Chrome secure tools guidance](https://developer.chrome.com/docs/ai/webmcp/secure-tools)
- [Chrome WebMCP best practices](https://developer.chrome.com/docs/ai/webmcp/best-practices)
