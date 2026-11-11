"""Offline-first MOSS SFX planning, standard-library DSP, and immutable runs.

Torch and MOSS enter only through get_pipeline(). The butler checks the guest
list before inviting eleven gigabytes of orchestra into the drawing room.
"""
from __future__ import annotations

from array import array
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import importlib.metadata
import importlib.util
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import threading
import time
from urllib.parse import urlparse
from urllib.request import url2pathname
import wave

SAMPLE_RATE = 48000
MOSS_COMMIT = "934d6826b084c46a0d033402174d5f8ac4ed2519"
MODEL_ID = "OpenMOSS-Team/MOSS-SoundEffect-v2.0"
PEAK_LIMIT = 10 ** (-1 / 20)
DEFAULTS = {"steps": 100, "cfg": 4.0, "sigma_shift": 5.0,
            "negative_prompt": "music, speech, voice, singing, distortion, low quality",
            "normalize": {"mode": "peak", "target_db": -1.0}}
_GPU_LOCK = threading.RLock()
_PIPELINE = None
_PIPELINE_CONFIG = None
_PIPELINE_METADATA = None


def utc_now():
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def env_bool(name, default):
    value = os.environ.get(name, str(default)).lower().strip()
    if value not in {"true", "false"}:
        raise ValueError(f"{name} must be true or false")
    return value == "true"


@dataclass(frozen=True)
class Config:
    model: str = MODEL_ID
    device: str = "cuda"
    allow_downloads: bool = False
    output_path: str | None = None
    revision: str | None = None
    compile: bool = False

    @classmethod
    def from_env(cls):
        compile_default = sys.platform != "win32" and os.environ.get("TORCHDYNAMO_DISABLE") != "1"
        game_repo = os.environ.get("GAME_REPO")
        output = os.environ.get("MOSS_SFX_OUTPUT_PATH")
        if not output and game_repo:
            output = str(Path(game_repo) / "SourceAudio" / "SFX")
        config = cls(model=os.environ.get("MOSS_SFX_MODEL", MODEL_ID),
                     device=os.environ.get("MOSS_SFX_DEVICE", "cuda"),
                     allow_downloads=env_bool("MOSS_SFX_ALLOW_DOWNLOADS", False),
                     output_path=output,
                     revision=os.environ.get("MOSS_SFX_REVISION") or None,
                     compile=env_bool("MOSS_SFX_COMPILE", compile_default))
        if not config.model.strip():
            raise ValueError("MOSS_SFX_MODEL must not be empty")
        if not re.fullmatch(r"cuda(?::[0-9]+)?|cpu", config.device):
            raise ValueError("MOSS_SFX_DEVICE must be cuda, cuda:N, or cpu")
        return config


def output_root(config, override=None):
    value = override or config.output_path
    if not value:
        raise ValueError("Set GAME_REPO, MOSS_SFX_OUTPUT_PATH, or --out; no game repository is assumed")
    result = Path(value).expanduser().resolve()
    if any(part.casefold() == "content" for part in result.parts):
        raise ValueError("Output must be outside Unreal Content/; use SourceAudio/SFX")
    return result


def _object(value, allowed, label):
    if not isinstance(value, dict):
        raise ValueError(f"{label} must be an object")
    unknown = set(value) - allowed
    if unknown:
        raise ValueError(f"{label}: unknown fields: {', '.join(sorted(unknown))}")


def _number(value, low, high, label, integer=False):
    if (isinstance(value, bool) or not isinstance(value, int if integer else (int, float))
            or not low <= value <= high):
        raise ValueError(f"{label} must be {'an integer' if integer else 'a number'} from {low} to {high}")
    return value


def _normalize_settings(value):
    _object(value, {"mode", "target_db"}, "normalize")
    mode = value.get("mode", "peak")
    if mode not in ("peak", "rms"):
        raise ValueError("normalize.mode must be peak or rms")
    target = _number(value.get("target_db", -1.0), -60, -1, "normalize.target_db")
    return {"mode": mode, "target_db": float(target)}


