# MOSS sound effects — Phase 1 evidence ledger

## Contract and scope

Deliver an optional local batch CLI and reusable core for MOSS-SoundEffect v2.0,
with offline inspection, deterministic planning, standard-library DSP, immutable
WAV runs and honest provenance. This explicitly requested tooling milestone is
not an application creative workflow. Phase 2 (HTTP worker, shared app jobs,
library/Ship to game and Effects UI) awaits the owner's separate go-ahead.

This adds an explicit CLI provider-dispatch route only. Existing composition,
identity, wardrobe, style/world authority, review state and YuE2/voice/ComfyUI
routes are unaffected. The game repository does not exist yet; the owner asked
to keep a local handoff note instead of changing another repository.

## Baseline and authorization

- Started November 11, 2026, before the stated 22:00 America/Denver jam opening.
- Normal branch `feat/moss-sfx` from `main` at
  `58f61ed1e5fb304d96af99c3df500243be1b139f`; remote main matched at discovery.
- Preserved an existing line-ending-only `src/storyboard-studio-web/package.json`
  change; it is excluded from this feature's commits.
- Explicit owner approval received for isolated runtime install, the 11.2 GB
  snapshot download, and only the wooden-block smoke test, repeated for timing.
- The owner subsequently requested five fantasy RPG examples for a separate
  project, explicitly authorizing that local generation before the jam. The jam
  starter list remains ungenerated under ignored `.framewright/moss-sfx/`;
  no game prompts or generated media are published here.
- The owner also authorized fixing the intermittent browser journeys before
  publication. Existing save/reopen acceptance remains enforced.
- Local SemaForge status was unreachable during discovery; this file records
  implementation evidence without claiming a synchronized external ledger.

## Acceptance and current evidence

| Check | Evidence / status |
| --- | --- |
| Schema, seeds, paths, DSP, failure manifests, offline inspection | 31 focused stdlib tests passed in the final gate at `f83e637`; one opt-in GPU test skipped under the gate interpreter |
| Same gate interpreter | Python 3.12.10 embedded interpreter selected by existing YuE2 gate; no dependency added to it |
| Runtime | Isolated Python 3.12.7; upstream commit `934d6826b084c46a0d033402174d5f8ac4ed2519` verified clean; install succeeded, `pip check` passed |
| GPU commissioning | Passed on `d10a593`, Windows 11 / RTX 4090 24 GB, Python 3.12.7, Torch 2.9.0+cu128; compile off; downloads off; approved wooden-block smoke plus five later owner-requested examples |
| Full repository gate | **Passed** at code revision `f83e637` on Windows 11: frontend checks/build (51 unit tests), 433 backend tests, script/Python checks, dependency audits, and 214 browser passes with 6 existing skips and zero failures. Browser duration 9.9 minutes. Earlier failures and their repairs are documented below |
| Public release-content audit | Passed against tracked and non-ignored candidate files |
| Windows native DSP/CLI evidence | Focused tests; installed-runtime health reports ready with Torch absent from `sys.modules`; actual starter-list dry-run prints 44 takes and creates no output folder |
| Application/UI, packaged app, library import | Not implemented in Phase 1; no app workflow acceptance claimed |
| Human listening / artistic acceptance | Pending owner audition; automatic tests do not establish suitability for the game |

## Runtime receipt and limits

Smoke completed at **2026-11-11 22:42:59 UTC / 15:42:59 America/Denver**, before
the stated 22:00 local jam opening. Load including imports: **33.2789 seconds**;
first inference: **13.8225 seconds**; warm inference: **11.9432 seconds**. Both
calls returned 96,000 samples (2.000 seconds), non-silent mono audio exported
as 48 kHz, 16-bit PCM WAV. The already-generated first take also passed the
production batch/DSP/manifest path, with no third GPU call. Its post-trim output
is 1.125917 seconds, peak −1.000152 dBFS, RMS −30.436563 dBFS.

**Same-seed audio is not bit-exact on this setup.** Raw float32 and PCM WAV
SHA-256 both differed between calls. The requested shape/non-silence fallback
passed; deterministic seed assignment does not promise identical audio bytes.

