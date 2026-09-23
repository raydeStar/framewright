# Goal: Ship the Framewright MVP

**Project:** Framewright  
**Created:** 2026-09-23  
**Branch:** `mvp-release`  
**Overall status:** IN_PROGRESS  
**Active milestone:** R02

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
| R02 | First run: a welcome that offers "start your own" or "explore the sample"; the sample is labelled as one | NOT_STARTED |
| R03 | New shot: one description is enough, duration in seconds, a button on the empty board, and a blank frame instead of borrowed demo art | NOT_STARTED |
| R04 | Generation readiness: generate actions say plainly when nothing is set up, and link to setup | NOT_STARTED |
| R05 | In-app generation setup: the ComfyUI endpoint, a connection test and lane switches without scripts or JSON; the setup drawer leads with what the artist needs | NOT_STARTED |
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
