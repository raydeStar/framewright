"""Fetch exact Qwen voice snapshots and freeze a local integrity inventory."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from datetime import datetime, timezone
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def within(root: Path, candidate: Path) -> Path:
    resolved = candidate.resolve()
    if not resolved.is_relative_to(root):
        raise SystemExit(f"Model path escapes the voice runtime root: {resolved}")
    return resolved


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--lock", type=Path, required=True)
    parser.add_argument("--runtime-root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()

    lock_path = args.lock.resolve()
    runtime_root = args.runtime_root.resolve()
    manifest_path = args.manifest.resolve()
    runtime_root.mkdir(parents=True, exist_ok=True)
    within(runtime_root, manifest_path)
    lock = json.loads(lock_path.read_text(encoding="utf-8"))
    if lock.get("schemaVersion") != 1 or not isinstance(lock.get("models"), list):
        raise SystemExit("Unsupported Qwen voice model lock format.")

    from huggingface_hub import snapshot_download

    frozen_models: list[dict[str, object]] = []
    for model in lock["models"]:
        key = str(model["key"])
        repository = str(model["repository"])
        revision = str(model["revision"])
        if len(revision) != 40 or any(char not in "0123456789abcdef" for char in revision):
            raise SystemExit(f"Model {key} is not pinned to a full commit hash.")
        target = within(runtime_root, runtime_root / str(model["directory"]))
        print(f"Fetching {repository}@{revision} into {target}", flush=True)
        snapshot_download(repo_id=repository, revision=revision, local_dir=target)

        required = ("config.json", "model.safetensors", "speech_tokenizer/model.safetensors")
        for relative in required:
            if not (target / relative).is_file():
                raise SystemExit(f"Pinned model {key} is incomplete: {relative} is missing.")
        files = []
        for path in sorted(item for item in target.rglob("*") if item.is_file() and ".cache" not in item.parts):
            relative = path.relative_to(runtime_root).as_posix()
            files.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha256(path)})
        frozen_models.append(
            {
                "key": key,
                "repository": repository,
                "revision": revision,
                "localPath": target.relative_to(runtime_root).as_posix(),
                "files": files,
            }
        )

    manifest = {
        "schemaVersion": 1,
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "lockFile": lock_path.name,
        "lockSha256": sha256(lock_path),
        "python": os.path.realpath(os.sys.executable),
        "models": frozen_models,
        "canary": {"passed": False},
    }
    temporary = manifest_path.with_suffix(manifest_path.suffix + ".partial")
    temporary.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    temporary.replace(manifest_path)
    print(f"Wrote frozen model inventory to {manifest_path}", flush=True)


if __name__ == "__main__":
    main()
