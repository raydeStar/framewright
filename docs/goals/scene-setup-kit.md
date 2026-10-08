# Goal: Scene setup kit

**Project:** Framewright
**Created:** 2026-10-08
**Branch:** `feat/scene-setup-kit` from `main` at `94145b20abf510c056acf61607056c7ecd66e638`; K06 on `feat/blockout-person-card` from `feat/scene-setup-kit` at `e913ed9`; K07 on `feat/blockout-image-search` from `feat/blockout-person-card` at `d45d747`
**Overall status:** VERIFIED for K00-K05 and CONTRACT_VERIFIED for K06 and K07 on the branches; not pushed, not released, no human visual acceptance recorded

## 1. Outcome

An artist can stage a scene by hand, quickly, before anything is generated:
drop in simple stand-ins, push them around directly in the view, and frame the
shot through the camera that will actually render it. The existing scene
contracts stay authoritative: one versioned Save, instances pinned to exact
revisions or explicit stand-in geometry, a shot camera distinct from the
inspection camera, and renders from saved versions only.

### Origin of the ideas

The artist reviewed `github.com/storytold/artcraft` on 2026-10-08 and agreed
with its direction of building the scene before generating. ArtCraft ships
under a "fair source" licence that restricts use of its code to private
purposes and forbids competing products, so **no ArtCraft code, assets, or
text are copied here**. Only product ideas are borrowed, and every
implementation is written against Framewright's own contracts.

| ArtCraft idea | Framewright before this goal | Milestone |
| --- | --- | --- |
| Scene blocking with kitbashing | Stand-ins only arrived through an agent blockout plan | K01 |
| Position, rotate, and frame objects in 3D | Numeric fields only; the view could orbit but not move objects | K02 |
| Camera setup | Shot camera edited only as numbers | K03 |
| Character posing in 3D | 2D poseable mannequins in Sketch; no 3D figure | K04 |
| 3D image compositing, cutouts | Scenes hold models and stand-ins only | K05 |
| Background removal, identity transfer, splat worlds | Not covered here | Deferred |

## 2. Boundaries

- Manual controls come first. Every milestone has an ordinary UI path; agent
  tools may gain conveniences later but are not required.
- Viewport handles edit **working** state only. Save remains the only write,
  against the version the scene was read at.
- Orbiting must never change an object; a handle drag must never orbit.
- No provider calls, GPU jobs, downloads, or new runtime dependencies without
  the artist's agreement. `three/examples/jsm` add-ons ship inside the existing
  pinned `three@0.186.0` package and are not new dependencies.
- Schema changes (K04, K05) need an isolated old-schema migration check and a
  portable-package round trip, per `AGENTS.md`.

## 3. Milestones

| ID | Observable increment | Depends on | Evidence |
| --- | --- | --- | --- |
| K00 | Baseline recorded on the branch | none | D |
| K01 | Hand-placed stand-ins: add, resize, save, reopen, replace with a library model | K00 | D, A |
| K02 | Move, rotate, and scale the selected object with handles in the view; drop to floor; focus | K01 | D, A, desktop + tablet |
| K03 | Look through the shot camera with delivery-aspect guides; orbiting there edits the shot camera only | K00 | D, A, desktop + tablet |
| K04 | A posable 3D stand-in person with pose presets, saved and reopened | K01 | D, A, migration, package |
| K05 | Image cards: a library image as a backdrop or cutout plane | K01 | D, A, migration, package |
| K06 | An agent's blockout plan can propose a posed person and an image card | K04, K05, director-mode M11 | D, A (shim agent), migration, package |
| K07 | An agent can find a project picture other than the selected reference to put on a planned card | K06 | D, A (shim agent), desktop + tablet |

### K01. Hand-placed stand-ins

**Deliver:** A stand-in kit in the scene inspector: box, cylinder, sphere,
floor, and wall at sensible real-world sizes. A selected stand-in's size is
editable in metres. The existing "Replace stand-in with" path works for
hand-placed stand-ins exactly as it does for planned ones.

**Prove:** Add two stand-ins, resize one, save, reload, and see both with the
right shapes and sizes. Invalid sizes are refused by the service, not only the
form. A hand-placed stand-in has no plan provenance.

### K02. Direct manipulation

