# Workflow library

Curated ComfyUI workflows that Storyboard Studio is allowed to dispatch.

**This directory is written to be edited by an AI on request.** Ask for
*"a new fast-draft workflow that uses Z-Image Turbo instead"* or *"make the music
workflow 30 seconds by default"* and the change is: edit or add a JSON file,
update `library.json`, run `dotnet test`. The validator turns a bad workflow into
a failing test rather than a failed render two minutes into a queue.

---

## What is here

| File | Purpose |
|---|---|
| `library.json` | The index. Every dispatchable workflow is declared here. |
| `text-draft.json` | Shot card → rendered frame. No sketch required; Qwen-Image 2512 + 4-step turbo LoRA. |
| `reference-draft.json` | Approved authority images + brief → newly synthesized frame; Qwen-Image-Edit 2511 multi-reference route. |
| `fast-draft.json` | Stick-figure sketch → rendered frame. Qwen-Image-Edit 2511 + 4-step Lightning LoRA. |
| `current-frame-edit.json` | Existing image + pinned notes → controlled revision. Qwen-Image-Edit 2511 at its installed 20-step quality baseline. |

These are **API-format** exports (the shape ComfyUI's `POST /prompt` accepts —
a flat object of `id → { class_type, inputs }`), not the UI graph format. In
ComfyUI: enable dev mode, then *Workflow → Export (API)*.

Per `docs/IMPLEMENTATION_HANDOFF.md` §4 these are application configuration
authored here — **never** workflow JSON copied out of the live production
installation. Keep it that way.

---

## Placeholder contract

The adapter does literal string substitution on the raw JSON **before** parsing
it. General numeric placeholders such as `{{SEED}}` remain quoted because their
adapters replace the token and its surrounding quotes. The H3 quality-profile
placeholders are deliberately unquoted because that adapter replaces the raw
token with a JSON number.

| Placeholder | Type | Meaning |
|---|---|---|
| `{{PROMPT}}` | string | Required everywhere. The assembled creative brief plus authority packet and locked constraints. |
| `{{NEGATIVE_PROMPT}}` | string | Optional. Empty string when unset. |
| `{{INPUT_IMAGE}}` | string | Filename returned by ComfyUI's `/upload/image`. Required by any image-conditioned workflow. |
| `{{REFERENCE_IMAGE_1}}` through `{{REFERENCE_IMAGE_3}}` | string | Optional approved authority images. The adapter uploads exact ratified versions, prioritizes a spatially pinned authority, labels each image in the prompt, and removes unused reference nodes before dispatch. |
| `{{LAST_FRAME_IMAGE}}` | string | Second anchor for two-anchor video. If present in the file, a last frame becomes mandatory. |
| `{{SEED}}` | number | Derived deterministically from the manifest hash, so the same frozen manifest reproduces the same render. |
| `{{DURATION_SECONDS}}` | number | Music only. |
| `{{LYRICS}}` | string | Music only. Empty string for an instrumental bed. |
| `{{VIDEO_WIDTH}}`, `{{VIDEO_HEIGHT}}` | number | H3 native render dimensions selected by the Low, Medium, High, or promotion-only Max profile. |
| `{{VIDEO_STEPS}}` | number | H3 sampling budget selected by the quality profile. |
| `{{VIDEO_OUTPUT_WIDTH}}`, `{{VIDEO_OUTPUT_HEIGHT}}` | number | Exact encoded canvas. Review proxies preserve the project ratio; High and Max use the project delivery width and height. |
| `{{VIDEO_FPS}}` | number | Project frame rate frozen into the manifest and passed to the encoder. |
| `{{VIDEO_LENGTH}}` | number | Exact shot duration in frames, frozen into the manifest. |
| `{{VIDEO_FILENAME_PREFIX}}` | string | Quality-labelled output prefix used to identify the rendered pass in ComfyUI history. |

A placeholder listed in `requiredPlaceholders` must appear in the file, and one
that appears in the file but is never supplied is also an error. Both directions
are checked.

---

## Adding a workflow

1. Export the workflow from ComfyUI in **API format**.
2. Replace the values you want Storyboard Studio to drive with placeholders.
3. Drop the file in this directory.
4. Add an entry to `library.json`:

```jsonc
{
  "id": "kebab-case-id",
  "name": "Human name shown in the UI",
  "file": "your-workflow.json",
  "kind": "Image" | "Video" | "Music",
  "configKey": "Integrations:ComfyUi:ExternalWorkflowPath",
  "summary": "What it does and roughly what it costs to run.",
  "requiredPlaceholders": ["{{PROMPT}}", "{{SEED}}"],
  "optionalPlaceholders": [],
  "requiredNodeTypes": ["UNETLoader", "KSampler", "SaveImage"],
  "requiredModels": { "UNETLoader.unet_name": "the-file-you-selected.safetensors" },
  "outputs": "image" | "video" | "audio"
}
```

5. `dotnet test` — `WorkflowLibraryTests` will fail loudly if the JSON is
   malformed, a declared placeholder is missing, a node referenced in `inputs`
   does not exist, or `requiredModels` disagrees with the file.
6. `GET /api/workflows` additionally checks the live ComfyUI: whether each
   `requiredNodeTypes` entry is installed and each `requiredModels` value is
   actually present in that loader's options. That part needs ComfyUI running
   and is **read-only** — it calls `/object_info` and nothing else.

---

## Notes on the shipped defaults

**`text-draft.json`.** This is the default for a shot card with no saved
composition and no visual authorities. Framewright assembles description, action, camera, exact authority
versions, and locked constraints, then uses Qwen-Image 2512 to establish the
first visual candidate. The artist never selects a graph. If a composition is
later added, the dispatcher automatically switches to `fast-draft.json`.

**`reference-draft.json`.** When there is no composition but one or more visual
authorities are selected, Framewright uses Qwen-Image-Edit instead of asking the
text-to-image model to interpret reference pixels. Up to three exact images feed
the model directly; any remaining authorities still travel in the locked text
packet. This prevents the text route from reproducing references as a collage.

**`fast-draft.json`.** Qwen-Image-Edit is instruction-driven: it reads the sketch
as an image and the brief as an edit instruction, which is why it holds blocking
and framing without a ControlNet. There are no ControlNet models installed on
this workstation, so a scribble-ControlNet approach would need a download first;
this route needs nothing new. The Lightning LoRA is what makes four steps at
CFG 1.0 viable — if you drop the LoRA, raise steps to ~20 and CFG to ~2.5.

**`current-frame-edit.json`.** Revision notes need stronger instruction following
than rough composition drafts. This route therefore follows the installed Qwen
2511 edit blueprint's 20-step, CFG 4 baseline with native Kontext scaling and
CFG normalization. It is slower than the four-step sketch loop, but it is far
less likely to ignore a small correction or casually redesign the whole image.

**`h3-video-i2v.json`.** The H3 image-to-video route accepts a required first
frame and optional last frame. Framewright supplies a quality profile rather than
exposing graph settings. Low and Medium are disposable project-ratio proxies;
High is a review pass upscaled to the exact project delivery canvas. Max is
promotion-only: it reuses the reviewed take's immutable seed, anchors, prompt,
authorities, and constraints, then rerenders at 32 steps on that same delivery
canvas. Mixed-aspect source endpoints are normalized through the same visible
center-crop path. The workflow receives the project's frame rate and the shot's
exact frame count; Max output is probed after render and cannot become production
evidence unless its encoded dimensions, rate, duration, and frame count match.
