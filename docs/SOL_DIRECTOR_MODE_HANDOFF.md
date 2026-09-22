# Sol handoff: finish the Director Mode workflow

Prepared 2026-09-21 and refreshed through the reusable-asset discovery increment on 2026-09-22. Public `main` at `774cf6691a1323a2d89b694aa01b8e7aecd6860d` is hosted-green, including the M17 exact-frame render and portable recovery contracts. The current checkout adds the bounded library search described below. This is a continuation brief, not an instruction to start generation or a claim of release readiness.

> **Repository destination update — 2026-09-21:** `github.com/raydeStar/framewright` is now the maintained Framewright repository and the former private mirror is deprecated. Publish authorized Framewright work to public `main`. Geometry, retopology, UV, material, texture, rigging, deformation-gate, payload, and compiler-receipt changes belong in `github.com/raydeStar/reference-asset-compiler`; Framewright consumes that versioned contract and owns orchestration, import, scene use, and review.

The artist's desired outcome is simple:

> "My hope is to eventually just pop into director mode and have you drive most things."

Sol's job is to complete the connected workflow behind that experience. The artist supplies intent, points at things, evaluates results, and makes consequential creative decisions. The assistant handles context gathering, routine analysis, finding reusable assets, preparing coherent changes, and explaining progress or failures. The artist should not have to become the integration engineer. The machinery may wear a waistcoat, but it must still keep receipts.

## Start here

Read [AGENTS.md](../AGENTS.md), [CONTRIBUTING.md](../CONTRIBUTING.md), and the current execution state, contracts, dependencies, and acceptance scenario in [the canonical Director Mode goal](goals/director-mode-3d-scenes.md). That goal is the sole milestone/status ledger. Update it as work proceeds; do not maintain another status matrix here.

Inspect the actual checkout and any research supplied with the continuation. The older [implementation handoff](IMPLEMENTATION_HANDOFF.md) and [frontend QA handoff](FRONTEND_QA_HANDOFF.md) contain useful feature explanations and historical evidence, but their dates, counts, commissioning claims, and gaps must be checked against current source. They are not proof that the remaining 3D goal is complete.

This brief is now active. Continue across proven, dependency-ready increments without asking permission for ordinary engineering. Preserve the existing product and the user's work.

## What “drive most things” should feel like

A representative interaction is: enter Director Mode, select or mark part of the displayed reference, and say, "Use our existing assets to block out this workshop. Keep this character and the warm lighting; move the lantern closer, then help me make a short shot."

The completed path should let the assistant:

1. Identify the exact visible project, image revision or scene instance, selection, annotations, references, locks, camera, and relevant time. Observe matching visual content and refresh context when it changes.
2. Inspect available library assets and supported operations. Explain uncertainty in the reference and reuse suitable assets before suggesting generation. Do not silently substitute a different character, source, provider, or rig.
3. Present a bounded, coherent proposal with understandable changes and preserved constraints. The artist reviews/applies or revises it in the same workspace. Avoid one confirmation per field.
4. Inspect the applied result and prepare the next useful action. For work needing a provider, make the concrete inputs, consequences, readiness, and existing Generate/Render control easy to reach. Browser proposals must not dispatch as a side effect.
5. Follow owned work and report real stage progress, results, or a useful failure. Bring the result back to normal inspection/review, preserving editable source data and lineage.
6. Repeat naturally; save, reopen, and disconnect the assistant without losing work or making the project unusable manually.

Director Mode now supports both the existing **Shot/image** workspace and the same persistent **Scene** workspace in full view. Scene selection and unsaved working transforms survive entering and leaving it. Continue to reuse these workspaces and their persistence; do not build a second chat application.

The artist's ambition does not remove the current explicit Generate/Render or ratification boundaries. Sol may implement and test authorized code autonomously; product-runtime tools still follow inspect → propose → artist reviews/applies → inspect result. Keep proposed, applied, ratified, dispatched, and completed states distinct.

## What the checkpoint actually proves

The historical pre-public QC reviewed 46 commits and repaired data-integrity and UI lifecycle defects. Its detailed receipt is in the goal's **WIP quality-control record - 2026-09-21**. The maintained remote is now public `raydeStar/framewright`; the private push named in that older receipt is provenance, not the current destination.

