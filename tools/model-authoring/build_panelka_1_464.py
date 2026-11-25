"""Procedural modular Soviet 5-story Series 1-464 panel apartment building (panelka).

Exports a single compact GLB containing reusable architectural modules calibrated to
real-world Soviet Series 1-464 dimensions (14.4 m pod'ezd width x 11.8 m depth,
2.75 m floor-to-floor height, 5 floors = 13.75 m cornice / 14.17 m parapet,
2.1 m central rubble saddle with 2.6-3.4 m jagged ground-floor ruin corner walls):
  - Section_Intact_LOD0 / LOD1 / LOD2: merged 5-story intact section for 1-draw-call MultiMesh.
  - Floor_Ground_Intact: plinth, Soviet entrance porch with concrete visor, stairwell
    window, recessed 3-pane casement windows, and precast panel expansion seams.
  - Floor_Mid_Intact: repeating precast residential floor with cantilevered balconies,
    painted steel railings, recessed casement windows, and stairwell window.
  - Floor_Breached: asymmetric battle-scarred floor with blown-out facade breach,
    soot plumes above shattered windows, exposed interior cavity, and bent rebar.
  - Top_Jagged_Cap: asymmetric war-torn broken upper concrete skeleton placed on top of
    partially collapsed buildings so partial ruins have a jagged urban combat skyline.
  - Roof_Cap: bitumen flat roof slab, perimeter parapet, concrete ventilation shafts,
    and Soviet VHF/UHF TV antenna mast.
  - Rubble_Mound / Rubble_Mound_B: two asymmetric variants of impassable war-torn
    ground-floor ruins with jagged corner wall stubs (2.6-3.4 m), broken precast slabs,
    scorch craters, and twisted rebar across a 2.1 m central rubble choke.
  - Debris_Slab_A, Debris_Slab_B, Debris_Chunk_C: single-material low-poly meshes for
    client-side MultiMesh debris physics.

Coordinates: meters, +Y up, +Z front courtyard facade, +X section chaining axis,
horizontal center at (0, 0), module base at Y = 0.
"""

import argparse
import json
import math
from pathlib import Path
import sys

import numpy as np

_THIS_DIR = Path(__file__).resolve().parent
if str(_THIS_DIR) not in sys.path:
    sys.path.insert(0, str(_THIS_DIR))

from vehicle_kit import (  # noqa: E402
    GltfModel,
    obox,
    rod,
    save_model,
)

DEFAULT_OUTPUT_DIR = _THIS_DIR.parents[1] / "src/Game.Godot/Assets/Buildings/Panelka1464"

# Authentic Soviet Series 1-464 proportions (1 pod'ezd = 14.4 m x 11.8 m, floor = 2.75 m)
SECTION_WIDTH = 14.4
SECTION_DEPTH = 11.8
HALF_W = SECTION_WIDTH * 0.5
HALF_D = SECTION_DEPTH * 0.5
FLOOR_HEIGHT = 2.75
FLOORS_TOTAL = 5
RUBBLE_HEIGHT = 1.65
ROOF_PARAPET_HEIGHT = 0.40

BAY_CENTERS_X = (-5.4, -1.8, 1.8, 5.4)
BAY_WIDTH = 3.6

MODULE_NODES = (
    "Section_Intact_LOD0",
    "Section_Intact_LOD1",
    "Section_Intact_LOD2",
    "Floor_Ground_Intact",
    "Floor_Mid_Intact",
    "Floor_Breached",
    "Top_Jagged_Cap",
    "Roof_Cap",
    "Rubble_Mound",
    "Rubble_Mound_B",
    "Debris_Slab_A",
    "Debris_Slab_B",
    "Debris_Chunk_C",
)


def _ortho_axes(yaw_rad, pitch_rad, roll_rad):
    cy, sy = math.cos(yaw_rad), math.sin(yaw_rad)
    cp, sp = math.cos(pitch_rad), math.sin(pitch_rad)
    cr, sr = math.cos(roll_rad), math.sin(roll_rad)
    ry = np.array([[cy, 0.0, sy], [0.0, 1.0, 0.0], [-sy, 0.0, cy]])
    rx = np.array([[1.0, 0.0, 0.0], [0.0, cp, -sp], [0.0, sp, cp]])
    rz = np.array([[cr, -sr, 0.0], [sr, cr, 0.0], [0.0, 0.0, 1.0]])
    rot = ry @ rx @ rz
    return (
        tuple(float(v) for v in rot[:, 0]),
        tuple(float(v) for v in rot[:, 1]),
        tuple(float(v) for v in rot[:, 2]),
    )


def _add_grounded_obox(m, name, center, axes, half, mat, parent, tint=1.0, min_y=0.02):
    """Place an oriented box lifted if needed so no corner dips below min_y."""
    lowest_dy = 0.0
    for sx in (-1.0, 1.0):
        for sy in (-1.0, 1.0):
            for sz in (-1.0, 1.0):
                dy = sx * axes[0][1] * half[0] + sy * axes[1][1] * half[1] + sz * axes[2][1] * half[2]
                if dy < lowest_dy:
                    lowest_dy = dy
    cy = max(center[1], min_y - lowest_dy)
    obox(m, name, (center[0], cy, center[2]), axes, half, mat, parent, tint=tint)


def _add_box(m, name, size, center, mat, parent, tint=1.0):
    m.add_box(name, size, center, mat, parent, tint=tint)


def _add_window_z(m, prefix, cx, cy, z_face, sign_z, w, h, mats, parent, broken=False, scorched=False):
    """Recessed 3-pane Soviet wooden casement window on a +/-Z facade."""
    z_frame = z_face + sign_z * 0.012
    z_glass = z_face + sign_z * 0.024
    z_mullion = z_face + sign_z * 0.036

    _add_box(m, f"{prefix}_Frame", (w, h, 0.03), (cx, cy, z_frame), mats["frame"], parent)

    if not broken:
        _add_box(m, f"{prefix}_Glass", (w - 0.13, h - 0.13, 0.02), (cx, cy, z_glass), mats["glass"], parent)
        _add_box(m, f"{prefix}_M1", (0.045, h - 0.12, 0.02), (cx - w * 0.16, cy, z_mullion), mats["frame"], parent)
        _add_box(m, f"{prefix}_M2", (0.045, h - 0.12, 0.02), (cx + w * 0.18, cy, z_mullion), mats["frame"], parent)
        _add_box(
            m,
            f"{prefix}_Fort",
            (w * 0.30, 0.04, 0.02),
            (cx + w * 0.33, cy + h * 0.18, z_mullion),
            mats["frame"],
            parent,
        )
    else:
        _add_box(m, f"{prefix}_Hole", (w - 0.12, h - 0.12, 0.09), (cx, cy, z_glass), mats["soot"], parent)
        if scorched:
            # Classic dark fire/blast soot plume climbing above shattered window lintel
            plume_y = min(FLOOR_HEIGHT - 0.18, cy + h * 0.5 + 0.24)
            _add_box(m, f"{prefix}_Plume", (w * 0.85, 0.36, 0.03), (cx, plume_y, z_face + sign_z * 0.018), mats["soot"], parent, tint=0.92)

    _add_box(
        m,
        f"{prefix}_Sill",
        (w + 0.16, 0.07, 0.16),
        (cx, cy - h * 0.5 - 0.035, z_face + sign_z * 0.05),
        mats["seam"],
        parent,
        tint=0.95,
    )


def _add_panel_seams(m, prefix, parent, mats, y_min=0.0, y_max=FLOOR_HEIGHT, skip_front_bays=()):
    """Precast 1-464 concrete joint grooves on all four faces."""
    h = y_max - y_min
    cy = (y_min + y_max) * 0.5
    y_top = y_max - 0.045
    _add_box(m, f"{prefix}_HTopF", (SECTION_WIDTH + 0.04, 0.07, 0.04), (0.0, y_top, HALF_D + 0.01), mats["seam"], parent)
    _add_box(m, f"{prefix}_HTopB", (SECTION_WIDTH + 0.04, 0.07, 0.04), (0.0, y_top, -HALF_D - 0.01), mats["seam"], parent)
    _add_box(m, f"{prefix}_HTopL", (0.04, 0.07, SECTION_DEPTH + 0.04), (-HALF_W - 0.01, y_top, 0.0), mats["seam"], parent)
    _add_box(m, f"{prefix}_HTopR", (0.04, 0.07, SECTION_DEPTH + 0.04), (HALF_W + 0.01, y_top, 0.0), mats["seam"], parent)

    for idx, x in enumerate((-3.6, 0.0, 3.6)):
        if idx not in skip_front_bays:
            _add_box(m, f"{prefix}_VF{idx}", (0.07, h, 0.04), (x, cy, HALF_D + 0.01), mats["seam"], parent)
        _add_box(m, f"{prefix}_VB{idx}", (0.07, h, 0.04), (x, cy, -HALF_D - 0.01), mats["seam"], parent)

    for idx, z in enumerate((-2.95, 0.0, 2.95)):
        _add_box(m, f"{prefix}_VL{idx}", (0.04, h, 0.07), (-HALF_W - 0.01, cy, z), mats["seam"], parent)
        _add_box(m, f"{prefix}_VR{idx}", (0.04, h, 0.07), (HALF_W + 0.01, cy, z), mats["seam"], parent)


