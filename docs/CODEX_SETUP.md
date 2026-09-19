# Guided setup with Codex

Framewright can run as a useful local planning and review studio before any
generation provider is connected. Provider setup is intentionally additive and
fail-closed.

## One-prompt setup

Clone the repository, open the `framewright` folder as a local Codex task, and
send:

> Use `$framewright-setup` to configure Framewright on this workstation. Inspect
> what is already installed before asking me questions. Explain every file,
> download, login, and provider capability before changing it. Do not start a
> generation job. When setup is ready, launch Framewright and open its Setup
> drawer for me.

The repository-local skill asks only for choices it cannot discover safely:
Docker versus native mode, the generation lanes you actually want, any unusual
ComfyUI location, and permission before large downloads or login flows.

## What Codex may inspect

- Docker, .NET, Node.js, and Codex CLI availability;
- Codex login status, never the contents of `auth.json`;
- ComfyUI's read-only queue, system, node, and model inventory endpoints;
- the checked-in workflow contracts and their exact model filenames; and
- Framewright's ignored local configuration.

## What remains explicit

Codex must stop and ask before downloading model weights or custom nodes,
starting ComfyUI, opening an authentication flow, enabling remote ComfyUI,
installing the optional voice runtime, or performing a paid/provider generation.
It never asks you to paste an API key into chat.

Local choices are written to ignored `.env` or `appsettings.Local.json`. The
tracked defaults keep Codex ImageGen, local voice, and ComfyUI still, video, and
music submission off. The app's Setup drawer shows what is ready, degraded, or
still disabled after launch.
