# Goal: Ship the Framewright MVP

**Project:** Framewright  
**Created:** 2026-09-23  
**Branch:** `mvp-release`  
**Overall status:** IN_PROGRESS  
**Active milestone:** R06

**Implementation authority:** Existing repository and scoped `AGENTS.md` instructions remain in force. This goal does not authorize provider calls, GPU work, downloads, pushing, tagging, or publishing; each still needs the artist's explicit go-ahead.

## 1. Outcome

A first-time artist installs Framewright, starts their own project, and reaches an approved first frame without documentation, a terminal, or an agent. The 3D path (model, scene, shot, animated take) ships in the same release.

**Release measure:** someone who has never used Framewright gets from install to an approved first frame in under ten minutes, unaided. This is a recorded human check (evidence label **V**), not something automation can claim.

### Scope decisions

- 2026-09-23: The artist chose to ship with 3D rather than labelling it Preview. The 3D critical path (M09 → M14 → M17 → M18 in [director-mode-3d-scenes.md](director-mode-3d-scenes.md)) is therefore a release dependency. That goal keeps authority over its own milestones; this file only tracks that they are done.

### Non-goals

- A light theme, a new generation provider, or a redesign of the image, voice, music or sequence workflows beyond the friction listed here.
- Large refactors for their own sake. `StudioWorkspaces.tsx` is split only where a milestone is already changing that code.

## 2. Findings that drive this goal

These come from a first-run walkthrough on an isolated data root (2026-09-23, commit `7c3fab8`) plus a source and documentation audit.

1. A fresh install opens inside the seeded demo project, which isn't labelled as a sample. There is no welcome or start path.
2. The Create Project dialog's fields are unstyled, because the field rules in `studio.css` only apply inside other dialog classes. It needs 8 fields, including pixel sizes. Required fields have placeholders that look like filled-in values. The disabled Create button gives no reason. After creation the app stays on the old project.
3. A new shot needs title, description, action and camera. Duration is in frames. The empty board has no button. A new shot shows placeholder art from the demo as its "Sketch v1".
4. Generate stays clickable with no provider configured and fails with "Submission is off. Read-only discovery cannot alter the existing queue.", with no route to setup.
5. The setup drawer cannot enable ComfyUI or change its endpoint. It leads with database and path diagnostics, and reports Codex "Ready… for explicit ImageGen jobs" even when that lane is off.
6. Terminology drifts: shot/card/slot, version/revision/candidate/take, ratify/approve/promote, authority/reference.
7. Silent loss and dead ends: the Delete key removes a draft without confirmation, switching scenes drops unsaved edits, the Music button only closes its dialog, the Sequence view shows hard-coded demo text, and Connect always shows a green dot.
8. There is no React error boundary. The 700 ms full-snapshot polling causes visible flicker. Error toasts disappear too quickly to read.
9. Shipping: there is no downloadable artifact, the Start Menu shortcut runs PowerShell with `-ExecutionPolicy Bypass`, there are no tags or changelog, the README overclaims readiness, and every mandatory acceptance row in `RELEASE_EVIDENCE.md` is "Not run".

## 3. Milestones

Evidence labels follow the 3D goal: **D** deterministic, **A** actual application and persistence, **H** actual host, **L** authorized live provider, **V** recorded human acceptance, **P** packaged runtime.

| ID | Outcome | Status |
| --- | --- | --- |
| R01 | New project: styled, only a name required, format presets instead of pixel sizes, opens the project it created | VERIFIED |
| R02 | First run: a welcome that offers "start your own" or "explore the sample"; the sample is labelled as one | VERIFIED |
| R03 | New shot: one description is enough, duration in seconds, a button on the empty board, and a blank frame instead of borrowed demo art | VERIFIED |
| R04 | Generation readiness: generate actions say plainly when nothing is set up, and link to setup | VERIFIED |
| R05 | In-app generation setup: the ComfyUI endpoint, a connection test and lane switches without scripts or JSON; the setup drawer leads with what the artist needs | VERIFIED |
| R06 | One word for each concept across the UI | NOT_STARTED |
| R07 | No silent loss or dead ends (the item 7 findings) | NOT_STARTED |
| R08 | Hardening: an error boundary, polling that doesn't flicker, readable errors | NOT_STARTED |
| R09 | 3D release path: M09, M14, M17 and M18 closed in their own goal | NOT_STARTED |
| R10 | Ship: a downloadable build, version and changelog, accurate docs, `verify.ps1`, the mandatory acceptance rows, and the hallway test | NOT_STARTED |

