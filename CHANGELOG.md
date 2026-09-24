# Changelog

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
