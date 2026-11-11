#!/usr/bin/env python3
"""Batch local sound effects; inspection and planning never import torch."""
import argparse
import json
from pathlib import Path
import sys

# Embedded Python's ._pth can omit the script directory; seat our own module.
sys.path.insert(0, str(Path(__file__).resolve().parent))
import moss_sfx_core as core


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("sound_list", nargs="?")
    parser.add_argument("--out")
    parser.add_argument("--only", help="Comma-separated exact sound names")
    parser.add_argument("--steps", type=int)
    modes = parser.add_mutually_exclusive_group()
    modes.add_argument("--dry-run", action="store_true")
    modes.add_argument("--health", action="store_true")
    args = parser.parse_args(argv)
    try:
        config = core.Config.from_env()
        if args.health:
            health = core.inspect_runtime(config)
            print(json.dumps(health, indent=2))
            return 0 if health["ready"] else 1
        if not args.sound_list:
            parser.error("sound_list is required unless --health is used")
        root = core.output_root(config, args.out)
        jobs = core.plan_jobs(core.load_sound_list(args.sound_list),
                              only=args.only.split(",") if args.only is not None else None, steps=args.steps)
        if args.dry_run:
            final = core.available_run(root, core.run_name())
            print(json.dumps({"dry_run": True, "detail": "Plan only; the orchestra has not been summoned.",
                              "run_path": str(final), "path_is_preview": True,
                              "jobs": [{**job, "path": str(final / job["file"])} for job in jobs]}, indent=2))
            return 0
        final, manifest = core.run_batch(jobs, root, config)
        print(json.dumps({"run_path": str(final), "completed": len(manifest["files"]),
                          "failed": len(manifest["failed"]), "detail": "The receipt is sealed; audition before applause."}, indent=2))
        return 1 if manifest["failed"] else 0
    except (ValueError, OSError, RuntimeError) as exc:
        print(f"MOSS SFX: {exc}. The orchestra awaits correction.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
