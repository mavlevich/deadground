"""Procedural CH-47D Chinook heavy-lift transport helicopter for Dead Ground (Iteration 3, 3 LODs).

Calibrated against official Boeing CH-47D / US Army TM 55-1520-240-10 dimensions:
- Fuselage length:       15.54 m  (nose tip to rear pylon trailing edge)
- Width over sponsons:    3.78 m  (cabin width 2.52 m)
- Overall height:         5.77 m  (to top of rear rotor hub; front hub 4.72 m)
- Belly ground clearance: 0.72 m  (squat heavy-lift stance)
- Rotor diameter (x2):   18.29 m  (3 blades, 0.813 m chord, 11.94 m hub spacing)
- Forward / Rear tilt:    9.0 deg / 4.0 deg forward
- Wheelbase / Track:      7.87 m wheelbase, 3.20 m front dual track, 3.56 m aft track

Iteration 3 Showcase Upgrades:
1. Smooth integral radome nose & larger thin-mullion wrap-around "aquarium" cockpit glazing
2. Aerodynamic airfoil-section rear pylon & real hollow openable rear cargo bay with interior
   floor, vehicle treadways, folding troop benches, and ground-reaching hinged RearRamp
3. Frame-aligned aerospace skin UVs + seamless organic CARC Olive Drab PBR texture (no stray grid
   lines on curved cones), baked vertex AO, transmission oil streaks & T55 exhaust soot (COLOR_0)
4. Streamlined forward pylon + detailed T55-GA-714A engine pods with beveled EAPS intake cowlings,
   inlet screens, and extended metallic exhaust diffusers with heat shields
5. Game-ready RTS flight kit: translucent radial motion-blur rotor discs (FrontRotorBlur /
   RearRotorBlur) with yellow tip warning rings + twin M60D 7.62mm pintle door/window machine guns

Articulated Godot Node Hierarchy (CH47):
    CH47
    |-- Fuselage         (hollow rear bay + cabin interior, airframe, T55 pods, M60D guns, gear, decals)
    |-- FrontRotor       (3D rotor head & blades, pivot (0, 4.72, -5.09), tilt 9 deg fwd)
    |-- RearRotor        (3D rotor head & blades, pivot (0, 5.77, +6.85), tilt 4 deg fwd)
    |-- FrontRotorBlur   (translucent flight motion-blur disc, pivot (0, 4.72, -5.09), tilt 9 deg fwd)
    |-- RearRotorBlur    (translucent flight motion-blur disc, pivot (0, 5.77, +6.85), tilt 4 deg fwd)
    `-- RearRamp         (hinged cargo ramp, pivot (0, 0.78, +3.55); rotate +42 deg local X to open)

Run with:
    blender --background --python build_ch47.py -- --render-dir <optional_dir> --compare-lineup
"""

import argparse
import json
import math
import sys
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Euler, Matrix, Vector

# ---------------------------------------------------------------------------
# Official Boeing CH-47D Chinook Reference Dimensions (meters)
# Blender space: +X starboard, +Y forward (nose), +Z up.
# Exported glTF (+Y up): (X, Y, Z) -> (X, Z, -Y) => Dead Ground +Y up, -Z forward.
# ---------------------------------------------------------------------------

FUSELAGE_LENGTH = 15.54        # +7.77 nose tip to -7.77 aft pylon trailing edge
CABIN_WIDTH = 2.52
SPONSON_WIDTH = 3.78           # Official width across side fuel sponsons (12 ft 5 in)
BELLY_Z = 0.72
ROOF_Z = 3.08

ROTOR_RADIUS = 9.145           # 18.29 m (60 ft) diameter
BLADE_CHORD = 0.813            # 32 in chord
BLADE_THICK = 0.088

FRONT_HUB = Vector((0.0, 5.09, 4.72))
FRONT_MAST_TILT_DEG = 9.0

REAR_HUB = Vector((0.0, -6.85, 5.77))   # Hub spacing = 11.94 m (39 ft 1 in)
REAR_MAST_TILT_DEG = 4.0

FRONT_GEAR_Y = 3.67            # Wheelbase = 7.87 m (25 ft 10 in)
REAR_GEAR_Y = -4.20
FRONT_GEAR_X = 1.60            # Front dual-wheel track = 3.20 m
REAR_GEAR_X = 1.78             # Aft cantilever swivel track = 3.56 m
FRONT_TIRE_R = 0.285
REAR_TIRE_R = 0.235

BAY_HALF_W = 0.96              # Interior rear cargo bay width = 1.92 m
BAY_FLOOR_Z = 0.80
BAY_CEIL_Z = 2.46
RAMP_HINGE = Vector((0.0, -3.55, 0.78))
RAMP_CLOSED_PITCH_DEG = 24.5
RAMP_OPEN_SWING_DEG = 42.0     # Rotate local +X by +42 deg to touch ground at Z = 0

DEFAULT_OUT_DIR = Path(__file__).resolve().parents[2] / "src/Game.Godot/Assets/Vehicles/CH47"
DEFAULT_BTR70_GLB = Path(__file__).resolve().parents[2] / "src/Game.Godot/Assets/Vehicles/BTR70/btr70.glb"
DEFAULT_M60_GLB = Path(__file__).resolve().parents[2] / "src/Game.Godot/Assets/Vehicles/M60/m60.glb"


# ---------------------------------------------------------------------------
# Procedural Textures (Seamless CARC Skin, Stencil Decal Atlas, Rotor Blur Disc)
# ---------------------------------------------------------------------------


def srgb_hex_to_linear(hex_str: str) -> tuple[float, float, float, float]:
    h = hex_str.lstrip("#")
    vals = [int(h[i : i + 2], 16) / 255.0 for i in (0, 2, 4)]
    lin = [c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4 for c in vals]
    return (lin[0], lin[1], lin[2], 1.0)


def _value_noise_2d(h: int, w: int, grid_y: int, grid_x: int, seed: int) -> np.ndarray:
    rng = np.random.default_rng(seed)
    g = rng.random((grid_y, grid_x), dtype=np.float32)
    yy = np.linspace(0.0, grid_y, h, endpoint=False, dtype=np.float32)
    xx = np.linspace(0.0, grid_x, w, endpoint=False, dtype=np.float32)
    y0 = np.floor(yy).astype(np.int32) % grid_y
    x0 = np.floor(xx).astype(np.int32) % grid_x
    y1 = (y0 + 1) % grid_y
    x1 = (x0 + 1) % grid_x
    ty = yy - np.floor(yy)
    tx = xx - np.floor(xx)
    ty = ty * ty * (3.0 - 2.0 * ty)
    tx = tx * tx * (3.0 - 2.0 * tx)
    v00 = g[y0[:, None], x0[None, :]]
    v10 = g[y0[:, None], x1[None, :]]
    v01 = g[y1[:, None], x0[None, :]]
    v11 = g[y1[:, None], x1[None, :]]
    top = v00 * (1.0 - tx[None, :]) + v10 * tx[None, :]
    bot = v01 * (1.0 - tx[None, :]) + v11 * tx[None, :]
    return top * (1.0 - ty[:, None]) + bot * ty[:, None]


