# Terrain & Road PBR Texture Pipeline

This directory contains the reproducible source tiles (`sources/*.png`, 640x640 RGB) and the deterministic tile baker (`process_pbr_tiles.py`) that produces all runtime textures in `src/Game.Godot/Assets/Terrain/`.

## Rebuilding Runtime Textures

Requirements: Python 3 with `numpy` and `Pillow`.

```bash
python3 tools/terrain-textures/process_pbr_tiles.py --self-test
python3 tools/terrain-textures/process_pbr_tiles.py \
  tools/terrain-textures/sources \
  src/Game.Godot/Assets/Terrain
```

After rebuilding, re-import changed textures in Godot (or run `./tools/run-client.sh --headless -- --smoke-test`).

## Source Tile Inventory (640x640 RGB)

| File | SHA-256 | Notes |
| :--- | :--- | :--- |
| `sources/meadow_grass_raw.png` | `10227d114d69c8e65a90e5997807a3748fff24594bc504bba1fcb05f303725b2` | Top-down overcast European meadow grass base |
| `sources/dry_field_raw.png` | `b1bd144e2c6ae5c41a34b41c20e08d9d18470e2dd80cd14149111d7add003065` | Top-down dry autumn fallow grass/stalks base |
| `sources/slope_soil_raw.png` | `c2fcf573494eaf551198d19b0cc8606020085ae0ec9e59e6d42c9bb4a02fbf09` | Top-down damp clay-loam soil and riverbank pebbles base |
| `sources/asphalt_raw.png` | `5b06f49dbfc39d8e6a1e0ed894eb268354ac2a6eb34009e0edd54dc962a97c92` | Top-down weathered 1980s cracked highway asphalt base |
| `sources/gravel_raw.png` | `80684d38ab878fb2e9035faa6bdba3499918e2ff06459e13a7799aceacc17ab8` | Top-down compacted rural gravel and shoulder base |

## Guarantees Enforced by `process_pbr_tiles.py`

- **Exact partition-of-unity overlap blending (`make_seamless_overlap`):** uses the genuine 128 px continuation margins of each 640x640 source to cross-fade the left and top edges of the 512x512 output (`w + (1 - w) == 1` everywhere). Preserves flat constant fields exactly (zero dark outlier pixels) and avoids artificial half-tile duplication.
- **Structural high-pass normals (`structural_height_from_albedo`):** uses periodic FFT multi-band high-pass filtering so broad albedo color shifts do not produce false pillowy bumps.
- **Deterministic utility maps:** generates periodic `water_normal.png`, `macro_noise.png`, and `rain_streaks.png` with fixed RNG seeds.