**Deliver:** Move, rotate, and scale handles on the selected object, with a
mode switch. Dragging a handle edits the working transform and marks the scene
unsaved; dragging empty space still orbits. "Drop to floor" rests the object's
lowest point on the ground. "Focus" frames the selection. The numeric fields
stay as the keyboard path.

**Prove:** A handle drag changes only the selected object and survives save and
reload. A drag on empty space changes the inspection camera and no object. Works
with touch on the tablet project.

### K03. Look through the shot camera

**Deliver:** A toggle that shows the view from the shot camera, masked to the
project delivery aspect. While it is on, orbit and zoom edit the shot camera,
not the inspection camera. Leaving the mode restores the inspection view.

**Prove:** Orbiting in shot view changes the shot camera fields and leaves the
saved inspection camera alone. The guide matches the delivery aspect. A still
rendered afterwards uses the framed shot camera.

### K04 and K05

Specified in detail when K01 to K03 are proven. Each adds a column to scene
instances, so each needs an old-schema migration check, package export/import,
and render-path evidence before it is called verified.

### K06. Agent-proposed people and cards

**Deliver:** The M11 blockout tool (`propose_scene_blockout`) offers the same
stand-ins the artist can place by hand. Its object schema gains `Person` and
`Card` shapes, an optional `pose` (the nine names in `scenePoses.ts`, equal to
`SceneService.SupportedPoses`), and an optional `imageAssetId`. A plan item
stores its pose and card picture, the plan review names both, and Build makes
ordinary scene objects carrying them.

**Prove:** A shim agent stages a plan with a seated person and a card; staging
builds nothing; the artist builds it; the result saves, reopens, and has the
same stand-in description as the hand-placed equivalents. A card must cite an
Image in the active project, and every refusal leaves no plan or item behind.
Build refuses a plan whose card picture has gone. Old-schema migration with a
pre-migration backup, and a working package that remaps a planned card's
picture.

**Defer:** An image search tool for agents (a card can cite the selected
reference, whose id the director context already gives, or an id the agent
learned elsewhere), and actual-host evidence for the widened schema. K07
delivers the search.

### K07. Image search for planned cards

**Deliver:** A twelfth WebMCP tool, `search_project_images`, read-only and
closed like `search_scene_assets`: optional `search` (120 characters or
fewer), `offset`, and `limit` (1 to 20). It returns current, non-archived
Image assets in the active project with `assetId`, name, content hash, width,
and height, plus revision identity and tags. `GET /api/webmcp/images` in
`WebMcpStoryboardService` shares the model search's library filter. A new
tool rather than a kind filter on `search_scene_assets`, so that tool's
model-specific result and its host-verified schema stay unchanged.

**Prove:** Archived pictures, superseded revisions, and models never appear;
another project's pictures never appear; a page never exceeds twenty;
searching creates no asset, job, scene, object, or plan; and an id the search
returns is accepted as a card's `imageAssetId` and builds a card showing those
exact bytes.

**Defer:** Actual-host discovery of the twelve-tool registry. Archived images
are still accepted on a card by id, as before; the search only stops offering
them.

## 4. Progress and evidence ledger

