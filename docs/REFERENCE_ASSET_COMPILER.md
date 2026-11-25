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

Pixal3D generation requires a compiler advertising the `topology` option.
The artist can request a closed shell or preserve open boundaries and give
a width, height or longest side in metres. The job freezes these settings.
A failed game-mesh check stops before UVs and paint and remains visible in
the job history, including after recovery. Only the compiler measures the mesh.

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
- **Generating.** A generated model's budget is decided at the remesh, before
  anything is painted, so the painter paints the final mesh (a mesh reduced
  after painting drags its UVs with every collapse). When the compiler's
  `run-stage --list` shows a remesh stage that takes `asset_name` (compiler
  `9af58b3` and later), generation sends `--triangle-budget auto` and
  `--asset-name <name>`, and a hero also `--role hero`; the hero keeps its finer
  grid and lighter smoothing. The decision is recorded from the remesh receipt
  and quoted in the model's revision note. An older compiler, whose remesh takes
  only a number, is still sent the detail choice's fixed number: 20,000
  triangles for set dressing, 80,000 for a hero. The generation panel says
  which of the two will happen before anything is queued.
- **Keeping a prepared model's paint.** A library model was painted before
  anyone reduced it, so its reduction drags the UVs and the paint slides off
  bands and rivets; no deviation threshold sees it. When `run-stage --list`
  offers `rebake-maps` (compiler `feat/rebake-maps`, 2026-10-04, and later) and
  the model embeds at least one texture, preparation routes
  `review-views → adopt-mesh → reduce-mesh → rebake-maps → browser-payload →
  review-views`. The re-bake reads the reduction and is passed `--dense` (the
  adoption's `.blend`), `--reduction-report` (the reduction's receipt, which
  binds the pair by hash and carries the budget ladder) and
  `--appearance-reference` (the library model). It bakes the paint back from
  the original, compares four fixed views of both on the CPU, and climbs the
  ladder until a rung looks like the original. If none does, it refuses and the
  job fails with its reason; nothing is delivered. The revision note quotes its
  lit and unlit similarity. The panel says this before **Prepare for runtime** is
  pressed (`preparationKeepsPaint` on the readiness answer). An untextured model
  or an older compiler keeps the route it had.

## Making a prop with Pixal3D

