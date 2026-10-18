"""Procedural BRDM-2 (GAZ-41-08) 4x4 amphibious scout car model for Dead Ground, with three LODs.

Reference:
- Primary drawing: https://drawingdatabase.com/wp-content/uploads/2015/05/brdm-21.gif
- Published technical specifications:
  Wikipedia: https://en.wikipedia.org/wiki/BRDM-2
  FAS Military Analysis Network: https://man.fas.org/dod-101/sys/land/row/brdm-2.htm
  Length 5.75 m, width 2.35 m, height 2.31 m, ground clearance 0.43 m, 4x4 drive with two pairs
  of chain-driven retractable auxiliary belly wheels between the axles for trench crossing
  (modeled retracted), BPU-1 conical turret with 14.5 mm KPVT heavy machine gun and coaxial 7.62 mm PKT.
- Era: 1976 to 1982, Soviet olive green, no modern additions.

Node hierarchy:
BRDM2
|-- Hull (body, belly tub, retracted belly wheels, hatches, engine louvres, mufflers, waterjet)
|-- LeftTrack (left front and rear road wheels as motion meshes)
|-- RightTrack (right front and rear road wheels as motion meshes)
`-- TurretYaw
    `-- GunPitch
        `-- BarrelRecoil
            `-- Muzzle
"""

import argparse
import hashlib
import json
import math
from pathlib import Path
import sys

import numpy as np

_THIS_DIR = Path(__file__).resolve().parent
if str(_THIS_DIR) not in sys.path:
    sys.path.insert(0, str(_THIS_DIR))

import build_btr70 as btr70
from vehicle_kit import (
    GltfModel,
    MarkingAtlas,
    add_conformal_decal,
    bake_vertex_shading,
    decal_material,
    domed_hatch,
    emit_motion_parts,
    loft_sections,
    obox,
    rod,
    surface_heights,
    surface_hit,
    tag_wheel,
    tube,
)

DEFAULT_OUTPUT_DIR = _THIS_DIR.parents[1] / "src/Game.Godot/Assets/Vehicles/BRDM2"

# ----------------------------------------------------------------------------
# Reference dimensions in meters (+Y up, -Z forward, ground at Y = 0)
# ----------------------------------------------------------------------------

HULL_LENGTH = 5.600
OVERALL_LENGTH = 5.750
WIDTH_OVER_HULL = 2.350
HEIGHT_TO_TURRET_TOP = 2.170
HEIGHT_TO_SEARCHLIGHT = 2.310
GROUND_CLEARANCE = 0.430

# Hull body extents in Z: front prow at -2.800 m, stern transom at +2.800 m.
# Front and rear towing shackles reach -2.875 m and +2.875 m, making overall length 5.750 m.
HULL_FRONT_Z = -2.800
HULL_REAR_Z = 2.800

# Vertical stations
BELLY_Y = GROUND_CLEARANCE          # 0.430 m
ARCH_LIP_Y = 1.150                  # horizontal wheel-arch cutout lip
CHINE_Y = 1.380                     # main hull chine ledge
ROOF_Y = 1.780                      # horizontal fighting compartment roof
ENGINE_ROOF_Y = 1.660               # engine deck rear level
STERN_TOP_Y = 1.660                 # top of rear transom plate

# Longitudinal stations along Z
NOSE_TIP_Y = 1.320                  # prow cutwater tip
NOSE_PLATE_Z = -2.420               # intermediate forward nose crease
WINDSHIELD_BASE_Z = -2.100          # crease between upper nose plate and windshield visor plate
WINDSHIELD_BASE_Y = 1.460           # height of crease
CAB_ROOF_Z = -1.700                 # front edge of horizontal cab roof
CAB_ROOF_W = 0.880                  # roof half width at front cab crease
FIGHTING_REAR_Z = 0.950             # rear of horizontal fighting roof
ENGINE_DECK_SLOPE_Z = 2.800         # rear of engine deck before stern transom

