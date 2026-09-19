"""Run the pinned local Qwen model and record a real synthesis canary."""

from __future__ import annotations

import argparse
import gc
import hashlib
import json
import os
import sys
from datetime import datetime, timezone
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--lock", type=Path, required=True)
    parser.add_argument("--canary-output", type=Path, required=True)
    args = parser.parse_args()
    packages = args.packages.resolve()
    if not packages.is_dir():
        raise SystemExit(f"Voice package directory does not exist: {packages}")

    manifest_path = args.manifest.resolve()
    runtime_root = manifest_path.parent
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    lock = json.loads(args.lock.resolve().read_text(encoding="utf-8"))
    design = next((item for item in manifest.get("models", []) if item.get("key") == "design"), None)
    if design is None:
        raise SystemExit("The voice runtime manifest does not contain the pinned design model.")
    design_path = (runtime_root / str(design["localPath"])).resolve()
    if not design_path.is_relative_to(runtime_root) or not design_path.is_dir():
        raise SystemExit(f"The pinned design model is unavailable: {design_path}")

    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    worker_root = Path(__file__).resolve().parent
    sys.path.insert(0, str(worker_root))
    sys.path.insert(0, str(packages))
    try:
        import soundfile  # noqa: F401 - import is the verification
        import torch
        from qwen_tts import Qwen3TTSModel
        from qwen_voice_worker import capture_runtime_environment
    except Exception as exc:
        raise SystemExit(f"Voice runtime imports failed: {exc}") from exc

    if not torch.cuda.is_available():
        raise SystemExit(
            "PyTorch imported, but CUDA is unavailable. Use the GPU-enabled "
            "ComfyUI Python runtime before generating local voices."
        )
    canary = lock.get("canary", {})
    torch.manual_seed(int(canary["seed"]))
    model = Qwen3TTSModel.from_pretrained(
        str(design_path),
        local_files_only=True,
        device_map="cuda:0",
        dtype=torch.bfloat16,
        attn_implementation="sdpa",
    )
    wavs, sample_rate = model.generate_voice_design(
        text=str(canary["text"]),
        language="English",
        instruct=str(canary["direction"]),
    )
    output = args.canary_output.resolve()
    if not output.is_relative_to(runtime_root):
        raise SystemExit("Canary output must stay inside the voice runtime root.")
    output.parent.mkdir(parents=True, exist_ok=True)
    soundfile.write(output, wavs[0], sample_rate)
    info = soundfile.info(output)
    if info.frames <= 0 or info.samplerate <= 0 or output.stat().st_size < 1_000:
        raise SystemExit("Qwen completed without producing a valid canary WAV.")

    digest = hashlib.sha256(output.read_bytes()).hexdigest()
    manifest["canary"] = {
        "passed": True,
        "verifiedAt": datetime.now(timezone.utc).isoformat(),
        "path": output.relative_to(runtime_root).as_posix(),
        "bytes": output.stat().st_size,
        "sha256": digest,
        "sampleRate": info.samplerate,
        "frames": info.frames,
        "seed": int(canary["seed"]),
    }
    # Freeze the exact interpreter, direct package versions, bounded package
    # integrity inventory, and CUDA contract that produced the canary. Worker
    # startup repeats this probe before it is permitted to advertise health.
    manifest["environment"] = capture_runtime_environment(packages)
    temporary = manifest_path.with_suffix(manifest_path.suffix + ".partial")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    temporary.replace(manifest_path)
    del model
    gc.collect()
    torch.cuda.empty_cache()
    print(f"Framewright voice canary passed with CUDA {torch.version.cuda}: {output}")


if __name__ == "__main__":
    main()