def _settings(value):
    _number(value["steps"], 1, 1000, "steps", integer=True)
    _number(value["cfg"], 0, 100, "cfg")
    _number(value["sigma_shift"], 0.01, 100, "sigma_shift")
    if not isinstance(value["negative_prompt"], str) or len(value["negative_prompt"]) > 8000:
        raise ValueError("negative_prompt must be a string of at most 8000 characters")
    value["normalize"] = _normalize_settings(value["normalize"])
    return value


def validate_sound_list(document):
    _object(document, {"defaults", "sounds"}, "sound list")
    defaults = document.get("defaults", {})
    _object(defaults, set(DEFAULTS), "defaults")
    defaults = _settings({**DEFAULTS, **defaults})
    sounds = document.get("sounds")
    if not isinstance(sounds, list) or not sounds:
        raise ValueError("sounds must be a non-empty array")
    seen, result = set(), []
    for index, sound in enumerate(sounds):
        label = f"sounds[{index}]"
        _object(sound, set(DEFAULTS) | {"name", "category", "prompt", "seconds", "variations", "seed", "loop"}, label)
        name = sound.get("name")
        if not isinstance(name, str) or not re.fullmatch(r"[A-Z][A-Za-z0-9]{0,95}", name):
            raise ValueError(f"{label}.name must be PascalCase ASCII (1 to 96 letters/digits)")
        if name.casefold() in seen:
            raise ValueError(f"duplicate name: {name} (Windows filenames are case-insensitive)")
        seen.add(name.casefold())
        category = sound.get("category")
        if (not isinstance(category, str) or not re.fullmatch(r"[A-Za-z][A-Za-z0-9 _-]{0,63}", category)
                or category != category.rstrip()
                or re.fullmatch(r"CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9]", category, re.I)):
            raise ValueError(f"{label}.category must be a safe ASCII folder name")
        prompt = sound.get("prompt")
        if not isinstance(prompt, str) or not prompt.strip() or len(prompt) > 8000:
            raise ValueError(f"{label}.prompt must contain 1 to 8000 characters")
        seconds = _number(sound.get("seconds"), 0.1, 30, f"{label}.seconds")
        variations = _number(sound.get("variations", 1), 1, 8, f"{label}.variations", integer=True)
        seed = sound.get("seed")
        if seed is not None:
            _number(seed, 0, 2**32 - variations, f"{label}.seed", integer=True)
        loop = sound.get("loop", False)
        if not isinstance(loop, bool):
            raise ValueError(f"{label}.loop must be true or false")
        settings = {**defaults, **{k: sound[k] for k in DEFAULTS if k in sound}}
        if settings["normalize"] is None:
            settings["normalize"] = defaults["normalize"]
        result.append({**_settings(settings), "name": name, "category": category,
                       "prompt": prompt, "seconds": float(seconds), "variations": variations,
                       "seed": seed, "loop": loop})
    return result


def _unique_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON field: {key}")
        result[key] = value
    return result


def load_sound_list(path):
    with Path(path).open(encoding="utf-8-sig") as stream:
        return validate_sound_list(json.load(stream, object_pairs_hook=_unique_keys))


def plan_jobs(sounds, only=None, steps=None):
    if steps is not None:
        _number(steps, 1, 1000, "steps", integer=True)
    selected = set(only) if only is not None else None
    if selected is not None:
        unknown = selected - {sound["name"] for sound in sounds}
        if unknown or not selected:
            raise ValueError(f"--only contains unknown or empty names: {', '.join(sorted(unknown))}")
    jobs = []
    for sound in sounds:
        if selected is not None and sound["name"] not in selected:
            continue
        seed = sound["seed"]
        if seed is None:
            seed = int.from_bytes(hashlib.sha256(sound["name"].encode("ascii")).digest()[:4], "big") & 0x7fffffff
        for index in range(sound["variations"]):
            seconds = round(sound["seconds"], 1)  # Match the pinned pipeline, including rounding ties.
            jobs.append({**sound, "seed": seed + index, "variation": index + 1,
                         "steps": steps if steps is not None else sound["steps"],
                         "seconds_requested": sound["seconds"], "seconds": seconds,
                         "effective_prompt": f"{sound['prompt'].strip()} duration: {seconds:.1f}s",
                         "file": f"{sound['category']}/SFX_{sound['name']}_{index + 1:02d}.wav"})
    return jobs


