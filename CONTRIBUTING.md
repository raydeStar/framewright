# Contributing to Framewright

Framewright protects production evidence: source frames, authority versions,
reference bindings, manifests, provider requests, and generated outputs must
remain traceable. A visually plausible result is not sufficient proof that a
generation path is correct.

## Before changing code

1. Create a normal branch from `main`; do not create a linked worktree.
2. Preserve existing local changes and the artist's ComfyUI queue.
3. Keep secrets, generated media, databases, and production assets outside Git.
4. State whether a change affects composition, identity, wardrobe, style,
   world rules, provider dispatch, or review state.

## Required verification

Run the repository gate from PowerShell:

```powershell
.\scripts\verify.ps1
```

Provider changes also require contract tests proving the exact ordered visual
attachments and prompt labels. Job changes require restart/recovery and
idempotency tests. UI changes require desktop Chromium and iPad WebKit evidence.

Do not clear, interrupt, reorder, or claim ownership of work submitted directly
to ComfyUI or by another application. Framewright may observe only the provider
identifiers that it submitted and persisted itself.

## Pull requests

Keep commits small enough to revert independently. Include:

- the artist-facing outcome;
- the invariant or failure mode being protected;
- tests run and their results;
- screenshots for changed interaction states; and
- migration, backup, or rollback notes when persistent data changes.