## 4. Acceptance records

### R01 acceptance record

```text
Milestone / status / date: R01 / VERIFIED / 2026-09-23
Outcome: New project asks for a name only. Production defaults to the name,
  sequence to SQ-01 "Sequence 1". Format is one of four named presets (Widescreen
  HD default, Widescreen 4K, Cinema scope, Vertical) or Custom with the pixel
  fields. Create is never silently disabled: an empty name explains itself and
  refocuses. After creation the dialog activates the project (a second explicit
  call; the server still creates projects inactive) and the board opens on it.
  The dialog's fields are styled. Two leaking header rules were also fixed:
  `.project-identity p` uppercased every paragraph in the create/delete dialogs,
  and below 860px it hid them, including the delete dialog's "This removes..."
  scope text. Both are now scoped to `.project-switch p`.
Evidence: D/A. e2e/first-run.spec.ts, 2 tests x desktop + iPad = 4 pass, and
  the related studio/portable journeys pass (9). `npm run check` passes. Visual
  inspection at 1440x900 and 834x1194 on an isolated data root.
Bug detection: removing the activation call fails at the project-switcher
  assertion; making dialog labels inline fails the layout assertion; restoring
  the `.project-identity p { display: none }` compact rule fails the phone-width
  note visibility assertion.
Limits: Setup drawer presets now share the same list (four instead of three,
  "Streaming" renamed "Widescreen"). Import package still leaves the imported
  project inactive; that is deliberate (import is not a request to switch).
Tested: branch mvp-release on 7c3fab8 plus this change, Windows 11,
  Node 22.15.0, Playwright Chromium desktop and WebKit iPad Pro 11.
```

### R02 acceptance record

```text
Milestone / status / date: R02 / VERIFIED / 2026-09-23
Outcome: Projects carry IsSample, set only when a database this run created
  seeds the demo. Migration v20 adds it as false, so upgraded workstations
  (whose first project may be the artist's renamed work) are never labelled,
  and a project re-seeded after all its shots were deleted is not relabelled.
  Imports always clear it. The sample shows a "Sample" tag in the switcher and
  menu, plus a board welcome with "Start your own project" (opens the create
  dialog) and "Keep exploring" (dismissal remembered per browser). The
  project menu now closes on Escape and returns focus; before, its scrim
  swallowed the next click.
Evidence: D/A. SampleProjectTests (4, backend): fresh database, upgraded
  workstation, reseed after deleting shots, and an imported package that
  claims isSample (re-hashed inventory). first-run.spec "fresh studio" on
  desktop and iPad. Full suites at the R02 checkpoint: 301 backend, 154
  browser passed, 6 skipped (existing desktop-only skips). Visual check on a
  fresh isolated data root.
Bug detection: dropping the fresh-database guard fails the reseed test;
  dropping the import reset fails the import test (it passed before the test
  was made to claim the flag, which is why it now does); suppressing the
  welcome fails the journey; removing the Escape handler fails the journey.
Limits: The welcome's dismissal is per browser (localStorage), by design.
```

Full-suite state at the R02-R05 checkpoint: backend 307 passed; browser 158
passed, 6 skipped. Two failed: the iPad overlap above (then fixed; the iPad
studio journeys rerun 37 passed, 6 skipped), and one transient
`net::ERR_NO_BUFFER_SPACE` console error. That error was a Windows socket
buffer exhaustion, seen once in each of two runs in different tests and
unrelated to their assertions. It is recorded here as environmental, not
waived; watch for it in R10's gate run.

### R03 acceptance record

```text
Milestone / status / date: R03 / VERIFIED / 2026-09-23
Outcome: A new shot needs only "Describe the shot". The title defaults to the
  first sentence (trimmed with an ellipsis at a word boundary past 60
  characters), and length is in seconds (stored as frames at the project
  rate, default 3 s). Camera, action, references, locked details and shot code
  are folded under "Camera, action and references". The server now accepts
  empty camera and action (still length-limited); the prompt builders already
  skipped empty values, and the two feedback-regeneration briefs no longer
  write "Camera: ." when they are empty. "Suggest details with Codex" works
  from the artist's description and never overwrites it. The empty board has
  an "Add your first shot" button. Outside the sample, placeholders are an
  honest "No image yet" frame instead of the demo illustration. The video
  start/end strip stays hidden until the shot has an image.
Evidence: D/A. first-run.spec "first shot" on desktop and iPad asserts the
  persisted title, 108 frames for 4.5 s at 24 fps, empty camera and action,
  the empty frame, and no video region. Existing shot-form journeys were
  updated to the new labels and pass.
Bug detection: forcing the demo illustration everywhere fails the empty-frame
  assertion.
Limits: The board's "Add card" wording is left for R06.
```

