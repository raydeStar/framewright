"""Builds the known-dimension asymmetric fixtures Framewright tests its 3D path with.

Run from the repository root:

    python fixtures/glb/build_asymmetric_block.py

The fixture is deliberately asymmetric on all three axes so a mirrored, rotated,
or mis-scaled import cannot pass by accident:

    body  2.00 m (X) x 1.00 m (Y) x 0.50 m (Z), min corner at the origin
    tab   0.25 m cube, carried by a node translated to (1.75, 0.75, 0.50)

    overall bounds  min (0, 0, 0)  max (2.00, 1.00, 0.75)

The tab exists only at +X +Y +Z, so any axis flip moves it somewhere a test can
see. Its offset lives on the node rather than in the vertices, so reading the
bounds also exercises the node transform rather than raw accessor values.

A second model, ``asymmetric-post.glb``, is built from the same code with
different extents - bounds (0, 0, 0) to (1.00, 2.00, 0.45) - so a test that
switches between two models can tell they really swapped.

Authored for this repository; they carry the repository's own licence and
contain no third-party asset.
"""

import json
import pathlib
import struct

HERE = pathlib.Path(__file__).parent


def box(size_x: float, size_y: float, size_z: float):
    """Axis-aligned box with its min corner at the local origin."""
    corners = [
        (0, 0, 0), (size_x, 0, 0), (size_x, size_y, 0), (0, size_y, 0),
        (0, 0, size_z), (size_x, 0, size_z), (size_x, size_y, size_z), (0, size_y, size_z),
    ]
    faces = [
        (0, 1, 2), (0, 2, 3),  # -Z
        (4, 6, 5), (4, 7, 6),  # +Z
        (0, 4, 5), (0, 5, 1),  # -Y
        (3, 2, 6), (3, 6, 7),  # +Y
        (0, 3, 7), (0, 7, 4),  # -X
        (1, 5, 6), (1, 6, 2),  # +X
    ]
    positions = b"".join(struct.pack("<3f", *corner) for corner in corners)
    indices = b"".join(struct.pack("<3H", *face) for face in faces)
    return corners, positions, indices


def pad(data: bytes, filler: bytes) -> bytes:
    remainder = len(data) % 4
    return data if remainder == 0 else data + filler * (4 - remainder)


def build(output: pathlib.Path, body_size, tab_size, tab_at, scene_name: str) -> None:
    body_corners, body_positions, body_indices = box(*body_size)
    tab_corners, tab_positions, tab_indices = box(*tab_size)

    chunks = [body_positions, body_indices, tab_positions, tab_indices]
    binary = b""
    offsets = []
    for chunk in chunks:
        binary = pad(binary, b"\x00")
        offsets.append(len(binary))
        binary += chunk
    binary = pad(binary, b"\x00")

    def bounds(corners):
        return (
            [min(corner[axis] for corner in corners) for axis in range(3)],
            [max(corner[axis] for corner in corners) for axis in range(3)],
        )

    body_min, body_max = bounds(body_corners)
    tab_min, tab_max = bounds(tab_corners)

    document = {
        "asset": {"version": "2.0", "generator": "Framewright fixture builder"},
        "scene": 0,
        "scenes": [{"name": scene_name, "nodes": [0, 1]}],
        "nodes": [
            {"name": "Block body", "mesh": 0},
            {"name": "Corner tab", "mesh": 1, "translation": list(tab_at)},
        ],
        "meshes": [
            {"name": "Block body", "primitives": [{"attributes": {"POSITION": 0}, "indices": 1, "material": 0, "mode": 4}]},
            {"name": "Corner tab", "primitives": [{"attributes": {"POSITION": 2}, "indices": 3, "material": 1, "mode": 4}]},
        ],
        "materials": [
            {"name": "Block body", "doubleSided": False, "alphaMode": "OPAQUE",
             "pbrMetallicRoughness": {"baseColorFactor": [0.78, 0.30, 0.22, 1.0], "metallicFactor": 0.0, "roughnessFactor": 0.72}},
            {"name": "Corner tab", "doubleSided": False, "alphaMode": "OPAQUE",
             "pbrMetallicRoughness": {"baseColorFactor": [0.29, 0.52, 0.72, 1.0], "metallicFactor": 0.1, "roughnessFactor": 0.45}},
        ],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": 8, "type": "VEC3", "min": body_min, "max": body_max},
            {"bufferView": 1, "componentType": 5123, "count": 36, "type": "SCALAR"},
            {"bufferView": 2, "componentType": 5126, "count": 8, "type": "VEC3", "min": tab_min, "max": tab_max},
            {"bufferView": 3, "componentType": 5123, "count": 36, "type": "SCALAR"},
        ],
        "bufferViews": [
            {"buffer": 0, "byteOffset": offsets[0], "byteLength": len(body_positions), "target": 34962},
            {"buffer": 0, "byteOffset": offsets[1], "byteLength": len(body_indices), "target": 34963},
            {"buffer": 0, "byteOffset": offsets[2], "byteLength": len(tab_positions), "target": 34962},
            {"buffer": 0, "byteOffset": offsets[3], "byteLength": len(tab_indices), "target": 34963},
        ],
        "buffers": [{"byteLength": len(binary)}],
    }

    json_chunk = pad(json.dumps(document, separators=(",", ":")).encode("utf-8"), b" ")
    total = 12 + 8 + len(json_chunk) + 8 + len(binary)
    glb = struct.pack("<III", 0x46546C67, 2, total)
    glb += struct.pack("<II", len(json_chunk), 0x4E4F534A) + json_chunk
    glb += struct.pack("<II", len(binary), 0x004E4942) + binary

    output.write_bytes(glb)
    extent = [round(max(body_size[axis], tab_at[axis] + tab_size[axis]), 4) for axis in range(3)]
    print(f"{output.name}: {len(glb)} bytes, bounds (0, 0, 0) to {tuple(extent)}")


def main() -> None:
    # The primary fixture, and a second model with different extents so a test
    # that switches between them can tell they really swapped.
    build(HERE / "asymmetric-block.glb", (2.0, 1.0, 0.5), (0.25, 0.25, 0.25), (1.75, 0.75, 0.5), "Asymmetric block")
    build(HERE / "asymmetric-post.glb", (1.0, 2.0, 0.25), (0.2, 0.2, 0.2), (0.8, 1.8, 0.25), "Asymmetric post")


if __name__ == "__main__":
    main()
