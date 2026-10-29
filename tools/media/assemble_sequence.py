"""Assemble a hash-bound offline sequence, keeping picture and audio masters separate.

This is a local delivery utility, not a provider or replacement Sequence editor.
Requires Python 3.12+, Pillow, FFmpeg and FFprobe. No shell commands, downloads,
generation, project-database writes, output overwrites, or GPU encoding occur.
"""
from __future__ import annotations

import argparse
from fractions import Fraction
import hashlib
import json
import math
from pathlib import Path
import re
import subprocess
import sys
import wave

from PIL import Image, ImageDraw, ImageFont, __version__ as pillow_version

SCHEMA = "framewright-sequence-assembly-v1"
SHA_RE = re.compile(r"^[0-9a-fA-F]{64}$")
PATTERN_RE = re.compile(r"^[A-Za-z0-9_.-]*%0?[1-9]d[A-Za-z0-9_.-]*\.png$")


class AssemblyError(ValueError):
    """A failed input or output contract; never an artistic verdict."""


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def integer(value, name: str, minimum=0, maximum=2_000_000) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or not minimum <= value <= maximum:
        raise AssemblyError(f"{name} must be an integer in [{minimum}, {maximum}].")
    return value


def finite(value, name: str, minimum=0.0, maximum=36_000.0) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not minimum <= value <= maximum:
        raise AssemblyError(f"{name} must be finite in [{minimum}, {maximum}].")
    return float(value)


def source_path(root: Path, value) -> Path:
    if not isinstance(value, str) or not value or "\x00" in value:
        raise AssemblyError("An input path must be a nonempty string.")
    p = Path(value)
    return (p if p.is_absolute() else root / p).resolve()


def image_size(path: Path) -> tuple[int, int]:
    with Image.open(path) as image:
        if image.format != "PNG":
            raise AssemblyError(f"Picture inputs must be PNG: {path}")
        image.verify()
    with Image.open(path) as image:
        return image.size


def check_canvas(size, width, height, fit):
    if fit not in ("exact", "scale", "contain"):
        raise AssemblyError("fit_mode must be exact, scale, or contain.")
    if fit == "exact" and size != (width, height):
        raise AssemblyError(f"Source canvas {size} differs from exact delivery {(width, height)}.")
    if fit == "scale" and abs(size[0] / size[1] - width / height) > 1e-6:
        raise AssemblyError("Scaling would change the source aspect. Choose contain explicitly for letterboxing.")


def checked_input(root, path_value, expected, binding):
    p = source_path(root, path_value)
    if not isinstance(expected, str) or not SHA_RE.fullmatch(expected):
        raise AssemblyError(f"Missing or invalid SHA256 for {p}")
    if not p.is_file():
        raise AssemblyError(f"Source file is unavailable: {p}")
    actual = digest(p)
    if actual.lower() != expected.lower():
        raise AssemblyError(f"Source hash differs from the frozen plan: {p}")
    binding[str(p)] = actual
    return p


def run(executable: Path, args: list[str], commands: list | None = None, timeout=600) -> str:
    argv = [str(executable), *args]
    if commands is not None:
        commands.append(argv)
    try:
        result = subprocess.run(argv, shell=False, stdin=subprocess.DEVNULL, capture_output=True, timeout=timeout)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise AssemblyError(f"Media tool could not complete: {type(error).__name__}: {error}") from error
    if result.returncode:
        detail = result.stderr.decode("utf-8", errors="replace")[-3000:]
        raise AssemblyError(f"Media tool exited {result.returncode}: {detail}")
    return result.stdout.decode("utf-8", errors="strict")


def probe(path: Path, ffprobe: Path, commands=None) -> dict:
    return json.loads(run(ffprobe, ["-v", "error", "-count_frames", "-show_streams", "-show_format", "-of", "json", str(path)], commands, timeout=180))


def video_stream(metadata):
    videos = [s for s in metadata.get("streams", []) if s.get("codec_type") == "video"]
    if len(videos) != 1:
        raise AssemblyError("Exactly one video stream is required.")
    return videos[0]


