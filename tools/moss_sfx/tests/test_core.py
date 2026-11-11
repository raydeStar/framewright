import contextlib
from copy import deepcopy
from datetime import datetime, timezone
import importlib.util
import io
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import time
import types
import unittest
from unittest import mock
import wave

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import moss_sfx_core as core
import moss_sfx_cli as cli


def document(**changes):
    sound = {"name": "WoodKnock", "category": "World", "prompt": "  A wooden knock.  ",
             "seconds": .1, "variations": 2, "seed": None, "normalize": None}
    sound.update(changes)
    return {"sounds": [sound]}


def jobs(**changes):
    return core.plan_jobs(core.validate_sound_list(document(**changes)))


def sine(count=48000, amplitude=.25, frequency=1000):
    return core.array("d", (amplitude * math.sin(2 * math.pi * frequency * i / 48000) for i in range(count)))


def fixture_snapshot(root):
    files = ["model_index.json", "scheduler/scheduler_config.json", "transformer/config.json",
             "transformer/diffusion_pytorch_model.safetensors", "vae/config.json", "vae/vae_128d_48k.pth",
             "text_encoder/config.json", "text_encoder/model.safetensors",
             "tokenizer/tokenizer_config.json", "tokenizer/tokenizer.json"]
    for name in files:
        path = root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("{}", encoding="utf-8")
    return root