| ID | Status | Notes |
| --- | --- | --- |
| K00 | VERIFIED | Frontend `check` passed (29 unit tests after K01 added 6). `e2e/scenes.spec.ts` 26/26 on desktop and tablet before any change. |
| K01 | VERIFIED | D: `unit/sceneStandIns.test.ts`. A: `e2e/scene-setup.spec.ts` stand-in journey on desktop and tablet adds, resizes, saves, reloads, gets a service 400 for size 0 with the version unchanged, and replaces a hand-placed stand-in with a library model. Sabotage: a resize that keeps the old size fails the journey. No schema change; the service already accepted asset-less placeholders. |
| K02 | VERIFIED | D: `unit/sceneTransforms.test.ts`. A: handles journey on desktop Chromium and iPad-sized WebKit grabs the move, rotate, and scale handles with a mouse, drops a raised box and an upright cylinder to the floor, orbits on empty space without moving an object, saves, reloads, and finds only the selected object changed. Found and fixed two defects: three's centre scale handle jumped to 50x on a 70 px drag (now doubles per 150 px), and the narrow layout put the toolbar 12 px over the stage (pre-existing, now asserted). Sabotage: removing the guard that keeps a handle grab from orbiting fails the journey. Not exercised: real touch pointers and a physical stylus; Playwright drove mouse events on the tablet profile. Pre-existing, not caused here: WebKit screenshots of the full-screen tablet Director Mode canvas come back black although the canvas buffer holds the scene, on main as well; check on a real iPad. |
| K03 | VERIFIED | D: `unit/sceneFraming.test.ts` proves the stage and a render cover the same picture, including when a 16:9 frame is wider than a portrait stage. A: look-through journey on desktop and tablet shows a delivery-shaped frame that fits the stage's long side, orbits only the shot camera with the scene left saved and its inspection camera unchanged, returns to the inspection view, and renders a still whose stored camera equals the framed one. Shot-camera fields are rounded to field precision. Sabotage: routing orbit to the inspection camera fails the journey. Not proven: a pixel comparison between the framed stage and the rendered still; the shared geometry is unit-tested instead. |
| K04 | VERIFIED | Schema: migration `20261008-person-stand-in-pose-v22` adds nullable `SceneInstances.PlaceholderPose`. D: `SceneStandInTests` (pose survives restart and a working-package import with fresh IDs, unknown poses and poses on other shapes refused with the version unchanged, a still freezes the pose it was rendered with after the pose changes), `SchemaMigrationTests.PersonStandInV22...` (an isolated v21 database upgrades, keeps its box stand-in, writes a pre-migration backup, records v22, and takes a posed person), `unit/scenePoses.test.ts` (front-end pose list matches `SceneService.SupportedPoses`). A: person journey on desktop and tablet drops every pose to the same resting height, saves height and pose, reloads three posed figures. Full backend suite 379/379. Non-person stand-ins serialise without a `pose` field, so their snapshots are byte-identical to before. Sabotage: not storing the pose fails three backend tests; not sending it fails the journey. Also fixed: stand-in colours came from a hash that put `Box 1` and `Box 2` a degree apart. Not done: the agent blockout tool's shape list still omits Person, and blockout plans cannot carry a pose; agent-built people stand in Neutral. |
| K05 | VERIFIED | Schema: migration `20261008-image-card-stand-in-v23` adds nullable `SceneInstances.PlaceholderImageAssetId`. A Card stand-in cites a project Image asset by id; the service describes it with URL and content hash, so stills freeze the exact picture. D: `SceneImageCardTests` (card survives restart; working-package import remaps the picture to the new project's copy with the same hash; library usage counts the card; a missing picture, a model, another project's image, an unknown id, or a picture on a box is refused with the version unchanged), `SchemaMigrationTests.ImageCardV23...`, unit tests for card shape and aspect-keeping resize. A: image-card journey on desktop and tablet adds a 2:1 half-transparent picture, keeps its shape on resize, samples canvas pixels showing the opaque half red and unlit and the clear half not filled, saves and reopens it, and proves a still waits for a held picture instead of freezing a blank card. Full backend suite 382/382. Sabotage: dropping the package remap fails the package test; skipping the picture wait fails the journey. Not done: background removal for cutouts needs a provider route and stays deferred; cards are unlit by design. |
| K06 | CONTRACT_VERIFIED | Schema: migration `20261008-blockout-person-and-card-v24` adds nullable `SceneBlockoutItems.PlaceholderPose` and `SceneBlockoutItems.PlaceholderImageAssetId`. Staging validates through `SceneService.ValidatePlaceholder`, so a plan accepts exactly what a hand save accepts; a planned person with no pose is stored as Neutral, as a hand-placed one is. D: `SceneBlockoutStandInTests` (a seated person and a card build into objects whose placeholder JSON equals the hand-placed equivalents' through a restart; the plan describes pose, picture id, URL, and hash; a working package remaps the planned card to the new project's copy, the same one the built card shows; nine refusals including a card with no picture, a model, an unknown id, another project's image, a picture on a box, a pose on a box or a match, and an unknown pose leave zero plans and items; Build refuses a plan whose picture row has gone and leaves it Pending), `SchemaMigrationTests.BlockoutPersonCardV24...`. A: the shim agent's refused ghost card stages nothing; the staged plan shows the pose and the picture's name; Build makes a seated person and a ready card; the artist hand-places the same pair; after save and reload all four reopen and the planned and hand-placed placeholders are equal; desktop Chromium and iPad-sized WebKit. The WebMCP registration test now pins the blockout object schema (closed, field list, both enums, `imageAssetId` shape). Sabotage: Build not copying pose and picture fails two backend tests and the journey (card never ready); Build not copying only the pose fails the journey (`Neutral`, not `Seated`); dropping the package remap fails the package test; dropping `Person` from the tool enum fails the registration test. Not proven: an actual WebMCP host seeing the widened schema (the 2026-09-22 host check saw the old one), so this is CONTRACT_VERIFIED, not VERIFIED. Agents still have no image search; see the K06 Defer note. |
| K07 | CONTRACT_VERIFIED | No schema change. D: `WebMcpApiTests.ProjectImageSearchFindsOnlyCurrentImagesInTheActiveProjectAndStartsNoWork` returns the graded current revision and a picture found by tag, never the superseded revision, the archived picture, or a model whose file name matches; checks id, name, kind, hash, width, and height against the import; pages of one return different pictures, a limit of 500 is clamped to 20, an overlong search is refused, and `%` is literal; asset, job, scene, object, plan, and plan-item counts across all projects are unchanged by searching; a found id stages a card; a second project sees none of the first's pictures and only its own, and switching back restores the first's. A: the registration test now expects twelve tools and pins `search_project_images`'s closed schema and read-only annotations; a shim-agent journey on desktop Chromium and iPad-sized WebKit uploads a reference, a picture, and an archived picture, reads the director context (which names only the reference), searches and gets the reference and the picture but not the archived one, with size and hash matching the upload, sees nothing staged or imported, stages a card citing the picture, sees the plan name it, builds it, reloads, and finds a ready card whose stored placeholder is the searched id, URL, and hash. Sabotage: dropping the archived filter fails the backend test (3, not 2) and the journey (3, not 2); bypassing the project query filter fails isolation (2, not 0). Not proven: an actual WebMCP host seeing the twelve-tool registry. |

### Full gate record - 2026-10-08

`scripts/verify.ps1` passed in one invocation on Windows at `e42409e` plus the
README, changelog, and handoff edits: npm ci and audit (0 vulnerabilities),
frontend checks (40 unit tests) and build, public release audit, locked
restore, warning-free Release rebuild, 382 backend tests, backup, launcher,
Docker, setup, voice-PID, and release-candidate script tests, voice and YuE2
contract tests, NuGet vulnerability inspection (none), and 198 Playwright
journeys with 6 intentional tablet exclusions. The known Three.js chunk-size
advisory remains. Two follow-ups landed after the gate and were checked with
frontend `check` and the ten scene-setup journeys: a comment moved back above
the clip wait, and an emptied size field no longer snaps a stand-in to 0.01 m.

Not run or not claimed: no provider, GPU, or compiler work; no push or
release; no packaged-runtime smoke (no runtime dependency changed); no
physical touch, stylus, or real-iPad check; no human acceptance of how the
figure, cards, or handles look. AI screenshot inspection is not artistic
acceptance.

### K06 gate record - 2026-10-08

`scripts/verify.ps1` passed in one invocation on Windows at `be37a55` on
`feat/blockout-person-card`: npm ci and audit (0 vulnerabilities), frontend
checks (40 unit tests) and build, public release audit, locked restore,
Release rebuild, 386 backend tests, the backup, launcher, Docker, setup,
voice-PID, and release-candidate script tests, NuGet vulnerability inspection
(none), and 200 Playwright journeys with 6 intentional tablet exclusions. The
known Three.js chunk-size advisory remains. Not run: an actual WebMCP host, any
provider or GPU work, a packaged-runtime smoke (no runtime dependency changed),
and human review of the plan text or the figures.

### K07 gate record - 2026-10-08

`scripts/verify.ps1` passed in one invocation on Windows at `46f3179` on
`feat/blockout-image-search`: npm ci and audit (0 vulnerabilities), frontend
checks (40 unit tests) and build, public release audit, locked restore,
warning-free Release rebuild, 387 backend tests, the backup, launcher, Docker,
setup, voice-PID, and release-candidate script tests, voice contract tests,
NuGet vulnerability inspection (none), and 202 Playwright journeys with 6
intentional tablet exclusions. The known Three.js chunk-size advisory remains.
Not run: an actual WebMCP host, any provider or GPU work, a packaged-runtime
smoke (no runtime dependency changed), and human review of the search results
or the plan text.

### Next candidates

- Exercise the widened blockout schema and the twelve-tool registry, including
  `search_project_images`, in an actual WebMCP host.
- Background removal for cutouts, as a separately gated provider route.
- A pixel comparison between the framed stage and a rendered still.

