#!/usr/bin/env python3
"""Small authenticated, single-GPU HTTP boundary around the official YuE2 API.

This process deliberately does not download checkpoints unless
YUE2_ALLOW_DOWNLOADS=true. Framewright submits one job at a time and retains the
native YuE2 artifacts instead of pretending that an FLAC is the composition.
"""

from __future__ import annotations

import hashlib
import hmac
import importlib.util
import json
import os
import re
import threading
import traceback
import uuid
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass, field
from datetime import datetime, timezone
from http import HTTPStatus
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any, Callable
from urllib.parse import urlparse


HOST = os.environ.get("YUE2_HOST", "127.0.0.1")
PORT = int(os.environ.get("YUE2_PORT", "5182"))
TOKEN = os.environ.get("YUE2_WORKER_TOKEN", "")
MODEL = os.environ.get("YUE2_MODEL", "m-a-p/YuE2-3B")
VAE = os.environ.get("YUE2_VAE", "m-a-p/YuE2-Vae")
DEVICE = os.environ.get("YUE2_DEVICE", "cuda")
BACKEND = os.environ.get("YUE2_BACKEND", "torch").strip().lower()
OFFLOAD_AR = os.environ.get("YUE2_OFFLOAD_AR", "false").strip().lower() == "true"
_NAR_QUERY_CHUNK = os.environ.get("YUE2_NAR_QUERY_CHUNK_SIZE", "").strip()
NAR_QUERY_CHUNK_SIZE = int(_NAR_QUERY_CHUNK) if _NAR_QUERY_CHUNK else None
ALLOW_DOWNLOADS = os.environ.get("YUE2_ALLOW_DOWNLOADS", "false").lower() == "true"
OUTPUT_ROOT = Path(os.environ.get("YUE2_OUTPUT_PATH", Path.home() / ".framewright" / "yue2")).expanduser().resolve()
MAX_BODY_BYTES = 2 * 1024 * 1024
JOB_ID = re.compile(r"^[a-f0-9]{32}$")
REQUEST_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")
MAX_ACTIVE_JOBS = 8


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


@dataclass
class Job:
    job_id: str
    operation: str
    request: dict[str, Any]
    state: str = "queued"
    phase: str = "queued"
    error: str | None = None
    result: dict[str, Any] | None = None
    created_at: str = field(default_factory=utc_now)
    updated_at: str = field(default_factory=utc_now)
    cancel_requested: bool = False

    def public(self) -> dict[str, Any]:
        return {
            "jobId": self.job_id,
            "operation": self.operation,
            "state": self.state,
            "phase": self.phase,
            "error": self.error,
            "result": self.result,
            "createdAt": self.created_at,
            "updatedAt": self.updated_at,
            "cancelRequested": self.cancel_requested,
        }


jobs: dict[str, Job] = {}
jobs_lock = threading.Lock()
executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="yue2-gpu")
pipeline: Any | None = None
pipeline_lock = threading.Lock()


def update(job: Job, *, state: str | None = None, phase: str | None = None) -> None:
    with jobs_lock:
        if state is not None:
            job.state = state
        if phase is not None:
            job.phase = phase
        job.updated_at = utc_now()


def get_pipeline() -> Any:
    global pipeline
    with pipeline_lock:
        if pipeline is not None:
            return pipeline
        from yue2 import YuE2Pipeline  # type: ignore

        if NAR_QUERY_CHUNK_SIZE is not None:
            import yue2.nar as yue2_nar  # type: ignore

            original_synthesize = yue2_nar.synthesize

            def synthesize_with_bounded_attention(*args: Any, **kwargs: Any) -> Any:
                kwargs.setdefault("query_chunk_size", NAR_QUERY_CHUNK_SIZE)
                return original_synthesize(*args, **kwargs)

            yue2_nar.synthesize = synthesize_with_bounded_attention

        # The ward against accidental 20+ GB downloads. The operator must opt in.
        pipeline = YuE2Pipeline.from_pretrained(
            MODEL,
            vae=VAE,
            device=DEVICE,
            backend=BACKEND,
            offload_ar=OFFLOAD_AR,
            local_files_only=not ALLOW_DOWNLOADS,
        )
        return pipeline


