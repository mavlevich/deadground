"""Procedural BTR-70 (GAZ-4905) 8x8 APC visual study for Dead Ground, with three explicit LODs.

Calibrated against published BTR-70 1:35 scale factory drawings (side, front, top, rear)
and technical documentation for the GAZ-4905 chassis:
- Exact 8x8 axle stations (WHEEL_Z = [-2.020, -0.635, +1.080, +2.450] m) with long bow overhang,
  tightly paired front and rear bogies, central troop door at Z = +0.28 m, and compact stern overhang
- 13.00-18 tires (WHEEL_R = 0.560 m, diameter 1.120 m) on 2.400 m track gauge
- Overall length 7.540 m (bounds [-3.770, +3.770] m from bow shackle to stern shackle)
- Overall width 2.800 m (bounds [-1.400, +1.400] m over chine rails)
- Clean roofline: forward periscopes, crew hatches, troop hatches, and rear engine cooling louvres;
  no external stowage boxes, barrels or tarps on the roof
- BPU-1 conical turret with OU-3GA2 searchlight and 14.5 mm KPVT gun
- Three explicit LODs within budgets: LOD0 <= 12k, LOD1 <= 6k, LOD2 <= 2k
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
    MarkingAtlas,
    add_conformal_decal,
    bake_vertex_shading,
    decal_material,
    domed_hatch,
    emit_motion_parts,
    loft_sections,
    obox,
    revolve,
    rod,
    surface_heights,
    surface_hit,
    tag_wheel,
    tube,
)

DEFAULT_OUTPUT_DIR = _THIS_DIR.parents[1] / "src/Game.Godot/Assets/Vehicles/BTR70"

# Reference dimensions in meters (+Y up, -Z forward, ground at Y = 0)
HULL_LENGTH = 7.420
OVERALL_LENGTH = 7.540
WIDTH_OVER_HULL = 2.800
HEIGHT_TO_TURRET_TOP = 2.235
GROUND_CLEARANCE = 0.475

# Hull body extents in Z: front prow at -3.710, stern transom at +3.710.
# Front and rear towing shackles reach -3.770 and +3.770, making overall length 7.540 m.
HULL_FRONT_Z = -3.710
HULL_REAR_Z = 3.710

# Vertical stations
BELLY_Y = GROUND_CLEARANCE          # 0.475 m
ARCH_LIP_Y = 1.185                  # horizontal wheel-arch cutout lip
CHINE_Y = 1.280                     # main hull chine ledge
ROOF_Y = 1.720                      # flat horizontal main hull roof
REAR_ROOF_Y = 1.685                 # engine deck step
STERN_TOP_Y = 1.610                 # top of rear transom
NOSE_TIP_Y = 1.160                  # sharp horizontal prow edge
GLACIS_START_Z = -3.420             # start of main sloped front glacis
GLACIS_START_Y = 1.210
CAB_ROOF_Z = -2.260                 # front edge of horizontal roof
FIGHTING_REAR_Z = 1.480             # transition to rear engine deck
ENGINE_SLOPE_Z = 3.540              # rear of engine deck before stern transom

# Running gear: 8x8 wheeled chassis with 13.00-18 tires (diameter 1.12 m, R = 0.56 m)
TRACK_GAUGE = 2.400
WHEEL_X_CENTER = TRACK_GAUGE / 2.0  # 1.200 m
WHEEL_R = 0.560                     # tire radius 0.560 m (diameter 1.12 m)
WHEEL_Y = WHEEL_R                   # axle height at 0.560 m, resting on ground at Y = 0
TIRE_WIDTH = 0.350
WHEEL_Z = [-2.020, -0.635, 1.080, 2.450]
DOOR_CENTER_Z = 0.280

# BPU-1 squat conical turret seated at Z = -1.028 m
TURRET_RING = (0.0, ROOF_Y + 0.025, -1.028)
TURRET_HEIGHT = 0.355
TURRET_PROFILE = [
    (0.675, 0.000),
    (0.668, 0.032),
    (0.620, 0.105),
    (0.560, 0.190),
    (0.500, 0.270),
    (0.445, 0.338),
    (0.405, 0.351),
    (0.000, 0.355),
]
TURRET_SURFACE = {"TurretCone"}
GUN_TRUNNION_LOCAL = (0.0, 0.190, -0.500)
MUZZLE_LOCAL_Z = -1.420

HULL_SURFACE = {"HullBody"}


def materials(m):
    return {
        "paint": m.material("BTR70_Paint", "5a5d3d", metal=0.14, rough=0.74),
        "rubber": m.material("BTR70_Rubber", "252522", metal=0.02, rough=0.92),
        "steel": m.material("BTR70_DarkSteel", "33362e", metal=0.48, rough=0.52),
        "glass": m.material("BTR70_Optics", "1d2a30", metal=0.25, rough=0.14),
        "cavity": m.material("BTR70_Cavity", "121310", metal=0.05, rough=0.98),
        "marker_red": m.material("BTR70_TailRed", "96261c", metal=0.10, rough=0.42),
        "marker_amber": m.material("BTR70_Amber", "c46c1c", metal=0.10, rough=0.42),
    }


def upper_hull_section(w_chine, y_chine, w_roof, y_roof, crown_inset=0.065, apron_drop=0.085):
    """Upper hull cross-section above the wheel-arch lip, counterclockwise in (x, y)."""
    w_crown = max(0.08, w_roof - crown_inset)
    right = [
        (w_chine - 0.025, y_chine - apron_drop),
        (w_chine, y_chine),
        (w_roof, y_roof - 0.018),
        (w_crown, y_roof),
    ]
    left = [(-x, y) for x, y in reversed(right)]
    return right + left


def planar_roof_w(y_roof, w_chine=1.390):
    """Width at y_roof - 0.018 that keeps the upper hull side plate strictly planar."""
    slope = (1.390 - 1.050) / ((ROOF_Y - 0.018) - CHINE_Y)
    return w_chine - slope * ((y_roof - 0.018) - CHINE_Y)


def lower_tub_section(w_belly, y_belly, w_cheek, y_cheek, w_top, y_top):
    """6-point faceted lower boat-hull cross-section below the chine, CCW in (x, y)."""
    right = [
        (w_belly, y_belly),
        (w_cheek, y_cheek),
        (w_top, y_top),
    ]
    left = [(-x, y) for x, y in reversed(right)]
    return right + left


def add_framed_plate(m, name, center, axes, half, paint, cavity, steel, parent, tint=0.90, lod=0, seam=0.014):
    """Armored hatch/panel with a recessed perimeter seam."""
    if lod < 2:
        seam_c = tuple(center[k] - axes[0][k] * (half[0] * 0.45) for k in range(3))
        obox(m, name + "Seam", seam_c, axes, (half[0] * 0.55, half[1] + seam, half[2] + seam), cavity, parent)
    obox(m, name, center, axes, half, paint, parent, tint=tint)


# ----------------------------------------------------------------------------
# Hull
# ----------------------------------------------------------------------------


def build_hull(m, hull, mats, lod):
    paint, steel, cavity = mats["paint"], mats["steel"], mats["cavity"]

    # 1. Upper boat hull above the wheel-arch apron: single continuous front glacis into
    #    a flat horizontal roofline (no central hump). Width over chine is 2.780 m, rub rail brings it to 2.800 m.
    upper_stations = [
        (HULL_FRONT_Z, upper_hull_section(1.060, NOSE_TIP_Y, 1.000, NOSE_TIP_Y + 0.025, 0.050, 0.025)),
        (GLACIS_START_Z, upper_hull_section(1.330, CHINE_Y - 0.040, 1.230, GLACIS_START_Y, 0.060, 0.045)),
        (-2.840, upper_hull_section(1.390, CHINE_Y, planar_roof_w(1.480), 1.480, 0.065, 0.085)),
        (CAB_ROOF_Z, upper_hull_section(1.390, CHINE_Y, planar_roof_w(ROOF_Y), ROOF_Y, 0.065, 0.085)),
        (FIGHTING_REAR_Z, upper_hull_section(1.390, CHINE_Y, planar_roof_w(ROOF_Y), ROOF_Y, 0.065, 0.085)),
        (FIGHTING_REAR_Z + 0.140, upper_hull_section(1.390, CHINE_Y, planar_roof_w(REAR_ROOF_Y), REAR_ROOF_Y, 0.065, 0.085)),
        (ENGINE_SLOPE_Z, upper_hull_section(1.390, CHINE_Y, planar_roof_w(REAR_ROOF_Y - 0.020), REAR_ROOF_Y - 0.020, 0.065, 0.085)),
        (HULL_REAR_Z, upper_hull_section(1.330, CHINE_Y - 0.020, 1.020, STERN_TOP_Y, 0.060, 0.050)),
    ]
    loft_sections(m, "HullBody", upper_stations, paint, hull, tint=0.96)

    # 2. Lower hull boat tub with wheel-arch cutouts calibrated to the 1.12 m tires:
    #    - 6-point faceted bow cheeks ahead of Wheel 1
    #    - Inter-wheel fender divider between Wheel 1 and Wheel 2 (Z = -1.33)
    #    - Central trapezoidal lower troop door bay between Wheel 2 and Wheel 3 (Z = +0.28)
    #    - Inter-wheel fender divider between Wheel 3 and Wheel 4 (Z = +1.76)
    #    - Rear wheel-arch cutout behind Wheel 4 into the stern tub
    lower_stations = [
        (HULL_FRONT_Z, lower_tub_section(0.760, NOSE_TIP_Y - 0.030, 0.980, NOSE_TIP_Y - 0.010, 1.050, NOSE_TIP_Y)),
        (GLACIS_START_Z, lower_tub_section(0.780, 0.920, 1.200, 0.940, 1.330, CHINE_Y - 0.060)),
        (-2.840, lower_tub_section(0.800, BELLY_Y + 0.040, 1.240, 0.620, 1.370, ARCH_LIP_Y)),
        # Front wheel-arch upward cut ahead of Axle 1 (Z = -2.020)
        (-2.500, lower_tub_section(0.820, BELLY_Y, 0.920, 0.800, 0.960, ARCH_LIP_Y)),
        (-1.460, lower_tub_section(0.820, BELLY_Y, 0.920, 0.800, 0.960, ARCH_LIP_Y)),
        # Inter-wheel fender divider between Wheel 1 (-2.020) and Wheel 2 (-0.635)
        (-1.380, lower_tub_section(0.840, BELLY_Y, 1.260, 0.840, 1.360, ARCH_LIP_Y)),
        (-1.270, lower_tub_section(0.840, BELLY_Y, 1.260, 0.840, 1.360, ARCH_LIP_Y)),
        (-1.190, lower_tub_section(0.820, BELLY_Y, 0.920, 0.800, 0.960, ARCH_LIP_Y)),
        (-0.040, lower_tub_section(0.820, BELLY_Y, 0.920, 0.800, 0.960, ARCH_LIP_Y)),
        # Central trapezoidal lower troop door bay between Wheel 2 (-0.635) and Wheel 3 (+1.080)
        (0.080, lower_tub_section(0.860, BELLY_Y, 1.240, 0.720, 1.360, ARCH_LIP_Y)),
        (0.460, lower_tub_section(0.860, BELLY_Y, 1.240, 0.720, 1.360, ARCH_LIP_Y)),
        (0.580, lower_tub_section(0.820, BELLY_Y, 0.920, 0.800, 0.960, ARCH_LIP_Y)),
        (1.630, lower_tub_section(0.820, BELLY_Y, 0.920, 0.800, 0.960, ARCH_LIP_Y)),
        # Inter-wheel fender divider between Wheel 3 (+1.080) and Wheel 4 (+2.450)
        (1.710, lower_tub_section(0.840, BELLY_Y, 1.260, 0.840, 1.360, ARCH_LIP_Y)),
        (1.820, lower_tub_section(0.840, BELLY_Y, 1.260, 0.840, 1.360, ARCH_LIP_Y)),
        (1.900, lower_tub_section(0.820, BELLY_Y, 0.920, 0.800, 0.960, ARCH_LIP_Y)),
        (2.960, lower_tub_section(0.820, BELLY_Y, 0.920, 0.800, 0.960, ARCH_LIP_Y)),
        # Rear wheel-arch cut behind Wheel 4 (+2.450)
        (3.220, lower_tub_section(0.840, BELLY_Y + 0.040, 1.240, 0.680, 1.370, ARCH_LIP_Y)),
        (ENGINE_SLOPE_Z, lower_tub_section(0.820, BELLY_Y + 0.080, 1.220, 0.740, 1.360, ARCH_LIP_Y)),
        (HULL_REAR_Z, lower_tub_section(0.780, 0.620, 1.160, 0.860, 1.320, CHINE_Y - 0.020)),
    ]
    loft_sections(m, "HullLowerTub", lower_stations, paint, hull, tint=0.92)

    # Front nose trim vane plate folded flush on the bow lip
    glacis_slope = math.atan2(ROOF_Y - GLACIS_START_Y, CAB_ROOF_Z - GLACIS_START_Z)
    glacis_along = (0.0, math.sin(glacis_slope), math.cos(glacis_slope))
    glacis_normal = (0.0, math.cos(glacis_slope), -math.sin(glacis_slope))
    obox(m, "TrimVane", (0.0, 1.205, -3.520), ((1.0, 0.0, 0.0), (0.0, 0.98, -0.20), (0.0, 0.20, 0.98)), (0.86, 0.015, 0.12), paint, hull, tint=0.92)
    if lod == 0:
        for hx in (-0.64, -0.22, 0.22, 0.64):
            m.add_box("VaneHinge", (0.045, 0.032, 0.065), (hx, 1.220, -3.400), steel, hull)

    # Flush armored windshield shutters with recessed shadow seams
    for sign in (-1, 1):
        wx = sign * 0.44
        wz = -2.76
        wy = GLACIS_START_Y + (wz - GLACIS_START_Z) * (ROOF_Y - GLACIS_START_Y) / (CAB_ROOF_Z - GLACIS_START_Z)
        axes_g = ((1.0, 0.0, 0.0), glacis_normal, glacis_along)
        seam_c = (wx, wy + glacis_normal[1] * 0.010, wz + glacis_normal[2] * 0.010)
        if lod < 2:
            obox(m, "WindshieldSeam", seam_c, axes_g, (0.325, 0.010, 0.265), cavity, hull)
        base_c = (wx, wy + glacis_normal[1] * 0.020, wz + glacis_normal[2] * 0.020)
        obox(m, "WindshieldCoaming", base_c, axes_g, (0.310, 0.016, 0.250), paint, hull, tint=0.84)
        hatch_c = (wx, wy + glacis_normal[1] * 0.036, wz + glacis_normal[2] * 0.036)
        obox(m, "WindshieldShutter", hatch_c, axes_g, (0.285, 0.014, 0.225), paint, hull, tint=0.95)
        if lod == 0:
            for rz in (-0.075, 0.075):
                rc = (
                    wx,
                    hatch_c[1] + glacis_normal[1] * 0.012 + glacis_along[1] * rz,
                    hatch_c[2] + glacis_normal[2] * 0.012 + glacis_along[2] * rz,
                )
                obox(m, "ShutterRib", rc, axes_g, (0.24, 0.008, 0.018), paint, hull, tint=1.04)
            for hx_off in (-0.18, 0.18):
                hc = (
                    wx + hx_off,
                    hatch_c[1] + glacis_normal[1] * 0.012 + glacis_along[1] * 0.21,
                    hatch_c[2] + glacis_normal[2] * 0.012 + glacis_along[2] * 0.21,
                )
                obox(m, "ShutterHinge", hc, axes_g, (0.028, 0.012, 0.035), steel, hull)
        # Twin armored periscope hoods right above windshields at the front roofline
        px, py, pz = sign * 0.44, ROOF_Y + 0.038, CAB_ROOF_Z + 0.05
        if lod < 2:
            m.add_box("PeriscopeSeam", (0.50, 0.020, 0.20), (px, ROOF_Y + 0.006, pz), cavity, hull)
        m.add_box("PeriscopeHood", (0.48, 0.076, 0.18), (px, py, pz), paint, hull, tint=0.89)
        if lod == 0:
            for ox in (-0.15, 0.0, 0.15):
                m.add_box("PeriscopeBezel", (0.125, 0.048, 0.024), (px + ox, py + 0.004, pz - 0.084), steel, hull)
                m.add_box("PeriscopeGlass", (0.105, 0.034, 0.026), (px + ox, py + 0.004, pz - 0.088), mats["glass"], hull)
        # Commander sight hood on the right front roof corner
        if sign < 0 and lod < 2:
            m.add_box("CommanderSightHood", (0.14, 0.11, 0.16), (sign * 0.78, ROOF_Y + 0.055, CAB_ROOF_Z + 0.02), paint, hull, tint=0.88)
            m.add_box("CommanderSightGlass", (0.09, 0.05, 0.02), (sign * 0.78, ROOF_Y + 0.065, CAB_ROOF_Z - 0.058), mats["glass"], hull)

    def side_frame(sign, y, z, surface=HULL_SURFACE):
        hit = surface_hit(m, hull, (sign * 2.0, y, z), (-float(sign), 0.0, 0.0), surface)
        if hit is None:
            raise RuntimeError(f"side_frame missed {surface} at y={y}, z={z}, sign={sign}")
        pt, out = hit
        fwd = (0.0, 0.0, 1.0)
        up_wall = (
            out[1] * fwd[2] - out[2] * fwd[1],
            out[2] * fwd[0] - out[0] * fwd[2],
            out[0] * fwd[1] - out[1] * fwd[0],
        )
        u_len = math.hypot(*up_wall) or 1.0
        up_wall = tuple(v / u_len for v in up_wall)
        if up_wall[1] < 0:
            up_wall = tuple(-v for v in up_wall)
        right_z = (
            up_wall[1] * out[2] - up_wall[2] * out[1],
            up_wall[2] * out[0] - up_wall[0] * out[2],
            up_wall[0] * out[1] - up_wall[1] * out[0],
        )
        return pt, (out, up_wall, right_z)

    for sign in (-1, 1):
        # Upper chine rub rail (brings hull width to exactly 2.800 m) + lower wheel-arch lip molding
        m.add_box("ChineRubRail", (0.020, 0.030, 6.70), (sign * 1.390, CHINE_Y, 0.05), steel, hull)
        m.add_box("WheelArchLip", (0.020, 0.025, 5.60), (sign * 1.370, ARCH_LIP_Y, 0.22), paint, hull, tint=0.80)

        # Braided steel tow cable clamped along the chine ledge
        if lod < 2:
            m.add_cylinder("ChineTowCable", 0.020, 4.60, (sign * 1.350, CHINE_Y + 0.035, 0.02), steel, "z", 6, parent=hull)
            for ez in (-2.30, 2.34):
                m.add_cylinder("TowCableEye", 0.045, 0.025, (sign * 1.350, CHINE_Y + 0.040, ez), steel, "x", 6, parent=hull)
            if lod == 0:
                for cz in (-1.90, -0.85, 0.05, 1.05, 2.00):
                    m.add_box("TowCableClamp", (0.036, 0.050, 0.032), (sign * 1.355, CHINE_Y + 0.030, cz), steel, hull)

        # Wheel well backing and suspension inside each wheel arch
        for z in WHEEL_Z:
            m.add_box("WheelWellShadow", (0.04, 0.58, 1.00), (sign * 0.980, 0.82, z), cavity, hull)
            if lod < 2:
                rod(m, "LowerWishbone", (sign * 0.92, 0.52, z - 0.06), (sign * 1.14, WHEEL_Y - 0.04, z), 0.032, steel, hull, sides=4)
                rod(m, "UpperWishbone", (sign * 0.92, 0.74, z + 0.05), (sign * 1.13, WHEEL_Y + 0.08, z), 0.026, steel, hull, sides=4)
                rod(m, "ShockAbsorber", (sign * 0.95, 1.04, z - 0.14), (sign * 1.07, 0.58, z - 0.02), 0.026, steel, hull, sides=4)

        # Welded V-struts in the inter-wheel gaps and boarding steps
        if lod < 2:
            for gap_z in (-1.325, 1.765):
                rod(m, "FenderVStrut", (sign * 1.33, 0.85, gap_z), (sign * 1.35, ARCH_LIP_Y, gap_z - 0.13), 0.015, paint, hull, sides=4)
                rod(m, "FenderVStrut", (sign * 1.33, 0.85, gap_z), (sign * 1.35, ARCH_LIP_Y, gap_z + 0.13), 0.015, paint, hull, sides=4)
            # Central troop boarding stirrup step below the lower side door (Z = DOOR_CENTER_Z)
            rod(m, "DoorStepSide", (sign * 1.23, 0.74, DOOR_CENTER_Z - 0.15), (sign * 1.26, 0.63, DOOR_CENTER_Z - 0.15), 0.012, steel, hull, sides=4)
            rod(m, "DoorStepSide", (sign * 1.23, 0.74, DOOR_CENTER_Z + 0.15), (sign * 1.26, 0.63, DOOR_CENTER_Z + 0.15), 0.012, steel, hull, sides=4)
            rod(m, "DoorStepRung", (sign * 1.26, 0.63, DOOR_CENTER_Z - 0.15), (sign * 1.26, 0.63, DOOR_CENTER_Z + 0.15), 0.012, steel, hull, sides=4)
            # Rear hull boarding stirrup step behind Axle 4
            rod(m, "RearStepRung", (sign * 1.24, 0.64, 3.36), (sign * 1.24, 0.64, 3.58), 0.012, steel, hull, sides=4)

        # Forward crew side hatch directly above Wheel 1 (Z = -2.04 m)
        ch_pt, ch_axes = side_frame(sign, 1.48, -2.04)
        add_framed_plate(
            m, "ForwardSideHatch",
            tuple(ch_pt[k] + ch_axes[0][k] * 0.014 for k in range(3)),
            ch_axes, (0.016, 0.145, 0.150), paint, cavity, steel, hull, tint=0.90, lod=lod
        )

        # Central two-piece side troop door centered at DOOR_CENTER_Z = +0.28 m
        low_pt, low_axes = side_frame(sign, 0.99, DOOR_CENTER_Z, {"HullLowerTub"})
        add_framed_plate(
            m, "LowerSideDoor",
            tuple(low_pt[k] + low_axes[0][k] * 0.014 for k in range(3)),
            low_axes, (0.018, 0.20, 0.17), paint, cavity, steel, hull, tint=0.84, lod=lod
        )
        up_pt, up_axes = side_frame(sign, 1.48, DOOR_CENTER_Z)
        add_framed_plate(
            m, "UpperSideHatch",
            tuple(up_pt[k] + up_axes[0][k] * 0.014 for k in range(3)),
            up_axes, (0.016, 0.190, 0.230), paint, cavity, steel, hull, tint=0.89, lod=lod
        )

        # Teardrop swivel firing ports and rear vertical armor ribs
        if lod < 2:
            for fz in (-1.50, 0.82, 1.82):
                fp_pt, fp_axes = side_frame(sign, 1.48, fz)
                obox(m, "FiringPortBoss", tuple(fp_pt[k] + fp_axes[0][k] * 0.014 for k in range(3)), fp_axes, (0.016, 0.055, 0.075), paint, hull, tint=0.84)
                obox(m, "FiringPortSlot", tuple(fp_pt[k] + fp_axes[0][k] * 0.024 for k in range(3)), fp_axes, (0.008, 0.020, 0.034), cavity, hull)
            for rz in (1.51, 2.09):
                rb_pt, rb_axes = side_frame(sign, 1.46, rz)
                obox(m, "SideArmorRib", tuple(rb_pt[k] + rb_axes[0][k] * 0.015 for k in range(3)), rb_axes, (0.016, 0.17, 0.045), paint, hull, tint=0.84)
            # Stamped engine access hatch on rear side wall (Z = 2.40 m)
            ep_pt, ep_axes = side_frame(sign, 1.46, 2.40)
            add_framed_plate(
                m, "RearSideAccessHatch",
                tuple(ep_pt[k] + ep_axes[0][k] * 0.012 for k in range(3)),
                ep_axes, (0.014, 0.13, 0.12), paint, cavity, steel, hull, tint=0.88, lod=lod
            )
            # Rear side louver box at Z = 2.86 m
            lv_pt, lv_axes = side_frame(sign, 1.45, 2.86)
            add_framed_plate(
                m, "SideLouverBox",
                tuple(lv_pt[k] + lv_axes[0][k] * 0.018 for k in range(3)),
                lv_axes, (0.022, 0.130, 0.260), paint, cavity, steel, hull, tint=0.82, lod=lod
            )

        # Front cheek headlight shelf + lights
        hx, hy, hz = sign * 1.18, 1.37, -3.06
        m.add_box("HeadlightBracket", (0.24, 0.026, 0.20), (hx, hy - 0.075, hz), paint, hull, tint=0.88)
        m.add_cylinder("HeadlightHousing", 0.072, 0.12, (hx - sign * 0.04, hy, hz), steel, "z", 8 if lod < 2 else 6, parent=hull)
        if lod < 2:
            m.add_disc("HeadlightLens", 0.060, (hx - sign * 0.04, hy, hz - 0.062), mats["glass"], "z", 8, hull, -1.0)
            m.add_cylinder("TurnIndicator", 0.032, 0.05, (hx + sign * 0.10, hy - 0.01, hz - 0.01), mats["marker_amber"], "z", 6, parent=hull)
            m.add_cylinder("IrHeadlight", 0.064, 0.10, (hx - sign * 0.16, hy + 0.01, hz + 0.02), steel, "z", 8, parent=hull)
            m.add_disc("IrHeadlightLens", 0.052, (hx - sign * 0.16, hy + 0.01, hz - 0.032), cavity, "z", 8, hull, -1.0)
        if lod == 0:
            c_left = hx - sign * 0.22
            c_right = hx + sign * 0.14
            for gx in (c_left, c_right):
                rod(m, "HeadlightGuard", (gx, hy - 0.075, hz + 0.07), (gx, hy + 0.090, hz + 0.07), 0.008, steel, hull, sides=4)
                rod(m, "HeadlightGuard", (gx, hy - 0.075, hz - 0.075), (gx, hy + 0.090, hz - 0.075), 0.008, steel, hull, sides=4)
                rod(m, "HeadlightGuard", (gx, hy + 0.090, hz + 0.07), (gx, hy + 0.090, hz - 0.075), 0.008, steel, hull, sides=4)
            rod(m, "HeadlightGuardBar", (c_left, hy + 0.090, hz - 0.075), (c_right, hy + 0.090, hz - 0.075), 0.008, steel, hull, sides=4)
            # Rearview mirror on stalk
            rod(m, "MirrorStalk", (sign * 1.24, 1.48, -2.44), (sign * 1.32, 1.68, -2.44), 0.008, steel, hull, sides=4)
            m.add_box("MirrorHead", (0.035, 0.13, 0.080), (sign * 1.32, 1.72, -2.44), steel, hull)

        # Heavy forged bow tow hooks and shackles (frontmost point reaches Z = -3.770 m)
        m.add_box("BowTowMount", (0.09, 0.10, 0.06), (sign * 0.68, 1.04, HULL_FRONT_Z + 0.03), steel, hull)
        m.add_cylinder("BowShackle", 0.055, 0.032, (sign * 0.68, 0.98, -3.715), steel, "x", 8 if lod < 2 else 6, parent=hull)

    # Low turret base plinth on the forward roof
    m.add_cylinder("TurretPlinth", 0.70, 0.036, (0.0, ROOF_Y + 0.012, TURRET_RING[2]), steel, "y", (20, 14, 8)[lod], parent=hull)

    # Whip antenna mounted on left upper hull shoulder
    if lod < 2:
        m.add_cylinder("AntennaBase", 0.044, 0.09, (-0.98, ROOF_Y + 0.02, 0.87), steel, "y", 6, parent=hull)
        if lod == 0:
            rod(m, "Antenna", (-0.98, ROOF_Y + 0.06, 0.87), (-0.98, ROOF_Y + 1.82, 0.85), 0.007, steel, hull, sides=4)

    # Troop roof hatches behind the turret
    for sign in (-1, 1):
        if lod < 2:
            m.add_box("TroopHatchSeam", (0.45, 0.016, 0.84), (sign * 0.42, ROOF_Y + 0.002, 0.28), cavity, hull)
        m.add_box("TroopRoofHatch", (0.42, 0.028, 0.80), (sign * 0.42, ROOF_Y + 0.010, 0.28), paint, hull, tint=0.92)
        if lod == 0:
            for rx in (-0.10, 0.10):
                m.add_box("HatchRib", (0.042, 0.010, 0.68), (sign * 0.42 + rx, ROOF_Y + 0.022, 0.28), paint, hull, tint=0.98)

    # Clean rear engine deck (MTO): longitudinal cooling grilles and louvers only (no stowage boxes or tarps)
    for sign in (-1, 1):
        m.add_box("EngineGrille", (0.48, 0.020, 0.70), (sign * 0.38, REAR_ROOF_Y + 0.004, 1.88), cavity, hull)
        if lod == 0:
            for k in range(5):
                m.add_box("EngineLouver", (0.44, 0.018, 0.036), (sign * 0.38, REAR_ROOF_Y + 0.012, 1.60 + k * 0.14), paint, hull, tint=0.86)
        elif lod == 1:
            m.add_box("EngineLouverMid", (0.44, 0.014, 0.66), (sign * 0.38, REAR_ROOF_Y + 0.010, 1.88), paint, hull, tint=0.86)

    # Central engine inspection deck hatch
    m.add_box("EngineCenterHatch", (0.40, 0.016, 0.65), (0.0, REAR_ROOF_Y + 0.008, 2.70), paint, hull, tint=0.90)

    # Stern transom: waterjet outlet, tow shackles, and taillights (rearmost point reaches Z = +3.770 m)
    if lod < 2:
        m.add_box("WaterjetFrame", (0.88, 0.58, 0.025), (0.0, 0.98, HULL_REAR_Z - 0.010), cavity, hull)
    m.add_box("WaterjetCover", (0.84, 0.54, 0.040), (0.0, 0.98, HULL_REAR_Z - 0.020), paint, hull, tint=0.86)
    for sign in (-1, 1):
        m.add_box("SternTowMount", (0.09, 0.10, 0.06), (sign * 0.72, 0.88, HULL_REAR_Z - 0.03), steel, hull)
        m.add_cylinder("SternShackle", 0.055, 0.032, (sign * 0.72, 0.82, 3.715), steel, "x", 8 if lod < 2 else 6, parent=hull)
        tx, ty, tz = sign * 0.88, 1.38, HULL_REAR_Z - 0.03
        m.add_box("TailLightBox", (0.24, 0.12, 0.05), (tx, ty, tz), steel, hull)
        if lod < 2:
            m.add_cylinder("TailLampRed", 0.038, 0.025, (tx - sign * 0.05, ty, tz + 0.020), mats["marker_red"], "z", 6, parent=hull)
            m.add_cylinder("TailLampAmber", 0.038, 0.025, (tx + sign * 0.05, ty, tz + 0.020), mats["marker_amber"], "z", 6, parent=hull)


# ----------------------------------------------------------------------------
# Running gear (Toroidal KI-80 13.00-18 chevron tires + deep CTIS hubs)
# ----------------------------------------------------------------------------


def road_wheel(m, sign, z, sides, mats, parent, lod):
    x = sign * WHEEL_X_CENTER
    y, r = WHEEL_Y, WHEEL_R
    hw = TIRE_WIDTH * 0.5
    rim_r = r * 0.52

    if lod == 2:
        # LOD2: lightweight cylinder tire and flat wheel face
        m.add_cylinder("RoadWheelTire", r, TIRE_WIDTH, (x, y, z), mats["rubber"], "x", sides, parent=parent, caps="none")
        face_x = x + sign * hw
        m.add_disc("RoadWheelTireWall", r, (face_x, y, z), mats["rubber"], "x", sides, parent, float(sign))
        m.add_disc("RoadWheelDish", rim_r, (face_x + sign * 0.005, y, z), mats["paint"], "x", sides, parent, float(sign))
        m.add_cylinder("RoadWheelHub", r * 0.22, 0.04, (face_x + sign * 0.020, y, z), mats["steel"], "x", sides, parent=parent, caps="pos" if sign > 0 else "neg")
        return

    # Toroidal KI-80 tire carcass profile
    profile = [
        (-hw * 0.75, rim_r),
        (-hw * 0.96, r * 0.78),
        (-hw * 0.85, r * 0.98),
        (hw * 0.85, r * 0.98),
        (hw * 0.96, r * 0.78),
        (hw * 0.75, rim_r),
    ]
    pts, nrms, idx = [], [], []
    rcount = len(profile)
    for k, (ax, rad) in enumerate(profile):
        prev_p = profile[max(0, k - 1)]
        next_p = profile[min(rcount - 1, k + 1)]
        dax = (next_p[0] - prev_p[0]) * sign
        drad = next_p[1] - prev_p[1]
        nx_raw = -drad * sign
        nr_raw = abs(dax)
        nlen = math.hypot(nx_raw, nr_raw) or 1.0
        nx_val, nr_val = nx_raw / nlen, nr_raw / nlen
        px_ring = x + sign * ax
        for i in range(sides):
            a = i * math.tau / sides
            sa, ca = math.sin(a), math.cos(a)
            pts.append((px_ring, y + rad * sa, z + rad * ca))
            nrms.append((nx_val, nr_val * sa, nr_val * ca))
    for k in range(rcount - 1):
        for i in range(sides):
            j = (i + 1) % sides
            a, b, c, d = k * sides + i, k * sides + j, (k + 1) * sides + j, (k + 1) * sides + i
            if sign > 0:
                idx.extend([a, c, b, a, d, c])
            else:
                idx.extend([a, b, c, a, c, d])
    m.add_mesh_direct("TireCarcass", pts, nrms, idx, mats["rubber"], parent, tint=0.92)

    # Recessed steel wheel dish with rim shadow ring, bead lock, and planetary hub
    rim_x = x + sign * (hw * 0.50)
    m.add_disc("WheelRimShadow", rim_r * 1.02, (rim_x, y, z), mats["cavity"], "x", sides, parent, float(sign))
    m.add_disc("WheelRimDish", rim_r * 0.90, (rim_x + sign * 0.010, y, z), mats["paint"], "x", sides, parent, float(sign))
    m.add_cylinder("WheelRimLip", rim_r * 0.96, 0.030, (rim_x + sign * 0.016, y, z), mats["steel"], "x", sides, parent=parent, caps="none")
    m.add_cylinder("WheelBeadRing", rim_r * 0.74, 0.024, (rim_x + sign * 0.020, y, z), mats["paint"], "x", max(8, sides // 2), parent=parent, caps="pos" if sign > 0 else "neg")
    m.add_cylinder("WheelHubDrum", r * 0.28, 0.048, (rim_x + sign * 0.034, y, z), mats["paint"], "x", max(8, sides // 2), parent=parent, caps="pos" if sign > 0 else "neg")
    m.add_cylinder("WheelHubCenter", r * 0.16, 0.070, (rim_x + sign * 0.052, y, z), mats["steel"], "x", max(6, sides // 2), parent=parent, caps="pos" if sign > 0 else "neg")
    m.add_cylinder("CtisCap", r * 0.090, 0.030, (rim_x + sign * 0.075, y, z), mats["paint"], "x", 6, parent=parent, caps="pos" if sign > 0 else "neg")

    if lod == 0:
        lug_pairs = 12
        for i in range(lug_pairs):
            a = i * math.tau / lug_pairs
            radial = (0.0, math.sin(a), math.cos(a))
            tangential = (0.0, math.cos(a), -math.sin(a))
            for half_side in (-1, 1):
                ca_ang, sa_ang = math.cos(0.40 * half_side), math.sin(0.40 * half_side)
                axis_w = (ca_ang, tangential[1] * sa_ang, tangential[2] * sa_ang)
                axis_t = (-sa_ang, tangential[1] * ca_ang, tangential[2] * ca_ang)
                lug_x = x + half_side * (TIRE_WIDTH * 0.18)
                center = (lug_x, y + radial[1] * (r - 0.010), z + radial[2] * (r - 0.010))
                obox(m, "TireLug", center, (axis_w, radial, axis_t), (TIRE_WIDTH * 0.16, 0.010, 0.020), mats["rubber"], parent, tint=0.82)
        for i in range(6):
            a = i * math.tau / 6
            m.add_disc("RimBolt", 0.012, (rim_x + sign * 0.022, y + rim_r * 0.74 * math.sin(a), z + rim_r * 0.74 * math.cos(a)), mats["steel"], "x", 4, parent, float(sign))
            radial = (0.0, math.sin(a), math.cos(a))
            tangential = (0.0, math.cos(a), -math.sin(a))
            obox(m, "RimRib", (rim_x + sign * 0.054, y + radial[1] * (r * 0.20), z + radial[2] * (r * 0.20)), ((1.0, 0.0, 0.0), radial, tangential), (0.012, r * 0.055, 0.016), mats["steel"], parent)


def build_running_gear(m, side, sign, mats, lod):
    sides = (16, 10, 8)[lod]
    for z in WHEEL_Z:
        first = len(m.parts)
        road_wheel(m, sign, z, sides, mats, side, lod)
        tag_wheel(m, first, (WHEEL_Y, z), WHEEL_R)


# ----------------------------------------------------------------------------
# BPU-1 Turret
# ----------------------------------------------------------------------------


def build_turret(m, turret, mats, lod):
    paint, steel, cavity = mats["paint"], mats["steel"], mats["cavity"]
    sides = (18, 12, 8)[lod]
    profile = TURRET_PROFILE if lod < 2 else [TURRET_PROFILE[i] for i in (0, 2, 4, 6, 7)]
    revolve(m, "TurretCone", profile, sides, paint, turret, scale_x=0.96, tint=0.98)

    # Front gun slot housing on the BPU-1 cone
    m.add_box("MantletHousing", (0.34, 0.21, 0.32), (0.0, 0.185, -0.45), paint, turret, tint=0.90)
    if lod < 2:
        m.add_box("MantletCavity", (0.28, 0.17, 0.04), (0.0, 0.185, -0.60), cavity, turret)
        # OU-3GA2 infrared searchlight on top of turret roof (reaches 2.235 m)
        sx, sy, sz = -0.05, TURRET_HEIGHT + 0.050, -0.08
        m.add_box("TurretLightBracket", (0.08, 0.06, 0.07), (sx, TURRET_HEIGHT + 0.020, sz), steel, turret)
        m.add_cylinder("TurretSearchlight", 0.085, 0.13, (sx, sy, sz), paint, "z", 8 if lod == 0 else 6, parent=turret)
        m.add_cylinder("SearchlightRim", 0.090, 0.028, (sx, sy, sz - 0.055), steel, "z", 8 if lod == 0 else 6, parent=turret)
        m.add_disc("SearchlightLens", 0.076, (sx, sy, sz - 0.068), mats["glass"], "z", 8 if lod == 0 else 6, turret, -1.0)
        # Sight hood and hatch
        m.add_box("TurretSightHood", (0.08, 0.06, 0.10), (0.18, TURRET_HEIGHT + 0.022, -0.16), steel, turret)
        low, _ = surface_heights(m, turret, (0.04, 0.10), 0.18, TURRET_SURFACE)
        domed_hatch(m, "TurretHatch", turret, (0.04, low - 0.005, 0.10), 0.18, paint, (10, 8, 6)[lod], rise=0.020, hinge_mat=steel, hinge_dir=(0.0, 0.0, 1.0))


def build_gun(m, pitch, recoil, mats, lod):
    paint, steel = mats["paint"], mats["steel"]
    sides = (10, 8, 6)[lod]
    m.add_cylinder("MantletDrum", 0.105, 0.25, (0.0, 0.0, 0.04), steel, "x", sides, parent=pitch)
    if lod < 2:
        bx, by, bz = -0.22, 0.03, -0.06
        m.add_box("ExternalAmmoBox", (0.16, 0.18, 0.32), (bx, by, bz), paint, pitch, tint=0.88)
        if lod == 0:
            for sz in (-0.18, 0.06):
                m.add_box("AmmoBoxStrap", (0.175, 0.195, 0.022), (bx, by, sz), steel, pitch)
            rod(m, "AmmoCageBar", (bx - 0.09, by + 0.10, bz - 0.18), (bx - 0.09, by + 0.10, bz + 0.18), 0.007, steel, pitch, sides=4)
            rod(m, "AmmoCageBar", (bx - 0.09, by - 0.10, bz - 0.18), (bx - 0.09, by - 0.10, bz + 0.18), 0.007, steel, pitch, sides=4)
            rod(m, "AmmoCageBar", (bx - 0.09, by - 0.10, bz - 0.18), (bx - 0.09, by + 0.10, bz - 0.18), 0.007, steel, pitch, sides=4)
        tube(m, "CoaxBarrel", [(0.0, 0.018), (-0.46, 0.015)], max(4, sides // 2), steel, pitch, pos=(0.14, -0.01, 0.0))

    rings = [
        (0.05, 0.048),
        (-0.62, 0.046),
        (-0.64, 0.024),
        (MUZZLE_LOCAL_Z + 0.15, 0.022),
        (MUZZLE_LOCAL_Z + 0.13, 0.034),
        (MUZZLE_LOCAL_Z, 0.040),
    ]
    tube(m, "KpvtBarrel", rings, sides, steel, recoil, tint=0.88)
    m.add_disc("MuzzleBore", 0.024, (0.0, 0.0, MUZZLE_LOCAL_Z - 0.002), mats["cavity"], "z", sides, recoil, -1.0)
    if lod == 0:
        for z_ring in (-0.14, -0.36, -0.58):
            m.add_cylinder("JacketCollar", 0.054, 0.026, (0.0, 0.0, z_ring), paint, "z", sides, parent=recoil)
        for z_slot in (-0.25, -0.47):
            m.add_cylinder("JacketCoolingSlots", 0.048, 0.07, (0.0, 0.0, z_slot), mats["cavity"], "z", sides, parent=recoil)


# ----------------------------------------------------------------------------
# Markings (Red Star at Z = -0.74 m + "312" at Z = -0.12 m)
# ----------------------------------------------------------------------------


def build_markings(m, hull, lod=0):
    """Soviet red star with white outline and 312 tactical number."""
    atlas = MarkingAtlas(512, 256)
    atlas.star((0.0, 0.0, 0.5, 1.0), fill=(178, 34, 30, 255), outline=(232, 228, 214, 255), outline_width=0.07)
    atlas.text((0.5, 0.14, 1.0, 0.86), "312", fill=(232, 228, 214, 255))
    material = decal_material(m, atlas, "BTR70_Markings")

    grid = (6, 4) if lod == 0 else (4, 3)
    for sign in (-1, 1):
        for z, size, region in [
            (-0.74, (0.30, 0.30), (0.0, 0.0, 0.5, 1.0)),
            (-0.12, (0.48, 0.27), (0.5, 0.0, 1.0, 1.0)),
        ]:
            hit = surface_hit(m, hull, (sign * 2.0, 1.48, z), (-float(sign), 0.0, 0.0), HULL_SURFACE)
            if hit is None:
                raise RuntimeError(f"Marking ray missed HullBody at z={z}, sign={sign}")
            center, normal = hit
            add_conformal_decal(m, "Marking", hull, center, normal, (0.0, 1.0, 0.0), size, region, material, HULL_SURFACE, grid=grid)


def bake_edge_wear_and_grime(m):
    """Add metallic edge-wear and crevice contrast into vertex colors.
    Wheel AO and dirt masks are radially symmetric so rotating wheels have no static shadow wedges.
    """
    motion = getattr(m, "motion", {})
    for part_index, part in enumerate(m.parts):
        node_id, parent, mat, points, normals, indices, colors = part
        pts = np.asarray(points, dtype=float)
        nrms = np.asarray(normals, dtype=float)
        cols = np.asarray(colors, dtype=float)

        if node_id in motion and motion[node_id][0] == "wheel":
            _, (cy, cz), r = motion[node_id]
            rad = np.hypot(pts[:, 1] - cy, pts[:, 2] - cz) / max(r, 1e-6)
            radial_ao = np.clip(0.70 + 0.26 * rad, 0.65, 0.96)
            radial_dirt = np.clip(0.30 + 0.35 * (rad ** 1.5), 0.25, 0.70)
            rgb = np.repeat(radial_ao[:, None], 3, axis=1)
            alpha = radial_dirt
        else:
            hx = np.sin(pts[:, 0] * 6.3 + pts[:, 2] * 4.7 + pts[:, 1] * 8.1)
            hy = np.cos(pts[:, 0] * 13.7 - pts[:, 2] * 11.3 + pts[:, 1] * 5.9)
            micro = 1.0 + 0.040 * hx + 0.020 * hy

            edge_boost = np.ones(len(pts))
            near_chine = np.exp(-((pts[:, 1] - CHINE_Y) ** 2) / (0.065 ** 2)) * (np.abs(pts[:, 0]) > 1.25)
            near_roof = np.exp(-((pts[:, 1] - ROOF_Y) ** 2) / (0.055 ** 2)) * (np.abs(pts[:, 0]) > 0.90)
            near_arch = np.exp(-((pts[:, 1] - ARCH_LIP_Y) ** 2) / (0.045 ** 2)) * (np.abs(pts[:, 0]) > 1.25)
            upward = np.clip(nrms[:, 1], 0.0, 1.0)
            edge_boost += 0.10 * (near_chine + near_roof + 0.8 * near_arch) + 0.04 * upward

            rgb = np.clip(cols[:, :3] * (micro * edge_boost)[:, None], 0.18, 1.15)
            mud_splash = np.clip((1.38 - pts[:, 1]) / 1.15, 0.0, 1.0) * (0.18 + 0.14 * np.abs(hx))
            alpha = np.clip(cols[:, 3] + mud_splash, 0.0, 1.0)

        new_colors = [(float(rgb[i, 0]), float(rgb[i, 1]), float(rgb[i, 2]), float(alpha[i])) for i in range(len(pts))]
        m.parts[part_index] = (node_id, parent, mat, points, normals, indices, new_colors)


# ----------------------------------------------------------------------------
# Build & Manifest
# ----------------------------------------------------------------------------


def wheel_wrap_period(wheel_radius, target=1000.0):
    """Exact whole number of wheel circumferences near target meters so wheel rotation wraps seamlessly."""
    circumference = math.tau * wheel_radius
    turns = max(1, round(target / circumference))
    return turns * circumference


def build_btr70_lod(lod=0, output_dir=None):
    m = GltfModel(f"BTR70_LOD{lod}")
    m.data["asset"]["generator"] = "Dead Ground BTR-70 model authoring"
    mats = materials(m)
    hull = m.node("Hull")
    left = m.node("LeftTrack")
    right = m.node("RightTrack")
    turret = m.node("TurretYaw", TURRET_RING)
    pitch = m.node("GunPitch", GUN_TRUNNION_LOCAL, turret)
    recoil = m.node("BarrelRecoil", parent=pitch)
    m.node("Muzzle", (0.0, 0.0, MUZZLE_LOCAL_Z), recoil)

    build_hull(m, hull, mats, lod)
    build_running_gear(m, left, -1, mats, lod)
    build_running_gear(m, right, 1, mats, lod)
    build_turret(m, turret, mats, lod)
    build_gun(m, pitch, recoil, mats, lod)

    bake_vertex_shading(m, gear_nodes=("LeftTrack", "RightTrack"), max_edge=(0.60, 1.10, None)[lod])
    bake_edge_wear_and_grime(m)
    emit_motion_parts(m)
    if lod < 2:
        build_markings(m, hull, lod=lod)

    name = "btr70.glb" if lod == 0 else f"btr70_lod{lod}.glb"
    out_dir = Path(output_dir) if output_dir else DEFAULT_OUTPUT_DIR
    out_dir.mkdir(parents=True, exist_ok=True)
    stats = m.save(out_dir / name)
    stats["lod"] = lod
    stats["role"] = ("close", "rts", "distant")[lod]
    return stats


def generate_all(output_dir=None):
    out_dir = Path(output_dir) if output_dir else DEFAULT_OUTPUT_DIR
    out_dir.mkdir(parents=True, exist_ok=True)
    lods = [build_btr70_lod(lod, out_dir) for lod in range(3)]
    manifest = {
        "asset": "BTR-70 (GAZ-4905) 8x8 APC RTS visual study",
        "configuration": "BTR-70 with exact 8x8 axle stations, wheel-arch V-struts, boarding steps, toroidal 13.00-18 chevron tires, clean engine deck louvres, BPU-1 turret with OU-3GA2 searchlight, and 14.5 mm KPVT gun",
        "units": "meters",
        "up": "+Y",
        "forward": "-Z",
        "color_authoring": "sRGB hex converted to linear glTF factors",
        "vertex_colors": "rgb: tint x baked ambient occlusion x edge wear, a: dirt mask",
        "reference_anchors": {
            "hull_length_meters": HULL_LENGTH,
            "overall_length_meters": OVERALL_LENGTH,
            "width_over_hull_meters": WIDTH_OVER_HULL,
            "height_to_turret_top_meters": HEIGHT_TO_TURRET_TOP,
            "ground_clearance_meters": GROUND_CLEARANCE,
            "wheelbase_meters": WHEEL_Z[-1] - WHEEL_Z[0],
            "track_gauge_meters": TRACK_GAUGE,
            "wheel_radius_meters": WHEEL_R,
            "wheels_per_side": len(WHEEL_Z),
        },
        "lods": lods,
        "track_gauge_meters": TRACK_GAUGE,
        "track_wrap_meters": wheel_wrap_period(WHEEL_R),
    }
    (out_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=None)
    args = parser.parse_args()
    result = generate_all(args.output_dir)
    for item in result["lods"]:
        print(f"{item['file']}: {item['triangles']} triangles, {item['mesh_parts']} parts, {item['bytes']} bytes")
