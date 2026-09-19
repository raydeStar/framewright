"""Authenticated workstation boundary for Framewright's local Qwen3-TTS runtime.

The web application can live in Docker while this deliberately small service
owns the Windows GPU process. It accepts only two bounded operations and never
loads arbitrary paths supplied by a client.
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import hmac
import importlib.metadata
import json
import os
import subprocess
import sys
import tempfile
import threading
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path


MAX_REQUEST_BYTES = 75 * 1024 * 1024
GPU_LOCK = threading.Lock()


class VoiceWorker:
    def __init__(self, python: Path, packages: Path, bridge: Path, manifest: Path, lock: Path, token: str) -> None:
        self.python = python.resolve()
        self.packages = packages.resolve()
        self.bridge = bridge.resolve()
        self.manifest = manifest.resolve()
        self.token = token
        self.models = validate_runtime_manifest(self.manifest, lock, self.packages)

    def run(self, arguments: list[str], timeout_seconds: int = 1800) -> None:
        command = [
            str(self.python),
            str(self.bridge),
            "--packages",
            str(self.packages),
            "--manifest",
            str(self.manifest),
            *arguments,
        ]
        with GPU_LOCK:
            completed = subprocess.run(
                command,
                check=False,
                capture_output=True,
                text=True,
                timeout=timeout_seconds,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
            )
        if completed.returncode != 0:
            detail = (completed.stderr or completed.stdout or "Qwen returned no diagnostic.")[-2000:]
            raise RuntimeError(detail)


class Handler(BaseHTTPRequestHandler):
    server_version = "FramewrightVoice/1.0"

    @property
    def worker(self) -> VoiceWorker:
        return self.server.worker  # type: ignore[attr-defined]

    def do_GET(self) -> None:  # noqa: N802 - stdlib handler contract
        if not self._authorized():
            return
        if self.path != "/health":
            self._json(HTTPStatus.NOT_FOUND, {"error": "Unknown route."})
            return
        self._json(
            HTTPStatus.OK,
            {"status": "ready", "model": "Qwen3-TTS local", "busy": GPU_LOCK.locked(), "revisions": self.worker.models},
        )

    def do_POST(self) -> None:  # noqa: N802 - stdlib handler contract
        if not self._authorized():
            return
        try:
            payload = self._read_json()
            if self.path == "/v1/design":
                self._design(payload)
            elif self.path == "/v1/clone":
                self._clone(payload)
            else:
                self._json(HTTPStatus.NOT_FOUND, {"error": "Unknown route."})
        except (ValueError, KeyError, TypeError) as exc:
            self._json(HTTPStatus.BAD_REQUEST, {"error": str(exc)})
        except subprocess.TimeoutExpired:
            self._json(HTTPStatus.GATEWAY_TIMEOUT, {"error": "The local voice render exceeded 30 minutes and was stopped."})
        except Exception as exc:  # The boundary returns a bounded diagnostic, never a traceback.
            self._json(HTTPStatus.INTERNAL_SERVER_ERROR, {"error": str(exc)[-2000:]})

    def _design(self, payload: dict[str, object]) -> None:
        direction = self._text(payload, "direction", 20, 1000)
        text = self._text(payload, "text", 20, 150)
        count = int(payload.get("count", 0))
        seed_base = int(payload.get("seedBase", 0))
        if count not in (1, 2, 3):
            raise ValueError("count must be between one and three")
        if seed_base < 0:
            raise ValueError("seedBase must be non-negative")
        with tempfile.TemporaryDirectory(prefix="framewright-qwen-worker-") as directory:
            target = Path(directory)
            self.worker.run(["design", "--direction", direction, "--text", text, "--count", str(count), "--seed-base", str(seed_base), "--output-dir", str(target)])
            auditions = []
            for index in range(count):
                audio = target / f"audition-{index + 1}.wav"
                if not audio.is_file():
                    raise RuntimeError("Qwen completed without every requested audition.")
                auditions.append({"seed": seed_base + index, "audioBase64": base64.b64encode(audio.read_bytes()).decode("ascii")})
        self._json(HTTPStatus.OK, {"model": "Qwen3-TTS-12Hz-1.7B-VoiceDesign", "auditions": auditions, "audioBase64": None, "error": None})

    def _clone(self, payload: dict[str, object]) -> None:
        reference_text = self._text(payload, "referenceText", 1, 500)
        text = self._text(payload, "text", 1, 4000)
        encoded = self._text(payload, "referenceAudioBase64", 16, MAX_REQUEST_BYTES)
        try:
            reference_audio = base64.b64decode(encoded, validate=True)
        except ValueError as exc:
            raise ValueError("referenceAudioBase64 is invalid") from exc
        if len(reference_audio) > 50 * 1024 * 1024:
            raise ValueError("reference audio exceeds 50 MB")
        with tempfile.TemporaryDirectory(prefix="framewright-qwen-clone-") as directory:
            root = Path(directory)
            reference = root / "reference.wav"
            output = root / "dialogue.wav"
            reference.write_bytes(reference_audio)
            self.worker.run(["clone", "--reference", str(reference), "--reference-text", reference_text, "--text", text, "--output", str(output)])
            if not output.is_file():
                raise RuntimeError("Qwen completed without dialogue audio.")
            audio = base64.b64encode(output.read_bytes()).decode("ascii")
        self._json(HTTPStatus.OK, {"model": "Qwen3-TTS-12Hz-1.7B-Base", "auditions": [], "audioBase64": audio, "error": None})

    def _authorized(self) -> bool:
        supplied = self.headers.get("X-Framewright-Voice-Token", "")
        if hmac.compare_digest(supplied, self.worker.token):
            return True
        self._json(HTTPStatus.UNAUTHORIZED, {"error": "Voice worker token rejected."})
        return False

    def _read_json(self) -> dict[str, object]:
        transfer_encoding = self.headers.get("Transfer-Encoding", "").lower()
        if transfer_encoding == "chunked":
            body = self._read_chunked_body()
        else:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0 or length > MAX_REQUEST_BYTES:
                raise ValueError("request body is empty or too large")
            body = self.rfile.read(length)
        decoded = json.loads(body)
        if not isinstance(decoded, dict):
            raise ValueError("request body must be a JSON object")
        return decoded

    def _read_chunked_body(self) -> bytes:
        body = bytearray()
        while True:
            size_line = self.rfile.readline(128)
            if not size_line or len(size_line) >= 128:
                raise ValueError("request chunk header is invalid")
            try:
                size = int(size_line.split(b";", 1)[0].strip(), 16)
            except ValueError as exc:
                raise ValueError("request chunk header is invalid") from exc
            if size == 0:
                # Consume the terminal CRLF and ignore optional trailers; this
                # worker's clients do not emit them.
                self.rfile.readline(2)
                break
            if len(body) + size > MAX_REQUEST_BYTES:
                raise ValueError("request body is empty or too large")
            chunk = self.rfile.read(size)
            if len(chunk) != size or self.rfile.read(2) != b"\r\n":
                raise ValueError("request chunk is truncated")
            body.extend(chunk)
        if not body:
            raise ValueError("request body is empty or too large")
        return bytes(body)

    @staticmethod
    def _text(payload: dict[str, object], key: str, minimum: int, maximum: int) -> str:
        value = payload.get(key)
        if not isinstance(value, str) or not minimum <= len(value.strip()) <= maximum:
            raise ValueError(f"{key} must contain {minimum} to {maximum} characters")
        return value.strip()

    def _json(self, status: HTTPStatus, payload: dict[str, object]) -> None:
        body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
        self.send_response(status.value)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format: str, *args: object) -> None:
        print(f"[Framewright voice] {self.address_string()} {format % args}", flush=True)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _integrity_entry(path: Path) -> dict[str, object]:
    resolved = path.resolve()
    return {"path": str(resolved), "bytes": resolved.stat().st_size, "sha256": _sha256(resolved)}


def capture_runtime_environment(packages_path: Path) -> dict[str, object]:
    """Capture the bounded runtime surface required before health may bind."""
    packages = packages_path.resolve()
    if not packages.is_dir():
        raise RuntimeError(f"Voice package directory does not exist: {packages}")
    package_text = str(packages)
    if not sys.path or sys.path[0] != package_text:
        sys.path.insert(0, package_text)
    try:
        import qwen_tts
        import soundfile
        import torch
        from qwen_tts import Qwen3TTSModel  # noqa: F401 - proves the public runtime import
    except Exception as exc:
        raise RuntimeError(f"Voice runtime imports failed: {exc}") from exc
    if not torch.cuda.is_available():
        raise RuntimeError("Voice runtime imported, but CUDA is unavailable.")

    distribution_names = ("qwen-tts", "torch", "soundfile", "accelerate", "onnxruntime", "sox", "transformers")
    distributions: dict[str, str] = {}
    integrity_paths: set[Path] = set()
    for name in distribution_names:
        try:
            distribution = importlib.metadata.distribution(name)
        except importlib.metadata.PackageNotFoundError as exc:
            raise RuntimeError(f"Required voice distribution is missing: {name}") from exc
        distributions[name] = distribution.version
        for entry in distribution.files or ():
            normalized = entry.as_posix()
            if normalized.endswith(".dist-info/METADATA") or normalized.endswith(".dist-info/RECORD"):
                located = Path(distribution.locate_file(entry)).resolve()
                if located.is_file():
                    integrity_paths.add(located)

    qwen_file = Path(qwen_tts.__file__ or "").resolve()
    if not qwen_file.is_file():
        raise RuntimeError("The qwen_tts module has no verifiable source path.")
    qwen_root = qwen_file.parent
    integrity_paths.update(
        path.resolve()
        for path in qwen_root.rglob("*")
        if path.is_file() and "__pycache__" not in path.parts and path.suffix.lower() != ".pyc"
    )
    for module_file in (getattr(torch, "__file__", None), getattr(torch._C, "__file__", None), getattr(soundfile, "__file__", None)):
        if module_file:
            candidate = Path(module_file).resolve()
            if candidate.is_file():
                integrity_paths.add(candidate)

    python = Path(sys.executable).resolve()
    if not python.is_file():
        raise RuntimeError(f"Voice Python executable is missing: {python}")
    return {
        "python": {
            "path": str(python),
            "version": sys.version,
            "bytes": python.stat().st_size,
            "sha256": _sha256(python),
        },
        "packagesRoot": str(packages),
        "distributions": distributions,
        "torch": {"cudaAvailable": True, "cuda": str(torch.version.cuda or "")},
        "files": [_integrity_entry(path) for path in sorted(integrity_paths, key=lambda item: str(item).lower())],
    }


def validate_runtime_environment(
    expected: object,
    packages_path: Path,
    probe=capture_runtime_environment,
) -> None:
    if not isinstance(expected, dict):
        raise RuntimeError("Voice runtime manifest has no verified Python environment.")
    actual = probe(packages_path)
    if actual != expected:
        raise RuntimeError(
            "The current Python, package, or CUDA runtime differs from the environment that passed the voice canary. "
            "Run scripts/setup-voice-worker.ps1 again."
        )


def validate_runtime_manifest(
    manifest_path: Path,
    lock_path: Path | None = None,
    packages_path: Path | None = None,
    environment_probe=capture_runtime_environment,
) -> dict[str, str]:
    runtime_root = manifest_path.resolve().parent
    resolved_lock = (lock_path or Path(__file__).with_name("models.lock.json")).resolve()
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        model_lock = json.loads(resolved_lock.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise RuntimeError(f"Voice runtime manifest is unreadable: {exc}") from exc
    if manifest.get("schemaVersion") != 1 or manifest.get("canary", {}).get("passed") is not True:
        raise RuntimeError("Voice runtime setup has not completed its synthesis canary.")
    if model_lock.get("schemaVersion") != 1 or not isinstance(model_lock.get("models"), list):
        raise RuntimeError("Voice model lock is invalid.")
    lock_digest = _sha256(resolved_lock)
    if not hmac.compare_digest(lock_digest, str(manifest.get("lockSha256", ""))):
        raise RuntimeError("Voice runtime manifest does not match the checked-in model lock.")

    expected_models = {str(item.get("key", "")): item for item in model_lock["models"]}
    if set(expected_models) != {"design", "base"}:
        raise RuntimeError("Voice model lock must contain exactly the design and base models.")

    revisions: dict[str, str] = {}
    for model in manifest.get("models", []):
        key = str(model.get("key", ""))
        revision = str(model.get("revision", ""))
        expected = expected_models.get(key)
        if expected is None or key in revisions:
            raise RuntimeError("Voice runtime manifest contains an invalid model identity.")
        if (
            revision != str(expected.get("revision", ""))
            or str(model.get("repository", "")) != str(expected.get("repository", ""))
            or str(model.get("localPath", "")) != str(expected.get("directory", ""))
        ):
            raise RuntimeError(f"Pinned {key} model identity does not match the checked-in lock.")
        revisions[key] = revision
        files = model.get("files", [])
        if not files:
            raise RuntimeError(f"Pinned {key} model has no integrity inventory.")
        inventoried: set[Path] = set()
        for entry in files:
            path = (runtime_root / str(entry.get("path", ""))).resolve()
            if not path.is_relative_to(runtime_root) or not path.is_file() or path in inventoried:
                raise RuntimeError(f"Pinned {key} model file is missing: {path}")
            inventoried.add(path)
            if path.stat().st_size != int(entry.get("bytes", -1)):
                raise RuntimeError(f"Pinned {key} model file has changed size: {path}")
            expected_digest = str(entry.get("sha256", ""))
            if len(expected_digest) != 64 or not hmac.compare_digest(_sha256(path), expected_digest):
                raise RuntimeError(f"Pinned {key} model file failed its integrity check: {path}")
        model_root = (runtime_root / str(model.get("localPath", ""))).resolve()
        actual_files = {
            path.resolve()
            for path in model_root.rglob("*")
            if path.is_file() and ".cache" not in path.parts
        }
        if actual_files != inventoried:
            raise RuntimeError(f"Pinned {key} model inventory contains added or missing files.")
    if set(revisions) != {"design", "base"}:
        raise RuntimeError("Voice runtime requires both pinned design and base models.")

    canary = manifest["canary"]
    canary_path = (runtime_root / str(canary.get("path", ""))).resolve()
    if not canary_path.is_relative_to(runtime_root) or not canary_path.is_file():
        raise RuntimeError("Voice runtime canary WAV is missing.")
    if canary_path.stat().st_size != int(canary.get("bytes", -1)):
        raise RuntimeError("Voice runtime canary WAV has changed size.")
    digest = _sha256(canary_path)
    if not hmac.compare_digest(digest, str(canary.get("sha256", ""))):
        raise RuntimeError("Voice runtime canary WAV failed its integrity check.")
    if packages_path is not None:
        validate_runtime_environment(manifest.get("environment"), packages_path, environment_probe)
    return revisions


def main() -> None:
    parser = argparse.ArgumentParser(description="Run Framewright's authenticated local Qwen voice worker.")
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=5181)
    parser.add_argument("--python", type=Path, default=Path(r"C:\Comfy\python_embeded\python.exe"))
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--lock", type=Path, default=Path(__file__).with_name("models.lock.json"))
    parser.add_argument("--bridge", type=Path, default=Path(__file__).with_name("qwen_voice_tool.py"))
    parser.add_argument("--token", default=os.environ.get("FRAMEWRIGHT_VOICE_WORKER_TOKEN", ""))
    args = parser.parse_args()
    if len(args.token) < 32:
        raise SystemExit("FRAMEWRIGHT_VOICE_WORKER_TOKEN must contain at least 32 characters.")
    for required in (args.python, args.packages, args.bridge, args.manifest, args.lock):
        if not required.exists():
            raise SystemExit(f"Required Qwen path not found: {required}")
    # Validate the exact checked-in lock before binding a health endpoint. The
    # worker performs this expensive hash pass once per process, not per poll.
    worker = VoiceWorker(args.python, args.packages, args.bridge, args.manifest, args.lock, args.token)
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    server.worker = worker  # type: ignore[attr-defined]
    print(f"Framewright voice worker ready on http://{args.host}:{args.port}; the local oracle is listening.", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
