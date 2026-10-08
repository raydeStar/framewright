# Changelog

## Unreleased

### 3D models
- **Blender is found for you.** When no Blender is configured, Framewright uses
  the `BLENDER` or `RAC_BLENDER` environment variable, then `PATH`, then the
  usual install places for the system: Program Files (newest version first),
  every Steam library, the Microsoft Store app, `/Applications` on macOS, and
  `/usr/bin`, `/snap/bin` and Steam on Linux. A found Blender is used only if it
  answers `--version`. It is passed to the compiler as `--blender`, exactly like
  a configured one, so receipts still name the exact executable. The model
  panels show which Blender is in use, its version, how it was found, and how to
  choose another. Blender stays optional, and a configured path is always used
  as given. See [Setting it up](docs/REFERENCE_ASSET_COMPILER.md#setting-it-up).
- **Auto runtime budgets.** **Prepare for runtime** defaults to Auto: the
  Reference Asset Compiler decides the triangle budget from what the model is
  and how big it is (`rac budget`, and `--triangle-budget auto` on the reduce
  stage), and the panel shows its answer before anything runs. The Geometry
  panel shows the budget beside the triangle count. A typed number is still one
  step away. With a compiler that cannot decide, there is no suggestion and the
  artist types a number; the old fixed 10,000 default is gone.
- **Generated models are budgeted before they are painted.** With a compiler
  whose remesh decides budgets (9af58b3 and later), generation sends
  `--triangle-budget auto` and the model's name (a hero also `--role hero`), so
  the painter paints the final mesh. Older compilers still get 20,000 / 80,000.

### Scene setup
- **Stand-ins by hand.** The scene inspector has a stand-in kit: a person, a
  box, a cylinder, a sphere, a floor, and a wall, placed on the floor where
  the camera is looking. A stand-in's size is edited in metres. Stand-ins
  previously came only from an agent's blockout plan.
- **A posable person.** The person stand-in is a simple jointed figure at a
  chosen height, in one of nine poses named like the Sketch blocking kit's.
  Every pose rests on the floor. The pose is saved with the scene, carried
  in working packages, and frozen into scene stills.
- **Image cards.** Any library picture can stand in the scene as an upright
  card in the picture's own shape, unlit, with transparency kept for
  cutouts. A card cites the picture by id and content hash, a still waits
  for the picture before capturing, and the library counts the card as a use.
- **Handles in the view.** A selected object shows move, rotate, and scale
  handles, with snapping, Drop to floor, and Focus. Handles edit the working
  scene; Save is still the only write. Dragging empty space still orbits.
- **Look through the shot camera.** Shot setup can show the view from the
  shot camera with the delivery frame outlined. Orbiting there moves only the
  shot camera, and a narrow stage widens its view so the whole frame fits.
- Two schema migrations (v22 and v23) add one nullable column each to scene
  objects. Both back up the database first.

### Library review
- **Approve or send back any asset.** Images, models, video and audio each
  carry a per-revision decision (Pending, Approved, Changes requested) with a
  short reason and a time, set from the panel the asset opens in. Cards show it
  and the library filters by it. A new revision of something sent back starts
  Pending and shows the send-back it answers. See
  [Review](docs/ASSET_LIBRARY.md#review-approve-send-back-and-notes).
- **Review notes on every kind.** Images keep their pins; video and audio notes
  can mark a moment, shown along the player's timeline; model notes can keep
  the orbit camera they were written from; any of those can be about the whole
  asset instead.
- **Ship to game.** Ship a collection, or a picked selection, of approved assets
  to a game project as an engine-neutral bundle: the current revision's files
  plus `framewright-bundle.json` (schema `framewright.bundle.v1`: asset and
  revision ids, kind, collection, file, SHA-256, measured size and triangle
  count for models, tags, notes and the review decision). It goes to a
  destination named in `Integrations:Shipping:Targets` as a new timestamped
  folder (never over an earlier one), or downloads as a zip. Only approved work
  ships unless pending work is included; sent-back work never does. See
  [Shipping to a game](docs/GAME_BUNDLE.md).

### Fixes
- **Stand-ins of one kind no longer share a colour.** Stand-in colours came
  from a running sum of the name, which put "Box 1" and "Box 2" a degree
  apart. A mixed hash now spreads them across the wheel.
- **The scene toolbar no longer covers the stage on a tablet.** On narrow
  screens the toolbar sat 12 pixels over the bottom of the 3D view.
- **Confirmations are no longer lost.** A message that arrived just as the
  previous one expired could be cleared with it and never appear, such as
  "... queued. It keeps going if you leave this screen." right after an
  import. And when two library changes overlapped, the older one's message
  could land last and replace the newer one, so sending an image back and
  then importing its next revision ended on the send-back. Each message now
  stays its full time, and the latest change is the one announced.

## 0.1.0 — 2026-09-24, first release

The first Framewright release: a local-first production workspace for a trusted
Windows workstation, from a described shot through images, 3D scenes, animated
takes, sound and export. Status of every claim below is recorded in
[docs/goals/mvp-release.md](docs/goals/mvp-release.md); live-provider and human
checks are in [docs/RELEASE_EVIDENCE.md](docs/RELEASE_EVIDENCE.md).

### Getting started
- A portable Windows zip. Extract it and double-click **Framewright Studio.exe**.
  It starts the studio hidden, opens the browser, and explains a failed start in
  a dialog. **Stop Framewright.cmd** stops it. There is no PowerShell or
  execution-policy bypass on the everyday path.
- A new project needs only a name and a picture format (Widescreen HD or 4K,
  Cinema scope, Vertical, or custom). It opens as soon as it is created.
- A fresh studio opens on a labelled sample project to explore, with demo art
  shown only there.
- A shot starts from a sentence and a length in seconds. Camera, action and
  references are optional details.
- Image and video generation are switched on inside the app, in
  **Production setup → Image and video generation**, including a ComfyUI address
  test. A switched-off route says so before you press it, and nothing is sent
  anywhere until you turn it on.

### Creative work
- Shots, immutable versions, pinned feedback, candidate comparison and approval.
  A new version never silently replaces approved work.
- Director Mode: point at a frame or a scene object and describe intent. A
  browser agent can propose a change, which you review and apply. Proposing
  never generates or saves anything by itself.
- 3D scenes. Generate a static prop from a reference image through the Reference
  Asset Compiler, prepare it (runtime reduction, culling, texture compression,
  resurfacing), and place reusable revisions. Block a scene out from a reference,
  and replace one stand-in without moving anything else.
- Rig a prepared humanoid (UE5 Manny profile) as a candidate revision, review how
  it bends in a pose suite, and accept it. Bind compatible clips, and give rigid
  parts pivot motion.
- Render a scene still into ordinary shot review. Once it is approved, render an
  exact-frame animated take encoded with FFmpeg.
- Separate dialogue, voice and music lanes, with sequence assembly and an
  independent audio mix export.

### Safety and recovery
- Confirmation before discarding a draft or leaving an unsaved scene. A broken
  view recovers without losing the rest of the studio.
- Editable project export/import with fresh identities and verified content.
  Verified backups, with an offline restore tool in the package.
- Loopback-only by default. Provider credentials stay server-side, and the ignored
  workstation settings are never packaged.

### Known limits
- Unsigned build: Windows SmartScreen asks for confirmation on first launch.
- 3D generation and rigging need a separately installed Reference Asset Compiler
  and Blender; local voice and music need their own workers.
- The direct OpenAI key lane, YuE2 music and paired-tablet review are previews.
