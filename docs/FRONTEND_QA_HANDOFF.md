# Framewright frontend QA handoff

This is the working, page-by-page acceptance checklist for the trusted local workstation. It complements `QA_RUNBOOK.md`: that document proves the mandatory production chain, while this one tests the complete visible product as a user encounters it.

## Test record

| Field | Value |
| --- | --- |
| Candidate | `0.1.0-rc.1` |
| Starting commit | `ba4fa45ed4a7cdd87545b016c7e1c538119f3114` |
| Operator | Codex frontend QA |
| Browser targets | Desktop Chromium and tablet-sized WebKit automation; live in-app browser for visual/user-flow inspection |
| Runtime | Docker Framewright on `127.0.0.1:5179`; external ComfyUI on `127.0.0.1:8188` |
| Rule | A failure is fixed immediately, the affected path is rerun, and the exact final candidate identity is recorded by `artifacts/release/candidate-manifest.json`. |

Legend: **Not run**, **Pass**, **Fail**, or **Blocked**. “Pass” requires direct visible or persisted-state evidence, not merely a successful HTTP response.

## 1. Runtime, shell, and navigation

- [x] **Pass** — Framewright and ComfyUI start without taking ownership of unrelated GPU jobs. Port, process, GPU, and queue ownership were inspected before launch; ComfyUI remained externally owned and idle.
- [x] **Pass** — `/health/live`, `/health/ready`, Setup, Docker labels, and Git identify the same build. The pre-handoff rebuild served `0.1.0-rc.1`, the expected commit plus the honest `-dirty` suffix while the final UI fix was under test, and `release-candidate`; the clean candidate command performs the final exact-HEAD assertion.
- [x] **Pass** — Board, Assets, World, Shot, Review, and Sequence open from the rail and command palette. Workspace search now returns `Assets` as a first-class result.
- [x] **Pass** — Selected project/shot context remains correct while moving between pages. LAB-040 remained selected through Shot, Assets, Sequence, and back to Board until another card was deliberately selected.
- [x] **Pass** — Connect and Settings open the understandable production-setup drawer, including exact build identity and mandatory-versus-preview integration labels.
- [x] **Pass** — Loading, empty, degraded, error, queued, running, completed, and retry states are legible. The failed 124-frame Max attempt remained auditable, Retry created attempt 2, the successful replacement suppressed the stale failure, and the recovered shot no longer looked broken.
- [x] **Pass** — Browser console contains no uncaught application errors during the automated desktop/tablet journeys. Every journey installs an error trap and the full `73 passed / 7 intentional skips` suite completed cleanly.
- [x] **Pass** — Keyboard focus, Escape, tab order, and touch-sized controls work on principal dialogs. Command palette, candidate review, promotion disclosure, audio editor, and the 44-pixel tablet target audit all pass.

## 2. Board — shots

- [x] **Pass** — Shot cards show correct stage, version, approval, notes, duration, and thumbnail for LAB-010 through LAB-040.
- [x] **Pass** — Board filters produce the expected set without losing selection. All, Needs review, and the empty Ratified state were exercised in the live UI.
- [x] **Pass** — A new intent-only shot can be created, edited, and reopened. Codex converted one paragraph into editable production fields and LAB-040 `Lantern Vigil` was saved with six authorities and seven locked rules; command-palette retrieval remains in the final sweep.
- [x] **Pass** — Dropping/importing an image creates a real draft with an editable detail form. `LAB-050 Imported Frame QA` was created from `framewright-sketch-to-image.jpg`; it opened directly as Draft v1 with editable intent, camera, references, endpoints, and revision controls.
- [x] **Pass** — Direct generation from a shot card exposes both ComfyUI and Codex ImageGen routes. LAB-020 exposed both while Working, and desktop/tablet direct-card dispatch journeys pass.
- [x] **Pass** — Card menus and sequence ordering are understandable. LAB-040 moved earlier and returned to its original final position; the restored order survived the next workspace reload.

## 3. Board — authorities and global library

- [x] **Pass** — Authorities view distinguishes project authorities from the wider library.
- [x] **Pass** — A local authority can be created from an imported image and opened by clicking its card. `QA Lantern Compass` was created through the file chooser.
- [x] **Pass** — Global-library search/import adds only the chosen authority to the project. The deterministic desktop/tablet journey proves one selected authority is imported without leaking siblings; the live QA project already contained every available global candidate, so its dialog correctly had nothing else to add.
- [x] **Pass** — Reference-control role, identity text, locked constraints, and accent save as a new revision. The QA authority advanced from v1 to v2.
- [x] **Pass** — Revision history is immutable, selectable, and visually identifies the live authority.
- [x] **Pass** — Face, clothing, style, prop, environment, pose, and variable-identity roles are understandable. The shot reference copy and blocking-kit controls explicitly separate exact-character identity from role/wardrobe transfer and global style.

