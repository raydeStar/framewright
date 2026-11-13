"""Explicit GPU commissioning only: MOSS_SFX_SMOKE=1 in .moss-sfx-runtime."""
from array import array
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import unittest
import wave

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import moss_sfx_core as core

RUNTIME = ROOT.parents[1] / ".moss-sfx-runtime"
APPROVED = os.environ.get("MOSS_SFX_SMOKE") == "1" and Path(sys.prefix).resolve() == RUNTIME.resolve()


@unittest.skipUnless(APPROVED, "GPU smoke requires MOSS_SFX_SMOKE=1 and the isolated .moss-sfx-runtime interpreter")
class MossGpuSmokeTests(unittest.TestCase):
    def test_wooden_block_repeatability(self):
        config = core.Config.from_env()
        job = core.plan_jobs(core.validate_sound_list({"sounds": [{"name": "WoodenBlockSmoke", "category": "Tooling",
            "prompt": "A single wooden block knocked twice on a table.", "seconds": 2.0, "seed": 1}]}))[0]
        start = time.perf_counter()
        _, metadata = core.get_pipeline(config)
        load_seconds = time.perf_counter() - start
        partial, final = core.reserve_run(RUNTIME / "smoke", core.run_name())
        hashes, wav_hashes, timings, shapes = [], [], [], []
        first_audio, first_metadata = None, None
        for index in range(2):
            audio, take = core.generate_take(job, config)
            self.assertAlmostEqual(len(audio) / 48000, 2.0, delta=.010)
            core.validate_audio(audio)
            hashes.append(hashlib.sha256(array("f", audio).tobytes()).hexdigest())
            timings.append(take["generation_seconds"])
            shapes.append(len(audio))
            path = partial / f"wooden-block-{index + 1}.wav"
            core.write_wav(path, audio)
            wav_hashes.append(core.sha256(path))
            if index == 0:
                first_audio, first_metadata = audio, take
            with wave.open(str(path), "rb") as wav:
                self.assertEqual((wav.getframerate(), wav.getnchannels(), wav.getsampwidth()), (48000, 1, 2))
                self.assertAlmostEqual(wav.getnframes() / wav.getframerate(), 2.0, delta=.010)
        self.assertEqual(shapes[0], shapes[1])
        # Exercise the production batch/DSP/manifest path with the already-made
        # smoke audio. No third summons to the GPU orchestra is required.
        processed, manifest = core.run_batch([job], partial / "processed", config,
            generator=lambda request, settings: (first_audio, first_metadata))
        self.assertEqual(manifest["failed"], [])
        self.assertEqual(len(manifest["files"]), 1)
        report = {**metadata, "prompt": job["prompt"], "seed": 1, "seconds": 2.0,
                  "timestamp_utc": core.utc_now(), "load_seconds": load_seconds,
                  "first_call_seconds": timings[0], "warm_call_seconds": timings[1],
                  "raw_float32_sha256": hashes, "wav_sha256": wav_hashes,
                  "bit_exact": hashes[0] == hashes[1], "samples": shapes,
                  "processed_manifest": (processed / "sfx-manifest.json").relative_to(partial).as_posix(),
                  "license": "Apache-2.0", "purpose": "Pre-jam tooling smoke; not a game asset"}
        core.atomic_json(partial / "smoke-report.json", report)
        partial.rename(final)
        print("\nMOSS smoke receipt; the wooden block has spoken:\n" + json.dumps({**report, "output": str(final)}, indent=2), flush=True)
        if hashes[0] != hashes[1]:
            print("Same-seed output is NOT bit-exact; shape and non-silence passed. No identical-waveform claim is made.", flush=True)


if __name__ == "__main__":
    unittest.main()