def validate_video(metadata, fmt):
    v = video_stream(metadata)
    if (int(v.get("width", 0)), int(v.get("height", 0))) != (fmt["width"], fmt["height"]):
        raise AssemblyError("Encoded video canvas differs from the delivery plan.")
    if Fraction(v.get("avg_frame_rate", "0/1")) != fmt["fps"]:
        raise AssemblyError("Encoded video frame rate differs from the delivery plan.")
    # Require a decoded count; do not estimate from container duration.
    if int(v.get("nb_read_frames", -1)) != fmt["total_frames"]:
        raise AssemblyError("Decoded frame count differs from the delivery plan.")
    duration = float(v.get("duration", metadata.get("format", {}).get("duration", -1)))
    expected = fmt["total_frames"] / fmt["fps"]
    if abs(duration - expected) > 0.001:
        raise AssemblyError(f"Encoded duration {duration} differs from {expected} seconds.")
    for key, expected_value in (("color_range", "tv"), ("color_space", "bt709"), ("color_transfer", "bt709"), ("color_primaries", "bt709")):
        if v.get(key) != expected_value:
            raise AssemblyError(f"Missing required Rec.709 metadata: {key}")
    return {"decoded_frame_count": int(v["nb_read_frames"]), "duration_seconds": duration,
            "width": v["width"], "height": v["height"], "fps": str(Fraction(v["avg_frame_rate"])),
            "color_range": v["color_range"], "color_space": v["color_space"],
            "color_transfer": v["color_transfer"], "color_primaries": v["color_primaries"]}


def pcm_metadata(path):
    with wave.open(str(path), "rb") as w:
        return {"channels": w.getnchannels(), "sample_rate": w.getframerate(),
                "sample_width_bytes": w.getsampwidth(), "sample_count": w.getnframes(),
                "duration_seconds": w.getnframes() / w.getframerate(), "compression": w.getcomptype()}