def generate_carc_skin_images(size: int = 512) -> tuple[bpy.types.Image, bpy.types.Image]:
    """Generate clean weathered US Army CARC Olive Drab PBR maps (albedo + roughness).

    Frame-aligned UV layout: U runs along fuselage length (Y), V runs around/vertical (Z).
    Includes ultra-subtle horizontal stringer seams & vertical frame seams that fade smoothly
    without creating distracting dark grid lines on curved cones.
    """
    n_coarse = _value_noise_2d(size, size, 6, 6, seed=4711)
    n_mid = _value_noise_2d(size, size, 18, 18, seed=4712)
    n_fine = _value_noise_2d(size, size, 80, 80, seed=4713)
    # Subtle vertical rain/oil grime streaks along V
    n_streak = _value_noise_2d(size, size, 4, 48, seed=4714)

    mottle = 0.56 * n_coarse + 0.30 * n_mid + 0.14 * n_fine
    streaks = (n_streak - 0.5) * 0.045

    # Whisper-fine sheet-metal seams (only 6-7% darkening on 1px seam)
    yy, xx = np.mgrid[0:size, 0:size]
    frame_step = size // 4
    stringer_step = size // 3
    dx = np.minimum(xx % frame_step, frame_step - (xx % frame_step))
    dy = np.minimum(yy % stringer_step, stringer_step - (yy % stringer_step))
    seam_dist = np.minimum(dx, dy)
    seam_dark = np.where(seam_dist == 0, 0.925, np.where(seam_dist == 1, 0.97, 1.0)).astype(np.float32)

    # Very subtle per-sheet tonal variation
    rng = np.random.default_rng(4719)
    panel_tint = rng.uniform(0.982, 1.018, size=(3, 4)).astype(np.float32)
    p_shade = panel_tint[(yy // stringer_step) % 3, (xx // frame_step) % 4]

    # US Army CARC Olive Drab sRGB tuned right alongside Dead Ground M60 (#51553e)
    base_srgb = np.array([0x50 / 255.0, 0x54 / 255.0, 0x3E / 255.0], dtype=np.float32)
    factor = np.clip((0.935 + 0.13 * mottle + streaks) * p_shade * seam_dark, 0.78, 1.09)
    rgb = np.clip(base_srgb[None, None, :] * factor[:, :, None], 0.0, 1.0)

    # Subtle warm dust highlights
    dust = np.clip((mottle - 0.64) * 0.12, 0.0, 0.045)
    rgb[:, :, 0] = np.clip(rgb[:, :, 0] + dust * 0.75, 0.0, 1.0)
    rgb[:, :, 1] = np.clip(rgb[:, :, 1] + dust * 0.65, 0.0, 1.0)
    rgb[:, :, 2] = np.clip(rgb[:, :, 2] + dust * 0.32, 0.0, 1.0)

    rgba = np.ones((size, size, 4), dtype=np.float32)
    rgba[:, :, :3] = rgb

    albedo_img = bpy.data.images.new("CH47_CARC_Albedo", width=size, height=size, alpha=False)
    albedo_img.colorspace_settings.name = "sRGB"
    albedo_img.pixels.foreach_set(rgba.ravel())
    albedo_img.pack()

    rough = np.clip(0.76 + 0.12 * (1.0 - mottle) + (1.0 - seam_dark) * 0.4, 0.66, 0.94)
    r_rgba = np.ones((size, size, 4), dtype=np.float32)
    r_rgba[:, :, 0] = rough
    r_rgba[:, :, 1] = rough
    r_rgba[:, :, 2] = rough
    rough_img = bpy.data.images.new("CH47_CARC_Roughness", width=size, height=size, alpha=False)
    rough_img.colorspace_settings.name = "Non-Color"
    rough_img.pixels.foreach_set(r_rgba.ravel())
    rough_img.pack()
    return albedo_img, rough_img


def generate_rotor_blur_image(size: int = 512) -> bpy.types.Image:
    """Create radial translucent motion-blur disc texture for airborne RTS rotors."""
    yy, xx = np.mgrid[0:size, 0:size]
    cx = (xx + 0.5) / size * 2.0 - 1.0
    cy = (yy + 0.5) / size * 2.0 - 1.0
    r = np.hypot(cx, cy)
    theta = np.arctan2(cy, cx)

    # 3-blade soft sweeping directional blur waves
    wave = 0.55 + 0.45 * np.cos(3.0 * theta + r * 0.85) ** 2
    # Radial envelope: open hub center (r < 0.11), smooth feathered outer edge (r = 1.0)
    inner_mask = np.clip((r - 0.10) / 0.05, 0.0, 1.0)
    outer_mask = np.clip((1.0 - r) / 0.03, 0.0, 1.0)
    radial = inner_mask * outer_mask

    # Crisp high-vis yellow warning tip ring (matches 3D blade tip band at r ~ 0.90..0.965)
    tip_ring = np.exp(-((r - 0.935) ** 2) / (0.022 ** 2))
    # Subtle silver metallic leading-edge sheen ring
    mid_sheen = np.exp(-((r - 0.58) ** 2) / (0.28 ** 2)) * 0.06

    # Color: dark composite charcoal + bright tactical yellow outer tip stripe
    col_dark = np.array([0.11, 0.12, 0.10], dtype=np.float32)
    col_yel = np.array([0.86, 0.68, 0.14], dtype=np.float32)

    mix_y = np.clip(tip_ring * 1.15, 0.0, 1.0)
    rgb = col_dark[None, None, :] * (1.0 - mix_y[:, :, None]) + col_yel[None, None, :] * mix_y[:, :, None]
    rgb = np.clip(rgb + mid_sheen[:, :, None], 0.0, 1.0)

    # Alpha: realistic translucent blur (0.24..0.44 mid-disc, 0.68 at yellow tip ring)
    alpha = radial * (0.24 * wave + 0.16 * (1.0 - r * 0.35) + 0.38 * tip_ring)
    alpha = np.clip(alpha, 0.0, 0.78)

    buf = np.zeros((size, size, 4), dtype=np.float32)
    buf[:, :, :3] = rgb
    buf[:, :, 3] = alpha

    img = bpy.data.images.new("CH47_RotorBlur", width=size, height=size, alpha=True)
    img.colorspace_settings.name = "sRGB"
    img.pixels.foreach_set(buf.ravel())
    img.pack()
    return img


_FONT_5X7 = {
    "A": ["01110", "10001", "10001", "11111", "10001", "10001", "10001"],
    "D": ["11110", "10001", "10001", "10001", "10001", "10001", "11110"],
    "E": ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
    "G": ["01110", "10001", "10000", "10111", "10001", "10001", "01110"],
    "I": ["11111", "00100", "00100", "00100", "00100", "00100", "11111"],
    "M": ["10001", "11011", "10101", "10101", "10001", "10001", "10001"],
    "N": ["10001", "11001", "10101", "10011", "10001", "10001", "10001"],
    "R": ["11110", "10001", "10001", "11110", "10100", "10010", "10001"],
    "S": ["01111", "10000", "10000", "01110", "00001", "00001", "11110"],
    "T": ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
    "U": ["10001", "10001", "10001", "10001", "10001", "10001", "01110"],
    "Y": ["10001", "10001", "01010", "00100", "00100", "00100", "00100"],
    "0": ["01110", "10011", "10101", "10101", "11001", "10001", "01110"],
    "1": ["00100", "01100", "00100", "00100", "00100", "00100", "01110"],
    "2": ["01110", "10001", "00001", "00110", "01000", "10000", "11111"],
    "4": ["00010", "00110", "01010", "10010", "11111", "00010", "00010"],
    "6": ["01110", "10000", "11110", "10001", "10001", "10001", "01110"],
    "8": ["01110", "10001", "10001", "01110", "10001", "10001", "01110"],
    "-": ["00000", "00000", "00000", "11111", "00000", "00000", "00000"],
    ".": ["00000", "00000", "00000", "00000", "00000", "01100", "01100"],
    " ": ["00000", "00000", "00000", "00000", "00000", "00000", "00000"],
}


def _stamp_text(buf: np.ndarray, text: str, x0: int, y0: int, scale: int, rgba):
    h, w, _ = buf.shape
    cursor = x0
    for ch in text:
        glyph = _FONT_5X7.get(ch.upper(), _FONT_5X7[" "])
        for row_idx, row in enumerate(glyph):
            py0 = y0 + (6 - row_idx) * scale
            for col_idx, bit in enumerate(row):
                if bit == "1":
                    px0 = cursor + col_idx * scale
                    buf[max(0, py0) : min(h, py0 + scale), max(0, px0) : min(w, px0 + scale)] = rgba
        cursor += 6 * scale


def _stamp_star_and_bars(buf: np.ndarray, cx: int, cy: int, r_outer: int, rgba):
    h, w, _ = buf.shape
    yy, xx = np.mgrid[0:h, 0:w]
    bar_h = int(r_outer * 0.34)
    bar_w = int(r_outer * 2.15)
    bars = (np.abs(yy - cy) <= bar_h) & (np.abs(xx - cx) >= int(r_outer * 0.92)) & (np.abs(xx - cx) <= bar_w)
    buf[bars] = rgba
    dist = np.hypot(xx - cx, yy - cy)
    buf[(dist >= r_outer * 0.88) & (dist <= r_outer * 1.04)] = rgba
    ang = np.arctan2(yy - cy, xx - cx) - math.pi * 0.5
    sector = (ang % (2.0 * math.pi / 5.0)) - (math.pi / 5.0)
    r_inner = r_outer * 0.382
    a = np.abs(sector)
    r_edge = (r_outer * r_inner * math.sin(math.pi / 5.0)) / (
        r_inner * np.sin(math.pi / 5.0 - a) + r_outer * np.sin(a) + 1e-6
    )
    buf[dist <= r_edge * 0.86] = rgba


def generate_decal_atlas() -> bpy.types.Image:
    """1024x512 Cold War US Army faded tactical off-white stencil decal atlas."""
    w, h = 1024, 512
    buf = np.zeros((h, w, 4), dtype=np.float32)

    ink_white = (0.86, 0.84, 0.76, 0.90)
    ink_warn = (0.72, 0.16, 0.12, 0.92)
    ink_yel = (0.84, 0.68, 0.16, 0.92)

    _stamp_text(buf, "UNITED STATES ARMY", 80, 340, 8, ink_white)
    _stamp_text(buf, "U.S. ARMY", 65, 145, 7, ink_white)
    _stamp_text(buf, "84-24162", 78, 52, 7, ink_white)
    _stamp_star_and_bars(buf, 635, 128, 52, ink_white)

    buf[175:195, 795:1005] = ink_yel
    buf[70:90, 795:1005] = ink_warn
    _stamp_text(buf, "DANGER", 806, 112, 5, ink_warn)

    chip = _value_noise_2d(h, w, 48, 96, seed=4799)
    buf[:, :, 3] *= np.clip(0.84 + 0.25 * chip, 0.65, 1.0)

    img = bpy.data.images.new("CH47_DecalAtlas", width=w, height=h, alpha=True)
    img.colorspace_settings.name = "sRGB"
    img.pixels.foreach_set(buf.ravel())
    img.pack()
    return img


# ---------------------------------------------------------------------------
# Materials
# ---------------------------------------------------------------------------


def make_solid_material(
    name: str,
    hex_color: str,
    metallic: float = 0.08,
    roughness: float = 0.80,
    use_vcol: bool = True,
    emission_hex: str | None = None,
    emission_strength: float = 0.0,
) -> bpy.types.Material:
    mat = bpy.data.materials.new(name=name)
    if not mat.node_tree:
        mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    lin_col = srgb_hex_to_linear(hex_color)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    if emission_hex and "Emission Color" in bsdf.inputs:
        bsdf.inputs["Emission Color"].default_value = srgb_hex_to_linear(emission_hex)
        bsdf.inputs["Emission Strength"].default_value = emission_strength

    if use_vcol:
        vcol = nt.nodes.new("ShaderNodeVertexColor")
        vcol.layer_name = "Col"
        mix = nt.nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        mix.blend_type = "MULTIPLY"
        mix.inputs["Factor"].default_value = 1.0
        mix.inputs["A"].default_value = lin_col
        nt.links.new(vcol.outputs["Color"], mix.inputs["B"])
        nt.links.new(mix.outputs["Result"], bsdf.inputs["Base Color"])
    else:
        bsdf.inputs["Base Color"].default_value = lin_col
    return mat


def make_carc_material(
    name: str,
    albedo_img: bpy.types.Image,
    rough_img: bpy.types.Image,
    tint_mult: tuple[float, float, float] = (1.0, 1.0, 1.0),
    metallic: float = 0.07,
) -> bpy.types.Material:
    mat = bpy.data.materials.new(name=name)
    if not mat.node_tree:
        mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    bsdf.inputs["Metallic"].default_value = metallic

    tex_albedo = nt.nodes.new("ShaderNodeTexImage")
    tex_albedo.image = albedo_img
    tex_rough = nt.nodes.new("ShaderNodeTexImage")
    tex_rough.image = rough_img

    vcol = nt.nodes.new("ShaderNodeVertexColor")
    vcol.layer_name = "Col"

    mix_ao = nt.nodes.new("ShaderNodeMix")
    mix_ao.data_type = "RGBA"
    mix_ao.blend_type = "MULTIPLY"
    mix_ao.inputs["Factor"].default_value = 1.0
    nt.links.new(tex_albedo.outputs["Color"], mix_ao.inputs["A"])
    nt.links.new(vcol.outputs["Color"], mix_ao.inputs["B"])

    if tint_mult != (1.0, 1.0, 1.0):
        mix_tint = nt.nodes.new("ShaderNodeMix")
        mix_tint.data_type = "RGBA"
        mix_tint.blend_type = "MULTIPLY"
        mix_tint.inputs["Factor"].default_value = 1.0
        mix_tint.inputs["B"].default_value = (*tint_mult, 1.0)
        nt.links.new(mix_ao.outputs["Result"], mix_tint.inputs["A"])
        nt.links.new(mix_tint.outputs["Result"], bsdf.inputs["Base Color"])
    else:
        nt.links.new(mix_ao.outputs["Result"], bsdf.inputs["Base Color"])

    nt.links.new(tex_rough.outputs["Color"], bsdf.inputs["Roughness"])
    return mat


def make_alpha_texture_material(
    name: str, img: bpy.types.Image, roughness: float = 0.84, metallic: float = 0.04, double_sided: bool = False
) -> bpy.types.Material:
    mat = bpy.data.materials.new(name=name)
    if not mat.node_tree:
        mat.use_nodes = True
    mat.use_backface_culling = not double_sided
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.image = img
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    nt.links.new(tex.outputs["Alpha"], bsdf.inputs["Alpha"])
    return mat


def create_materials() -> dict[str, bpy.types.Material]:
    albedo_img, rough_img = generate_carc_skin_images(512)
    decal_img = generate_decal_atlas()
    blur_img = generate_rotor_blur_image(512)
    return {
        "paint": make_carc_material("CH47_Paint", albedo_img, rough_img, (1.0, 1.0, 1.0), 0.07),
        "paint_dark": make_carc_material("CH47_PaintDark", albedo_img, rough_img, (0.72, 0.72, 0.70), 0.10),
        "paint_accent": make_carc_material("CH47_PaintAccent", albedo_img, rough_img, (1.07, 1.07, 1.03), 0.08),
        "radome": make_solid_material("CH47_Radome", "31342c", metallic=0.05, roughness=0.70),
        "interior": make_solid_material("CH47_Interior", "2c2f28", metallic=0.14, roughness=0.88),
        "interior_floor": make_solid_material("CH47_CargoFloor", "383b36", metallic=0.38, roughness=0.72),
        "bench_canvas": make_solid_material("CH47_BenchCanvas", "6a3228", metallic=0.02, roughness=0.92),
        "steel": make_solid_material("CH47_DarkSteel", "333731", metallic=0.55, roughness=0.46),
        "gunmetal": make_solid_material("CH47_Gunmetal", "222523", metallic=0.72, roughness=0.38),
        "metal_edge": make_solid_material("CH47_TitaniumEdge", "7d8078", metallic=0.74, roughness=0.32),
        "rotor": make_solid_material("CH47_RotorBlade", "252723", metallic=0.16, roughness=0.66),
        "tip_yellow": make_solid_material("CH47_TipYellow", "d8ae28", metallic=0.04, roughness=0.54, use_vcol=False),
        "rubber": make_solid_material("CH47_Rubber", "1e1f1c", metallic=0.02, roughness=0.94),
        "glass": make_solid_material("CH47_Glass", "142632", metallic=0.38, roughness=0.07, use_vcol=False),
        "cavity": make_solid_material("CH47_Cavity", "0d0e0c", metallic=0.05, roughness=0.98, use_vcol=False),
        "exhaust": make_solid_material("CH47_Exhaust", "3b342d", metallic=0.68, roughness=0.48),
        "nav_red": make_solid_material(
            "CH47_NavRed", "b8241c", metallic=0.10, roughness=0.30, use_vcol=False, emission_hex="e0281e", emission_strength=0.6
        ),
        "nav_green": make_solid_material(
            "CH47_NavGreen", "229a3c", metallic=0.10, roughness=0.30, use_vcol=False, emission_hex="28be48", emission_strength=0.6
        ),
        "decal": make_alpha_texture_material("CH47_Markings", decal_img, roughness=0.84, metallic=0.04, double_sided=False),
        "rotor_blur": make_alpha_texture_material("CH47_RotorBlurMat", blur_img, roughness=0.62, metallic=0.08, double_sided=True),
    }


# ---------------------------------------------------------------------------
# Geometry Primitives with Clean Frame-Aligned UVs
# ---------------------------------------------------------------------------


def clear_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def link_object(obj: bpy.types.Object, parent: bpy.types.Object | None = None):
    bpy.context.scene.collection.objects.link(obj)
    if parent is not None:
        obj.parent = parent
    return obj


def assign_frame_uvs(bm: bmesh.types.BMesh, scale_y: float = 0.22, scale_xz: float = 0.26):
    """Assign aircraft frame-aligned UVs: U aligns with fuselage station Y, V aligns with perimeter/height.
    Eliminates arbitrary box-projection seam lines across curved noses and tapered cones."""
    uv_layer = bm.loops.layers.uv.verify()
    for face in bm.faces:
        n = face.normal
        # Use cylindrical/frame coordinates so panel seams follow real bulkhead stations along Y
        if abs(n.y) > 0.78:
            # Front/rear faces: clean planar projection without seam distortion
            for loop in face.loops:
                co = loop.vert.co
                loop[uv_layer].uv = (co.x * scale_xz * 0.6 + 0.5, co.z * scale_xz * 0.6 + 0.5)
        else:
            for loop in face.loops:
                co = loop.vert.co
                # Smooth perimeter coordinate around cabin cross-section
                v_coord = (co.z if abs(n.x) >= abs(n.z) else co.x) * scale_xz
                loop[uv_layer].uv = (co.y * scale_y, v_coord)


def make_mesh_obj(
    name: str,
    bm: bmesh.types.BMesh,
    mat: bpy.types.Material | list[bpy.types.Material],
    parent: bpy.types.Object | None = None,
    smooth: bool = True,
    split_angle: float | None = 34.0,
    auto_uv: bool = True,
    recalc_normals: bool = True,
) -> bpy.types.Object:
    if recalc_normals:
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    if auto_uv:
        assign_frame_uvs(bm)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mats_list = mat if isinstance(mat, list) else [mat]
    for m in mats_list:
        mesh.materials.append(m)
    obj = bpy.data.objects.new(name, mesh)
    link_object(obj, parent)
    mesh.polygons.foreach_set("use_smooth", [smooth] * len(mesh.polygons))
    mesh.update()
    if smooth and split_angle is not None:
        mod = obj.modifiers.new(name="EdgeSplit", type="EDGE_SPLIT")
        mod.use_edge_angle = True
        mod.split_angle = math.radians(split_angle)
    return obj


def loaf_ring(
    half_w: float,
    z_bot: float,
    z_top: float,
    chamfer_top: float,
    chamfer_bot: float,
    crown: float = 0.055,
    tumblehome: float = 0.04,
) -> list[tuple[float, float]]:
    """16-point smooth aerospace fuselage cross-section in (x, z), CCW."""
    ct = min(chamfer_top, half_w * 0.78, (z_top - z_bot) * 0.44)
    cb = min(chamfer_bot, half_w * 0.78, (z_top - z_bot) * 0.44)
    z_mid = (z_bot + z_top) * 0.5
    right = [
        (half_w - cb, z_bot),
        (half_w - cb * 0.25, z_bot + cb * 0.35),
        (half_w, z_bot + cb),
        (half_w, z_mid),
        (half_w - tumblehome, z_top - ct),
        (half_w - ct * 0.45 - tumblehome, z_top - ct * 0.22),
        (half_w - ct, z_top),
    ]
    left = [(-x, z) for (x, z) in reversed(right)]
    return right + [(0.0, z_top + crown)] + left + [(0.0, z_bot - crown * 0.35)]


def build_loft_along_y(
    name: str,
    stations: list[tuple[float, list[tuple[float, float]]]],
    mat: bpy.types.Material | list[bpy.types.Material],
    parent: bpy.types.Object,
    cap_start: bool = True,
    cap_end: bool = True,
    split_angle: float = 34.0,
    segment_mat_indices: list[int] | None = None,
    cap_start_mat: int = 0,
) -> bpy.types.Object:
    bm = bmesh.new()
    rings = [[bm.verts.new((x, y, z)) for (x, z) in pts] for (y, pts) in stations]
    n = len(rings[0])
    for i in range(len(rings) - 1):
        r0, r1 = rings[i], rings[i + 1]
        m_idx = segment_mat_indices[i] if segment_mat_indices else 0
        for j in range(n):
            jn = (j + 1) % n
            f = bm.faces.new((r0[j], r0[jn], r1[jn], r1[j]))
            f.material_index = m_idx
    if cap_start:
        f = bm.faces.new(list(reversed(rings[0])))
        f.material_index = cap_start_mat
    if cap_end:
        f = bm.faces.new(rings[-1])
        f.material_index = 0
    return make_mesh_obj(name, bm, mat, parent, smooth=True, split_angle=split_angle)


def build_loft_along_z(
    name: str,
    stations: list[tuple[float, list[tuple[float, float]]]],
    mat: bpy.types.Material,
    parent: bpy.types.Object,
    cap_bottom: bool = True,
    cap_top: bool = True,
    split_angle: float = 42.0,
) -> bpy.types.Object:
    bm = bmesh.new()
    rings = [[bm.verts.new((x, y, z)) for (x, y) in pts] for (z, pts) in stations]
    n = len(rings[0])
    for i in range(len(rings) - 1):
        r0, r1 = rings[i], rings[i + 1]
        for j in range(n):
            jn = (j + 1) % n
            bm.faces.new((r0[j], r0[jn], r1[jn], r1[j]))
    if cap_bottom:
        bm.faces.new(list(reversed(rings[0])))
    if cap_top:
        bm.faces.new(rings[-1])
    return make_mesh_obj(name, bm, mat, parent, smooth=True, split_angle=split_angle)


def add_conformal_panel(
    name: str,
    quad_pts: list[Vector],
    out_offset: float,
    thickness: float,
    mat: bpy.types.Material,
    parent: bpy.types.Object,
    outward_hint: Vector,
) -> bpy.types.Object:
    p0, p1, p2, p3 = quad_pts
    n = (p1 - p0).cross(p3 - p0)
    n = outward_hint.normalized() if n.length < 1e-6 else n.normalized()
    if n.dot(outward_hint) < 0.0:
        n = -n
        p0, p1, p2, p3 = p0, p3, p2, p1

    outer = [p + n * out_offset for p in (p0, p1, p2, p3)]
    inner = [p + n * (out_offset - thickness) for p in (p0, p1, p2, p3)]
    bm = bmesh.new()
    vo = [bm.verts.new(p) for p in outer]
    vi = [bm.verts.new(p) for p in inner]
    bm.faces.new(vo)
    bm.faces.new(list(reversed(vi)))
    for i in range(4):
        ni = (i + 1) % 4
        bm.faces.new((vi[i], vi[ni], vo[ni], vo[i]))
    return make_mesh_obj(name, bm, mat, parent, smooth=False, split_angle=None)


def add_decal_quad(
    name: str,
    quad_pts: list[Vector],
    uv_rect: tuple[float, float, float, float],
    out_offset: float,
    mat: bpy.types.Material,
    parent: bpy.types.Object,
    outward_hint: Vector,
) -> bpy.types.Object:
    p0, p1, p2, p3 = quad_pts  # BL, BR, TR, TL as viewed from outside
    n = (p1 - p0).cross(p3 - p0)
    n = outward_hint.normalized() if n.length < 1e-6 else n.normalized()
    if n.dot(outward_hint) < 0.0:
        n = -n
    pts = [p + n * out_offset for p in (p0, p1, p2, p3)]
    u0, v0, u1, v1 = uv_rect
    uvs = [(u0, v0), (u1, v0), (u1, v1), (u0, v1)]
    if (pts[1] - pts[0]).cross(pts[3] - pts[0]).dot(outward_hint) < 0.0:
        pts = list(reversed(pts))
        uvs = list(reversed(uvs))
    bm = bmesh.new()
    verts = [bm.verts.new(p) for p in pts]
    face = bm.faces.new(verts)
    uv_layer = bm.loops.layers.uv.verify()
    for loop, uv in zip(face.loops, uvs):
        loop[uv_layer].uv = uv
    return make_mesh_obj(name, bm, mat, parent, smooth=False, split_angle=None, auto_uv=False, recalc_normals=False)


def add_box(
    name: str,
    center: tuple[float, float, float],
    size: tuple[float, float, float],
    mat: bpy.types.Material,
    parent: bpy.types.Object,
    rot_deg: tuple[float, float, float] = (0.0, 0.0, 0.0),
    bevel: float = 0.0,
    smooth: bool = False,
) -> bpy.types.Object:
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=size, verts=bm.verts)
    if bevel > 0.0:
        bmesh.ops.bevel(bm, geom=list(bm.edges), offset=bevel, segments=1, affect="EDGES", clamp_overlap=True)
    rot_m = Euler(tuple(math.radians(a) for a in rot_deg), "XYZ").to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=Matrix.Translation(center) @ rot_m, verts=bm.verts)
    return make_mesh_obj(name, bm, mat, parent, smooth=smooth, split_angle=34.0 if smooth else None)