## 4. Authority detail and asset comments

- [x] **Pass** — Pins can be added to an authority image, opened, collapsed back to pins, moved, resolved, and cleared. Verified on Mara v5, including keyboard nudge and final resolved state.
- [x] **Pass** — A pinned revision request is included in the next authority generation packet. The exact-asset browser journey verifies the note and authority image enter the next revision while remaining bound to their source revision.
- [x] **Pass** — Import image and generate revision both create a new top revision without overwriting history. The QA authority retained v1/v2 and ComfyUI attached v3.
- [x] **Pass** — Fast Draft and Codex ImageGen are always available where their readiness permits. Both routes were selected from the same authority/asset flow.
- [x] **Pass** — Completed background generation appears without leaving/reopening the authority. The global progress item landed both an authority ComfyUI revision and a Codex asset revision while the operator navigated elsewhere.
- [x] **Pass** — Voice design exposes three auditions, approval, playback, and reusable lock identity. Mara Vey and QA Warden Speaker each retained a distinct approved local Qwen profile and playable authority sample.

## 5. Assets

- [x] **Pass** — Image, audio, music, and video filters show the correct assets and useful metadata. Search, Name sort, audio playback control, video detail, collections, and shot placement were exercised.
- [x] **Pass** — New still-image generation supports text direction, up to eight precision references, ComfyUI, Codex ImageGen, and a clearly marked direct-OpenAI Preview route.
- [x] **Pass** — Existing stills can be opened, commented on, revised, promoted to authority, and attached to shots. The native promotion disclosure opened and closed in the rebuilt live UI; metadata, pins, revisions, collection movement, and reversible shot placement were also exercised.
- [x] **Pass** — Imported files are content-addressed and appear immediately. Generic library import, sketch-underlay import, Board image-first creation, and all resulting media survived repeated container recreation.
- [ ] **Re-run required after YuE2 migration** — Music is now an editable composition studio in Sequence → Music. Verify the Compose → inspect/edit → render separation, revision history, ABC advanced view, and preserved legacy audio; the former MiniMax preview proof is superseded.
- [x] **Pass** — Background asset jobs remain visible globally and land on the relevant asset/revision automatically. ComfyUI and Codex jobs were queued together, exposed `1 waiting safely`, and both completed while navigating elsewhere.

## 6. World

- [x] **Pass** — World/style settings load, validate, save, and restore the production value after a temporary persistence probe.
- [x] **Pass** — Art direction, negative style guidance, physics/canon, camera/render, and continuity laws are distinct, separately editable sections rather than one undifferentiated prompt field.
- [x] **Pass** — Saved world rules visibly enter a newly prepared image manifest. The live ComfyUI request begins with the project visual language, world canon, preserve/avoid laws, and consistency protocol.
- [x] **Pass** — Shot composition remains separate from global style/world canon. IMAGE 1 is explicitly the sole composition base while the reserved Style authority occupies its own global reference slot.

## 7. Shot — intent, references, sketch, and notes

- [x] **Pass** — Intent, action, lens, framing, movement, duration, and delivery contract save and reopen correctly. A temporary LAB-040 action edit persisted and was then restored.
- [x] **Pass** — References can be attached, removed, and inspected with exact authority revision context. QA Lantern Compass was added and removed without disturbing the six intended authorities.
- [x] **Pass** — Style occupies its reserved conditioning role and pinned references retain priority. The live prepared Qwen request bound Style as IMAGE 2 and Mara identity as IMAGE 3 after the sketch base.
- [x] **Pass** — Sketch underlays, poseable humans, presets, identity/role assignments, full-body locks, duplicate, undo, redo, and clear persist correctly. LAB-040 reached sketch revision r5 through the real UI.
- [x] **Pass** — Image pins can be added, collapsed, moved, resolved, cleared, and retained on their exact version. A LAB-050 v1 note was created on the image, reopened from marker 1, collapsed, and cleared; the live counter returned from one open note to zero without changing the frame.
- [x] **Pass** — “Ask Codex” is read-only until the user explicitly chooses to implement the directing pass. LAB-020 received a live directing note; only `Make these changes` opened the regeneration packet, and cancellation submitted no job.
- [x] **Pass** — Regeneration offers ComfyUI and Codex ImageGen without requiring ratification. LAB-020 exposed both ready routes with two open notes while remaining Working.
- [x] **Pass** — Generated drafts update in place when background work completes and survive refresh. Both LAB-040 ComfyUI attempts appeared without leaving the shot; the corrected attempt remained available after reload even though visual acceptance failed its three-subject contract.