Ignored receipt and audio:
`.moss-sfx-runtime/smoke/2026-11-11T22-42-33Z/smoke-report.json`, with the
processed `sfx-manifest.json` under `processed/2026-11-11T22-42-59Z/`.
No jam starter sound was generated. Compile-on behavior, other GPUs/platforms,
subjective sound quality, Unreal import and Phase 2 application integration
remain unverified or unimplemented. No application package/runtime was changed.

## Checkpoint

Code checkpoints: `d292c68` (core, CLI, tests, ignore and verification integration)
and `d10a593` (explicitly approved downloads can repair an incomplete cached
snapshot, with a contract test).
Model download: approved 11,230,170,282-byte snapshot
`e35df4d82fbe87fcd5d14e5d100e349c0c3c076d` is complete. Whole-file HTTP stalled
before weight bytes arrived; a bounded HTTP-range fallback completed, and all
four model weight files plus the LFS tokenizer passed their published SHA-256
hashes. The existing tokenizer was preserved after checking its hash. No extra
package was added for transport. Receipt: ignored
`.moss-sfx-runtime/model-receipt.json`.

The Phase 1 core/CLI is contract-tested, live-verified, and passes the required
local repository gate. The owner's condition for draft publication is satisfied;
do not merge. Phase 2 remains unstarted. This is not artistic acceptance or a
claim that the unimplemented application integration is release-ready.

Local evidence (ignored, workstation-owned): `.tmp/moss-sfx/verify-repaired.log`
(successful final gate), `.tmp/moss-sfx/unit-tests.log`,
`verify.log`, `verify-final.log`, `effects-targeted.log`, and
`initial-tablet-failure/trace.zip`, `final-tablet-scene-failure/trace.zip`, and
`scene-targeted.log`. The initial WebKit trace reported an access
control page error around reload while recorded asset responses were HTTP 200.
That Effects journey passed unchanged on both browsers when isolated, and in
the second full run. The second full run failed the tablet scene-instance
reload journey at `scenes.spec.ts:137`, where the position X editor was absent
at the 8-second assertion timeout; it had passed in the first full run.
That scene journey subsequently passed unchanged in isolation on both desktop
and tablet (2/2). These targeted passes do not turn either failed full run into
a green gate.
Subsequent inspection found that this installed Windows WebKit build has no
`AudioContext`, while Chromium successfully decoded the same 100 ms PCM tone.
The waveform component previously fetched audio before attempting the missing
constructor. The regression assertion reproduced two unnecessary fetches on
tablet (`waveform-regression-before.log`); the capability guard now retains the
existing visual guide without that fetch. The Effects journey uses a realistic
PCM fixture, waits for decoded waveforms where supported, asserts no preview
fetch when unsupported, and gives repeated runs unique labels in the shared
disposable database. Console assertions are retained without new exclusions.

The scene journey now waits for the lazy viewport and both objects before
selecting from the inspector, uses a real tap on tablet, and checks the selected
button's pressed state. Its inspector sits below the changing viewport on
tablet; the precise timing of the original missed click remains unproven.
All transform persistence and scene-graph assertions remain. Six repeated
scene runs and six repeated Effects runs passed (three per browser per journey),
recorded in `stable-journeys.log` and `stable-effects-fixed.log`. The first
Effects repetition attempt exposed duplicate labels in the shared test database;
unique repetition labels fix that test isolation issue. Browser repairs are
checkpointed at `f83e637`; the final full gate passed both changes together.
No browser tests were removed and no new skips or console exclusions were added.

## Owner-requested local examples

Five separate fantasy RPG examples completed at 2026-11-11 22:52:40 UTC, using
the same pinned runtime with downloads and compilation disabled. All five
manifest hashes, WAV headers, final durations and non-silent/unclipped sample
data were independently verified. Local files and rerun instructions live in
`.moss-sfx-runtime/examples/aether-wars/README.md`; the immutable run is
`2026-11-11T22-50-59Z/`. These are not jam starter assets and were not imported
into Unreal. The ambience's transient peaks limited its RMS below the requested
target under the -1 dBFS peak ceiling. Human listening remains the acceptance
boundary.
