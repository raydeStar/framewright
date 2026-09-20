# Framewright 3D conventions

One convention, written down before the first model was imported, so that a
mirrored axis or a silent unit change is a test failure rather than a discovery
made three milestones later. Everything here describes the supported route as it
actually behaves today; anything not listed is unsupported, not implied.

## Coordinate system and units

Framewright adopts glTF 2.0's conventions unchanged at the interchange boundary,
because converting on import is how orientation bugs get built in.

| Property | Convention |
| --- | --- |
| Handedness | Right-handed |
| Up axis | +Y |
| Forward | -Z (a camera looks down -Z; +Z points back toward the viewer) |
| Right | +X |
| Distance unit | Metres. One scene unit is one metre. |
| Angle unit | Radians in data; degrees only in artist-facing labels |
| Rotation | Quaternions `[x, y, z, w]`, as glTF stores them |
| Matrices | Column-major 16-float arrays, as glTF stores them |

No axis conversion, unit scaling, or handedness flip happens on import. A GLB is
stored byte-identical to the file the artist chose, addressed by its SHA-256
content hash, and served back unchanged.

## Transforms and pivots

A node's pivot is its own origin. Placement is `T * R * S` applied to the node's
local space, composed down the node tree, exactly as glTF defines it. Framewright
does not re-centre geometry, bake transforms, or move a pivot on import: if a
model arrives with its origin in an odd place, that is a fact about the model and
the artist can see it in the reported bounds.

Reported bounds are **scene-space**, computed by transforming every accessor's
declared `min`/`max` corners through the node tree. Raw accessor values are not
reported as dimensions, because they ignore where the node tree puts the
geometry.

## Colour

| Stage | Handling |
| --- | --- |
| Base colour factors, emissive factors | Linear, as glTF defines them |
| Base colour and emissive textures | sRGB-encoded, decoded to linear on sample |
| Normal, metallic-roughness, occlusion textures | Linear, never sRGB-decoded |
| Viewer output | Linear lighting, sRGB output transform |

The project's delivery colour space (`Rec.709`, `Display P3 D65`, `Rec.2020`)
governs rendered picture, not model inspection. Inspecting a model does not read
or change it.

## Supported import subset

The initial browser-facing route accepts **self-contained GLB (binary glTF)
2.0** and nothing else. Validation happens server-side, before any bytes reach a
graphics context, and a refusal names its reason.

Refused, by design:

- any container that is not GLB 2.0, or whose header length disagrees with the
  file, or whose chunk table does not fill the file;
- more than one JSON or binary chunk, or a first chunk that is not JSON;
- any `uri` on a buffer or an image — the initial route resolves everything from
  the file's own binary chunk and fetches nothing;
- any entry in `extensionsRequired` that is not on the supported list, which is
  currently **empty**;
- geometry whose POSITION accessors do not declare `min` and `max`, because
  bounds would then have to be guessed;
- a document with no mesh primitives at all.

### Resource ceilings

Checked before loading, not after:

| Ceiling | Value |
| --- | --- |
| File size | 64 MB |
| Vertices | 1,500,000 |
| Triangles | 2,000,000 |
| Embedded texture bytes | 48 MB |
| Nodes | 4,096 |
| Materials | 256 |
| Images | 64 |

These are the `GlbSupportProfile.Default` values and are reported to the client
with every model profile, so the artist reads the real ceiling rather than a
number in a document that may have drifted.

## Test fixtures

`fixtures/glb/asymmetric-block.glb` is the known-dimension fixture the 3D path
is tested against. It is authored by this repository's own builder
(`fixtures/glb/build_asymmetric_block.py`), carries the repository's licence,
and contains no third-party asset.

