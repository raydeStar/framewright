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

The pinned wheel gives contracts and receipts. It does **not** give the Blender
stages or the pinned workflow bundles; those need a configured checkout, the
same way Framewright already needs a Blender path and a ComfyUI root, and it is
discovered and reported through the same readiness surface. With no checkout,
the capability is unavailable and the work is refused with a reason.

Compiler stages are minutes to hours. They are queued work, never a request, and
progress is reported from stage receipts landing on disk — `stage 4 of 9 ·
retopology` — because the ledger stages are a known ordered list. A percentage
interpolated against a guessed duration is a lie and is not shown.
