# Goal: Director Mode and reusable 3D scene production

**Project:** Framewright  
**Suggested repository path:** `docs/goals/director-mode-3d-scenes.md`  
**Plan version:** 1.0  
**Created:** 2026-09-19  
**Overall status:** IN_PROGRESS  
**Active milestone:** M09, awaiting the artist's verdict on the prepared sword derivative. M00, M01, M02, M03, M04, M05, M06, M07, M08, M10, M13 and M15 are VERIFIED; M11, M12 and M16 are CONTRACT_VERIFIED. M11 now lacks only recorded human composition acceptance. M14 requires M09 acceptance as well as M13; a checked-out compiler alone does not satisfy that dependency. M17 and M18 remain open, with portable-package recovery groundwork implemented for M18.

**Implementation authority:** Existing repository and scoped `AGENTS.md` instructions remain in force.

> Deliver small, working increments. Prove each increment's agreed contract before dependent work advances. Defer breadth and polish, not correctness that the next increment requires.

This is an implementation goal, not a claim that the described capabilities exist. The author reviewed repository documentation, not a running workstation or a complete source audit. No implementation, tests, provider calls, or production acceptance were performed while preparing this file. M00 must reconcile this plan against the actual checkout.

## 1. Start here

Read the applicable `AGENTS.md`, this section, the shared contracts below, and the next dependency-ready milestone. Inspect relevant source before editing. Read supporting documents when the active milestone needs them; do not repeatedly load every document or implement the entire roadmap at once.

Use this kickoff instruction:

```text
Execute docs/goals/director-mode-3d-scenes.md from its current evidence ledger.
Follow the applicable AGENTS.md. Establish the current baseline first.
Work through one dependency-ready milestone at a time. Implement, test,
inspect the actual result, repair failures, and record evidence before advancing.
Continue automatically across proven milestones within existing permissions.
Do not weaken acceptance criteria, invent passing evidence, rebuild existing
features unnecessarily, or turn this into one wholesale implementation.
```

Keep this file as the single goal-level source of truth. Update the progress and decision records as work proceeds. Do not create a parallel requirements document for every milestone. Detailed specifications may be linked when necessary; each requirement should have one authoritative home.

For the post-QC Sol continuation, use [the execution handoff](../SOL_DIRECTOR_MODE_HANDOFF.md). It provides entry points and an implementation brief; this goal retains all milestone status and acceptance authority.

### Execution rules for this goal

1. **Before a milestone:** Inspect its implementation surface and dependencies. Record concrete checks and fixtures before changing behavior. Reuse working capabilities; verify an already implemented milestone instead of rebuilding it.
2. **During implementation:** Run focused checks first. Then exercise the connected behavior through the actual application and persistence boundary. Do not substitute mocked UI responses for integration evidence.
3. **Before advancing:** Check relevant existing regressions, inspect the diff and test quality, record results and limitations, and make a local checkpoint consistent with repository policy. Never include unrelated user changes, private media, secrets, or weights.
4. **When a repair stalls:** After two attempts without new evidence, stop speculative patching. Capture inputs, state, logs, and a minimal reproducer before trying another repair.
5. **When a dependency fails:** Repair and regression-test the prerequisite. Do not compensate for a broken contract in its consumer. Independent milestones may proceed when their own dependencies are satisfied.
6. **When the plan needs adjustment:** Preserve the outcome and acceptance guarantees. Record justified implementation changes. Obtain user agreement before cutting required scope, changing trust boundaries, weakening approval rules, or adding substantial infrastructure.

No approval is needed merely to advance a passing engineering milestone. Existing consent requirements for provider calls, GPU work, downloads, production canaries, credentials, artistic ratification, and destructive actions still apply. This goal does not authorize automatic publishing, deployment, tagging, or edits to another repository.

### Status and evidence vocabulary

- `NOT_STARTED`: No implementation evidence recorded.
- `IN_PROGRESS`: One active integrated slice is being implemented or repaired.
- `CONTRACT_VERIFIED`: Deterministic application and contract checks pass, but a declared external-host, provider, hardware, or human acceptance check remains unavailable.
- `VERIFIED`: All required acceptance checks for the milestone have actually passed.
- `BLOCKED`: A named prerequisite, defect, permission, or environment condition prevents further work.

Dependencies normally require `VERIFIED`. A `CONTRACT_VERIFIED` interface may support explicitly labeled deterministic development, but it does not prove the real integration. Record that dependency and keep the uncommissioned runtime capability disabled or visibly unavailable. Never work around a known failing contract or claim the dependent live workflow is supported.

Evidence labels: **D** = deterministic checks; **A** = actual application with real service/persistence; **H** = actual browser/agent host; **L** = explicitly authorized live provider/worker; **V** = recorded human visual acceptance; **P** = packaged runtime/recovery.

## 2. Product outcome and boundaries

Framewright becomes a local-first production workspace in which an artist can:

- Draw, annotate, and refine an image using the existing shared image workspace.
- Inspect a model, save it as a reusable library asset, and preserve its source and revisions.
- Generate a model from a selected image through a validated provider route.
- Prepare a model through the existing Reference Asset Compiler where its capabilities are proven.
- Leave static props unrigged, animate rigid parts when appropriate, and optionally rig and animate a supported character class.
- Assemble individually selectable assets into an editable scene, frame a shot, and render a reviewed animated take.
- Use a full-view **Director Mode** with model-independent browser tools for shared visual context, comments, and proposed edits.

The workflow is connected, not mandatory:

```text
Sketch or reference -> image revision -> model revision -> optional preparation
    -> optional rig -> compatible animation -> scene instances -> shot -> render

Imported models, existing library assets, and approved clips can enter at their
appropriate stage. The library is available throughout, not only at the end.
```

Astra may be the user's preferred visual collaborator. No domain entity, tool schema, provider contract, or required application control may depend on that model. The manual application must remain usable without an agent or browser-tool support.

**Artist clarification, 2026-09-21:** "My hope is to eventually just pop into director mode and have you drive most things." The intended experience is to enter the workspace, point at the relevant image or scene object, and describe creative intent while the assistant carries context, inspects available assets/capabilities, and prepares coherent proposed changes. The artist should not relay screenshots, repeat revision IDs, copy prompts between tools, or operate worker internals. Proposals remain reviewable; explicit Generate/Render and artistic ratification retain their existing boundaries. Verify actual-host collaboration early because it is central to this outcome. Full-view scene/timing interaction is a desired completion experience, not a claim that today's image-only Director Mode already provides it; any needed UX extension must reuse the existing scene workspace and the shared contracts below.

**Editable scene data and generated video are different products.** A video is a take derived from a frozen scene/shot state. It must not replace the scene that produced it.

### First supported release profile

Keep the first release deliberately bounded:

| Area | Required initial support |
| --- | --- |
| Image creation and review | Extend existing sketch, image, reference, feedback, and revision behavior. |
| Browser-facing model format | A validated, self-contained GLB subset; explicitly list supported materials and extensions. |
| Static assets | Import, inspect, preserve revisions, reuse in library and scenes. |
| Model generation | One commissioned image-to-3D route, not a provider marketplace. |
| Preparation | One proven compiler/Blender route preserving the chosen source authority. |
| Rigging | One named, tested humanoid profile and one verified rig-creation route. |
| Animation | Compatible clip import/playback, basic instance timing, rigid-part and camera motion. |
| Scenes | Small editable scenes with object instances, basic lighting, camera, and save/reopen. |
| Browser-agent collaboration | Narrow structured reads/proposals plus actual host and visual-context evidence. |
| Output | A reviewed still and short animated take, plus a portable editable project package. |

Preserve existing image, video, dialogue, voice, and music workflows. YuE2 composition work and other existing media routes are not being redesigned by this goal.

**Outside this release:** arbitrary topology editing in the browser; a general Blender replacement; universal auto-rigging; arbitrary-skeleton retargeting; facial performance, cloth/hair simulation, physics, gameplay, and a full nonlinear editor; exact single-image room reconstruction; new hosting/multi-tenant architecture; mandatory new subscriptions; automatic model downloads; distributed GPU scheduling; a second chat or agent-orchestration platform; new engine-export features beyond proven existing routes. These exclusions do not remove rigging, animation, or editable animated scenes from the required goal.

## 3. Existing foundations to preserve

The inspected Framewright documentation describes a local-first React/ASP.NET Core application, SQLite plus content-addressed assets, immutable review evidence, shared image composition, project-scoped library records, owned jobs, and server-side validation/approval boundaries. Reconcile the exact source implementation in M00 rather than treating documentation as runtime proof. [R1][R2][R3]

The existing `AGENTS.md` requires isolated tests, human-initiated provider dispatch, read-only Codex advice, and browser tools that may stage proposals but must not ratify canon or dispatch providers as a side effect. Preserve these boundaries. [R0]

The Reference Asset Compiler is `raydeStar/reference-asset-compiler`. Its README documents mesh preparation, review receipts, static-prop routes, rig-related tooling, and engine validation. It also explicitly limits the resumable character operator to modeling review and distinguishes contract-tested routes from fresh live acceptance. Do not infer that its character pipeline is already safely autonomous. [R4]

### Responsibility boundaries

| Owner | Responsibility |
| --- | --- |
| Framewright application service | Intent, project scope, IDs/revisions, library, scenes/shots, validation, approvals, persistence, audit, and job ownership. |
| Existing browser client | Canvas/model/scene interaction, selection, previews, markup, playback, and accessible controls. |
| WebMCP adapter | Narrow browser-facing context/proposal tools over the same application behavior, not an alternative backend. |
| ComfyUI | Allowlisted generation workflows whose capabilities and outputs are validated. |
| Compiler/Blender worker | Named preparation, rig, validation, and export operations proven in the existing pipeline. |
| Existing output/audio services | Encoding, production packaging, and separately editable sound. |

Prefer existing abstractions and dependencies. Choose a browser 3D renderer after inspecting available code; Three.js is a candidate, not a mandate to replace working infrastructure. Reuse compiler functions through a narrow adapter instead of copying an entire repository or importing an Unreal runtime requirement into Framewright's browser workflow.

ComfyUI remains the preferred generation backend where a suitable route is proven. If the compiler's existing direct generator is the practical initial route, document the tradeoff and obtain agreement before substituting it for the requested ComfyUI-backed path. Do not rebuild a working direct pipeline as a node graph solely for uniformity.

## 4. Shared contracts

These are acceptance requirements, not prescribed class names. Map them to current types. Add schema only as the first consuming milestone requires it; do not prebuild every future subsystem.

### 4.1 Identity, versions, and scene instances

- Preserve project scoping on every read, write, job, asset, annotation, and export. A guessed ID from another project must not grant access.
- Separate reusable asset identity from immutable source/model revisions and editable library metadata. Reuse the content-addressed store; do not create a competing asset system.
- A scene contains distinct instances referencing exact asset revisions. Two chairs may share source bytes but have different transforms. Do not overload a shot-placement relationship that cannot represent repeated scene instances.
- Define scene working state, immutable scene snapshots, and shot bindings. A shot binding identifies the exact scene snapshot, camera, and time interval.
- Creating a new model revision does not silently update existing scenes. Explicit upgrades are reviewable and preserve old references.
- Source images, meshes, textures, rigs, clips, and renders retain provenance and hashes. Archive is not deletion; referenced evidence must remain resolvable.
- New mesh topology invalidates dependent rig, skin, animation-compatibility, and geometry-anchor approvals until revalidated. No silent transfer of a successful badge.

### 4.2 Units, geometry, and format support

Before M04, record a single scene coordinate convention, physical units, transform/pivot semantics, color handling, and import/export conversions. Use glTF conventions for the browser interchange boundary unless a documented existing constraint requires conversion. Add a known-dimension asymmetric fixture to catch mirror, axis, and scale mistakes. [W3]

Keep native authoring outputs when necessary, but use supported GLB artifacts for browser inspection. Preserve Framewright-specific metadata in its own project manifest rather than pretending a GLB contains all review and authoring state.

Support a bounded import subset first. Validate containers, resources, geometry, finite transforms, texture allocations, counts, and declared required extensions. Block external network/file references in the initial self-contained route. Enforce configurable resource limits before expensive loading. Unsupported assets get a useful error, not a blank viewport or a success record.

For prepared runtime assets, use the user's approximately 50,000-vertex target where practical. Record vertex and triangle counts separately. Quality and deformation gates still apply; reaching a count is not proof of production quality. Never destroy the high-resolution authority to meet a derived-asset budget.

### 4.3 Director Mode and annotations

Reuse existing image/sketch/feedback components and revision semantics. Entering full-view mode must not fork document state or lose unsaved work.

Image annotations bind to the exact image revision and content-area coordinates, excluding letterboxing. 3D annotations bind to the project, scene/object instance, model revision, captured camera, and timeline position where relevant. Use a revision-bound surface anchor when supported; otherwise label a note as attached to a frozen viewport capture. Do not invent depth or silently remap marks after topology changes.

Provide both a bounded structured context packet and an actual visual representation. They must identify the same state revision, selection, camera, and time. Detect mismatches caused by user edits or navigation and request fresh context instead of applying stale instructions.

Orbiting the inspection camera must not transform the model. Essential drag, drawing, and selection actions need explicit touch/keyboard alternatives. Unsupported WebGL or browser-tool capabilities must leave an honest, usable fallback.

### 4.4 Agent tools, proposals, and permissions

Expose typed operations for current context, selected objects, relevant annotations, available capabilities, proposal staging, and owned job/result inspection. Add only the tools the active milestone needs.

Browser tools and normal UI commands share validation and application services. Preserve the existing distinction between server MCP and browser WebMCP. Verify the current browser API and actual host; do not assume a familiar API name, a polyfill, or a mocked registration proves native integration. WebMCP remains an evolving draft at this plan's source-review date. [W1]

The initial agent interaction is **inspect -> propose -> user reviews/applies -> inspect result**. User application of a proposal uses normal validated commands. An explicit Generate/Render action remains the provider authorization boundary. A proposed edit, an applied edit, an approved asset, and a dispatched job are separate states. Agent tools cannot ratify canon or turn a staged proposal into provider dispatch.

Proposals include target IDs, expected revisions, intended changes, preserved constraints, and a bounded scope. Validate the whole batch before applying it. A stale, unauthorized, or malformed batch leaves existing state intact. Applied changes are reversible through the supported history/undo mechanism; undo never rewrites accepted historical evidence.

Treat imported text, model metadata, comments, and provider responses as untrusted content. Tool hints are not authorization. Enforce same-origin/session protections and permissions on the service, not merely in the UI. Expose no arbitrary SQL, shell, Python, filesystem paths, node graphs, provider endpoints, or credentials.

A timeout is not proof that a mutation failed. Reconcile by request/proposal/job identity; represent unresolved outcomes honestly and do not encourage blind retries.

### 4.5 Providers and workers

- Use typed, allowlisted operations with capability preflight and bounded inputs/outputs. Missing workers or unsupported profiles produce actionable blocked states before dispatch.
- Extend existing job ownership where appropriate; do not create a separate scene-generation queue with competing semantics.
- Freeze exact input revisions, workflow/worker versions, parameters, ownership, and output lineage. Persist request/job identities for recovery.
- Reconcile interruptions against the existing provider job. Do not claim exactly-once execution across an ambiguous network failure or silently submit a duplicate.
- Never clear, interrupt, cancel globally, reorder, or harvest someone else's ComfyUI work. UI abandonment is not provider cancellation. ComfyUI exposes distinct queue/history/control routes, so allowlisting matters. [W2]
- Default new GPU-heavy Framewright operations to conservative serialized execution on the shared workstation. Check capacity without terminating other applications or modifying their jobs.
- Keep credentials and native worker secrets server-side. Named worker operations resolve asset IDs to authorized storage; browser input cannot choose host commands or arbitrary paths.
- No provider output is current until validation/import succeeds and its source state remains eligible. Stale output stays a provenance-linked candidate rather than replacing newer work.

### 4.6 Motion and output

Declare asset motion intent as static, rigid-part, or deforming. Static props need no skeleton. Rigid-part motion can use transforms and pivots. Character clips require a verified skeleton/profile relationship.

A rig is a revision-bound derivative, not a generic success flag. Record skeleton profile, rest pose, skinning results, supported clips, and deformation evidence. Initially reject incompatible clips rather than attempting unverified retargeting.

Define timing units, frame rate, clip trim/loop behavior, root-motion policy, and camera interpolation. Preserve project delivery settings. Audio remains separately editable and is not injected into image or motion-generation prompts.

Render from frozen scene/shot inputs, not the browser's incidental current camera. Existing still approval and video gates remain intact. A new 3D render route must join the existing review/candidate flow explicitly, never bypass it through a new export button.

## 5. Verification and release gates

### Milestone acceptance

Before calling a milestone verified, record:

- Its defined user outcome through the real application where applicable.
- Focused regression evidence and at least the meaningful failure/conflict case for its contract.
- Save/reopen or restart evidence whenever persistent state is introduced or changed.
- Relevant earlier walking-path regressions, not just the newest component tests.
- Actual external/visual evidence required below, with unavailable checks marked unrun.