def levels(samples):
    if not samples:
        return {"peak_db": None, "rms_db": None}
    peak = max(abs(value) for value in samples)
    rms = math.sqrt(math.fsum(value * value for value in samples) / len(samples))
    return {"peak_db": 20 * math.log10(peak) if peak else None,
            "rms_db": 20 * math.log10(rms) if rms else None}


def validate_audio(samples):
    if not samples or any(not math.isfinite(value) for value in samples):
        raise ValueError("audio must contain finite mono samples")
    if max(abs(value) for value in samples) < 10 ** (-60 / 20):
        raise ValueError("near-silent output: peak below -60 dBFS")


def trim_silence(samples, sample_rate=SAMPLE_RATE):
    threshold = 10 ** (-50 / 20)
    first = next((i for i, value in enumerate(samples) if abs(value) >= threshold), None)
    if first is None:
        raise ValueError("no audio above the -50 dBFS trim threshold")
    last = next(i for i in range(len(samples) - 1, -1, -1) if abs(samples[i]) >= threshold)
    return array("d", samples[max(0, first - round(sample_rate * .010)):last + 1])


def apply_fades(samples, sample_rate=SAMPLE_RATE):
    result = array("d", samples)
    for count, fade_in in ((min(len(result), round(sample_rate * .005)), True),
                           (min(len(result), round(sample_rate * .020)), False)):
        for i in range(count):
            gain = i / (count - 1) if count > 1 else 0.0
            position = i if fade_in else len(result) - 1 - i
            result[position] *= gain
    return result