- Application fixes at `3abed12`: database-enforced scene concurrency; edits retained during save; atomic model import/revision/lineage/delivery; no current-selection reset when an older source finishes preparation; malformed GLB rejection; glass-stage preflight; readiness retry; current-state async scene callbacks; independent selection materials and resource disposal; configuration precedence and analyzer fixes.
- Test launcher at `7ac0216`: `STUDIO_E2E_PORT` override and disposable test binaries, so existing apps can keep their port and Debug assemblies.
- Local evidence: **277 backend tests passed**, **130 browser tests passed / six existing tablet exclusions**, frontend checks/build and supporting script tests passed. No dependency vulnerabilities were reported at that checkpoint. Three new data-integrity regressions were shown to fail before their repairs.
- Browser coverage used desktop Chromium and emulated iPad Pro 11 WebKit. Coding-agent screenshot inspection is not human artistic acceptance or a physical stylus check.
- The full verification script passed its pre-browser checks, then exposed occupied-port/locked-binary startup problems. After the launcher repair, the complete browser suite passed separately. Do not describe that as one final successful `verify.ps1` invocation.
- No new live generation or packaged release acceptance occurred during QC. Actual-host commissioning happened afterward and is recorded in the canonical goal. Earlier milestone records contain separate live worker evidence. The Three.js bundle still has a Vite size warning.

The implemented MVP increments add precise placeholder replacement, full-view Scene Director Mode, and a real browser-rendered scene still that freezes saved scene state, a distinct shot camera, timing, delivery settings, and a content hash into the ordinary candidate-review path. A follow-on M17 contract starts only from that ratified still, draws numbered Three.js PNGs at exact scene times with inspection-to-shot camera motion, resumes missing frames after restart, binds the local FFmpeg version, probes the encoded MP4, and promotes it into existing Max-take review without recording the viewport. Working packages round-trip the complete editable project graph into a separate project with fresh identities and verified content. A completed controlled M17 take now survives export, fresh-ID import, application restart, playback, and re-export while immutable source manifests and snapshot hashes remain exact. Framewright also consumes skeleton profiles owned by the compiler checkout rather than carrying a product fallback. The new read-only asset search lets Director Mode find exact reusable model revisions before suggesting generation. The complete local gate passes 289 backend tests and 136 browser journeys plus six intentional tablet exclusions, including all 22 scene journeys on desktop Chromium and emulated iPad Pro 11 WebKit. Frontend checks/build, the release audit, locked restore, warning-free Release build, supporting script tests, and the NuGet vulnerability scan also pass. No provider or production queue was contacted. The M17 live M14-rig render, representative-frame/human verdict, and full packaged-runtime proof remain open.

Ignored evidence, when still present locally: `artifacts/qc/regressions-before.log`, `regressions-after.log`, `verify-final-5182.log`, `browser-final.log`, `{model,scene}-{desktop,tablet}.png`, and `src/storyboard-studio-web/playwright-report/index.html`. Do not expect these files in a fresh clone or commit private media to make them portable.