def _add_balcony(m, prefix, cx, z_face, sign_z, mats, parent, slumped=False):
    """Cantilevered Soviet 1-464 balcony with concrete slab, guard screen, and steel rail."""
    bw = 2.75
    bd = 0.86
    slab_h = 0.13
    guard_h = 0.90
    cz = z_face + sign_z * (bd * 0.5)

    if slumped:
        axes = _ortho_axes(0.10 * sign_z, 0.28 * sign_z, -0.14)
        _add_grounded_obox(m, f"{prefix}_SlumpSlab", (cx, 0.36, cz), axes, (bw * 0.44, slab_h * 0.5, bd * 0.44), mats["broken"], parent, tint=0.84)
        _add_grounded_obox(m, f"{prefix}_SlumpGuard", (cx - 0.28, 0.72, cz + sign_z * 0.32), axes, (bw * 0.34, 0.30, 0.05), mats["balcony_screen"], parent, tint=0.78)
        return

    _add_box(m, f"{prefix}_Deck", (bw, slab_h, bd), (cx, slab_h * 0.5 + 0.03, cz), mats["concrete"], parent, tint=0.92)
    panel_y = slab_h + 0.03 + guard_h * 0.44
    panel_h = guard_h * 0.82
    z_front = z_face + sign_z * (bd - 0.04)
    _add_box(m, f"{prefix}_FrontPanel", (bw - 0.06, panel_h, 0.06), (cx, panel_y, z_front), mats["balcony_screen"], parent, tint=0.98)
    _add_box(m, f"{prefix}_SideL", (0.06, panel_h, bd - 0.06), (cx - bw * 0.5 + 0.04, panel_y, cz), mats["balcony_screen"], parent, tint=0.94)
    _add_box(m, f"{prefix}_SideR", (0.06, panel_h, bd - 0.06), (cx + bw * 0.5 - 0.04, panel_y, cz), mats["balcony_screen"], parent, tint=0.94)

    rail_y = slab_h + 0.03 + guard_h
    _add_box(m, f"{prefix}_RailF", (bw + 0.02, 0.045, 0.045), (cx, rail_y, z_front), mats["rail"], parent)
    _add_box(m, f"{prefix}_RailL", (0.045, 0.045, bd), (cx - bw * 0.5 + 0.03, rail_y, cz), mats["rail"], parent)
    _add_box(m, f"{prefix}_RailR", (0.045, 0.045, bd), (cx + bw * 0.5 - 0.03, rail_y, cz), mats["rail"], parent)
    for tag, px in (("PL", cx - bw * 0.5 + 0.04), ("PR", cx + bw * 0.5 - 0.04)):
        _add_box(m, f"{prefix}_{tag}", (0.045, guard_h, 0.045), (px, slab_h + 0.03 + guard_h * 0.5, z_front), mats["rail"], parent)

    _add_box(m, f"{prefix}_DoorGlass", (0.72, 1.92, 0.03), (cx - 0.54, 1.12, z_face + sign_z * 0.006), mats["glass"], parent)
    _add_box(m, f"{prefix}_DoorFrame", (0.82, 2.02, 0.05), (cx - 0.54, 1.12, z_face + sign_z * 0.002), mats["frame"], parent)
    _add_window_z(m, f"{prefix}_Win", cx + 0.48, 1.42, z_face, sign_z, 1.12, 1.28, mats, parent)


def _add_end_wall_details(m, prefix, parent, mats):
    """Narrow stairwell/utility gable windows on -X / +X end walls."""
    for tag, sx in (("L", -1.0), ("R", 1.0)):
        x_face = sx * HALF_W
        for w_idx, wz in enumerate((-2.7, 2.7)):
            _add_box(m, f"{prefix}_EW_{tag}{w_idx}_G", (0.03, 1.18, 0.84), (x_face + sx * 0.008, 1.44, wz), mats["glass"], parent)
            _add_box(m, f"{prefix}_EW_{tag}{w_idx}_F", (0.05, 1.28, 0.94), (x_face + sx * 0.004, 1.44, wz), mats["frame"], parent)
            _add_box(m, f"{prefix}_EW_{tag}{w_idx}_S", (0.14, 0.06, 1.04), (x_face + sx * 0.05, 0.80, wz), mats["seam"], parent)


def _build_ground_floor(m, parent, mats):
    plinth_h = 0.48
    _add_box(m, "Ground_Plinth", (SECTION_WIDTH - 0.06, plinth_h, SECTION_DEPTH - 0.06), (0.0, plinth_h * 0.5, 0.0), mats["plinth"], parent)

    wall_h = FLOOR_HEIGHT - plinth_h
    wall_cy = plinth_h + wall_h * 0.5
    panel_tints = (0.98, 1.02, 0.96, 1.01)
    for i, bx in enumerate(BAY_CENTERS_X):
        _add_box(m, f"Ground_Bay{i}", (BAY_WIDTH, wall_h, SECTION_DEPTH), (bx, wall_cy, 0.0), mats["concrete"], parent, tint=panel_tints[i])

    _add_panel_seams(m, "Ground", parent, mats, y_min=plinth_h, y_max=FLOOR_HEIGHT)

    for bx in (-5.4, 1.8, 5.4):
        _add_box(m, f"Ground_VentF_{bx}", (0.76, 0.22, 0.06), (bx, 0.24, HALF_D - 0.01), mats["soot"], parent)
        _add_box(m, f"Ground_VentB_{bx}", (0.76, 0.22, 0.06), (bx, 0.24, -HALF_D + 0.01), mats["soot"], parent)

    # Front facade (+Z): Bay 1 (-1.8) is the iconic Soviet entrance (pod'ezd)
    ent_x = BAY_CENTERS_X[1]
    _add_box(m, "Ent_Step1", (2.25, 0.18, 0.86), (ent_x, 0.09, HALF_D + 0.43), mats["plinth"], parent)
    _add_box(m, "Ent_Step2", (2.02, 0.18, 0.52), (ent_x, 0.27, HALF_D + 0.26), mats["plinth"], parent)
    _add_box(m, "Ent_Surround", (1.74, 2.04, 0.12), (ent_x, 1.38, HALF_D + 0.02), mats["seam"], parent)
    _add_box(m, "Ent_Door", (1.38, 1.86, 0.08), (ent_x, 1.30, HALF_D + 0.05), mats["rail"], parent, tint=0.88)
    _add_box(m, "Ent_Transom", (1.34, 0.28, 0.04), (ent_x, 2.38, HALF_D + 0.04), mats["glass"], parent)
    _add_box(m, "Ent_Visor", (2.35, 0.14, 1.02), (ent_x, 2.34, HALF_D + 0.51), mats["concrete"], parent, tint=0.90)
    _add_box(m, "Ent_VisorTrim", (2.39, 0.05, 1.06), (ent_x, 2.42, HALF_D + 0.51), mats["roof_tar"], parent)
    _add_box(m, "Ent_BracketL", (0.12, 0.42, 0.70), (ent_x - 1.02, 2.08, HALF_D + 0.35), mats["seam"], parent)
    _add_box(m, "Ent_BracketR", (0.12, 0.42, 0.70), (ent_x + 1.02, 2.08, HALF_D + 0.35), mats["seam"], parent)

    for i, bx in enumerate((BAY_CENTERS_X[0], BAY_CENTERS_X[2], BAY_CENTERS_X[3])):
        _add_window_z(m, f"Ground_WF{i}", bx, 1.62, HALF_D, 1.0, 1.68, 1.30, mats, parent)

    for i, bx in enumerate(BAY_CENTERS_X):
        _add_window_z(m, f"Ground_WB{i}", bx, 1.62, -HALF_D, -1.0, 1.68, 1.30, mats, parent)

    _add_end_wall_details(m, "Ground", parent, mats)


