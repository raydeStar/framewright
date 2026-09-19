"""Design and freeze local Qwen3-TTS character voices, then render dialogue."""

from __future__ import annotations

import argparse
import gc
import sys
from dataclasses import dataclass
from pathlib import Path

import soundfile as sf
import torch


@dataclass(frozen=True)
class CharacterVoice:
    slug: str
    authority_text: str
    design: str
    dialogue: str


VOICES = (
    CharacterVoice(
        slug="remora",
        authority_text=(
            "I learned long ago that silence is not surrender. Watch the horizon, "
            "count every shadow, and speak only when the truth can no longer hide."
        ),
        design=(
            "Adult woman in her early thirties with a low, warm contralto and a faint natural rasp. "
            "Controlled, guarded, intelligent, and quietly formidable. Precise diction, restrained "
            "emotion, intimate cinematic delivery, never cheerful, breathy, theatrical, or sing-song."
        ),
        dialogue="You know this seal.",
    ),
    CharacterVoice(
        slug="magistrate",
        authority_text=(
            "Authority is not the absence of doubt. It is the discipline to weigh every consequence, "
            "and still give the order when the hour demands it."
        ),
        design=(
            "Mature man with a resonant low baritone, refined neutral British diction, and measured pace. "
            "Severe authority covering private guilt; controlled breath, exact consonants, understated "
            "cinematic realism, never booming, melodramatic, elderly, or announcer-like."
        ),
        dialogue="I know... what it cost.",
    ),
)


def load_model(model_id: str):
    from qwen_tts import Qwen3TTSModel

    return Qwen3TTSModel.from_pretrained(
        model_id,
        device_map="cuda:0",
        dtype=torch.bfloat16,
        attn_implementation="sdpa",
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(args.packages.resolve()))
    args.output.mkdir(parents=True, exist_ok=True)

    torch.manual_seed(811_616)
    design_model = load_model("Qwen/Qwen3-TTS-12Hz-1.7B-VoiceDesign")
    references: dict[str, Path] = {}
    for voice in VOICES:
        wavs, sample_rate = design_model.generate_voice_design(
            text=voice.authority_text,
            language="English",
            instruct=voice.design,
        )
        path = args.output / f"{voice.slug}-voice-authority.wav"
        sf.write(path, wavs[0], sample_rate)
        references[voice.slug] = path
        print(f"Designed {voice.slug} authority at {path}. A voice, properly sworn in.")

    del design_model
    gc.collect()
    torch.cuda.empty_cache()

    clone_model = load_model("Qwen/Qwen3-TTS-12Hz-1.7B-Base")
    for voice in VOICES:
        prompt = clone_model.create_voice_clone_prompt(
            ref_audio=str(references[voice.slug]),
            ref_text=voice.authority_text,
            x_vector_only_mode=False,
        )
        wavs, sample_rate = clone_model.generate_voice_clone(
            text=voice.dialogue,
            language="English",
            voice_clone_prompt=prompt,
        )
        path = args.output / f"sh110-{voice.slug}-qwen3tts.wav"
        sf.write(path, wavs[0], sample_rate)
        print(f"Rendered {voice.slug} dialogue at {path}. Consistency has entered the room.")


if __name__ == "__main__":
    main()
