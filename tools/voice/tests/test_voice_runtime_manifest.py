from __future__ import annotations

import hashlib
import json
import copy
import sys
import tempfile
import unittest
from pathlib import Path


VOICE_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(VOICE_ROOT))

from qwen_voice_worker import validate_runtime_manifest  # noqa: E402


def digest(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def create_runtime(root: Path) -> tuple[Path, Path, dict[str, Path]]:
    canary = root / "canary.wav"
    canary.write_bytes(b"RIFF-framewright-canary")
    locked_models = []
    manifest_models = []
    model_files: dict[str, Path] = {}
    for key, revision in (("design", "a" * 40), ("base", "b" * 40)):
        directory = f"models/{key}-{revision}"
        weight = root / directory / "model.safetensors"
        weight.parent.mkdir(parents=True, exist_ok=True)
        weight.write_bytes(f"{key}-weights".encode("ascii"))
        model_files[key] = weight
        repository = f"Framewright/Test-{key}"
        locked_models.append(
            {"key": key, "repository": repository, "revision": revision, "directory": directory}
        )
        manifest_models.append(
            {
                "key": key,
                "repository": repository,
                "revision": revision,
                "localPath": directory,
                "files": [
                    {
                        "path": weight.relative_to(root).as_posix(),
                        "bytes": weight.stat().st_size,
                        "sha256": digest(weight),
                    }
                ],
            }
        )

    lock_path = root / "models.lock.json"
    lock_path.write_text(json.dumps({"schemaVersion": 1, "models": locked_models}), encoding="utf-8")
    manifest = {
        "schemaVersion": 1,
        "lockSha256": digest(lock_path),
        "models": manifest_models,
        "canary": {
            "passed": True,
            "path": canary.name,
            "bytes": canary.stat().st_size,
            "sha256": digest(canary),
        },
    }
    manifest_path = root / "runtime-manifest.json"
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    return manifest_path, lock_path, model_files


class VoiceRuntimeManifestTests(unittest.TestCase):
    def test_validated_canary_and_exact_lock_are_required(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manifest_path, lock_path, _ = create_runtime(Path(directory))
            self.assertEqual(
                {"design": "a" * 40, "base": "b" * 40},
                validate_runtime_manifest(manifest_path, lock_path),
            )

            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["canary"]["passed"] = False
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "synthesis canary"):
                validate_runtime_manifest(manifest_path, lock_path)

    def test_same_size_model_tamper_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manifest_path, lock_path, models = create_runtime(Path(directory))
            original = models["design"].read_bytes()
            models["design"].write_bytes(b"X" * len(original))

            with self.assertRaisesRegex(RuntimeError, "integrity check"):
                validate_runtime_manifest(manifest_path, lock_path)

    def test_changed_canary_and_lock_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            manifest_path, lock_path, _ = create_runtime(Path(directory))
            canary = Path(directory) / "canary.wav"
            canary.write_bytes(b"X" * canary.stat().st_size)
            with self.assertRaisesRegex(RuntimeError, "integrity"):
                validate_runtime_manifest(manifest_path, lock_path)

            manifest_path, lock_path, _ = create_runtime(Path(directory))
            lock = json.loads(lock_path.read_text(encoding="utf-8"))
            lock["models"][0]["revision"] = "c" * 40
            lock_path.write_text(json.dumps(lock), encoding="utf-8")
            with self.assertRaisesRegex(RuntimeError, "model lock"):
                validate_runtime_manifest(manifest_path, lock_path)

    def test_current_python_and_package_environment_must_match_canary(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest_path, lock_path, _ = create_runtime(root)
            expected = {
                "python": {"path": "C:/verified/python.exe", "version": "3.12.0", "bytes": 42, "sha256": "a" * 64},
                "packagesRoot": "C:/verified/packages",
                "distributions": {"qwen-tts": "0.1.1", "torch": "2.9.1", "soundfile": "0.13.1"},
                "torch": {"cudaAvailable": True, "cuda": "13.0"},
                "files": [{"path": "C:/verified/qwen_tts/__init__.py", "bytes": 12, "sha256": "b" * 64}],
            }
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["environment"] = expected
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            packages = root / "packages"
            packages.mkdir()
            self.assertEqual(
                {"design": "a" * 40, "base": "b" * 40},
                validate_runtime_manifest(manifest_path, lock_path, packages, lambda _: copy.deepcopy(expected)),
            )

            wrong_python = copy.deepcopy(expected)
            wrong_python["python"]["path"] = "C:/changed/python.exe"
            with self.assertRaisesRegex(RuntimeError, "differs from the environment"):
                validate_runtime_manifest(manifest_path, lock_path, packages, lambda _: wrong_python)

            changed_package = copy.deepcopy(expected)
            changed_package["distributions"]["qwen-tts"] = "0.1.2"
            with self.assertRaisesRegex(RuntimeError, "differs from the environment"):
                validate_runtime_manifest(manifest_path, lock_path, packages, lambda _: changed_package)

    def test_missing_qwen_package_refuses_ready(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest_path, lock_path, _ = create_runtime(root)
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            manifest["environment"] = {"verified": True}
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            packages = root / "packages"
            packages.mkdir()

            def missing_qwen(_: Path) -> dict[str, object]:
                raise RuntimeError("Required voice distribution is missing: qwen-tts")

            with self.assertRaisesRegex(RuntimeError, "qwen-tts"):
                validate_runtime_manifest(manifest_path, lock_path, packages, missing_qwen)


if __name__ == "__main__":
    unittest.main()