Public `main` at `774cf6691a1323a2d89b694aa01b8e7aecd6860d` passed [hosted run 35700469040](https://github.com/raydeStar/framewright/actions/runs/35700469040): build/API, container runtime, and browser acceptance were all green. That run includes the M17 scene-render baseline and portable recovery extension. The reusable-asset discovery increment is newer and requires its own exact-head hosted result after push.

## Where to resume

Use the canonical goal's dependency table and acceptance records to choose each increment. The following is reading and execution guidance, not a replacement roadmap.

**The primary host path is commissioned.** The original running-app commissioning discovered the historical ten page-defined WebMCP tools. After the reusable-asset increment, Codex's in-app browser discovered the exact new eleven-tool registry against an isolated current build. M02 and M03 are `VERIFIED`; M10 is `VERIFIED` after a live scene proposal/apply/save/reopen round trip. The commissioning found and repaired a stale-view defect: browser tools refuse unsaved scene drafts, include the visible playhead, and bind scene proposals to that time. M11 has actual-host evidence for proposal, build, correction, save, and reopen, but remains `CONTRACT_VERIFIED` until the artist accepts a composition. Keep the existing disconnect/manual-path regressions green and recommission after any further host API or registration change.

The browser registry in `src/storyboard-studio-web/src/webmcp.ts` now exposes eleven tools: `get_storyboard_context`, `list_storyboard_shots`, `get_shot_details`, `inspect_shot_continuity`, `get_director_context`, `search_scene_assets`, `observe_current_frame`, `propose_shot_revision`, `propose_scene_edit`, `propose_scene_blockout`, and `get_generation_status`. `search_scene_assets` is a bounded, project-scoped read of current, non-archived model revisions. It returns exact asset IDs that can be supplied as `matchAssetId` in a blockout proposal and never imports, places, generates, or dispatches work. Its API and desktop/tablet browser contracts are verified, and the actual host discovered the exact closed-schema registration with read-only annotations on 2026-09-22. Do not invent additional capabilities in prompts.

**Close M09 with the right evidence.** The preparation route has run live; the prepared sword derivative still awaits the artist's recorded source/derivative verdict. Locate its existing fixed-view evidence and present a concrete comparison. A later favorable texture verdict on a different hero asset does not accept that sword derivative. A mechanical pass is not a production-ready declaration. Do not regenerate merely to obtain a fresh-looking receipt.

**Respect the two dependency chains.** M12's precise replacement, M16's scene-to-shot path, and M17's exact-frame job/encoding path are `CONTRACT_VERIFIED`; their declared live/human evidence still matters. M14 requires M09 acceptance and M13, then integrates a real supported rig-creation route and deformation review. A compiler checkout or consumed profile alone does not satisfy M14. M17's application route is ready to consume that accepted rig, but fixtures and a controlled encoder cannot prove the actual playable take. M18 now has a separate-data-root graph round trip for a rig, clip, articulated prop, annotation, derivative revision, frozen still, candidate lineage, and snapshot hash; a controlled completed M17 take also survives fresh-ID import, restart, playback, and re-export. A separate disposable packaged-runtime smoke proves export/import, backup, offline restore, restart, and hash verification. Completion still requires the connected full scenario with the accepted M14 rig and its real M17 take on the packaged runtime. Consult the exact milestone contracts before coding; do not skip the rig or substitute a viewport recording to claim completion.

**Keep compiler ownership clear.** Read [REFERENCE_ASSET_COMPILER.md](REFERENCE_ASSET_COMPILER.md) and the compiler checkout's canonical `docs/BROWSER_STUDIO_CONTRACT.md`. Framewright now reads `profiles/skeletons/*.json` from that checkout and enforces its hierarchy, list, count, root, influence, and triangle-budget contracts; its fingerprint remains byte-compatible with the compiler vector. `humanoid-a` exists only as a deterministic Framewright test fixture. The compiler owns generation/preparation/rigging, source conditioning, and verdicts. Framewright owns validated import, user review, library/scene state, and applying root motion once. Do not replace the reference-conditioned pipeline with an eyeballed Blender reconstruction.

## Source map

Paths below are relative to the repository root. Inspect the nearest tests and callers before changing a boundary.

| Concern | Starting points |
| --- | --- |
| Shared shell, selection, director context | `src/storyboard-studio-web/src/App.tsx`, `types.ts`, `api.ts`, `webmcp.ts` |
| Scene authoring and rendering in browser | `src/storyboard-studio-web/src/components/SceneWorkspace.tsx`, `SceneViewport.tsx` |
| Exact-frame scene-take jobs and encoding | `src/StoryboardStudio.Api/Services/SceneRenderService.cs`, `SceneVideoEncoder.cs` |
| Model inspection, generation, preparation | `src/storyboard-studio-web/src/components/ModelInspectionWorkspace.tsx`, `ModelViewer.tsx`, `ModelFromReference.tsx`, `ModelPreparation.tsx` |
| Proposal and annotation services | `src/StoryboardStudio.Api/Services/WebMcpStoryboardService.cs`, `SceneDirectionService.cs`, `SceneBlockoutService.cs` |
| Persistent scene and motion contracts | `src/StoryboardStudio.Api/Services/SceneService.cs`, `SceneMotionService.cs`; `src/StoryboardStudio.Core/StudioContracts.cs` |
| Owned compiler work and validated assets | `src/StoryboardStudio.Api/Services/ModelGenerationService.cs`, `CompilerGateway.cs`, `AssetStore.cs`, `GlbModelInspector.cs`, `GlbRigInspector.cs`, `GlbClipInspector.cs` |
| Backend regressions | `tests/StoryboardStudio.Api.Tests/Scene*Tests.cs`, `WebMcpApiTests.cs`, `ModelGenerationApiTests.cs`, `CompilerGatewayTests.cs`, `GlbModelInspectorTests.cs` |
| Browser tests and isolated runtime | `src/storyboard-studio-web/playwright.config.ts`, its configured test directory, `scripts/run-e2e-server.ps1`, `scripts/e2e-compiler-stub.ps1` |

For storage/trust boundaries use [ARCHITECTURE.md](ARCHITECTURE.md); for library relationships use [ASSET_LIBRARY.md](ASSET_LIBRARY.md); for formats use [3D_CONVENTIONS.md](3D_CONVENTIONS.md). Use [CODEX_SETUP.md](CODEX_SETUP.md) and the repository-local `framewright-setup` skill for setup/launch. Use [QA_RUNBOOK.md](QA_RUNBOOK.md) and [RELEASE_EVIDENCE.md](RELEASE_EVIDENCE.md) for manual and packaged acceptance.

## Research intake

The artist intends to supply research before completion. When it arrives, extract the specific decisions it resolves: additional-host compatibility, compiler route/profile, reference and clip licensing, renderer and delivery policy, hardware/install requirements, and remaining human evidence. Link primary sources and record tested/pinned versions where applicable.

Separate recommendations from capabilities already verified here. Prefer an existing working route over introducing another framework. Put adopted decisions and their evidence in the canonical goal; do not append an incompatible second specification. If research changes scope, trust boundaries, substantial infrastructure, or acceptance, make that conflict concrete for the artist before implementation. Ordinary API details and routine fixes are Sol's work.

## Validation and operating boundaries

For each increment, prove the actual result through the application and storage, including its manual path. Run focused regression checks, then the relevant desktop/tablet journeys. For persistent changes, include reopen/restart, conflict handling, and relevant migration/recovery checks. For jobs, include owned-ID recovery, duplicate delivery, stale results, and ambiguous outcomes. Verify that disconnecting the assistant leaves the same saved project usable.

Before final completion, run the repository gate from the nested repository root:

```powershell
# Choose a free test port; let the resident applications keep their chairs.
$env:STUDIO_E2E_PORT = '5182'
.\scripts\verify.ps1
```

Check that the port is actually free first; no test port is reserved forever. The gate restores dependencies and runs audits, builds, backend/script tests, and Playwright. Use focused commands from AGENTS.md during iteration instead of running the full gate for every edit. Report the exact tested revision, command outcomes, skips, environment, artifacts, and unrun checks. Hosted CI, actual-host proof, authorized live-worker proof, human acceptance, and full packaged recovery remain separate evidence categories.

Use disposable data and bounded licensed fixtures. Preserve active applications, production databases/media, GPU work, and external queues. Keep local configuration, tokens, model weights, and generated/private assets out of Git. Work in the existing checkout without new linked worktrees. The maintained Framewright destination is public `raydeStar/framewright`; do not resume the deprecated private mirror. Keep compiler-owned implementation in `raydeStar/reference-asset-compiler` and consume its pinned contract here.

Immediately before a new live provider/GPU run, download, login, or paid action, follow AGENTS.md and request the required specific authorization with concrete inputs and cost/resource scope. Gather existing evidence and prepare the review first. Do not repeatedly ask about already authorized ordinary work. Artistic acceptance belongs to the artist, not to an assistant inspecting its own output.

## What counts as finished

Complete the canonical goal's small-workshop acceptance scenario and all required milestone evidence. It must include actual-host direction, a reused/generated prop with precise instance replacement, the supported generated rig and compatible clip, rigid-part motion, a shot camera and timing, a ratified scene-derived still, and a real rendered animated take. Reopen the editable scene, export/import it into an isolated workspace, and restore a backup with correct resources, identities, lineage, and playable output.

Then exercise the artist's experience end to end: enter the workspace, point and direct, review a coherent proposal, obtain the next result through the existing explicit action, refine it, and continue without screenshot relay or technical plumbing. Confirm that manual use still works when the host disconnects. Record physical tablet/stylus checks and human visual verdicts separately from automation.

If required external evidence is unavailable, deliver a clear engineering checkpoint with the exact remaining blocker and prepared next action. Do not label that complete. The final receipt must use the goal's status vocabulary, identify the tested code revision and supported scope, and link evidence and limitations. A convincing demonstration is welcome; an editable, recoverable result is mandatory.

## Paste into the Sol implementation task

```text
Complete Framewright's Director Mode and reusable 3D workflow using
docs/SOL_DIRECTOR_MODE_HANDOFF.md as the entry brief and
docs/goals/director-mode-3d-scenes.md as the sole acceptance/status ledger.
Read AGENTS.md, inspect the current checkout and any research attached here,
and resume from the current evidence rather than restarting M00.

My intended experience is to open Director Mode, point and describe what I want,
and have the assistant handle context and routine work while I review coherent
changes and make the existing Generate/Render and artistic acceptance decisions.
Prove the actual host connection early, preserve ordinary manual workflows,
and complete the required scene, rig, timing, rendering, and recovery path.

Implement, test, inspect, fix, checkpoint, and continue across dependency-ready
milestones. Keep the ledger honest about fixture, actual-host, live-provider,
human, and packaged evidence. Prepare concrete review material before asking
for required input; do not ask me to coordinate routine engineering.

Use the existing checkout, preserve unrelated work, and deliver authorized
Framewright changes to public main without a PR. Do not deploy or tag. Put
modeling, texturing, rigging, and compiler-receipt implementation in
raydeStar/reference-asset-compiler, then update Framewright's pinned consumer.
Follow AGENTS.md for specific live-run/download consent. Report
genuine blockers precisely and continue independent authorized work where useful.
```