def add_cylinder(
    name: str,
    center: tuple[float, float, float],
    radius1: float,
    radius2: float,
    depth: float,
    mat: bpy.types.Material,
    parent: bpy.types.Object,
    axis: str = "Z",
    segments: int = 16,
    rot_deg: tuple[float, float, float] = (0.0, 0.0, 0.0),
    cap_ends: bool = True,
) -> bpy.types.Object:
    bm = bmesh.new()
    bmesh.ops.create_cone(
        bm, cap_ends=cap_ends, cap_tris=False, segments=segments, radius1=radius1, radius2=radius2, depth=depth
    )
    if axis == "Y":
        base_rot = Euler((math.radians(90.0), 0.0, 0.0), "XYZ").to_matrix().to_4x4()
    elif axis == "X":
        base_rot = Euler((0.0, math.radians(90.0), 0.0), "XYZ").to_matrix().to_4x4()
    else:
        base_rot = Matrix.Identity(4)
    extra_rot = Euler(tuple(math.radians(a) for a in rot_deg), "XYZ").to_matrix().to_4x4()
    bmesh.ops.transform(bm, matrix=Matrix.Translation(center) @ extra_rot @ base_rot, verts=bm.verts)
    return make_mesh_obj(name, bm, mat, parent, smooth=True, split_angle=48.0)


def add_strut(
    name: str,
    p0: tuple[float, float, float],
    p1: tuple[float, float, float],
    radius: float,
    mat: bpy.types.Material,
    parent: bpy.types.Object,
    segments: int = 10,
) -> bpy.types.Object:
    v0, v1 = Vector(p0), Vector(p1)
    diff = v1 - v0
    mid = (v0 + v1) * 0.5
    bm = bmesh.new()
    bmesh.ops.create_cone(
        bm, cap_ends=True, cap_tris=False, segments=segments, radius1=radius, radius2=radius, depth=diff.length
    )
    quat = Vector((0.0, 0.0, 1.0)).rotation_difference(diff.normalized())
    bmesh.ops.transform(bm, matrix=Matrix.Translation(mid) @ quat.to_matrix().to_4x4(), verts=bm.verts)
    return make_mesh_obj(name, bm, mat, parent, smooth=True, split_angle=50.0)


# ---------------------------------------------------------------------------
# CH-47D Component Builders (Iteration 3)
# ---------------------------------------------------------------------------

# Forward fuselage & main cabin stations (Y = +7.77 back to Y = -3.55, where hollow rear cargo bay opens!)
# Smooth multi-ring bulbous nose (indices 0..3) seamlessly integrates the dark weather-radar radome (0..1)
# Windshield ruled greenhouse spans station 3 (Y=7.08) -> station 4 (Y=5.82)
FWD_FUSELAGE_STATIONS = [
    (7.77, loaf_ring(0.14, 1.38, 1.62, 0.07, 0.07, crown=0.02, tumblehome=0.01)),  # 0: Smooth nose apex (radome)
    (7.69, loaf_ring(0.46, 1.20, 1.80, 0.18, 0.18, crown=0.03, tumblehome=0.02)),  # 1: Radome edge (seamless!)
    (7.48, loaf_ring(0.84, 0.98, 1.98, 0.30, 0.26, crown=0.05, tumblehome=0.03)),  # 2: Rounded chin/nose cheek
    (7.08, loaf_ring(1.09, 0.80, 2.08, 0.32, 0.24, crown=0.05, tumblehome=0.04)),  # 3: Windshield base brow
    (5.82, loaf_ring(1.26, BELLY_Z, ROOF_Z, 0.36, 0.22, crown=0.06, tumblehome=0.04)),  # 4: Windshield top / cabin start
    (3.60, loaf_ring(1.26, BELLY_Z, ROOF_Z, 0.36, 0.22, crown=0.06, tumblehome=0.04)),  # 5
    (1.20, loaf_ring(1.26, BELLY_Z, ROOF_Z, 0.36, 0.22, crown=0.06, tumblehome=0.04)),  # 6
    (-1.20, loaf_ring(1.26, BELLY_Z, ROOF_Z, 0.36, 0.22, crown=0.06, tumblehome=0.04)),  # 7
    (-3.55, loaf_ring(1.26, BELLY_Z, ROOF_Z, 0.36, 0.22, crown=0.06, tumblehome=0.04)),  # 8: Rear cargo bay opening
]


def _st_pt(station_idx: int, ring_idx: int) -> Vector:
    y, pts = FWD_FUSELAGE_STATIONS[station_idx]
    x, z = pts[ring_idx]
    return Vector((x, y, z))


def _bilerp(p00: Vector, p10: Vector, p11: Vector, p01: Vector, u: float, v: float) -> Vector:
    return p00.lerp(p10, u).lerp(p01.lerp(p11, u), v)


