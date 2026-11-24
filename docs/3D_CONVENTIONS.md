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

## Clips and rigid-part motion

A clip is read from an ordinary model file's own animations: its channels, its
keyframes, the bones it moves, and whether this build can sample it exactly.
LINEAR and STEP interpolation are supported; CUBICSPLINE and morph-target
channels are not, and a clip that uses them is reported with the reason rather
than played as an approximation of itself. A clip runs for more than zero and no
more than 600 seconds, with at most 4,096 keyframes on a channel.

A binding lives on the scene object, never on the clip. Two objects can share
one clip and still be trimmed, sped, looped, and scrubbed apart, because the
trim, speed, loop, playback position, and root-motion policy all belong to the
object. A binding is checked before anything plays: the clip must exist in that
file and be supported, the object's rig must be animation-ready and must have
every bone the clip moves, and the trim, speed, and playback position must lie
inside the clip.

**Root motion policy.** A clip's movement of the root bone reaches the scene
exactly once, under the object's own stated policy:

- **Hold** (the default) keeps the character where the artist put it. The
  root's travel is dropped from the pose and the object does not move.
- **Offset** takes that same travel out of the pose and reports it once, as an
  offset to the object's own position.

Neither applies the movement twice, and no policy writes the object's saved
transform: playback draws, and only a save writes.

A rigid part needs no skeleton. It declares a pivot in its own local space, an
axis, a swing between two angles, and a duration, optionally swinging back
again. The pivot is the one point the motion leaves exactly where it is. A
static prop declares no motion and sits where it was put at every time anyone
asks about.

Sampling is pure arithmetic over the stored files: it stores nothing, draws
nothing, and gives the same numbers every time, so scrubbing to a known time has
one right answer. A browser journey holds what the view has on screen against
what the service says is true at the same time.

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

### Two lights

A lit surface is inspected under one of two lights, chosen with the **Look-dev**
toggle in the viewer bar. They answer different questions, so neither replaces
the other.

| | Inspection (the default) | Look-dev |
| --- | --- | --- |
| Flat ambient | 0.75 | none |
| Key, from (1, 2, 1.4) | 1.9 | 1.9 |
| Fill, from (-1.1, -1.8, -0.9) | 1.1 | none |
| Room environment | 0.65 | 1.0 |
| Tone map | none | ACES filmic, exposure 0.35 |

The **inspection light** is even on purpose. Ambient, a key, a fill from below
and a dim room leave no face of a prop unreadable, which is what finding a torn
surface needs. It is not a light for judging paint: it puts about twice the
light on the lit side of a surface that its base colour assumes and shows the
result without a tone map. A matte 18% grey card turned to face the key, stored as
sRGB 118, is shown as about 165; a mid teal authored as sRGB (76, 116, 108) is
shown as about (113, 163, 153). Paint reads pastel, metal has little to
reflect, and highlights clip.

The **look-dev light** asks what the surface will look like in an engine. The
room becomes the main light, the flat ambient and the fill are turned off so
occlusion and shading keep their depth, one key stays for shape and a
highlight, and the picture goes through three.js's ACES filmic tone map. The
exposure is a measurement, not a taste: 0.35 is the value at which that same
grey card facing the key is shown as sRGB 118. The teal above is then shown as
about (70, 115, 106). Changing the key or the room means measuring the exposure
again, and a unit test holds the three numbers together.

What look-dev is not:

- It is not any engine's own tone curve, sky, or exposure. It is the same kind
  of picture — image-based light and a filmic curve, exposed for mid-grey — and
  an asset still has to be seen in the engine it is bound for.
- Nothing casts a shadow. Occlusion comes from the model's own occlusion map.
- The light does not turn with the camera. A face turned away from the key and
  the bright side of the room is shown two and a half to three stops down (the
  grey card reads about sRGB 36 to 52 there), as it would be on the shaded side of a
  lit scene. The inspection light is the one that reads those faces.

The single-channel views — Colour, Normal, Roughness, Metal — are unlit and
bypass the tone map, so they show the stored numbers under either light and the
toggle is unavailable while one is shown. The clay surface and the wireframe
are lit, and follow the toggle. The floor grid keeps its colours under both.

The light is part of how a surface is being looked at, with paint, wireframe
and channel: it is carried to the next revision of the same stack, so two paint
jobs are judged under one light. It is not saved. A model opened afresh is
under the inspection light.

### Close inspection

The inspection camera's distance is in metres, measured to the point it looks
at, and is shown with its yaw and pitch under the view. The wheel, a two-finger
pinch, and the plus and minus keys all move it between the same two limits:

- **Nearest:** 0.25 m, or 0.4 of the model's longest side where that is nearer
  still, and never nearer than twice the camera's near plane (2 cm). A part is
  judged from where a person will stand, and that distance does not grow with
  the part; the limit used to be 0.4 of the longest side for every model, which
  held a two-metre part 80 cm away. A fitting under 62.5 cm keeps the closer
  limit it already had.
- **Farthest:** 40 times the model's longest side.

Two fingers moved together slide the view, as right-drag and shift-drag do, so
a touch screen reaches every part of a model it has closed in on.

### Keys on the 3D view

While the 3D view has the keyboard, its keys are its own: the arrows turn the
inspection camera, Shift with an arrow slides it, and plus and minus move it in
and out. An asset's own page also steps to the previous and next asset in the
library list on the left and right arrows, and that shortcut stands aside for
any key the focused view has used, as it does for a field being typed in. The
arrows step through the list from anywhere else on the page. Stepping away from
a model because its view was being turned would lose the artist's place to a
shortcut. Quick review is the exception and says so: there the left and right
arrows always move through the queue, and up and down still tilt the model.

The point the camera looks at starts at the middle of the model's bounds. On a
thin part that is just behind the surface. On a thick one it is deep inside, and
the nearest views are from within the mesh: slide the view until the point is on
the surface to be judged before closing in.

A browser without WebGL keeps an honest fallback: the measurements, materials,
and supported-subset panels remain, and the surface says the 3D view is
unavailable rather than showing an empty rectangle.

## Deferred

Other containers (`.gltf` + external resources, FBX, OBJ, USD), Draco and
Meshopt compression, texture transcoding, automatic repair, axis or unit
conversion on import, any export of 3D data, and, for rigs, further body
classes, hand and facial rigs, and retargeting between skeletons. For motion:
clip blending, procedural motion generation, and animation editing beyond trim,
speed, loop, and root-motion policy. None of these are supported,
and none are implied by the presence of a model in the library.
