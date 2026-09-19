# YuE2 music composition

Framewright treats YuE2 as a renderer for an editable composition, not as a
prompt-to-audio button.

```text
artist intent
  → Codex composition proposal (lyrics, sections, harmony intent)
  → YuE2 full symbolic plan (ABC melody and chords)
  → human inspection and immutable revision
  → YuE2 render
  → FLAC asset associated with that exact revision
```

The application owns the composition records. The YuE2 service owns inference
artifacts under an ignored workstation directory. Existing MiniMax-generated or
imported audio remains playable in Assets and on the Music lane, but it does not
gain composition metadata retroactively.

## What is stored

Each composition revision preserves:

- title, description, style, performance direction, tempo, meter, and key;
- ordered sections with lyrics, bars, chord symbols, and melody intent;
- the exact ABC score submitted to YuE2;
- a content hash, parent revision, edit summary, and creation time; and
- zero or more renders, each with its asset, job, model settings, seed, and
  native YuE2 artifact manifest.

Saving never overwrites the only score. AI editing receives the current
composition, ABC, and one bounded instruction, then returns a complete candidate
revision. The server rejects stale editors.

The friendly editor is wired to the performed score: tempo, key, chord, section
order, and bar-count changes synchronize into a new ABC while reusing existing
melody bars wherever possible. New sections reuse a prior motif until the artist
changes their notes. Exact note-level melody edits use the bounded Codex editor
or the Advanced ABC view; the prose melody-intent field is documentation, not a
secret second notation format.

YuE2 editing creates a new complete recording. Preserving unchanged notes or
sections does **not** promise identical singing, timbre, accompaniment, or
waveform outside the changed passage. Compare the old and new renders by ear.

## Local service

The worker at `tools/yue2/yue2_service.py` wraps the official `yue2` Python API.
It runs one GPU job at a time and exposes:

- `GET /health`
- `POST /compose`
- `POST /render`
- `GET /jobs/{id}`
- `POST /jobs/{id}/cancel`
- `GET /jobs/{id}/audio`

The worker binds to loopback without a token. Binding beyond loopback requires
`YUE2_WORKER_TOKEN`; Framewright sends it through the ignored local environment.
Framewright accepts ordinary HTTP only for loopback and `host.docker.internal`;
an explicitly enabled remote worker must use HTTPS.
Request bodies are bounded, output paths are worker-owned, and audio retrieval
can only address a completed known job. Unsafe request IDs are rejected, the
active queue is capped, and the launcher verifies PID ownership before stopping
or restarting a process.

Native artifact directories intentionally contain the complete lyrics, style,
score, model settings, and audio for reproducibility. Treat the configured
output directory as private production data even though it is excluded from
Git.

Cancellation is honest: queued jobs stop immediately. A cancellation requested
inside a monolithic YuE2 model call is recorded and takes effect when that call
returns; the service does not claim that Python can pre-empt CUDA safely.

## Setup

Use Python 3.12 and pin an audited commit of the official YuE repository:

```powershell
.\scripts\setup-yue2-worker.ps1 `
  -InstallRuntime `
  -YuERevision <official-commit-sha>
```

That creates `.yue2-runtime`, which is ignored. It does not authorize model
downloads. To permit the official runtime to obtain missing snapshots, repeat
with `-AllowModelDownloads` only after reviewing the model/VAE sizes and license.
Local model directories are preferred for an offline workstation.

Start and stop the worker explicitly:

```powershell
.\scripts\start-yue2-worker.ps1
.\scripts\stop-yue2-worker.ps1
```

Then enable the lane only after the read-only health check succeeds. With
downloads disabled, health requires both configured snapshots to be present
locally; it resolves cache metadata without loading GPU weights or using the
network:

```powershell
.\scripts\setup.ps1 -Mode Docker -EnableYuE2 -Launch
```

Docker setup writes a generated 256-bit worker token only to the ignored
`.env`, refuses to disturb a busy YuE2 queue, and restarts an idle worker once
so the container can reach the authenticated host endpoint.

The guided `$framewright-setup` skill asks where the runtime and model snapshots
live, explains downloads before approval, performs only `/health` discovery,
and opens Framewright's Setup drawer without generating a canary song.

## Configuration

Tracked defaults fail closed:

```text
YuE2__Enabled=false
YuE2__Endpoint=http://127.0.0.1:5182
YuE2__Model=m-a-p/YuE2-3B
YuE2__Vae=m-a-p/YuE2-Vae
YuE2__Device=cuda
```

Docker uses the equivalent `FRAMEWRIGHT_YUE2_*` values and reaches the host
worker through `host.docker.internal`. Tokens and local paths belong only in the
ignored `.env` or `appsettings.Local.json`.

## Error and progress contract

Framewright reports named lifecycle phases—Queued, Preparing composition,
Rendering, Processing audio, Complete, or Failed—and never invents percentages
from elapsed time. CUDA OOM, missing checkpoints, malformed ABC, unavailable
service, timeouts, and cancellation remain distinct operator-facing failures.

The supported baseline is one request at a time on a BF16-capable NVIDIA GPU
with 24 GB VRAM. Framewright does not silently shorten a song or reduce quality
to disguise an out-of-memory condition.
