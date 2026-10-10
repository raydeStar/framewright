# Offline sequence assembly

`assemble_sequence.py` assembles existing reviewed pictures and recorded audio
into a local delivery. Framewright owns this editorial utility; Reference Asset
Compiler continues to own asset changes, rigs, renders and their quality receipts.
Keep production-specific delivery plans beside the production's own outputs.
This utility does not alter Framewright projects, approvals, providers or UI.
It is not a newly shipped Sequence editor or an asset-quality gate.

Tested environment: Python 3.12.7, Pillow 10.4.0, FFmpeg/FFprobe 8.0.1, Windows.
Use installed tools; this utility never installs or downloads dependencies.

```powershell
python tools/media/assemble_sequence.py --plan C:/production/delivery-plan.json `
  --out C:/production/fresh-delivery --ffmpeg C:/tools/ffmpeg.exe `
  --ffprobe C:/tools/ffprobe.exe
```

The output directory must not exist. Input paths are resolved against the plan's
directory. Input hashes are mandatory and checked again after encoding. The
utility uses argument arrays with `shell=False`, safe generated basenames for
FFmpeg concat, CPU H.264 encoding, and bounded processes. Input validation refuses
invalid plans before creating an output directory. Later failures preserve an
explicit failure receipt; they do not overwrite an older delivery or mark it valid.

## Plan contract

```json
{
  "schema": "framewright-sequence-assembly-v1",
  "format": {
    "width": 1920,
    "height": 1080,
    "fps": 24,
    "total_frames": 1440,
    "audio_sample_rate": 48000,
    "audio_channels": 2,
    "fit_mode": "scale"
  },
  "shots": [
    {
      "kind": "frames",
      "label": "Opening scene",
      "directory": "scene-frames",
      "pattern": "frame-%06d.png",
      "start_number": 0,
      "frame_count": 1440,
      "frame_sha256": ["one actual SHA256 string for every requested PNG"]
    }
  ],
  "audio": [
    {
      "path": "recorded-line.wav",
      "sha256": "actual WAV SHA256",
      "start_frame": 72,
      "volume": 1
    }
  ]
}
```

Replace explanatory hash placeholders with actual 64-character hex hashes; the
example is a contract illustration, not a runnable media fixture. Shot lengths
must add up exactly. There is no silent picture padding or retiming.

Supported shot inputs:

- `frames`: directory, basename PNG pattern, start number, frame count and one
  `frame_sha256` per requested consecutive PNG. Missing frames fail.
- `still`: PNG `path`, `sha256` and `frame_count`.
- `stills`: `images` array containing those same PNG fields and frame counts;
  each image stays on screen for its declared duration.
- `video`: `path`, `sha256`, `start_frame` and `frame_count`. The source must
  already use the delivery FPS and contain the entire requested decoded range.
  Embedded sound is excluded; sound comes only from declared WAV tracks.

`fit_mode` is `exact` by default. `scale` resizes only when aspect ratios already
match. `contain` explicitly permits black letterboxing. No mode stretches or
crops the picture. A shot or individual still can override the format's mode.

WAV tracks can declare `trim_start_seconds` and `duration_seconds`; omitted,
the entire remaining source plays. Clips must fit within the delivery, and start
frames must map to whole output samples. Volume is explicit. The original WAV
is preserved; resampling, channel conversion and timing happen only in the master.

## Outputs and verification

The utility retains `picture-master.mp4`, `editorial-mix.wav` (24-bit PCM),
`final.mp4` (H.264/AAC), `contact-sheet.png`, per-shot sample frames, the frozen
plan, exact commands/tool versions, input/output hashes and `receipt.json`.
Picture and sound remain independently editable. A silent bed extends the PCM
master to the exact declared sample count, even when the only recording is short.

Every segment, the picture master and final movie are decoded/probed for actual
frame count, FPS, dimensions, duration and limited-range Rec.709 metadata. Both
final streams also pass a full error-failing decode. The PCM count/rate/channels/
bit depth and AAC stream format/duration are checked. A frame from the middle of
every expanded shot is extracted from the final movie into a labeled contact
sheet. These checks establish media integrity; artistic fidelity, readable shots,
mouth clearance, lip sync and performance require visual/playback review.

## Focused tests

```powershell
$env:FRAMEWRIGHT_TEST_FFMPEG='C:/tools/ffmpeg.exe'
$env:FRAMEWRIGHT_TEST_FFPROBE='C:/tools/ffprobe.exe'
# If the host redirects its normal Temp to an inaccessible sandbox, set this to
# an existing writable test-only directory. No artist media goes there.
$env:FRAMEWRIGHT_TEST_TEMP_DIR='C:/workspace/.tmp/media-test-temp'
python -m unittest discover -s tools/media/tests -v
```

Six plan tests reject bad hashes, missing frames, path patterns, mismatched totals,
implicit aspect changes and audio that overruns its delivery. Three optional
real-tool tests prove mixed input types, video ordering, decoded timing/color,
exact audio onset/tail, a 60-second PCM master from a 50 ms source, and the existing
application audio-mix padding fix. Without the explicit tool paths, those three
tests report skips. They use isolated synthetic fixtures and never artist data.

The backend `AudioMasteringService` now pads before trimming its mix: `atrim`
alone shortened a long timeline to its last short recording. Its existing trim,
volume, limiter and millisecond offset policy remain; this narrow fix does not
claim arbitrary-FPS sample accuracy for that older service. The offline utility
uses sample-aligned offsets and exact sample counts.