A success notification, endpoint status, schema migration, screenshot, or green mocked test is not sufficient by itself. Inspect resulting state and behavior. Test new bug checks against the reproducer or an intentionally broken implementation when practical. Do not weaken tests, automatically bless changed visual baselines, or suppress existing failures to manufacture a pass.

Use known, licensed, small fixtures in isolated workspaces. Do not read or modify the artist's production database/media for routine tests. Explicit live canaries use authorized disposable inputs and obey existing provider permissions.

### Test layers

**Fast:** Existing build/type/lint checks and focused unit/contract tests. Discover exact commands in M00.

**Feature:** Real browser/service/storage journeys, invalid input, concurrency, revision binding, and worker-adapter tests using clearly labeled controlled doubles where external execution is unavailable.

**Live:** Separately authorized provider/worker and actual browser-agent checks. Record installed versions, workflow hashes, hardware, outputs, and known quality limits. No fixture result counts as live evidence.

**Release:** Existing repository gates, packaged runtime, supported desktop/tablet behavior, migrations, backup/restore, export/reopen, degraded operation, and targeted security checks. The current `AGENTS.md` names `./scripts/verify.ps1` and `./scripts/public-release-audit.ps1`; verify their current contracts before use. [R0]

For each schema change, test upgrading a representative prior-schema fixture in an isolated workspace at the introducing milestone. Do not wait until release to discover an incompatible migration, and never use the production database as a migration experiment.

Add package smoke checks when a milestone changes runtime dependencies, native-worker access, assets, or deployment configuration. Do not postpone all packaging discovery until M18.

### Safe to build on versus ready to release

A proven narrow contract can advance while optional formats, richer controls, thumbnails, and optimization remain deferred. Data loss, wrong-target edits, permission bypasses, false success, unreconciled provider effects, and broken prerequisite behavior cannot be filed as polish.

A phase may be released independently only for its verified capabilities. New unfinished routes remain gated. The full goal is complete only when all required milestones and release acceptance pass; adding documentation or checkboxes does not make a capability officially supported.

## 6. Milestone roadmap

All milestones start `NOT_STARTED`. The order below is the default execution order; use the dependencies to select independent work when an external acceptance gate is blocked.

| ID | Phase | Observable increment | Dependencies | Required evidence |
| --- | --- | --- | --- | --- |
| M00 | Baseline | Reproduce and protect the existing image/library path | None | D, A |
| M01 | Director Mode | Full-view image feedback with correct revisions | M00 | D, A |
| M02 | Director Mode | Browser tools observe the exact displayed state | M01 | D, A, H |
| M03 | Director Mode | Review and apply a bounded image-edit proposal | M02 contract | D, A, H |
| M04 | Model foundation | Safely import and inspect one supported GLB | M00 | D, A |
| M05 | Model foundation | Save/reopen reusable model revisions | M04 | D, A |
| M06 | Model foundation | Place library models into a persistent scene | M05 | D, A |
| M07 | Generation | One image-to-model adapter proves its owned-job contract | M05 | D, A |
| M08 | Generation | One live image-to-model route reaches the scene | M06, M07 | D, A, L, V |
| M09 | Preparation | Produce a reviewed derivative without replacing its source | M05, M07 | D, A, L, V |
| M10 | Scene direction | Orbit, annotate, and propose edits to the correct 3D object | M03 contract, M06 | D, A, H |
| M11 | Scene direction | Turn a reference into an editable blockout proposal | M10 | D, A, H, V |
| M12 | Scene direction | Replace one placeholder with a detailed reusable asset | M08, M11 | D, A, L |
| M13 | Motion | Inspect a known rigged character and validate its rig | M05 | D, A |
| M14 | Motion | Create and review a rig for the supported character profile | M09, M13 | D, A, L, V |
| M15 | Motion | Reuse compatible clips and animate rigid parts | M06, M13 | D, A |
| M16 | Shots | Persist scene timing/camera and review a scene-derived still | M12, M15 | D, A, L, V |
| M17 | Rendering | Render a reviewed animated take from frozen scene state | M14, M16 | D, A, L, V, P |
| M18 | Release | Reopen/export/recover the complete supported workflow | All required milestones | D, A, H, L, V, P |

### M00. Protect the current walking path

**Deliver:** Inspect actual source, repository instructions, branches/worktree, tests, packaging, existing MCP/browser tools, and the relevant compiler checkout when accessible. Record exact commit IDs, commands, prerequisites, and a reuse map in the ledger. Do not alter another repository or install missing tooling.

Reproduce a small existing image/sketch -> feedback -> revision -> library save/reopen journey in an isolated workspace. Identify which reported manual failures are reproducible rather than assuming the entire app is healthy or broken.

**Prove:** The protected journey exercises real persistence. Reproduce and repair failures that invalidate the next phase, with regression checks. Record unrelated baseline failures without hiding them. Confirm tests cannot reach production paths/providers. Establish the small fixture set and package-smoke command.

**Defer:** Unrelated application cleanup. Do not begin new Director Mode or 3D features in this milestone.

### M01. Full-view image direction

**Deliver:** A full-view Director Mode using existing image/sketch/comment components. Preserve the active subject, revision, references, locks, draft state, and navigation context. Expose draw, pin/comment, selection, and compare through existing behavior.

**Prove:** Draw and pin an instruction on revision A; switching to B does not move the note or mutate B. Return to A and reopen the workspace with the expected saved state. Resizing/letterboxing does not misplace pins. Entering/exiting full view preserves edits. Archived/ratified restrictions still apply. Keyboard and touch alternatives work for the primary path.

**Defer:** Rich brush libraries, custom layouts, and a new conversational UI.

### M02. Exact context through browser tools

**Deliver:** A versioned context packet and the smallest read-only browser-tool surface. Include selected subject/revision, relevant annotations/constraints, available actions, and a matching viewport capture or validated host visual-observation path. Feature-detect the actual browser API and keep host specifics in a thin adapter.

**Prove:** An actual supported agent/browser can discover tools, read context, and observe the same annotated revision the user sees. Record host/browser versions and access configuration. Reject or refresh stale selection/capture combinations and wrong-project requests. Navigation unregisters or safely invalidates old tool state. Unsupported environments retain manual Director Mode.

**Defer:** Additional hosts. Without actual-host evidence, mark `CONTRACT_VERIFIED`, not native WebMCP support. A shim or synthetic client proves only its own contract.

### M03. Proposed image edits, not hidden writes

**Deliver:** The agent can stage a bounded edit proposal from the current image, notes, and constraints. The user can inspect, reject, or apply it. Applied instructions feed the existing revision/generation UI; only the existing explicit user action authorizes generation.

**Prove:** A supported host stages an edit aimed at a marked region while preserving a locked subject. Rejecting changes nothing. Applying targets the expected revision; stale revisions and malformed batches fail without partial mutation. Applying twice cannot double-apply the same proposal. The prior revision remains inspectable. A real-app proof adapter demonstrates candidate feedback without contacting production; do not claim live image generation from that test.

**Defer:** Autonomous ratification, provider dispatch, and unattended acceptance. Existing Codex advice remains read-only.

### M04. Import and inspect one supported model

**Deliver:** Import a small self-contained GLB into an unapproved project asset, validate it, and open an isolated model viewer. Support orbit, zoom, frame/reset view, material inspection, and basic geometry/resource statistics. Record the supported format/extension and resource-limit profile.

**Prove:** A known-scale asymmetric fixture has correct orientation, dimensions, and materials. Camera orbit does not alter object transforms. Malformed/truncated assets, oversized resources, external references, and unsupported required extensions are rejected safely. Failed imports create no usable-looking broken asset. Switching models releases resources; missing graphics support produces an honest fallback.

**Defer:** Other file formats, advanced shading, sculpting, and automatic repair.

### M05. Durable model library and revisions

**Deliver:** Extend existing library behavior for reusable models: name, tags/collection, revision selection, source/provenance, and non-destructive archive. Preserve immutable media facts while editing organization. Use current project/global-library boundaries; do not invent a global sharing service.

**Prove:** Save a model, restart the app, and reopen the exact revision with correct metadata/materials. A new revision does not overwrite the old one. Duplicate content reuses storage where appropriate without crossing project permissions. Referenced archived assets remain resolvable. Attempts to access another project's model fail. Existing image/audio/video library behavior still passes.

**Defer:** Bulk import, automated tagging, and elaborate thumbnails.

### M06. A small persistent scene

**Deliver:** A scene editor with basic lighting/camera and distinct instances of library revisions. Add, select, move, rotate, scale, duplicate, and remove an instance using explicit controls and optional gizmos. Establish scene snapshots and revision-conflict behavior only as needed for this slice.

**Prove:** Place two instances of one prop and transform them independently. Save, restart, and reopen with the correct asset revisions, transforms, camera, and light settings. Editing a library name does not move objects. A stale save fails without erasing newer work. Missing/unavailable assets have explicit placeholders with preserved identity. Removing an instance does not delete the library asset.

**Defer:** Advanced lighting, collision, snapping, large-world editing, and instancing optimization.

### M07. Image-to-model contract without pretending it is commissioned

**Deliver:** Freeze a selected image revision into a model-generation request. Add one typed provider/worker adapter, capability preflight, durable owned-job progress, and validated model-candidate import. Use controlled adapter outputs to prove application behavior without making GPU/provider requests.

**Prove:** A real-app fixture run produces a reviewable model with exact image lineage. Missing capabilities block before submission. Invalid output never becomes a usable model. Restart resumes a known job identity. Simulate the ambiguous submission/reply window: reconcile or report unknown, never silently resubmit. A stale source leaves the output as an older-source candidate. Duplicate deliveries do not duplicate accepted artifacts.

**Defer:** Provider comparisons and additional models. Label the route uncommissioned until M08.

### M08. Commission one live image-to-model route

**Deliver:** After explicit authorization, use the actual configured ComfyUI workflow or agreed compiler-backed route on a bounded image. Capture workflow/worker versions, request/job IDs, source/output hashes, resource use, and relevant limitations.

**Prove:** A real generated model imports, can be orbited, survives library save/reopen, and appears in the M06 scene at sensible scale. Record human visual acceptance of the supported static-prop output. Validate known-job recovery without manipulating unrelated provider work. A repeat delivery does not replace newer state or create duplicate committed output.

**Defer:** Broad style fidelity, arbitrary character readiness, and multiple generators. Missing hardware, authorization, or a viable route is a named blocker, not a fixture pass.

### M09. Reviewed model preparation

**Deliver:** Adapt one proven Reference Asset Compiler/Blender preparation route for a selected model revision. Produce a derivative for browser/runtime use with appropriate geometry, UV/material handling, statistics, fixed-view review evidence, and retained native output when needed. Reuse existing receipts and review gates.

**Prove:** Run the actual worker on a bounded supported mesh. Source geometry remains unchanged; the derivative cites it. Compare source/derivative fixed views and inspect material/UV integrity. Save/reopen both revisions. Worker failure, bad output, or excessive quality loss leaves the source usable and the derivative unapproved. Record human acceptance. A topology-changing derivative cannot inherit old rig/anchor approval.

**Defer:** General mesh editing, universal retopology, and automatic artistic approval.

### M10. Direct the selected 3D object

**Deliver:** Extend Director Mode to isolated models and scenes. Context includes object/instance identity, revision, camera, visible notes, and relevant time. Add supported surface/captured-view annotations and proposal operations for existing transforms or revision requests.

**Prove:** Select one of two identical prop instances, orbit, annotate it, and have an actual host propose a change to that instance only. User application leaves the other instance unchanged. Save/reopen the annotation and resulting state. Changing topology makes the old surface note explicitly stale rather than moving it elsewhere. Scene edits occurring after capture invalidate stale proposals. Agent-unavailable mode preserves normal controls.

**Defer:** Arbitrary screen-to-geometry reconstruction and free-form mesh editing.

### M11. Reference-to-scene blockout

**Deliver:** From a selected reference or sketch, let the agent propose an editable construction plan: object roles, library matches, simple geometry, motion intent, approximate scale, camera, and uncertain/occluded areas. Show a lightweight blockout before any expensive asset generation.

**Prove:** In the actual host workflow, review a bounded scene proposal and create distinct placeholders/simple geometry through user approval. The user can correct one placement and camera framing independently. Save/reopen the blockout. Reference IDs and assumptions remain inspectable. No provider job runs merely because the plan was proposed or applied. Record human composition acceptance.

**Defer:** Exact reconstruction of hidden geometry, automatic physical inference, and unlimited object batches.

### M12. Replace a placeholder, preserve the scene

**Deliver:** For one selected placeholder, reuse a library model or explicitly generate a candidate, inspect/review it, and bind the chosen revision to that scene instance. Keep the generation plan and lineage attached to the intended target.

**Prove:** Replace one placeholder without altering its instance identity, approved placement/pivot, other objects, lighting, or camera. Handle source-unit conversion explicitly. Save/reopen the result. A failed job leaves the blockout usable; a stale scene cannot be overwritten by a late result. Reuse the resulting asset in a second scene. Exercise a bounded live generation path after authorization, not only preexisting fixtures.

**Defer:** Automatic full-scene regeneration and concurrent generation orchestration.

### M13. Inspect a known rigged character

**Deliver:** Import a licensed, known-good rigged fixture for one documented humanoid profile. Expose skeleton/rest-pose inspection and profile/skin validation. This proves storage and inspection before trusting generated rigs.

**Prove:** Save/reopen mesh, skeleton, bind data, and materials. Confirm expected bone hierarchy/profile, finite transforms, valid skin weights, and a deterministic pose change. Wrong-profile or broken-skin fixtures fail honestly. A rig remains linked to its exact mesh revision. Static props still require no skeleton. Do not label arbitrary imported bones as animation-ready.

**Defer:** More body classes, hand/facial rigs, and arbitrary retargeting.

### M14. Create one supported rig

**Deliver:** Integrate one actual compiler/Blender rigging route for an eligible prepared humanoid revision. Preflight the input, installed tooling/license where applicable, profile, and landmarks. Produce a new candidate rig and the existing pipeline's deformation-review evidence.

**Prove:** After authorization, run the real worker and inspect a representative pose suite in the browser and worker output. Check source preservation, bind/rest pose, weights, obvious joint collapse, and source/profile lineage. A human records acceptance. Validate a known compatible clip through the compiler's existing motion-proof route; reusable browser clip controls are verified separately in M15. Missing landmarks, unusable topology, or poor deformation leave a reviewable failure, not an animation-ready badge.

**Defer:** Universal automatic rigging and silent mannequin substitution. Existing compiler automation limits are engineering work, not permission to skip this milestone.

### M15. Compatible clips and rigid-part motion

**Deliver:** Add reusable compatible animation clips to the library. Inspect, play, pause, scrub, trim, loop, and attach a clip to a compatible character instance. Support a minimal transform/pivot track for a rigid prop, such as a door or lantern, without creating a skeleton.

**Prove:** Two character instances share one clip but have independent playback settings. Scrubbing to known times produces expected poses. Save/reopen clip bindings and timing. Reject mismatched skeletons and invalid duration/time values before playback. A rigid prop animates around its declared pivot; static props stay unchanged. Record the supported root-motion policy and prevent double application of movement.

**Defer:** Clip blending, arbitrary retargeting, procedural motion generation, and advanced animation editing.

### M16. Scene timing, camera, and a reviewed shot still

**Deliver:** Assemble object motion and camera timing in a small scene timeline. Bind a Framewright shot to a frozen scene snapshot, camera, time range, and project delivery contract. Produce a scene-derived still through one actual render path and feed it into existing candidate review.

**Prove:** Save/reopen exact timing, clip revisions, camera, and delivery settings. The shot camera is distinct from the inspection camera. Scrubbing fixed times reproduces expected poses and framing. Render the selected frame through the real route after authorization and retain provenance. Human review/ratification uses existing rules; later scene edits do not rewrite the approved frame or snapshot. Existing sequence/audio controls remain functional.

**Defer:** Complex camera curves, shot blending, and a replacement sequence editor.

### M17. Render an animated shot, not just a viewport recording

**Deliver:** Use one verified deterministic 3D rendering route, preferably existing Blender/encoding capabilities, to render the bound scene/shot interval. Freeze scene, assets, rigs, clips, camera, frame rate, dimensions, and render settings. Join owned-job recovery, candidate review, and existing production export. Preserve the approved-still prerequisite.

**Prove:** A short bounded scene containing the M14-generated rig, a compatible clip, a rigid/static prop, and camera motion produces a playable take. Validate decoded duration, frame dimensions/rate, frame count/timing policy, and source manifest. Inspect representative frames and record human visual acceptance. Reopen the editable scene after rendering. Reconcile interrupted/duplicate delivery without replacing accepted work. Test the packaged route; keep sound separately editable/exportable.

**Defer:** A new generative-video model, high-end render farm, and film-quality universal automation. H3 generation remains a separate existing route.

### M18. Release and recovery proof