# Running gear: 4x4 chassis with 13.00-18 tires (diameter 1.120 m, R = 0.560 m)
TRACK_GAUGE_FRONT = 1.840
TRACK_GAUGE_REAR = 1.790
TRACK_GAUGE = TRACK_GAUGE_FRONT
WHEEL_R = 0.560
WHEEL_Y = WHEEL_R                   # resting on ground at Y = 0
TIRE_WIDTH = 0.350

WHEELBASE = 3.100
WHEEL_Z_FRONT = -1.385
WHEEL_Z_REAR = 1.715
WHEEL_Z = [WHEEL_Z_FRONT, WHEEL_Z_REAR]

# Retractable auxiliary belly wheels (two pairs between axles, modeled retracted)
BELLY_WHEEL_Z = [-0.250, 0.610]
BELLY_WHEEL_R = 0.230               # diameter 0.460 m
BELLY_WHEEL_Y = 0.330               # axle height retracted (~0.10 m above ground)
BELLY_WHEEL_WIDTH = 0.150
BELLY_WHEEL_X = 0.760

# BPU-1 conical turret seated on the roof plinth
TURRET_RING = (0.0, ROOF_Y + 0.035, 0.180)
TURRET_SURFACE = {"TurretCone"}
HULL_SURFACE = {"HullBody"}


def materials(m):
    return {
        "paint": m.material("BRDM2_Paint", "5a5d3d", metal=0.14, rough=0.74),
        "rubber": m.material("BRDM2_Rubber", "252522", metal=0.02, rough=0.92),
        "steel": m.material("BRDM2_DarkSteel", "33362e", metal=0.48, rough=0.52),
        "glass": m.material("BRDM2_Optics", "1d2a30", metal=0.25, rough=0.14),
        "cavity": m.material("BRDM2_Cavity", "121310", metal=0.05, rough=0.98),
        "marker_red": m.material("BRDM2_TailRed", "96261c", metal=0.10, rough=0.42),
        "marker_amber": m.material("BRDM2_Amber", "c46c1c", metal=0.10, rough=0.42),
    }


def upper_hull_section(w_chine, y_chine, w_roof, y_roof, crown_inset=0.055, apron_drop=0.080):
    """Upper hull cross-section above the chine, counterclockwise in (x, y)."""
    w_crown = max(0.08, w_roof - crown_inset)
    right = [
        (w_chine - 0.020, y_chine - apron_drop),
        (w_chine, y_chine),
        (w_roof, y_roof - 0.016),
        (w_crown, y_roof),
    ]
    left = [(-x, y) for x, y in reversed(right)]
    return right + left


def lower_tub_section(w_belly, y_belly, w_cheek, y_cheek, w_top, y_top):
    """6-point faceted lower boat-hull cross-section below the chine, CCW in (x, y)."""
    right = [
        (w_belly, y_belly),
        (w_cheek, y_cheek),
        (w_top, y_top),
    ]
    left = [(-x, y) for x, y in reversed(right)]
    return right + left


def add_framed_plate(m, name, center, axes, half, paint, cavity, steel, parent, tint=0.90, lod=0, seam=0.012):
    """Armored hatch or access panel with a recessed perimeter seam."""
    if lod < 2:
        seam_c = tuple(center[k] - axes[0][k] * (half[0] * 0.45) for k in range(3))
        obox(m, name + "Seam", seam_c, axes, (half[0] * 0.55, half[1] + seam, half[2] + seam), cavity, parent)
    obox(m, name, center, axes, half, paint, parent, tint=tint)