class ValidationTests(unittest.TestCase):
    def test_bad_names_categories_numbers_and_types(self):
        cases = {"name": ("badName", "Bad_Name", "../Escape", "Émber", "", 1),
                 "category": ("../World", "Wörld", "CON", "LPT1", "World ", "C:\\World", ""),
                 "seconds": (0, .099, 30.01, True, float("nan"), float("inf"), "2"),
                 "variations": (0, 9, 1.5, True), "seed": (-1, True, 2**32 - 1, 10**400),
                 "prompt": ("", "  ", None), "loop": (1, "true"),
                 "steps": (0, 1001, True), "cfg": (-1, float("nan")), "sigma_shift": (0,)}
        for field, values in cases.items():
            for value in values:
                with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                    core.validate_sound_list(document(**{field: value}))

    def test_duplicates_are_case_insensitive(self):
        value = document()
        value["sounds"].append({**value["sounds"][0], "name": "Woodknock"})
        with self.assertRaisesRegex(ValueError, "duplicate name"):
            core.validate_sound_list(value)

    def test_unknown_fields_at_every_level(self):
        for value in ({**document(), "typo": 1}, {**document(), "defaults": {"typo": 1}},
                      document(typo=1), document(normalize={"mode": "peak", "typo": 1})):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "unknown fields"):
                core.validate_sound_list(value)

    def test_normalization_inheritance_and_overrides(self):
        value = {**document(), "defaults": {"normalize": {"mode": "rms", "target_db": -26}}}
        self.assertEqual(core.validate_sound_list(value)[0]["normalize"], {"mode": "rms", "target_db": -26.0})
        for invalid in ({"mode": "lufs"}, {"target_db": 0}, {"target_db": True}, {}):
            if not invalid:
                self.assertEqual(core.validate_sound_list(document(normalize=invalid))[0]["normalize"], core.DEFAULTS["normalize"])
            else:
                with self.assertRaises(ValueError):
                    core.validate_sound_list(document(normalize=invalid))

    def test_duplicate_json_keys_are_errors(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "list.json"
            path.write_text('{"sounds": [], "sounds": []}', encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "duplicate JSON field"):
                core.load_sound_list(path)

    def test_deterministic_seeds_names_order_and_rounding(self):
        plan = jobs(seconds=1.24)
        expected = int.from_bytes(core.hashlib.sha256(b"WoodKnock").digest()[:4], "big") & 0x7fffffff
        self.assertEqual([j["seed"] for j in plan], [expected, expected + 1])
        self.assertEqual([j["seed"] for j in jobs(seed=10)], [10, 11])
        self.assertEqual(plan[0]["file"], "World/SFX_WoodKnock_01.wav")
        self.assertEqual(plan[0]["seconds"], 1.2)
        self.assertEqual(plan[0]["seconds_requested"], 1.24)
        self.assertEqual(plan[0]["effective_prompt"], "A wooden knock. duration: 1.2s")
        code = f"import sys; sys.path.insert(0, {str(ROOT)!r}); import moss_sfx_core as c; print(c.plan_jobs(c.validate_sound_list({document()!r}))[0]['seed'])"
        for hash_seed in ("1", "42"):
            result = subprocess.run([sys.executable, "-c", code], env={**os.environ, "PYTHONHASHSEED": hash_seed}, capture_output=True, text=True, check=True)
            self.assertEqual(int(result.stdout), expected)

    def test_only_and_steps_do_not_reorder_or_mutate(self):
        value = document()
        value["sounds"].append({**value["sounds"][0], "name": "OtherKnock"})
        sounds = core.validate_sound_list(value)
        before = deepcopy(sounds)
        plan = core.plan_jobs(sounds, only=["OtherKnock"], steps=12)
        self.assertEqual([j["name"] for j in plan], ["OtherKnock"] * 2)
        self.assertEqual(plan[0]["steps"], 12)
        self.assertEqual(before, sounds)
        for only in ([], ["Missing"]):
            with self.assertRaises(ValueError):
                core.plan_jobs(sounds, only=only)

    def test_output_is_explicit_and_outside_content(self):
        with self.assertRaisesRegex(ValueError, "Set GAME_REPO"):
            core.output_root(core.Config())
        with self.assertRaisesRegex(ValueError, "outside Unreal"):
            core.output_root(core.Config(), "Game/Content/SFX")
        with mock.patch.dict(os.environ, {"GAME_REPO": "Game", "MOSS_SFX_OUTPUT_PATH": "", "MOSS_SFX_ALLOW_DOWNLOADS": "false"}):
            self.assertEqual(Path(core.Config.from_env().output_path), Path("Game/SourceAudio/SFX"))


class DspTests(unittest.TestCase):
    def test_trim_exact_preroll_and_sample_count(self):
        audio = [0.] * 1000 + [.1] * 2000 + [0.] * 1000
        trimmed = core.trim_silence(audio)
        self.assertEqual(len(trimmed), 2480)
        self.assertEqual(list(trimmed[:480]), [0.] * 480)
        self.assertEqual(trimmed[480], .1)
        self.assertEqual(trimmed[-1], .1)
        self.assertEqual(len(core.trim_silence([.1] * 100)), 100)

    def test_fades_have_exact_endpoints_and_lengths(self):
        faded = core.apply_fades([1.] * 4800)
        self.assertEqual(len(faded), 4800)
        self.assertEqual(faded[0], 0)
        self.assertEqual(faded[239], 1)
        self.assertEqual(faded[240], 1)
        self.assertEqual(faded[-960], 1)
        self.assertEqual(faded[-1], 0)
        self.assertAlmostEqual(faded[120], 120 / 239)
        self.assertAlmostEqual(faded[-480], 479 / 959)
        self.assertEqual(len(core.apply_fades([1.] * 100)), 100)

    def test_peak_and_rms_normalization_and_peak_ceiling(self):
        original = sine()
        peak = core.normalize_audio(original, {"mode": "peak", "target_db": -1})
        self.assertAlmostEqual(core.levels(peak)["peak_db"], -1, places=9)
        rms = core.normalize_audio(original, {"mode": "rms", "target_db": -26})
        self.assertAlmostEqual(core.levels(rms)["rms_db"], -26, places=9)
        impulse = [0.] * 47999 + [.5]
        limited = core.normalize_audio(impulse, {"mode": "rms", "target_db": -1})
        self.assertLessEqual(core.levels(limited)["peak_db"], -1 + 1e-9)
        self.assertLess(core.levels(limited)["rms_db"], -40)

    def test_equal_power_loop_overlap_and_seam(self):
        audio = sine(96000, frequency=137)
        loop = core.loop_crossfade(audio)
        self.assertEqual(len(loop), 72000)
        for index in (0, 6000, 12000, 23999):
            theta = math.pi / 2 * index / 23999
            expected = audio[72000 + index] * math.cos(theta) + audio[index] * math.sin(theta)
            self.assertAlmostEqual(loop[index], expected, places=12)
        self.assertEqual(loop[24000], audio[24000])
        self.assertAlmostEqual(loop[0] - loop[-1], audio[72000] - audio[71999])
        self.assertEqual(len(core.loop_crossfade(sine(4800))), 2400)

    def test_loop_skips_trimming_and_edge_fades(self):
        original = [.1] * 96000
        processed = core.post_process(original, loop=True)
        self.assertEqual(len(processed), 72000)
        self.assertGreater(processed[0], .5)
        self.assertGreater(processed[-1], .5)

    def test_near_silence_nonfinite_empty_and_trim_threshold(self):
        for audio in ([0.] * 4800, [.00099] * 4800, [], [float("nan")], [float("inf")]):
            with self.subTest(audio=audio[:1]), self.assertRaises(ValueError):
                core.post_process(audio)
        core.validate_audio([.001])
        with self.assertRaisesRegex(ValueError, "trim threshold"):
            core.trim_silence([.002] * 100)

    def test_wav_format_actual_pcm_metrics_and_no_overwrite(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "take.wav"
            audio = core.normalize_audio(sine(4800), core.DEFAULTS["normalize"])
            metrics = core.write_wav(path, audio)
            with wave.open(str(path), "rb") as wav:
                self.assertEqual((wav.getframerate(), wav.getnchannels(), wav.getsampwidth(), wav.getnframes()), (48000, 1, 2, 4800))
            self.assertLessEqual(metrics["peak_db"], -1)
            self.assertAlmostEqual(metrics["peak_db"], -1, delta=.001)
            previous = path.read_bytes()
            with self.assertRaises(FileExistsError):
                core.write_wav(path, sine(4800))
            self.assertEqual(path.read_bytes(), previous)


class RuntimeTests(unittest.TestCase):
    def test_offline_health_detects_incomplete_snapshots_even_with_downloads_allowed(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            config = core.Config(model=str(root), allow_downloads=True)
            with mock.patch.object(core.importlib.util, "find_spec", return_value=object()):
                self.assertFalse(core.inspect_runtime(config)["ready"])
                fixture_snapshot(root)
                health = core.inspect_runtime(config)
                self.assertTrue(health["ready"])
                self.assertFalse(health["weights_loaded"])
                self.assertNotIn("torch", sys.modules)

    def test_cache_refs_and_shards_are_checked_without_hub_import(self):
        with tempfile.TemporaryDirectory() as temp, mock.patch.dict(os.environ, {"HF_HUB_CACHE": temp}):
            repo = Path(temp) / "models--OpenMOSS-Team--MOSS-SoundEffect-v2.0"
            snapshot = fixture_snapshot(repo / "snapshots" / ("a" * 40))
            (repo / "refs").mkdir()
            (repo / "refs/main").write_text("a" * 40)
            self.assertEqual(core.local_snapshot(core.Config()), snapshot.resolve())
            index = snapshot / "text_encoder/model.safetensors.index.json"
            index.write_text(json.dumps({"weight_map": {"x": "missing-shard.safetensors"}}))
            self.assertIn("missing-shard", " ".join(core.snapshot_errors(snapshot)))
            index.write_text(json.dumps({"weight_map": {"x": "../escape"}}))
            self.assertIn("unsafe", " ".join(core.snapshot_errors(snapshot)))

    def test_compile_config_is_explicit_and_windows_defaults_off(self):
        with mock.patch.dict(os.environ, {}, clear=True), mock.patch.object(core.sys, "platform", "win32"):
            self.assertFalse(core.Config.from_env().compile)
            self.assertFalse(core.Config.from_env().allow_downloads)
            os.environ["MOSS_SFX_COMPILE"] = "true"
            self.assertTrue(core.Config.from_env().compile)
            os.environ["MOSS_SFX_ALLOW_DOWNLOADS"] = "yes"
            with self.assertRaises(ValueError):
                core.Config.from_env()

    def test_model_adapter_passes_offline_flag_once_and_caches(self):
        with tempfile.TemporaryDirectory() as temp:
            root = fixture_snapshot(Path(temp))
            config = core.Config(model=str(root))
            pipe = types.SimpleNamespace(sample_rate=48000, max_inference_seconds=30, device="cuda")
            factory = mock.Mock(return_value=pipe)
            fake_torch = types.SimpleNamespace(bfloat16="bf16", __version__="fixture-torch", cuda=types.SimpleNamespace(is_available=lambda: True, synchronize=lambda device: None))
            with mock.patch.dict(sys.modules, {"torch": fake_torch, "moss_soundeffect_v2": types.SimpleNamespace(MossSoundEffectPipeline=types.SimpleNamespace(from_pretrained=factory))}), \
                 mock.patch.object(core, "_PIPELINE", None), mock.patch.object(core, "_PIPELINE_CONFIG", None), \
                 mock.patch.object(core, "_PIPELINE_METADATA", None), mock.patch.object(core, "source_provenance", return_value={"moss_repo_commit": "fixture"}), \
                 mock.patch.dict(os.environ, {}, clear=True):
                first, metadata = core.get_pipeline(config)
                self.assertIs(core.get_pipeline(config)[0], first)
                factory.assert_called_once_with(str(root.resolve()), torch_dtype="bf16", device="cuda", local_files_only=True, revision=None)
                self.assertEqual(os.environ["TORCHDYNAMO_DISABLE"], "1")
                self.assertEqual(os.environ["HF_HUB_OFFLINE"], "1")
                self.assertFalse(metadata["compile"])
                with self.assertRaisesRegex(ValueError, "configuration changed"):
                    core.get_pipeline(core.Config(model=str(root), compile=True))

    def test_adapter_uses_original_prompt_and_serializes_gpu_calls(self):
        active = 0
        maximum = 0
        seen = []
        class Tensor:
            shape = (1, 1, 4800)
            def __getitem__(self, key): return self
            def detach(self): return self
            def float(self): return self
            def cpu(self): return self
            def tolist(self): return [.1] * 4800
        def pipeline(**kwargs):
            nonlocal active, maximum
            active += 1
            maximum = max(maximum, active)
            seen.append(kwargs)
            time.sleep(.02)
            active -= 1
            return Tensor()
        errors = []
        def run():
            try:
                core.generate_take(jobs()[0], core.Config())
            except Exception as exc:
                errors.append(exc)
        with mock.patch.object(core, "get_pipeline", return_value=(pipeline, {})):
            threads = [threading.Thread(target=run) for _ in range(3)]
            for thread in threads: thread.start()
            for thread in threads: thread.join()
        self.assertEqual(errors, [])
        self.assertEqual(maximum, 1)
        self.assertEqual(seen[0]["prompt"], "  A wooden knock.  ")
        self.assertNotIn("duration:", seen[0]["prompt"])

    def test_dry_run_and_health_cannot_import_torch_or_use_network(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "list.json"
            path.write_text(json.dumps(document()))
            for arguments in (["--health"], [str(path), "--out", str(Path(temp) / "out"), "--dry-run"]):
                code = f"""
import sys, runpy, importlib.abc, socket
class DenyHeavy(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname.split('.')[0] in ('torch', 'torchaudio', 'moss_soundeffect_v2', 'huggingface_hub', 'numpy'):
            raise AssertionError('heavy import attempted: ' + fullname)
def no_network(*args, **kwargs):
    raise AssertionError('network attempted')
socket.socket = type('BlockedSocket', (socket.socket,), {{'connect': no_network, 'connect_ex': no_network}})
socket.getaddrinfo = no_network
sys.meta_path.insert(0, DenyHeavy())
sys.path.insert(0, {str(ROOT)!r})
import moss_sfx_core as core
# find_spec is an intentional presence check, not a module import.
core.importlib.util.find_spec = lambda name: None
import moss_sfx_cli as cli
result = cli.main({arguments!r})
assert result in (0, 1)
assert 'torch' not in sys.modules
assert 'numpy' not in sys.modules
"""
                env = {**os.environ, "MOSS_SFX_MODEL": str(Path(temp) / "missing"), "MOSS_SFX_ALLOW_DOWNLOADS": "false"}
                result = subprocess.run([sys.executable, "-I", "-S", "-c", code], env=env, capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
            self.assertFalse((Path(temp) / "out").exists())

    def test_missing_snapshot_fails_before_importing_torch(self):
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(core, "_PIPELINE", None), mock.patch.dict(os.environ, {}, clear=True):
            with self.assertRaisesRegex(RuntimeError, "not available locally"):
                core.get_pipeline(core.Config(model=str(Path(temp) / "missing")))
            self.assertNotIn("torch", sys.modules)

    def test_wrong_channels_and_sample_count_fail(self):
        for shape, count in (((1, 2, 4800), 4800), ((1, 1, 4799), 4799)):
            audio = mock.Mock()
            audio.shape = shape
            audio.__getitem__ = mock.Mock(return_value=audio)
            audio.detach.return_value.float.return_value.cpu.return_value.tolist.return_value = [.1] * count
            with mock.patch.object(core, "get_pipeline", return_value=(mock.Mock(return_value=audio), {})):
                with self.assertRaises(ValueError):
                    core.generate_take(jobs()[0], core.Config())


class BatchTests(unittest.TestCase):
    def test_cli_script_works_under_the_gate_interpreter(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "list.json"
            path.write_text(json.dumps(document()))
            result = subprocess.run([sys.executable, str(ROOT / "moss_sfx_cli.py"), str(path),
                                     "--out", str(Path(temp) / "out"), "--dry-run"], capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(len(json.loads(result.stdout)["jobs"]), 2)
            self.assertFalse((Path(temp) / "out").exists())

    @staticmethod
    def generate(job, config):
        return sine(int(job["seconds"] * 48000)), {"model_id": config.model, "snapshot_path": "fixture/snapshot",
            "snapshot_revision": "fixture-revision", "moss_repo_commit": core.MOSS_COMMIT,
            "torch_version": "fixture", "compile": config.compile, "generation_seconds": .125}

    def test_manifest_and_never_overwrite_same_second_or_partial(self):
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(core, "run_name", return_value="2026-11-12T05-10-00Z"):
            root = Path(temp)
            abandoned = root / "2026-11-12T05-10-00Z.partial"
            abandoned.mkdir()
            first, manifest = core.run_batch(jobs(), root, core.Config(), generator=self.generate)
            original = (first / "sfx-manifest.json").read_bytes()
            second, _ = core.run_batch(jobs(), root, core.Config(), generator=self.generate)
            self.assertTrue(first.name.endswith("-2"))
            self.assertTrue(second.name.endswith("-3"))
            self.assertEqual((first / "sfx-manifest.json").read_bytes(), original)
            self.assertTrue(abandoned.exists())
            self.assertEqual(len(manifest["files"]), 2)
            self.assertEqual(manifest["failed"], [])
            entry = manifest["files"][0]
            required = {"name", "category", "file", "sha256", "prompt", "effective_prompt", "negative_prompt", "seed", "steps", "cfg", "sigma_shift",
                "seconds_requested", "final_duration", "loop", "normalize", "peak_db", "rms_db", "model_id", "snapshot_path", "snapshot_revision",
                "moss_repo_commit", "torch_version", "compile", "license", "generation_seconds", "timestamp_utc"}
            self.assertTrue(required <= set(entry), required - set(entry))
            self.assertEqual(entry["sha256"], core.sha256(first / entry["file"]))
            self.assertEqual(entry["license"], "Apache-2.0")
            self.assertEqual(entry["prompt"], "  A wooden knock.  ")
            self.assertLessEqual(entry["peak_db"], -1)
            self.assertEqual(json.loads(original)["files"], manifest["files"])

    def test_failed_take_cannot_be_a_success(self):
        def generator(job, config):
            if job["variation"] == 1:
                return [0.] * 4800, {}
            raise RuntimeError("fixture GPU failure")
        with tempfile.TemporaryDirectory() as temp:
            final, manifest = core.run_batch(jobs(), Path(temp), core.Config(), generator=generator)
            self.assertEqual(manifest["files"], [])
            self.assertEqual(len(manifest["failed"]), 2)
            self.assertIn("near-silent", manifest["failed"][0]["error"])
            self.assertIn("fixture GPU failure", manifest["failed"][1]["error"])
            self.assertEqual(list(final.rglob("*.wav")), [])

    def test_interrupt_preserves_partial_and_does_not_publish_manifest(self):
        with tempfile.TemporaryDirectory() as temp:
            with self.assertRaises(KeyboardInterrupt):
                core.run_batch(jobs(), Path(temp), core.Config(), generator=mock.Mock(side_effect=KeyboardInterrupt))
            folders = list(Path(temp).iterdir())
            self.assertEqual(len(folders), 1)
            self.assertTrue(folders[0].name.endswith(".partial"))
            self.assertFalse((folders[0] / "sfx-manifest.json").exists())

    def test_final_directory_appearing_during_run_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(core, "run_name", return_value="collision"):
            root = Path(temp)
            def generator(job, config):
                target = root / "collision"
                target.mkdir(exist_ok=True)
                (target / "keep.txt").write_text("existing owner")
                return self.generate(job, config)
            with self.assertRaisesRegex(FileExistsError, "preserved staging"):
                core.run_batch(jobs(), root, core.Config(), generator=generator)
            self.assertEqual((root / "collision/keep.txt").read_text(), "existing owner")
            self.assertTrue((root / "collision.partial/sfx-manifest.json").exists())

    def test_manifest_failure_does_not_publish_run(self):
        with tempfile.TemporaryDirectory() as temp, mock.patch.object(core, "atomic_json", side_effect=OSError("disk full")):
            with self.assertRaisesRegex(OSError, "disk full"):
                core.run_batch(jobs(), Path(temp), core.Config(), generator=self.generate)
            self.assertTrue(all(path.name.endswith(".partial") for path in Path(temp).iterdir()))

    def test_cli_exit_code_signals_failed_takes(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "list.json"
            path.write_text(json.dumps(document()))
            with mock.patch.object(core, "generate_take", side_effect=RuntimeError("fixture failure")), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(cli.main([str(path), "--out", str(Path(temp) / "out")]), 1)


if __name__ == "__main__":
    unittest.main()
