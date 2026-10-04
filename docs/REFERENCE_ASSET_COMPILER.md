# The Reference Asset Compiler dependency

Framewright does not generate 3D assets. It imports, inspects, arranges,
directs and reviews them. The work of turning a reference image into a
textured, rigged, animated model belongs to the Reference Asset Compiler
(`github.com/raydeStar/reference-asset-compiler`, MIT), which is a facilitator
with no front end of its own.

**The contract between them is canonical in that repository**, at
`docs/BROWSER_STUDIO_CONTRACT.md`. It is not copied here. Integration work needs
a checkout anyway — you cannot build against a pipeline you do not have — and a
second copy of a contract is a second thing to keep true.

The repository boundary is also a maintenance rule. Changes to geometry
generation, mesh cleanup, retopology, UVs, texture generation or baking,
material packing, texture compression, skeleton profiles, rigging, deformation
gates, payload construction, or compiler receipts are implemented and tested in
`reference-asset-compiler`. Framewright then updates its pinned integration and
consumes the resulting contract. Framewright may provide the controls, durable
job lifecycle, validated library import, scene use, and review UI; it does not
fork the modeling or texturing implementation.

## What that means here

- **Skeleton profiles are not ours.** `profiles/skeletons/*.json` in the
  compiler is the single source of truth for what a rig must contain:
  `required_bones`, `expected_parents`, `optional_bones`,
  `allow_unlisted_bones`, `exact_bone_count`, budgets. Framewright reads them.
  It does not carry its own, and it does not invent profiles.
  `humanoid-a` in [3D_CONVENTIONS.md](3D_CONVENTIONS.md) is a **test fixture**
  for this repository's own generated GLBs, not an engine profile, and should
  not be presented to an artist as one.
- **Verdicts are not ours.** The compiler gates an asset and writes a receipt.
  Framewright records the receipt hash, the compiler version, the `profile_id`
  and the ledger's `production_ready` flag, and displays what they say. It never
  re-runs a gate or upgrades a claim.
- **Conversions are not ours.** The payload arrives as a self-contained GLB in
  +Y up metres. Framewright performs no axis or unit conversion on import, which
  is already the rule in [3D_CONVENTIONS.md](3D_CONVENTIONS.md). A payload that
  would need converting is a wrong export, not a missing import feature.
- **Root motion is ours to apply, once.** The compiler declares whether a clip
  translates the root bone. Framewright decides the policy per object — Hold or
  Offset — exactly as M15 already implements it. Only one side moves the object.

## Implemented consumer checks

- **Profiles come from the compiler checkout.** Framewright reads
  `profiles/skeletons/*.json` from the configured checkout and enforces required
  bones, expected parents, optional and unlisted bones, exact counts, roots,
  influence limits, and triangle budgets. If the directory is absent or
  malformed, a skinned model remains an unknown skeleton and cannot become
  animation-ready. The repository's `humanoid-a` data now exists only under
  test fixtures.
- **The skeleton fingerprint matches the compiler contract.** Both sides use
  ordinal bone ordering, integer quantization rather than formatted decimals,
  and quaternion sign canonicalization to `w >= 0`. Both repositories assert
  the same expected hex string against the shared vector. That is what lets a
  rig with no standard profile — a spider, a machine — carry clips honestly: a
  fingerprint match means *these clips were made for this skeleton*, and claims
  nothing about retargeting.

## Running it

The browser scene/rig integration was validated against compiler commit
`07460ea` (2026-09-22), retaining the `0.1.2` CLI contract. That checkout adds
`quadruped_cat_browser` and `ue5_manny_browser`: explicit 50k browser profiles
with the canonical bone hierarchy and weight limits. The original 20k
production profiles remain unchanged. A matching browser rig is usable for
posing; it does not constitute creative approval or UE production certification.
Floor normalization, component preservation, material repair and rig stress
tests remain compiler-owned. See its canonical contract for invocation/evidence.
The clip-selective export command was validated against compiler commit
`15791dd` (2026-09-22), also on the `0.1.2` CLI.

The asset inspector reads the named clips from each imported GLB revision and
previews them on that revision's rig. Its previous/next picker, play/pause and
time scrub do not change the stored asset. For a download, Framewright sends
the checked clip names to `rac export-animations`; the compiler packages a
GLB containing all, some, or none of the animation declarations. The library
revision is immutable. The compiler preserves the original binary chunk, so
selective exports can retain unused animation bytes rather than becoming
proportionally smaller.

The pinned wheel gives contracts and receipts. It does **not** give the Blender
stages or the pinned workflow bundles; those need a configured checkout, the
same way Framewright needs a Blender and a ComfyUI root, and it is discovered
and reported through the same readiness surface. With no checkout, the
capability is unavailable and the work is refused with a reason.

Compiler stages are minutes to hours. They are queued work, never a request, and
progress is reported from stage receipts landing on disk — `stage 4 of 9 ·
retopology` — because the ledger stages are a known ordered list. A percentage
interpolated against a guessed duration is a lie and is not shown.

## Setting it up

You do not need an agent for this. Three things make the 3D routes work:

1. **The compiler.** Install it so that `rac` runs from a terminal (from a
   checkout: `pip install -e .`). If it lives somewhere else, set
   `Integrations:ReferenceAssetCompiler:Executable` to its full path.
2. **A checkout of the compiler**, for the stages that run scripts. Set
   `Integrations:ReferenceAssetCompiler:CheckoutPath` to it.
3. **Blender**, for most stages. Framewright looks for it for you (below).

Settings go in `appsettings.Local.json` beside Framewright (it is never
committed or packaged; `appsettings.Local.example.json` shows the shape).
Generation and preparation stay switched off until you also set
`Integrations:ReferenceAssetCompiler:SubmissionEnabled` to `true`. Open any
model or reference image afterwards: the 3D panels say what is ready, what is
missing and why.

### How Framewright finds Blender

Blender is optional. Without it, everything that does not need it keeps
working, and the stages that do need it say "blender missing" along with what
Framewright looked for and how to point it at one.

Framewright takes the first of these that applies:

| Order | Where | Used how |
| --- | --- | --- |
| 1 | `Integrations:ReferenceAssetCompiler:BlenderPath` | As given |
| 2 | The `BLENDER` environment variable, then `RAC_BLENDER` | As given |
| 3 | `blender` (`blender.exe` on Windows) on `PATH` | If it answers `--version` |
| 4 | The usual install places for this system (below) | If it answers `--version` |

The usual install places:

- **Windows:** `%ProgramFiles%\Blender Foundation\Blender X.Y\blender.exe`
  (the newest version first); then every Steam library, read from Steam's
  `libraryfolders.vdf`, at `steamapps\common\Blender\blender.exe` (Steam is
  found from the registry, or at `%ProgramFiles(x86)%\Steam`); then the
  Microsoft Store app. A winget install lands in Program Files.
- **macOS:** `/Applications/Blender.app` and `~/Applications/Blender.app`.
- **Linux:** `/usr/bin/blender`, `/usr/local/bin/blender`, `/snap/bin/blender`,
  then Steam libraries under `~/.steam/steam` and `~/.local/share/Steam`.

A Blender you named yourself (rows 1 and 2) is always the one used. Framewright
still asks it for its version, and if it does not answer, the panel says so; it
does not quietly swap in a different Blender. A Blender it found (rows 3 and
4) is only used if it answers `blender --version`; one that does not is
skipped.

Whichever it is, Framewright hands it to the compiler as `--blender <path>`,
exactly as a configured path always was, so every receipt names the exact
executable. The compiler itself does no searching. The model panels show the
Blender in use, its version, how it was found, and how to choose another. The
answer is remembered until a setting changes; when nothing usable was found,
Framewright looks again a minute later, so installing Blender does not need a
restart.

To stop Framewright looking at all, set
`Integrations:ReferenceAssetCompiler:DiscoverBlender` to `false`; then only
`BlenderPath` is used.

## Runtime triangle budgets

What a model should cost at runtime is the compiler's decision. A coin, a chest
and a cart are not the same budget, and a fixed number in this studio gave them
one. The compiler decides from the asset's name (which says whether it is a
prop, a hero piece, a modular kit piece, vegetation or a character) and its real
size.

- **Asking.** For any model, Framewright runs
  `rac budget --name=<display name> --dims <x> <y> <z>`, using the model's
  library name and the dimensions measured from its stored file. This needs no
  Blender and runs nothing on a GPU. The answer
  (`reference-asset-compiler.triangle-budget.v1`) is served unchanged from
  `GET /api/assets/{id}/triangle-budget`.
- **Showing.** The Geometry panel shows the budget beside the triangle count,
  for example `100,000 · budget 5,000 (prop)`, with a gentle note when the model
  is over it. Over budget is not an error: a reviewed master is often dense on
  purpose.
- **Preparing.** **Prepare for runtime** defaults to **Auto**, showing the
  compiler's sentence and its reason before anything runs. A request with no
  `triangleBudget` is Auto: the reduce stage is told `--triangle-budget auto`
  and `--asset-name <display name>`, and decides from the mesh it actually
  measures. Its receipt's `budget_decision` (the same fields, plus every rung of
  the ladder it tried) is copied into the job result, and the new revision's
  note says the budget was the compiler's. A number typed under **Choose a
  number instead** is sent as it was before.
- **Refusing early.** Auto is refused before anything is queued when the
  compiler gives the model no number (a character takes the rig route), when the
  model is already within the budget, or when the compiler cannot answer.
- **Degrading honestly.** With no compiler, a compiler older than `rac budget`
  (argparse's "invalid choice"), or a compiler that refuses the model, there is
  no suggestion and no Auto: the panel says why and asks for a number.
  Framewright never fills in a budget of its own.

Generation keeps two fixed numbers on purpose: set dressing is remeshed to
20,000 triangles and a hero to 80,000. The remesh stage has no Auto in the
compiler's contract, and those numbers are what the artist's own answer to "how
close will the camera get?" stands for, together with the grid, octree and
texture sizes of the same recipe.