def _build_mid_floor(m, parent, mats):
    panel_tints = (1.01, 0.96, 1.02, 0.97)
    for i, bx in enumerate(BAY_CENTERS_X):
        _add_box(m, f"Mid_Bay{i}", (BAY_WIDTH, FLOOR_HEIGHT, SECTION_DEPTH), (bx, FLOOR_HEIGHT * 0.5, 0.0), mats["concrete"], parent, tint=panel_tints[i])

    _add_box(m, "Mid_TrimF", (SECTION_WIDTH + 0.02, 0.12, 0.03), (0.0, 0.10, HALF_D + 0.008), mats["brick_trim"], parent, tint=0.95)
    _add_box(m, "Mid_TrimB", (SECTION_WIDTH + 0.02, 0.12, 0.03), (0.0, 0.10, -HALF_D - 0.008), mats["brick_trim"], parent, tint=0.95)

    _add_panel_seams(m, "Mid", parent, mats)

    # Front (+Z): balconies on Bay 0 (-5.4) and Bay 2 (+1.8), stairwell window on Bay 1 (-1.8), apt window on Bay 3 (+5.4)
    _add_balcony(m, "Mid_BalcF0", BAY_CENTERS_X[0], HALF_D, 1.0, mats, parent)
    _add_balcony(m, "Mid_BalcF2", BAY_CENTERS_X[2], HALF_D, 1.0, mats, parent)
    _add_window_z(m, "Mid_StairF", BAY_CENTERS_X[1], 1.14, HALF_D, 1.0, 1.18, 1.64, mats, parent)
    _add_window_z(m, "Mid_WF3", BAY_CENTERS_X[3], 1.42, HALF_D, 1.0, 1.68, 1.30, mats, parent)

    # Rear (-Z): balconies on Bay 1 & Bay 3, windows on Bay 0 & Bay 2
    _add_window_z(m, "Mid_WB0", BAY_CENTERS_X[0], 1.42, -HALF_D, -1.0, 1.68, 1.30, mats, parent)
    _add_balcony(m, "Mid_BalcB1", BAY_CENTERS_X[1], -HALF_D, -1.0, mats, parent)
    _add_window_z(m, "Mid_WB2", BAY_CENTERS_X[2], 1.42, -HALF_D, -1.0, 1.68, 1.30, mats, parent)
    _add_balcony(m, "Mid_BalcB3", BAY_CENTERS_X[3], -HALF_D, -1.0, mats, parent)

    _add_end_wall_details(m, "Mid", parent, mats)


def _build_breached_floor(m, parent, mats):
    """Asymmetric battle-scarred floor with blown-out front-right bay, soot plumes, and hanging slab."""
    # Rear core remains mostly intact (Z in [-5.9, 0.2])
    _add_box(m, "Br_RearCore", (SECTION_WIDTH, FLOOR_HEIGHT, 6.1), (0.0, FLOOR_HEIGHT * 0.5, -2.85), mats["concrete"], parent, tint=0.88)
    # Asymmetric front piers: wide scarred pier on left (-7.2..-2.2), narrower shattered pier on right (+3.8..+7.2)
    _add_box(m, "Br_PierL", (5.0, FLOOR_HEIGHT, 5.7), (-4.7, FLOOR_HEIGHT * 0.5, 3.05), mats["concrete"], parent, tint=0.87)
    _add_box(m, "Br_PierR", (3.2, FLOOR_HEIGHT, 5.7), (5.6, FLOOR_HEIGHT * 0.5, 3.05), mats["concrete"], parent, tint=0.83)

    # Exposed jagged floor and ceiling slabs spanning the blown-out bay (X in [-2.2, +4.0])
    _add_box(m, "Br_FloorSlab", (6.6, 0.24, 5.5), (0.9, 0.12, 2.95), mats["broken"], parent, tint=0.84)
    _add_box(m, "Br_CeilSlab", (6.2, 0.22, 4.9), (0.8, FLOOR_HEIGHT - 0.11, 2.65), mats["broken"], parent, tint=0.80)

    # Dark scorched interior cavity + broken interior room partition & hanging ceiling chunk
    _add_box(m, "Br_CavityBack", (6.2, FLOOR_HEIGHT - 0.42, 0.30), (0.9, FLOOR_HEIGHT * 0.5, 0.35), mats["soot"], parent)
    _add_box(m, "Br_PartitionStub", (0.24, FLOOR_HEIGHT - 0.48, 3.8), (0.15, FLOOR_HEIGHT * 0.5, 2.2), mats["broken"], parent, tint=0.72)
    hang_axes = _ortho_axes(0.12, 0.34, -0.18)
    _add_grounded_obox(m, "Br_HangSlab", (1.8, 1.95, 4.9), hang_axes, (1.35, 0.12, 0.85), mats["broken"], parent, tint=0.82)

    # Jagged concrete fracture edges framing the blast hole
    jagged_specs = [
        ((-2.05, 2.15, 5.72), (0.85, 0.95, 0.30), (0.0, 0.0, -0.18)),
        ((-1.95, 0.68, 5.68), (0.95, 0.88, 0.30), (0.0, 0.0, 0.14)),
        ((3.95, 2.08, 5.70), (0.88, 1.05, 0.30), (0.0, 0.0, 0.22)),
        ((3.82, 0.64, 5.66), (1.02, 0.82, 0.30), (0.0, 0.0, -0.16)),
        ((0.90, 0.42, 5.42), (2.75, 0.42, 0.38), (0.05, -0.08, 0.06)),
    ]
    for i, (center, size, rot) in enumerate(jagged_specs):
        axes = _ortho_axes(*rot)
        _add_grounded_obox(m, f"Br_Jag{i}", center, axes, (size[0] * 0.5, size[1] * 0.5, size[2] * 0.5), mats["broken"], parent, tint=0.82)

    # Bent rusted steel rebar rods jutting from torn slabs and piers
    rebar_segments = [
        ((-1.85, 2.38, 5.75), (-0.85, 2.02, 6.18)),
        ((-1.82, 1.62, 5.72), (-0.98, 1.48, 6.04)),
        ((-1.78, 0.85, 5.72), (-0.75, 1.05, 6.10)),
        ((3.78, 2.28, 5.72), (2.78, 1.88, 6.14)),
        ((3.72, 1.25, 5.70), (2.70, 1.38, 6.08)),
        ((-0.25, 2.60, 5.10), (-0.05, 1.98, 5.72)),
        ((1.55, 2.62, 5.12), (1.88, 1.92, 5.78)),
        ((2.25, 0.22, 5.48), (1.95, 0.72, 6.02)),
    ]
    for i, (p0, p1) in enumerate(rebar_segments):
        rod(m, f"Br_Rebar{i}", p0, p1, 0.028, mats["rebar"], parent, sides=5)

    # Heavy smoke/blast soot smears around breach & rear pass-through blast
    _add_box(m, "Br_SootL", (0.95, 2.05, 0.04), (-2.65, 1.50, HALF_D + 0.02), mats["soot"], parent)
    _add_box(m, "Br_SootR", (0.95, 1.95, 0.04), (4.45, 1.52, HALF_D + 0.02), mats["soot"], parent)
    _add_box(m, "Br_RearBlast", (4.10, 1.78, 0.14), (0.6, 1.42, -HALF_D - 0.03), mats["soot"], parent)
    _add_box(m, "Br_RearEdgeL", (0.60, 1.92, 0.18), (-1.55, 1.42, -HALF_D - 0.02), mats["broken"], parent, tint=0.78)
    _add_box(m, "Br_RearEdgeR", (0.60, 1.92, 0.18), (2.75, 1.42, -HALF_D - 0.02), mats["broken"], parent, tint=0.78)
    rod(m, "Br_RearRebar0", (-1.35, 1.95, -HALF_D - 0.04), (-0.45, 1.65, -HALF_D - 0.42), 0.028, mats["rebar"], parent, sides=5)
    rod(m, "Br_RearRebar1", (2.52, 1.12, -HALF_D - 0.04), (1.58, 1.32, -HALF_D - 0.38), 0.028, mats["rebar"], parent, sides=5)

    # Slumped balcony on Bay 0 + shattered scorched windows
    _add_balcony(m, "Br_BalcF0", BAY_CENTERS_X[0], HALF_D, 1.0, mats, parent, slumped=True)
    _add_window_z(m, "Br_WF3", BAY_CENTERS_X[3], 1.42, HALF_D, 1.0, 1.68, 1.30, mats, parent, broken=True, scorched=True)
    for i, bx in enumerate(BAY_CENTERS_X):
        _add_window_z(m, f"Br_WB{i}", bx, 1.42, -HALF_D, -1.0, 1.68, 1.30, mats, parent, broken=(i in (1, 2)), scorched=(i in (1, 2)))

    _add_panel_seams(m, "Br", parent, mats, skip_front_bays=(1, 2))


