"""Builds a prop dense enough that reducing it means something.

Run from the repository root:

    python fixtures/glb/build_dense_prop.py

The other fixtures are two dozen triangles each, which is right for testing
bounds, nodes and rigs and useless for testing preparation: a runtime budget is
at least a thousand triangles, so no budget can ever be smaller than a block.

This is a heightfield -- a wavy surface on a grid -- because it tessellates to
any resolution from the same expression, so the pair below really are the same
shape at two densities rather than two unrelated meshes:

    dense-prop.glb           50 x 50 cells   5,000 triangles
    dense-prop-runtime.glb   20 x 20 cells     800 triangles

    both bounded (0, 0, 0) to (1.20, 0.35, 0.80)

The wave is asymmetric in X and Y, so a mirrored or rotated import cannot pass
by accident, and the surface carries UVs because preserving them is the whole
point of the route these fixtures exist for.

Authored for this repository; they carry the repository's own licence and
contain no third-party asset.
"""

import json
import math
import pathlib
import struct

HERE = pathlib.Path(__file__).parent

WIDTH_M = 1.20
DEPTH_M = 0.80
HEIGHT_M = 0.35


def height(u: float, v: float) -> float:
    """A ridge that is not the same in either direction, at 0..1 of the extent."""
    return HEIGHT_M * (
        0.55 * math.sin(math.pi * u * 1.5)
        + 0.30 * math.sin(math.pi * v * 2.5 + 0.7)
        + 0.15 * math.sin(math.pi * (u + v) * 3.5)
    ) * 0.5 + HEIGHT_M * 0.5


def surface(cells: int):
    positions = []
    uvs = []
    for row in range(cells + 1):
        for column in range(cells + 1):
            u = column / cells
            v = row / cells
            positions.append((u * WIDTH_M, height(u, v), v * DEPTH_M))
            uvs.append((u, v))
    faces = []
    stride = cells + 1
    for row in range(cells):
        for column in range(cells):
            a = row * stride + column
            faces.append((a, a + stride, a + 1))
            faces.append((a + 1, a + stride, a + stride + 1))
    return positions, uvs, faces


def pad(data: bytes, filler: bytes) -> bytes:
    remainder = len(data) % 4
    return data if remainder == 0 else data + filler * (4 - remainder)


def build(output: pathlib.Path, cells: int, scene_name: str) -> None:
    positions, uvs, faces = surface(cells)
    position_bytes = b"".join(struct.pack("<3f", *point) for point in positions)
    uv_bytes = b"".join(struct.pack("<2f", *point) for point in uvs)
    # Unsigned int indices: a 50-cell grid has 2,601 vertices, which still fits
    # a short, but the component type is chosen once for both densities so the
    # pair differ in geometry and nothing else.
    index_bytes = b"".join(struct.pack("<3I", *face) for face in faces)

    binary = b""
    offsets = []
    for chunk in (position_bytes, uv_bytes, index_bytes):
        binary = pad(binary, b"\x00")
        offsets.append(len(binary))
        binary += chunk
    binary = pad(binary, b"\x00")

    minimum = [min(point[axis] for point in positions) for axis in range(3)]
    maximum = [max(point[axis] for point in positions) for axis in range(3)]

    document = {
        "asset": {"version": "2.0", "generator": "Framewright fixture builder"},
        "scene": 0,
        "scenes": [{"name": scene_name, "nodes": [0]}],
        "nodes": [{"name": "Ridge", "mesh": 0}],
        "meshes": [{
            "name": "Ridge",
            "primitives": [{
                "attributes": {"POSITION": 0, "TEXCOORD_0": 1},
                "indices": 2, "material": 0, "mode": 4,
            }],
        }],
        "materials": [{
            "name": "Ridge surface", "doubleSided": False, "alphaMode": "OPAQUE",
            "pbrMetallicRoughness": {
                "baseColorFactor": [0.62, 0.55, 0.42, 1.0],
                "metallicFactor": 0.0, "roughnessFactor": 0.65,
            },
        }],
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": len(positions), "type": "VEC3",
             "min": minimum, "max": maximum},
            {"bufferView": 1, "componentType": 5126, "count": len(uvs), "type": "VEC2"},
            {"bufferView": 2, "componentType": 5125, "count": len(faces) * 3, "type": "SCALAR"},
        ],
        "bufferViews": [
            {"buffer": 0, "byteOffset": offsets[0], "byteLength": len(position_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": offsets[1], "byteLength": len(uv_bytes), "target": 34962},
            {"buffer": 0, "byteOffset": offsets[2], "byteLength": len(index_bytes), "target": 34963},
        ],
        "buffers": [{"byteLength": len(binary)}],
    }

    json_chunk = pad(json.dumps(document, separators=(",", ":")).encode("utf-8"), b" ")
    total = 12 + 8 + len(json_chunk) + 8 + len(binary)
    glb = struct.pack("<III", 0x46546C67, 2, total)
    glb += struct.pack("<II", len(json_chunk), 0x4E4F534A) + json_chunk
    glb += struct.pack("<II", len(binary), 0x004E4942) + binary

    output.write_bytes(glb)
    print(f"{output.name}: {len(glb)} bytes, {len(faces)} triangles, {len(positions)} vertices")


def main() -> None:
    build(HERE / "dense-prop.glb", 50, "Dense prop")
    build(HERE / "dense-prop-runtime.glb", 20, "Dense prop runtime")


if __name__ == "__main__":
    main()
