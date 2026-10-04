# Shipping to a game: the Framewright bundle

Framewright is where assets are put together and reviewed before they go into a
game. **Ship to game** hands the approved ones over as an engine-neutral
*bundle*: the asset files as they are in the library, plus a
`framewright-bundle.json` manifest that says what each file is, how big it
really is, and who approved it. Framewright knows nothing about any engine; an
importer (an Unreal editor script, a Godot plugin, a Unity `AssetPostprocessor`,
a build step) reads the bundle on its own terms.

## Shipping from the library

- **A collection:** open the collection and press **Ship collection**.
- **A selection:** press **Select**, tick the cards to send, then **Ship to game**.

The dialog shows what will ship and what will stay behind, and why, before
anything is written:

- Only **Approved** revisions ship by default.
- **Include pending work** also ships revisions nobody has judged yet.
- A revision that was **sent back** (Changes requested) never ships, and neither
  does an archived one or one whose stored file is missing.
- Each asset ships as its family's **current revision**. Naming an older
  revision in a selection ships the current one.

Then choose a destination: a game folder configured on this workstation, or
**Download a .zip** with the same contents.

## Configuring destinations

Destinations live in workstation configuration only. The browser can pick a
destination by name, but it can never supply a path. Add them to
`appsettings.Local.json` beside Framewright (it is never committed or packaged):

```json
{
  "Integrations": {
    "Shipping": {
      "Targets": [
        { "Name": "My game", "Path": "/path/to/MyGame/Content/Framewright" }
      ]
    }
  }
}
```

The same can be set with environment variables, e.g.
`Integrations__Shipping__Targets__0__Name` and
`Integrations__Shipping__Targets__0__Path`. A `Path` must be a full path to a
folder that already exists; Framewright never creates the destination itself,
so a typo is reported rather than turned into a stray folder. With no targets
configured, shipping offers the zip download only.
`appsettings.Local.example.json` shows the shape with a blank entry; blank
entries are ignored until both fields are filled in.

## Never written over

Every shipment is a **new folder**:

```
<target>/<collection-name-or-"selection">/<UTC time, e.g. 2026-10-04T15-40-11Z>/
    framewright-bundle.json
    files/models/floor-brazier-1a2b3c4d.glb
    files/images/tavern-sign-9f8e7d6c.png
    files/audio/hearth-loop-0011aabb.wav
    files/video/...
```

The folder name sorts by time (two shipments in the same second get `-2`,
`-3`, ...). Earlier shipments are never touched. The bundle is built in a hidden
`.partial-<id>` folder beside its final name and moved into place whole, and
the manifest is written last, so an importer that waits for
`framewright-bundle.json` never sees half a bundle. A zip download has the same
contents at its root.

Framewright keeps its own receipt of each shipment in its audit log
(`AssetsShipped`): the shipment id, destination, folder, and the revision and
content hash of everything shipped or skipped.

## Manifest schema `framewright.bundle.v1`

```jsonc
{
  "schema": "framewright.bundle.v1",
  "shipmentId": "a6b1…",                       // unique per shipment
  "createdAt": "2026-10-04T15:40:11.25+00:00",
  "generator": { "name": "Framewright", "version": "…", "commit": "…" },
  "project": { "id": "…", "name": "The Aether Wars" },
  "source": { "kind": "collection", "collectionId": "…", "name": "Tavern props" },
  "includePending": false,
  "units": { "length": "metres", "up": "+Y", "modelFormat": "glTF 2.0 binary (.glb)" },
  "items": [
    {
      "assetId": "…",            // stable across revisions: see below
      "revisionId": "…",         // the exact revision shipped
      "revisionNumber": 2,
      "displayName": "Floor brazier",
      "kind": "Model",           // Image | Model | Video | Audio
      "collection": "Tavern props",
      "file": "files/models/floor-brazier-1a2b3c4d.glb",   // relative, forward slashes
      "mimeType": "model/gltf-binary",
      "bytes": 1843200,
      "sha256": "1a2b3c4d…",     // of the file in this bundle
      "dimensionsMetres": [0.9, 1.2, 0.9],  // models: measured bounding box, x y z
      "triangleCount": 9800,                 // models
      "widthPixels": 2048, "heightPixels": 2048,  // images and video, when known
      "durationSeconds": 12.5,                     // audio and video, when known
      "tags": ["prop", "fire"],
      "notes": "Lit at night.",
      "review": { "decision": "Approved", "note": "Ready for the tavern.", "decidedAt": "…" }
    }
  ],
  "skipped": [
    { "assetId": "…", "revisionId": "…", "displayName": "Weak glow", "kind": "Image",
      "reason": "Sent back: The glow reads weak." }
  ]
}
```

Fields that do not apply to a kind are omitted (a model has no `widthPixels`; an
image has no `triangleCount`). Reasons in `skipped` are `Pending review`,
`Sent back[: reason]`, `Archived`, or `Its stored file is missing`.

### Identity across shipments

`assetId` is the id of the asset's **first revision**: the id it had before it
was ever revised. It stays the same for every later revision, so an importer
can **update** the game asset it made last time instead of creating a
duplicate. `revisionId` changes with each new revision; `sha256` changes with
the bytes.

### Units and coordinates

Models are self-contained GLB in metres with +Y up, the same convention the
library uses (see [3D conventions](3D_CONVENTIONS.md)). `dimensionsMetres` is
the axis-aligned bounding box measured from the shipped file. Convert to the
engine's units and up-axis once, on import (for example Unreal uses
centimetres and +Z up).

## Writing an importer

1. Find the newest bundle folder (folder names sort by time), or watch for a
   new `framewright-bundle.json`.
2. Check `schema` is `framewright.bundle.v1`; refuse versions you do not know.
3. For each item, verify the file's SHA-256 against `sha256` before importing.
4. Key game assets by `assetId`; record `revisionId` and `sha256` to skip work
   that has not changed.
5. Use `kind`, `dimensionsMetres`, `tags` and `collection` to place things (for
   example, into a folder per collection).

The bundle is plain files and JSON, so an importer needs no Framewright code or
connection.