def side_frame(sign, y, z, search_names=None):
    """Outward normal and axes on the hull side plate at (y, z)."""
    target = HULL_SURFACE if search_names is None else search_names
    for offset in (0.0, -0.04, 0.04, -0.08, 0.08):
        hit = surface_hit(GltfModel.active, 0, (sign * 2.0, y + offset, z), (-float(sign), 0.0, 0.0), target)
        if hit is not None:
            break
    if hit is None:
        raise RuntimeError(f"Hull side ray missed at sign={sign}, y={y:.2f}, z={z:.2f}")
    pt, norm = hit
    up = (0.0, 1.0, 0.0)
    tangent = (
        norm[1] * up[2] - norm[2] * up[1],
        norm[2] * up[0] - norm[0] * up[2],
        norm[0] * up[1] - norm[1] * up[0],
    )
    t_len = math.sqrt(tangent[0] * tangent[0] + tangent[1] * tangent[1] + tangent[2] * tangent[2]) or 1.0
    tangent = (tangent[0] / t_len, tangent[1] / t_len, tangent[2] / t_len)
    bitangent = (
        norm[1] * tangent[2] - norm[2] * tangent[1],
        norm[2] * tangent[0] - norm[0] * tangent[2],
        norm[0] * tangent[1] - norm[1] * tangent[0],
    )
    return pt, (norm, bitangent, tangent)


# ----------------------------------------------------------------------------
# Hull
# ----------------------------------------------------------------------------


