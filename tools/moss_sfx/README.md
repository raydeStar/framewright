# Local sound effects with MOSS-SoundEffect v2.0

Phase 1 is an optional batch tool, separate from the Framewright application.
It exports mono 48 kHz, 16-bit PCM WAVs with provenance. There is no HTTP worker,
Effects-lane action, library import, or Ship to game integration yet; those are
Phase 2 and require a separate go-ahead. Import these WAVs into Unreal manually.
See the [Phase 1 evidence ledger](../../docs/goals/moss-sfx-phase1.md) for the
commissioned runtime, smoke timings, repeatability limits and repository gate
results.

## Isolated setup (explicit approval required)

Use **Python 3.12** on a CUDA-capable workstation. The upstream package declares
Python >=3.12; its pinned NumPy 1.26.4 makes 3.12 the practical installation
target. Keep this runtime separate from voice, YuE2, and ComfyUI.

The [official model Files tab](https://huggingface.co/OpenMOSS-Team/MOSS-SoundEffect-v2.0/tree/main)
lists approximately **11.2 GB**: transformer 5.66 GB, text encoder 4.06 GB, VAE
1.49 GB. The Windows CPython 3.12 PyTorch CUDA 12.8 wheel is 2.86 GB, plus other
dependencies. Allow roughly 25–35 GB for runtime, weights and caches (estimate,
not a measured installed-size guarantee). Code and weights are **Apache-2.0**;
retain upstream notices. Framewright's adapter remains under its repository
license.

After approving the runtime install, from the Framewright repository root:

```powershell
python -m venv .moss-sfx-runtime
git clone --filter=blob:none --no-checkout https://github.com/OpenMOSS/MOSS-TTS.git .moss-sfx-runtime/upstream
git -C .moss-sfx-runtime/upstream sparse-checkout set moss_soundeffect_v2
git -C .moss-sfx-runtime/upstream checkout --detach 934d6826b084c46a0d033402174d5f8ac4ed2519
& .\.moss-sfx-runtime\Scripts\python.exe -m pip install --extra-index-url https://download.pytorch.org/whl/cu128 -e '.moss-sfx-runtime/upstream/moss_soundeffect_v2[torch-cu128]'
```

This uses the upstream extras unchanged, including torch/torchaudio 2.9.0+cu128,
torchvision 0.24.0+cu128 and torchcodec 0.8.0. No new dependency is needed by the
planner, DSP, CLI, or ordinary tests. The venv, source, caches and smoke audio
stay under ignored `.moss-sfx-runtime/`.

After approving the weight download, use the Hub client in that runtime. To
record the exact snapshot and retain a normal `main` cache ref:

```powershell
$env:HF_HOME = Join-Path $PWD '.moss-sfx-runtime/huggingface'
& .\.moss-sfx-runtime\Scripts\python.exe -c "from huggingface_hub import snapshot_download; print(snapshot_download('OpenMOSS-Team/MOSS-SoundEffect-v2.0', max_workers=4))"
```

Set `HF_HOME` again in a new terminal, or set `MOSS_SFX_MODEL` to the printed
absolute snapshot directory. For reproducibility, use `MOSS_SFX_REVISION` with
that snapshot's full commit hash. Never commit personal cache paths or weights.

## Health, dry-run, generate

With `GAME_REPO` set, the default output is `GAME_REPO/SourceAudio/SFX`.
Alternatively use `MOSS_SFX_OUTPUT_PATH` or `--out`. There is deliberately no
guessed game repository. Output beneath any `Content` directory is rejected.

```powershell
& .\.moss-sfx-runtime\Scripts\python.exe tools/moss_sfx/moss_sfx_cli.py --health
& .\.moss-sfx-runtime\Scripts\python.exe tools/moss_sfx/moss_sfx_cli.py "$env:GAME_REPO/SourceAudio/sfx-list.json" --dry-run
& .\.moss-sfx-runtime\Scripts\python.exe tools/moss_sfx/moss_sfx_cli.py "$env:GAME_REPO/SourceAudio/sfx-list.json" --only EmberSparkIgnite --steps 100
```

`--health` never imports Torch, loads weights or contacts the network. It
checks packages, snapshot layout and every text-encoder shard. A ready result
means files are present, **not** that CUDA, binary compatibility, model integrity
or inference have been verified. `--dry-run` validates and prints names, seeds,
requested/effective durations and prospective paths without creating anything.
The run path is a preview; an intervening run can claim the same timestamp.

| Environment variable | Default / meaning |
| --- | --- |
| `MOSS_SFX_MODEL` | `OpenMOSS-Team/MOSS-SoundEffect-v2.0`, or a local snapshot directory |
| `MOSS_SFX_DEVICE` | `cuda`; `cuda:N` and explicit `cpu` are accepted; no silent CPU fallback |
| `MOSS_SFX_ALLOW_DOWNLOADS` | `false`; only literal `true` permits missing files to download during generation |
| `MOSS_SFX_OUTPUT_PATH` | Overrides `GAME_REPO/SourceAudio/SFX`; `--out` takes precedence |
| `MOSS_SFX_REVISION` | Optional Hub ref or full commit hash |
| `MOSS_SFX_COMPILE` | Off on Windows; elsewhere on unless `TORCHDYNAMO_DISABLE=1`; explicit true/false overrides |
| `HF_HOME`, `HF_HUB_CACHE` | Standard Hugging Face cache locations; inspected offline |

Compilation is set before MOSS imports its `torch.compile` decorators. Windows
defaults to `TORCHDYNAMO_DISABLE=1`; use `MOSS_SFX_COMPILE=true` only to opt in.
Every take records that choice. One pipeline is cached per process and all model
calls are serialized. A changed pipeline configuration requires a fresh process.

**A short click is not a cheap preview:** the model always denoises a full
30-second latent, then crops. A 0.5-second effect costs roughly the same model
work as a 30-second ambience at the same step count. The first compiled call
can take minutes. The smoke report measures load, first call and warm call.

## Sound-list contract

```json
{
  "defaults": {
    "steps": 100,
    "cfg": 4.0,
    "sigma_shift": 5.0,
    "negative_prompt": "music, speech, voice, singing, distortion, low quality",
    "normalize": {"mode": "peak", "target_db": -1.0}
  },
  "sounds": [{
    "name": "WoodKnock",
    "category": "World",
    "prompt": "A single wooden block knocked twice on a table.",
    "seconds": 2.0,
    "variations": 1,
    "seed": 1,
    "loop": false,
    "normalize": null
  }]
}
```

Names start with an ASCII capital and contain only letters/digits (up to 96
characters); uniqueness is case-insensitive for Windows. Categories are safe
ASCII folder names (letters, digits, spaces, underscore, hyphen; up to 64
characters), excluding Windows device names and trailing spaces. Unknown
fields, duplicate JSON keys, invalid types and nonfinite numbers are errors.

Durations are 0.1–30 seconds, rounded to one decimal by the same rule as MOSS.
Variations are 1–8. Seeds are nonnegative 32-bit integers with room for every
variation; a null seed uses the first four SHA-256 bytes of the ASCII name,
big-endian, masked with `0x7fffffff`. Take N adds N−1. List order is priority
order; `--only` filters without reordering. All default settings can be
overridden per sound; `normalize:null` inherits the defaults. Steps are 1–1000,
cfg 0–100, sigma shift 0.01–100, and normalization targets −60 to −1 dBFS.

The exact written prompt is retained and passed unchanged to MOSS. The manifest
also records the stripped prompt plus ` duration: X.Xs` that MOSS itself appends.
Do not append a duration suffix to your input prompt.

## Processing and immutable output

Non-loop takes trim samples below −50 dBFS, retain 480 samples (10 ms) of
available pre-roll, then apply a 240-sample linear fade-in and a 960-sample
linear fade-out. Short buffers use bounded overlapping fades. A take with no
sample at the trim threshold fails instead of producing an empty WAV.

Loops skip trimming and these edge fades. The last 24,000 samples overlap the
first 24,000 with cosine/sine equal-power gains. That overlap shortens the clip
by **0.5 seconds**: a requested 20-second loop exports 19.5 seconds. For clips
shorter than a second, overlap is half their sample count. The boundary joins
adjacent original samples; audition for content/repetition quality. A crossfade
cannot promise that every generated musical or rhythmic phrase feels seamless.

Peak normalization defaults to −1 dBFS. RMS normalization uses the requested
target unless doing so would exceed the −1 dBFS peak ceiling. Metrics describe
the actual quantized PCM. Raw/processed peaks below −60 dBFS, NaN/Infinity,
wrong channel shape or wrong sample counts fail the take.

```text
<out>/2026-11-12T05-10-00Z/
  World/SFX_WoodKnock_01.wav
  sfx-manifest.json
```

Same-second runs receive `-2`, `-3`, etc. Generation happens in a sibling
`.partial` directory. WAV creation is exclusive; the manifest is written last
via an atomic rename, then the whole run is renamed into place. Interrupted
staging folders remain available for diagnosis and are never reused. Only
consume finalized directories. A completed run can include failed takes; these
appear under `failed`, never under `files`, and the CLI returns a nonzero exit.

The manifest records file hashes, prompts, seed/settings, requested/effective
and final durations, postprocessing, actual PCM levels, model/snapshot,
installed source commit and dirty state when discoverable, Torch version,
compile choice, inference duration, UTC timestamp and license. Unknown source
provenance stays null; the expected pin is separately labeled.

## Tests and the opt-in GPU smoke

Ordinary tests need only stdlib, run under the same interpreter as YuE2 in
`scripts/verify.ps1`, and never load Torch or touch the GPU. `ci.yml` currently
has no YuE2 test step, so it is unchanged as requested.

```powershell
python -m unittest discover -s tools/moss_sfx/tests -v
```

Only after explicit approval, in the isolated runtime:

```powershell
$env:HF_HOME = Join-Path $PWD '.moss-sfx-runtime/huggingface'
$env:MOSS_SFX_SMOKE = '1'
& .\.moss-sfx-runtime\Scripts\python.exe -m unittest discover -s tools/moss_sfx/tests -p test_smoke.py -v
Remove-Item Env:MOSS_SFX_SMOKE
```

The smoke is skipped unless **both** the opt-in and the isolated interpreter
match. It generates only the two-second wooden-block tooling clip, twice with
seed 1 and 100 steps. It checks format, pre-trim duration and non-silence; prints
load/first/warm timings; and compares raw float32 SHA-256. If not bit-exact, it
reports that limitation and requires matching shapes instead. Audio and the
JSON receipt stay in `.moss-sfx-runtime/smoke/`. Stop after two identical install
or smoke failures; preserve the exact error before choosing a different fix.

For the November 11 jam, the local starter list must not be generated before
**22:00 America/Denver (November 12, 05:00 UTC)**. Tool development and this
neutral smoke clip are pre-jam tooling. See the local jam handoff note for the
future game repository declaration.