| Property | Value |
| --- | --- |
| Body | 2.00 m (X) × 1.00 m (Y) × 0.50 m (Z), min corner at the origin |
| Corner tab | 0.25 m cube, on a node translated to (1.75, 0.75, 0.50) |
| Scene bounds | min (0, 0, 0), max (2.00, 1.00, 0.75) |
| Vertices / triangles | 16 / 24 |
| Materials | `Block body`, `Corner tab` — distinct, untextured |

It is deliberately asymmetric on all three axes, and the tab's offset lives on
its node rather than in its vertices. A mirrored, rotated, mis-scaled, or
transform-ignoring import therefore produces different numbers, instead of
passing because a symmetric cube looks the same either way.

`fixtures/glb/asymmetric-post.glb` is a second model from the same builder
with deliberately different extents — bounds min (0, 0, 0), max (1.00, 2.00,
0.45) — so a test that switches between two models can tell they really swapped.

Three rigged fixtures come out of one description in
`fixtures/glb/build_rigged_figure.py`: `rigged-figure.glb` (a complete
humanoid-a skeleton), `rigged-wrong-profile.glb` (the same mesh and skin with
bones named `Bone_00` and so on), and `rigged-broken-skin.glb` (one vertex
weighted to no bone). The broken ones differ from the good one in exactly the
way their name says and in nothing else. The figure is asymmetric — the
character's left arm is longer than its right — so a mirrored import lands
somewhere a test can see.

## Rigs

One body class is supported: **humanoid-a**, a spine, two arms, and two legs.
Its bones are named exactly, and a rig is only ever described as matching this
profile when every one of them is present:

    Hips, Spine, Chest, Neck, Head,
    LeftUpperArm, LeftLowerArm, LeftHand,
    RightUpperArm, RightLowerArm, RightHand,
    LeftUpperLeg, LeftLowerLeg, LeftFoot,
    RightUpperLeg, RightLowerLeg, RightFoot

"Left" is the character's own left, which with +Y up and the character facing
+Z is +X.

The hierarchy is read from the file's node tree rather than inferred from the
names, and each bone reports both its own rest transform and the rest position
that composes down the tree. A model's bind pose is confirmed only when its
inverse bind matrices exist, match the joint count, and are finite. Skin weights
are read from the file's binary chunk: every skinned vertex must be weighted to
bones the skin actually has, with finite non-negative weights that sum to one.
Above 250,000 skinned vertices the weights are reported unchecked rather than
assumed sound.

A rig is called **animation-ready** only when it matches this profile and passes
every one of those checks. A skeleton whose bones are named anything else is an
unknown skeleton, not an almost-humanoid, and nothing downstream may treat it as
animatable. A static prop has no skeleton at all, which is an ordinary answer
rather than a fault.

A rig can be posed for inspection: named bones are rotated away from the rest
pose and every joint's resulting position is computed from the stored bytes. The
calculation stores nothing and draws nothing, so the same rig and the same pose
give the same numbers every time, and a rig that is not animation-ready is not
posed at all.

## Browser renderer

The viewer uses **three.js 0.186.0** with its `GLTFLoader`, pinned to the exact
version tested rather than a range. It loads only when an artist opens a model:
three.js has its own build chunk, so the shared bundle every page load pays for
is unchanged by the presence of 3D.

The inspection camera orbits; the model is never transformed. The viewer also
publishes the bounding box it actually loaded next to the bounds the service
measured from the stored bytes, and a browser journey asserts the two agree axis
for axis — an orientation swap between file and view would break that agreement
even though the picture would still look like a plausible object.

A browser without WebGL keeps an honest fallback: the measurements, materials,
and supported-subset panels remain, and the surface says the 3D view is
unavailable rather than showing an empty rectangle.

## Deferred

Other containers (`.gltf` + external resources, FBX, OBJ, USD), Draco and
Meshopt compression, texture transcoding, automatic repair, axis or unit
conversion on import, any export of 3D data, and, for rigs, further body
classes, hand and facial rigs, and retargeting between skeletons. None of these are supported,
and none are implied by the presence of a model in the library.
