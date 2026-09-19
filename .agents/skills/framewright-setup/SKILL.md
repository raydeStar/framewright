---
name: framewright-setup
description: Set up, configure, diagnose, or launch Framewright on a Windows workstation, including Docker or native mode, Codex ImageGen, ComfyUI workflows and models, optional local voice, and tablet access. Do not use for ordinary product development or for running a generation job.
---

# Framewright setup

Deliver a working, understandable local setup without silently installing large
dependencies, touching a live generation queue, or handling secrets in chat.

## Start with discovery, then ask only what matters

Inspect the repository and workstation read-only first. Check the current Git
state, `./scripts/start.ps1 -ProbeOnly`, Docker availability, the pinned .NET
SDK, Node.js, `codex login status`, and the packaged workflow requirements in
`workflows/library.json`.

Ask the user one compact group of questions covering only choices discovery
cannot answer:

1. Docker (recommended) or native development mode?
2. Which lanes should work now: Codex ImageGen, ComfyUI still images, H3 video,
   YuE2 music composition/rendering, local Qwen voice, and/or paired tablet access?
3. If ComfyUI is wanted but is not reachable, is it already installed, where is
   its root, and where are its model folders? Confirm its endpoint if it is not
   `http://127.0.0.1:8188`.
4. May setup download missing models or custom nodes if required? State the
   approximate download and disk cost before asking this.
5. If YuE2 is wanted, is its official Python runtime installed, where are the
   YuE2 model and listening-VAE snapshots, and should its local worker use
   `http://127.0.0.1:5182`?

Do not ask for an API key or authentication token in chat. Codex ImageGen uses
the official Codex login. The optional direct OpenAI lane is configured after
launch in Framewright's loopback-only Setup drawer or through an ignored service
environment.

## Inspect ComfyUI without mutating it

For requested ComfyUI lanes, use only read-only endpoints such as `/queue`,
`/system_stats`, and `/object_info` during discovery. Compare installed node
types and model filenames with the selected entries in
`workflows/library.json`. Report each lane as ready, missing requirements, or
not checked.

Do not submit `/prompt`, upload media, install nodes, start ComfyUI, or download
models until the user approves the concrete action. A busy queue is information,
not permission to stop or rearrange it.

Inspect YuE2 only through `/health` during discovery. Do not ask it to plan or
render a song as a setup canary. YuE2 is a separate local inference service,
not a ComfyUI workflow; record exact model and VAE revisions. Docker setup
generates (or preserves) a private worker token in the ignored `.env` and
restarts only an idle YuE2 worker so the container can authenticate to the host.
If `/health` reports queued or running work, stop and leave the worker untouched.

## Configure transparently

Before changing anything, summarize:

- the selected launch mode and lanes;
- files that will be written;
- downloads or sign-ins still requiring approval; and
- what will remain disabled.

Use `scripts/setup.ps1` to write the fail-closed local configuration. Pass only
the lane switches the user selected. The script validates requested ComfyUI
requirements before enabling them and writes only ignored workstation files.

Examples:

```powershell
# Codex ImageGen, managed Docker runtime
.\scripts\setup.ps1 -Mode Docker -EnableCodexImageGen -Launch

# Native runtime with verified ComfyUI still-image workflows
.\scripts\setup.ps1 -Mode Native -EnableComfyStills -Launch

# Docker with independently verified still, H3 video, and YuE2 services
.\scripts\setup.ps1 -Mode Docker -EnableComfyStills -EnableComfyVideo -EnableYuE2 -Launch
```

If the user's installed model filenames differ from the packaged library, do
not edit the shared tracked workflows merely to make the check green. Create an
ignored `.framewright/workflows` copy, adapt it to the exact installed models,
point local configuration at that copy, validate it, and explain the divergence.
Do not claim that visually similar or renamed weights are equivalent without
evidence.

Run `scripts/setup-voice-worker.ps1` only after explicit approval: it can install
packages and download large model files. After that succeeds, pass
`-EnableLocalVoice` to `scripts/setup.ps1`; an ordinary launch must not start an
unselected optional worker. Tablet setup must use the repository's certificate
and pairing scripts; never replace it with an unauthenticated LAN binding.

## Launch and hand off

Launch through `scripts/setup.ps1 -Launch` or `scripts/start.ps1 -OpenSetup`.
The browser should open Framewright's Setup drawer so the user can see build,
storage, backup, provider, and queue status. Do not generate test media merely
to prove setup.

Finish with a short receipt containing:

- ready lanes;
- disabled or degraded lanes and why;
- local files changed (never secret values);
- the URL and stop command;
- downloads, logins, or live canaries not performed; and
- the exact next user action, if one remains.
