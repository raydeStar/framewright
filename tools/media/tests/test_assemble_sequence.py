"""Bounded fixtures only; no artist databases, providers, or GPU workloads."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import wave

from PIL import Image

MODULE_PATH = Path(__file__).resolve().parents[1] / "assemble_sequence.py"
spec = importlib.util.spec_from_file_location("assemble_sequence", MODULE_PATH)
assembly = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assembly)


def png(path, color):
    Image.new("RGB", (160, 90), color).save(path)
    return {"path": str(path), "sha256": assembly.digest(path)}


def tone_wav(path):
    # A short nonzero pulse is easier to locate exactly than speech or a sine.
    samples = b"\x00\x10" * 1200  # 50 ms, 24 kHz PCM16 mono.
    with wave.open(str(path), "wb") as target:
        target.setparams((1, 2, 24000, 0, "NONE", "not compressed"))
        target.writeframes(samples)


class PlanTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir=os.environ.get("FRAMEWRIGHT_TEST_TEMP_DIR"))
        self.root = Path(self.tmp.name)
        self.still = png(self.root / "source.png", "red")
        self.plan = {"schema": assembly.SCHEMA,
                     "format": {"width": 160, "height": 90, "fps": 24, "total_frames": 24},
                     "shots": [{"kind": "still", "frame_count": 24, **self.still}], "audio": []}

    def tearDown(self):
        self.tmp.cleanup()

    def save(self):
        p = self.root / "plan.json"
        p.write_text(json.dumps(self.plan))
        return p

    def test_wrong_hash_refuses_before_output_or_process(self):
        self.plan["shots"][0]["sha256"] = "0" * 64
        with self.assertRaisesRegex(assembly.AssemblyError, "hash differs"):
            assembly.validate_plan(self.save(), Path("not-invoked"))

    def test_short_total_refuses_instead_of_padding_picture_silently(self):
        self.plan["format"]["total_frames"] = 25
        with self.assertRaisesRegex(assembly.AssemblyError, "do not add up"):
            assembly.validate_plan(self.save(), Path("not-invoked"))

    def test_missing_frame_refuses(self):
        self.plan["shots"] = [{"kind": "frames", "directory": str(self.root),
                               "pattern": "frame-%06d.png", "frame_count": 24,
                               "frame_sha256": ["0" * 64] * 24}]
        with self.assertRaisesRegex(assembly.AssemblyError, "unavailable"):
            assembly.validate_plan(self.save(), Path("not-invoked"))

    def test_directory_escape_pattern_refuses(self):
        self.plan["shots"] = [{"kind": "frames", "directory": str(self.root),
                               "pattern": "../frame-%06d.png", "frame_count": 24,
                               "frame_sha256": ["0" * 64] * 24}]
        with self.assertRaisesRegex(assembly.AssemblyError, "basename PNG pattern"):
            assembly.validate_plan(self.save(), Path("not-invoked"))

    def test_aspect_change_requires_explicit_contain(self):
        Image.new("RGB", (90, 160), "red").save(self.root / "source.png")
        self.plan["shots"][0]["sha256"] = assembly.digest(self.root / "source.png")
        self.plan["format"]["fit_mode"] = "scale"
        with self.assertRaisesRegex(assembly.AssemblyError, "change the source aspect"):
            assembly.validate_plan(self.save(), Path("not-invoked"))
        self.plan["format"]["fit_mode"] = "contain"
        assembly.validate_plan(self.save(), Path("not-invoked"))

    def test_audio_must_fit_with_an_explicit_trim(self):
        wav = self.root / "pulse.wav"
        tone_wav(wav)
        self.plan["audio"] = [{"path": str(wav), "sha256": assembly.digest(wav), "start_frame": 23}]
        with self.assertRaisesRegex(assembly.AssemblyError, "extends beyond the delivery"):
            assembly.validate_plan(self.save(), Path("not-invoked"))


@unittest.skipUnless(os.environ.get("FRAMEWRIGHT_TEST_FFMPEG") and os.environ.get("FRAMEWRIGHT_TEST_FFPROBE"),
                     "Set installed FFmpeg/FFprobe paths for bounded real-media fixtures.")
class RealMediaTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory(dir=os.environ.get("FRAMEWRIGHT_TEST_TEMP_DIR"))
        self.root = Path(self.tmp.name)
        self.ffmpeg = Path(os.environ["FRAMEWRIGHT_TEST_FFMPEG"])
        self.ffprobe = Path(os.environ["FRAMEWRIGHT_TEST_FFPROBE"])

    def tearDown(self):
        self.tmp.cleanup()

    def test_frames_video_still_array_audio_offset_and_exact_length(self):
        frames = self.root / "frames"
        frames.mkdir()
        hashes = []
        for i in range(6):
            file = frames / f"frame-{i:06d}.png"
            png(file, "red")
            hashes.append(assembly.digest(file))
        blue = png(self.root / "blue.png", "blue")
        green = png(self.root / "green.png", "green")
        video = self.root / "video.mp4"
        assembly.run(self.ffmpeg, ["-nostdin", "-n", "-v", "error", "-loop", "1", "-framerate", "24",
                                  "-i", green["path"], "-frames:v", "8", "-c:v", "libx264", "-pix_fmt", "yuv420p", str(video)])
        wav = self.root / "pulse.wav"
        tone_wav(wav)
        plan = {"schema": assembly.SCHEMA,
                "format": {"width": 160, "height": 90, "fps": 24, "total_frames": 24,
                           "audio_sample_rate": 48000, "audio_channels": 2},
                "shots": [{"kind": "frames", "label": "Frames", "directory": str(frames),
                           "pattern": "frame-%06d.png", "frame_count": 6, "frame_sha256": hashes},
                          {"kind": "video", "label": "Video", "path": str(video), "sha256": assembly.digest(video),
                           "start_frame": 2, "frame_count": 6},
                          {"kind": "stills", "label": "Stills", "images": [{**blue, "frame_count": 6}, {**green, "frame_count": 6}]}],
                "audio": [{"path": str(wav), "sha256": assembly.digest(wav), "start_frame": 6}]}
        p = self.root / "plan.json"
        p.write_text(json.dumps(plan))
        out = self.root / "delivery"
        receipt = assembly.assemble(p, out, self.ffmpeg, self.ffprobe)
        self.assertEqual(24, receipt["final_validation"]["decoded_frame_count"])
        self.assertEqual(48000, receipt["pcm_validation"]["sample_count"])
        self.assertEqual(2, receipt["pcm_validation"]["channels"])
        self.assertEqual(3, receipt["pcm_validation"]["sample_width_bytes"])
        self.assertEqual("passed", receipt["all_stream_decode"])
        with wave.open(str(out / "editorial-mix.wav"), "rb") as w:
            pcm = w.readframes(w.getnframes())
        samples = [int.from_bytes(pcm[i:i+3], "little", signed=True) for i in range(0, len(pcm), 6)]
        self.assertTrue(all(v == 0 for v in samples[:12000]), "Audio must stay silent before its frame6 offset.")
        self.assertGreater(abs(samples[12000]), 1000)
        self.assertTrue(all(v == 0 for v in samples[16000:]), "PCM must pad the whole tail with silence.")
        expected_colors = [("red", (255, 0, 0)), ("green", (0, 128, 0)), ("blue", (0, 0, 255)), ("green", (0, 128, 0))]
        for selection, (_, expected) in zip(receipt["contact_frames"], expected_colors, strict=True):
            with Image.open(selection["sample_path"]) as image:
                actual = image.convert("RGB").getpixel((80, 45))
            self.assertTrue(all(abs(a - b) < 15 for a, b in zip(actual, expected)), (actual, expected))
        original_hash = assembly.digest(out / "final.mp4")
        with self.assertRaisesRegex(assembly.AssemblyError, "already exists"):
            assembly.assemble(p, out, self.ffmpeg, self.ffprobe)
        self.assertEqual(original_hash, assembly.digest(out / "final.mp4"))

    def test_audio_padding_keeps_sixty_seconds_with_only_fifty_ms_source(self):
        wav = self.root / "pulse.wav"
        tone_wav(wav)
        out = self.root / "sixty-seconds.wav"
        fmt = {"fps": 24, "total_frames": 1440, "audio_sample_rate": 48000, "audio_channels": 2}
        clips = [{"start_frame": 72, "trim_start_seconds": 0, "duration_seconds": .05, "volume": 1}]
        assembly.run(self.ffmpeg, ["-nostdin", "-n", "-v", "error", "-i", str(wav), "-filter_complex", assembly.audio_filter(fmt, clips),
                                  "-map", "[out]", "-c:a", "pcm_s24le", "-ar", "48000", "-ac", "2", str(out)])
        info = assembly.pcm_metadata(out)
        self.assertEqual(2_880_000, info["sample_count"])
        self.assertEqual(60, info["duration_seconds"])

    def test_backend_mix_padding_reproducer_and_retained_filter(self):
        wav = self.root / "pulse.wav"
        tone_wav(wav)
        old = self.root / "unpadded.wav"
        fixed = self.root / "padded.wav"
        prefix = "[0:a]asetpts=PTS-STARTPTS,adelay=1000:all=1[a0];[a0]amix=inputs=1:duration=longest:normalize=0,"
        # This is the actual pre-existing defect, not a manufactured model fixture.
        assembly.run(self.ffmpeg, ["-nostdin", "-n", "-v", "error", "-i", str(wav), "-filter_complex",
                                  prefix + "atrim=duration=60,alimiter=limit=0.95[out]", "-map", "[out]", "-c:a", "pcm_s24le", "-ar", "48000", str(old)])
        self.assertLess(assembly.pcm_metadata(old)["duration_seconds"], 2)
        source = MODULE_PATH.parents[2] / "src/StoryboardStudio.Api/Services/AudioMasteringService.cs"
        # Read the retained service's output filter. Parameters below substitute
        # the actual service interpolation expressions for this isolated fixture.
        line = next(s for s in source.read_text().splitlines() if "filters.Add(" in s and "amix=" in s)
        self.assertIn("apad=whole_dur=", line)
        tail = line.split("normalize=0,", 1)[1].rsplit('[out]");', 1)[0]
        duration_expression = '{duration.ToString("0.###", CultureInfo.InvariantCulture)}'
        tail = tail.replace(duration_expression, "60")
        self.assertNotIn("{", tail)
        assembly.run(self.ffmpeg, ["-nostdin", "-n", "-v", "error", "-i", str(wav), "-filter_complex",
                                  prefix + tail + "[out]", "-map", "[out]", "-c:a", "pcm_s24le", "-ar", "48000", str(fixed)])
        self.assertEqual(2_880_000, assembly.pcm_metadata(fixed)["sample_count"])


if __name__ == "__main__":
    unittest.main()
