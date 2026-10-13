# Original prototype vehicles

These fictional low-poly vehicles were created for Dead Ground without third-party models or textures.
They are blockouts for testing silhouette readability, not historical replicas or final art.

- `tank.glb`: 1,936 triangles, 14 mesh surfaces, tracked hull and separate turret/gun pivots.
- `recon.glb`: 920 triangles, 10 mesh surfaces, wheeled hull and separate optical head.
- `visuals.json`: client presentation mapping keyed by the content unit ID, with initial visual scale 0.75.
- `manifest.json`: generated geometry statistics and coordinate conventions.

Regenerate the GLB files from the repository root with `python3 tools/model-authoring/build_vehicles.py`.
The Python source requires only the standard library. Static parts are combined by material within
each functional pivot, so tread pads and other small details do not each require a mesh surface.

Source coordinates use meters, +Y up and -Z forward, with the ground plane at Y=0. The tank retains
`TurretYaw`, `GunPitch`, `BarrelRecoil` and `Muzzle` nodes. The recon retains `SensorYaw`.
Materials are flat colors with no external texture dependencies.

Only presentation transforms are inferred from client observations. Mesh scale does not modify
server collision, navigation, detection or weapon profiles. Vehicle facing is inferred from movement;
the current protocol does not replicate an authoritative hull heading. Wheels and tracks are static.

Use close, normal and distant camera views to judge readability. Import-generated mesh LOD is enabled
by default, but these assets have not been profiled as a large-battle rendering workload.