def _build_top_jagged_cap(m, parent, mats):
    """War-torn asymmetric jagged upper wall skeleton for partially collapsed panelkas.

    Sits at Y = standing * FLOOR_HEIGHT so partially destroyed buildings never look like
    clean flat-cut rectangular blocks: left/rear stairwell walls rise jaggedly 1.2-2.2 m
    higher than the sheared right side, with overhanging broken slabs and rebar.
    """
    # Scorched exposed broken concrete deck slab with debris scatter
    _add_box(m, "Cap_Deck", (SECTION_WIDTH - 0.30, 0.24, SECTION_DEPTH - 0.30), (0.0, 0.12, 0.0), mats["broken"], parent, tint=0.80)
    _add_box(m, "Cap_SootCore", (6.8, 0.12, 5.2), (1.8, 0.26, 0.8), mats["soot"], parent)

    # Jagged standing left gable & front-left stairwell corner pier (rises up to 2.25 m!)
    _add_box(m, "Cap_LeftGable", (0.38, 1.95, 7.2), (-HALF_W + 0.20, 0.98, -1.4), mats["concrete"], parent, tint=0.86)
    _add_box(m, "Cap_LeftGableTop", (0.38, 0.55, 3.8), (-HALF_W + 0.20, 2.02, -3.0), mats["broken"], parent, tint=0.82)
    _add_box(m, "Cap_FrontLeftWall", (4.8, 1.55, 0.36), (-4.7, 0.78, HALF_D - 0.18), mats["concrete"], parent, tint=0.88)
    _add_box(m, "Cap_FrontLeftTooth", (2.2, 0.65, 0.34), (-5.8, 1.72, HALF_D - 0.18), mats["broken"], parent, tint=0.83)

    # Rear ragged wall descending diagonally from left (2.1 m) to right (0.7 m)
    _add_box(m, "Cap_RearTall", (5.4, 1.90, 0.36), (-4.4, 0.95, -HALF_D + 0.18), mats["concrete"], parent, tint=0.85)
    _add_box(m, "Cap_RearMid", (4.2, 1.25, 0.36), (0.3, 0.63, -HALF_D + 0.18), mats["broken"], parent, tint=0.82)
    _add_box(m, "Cap_RearLow", (3.8, 0.62, 0.36), (4.6, 0.31, -HALF_D + 0.18), mats["broken"], parent, tint=0.79)

    # Interior broken concrete room partition walls exposed to the sky!
    _add_box(m, "Cap_IntWall1", (0.24, 1.48, 6.4), (-3.2, 0.74, -1.6), mats["broken"], parent, tint=0.76)
    _add_box(m, "Cap_IntWall2", (5.2, 1.15, 0.24), (-0.8, 0.58, -0.4), mats["broken"], parent, tint=0.74)
    _add_box(m, "Cap_RightStub", (0.36, 0.85, 4.4), (HALF_W - 0.18, 0.43, -2.6), mats["concrete"], parent, tint=0.82)

    # Draped/collapsed broken floor slabs tilting down over the torn front-right edge
    slabs = [
        ((1.9, 0.42, 4.6), (3.4, 0.22, 2.4), (0.14, 0.32, -0.18), "concrete", 0.88),
        ((4.6, 0.38, 2.2), (2.8, 0.22, 2.1), (-0.22, 0.26, 0.20), "broken", 0.84),
        ((-1.2, 0.55, 2.6), (3.1, 0.22, 2.2), (0.28, -0.18, -0.14), "broken", 0.86),
        ((-3.8, 0.52, -2.2), (2.6, 0.20, 1.9), (-0.15, 0.22, 0.16), "concrete", 0.84),
    ]
    for i, (center, size, rot, mat_key, tint) in enumerate(slabs):
        axes = _ortho_axes(*rot)
        _add_grounded_obox(m, f"Cap_Slab{i}", center, axes, (size[0] * 0.5, size[1] * 0.5, size[2] * 0.5), mats[mat_key], parent, tint=tint, min_y=0.14)

    # Jutting bent steel rebar cages along broken wall crowns
    rebar = [
        ((-5.2, 1.85, HALF_D - 0.18), (-4.7, 2.52, HALF_D + 0.12)),
        ((-3.4, 1.48, HALF_D - 0.18), (-2.6, 1.96, HALF_D + 0.18)),
        ((-4.2, 1.82, -HALF_D + 0.18), (-3.7, 2.48, -HALF_D - 0.15)),
        ((0.8, 1.18, -HALF_D + 0.18), (1.4, 1.72, -HALF_D - 0.10)),
        ((2.2, 0.48, 5.4), (2.8, 0.92, 6.05)),
        ((-3.2, 1.42, 0.2), (-2.6, 1.98, 0.65)),
    ]
    for i, (p0, p1) in enumerate(rebar):
        rod(m, f"Cap_Rebar{i}", p0, p1, 0.028, mats["rebar"], parent, sides=5)


def _build_roof_cap(m, parent, mats):
    """Flat Soviet ruberoid roof with perimeter parapet, concrete vent shafts, and TV aerial."""
    _add_box(m, "Roof_Deck", (SECTION_WIDTH - 0.16, 0.12, SECTION_DEPTH - 0.16), (0.0, 0.08, 0.0), mats["roof_tar"], parent)

    pt = 0.28
    ph = ROOF_PARAPET_HEIGHT
    _add_box(m, "Roof_ParF", (SECTION_WIDTH + 0.08, ph, pt), (0.0, ph * 0.5, HALF_D - pt * 0.5 + 0.04), mats["concrete"], parent, tint=0.92)
    _add_box(m, "Roof_ParB", (SECTION_WIDTH + 0.08, ph, pt), (0.0, ph * 0.5, -HALF_D + pt * 0.5 - 0.04), mats["concrete"], parent, tint=0.92)
    _add_box(m, "Roof_ParL", (pt, ph, SECTION_DEPTH - pt), (-HALF_W + pt * 0.5 - 0.04, ph * 0.5, 0.0), mats["concrete"], parent, tint=0.90)
    _add_box(m, "Roof_ParR", (pt, ph, SECTION_DEPTH - pt), (HALF_W - pt * 0.5 + 0.04, ph * 0.5, 0.0), mats["concrete"], parent, tint=0.90)

    _add_box(m, "Roof_CopF", (SECTION_WIDTH + 0.12, 0.04, pt + 0.06), (0.0, ph + 0.02, HALF_D - pt * 0.5 + 0.04), mats["seam"], parent)
    _add_box(m, "Roof_CopB", (SECTION_WIDTH + 0.12, 0.04, pt + 0.06), (0.0, ph + 0.02, -HALF_D + pt * 0.5 - 0.04), mats["seam"], parent)

    _add_box(m, "Roof_AccessBox", (2.20, 0.88, 1.75), (-1.80, 0.50, 0.55), mats["concrete"], parent, tint=0.88)
    _add_box(m, "Roof_AccessLid", (2.34, 0.10, 1.89), (-1.80, 0.98, 0.55), mats["roof_tar"], parent)

    for i, (vx, vz) in enumerate(((-4.6, -2.3), (2.6, -2.3), (4.2, 2.1))):
        _add_box(m, f"Roof_Vent{i}", (1.40, 0.74, 0.72), (vx, 0.43, vz), mats["concrete"], parent, tint=0.86)
        _add_box(m, f"Roof_VentCap{i}", (1.54, 0.10, 0.86), (vx, 0.84, vz), mats["seam"], parent)
        _add_box(m, f"Roof_VentGap{i}", (1.22, 0.16, 0.58), (vx, 0.68, vz), mats["soot"], parent)

    mast_x, mast_z = 1.95, -0.40
    rod(m, "Roof_TvMast", (mast_x, 0.10, mast_z), (mast_x, 2.55, mast_z), 0.032, mats["rail"], parent, sides=6)
    rod(m, "Roof_TvBoom1", (mast_x - 0.78, 2.28, mast_z), (mast_x + 0.78, 2.28, mast_z), 0.018, mats["rail"], parent, sides=5)
    rod(m, "Roof_TvBoom2", (mast_x - 0.52, 1.94, mast_z), (mast_x + 0.52, 1.94, mast_z), 0.018, mats["rail"], parent, sides=5)
    rod(m, "Roof_TvCross", (mast_x, 2.28, mast_z - 0.56), (mast_x, 2.28, mast_z + 0.56), 0.018, mats["rail"], parent, sides=5)


