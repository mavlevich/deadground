# T-62 RTS asset

An original procedural T-62 visual study for Dead Ground. The direction is restrained
stylized realism: reference-based proportions, rounded cast-armor profiles, hard armor plate
edges, readable running gear and matte materials. There is no intentional pixel filter.
This is a gameplay presentation asset, not an engineering or museum reconstruction.

## Scale and articulation

Meters, +Y up, -Z forward. Root is centered on the hull footprint at ground level.
Import at scale 1. Hull length is 6.630 m, fender width 3.300 m and gun-forward length
from the rear armor plate is 9.335 m. External rear stowage is excluded from that
length. Cupola height is 2.395 m; antenna is excluded. The approximate internal
layout and minor fittings are simplified for RTS viewing.

```text
T62
|-- Hull
|-- LeftTrack
|-- RightTrack
`-- TurretYaw
    `-- GunPitch
        `-- BarrelRecoil
            `-- Muzzle
```

TurretYaw is at (0, 1.480, -0.380). GunPitch is at (0, 0.370, -1.050)
relative to it. Muzzle is at (0, 0, -4.590) relative to BarrelRecoil,
which puts it at (0, 1.850, -6.020) in the neutral model. Only the barrel
recoils along local +Z. Its cover and IR housing remain on the pitching mount.
Wheels, tracks and suspension are static.

## Materials and LOD

Colors are authored as sRGB hex, converted to linear glTF factors. Per-part
linear vertex tints separate lower armor, deck panels, turret and barrel without
creating another material for every tone. `import_materials.gd` enables these tints
at import, with sRGB conversion disabled for the linear vertex multipliers.
The Godot import script also adds subtle, seeded triplanar paint variation using a
256 x 256 procedural noise texture with mipmaps. A separate seeded texture varies
paint roughness. Height-dependent warm dust and small tonal differences are baked
into linear vertex colors, so their broad distribution also survives in other viewers.
No external textures or third-party meshes are required.
The close model adds a seeded 512 x 512 procedural cast-surface normal texture.
The RTS and distant materials omit this extra surface-relief sample.

| Asset | Triangles | Mesh surfaces | Materials |
| --- | ---: | ---: | ---: |
| t62.glb | 8392 | 17 | 5 |
| t62_lod1.glb | 4304 | 17 | 5 |
| t62_lod2.glb | 1820 | 11 | 3 |

LOD0 spends geometry on the rounded casting, dished wheels, toothed rear drive
sprocket, curved track runs, the rear roof case-ejection hatch and barrel.
The narrower armored hull leaves room for fender-mounted fuel cells. The three
right-side cells have a lower, longer outline, approximately 95 L of exterior box
volume per cell. Both removable rear drums sit behind the rear armor on brackets,
with transverse axes. The upright V-shaped wave deflector follows the glacis
rather than acting as a thick armor wedge.
The close version (LOD0) has five pressed depressions in each wheel dish,
regularly spaced track shoes, towing loops and low turret rails. The turret has
a wider roof and more upright cheeks around the gun opening. Thin front mudguards
follow a rolled profile with folded outer lips. These local profiles are estimates.
The RTS version (LOD1) keeps wheel dishes and the common silhouette while removing
pressed wheel details, fuel-cell caps/feet and individual track shoes. LOD2 simplifies
these parts while retaining the fuel cells and drums in its silhouette. Godot automatic
mesh LOD generation is disabled for these assets, which have three authored LODs
with matching axes, muzzle and physical extents. The manifest is generated
from actual geometry, without machine-specific paths. These are visual study budgets;
a large-battle GPU/draw-call validation remains a separate task. The supplied
concept sheet guides the shape and palette, not a claimed 620-polygon budget.

## Regeneration and preview

From the repository root:

```sh
python3 tools/model-authoring/build_t62.py
python3 -m unittest discover -s tools/model-authoring -p 'test_t62_*.py'
./tools/run-client.sh -- --vehicle-preview --vehicle-preview-variant close
./tools/run-client.sh -- --vehicle-preview --vehicle-preview-variant rts
./tools/run-client.sh --headless -- --vehicle-preview --smoke-test
```

