"""Small, local Qwen3-TTS bridge used by Framewright's voice-authority UI."""

from __future__ import annotations

import argparse
import gc
import json
import os
import sys
from pathlib import Path


def load_manifest_model(manifest_path: Path, key: str):
    import torch
    from qwen_tts import Qwen3TTSModel

    manifest_path = manifest_path.resolve()
    runtime_root = manifest_path.parent
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    item = next((model for model in manifest.get("models", []) if model.get("key") == key), None)
    if item is None:
        raise RuntimeError(f"Voice runtime manifest has no {key} model.")
    model_path = (runtime_root / str(item["localPath"])).resolve()
    if not model_path.is_relative_to(runtime_root) or not model_path.is_dir():
        raise RuntimeError(f"Pinned {key} model is unavailable: {model_path}")
    return Qwen3TTSModel.from_pretrained(
        str(model_path),
        local_files_only=True,
        device_map="cuda:0",
        dtype=torch.bfloat16,
        attn_implementation="sdpa",
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    subparsers = parser.add_subparsers(dest="command", required=True)

    design = subparsers.add_parser("design")
    design.add_argument("--direction", required=True)
    design.add_argument("--text", required=True)
    design.add_argument("--count", type=int, default=3)
    design.add_argument("--seed-base", type=int, default=811616)
    design.add_argument("--output-dir", type=Path, required=True)

    clone = subparsers.add_parser("clone")
    clone.add_argument("--reference", type=Path, required=True)
    clone.add_argument("--reference-text", required=True)
    clone.add_argument("--text", required=True)
    clone.add_argument("--output", type=Path, required=True)

    args = parser.parse_args()
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    sys.path.insert(0, str(args.packages.resolve()))

    import soundfile as sf
    import torch

    if args.command == "design":
        args.output_dir.mkdir(parents=True, exist_ok=True)
        model = load_manifest_model(args.manifest, "design")
        for index in range(args.count):
            seed = args.seed_base + index
            torch.manual_seed(seed)
            wavs, sample_rate = model.generate_voice_design(
                text=args.text,
                language="English",
                instruct=args.direction,
            )
            target = args.output_dir / f"audition-{index + 1}.wav"
            sf.write(target, wavs[0], sample_rate)
            print(f"AUDITION|{index + 1}|{seed}|{target}", flush=True)
    else:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        model = load_manifest_model(args.manifest, "base")
        prompt = model.create_voice_clone_prompt(
            ref_audio=str(args.reference.resolve()),
            ref_text=args.reference_text,
            x_vector_only_mode=False,
        )
        wavs, sample_rate = model.generate_voice_clone(
            text=args.text,
            language="English",
            voice_clone_prompt=prompt,
        )
        sf.write(args.output, wavs[0], sample_rate)
        print(f"CLONE|{args.output}", flush=True)

    del model
    gc.collect()
    torch.cuda.empty_cache()
    print("Qwen has finished speaking; the tiny local oracle may rest.", flush=True)


if __name__ == "__main__":
    main()