def _build_rubble_ruin(m, parent, mats, mirror_x=False):
    """Impassable war-torn ground-floor battle ruin (1.65 m rubble crest + 1.85-2.05 m jagged corner stubs).

    Matches logical rubble_height (1.65 m) closely so tank turrets (2.2-2.9 m) clearly sight and fire
    over the collapsed ruins while blown-out masonry piers and leaning slabs block vehicle movement.
    """
    sx = -1.0 if mirror_x else 1.0
    pfx = "RubB" if mirror_x else "RubA"

    # Heavy concrete rubble core choking the interior (1.65 m crest matching logical rubble_height)
    _add_box(m, f"{pfx}_Base", (SECTION_WIDTH - 0.4, 0.58, SECTION_DEPTH - 0.4), (0.0, 0.29, 0.0), mats["broken"], parent, tint=0.80)
    _add_box(m, f"{pfx}_Tier1", (SECTION_WIDTH - 2.6, 0.52, SECTION_DEPTH - 2.4), (sx * 0.25, 0.82, -0.15), mats["broken"], parent, tint=0.84)
    _add_box(m, f"{pfx}_Tier2", (7.8, 0.46, 6.2), (sx * -0.45, 1.24, 0.25), mats["broken"], parent, tint=0.88)
    _add_box(m, f"{pfx}_Crest", (4.2, 0.30, 3.4), (sx * -0.35, 1.50, 0.10), mats["concrete"], parent, tint=0.84)

    # Dark scorched blast craters & ash
    _add_box(m, f"{pfx}_Soot1", (4.6, 0.14, 2.9), (sx * 2.6, 0.62, 2.4), mats["soot"], parent)
    _add_box(m, f"{pfx}_Soot2", (3.8, 0.14, 3.1), (sx * -3.2, 0.58, -2.2), mats["soot"], parent)
    _add_box(m, f"{pfx}_Soot3", (3.4, 0.12, 2.4), (0.0, 1.48, 0.3), mats["soot"], parent)

    # Jagged surviving ground-floor corner stubs (1.75-2.05 m peak -> impassable ruin without clipping tank LOS)
    # Corner 1: broken stairwell/front-left concrete corner pier (2.05 m peak)
    _add_box(m, f"{pfx}_WallFL_Base", (3.8, 1.65, 0.42), (sx * -5.1, 0.825, HALF_D - 0.25), mats["concrete"], parent, tint=0.86)
    _add_box(m, f"{pfx}_WallFL_Tooth", (1.8, 0.40, 0.40), (sx * -6.0, 1.85, HALF_D - 0.25), mats["broken"], parent, tint=0.82)
    _add_box(m, f"{pfx}_WallFL_Side", (0.42, 1.85, 4.4), (sx * (-HALF_W + 0.25), 0.925, 3.4), mats["concrete"], parent, tint=0.84)
    _add_box(m, f"{pfx}_WallFL_WinSoot", (1.45, 0.82, 0.10), (sx * -4.6, 1.05, HALF_D - 0.02), mats["soot"], parent)

    # Corner 2: rear-right shattered corner pier (1.92 m peak)
    _add_box(m, f"{pfx}_WallRB_Base", (4.2, 1.54, 0.42), (sx * 4.8, 0.77, -HALF_D + 0.25), mats["concrete"], parent, tint=0.83)
    _add_box(m, f"{pfx}_WallRB_Tooth", (2.1, 0.38, 0.40), (sx * 5.7, 1.73, -HALF_D + 0.25), mats["broken"], parent, tint=0.79)
    _add_box(m, f"{pfx}_WallRB_Side", (0.42, 1.72, 4.6), (sx * (HALF_W - 0.25), 0.86, -3.3), mats["concrete"], parent, tint=0.81)
    _add_box(m, f"{pfx}_WallRB_WinSoot", (1.48, 0.80, 0.10), (sx * 4.4, 0.98, -HALF_D + 0.02), mats["soot"], parent)

    # Corner 3: lower broken front-right pier stub (1.45 m) + interior partition stub (1.60 m)
    _add_box(m, f"{pfx}_WallFR_Stub", (2.8, 1.45, 0.40), (sx * 5.4, 0.725, HALF_D - 0.25), mats["broken"], parent, tint=0.82)
    _add_box(m, f"{pfx}_IntPier", (0.34, 1.60, 4.2), (sx * -1.4, 0.80, -2.4), mats["broken"], parent, tint=0.76)

    # Leaning fractured precast wall panels (huge concrete slabs slumped across the ruins)
    slab_specs = [
        ((sx * -3.6, 0.98, 2.6), (3.4, 0.24, 2.3), (0.38 * sx, 0.34, -0.24 * sx), "concrete", 0.94),
        ((sx * 3.5, 0.92, 2.1), (3.1, 0.24, 2.1), (-0.42 * sx, -0.30, 0.26 * sx), "concrete", 0.90),
        ((sx * -2.8, 1.05, -3.2), (3.5, 0.24, 2.2), (0.24 * sx, -0.34, 0.20 * sx), "broken", 0.88),
        ((sx * 2.9, 1.00, -2.5), (3.2, 0.24, 2.2), (-0.30 * sx, 0.30, -0.22 * sx), "concrete", 0.92),
        ((sx * 0.2, 1.38, 1.2), (3.0, 0.22, 1.9), (0.16 * sx, 0.26, 0.18 * sx), "concrete", 0.96),
        ((sx * -0.8, 1.32, -1.3), (2.8, 0.22, 1.8), (-0.22 * sx, -0.24, -0.18 * sx), "broken", 0.84),
        ((sx * -5.4, 0.84, -0.9), (2.4, 0.26, 1.7), (0.48 * sx, 0.18, 0.24 * sx), "broken", 0.80),
        ((sx * 5.2, 0.86, 0.6), (2.5, 0.26, 1.7), (-0.45 * sx, -0.18, -0.24 * sx), "concrete", 0.86),
    ]
    for i, (center, size, rot, mat_key, tint) in enumerate(slab_specs):
        axes = _ortho_axes(*rot)
        _add_grounded_obox(m, f"{pfx}_Slab{i}", center, axes, (size[0] * 0.5, size[1] * 0.5, size[2] * 0.5), mats[mat_key], parent, tint=tint)

    # Heavy concrete boulders & broken balcony pieces spilled around perimeter (kept inside footprint)
    chunk_specs = [
        ((sx * -5.8, 0.36, 4.3), (1.3, 0.62, 1.0), (0.4, 0.12, 0.2)),
        ((sx * 5.6, 0.34, 4.4), (1.2, 0.56, 0.95), (-0.3, -0.15, 0.1)),
        ((sx * -5.7, 0.38, -4.2), (1.4, 0.66, 1.05), (0.2, 0.14, -0.2)),
        ((sx * 5.7, 0.32, -4.3), (1.1, 0.54, 0.90), (-0.5, 0.12, 0.15)),
        ((sx * 0.6, 0.52, 4.7), (1.6, 0.60, 0.98), (0.15, -0.14, 0.08)),
        ((sx * -0.7, 0.50, -4.6), (1.5, 0.62, 1.02), (-0.22, 0.16, -0.1)),
    ]
    for i, (center, size, rot) in enumerate(chunk_specs):
        axes = _ortho_axes(*rot)
        _add_grounded_obox(m, f"{pfx}_Chunk{i}", center, axes, (size[0] * 0.5, size[1] * 0.5, size[2] * 0.5), mats["broken"], parent, tint=0.78)

    # Twisted rebar spikes jutting out of corner walls and rubble core
    rebar_spikes = [
        ((sx * -5.6, 1.95, HALF_D - 0.25), (sx * -4.9, 2.25, HALF_D - 0.05)),
        ((sx * -4.0, 1.60, HALF_D - 0.25), (sx * -3.2, 1.92, HALF_D + 0.12)),
        ((sx * 5.2, 1.82, -HALF_D + 0.25), (sx * 4.5, 2.15, -HALF_D - 0.05)),
        ((sx * -1.4, 1.54, -1.6), (sx * -0.8, 1.95, -0.9)),
        ((sx * 1.4, 1.22, 1.4), (sx * 2.1, 1.72, 2.1)),
        ((sx * -2.6, 1.18, 0.4), (sx * -3.4, 1.68, 1.0)),
        ((sx * 3.1, 1.08, -1.8), (sx * 3.8, 1.58, -2.4)),
    ]
    for i, (p0, p1) in enumerate(rebar_spikes):
        rod(m, f"{pfx}_Rebar{i}", p0, p1, 0.032, mats["rebar"], parent, sides=5)


