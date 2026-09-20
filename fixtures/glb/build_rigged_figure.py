"""Builds the rigged fixtures Framewright tests its skeleton path with.

Run from the repository root:

    python fixtures/glb/build_rigged_figure.py

Three fixtures come out of one description, so the broken ones differ from the
good one in exactly the way their name says and in nothing else:

    rigged-figure.glb         a complete "humanoid-a" skeleton, every bone named
                              by the documented profile, one influence per
                              vertex at full weight
    rigged-wrong-profile.glb  the same mesh and the same skin, with the bones
                              renamed Bone_00, Bone_01 and so on: a rig nobody
                              can claim is animation-ready
    rigged-broken-skin.glb    the same skeleton, with one vertex whose weights
                              are all zero, so it belongs to no bone at all
    clip-arm-raise.glb        the same figure carrying two reusable clips:
                              "Arm raise", which turns the character's left
                              upper arm a quarter turn over two seconds, and
                              "Step forward", which translates the hips 0.6 m
                              along +Z over two seconds so root motion has
                              something to be a policy about
    clip-wrong-skeleton.glb   a clip for the generically named skeleton, which
                              no humanoid character can accept

The figure is deliberately asymmetric: the character's left arm is longer than
its right, so a mirrored import lands somewhere a test can see. The convention
is the repository's own - right-handed, +Y up, metres, facing +Z - which puts
the character's left at +X.

Authored for this repository; they carry the repository's own licence and
contain no third-party asset.
"""

import json
import pathlib
import struct

HERE = pathlib.Path(__file__).parent

# name, parent, local translation, the size of the box drawn at this joint.
SKELETON = [
    ("Hips", None, (0.0, 0.95, 0.0), (0.26, 0.16, 0.18)),
    ("Spine", "Hips", (0.0, 0.18, 0.0), (0.24, 0.18, 0.16)),
    ("Chest", "Spine", (0.0, 0.22, 0.0), (0.30, 0.22, 0.18)),
    ("Neck", "Chest", (0.0, 0.20, 0.0), (0.10, 0.10, 0.10)),
    ("Head", "Neck", (0.0, 0.10, 0.0), (0.19, 0.24, 0.20)),
    ("LeftUpperArm", "Chest", (0.18, 0.14, 0.0), (0.26, 0.11, 0.11)),
    ("LeftLowerArm", "LeftUpperArm", (0.28, 0.0, 0.0), (0.24, 0.09, 0.09)),
    ("LeftHand", "LeftLowerArm", (0.25, 0.0, 0.0), (0.12, 0.08, 0.06)),
    ("RightUpperArm", "Chest", (-0.18, 0.14, 0.0), (0.24, 0.11, 0.11)),
    ("RightLowerArm", "RightUpperArm", (-0.26, 0.0, 0.0), (0.22, 0.09, 0.09)),
    ("RightHand", "RightLowerArm", (-0.23, 0.0, 0.0), (0.11, 0.08, 0.06)),
    ("LeftUpperLeg", "Hips", (0.10, -0.06, 0.0), (0.13, 0.40, 0.13)),
    ("LeftLowerLeg", "LeftUpperLeg", (0.0, -0.42, 0.0), (0.11, 0.38, 0.11)),
    ("LeftFoot", "LeftLowerLeg", (0.0, -0.40, 0.06), (0.10, 0.07, 0.22)),
    ("RightUpperLeg", "Hips", (-0.10, -0.06, 0.0), (0.13, 0.40, 0.13)),
    ("RightLowerLeg", "RightUpperLeg", (0.0, -0.40, 0.0), (0.11, 0.38, 0.11)),
    ("RightFoot", "RightLowerLeg", (0.0, -0.38, 0.06), (0.10, 0.07, 0.22)),
]


def world_positions():
    """Each joint's rest position in model space, walked down the hierarchy."""
    index = {name: position for position, (name, *_rest) in enumerate(SKELETON)}
    world = {}
    for name, parent, translation, _size in SKELETON:
        base = world[parent] if parent else (0.0, 0.0, 0.0)
        world[name] = tuple(base[axis] + translation[axis] for axis in range(3))
    return index, world


