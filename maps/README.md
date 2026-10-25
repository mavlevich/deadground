# Maps

M0 creates a deterministic 128 x 128 m heightmap with `HeightMap.CreatePrototype`.
The host and the client use the same immutable samples.
Exported map packages and structure decks are added in M1.

The approved 5000 x 5000 m map schematic lives in `docs/maps/ustye/layout.json`
with authoring tools in `tools/map-authoring/`.
A 3D visual preview mode in Godot can be inspected via `./tools/run-client.sh -- --map-preview ustye`.
