# Framewright release evidence

Framewright's automated gate proves deterministic application contracts. It does not pretend that a mocked provider, a disabled GPU test, or a skipped canary proves the complete production pipeline. Before tagging a trusted-local-artist release, record the real workstation evidence below against the exact commit and packaged build.

## Automated release gate

| Contract | Evidence |
| --- | --- |
| Frozen reference order, roles, image bindings, and text-only reporting | Backend adapter and reference-correctness tests |
| Content-identical output does not create a false revision | Backend no-change contract tests |
| Persistent FIFO claims, active-lease exclusion, expired-lease recovery, and explicit retry | Durable queue tests using SQLite |
| Voice requests queue before provider work and attach completed media | Voice synthesis contract tests |
| Additive schema ledger and verified pre-migration backup | Schema migration tests |
| Backup inventory/hash verification and recovery behavior | Backup operation tests, restore script canaries, isolated-workspace round trip of rig/clip/motion/annotation/revision/still lineage, and packaged export/import -> backup -> offline restore -> restart smoke |
| Max-video binding required for video approval | Video ratification/export contract test |
| Deterministic scene take recovery and promotion | Exact-frame scene-render restart, frame-integrity, FFmpeg-boundary, retry-lineage, decoded-media, browser-action, and portable fresh-ID import/restart/playback tests |
| Reusable scene-asset discovery | Project-scoped API and desktop/tablet browser tests prove bounded search returns only current, non-archived model revisions and starts no import, placement, generation, or job |
| Browser navigation and fast draft UX | Default Playwright suite using isolated local state and mocked provider boundaries |
| Frontend type, lint, formatting, and production bundle | `npm run check` and `npm run build` |
| Reproducible API container | Locked restore plus Docker image build/smoke |
| Exact candidate provenance | Compiled version/commit/build time, OCI labels, runtime identity, and guarded cutover contracts |
| Redacted QA evidence | Workstation-only checksummed diagnostics contract and browser download journey |

## Mandatory core acceptance

Fill this table against the exact candidate recorded in `candidate.json`. Any result other than **Pass** blocks `v0.1.0` even when automated tests are green.

| Workflow leg | Required proof | Result |
| --- | --- | --- |
| ComfyUI draft | Reference-bearing draft completes, appears without navigation, and survives restart | Not run |
| ComfyUI revision | Current frame, original authorities, pinned notes, and style are visible in the frozen packet and affect output | Not run |
| Codex ImageGen | Queued image completes through the signed-in Codex path and appears without navigation | Not run |
| Visual consistency reconciliation | Image-bound Codex audit catches a deliberate visible contradiction; Fix/Adopt/Later choices persist; structural repair uses the sketch; production final stays locked until a fresh audit is Clear | Not run |
| First and optional Last Frame | Both endpoints are visible/editable and source/crop/style contracts remain compatible | Not run |
| H3 Low/Medium proxy | Five-second visual-only take has visible motion and preserves endpoints | Not run |
| H3 Max promotion | Reviewed take promotes with the same take id/seed, binds to the shot, and can be ratified | Not run |
| Qwen voice design | Three local auditions render, survive navigation, and one can be approved to the character card | Not run |
| Qwen timeline synthesis | Two character voices render, attach to clips, play, and remain after restart | Not run |
| Audio/video assembly | Both voices remain separate editorial media and the final review file has expected sound and motion | Not run |
| Concurrent work | Four mixed image/audio jobs complete serially or safely in parallel without collision or invisible results | Not run |
| Restart/resume | Kill after queue and before provider submission; restart recovers once. Kill after provider acceptance; job requires explicit retry unless reconnectable | Not run |
| Backup/restore | Restore a verified backup into a staged root; confirm exact asset snapshot and database integrity after restart | Not run |
| Production package | All shots ratified with bound Max takes, all required voice cues rendered, hashes verified, package reopens from inventory | Not run |

## Optional preview acceptance

These capabilities remain available but do not block `v0.1.0`. Record them honestly; **Not run** means preview, not implicit success.

| Workflow leg | Required proof | Result |
| --- | --- | --- |
| OpenAI GPT Image | Direct-key precision route completes with the same frozen references | Not run |
| Music generation | Music queues independently, plays, and can be placed on the Music lane | Not run |
| Paired tablet | Pair, review, add/move/resolve notes, and observe desktop convergence on the trusted LAN | Not run |

## Recording a candidate

Run `./scripts/prepare-release-candidate.ps1` from a clean, pushed `main`. It records the commit SHA, compiled build identity, container image ID and labels, verified backup hash, data fingerprint, diagnostic hash, and provider readiness blockers under `artifacts/release/` without submitting provider work.

During manual QA, add the GPU/driver, ComfyUI workflow revision, operator, result, job IDs, and artifact locations using [the QA runbook](QA_RUNBOOK.md). Preserve failure screenshots and job IDs as evidence; do not replace a failed row with a prose assurance. The secret order is fond of optimism, but not in release ledgers.
