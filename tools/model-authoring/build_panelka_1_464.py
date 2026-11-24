"""Procedural modular Soviet 5-story Series 1-464 panel apartment building (panelka).

Exports a single compact GLB containing reusable architectural modules sized to a
12.0 m x 12.0 m section footprint and 3.1 m floor height (5 floors = 15.5 m, rubble
height = 1.5 m), matching StructureDestructionProfile parameters in Engine.Core:
  - Floor_Ground_Intact: plinth, Soviet entrance porch with concrete visor, stairwell
    window, recessed 3-pane casement windows, and precast panel expansion seams.
  - Floor_Mid_Intact: repeating precast residential floor with cantilevered balconies,
    painted steel railings, recessed casement windows, and stairwell window.
  - Floor_Breached: battle-damaged floor module with blown-out facade breach, charred
    interior cavity, broken slab edges, jutting rebar rods, and slumped balcony.
  - Roof_Cap: bitumen flat roof slab, perimeter parapet, concrete ventilation shafts,
    and Soviet VHF/UHF TV antenna mast.
  - Rubble_Mound: 1.5 m concrete debris pile with tilted fractured wall slabs and rebar.
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
    bake_vertex_shading,
    obox,
    rod,
    save_model,
)

DEFAULT_OUTPUT_DIR = _THIS_DIR.parents[1] / "src/Game.Godot/Assets/Buildings/Panelka1464"

SECTION_WIDTH = 12.0
SECTION_DEPTH = 12.0
HALF_W = SECTION_WIDTH * 0.5
HALF_D = SECTION_DEPTH * 0.5
FLOOR_HEIGHT = 3.1
FLOORS_TOTAL = 5
RUBBLE_HEIGHT = 1.5
ROOF_PARAPET_HEIGHT = 0.42

BAY_CENTERS_X = (-4.5, -1.5, 1.5, 4.5)
BAY_WIDTH = 3.0

MODULE_NODES = (
    "Floor_Ground_Intact",
    "Floor_Mid_Intact",
    "Floor_Breached",
    "Roof_Cap",
    "Rubble_Mound",
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


def _add_window_z(m, prefix, cx, cy, z_face, sign_z, w, h, mats, parent, broken=False):
    """Recessed 3-pane Soviet wooden casement window on a +/-Z facade."""
    z_frame = z_face + sign_z * 0.012
    z_glass = z_face + sign_z * 0.024
    z_mullion = z_face + sign_z * 0.036

    # Single outer wooden frame backing box (saves 36 tris per window vs 4 separate strips)
    _add_box(m, f"{prefix}_Frame", (w, h, 0.03), (cx, cy, z_frame), mats["frame"], parent)

    if not broken:
        # Dark glass inset inside frame + classic Soviet 3-pane mullion & fortochka transom
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
        _add_box(m, f"{prefix}_Hole", (w - 0.12, h - 0.12, 0.08), (cx, cy, z_glass), mats["soot"], parent)

    # Concrete window sill ledge
    _add_box(
        m,
        f"{prefix}_Sill",
        (w + 0.16, 0.08, 0.18),
        (cx, cy - h * 0.5 - 0.04, z_face + sign_z * 0.06),
        mats["seam"],
        parent,
        tint=0.95,
    )


def _add_panel_seams(m, prefix, parent, mats, y_min=0.0, y_max=FLOOR_HEIGHT, skip_front_bays=()):
    """Precast 1-464 concrete joint grooves on all four faces."""
    h = y_max - y_min
    cy = (y_min + y_max) * 0.5
    # Horizontal floor slab seam band along top edge (no overlap when floors stack)
    y_top = y_max - 0.05
    _add_box(m, f"{prefix}_HTopF", (SECTION_WIDTH + 0.04, 0.08, 0.04), (0.0, y_top, HALF_D + 0.01), mats["seam"], parent)
    _add_box(m, f"{prefix}_HTopB", (SECTION_WIDTH + 0.04, 0.08, 0.04), (0.0, y_top, -HALF_D - 0.01), mats["seam"], parent)
    _add_box(m, f"{prefix}_HTopL", (0.04, 0.08, SECTION_DEPTH + 0.04), (-HALF_W - 0.01, y_top, 0.0), mats["seam"], parent)
    _add_box(m, f"{prefix}_HTopR", (0.04, 0.08, SECTION_DEPTH + 0.04), (HALF_W + 0.01, y_top, 0.0), mats["seam"], parent)

    # Vertical precast panel joints every 3.0 m
    for idx, x in enumerate((-3.0, 0.0, 3.0)):
        if idx not in skip_front_bays:
            _add_box(m, f"{prefix}_VF{idx}", (0.07, h, 0.04), (x, cy, HALF_D + 0.01), mats["seam"], parent)
        _add_box(m, f"{prefix}_VB{idx}", (0.07, h, 0.04), (x, cy, -HALF_D - 0.01), mats["seam"], parent)

    for idx, z in enumerate((-3.0, 0.0, 3.0)):
        _add_box(m, f"{prefix}_VL{idx}", (0.04, h, 0.07), (-HALF_W - 0.01, cy, z), mats["seam"], parent)
        _add_box(m, f"{prefix}_VR{idx}", (0.04, h, 0.07), (HALF_W + 0.01, cy, z), mats["seam"], parent)


def _add_balcony(m, prefix, cx, z_face, sign_z, mats, parent, slumped=False):
    """Cantilevered Soviet 1-464 balcony with concrete slab, guard screen, and steel rail."""
    bw = 2.45
    bd = 0.88
    slab_h = 0.14
    guard_h = 0.96
    cz = z_face + sign_z * (bd * 0.5)

    if slumped:
        # Tilted damaged balcony slab hanging downward while staying above Y = 0
        axes = _ortho_axes(0.08 * sign_z, 0.24 * sign_z, -0.10)
        obox(m, f"{prefix}_SlumpSlab", (cx, 0.40, cz), axes, (bw * 0.44, slab_h * 0.5, bd * 0.44), mats["broken"], parent, tint=0.88)
        obox(m, f"{prefix}_SlumpGuard", (cx - 0.25, 0.78, cz + sign_z * 0.34), axes, (bw * 0.34, 0.32, 0.05), mats["balcony_screen"], parent, tint=0.82)
        return

    # Concrete deck slab
    _add_box(m, f"{prefix}_Deck", (bw, slab_h, bd), (cx, slab_h * 0.5 + 0.04, cz), mats["concrete"], parent, tint=0.92)
    # Front and side asbestos-cement/concrete guard panels
    panel_y = slab_h + 0.04 + guard_h * 0.44
    panel_h = guard_h * 0.82
    z_front = z_face + sign_z * (bd - 0.04)
    _add_box(m, f"{prefix}_FrontPanel", (bw - 0.06, panel_h, 0.06), (cx, panel_y, z_front), mats["balcony_screen"], parent, tint=0.98)
    _add_box(m, f"{prefix}_SideL", (0.06, panel_h, bd - 0.06), (cx - bw * 0.5 + 0.04, panel_y, cz), mats["balcony_screen"], parent, tint=0.94)
    _add_box(m, f"{prefix}_SideR", (0.06, panel_h, bd - 0.06), (cx + bw * 0.5 - 0.04, panel_y, cz), mats["balcony_screen"], parent, tint=0.94)

    # Painted metal top handrail and corner stanchions
    rail_y = slab_h + 0.04 + guard_h
    _add_box(m, f"{prefix}_RailF", (bw + 0.02, 0.05, 0.05), (cx, rail_y, z_front), mats["rail"], parent)
    _add_box(m, f"{prefix}_RailL", (0.05, 0.05, bd), (cx - bw * 0.5 + 0.03, rail_y, cz), mats["rail"], parent)
    _add_box(m, f"{prefix}_RailR", (0.05, 0.05, bd), (cx + bw * 0.5 - 0.03, rail_y, cz), mats["rail"], parent)
    for tag, px in (("PL", cx - bw * 0.5 + 0.04), ("PR", cx + bw * 0.5 - 0.04)):
        _add_box(m, f"{prefix}_{tag}", (0.05, guard_h, 0.05), (px, slab_h + 0.04 + guard_h * 0.5, z_front), mats["rail"], parent)

    # Recessed balcony door + side window behind the balcony
    _add_box(m, f"{prefix}_DoorGlass", (0.72, 2.05, 0.03), (cx - 0.48, 1.24, z_face + sign_z * 0.006), mats["glass"], parent)
    _add_box(m, f"{prefix}_DoorFrame", (0.82, 2.15, 0.05), (cx - 0.48, 1.24, z_face + sign_z * 0.002), mats["frame"], parent)
    _add_window_z(m, f"{prefix}_Win", cx + 0.42, 1.56, z_face, sign_z, 1.05, 1.38, mats, parent)


def _add_end_wall_details(m, prefix, parent, mats, y_min=0.55):
    """Subtle narrow utility windows on -X / +X end gables for standalone readability."""
    for tag, sx in (("L", -1.0), ("R", 1.0)):
        x_face = sx * HALF_W
        for w_idx, wz in enumerate((-2.8, 2.8)):
            _add_box(m, f"{prefix}_EW_{tag}{w_idx}_G", (0.03, 1.28, 0.84), (x_face + sx * 0.008, 1.62, wz), mats["glass"], parent)
            _add_box(m, f"{prefix}_EW_{tag}{w_idx}_F", (0.05, 1.38, 0.94), (x_face + sx * 0.004, 1.62, wz), mats["frame"], parent)
            _add_box(m, f"{prefix}_EW_{tag}{w_idx}_S", (0.14, 0.07, 1.04), (x_face + sx * 0.05, 0.92, wz), mats["seam"], parent)


def _build_ground_floor(m, parent, mats):
    plinth_h = 0.55
    # Foundation socle slightly inset horizontally with darker concrete
    _add_box(m, "Ground_Plinth", (SECTION_WIDTH - 0.06, plinth_h, SECTION_DEPTH - 0.06), (0.0, plinth_h * 0.5, 0.0), mats["plinth"], parent)

    # Main 1st-floor precast structural body (0.55..3.10 m)
    wall_h = FLOOR_HEIGHT - plinth_h
    wall_cy = plinth_h + wall_h * 0.5
    # Four slight tonal panel blocks across X so precast concrete feels modular
    panel_tints = (0.98, 1.02, 0.97, 1.01)
    for i, bx in enumerate(BAY_CENTERS_X):
        _add_box(m, f"Ground_Bay{i}", (BAY_WIDTH, wall_h, SECTION_DEPTH), (bx, wall_cy, 0.0), mats["concrete"], parent, tint=panel_tints[i])

    _add_panel_seams(m, "Ground", parent, mats, y_min=plinth_h, y_max=FLOOR_HEIGHT)

    # Basement vent grates along plinth
    for bx in (-4.5, 1.5, 4.5):
        _add_box(m, f"Ground_VentF_{bx}", (0.72, 0.24, 0.06), (bx, 0.28, HALF_D - 0.01), mats["soot"], parent)
        _add_box(m, f"Ground_VentB_{bx}", (0.72, 0.24, 0.06), (bx, 0.28, -HALF_D + 0.01), mats["soot"], parent)

    # Front facade (+Z): Bay 1 (-1.5) is the iconic Soviet entrance (pod'ezd)
    ent_x = BAY_CENTERS_X[1]
    # Concrete entrance steps
    _add_box(m, "Ent_Step1", (2.15, 0.20, 0.85), (ent_x, 0.10, HALF_D + 0.42), mats["plinth"], parent)
    _add_box(m, "Ent_Step2", (1.95, 0.20, 0.52), (ent_x, 0.30, HALF_D + 0.26), mats["plinth"], parent)
    # Recessed dark steel entrance door + surround frame
    _add_box(m, "Ent_Surround", (1.72, 2.24, 0.12), (ent_x, 1.52, HALF_D + 0.02), mats["seam"], parent)
    _add_box(m, "Ent_Door", (1.36, 2.02, 0.08), (ent_x, 1.44, HALF_D + 0.05), mats["rail"], parent, tint=0.88)
    _add_box(m, "Ent_Transom", (1.32, 0.34, 0.04), (ent_x, 2.66, HALF_D + 0.04), mats["glass"], parent)
    # Cantilevered concrete visor (kozyrek) with cheek brackets
    _add_box(m, "Ent_Visor", (2.25, 0.15, 1.05), (ent_x, 2.58, HALF_D + 0.52), mats["concrete"], parent, tint=0.90)
    _add_box(m, "Ent_VisorTrim", (2.29, 0.06, 1.09), (ent_x, 2.66, HALF_D + 0.52), mats["roof_tar"], parent)
    _add_box(m, "Ent_BracketL", (0.12, 0.48, 0.72), (ent_x - 0.96, 2.30, HALF_D + 0.36), mats["seam"], parent)
    _add_box(m, "Ent_BracketR", (0.12, 0.48, 0.72), (ent_x + 0.96, 2.30, HALF_D + 0.36), mats["seam"], parent)

    # Apartment windows on remaining front bays (-4.5, +1.5, +4.5)
    for i, bx in enumerate((BAY_CENTERS_X[0], BAY_CENTERS_X[2], BAY_CENTERS_X[3])):
        _add_window_z(m, f"Ground_WF{i}", bx, 1.78, HALF_D, 1.0, 1.72, 1.44, mats, parent)

    # Rear facade (-Z): 4 apartment windows
    for i, bx in enumerate(BAY_CENTERS_X):
        _add_window_z(m, f"Ground_WB{i}", bx, 1.78, -HALF_D, -1.0, 1.72, 1.44, mats, parent)

    _add_end_wall_details(m, "Ground", parent, mats)


def _build_mid_floor(m, parent, mats):
    panel_tints = (1.01, 0.96, 1.02, 0.98)
    for i, bx in enumerate(BAY_CENTERS_X):
        _add_box(m, f"Mid_Bay{i}", (BAY_WIDTH, FLOOR_HEIGHT, SECTION_DEPTH), (bx, FLOOR_HEIGHT * 0.5, 0.0), mats["concrete"], parent, tint=panel_tints[i])

    # Subtle decorative ochre-grey concrete band under balconies (typical 1-464 trim)
    _add_box(m, "Mid_TrimF", (SECTION_WIDTH + 0.02, 0.14, 0.03), (0.0, 0.12, HALF_D + 0.008), mats["brick_trim"], parent, tint=0.95)
    _add_box(m, "Mid_TrimB", (SECTION_WIDTH + 0.02, 0.14, 0.03), (0.0, 0.12, -HALF_D - 0.008), mats["brick_trim"], parent, tint=0.95)

    _add_panel_seams(m, "Mid", parent, mats)

    # Front facade (+Z):
    # Bay 0 (-4.5) and Bay 2 (+1.5): balconies!
    _add_balcony(m, "Mid_BalcF0", BAY_CENTERS_X[0], HALF_D, 1.0, mats, parent)
    _add_balcony(m, "Mid_BalcF2", BAY_CENTERS_X[2], HALF_D, 1.0, mats, parent)
    # Bay 1 (-1.5): stairwell vertical half-landing window (offset lower, taller)
    _add_window_z(m, "Mid_StairF", BAY_CENTERS_X[1], 1.28, HALF_D, 1.0, 1.18, 1.82, mats, parent)
    # Bay 3 (+4.5): standard 3-pane apartment window
    _add_window_z(m, "Mid_WF3", BAY_CENTERS_X[3], 1.56, HALF_D, 1.0, 1.72, 1.44, mats, parent)

    # Rear facade (-Z):
    # Bay 1 (-1.5) and Bay 3 (+4.5): balconies; Bay 0 & Bay 2: apartment windows
    _add_window_z(m, "Mid_WB0", BAY_CENTERS_X[0], 1.56, -HALF_D, -1.0, 1.72, 1.44, mats, parent)
    _add_balcony(m, "Mid_BalcB1", BAY_CENTERS_X[1], -HALF_D, -1.0, mats, parent)
    _add_window_z(m, "Mid_WB2", BAY_CENTERS_X[2], 1.56, -HALF_D, -1.0, 1.72, 1.44, mats, parent)
    _add_balcony(m, "Mid_BalcB3", BAY_CENTERS_X[3], -HALF_D, -1.0, mats, parent)

    _add_end_wall_details(m, "Mid", parent, mats)


def _build_breached_floor(m, parent, mats):
    """Battle-damaged floor with blown-out front-center cavity, exposed interior slabs, and rebar."""
    # Rear half remains structurally intact (Z in [-6.0, 0.2])
    _add_box(m, "Br_RearCore", (SECTION_WIDTH, FLOOR_HEIGHT, 6.2), (0.0, FLOOR_HEIGHT * 0.5, -2.9), mats["concrete"], parent, tint=0.90)
    # Left and right intact corner piers on front half (Z in [0.2, 6.0])
    _add_box(m, "Br_PierL", (3.25, FLOOR_HEIGHT, 5.8), (-4.375, FLOOR_HEIGHT * 0.5, 3.1), mats["concrete"], parent, tint=0.88)
    _add_box(m, "Br_PierR", (2.85, FLOOR_HEIGHT, 5.8), (4.575, FLOOR_HEIGHT * 0.5, 3.1), mats["concrete"], parent, tint=0.86)

    # Exposed lower floor slab and upper ceiling slab across the blown-out center breach (X in [-2.8, +3.2])
    _add_box(m, "Br_FloorSlab", (6.2, 0.26, 5.4), (0.2, 0.13, 2.9), mats["broken"], parent, tint=0.85)
    _add_box(m, "Br_CeilSlab", (6.0, 0.24, 5.0), (0.15, FLOOR_HEIGHT - 0.12, 2.7), mats["broken"], parent, tint=0.82)

    # Dark scorched interior cavity backdrop and shattered interior partition wall stub
    _add_box(m, "Br_CavityBack", (5.8, FLOOR_HEIGHT - 0.45, 0.30), (0.2, FLOOR_HEIGHT * 0.5, 0.35), mats["soot"], parent)
    _add_box(m, "Br_PartitionStub", (0.22, FLOOR_HEIGHT - 0.55, 3.4), (-0.45, FLOOR_HEIGHT * 0.5, 2.0), mats["broken"], parent, tint=0.72)

    # Jagged broken concrete edge blocks framing the breach
    jagged_specs = [
        ((-2.65, 2.35, 5.82), (0.75, 1.05, 0.28), (0.0, 0.0, -0.18)),
        ((-2.55, 0.72, 5.78), (0.85, 0.92, 0.28), (0.0, 0.0, 0.14)),
        ((2.98, 2.25, 5.80), (0.80, 1.15, 0.28), (0.0, 0.0, 0.20)),
        ((2.88, 0.68, 5.75), (0.95, 0.85, 0.28), (0.0, 0.0, -0.16)),
        ((0.15, 0.48, 5.45), (2.40, 0.46, 0.36), (0.05, -0.10, 0.06)),
    ]
    for i, (center, size, rot) in enumerate(jagged_specs):
        axes = _ortho_axes(*rot)
        obox(m, f"Br_Jag{i}", center, axes, (size[0] * 0.5, size[1] * 0.5, size[2] * 0.5), mats["broken"], parent, tint=0.84)

    # Bent rusted steel rebar rods protruding from fractured concrete slabs and piers
    rebar_segments = [
        ((-2.45, 2.65, 5.85), (-1.55, 2.25, 6.22)),
        ((-2.42, 1.85, 5.82), (-1.68, 1.72, 6.08)),
        ((-2.38, 0.95, 5.82), (-1.42, 1.15, 6.14)),
        ((2.82, 2.55, 5.82), (1.92, 2.10, 6.20)),
        ((2.78, 1.40, 5.80), (1.86, 1.55, 6.12)),
        ((-0.85, 2.92, 5.20), (-0.70, 2.28, 5.78)),
        ((0.65, 2.94, 5.22), (0.95, 2.18, 5.85)),
        ((1.45, 0.25, 5.55), (1.20, 0.78, 6.05)),
    ]
    for i, (p0, p1) in enumerate(rebar_segments):
        rod(m, f"Br_Rebar{i}", p0, p1, 0.028, mats["rebar"], parent, sides=5)

    # Scorch soot smear around breach exterior (front and rear)
    _add_box(m, "Br_SootL", (0.75, 2.20, 0.04), (-3.10, 1.65, HALF_D + 0.02), mats["soot"], parent)
    _add_box(m, "Br_SootR", (0.75, 2.10, 0.04), (3.45, 1.70, HALF_D + 0.02), mats["soot"], parent)
    _add_box(m, "Br_RearBlast", (3.60, 1.95, 0.14), (0.0, 1.55, -HALF_D - 0.03), mats["soot"], parent)
    _add_box(m, "Br_RearEdgeL", (0.55, 2.10, 0.18), (-1.95, 1.55, -HALF_D - 0.02), mats["broken"], parent, tint=0.80)
    _add_box(m, "Br_RearEdgeR", (0.55, 2.10, 0.18), (1.95, 1.55, -HALF_D - 0.02), mats["broken"], parent, tint=0.80)
    rod(m, "Br_RearRebar0", (-1.75, 2.15, -HALF_D - 0.04), (-0.95, 1.85, -HALF_D - 0.42), 0.028, mats["rebar"], parent, sides=5)
    rod(m, "Br_RearRebar1", (1.72, 1.25, -HALF_D - 0.04), (0.88, 1.45, -HALF_D - 0.38), 0.028, mats["rebar"], parent, sides=5)

    # Slumped balcony on bay 0, shattered window on bay 3, rear windows darkened/broken
    _add_balcony(m, "Br_BalcF0", BAY_CENTERS_X[0], HALF_D, 1.0, mats, parent, slumped=True)
    _add_window_z(m, "Br_WF3", BAY_CENTERS_X[3], 1.56, HALF_D, 1.0, 1.72, 1.44, mats, parent, broken=True)
    for i, bx in enumerate(BAY_CENTERS_X):
        _add_window_z(m, f"Br_WB{i}", bx, 1.56, -HALF_D, -1.0, 1.72, 1.44, mats, parent, broken=(i in (1, 2)))

    _add_panel_seams(m, "Br", parent, mats, skip_front_bays=(0, 1, 2))


def _build_roof_cap(m, parent, mats):
    """Flat Soviet ruberoid roof with perimeter parapet, concrete vent shafts, and TV aerial."""
    # Main bitumen roof deck slab (sits cleanly above floor top with +0.02 m clearance)
    _add_box(m, "Roof_Deck", (SECTION_WIDTH - 0.16, 0.12, SECTION_DEPTH - 0.16), (0.0, 0.08, 0.0), mats["roof_tar"], parent)

    # Perimeter concrete parapet walls (height = ROOF_PARAPET_HEIGHT = 0.42 m)
    pt = 0.28
    ph = ROOF_PARAPET_HEIGHT
    _add_box(m, "Roof_ParF", (SECTION_WIDTH + 0.08, ph, pt), (0.0, ph * 0.5, HALF_D - pt * 0.5 + 0.04), mats["concrete"], parent, tint=0.92)
    _add_box(m, "Roof_ParB", (SECTION_WIDTH + 0.08, ph, pt), (0.0, ph * 0.5, -HALF_D + pt * 0.5 - 0.04), mats["concrete"], parent, tint=0.92)
    _add_box(m, "Roof_ParL", (pt, ph, SECTION_DEPTH - pt), (-HALF_W + pt * 0.5 - 0.04, ph * 0.5, 0.0), mats["concrete"], parent, tint=0.90)
    _add_box(m, "Roof_ParR", (pt, ph, SECTION_DEPTH - pt), (HALF_W - pt * 0.5 + 0.04, ph * 0.5, 0.0), mats["concrete"], parent, tint=0.90)

    # Galvanized metal parapet coping strip along top edge
    _add_box(m, "Roof_CopF", (SECTION_WIDTH + 0.12, 0.04, pt + 0.06), (0.0, ph + 0.02, HALF_D - pt * 0.5 + 0.04), mats["seam"], parent)
    _add_box(m, "Roof_CopB", (SECTION_WIDTH + 0.12, 0.04, pt + 0.06), (0.0, ph + 0.02, -HALF_D + pt * 0.5 - 0.04), mats["seam"], parent)

    # Concrete stairwell roof access penthouse / hatch box
    _add_box(m, "Roof_AccessBox", (2.10, 0.92, 1.80), (-1.50, 0.52, 0.60), mats["concrete"], parent, tint=0.88)
    _add_box(m, "Roof_AccessLid", (2.24, 0.10, 1.94), (-1.50, 1.02, 0.60), mats["roof_tar"], parent)

    # Two pairs of precast Soviet rooftop ventilation shafts (ventshakhty)
    for i, (vx, vz) in enumerate(((-3.8, -2.4), (2.4, -2.4), (3.4, 2.2))):
        _add_box(m, f"Roof_Vent{i}", (1.35, 0.78, 0.72), (vx, 0.45, vz), mats["concrete"], parent, tint=0.86)
        _add_box(m, f"Roof_VentCap{i}", (1.50, 0.10, 0.86), (vx, 0.89, vz), mats["seam"], parent)
        _add_box(m, f"Roof_VentGap{i}", (1.18, 0.18, 0.58), (vx, 0.72, vz), mats["soot"], parent)

    # Iconic Soviet collective TV antenna mast + cross dipoles
    mast_x, mast_z = 1.65, -0.40
    rod(m, "Roof_TvMast", (mast_x, 0.10, mast_z), (mast_x, 2.65, mast_z), 0.032, mats["rail"], parent, sides=6)
    rod(m, "Roof_TvBoom1", (mast_x - 0.78, 2.38, mast_z), (mast_x + 0.78, 2.38, mast_z), 0.018, mats["rail"], parent, sides=5)
    rod(m, "Roof_TvBoom2", (mast_x - 0.52, 2.02, mast_z), (mast_x + 0.52, 2.02, mast_z), 0.018, mats["rail"], parent, sides=5)
    rod(m, "Roof_TvCross", (mast_x, 2.38, mast_z - 0.56), (mast_x, 2.38, mast_z + 0.56), 0.018, mats["rail"], parent, sides=5)


def _build_rubble_mound(m, parent, mats):
    """Collapsed 1.5 m concrete rubble heap with leaning broken wall slabs and twisted rebar."""
    # Stepped/tiered rubble core rising cleanly to RUBBLE_HEIGHT (1.50 m) within 11.8 x 11.8 m footprint
    _add_box(m, "Rub_Base", (11.6, 0.48, 11.6), (0.0, 0.24, 0.0), mats["broken"], parent, tint=0.84)
    _add_box(m, "Rub_Tier1", (9.6, 0.46, 9.4), (0.15, 0.68, -0.10), mats["broken"], parent, tint=0.88)
    _add_box(m, "Rub_Tier2", (6.8, 0.40, 6.4), (-0.25, 1.06, 0.20), mats["broken"], parent, tint=0.92)
    _add_box(m, "Rub_Peak", (3.6, 0.28, 3.2), (-0.10, 1.34, 0.05), mats["concrete"], parent, tint=0.86)

    # Scorched dark dust/soot depressions across rubble heap
    _add_box(m, "Rub_Soot1", (3.8, 0.14, 2.4), (2.4, 0.52, 2.6), mats["soot"], parent)
    _add_box(m, "Rub_Soot2", (3.2, 0.14, 2.8), (-2.8, 0.50, -2.2), mats["soot"], parent)

    # Leaning fractured precast wall panels (giving unmistakable collapsed-panelka silhouette!)
    slab_specs = [
        ((-3.4, 0.72, 2.9), (2.9, 0.22, 2.1), (0.35, 0.28, -0.22), "concrete", 0.96),
        ((3.1, 0.68, 2.4), (2.7, 0.22, 1.9), (-0.42, -0.25, 0.24), "concrete", 0.92),
        ((-2.6, 0.85, -3.1), (3.1, 0.22, 2.0), (0.20, -0.30, 0.18), "broken", 0.90),
        ((2.8, 0.78, -2.7), (2.8, 0.22, 2.1), (-0.28, 0.26, -0.20), "concrete", 0.94),
        ((0.1, 1.18, 1.3), (2.6, 0.20, 1.7), (0.12, 0.22, 0.14), "concrete", 0.98),
        ((-0.6, 1.12, -1.4), (2.4, 0.20, 1.6), (-0.18, -0.20, -0.15), "broken", 0.86),
        ((-4.6, 0.56, -0.4), (2.0, 0.24, 1.5), (0.45, 0.14, 0.20), "broken", 0.82),
        ((4.5, 0.58, 0.3), (2.1, 0.24, 1.5), (-0.42, -0.14, -0.20), "concrete", 0.88),
    ]
    for i, (center, size, rot, mat_key, tint) in enumerate(slab_specs):
        axes = _ortho_axes(*rot)
        _add_grounded_obox(m, f"Rub_Slab{i}", center, axes, (size[0] * 0.5, size[1] * 0.5, size[2] * 0.5), mats[mat_key], parent, tint=tint)

    # Scattered smaller concrete boulders around perimeter
    chunk_specs = [
        ((-4.9, 0.30, 4.4), (1.1, 0.55, 0.9), (0.4, 0.1, 0.2)),
        ((4.7, 0.28, 4.5), (1.0, 0.50, 0.85), (-0.3, -0.15, 0.1)),
        ((-4.8, 0.32, -4.3), (1.2, 0.58, 0.95), (0.2, 0.12, -0.2)),
        ((4.8, 0.26, -4.4), (0.95, 0.48, 0.80), (-0.5, 0.1, 0.15)),
        ((0.4, 0.42, 4.7), (1.4, 0.52, 0.88), (0.15, -0.12, 0.08)),
        ((-0.5, 0.40, -4.6), (1.3, 0.54, 0.92), (-0.22, 0.14, -0.1)),
    ]
    for i, (center, size, rot) in enumerate(chunk_specs):
        axes = _ortho_axes(*rot)
        _add_grounded_obox(m, f"Rub_Chunk{i}", center, axes, (size[0] * 0.5, size[1] * 0.5, size[2] * 0.5), mats["broken"], parent, tint=0.80)

    # Jutting bent steel rebar spikes
    rebar_spikes = [
        ((-1.4, 0.95, 1.6), (-1.9, 1.48, 2.2)),
        ((1.3, 0.92, 1.2), (1.8, 1.46, 1.9)),
        ((-0.2, 1.15, -0.8), (-0.6, 1.49, -1.5)),
        ((2.2, 0.70, -1.8), (2.7, 1.32, -2.3)),
        ((-2.6, 0.68, 0.5), (-3.2, 1.28, 0.9)),
        ((0.8, 0.85, 2.4), (0.5, 1.36, 3.0)),
    ]
    for i, (p0, p1) in enumerate(rebar_spikes):
        rod(m, f"Rub_Rebar{i}", p0, p1, 0.030, mats["rebar"], parent, sides=5)


def _build_debris_meshes(m, nodes, mats):
    """Three single-material low-poly meshes used directly by MultiMesh debris physics."""
    # Debris_Slab_A: fractured precast wall panel slab (~1.55 x 0.22 x 1.05 m)
    pts_a = [
        (-0.78, -0.11, -0.52),
        (0.74, -0.11, -0.46),
        (0.80, -0.11, 0.42),
        (-0.62, -0.11, 0.54),
        (-0.75, 0.11, -0.50),
        (0.70, 0.11, -0.44),
        (0.76, 0.11, 0.40),
        (-0.60, 0.11, 0.50),
    ]
    faces_box = [(0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
    m.add_flat_mesh("DebrisSlabA_Geo", pts_a, faces_box, mats["concrete"], nodes["Debris_Slab_A"], auto_orient=True, tint=0.92)

    # Debris_Slab_B: broken balcony/corner concrete beam (~1.05 x 0.28 x 0.72 m)
    pts_b = [
        (-0.54, -0.14, -0.36),
        (0.50, -0.14, -0.32),
        (0.46, -0.14, 0.36),
        (-0.50, -0.14, 0.30),
        (-0.48, 0.14, -0.32),
        (0.44, 0.14, -0.28),
        (0.42, 0.14, 0.32),
        (-0.46, 0.14, 0.26),
    ]
    m.add_flat_mesh("DebrisSlabB_Geo", pts_b, faces_box, mats["broken"], nodes["Debris_Slab_B"], auto_orient=True, tint=0.88)

    # Debris_Chunk_C: faceted irregular concrete rubble chunk (~0.56 x 0.42 x 0.48 m)
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
    m.add_flat_mesh("DebrisChunkC_Geo", pts_c, faces_box, mats["broken"], nodes["Debris_Chunk_C"], auto_orient=True, tint=0.82)


def build_panelka_model():
    m = GltfModel("Panelka_1_464", surface_weathering=False)
    m.data["asset"]["generator"] = "Dead Ground Panelka 1-464 model authoring"

    mats = {
        "concrete": m.material("ConcretePanel", "#b7b3a7", metal=0.0, rough=0.90),
        "seam": m.material("ConcreteSeam", "#6d6b63", metal=0.0, rough=0.94),
        "plinth": m.material("ConcretePlinth", "#7b7a74", metal=0.0, rough=0.92),
        "broken": m.material("ConcreteBroken", "#8e897f", metal=0.0, rough=0.95),
        "frame": m.material("WindowFrame", "#dcd6c8", metal=0.0, rough=0.78),
        "glass": m.material("WindowGlass", "#25343d", metal=0.18, rough=0.22),
        "rail": m.material("BalconyRail", "#44524c", metal=0.45, rough=0.62),
        "balcony_screen": m.material("BalconyScreen", "#9ea39b", metal=0.0, rough=0.88),
        "roof_tar": m.material("RoofTar", "#3a3b3d", metal=0.0, rough=0.92),
        "soot": m.material("SootInterior", "#242220", metal=0.0, rough=0.96),
        "rebar": m.material("RebarSteel", "#5e4334", metal=0.65, rough=0.72),
        "brick_trim": m.material("BrickTrim", "#8f7565", metal=0.0, rough=0.88),
    }

    nodes = {name: m.node(name, (0.0, 0.0, 0.0), parent=0) for name in MODULE_NODES}

    _build_ground_floor(m, nodes["Floor_Ground_Intact"], mats)
    _build_mid_floor(m, nodes["Floor_Mid_Intact"], mats)
    _build_breached_floor(m, nodes["Floor_Breached"], mats)
    _build_roof_cap(m, nodes["Roof_Cap"], mats)
    _build_rubble_mound(m, nodes["Rubble_Mound"], mats)
    _build_debris_meshes(m, nodes, mats)

    # Bake clean ambient occlusion per module independently so modules don't shadow one another
    # (all modules share origin (0,0,0), so we only apply subtle vertical shading per vertex).
    for part_idx, (node_id, parent, material, points, normals, indices, colors) in enumerate(m.parts):
        if parent in (nodes["Debris_Slab_A"], nodes["Debris_Slab_B"], nodes["Debris_Chunk_C"]):
            continue
        shaded = []
        for p, n, c in zip(points, normals, colors):
            # Subtle architectural contact shading: slightly darker near floor base/eaves and downward faces
            up_factor = 0.90 + 0.12 * max(-1.0, min(1.0, n[1]))
            if parent == nodes["Rubble_Mound"]:
                height_ao = 0.78 + 0.24 * min(1.0, max(0.0, p[1] / RUBBLE_HEIGHT))
            else:
                height_ao = 0.88 + 0.12 * min(1.0, max(0.0, p[1] / FLOOR_HEIGHT))
            f = max(0.68, min(1.06, up_factor * height_ao))
            shaded.append((c[0] * f, c[1] * f, c[2] * f, c[3]))
        m.parts[part_idx] = (node_id, parent, material, points, normals, indices, shaded)

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
    """Render a 4-panel assembled architectural & destruction review sheet using matplotlib."""
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

        # Backface cull
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
        ax.set_title(title, color="#eceae0", fontsize=11, pad=6)

    # 1. Single intact 5-story section
    intact_1 = [place("Floor_Ground_Intact", 0, 0, 0)]
    for f in range(1, 5):
        intact_1.append(place("Floor_Mid_Intact", 0, f * FLOOR_HEIGHT, 0))
    intact_1.append(place("Roof_Cap", 0, 5 * FLOOR_HEIGHT, 0))

    # 2. Classic 4-section (48 m) Soviet panelka block with progressive battle damage!
    block_4 = []
    # Section 0: intact 5 floors
    block_4.append(place("Floor_Ground_Intact", -18, 0, 0))
    for f in range(1, 5):
        block_4.append(place("Floor_Mid_Intact", -18, f * FLOOR_HEIGHT, 0))
    block_4.append(place("Roof_Cap", -18, 5 * FLOOR_HEIGHT, 0))
    # Section 1: floor 2 breached, 5 floors standing
    block_4.append(place("Floor_Ground_Intact", -6, 0, 0))
    block_4.append(place("Floor_Mid_Intact", -6, FLOOR_HEIGHT, 0))
    block_4.append(place("Floor_Breached", -6, 2 * FLOOR_HEIGHT, 0))
    block_4.append(place("Floor_Mid_Intact", -6, 3 * FLOOR_HEIGHT, 0))
    block_4.append(place("Floor_Mid_Intact", -6, 4 * FLOOR_HEIGHT, 0))
    block_4.append(place("Roof_Cap", -6, 5 * FLOOR_HEIGHT, 0))
    # Section 2: partial collapse (2 floors left, top breached)
    block_4.append(place("Floor_Ground_Intact", 6, 0, 0))
    block_4.append(place("Floor_Breached", 6, FLOOR_HEIGHT, 0))
    # Section 3: full collapse -> rubble mound
    block_4.append(place("Rubble_Mound", 18, 0, 0))

    # 3. Close-up of Floor_Breached + Debris meshes
    breached_detail = [
        place("Floor_Ground_Intact", 0, 0, 0),
        place("Floor_Breached", 0, FLOOR_HEIGHT, 0),
        place("Debris_Slab_A", -3.2, 0.3, 8.2),
        place("Debris_Slab_B", 0.4, 0.3, 8.5),
        place("Debris_Chunk_C", 3.1, 0.3, 8.0),
    ]

    # 4. Full collapse rubble mound close-up
    rubble_only = [place("Rubble_Mound", 0, 0, 0)]

    fig, axes = plt.subplots(2, 2, figsize=(13, 10), facecolor="#262b24")
    draw_scene(axes[0, 0], intact_1, yaw_deg=32, pitch_deg=24, title="Intact 5-Story Series 1-464 Section (Front +Z Courtyard Facade)")
    draw_scene(axes[0, 1], block_4, yaw_deg=25, pitch_deg=36, title="4-Section Block (RTS Camera): Intact -> Breached -> Partial -> Rubble")
    draw_scene(axes[1, 0], breached_detail, yaw_deg=22, pitch_deg=20, title="Breached Floor Cavity, Rebar, Slumped Balcony + Debris Meshes")
    draw_scene(axes[1, 1], rubble_only, yaw_deg=38, pitch_deg=34, title="Collapsed Rubble Mound (11.8x11.8x1.5m)")
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