def build_fuselage_and_cargo_bay(parent: bpy.types.Object, mats: dict[str, bpy.types.Material], lod: int):
    """Smooth integral-radome nose + cabin + REAL hollow openable rear cargo bay & interior."""
    # 1. Forward nose + main cabin outer shell (open at Y = -3.55 where hollow cargo bay begins!)
    # Segment 0 (nose apex -> radome edge) gets dark radome material seamlessly with zero protruding seam!
    seg_mats = [1] + [0] * (len(FWD_FUSELAGE_STATIONS) - 2)
    build_loft_along_y(
        "FuselageForwardCabin",
        FWD_FUSELAGE_STATIONS,
        [mats["paint"], mats["radome"]],
        parent,
        cap_start=True,
        cap_end=False,  # Open into hollow rear cargo bay!
        split_angle=40.0,
        segment_mat_indices=seg_mats,
        cap_start_mat=1,
    )

    # 2. Hollow Aft Fuselage Framing (left & right upswept cheek fairings + upper rear roof overhang)
    # Leaves a genuine 1.92 m wide x 1.66 m tall open cargo portal from Y = -3.55 back to Y = -5.95!
    for sign, side in ((1.0, "Right"), (-1.0, "Left")):
        def cheek_ring(x_in: float, x_out: float, z_bot: float, z_top: float):
            pts = [
                (sign * x_in, z_bot + 0.04),
                (sign * (x_out - 0.14), z_bot),
                (sign * x_out, z_bot + 0.14),
                (sign * x_out, z_top - 0.34),
                (sign * (x_out - 0.24), z_top),
                (sign * x_in, z_top),
            ]
            return pts if sign > 0 else list(reversed(pts))

        cheek_stations = [
            (-3.55, cheek_ring(BAY_HALF_W, 1.26, BELLY_Z, ROOF_Z)),
            (-4.75, cheek_ring(BAY_HALF_W, 1.22, 1.28, ROOF_Z)),
            (-5.95, cheek_ring(BAY_HALF_W - 0.04, 1.12, 1.84, 3.04)),
            (-6.35, cheek_ring(0.55, 1.00, 2.08, 3.02)),
        ]
        build_loft_along_y(f"AftCheek_{side}", cheek_stations, mats["paint"], parent, split_angle=36.0)

    # Upper rear roof overhang above the open cargo bay (Z = 2.46..3.08) transitioning into closed tail cone
    roof_aft_stations = [
        (-3.55, loaf_ring(1.22, BAY_CEIL_Z, ROOF_Z, 0.34, 0.04, crown=0.06, tumblehome=0.04)),
        (-4.75, loaf_ring(1.18, BAY_CEIL_Z, ROOF_Z, 0.32, 0.04, crown=0.05, tumblehome=0.04)),
        (-5.95, loaf_ring(1.08, BAY_CEIL_Z, 3.04, 0.30, 0.06, crown=0.04, tumblehome=0.04)),
        (-6.80, loaf_ring(0.84, 2.26, 2.98, 0.26, 0.12, crown=0.03, tumblehome=0.03)),
        (-7.42, loaf_ring(0.36, 2.54, 2.88, 0.14, 0.10, crown=0.02, tumblehome=0.02)),
    ]
    build_loft_along_y("AftRoofTail", roof_aft_stations, mats["paint"], parent, split_angle=38.0)

    # 3. Real Hollow Cargo Bay Interior (visible when RearRamp lowers!)
    # Recessed cabin interior spanning Y = -0.60 back to Y = -3.55
    bay_len = 3.00
    bay_mid_y = -2.06
    # Non-skid steel cargo floor + lower fuselage sill lip
    add_box("CargoFloor", (0.0, bay_mid_y, BAY_FLOOR_Z - 0.04), (BAY_HALF_W * 2.02, bay_len, 0.10), mats["interior_floor"], parent)
    # Forward cabin dark bulkhead closing the front of the visible bay
    add_box("CargoBulkheadFwd", (0.0, -0.56, (BAY_FLOOR_Z + BAY_CEIL_Z) * 0.5), (BAY_HALF_W * 2.02, 0.10, BAY_CEIL_Z - BAY_FLOOR_Z), mats["interior"], parent)
    # Interior side walls & ceiling liner
    for sign, side in ((1.0, "Right"), (-1.0, "Left")):
        add_box(
            f"CargoWall_{side}",
            (sign * (BAY_HALF_W + 0.04), bay_mid_y, (BAY_FLOOR_Z + BAY_CEIL_Z) * 0.5),
            (0.08, bay_len, BAY_CEIL_Z - BAY_FLOOR_Z),
            mats["interior"],
            parent,
        )
    add_box("CargoCeiling", (0.0, bay_mid_y, BAY_CEIL_Z + 0.03), (BAY_HALF_W * 2.02, bay_len, 0.08), mats["interior"], parent)

    if lod < 2:
        # Twin metallic vehicle wheel treadways / roller tracks along the cargo floor
        for sx in (-0.54, 0.54):
            add_box(f"CargoTreadway_{sx:+.2f}", (sx, bay_mid_y - 0.05, BAY_FLOOR_Z + 0.015), (0.34, bay_len - 0.12, 0.025), mats["steel"], parent)
        # Folding red-canvas troop benches lining left & right cabin walls (classic CH-47 interior!)
        for sign, side in ((1.0, "Right"), (-1.0, "Left")):
            add_box(f"TroopBenchSeat_{side}", (sign * 0.78, -2.10, 1.24), (0.34, 2.65, 0.05), mats["bench_canvas"], parent)
            add_box(f"TroopBenchBack_{side}", (sign * 0.94, -2.10, 1.56), (0.05, 2.65, 0.44), mats["bench_canvas"], parent)
        # Upper fold-in rear cargo hatch retracted cleanly into ceiling at top of bay opening
        add_box("UpperCargoHatch", (0.0, -4.65, BAY_CEIL_Z + 0.02), (BAY_HALF_W * 1.92, 1.35, 0.06), mats["interior"], parent)

    # 4. Dorsal synchronizing driveshaft tunnel
    spine_stations = [
        (3.85, loaf_ring(0.40, 2.94, 3.46, 0.16, 0.05, crown=0.035, tumblehome=0.02)),
        (1.00, loaf_ring(0.40, 2.94, 3.47, 0.16, 0.05, crown=0.035, tumblehome=0.02)),
        (-1.60, loaf_ring(0.40, 2.94, 3.47, 0.16, 0.05, crown=0.035, tumblehome=0.02)),
        (-4.35, loaf_ring(0.42, 2.94, 3.48, 0.16, 0.05, crown=0.035, tumblehome=0.02)),
    ]
    build_loft_along_y("DorsalSpine", spine_stations, mats["paint_accent"], parent, split_angle=34.0)

    if lod < 2:
        for idx, hy in enumerate((2.10, 0.05, -2.05)):
            add_box(f"CargoHookWell_{idx}", (0.0, hy, BELLY_Z - 0.015), (0.36, 0.44, 0.06), mats["cavity"], parent)
            add_box(f"CargoHookRim_{idx}", (0.0, hy, BELLY_Z - 0.005), (0.44, 0.52, 0.04), mats["paint_dark"], parent)
        for idx, ly in enumerate((2.35, 0.40, -1.65, -3.20)):
            add_box(f"SpineHatch_{idx}", (0.0, ly, 3.485), (0.46, 0.62, 0.028), mats["paint_dark"], parent, bevel=0.008)


def build_sponsons(parent: bpy.types.Object, mats: dict[str, bpy.types.Material], lod: int):
    """Long twin CH-47D fuel sponsons with sectional seam bands, flare pods, and aft gear fairings."""

    def sponson_ring(sign: float, x_in: float, x_out: float, z_bot: float, z_top: float, bevel: float):
        pts = [
            (sign * x_in, z_bot),
            (sign * (x_out - bevel), z_bot),
            (sign * x_out, z_bot + bevel * 0.85),
            (sign * x_out, z_top - bevel * 1.15),
            (sign * (x_out - bevel * 1.25), z_top),
            (sign * x_in, z_top + 0.05),
        ]
        return pts if sign > 0 else list(reversed(pts))

    for sign, side in ((1.0, "Right"), (-1.0, "Left")):
        stations = [
            (4.92, sponson_ring(sign, 1.04, 1.24, 0.94, 1.44, 0.08)),
            (4.20, sponson_ring(sign, 1.08, 1.78, 0.78, 1.82, 0.16)),
            (3.40, sponson_ring(sign, 1.10, SPONSON_WIDTH * 0.5, 0.76, 1.92, 0.18)),
            (0.00, sponson_ring(sign, 1.10, SPONSON_WIDTH * 0.5, 0.76, 1.92, 0.18)),
            (-3.45, sponson_ring(sign, 1.10, SPONSON_WIDTH * 0.5, 0.76, 1.92, 0.18)),
            (-4.28, sponson_ring(sign, 1.06, 1.76, 0.84, 1.84, 0.16)),
            (-4.95, sponson_ring(sign, 1.00, 1.24, 1.12, 1.56, 0.08)),
        ]
        build_loft_along_y(f"FuelSponson_{side}", stations, mats["paint"], parent, split_angle=34.0)

        nav_mat = mats["nav_green"] if sign > 0 else mats["nav_red"]
        add_box(f"NavLight_{side}", (sign * 1.89, 3.65, 1.42), (0.07, 0.18, 0.09), nav_mat, parent, bevel=0.018)

        if lod < 2:
            for idx, sy in enumerate((2.25, -2.15)):
                add_box(f"SponsonSeam_{side}_{idx}", (sign * 1.50, sy, 1.34), (0.81, 0.04, 1.14), mats["paint_dark"], parent)
            add_cylinder(f"FuelCap_{side}", (sign * 1.54, 0.45, 1.93), 0.09, 0.09, 0.04, mats["steel"], parent, axis="Z", segments=10)
            add_box(
                f"FlarePod_{side}",
                (sign * 1.84, -3.55, 1.58),
                (0.14, 0.52, 0.24),
                mats["paint_dark"],
                parent,
                rot_deg=(0.0, 0.0, sign * 6.0),
                bevel=0.02,
            )
            add_box(
                f"AftGearFairing_{side}",
                (sign * 1.62, REAR_GEAR_Y + 0.15, 1.12),
                (0.46, 0.72, 0.34),
                mats["paint_accent"],
                parent,
                bevel=0.06,
                smooth=True,
            )