def _build_debris_meshes(m, nodes, mats):
    """Three single-material low-poly meshes used directly by MultiMesh debris physics."""
    pts_a = [
        (-0.82, -0.11, -0.54),
        (0.76, -0.11, -0.48),
        (0.84, -0.11, 0.44),
        (-0.64, -0.11, 0.56),
        (-0.78, 0.11, -0.52),
        (0.72, 0.11, -0.46),
        (0.80, 0.11, 0.42),
        (-0.62, 0.11, 0.52),
    ]
    faces_box = [(0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
    m.add_flat_mesh("DebrisSlabA_Geo", pts_a, faces_box, mats["concrete"], nodes["Debris_Slab_A"], auto_orient=True, tint=0.90)

    pts_b = [
        (-0.56, -0.14, -0.38),
        (0.52, -0.14, -0.34),
        (0.48, -0.14, 0.38),
        (-0.52, -0.14, 0.32),
        (-0.50, 0.14, -0.34),
        (0.46, 0.14, -0.30),
        (0.44, 0.14, 0.34),
        (-0.48, 0.14, 0.28),
    ]
    m.add_flat_mesh("DebrisSlabB_Geo", pts_b, faces_box, mats["broken"], nodes["Debris_Slab_B"], auto_orient=True, tint=0.86)

    pts_c = [
        (-0.28, -0.20, -0.22),
        (0.26, -0.18, -0.24),
        (0.28, -0.21, 0.20),
        (-0.24, -0.19, 0.24),
        (-0.20, 0.21, -0.16),
        (0.22, 0.19, -0.18),
        (0.18, 0.20, 0.18),
        (-0.18, 0.18, 0.16),
    ]
    m.add_flat_mesh("DebrisChunkC_Geo", pts_c, faces_box, mats["broken"], nodes["Debris_Chunk_C"], auto_orient=True, tint=0.80)


def _build_intact_lod1(m, parent, mats):
    """Medium RTS-distance 5-story intact section (~900 tris, 1 merged draw call)."""
    total_h = FLOORS_TOTAL * FLOOR_HEIGHT
    plinth_h = 0.48

    _add_box(m, "Lod1_Plinth", (SECTION_WIDTH - 0.06, plinth_h, SECTION_DEPTH - 0.06), (0.0, plinth_h * 0.5, 0.0), mats["plinth"], parent)
    body_h = total_h - plinth_h
    _add_box(m, "Lod1_Body", (SECTION_WIDTH, body_h, SECTION_DEPTH), (0.0, plinth_h + body_h * 0.5, 0.0), mats["concrete"], parent, tint=0.99)

    ent_x = BAY_CENTERS_X[1]
    _add_box(m, "Lod1_EntStep", (2.25, 0.28, 0.86), (ent_x, 0.14, HALF_D + 0.43), mats["plinth"], parent)
    _add_box(m, "Lod1_EntDoor", (1.50, 1.94, 0.08), (ent_x, 1.32, HALF_D + 0.04), mats["rail"], parent, tint=0.88)
    _add_box(m, "Lod1_EntVisor", (2.35, 0.16, 1.02), (ent_x, 2.34, HALF_D + 0.51), mats["concrete"], parent, tint=0.90)

    for f in range(FLOORS_TOTAL):
        y0 = f * FLOOR_HEIGHT
        if f > 0:
            _add_box(m, f"Lod1_TrimF_{f}", (SECTION_WIDTH + 0.04, 0.12, 0.04), (0.0, y0 + 0.09, HALF_D + 0.01), mats["brick_trim"], parent)
            _add_box(m, f"Lod1_TrimB_{f}", (SECTION_WIDTH + 0.04, 0.12, 0.04), (0.0, y0 + 0.09, -HALF_D - 0.01), mats["brick_trim"], parent)
        for bi, bx in enumerate(BAY_CENTERS_X):
            if f == 0 and bi == 1:
                pass
            elif f > 0 and bi in (0, 2):
                _add_box(m, f"Lod1_BalcF_{f}_{bi}", (2.75, 0.90, 0.86), (bx, y0 + 0.52, HALF_D + 0.43), mats["balcony_screen"], parent, tint=0.95)
                _add_box(m, f"Lod1_BWinF_{f}_{bi}", (1.92, 1.28, 0.03), (bx, y0 + 1.44, HALF_D + 0.015), mats["glass"], parent)
            else:
                wy = y0 + (1.14 if (f > 0 and bi == 1) else (1.62 if f == 0 else 1.42))
                wh = 1.64 if (f > 0 and bi == 1) else 1.30
                ww = 1.18 if (f > 0 and bi == 1) else 1.68
                _add_box(m, f"Lod1_WinF_{f}_{bi}", (ww, wh, 0.03), (bx, wy, HALF_D + 0.015), mats["glass"], parent)

            if f > 0 and bi in (1, 3):
                _add_box(m, f"Lod1_BalcB_{f}_{bi}", (2.75, 0.90, 0.86), (bx, y0 + 0.52, -HALF_D - 0.43), mats["balcony_screen"], parent, tint=0.95)
                _add_box(m, f"Lod1_BWinB_{f}_{bi}", (1.92, 1.28, 0.03), (bx, y0 + 1.44, -HALF_D - 0.015), mats["glass"], parent)
            else:
                wy = y0 + (1.62 if f == 0 else 1.42)
                _add_box(m, f"Lod1_WinB_{f}_{bi}", (1.68, 1.30, 0.03), (bx, wy, -HALF_D - 0.015), mats["glass"], parent)

    _add_box(m, "Lod1_RoofTar", (SECTION_WIDTH - 0.24, 0.14, SECTION_DEPTH - 0.24), (0.0, total_h + 0.07, 0.0), mats["roof_tar"], parent)
    _add_box(m, "Lod1_ParF", (SECTION_WIDTH + 0.08, ROOF_PARAPET_HEIGHT, 0.30), (0.0, total_h + ROOF_PARAPET_HEIGHT * 0.5, HALF_D - 0.11), mats["concrete"], parent, tint=0.92)
    _add_box(m, "Lod1_ParB", (SECTION_WIDTH + 0.08, ROOF_PARAPET_HEIGHT, 0.30), (0.0, total_h + ROOF_PARAPET_HEIGHT * 0.5, -HALF_D + 0.11), mats["concrete"], parent, tint=0.92)
    _add_box(m, "Lod1_ParL", (0.30, ROOF_PARAPET_HEIGHT, SECTION_DEPTH - 0.30), (-HALF_W + 0.11, total_h + ROOF_PARAPET_HEIGHT * 0.5, 0.0), mats["concrete"], parent, tint=0.90)
    _add_box(m, "Lod1_ParR", (0.30, ROOF_PARAPET_HEIGHT, SECTION_DEPTH - 0.30), (HALF_W - 0.11, total_h + ROOF_PARAPET_HEIGHT * 0.5, 0.0), mats["concrete"], parent, tint=0.90)
    _add_box(m, "Lod1_Penthouse", (2.20, 0.90, 1.75), (-1.80, total_h + 0.45, 0.55), mats["concrete"], parent, tint=0.88)
    _add_box(m, "Lod1_VentA", (1.40, 0.76, 0.72), (-4.6, total_h + 0.38, -2.3), mats["concrete"], parent, tint=0.86)
    _add_box(m, "Lod1_VentB", (1.40, 0.76, 0.72), (2.6, total_h + 0.38, -2.3), mats["concrete"], parent, tint=0.86)


def _build_intact_lod2(m, parent, mats):
    """Distant silhouette 5-story intact section (< 200 tris, 1 merged draw call)."""
    total_h = FLOORS_TOTAL * FLOOR_HEIGHT
    plinth_h = 0.48

    _add_box(m, "Lod2_Plinth", (SECTION_WIDTH - 0.06, plinth_h, SECTION_DEPTH - 0.06), (0.0, plinth_h * 0.5, 0.0), mats["plinth"], parent)
    _add_box(m, "Lod2_Body", (SECTION_WIDTH, total_h - plinth_h, SECTION_DEPTH), (0.0, plinth_h + (total_h - plinth_h) * 0.5, 0.0), mats["concrete"], parent, tint=0.98)
    _add_box(m, "Lod2_EntVisor", (2.35, 0.22, 1.02), (BAY_CENTERS_X[1], 2.32, HALF_D + 0.51), mats["concrete"], parent, tint=0.90)

    for f in range(FLOORS_TOTAL):
        wy = f * FLOOR_HEIGHT + (1.62 if f == 0 else 1.42)
        _add_box(m, f"Lod2_BandF_{f}", (SECTION_WIDTH - 0.8, 1.08, 0.04), (0.0, wy, HALF_D + 0.02), mats["glass"], parent)
        _add_box(m, f"Lod2_BandB_{f}", (SECTION_WIDTH - 0.8, 1.08, 0.04), (0.0, wy, -HALF_D - 0.02), mats["glass"], parent)

    _add_box(m, "Lod2_Roof", (SECTION_WIDTH + 0.08, ROOF_PARAPET_HEIGHT, SECTION_DEPTH + 0.08), (0.0, total_h + ROOF_PARAPET_HEIGHT * 0.5, 0.0), mats["concrete"], parent, tint=0.90)
    _add_box(m, "Lod2_RoofTar", (SECTION_WIDTH - 0.40, 0.06, SECTION_DEPTH - 0.40), (0.0, total_h + ROOF_PARAPET_HEIGHT + 0.02, 0.0), mats["roof_tar"], parent)
    _add_box(m, "Lod2_Penthouse", (2.20, 0.88, 1.75), (-1.80, total_h + ROOF_PARAPET_HEIGHT + 0.44, 0.55), mats["concrete"], parent, tint=0.86)


def _clone_module_parts_into(m, src_parent, dst_parent, offset_y, prefix):
    """Clone already-authored parts from src_parent into dst_parent shifted along +Y."""
    snapshot = list(m.parts)
    for idx, (node_id, parent, material, points, normals, indices, colors) in enumerate(snapshot):
        if parent != src_parent:
            continue
        src_t = m.data["nodes"][node_id].get("translation", [0.0, 0.0, 0.0])
        new_node = m.node(f"{prefix}_{idx}", (src_t[0], src_t[1] + offset_y, src_t[2]), parent=dst_parent)
        m.parts.append((new_node, dst_parent, material, list(points), list(normals), list(indices), list(colors)))


def build_panelka_model():
    m = GltfModel("Panelka_1_464", surface_weathering=False)
    m.data["asset"]["generator"] = "Dead Ground Panelka 1-464 model authoring"

    mats = {
        "concrete": m.material("ConcretePanel", "#b5b0a3", metal=0.0, rough=0.90),
        "seam": m.material("ConcreteSeam", "#6c6960", metal=0.0, rough=0.94),
        "plinth": m.material("ConcretePlinth", "#787770", metal=0.0, rough=0.92),
        "broken": m.material("ConcreteBroken", "#888378", metal=0.0, rough=0.95),
        "frame": m.material("WindowFrame", "#dbd4c4", metal=0.0, rough=0.78),
        "glass": m.material("WindowGlass", "#23313a", metal=0.18, rough=0.22),
        "rail": m.material("BalconyRail", "#42504a", metal=0.45, rough=0.62),
        "balcony_screen": m.material("BalconyScreen", "#9ba097", metal=0.0, rough=0.88),
        "roof_tar": m.material("RoofTar", "#38393b", metal=0.0, rough=0.92),
        "soot": m.material("SootInterior", "#211f1d", metal=0.0, rough=0.96),
        "rebar": m.material("RebarSteel", "#5d4233", metal=0.65, rough=0.72),
        "brick_trim": m.material("BrickTrim", "#8d7362", metal=0.0, rough=0.88),
    }

    nodes = {name: m.node(name, (0.0, 0.0, 0.0), parent=0) for name in MODULE_NODES}

    _build_ground_floor(m, nodes["Floor_Ground_Intact"], mats)
    _build_mid_floor(m, nodes["Floor_Mid_Intact"], mats)
    _build_breached_floor(m, nodes["Floor_Breached"], mats)
    _build_top_jagged_cap(m, nodes["Top_Jagged_Cap"], mats)
    _build_roof_cap(m, nodes["Roof_Cap"], mats)
    _build_rubble_ruin(m, nodes["Rubble_Mound"], mats, mirror_x=False)
    _build_rubble_ruin(m, nodes["Rubble_Mound_B"], mats, mirror_x=True)
    _build_debris_meshes(m, nodes, mats)

    # Assemble pixel-exact Section_Intact_LOD0 from Ground + 4x Mid + Roof, plus simplified LOD1/LOD2
    _clone_module_parts_into(m, nodes["Floor_Ground_Intact"], nodes["Section_Intact_LOD0"], 0.0, "Lod0_F0")
    for f in range(1, FLOORS_TOTAL):
        _clone_module_parts_into(m, nodes["Floor_Mid_Intact"], nodes["Section_Intact_LOD0"], f * FLOOR_HEIGHT, f"Lod0_F{f}")
    _clone_module_parts_into(m, nodes["Roof_Cap"], nodes["Section_Intact_LOD0"], FLOORS_TOTAL * FLOOR_HEIGHT, "Lod0_Roof")

    _build_intact_lod1(m, nodes["Section_Intact_LOD1"], mats)
    _build_intact_lod2(m, nodes["Section_Intact_LOD2"], mats)

    # Bake material baseColorFactor + architectural contact AO into COLOR_0 and unify onto 1 PBR material
    mat_colors = [
        mat["pbrMetallicRoughness"]["baseColorFactor"][:3]
        for mat in m.data["materials"]
    ]
    total_section_h = FLOORS_TOTAL * FLOOR_HEIGHT
    debris_parents = (nodes["Debris_Slab_A"], nodes["Debris_Slab_B"], nodes["Debris_Chunk_C"])
    rubble_parents = (nodes["Rubble_Mound"], nodes["Rubble_Mound_B"])
    section_lod_parents = (nodes["Section_Intact_LOD0"], nodes["Section_Intact_LOD1"], nodes["Section_Intact_LOD2"])

    for part_idx, (node_id, parent, material, points, normals, indices, colors) in enumerate(m.parts):
        base_r, base_g, base_b = mat_colors[material]
        shaded = []
        for p, n, c in zip(points, normals, colors):
            if parent in debris_parents:
                f = 1.0
            else:
                up_factor = 0.90 + 0.12 * max(-1.0, min(1.0, n[1]))
                if parent in rubble_parents:
                    height_ao = 0.76 + 0.26 * min(1.0, max(0.0, p[1] / 3.2))
                elif parent in section_lod_parents:
                    local_y = p[1] % FLOOR_HEIGHT if p[1] < total_section_h else FLOOR_HEIGHT
                    height_ao = 0.88 + 0.12 * min(1.0, max(0.0, local_y / FLOOR_HEIGHT))
                else:
                    height_ao = 0.88 + 0.12 * min(1.0, max(0.0, p[1] / FLOOR_HEIGHT))
                f = max(0.68, min(1.06, up_factor * height_ao))
            shaded.append((
                min(1.0, c[0] * base_r * f),
                min(1.0, c[1] * base_g * f),
                min(1.0, c[2] * base_b * f),
                c[3],
            ))
        m.parts[part_idx] = (node_id, parent, 0, points, normals, indices, shaded)

    m.data["materials"] = [
        {
            "name": "PanelkaArchitectural",
            "pbrMetallicRoughness": {
                "baseColorFactor": [1.0, 1.0, 1.0, 1.0],
                "metallicFactor": 0.05,
                "roughnessFactor": 0.88,
            },
        }
    ]
    m.materials = {"PanelkaArchitectural": 0}

    return m


def export_panelka(output_dir):
    out = Path(output_dir)
    out.mkdir(parents=True, exist_ok=True)
    glb_path = out / "panelka_1_464.glb"

    model = build_panelka_model()
    stats = save_model(model, glb_path)

    manifest = {
        "asset": "panelka_1_464",
        "series": "Soviet 1-464 5-story precast concrete panel apartment",
        "section_footprint_m": [SECTION_WIDTH, SECTION_DEPTH],
        "floor_height_m": FLOOR_HEIGHT,
        "floors_total": FLOORS_TOTAL,
        "rubble_height_m": RUBBLE_HEIGHT,
        "modules": list(MODULE_NODES),
        "glb": stats,
    }
    (out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return glb_path, manifest


def render_review_sheet(glb_path, out_png):
    """Render a 4-panel assembled architectural & war-torn ruin review sheet using matplotlib."""
    import matplotlib

    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib.collections import PolyCollection
    from render_review import camera_basis, read_glb

    data, binary = read_glb(glb_path)
    nodes = data["nodes"]

    def _accessor(index):
        accessor = data["accessors"][index]
        view = data["bufferViews"][accessor["bufferView"]]
        width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[accessor["type"]]
        dtype = "<f4" if accessor["componentType"] == 5126 else "<u4"
        raw = np.frombuffer(binary[view["byteOffset"] : view["byteOffset"] + view["byteLength"]], dtype=dtype)
        return raw.reshape(-1, width) if width > 1 else raw

    def extract_module(module_name):
        idx = next(i for i, n in enumerate(nodes) if n["name"] == module_name)
        tris, cols = [], []

        def visit(ni, offset):
            n = nodes[ni]
            cur = offset + np.asarray(n.get("translation", [0.0, 0.0, 0.0]), dtype=float)
            if "mesh" in n:
                for prim in data["meshes"][n["mesh"]]["primitives"]:
                    attrs = prim["attributes"]
                    pts = _accessor(attrs["POSITION"]).astype(float) + cur
                    tri_idx = _accessor(prim["indices"]).reshape(-1, 3)
                    mat = data["materials"][prim["material"]]
                    base = np.asarray(mat["pbrMetallicRoughness"].get("baseColorFactor", [0.5, 0.5, 0.5, 1.0])[:3])
                    if "COLOR_0" in attrs:
                        vc = _accessor(attrs["COLOR_0"])[:, :3]
                        c = vc[tri_idx].mean(axis=1) * base
                    else:
                        c = np.repeat(base[None, :], len(tri_idx), axis=0)
                    tris.append(pts[tri_idx])
                    cols.append(c)
            for ch in n.get("children", []):
                visit(ch, cur)

        visit(idx, np.zeros(3))
        return np.concatenate(tris), np.concatenate(cols)

    mods = {name: extract_module(name) for name in MODULE_NODES}

    def place(mod_name, tx, ty, tz):
        t, c = mods[mod_name]
        return t + np.array([tx, ty, tz]), c

    def draw_scene(ax, parts, yaw_deg, pitch_deg, title):
        tris = np.concatenate([p[0] for p in parts])
        cols = np.concatenate([p[1] for p in parts])
        view, right, up = camera_basis(yaw_deg, pitch_deg)
        e1 = tris[:, 1] - tris[:, 0]
        e2 = tris[:, 2] - tris[:, 0]
        n = np.cross(e1, e2)
        n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-9)

        facing = (n @ (-view)) > -0.02
        tris = tris[facing]
        cols = cols[facing]
        n = n[facing]

        sun = np.array([0.48, 0.76, 0.44])
        sun /= np.linalg.norm(sun)
        ndotl = np.clip(n @ sun, 0.0, 1.0)
        sky = 0.5 + 0.5 * n[:, 1]
        lit = cols * (0.34 + 0.14 * sky[:, None] + 0.85 * ndotl[:, None])
        srgb = np.where(lit <= 0.0031308, lit * 12.92, 1.055 * np.power(np.clip(lit, 0.0, 1.0), 1.0 / 2.4) - 0.055)
        srgb = np.clip(srgb, 0.0, 1.0)

        centers = tris.mean(axis=1)
        depth = centers @ view
        order = np.argsort(-depth)
        tris = tris[order]
        srgb = srgb[order]

        px = tris @ right
        py = tris @ up
        polys = np.stack([px, py], axis=-1)

        ax.set_facecolor("#495346")
        ax.add_collection(PolyCollection(polys, facecolors=srgb, edgecolors="none", antialiaseds=False))
        xmin, xmax = px.min(), px.max()
        ymin, ymax = py.min(), py.max()
        pad = max(xmax - xmin, ymax - ymin) * 0.10
        cx, cy = (xmin + xmax) * 0.5, (ymin + ymax) * 0.5
        span = max(xmax - xmin, ymax - ymin) * 0.5 + pad
        ax.set_xlim(cx - span, cx + span)
        ax.set_ylim(cy - span, cy + span)
        ax.set_aspect("equal")
        ax.set_xticks([])
        ax.set_yticks([])
        ax.set_title(title, color="#eceae0", fontsize=10, pad=6)

    # 1. Single intact 5-story section (true 14.4x11.8x13.75 m scale)
    intact_1 = [place("Section_Intact_LOD0", 0, 0, 0)]

    # 2. Classic 4-section (57.6 m) Soviet panelka block with war-torn jagged ruins!
    w = SECTION_WIDTH
    block_4 = [
        place("Section_Intact_LOD0", -1.5 * w, 0, 0),
        place("Floor_Ground_Intact", -0.5 * w, 0, 0),
        place("Floor_Mid_Intact", -0.5 * w, FLOOR_HEIGHT, 0),
        place("Floor_Breached", -0.5 * w, 2 * FLOOR_HEIGHT, 0),
        place("Floor_Mid_Intact", -0.5 * w, 3 * FLOOR_HEIGHT, 0),
        place("Floor_Mid_Intact", -0.5 * w, 4 * FLOOR_HEIGHT, 0),
        place("Roof_Cap", -0.5 * w, 5 * FLOOR_HEIGHT, 0),
        place("Floor_Ground_Intact", 0.5 * w, 0, 0),
        place("Floor_Breached", 0.5 * w, FLOOR_HEIGHT, 0),
        place("Top_Jagged_Cap", 0.5 * w, 2 * FLOOR_HEIGHT, 0),
        place("Rubble_Mound", 1.5 * w, 0, 0),
    ]

    # 3. Half-collapsed war-torn shell with Top_Jagged_Cap + breached floor
    jagged_shell = [
        place("Floor_Ground_Intact", 0, 0, 0),
        place("Floor_Breached", 0, FLOOR_HEIGHT, 0),
        place("Top_Jagged_Cap", 0, 2 * FLOOR_HEIGHT, 0),
        place("Debris_Slab_A", -3.8, 0.3, 8.0),
        place("Debris_Slab_B", 0.4, 0.3, 8.3),
        place("Debris_Chunk_C", 3.6, 0.3, 7.8),
    ]

    # 4. Pair of impassable ground-floor battle ruins (Rubble_Mound + Rubble_Mound_B)
    ruins_pair = [
        place("Rubble_Mound", -0.5 * w, 0, 0),
        place("Rubble_Mound_B", 0.5 * w, 0, 0),
    ]

    fig, axes = plt.subplots(2, 2, figsize=(13, 10), facecolor="#262b24")
    draw_scene(axes[0, 0], intact_1, yaw_deg=32, pitch_deg=24, title="Intact Series 1-464 Section (14.4x11.8x13.75m Authentic Scale)")
    draw_scene(axes[0, 1], block_4, yaw_deg=25, pitch_deg=34, title="4-Section (57.6m) Block: Intact -> Breached -> Jagged Shell -> Battle Ruins")
    draw_scene(axes[1, 0], jagged_shell, yaw_deg=24, pitch_deg=22, title="Partial Collapse with Asymmetric Top_Jagged_Cap & Breached Floor")
    draw_scene(axes[1, 1], ruins_pair, yaw_deg=34, pitch_deg=30, title="Impassable Ground-Floor Battle Ruins (Rubble_Mound + Rubble_Mound_B)")
    fig.tight_layout(pad=1.2)
    Path(out_png).parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(out_png, dpi=140, facecolor=fig.get_facecolor())
    plt.close(fig)


def main():
    parser = argparse.ArgumentParser(description="Build modular Soviet Series 1-464 panelka GLB.")
    parser.add_argument("--out", type=Path, default=DEFAULT_OUTPUT_DIR, help="Output asset directory.")
    parser.add_argument("--render", type=Path, default=None, help="Optional PNG path for 4-panel review sheet.")
    args = parser.parse_args()

    glb_path, manifest = export_panelka(args.out)
    print(f"Exported {glb_path} ({manifest['glb']['triangles']} tris, {manifest['glb']['bytes']} bytes)")
    if args.render is not None:
        render_review_sheet(glb_path, args.render)
        print(f"Rendered review sheet to {args.render}")


if __name__ == "__main__":
    main()