def build_hull(m, hull, mats, lod):
    paint, steel, cavity = mats["paint"], mats["steel"], mats["cavity"]

    # 1. Upper boat hull: prow tip, nose slope, windshield slope, cab roof, fighting roof, engine deck
    upper_stations = [
        (HULL_FRONT_Z, upper_hull_section(0.780, NOSE_TIP_Y - 0.030, 0.750, NOSE_TIP_Y, 0.025, 0.020)),
        (NOSE_PLATE_Z, upper_hull_section(0.960, CHINE_Y - 0.020, 0.820, 1.440, 0.035, 0.030)),
        (WINDSHIELD_BASE_Z, upper_hull_section(1.140, CHINE_Y, 0.840, WINDSHIELD_BASE_Y, 0.045, 0.050)),
        (CAB_ROOF_Z, upper_hull_section(1.175, CHINE_Y, CAB_ROOF_W, ROOF_Y, 0.055, 0.080)),
        (-0.800, upper_hull_section(1.175, CHINE_Y, CAB_ROOF_W, ROOF_Y, 0.055, 0.080)),
        (TURRET_RING[2], upper_hull_section(1.175, CHINE_Y, CAB_ROOF_W, ROOF_Y, 0.055, 0.080)),
        (FIGHTING_REAR_Z, upper_hull_section(1.175, CHINE_Y, CAB_ROOF_W, ROOF_Y, 0.055, 0.080)),
        (FIGHTING_REAR_Z + 0.120, upper_hull_section(1.175, CHINE_Y, 0.860, ENGINE_ROOF_Y + 0.060, 0.055, 0.080)),
        (2.050, upper_hull_section(1.175, CHINE_Y, 0.850, ENGINE_ROOF_Y, 0.055, 0.080)),
        (HULL_REAR_Z, upper_hull_section(1.150, CHINE_Y - 0.020, 0.820, STERN_TOP_Y, 0.050, 0.040)),
    ]
    loft_sections(m, "HullBody", upper_stations, paint, hull, tint=0.96)

    # 2. Lower boat tub with wheel-arch cutouts and belly recesses
    lower_stations = [
        (HULL_FRONT_Z, lower_tub_section(0.550, NOSE_TIP_Y - 0.080, 0.720, NOSE_TIP_Y - 0.050, 0.760, NOSE_TIP_Y - 0.030)),
        (NOSE_PLATE_Z, lower_tub_section(0.650, 0.950, 0.880, 1.050, 0.940, CHINE_Y - 0.040)),
        (-1.950, lower_tub_section(0.680, BELLY_Y + 0.040, 1.050, 0.640, 1.150, ARCH_LIP_Y)),
        # Front wheel-arch upward cutout around Z = -1.385
        (-1.850, lower_tub_section(0.700, BELLY_Y, 0.760, 0.800, 0.820, ARCH_LIP_Y)),
        (-0.920, lower_tub_section(0.700, BELLY_Y, 0.760, 0.800, 0.820, ARCH_LIP_Y)),
        # Mid-section belly wheel sponson bay (Z = -0.70 to +1.00)
        (-0.750, lower_tub_section(0.720, BELLY_Y, 1.060, 0.720, 1.150, ARCH_LIP_Y)),
        (-0.250, lower_tub_section(0.720, BELLY_Y, 1.060, 0.720, 1.150, ARCH_LIP_Y)),
        (0.610, lower_tub_section(0.720, BELLY_Y, 1.060, 0.720, 1.150, ARCH_LIP_Y)),
        (1.050, lower_tub_section(0.720, BELLY_Y, 1.060, 0.720, 1.150, ARCH_LIP_Y)),
        # Rear wheel-arch upward cutout around Z = +1.715
        (1.180, lower_tub_section(0.700, BELLY_Y, 0.760, 0.800, 0.820, ARCH_LIP_Y)),
        (2.250, lower_tub_section(0.700, BELLY_Y, 0.760, 0.800, 0.820, ARCH_LIP_Y)),
        # Stern lower tub into rear transom
        (2.550, lower_tub_section(0.680, BELLY_Y + 0.060, 1.040, 0.720, 1.150, ARCH_LIP_Y)),
        (HULL_REAR_Z, lower_tub_section(0.620, 0.600, 0.980, 0.880, 1.140, CHINE_Y - 0.020)),
    ]
    loft_sections(m, "HullLowerTub", lower_stations, paint, hull, tint=0.92)

    # Folded amphibious trim vane on the lower nose plate
    m.add_box("TrimVaneFolded", (1.46, 0.024, 0.38), (0.0, 0.96, -2.62), paint, hull, tint=0.88)
    if lod == 0:
        for vx in (-0.55, 0.55):
            rod(m, "TrimVaneStrut", (vx, 0.96, -2.62), (vx, 1.08, -2.52), 0.012, steel, hull, sides=4)

    # Armored windshield visor flaps over forward driver and commander viewports
    # Seated on cab front plate with surface_hit (flush, distance <= 1 cm), hinged at top edge
    for sign in (-1, 1):
        hit = surface_hit(m, hull, (sign * 0.360, 2.50, -1.900), (0.0, -1.0, 0.0), HULL_SURFACE)
        if hit is None:
            raise RuntimeError(f"Visor surface_hit missed at sign={sign}")
        pt, norm = hit
        axis_n = norm
        axis_w = (1.0, 0.0, 0.0)
        axis_h = (0.0, -norm[2], norm[1])
        half = (0.012, 0.290, 0.150)
        c = tuple(pt[k] + axis_n[k] * half[0] for k in range(3))
        obox(m, "WindshieldVisor", c, (axis_n, axis_w, axis_h), half, paint, hull, tint=0.90)

        # Hinge at the top edge
        top_edge = tuple(c[k] + axis_h[k] * half[2] for k in range(3))
        m.add_cylinder("WindshieldVisorHinge", 0.016, 0.580, top_edge, steel, "x", 8 if lod < 2 else 6, parent=hull)

        if lod < 2:
            slit_c = tuple(c[k] + axis_n[k] * (half[0] + 0.002) for k in range(3))
            obox(m, "WindshieldVisorSlit", slit_c, (axis_n, axis_w, axis_h), (0.004, 0.120, 0.025), cavity, hull)
            # Periscope vision blocks on cab roof just behind the visor hinge
            m.add_box("PeriscopeHood", (0.12, 0.07, 0.09), (sign * 0.360, ROOF_Y + 0.035, -1.63), steel, hull)
            m.add_box("PeriscopeGlass", (0.09, 0.030, 0.02), (sign * 0.360, ROOF_Y + 0.035, -1.68), mats["glass"], hull)

    # Forward crew hatches: driver (left) and commander (right)
    for sign in (-1, 1):
        hx = sign * 0.400
        hz = -1.100
        domed_hatch(m, "CrewHatch", hull, (hx, ROOF_Y + 0.005, hz), 0.330, paint, (12, 10, 6)[lod], rise=0.026, hinge_mat=steel, hinge_dir=(0.0, 0.0, 1.0))
        if lod == 0:
            m.add_box("HatchLatch", (0.04, 0.025, 0.06), (hx, ROOF_Y + 0.032, hz - 0.300), steel, hull)

    # Turret base plinth on fighting compartment roof
    m.add_cylinder("TurretPlinth", 0.68, 0.035, (0.0, ROOF_Y + 0.018, TURRET_RING[2]), steel, "y", (20, 14, 8)[lod], parent=hull)

    # Rear engine compartment hatches behind turret
    for sign in (-1, 1):
        add_framed_plate(
            m, "EngineDeckHatch",
            (sign * 0.38, ENGINE_ROOF_Y + 0.008, 1.55),
            ((0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (1.0, 0.0, 0.0)),
            (0.014, 0.40, 0.28), paint, cavity, steel, hull, tint=0.90, lod=lod
        )

    # Rear engine cooling louvres: two large grilles (Z: 2.07 to 2.80, |X|: 0.25 to 0.70)
    for sign in (-1, 1):
        lx = sign * 0.475
        lz = 2.435
        m.add_box("EngineGrille", (0.450, 0.020, 0.730), (lx, ENGINE_ROOF_Y + 0.006, lz), cavity, hull)
        if lod == 0:
            num_slats = 9
            slat_spacing = 0.680 / (num_slats - 1)
            for k in range(num_slats):
                sz = 2.095 + k * slat_spacing
                m.add_box("EngineLouver", (0.430, 0.016, 0.038), (lx, ENGINE_ROOF_Y + 0.014, sz), paint, hull, tint=0.86)
        elif lod == 1:
            m.add_box("EngineLouverMid", (0.430, 0.014, 0.710), (lx, ENGINE_ROOF_Y + 0.012, lz), paint, hull, tint=0.86)

    # Distinctive side exhaust mufflers with perforated heat shields
    for sign in (-1, 1):
        mx = sign * 1.04
        my = 1.62
        mz = 2.10
        # Muffler cylinder
        m.add_cylinder("ExhaustMuffler", 0.075, 0.62, (mx, my, mz), steel, "z", 8 if lod < 2 else 6, parent=hull)
        if lod < 2:
            # Curved pipe entering engine bay
            rod(m, "ExhaustPipe", (sign * 0.88, my - 0.02, mz - 0.38), (mx, my, mz - 0.31), 0.028, steel, hull, sides=4)
            # Perforated heat shield over muffler
            m.add_cylinder("MufflerHeatShield", 0.086, 0.54, (mx, my + 0.01, mz), paint, "z", 8 if lod == 0 else 6, parent=hull, caps="none")
        if lod == 0:
            for sz in (mz - 0.22, mz + 0.22):
                m.add_cylinder("MufflerClamp", 0.092, 0.024, (mx, my + 0.01, sz), steel, "z", 6, parent=hull)

    # Retractable auxiliary belly wheels (two pairs, modeled retracted)
    sides_belly = (12, 8, 6)[lod]
    for sign in (-1, 1):
        for bz in BELLY_WHEEL_Z:
            build_belly_wheel(m, sign, bz, sides_belly, mats, hull, lod)

    # Chine rub rail along the hull sides
    for sign in (-1, 1):
        m.add_box("ChineRubRail", (0.020, 0.030, 4.40), (sign * 1.165, CHINE_Y, 0.0), steel, hull)

    # Headlights in protective wire cages on bow corners
    for sign in (-1, 1):
        hx, hy, hz = sign * 0.92, 1.48, -2.48
        m.add_box("HeadlightBracket", (0.16, 0.024, 0.14), (hx, hy - 0.06, hz), paint, hull, tint=0.88)
        m.add_cylinder("HeadlightHousing", 0.065, 0.10, (hx, hy, hz), steel, "z", 8 if lod < 2 else 6, parent=hull)
        if lod < 2:
            m.add_disc("HeadlightLens", 0.054, (hx, hy, hz - 0.052), mats["glass"], "z", 8, hull, -1.0)
            m.add_cylinder("TurnIndicator", 0.028, 0.04, (hx + sign * 0.09, hy - 0.01, hz), mats["marker_amber"], "z", 6, parent=hull)
        if lod == 0:
            for gx in (hx - 0.08, hx + 0.08):
                rod(m, "HeadlightGuard", (gx, hy - 0.06, hz + 0.06), (gx, hy + 0.08, hz + 0.06), 0.007, steel, hull, sides=4)
                rod(m, "HeadlightGuard", (gx, hy - 0.06, hz - 0.06), (gx, hy + 0.08, hz - 0.06), 0.007, steel, hull, sides=4)
                rod(m, "HeadlightGuard", (gx, hy + 0.08, hz + 0.06), (gx, hy + 0.08, hz - 0.06), 0.007, steel, hull, sides=4)

    # Whip antenna base on left rear shoulder
    if lod < 2:
        m.add_cylinder("AntennaBase", 0.040, 0.08, (-0.88, ROOF_Y + 0.02, 0.70), steel, "y", 6, parent=hull)
        if lod == 0:
            rod(m, "Antenna", (-0.88, ROOF_Y + 0.06, 0.70), (-0.88, ROOF_Y + 1.80, 0.68), 0.006, steel, hull, sides=4)

    # Heavy bow and stern towing mounts with shackles
    for sign in (-1, 1):
        m.add_box("BowTowMount", (0.08, 0.09, 0.05), (sign * 0.62, 1.08, HULL_FRONT_Z + 0.03), steel, hull)
        m.add_cylinder("BowShackle", 0.050, 0.028, (sign * 0.62, 1.04, -2.825), steel, "x", 8 if lod < 2 else 6, parent=hull)

        m.add_box("SternTowMount", (0.08, 0.09, 0.05), (sign * 0.62, 0.88, HULL_REAR_Z - 0.03), steel, hull)
        m.add_cylinder("SternShackle", 0.050, 0.028, (sign * 0.62, 0.84, 2.825), steel, "x", 8 if lod < 2 else 6, parent=hull)

        tx, ty, tz = sign * 0.82, 1.35, HULL_REAR_Z - 0.025
        m.add_box("TailLightBox", (0.18, 0.10, 0.04), (tx, ty, tz), steel, hull)
        if lod < 2:
            m.add_cylinder("TailLampRed", 0.032, 0.020, (tx - sign * 0.04, ty, tz + 0.015), mats["marker_red"], "z", 6, parent=hull)
            m.add_cylinder("TailLampAmber", 0.032, 0.020, (tx + sign * 0.04, ty, tz + 0.015), mats["marker_amber"], "z", 6, parent=hull)

    # Stern waterjet outlet cover in the center of the rear transom
    if lod < 2:
        m.add_box("WaterjetFrame", (0.64, 0.52, 0.020), (0.0, 1.02, HULL_REAR_Z - 0.010), cavity, hull)
    m.add_box("WaterjetCover", (0.58, 0.48, 0.035), (0.0, 1.02, HULL_REAR_Z - 0.020), paint, hull, tint=0.86)


def build_belly_wheel(m, sign, z, sides, mats, parent, lod):
    """Retractable auxiliary chain-driven belly wheel for trench crossing."""
    x = sign * BELLY_WHEEL_X
    y, r = BELLY_WHEEL_Y, BELLY_WHEEL_R
    hw = BELLY_WHEEL_WIDTH * 0.5
    rim_r = r * 0.55

    m.add_cylinder("BellyWheelTire", r, BELLY_WHEEL_WIDTH, (x, y, z), mats["rubber"], "x", sides, parent=parent, caps="none")
    face_x = x + sign * hw
    m.add_disc("BellyWheelTireWall", r, (face_x, y, z), mats["rubber"], "x", sides, parent, float(sign))
    m.add_disc("BellyWheelDish", rim_r, (face_x + sign * 0.004, y, z), mats["paint"], "x", sides, parent, float(sign))
    m.add_cylinder("BellyWheelHub", r * 0.28, 0.030, (face_x + sign * 0.012, y, z), mats["steel"], "x", max(6, sides // 2), parent=parent, caps="pos" if sign > 0 else "neg")

    if lod < 2:
        rod(m, "BellyWheelArm", (sign * (BELLY_WHEEL_X - 0.10), y + 0.12, z), (sign * BELLY_WHEEL_X, y, z), 0.024, mats["steel"], parent, sides=4)


# ----------------------------------------------------------------------------
# Running gear (Toroidal KI-80 13.00-18 chevron tires + deep CTIS hubs)
# ----------------------------------------------------------------------------


def road_wheel(m, sign, x_center, z, sides, mats, parent, lod):
    x = sign * x_center
    y, r = WHEEL_Y, WHEEL_R
    hw = TIRE_WIDTH * 0.5
    rim_r = r * 0.52

    if lod == 2:
        m.add_cylinder("RoadWheelTire", r, TIRE_WIDTH, (x, y, z), mats["rubber"], "x", sides, parent=parent, caps="none")
        face_x = x + sign * hw
        m.add_disc("RoadWheelTireWall", r, (face_x, y, z), mats["rubber"], "x", sides, parent, float(sign))
        m.add_disc("RoadWheelDish", rim_r, (face_x + sign * 0.005, y, z), mats["paint"], "x", sides, parent, float(sign))
        m.add_cylinder("RoadWheelHub", r * 0.22, 0.04, (face_x + sign * 0.020, y, z), mats["steel"], "x", sides, parent=parent, caps="pos" if sign > 0 else "neg")
        return

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
    # Front wheel
    first = len(m.parts)
    road_wheel(m, sign, TRACK_GAUGE_FRONT / 2.0, WHEEL_Z_FRONT, sides, mats, side, lod)
    tag_wheel(m, first, (WHEEL_Y, WHEEL_Z_FRONT), WHEEL_R)

    # Rear wheel
    first = len(m.parts)
    road_wheel(m, sign, TRACK_GAUGE_REAR / 2.0, WHEEL_Z_REAR, sides, mats, side, lod)
    tag_wheel(m, first, (WHEEL_Y, WHEEL_Z_REAR), WHEEL_R)


# ----------------------------------------------------------------------------
# Markings (Soviet Red Star + Tactical Number)
# ----------------------------------------------------------------------------


def build_markings(m, hull, lod=0):
    atlas = MarkingAtlas(512, 256)
    atlas.star((0.0, 0.0, 0.5, 1.0), fill=(178, 34, 30, 255), outline=(232, 228, 214, 255), outline_width=0.07)
    atlas.text((0.5, 0.14, 1.0, 0.86), "245", fill=(232, 228, 214, 255))
    material = decal_material(m, atlas, "BRDM2_Markings")

    grid = (6, 4) if lod == 0 else (4, 3)
    for sign in (-1, 1):
        for z, size, region in [
            (-0.40, (0.28, 0.28), (0.0, 0.0, 0.5, 1.0)),
            (0.85, (0.44, 0.26), (0.5, 0.0, 1.0, 1.0)),
        ]:
            hit = surface_hit(m, hull, (sign * 2.0, 1.55, z), (-float(sign), 0.0, 0.0), HULL_SURFACE)
            if hit is None:
                raise RuntimeError(f"Marking ray missed HullBody at z={z}, sign={sign}")
            center, normal = hit
            add_conformal_decal(m, "Marking", hull, center, normal, (0.0, 1.0, 0.0), size, region, material, HULL_SURFACE, grid=grid)


def bake_edge_wear_and_grime(m):
    """Add metallic edge-wear and crevice contrast into vertex colors."""
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
            near_chine = np.exp(-((pts[:, 1] - CHINE_Y) ** 2) / (0.065 ** 2)) * (np.abs(pts[:, 0]) > 1.05)
            near_roof = np.exp(-((pts[:, 1] - ROOF_Y) ** 2) / (0.055 ** 2)) * (np.abs(pts[:, 0]) > 0.75)
            near_arch = np.exp(-((pts[:, 1] - ARCH_LIP_Y) ** 2) / (0.045 ** 2)) * (np.abs(pts[:, 0]) > 1.05)
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


def build_brdm2_lod(lod=0, output_dir=None):
    m = GltfModel(f"BRDM2_LOD{lod}")
    m.data["asset"]["generator"] = "Dead Ground BRDM-2 model authoring"
    mats = materials(m)
    hull = m.node("Hull")
    left = m.node("LeftTrack")
    right = m.node("RightTrack")
    turret = m.node("TurretYaw", TURRET_RING)
    pitch = m.node("GunPitch", btr70.GUN_TRUNNION_LOCAL, turret)
    recoil = m.node("BarrelRecoil", parent=pitch)
    m.node("Muzzle", (0.0, 0.0, btr70.MUZZLE_LOCAL_Z), recoil)

    build_hull(m, hull, mats, lod)
    build_running_gear(m, left, -1, mats, lod)
    build_running_gear(m, right, 1, mats, lod)
    btr70.build_turret(m, turret, mats, lod)
    btr70.build_gun(m, pitch, recoil, mats, lod)

    bake_vertex_shading(m, gear_nodes=("LeftTrack", "RightTrack"), max_edge=(0.60, 1.10, None)[lod])
    bake_edge_wear_and_grime(m)
    emit_motion_parts(m)
    if lod < 2:
        build_markings(m, hull, lod=lod)

    name = "brdm2.glb" if lod == 0 else f"brdm2_lod{lod}.glb"
    out_dir = Path(output_dir) if output_dir else DEFAULT_OUTPUT_DIR
    out_dir.mkdir(parents=True, exist_ok=True)
    stats = m.save(out_dir / name)
    stats["lod"] = lod
    stats["role"] = ("close", "rts", "distant")[lod]
    return stats


def generate_all(output_dir=None):
    out_dir = Path(output_dir) if output_dir else DEFAULT_OUTPUT_DIR
    out_dir.mkdir(parents=True, exist_ok=True)
    lods = [build_brdm2_lod(lod, out_dir) for lod in range(3)]
    manifest = {
        "asset": "BRDM-2 (GAZ-41-08) 4x4 amphibious scout car RTS visual study",
        "configuration": "BRDM-2 with 4x4 road wheels, retracted auxiliary belly wheels, forward visor flaps, dual rear deck louvres, side mufflers with heat shields, single rear waterjet outlet, BPU-1 turret, and 14.5 mm KPVT gun",
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
            "height_to_searchlight_meters": HEIGHT_TO_SEARCHLIGHT,
            "ground_clearance_meters": GROUND_CLEARANCE,
            "wheelbase_meters": WHEELBASE,
            "track_gauge_front_meters": TRACK_GAUGE_FRONT,
            "track_gauge_rear_meters": TRACK_GAUGE_REAR,
            "track_gauge_meters": TRACK_GAUGE,
            "wheel_radius_meters": WHEEL_R,
            "wheels_per_side": len(WHEEL_Z),
            "belly_wheels_per_side": len(BELLY_WHEEL_Z),
        },
        "lods": lods,
        "track_gauge_meters": TRACK_GAUGE,
        "track_wrap_meters": btr70.wheel_wrap_period(WHEEL_R),
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