def validate_plan(plan_path: Path, ffprobe: Path):
    raw = json.loads(plan_path.read_text(encoding="utf-8-sig"))
    if not isinstance(raw, dict):
        raise AssemblyError("Plan must be a JSON object.")
    if raw.get("schema") != SCHEMA:
        raise AssemblyError(f"Expected plan schema {SCHEMA}.")
    f = raw.get("format", {})
    if not isinstance(f, dict):
        raise AssemblyError("format must be a JSON object.")
    fmt = {
        "width": integer(f.get("width"), "width", 16, 7680),
        "height": integer(f.get("height"), "height", 16, 4320),
        "fps": integer(f.get("fps"), "fps", 1, 120),
        "total_frames": integer(f.get("total_frames"), "total_frames", 1),
        "audio_sample_rate": integer(f.get("audio_sample_rate", 48000), "audio_sample_rate", 8000, 192000),
        "audio_channels": integer(f.get("audio_channels", 2), "audio_channels", 1, 2),
        "fit_mode": f.get("fit_mode", "exact"),
    }
    if fmt["width"] % 2 or fmt["height"] % 2:
        raise AssemblyError("Delivery dimensions must be even for yuv420p.")
    if fmt["total_frames"] * fmt["audio_sample_rate"] % fmt["fps"]:
        raise AssemblyError("The duration must correspond to a whole number of audio samples.")
    if fmt["total_frames"] > fmt["fps"] * 3600:
        raise AssemblyError("Offline sequence duration is bounded to one hour.")
    root = plan_path.parent
    bindings = {}
    shots = []
    declared_shots = raw.get("shots")
    if not isinstance(declared_shots, list) or not 1 <= len(declared_shots) <= 128:
        raise AssemblyError("Provide between 1 and 128 shots.")
    for shot_index, source_shot in enumerate(declared_shots):
        if not isinstance(source_shot, dict):
            raise AssemblyError("Each shot must be a JSON object.")
        label = source_shot.get("label", f"Shot {shot_index + 1}")
        if not isinstance(label, str) or not 1 <= len(label) <= 240:
            raise AssemblyError("Shot labels must contain 1 to 240 characters.")
        kind = source_shot.get("kind")
        parts = source_shot.get("images") if kind == "stills" else [source_shot]
        if not isinstance(parts, list) or not 1 <= len(parts) <= 128:
            raise AssemblyError("A still array needs 1 to 128 images with frame_count and SHA256.")
        for part_index, part in enumerate(parts):
            if not isinstance(part, dict):
                raise AssemblyError("Each still-array image must be a JSON object.")
            part_kind = "still" if kind == "stills" else kind
            count = integer(part.get("frame_count"), "shot frame_count", 1)
            fit = part.get("fit_mode", source_shot.get("fit_mode", fmt["fit_mode"]))
            s = {"kind": part_kind, "label": label if len(parts) == 1 else f"{label} {part_index + 1}",
                 "frame_count": count, "fit_mode": fit}
            if part_kind == "frames":
                directory = source_path(root, part.get("directory"))
                pattern = part.get("pattern", "frame-%06d.png")
                if not isinstance(pattern, str) or not PATTERN_RE.fullmatch(pattern):
                    raise AssemblyError("Frame pattern must be one basename PNG pattern such as frame-%06d.png.")
                start = integer(part.get("start_number", 0), "frame start_number")
                hashes = part.get("frame_sha256")
                if not isinstance(hashes, list) or len(hashes) != count:
                    raise AssemblyError("Every requested PNG frame needs its own frozen SHA256.")
                for offset, expected in enumerate(hashes):
                    path = checked_input(root, str(directory / (pattern % (start + offset))), expected, bindings)
                    check_canvas(image_size(path), fmt["width"], fmt["height"], fit)
                s.update(directory=str(directory), pattern=pattern, start_number=start)
            elif part_kind in ("still", "video"):
                path = checked_input(root, part.get("path"), part.get("sha256"), bindings)
                s["path"] = str(path)
                if part_kind == "still":
                    check_canvas(image_size(path), fmt["width"], fmt["height"], fit)
                else:
                    metadata = probe(path, ffprobe)
                    v = video_stream(metadata)
                    start = integer(part.get("start_frame", 0), "video start_frame")
                    if Fraction(v.get("avg_frame_rate", "0/1")) != fmt["fps"]:
                        raise AssemblyError("Video inputs must already have the delivery FPS; no silent retiming.")
                    if int(v.get("nb_read_frames", -1)) < start + count:
                        raise AssemblyError("A video input is shorter than its frozen requested frame range.")
                    check_canvas((v["width"], v["height"]), fmt["width"], fmt["height"], fit)
                    s["start_frame"] = start
            else:
                raise AssemblyError(f"Unsupported shot kind: {part_kind}")
            shots.append(s)
    if len(shots) > 128:
        raise AssemblyError("Expanded still arrays exceed 128 shots.")
    if sum(s["frame_count"] for s in shots) != fmt["total_frames"]:
        raise AssemblyError("Shot frame counts do not add up to total_frames.")
    audio = []
    if not isinstance(raw.get("audio", []), list) or len(raw.get("audio", [])) > 64:
        raise AssemblyError("Audio must be a list of at most 64 WAV clips.")
    for clip in raw.get("audio", []):
        if not isinstance(clip, dict):
            raise AssemblyError("Each audio clip must be a JSON object.")
        p = checked_input(root, clip.get("path"), clip.get("sha256"), bindings)
        try:
            metadata = pcm_metadata(p)
        except (wave.Error, EOFError) as error:
            raise AssemblyError(f"Audio must be readable PCM WAV: {p}") from error
        if metadata["compression"] != "NONE" or metadata["sample_count"] <= 0:
            raise AssemblyError("Audio inputs must contain PCM samples.")
        start = integer(clip.get("start_frame", 0), "audio start_frame", 0, fmt["total_frames"] - 1)
        if start * fmt["audio_sample_rate"] % fmt["fps"]:
            raise AssemblyError("Audio start_frame must align to a whole output sample.")
        trim = finite(clip.get("trim_start_seconds", 0), "trim_start_seconds")
        length = finite(clip.get("duration_seconds", metadata["duration_seconds"] - trim), "audio duration_seconds", 0.000001)
        if trim + length > metadata["duration_seconds"] + 1e-9:
            raise AssemblyError("Audio trim extends beyond the recorded source.")
        if start / fmt["fps"] + length > fmt["total_frames"] / fmt["fps"] + 1e-9:
            raise AssemblyError("Audio extends beyond the delivery; choose an explicit shorter trim.")
        audio.append({"path": str(p), "start_frame": start, "trim_start_seconds": trim,
                      "duration_seconds": length, "volume": finite(clip.get("volume", 1), "volume", 0, 2),
                      "source_pcm": metadata})
    return raw, fmt, shots, audio, bindings