Pixal3D (TencentARC) keeps rivets, straps and plank seams as real geometry,
where Hunyuan3D rounds them into blobs. The owner compared the two on the
Frastown skiff and chose Pixal3D for props on 24 November. The compiler's
`pixal-geometry` stage (raydeStar/reference-asset-compiler#22 and later) makes
it one stage. One picture in: a levelled game mesh from Pixal's own simplifier
(a `.blend`) out, and beside it the levelled million-triangle dense mesh.

- **Asking for it.** `POST /api/models/generation` with `engine: "pixal3d"`.
  - Unnamed is `hunyuan3d`, as before.
  - Pixal3D makes props only. Its head end is `none`, and any other is refused.
  - `seed` (1 or more) picks another design from the same picture. A seed picks
    the design, not exact triangles: Pixal3D is not deterministic.
  - `up` says how the piece stands: `shortest` (unnamed: a boat, a bench),
    `longest` (a lantern, a column) or `middle` (a panel on its edge). Pixal3D
    keeps the picture's camera tilt, and this is how the compiler stands it up.
  - Seed and up are refused on a Hunyuan3D request.
- **The route.**
  1. `pixal-geometry`: the size is the landmark the artist chose, a height;
     `--vertex-target` is 100000 for a hero and 40000 for set dressing.
  2. `uv-unwrap`.
  3. `texture`: an 8192 sheet for a hero, 4096 for set dressing.
  4. `paint-sections`: a hero only, at 8192, under the same rules as above.
  5. `project-reference`: the frozen reference laid onto the faces it shows,
     with `--geometry` the report step 1 wrote. Pixal3D built the mesh in that
     picture's camera; the compiler fits it and refuses any other picture.
  6. `bake-detail`: `--resolution 2048`, with occlusion and no relief from the
     paint (after a projection, relief raises the picture's grain into lumps).
  7. `bake-shape`: `--dense` is the dense mesh step 1 wrote.
  8. `glass`, if asked for.
  9. `browser-payload`.
  10. `compress-textures`: 8192 colour and data maps for a hero, 4096 and 2048
     for set dressing; JPEG at 92.

  There is no staging and no remesh, because neither of the compiler's
  reductions keeps Pixal's hard edges. The bakes come after every paint,
  because they read its colour. Glass comes after the bakes, because the shape
  bake wants the one painted material it was given.
- **What it costs.** About 3 minutes for the shape on a GPU with 9 GiB free.
  The skiff took 179 s: a 97,938-triangle game mesh and a 991,833-triangle dense
  one, levelled to 2.2 m. The paints and sections then cost what they do on any
  route.
- **Where it is offered.** The readiness answer's `engines` lists `hunyuan3d`
  and, only where every stage of the Pixal3D route is listed and available,
  `pixal3d`. The generation panel shows the choice when there is one, and
  defaults to Pixal3D for that reason. Pixal3D runs in its own Linux
  environment (WSL on Windows), which the compiler finds through
  `RAC_PIXAL3D_ROOT`, `RAC_PIXAL3D_PYTHON` and `RAC_WSL_DISTRO`.
- **What the model records.** Its revision note says it was made with Pixal3D,
  with its seed, game mesh, sheet and bakes. The job's result carries `engine`,
  `seed` and `up`.

## Painting a large prop in sections

The painter sees a whole model through twelve views of 768 pixels, whatever its
size. A 45 cm prop gets about 0.6 mm of surface to a painted pixel and comes
out sharp; a 2.2 m skiff gets about 3 mm and is smeared and grainy up close. No
sheet size puts back what the painter never saw. The compiler's
`paint-sections` stage (compiler `e6b418f`, which merged
raydeStar/reference-asset-compiler#16, and later)
cuts a large model into sections of about half a metre, paints each one again
on its own from the same picture, and lays them back on the same UVs. Nothing
moves a vertex or a UV.

- **What it is told.** `--reference`, `--views 12 --resolution 768 --atlas
  4096`, and `--section 0.45 --overlap 0.2 --context 0.05 --min-length 1.2
  --max-sections 48`. These are sent rather than left to the compiler's
  defaults, so the sentence the artist reads before queuing is what the stage
  was told. It is the cut the owner judged on the Frastown skiff. The compiler
  keeps the first paint's colour above 3 cm by default, so sections add fine
  light and dark without repainting the design.
- **What it costs.** One painter run a section, roughly 3 minutes each, up to 48,
  on a GPU with 10.5 GiB free (21 GiB until the compiler's 12 GB work). The first proof, a 2.2 m mast, took 11.6 minutes
  for six sections at the old 0.6 m cut and went from about 3.2 mm to about
  1.3 mm of surface a painted pixel. The 2.2 m skiff at the finer cut took 109
  minutes for 45 sections and came out at about 0.7 mm. The exact count is the
  compiler's, decided when it cuts the model, so the panels quote the ceiling.
  Each painter run is allowed fifty minutes by the stage, and 48 of them outlast
  any hour; this stage
  gets `Integrations:ReferenceAssetCompiler:SectionsStageTimeoutMinutes`
  (240 by default) instead of `StageTimeoutMinutes`, and `rac run-stage` is
  passed the same limit as `--timeout`.
- **Generating a hero.** A hero is asked what it is: a standing figure, an
  animal seen from the side, or a prop (`headEnd` `top`, `left`, `right` or
  `none`; unnamed is `top`, as before). A figure or an animal keeps its head
  pass. A prop gets none: the head pass repaints the top of a model as if it
  were a face, and on a 2.2 m mast it left crisp iron above a hard line and flat
  putty below it. When `run-stage --list` offers `paint-sections`, a hero prop is
  painted again in sections straight after `texture`. It is on by default, the
  generation panel says what it costs before anything is queued, and the artist
  can switch it off (`paintSections: false`). Set dressing never runs it, and
  asking for it on set dressing or on a figure is refused. A prop under 1.2 m
  passes through at no cost. The model's revision note quotes the receipt: how
  many sections, and the millimetres a painted pixel covered for the whole model
  and for the coarsest section, or that it was passed through and why.
- **A model already in the library.** **Paint in sections** on a model
  revision routes `review-views → paint-sections → browser-payload →
  review-views` and delivers a revision beside it, unaccepted, with the same
  before-and-after views every derivative has. It paints from the picture linked
  to the model (its reference image), frozen by identity and hash when it is
  queued. It is for props: every face is painted again, so a hero prop that was
  given a head pass loses that hard band along with the soft paint below it.
  Before anything is queued it states its cost, or refuses in one sentence when
  the model has no linked picture, the compiler lacks the stage or the paint
  stack, the revision carries more than one material (sections are painted
  before surfacing; pick an earlier revision), it carries no painted texture, or
  its longest side is under 1.2 m. If the compiler passes it through anyway
  (it fits one section), the job stops with the compiler's reason rather than
  deliver the same model again under a new number.
- **For an agent.** `GET /api/models/sections/readiness` answers for the route;
  `GET /api/assets/{id}/section-paint` answers for one model (`canQueue`,
  `refusal`, `cost`, `longestMetres`, `materialCount`, `referenceAssetId`); and
  `POST /api/models/sections` with `{ sourceAssetId, name }` queues it. The
  generation readiness carries `paintSections` (the lengths and the cost). A
  finished job's result carries `sections`: `painted`, `count`,
  `wholeModelMmPerPixel`, `sectionMmPerPixel` and `note`, copied from the
  receipt (`reference-asset-compiler.section-detail-paint.v1`).