**Deliver:** Run the complete bounded acceptance scenario below on the actual packaged workstation runtime. Refresh support documentation and setup/preflight messages from evidence, not intended capabilities. Record exact release-candidate revision, dependency/workflow versions, known limitations, and rollback/recovery procedure.

**Prove:** The supported graph survives restart, export/import, and backup/restore in a disposable workspace, including model resources, native derivative references, rigs, clips, scene snapshots, annotations, and shot/take lineage. Exercise unavailable GPU/worker/agent modes and relevant security/resource-limit cases. Run required existing release gates and the manual desktop/tablet/stylus checks that automation cannot establish. No critical baseline regression is waived silently.

**Defer:** Documented noncritical polish only. Do not publish, tag, deploy, or alter production data without explicit authorization.

## 7. Bounded full-goal acceptance scenario

Use a disposable small-workshop project with licensed fixtures or user-authorized source assets. Keep test assets bounded; do not turn the proof into a large art-production project.

1. Start with a sketch/reference, use Director Mode markup and a host-generated proposal, and create a reviewed image through the existing supported image route. Record any required live-image authorization/evidence separately.
2. Generate one static prop from that image through M08. Inspect it, prepare it when needed, and save its revisions to the library.
3. Propose and approve a simple scene blockout. Place two instances of a reusable prop and replace one selected placeholder without moving unrelated objects.
4. Import a suitable prepared humanoid, create its rig through M14, review deformation, and assign a compatible clip. Keep a static prop unrigged and demonstrate one rigid-part motion.
5. Select and annotate an individual model in the scene. Have the actual host propose a bounded change; apply it through the user-facing review path and verify the correct target.
6. Define a short shot, review/ratify its still, and render the animated take. Use a small explicitly selected delivery preset for the disposable project; do not override an existing project's settings.
7. Close and reopen the app, export the editable package, import it into a clean isolated workspace, and restore a backup separately. Verify IDs/remapping, hashes, revision relationships, annotations, placements, motion, camera, and playable output. No original-machine absolute path should be required by the portable package.

The goal passes only with an editable result and recorded evidence. A polished demonstration video cannot substitute for these checks.

## 8. Progress and evidence ledger

Update this section after each milestone. Store verbose logs, images, captures, receipts, and fixtures in appropriate repository or ignored artifact locations; keep this file as their compact index. Never commit private production content or secret-bearing logs.

### Current execution state

Reconciled after QC and the repository-destination decision on 2026-09-21. This summary supersedes the historical execution notes below; individual acceptance records remain the evidence authority.