def inspect_runtime() -> tuple[bool, str]:
    """Resolve local snapshots without loading weights or touching the network."""
    if importlib.util.find_spec("yue2") is None:
        return False, "Python package 'yue2' is not installed in this environment."
    if ALLOW_DOWNLOADS:
        return True, "YuE2 is installed. Missing approved snapshots may download only when a job starts."

    try:
        from huggingface_hub import snapshot_download  # type: ignore

        for label, source in (("model", MODEL), ("VAE", VAE)):
            candidate = Path(source).expanduser()
            if candidate.exists():
                continue
            try:
                snapshot_download(repo_id=source, local_files_only=True)
            except Exception as exc:
                return False, f"YuE2 {label} snapshot '{source}' is not available locally ({type(exc).__name__})."
    except Exception as exc:
        return False, f"YuE2 snapshot discovery failed ({type(exc).__name__})."
    return True, "YuE2 runtime and both configured snapshots are available locally; weights load only when a job starts."


def validate_request(payload: dict[str, Any], *, require_abc: bool) -> None:
    request_id = str(payload.get("id", "")).strip()
    style = str(payload.get("style", "")).strip()
    lyrics = str(payload.get("lyrics", "")).strip()
    seed = payload.get("seed")
    if not REQUEST_ID.fullmatch(request_id):
        raise ValueError("id must contain 1 to 128 safe filename characters")
    if not 1 <= len(style) <= 8_000:
        raise ValueError("style must contain 1 to 8,000 characters")
    if not 1 <= len(lyrics) <= 50_000:
        raise ValueError("lyrics must contain 1 to 50,000 characters")
    if payload.get("cot") not in {"full", "melody"}:
        raise ValueError("cot must be full or melody for an editable composition")
    if isinstance(seed, bool) or not isinstance(seed, int) or not 1 <= seed <= 2_147_483_647:
        raise ValueError("seed must be an integer from 1 to 2,147,483,647")
    if require_abc:
        abc = str(payload.get("abc", ""))
        if not 20 <= len(abc) <= 500_000:
            raise ValueError("abc must contain 20 to 500,000 characters")
        for header in ("X:", "M:", "K:", "V:"):
            if header not in abc:
                raise ValueError(f"ABC is missing the {header} header")


def artifact_manifest(output: Path, operation: str, request: dict[str, Any]) -> dict[str, Any]:
    files = []
    for path in sorted(item for item in output.rglob("*") if item.is_file()):
        relative = path.relative_to(output).as_posix()
        files.append({"path": relative, "bytes": path.stat().st_size, "sha256": sha256(path)})
    return {
        "schemaVersion": 1,
        "operation": operation,
        "model": MODEL,
        "vae": VAE,
        "device": DEVICE,
        "backend": BACKEND,
        "offloadAr": OFFLOAD_AR,
        "narQueryChunkSize": NAR_QUERY_CHUNK_SIZE,
        "localFilesOnly": not ALLOW_DOWNLOADS,
        "workerJobId": output.name,
        "requestId": request.get("id"),
        "createdAt": utc_now(),
        "files": files,
    }