def loop_crossfade(samples, sample_rate=SAMPLE_RATE):
    """Overlap tail into head; shorten by 0.5s (half-length for clips <1s).

    The seam joins adjacent original samples. Do not fade a loop to silence:
    a conspicuous breath every twenty seconds is a rather poor disguise.
    """
    count = min(round(sample_rate * .5), len(samples) // 2)
    if count < 2:
        raise ValueError("loop needs at least four samples")
    result = array("d", (samples[len(samples) - count + i] * math.cos(math.pi * .5 * i / (count - 1))
                         + samples[i] * math.sin(math.pi * .5 * i / (count - 1))
                         for i in range(count)))
    result.extend(samples[count:len(samples) - count])
    return result


def normalize_audio(samples, settings):
    settings = _normalize_settings(settings)
    peak = max(abs(value) for value in samples)
    reference = peak if settings["mode"] == "peak" else math.sqrt(math.fsum(v * v for v in samples) / len(samples))
    if not reference:
        raise ValueError("cannot normalize silence")
    gain = min(10 ** (settings["target_db"] / 20) / reference, PEAK_LIMIT / peak)
    return array("d", (value * gain for value in samples))


def post_process(samples, *, loop=False, normalize=None):
    validate_audio(samples)
    processed = loop_crossfade(samples) if loop else apply_fades(trim_silence(samples))
    validate_audio(processed)
    return normalize_audio(processed, normalize or DEFAULTS["normalize"])


def pcm16(samples):
    # Quantization must respect the ceiling too, not round one count above it.
    ceiling = math.floor(PEAK_LIMIT * 32768)
    result = array("h", (max(-ceiling, min(ceiling, round(value * 32768))) for value in samples))
    if sys.byteorder != "little":
        result.byteswap()
    return result.tobytes()


def write_wav(path, samples):
    data = pcm16(samples)
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream, wave.open(stream, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(SAMPLE_RATE)
        wav.writeframes(data)
    quantized = array("h")
    quantized.frombytes(data)
    if sys.byteorder != "little":
        quantized.byteswap()
    return levels(array("d", (value / 32768 for value in quantized)))


def sha256(path):
    with Path(path).open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest() if hasattr(hashlib, "file_digest") else _hash_stream(stream)


def _hash_stream(stream):
    digest = hashlib.sha256()
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        digest.update(block)
    return digest.hexdigest()


def _cache_root():
    home = Path(os.environ.get("HF_HOME", Path(os.environ.get("XDG_CACHE_HOME", Path.home() / ".cache")) / "huggingface"))
    return Path(os.environ.get("HF_HUB_CACHE", os.environ.get("HUGGINGFACE_HUB_CACHE", home / "hub"))).expanduser()


def local_snapshot(config):
    """Inspect cache files directly: no Hub client, imports, or network needed."""
    local = Path(config.model).expanduser()
    if local.is_dir():
        return local.resolve()
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", config.model):
        return None
    root = _cache_root() / ("models--" + config.model.replace("/", "--"))
    revision = config.revision or "main"
    if not re.fullmatch(r"[A-Za-z0-9_./-]+", revision) or ".." in revision.split("/"):
        return None
    ref = root / "refs" / revision
    if ref.is_file():
        revision = ref.read_text(encoding="utf-8").strip()
    if not re.fullmatch(r"[a-fA-F0-9]{40}", revision):
        return None
    path = root / "snapshots" / revision
    return path.resolve() if path.is_dir() else None


def snapshot_errors(path):
    if path is None:
        return ["model snapshot is not available locally"]
    required = ["model_index.json", "scheduler/scheduler_config.json", "transformer/config.json",
                "transformer/diffusion_pytorch_model.safetensors", "vae/config.json",
                "text_encoder/config.json", "tokenizer/tokenizer_config.json", "tokenizer/tokenizer.json"]
    required.append("vae/vae_128d_48k.pth" if (path / "vae/vae_128d_48k.pth").is_file()
                    else "vae/diffusion_pytorch_model.safetensors")
    index = path / "text_encoder/model.safetensors.index.json"
    if index.is_file():
        try:
            shards = set(json.loads(index.read_text(encoding="utf-8"))["weight_map"].values())
            if not shards or any(not isinstance(s, str) or Path(s).name != s or "/" in s or "\\" in s for s in shards):
                raise ValueError("unsafe or empty shard index")
            required.extend("text_encoder/" + shard for shard in shards)
        except (ValueError, KeyError, TypeError, AttributeError) as exc:
            return [f"invalid text encoder shard index: {exc}"]
    else:
        required.append("text_encoder/model.safetensors")
    errors = [f"missing or empty {name}" for name in required
              if not (path / name).is_file() or (path / name).stat().st_size == 0]
    if not errors:
        try:
            model = json.loads((path / "model_index.json").read_text(encoding="utf-8"))
            if model.get("sample_rate", SAMPLE_RATE) != SAMPLE_RATE or model.get("max_inference_seconds", 30) != 30:
                errors.append("snapshot must use 48000 Hz and the 30-second latent")
        except (ValueError, AttributeError) as exc:
            errors.append(f"invalid model_index.json: {exc}")
    return errors


def inspect_runtime(config):
    packages = {name: importlib.util.find_spec(name) is not None
                for name in ("torch", "torchaudio", "moss_soundeffect_v2")}
    path = local_snapshot(config)
    errors = snapshot_errors(path)
    missing = [name for name, present in packages.items() if not present]
    if missing:
        errors.insert(0, "packages not installed: " + ", ".join(missing))
    if sys.version_info < (3, 12):
        errors.insert(0, "MOSS runtime requires Python >=3.12")
    return {"ready": not errors, "detail": "; ".join(errors) if errors else
            "Runtime packages and snapshot files present; CUDA and weight loading are unverified. The orchestra remains seated.",
            "packages": packages, "model_id": config.model, "snapshot_path": str(path) if path else None,
            "revision": config.revision, "device": config.device, "compile": config.compile,
            "downloads_allowed": config.allow_downloads, "weights_loaded": False}


def source_provenance():
    """Read installed metadata; never label an arbitrary checkout as the pinned one."""
    result = {"moss_repo_commit": None, "moss_source_dirty": None, "moss_expected_commit": MOSS_COMMIT}
    try:
        dist = importlib.metadata.distribution("moss-soundeffect-v2")
        direct = json.loads(dist.read_text("direct_url.json") or "{}")
        result["moss_repo_commit"] = direct.get("vcs_info", {}).get("commit_id")
        url = urlparse(direct.get("url", ""))
        if url.scheme == "file":
            directory = url2pathname(url.path)
            revision = subprocess.run(["git", "-C", directory, "rev-parse", "HEAD"], capture_output=True, text=True, timeout=10)
            status = subprocess.run(["git", "-C", directory, "status", "--porcelain", "--untracked-files=no"], capture_output=True, text=True, timeout=10)
            if revision.returncode == 0:
                result["moss_repo_commit"] = revision.stdout.strip()
                result["moss_source_dirty"] = bool(status.stdout.strip()) if status.returncode == 0 else None
    except (importlib.metadata.PackageNotFoundError, ValueError, OSError, subprocess.SubprocessError):
        pass
    return result


def get_pipeline(config):
    global _PIPELINE, _PIPELINE_CONFIG, _PIPELINE_METADATA
    with _GPU_LOCK:
        if _PIPELINE is not None:
            if _PIPELINE_CONFIG != config:
                raise ValueError("pipeline configuration changed; start a new process")
            return _PIPELINE, dict(_PIPELINE_METADATA)
        # Set this before importing the model's torch.compile decorators.
        os.environ["TORCHDYNAMO_DISABLE"] = "0" if config.compile else "1"
        if not config.allow_downloads:
            os.environ["HF_HUB_OFFLINE"] = "1"
            os.environ["TRANSFORMERS_OFFLINE"] = "1"
        path = local_snapshot(config)
        if not config.allow_downloads:
            errors = snapshot_errors(path)
            if errors:
                raise RuntimeError("; ".join(errors))
        import torch
        from moss_soundeffect_v2 import MossSoundEffectPipeline

        if config.device.startswith("cuda") and not torch.cuda.is_available():
            raise RuntimeError("CUDA requested but unavailable; refusing an implicit CPU fallback")
        start = time.perf_counter()
        pipe = MossSoundEffectPipeline.from_pretrained(str(path) if path else config.model,
                 torch_dtype=torch.bfloat16, device=config.device,
                 local_files_only=not config.allow_downloads, revision=config.revision)
        if pipe.sample_rate != SAMPLE_RATE or pipe.max_inference_seconds != 30:
            raise RuntimeError("pipeline must use 48000 Hz and the 30-second latent")
        if config.device.startswith("cuda"):
            torch.cuda.synchronize(config.device)
        path = local_snapshot(config)
        metadata = {"model_id": config.model, "snapshot_path": str(path) if path else None,
                    "snapshot_revision": path.name if path and re.fullmatch(r"[a-f0-9]{40}", path.name) else config.revision,
                    "torch_version": torch.__version__, "compile": config.compile, "device": str(pipe.device),
                    "load_seconds": time.perf_counter() - start, **source_provenance()}
        _PIPELINE, _PIPELINE_CONFIG, _PIPELINE_METADATA = pipe, config, metadata
        return pipe, dict(metadata)


def generate_take(job, config):
    with _GPU_LOCK:  # One model call at a time, even if a future worker has threads.
        pipe, metadata = get_pipeline(config)
        start = time.perf_counter()
        audio = pipe(prompt=job["prompt"], seconds=job["seconds"], num_inference_steps=job["steps"],
                     cfg_scale=job["cfg"], sigma_shift=job["sigma_shift"], seed=job["seed"],
                     negative_prompt=job["negative_prompt"])
        if tuple(audio.shape[:2]) != (1, 1) or len(audio.shape) != 3:
            raise ValueError(f"expected audio shape (1, 1, T), got {tuple(audio.shape)}")
        # The CPU transfer synchronizes CUDA, so timings include completed inference.
        samples = array("d", audio[0, 0].detach().float().cpu().tolist())
        if len(samples) != int(SAMPLE_RATE * job["seconds"]):
            raise ValueError("pipeline returned an unexpected sample count")
        metadata["generation_seconds"] = time.perf_counter() - start
        validate_audio(samples)
        return samples, metadata


def run_name():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H-%M-%SZ")


def available_run(root, name):
    index = 1
    while True:
        candidate = root / (name if index == 1 else f"{name}-{index}")
        if not candidate.exists() and not candidate.with_name(candidate.name + ".partial").exists():
            return candidate
        index += 1


def reserve_run(root, name):
    root.mkdir(parents=True, exist_ok=True)
    while True:
        final = available_run(root, name)
        partial = final.with_name(final.name + ".partial")
        try:
            partial.mkdir(exist_ok=False)
            return partial, final
        except FileExistsError:
            continue


def atomic_json(path, document):
    temporary = path.with_name(path.name + ".tmp")
    with temporary.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(document, stream, indent=2, ensure_ascii=False, allow_nan=False)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    if path.exists():
        raise FileExistsError(f"refusing to overwrite {path}")
    temporary.rename(path)


def run_batch(jobs, root, config, *, generator=None):
    generator = generator or generate_take
    partial, final = reserve_run(Path(root), run_name())
    manifest = {"schema": "framewright.sfx.v1", "created_at": utc_now(), "license": "Apache-2.0",
                "model_id": config.model, "compile": config.compile, "downloads_allowed": config.allow_downloads,
                "sample_rate": SAMPLE_RATE, "channels": 1, "bits_per_sample": 16, "files": [], "failed": []}
    for job in jobs:
        entry = {key: job[key] for key in ("name", "category", "file", "prompt", "effective_prompt", "negative_prompt",
                 "seed", "steps", "cfg", "sigma_shift", "seconds_requested", "loop", "normalize")}
        entry.update(seconds_effective=job["seconds"], timestamp_utc=utc_now(), license="Apache-2.0",
                     model_id=config.model, compile=config.compile)
        start = time.perf_counter()
        try:
            raw, metadata = generator(job, config)
            entry.update(metadata)
            if len(raw) != int(SAMPLE_RATE * job["seconds"]):
                raise ValueError("pipeline returned an unexpected sample count")
            audio = post_process(raw, loop=job["loop"], normalize=job["normalize"])
            path = partial / job["file"]
            entry.update(write_wav(path, audio))
            entry.update(sha256=sha256(path), final_duration=len(audio) / SAMPLE_RATE,
                         raw_duration=len(raw) / SAMPLE_RATE,
                         loop_crossfade_seconds=(len(raw) - len(audio)) / SAMPLE_RATE if job["loop"] else 0)
            manifest["files"].append(entry)
        except Exception as exc:
            entry.update(error=f"{type(exc).__name__}: {exc}", elapsed_seconds=time.perf_counter() - start)
            manifest["failed"].append(entry)
    manifest["completed_at"] = utc_now()
    manifest["status"] = "completed" if not manifest["failed"] else "completed_with_failures"
    atomic_json(partial / "sfx-manifest.json", manifest)  # The receipt is always the last guest to arrive.
    if final.exists():
        raise FileExistsError(f"run target appeared during generation; preserved staging at {partial}")
    partial.rename(final)
    return final, manifest