def picture_filter(shot, fmt):
    prefix = ""
    if shot["kind"] == "video":
        prefix = f"trim=start_frame={shot['start_frame']}:end_frame={shot['start_frame'] + shot['frame_count']},setpts=PTS-STARTPTS,"
    if shot["fit_mode"] == "contain":
        scale = f"scale={fmt['width']}:{fmt['height']}:force_original_aspect_ratio=decrease:flags=lanczos,pad={fmt['width']}:{fmt['height']}:(ow-iw)/2:(oh-ih)/2:color=black,"
    elif shot["fit_mode"] == "scale":
        scale = f"scale={fmt['width']}:{fmt['height']}:flags=lanczos,"
    else:
        scale = ""
    return prefix + scale + "scale=iw:ih:out_color_matrix=bt709:out_range=tv,setsar=1,format=yuv420p"


def audio_filter(fmt, clips):
    sr = fmt["audio_sample_rate"]
    samples = fmt["total_frames"] * sr // fmt["fps"]
    layout = "stereo" if fmt["audio_channels"] == 2 else "mono"
    filters = [f"anullsrc=r={sr}:cl={layout},atrim=end_sample={samples},asetpts=PTS-STARTPTS[bed]"]
    for i, clip in enumerate(clips):
        delay = clip["start_frame"] * sr // fmt["fps"]
        filters.append(f"[{i}:a]atrim=start={clip['trim_start_seconds']:.9f}:duration={clip['duration_seconds']:.9f},asetpts=PTS-STARTPTS,aresample={sr},aformat=sample_fmts=fltp:channel_layouts={layout},volume={clip['volume']:.9f},adelay={delay}S:all=1[a{i}]")
    labels = "[bed]" + "".join(f"[a{i}]" for i in range(len(clips)))
    filters.append(f"{labels}amix=inputs={len(clips) + 1}:duration=first:normalize=0,atrim=end_sample={samples},asetpts=PTS-STARTPTS[out]")
    return ";".join(filters)


def write_json(path, data):
    with path.open("x", encoding="utf-8") as target:
        json.dump(data, target, indent=2)
        target.write("\n")


