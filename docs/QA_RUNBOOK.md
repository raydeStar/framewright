# Framewright `v0.1.0-rc.1` manual QA runbook

Use this runbook only after `scripts/prepare-release-candidate.ps1` succeeds. Confirm that the Setup drawer, `/health/live`, `candidate.json`, and the Docker image label all show the same full commit before judging creative output.

For every row record **Pass**, **Fail**, or **Blocked**, plus the Framewright job ID, relevant artifact hash/path, screenshot, and a short observation. A mandatory failure requires a fix and a new RC; do not retest a changed build under the old candidate record.

## Candidate record

| Field | Value |
| --- | --- |
| Operator / date |  |
| Version / commit |  |
| Candidate manifest |  |
| Diagnostic bundle / SHA-256 |  |
| Backup / SHA-256 |  |
| Docker image ID / digest |  |
| GPU / driver |  |
| ComfyUI revision and workflow hashes |  |
| Voice model lock hash |  |

## Mandatory front-to-back pass

| # | Action and expected result | Result | Evidence |
| --- | --- | --- | --- |
| 1 | Open Setup. Build identity matches the candidate; readiness is not **NotReady**; ComfyUI and Codex can submit; local voice is ready. |  |  |
| 2 | Create or choose one two-character scene. Attach exact character/wardrobe/style references and create visible spatial notes. |  |  |
| 3 | Generate a ComfyUI draft. It appears without navigation, visibly respects the composition and style authorities, and remains after refresh. |  |  |
| 4 | Add a concrete revision note and regenerate through ComfyUI. The current frame remains the base; the requested change is visible; unmentioned content remains stable. |  |  |
| 5 | Run **Codex visual check** on the exact draft. Confirm it catches a deliberate subject-count or staging contradiction. Resolve every finding with **Fix image**, **Adopt image**, or **Decide later**, and verify optional **More details** is preserved. Structural fixes rebuild from the sketch; adopting updates only the shot contract. Production final remains locked until a fresh check is Clear. |  |  |
| 6 | Run a Codex ImageGen precision revision. It appears automatically, preserves frozen references, and creates a distinct reviewable revision. |  |  |
| 7 | Select a Start Frame and create an optional Last Frame from it. Both are directly viewable/editable and retain project aspect, crop, style, and character continuity. |  |  |
| 8 | Generate a five-second Low or Medium H3 take with only the Start Frame. It contains visible motion, has the project aspect and frame rate, and remains attached after refresh. |  |  |
| 9 | Generate a five-second take with Start and Last Frames, then promote the reviewed take to Max. Endpoint continuity, take/seed identity, encoded resolution, and shot binding remain exact. |  |  |
| 10 | Design three local auditions for each of two characters, approve one per card, synthesize both dialogue clips, navigate away, return, and play both. |  |  |
| 11 | Assemble video and two voice clips. The final review file has visible motion and audible, correctly placed dialogue; audio remains editorially separate from generated video. |  |  |
| 12 | Queue four mixed image/voice jobs. They complete without collision, duplication, invisible results, or another job's output being claimed. |  |  |
| 13 | With a safe disposable job queued before provider submission, restart Framewright only. It recovers once. A provider-accepted uncertain job requires explicit retry rather than silent duplication. |  |  |
| 14 | Ratify the final shot and Max take, resolve current notes, export the production package, verify its inventory/hashes, and reopen it from the recorded inventory. |  |  |
| 15 | Restore the verified pre-cutover backup into a staged root and confirm database integrity and exact asset inventory without touching the live data root. |  |  |

## Optional preview observations

| Capability | Result | Evidence / notes |
| --- | --- | --- |
| Direct-key OpenAI GPT Image | Not run |  |
| Music generation and placement | Not run |  |
| Paired-tablet review convergence | Not run |  |

## Failure capture

Keep the candidate manifest and diagnostic ZIP unchanged. Record the failing job ID, provider request ID when available, Framewright screenshot, provider queue/history screenshot, expected behavior, actual behavior, and whether retrying could duplicate paid or GPU work. Do not resolve a mandatory failure by changing configuration without recording the changed workflow/model hash.