- **Repository:** Public `github.com/raydeStar/framewright` is the maintained Framewright line; the former private mirror is deprecated. Its public history was deliberately sanitized and has no merge base with the older private history, so publish reviewed snapshots as normal descendants of public `main`, never by force-pushing the private graph. `github.com/raydeStar/reference-asset-compiler` is the single implementation home for modeling, texturing, retopology, UV, rigging, deformation gates, payload construction, and compiler receipts. Framewright owns orchestration, validated import, library/scene state, shot binding, and review.
- **Current MVP increment:** M12 has an ordinary UI path for replacing one selected placeholder with an exact library revision while preserving instance identity, transform, plan provenance, annotations, pivot/motion, camera, lighting, and unrelated objects. Scene Director Mode uses the same working state. M16 freezes a saved scene version, distinct shot camera, frame range, selected time, project delivery contract, and snapshot hash; the real Three.js canvas produces a delivery-sized PNG and advances the existing shot candidate/review path. Working-package schema v5 carries the complete editable project graph and content-addressed assets; the project switcher verifies and imports it as a separate inactive project with fresh relational identities, preserved revision groups/lineage, interrupted external jobs made terminal, and no machine-local storage paths.
- **Validation:** The complete `scripts/verify.ps1` gate passed in one invocation on isolated port 5197: dependency audits, frontend checks/build, public-content audit, locked restore, warning-free Release rebuild, 283 backend tests, all backup/setup/worker/release scripts, voice and YuE2 contract tests, NuGet vulnerability inspection, and 134 Playwright journeys with six intentional tablet exclusions. The known Three.js chunk-size advisory remains; scene modules are lazy-loaded. The import proof exports, verifies, imports, activates, reopens, and serves the model from a fresh project, then imports the same package again with fresh IDs. Negative controls reject a checksum failure and a self-consistent forged GLB without creating a partial project.
- **Compiler:** M08 generation and the M09 preparation route have recorded live workstation evidence. Framewright now reads the compiler-owned skeleton profiles from the configured checkout and enforces hierarchy, list, count, root, influence, and triangle-budget rules; no production humanoid fallback remains in this repository. The workstation's ignored local configuration points at the existing compiler checkout. Do not infer readiness for rigging from profile consumption or geometry/preparation success.
- **Active acceptance:** M09 still needs the artist's recorded source/derivative verdict. M14 requires that prerequisite and M13 before live milestone advancement. M02, M03, and M10 have actual-host proof and are VERIFIED. M11 has D/A/H evidence and still needs human composition acceptance. The milestone table below is authoritative.
- **Hosted CI:** The last public-main baseline, `ecffc0c`, passed [GitHub Actions run 35679438121](https://github.com/raydeStar/framewright/actions/runs/35679438121). The current checkpoint still requires its own hosted run after push; local results do not replace that evidence.
- **Next engineering action:** Follow the Sol handoff, confirm current source and any new research, and prepare a concrete M09 review from existing evidence without regenerating it. Continue only dependency-ready work, labeling permitted fixture-based development separately from live support.
- **Consent and resources:** This continuation authorizes direct public-main delivery of reviewed Framewright work and propagation of compiler-owned implementation to its repository. It does not authorize provider calls, GPU jobs, downloads, artistic acceptance, deployment, or tagging. Preserve running applications and external queues. Existing live receipts are evidence of prior runs, not permission for another run.

### MVP implementation checkpoint - 2026-09-21

Status: M12 manual replacement and M16 scene-still contracts are implemented and locally verified. M12's new live-generation evidence and M16's human ratification remain separate acceptance items. This checkpoint does not advance M14 or M17.

The replacement journey starts from an approved blockout, binds a chosen reusable model to the same instance, preserves its exact transform and source-plan identity, leaves every other object and scene setting untouched, saves, reloads, and draws the replacement. The scene can enter and leave Director Mode without forking or discarding unsaved state.

The still journey saves a scene, selects an existing shot, edits an independent shot camera and time, renders the actual Three.js scene at the project delivery dimensions, imports the PNG through the validated content-addressed asset gate, creates the next working candidate, and opens existing Review. The service rechecks scene and shot versions after upload, stores an immutable snapshot and SHA-256, leaves later scene edits unable to rewrite it, survives restart, and exports the complete scene graph and binding in working-package schema v5. A regression caught and repaired SQLite's inability to order `DateTimeOffset` server-side.

Evidence: `scripts/verify.ps1` with `STUDIO_E2E_PORT=5197` passed as described in the current validation summary. Focused scene, rig-profile, and portable-package checks also passed on desktop Chromium and emulated iPad Pro 11. No live provider, model download, production data, or external queue was used. Remaining full-goal evidence includes the M09 artist verdict, M11 human composition acceptance, a compiler-owned M14 route and human deformation verdict, M17 deterministic animated render, full packaged-runtime plus backup-restore recovery, and physical tablet/stylus acceptance.

### M12 and M16 contract checkpoint - 2026-09-21

```text
Milestone / status / date: M12 / CONTRACT_VERIFIED and M16 / CONTRACT_VERIFIED / 2026-09-21
Tested code revision or worktree identity: reviewed working tree based on public c21027b; the checkpoint commit immediately following this gate records the tested tree
Outcome and supported constraints: Precise replacement and scene-to-shot still review are implemented through
  the ordinary application and durable store. Replacement preserves the selected instance and unrelated scene
  state. Still creation freezes scene/shot/camera/time/delivery inputs and enters ordinary candidate review.
Results by evidence class: D and A pass in the complete backend/browser gate. M12 lacks its declared new live
  generation evidence; M16 lacks the declared human visual acceptance. Neither status implies M14 or M17.
Human approvals actually recorded: NONE for these milestone outputs.
Known limits: M11 still lacks human composition acceptance; M12 and M16 retain their declared live/human evidence ceilings. No provider or renderer was commissioned here.
Next dependency-ready milestone: M09 remains active; M14 follows its artist verdict.
```

### Portable recovery groundwork - 2026-09-21

```text
Work / status / date: M18 editable package export/import groundwork / CONTRACT_VERIFIED / 2026-09-21
Outcome: schema v5 exports the complete project-scoped editable graph and asset bytes. Import validates archive
  paths, inventory lengths and hashes, resource ceilings, GLB structure, project scope, identities, and every
  relational reference; stages content, remaps database and revision-family identities, commits atomically, and
  creates a separate inactive project. In-flight external jobs become failed/interrupted and never resume.
Evidence: export -> import -> activate -> reopen -> model-content retrieval passes; a second import receives
  fresh IDs while content deduplicates. Checksum corruption and a forged self-consistent GLB both leave no
  partial project. Desktop and tablet project-switcher journeys pass in the complete gate.
Limit: this is not M18 completion. The actual packaged workstation runtime, full small-workshop scenario,
  playable M17 output, separate backup restore, and human/physical-device evidence remain open.
```

### Historical execution notes before main QC

Retained for provenance. Branch, environment, dependency, and next-action statements in this subsection describe earlier points in the implementation and are superseded by the current summary, milestone table, and later acceptance records.

- **Repository commit / worktree:** `9ab9b68` on `main`; work continues on `feature/director-mode` branched from it. The artist's previously uncommitted working tree (YuE2 music composition, guided setup/worker scripts, release audit, `AGENTS.md`, `LICENSE`, docs, this goal file) was landed as `9ab9b68` at the user's instruction before M00 was recorded. No linked worktrees. Branches `challenge/webmcp-storyboard` (`f8540a0`) and `production-hardening` (`64998d0`) are untouched.
- **Compiler revision and configured location:** Reference Asset Compiler is checked out on this workstation under the artist's own source tree (MIT, Python 3.11+, console script `rac`); the exact path is workstation state and is not recorded here. No revision is pinned here yet and Framewright has no configured location for it, so M09/M14 are dependency-ready rather than runnable. Read [../REFERENCE_ASSET_COMPILER.md](../REFERENCE_ASSET_COMPILER.md) before either; the contract itself is canonical in that repository at `docs/BROWSER_STUDIO_CONTRACT.md`.
- **Runtime / browser / agent host:** .NET SDK 10.0.203 (pinned by `global.json`, `rollForward: disable`), Node v22.15.0, npm 11.11.0, Windows 11 Pro 26200. Playwright projects: desktop Chromium 1440x960 and iPad Pro 11 WebKit. No actual WebMCP-capable agent host has been exercised by this execution agent; existing WebMCP evidence is browser-shim based (`CONTRACT_VERIFIED`).
- **Available providers and permissions:** Not exercised. No provider call, GPU job, model download, or live generation was authorized or made. The backend and browser suites pin ComfyUI to `http://127.0.0.1:1` with submission disabled, YuE2 disabled, and OpenAI submission disabled.
- **Baseline checks:** All green at `9ab9b68` - see the M00 acceptance record below.
- **Active milestone:** M09 (reviewed model preparation).
- **Last verified milestone:** M08. M02, M03, M10 and M11 are CONTRACT_VERIFIED pending an actual WebMCP host; M11 also awaits human composition acceptance.
- **External acceptance blockers:** (1) Resolved 2026-09-20: the Reference Asset Compiler is checked out, so M09/M14 are unblocked. Neither is runnable until Framewright can locate a pinned compiler and the browser payload export exists; both are engineering work, not an external blocker. (2) No verified WebMCP-capable browser/agent host - caps M02/M03/M10/M11 at `CONTRACT_VERIFIED` until a real host is exercised. (3) Resolved at M04: the user chose three.js, pinned at 0.186.0 and loaded only when a model is opened.
- **Next action:** M08 is in progress and its groundwork is done. The compiler now offers a `geometry` stage: one reference image in, one candidate mesh out, with the workspace, intake and request written for it, and a capability answer that names which of `legacy-root`, `geometry-environment`, `hunyuan-checkout` and `runners` is missing on a machine without the weights. This studio's gateway can name that studio tree (`Integrations:ReferenceAssetCompiler:StudioTreePath`) so the compiler is told where the weights are rather than left to find them. No inference has been run.
  The route is now wired and proven with a controlled compiler, no GPU involved. A request freezes a route rather than one stage name; preflight refuses unless every step can run and names the step that cannot; each step reads what the one before it wrote; each step keeps its own output and receipt, so an interrupted job resumes at the step it reached instead of asking a GPU to rebuild the same mesh; a step that fails stops the route on the compiler's own reason rather than on a guess from the file system; and the delivery records what each step did, including which were adopted from an interrupted attempt.
  The route has now been run live, end to end, on this workstation's RTX 4090. A reference image of the Trial Lantern became a delivered library model in about ninety seconds: real Hunyuan3D inference, real receipts per step, and a runtime mesh of 9,775 vertices and 19,998 triangles at 0.42 m, inside the compiler's V1 cohort contract. Configuration lives in an untracked `appsettings.Local.json`, so the paths and the commissioning flag reach neither repository.
  Three findings came out of running it rather than reasoning about it. A stage could inherit the service's standard input and wait on it for ever, which stopped the first run dead for nine minutes with no CPU and no output; stages now run with input closed. The raw generator output is not a browser asset -- 2,380,114 triangles and a 198 MB payload, refused by both library gates, correctly -- so the route gained a reduction step. And the reduction gate measures in absolute metres while a generator normalises everything to about two metres tall, so the same lantern was rejected at 1.99 m and passed comfortably at 0.42 m; the route now asks the artist how big the thing is, as a landmark on a person rather than a number of metres, because that is the question a person can actually answer.
  The route now paints as well. A generated mesh has no colour and no UVs, so two more stages sit before the payload: an unwrap that gives it a map without moving a vertex, and Hunyuan3D-Paint conditioned on the same reference the geometry came from, gated on face order, geometry and UV drift beyond 1e-6. The lantern came back painted in about two and a half minutes end to end, 16,436 vertices and 19,998 triangles with two embedded textures. The painter is the one stage judged by what it produced rather than by its exit code, because it can fault during teardown after passing its own gate; no other stage gets that leniency, and a test says why -- a rejected reduction writes its files and then exits nonzero, and treating that as success would deliver a rejection as a finished asset.
  What M08 still wants is the artist's recorded visual acceptance of the delivered prop, and a scene that places it at sensible scale beside a character. M09 is most of the way there as a by-product: the reduction stage reports `mechanical_pass` and never approval, and every receipt it writes says `production_grade: false` and `requires_fixed_view_review: true`. Closing M09 properly means capturing those fixed views in this studio and recording acceptance against the compiler's own ledger, which its `promote`, `cleanup-receipt` and `retopology-receipt` commands exist for and which deliberately cannot be automated away.
  After M08 come M09 and M14, then M12, M16 and M17. The four CONTRACT_VERIFIED milestones wait on an actual WebMCP-capable host, and M11 additionally on recorded human composition acceptance. Rigging routes and clip synthesis, including for non-humanoid creatures, are the compiler's work rather than this repository's; M15 consumes what they produce.

### Milestone status

| Milestone | Status | Evidence / blocker reference |
| --- | --- | --- |
| M00 | VERIFIED | M00 acceptance record below; commit `9ab9b68` |
| M01 | VERIFIED | M01 acceptance record below |
| M02 | VERIFIED | M02 acceptance and actual-host commissioning records below |
| M03 | VERIFIED | M03 acceptance and actual-host commissioning records below |
| M04 | VERIFIED | M04 acceptance record below |
| M05 | VERIFIED | M05 acceptance record below |
| M06 | VERIFIED | M06 acceptance record below |
| M07 | VERIFIED | M07 acceptance record below |
| M08 | VERIFIED | M08 acceptance record below |
| M09 | IN_PROGRESS | Route built and run live; awaiting the artist's recorded verdict |
| M10 | VERIFIED | M10 acceptance and actual-host commissioning records below |
| M11 | CONTRACT_VERIFIED | D/A/H pass; human composition acceptance remains |
| M12 | CONTRACT_VERIFIED | Precise replacement/app persistence pass; declared live evidence remains |
| M13 | VERIFIED | M13 acceptance record below |
| M14 | NOT_STARTED | Compiler checked out; requires M09's recorded acceptance and M13 |
| M15 | VERIFIED | M15 acceptance record below |
| M16 | CONTRACT_VERIFIED | Scene-to-shot/app persistence pass; human visual acceptance remains |
| M17 | NOT_STARTED | None |
| M18 | NOT_STARTED | Portable import/export groundwork passes; full packaged/recovery scenario depends on all milestones |

### M00 acceptance record

```text
Milestone / status / date: M00 / VERIFIED / 2026-09-19
Tested code revision or worktree identity: 9ab9b68 (main), clean working tree, no linked worktrees
Outcome and supported constraints: The existing image/sketch -> feedback -> revision -> library
  save/reopen walking path is reproducible in an isolated workspace and is protected by the
  existing browser suite. No Director Mode or 3D work was started in this milestone.
Implementation surfaces reused/changed: No application source changed. One baseline commit
  (9ab9b68) landed the artist's previously uncommitted working tree at their instruction.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check (tsc --noEmit, eslint --max-warnings 0, prettier)
  - npm --prefix src/storyboard-studio-web run test:e2e (desktop Chromium + iPad Pro 11 WebKit)
  - powershell -File ./scripts/public-release-audit.ps1
Results by evidence class (D/A/H/L/V/P):
  D: 144 backend tests passed, 0 failed, 0 skipped (31 s). Frontend typecheck, lint, and format
     checks clean. Tracked release-content audit passed.
  A: 86 browser journeys passed, 0 failed, 6 intentionally skipped tablet-only exclusions
     (92 discovered) in 3.1 min against a real ASP.NET Core service, real SQLite, and a real
     content-addressed asset root in a disposable temp data root. Covers the protected path:
     sketch composition persistence, version-bound pins and drawn markup, candidate review,
     feedback regeneration freezing the current frame, authority revision pins, and the asset
     library carrying media into shot work.
  H: NOT RUN. No actual WebMCP agent host was exercised.
  L: NOT RUN. No provider, GPU, or worker call was made or authorized.
  V: NOT RUN. No human visual acceptance was required for M00.
  P: NOT RUN. Packaged-runtime smoke deferred; no runtime dependency changed.
Artifact paths, hashes, job IDs, and environment versions: Playwright HTML report at
  src/storyboard-studio-web/playwright-report/ (gitignored). .NET SDK 10.0.203, Node v22.15.0,
  npm 11.11.0, Windows 11 Pro 26200.
Failure/conflict/restart checks: The existing suite already covers stale-save conflicts, archived
  candidate read-only locks, job restart recovery, and proposal staleness. No new ones were added.
Relevant earlier-path regression results: The full backend and browser suites are the earlier-path
  regression set and passed in full.
Checks NOT RUN and why: ./scripts/verify.ps1 in full (it runs npm ci and a Release rebuild; its
  component steps were run individually and passed, and it is reserved for the release-candidate
  gate). Reference Asset Compiler inspection (not present on this workstation). Live provider and
  packaged-runtime checks (out of scope for M00 and unauthorized).
Human approvals actually recorded, where required: The user authorized landing the working tree
  onto main. No provider or artistic approval was required or taken.
Known defects and dependency impact: No reproducible manual failure was found in the baseline; the
  reported remaining manual failures could not be reproduced by these checks. Record a concrete
  reproducer if one recurs.
Permitted deferrals: Full verify.ps1 gate until a release candidate; package smoke until a runtime
  dependency changes.
Checkpoint: 9ab9b68 on main; feature/director-mode created from it.
Next dependency-ready milestone: M01.
```

#### M00 reuse map

| Need | Existing surface to reuse | Notes |
| --- | --- | --- |
| Image canvas, pins, drawn markup | `ShotCanvas` in `src/storyboard-studio-web/src/components/StudioWorkspaces.tsx` | Version-bound comments, durable markup with undo/redo/clear, project-aspect stage, `object-fit: cover` so there is no letterbox margin to exclude. |
| Exact-image review notes | `AssetReviewPins` (`src/storyboard-studio-web/src/components/AssetReviewPins.tsx`) | Already excludes letterboxing by measuring natural aspect; bound to one asset id; drag, keyboard nudge, resolve. |
| Sketch and composition lab | `SketchWorkspace`, `BlockingKit` | Lazy-loaded; revision-checked. |
| Shot workspace shell | `ShotWorkspace` | Rail + canvas + inspector grid; owns tool state, candidate preview, and archived read-only locks. |
| Candidate compare | `CandidateReviewBar`, `ReviewWorkspace` | Compare to latest, peek with `\`, promote and delete. |
| Browser agent tools | `src/storyboard-studio-web/src/webmcp.ts`, `WebMcpStoryboardService`, `/api/webmcp/*` | Six closed-schema shot tools, proposal staging, abort-signal lifecycle, honest unsupported-browser fallback. |
| Server domain services | `StudioRepository`, `StudioDbContext`, `AssetStore`, `GenerationOrchestrator`, `GenerationJobLeaseService` | Project scoping, optimistic concurrency, content-addressed storage, owned jobs. |
| Test isolation | `StudioApiFactory` (temp data root, providers at `127.0.0.1:1`), `scripts/run-e2e-server.ps1` (temp data root, disabled submission) | Confirms tests cannot reach production paths or providers. |
| Fixture set | Seeded demo project (6+ shots, authorities) plus the 1x1 PNG data-URL fixture in `e2e/studio.spec.ts` | Add an asymmetric known-dimension 3D fixture before M04. |

### M01 acceptance record

```text
Milestone / status / date: M01 / VERIFIED / 2026-09-19
Tested code revision or worktree identity: feature/director-mode, working tree at the commit
  recorded below (branched from 9ab9b68)
Outcome and supported constraints: The Shot workspace has a full-view director mode. The rails,
  context bar, version rail, inspector, and video endpoint strip step aside; the frame takes the
  whole workstation at the exact delivery aspect; tools, the candidate review bar, Compare, and
  Exit stay in reach. Only the shell restyles - the workspace and canvas are never remounted - so
  the displayed revision, pins, unsaved markup, and unsaved inspector drafts survive the round
  trip. Escape leaves full view only after any open modal has had its own press, and leaving the
  Shot workspace always leaves full view. No new dependency, no schema change, no server change.
Implementation surfaces reused/changed:
  - src/storyboard-studio-web/src/App.tsx: directorMode state, shell class, Escape handling,
    exit-on-workspace-change, prop wiring.
  - src/storyboard-studio-web/src/components/StudioWorkspaces.tsx: ShotWorkspace props, the
    always-present .canvas-stage wrapper, the toolbar toggle, and --project-aspect-number.
  - src/storyboard-studio-web/src/studio.css: the Director Mode block plus one narrow-toolbar
    rule so Compare is not squeezed out on a tablet.
  - Reused unchanged: ShotCanvas, its comment pins and durable markup, CandidateReviewBar, the
    archived-candidate read-only locks, and the existing revision-bound comment API.
Commands and checks actually run:
  - npm --prefix src/storyboard-studio-web run check
  - npx playwright test studio.spec.ts -g "director mode gives the frame" (desktop, then tablet)
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
  - dotnet test Framewright.slnx --nologo
Results by evidence class (D/A/H/L/V/P):
  D: Frontend typecheck, lint, and format checks clean. 144 backend tests passed, 0 failed.
  A: 88 browser journeys passed, 0 failed, 6 intentionally skipped (94 discovered) in 3.2 min
     against the real service and real persistence in a disposable temp data root. That is the
     86-journey M00 baseline plus the new desktop and tablet director-mode journeys, with no
     regression and no change in the skip set.
  H: NOT RUN. M01 needs no agent host.
  L: NOT RUN. No provider was contacted; the second candidate came from the no-network local
     proof adapter.
  V: Desktop Chromium and iPad-sized WebKit full-view captures were inspected by the execution
     agent at artifacts/director-mode/ (gitignored). This is AI inspection, not recorded human
     visual acceptance.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: The new journey proves the archived-candidate locks still apply
  in full view (draw and comment disabled, live-head note absent from the archived revision), that
  a modal inside full view consumes its own Escape, and that a reload reopens the saved pin,
  markup, and comment version while the never-saved intent draft is correctly gone.
Relevant earlier-path regression results: The full backend and browser suites passed in full.
Checks NOT RUN and why: ./scripts/verify.ps1 in full (reserved for the release-candidate gate;
  its component steps passed individually). Physical stylus and screen-reader passes, which
  automation cannot establish.
Human approvals actually recorded, where required: None required. No provider call, no
  ratification, no destructive action.
Known defects and dependency impact: None found. On a tablet the frame is already full width, so
  full view buys chrome-free height rather than more pixels; the journey asserts that honestly
  rather than pretending the frame grows.
Permitted deferrals: Rich brush libraries, custom full-view layouts, and any new conversational
  UI, all explicitly deferred by M01.
Bug-detection check: The new journey was run against two deliberately broken builds. Stretching
  the stage to ignore the delivery aspect failed the aspect guard (expected 2.4, received 1.88);
  remounting the shot workspace on entering full view failed the unsaved-draft guard. Both
  sabotages were reverted before the recorded run.
Checkpoint: see the M01 commit on feature/director-mode.
Next dependency-ready milestone: M02.
```

### M02 acceptance record

```text
Milestone / status / date: M02 / VERIFIED / 2026-09-21
Tested code revision or worktree identity: public main including the actual-host commissioning checkpoint
Outcome and supported constraints: Browser tools can read the exact state the artist has on
  screen and the picture that goes with it, and cannot mix the two up. `get_director_context`
  returns one bounded, versioned packet - project, subject, live and displayed revision, archived
  flag, camera, duration, view flags, visual, open annotations with normalized coordinates,
  constraints, authorities, available actions - plus a `stateToken`. `observe_current_frame`
  resolves that revision's picture only while the token still matches. The packet follows an
  archived candidate preview rather than the live head, and omits `propose_shot_revision` from
  available actions while an archived revision is displayed, matching the read-only lock the
  artist sees. Codex's in-app browser then discovered the page-defined tools and read the same
  selected shot, displayed revision, notes, authorities, state token, and frame through the real
  host path, advancing this milestone from contract evidence to VERIFIED.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/WebMcpStoryboardService.cs: DirectorContextAsync,
    DirectorObservationAsync, ResolveViewAsync, and the state-token digest.
  - src/StoryboardStudio.Api/Program.cs: GET /api/webmcp/director/context and
    GET /api/webmcp/director/observation, same-origin and project-scoped like the rest.
  - src/storyboard-studio-web/src/webmcp.ts, api.ts, types.ts: two closed-schema read-only tools
    (eight in total) behind the existing AbortController registration lifecycle.
  - src/storyboard-studio-web/src/App.tsx and components/StudioWorkspaces.tsx: the Shot workspace
    publishes the revision it is displaying, so the tools follow the artist rather than the head.
  - Reused unchanged: the envelope shape, activity log, project query filters, continuity and
    proposal services, and the existing /mcp workstation endpoint.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check
  - npx playwright test webmcp.spec.ts --project=desktop
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
Results by evidence class (D/A/H/L/V/P):
  D: 147 backend tests passed, 0 failed (144 baseline plus three director-context contract tests).
     Frontend typecheck, lint, and format checks clean.
  A: 92 browser journeys passed, 0 failed, 6 intentionally skipped (98 discovered) against the
     real service and real persistence in a disposable temp data root. Two new agent journeys
     cover the live head with staleness and refresh, and an archived preview with a cross-revision
     token refusal.
  H: PASSED on 2026-09-21 with Codex's in-app browser against the running local Framewright app.
     The host discovered all ten tools; `get_director_context` and `observe_current_frame` agreed
     on LAB-010 v16, then a navigation produced LAB-020 v9 with its two open notes and a new token.
     The host exposed no semantic browser/WebMCP API version, so none is invented; access was
     same-origin localhost through the Codex IAB host.
  L: NOT RUN. No provider was contacted. The second candidate used by the archived-preview
     journey came from the no-network local proof adapter.
  V: NOT RUN. No human visual acceptance was required for M02.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: An edit after the context was read refuses the paired observation
  with `stale_context` and `retryable: true`, and the refusal deliberately carries no replacement
  token so it cannot be used to skip the re-read it asks for. A token read while previewing an
  archived revision is refused against the live head. Unknown shots, revisions above the live
  head, malformed tokens, and a shot belonging to another project are each refused by code.
Relevant earlier-path regression results: The full backend and browser suites passed in full,
  including the M01 director-mode journeys.
Checks NOT RUN and why: Physical stylus and screen-reader passes.
Human approvals actually recorded, where required: None required. The new tools are read-only,
  create nothing, and dispatch nothing.
Suite stability observed: one intermediate full-matrix run failed two pre-existing authority
  journeys (studio.spec.ts:1637 tablet, studio.spec.ts:1749 desktop) that this change does not
  touch. Both passed in isolation on desktop and tablet and in the recorded full run. Treated as
  load-sensitive flakiness in the shared serial database, not a regression, and recorded here
  rather than hidden. Re-check if either recurs.
Known defects and dependency impact: The workspace publishes its displayed revision one commit
  after a candidate click, so an agent reading in that instant sees the previous revision. The
  state token makes that visible rather than silent - the paired observation is refused - and the
  journey polls rather than assuming lockstep. M03 must not treat a context read as proof that
  the artist is still on that revision; proposals keep validating expected version server-side.
Permitted deferrals: Additional hosts, and any capture of the browser viewport itself. The
  observation resolves the exact frame asset instead, which has no letterbox ambiguity.
Bug-detection check: With the state token computed without open annotations, the backend
  staleness test failed as intended (Assert.False failure on the refusal) and passed again once
  the sabotage was reverted.
Checkpoint: see the M02 commit on feature/director-mode.
Next dependency-ready milestone: M03, which the goal allows to proceed on the M02 contract.
```

### M03 acceptance record

```text
Milestone / status / date: M03 / VERIFIED / 2026-09-21
Tested code revision or worktree identity: public main including the actual-host commissioning checkpoint
Outcome and supported constraints: A browser agent can stage a bounded edit proposal against the
  exact view it read, and the artist decides what happens to it. A proposal now carries the
  director-context state token it was based on, the pinned notes it targets, and the shot rules it
  promises to preserve. The service refuses a proposal built on a view that has moved
  (stale_context) and refuses any preserved constraint the shot and its attached authorities do
  not actually hold (invalid_preserved_constraints), so an agent cannot write blind or reassure
  the artist with a rule nothing enforces. States are distinct and ordered: Pending, then Accepted
  or Rejected, then Applied. Rejecting changes nothing. Applying is the artist's action, happens
  once, refuses a shot that moved on, and opens frozen instructions - direction, rationale,
  preserved constraints, targeted notes with normalized coordinates - in the existing revision
  surface. Applying again replays that one application. Nothing in this path authorizes a
  provider: the instructions carry generationAuthorized: false and the existing explicit Generate
  action remains the only thing that spends GPU time or money. The actual Codex host staged a
  two-note LAB-020 proposal, displayed it in Agent Activity, and the artist-facing Reject action
  left the shot at v9 with the same notes and no job, advancing this milestone to VERIFIED.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/WebMcpStoryboardService.cs: proposal request gains
    PreservedConstraints and ObservedStateToken with validation, plus ApplyAsync and its frozen
    instruction packet.
  - src/StoryboardStudio.Api/Program.cs: POST /api/webmcp/proposals/{id}/apply.
  - src/StoryboardStudio.Api/Persistence: ShotRevisionProposals gains AppliedAt,
    ObservedStateToken, and PreservedConstraintsJson behind migration
    20260919-director-proposal-apply-v10, additive so existing staged proposals survive.
  - src/storyboard-studio-web: propose tool schema, apply client call, proposal card scope and
    apply/reopen actions, and the failure line that keeps a refusal on the card.
  - Reused unchanged: the existing FeedbackRegenerationDialog revision surface, the Codex
    implementation-request path it already had, comment/pin storage, and the review gates.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - dotnet test --filter FullyQualifiedName~SchemaMigrationTests
  - npm --prefix src/storyboard-studio-web run check
  - npx playwright test webmcp.spec.ts --project=desktop
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
Results by evidence class (D/A/H/L/V/P):
  D: 151 backend tests passed, 0 failed (147 before this milestone plus three proposal-scope and
     apply contract tests and one schema-upgrade test). Frontend typecheck, lint, and format clean.
  A: 94 browser journeys passed, 0 failed, 6 intentionally skipped (100 discovered) against the
     real service and real persistence in a disposable temp data root. The new journey pins a
     note, has the agent read context and propose against it, rejects one proposal without any
     change, accepts and applies another, checks the revision surface opens carrying the preserved
     rule and the marked region, reopens it as a replay, and asserts the shot version and the
     durable job ledger are untouched throughout.
  H: PASSED on 2026-09-21 through Codex's in-app browser. The real host staged a bounded proposal
     against LAB-020 v9 and both visible notes; the UI showed the proposal, rejection changed no
     shot state, and a fresh context read returned the same v9 and note set.
  L: NOT RUN. No provider was contacted. Applying a proposal creates no job, which the journey
     asserts against the durable ledger.
  V: NOT RUN. No human visual acceptance was required for M03.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: Applying before accepting is refused (proposal_not_accepted);
  applying a rejected proposal is refused (proposal_rejected); applying after the shot moved is
  refused (stale_shot) and leaves the proposal Accepted rather than Applied; applying twice
  replays the same AppliedAt rather than doubling; a proposal built on a stale context or an
  invented constraint leaves no record at all. Schema upgrade from a database that recorded v9
  restores the three columns and preserves the proposal staged before the upgrade.
Relevant earlier-path regression results: The full backend and browser suites passed in full,
  including the M01 director-mode journeys and the M02 context journeys.
Checks NOT RUN and why: Live image generation from an applied direction - the milestone explicitly
  forbids claiming it, and the journey only proves the instructions reach the revision surface
  unstarted. Physical stylus and screen-reader passes.
Human approvals actually recorded, where required: None required. Accept and apply are simulated
  artist clicks inside an isolated test workspace, not artistic ratification of real work.
Known defects and dependency impact: None found. Note that a proposal binds notes on the live head
  only; proposing against an archived preview is not offered, matching M02's available actions.
Permitted deferrals: Autonomous ratification, provider dispatch, and unattended acceptance, all
  explicitly deferred by M03.
Bug-detection check: With the once-only guard removed from ApplyAsync, the apply test failed as
  intended on the replayed AppliedAt comparison, and passed again once the sabotage was reverted.
Checkpoint: see the M03 commit on feature/director-mode.
Next dependency-ready milestone: M04, which depends only on M00 but needs the user's decision on a
  browser 3D renderer dependency before any code is written.
```

### M04 acceptance record

```text
Milestone / status / date: M04 / VERIFIED / 2026-09-19
Tested code revision or worktree identity: feature/director-mode, working tree at the M04 commit
Outcome and supported constraints: A self-contained GLB can be imported, validated, measured, and
  inspected in an isolated 3D surface. Validation is server-side and happens before any bytes
  reach a graphics context: container, chunk table, glTF version, required extensions, external
  URIs, geometry, and seven resource ceilings. A refusal names its reason and leaves no asset
  record behind. The reported dimensions are scene-space, computed through the node tree rather
  than read off raw accessors. The viewer orbits an inspection camera, frames and resets the view,
  lists materials and geometry statistics, releases its GPU context when the model changes or the
  surface closes, and falls back honestly when WebGL is unavailable. The supported subset is
  documented in docs/3D_CONVENTIONS.md and reported with every profile, so the artist reads the
  real ceiling rather than a number in a document that may have drifted.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/GlbModelInspector.cs: the container/subset/ceiling gate and
    the scene-space bounds computation.
  - src/StoryboardStudio.Api/Services/AssetStore.cs: ImportModelAsync and ModelProfileAsync, using
    the existing staging, hashing, content-addressed storage, dedupe, and audit paths.
  - src/StoryboardStudio.Api/Program.cs: POST /api/assets/models and
    GET /api/assets/{id}/model-profile, with their own request-size ceiling.
  - src/StoryboardStudio.Core/StudioContracts.cs: AssetKind.Model plus the profile contracts.
  - src/storyboard-studio-web: ModelViewer (three.js), ModelInspectionWorkspace, a Models smart
    view, and the model branch of the existing import control.
  - fixtures/glb/: the known-dimension asymmetric fixtures and their builder. The folder is
    named glb rather than models so it does not fall under the gitignore rule that keeps model
    weights out of the repository.
  - Reused unchanged: the asset library, content-addressed store, project scoping, and the
    existing import control the artist already uses for images, audio, and video.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check
  - npm --prefix src/storyboard-studio-web run build
  - npx playwright test models.spec.ts (desktop and tablet)
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
Results by evidence class (D/A/H/L/V/P):
  D: 164 backend tests passed, 0 failed (151 before this milestone plus ten inspector contract
     tests and three import/profile API tests). Frontend typecheck, lint, and format clean.
  A: 100 browser journeys passed, 0 failed, 6 intentionally skipped (106 discovered) against the
     real service and real persistence in a disposable temp data root. Three new model journeys
     import the known fixture and check its measurements, materials, and supported subset; open
     the real 3D view, orbit it by keyboard, and confirm the stored model is unchanged; switch
     between two models with different extents and confirm exactly one drawing context survives;
     and refuse a file that is not a GLB with a reason, leaving no model asset behind. The
     bounding box three.js actually loaded is asserted to match the service's measurement axis
     for axis.
  H: NOT REQUIRED for M04. No agent host is involved in importing or inspecting a model.
  L: NOT RUN. No provider, GPU worker, or network fetch is involved; the initial route refuses
     external references outright.
  V: The desktop capture at artifacts/director-mode/desktop-model-viewer.png was inspected by the
     execution agent. This is AI inspection, not recorded human visual acceptance. The orientation
     claim does not rest on it: the browser journey asserts that the bounding box three.js
     actually loaded matches the bounds the service measured, axis for axis.
  P: NOT RUN as a packaged-runtime check, but the production bundle was built and measured. three
     .js occupies its own 609 KB (153 KB gzipped) chunk that loads only when a model is opened;
     the shared vendor chunk is unchanged at 209 KB (66.5 KB gzipped), so start-up cost for
     artists who never open a model is unchanged.
Failure/conflict/restart checks: Corrupt magic, wrong GLB version, a header length that disagrees
  with the file, truncation, external buffer or image URIs, an unsupported required extension,
  missing accessor bounds, a document with no primitives, and each resource ceiling are all
  refused by code with a specific reason. A refused import leaves no asset record; re-importing
  identical bytes returns the same asset rather than a duplicate. A model is rejected as a shot
  placement rather than being mislabelled with an audio role.
Relevant earlier-path regression results: The full backend and browser suites passed in full,
  including the M01 director-mode journeys and the M02/M03 agent journeys.
Checks NOT RUN and why: ./scripts/verify.ps1 in full (release-candidate gate; component steps
  passed individually). Human visual acceptance (not required by M04). A real WebGL-less browser
  (the fallback path is implemented and typed but was not exercised by a journey; Playwright's
  browsers both provide WebGL).
Human approvals actually recorded, where required: The user chose three.js over the alternatives
  before any 3D code was written, and asked to be consulted first. No other approval was required.
Known defects and dependency impact: The no-WebGL fallback is untested by automation. Models are
  library assets only: they have no revision stack, no shot placement, and no scene yet, which is
  exactly what M05 and M06 add.
Permitted deferrals: Other file formats, advanced shading, sculpting, and automatic repair, all
  explicitly deferred by M04.
Bug-detection check: With the inspector ignoring node transforms, the fixture test failed on the
  dimensions (expected [2, 1, 0.75], got [2, 1, 0.5]) and the mirrored-import test failed on the
  bounds, then both passed again once the sabotage was reverted.
Checkpoint: see the M04 commit on feature/director-mode.
Next dependency-ready milestone: M05.
```

### M05 acceptance record

```text
Milestone / status / date: M05 / VERIFIED / 2026-09-19
Tested code revision or worktree identity: feature/director-mode, working tree at the M05 commit
Outcome and supported constraints: A model is a reusable library record rather than a one-off
  upload. It carries a revision stack, editable organisation (name, tags, notes, collection),
  provenance, and a non-destructive archive, and every one of those survives the application
  closing and reopening. Revisions never overwrite each other: each keeps its own stored file,
  measurements, and materials, and selecting an earlier revision as current is an ordinary
  reversible move. A revision must be the same kind of asset it revises, so a picture cannot enter
  a model's history. Identical bytes are stored once on disk but never shared across projects: a
  second project importing the same model gets its own record and cannot reach the first
  project's.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/AssetStore.cs: ListRevisionsAsync and AddRevisionAsync now
    accept models, with a kind-match guard; PromoteRevisionAsync was already kind-agnostic.
  - src/storyboard-studio-web/src/components/ModelInspectionWorkspace.tsx: revision stack with
    per-revision inspection, add-revision import, make-current, editable library details, and
    archive/restore.
  - Reused unchanged: the existing asset revision machinery built for images, the content-
    addressed store, project query filters, the metadata and archive endpoints, and the M04
    import and profile routes.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check (exit code checked directly, not through a pipe)
  - npx playwright test models.spec.ts --project=desktop
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
Results by evidence class (D/A/H/L/V/P):
  D: 168 backend tests passed, 0 failed (164 before this milestone plus four model-library tests).
     Frontend typecheck, lint, and format checks clean.
  A: 102 browser journeys passed, 0 failed, 6 intentionally skipped (108 discovered) against the
     real service and real persistence in a disposable temp data root. The new journey imports a
     model of its own, renames and tags it, adds a second revision, moves between revisions and
     confirms each reports its own geometry, makes the earlier one current, reloads the page and
     confirms the saved state reopens, then archives and restores it.
  H: NOT REQUIRED for M05. No agent host is involved in library organisation.
  L: NOT RUN. No provider is involved.
  V: NOT RUN. No human visual acceptance was required for M05.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: The restart check is the centrepiece - a model is imported,
  renamed and tagged, given a second revision, and then the whole service is disposed and
  reconstructed on the same data root, where the exact revision, its metadata, its provenance
  note, and its own geometry and materials all reopen unchanged. Adding an image as a revision of
  a model is refused and leaves the stack untouched. An archived model still resolves its bytes
  and its profile. Another project cannot read the model's profile or its revision stack.
Relevant earlier-path regression results: The full backend and browser suites passed in full,
  including the existing image revision stacks, which share this machinery.
Checks NOT RUN and why: ./scripts/verify.ps1 in full (release-candidate gate; component steps
  passed individually). Human visual acceptance (not required). A real WebGL-less browser.
Human approvals actually recorded, where required: None required. No provider, no ratification,
  no destructive action; archive is reversible by design.
Known defects and dependency impact: None found. Models still have no scene placement, which is
  what M06 adds.
Permitted deferrals: Bulk import, automated tagging, and elaborate thumbnails, all explicitly
  deferred by M05.
Bug-detection check: With the model profile resolving the family's current revision instead of the
  requested one, the restart test failed as intended (expected [2, 1, 0.75], got [1, 2, 0.45]) and
  passed again once the sabotage was reverted.
Checkpoint: see the M05 commit on feature/director-mode.
Next dependency-ready milestone: M06. M07 and M08 are deferred at the user's request: they intend
  to supply an existing generation pipeline, so the image-to-model route will be designed against
  that rather than invented here.
```

### M06 acceptance record

```text
Milestone / status / date: M06 / VERIFIED / 2026-09-19
Tested code revision or worktree identity: feature/director-mode, working tree at the M06 commits
Outcome and supported constraints: A project holds small editable scenes. A scene contains
  distinct instances, each pinned to an exact model revision, with its own name and transform, an
  orbit camera, and basic key/ambient lighting. Two instances of one model move independently
  because the transform lives on the instance. The artist adds, selects, renames, moves, rotates,
  scales, duplicates, and removes instances through explicit numeric controls, and can also select
  by clicking in the viewport; dragging orbits the camera and never changes the selection or an
  object. The whole scene saves as one unit against the version it was read at, so a save built on
  a stale view is refused and newer work survives. An instance whose model becomes unavailable
  keeps its identity and transform and draws as a placeholder rather than vanishing. Removing an
  instance removes the placement only; the library asset is untouched, and editing library
  organisation moves nothing.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/SceneService.cs: list, get, create, and a validate-then-write
    save with optimistic concurrency.
  - src/StoryboardStudio.Api/Persistence: Scenes and SceneInstances behind migration
    20260919-model-scenes-v11.
  - src/StoryboardStudio.Api/Program.cs: the /api/scenes routes.
  - src/storyboard-studio-web: a Scene workspace on the rail, SceneViewport (three.js), and the
    scene client API.
  - Reused unchanged: the model library and its content route, project query filters, the audit
    trail, the workspace shell, and the lazily loaded three.js chunk from M04.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check (exit code checked directly)
  - npx playwright test scenes.spec.ts (desktop and tablet)
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
Results by evidence class (D/A/H/L/V/P):
  D: 174 backend tests passed, 0 failed (168 before this milestone plus six scene contract tests).
     Frontend typecheck, lint, and format checks clean.
  A: 106 browser journeys passed, 0 failed, 6 intentionally skipped (112 discovered) against the
     real service and real persistence in a disposable temp data root. The new journeys place two
     instances of one model, move one of them only, save, reload, and confirm both reopen with
     their own transforms behind a single asset id - including that both really reached the scene
     graph, not only the API - and separately confirm that a save built on a stale view is refused
     while the model stays in the library untouched.
  H: NOT REQUIRED for M06. No agent host is involved in placing objects.
  L: NOT RUN. No provider is involved.
  V: The desktop capture at artifacts/director-mode/desktop-scene.png was inspected by the
     execution agent. That inspection is what revealed an object placed away from the origin was
     simply off screen, which is why the viewport now has a Frame all control. This is AI
     inspection, not recorded human visual acceptance.
  P: NOT RUN. No runtime dependency changed; three.js still loads only when 3D is opened.
Failure/conflict/restart checks: A stale save is refused and the newer work is intact afterwards.
  Zero scale, positions outside the working volume, an image used as a model, duplicate instance
  ids, and another project's scene are each refused, and a refused save leaves the stored scene at
  its previous version with its previous instances. A scene reopens after a real service restart
  with the correct revisions, transforms, camera, and lighting. Deleting the stored model file
  leaves the instance present, marked unavailable, and drawn as a placeholder.
Relevant earlier-path regression results: The full backend and browser suites passed in full.
Checks NOT RUN and why: ./scripts/verify.ps1 in full (release-candidate gate; component steps
  passed individually). Human visual acceptance (not required by M06). A real WebGL-less browser;
  the viewport has a fallback that keeps the object list and placement controls usable, but no
  journey exercises it.
Human approvals actually recorded, where required: None required. No provider, no ratification,
  no destructive action.
Known defects and dependency impact: None found. Scenes have no snapshots or shot binding yet,
  which M16 adds, and no per-object annotation, which M10 adds.
Permitted deferrals: Advanced lighting, collision, snapping, large-world editing, and instancing
  optimisation, all explicitly deferred by M06. Gizmos are optional in the milestone and are not
  implemented; placement is numeric and explicit.
Bug-detection check: With the version check removed from the save path, the stale-save test failed
  as intended (expected Conflict, got OK) and passed again once the sabotage was reverted.
Checkpoint: see the M06 commits on feature/director-mode.
Next dependency-ready milestone: M10. M07 and M08 remain deferred pending the user's generation
  pipeline, and M09/M14 remain blocked by the absent Reference Asset Compiler, so the open runway
  is M10, M11, M13 and M15.
```

### M10 acceptance record

```text
Milestone / status / date: M10 / VERIFIED / 2026-09-21
Tested code revision or worktree identity: public main including the actual-host commissioning checkpoint
Outcome and supported constraints: Director Mode now reaches inside a scene, where two identical
  props are two different objects. A note is anchored in one instance's own local space against
  the exact model revision it was measured on, together with the view it was placed from; pinning
  that instance to a different revision makes the note explicitly stale rather than moving it or
  deleting it. The scene director context names the selected object, its revision and transform,
  the camera, its open notes, and every other object by identity, with a state token bound to all
  of it. A proposal must carry that token, must name one instance, and must name at least one of
  position, rotation, or scale; one built on a view that has since moved is refused, as is one
  applied after the scene changed. Applying is the artist's action and writes nothing: it puts the
  change into working state for that one object, and the transform reaches the scene only through
  the ordinary validated save. Codex's in-app browser then completed that same bounded path against
  a disposable current build: exact selected object and view, proposal, apply, save, cold reopen,
  and exact reread. That actual-host result advances M10 to VERIFIED.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/SceneDirectionService.cs: annotations, the scene context
    packet and its token, and single-instance proposals with accept, reject, and a single apply.
  - src/StoryboardStudio.Api/Persistence: SceneAnnotations and SceneProposals behind migration
    20260919-scene-direction-v12.
  - src/storyboard-studio-web: note placement by raycast into an object's local space, a notes and
    proposals panel in the scene inspector, and a ninth browser tool (propose_scene_edit).
    get_director_context now covers both the shot and the scene surface.
  - Reused unchanged: the M06 scene save as the only write path, the existing WebMCP envelope,
    registration lifecycle and activity log, and project query filters.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check (exit code checked directly)
  - npx playwright test scenes.spec.ts --project=desktop
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
Results by evidence class (D/A/H/L/V/P):
  D: 179 backend tests passed, 0 failed (174 before this milestone plus five scene direction
     tests). Frontend typecheck, lint, and format checks clean.
  A: 110 browser journeys passed, 0 failed, 6 intentionally skipped (116 discovered) against the
     real service and real persistence in a disposable temp data root. The new journeys have an
     agent read the context for one of two identical props, stage a rotation for that instance,
     and confirm after the artist applies and saves that the other prop never moved; and confirm
     the scene is fully usable in a browser with no WebMCP at all.
  H: PASSED on 2026-09-21 through Codex's in-app browser. A selected Lantern marker was read at
     scene v2 and time 0, proposed at rotation Y 1.5708, applied only to that instance, saved as
     v3, reopened cold, selected again, and reread with the saved transform and camera intact.
  L: NOT RUN. No provider is involved; proposals create no jobs.
  V: NOT RUN as human acceptance.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: A proposal with a token from a view that has since moved is
  refused as stale_scene and nothing is staged. An unsaved camera or object draft is refused as
  unsaved_scene for both context reads and proposals, and a token read at another playhead time is
  refused as stale_context. A blind token, an empty proposal, a collapsed
  scale, and an object from another scene are each refused. Applying before accepting, and
  applying a rejected proposal, are refused; applying twice replays the single application.
  Annotations survive and report staleness after a revision swap.
Relevant earlier-path regression results: The full backend and browser suites passed in full,
  including the M01 director-mode journeys, the M02/M03 shot agent journeys, and the M06 scene
  journeys.
Checks NOT RUN and why: Human visual acceptance, which M10 does not require.
Human approvals actually recorded, where required: None required. Accept and apply are simulated
  artist clicks in an isolated test workspace, not artistic ratification.
Known defects and dependency impact: Actual-host commissioning found that an unsaved orbit or
  transform could be represented by the older persisted context. The browser bridge now refuses
  dirty scenes, carries the visible playhead, and binds scene proposal tokens to that time. Notes
  remain anchored to a point rather than a named surface feature, which M10 permits; arbitrary
  screen-to-geometry reconstruction stays out of scope.
Permitted deferrals: Arbitrary screen-to-geometry reconstruction and free-form mesh editing, both
  explicitly deferred by M10.
Bug-detection check: With notes reporting Stale: false unconditionally, the revision-binding test
  failed as intended and passed again once the sabotage was reverted.
Two defects the browser journeys found, both fixed: staging a scene proposal opened the agent
  panel directly over the scene controls the artist needs in order to act on it; and creating a
  new scene left the previous scene's notes and proposals on screen against the new scene's
  objects.
Checkpoint: see the M10 commits on feature/director-mode.
Next dependency-ready milestone: M11.
```

### M11 acceptance record

```text
Milestone / status / date: M11 / CONTRACT_VERIFIED / 2026-09-21
Tested code revision or worktree identity: public main including the actual-host commissioning checkpoint
Outcome and supported constraints: A reference picture now becomes a construction plan the artist
  can argue with before anything is built. The plan names each object the reference seems to call
  for, gives it a role, matches the ones the library already holds, describes simple stand-in
  geometry for the rest, states a per-object confidence and motion intent, proposes a camera, and
  lists what it assumed and what it could not see. It is bound to the exact reference bytes it was
  read from, so a plan built on a picture the artist has since replaced is refused rather than
  applied to something nobody looked at. Plans are bounded at twelve objects, and each object is
  either a library match or a placeholder, never both and never neither. Approving is the artist's
  move and the only thing that builds: it creates a new scene, so no existing work can be
  overwritten, out of placeholders and models the project already holds. Approving twice hands back
  the same scene. From there the blockout is an ordinary scene: one object's placement and the
  camera framing are corrected independently through the ordinary validated save, and after
  reopening every object still names the role and plan it came from, with the reference id and the
  assumptions still readable. No provider job is created by proposing or by approving.
  Codex's in-app browser has now exercised proposal, visible review, artist build, independent
  placement and camera corrections, save, and cold reopen through the actual host. Status remains
  CONTRACT_VERIFIED for one stated ceiling: recorded human composition acceptance cannot be
  manufactured from the one-pixel commissioning fixture.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/SceneBlockoutService.cs: plans, their bounded items, the
    reference binding, rejection, and the single approval that builds.
  - src/StoryboardStudio.Api/Persistence: SceneBlockoutPlans and SceneBlockoutItems, and a
    SceneInstances rebuild so an object can be a placeholder rather than a model revision, behind
    migration 20260919-scene-blockout-v13.
  - src/storyboard-studio-web: a reference picker and a plan review panel in the scene inspector,
    stand-in geometry drawn as itself in the viewport, and a tenth browser tool
    (propose_scene_blockout). The scene director context now names the selected reference and its
    content hash.
  - Reused unchanged: the M06 scene save as the only edit path, the M10 notes and proposals, the
    existing WebMCP envelope, registration lifecycle and activity log, and project query filters.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check (exit code checked directly)
  - npx playwright test scenes.spec.ts (desktop and tablet)
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
Results by evidence class (D/A/H/L/V/P):
  D: 184 backend tests passed, 0 failed (179 before this milestone plus five blockout tests).
     Frontend typecheck, lint, and format checks clean.
  A: 114 browser journeys passed, 0 failed, 6 intentionally skipped (120 discovered) against the
     real service and real persistence in a disposable temp data root. The new journeys have an
     agent read the selected reference into a three-object plan, confirm the open scene is still
     empty and still version 1, build the blockout, confirm two of the three objects are stand-ins
     and the third is the library model, correct one placement, save, reopen the studio cold, and
     find the correction, the stand-in geometry, and the plan the scene came from still there; and
     confirm a rejected plan builds nothing at all.
  H: PASSED on 2026-09-21 through Codex's in-app browser. The host staged a three-object plan bound
     to an exact selected reference hash; the UI showed assumptions and uncertainty, the artist
     action built a separate scene, placement and inspection camera were corrected, and the scene
     survived save and cold reopen with all three object identities and plan lineage intact.
  L: NOT RUN. No provider is involved, which is asserted rather than assumed: proposing and
     approving leave the job and manifest tables empty.
  V: NOT RUN, and this is the milestone's second ceiling. M11 asks for recorded human composition
     acceptance; no human has accepted a composition, and that cannot be simulated.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: A plan whose observed reference hash does not match the stored
  bytes is refused as stale_reference. An empty plan, a thirteen-object plan, an object with both a
  library match and a stand-in, an object with neither, an unknown shape, a collapsed size, a match
  that is not a model in this project, and an unstated confidence are each refused, and none of
  them leave a plan or a scene behind. Replaying an idempotency key returns the same plan rather
  than a second one. Approving twice returns the one scene already built; a rejected plan cannot be
  approved, and an approved plan cannot be withdrawn. Another project can neither read nor approve
  this project's plan. The blockout and its corrections survive closing and reopening the
  application on the same data root.
Relevant earlier-path regression results: The full backend and browser suites passed in full,
  including the M01 director-mode journeys, the M02/M03 shot agent journeys, and the M06 and M10
  scene journeys.
Checks NOT RUN and why: Human composition acceptance (requires the artist and a meaningful
  reference rather than the one-pixel host-contract fixture).
Human approvals actually recorded, where required: None. The approvals in the journeys are
  simulated artist clicks in an isolated test workspace and are recorded as such, not as
  composition acceptance.
Known defects and dependency impact: None found. Placeholders are the four simple shapes the
  viewport can draw without loading anything; exact reconstruction of hidden geometry and automatic
  physical inference stay out of scope, as the milestone permits.
Permitted deferrals: Exact reconstruction of hidden geometry, automatic physical inference, and
  unlimited object batches, all explicitly deferred by M11.
Bug-detection check: With the save dropping each object's stand-in geometry, the blockout journey
  failed as intended and passed again once that was reverted.
Three defects the browser journeys found, all fixed: the shell assembled the agent's context packet
  while it rendered, not when a tool asked, so an agent could be told about the selection before
  last, which M10 had got away with by luck; opening any ordinary scene asked the service for a
  construction plan it had never had, logging a 404 for a perfectly normal scene; and a reference
  fixture shared its bytes with another journey's upload, which the content-addressed store
  correctly treated as the same asset.
Checkpoint: see the M11 commits on feature/director-mode.
Next dependency-ready milestone: M13.
```

### M13 acceptance record

```text
Milestone / status / date: M13 / VERIFIED / 2026-09-19
Tested code revision or worktree identity: feature/director-mode, working tree at the M13 commits
Outcome and supported constraints: A rigged character can be imported, stored, and inspected before
  anything trusts a generated rig. Mesh, skeleton, bind data, and materials all survive closing and
  reopening the application, because they are re-read from the same stored bytes rather than cached
  as claims. The bone hierarchy comes from the file's node tree, not from bone names, and each bone
  reports its own rest transform and the rest position that composes down that tree. Bind matrices
  must exist, match the joint count, and be finite. Every skinned vertex's joints and weights are
  read out of the binary chunk and must be finite, non-negative, weighted to bones the skin has, and
  sum to one; above 250,000 skinned vertices the weights are reported unchecked rather than assumed
  sound. A rig is called animation-ready only when it matches the one documented body profile,
  humanoid-a, and passes every check: a skeleton whose bones are named something else is an unknown
  skeleton, never an almost-humanoid, and it is not posed at all. A static prop has no skeleton,
  which is an ordinary answer rather than a fault. A pose rotates named bones away from the rest
  pose and reports where every joint lands; it is calculated from the stored bytes, stores nothing,
  draws nothing, and is bound to the exact model revision, so the same pose gives the same numbers
  every time.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/GlbRigInspector.cs: the documented profile, the skeleton, bind
    data, and skin weight validation.
  - src/StoryboardStudio.Api/Services/RigPoseCalculator.cs: the pure pose calculation.
  - src/StoryboardStudio.Api/Services/GlbModelInspector.cs now carries the binary chunk to the rig
    inspector, and AssetStore exposes the rig on the existing model profile plus one pose route.
  - fixtures/glb/build_rigged_figure.py and its three fixtures.
  - src/storyboard-studio-web: a rig panel in the existing model inspector, with the bone
    hierarchy, the checks, the findings, and a test pose.
  - Reused unchanged: the M04 import route, the content-addressed store, the M05 revision stack, and
    the existing model profile surface.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check (exit code checked directly)
  - npx playwright test models.spec.ts (desktop and tablet)
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
Results by evidence class (D/A/H/L/V/P):
  D: 191 backend tests passed, 0 failed (184 before this milestone plus seven rig tests). Frontend
     typecheck, lint, and format checks clean.
  A: 116 browser journeys passed, 0 failed, 6 intentionally skipped (122 discovered) against the
     real service and real persistence in a disposable temp data root. The new journey imports the
     rigged fixture, reads its skeleton and hierarchy on screen, poses it twice and gets the same
     numbers, confirms the other arm never moved, then opens the wrong-profile fixture and finds it
     named an unknown skeleton with its test pose refused, and a static prop reporting no skeleton.
  H: NOT RUN. No agent host is involved in this milestone; M13 asks for D and A only.
  L: NOT RUN. No provider is involved.
  V: NOT RUN as human acceptance; M13 does not ask for it.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: The application was closed and reopened on the same data root: the
  content hash, vertex count, bone count, bind pose, skin weights, materials, and the posed
  positions all came back identical. A joint that is not a node, a bind matrix set that does not
  match the joint count, and a skin with no bind matrices are each reported rather than assumed
  sound. A pose naming a bone the rig does not have, naming one bone twice, carrying a rotation
  beyond one full turn, or carrying a number that overflows a double is refused. An empty pose is
  the rest pose, which is an ordinary answer.
Relevant earlier-path regression results: The full backend and browser suites passed in full,
  including the M04 import journeys, the M05 revision journeys, and the M06, M10 and M11 scene
  journeys.
Checks NOT RUN and why: ./scripts/verify.ps1 in full (release-candidate gate; component steps
  passed individually).
Human approvals actually recorded, where required: None required by this milestone.
Known defects and dependency impact: None found. The rigged fixtures are authored in this
  repository and carry its own licence, which is what "licensed, known-good" means here; no
  third-party asset is vendored.
Permitted deferrals: More body classes, hand and facial rigs, and arbitrary retargeting, all
  explicitly deferred by M13.
Bug-detection check: Two sabotages, each reverted. With animation-ready no longer requiring a
  profile match, the wrong-profile test failed as intended; with the browser's test pose no longer
  gated on animation-ready, the browser journey failed as intended.
One defect the browser journeys found, fixed: the asset library's refreshes could land out of
  order, so a refresh already in flight when an import finished could put the library back to how it
  was before it, which reads as a file that silently failed to arrive.
Checkpoint: see the M13 commits on feature/director-mode.
Next dependency-ready milestone: M15.
```

### M15 acceptance record

```text
Milestone / status / date: M15 / VERIFIED / 2026-09-19
Tested code revision or worktree identity: feature/director-mode, working tree at the M15 commits
Outcome and supported constraints: Clips are reusable library material read from a model file's own
  animations: its channels, keyframes, target bones, and whether this build can sample it exactly.
  Only clips it can sample are ever bindable. A binding lives on the scene object rather than on the
  clip, so two characters share one clip and still hold their own trim, speed, loop, playback
  position, and root-motion policy. Every binding is checked before anything plays: the clip must
  exist in that file and be supported, the object's rig must be animation-ready and must have every
  bone the clip moves, and the trim, speed, and playback position must lie inside the clip.
  Scrubbing to a known time has one right answer, calculated from the stored files, and a browser
  journey holds what the view has on screen against what the service says is true at the same time.
  Root motion has one stated policy and reaches the scene exactly once: Hold drops the root's travel
  from the pose and leaves the object where the artist put it; Offset takes that same travel out of
  the pose and reports it once as an offset to the object. Neither applies it twice, and neither
  writes the object's saved transform. A rigid part turns about a pivot declared in its own local
  space with no skeleton anywhere near it, and that pivot is the one point the motion leaves exactly
  where it is; a static prop declares no motion and does not move.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/GlbClipInspector.cs: clips, their keyframes, and what this
    build refuses to sample.
  - src/StoryboardStudio.Api/Services/ClipSampler.cs and RigidMotionSampler.cs: the pure sampling.
  - src/StoryboardStudio.Api/Services/SceneMotionService.cs and one sample route.
  - src/StoryboardStudio.Api/Services/SceneService.cs validates every binding before writing.
  - src/StoryboardStudio.Api/Persistence: per-instance clip bindings and rigid motion, behind
    migration 20260919-scene-motion-v14.
  - fixtures/glb/build_rigged_figure.py now also emits clip-arm-raise.glb and
    clip-wrong-skeleton.glb.
  - src/storyboard-studio-web: a transport and motion panel in the scene inspector, and per-object
    animation in the viewport with its own skeleton and its own mixer.
  - Reused unchanged: the M04 import route, the M13 rig reading, the M06 scene save as the only
    write path, and the existing model profile surface.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check (exit code checked directly)
  - npx playwright test scenes.spec.ts (desktop and tablet)
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
Results by evidence class (D/A/H/L/V/P):
  D: 195 backend tests passed, 0 failed (191 before this milestone plus four motion tests). Frontend
     typecheck, lint, and format checks clean.
  A: 120 browser journeys passed, 0 failed, 6 intentionally skipped (126 discovered) against the
     real service and real persistence in a disposable temp data root. The new journeys give two
     characters one clip with different trims and speeds, scrub to a known time, hold the hands on
     screen against the service's own sample for each object, swing a door about its declared pivot,
     confirm the two characters hold two skeletons rather than one, confirm playing and pausing
     leave the saved scene untouched, reopen the studio and find the bindings and timing intact, and
     confirm a character clip bound to a crate is refused before anything plays.
  H: NOT RUN. No agent host is involved in this milestone; M15 asks for D and A only.
  L: NOT RUN. No provider is involved; nothing here generates motion.
  V: NOT RUN as human acceptance; M15 does not ask for it.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: The application was closed and reopened on the same data root: both
  bindings, their trims, speeds, loops, and root-motion policies, and the sampled poses all came
  back identical. A clip whose bones the skeleton does not have, a clip name that is not in the
  file, a trim beyond the clip, a trim that ends where it starts, a speed outside 0.1 to 4, a
  playback position outside the trim, an unknown root-motion policy, and a clip bound to a model
  with no skeleton are each refused, and none of them write anything. A sample time past a
  non-looping clip's own runtime is refused; the same time on a looping object is an ordinary
  answer. A rigid part with an axis that is not an axis, a swing that ends where it began, or a
  duration of zero is refused, and a time beyond its duration is refused.
Relevant earlier-path regression results: The full backend and browser suites passed in full,
  including the M06 scene journeys, the M10 direction journeys, the M11 blockout journeys, and the
  M13 rig journeys.
Checks NOT RUN and why: ./scripts/verify.ps1 in full (release-candidate gate; component steps
  passed individually).
Human approvals actually recorded, where required: None required by this milestone.
Known defects and dependency impact: None found. Clip blending, retargeting between skeletons,
  procedural motion, and animation editing beyond trim, speed, loop, and root-motion policy stay out
  of scope, as the milestone permits.
Permitted deferrals: Clip blending, arbitrary retargeting, procedural motion generation, and
  advanced animation editing, all explicitly deferred by M15.
Bug-detection check: Two sabotages, each reverted. With the root motion applied to the object while
  it was also left in the pose, the root-motion test failed as intended. With two skinned objects
  sharing one skeleton, the first attempt did not fail, because the browser check was reading bone
  positions rather than the skin binding; the check was strengthened to count distinct skeletons,
  the sabotage then failed as intended, and it passed again once reverted.
Three defects the browser journeys found, all fixed: trimming a clip could leave its playback
  position outside the trim, which the service refused and the artist could not see the cause of;
  the scene workspace could still be edited while another scene was being opened or created, and
  those edits were then silently discarded when the new scene arrived; and a slower scene load could
  land after a newer one and put the artist back in a scene they had already left.
Checkpoint: see the M15 commits on feature/director-mode.
Next dependency-ready milestone: none. See the runway note above.
```

### M07 acceptance record

```text
Milestone / status / date: M07 / VERIFIED / 2026-09-20
Tested code revision or worktree identity: feature/director-mode, working tree at the M07 commits
Outcome and supported constraints: An artist selects a reference image and the studio freezes exactly
  which bytes of it were chosen into a model-generation request, queues durable owned work, and hands
  the artist back to what they were doing. The Reference Asset Compiler does the work through one
  typed gateway that names a stage and reads the receipt; this studio never re-derives a verdict the
  compiler already reached. A capability that is missing blocks at enqueue, before anything is
  submitted, and an uncommissioned route is a different answer from an absent compiler: both refuse,
  and each says which it is. A generated model enters the library through exactly the gate an
  imported one does, so a worker cannot place anything an artist could not have imported by hand. The
  route is labelled uncommissioned by default, as M07 requires, and no provider or GPU request was
  made at any point in this milestone.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/CompilerGateway.cs: the one door to the compiler, with four
    distinct answers to the capability question.
  - src/StoryboardStudio.Api/Services/ModelGenerationService.cs: the frozen request, the owned job,
    reconciliation, staleness, lineage and delivery.
  - src/StoryboardStudio.Api/Services/AssetStore.cs: a generated model imports through the existing
    validated model gate.
  - src/storyboard-studio-web: the "Make a 3D model" affordance on a reference, and the dock opened
    into a work queue.
  - scripts/e2e-compiler-stub.ps1 and .cmd: the controlled worker the browser journeys run against.
  - Reused unchanged: the job record, the lease service, the queue worker's claim loop, the model
    inspector, and the M13 rig reading that makes a delivered model inspectable.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check (exit code checked directly)
  - npx playwright test model-generation.spec.ts (desktop and tablet)
  - npm --prefix src/storyboard-studio-web run test:e2e (full desktop + tablet matrix)
Results by evidence class (D/A/H/L/V/P):
  D: 218 backend tests passed, 0 failed (209 before this milestone plus nine model-generation tests).
     Frontend typecheck, lint, and format checks clean.
  A: 124 browser journeys passed, 0 failed, 6 intentionally skipped (130 discovered) against the real
     service and real persistence in a disposable temp data root. The new journeys import a
     reference, read the studio's capability answer on screen, queue the work, leave the workspace
     entirely, and find a delivered model that inspects as a real rig and names the reference it came
     from; and confirm the queue holds the work across a full page reload.
  H: NOT RUN. No agent host is involved in this milestone.
  L: NOT RUN, deliberately and as the milestone requires. No provider or GPU request was made. The
     compiler is a controlled stand-in that answers the same JSON and writes the same receipt shape
     as the real one, which is what M07 asks for and what M08 exists to go beyond.
  V: NOT RUN as human acceptance; M07 does not ask for it.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: A missing compiler and an uncommissioned route each refuse at
  enqueue and leave no job behind. Output that is not a valid GLB fails the job and leaves the
  library empty. A job interrupted after its stage wrote both payload and receipt is reconciled
  rather than run again. A job interrupted with only half an answer is run again out loud: the
  remains are cleared so they cannot later be adopted as whole, the phase says so, and the delivery
  records that it happened. A restart on the same data root recovers the same job identity and
  completes it. A reference that changed after the request was frozen leaves the output as an
  older-source candidate, labelled in the job and in the asset, not rejected. A duplicate delivery
  produces no second model and no second lineage record.
Relevant earlier-path regression results: The full backend and browser suites passed in full,
  including the M04/M05 model journeys, the M06/M10/M11/M15 scene journeys, and the existing
  generation dock journeys.
Checks NOT RUN and why: ./scripts/verify.ps1 in full (release-candidate gate; component steps passed
  individually). A live compiler run, which is M08.
Human approvals actually recorded, where required: None required by this milestone.
Known defects and dependency impact: The `rac` installed on this workstation's PATH predates
  run-stage, so the real route cannot run until it is reinstalled from the checkout. This does not
  affect M07, whose evidence is deliberately controlled, and it is the first thing M08 needs.
Permitted deferrals: Provider comparisons and additional models. The route is labelled uncommissioned
  until M08, which is enforced rather than merely written down: submission defaults to false.
Bug-detection check: Two sabotages. Removing the capability gate failed two tests as intended.
  Removing the duplicate-delivery guard failed nothing, because content addressing and the reconcile
  path already prevent a second asset; the guard was belt-and-braces and the test could not tell. The
  test now asserts the lineage is recorded exactly once, which the guard is load-bearing for, and it
  fails without it. That correction is the honest outcome of the check rather than a pass.
Three defects the tests found, all fixed: an interrupted run that left only half an answer was rerun
  silently, which the milestone forbids; the tests that count compiler invocations were racing the
  queue worker, which was already delivering the same jobs, so the counts were about timing rather
  than behaviour; and the first browser journey assumed a delivered model would carry a per-run name,
  which content addressing correctly refuses, since the same bytes are the same asset.
Checkpoint: see the M07 commits on feature/director-mode.
Next dependency-ready milestone: M08.
```

### M08 acceptance record

```text
Milestone / status / date: M08 / VERIFIED / 2026-09-20
Tested code revision or worktree identity: feature/director-mode, working tree at the M08 commits
Outcome and supported constraints: A reference image in this studio's library becomes a delivered,
  inspectable library model through the artist's own affordance, on this workstation's hardware, in
  about two minutes. The route is the Reference Asset Compiler's, stage by stage, through the one
  typed gateway: generate, set its real size, rebuild it to a runtime budget, unwrap, paint, glaze,
  export. Every verdict is the compiler's and every receipt is recorded rather than re-derived. The
  delivered prop is a static one, which is what this milestone supports; characters and rigs are not
  claimed.
Implementation surfaces reused/changed:
  - src/StoryboardStudio.Api/Services/ModelGenerationService.cs: the frozen route, its per-step
    reconciliation, the artist's size and glass answers, and the browser-scale options this studio
    asks for.
  - src/StoryboardStudio.Api/Services/CompilerGateway.cs: per-stage options, the studio tree, and
    the vocabularies read back from the compiler rather than copied.
  - src/StoryboardStudio.Api/Program.cs: an untracked local settings layer, so paths and a
    commissioned route reach neither repository.
  - src/storyboard-studio-web: the size and glass questions on a reference, and the model viewer's
    paint and wire toggles, panning, fill light and environment.
  - Reused unchanged: the job record, the lease service, the queue worker, content-addressed import,
    and the M13 rig reading that makes a delivered model inspectable.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check
  - npm --prefix src/storyboard-studio-web run test:e2e
  - python -m pytest (the compiler, in its own checkout)
  - rac run-stage --list, and each stage by name against the real lantern
Results by evidence class (D/A/H/L/V/P):
  D: 238 backend tests passed, 0 failed. The compiler's own suite passed in full. Frontend
     typecheck, lint and format clean.
  A: 124 browser journeys passed, 0 failed, 6 intentionally skipped, against the real service and
     real persistence. The generation journeys drive the real gateway and process boundary through a
     controlled compiler that speaks every stage.
  H: NOT RUN. No agent host is involved in this milestone.
  L: RUN. Live on this workstation's RTX 4090: Hunyuan3D single-view geometry at octree 256
     (47 s, 592,024 triangles), voxel rebuild to 9,000 vertices and 18,000 triangles, UV unwrap with
     a maximum vertex delta of 8.4e-07, Hunyuan3D-Paint 2.1 at 6 views and 512 px with faces
     unchanged, geometry delta 2.9e-08 and UV delta 5.3e-08, and glazing of 1,651 of 19,998 faces.
     Delivered model: 12,927 vertices, 18,000 triangles, 0.24 x 0.42 x 0.26 m, two materials, one
     transmitting at 85%. Receipts retained per step with their hashes.
  V: RECORDED. The artist inspected the delivered prop in the studio and accepted it, and separately
     accepted the rebuilt surface on sight after rejecting the earlier collapsed one. The prop is
     placed in the M06 scene "Ayric - character stage" (version 3) at knee height beside the
     character, which is the size the artist asked for.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: A missing capability refuses at enqueue and leaves no job. A stage
  the compiler does not offer refuses the whole route and names the step. A size or glass colour the
  compiler does not know is refused before anything runs. An interrupted job resumes at the step it
  reached rather than repeating the GPU work. A half-written step is cleared and run again out loud,
  and an output from one attempt cannot pair with a receipt from the next. A duplicate delivery
  produces no second model and no second lineage record.
Relevant earlier-path regression results: The full backend and browser suites passed, including the
  M04/M05 model journeys, the M06/M10/M11/M15 scene journeys, and the M07 generation journeys.
Checks NOT RUN and why: ./scripts/verify.ps1 in full (release-candidate gate; component steps passed
  individually).
Human approvals actually recorded, where required: The artist accepted the delivered prop on
  2026-09-20, in the studio, after inspecting it with the paint and wire toggles.
Known defects and dependency impact: The compiler's own ledger route for a generated asset is not
  wired: geometry writes a workspace that satisfies its own preflight but is not a `rac new` ledger
  workspace, so `cleanup-receipt` and `retopology-receipt` cannot yet be recorded against it. That
  is M09's work and does not weaken what is claimed here, because nothing here claims production
  readiness: every reduction receipt says `production_grade: false` and
  `requires_fixed_view_review: true`.
Permitted deferrals: Broad style fidelity, arbitrary character readiness, and multiple generators.
  Rigs, clips and the ledger's approval chain are M09 and M14.
Bug-detection check: Sabotages across this milestone's surfaces, all caught after correction: a
  defaulted size, a defaulted glass colour, a colour nobody offers, always glazing, a reducer
  pointed at its own destination, a stage that could wait on input, and a failure that could be
  waved away while still running. Three passed at first and each exposed a real gap rather than a
  strong design: a failed step was being caught by a missing file rather than by the run's verdict;
  clearing a half-answer looked redundant until the mismatched-pair case was written; and letting
  any stage survive a nonzero exit would have delivered a rejected reduction as a finished asset.
Defects found by running it rather than reasoning about it: a stage could inherit the service's
  standard input and hang for ever holding a lease; the route named every step's file .glb when a
  staged mesh is a .blend; the raw generator output was refused by both library gates, correctly;
  the reduction gate's absolute-metre thresholds were meaningless against an unscaled mesh; and a
  dismissed job failure came back on every reload, for ever.
Checkpoint: see the M08 commits on feature/director-mode.
Next dependency-ready milestone: M09.
```

### M09 progress record

```text
Milestone / status / date: M09 / IN_PROGRESS / 2026-09-20
Tested code revision or worktree identity: feature/director-mode at the M09 commits; Reference Asset
  Compiler at 5b285a0 on its main.
Outcome and supported constraints: A model already in this library becomes a runtime derivative
  through the artist's own affordance, in about twenty seconds on this workstation, and arrives as a
  revision beside its source rather than instead of it. The handling is deliberately the opposite of
  generation: a generated mesh has no topology worth keeping and is rebuilt and repainted, while a
  library model's UVs, materials and shell are the review, so it is adopted unchanged and collapsed
  with them riding along. What is supported is a bounded static mesh with one mesh object and a UV
  layer. Rigged characters and multi-mesh assets are not claimed.
Implementation surfaces reused/changed:
  - Compiler: scripts/blender/adopt_reviewed_mesh.py (new), scripts/blender/reduction_verdict.py
    (new, the one judgement that is not arithmetic, kept away from Blender so it can be argued
    with), reduce_feature_qem.py and run_feature_qem_reduction.ps1 gaining one named
    runtime-derivative mode, stages.py and cli.py registering both, contract section 9.
  - src/StoryboardStudio.Api/Services/ModelGenerationService.cs: a second work kind on the frozen
    packet (v6), a per-step ReadsOriginal flag, directory-aware step completion, the preparation
    route, the stacking that leaves the source current, and the evidence reader.
  - src/StoryboardStudio.Api/Services/AssetStore.cs: per-asset preparation acceptance.
  - src/StoryboardStudio.Api/Persistence: schema v16, four columns on Assets, backup taken.
  - src/storyboard-studio-web/src/components/ModelPreparation.tsx: the budget, the progress, the
    side-by-side fixed views in both passes, and the accept/refuse gate.
  - Reused unchanged: the durable job record, the lease service, the queue worker, content-addressed
    import, the revision stack, and the M04/M13 model inspection.
Commands and checks actually run:
  - dotnet test Framewright.slnx --nologo
  - npm --prefix src/storyboard-studio-web run check
  - npm --prefix src/storyboard-studio-web run test:e2e
  - python -m pytest (the compiler, in its own checkout)
  - ./scripts/public-release-audit.ps1
  - rac run-stage adopt-mesh / reduce-mesh / browser-payload / review-views against real assets
Results by evidence class (D/A/H/L/V/P):
  D: 251 backend tests passed, 0 failed. 472 compiler tests passed. Frontend typecheck, lint and
     format clean.
  A: 124 browser journeys passed, 0 failed, 6 intentionally skipped. One journey, the M18 director
     mode one, failed once under full-suite load and passes in isolation and on re-run; it is flaky
     and unrelated to this milestone's surfaces.
  H: NOT RUN. No agent host is involved in this milestone.
  L: RUN. Live on this workstation. The Ayric sword adopted at 18,000 triangles with one UV layer,
     one material and two textures, extent drift exactly 0; reduced to 8,000 with p99 surface
     deviation 2.9 mm and maximum 7.7 mm on a 1.35 m blade; exported at 5,326 vertices with both
     textures intact; eight fixed views of the source and eight of the derivative, each bound to the
     hash of the bytes it is a picture of. A second run at 6,000 triangles through the panel took
     about twenty seconds end to end. The glazed lantern is refused at 97 mm maximum deviation on a
     424 mm object, and nothing was delivered.
     CORRECTED 2026-09-21: the reason given here for that refusal -- "collapsing folds its glass
     panes" -- was wrong. The real cause was that glTF splits a vertex at every UV seam, so the
     lantern arrived reading as 12,358 boundary edges, and a repair filled 5,115 of those "holes"
     and invented surface. Welded by position first, the same lantern reduces at 4.5 mm and the
     sword passes the strict gate outright at 1.71 mm. The refusal was right; the explanation was
     not, and it would have sent the next person after the glass.
  V: NOT RECORDED. This is what M09 still wants. The derivative is in the library as revision 3 of
     the Ayric sword, unaccepted, with its source still the current revision and its comparison
     views reachable from the asset itself. The artist has not yet pressed accept or refuse.
  P: NOT RUN. No runtime dependency changed.
Failure/conflict/restart checks: A stage this compiler does not offer refuses the route and names
  it. A budget that would not reduce the model is refused before anything is queued, naming the
  count the model actually has. A failing stage leaves the source usable, delivers no derivative,
  and invents no revision stack. An interrupted run adopts the fixed views it already rendered
  rather than re-asking for a directory the compiler refuses to overwrite. A view the manifest does
  not list is not served, whatever is on disk beside it.
Relevant earlier-path regression results: The full backend and browser suites passed, including the
  M04/M05 model journeys, the M07/M08 generation journeys, and the M06/M10/M11/M15 scene journeys.
Checks NOT RUN and why: ./scripts/verify.ps1 in full (release-candidate gate; component steps passed
  individually).
Human approvals actually recorded, where required: NONE YET. This is the outstanding item.
Known defects and dependency impact: The compiler's own ledger route is still not wired -- the
  geometry and preparation stages write workspaces that satisfy their own preflight but are not
  `rac new` ledger workspaces, so `cleanup-receipt` and `retopology-receipt` still cannot be
  recorded against them. Nothing here claims production readiness: every reduction receipt says
  `production_grade: false` and `requires_fixed_view_review: true`, and the mode this route uses
  records every allowance it makes by name in `accepted_findings`.
Permitted deferrals: Rigged and multi-mesh preparation, texture rebaking to a runtime budget, and
  the ledger's approval chain.
Bug-detection check: Eleven sabotages across both repositories, all caught. In the studio: a
  derivative that quietly becomes the current revision, one that inherits its parent's acceptance, a
  route that reads only the step before it, evidence not recognised because it is a directory, a
  topology change that goes unrecorded, a refusal with no reason, and a view endpoint that serves
  anything in the workspace. In the compiler: a relaxation that stops asking whether the source was
  already open, openness that stops counting non-manifold edges, every reduction quietly becoming a
  runtime derivative, and adoption insisting on UVs nobody asked about.
Defects found by running it rather than reasoning about it: the lineage sentence written after
  delivery overwrote the revision note that carried the triangle counts, and called a preparation
  "Generated"; a model's first revision was labelled "Original image"; the readiness line offered
  "model generation" above a button that reduces something; SQLite cannot ORDER BY a DateTimeOffset,
  so finding the job that delivered an asset threw rather than answering; asking a model with no
  derivative for its evidence returned 404 and put a console error on every model an artist opened;
  and the sabotage harness itself restored files with their original timestamps, so one full-suite
  run tested a binary that still had the sabotage in it.
Checkpoint: see the M09 commits on feature/director-mode, and 5b285a0 on the compiler's main.
Next dependency-ready milestone: M14, once M09's verdict is recorded.
```

### Texture quality record (between M09 and M14)

```text
Work / status / date: Production-grade texture chain for generated characters / DELIVERED, awaiting the artist's
  verdict on the hero ninja / 2026-09-21.
Why: the artist judged the generated ninja "good, not great" up close. The complaint was measured rather than
  argued with, on the ninja itself, and the chain turned out to be leaking in five places before the painter's
  own limit was reached.
Found by measuring, each with the number that found it:
  1. UV unwrap left 66.7% of every atlas as gutter (601 islands, 33.3% occupied) and wrote the figure into its
     own receipt each time. Blender's own packer over the same islands: 55.1%. Compiler.
  2. The studio never told the painter anything but the reference, so its 512/6 floor defaults stood. 768/12.
  3. `--relief-from-paint` existed and was never passed: no generated model had ever had a normal map. Wired,
     default 0.3 chosen by looking (0.7 gives cotton a satin sheen; the gain is 32x a luminance gradient).
  4. The painter computed a 4096 atlas and saved it at 2048, as JPEG; the bake then re-encoded the changed
     JPEG as JPEG again (730 KB to 298 KB). Now: studio runner keeps 4096, writes PNG; the bake writes the base
     colour once, as PNG.
  5. The derived normal read every UV island's edge as a cliff and outlined all 601 of them -- the "drawn on
     triangles" look around the eyes. The gutter is filled from the islands before measuring and uncovered
     texels are left flat (mean tilt 6.62 to 4.88 with the real relief kept).
  6. Python-run compiler stages were handed their three paths and no options: everything the studio chose for
     the re-encoder ran as the script's defaults. Fixed with a test.
  7. The compiler's hi-res paint variant loaded the mesh with maintain_order=True, one UV per OBJ position, so a
     third of all faces (6,134 of 18,000) spanned the atlas and were painted with whatever the sliver crossed.
     The 2026-09-05 experiment's "olive spread across the face" was this, not the painter. Caught only because
     the head stage's own UV mask came out covering 92% of a sheet. Fixed: force="mesh", no merging; 13,862
     vertices, zero spanning faces, gate passes at 9e-8.
The face itself: twelve views at 768 of a 1.75 m figure give it ~90 px of each, and nothing downstream can put
  back what the diffusion never saw. New compiler stage `paint-head`: cut every face above 0.78 of the height,
  crop the reference to the same band from its own silhouette (rembg, bounding box, square), paint it again
  through the same launcher, lay it over the body in UV space (same UVs, so it lands where the first paint did;
  0.03 feather across the cut; body texels byte for byte). No Blender in it. 182 s on the RTX 4090.
Studio: a "hero" detail tier on the generation request (packet v9). Hero = octree 384, remesh to 80,000
  triangles (target 72,000, voxel grid 640, two smoothing passes rather than five), atlas 4096, paint-head,
  then compress-textures (4096 colour at JPEG 92, 2048 data). Set dressing keeps 20,000 / 256 / 2048 and now
  paints lossless. Offered only where the compiler has both extra stages; refused by name otherwise. Viewers
  sample textures at the GPU's maximum anisotropy (three's default is 1).
Commands and checks actually run: compiler pytest 563 passed (new: paint_relief, head_detail, option
  plumbing, atlas/runner); backend 270 passed (new: hero route and options, set default, hero refusal);
  tsc and eslint clean; live readiness offers set and hero against the real compiler; real GPU runs of the
  studio paint (4096 PNG, gate 9e-8), the seam-free bake, and the head pass on the ninja.
Hero route end to end, through the live studio against the real compiler (job 9a01d339, 2026-09-21): octree
  384 gave 417,138 raw triangles; the voxel grid at 640 gave 1,060,854; collapsed to 72,000 (36,000 vertices).
  Unwrap packed 29.4% as cut to 53.8%. Body paint 4 min, head pass 4.5 min, whole route 12 min 27 s. Delivered
  4.85 MB: 72,000 triangles, 4096 base colour as JPEG 92, 2048 metallic-roughness. The face has two eyes and
  brows where the set-dressing ninja had a smear; the hood edge is a curve rather than a polygon.
Found while it ran: the model viewer was torn down and the model reloaded on every parent render, and the
  workspace re-renders every 700 ms while any job is queued -- the model "blinked in and out" for as long as
  anything was running. The viewer now depends on the three dimension numbers rather than the array; an e2e
  check renames the model while looking at it and expects the same canvas.
Artist's verdict on the hero (2026-09-21): "a LOT better"; pinned as good; one critique -- the skin was far
  too shiny. Measured under the head mask: the painter had guessed skin at roughness 0.30 and metallic up to
  0.41 while the cloth beside it sat at 0.92 and 0.15. The head stage now floors roughness at 0.55 and sets
  metallic to 0 before laying the head down (both options; a negative metallic keeps the painter's guess).
Playwright, full suite after the GPU work: 123 passed, 6 skipped, 1 failed -- the generation journey on
  desktop, on a stray 400 from another test sharing the stand-in server; it passes alone in 26 s and was one
  of the three failures already seen at the start of the session. Recorded as a cross-test flake, not fixed.
Second hero through the studio with the skin fix (job 96a0fc63, 17 min 31 s including a slower paint):
  delivered 4.85 MB; skin under the head mask measured at roughness 0.555 and metallic 0.005 on the delivered
  file, against 0.30 and 0.11 before. The nose-bridge streak is a soft sheen. Awaiting the artist's next look.
Checks NOT RUN at the time of this record: none required.
Known limits: the head band is a height fraction, right for a standing humanoid and wrong for a crouching
  one; a hero's reference crop keeps whatever the silhouette's top 22% holds (here hood and shoulders, which
  share the head's cloth); the hi-res provenance runner keeps its fault, recorded in the catalog.
```

### WIP quality-control record - 2026-09-21

Scope: review the 46 commits from `9ab9b68` through `8c17be5`, repair concrete
regressions, and integrate into private `origin/main` at the user's explicit
request. This review does not advance unfinished milestones or commission
providers. The starting checkout was clean on `feature/director-mode`.

Tested application changes: `3abed12e1f137a35466fa7bb01f7a20dc8324046`.
Final browser launcher: `7ac0216` (application code unchanged).
Environment: Windows, .NET SDK 10.0.203, Node 22.15.0, Playwright 1.62.1;
desktop Chromium and emulated iPad Pro 11 WebKit.

Repairs:

- Enforce scene versions in the database, so overlapping saves cannot both win.
- Preserve edits made while a Save response is in flight.
- Commit model import, revision membership, lineage, and delivery together;
  an interrupted delivery rolls back and can recover from existing stage outputs.
- Keep the artist's current revision when preparation finishes against an older source.
- Refuse malformed GLB field types and unavailable optional glass stages cleanly.
- Surface readiness failures with a retry instead of an endless loading indicator.
- Keep delayed model/clip callbacks bound to the current scene, deduplicate model
  loads, give each instance its own selection materials, and release original
  model materials when closing an unpainted inspection view.
- Restore command-line configuration precedence and correct 58 analyzer errors
  in test data without suppressing diagnostics or weakening assertions.
- Support `STUDIO_E2E_PORT` and build test binaries in the disposable test directory,
  allowing verification while existing workstation apps own port 5180/Debug DLLs.

Observed evidence:

- **D/A:** Three new data-integrity regressions failed against the original code:
  overlapping saves, older-source selection, and partially committed model delivery.
  After repairs, 74 focused backend tests passed; the final complete backend suite
  passed **277/277**, with **zero build warnings/errors**.
- **D:** TypeScript, lint, formatting, production bundle, public-content audit,
  npm audit, NuGet vulnerability inspection, six PowerShell contract suites,
  five voice tests, and five YuE2 tests passed. No dependency vulnerabilities reported.
- **A:** Full Playwright suite passed **130**, with **6 existing tablet exclusions**,
  in 5.7 minutes. The exclusions duplicate desktop coverage for seeded placeholder
  feedback, job recovery, project-rate conversion, generated-media provenance,
  immutable authority drops, and background authority refresh.
- **Visual inspection by the coding agent:** Desktop/tablet model and scene
  screenshots inspected; layouts and controls readable. This is not human artistic
  acceptance or a physical stylus test.
- The `verify.ps1` run completed all pre-browser gates; browser startup exposed
  the occupied port and locked shared binaries. After repairing the launcher,
  `STUDIO_E2E_PORT=5182` with `npm --prefix src/storyboard-studio-web run test:e2e`
  completed the remaining gate. Its disposable server shut down; existing apps
  were not stopped.

Local evidence (ignored): `artifacts/qc/regressions-before.log`,
`artifacts/qc/regressions-after.log`, `artifacts/qc/verify-final-5182.log`,
`artifacts/qc/browser-final.log`, and `artifacts/qc/{model,scene}-{desktop,tablet}.png`.
The HTML browser report is `src/storyboard-studio-web/playwright-report/index.html`.

Limits and next action: No live GPU generation, paid provider calls, actual-host
WebMCP commissioning, physical tablet/stylus acceptance, installer/container release
smoke, or production deployment was performed. Browser compiler work used the
controlled fixture worker. The separate Three.js bundle retains Vite's size warning.
M09 remains awaiting the artist's verdict; actual-host evidence for M02/M03/M10/M11
and the remaining not-started milestones stay open for the research/implementation
handoff. This is a tested WIP checkpoint, not release acceptance.

Remote verification: the reviewed branch was fast-forwarded and pushed to private
`origin/main`. [GitHub Actions run 35659270918](https://github.com/raydeStar/framewright-private/actions/runs/35659270918)
failed before executing any steps: "The job was not started because an Actions
budget is preventing further use." Hosted CI is therefore **blocked by the Actions
budget**, not verified by the local results. Restore that budget and rerun CI;
no workflow gates or account spending settings were changed during this review.

### Actual-host commissioning record - 2026-09-21

Scope: commission the page-defined WebMCP path in Codex's real in-app browser,
repair any defect the host exposes, and preserve the artist's running app and
data. Host/browser: Codex in-app browser (IAB) against same-origin localhost
Framewright. The host exposed no semantic browser or WebMCP API version, so
none is inferred. The running artist app was read on `127.0.0.1:5179`; scene
write-path proof used a disposable current build and data root on
`127.0.0.1:5198`, which was shut down and removed afterward.

Observed host evidence:

- The host discovered exactly the ten registered tools. On the artist app,
  `get_director_context` and `observe_current_frame` agreed on LAB-010 v16.
  Navigation to LAB-020 produced v9, its two open notes, and a new state token.
- A bounded two-note shot proposal appeared in Agent Activity. Rejecting it
  left LAB-020 at v9 with both notes and created no generation job.
- In the disposable current build, an exact one-pixel reference fixture staged
  a visibly reviewable three-object blockout plan with explicit assumptions and
  uncertainty. The artist action built a new scene without generation.
- The Lantern marker was corrected from X 0.8 to 1.15 and the inspection camera
  from yaw/pitch 0.9/0.42 to 0.98/0.5. While those changes were unsaved, both
  context and proposal calls returned `unsaved_scene`; after save, context
  reported scene v2 and the corrected object and camera.
- The host then staged a one-object rotation proposal, the artist action applied
  Y 1.5708 only to the Lantern marker, and save produced v3. A cold reload and
  fresh selection returned the same three object identities, camera, position,
  rotation, scene version, and plan lineage.

Defect found and fixed: the scene workspace originally published only the
persisted scene identity. After an unsaved orbit or transform, a host read could
therefore describe the older persisted camera/object while the artist saw a
different draft. The bridge now publishes dirty state and playhead time, refuses
dirty scene reads and proposals, returns time in the scene context, and binds a
proposal token to the observed time. The exact dirty and time conflicts are
covered in the desktop/tablet scene journey and API tests.

Final local gate on the containing revision: `STUDIO_E2E_PORT=5199; .\scripts\verify.ps1`
passed dependency audits, frontend checks and production
build, public-content audit, a zero-warning Release build, **283/283 backend
tests**, script/voice/YuE2 contracts, and **134 Playwright journeys with the same
six intentional tablet exclusions**. No provider, GPU generation, production
queue, deployment, or artist data was used. The existing separate Three.js
bundle warning remains.

Status impact: M02, M03, and M10 are VERIFIED. M11 now has D/A/H evidence but
remains CONTRACT_VERIFIED until the artist records composition acceptance using
a meaningful reference. M09 remains the active milestone pending the prepared
sword verdict.

### Acceptance record template

```text
Milestone / status / date:
Tested code revision or worktree identity:
Outcome and supported constraints:
Implementation surfaces reused/changed:
Commands and checks actually run:
Results by evidence class (D/A/H/L/V/P):
Artifact paths, hashes, job IDs, and environment versions:
Failure/conflict/restart checks:
Relevant earlier-path regression results:
Checks NOT RUN and why:
Human approvals actually recorded, where required:
Known defects and dependency impact:
Permitted deferrals:
Checkpoint:
Next dependency-ready milestone:
```

Do not claim verification of a later commit when only an earlier code state was tested. Documentation-only evidence updates may follow the tested checkpoint; distinguish them from code changes requiring rechecks.

### Decisions

| ID | Decision | Rationale / validation needed |
| --- | --- | --- |
| D01 | Model-independent Director Mode | User requirement; prove host separation and manual fallback. |
| D02 | Extend existing project, image, library, and job boundaries | Avoid competing state and orchestration systems; map actual source in M00. |
| D03 | Static/imported assets before generated character automation | Establish persistence/scene contracts without depending on unproven rigging. |
| D04 | Typed proposals with user application; explicit generation | Preserve current repository permission and approval boundaries. |
| D05 | One supported GLB/profile/provider/render route first | Prove narrow useful support before expanding breadth. |
| D06 | Reuse Reference Asset Compiler through a bounded adapter | Verify each reused operation; retain its human gates and receipts. |
| D07 | Two detail tiers on generation, asked as "how close will the camera get" | Set dressing at 20,000 triangles / 2048 is right for most things and a hero costs minutes more of GPU; the numbers each answer stands for live in one place in the service, not in the form. Hero is offered only where the compiler can paint a head on its own. |

### Deferred work

No execution deferrals recorded yet. Add each with an owner/milestone, reason, dependency impact, and disposition. Required acceptance work cannot be renamed polish.

### Known failures and blockers

See the milestone records and the 2026-09-21 QC record above for runtime evidence.
The remaining external-host, human acceptance, and not-started milestone boundaries
are still open; passing deterministic checks does not close them.

## 9. Source notes and refresh policy

The following documents were inspected on 2026-09-19. They establish the planning context, not live production evidence. File hashes below are Git blob hashes, not repository commit hashes. Capture actual checkout commits in M00. Read current repository instructions before executing; do not overwrite them with this plan.

**[R0] Framewright `AGENTS.md`.** Provider permissions, browser proposal boundary, isolation, and release-check entry points. Git blob `d2e8e195f64d3345b2c1a9c16997d4abc8f5cd26`.

**[R1] Framewright `docs/ARCHITECTURE.md`.** Domain ownership, immutable evidence, storage, project/service boundaries, and external job ownership. Git blob `4389d169d722dcc97ab25892b6c0100d174eb753`.

**[R2] Framewright `docs/IMPLEMENTATION_HANDOFF.md`.** Inspected relevant returned sections covering shared image composition, revision-bound feedback, current capabilities, and production boundaries. This document contains an older reconciliation date; source and runtime verification take precedence over its readiness language.

**[R3] Framewright `docs/ASSET_LIBRARY.md`.** Canonical asset/media-pool model, shared composition, metadata, provenance, and non-destructive placement/archive behavior. Git blob `b40819ffc8854baf2620d265844399677099b2f3`.

**[R4] Reference Asset Compiler `README.md`, lines 1-180.** Existing preparation, rig-related, static-prop, and receipt capabilities, including explicit operator/live-proof limitations. Git blob `13bf1cc12f2ab943ae12986e36cbe0b3786810d5`. Repository: `https://github.com/raydeStar/reference-asset-compiler`.

**[W1] WebMCP draft.** The inspected report is dated 2026-09-17 and identifies itself as a Community Group draft rather than a W3C Standard. Recheck the API and actual host support when implementing; do not rely on an old prototype's interface. Reference: `https://webmachinelearning.github.io/webmcp/`.

**[W2] Official ComfyUI server routes.** Verify submission, progress/history, and control-route semantics for the configured version. Reference: `https://docs.comfy.org/development/comfyui-server/comms_routes`.

**[W3] Khronos glTF 2.0 specification.** Source for the chosen interchange conventions and supported geometry/material/skin/animation subset. Reference: `https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html`.

Do not freeze incidental dependency versions from this goal. Pin the versions actually selected and tested during execution, and preserve those versions in live and release evidence.
