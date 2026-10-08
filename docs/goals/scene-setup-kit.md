# Goal: Scene setup kit

**Project:** Framewright
**Created:** 2026-10-08
**Branch:** `feat/scene-setup-kit` from `main` at `94145b20abf510c056acf61607056c7ecd66e638`
**Overall status:** IN_PROGRESS

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

## 4. Progress and evidence ledger

| ID | Status | Notes |
| --- | --- | --- |
| K00 | VERIFIED | Frontend `check` passed (29 unit tests after K01 added 6). `e2e/scenes.spec.ts` 26/26 on desktop and tablet before any change. |
| K01 | VERIFIED | D: `unit/sceneStandIns.test.ts`. A: `e2e/scene-setup.spec.ts` stand-in journey on desktop and tablet adds, resizes, saves, reloads, gets a service 400 for size 0 with the version unchanged, and replaces a hand-placed stand-in with a library model. Sabotage: a resize that keeps the old size fails the journey. No schema change; the service already accepted asset-less placeholders. |
| K02 | VERIFIED | D: `unit/sceneTransforms.test.ts`. A: handles journey on desktop Chromium and iPad-sized WebKit grabs the move, rotate, and scale handles with a mouse, drops a raised box and an upright cylinder to the floor, orbits on empty space without moving an object, saves, reloads, and finds only the selected object changed. Found and fixed two defects: three's centre scale handle jumped to 50x on a 70 px drag (now doubles per 150 px), and the narrow layout put the toolbar 12 px over the stage (pre-existing, now asserted). Sabotage: removing the guard that keeps a handle grab from orbiting fails the journey. Not exercised: real touch pointers and a physical stylus; Playwright drove mouse events on the tablet profile. Pre-existing, not caused here: WebKit screenshots of the full-screen tablet Director Mode canvas come back black although the canvas buffer holds the scene, on main as well; check on a real iPad. |
| K03 | VERIFIED | D: `unit/sceneFraming.test.ts` proves the stage and a render cover the same picture, including when a 16:9 frame is wider than a portrait stage. A: look-through journey on desktop and tablet shows a delivery-shaped frame that fits the stage's long side, orbits only the shot camera with the scene left saved and its inspection camera unchanged, returns to the inspection view, and renders a still whose stored camera equals the framed one. Shot-camera fields are rounded to field precision. Sabotage: routing orbit to the inspection camera fails the journey. Not proven: a pixel comparison between the framed stage and the rendered still; the shared geometry is unit-tested instead. |
| K04 | VERIFIED | Schema: migration `20261008-person-stand-in-pose-v22` adds nullable `SceneInstances.PlaceholderPose`. D: `SceneStandInTests` (pose survives restart and a working-package import with fresh IDs, unknown poses and poses on other shapes refused with the version unchanged, a still freezes the pose it was rendered with after the pose changes), `SchemaMigrationTests.PersonStandInV22...` (an isolated v21 database upgrades, keeps its box stand-in, writes a pre-migration backup, records v22, and takes a posed person), `unit/scenePoses.test.ts` (front-end pose list matches `SceneService.SupportedPoses`). A: person journey on desktop and tablet drops every pose to the same resting height, saves height and pose, reloads three posed figures. Full backend suite 379/379. Non-person stand-ins serialise without a `pose` field, so their snapshots are byte-identical to before. Sabotage: not storing the pose fails three backend tests; not sending it fails the journey. Also fixed: stand-in colours came from a hash that put `Box 1` and `Box 2` a degree apart. Not done: the agent blockout tool's shape list still omits Person, and blockout plans cannot carry a pose; agent-built people stand in Neutral. |
| K05 | NOT_STARTED | |