Use `--output-dir <directory>` with the generator to verify reproducibility without
touching checked-in assets. On Windows use the PowerShell client launcher.

The preview starts with the T-62 alone on a neutral ground. It uses orthographic
front, side, rear, top and three-quarter cameras and gameplay camera sizes 38/75/160.

- C: cycle cameras.
- A/D: turret yaw. W/S: gun pitch. Space: fire test shot. R: restore bind pose.
- 1/2/3: close / RTS / distant detail. 4: automatic preview LOD by orthographic camera size.
- Tab: show legacy tank, recon and 1.8 m scale reference.
- G: show measured grid.

For native capture, add `--vehicle-preview-capture <output_directory>`.
Normal mission models and enemy recognition mappings are unchanged.

## Reference correction and layout checks

The operator manual's Figures 1-2 through 1-6 guide the external layout and its
General Vehicle table supplies the overall dimension anchors. Museum photographs
cross-check the casting, exposed fender fuel cells, drive wheel and track structure.
The additional multiview drawing is a secondary visual aid, not a dimensioned factory
blueprint. No factory section drawing or measured turret point cloud was available.
Turret curvature, hull station widths, wheel stations and the 1.85 m gun-axis height
remain estimates fitted to these references, not verified manufacturing dimensions.
The represented configuration omits the later roof AA mount and applique armor.
The casting and mantlet-cover shape were also checked against the
[Bovington turret photograph](https://commons.wikimedia.org/wiki/File:Bovington_Tank_Museum_Russian_T62_tank_turret.JPG).
Photographs are visual references only; no photographic pixels are included in the asset.
The latest concept sheet guides the proportions and restrained finish. Its small
side/front/rear views and hero illustration are not treated as calibrated drawings.
The fender height, cell aspect ratio and cheek shaping remain artistic estimates.
The finish uses procedural albedo, roughness and close-detail relief. It does not
include a hand-painted UV wear atlas, markings or photorealistic damage.
Wheel-face orientation and upward-facing mudguard sheets have regression checks.

Generator regression checks cover wheel separation, the track envelope around every
wheel, ground contact, fuel-cell clearance above the fenders and behind the rear armor.
These checks accompany visual inspection; they do not certify historical accuracy.

## References and approximations

- [US Army 203d Military Intelligence Battalion, Operator's Manual for T-62 Tank](https://curioandrelic.com/fieldmanuals/russian/T-62_Medium_Tank_Operators_Manual.pdf), Figures 1-2 through 1-6 and General Vehicle table, printed pages 1-3 through 1-6 and 1-14.
- [Free Denizen, right-front museum photograph](https://commons.wikimedia.org/wiki/File:Right_side_of_the_T-62_tank_at_the_Military_Vehicle_Technology_Foundation.jpg), consulted in browser, not redistributed.
- [Free Denizen, track and wheel photograph](https://commons.wikimedia.org/wiki/File:Tracks_of_the_T-62_tank_at_the_Military_Vehicle_Technology_Foundation.jpg), consulted in browser, not redistributed.
- [US Department of Defense rear photograph, DD-ST-85-01283](https://commons.wikimedia.org/wiki/File:Rear_view_of_a_T-62A.JPEG), consulted in browser, not redistributed.
- [Supplementary T-62 multiview drawing](https://drawingdatabase.com/t-62-tank/), consulted in browser, not redistributed.
- [Museum of Russian Military History, T-62 dimensions](https://www.kskdivniy.ru/museum/eksponaty/t-62/).
- [The Tank Museum, T-62 collection entry](https://tankmuseum.org/tank-nuts/tank-collection/t-62/).
- [glTF 2.0 color and vertex multiplier specification](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html).

The dimension anchors guide the main silhouette. Curvature, auxiliary equipment,
wear and internal layout are original simplified interpretations. No reference
photographs or drawings are redistributed as game textures.