## 8. Review

- [x] **Pass** — Candidate stack, Wipe, Side by side, Overlay, previous/next, endpoint navigation, and live/ratified labels agree on LAB-010.
- [x] **Pass** — Approve, reject/candidate discard, ratify, reopen, and regenerate preserve immutable history. LAB-050 v2 was approved and protected; its intent edit opened v3, then promoting archived v2 created working v4 without overwriting any ancestor.
- [x] **Pass** — Notes belong to the exact frame/version and unresolved notes carry forward once. Exact-version markup, carry-forward, clear-one, and clear-all journeys pass on desktop and tablet.
- [x] **Pass** — Production-final action freezes the intended source and still exposes later revision work. Automated proof verifies the exact ratified frame is sent to precision generation and remains visible afterward.
- [x] **Pass** — Failed/retry states cannot silently duplicate an uncertain provider-accepted request. The first Max request failed deterministically at validation; the explicit Retry created attempt 2 with `retryOfJobId`, and the recovered result replaced—not duplicated—the current status.

## 9. First/last frames and video

- [x] **Pass** — Start Frame and optional Last Frame cards are visible, clickable, and directly viewable. LAB-010 navigated to approved v6 and v9 from the endpoint cards.
- [x] **Pass** — Last Frame creation carries style, camera/crop, character, world, and locked-rule continuity. LAB-050's live creator explicitly transferred its source image, camera/crop, one authority, world/style, and every locked rule while leaving Start Frame v4 unchanged.
- [x] **Pass** — User text can be improved before dispatch, while the original remains reviewable. Codex refined a one-sentence end-state into precise locked-camera direction, showed its rationale, and Undo restored the exact original before cancellation; no generation was submitted.
- [x] **Pass** — Start-only and Start+Last H3 paths expose 5-second low/medium/high settings correctly. Start-only and approved v6 → v9 Low routes both completed at 24fps with 124 unique motion frames; the latter visibly landed on the selected v9 composition.
- [x] **Pass** — Project resolution/aspect/frame-rate remain consistent across endpoints and video. The visible Streaming HD preset corrected the playground contract to `1920 × 1080 · 16:9 · 24fps`; the next frozen request reported that exact delivery canvas and used 864 × 480 only as the labeled Low proxy.
- [x] **Pass** — Generated video shows actual motion, remains attached after refresh, and can be promoted to Max. Retry attempt 2 produced H.264 at `1920 × 1080`, `24fps`, exactly `120` frames and `5.000s`; SHA-256 is `B75BDCA765B068718402A55C9881E06B5032DC65747373FB4616A9B8BB4FD870`. First and last extracted frames visibly match their intended source compositions.

## 10. Sequence, voices, assembly, and export

- [x] **Pass** — Shot order persists and sequence cards open the expected shot. LAB-040 moved earlier and back later with correct disabled boundary controls.
- [x] **Pass** — Two approved character voices synthesize separate dialogue clips and remain playable after refresh. Mara and the Warden queued concurrently, showed `1 waiting safely`, completed with distinct provider request/output IDs, and refreshed Sequence to `2 with media`.
- [x] **Pass** — Dialogue clips can be placed, moved, trimmed, and volume-adjusted against picture. Mara's source trim changed to `0.1s` and persisted across reload; the full desktop/tablet journey separately proves saved range-volume edits and keyboard reopening.
- [x] **Pass** — Editorial assembly plays visible motion and correctly placed speech as separate tracks. Live preview advanced from `00:00:14:00` to `00:00:15:00` while Mara's media element played from the persisted trim, and the exported 48 kHz/24-bit mono mix contains both scheduled voices.
- [ ] **Blocked** — Production package export is correctly protected until manual creative approval. The UI reports four explicit blockers: ratify five current shots, attach LAB-030's frame, approve four remaining production videos, and resolve eight open notes. The clearly labeled working package downloaded successfully; bypassing these creative gates would make the QA meaningless.

## 11. Concurrency, persistence, recovery, and responsive UX