### R04 acceptance record

```text
Milestone / status / date: R04 / VERIFIED / 2026-09-23
Outcome: The one-click generate buttons (Generate fast draft, Generate with
  Codex, and the Regenerate with Codex / Fast local pass pair) read
  /api/generation/adapters and are disabled when their engine cannot
  dispatch. The server refuses those requests anyway, so nothing that worked
  is lost. A notice beside them names what is missing and opens Production
  setup; when neither engine is ready it says sketching and dropping an image
  still work. The sketch lab keeps its button (it still freezes a manifest
  and refuses honestly) but its footer now reports the selected engine's
  real state instead of always "Ready to generate". Engine messages are in
  plain language ("ComfyUI image generation is turned off. Turn it on in
  Production setup...").
Evidence: D/A. first-run.spec "no generator" on desktop and iPad. Wiring
  journeys that mock the generate request now also mock ready engines.
Bug detection: removing the fast-draft gate fails the toBeDisabled assertion.
Full-suite regressions caught and fixed: on iPad the inspector is a 280-320px
  bottom sheet, and the notice pushed the generation panel over the controls
  above it. Below 860px the notice is now one line and the generation panel
  is capped at 150px and scrolls. Removing the cap fails two iPad journeys
  (Codex directing advice, director mode), reproduced by running the iPad
  studio journeys in suite order. Two existing journeys
  were brittle: one dragged from raw coordinates without scrolling the
  inspector (it now scrolls to the card first), and one asserted a source
  message that no longer exists and only passed when an earlier journey had
  already given SH-030 an image (it now accepts each current explanation).
```

### R05 acceptance record

```text
Milestone / status / date: R05 / VERIFIED / 2026-09-23
Outcome: Production setup leads with "Image and video generation". The
  ComfyUI card has the address, a Test connection button (read-only GET
  /queue, 5 s) and switches for draft images and video. The Codex card has a
  one-click images switch. The OpenAI key sits in its own card. The project
  contract, safety copies and tablet access follow; workstation readiness,
  build identity and the production guard are folded under Diagnostics.
  Switches persist to generation-settings.json in the data root, read by
  GenerationSettingsStore at every use (no restart). A value set by an
  environment variable or the command line wins and is shown locked, and an
  unreadable file fails closed. Changing or probing needs a trusted local
  caller and the studio header; a paired tablet sees state read-only.
  Remote ComfyUI addresses still need the workstation AllowRemote opt-in.
  Safety: the e2e server now points Codex at an absent executable, so no
  browser journey can reach a developer's signed-in Codex CLI.
Evidence: D/A. GenerationSetupTests (6): defaults off, a save changes the
  adapter state and survives a restart, header and loopback refusal, paired
  tablet read-only, unreachable address explained, launcher pin wins and is
  locked, corrupt file fails closed. first-run.spec "no generator" drives the
  drawer: ComfyUI switches locked by the harness, Codex switch saves and the
  server reports it. Visual check at 1440x900 and 834x1194.
Bug detection: ignoring launcher pins, dropping the header/local check, and
  ignoring the saved file each fail their tests.
Full-suite regression caught and fixed: each switch's accessible name
  included its description, so it answered to "API key". Switches are now
  named by their label, with the detail in aria-describedby.
Limits: Workflow file paths and AllowRemote remain file or launcher
  configuration. Test connection was not run against the artist's real
  ComfyUI (not authorised); discovery already probes the configured address
  when setup opens, as before.
```

### Hardening landed early (R08)

```text
The SPA fallback served "/" and every client route without Cache-Control,
so browsers kept an index.html naming the previous build's hashed assets and
an updated studio opened blank (reproduced twice in this session). The
fallback now sends no-cache, matching the static file middleware.
first-run.spec asserts it for "/" and a client route.
```
