# Color and local scene lighting

The Scene workspace's **Lighting → Color and local lights** controls save the
key, ambient and background colors, exposure, filmic response and floor-grid
visibility. Add named point lights for candles, hearths, lanterns and window
fill. Each has a position in metres, color, brightness and reach. Up to twelve
local lights are supported; at most two cast shadows to bound browser cost.

These are ordinary scene controls, available without an agent. They travel with
saved scenes, frozen shot snapshots and editable project packages. Existing
scenes retain their previous lighting defaults. Rendering a still uses the same
viewport and settings; it creates a working candidate, never an approval.

Scene saves validate colors, finite coordinates, exposure and light limits
atomically. Package import applies the same validation before storing embedded
lighting JSON. Migration v19 adds nullable scene environment JSON, preserving
the legacy columns for existing scenes and creating the normal migration backup.

The renderer uses inverse-square point lights, optional shadow maps and ACES
filmic tone mapping. These controls support local art direction; they do not
make a generated asset photoreal or replace character/material review.

Validation includes the scene save/restart API test, valid and invalid portable
package round-trips, ordinary browser save/reopen controls, and the scene still
rendering journey. The reference-conditioned tavern exercise also checked real
textured architecture, modular furnishings and two independently skinned rigs.
All geometry, material, rig and payload changes belong to Reference Asset
Compiler; see [the integration boundary](REFERENCE_ASSET_COMPILER.md).