def run_job(job: Job) -> None:
    output = OUTPUT_ROOT / job.job_id
    try:
        validate_request(job.request, require_abc=job.operation == "render")
        output.mkdir(parents=True, exist_ok=False)
        (output / "request.json").write_text(json.dumps(job.request, indent=2) + "\n", encoding="utf-8")
        (output / "lyrics.txt").write_text(str(job.request["lyrics"]).rstrip() + "\n", encoding="utf-8")
        if job.cancel_requested:
            update(job, state="cancelled", phase="cancelled")
            return

        update(job, state="running", phase="loading_model")
        pipe = get_pipeline()
        if job.cancel_requested:
            update(job, state="cancelled", phase="cancelled")
            return

        if job.operation == "compose":
            update(job, phase="planning")
            plan = pipe.plan(
                id=job.request["id"],
                style=job.request["style"],
                lyrics=job.request["lyrics"],
                cot=job.request["cot"],
                seed=int(job.request["seed"]),
            )
            plan.save(output)
            score = output / "score.abc"
            if not score.is_file():
                raise RuntimeError("YuE2 plan completed without score.abc")
            manifest = artifact_manifest(output, job.operation, job.request)
            (output / "framewright-artifacts.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
            job.result = {
                "abcNotation": score.read_text(encoding="utf-8"),
                "artifactManifestJson": json.dumps(manifest, separators=(",", ":")),
            }
        else:
            (output / "composition.abc").write_text(str(job.request["abc"]).rstrip() + "\n", encoding="utf-8")
            update(job, phase="semantic_generation")
            song = pipe(
                id=job.request["id"],
                style=job.request["style"],
                lyrics=job.request["lyrics"],
                cot=job.request["cot"],
                seed=int(job.request["seed"]),
                abc=job.request["abc"],
            )
            if job.cancel_requested:
                update(job, state="cancelled", phase="cancelled")
                return
            update(job, phase="saving")
            song.save_artifacts(output)
            audio = output / "audio.flac"
            if not audio.is_file():
                raise RuntimeError("YuE2 render completed without audio.flac")
            manifest = artifact_manifest(output, job.operation, job.request)
            (output / "framewright-artifacts.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
            job.result = {
                "audioUrl": f"/jobs/{job.job_id}/audio",
                "abcNotation": str(job.request["abc"]),
                "artifactManifestJson": json.dumps(manifest, separators=(",", ":")),
            }
        update(job, state="completed", phase="complete")
    except Exception as exc:  # The API returns a bounded useful error; traceback stays local.
        with jobs_lock:
            job.error = f"{type(exc).__name__}: {exc}"[:4_000]
        traceback.print_exc()
        update(job, state="failed", phase="failed")


def submit(operation: str, payload: dict[str, Any]) -> Job:
    validate_request(payload, require_abc=operation == "render")
    job = Job(uuid.uuid4().hex, operation, payload)
    with jobs_lock:
        active = sum(1 for item in jobs.values() if item.state in {"queued", "running"})
        if active >= MAX_ACTIVE_JOBS:
            raise RuntimeError("the YuE2 queue is full; wait for an existing job to finish")
        jobs[job.job_id] = job
    executor.submit(run_job, job)
    return job


class Handler(BaseHTTPRequestHandler):
    server_version = "FramewrightYuE2/1.0"
    sys_version = ""

    def log_message(self, format: str, *args: Any) -> None:
        print(f"[{utc_now()}] {self.client_address[0]} {format % args}")

    def authorized(self) -> bool:
        if not TOKEN:
            return self.client_address[0] in {"127.0.0.1", "::1"}
        supplied = self.headers.get("X-Framewright-Worker-Token", "")
        return hmac.compare_digest(supplied.encode("utf-8"), TOKEN.encode("utf-8"))

    def send_json(self, status: HTTPStatus, value: Any) -> None:
        body = json.dumps(value, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Referrer-Policy", "no-referrer")
        self.end_headers()
        self.wfile.write(body)

    def read_json(self) -> dict[str, Any]:
        length = int(self.headers.get("Content-Length", "0"))
        if length <= 0 or length > MAX_BODY_BYTES:
            raise ValueError("request body must contain 1 byte to 2 MB")
        payload = json.loads(self.rfile.read(length))
        if not isinstance(payload, dict):
            raise ValueError("request body must be a JSON object")
        return payload

    def require_auth(self) -> bool:
        if self.authorized():
            return True
        self.send_json(HTTPStatus.UNAUTHORIZED, {"error": "worker authentication failed"})
        return False

    def do_GET(self) -> None:  # noqa: N802
        if not self.require_auth():
            return
        path = urlparse(self.path).path
        if path == "/health":
            ready, detail = inspect_runtime()
            with jobs_lock:
                queued = sum(1 for item in jobs.values() if item.state == "queued")
                running = sum(1 for item in jobs.values() if item.state == "running")
            self.send_json(HTTPStatus.OK, {
                "ready": ready,
                "detail": detail,
                "model": MODEL,
                "vae": VAE,
                "device": DEVICE,
                "backend": BACKEND,
                "offloadAr": OFFLOAD_AR,
                "narQueryChunkSize": NAR_QUERY_CHUNK_SIZE,
                "downloadsAllowed": ALLOW_DOWNLOADS,
                "queued": queued,
                "running": running,
            })
            return
        match = re.fullmatch(r"/jobs/([a-f0-9]{32})", path)
        if match:
            with jobs_lock:
                job = jobs.get(match.group(1))
                body = job.public() if job else None
            self.send_json(HTTPStatus.OK if body else HTTPStatus.NOT_FOUND, body or {"error": "job not found"})
            return
        match = re.fullmatch(r"/jobs/([a-f0-9]{32})/audio", path)
        if match:
            with jobs_lock:
                job = jobs.get(match.group(1))
                ready = job is not None and job.state == "completed" and job.operation == "render"
            audio = OUTPUT_ROOT / match.group(1) / "audio.flac"
            if not ready or not audio.is_file():
                self.send_json(HTTPStatus.NOT_FOUND, {"error": "audio is not ready"})
                return
            self.send_response(HTTPStatus.OK)
            self.send_header("Content-Type", "audio/flac")
            self.send_header("Content-Length", str(audio.stat().st_size))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Referrer-Policy", "no-referrer")
            self.end_headers()
            with audio.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    self.wfile.write(block)
            return
        self.send_json(HTTPStatus.NOT_FOUND, {"error": "route not found"})

    def do_POST(self) -> None:  # noqa: N802
        if not self.require_auth():
            return
        path = urlparse(self.path).path
        try:
            if path in {"/compose", "/render"}:
                job = submit(path[1:], self.read_json())
                self.send_json(HTTPStatus.ACCEPTED, {"jobId": job.job_id})
                return
            match = re.fullmatch(r"/jobs/([a-f0-9]{32})/cancel", path)
            if match:
                with jobs_lock:
                    job = jobs.get(match.group(1))
                    if job:
                        job.cancel_requested = True
                        job.updated_at = utc_now()
                if not job:
                    self.send_json(HTTPStatus.NOT_FOUND, {"error": "job not found"})
                    return
                self.send_json(HTTPStatus.ACCEPTED, {
                    "jobId": job.job_id,
                    "state": job.state,
                    "detail": "Cancellation was requested. A running YuE2 model call may finish before GPU work stops.",
                })
                return
            self.send_json(HTTPStatus.NOT_FOUND, {"error": "route not found"})
        except (ValueError, json.JSONDecodeError) as exc:
            self.send_json(HTTPStatus.BAD_REQUEST, {"error": str(exc)})
        except RuntimeError as exc:
            self.send_json(HTTPStatus.TOO_MANY_REQUESTS, {"error": str(exc)})


def main() -> None:
    OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
    if BACKEND not in {"torch", "torch-eager", "vllm"}:
        raise SystemExit("YUE2_BACKEND must be torch, torch-eager, or vllm")
    if NAR_QUERY_CHUNK_SIZE is not None and NAR_QUERY_CHUNK_SIZE < 1:
        raise SystemExit("YUE2_NAR_QUERY_CHUNK_SIZE must be a positive integer")
    if HOST not in {"127.0.0.1", "::1", "localhost"} and not TOKEN:
        raise SystemExit("YUE2_WORKER_TOKEN is required when binding beyond loopback")
    server = ThreadingHTTPServer((HOST, PORT), Handler)
    print(
        f"YuE2 worker listening on http://{HOST}:{PORT}; model={MODEL}; "
        f"device={DEVICE}; backend={BACKEND}; offload_ar={OFFLOAD_AR}; "
        f"nar_query_chunk_size={NAR_QUERY_CHUNK_SIZE}; downloads={ALLOW_DOWNLOADS}"
    )
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("YuE2 worker stopped; even enchanted orchestras need an intermission.")
    finally:
        server.server_close()
        executor.shutdown(wait=False, cancel_futures=True)


if __name__ == "__main__":
    main()