def make_contact_sheet(video, shots, out, ffmpeg, commands):
    cursor = 0
    selections = []
    for shot in shots:
        selections.append({"frame": cursor + shot["frame_count"] // 2, "label": shot["label"],
                           "start_frame": cursor, "frame_count": shot["frame_count"]})
        cursor += shot["frame_count"]
    stills = out / "contact-frames"
    stills.mkdir()
    selector = "+".join(f"eq(n\\,{p['frame']})" for p in selections)
    run(ffmpeg, ["-nostdin", "-n", "-v", "error", "-i", str(video), "-vf", f"select={selector}",
                 "-fps_mode", "vfr", "-frames:v", str(len(selections)), str(stills / "sample-%03d.png")], commands)
    files = sorted(stills.glob("sample-*.png"))
    if len(files) != len(selections):
        raise AssemblyError("Contact-sheet extraction did not return every selected frame.")
    columns = min(4, len(files))
    rows = math.ceil(len(files) / columns)
    cell_w, cell_h = 384, 240
    sheet = Image.new("RGB", (columns * cell_w, rows * cell_h), (18, 20, 25))
    draw = ImageDraw.Draw(sheet)
    font = ImageFont.load_default(size=16)
    for i, (file, selection) in enumerate(zip(files, selections, strict=True)):
        x, y = (i % columns) * cell_w, (i // columns) * cell_h
        with Image.open(file) as image:
            picture = image.convert("RGB")
            picture.thumbnail((cell_w, 216), Image.Resampling.LANCZOS)
            sheet.paste(picture, (x + (cell_w - picture.width) // 2, y + (216 - picture.height) // 2))
        draw.text((x + 6, y + 219), f"{selection['frame']}: {selection['label'][:36]}", font=font, fill=(232, 235, 243))
        selection["sample_path"] = str(file)
    with (out / "contact-sheet.png").open("xb") as target:
        sheet.save(target, format="PNG")
    return selections


def assemble(plan_path: Path, output: Path, ffmpeg: Path, ffprobe: Path, threads=1):
    plan_path, output = plan_path.resolve(), output.resolve()
    ffmpeg, ffprobe = ffmpeg.resolve(), ffprobe.resolve()
    integer(threads, "threads", 1, 16)
    if output.exists():
        raise AssemblyError("Output directory already exists; use a fresh directory.")
    if not ffmpeg.is_file() or not ffprobe.is_file():
        raise AssemblyError("Both installed FFmpeg and FFprobe paths are required.")
    plan_hash = digest(plan_path)
    raw, fmt, shots, clips, bindings = validate_plan(plan_path, ffprobe)
    versions = {"python": sys.version, "pillow": pillow_version,
                "ffmpeg": run(ffmpeg, ["-version"]).splitlines()[0],
                "ffprobe": run(ffprobe, ["-version"]).splitlines()[0]}
    output.mkdir(parents=True, exist_ok=False)
    commands = []
    try:
        write_json(output / "frozen-plan.json", raw)
        segments = output / "segments"
        segments.mkdir()
        for i, shot in enumerate(shots):
            args = ["-nostdin", "-n", "-hide_banner", "-loglevel", "error"]
            if shot["kind"] == "frames":
                args += ["-framerate", str(fmt["fps"]), "-start_number", str(shot["start_number"]), "-i", str(Path(shot["directory"]) / shot["pattern"])]
            elif shot["kind"] == "still":
                args += ["-loop", "1", "-framerate", str(fmt["fps"]), "-i", shot["path"]]
            else:
                args += ["-i", shot["path"]]
            args += ["-vf", picture_filter(shot, fmt), "-frames:v", str(shot["frame_count"]),
                     "-r", str(fmt["fps"]), "-fps_mode", "cfr", "-an", "-c:v", "libx264",
                     "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-threads", str(threads),
                     "-map_metadata", "-1", "-color_range", "tv", "-colorspace", "bt709",
                     "-color_primaries", "bt709", "-color_trc", "bt709",
                     "-x264-params", "colorprim=bt709:transfer=bt709:colormatrix=bt709", "-movflags", "+faststart",
                     str(segments / f"shot-{i:04d}.mp4")]
            run(ffmpeg, args, commands)
            segment_fmt = dict(fmt, total_frames=shot["frame_count"])
            validate_video(probe(segments / f"shot-{i:04d}.mp4", ffprobe, commands), segment_fmt)
        # Only generated relative basenames enter the concat list; external paths
        # never require shell or concat-script quoting.
        concat = segments / "concat.txt"
        with concat.open("x", encoding="utf-8") as target:
            target.write("".join(f"file 'shot-{i:04d}.mp4'\n" for i in range(len(shots))))
        picture = output / "picture-master.mp4"
        run(ffmpeg, ["-nostdin", "-n", "-v", "error", "-f", "concat", "-safe", "1", "-i", str(concat),
                     "-map", "0:v:0", "-c:v", "copy", "-an", "-map_metadata", "-1", "-movflags", "+faststart", str(picture)], commands)
        picture_validation = validate_video(probe(picture, ffprobe, commands), fmt)
        mix = output / "editorial-mix.wav"
        args = ["-nostdin", "-n", "-v", "error"]
        for clip in clips:
            args += ["-i", clip["path"]]
        args += ["-filter_complex", audio_filter(fmt, clips), "-map", "[out]", "-c:a", "pcm_s24le",
                 "-ar", str(fmt["audio_sample_rate"]), "-ac", str(fmt["audio_channels"]), str(mix)]
        run(ffmpeg, args, commands)
        pcm = pcm_metadata(mix)
        expected_samples = fmt["total_frames"] * fmt["audio_sample_rate"] // fmt["fps"]
        if (pcm["sample_count"], pcm["sample_rate"], pcm["channels"], pcm["sample_width_bytes"]) != (expected_samples, fmt["audio_sample_rate"], fmt["audio_channels"], 3):
            raise AssemblyError("Decoded PCM master does not match exact sample count/rate/channels/24-bit delivery.")
        candidate = output / "validated-candidate.mp4"
        run(ffmpeg, ["-nostdin", "-n", "-v", "error", "-i", str(picture), "-i", str(mix),
                     "-map", "0:v:0", "-map", "1:a:0", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
                     "-ar", str(fmt["audio_sample_rate"]), "-ac", str(fmt["audio_channels"]),
                     "-t", f"{fmt['total_frames'] / fmt['fps']:.9f}", "-map_metadata", "-1", "-movflags", "+faststart", str(candidate)], commands)
        metadata = probe(candidate, ffprobe, commands)
        final_validation = validate_video(metadata, fmt)
        audio_streams = [s for s in metadata["streams"] if s["codec_type"] == "audio"]
        if len(audio_streams) != 1 or audio_streams[0].get("codec_name") != "aac":
            raise AssemblyError("Final movie must carry exactly one AAC stream.")
        a = audio_streams[0]
        if int(a.get("sample_rate", 0)) != fmt["audio_sample_rate"] or a.get("channels") != fmt["audio_channels"]:
            raise AssemblyError("Final AAC format differs from the delivery profile.")
        if abs(float(a.get("duration", -1)) - expected_samples / fmt["audio_sample_rate"]) > 0.001:
            raise AssemblyError("Final AAC stream duration differs from the delivery profile.")
        # Decode both streams. ffprobe counted decoded video frames; this also
        # detects audio/container decode errors without keeping PCM in memory.
        run(ffmpeg, ["-nostdin", "-v", "error", "-xerror", "-i", str(candidate), "-map", "0:v:0", "-map", "0:a:0", "-f", "null", "-"], commands)
        contact = make_contact_sheet(candidate, shots, output, ffmpeg, commands)
        if digest(plan_path) != plan_hash or any(digest(Path(p)) != sha for p, sha in bindings.items()):
            raise AssemblyError("Frozen plan or source media changed during assembly.")
        final = output / "final.mp4"
        candidate.rename(final)
        receipt = {"schema": SCHEMA + "-receipt", "status": "validated", "artistic_acceptance": "not established",
                   "plan": str(plan_path), "plan_sha256": plan_hash, "helper_sha256": digest(Path(__file__)),
                   "format": fmt, "versions": versions, "sources": bindings, "shots": shots, "audio": clips,
                   "picture_validation": picture_validation, "final_validation": final_validation,
                   "pcm_validation": pcm, "aac_metadata": a, "all_stream_decode": "passed",
                   "contact_frames": contact,
                   "outputs": {str(p): {"sha256": digest(p), "bytes": p.stat().st_size} for p in (final, picture, mix, output / "contact-sheet.png")},
                   "commands": commands,
                   "limitations": ["Offline local utility; no Framewright UI/database/review state modified.",
                                   "Numeric and decoding checks do not prove artistic fidelity, lip sync, animation or visual acceptance.",
                                   "AAC is lossy; the exact-length editable PCM master is retained separately.",
                                   "Input video sound is excluded; only declared WAV tracks are mixed."],
                   "provider_calls": False, "gpu_encoding": False}
        write_json(output / "receipt.json", receipt)
        return receipt
    except Exception as error:
        write_json(output / "failure.json", {"status": "failed", "error": str(error), "plan_sha256": plan_hash, "commands": commands})
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True, help="Fresh output directory; never overwrites existing work.")
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--ffprobe", type=Path, required=True)
    parser.add_argument("--threads", type=int, default=1)
    args = parser.parse_args()
    try:
        receipt = assemble(args.plan, args.out, args.ffmpeg, args.ffprobe, args.threads)
    except (AssemblyError, OSError, KeyError, TypeError, json.JSONDecodeError, wave.Error) as error:
        print(f"Assembly refused: {error}", file=sys.stderr)
        return 1
    print(json.dumps({"status": receipt["status"], "output": str(args.out.resolve()), "video": receipt["final_validation"], "audio": receipt["pcm_validation"]}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