def box_at(centre, size):
    """A box centred on a joint, so every vertex plainly belongs to that joint."""
    half = [value / 2 for value in size]
    corners = [
        (centre[0] + x * half[0], centre[1] + y * half[1], centre[2] + z * half[2])
        for x, y, z in [(-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
                        (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)]
    ]
    faces = [
        (0, 1, 2), (0, 2, 3), (4, 6, 5), (4, 7, 6), (0, 4, 5), (0, 5, 1),
        (3, 2, 6), (3, 6, 7), (0, 3, 7), (0, 7, 4), (1, 5, 6), (1, 6, 2),
    ]
    return corners, faces


# name, bone, path, keyframe times, keyframe values. Rotations are quaternions.
CLIPS = [
    ("Arm raise", "LeftUpperArm", "rotation", [0.0, 1.0, 2.0], [
        (0.0, 0.0, 0.0, 1.0),
        (0.0, 0.0, 0.38268343, 0.92387953),   # 45 degrees about Z
        (0.0, 0.0, 0.70710678, 0.70710678),   # 90 degrees about Z
    ]),
    ("Step forward", "Hips", "translation", [0.0, 1.0, 2.0], [
        (0.0, 0.95, 0.0),
        (0.0, 0.95, 0.3),
        (0.0, 0.95, 0.6),
    ]),
]


def pad(data: bytes, filler: bytes) -> bytes:
    remainder = len(data) % 4
    return data if remainder == 0 else data + filler * (4 - remainder)


def build(output: pathlib.Path, scene_name: str, *, generic_names: bool = False, break_skin: bool = False,
          with_clips: bool = False) -> None:
    index, world = world_positions()

    positions, indices, joints, weights = [], [], [], []
    for name, _parent, _translation, size in SKELETON:
        corners, faces = box_at(world[name], size)
        base = len(positions)
        positions.extend(corners)
        indices.extend((base + a, base + b, base + c) for a, b, c in faces)
        # One influence per vertex at full weight: nothing here needs blending
        # to be a valid skin, and a single influence is the easiest thing to
        # check against by hand.
        joints.extend([(index[name], 0, 0, 0)] * len(corners))
        weights.extend([(1.0, 0.0, 0.0, 0.0)] * len(corners))

    if break_skin:
        # One vertex that belongs to no bone. Nothing else about the file moves.
        weights[0] = (0.0, 0.0, 0.0, 0.0)

    position_bytes = b"".join(struct.pack("<3f", *corner) for corner in positions)
    index_bytes = b"".join(struct.pack("<3H", *face) for face in indices)
    joint_bytes = b"".join(struct.pack("<4H", *influence) for influence in joints)
    weight_bytes = b"".join(struct.pack("<4f", *influence) for influence in weights)
    # The inverse bind matrix is the inverse of each joint's rest transform, and
    # every rest transform here is a translation, so the inverse is the negated
    # translation. Column-major, as glTF stores it.
    bind_bytes = b"".join(
        struct.pack("<16f", 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -world[name][0], -world[name][1], -world[name][2], 1)
        for name, *_rest in SKELETON)

    # Clip keyframes live in the same binary chunk as everything else, so a clip
    # is an ordinary self-contained GLB rather than a second kind of file.
    clip_chunks = []
    if with_clips:
        for _name, _bone, path, times, values in CLIPS:
            clip_chunks.append(b"".join(struct.pack("<f", time) for time in times))
            stride = 4 if path == "rotation" else 3
            clip_chunks.append(b"".join(struct.pack(f"<{stride}f", *value) for value in values))

    chunks = [position_bytes, index_bytes, joint_bytes, weight_bytes, bind_bytes, *clip_chunks]
    binary = b""
    offsets = []
    for chunk in chunks:
        binary = pad(binary, b"\x00")
        offsets.append(len(binary))
        binary += chunk
    binary = pad(binary, b"\x00")

    minimum = [min(corner[axis] for corner in positions) for axis in range(3)]
    maximum = [max(corner[axis] for corner in positions) for axis in range(3)]

    def bone_name(position: int, name: str) -> str:
        return f"Bone_{position:02d}" if generic_names else name

    # Node 0 is the skinned mesh; the joints follow it, in skeleton order, so a
    # joint index is also its position in this list plus one.
    nodes = [{"name": "Figure", "mesh": 0, "skin": 0}]
    children = {name: [] for name, *_rest in SKELETON}
    for position, (name, parent, _translation, _size) in enumerate(SKELETON):
        if parent:
            children[parent].append(position + 1)
    for position, (name, _parent, translation, _size) in enumerate(SKELETON):
        node = {"name": bone_name(position, name), "translation": list(translation)}
        if children[name]:
            node["children"] = children[name]
        nodes.append(node)

    document = {
        "asset": {"version": "2.0", "generator": "Framewright fixture builder"},
        "scene": 0,
        "scenes": [{"name": scene_name, "nodes": [0, 1]}],
        "nodes": nodes,
        "skins": [{
            "name": "Figure skin",
            "skeleton": 1,
            "joints": list(range(1, len(SKELETON) + 1)),
            "inverseBindMatrices": 4,
        }],
        "meshes": [{
            "name": "Figure",
            "primitives": [{
                "attributes": {"POSITION": 0, "JOINTS_0": 2, "WEIGHTS_0": 3},
                "indices": 1, "material": 0, "mode": 4,
            }],
        }],
        "materials": [
            {"name": "Figure skin", "doubleSided": False, "alphaMode": "OPAQUE",
             "pbrMetallicRoughness": {"baseColorFactor": [0.72, 0.56, 0.44, 1.0], "metallicFactor": 0.0, "roughnessFactor": 0.68}},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": len(positions), "type": "VEC3", "min": minimum, "max": maximum},
            {"bufferView": 1, "componentType": 5123, "count": len(indices) * 3, "type": "SCALAR"},
            {"bufferView": 2, "componentType": 5123, "count": len(joints), "type": "VEC4"},
            {"bufferView": 3, "componentType": 5126, "count": len(weights), "type": "VEC4"},
            {"bufferView": 4, "componentType": 5126, "count": len(SKELETON), "type": "MAT4"},
        ],
        "bufferViews": [
            {"buffer": 0, "byteOffset": offsets[0], "byteLength": len(position_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": offsets[1], "byteLength": len(index_bytes), "target": 34963},
            {"buffer": 0, "byteOffset": offsets[2], "byteLength": len(joint_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": offsets[3], "byteLength": len(weight_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": offsets[4], "byteLength": len(bind_bytes)},
        ],
        "buffers": [{"byteLength": len(binary)}],
    }

    if with_clips:
        index_of = {name: position for position, (name, *_rest) in enumerate(SKELETON)}
        animations = []
        for clip_index, (name, bone, path, times, values) in enumerate(CLIPS):
            time_accessor = len(document["accessors"])
            value_accessor = time_accessor + 1
            view = len(document["bufferViews"])
            time_bytes = clip_chunks[clip_index * 2]
            value_bytes = clip_chunks[clip_index * 2 + 1]
            document["bufferViews"].append(
                {"buffer": 0, "byteOffset": offsets[5 + clip_index * 2], "byteLength": len(time_bytes)})
            document["bufferViews"].append(
                {"buffer": 0, "byteOffset": offsets[6 + clip_index * 2], "byteLength": len(value_bytes)})
            document["accessors"].append(
                {"bufferView": view, "componentType": 5126, "count": len(times), "type": "SCALAR",
                 "min": [min(times)], "max": [max(times)]})
            document["accessors"].append(
                {"bufferView": view + 1, "componentType": 5126, "count": len(values),
                 "type": "VEC4" if path == "rotation" else "VEC3"})
            animations.append({
                "name": name,
                "samplers": [{"input": time_accessor, "interpolation": "LINEAR", "output": value_accessor}],
                "channels": [{"sampler": 0, "target": {"node": index_of[bone] + 1, "path": path}}],
            })
        document["animations"] = animations

    json_chunk = pad(json.dumps(document, separators=(",", ":")).encode("utf-8"), b" ")
    total = 12 + 8 + len(json_chunk) + 8 + len(binary)
    glb = struct.pack("<III", 0x46546C67, 2, total)
    glb += struct.pack("<II", len(json_chunk), 0x4E4F534A) + json_chunk
    glb += struct.pack("<II", len(binary), 0x004E4942) + binary
    output.write_bytes(glb)

    print(f"{output.name}: {len(glb)} bytes, {len(SKELETON)} bones, {len(positions)} vertices, "
          f"bounds {tuple(round(value, 4) for value in minimum)} to {tuple(round(value, 4) for value in maximum)}")


def main() -> None:
    build(HERE / "rigged-figure.glb", "Rigged figure")
    build(HERE / "rigged-wrong-profile.glb", "Rigged figure wrong profile", generic_names=True)
    build(HERE / "rigged-broken-skin.glb", "Rigged figure broken skin", break_skin=True)
    build(HERE / "clip-arm-raise.glb", "Clip arm raise", with_clips=True)
    build(HERE / "clip-wrong-skeleton.glb", "Clip wrong skeleton", generic_names=True, with_clips=True)


if __name__ == "__main__":
    main()