- [x] **Pass** — Five mixed jobs queued without collision and completed in order: ComfyUI shot draft, two local Qwen voice casts, a Codex image revision, and an H3 start-only video. Every job retained a distinct provider request/output asset and the queue advanced without duplicate submission or interruption.
- [x] **Pass** — Navigation does not hide completion; global progress remained visible and both provider results attached automatically.
- [x] **Pass** — Framewright-only restart preserves durable state without touching ComfyUI. The release command contract refuses any cutover while Framewright or ComfyUI has active work, and the live persistence smoke restarted only Framewright with both queues empty.
- [x] **Pass** — Backup and diagnostic downloads completed through Setup. `framewright-backup-20260831-100105.zip` verified at 176.4 MB with 140 asset entries; archive/hash evidence is retained for final handoff.
- [x] **Pass** — Desktop and tablet layouts preserve all mandatory controls without clipping or dead zones. The full suite passed 73 journeys with 7 explicit single-target skips, including overflow, accessibility, and touch-target audits.
- [x] **Pass** — Production data, assets, completed jobs, and selected endpoints survived two Framewright-only container recreations. Counts remained 3 shots / 8 authorities / 12 jobs / 37 assets before QA-created records, and ComfyUI's queue was untouched.

## Defects and fixes

| ID | Page/path | Expected | Actual | Fix commit | Retest |
| --- | --- | --- | --- | --- | --- |
| QA-001 | Command palette | Workspace names and shots are searchable from the advertised global finder. | Typing `Assets` returned “No shots match.” | `e756150` | Focused Playwright desktop + tablet: 2/2 pass; live candidate now shows `Matching workspaces` → `Assets`. |
| QA-002 | Local voice bootstrap | An existing WinGet SoX install and the durable Qwen runtime are rediscovered from any launcher. | Re-run could miss SoX; packaged-host path virtualization and a missing worker import path broke the canary. | `e756150` | Runtime canary, five Python tests, authenticated worker health, and PID ownership contract pass. Real UI audition remains. |
| QA-003 | Review continuity preflight | A scoped report resolves for the active project, or the UI reports a useful failure. | Multiple projects caused `Sequence contains more than one element`; Review stayed on `Checking`. | `e756150` | Project-isolation API tests 4/4, TypeScript check, and live Review retest pass with 10 resolved checks. |
| QA-004 | Board plain-language shot intake | Connected Codex turns the artist's paragraph into editable shot fields. | The container ran from `/app`, which Codex rejected as an untrusted non-Git directory, so the UI always fell back despite valid ChatGPT login. | `251d556` | Boundary tests 2/2, exact container command, and live LAB-040 creation all pass. |
| QA-005 | Assets → Create new → Music | Optional music is visibly separated from the mandatory release chain. | The modal called MiniMax a curated production workflow without a Preview label or GA-boundary copy. | `7c60c58` | Added `Preview`, `Optional preview`, and non-blocking release copy; both automated coverage and rebuilt live UI pass. |
| QA-006 | Shot → Generate video | A project described as Streaming HD / 16:9 uses the expected `1920 × 1080` delivery canvas. | The playground project reported `1920 × 1088` while the modal labeled it `16:9`; that is a model-friendly canvas, not the standard HD delivery frame. | Data correction | The visible Streaming HD preset saved `1920 × 1080`; the next Start+Last packet and modal both reported the exact corrected contract. |
| QA-007 | Assets → Still image studio → Promote to authority | The promotion disclosure opens immediately and reveals the authority role and locking form. | The enabled button accepted mouse and keyboard activation but rendered no form, toast, or error in the live candidate. | `7c60c58` | Replaced the fragile state toggle with a native, keyboard-operable disclosure; automated and rebuilt live open/close retests pass. |
| QA-008 | Assets → generated video inspector | A completed shot render names the shot and its role even before manual library placement. | The H3 job completed for LAB-010, but its inspector said “Not attached to a shot yet,” making a valid review take appear orphaned. | `7c60c58` | The inspector derives immutable origin from the completed job and names the shot/route; deterministic browser proof passes after correcting its video fixture. |
| QA-009 | H3 Max promotion | The encoded production take contains exactly the shot's frozen frame count. | H3 padded a requested 120-frame render to 124 frames; strict validation correctly rejected the Max take. | `7c60c58` | Both H3 workflows trim decoded padding before encode. Live Retry attempt 2 is exactly 1920×1080, 24fps, 120 frames, and 5.000s. |
| QA-010 | Global job status after retry | A successful retry replaces the failed attempt as the current status while history remains auditable. | The completed Max take existed, but the first failure toast and Board badge still said LAB-010 was broken and physically covered timeline controls. | `a235bdb` | Superseded attempts are filtered from current Board/dock status; focused test and rebuilt live UI pass, while both jobs remain in `/api/studio` history. |

## Final disposition

**Status: Engineering QA complete; manual creative approval required.** Every engineering-owned row is Pass. Production package export remains deliberately **Blocked** by the explicit user-owned shot, note, and visual-approval gates listed above. The prepared Start+Last pair, exact Max render, reversible Codex refinement, two local voices, and protected working package prove the mandatory engineering path without pretending that creative approval belongs to automation.