def build_pylons_and_engines(parent: bpy.types.Object, mats: dict[str, bpy.types.Material], lod: int):
    """Streamlined forward pylon, true airfoil-tapered rear pylon, and detailed T55-GA-714A pods."""

    def airfoil_pylon_section(y_front: float, y_rear: float, half_w: float, nose_taper: float = 0.36, tail_taper: float = 0.16):
        """12-point smooth aerodynamic fin cross-section with rounded leading edge and sharp trailing edge."""
        L = y_front - y_rear
        y1 = y_front - L * 0.10
        y2 = y_front - L * 0.30
        y3 = y_front - L * 0.62
        y4 = y_front - L * 0.86
        right = [
            (half_w * nose_taper, y_front),
            (half_w * 0.82, y1),
            (half_w, y2),
            (half_w * 0.94, y3),
            (half_w * 0.56, y4),
            (half_w * tail_taper, y_rear),
        ]
        left = [(-x, y) for (x, y) in reversed(right)]
        return right + left

    # 1. Streamlined forward rotor pylon blending smoothly forward into cockpit roof brow
    fwd_stations = [
        (2.82, airfoil_pylon_section(6.32, 3.25, 0.86, 0.44, 0.46)),
        (3.18, airfoil_pylon_section(6.16, 3.48, 0.76, 0.42, 0.46)),
        (3.62, airfoil_pylon_section(5.94, 3.82, 0.64, 0.40, 0.45)),
        (4.02, airfoil_pylon_section(5.68, 4.24, 0.50, 0.42, 0.44)),
        (4.20, airfoil_pylon_section(5.52, 4.42, 0.34, 0.46, 0.46)),
    ]
    build_loft_along_z("ForwardPylon", fwd_stations, mats["paint"], parent, split_angle=44.0)

    # Sculpted rotor mast collar fairing at top of forward pylon
    add_cylinder("FwdMastCollar", (0.0, FRONT_HUB.y, 4.22), 0.36, 0.28, 0.18, mats["paint_dark"], parent, axis="Z", segments=16)

    if lod < 2:
        # Forward transmission oil-cooler intake with central divider vane
        add_box("FwdPylonIntakeFrame", (0.0, 5.95, 3.64), (0.44, 0.12, 0.30), mats["paint_dark"], parent, rot_deg=(28.0, 0.0, 0.0))
        add_box("FwdPylonIntakeCav", (0.0, 5.99, 3.66), (0.36, 0.08, 0.22), mats["cavity"], parent, rot_deg=(28.0, 0.0, 0.0))
        add_box("FwdPylonIntakeVane", (0.0, 6.00, 3.66), (0.03, 0.09, 0.22), mats["steel"], parent, rot_deg=(28.0, 0.0, 0.0))

    # 2. Tall swept-back aerodynamic rear rotor pylon (sharp airfoil trailing edge, smooth flanks!)
    rear_stations = [
        (2.40, airfoil_pylon_section(-3.95, -7.24, 0.90, 0.42, 0.22)),
        (2.95, airfoil_pylon_section(-4.05, -7.42, 0.86, 0.40, 0.20)),
        (3.45, airfoil_pylon_section(-4.48, -7.58, 0.78, 0.38, 0.18)),
        (3.98, airfoil_pylon_section(-5.02, -7.70, 0.68, 0.38, 0.17)),
        (4.52, airfoil_pylon_section(-5.56, -7.77, 0.58, 0.40, 0.17)),
        (4.96, airfoil_pylon_section(-5.96, -7.76, 0.50, 0.42, 0.20)),
        (5.18, airfoil_pylon_section(-6.14, -7.68, 0.44, 0.44, 0.26)),
        (5.30, airfoil_pylon_section(-6.28, -7.52, 0.32, 0.48, 0.34)),
    ]
    build_loft_along_z("RearPylon", rear_stations, mats["paint"], parent, split_angle=48.0)
    add_cylinder("RearMastCollar", (0.0, REAR_HUB.y, 5.30), 0.36, 0.28, 0.18, mats["paint_dark"], parent, axis="Z", segments=16)

    # Flush oil-cooler scoop + 3D louver slats on rear pylon swept leading edge
    add_box("RearPylonIntakeFrame", (0.0, -5.04, 4.02), (0.60, 0.14, 0.58), mats["paint_dark"], parent, rot_deg=(44.0, 0.0, 0.0))
    add_box("RearPylonIntakeCavity", (0.0, -4.99, 4.07), (0.48, 0.08, 0.46), mats["cavity"], parent, rot_deg=(44.0, 0.0, 0.0))
    if lod == 0:
        for k in range(-2, 3):
            add_box(
                f"RearPylonLouver_{k + 2}",
                (0.0, -4.98 - k * 0.055, 4.08 + k * 0.055),
                (0.47, 0.06, 0.025),
                mats["steel"],
                parent,
                rot_deg=(12.0, 0.0, 0.0),
            )
        for sign, side in ((1.0, "Right"), (-1.0, "Left")):
            for step_i, (sz, sx) in enumerate(((3.20, 0.62), (3.65, 0.55), (4.10, 0.48), (4.55, 0.42))):
                add_box(f"PylonStep_{side}_{step_i}", (sign * sx, -7.26, sz), (0.04, 0.15, 0.04), mats["cavity"], parent)

    # 3. Detailed Twin Lycoming T55-GA-714A Turbine Pods (with EAPS beveled intake + extended exhaust diffuser!)
    cyl_segs = (22, 14, 10)[lod]
    for sign, side in ((1.0, "Right"), (-1.0, "Left")):
        ex = sign * 1.44
        ey = -5.42
        ez = 3.04

        # Aerodynamic mounting pylon + upper/lower strut fillets into rear pylon
        add_box(
            f"EngineMount_{side}",
            (sign * 1.00, ey + 0.06, ez - 0.02),
            (0.76, 1.50, 0.38),
            mats["paint_dark"],
            parent,
            bevel=0.06,
            smooth=True,
        )
        # Main turbine nacelle cowling
        add_cylinder(f"EnginePod_{side}", (ex, ey, ez), 0.35, 0.40, 2.26, mats["paint_accent"], parent, axis="Y", segments=cyl_segs)
        # Beveled EAPS / particle-separator forward intake shroud (characteristic angled front cowl)
        add_cylinder(
            f"EngineIntakeShroud_{side}",
            (ex, ey + 1.28, ez + 0.01),
            0.40,
            0.35,
            0.36,
            mats["paint_dark"],
            parent,
            axis="Y",
            segments=cyl_segs,
            rot_deg=(4.0, 0.0, sign * 3.0),
        )
        # Protective intake lip ring + recessed dark compressor inlet + FOD screen ring + bullet spinner
        add_cylinder(
            f"EngineIntakeLip_{side}", (ex, ey + 1.46, ez + 0.02), 0.35, 0.31, 0.08, mats["steel"], parent, axis="Y", segments=cyl_segs
        )
        add_cylinder(f"EngineIntakeCavity_{side}", (ex, ey + 1.44, ez + 0.02), 0.27, 0.27, 0.08, mats["cavity"], parent, axis="Y", segments=cyl_segs)
        add_cylinder(
            f"EngineSpinner_{side}", (ex, ey + 1.50, ez + 0.02), 0.12, 0.03, 0.20, mats["metal_edge"], parent, axis="Y", segments=max(8, cyl_segs // 2)
        )
        if lod == 0:
            # Radial inlet guide struts inside EAPS mouth + cowling latch bands + upper air scoop
            for vane_ang in (0.0, 60.0, 120.0):
                add_box(
                    f"EngineVane_{side}_{int(vane_ang)}",
                    (ex, ey + 1.45, ez + 0.02),
                    (0.52, 0.04, 0.018),
                    mats["steel"],
                    parent,
                    rot_deg=(0.0, vane_ang, 0.0),
                )
            for b_idx, by in enumerate((ey + 0.62, ey - 0.42)):
                add_cylinder(
                    f"EngineBand_{side}_{b_idx}", (ex, by, ez), 0.402, 0.402, 0.045, mats["paint_dark"], parent, axis="Y", segments=cyl_segs
                )
            add_box(
                f"EngineTopScoop_{side}",
                (ex - sign * 0.04, ey + 0.40, ez + 0.40),
                (0.24, 0.92, 0.14),
                mats["paint_dark"],
                parent,
                bevel=0.03,
                smooth=True,
            )

        # Extended metallic T55 exhaust diffuser + outer IR heat-shield collar (canted 10 deg outward)
        add_cylinder(
            f"EngineExhaustHeatShield_{side}",
            (ex + sign * 0.06, ey - 1.26, ez),
            0.31,
            0.35,
            0.40,
            mats["paint_dark"],
            parent,
            axis="Y",
            segments=cyl_segs,
            rot_deg=(0.0, 0.0, -sign * 9.0),
        )
        add_cylinder(
            f"EngineExhaustNozzle_{side}",
            (ex + sign * 0.12, ey - 1.56, ez),
            0.23,
            0.29,
            0.52,
            mats["exhaust"],
            parent,
            axis="Y",
            segments=cyl_segs,
            rot_deg=(0.0, 0.0, -sign * 10.0),
        )
        add_cylinder(
            f"EngineExhaustHole_{side}",
            (ex + sign * 0.17, ey - 1.81, ez),
            0.19,
            0.19,
            0.06,
            mats["cavity"],
            parent,
            axis="Y",
            segments=max(10, cyl_segs - 4),
            rot_deg=(0.0, 0.0, -sign * 10.0),
        )
        if lod == 0:
            add_cylinder(
                f"EngineExhaustCone_{side}",
                (ex + sign * 0.14, ey - 1.70, ez),
                0.04,
                0.10,
                0.24,
                mats["metal_edge"],
                parent,
                axis="Y",
                segments=10,
                rot_deg=(0.0, 0.0, -sign * 10.0),
            )


def build_cockpit_and_windows(parent: bpy.types.Object, mats: dict[str, bpy.types.Material], lod: int):
    """Larger thin-mullion 'aquarium' Chinook cockpit glazing, wipers, cabin portholes, and crew door."""
    # FWD_FUSELAGE_STATIONS indices: 2 -> Y=7.48 (chin), 3 -> Y=7.08 (windshield base), 4 -> Y=5.82 (windshield top)
    # 16-pt loaf_ring:
    # Right: 2=(+w, z_bot+cb), 3=(+w, z_mid), 4=(+w-t, z_top-ct), 6=(+w-ct, z_top), 7=(0, z_top+crown)
    # Left:  8=(-(w-ct), z_top), 10=(-(w-t), z_top-ct), 11=(-w, z_mid), 12=(-w, z_bot+cb)

    def add_aquarium_pane(
        name: str, p00: Vector, p10: Vector, p11: Vector, p01: Vector, u0: float, u1: float, v0: float, v1: float, hint: Vector
    ):
        # Slimmer aerospace mullions (margin=0.025) for panoramic "aquarium" cockpit look!
        m = 0.025
        if lod < 2:
            fq = [
                _bilerp(p00, p10, p11, p01, max(0.0, u0 - m), max(0.0, v0 - m)),
                _bilerp(p00, p10, p11, p01, min(1.0, u1 + m), max(0.0, v0 - m)),
                _bilerp(p00, p10, p11, p01, min(1.0, u1 + m), min(1.0, v1 + m)),
                _bilerp(p00, p10, p11, p01, max(0.0, u0 - m), min(1.0, v1 + m)),
            ]
            add_conformal_panel(f"{name}_Frame", fq, 0.020, 0.028, mats["paint_dark"], parent, hint)
        gq = [
            _bilerp(p00, p10, p11, p01, u0, v0),
            _bilerp(p00, p10, p11, p01, u1, v0),
            _bilerp(p00, p10, p11, p01, u1, v1),
            _bilerp(p00, p10, p11, p01, u0, v1),
        ]
        add_conformal_panel(f"{name}_Glass", gq, 0.030, 0.020, mats["glass"], parent, hint)

    # 1. Larger panoramic front upper windshields (thin center post, spanning almost full brow)
    add_aquarium_pane("WindshieldFront_Right", _st_pt(3, 7), _st_pt(3, 6), _st_pt(4, 6), _st_pt(4, 7), 0.04, 0.96, 0.05, 0.88, Vector((0.25, 0.75, 0.65)))
    add_aquarium_pane("WindshieldFront_Left", _st_pt(3, 7), _st_pt(3, 8), _st_pt(4, 8), _st_pt(4, 7), 0.04, 0.96, 0.05, 0.88, Vector((-0.25, 0.75, 0.65)))

    # 2. Wide wrap-around corner panes smoothly connecting front windshield to side pilot windows
    add_aquarium_pane("WindshieldCorner_Right", _st_pt(3, 6), _st_pt(3, 4), _st_pt(4, 4), _st_pt(4, 6), 0.05, 0.95, 0.05, 0.88, Vector((0.75, 0.55, 0.45)))
    add_aquarium_pane("WindshieldCorner_Left", _st_pt(3, 8), _st_pt(3, 10), _st_pt(4, 10), _st_pt(4, 8), 0.05, 0.95, 0.05, 0.88, Vector((-0.75, 0.55, 0.45)))

    # 3. Large side cockpit sliding pilot windows
    add_aquarium_pane("CockpitSide_Right", _st_pt(3, 3), _st_pt(3, 4), _st_pt(4, 4), _st_pt(4, 3), 0.04, 0.96, 0.06, 0.92, Vector((1.0, 0.18, 0.0)))
    add_aquarium_pane("CockpitSide_Left", _st_pt(3, 11), _st_pt(3, 10), _st_pt(4, 10), _st_pt(4, 11), 0.04, 0.96, 0.06, 0.92, Vector((-1.0, 0.18, 0.0)))

    # 4. Prominent Chinook lower chin observation windows (clean, crisp, zero stray slits)
    add_aquarium_pane("ChinWindow_Right", _st_pt(2, 2), _st_pt(2, 4), _st_pt(3, 4), _st_pt(3, 2), 0.08, 0.88, 0.08, 0.90, Vector((0.75, 0.65, -0.08)))
    add_aquarium_pane("ChinWindow_Left", _st_pt(2, 12), _st_pt(2, 10), _st_pt(3, 10), _st_pt(3, 12), 0.08, 0.88, 0.08, 0.90, Vector((-0.75, 0.65, -0.08)))

    if lod == 0:
        for sign, side in ((1.0, "Right"), (-1.0, "Left")):
            p_base = _bilerp(_st_pt(3, 7), _st_pt(3, 6 if sign > 0 else 8), _st_pt(4, 6 if sign > 0 else 8), _st_pt(4, 7), 0.44, 0.04)
            p_tip = _bilerp(_st_pt(3, 7), _st_pt(3, 6 if sign > 0 else 8), _st_pt(4, 6 if sign > 0 else 8), _st_pt(4, 7), 0.28, 0.58)
            off = Vector((sign * 0.02, 0.045, 0.045))
            add_strut(f"Wiper_{side}", tuple(p_base + off), tuple(p_tip + off), 0.012, mats["steel"], parent, segments=6)

    # 6. Five circular cabin portholes per side
    porthole_ys = (2.85, 1.45, 0.05, -1.35, -2.75)
    p_segs = (16, 10, 8)[lod]
    for sign, side in ((1.0, "Right"), (-1.0, "Left")):
        for idx, py in enumerate(porthole_ys):
            if lod < 2:
                add_cylinder(
                    f"PortholeFrame_{side}_{idx}", (sign * 1.255, py, 2.34), 0.21, 0.21, 0.045, mats["paint_dark"], parent, axis="X", segments=p_segs
                )
            add_cylinder(
                f"PortholeGlass_{side}_{idx}", (sign * 1.274, py, 2.34), 0.16, 0.16, 0.032, mats["glass"], parent, axis="X", segments=p_segs
            )

    # 7. Starboard crew door (upper half open for M60D gunner!) & port emergency gunner window
    add_box("CrewDoorLower", (1.262, 4.38, 1.56), (0.045, 0.90, 0.68), mats["paint_dark"], parent, bevel=0.01)
    add_box("CrewDoorUpperOpen", (1.262, 4.38, 2.28), (0.04, 0.86, 0.76), mats["cavity"], parent)
    add_box("CrewDoorUpperRim", (1.268, 4.38, 2.66), (0.05, 0.92, 0.06), mats["paint_dark"], parent)

    add_box("PortGunWindowOpen", (-1.262, 4.38, 2.28), (0.04, 0.74, 0.66), mats["cavity"], parent)
    add_box("PortGunWindowRim", (-1.268, 4.38, 2.62), (0.05, 0.80, 0.06), mats["paint_dark"], parent)


def build_door_guns(parent: bpy.types.Object, mats: dict[str, bpy.types.Material], lod: int):
    """Twin combat M60D 7.62mm pintle-mounted door/window machine guns (LOD0 & LOD1)."""
    if lod >= 2:
        return
    for sign, side in ((1.0, "Right"), (-1.0, "Left")):
        # Pintle mount arm protruding cleanly from forward cabin doorway / gunner window
        gx = sign * 1.38
        gy = 4.36
        gz = 2.02
        add_strut(f"GunPintlePost_{side}", (sign * 1.26, gy, gz - 0.18), (gx, gy, gz), 0.022, mats["steel"], parent, 6)
        # Gun angled outward (~65 deg off bow) and slightly downward (-10 deg)
        yaw = -sign * 22.0  # slight sweep aft/outward
        # Receiver body
        add_box(f"M60D_Receiver_{side}", (gx + sign * 0.12, gy, gz + 0.04), (0.48, 0.09, 0.12), mats["gunmetal"], parent, rot_deg=(0.0, sign * 10.0, yaw), bevel=0.01)
        # Barrel + gas tube + flash hider extending outward
        add_cylinder(
            f"M60D_Barrel_{side}",
            (gx + sign * 0.56, gy - 0.16, gz - 0.04),
            0.022,
            0.018,
            0.62,
            mats["gunmetal"],
            parent,
            axis="X",
            segments=8,
            rot_deg=(0.0, sign * 10.0, yaw),
        )
        add_cylinder(
            f"M60D_FlashHider_{side}",
            (gx + sign * 0.86, gy - 0.28, gz - 0.09),
            0.026,
            0.022,
            0.12,
            mats["steel"],
            parent,
            axis="X",
            segments=8,
            rot_deg=(0.0, sign * 10.0, yaw),
        )
        # Side ammo can mounted beside receiver
        add_box(f"M60D_AmmoBox_{side}", (gx + sign * 0.10, gy + 0.11, gz - 0.02), (0.14, 0.12, 0.18), mats["paint_dark"], parent, rot_deg=(0.0, 0.0, yaw))
        if lod == 0:
            # Spade grip frame at back of receiver
            add_box(f"M60D_SpadeGrips_{side}", (gx - sign * 0.12, gy + 0.08, gz + 0.08), (0.06, 0.16, 0.12), mats["steel"], parent, rot_deg=(0.0, 0.0, yaw))


def build_landing_gear(parent: bpy.types.Object, mats: dict[str, bpy.types.Material], lod: int):
    t_segs = (20, 14, 10)[lod]

    for sign, side in ((1.0, "Right"), (-1.0, "Left")):
        gx, gy, az = sign * FRONT_GEAR_X, FRONT_GEAR_Y, FRONT_TIRE_R
        add_strut(f"FrontGearUpper_{side}", (gx - sign * 0.06, gy, az + 0.24), (gx - sign * 0.12, gy, 0.92), 0.072, mats["paint_dark"], parent, 10)
        add_strut(f"FrontGearPiston_{side}", (gx, gy, az), (gx - sign * 0.06, gy, az + 0.28), 0.048, mats["metal_edge"], parent, 10)
        if lod < 2:
            add_strut(f"FrontGearBrace_{side}", (gx, gy, az + 0.22), (gx - sign * 0.24, gy + 0.48, 0.86), 0.038, mats["steel"], parent, 8)
            add_strut(f"FrontGearSideBrace_{side}", (gx - sign * 0.05, gy, az + 0.24), (gx - sign * 0.42, gy, 0.84), 0.034, mats["steel"], parent, 8)
        add_cylinder(f"FrontAxle_{side}", (gx, gy, az), 0.048, 0.048, 0.48, mats["steel"], parent, axis="X", segments=10)
        for w_idx, dx in enumerate((-0.145, 0.145)):
            add_cylinder(f"FrontTire_{side}_{w_idx}", (gx + dx, gy, az), FRONT_TIRE_R, FRONT_TIRE_R, 0.155, mats["rubber"], parent, axis="X", segments=t_segs)
            add_cylinder(
                f"FrontRim_{side}_{w_idx}", (gx + dx, gy, az), FRONT_TIRE_R * 0.48, FRONT_TIRE_R * 0.48, 0.17, mats["steel"], parent, axis="X", segments=max(8, t_segs // 2)
            )

    for sign, side in ((1.0, "Right"), (-1.0, "Left")):
        gx, gy, az = sign * REAR_GEAR_X, REAR_GEAR_Y, REAR_TIRE_R
        add_strut(f"RearGearArm_{side}", (sign * 1.48, gy + 0.18, 0.98), (gx, gy + 0.14, 0.62), 0.068, mats["paint_dark"], parent, 10)
        add_strut(f"RearGearPiston_{side}", (gx, gy + 0.12, 0.62), (gx, gy + 0.08, az + 0.14), 0.044, mats["metal_edge"], parent, 10)
        add_strut(f"RearGearFork_{side}", (gx, gy, az), (gx, gy + 0.08, az + 0.14), 0.042, mats["steel"], parent, 8)
        add_cylinder(f"RearTire_{side}", (gx, gy, az), REAR_TIRE_R, REAR_TIRE_R, 0.19, mats["rubber"], parent, axis="X", segments=t_segs)
        add_cylinder(
            f"RearRim_{side}", (gx, gy, az), REAR_TIRE_R * 0.48, REAR_TIRE_R * 0.48, 0.205, mats["steel"], parent, axis="X", segments=max(8, t_segs // 2)
        )


def build_rotor_assembly(
    name: str,
    hub_pos: Vector,
    tilt_forward_deg: float,
    phase_deg: float,
    mast_drop: float,
    root_parent: bpy.types.Object,
    mats: dict[str, bpy.types.Material],
    lod: int,
) -> bpy.types.Object:
    rotor_node = bpy.data.objects.new(name, None)
    rotor_node.location = hub_pos
    rotor_node.rotation_euler = Euler((-math.radians(tilt_forward_deg), 0.0, 0.0), "XYZ")
    link_object(rotor_node, root_parent)

    h_segs = (18, 12, 8)[lod]
    add_cylinder(f"{name}_Mast", (0.0, 0.0, -mast_drop * 0.5), 0.17, 0.19, mast_drop, mats["steel"], rotor_node, axis="Z", segments=h_segs)
    if lod < 2:
        add_cylinder(f"{name}_Swashplate", (0.0, 0.0, -0.26), 0.38, 0.38, 0.08, mats["steel"], rotor_node, axis="Z", segments=h_segs)
    add_cylinder(f"{name}_Hub", (0.0, 0.0, 0.04), 0.46, 0.40, 0.26, mats["steel"], rotor_node, axis="Z", segments=h_segs)
    add_cylinder(f"{name}_Cap", (0.0, 0.0, 0.22), 0.36, 0.15, 0.13, mats["paint_dark"], rotor_node, axis="Z", segments=h_segs)

    droop_rad = math.radians(1.6)
    blade_start = 0.96
    tip_band_start = ROTOR_RADIUS - 0.85
    tip_band_end = ROTOR_RADIUS - 0.32

    for i in range(3):
        ang = math.radians(phase_deg + i * 120.0)
        dx, dy = math.sin(ang), -math.cos(ang)
        perp_x, perp_y = math.cos(ang), math.sin(ang)

        add_strut(f"{name}_Grip_{i}", (0.0, 0.0, 0.04), (dx * blade_start, dy * blade_start, 0.04), 0.088, mats["steel"], rotor_node, 8)
        if lod == 0:
            add_strut(
                f"{name}_PitchLink_{i}",
                (dx * 0.32 + perp_x * 0.14, dy * 0.32 + perp_y * 0.14, -0.24),
                (dx * 0.46 + perp_x * 0.16, dy * 0.46 + perp_y * 0.16, 0.05),
                0.022,
                mats["metal_edge"],
                rotor_node,
                6,
            )
            add_cylinder(
                f"{name}_Clevis_{i}",
                (dx * 0.62, dy * 0.62, 0.04),
                0.12,
                0.11,
                0.34,
                mats["paint_dark"],
                rotor_node,
                axis="Y",
                segments=10,
                rot_deg=(0.0, 0.0, math.degrees(ang + math.pi)),
            )

        rot_yaw = Euler((0.0, 0.0, ang + math.pi), "XYZ").to_matrix().to_4x4()
        rot_droop = Euler((-droop_rad, 0.0, 0.0), "XYZ").to_matrix().to_4x4()

        for seg_name, r0, r1, seg_mat in (
            (f"{name}_BladeMain_{i}", blade_start, tip_band_start, mats["rotor"]),
            (f"{name}_BladeTipStripe_{i}", tip_band_start, tip_band_end, mats["tip_yellow"]),
            (f"{name}_BladeTipCap_{i}", tip_band_end, ROTOR_RADIUS, mats["rotor"]),
        ):
            length = r1 - r0
            rmid = (r0 + r1) * 0.5
            z_mid = 0.04 - (rmid - blade_start) * math.sin(droop_rad)
            center = Vector((dx * rmid, dy * rmid, z_mid))

            bm = bmesh.new()
            hc, ht = BLADE_CHORD * 0.5, BLADE_THICK * 0.5
            profile = [
                (-hc, 0.0),
                (-hc * 0.58, ht),
                (hc * 0.32, ht * 0.72),
                (hc, 0.0),
                (hc * 0.32, -ht * 0.52),
                (-hc * 0.58, -ht * 0.76),
            ]
            ring0 = [bm.verts.new((px, -length * 0.5, pz)) for (px, pz) in profile]
            ring1 = [bm.verts.new((px, length * 0.5, pz)) for (px, pz) in profile]
            n = len(profile)
            for j in range(n):
                jn = (j + 1) % n
                bm.faces.new((ring0[j], ring0[jn], ring1[jn], ring1[j]))
            bm.faces.new(list(reversed(ring0)))
            bm.faces.new(ring1)
            bmesh.ops.transform(bm, matrix=Matrix.Translation(center) @ rot_yaw @ rot_droop, verts=bm.verts)
            make_mesh_obj(seg_name, bm, seg_mat, rotor_node, smooth=False)

        if lod == 0:
            le_len = tip_band_start - blade_start - 0.15
            rmid = blade_start + 0.08 + le_len * 0.5
            z_mid = 0.04 - (rmid - blade_start) * math.sin(droop_rad)
            le_center = Vector((dx * rmid - perp_x * (BLADE_CHORD * 0.44), dy * rmid - perp_y * (BLADE_CHORD * 0.44), z_mid))
            add_box(
                f"{name}_LeadingEdge_{i}",
                tuple(le_center),
                (0.09, le_len, BLADE_THICK * 0.62),
                mats["metal_edge"],
                rotor_node,
                rot_deg=(-1.6, 0.0, math.degrees(ang + math.pi)),
            )

    return rotor_node


def build_rotor_blur_disc(
    name: str,
    hub_pos: Vector,
    tilt_forward_deg: float,
    root_parent: bpy.types.Object,
    mats: dict[str, bpy.types.Material],
    lod: int,
) -> bpy.types.Object:
    """Translucent coned radial motion-blur rotor disc + hub/mast for standalone RTS flight swap."""
    blur_group = bpy.data.objects.new(name, None)
    blur_group.location = hub_pos
    blur_group.rotation_euler = Euler((-math.radians(tilt_forward_deg), 0.0, 0.0), "XYZ")
    link_object(blur_group, root_parent)

    # Keep mast + hub cap present when swapping from 3D blades to flight blur disc
    h_segs = (14, 10, 8)[lod]
    add_cylinder(f"{name}_Mast", (0.0, 0.0, -0.48), 0.17, 0.19, 0.96, mats["steel"], blur_group, axis="Z", segments=h_segs)
    add_cylinder(f"{name}_Hub", (0.0, 0.0, 0.04), 0.46, 0.40, 0.26, mats["steel"], blur_group, axis="Z", segments=h_segs)
    add_cylinder(f"{name}_Cap", (0.0, 0.0, 0.22), 0.36, 0.15, 0.13, mats["paint_dark"], blur_group, axis="Z", segments=h_segs)

    segs = (36, 28, 20)[lod]
    bm = bmesh.new()
    cone_z = ROTOR_RADIUS * math.sin(math.radians(2.2))
    r_inner = 0.42
    inner_verts = []
    outer_verts = []
    for i in range(segs):
        a = i * math.tau / segs
        ca, sa = math.cos(a), math.sin(a)
        inner_verts.append(bm.verts.new((ca * r_inner, sa * r_inner, 0.05)))
        outer_verts.append(bm.verts.new((ca * ROTOR_RADIUS, sa * ROTOR_RADIUS, 0.05 + cone_z)))

    uv_layer = bm.loops.layers.uv.verify()
    for i in range(segs):
        ni = (i + 1) % segs
        f = bm.faces.new((inner_verts[i], outer_verts[i], outer_verts[ni], inner_verts[ni]))
        for loop in f.loops:
            co = loop.vert.co
            u = 0.5 + (co.x / ROTOR_RADIUS) * 0.5
            v = 0.5 + (co.y / ROTOR_RADIUS) * 0.5
            loop[uv_layer].uv = (u, v)

    make_mesh_obj(f"{name}_Disc", bm, mats["rotor_blur"], blur_group, smooth=True, split_angle=None, auto_uv=False)
    return blur_group


def build_rear_ramp(root_parent: bpy.types.Object, mats: dict[str, bpy.types.Material], lod: int) -> bpy.types.Object:
    """Articulated hollow-bay cargo ramp: exterior stiffeners + interior drive-on vehicle treadways.

    Pivoted at RAMP_HINGE (0, -3.55, 0.78). Rotating local +X by +42 deg lowers the ramp flush to Z = 0.
    """
    ramp_node = bpy.data.objects.new("RearRamp", None)
    ramp_node.location = RAMP_HINGE
    link_object(ramp_node, root_parent)

    ramp_len = 2.66
    pitch_deg = RAMP_CLOSED_PITCH_DEG
    pitch_rad = math.radians(pitch_deg)
    mid_y = -0.5 * ramp_len * math.cos(pitch_rad)
    mid_z = 0.5 * ramp_len * math.sin(pitch_rad)

    # Structural thick ramp slab (outer paint underside + dark steel upper drive surface)
    add_box(
        "RearRampBody",
        (0.0, mid_y, mid_z),
        (BAY_HALF_W * 1.94, ramp_len, 0.14),
        mats["paint_dark"],
        ramp_node,
        rot_deg=(-pitch_deg, 0.0, 0.0),
        bevel=0.02,
        smooth=True,
    )
    # Interior drive-on steel deck surface (visible when ramp lowers!)
    add_box(
        "RearRampInnerDeck",
        (0.0, mid_y, mid_z + 0.068),
        (BAY_HALF_W * 1.84, ramp_len - 0.08, 0.03),
        mats["interior_floor"],
        ramp_node,
        rot_deg=(-pitch_deg, 0.0, 0.0),
    )
    if lod < 2:
        # Interior vehicle treadway guides on ramp deck
        for sx in (-0.54, 0.54):
            add_box(
                f"RampInnerTread_{sx:+.2f}",
                (sx, mid_y, mid_z + 0.09),
                (0.32, ramp_len - 0.16, 0.022),
                mats["steel"],
                ramp_node,
                rot_deg=(-pitch_deg, 0.0, 0.0),
            )
    if lod == 0:
        # Exterior underside stiffener ribs
        for rx in (-0.56, 0.0, 0.56):
            add_box(
                f"RearRampRib_{rx:+.2f}",
                (rx, mid_y, mid_z - 0.085),
                (0.09, ramp_len - 0.24, 0.045),
                mats["paint"],
                ramp_node,
                rot_deg=(-pitch_deg, 0.0, 0.0),
            )
        # Toe loading flap extensions at the tip of the ramp
        tip_y = -ramp_len * math.cos(pitch_rad)
        tip_z = ramp_len * math.sin(pitch_rad)
        add_box(
            "RearRampToeFlap",
            (0.0, tip_y - 0.10, tip_z + 0.05),
            (BAY_HALF_W * 1.86, 0.24, 0.055),
            mats["steel"],
            ramp_node,
            rot_deg=(-pitch_deg, 0.0, 0.0),
        )
    return ramp_node


def build_details_and_markings(parent: bpy.types.Object, mats: dict[str, bpy.types.Material], lod: int):
    """Pitot probes, VHF/UHF & HF towel-bar antennas, beacons, and conformal US Army stencil markings."""
    for sign, side in ((1.0, "Right"), (-1.0, "Left")):
        add_strut(f"Pitot_{side}", (sign * 0.38, 7.50, 1.80), (sign * 0.42, 8.02, 1.84), 0.018, mats["steel"], parent, 6)

    add_box("DorsalAntenna", (0.0, 0.55, 3.72), (0.04, 0.26, 0.50), mats["paint_dark"], parent, rot_deg=(18.0, 0.0, 0.0))
    add_box("TailFinAntenna", (0.0, -7.76, 4.74), (0.05, 0.30, 0.44), mats["paint_dark"], parent, rot_deg=(-12.0, 0.0, 0.0))

    if lod < 2:
        add_cylinder("BeaconTop", (0.0, 4.22, 4.20), 0.055, 0.045, 0.10, mats["nav_red"], parent, axis="Z", segments=10)
        add_cylinder("BeaconTail", (0.0, -7.68, 5.20), 0.055, 0.045, 0.10, mats["nav_red"], parent, axis="Z", segments=10)
        add_cylinder("BeaconBelly", (0.0, -1.20, BELLY_Z - 0.05), 0.055, 0.045, 0.09, mats["nav_red"], parent, axis="Z", segments=10)

    if lod == 0:
        add_strut("HFAntennaRail", (1.42, -0.80, 2.74), (1.42, -3.70, 2.74), 0.018, mats["steel"], parent, 6)
        for sy in (-0.85, -2.25, -3.65):
            add_strut(f"HFAntennaStand_{sy:.2f}", (1.24, sy, 2.74), (1.42, sy, 2.74), 0.016, mats["steel"], parent, 6)
        add_box("BellyAntenna1", (0.0, 4.55, BELLY_Z - 0.18), (0.03, 0.20, 0.34), mats["paint_dark"], parent, rot_deg=(-20.0, 0.0, 0.0))
        add_box("BellyAntenna2", (0.0, -2.80, BELLY_Z - 0.15), (0.03, 0.18, 0.28), mats["paint_dark"], parent, rot_deg=(-16.0, 0.0, 0.0))

    if lod < 2:
        decal_mat = mats["decal"]
        for sign, side in ((1.0, "Right"), (-1.0, "Left")):
            hint = Vector((sign, 0.0, 0.0))
            y_left, y_right = (-2.05, 2.15) if sign > 0 else (2.15, -2.05)
            x_sp = sign * (SPONSON_WIDTH * 0.5)
            add_decal_quad(
                f"Decal_USArmy_{side}",
                [Vector((x_sp, y_left, 1.04)), Vector((x_sp, y_right, 1.04)), Vector((x_sp, y_right, 1.62)), Vector((x_sp, y_left, 1.62))],
                (0.05, 0.55, 0.95, 0.92),
                0.012,
                decal_mat,
                parent,
                hint,
            )

            # Rear pylon tail code seated cleanly proud on tapered airfoil pylon flank
            # At Z=3.98, airfoil half_w=0.68 at mid (Y=-6.0) tapering toward -7.0; at Z=4.68 half_w=0.56
            py_aft, py_fwd = -6.88, -5.66
            x_bot_aft, x_bot_fwd = 0.65, 0.72
            x_top_aft, x_top_fwd = 0.54, 0.60
            pts_tail = (
                [
                    Vector((sign * x_bot_aft, py_aft, 3.88)),
                    Vector((sign * x_bot_fwd, py_fwd, 3.88)),
                    Vector((sign * x_top_fwd, py_fwd, 4.66)),
                    Vector((sign * x_top_aft, py_aft, 4.66)),
                ]
                if sign > 0
                else [
                    Vector((sign * x_bot_fwd, py_fwd, 3.88)),
                    Vector((sign * x_bot_aft, py_aft, 3.88)),
                    Vector((sign * x_top_aft, py_aft, 4.66)),
                    Vector((sign * x_top_fwd, py_fwd, 4.66)),
                ]
            )
            add_decal_quad(
                f"Decal_TailSerial_{side}",
                pts_tail,
                (0.03, 0.05, 0.46, 0.44),
                0.025,
                decal_mat,
                parent,
                Vector((sign, -0.10, 0.16)),
            )

            st_l, st_r = (-4.15, -3.05) if sign > 0 else (-3.05, -4.15)
            add_decal_quad(
                f"Decal_StarBar_{side}",
                [
                    Vector((sign * 1.26, st_l, 2.08)),
                    Vector((sign * 1.26, st_r, 2.08)),
                    Vector((sign * 1.26, st_r, 2.66)),
                    Vector((sign * 1.26, st_l, 2.66)),
                ],
                (0.49, 0.06, 0.76, 0.46),
                0.015,
                decal_mat,
                parent,
                hint,
            )

            # "DANGER" exhaust warning stencil clearly forward of aft flare pod
            dn_l, dn_r = (-3.15, -2.35) if sign > 0 else (-2.35, -3.15)
            add_decal_quad(
                f"Decal_Danger_{side}",
                [Vector((x_sp, dn_l, 1.12)), Vector((x_sp, dn_r, 1.12)), Vector((x_sp, dn_r, 1.50)), Vector((x_sp, dn_l, 1.50))],
                (0.76, 0.08, 0.99, 0.42),
                0.016,
                decal_mat,
                parent,
                hint,
            )


# ---------------------------------------------------------------------------
# Baked Vertex Shading (AO + Oil Streaks + Exhaust Soot + Belly Grime)
# ---------------------------------------------------------------------------


def bake_deadground_vertex_colors(obj: bpy.types.Object, is_rotor: bool = False):
    mesh = obj.data
    color_attr = mesh.color_attributes.get("Col") or mesh.color_attributes.new(name="Col", type="BYTE_COLOR", domain="CORNER")
    mesh.color_attributes.active_color = color_attr

    world_m = obj.matrix_world
    norm_m = world_m.to_3x3()
    verts = mesh.vertices
    v_cols = np.ones((len(verts), 4), dtype=np.float32)

    for i, v in enumerate(verts):
        wp = world_m @ v.co
        wn = (norm_m @ v.normal).normalized()

        if is_rotor:
            r_local = math.hypot(v.co.x, v.co.y)
            shade = min(1.06, max(0.76, 0.80 + 0.24 * (r_local / ROTOR_RADIUS) + 0.06 * max(0.0, wn.z)))
            v_cols[i] = (shade, shade, shade, 1.0)
            continue

        # Darken hollow cargo bay interior naturally for strong depth when ramp opens
        in_bay = (-5.8 < wp.y < -0.5) and (abs(wp.x) < BAY_HALF_W + 0.02) and (BAY_FLOOR_Z - 0.06 < wp.z < BAY_CEIL_Z + 0.04)
        if in_bay:
            depth_t = max(0.0, min(1.0, (-wp.y - 0.5) / 3.5))
            bay_ao = 0.52 + 0.36 * depth_t + 0.10 * max(0.0, wn.z)
            v_cols[i] = (bay_ao, bay_ao, bay_ao, 1.0)
            continue

        ao = 0.80 + 0.22 * (0.5 * wn.z + 0.5)
        if wp.z < 1.05 and abs(wp.x) > 1.00 and -4.8 < wp.y < 4.8:
            ao *= 0.74 + 0.26 * max(0.0, (wp.z - 0.55) / 0.50)

        sponson_seam = math.exp(-((wp.z - 1.94) ** 2) / (0.14 ** 2)) * math.exp(-((abs(wp.x) - 1.26) ** 2) / (0.14 ** 2))
        if -4.6 < wp.y < 4.6:
            ao *= 1.0 - 0.24 * sponson_seam

        spine_seam = math.exp(-((wp.z - 3.12) ** 2) / (0.12 ** 2)) * math.exp(-((abs(wp.x) - 0.44) ** 2) / (0.14 ** 2))
        if -4.5 < wp.y < 4.2:
            ao *= 1.0 - 0.18 * spine_seam

        eng_trough = (
            math.exp(-((wp.z - 3.04) ** 2) / (0.45 ** 2))
            * math.exp(-((wp.y + 5.42) ** 2) / (1.35 ** 2))
            * math.exp(-((abs(wp.x) - 1.00) ** 2) / (0.36 ** 2))
        )
        ao *= 1.0 - 0.28 * eng_trough

        # Realistic hydraulic/transmission oil darkening below forward & rear rotor masts
        oil_fwd = math.exp(-((wp.y - 4.75) ** 2) / (0.55 ** 2)) * math.exp(-((wp.z - 3.70) ** 2) / (0.65 ** 2)) * 0.12
        oil_aft = math.exp(-((wp.y + 6.40) ** 2) / (0.65 ** 2)) * math.exp(-((wp.z - 4.40) ** 2) / (0.85 ** 2)) * 0.14
        ao *= 1.0 - (oil_fwd + oil_aft)

        # Authentic T55 turbine exhaust soot plumes trailing aft along rear pylon flanks
        soot = 0.0
        if -7.85 < wp.y < -6.30:
            dy = -6.75 - wp.y
            if dy > -0.25:
                dz = wp.z - 3.06
                dx = abs(wp.x) - 1.05
                plume = (
                    math.exp(-(dz * dz) / (0.55 ** 2))
                    * math.exp(-(dx * dx) / (0.72 ** 2))
                    * math.exp(-(max(0.0, dy) ** 2) / (1.30 ** 2))
                )
                soot = 0.46 * plume

        up_bleach = 1.0 + 0.06 * max(0.0, wn.z) * min(1.0, max(0.0, (wp.z - 2.2) / 2.2))
        belly_grime = max(0.0, min(1.0, (1.15 - wp.z) / 0.85)) * 0.11
        micro = 1.0 + 0.02 * math.sin(wp.x * 5.7 + wp.y * 3.9 + wp.z * 7.3)

        shade = max(0.30, min(1.10, ao * up_bleach * micro * (1.0 - soot)))
        r = max(0.22, min(1.10, shade * (1.0 + belly_grime * 0.42 - soot * 0.08)))
        g = max(0.22, min(1.10, shade * (1.0 + belly_grime * 0.34 - soot * 0.08)))
        b = max(0.22, min(1.10, shade * (1.0 + belly_grime * 0.12 - soot * 0.04)))
        v_cols[i] = (r, g, b, 1.0)

    loop_vids = np.empty(len(mesh.loops), dtype=np.int32)
    mesh.loops.foreach_get("vertex_index", loop_vids)
    color_attr.data.foreach_set("color", v_cols[loop_vids].ravel())


# ---------------------------------------------------------------------------
# Articulated Mesh Merge, 3-LOD Export & Multi-State Studio Renderer
# ---------------------------------------------------------------------------


def merge_children_into_articulated_mesh(group_empty: bpy.types.Object) -> bpy.types.Object:
    bpy.context.view_layer.update()
    children = [obj for obj in list(group_empty.children) if obj.type == "MESH"]
    if not children:
        return group_empty

    depsgraph = bpy.context.evaluated_depsgraph_get()
    inv_group = group_empty.matrix_world.inverted()

    combined_bm = bmesh.new()
    uv_dst = combined_bm.loops.layers.uv.verify()
    mat_list: list[bpy.types.Material] = []
    mat_index_map: dict[str, int] = {}

    for child in children:
        eval_obj = child.evaluated_get(depsgraph)
        eval_mesh = eval_obj.to_mesh()
        local_xform = inv_group @ child.matrix_world

        slot_remap = []
        for mat in child.data.materials:
            if mat is None:
                slot_remap.append(0)
                continue
            orig_mat = bpy.data.materials[mat.name]
            if orig_mat.name not in mat_index_map:
                mat_index_map[orig_mat.name] = len(mat_list)
                mat_list.append(orig_mat)
            slot_remap.append(mat_index_map[orig_mat.name])

        vert_map = [combined_bm.verts.new(local_xform @ v.co) for v in eval_mesh.vertices]
        uv_src = eval_mesh.uv_layers.active.data if eval_mesh.uv_layers.active else None

        for poly in eval_mesh.polygons:
            verts = [vert_map[vi] for vi in poly.vertices]
            try:
                f = combined_bm.faces.new(verts)
                f.smooth = poly.use_smooth
                f.material_index = slot_remap[poly.material_index] if slot_remap else 0
                if uv_src is not None:
                    for loop, src_li in zip(f.loops, poly.loop_indices):
                        loop[uv_dst].uv = uv_src[src_li].uv
            except ValueError:
                pass
        eval_obj.to_mesh_clear()

    name, parent = group_empty.name, group_empty.parent
    loc, rot = group_empty.location.copy(), group_empty.rotation_euler.copy()

    for child in children:
        mesh_data = child.data
        bpy.data.objects.remove(child, do_unlink=True)
        if mesh_data.users == 0:
            bpy.data.meshes.remove(mesh_data)
    bpy.data.objects.remove(group_empty, do_unlink=True)

    new_mesh = bpy.data.meshes.new(name)
    combined_bm.to_mesh(new_mesh)
    combined_bm.free()
    for mat in mat_list:
        new_mesh.materials.append(mat)

    new_obj = bpy.data.objects.new(name, new_mesh)
    new_obj.location = loc
    new_obj.rotation_euler = rot
    link_object(new_obj, parent)
    return new_obj


def build_chinook(lod: int = 0) -> dict[str, bpy.types.Object]:
    clear_scene()
    mats = create_materials()

    root = bpy.data.objects.new("CH47", None)
    link_object(root)

    fuselage = bpy.data.objects.new("Fuselage", None)
    link_object(fuselage, root)

    build_fuselage_and_cargo_bay(fuselage, mats, lod)
    build_sponsons(fuselage, mats, lod)
    build_pylons_and_engines(fuselage, mats, lod)
    build_cockpit_and_windows(fuselage, mats, lod)
    build_door_guns(fuselage, mats, lod)
    build_landing_gear(fuselage, mats, lod)
    build_details_and_markings(fuselage, mats, lod)

    front_rotor = build_rotor_assembly("FrontRotor", FRONT_HUB, FRONT_MAST_TILT_DEG, 18.0, 0.96, root, mats, lod)
    rear_rotor = build_rotor_assembly("RearRotor", REAR_HUB, REAR_MAST_TILT_DEG, -18.0, 0.96, root, mats, lod)
    front_blur = build_rotor_blur_disc("FrontRotorBlur", FRONT_HUB, FRONT_MAST_TILT_DEG, root, mats, lod)
    rear_blur = build_rotor_blur_disc("RearRotorBlur", REAR_HUB, REAR_MAST_TILT_DEG, root, mats, lod)
    rear_ramp = build_rear_ramp(root, mats, lod)

    fuse_obj = merge_children_into_articulated_mesh(fuselage)
    fr_obj = merge_children_into_articulated_mesh(front_rotor)
    rr_obj = merge_children_into_articulated_mesh(rear_rotor)
    frb_obj = merge_children_into_articulated_mesh(front_blur)
    rrb_obj = merge_children_into_articulated_mesh(rear_blur)
    ramp_obj = merge_children_into_articulated_mesh(rear_ramp)

    bpy.context.view_layer.update()
    bake_deadground_vertex_colors(fuse_obj, is_rotor=False)
    bake_deadground_vertex_colors(fr_obj, is_rotor=True)
    bake_deadground_vertex_colors(rr_obj, is_rotor=True)
    bake_deadground_vertex_colors(frb_obj, is_rotor=True)
    bake_deadground_vertex_colors(rrb_obj, is_rotor=True)
    bake_deadground_vertex_colors(ramp_obj, is_rotor=False)
    bpy.context.view_layer.update()

    return {
        "root": root,
        "Fuselage": fuse_obj,
        "FrontRotor": fr_obj,
        "RearRotor": rr_obj,
        "FrontRotorBlur": frb_obj,
        "RearRotorBlur": rrb_obj,
        "RearRamp": ramp_obj,
    }


def collect_mesh_stats() -> dict:
    depsgraph = bpy.context.evaluated_depsgraph_get()
    total_tris = total_verts = mesh_count = 0
    materials = set()
    for obj in bpy.data.objects:
        if obj.type != "MESH":
            continue
        mesh_count += 1
        eval_obj = obj.evaluated_get(depsgraph)
        mesh = eval_obj.to_mesh()
        mesh.calc_loop_triangles()
        total_tris += len(mesh.loop_triangles)
        total_verts += len(mesh.vertices)
        for mat in obj.data.materials:
            if mat:
                materials.add(mat.name)
        eval_obj.to_mesh_clear()
    return {
        "triangles": total_tris,
        "vertices": total_verts,
        "mesh_parts": mesh_count,
        "materials": len(materials),
    }


def export_all_lods(out_dir: Path) -> tuple[dict, dict[str, bpy.types.Object]]:
    out_dir.mkdir(parents=True, exist_ok=True)
    lod_entries = []
    roles = ("close", "rts", "distant")
    parts_lod0 = {}

    for lod in (2, 1, 0):
        parts = build_chinook(lod)
        if lod == 0:
            parts_lod0 = parts
        fname = "ch47.glb" if lod == 0 else f"ch47_lod{lod}.glb"
        glb_path = out_dir / fname
        bpy.ops.export_scene.gltf(
            filepath=str(glb_path),
            export_format="GLB",
            export_yup=True,
            export_apply=True,
            export_materials="EXPORT",
            export_image_format="AUTO",
        )
        stats = collect_mesh_stats()
        lod_entries.append(
            {
                "file": fname,
                "triangles": stats["triangles"],
                "vertices": stats["vertices"],
                "mesh_parts": stats["mesh_parts"],
                "materials": stats["materials"],
                "bytes": glb_path.stat().st_size,
                "lod": lod,
                "role": roles[lod],
            }
        )

    lod_entries.sort(key=lambda e: e["lod"])
    manifest = {
        "asset": "CH-47D Chinook tandem-rotor heavy-lift transport helicopter (Dead Ground v3 production study)",
        "configuration": "Official Boeing CH-47D / TM 55-1520-240-10 proportions with integral smooth radome, thin-mullion aquarium cockpit, hollow openable cargo bay & interior, aerodynamic rear pylon, EAPS/heat-shielded T55 turbines, twin M60D door guns, translucent RTS rotor blur discs, frame-aligned CARC skin, baked vertex AO/oil/soot, and US Army stencil decals",
        "units": "meters",
        "up": "+Y",
        "forward": "-Z",
        "authoring_tool": f"Blender {bpy.app.version_string}",
        "vertex_colors": "rgb: baked ambient occlusion x transmission oil streaks x T55 exhaust soot x belly grime",
        "articulated_nodes": [
            "Fuselage",
            "FrontRotor",
            "RearRotor",
            "FrontRotorBlur",
            "RearRotorBlur",
            "RearRamp",
        ],
        "animation_hints": {
            "FrontRotor": "spin around local +Y; hide or fade during fast cruise",
            "RearRotor": "counter-spin around local -Y; hide or fade during fast cruise",
            "FrontRotorBlur": "show and spin around local +Y during flight",
            "RearRotorBlur": "show and counter-spin around local -Y during flight",
            "RearRamp": "rotate around local +X from 0 deg (closed) to +42 deg (ground loading)",
        },
        "reference_anchors": {
            "fuselage_length_meters": FUSELAGE_LENGTH,
            "cabin_width_meters": CABIN_WIDTH,
            "width_over_sponsons_meters": SPONSON_WIDTH,
            "belly_clearance_meters": BELLY_Z,
            "height_to_rear_rotor_hub_meters": round(REAR_HUB.z, 3),
            "height_to_front_rotor_hub_meters": round(FRONT_HUB.z, 3),
            "rotor_diameter_meters": round(ROTOR_RADIUS * 2.0, 3),
            "rotor_hub_spacing_meters": round(FRONT_HUB.y - REAR_HUB.y, 3),
            "forward_mast_tilt_deg": FRONT_MAST_TILT_DEG,
            "rear_mast_tilt_deg": REAR_MAST_TILT_DEG,
            "wheelbase_meters": round(FRONT_GEAR_Y - REAR_GEAR_Y, 3),
            "front_track_meters": round(FRONT_GEAR_X * 2.0, 3),
            "rear_track_meters": round(REAR_GEAR_X * 2.0, 3),
        },
        "lods": lod_entries,
    }
    (out_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest, parts_lod0


def setup_studio_and_render(render_dir: Path, parts: dict[str, bpy.types.Object], compare_lineup: bool = True):
    """Render landed hero/profile, open-ramp hollow cargo bay, airborne blur-disc flight, and M60/BTR-70 lineup."""
    render_dir.mkdir(parents=True, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = 1280
    scene.render.resolution_y = 720
    scene.render.image_settings.file_format = "PNG"
    scene.view_settings.view_transform = "AgX"

    world = bpy.data.worlds.new("StudioWorld")
    scene.world = world
    if not world.node_tree:
        world.use_nodes = True
    bg = world.node_tree.nodes.get("Background")
    if bg:
        bg.inputs["Color"].default_value = (0.64, 0.69, 0.75, 1.0)
        bg.inputs["Strength"].default_value = 0.90

    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=52.0)
    ground_mat = make_solid_material("StudioGround", "9ea3a6", metallic=0.0, roughness=0.95, use_vcol=False)
    make_mesh_obj("StudioGround", bm, ground_mat, None, smooth=False, split_angle=None)

    def add_sun(name: str, rot_deg, energy: float, color=(1.0, 0.97, 0.92)):
        ldata = bpy.data.lights.new(name=name, type="SUN")
        ldata.energy = energy
        ldata.color = color
        lobj = bpy.data.objects.new(name=name, object_data=ldata)
        lobj.rotation_euler = Euler(tuple(math.radians(a) for a in rot_deg), "XYZ")
        link_object(lobj)

    add_sun("KeySun", (48.0, 14.0, -38.0), 3.6, (1.0, 0.96, 0.90))
    add_sun("FillSun", (40.0, -12.0, 140.0), 1.35, (0.80, 0.88, 1.0))
    add_sun("RimSun", (28.0, 0.0, 62.0), 0.95, (0.95, 0.97, 1.0))

    cam_data = bpy.data.cameras.new("StudioCam")
    cam_obj = bpy.data.objects.new("StudioCam", cam_data)
    link_object(cam_obj)
    scene.camera = cam_obj

    def look_at(pos, target, ortho_scale=None, lens=46.0):
        cam_obj.location = Vector(pos)
        direction = Vector(target) - Vector(pos)
        cam_obj.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
        if ortho_scale is not None:
            cam_data.type = "ORTHO"
            cam_data.ortho_scale = ortho_scale
        else:
            cam_data.type = "PERSP"
            cam_data.lens = lens

    def set_flight_mode(blur_on: bool, ramp_open_deg: float = 0.0, hover_z: float = 0.0):
        parts["FrontRotorBlur"].hide_render = not blur_on
        parts["RearRotorBlur"].hide_render = not blur_on
        parts["FrontRotor"].hide_render = blur_on
        parts["RearRotor"].hide_render = blur_on
        parts["RearRamp"].rotation_euler = Euler((math.radians(ramp_open_deg), 0.0, 0.0), "XYZ")
        parts["root"].location = Vector((0.0, 0.0, hover_z))
        parts["root"].rotation_euler = Euler((math.radians(-4.5 if hover_z > 0 else 0.0), 0.0, 0.0), "XYZ")
        bpy.context.view_layer.update()

    # 1. Landed views (3D blades visible, blur discs hidden, ramp closed)
    set_flight_mode(blur_on=False, ramp_open_deg=0.0, hover_z=0.0)
    for fname, pos, target, ortho, lens in (
        ("ch47_v3_hero.png", (15.5, 14.2, 6.8), (0.0, 0.3, 2.4), None, 45.0),
        ("ch47_v3_side.png", (28.0, -0.5, 2.8), (0.0, -0.5, 2.8), 33.0, 50.0),
        ("ch47_v3_front.png", (0.0, 24.0, 2.5), (0.0, 0.0, 2.5), 20.5, 50.0),
    ):
        look_at(pos, target, ortho, lens)
        scene.render.filepath = str(render_dir / fname)
        bpy.ops.render.render(write_still=True)

    # 2. Rear 3/4 showcase with RearRamp OPEN showing hollow cargo bay, interior floor & troop benches!
    set_flight_mode(blur_on=False, ramp_open_deg=RAMP_OPEN_SWING_DEG, hover_z=0.0)
    look_at((9.5, -14.5, 4.2), (0.0, -3.2, 1.8), None, 46.0)
    scene.render.filepath = str(render_dir / "ch47_v3_rear_open.png")
    bpy.ops.render.render(write_still=True)

    # 3. Airborne RTS flight showcase (translucent motion-blur discs active + pitched forward in cruise!)
    set_flight_mode(blur_on=True, ramp_open_deg=0.0, hover_z=3.8)
    look_at((19.5, -16.5, 22.0), (0.0, -0.2, 4.2), None, 44.0)
    scene.render.filepath = str(render_dir / "ch47_v3_flight.png")
    bpy.ops.render.render(write_still=True)

    # 4. 1:1 Scale & Style Lineup alongside Dead Ground's real M60 & BTR-70 (ramp open on landing zone!)
    set_flight_mode(blur_on=False, ramp_open_deg=RAMP_OPEN_SWING_DEG, hover_z=0.0)
    if compare_lineup:
        imported_any = False
        for path, offset in (
            (DEFAULT_M60_GLB, Vector((6.2, 1.5, 0.0))),
            (DEFAULT_BTR70_GLB, Vector((-6.2, 1.5, 0.0))),
        ):
            if not path.exists():
                continue
            before = set(bpy.data.objects)
            bpy.ops.import_scene.gltf(filepath=str(path))
            added = [o for o in bpy.data.objects if o not in before]
            for o in added:
                if o.parent is None:
                    o.location += offset
            imported_any = True

        if imported_any:
            bpy.context.view_layer.update()
            for fname, pos, target, lens in (
                ("ch47_v3_lineup_hero.png", (19.5, 18.8, 8.4), (0.0, 0.2, 2.2), 42.0),
                ("ch47_v3_lineup_rts.png", (16.5, -18.5, 22.0), (0.0, 0.4, 1.8), 42.0),
            ):
                look_at(pos, target, None, lens)
                scene.render.filepath = str(render_dir / fname)
                bpy.ops.render.render(write_still=True)


def parse_args():
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser(description="Generate Dead Ground CH-47D Chinook GLB v3 (3 LODs)")
    parser.add_argument("--out-dir", type=Path, default=DEFAULT_OUT_DIR)
    parser.add_argument("--render-dir", type=Path, default=None)
    parser.add_argument("--compare-lineup", action="store_true")
    return parser.parse_args(argv)


def main():
    args = parse_args()
    manifest, parts_lod0 = export_all_lods(args.out_dir)
    print(json.dumps(manifest, indent=2))
    if args.render_dir is not None:
        setup_studio_and_render(args.render_dir, parts_lod0, compare_lineup=args.compare_lineup)


if __name__ == "__main__":
    main()
