"""Procedural 9K35 Strela-10SV (9A35M / SA-13 Gopher) tracked IR SAM launcher on an MT-LB chassis.

Three explicit LODs with animated tracked running gear, 360-degree turret traverse, and
-5 to +80 degree elevation for the four 9M37 missile launch containers and 9S86 ranging radar.

Sources (URLs in SOURCES and in the manifest):
- 9K35 Strela-10 / 9A35 combat vehicle on MT-LB chassis: hull length 6,450 mm, width 2,850 mm,
  hull roof height 1,865 mm, ground clearance 400 mm, track gauge 2,500 mm, track width 350 mm,
  6 road wheels per side with front drive sprockets and no return rollers.
- Turret and launcher: 360-degree continuous traverse, -5 to +80 degree elevation, central glazed
  operator cabin flanked by two pairs of rectangular 9M37 missile containers (2.22 m long) and
  center-mounted 9S86 ranging radar housing ("Snap Shot"), plus 9S16 passive RWR pods.

Coordinates: meters, +Y up, -Z forward, +X right, ground plane at Y = 0. Every node has unit scale
and no rotation in the rest pose. Rig nodes match the other vehicles: TurretYaw, GunPitch, Muzzle,
plus Launch_01 to Launch_04 (one per missile container front, bore along local -Z of GunPitch).
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
    add_decal,
    bake_vertex_shading,
    decal_material,
    domed_hatch,
    emit_motion_parts,
    hex_section,
    loft_sections,
    obox,
    rod,
    tag_wheel,
    track_belt,
    track_links,
    track_path,
    track_wrap_period,
    tube,
)

DEFAULT_OUTPUT_DIR = _THIS_DIR.parents[1] / "src/Game.Godot/Assets/Vehicles/Strela10"

SOURCES = [
    "https://en.wikipedia.org/wiki/9K35_Strela-10",
    "https://ru.wikipedia.org/wiki/%D0%A1%D1%82%D1%80%D0%B5%D0%BB%D0%B0-10",
    "https://en.wikipedia.org/wiki/MT-LB",
    "https://commons.wikimedia.org/wiki/Category:9K35_Strela-10",
]

# ----------------------------------------------------------------------------
# Reference layout in meters (+Y up, -Z forward, +X right, ground at Y = 0)
# ----------------------------------------------------------------------------

REFERENCE_LENGTH = 6.45
REFERENCE_WIDTH = 2.85
REFERENCE_HULL_HEIGHT = 1.865
REFERENCE_CLEARANCE = 0.40

HULL_FRONT_Z = -REFERENCE_LENGTH / 2.0      # -3.225 m (prow tip)
HULL_REAR_Z = REFERENCE_LENGTH / 2.0        # +3.225 m (rear transom)
HALF_WIDTH = REFERENCE_WIDTH / 2.0          # 1.425 m

DECK_Y = REFERENCE_HULL_HEIGHT              # 1.865 m
CHINE_Y = 1.14                              # top of fender shelf / sponson outer crease
NOSE_TIP_Y = 1.04                           # sharp amphibious prow nose height
FENDER_Y = 1.16

# MT-LB running gear: 6 stamped road wheels per side, front drive sprocket, rear idler, no return rollers
TRACK_GAUGE = 2.50                          # center-to-center distance between tracks
TRACK_WIDTH = 0.35
TRACK_X_CENTER = TRACK_GAUGE / 2.0          # 1.250 m
TRACK_X_INNER = TRACK_X_CENTER - TRACK_WIDTH / 2.0   # 1.075 m
TRACK_X_OUTER = TRACK_X_CENTER + TRACK_WIDTH / 2.0   # 1.425 m
TRACK_THICKNESS = 0.055

ROAD_WHEEL_Z = [-1.92, -1.16, -0.40, 0.36, 1.12, 1.88]
ROAD_WHEEL_R = 0.345
TRACK_PAD_HEIGHT = 0.022
ROAD_WHEEL_Y = ROAD_WHEEL_R + TRACK_THICKNESS + TRACK_PAD_HEIGHT  # 0.422 m -> grouser tips rest flush on Y = 0.0
SPROCKET = (-2.68, 0.742, 0.275)            # front drive sprocket (z, y, r)
IDLER = (2.66, 0.702, 0.260)                # rear tension idler (z, y, r)

# 9A35M launcher turret: mounted aft of the mid-hull engine deck, continuous 360-degree traverse
TURRET_POS = (0.0, DECK_Y, 1.02)
CABIN_HALF_W = 0.44
CABIN_HEIGHT = 1.00                         # turret cabin roof at world Y = 2.865 m

# Elevating cradle trunnions sit high on the turret cabin sides so containers clear the roof up to +80 deg
TRUNNION_LOCAL = (0.0, 0.90, 0.06)          # world (0.0, 2.765, 1.08)
ELEVATION_MIN_DEG = -5.0
ELEVATION_MAX_DEG = 80.0

# Four rectangular 9M37 launch containers: two pairs flanking the central operator cabin
CONTAINER_LENGTH = 2.20
CONTAINER_WIDTH = 0.31
CONTAINER_HEIGHT = 0.31
CONTAINER_FRONT_Z = -1.52                   # forward bias keeps rear swing >20 cm clear of hull roof at +80 deg
CONTAINER_REAR_Z = CONTAINER_FRONT_Z + CONTAINER_LENGTH   # +0.68 m
LAUNCH_Z = CONTAINER_FRONT_Z - 0.03         # -1.55 m

# Launch order: outer left, outer right, inner left, inner right (balanced visual pairs)
CONTAINER_SLOTS = [
    ("Launch_01", -1.12, 0.0),
    ("Launch_02", 1.12, 0.0),
    ("Launch_03", -0.75, 0.0),
    ("Launch_04", 0.75, 0.0),
]

LOD_BUDGETS = (21000, 5500, 1900)


def materials(m):
    return {
        "paint": m.material("Strela10_Paint", "626342", metal=0.08, rough=0.84),
        "pod": m.material("Strela10_PodPaint", "595c3c", metal=0.06, rough=0.82),
        "radome": m.material("Strela10_Radome", "78745c", metal=0.04, rough=0.78),
        "rubber": m.material("Strela10_Rubber", "2d2d29", rough=0.96),
        "track": m.material("Strela10_TrackSteel", "3f3e37", metal=0.40, rough=0.78),
        "steel": m.material("Strela10_DarkSteel", "34372f", metal=0.32, rough=0.70),
        "glass": m.material("Strela10_Optics", "24363d", metal=0.18, rough=0.20),
        "cavity": m.material("Strela10_Cavity", "161712", rough=1.0),
        "soot": m.material("Strela10_Soot", "23221d", rough=1.0),
        "wood": m.material("Strela10_Wood", "6b563b", rough=0.94),
    }


def _hull_section(wb, yb, wc, yc, wt, yt):
    """MT-LB boat-hull cross section with flat belly, flared sponson chine, and inward upper tumblehome."""
    return [
        (wb, yb),
        (wc, yc),
        (wt, yt),
        (-wt, yt),
        (-wc, yc),
        (-wb, yb),
    ]


# ----------------------------------------------------------------------------
# Hull (MT-LB amphibious tracked chassis)
# ----------------------------------------------------------------------------


def build_hull(m, hull, mats, lod):
    paint, steel, glass, cavity = mats["paint"], mats["steel"], mats["glass"], mats["cavity"]

    # Main armored boat hull: sharp overhanging amphibious bow, stepped front cab, long flat roof
    loft_sections(
        m,
        "HullBody",
        [
            (HULL_FRONT_Z, _hull_section(0.98, NOSE_TIP_Y - 0.05, 1.04, NOSE_TIP_Y, 1.02, NOSE_TIP_Y + 0.06)),
            (-2.42, _hull_section(1.00, REFERENCE_CLEARANCE, 1.18, CHINE_Y, 1.16, 1.46)),
            (-1.90, _hull_section(1.02, REFERENCE_CLEARANCE, 1.32, CHINE_Y + 0.04, 1.24, 1.56)),
            (-1.46, _hull_section(1.02, REFERENCE_CLEARANCE, 1.38, CHINE_Y + 0.08, 1.24, DECK_Y)),
            (2.86, _hull_section(1.02, REFERENCE_CLEARANCE, 1.38, CHINE_Y + 0.08, 1.24, DECK_Y)),
            (HULL_REAR_Z, _hull_section(1.00, 0.56, 1.34, CHINE_Y + 0.04, 1.20, DECK_Y - 0.06)),
        ],
        paint,
        hull,
        tint=0.94,
    )

    # Front glacis armored cab windscreen brow and twin driver/commander viewports
    # Angled plate from z = -1.90 (y = 1.56) to z = -1.46 (y = 1.865)
    brow_dz = -1.46 - (-1.90)
    brow_dy = DECK_Y - 1.56
    brow_len = math.hypot(brow_dz, brow_dy)
    brow_along = (0.0, brow_dy / brow_len, brow_dz / brow_len)
    brow_norm = (0.0, brow_dz / brow_len, -brow_dy / brow_len)

    for sign in (-1, 1):
        wx = sign * 0.56
        wz = -1.69
        wy = 1.71
        # Armored window frame and dark blue-green ballistic glass
        obox(m, "CabWindowFrame", (wx, wy, wz), ((1.0, 0.0, 0.0), brow_norm, brow_along), (0.28, 0.024, 0.14), steel, hull)
        obox(m, "CabWindowGlass", (wx, wy + brow_norm[1] * 0.016, wz + brow_norm[2] * 0.016), ((1.0, 0.0, 0.0), brow_norm, brow_along), (0.23, 0.010, 0.10), glass, hull)
        # Characteristic propped-open MT-LB armored window shutter visor above each windscreen
        if lod < 2:
            visor_c = (wx, wy + 0.145, wz - 0.07)
            obox(
                m,
                "WindowShutter",
                visor_c,
                ((1.0, 0.0, 0.0), (0.0, 0.96, -0.28), (0.0, 0.28, 0.96)),
                (0.28, 0.014, 0.13),
                paint,
                hull,
                tint=0.88,
            )

    # Front bow trim vane (amphibious wave deflector) folded across the upper bow plate
    bow_dz = -2.42 - HULL_FRONT_Z
    bow_dy = 1.46 - (NOSE_TIP_Y + 0.06)
    bow_len = math.hypot(bow_dz, bow_dy)
    bow_along = (0.0, bow_dy / bow_len, bow_dz / bow_len)
    bow_norm = (0.0, bow_dz / bow_len, -bow_dy / bow_len)
    vane_z = -2.86
    vane_y = 1.27
    vane_c = (0.0, vane_y + bow_norm[1] * 0.055, vane_z + bow_norm[2] * 0.055)
    obox(m, "WaveDeflector", vane_c, ((1.0, 0.0, 0.0), bow_norm, bow_along), (0.98, 0.048, 0.13), paint, hull, tint=0.86)

    # Side fenders / amphibious mudguards extending cleanly over the tracks
    for sign in (-1, 1):
        m.add_box("Fender", (0.40, 0.035, 5.56), (sign * 1.24, FENDER_Y, -0.02), paint, hull, tint=0.85)
        # Angled front & rear amphibious mudflaps
        for z0, z1, y1 in [(-2.80, -3.14, 0.96), (2.76, 3.12, 0.98)]:
            mid_z = (z0 + z1) / 2.0
            mid_y = (FENDER_Y + y1) / 2.0
            seg_len = math.hypot(z1 - z0, y1 - FENDER_Y)
            tangent = (0.0, (y1 - FENDER_Y) / seg_len, (z1 - z0) / seg_len)
            normal = (0.0, -tangent[2], tangent[1])
            obox(m, "FenderFlap", (sign * 1.24, mid_y, mid_z), ((1.0, 0.0, 0.0), normal, tangent), (0.20, 0.016, seg_len / 2.0), paint, hull, tint=0.82)

        # Front headlights with wire brush guards on upper bow shoulders
        if lod < 2:
            hl_x, hl_y, hl_z = sign * 1.08, 1.44, -2.54
            m.add_box("HeadlightHousing", (0.18, 0.15, 0.16), (hl_x, hl_y, hl_z), steel, hull)
            m.add_disc("HeadlightLens", 0.055, (hl_x, hl_y + 0.005, hl_z - 0.082), glass, "z", 10, hull, -1.0)
            if lod == 0:
                rod(m, "HeadlightGuard", (hl_x - 0.11, hl_y - 0.08, hl_z + 0.06), (hl_x - 0.11, hl_y + 0.11, hl_z - 0.11), 0.010, steel, hull)
                rod(m, "HeadlightGuard", (hl_x + 0.11, hl_y - 0.08, hl_z + 0.06), (hl_x + 0.11, hl_y + 0.11, hl_z - 0.11), 0.010, steel, hull)
                rod(m, "HeadlightGuard", (hl_x - 0.11, hl_y + 0.11, hl_z - 0.11), (hl_x + 0.11, hl_y + 0.11, hl_z - 0.11), 0.010, steel, hull)

            # Heavy front & rear tow hooks seated solidly onto the bow and rear armor plates
            m.add_box("FrontTowHook", (0.09, 0.11, 0.14), (sign * 0.62, 0.90, -3.08), steel, hull)
            m.add_box("RearTowHook", (0.09, 0.11, 0.12), (sign * 0.72, 0.90, HULL_REAR_Z + 0.02), steel, hull)
            m.add_box("TailLight", (0.12, 0.09, 0.05), (sign * 1.06, 1.56, HULL_REAR_Z + 0.02), steel, hull)

            # 9S16 "Flat Box" passive radar warning / direction-finder plates on front & rear hull corners (signature of 9A35)
            m.add_box("RwrPodFront", (0.14, 0.24, 0.06), (sign * 1.18, 1.62, -1.92), mats["radome"], hull)
            m.add_box("RwrPodRear", (0.14, 0.24, 0.06), (sign * 1.14, 1.62, HULL_REAR_Z + 0.025), mats["radome"], hull)

    # Crew hatches on forward roof behind windscreens
    hatch_sides = 14 if lod < 2 else 8
    for sign, name in ((-1, "DriverHatch"), (1, "CommanderHatch")):
        hx, hz = sign * 0.56, -1.04
        domed_hatch(m, name, hull, (hx, DECK_Y, hz), 0.27, paint, hatch_sides, rise=0.045, hinge_mat=steel if lod < 2 else None, hinge_dir=(0.0, 0.0, 1.0))
        if lod < 2:
            m.add_box("CrewPeriscope", (0.14, 0.055, 0.07), (hx, DECK_Y + 0.04, hz - 0.30), glass, hull)

    # Mid-hull MT-LB engine bay louvers & armored radiator grille (between crew cab and turret)
    m.add_box("EngineDeckFrame", (1.36, 0.04, 1.08), (-0.10, DECK_Y + 0.02, -0.18), steel, hull)
    if lod == 0:
        m.add_box("EngineDeckCavity", (1.24, 0.015, 0.96), (-0.10, DECK_Y + 0.038, -0.18), cavity, hull)
        for i in range(8):
            m.add_box("EngineLouver", (1.20, 0.025, 0.045), (-0.10, DECK_Y + 0.052, -0.60 + i * 0.12), paint, hull, tint=0.84)
    else:
        # Match LOD0 slatted visual brightness so LOD switches never pop to a solid black rectangle
        m.add_box("EngineDeckGrille", (1.24, 0.022, 0.96), (-0.10, DECK_Y + 0.040, -0.18), steel, hull, tint=0.78)

    # Signature MT-LB right-side armored exhaust muffler running along upper right deck/side
    m.add_box("ExhaustGuard", (0.30, 0.20, 1.24), (1.12, DECK_Y - 0.04, -0.22), steel, hull)
    if lod < 2:
        m.add_cylinder("ExhaustPipe", 0.065, 0.36, (1.12, DECK_Y - 0.05, 0.50), steel, "z", 10, parent=hull)
        m.add_box("ExhaustSoot", (0.34, 0.005, 0.95), (1.12, DECK_Y + 0.004, 0.92), mats["soot"], hull)

    # Side stowage boxes along rear upper hull flanks (clear of forward side markings)
    m.add_box("LeftStowageBox", (0.20, 0.28, 1.05), (-1.36, 1.46, 1.48), paint, hull, tint=0.82)
    m.add_box("RightStowageBox", (0.20, 0.28, 1.05), (1.36, 1.46, 1.48), paint, hull, tint=0.82)

    # Twin armored rear troop/reload doors on the rear transom
    if lod < 2:
        for sign in (-1, 1):
            m.add_box("RearDoor", (0.52, 0.96, 0.045), (sign * 0.30, 1.20, HULL_REAR_Z + 0.018), paint, hull, tint=0.90)
            if lod == 0:
                m.add_box("DoorHinge", (0.04, 0.10, 0.04), (sign * 0.54, 1.48, HULL_REAR_Z + 0.045), steel, hull)
                m.add_box("DoorHinge", (0.04, 0.10, 0.04), (sign * 0.54, 0.92, HULL_REAR_Z + 0.045), steel, hull)
                m.add_box("DoorHandle", (0.08, 0.03, 0.04), (sign * 0.10, 1.20, HULL_REAR_Z + 0.05), steel, hull)

    # Rear unditching log strapped low across the rear hull with steel brackets connected to the transom
    if lod < 2:
        m.add_cylinder("UnditchingLog", 0.105, 2.12, (0.0, 0.74, HULL_REAR_Z + 0.10), mats["wood"], "x", (12, 8, 6)[lod], parent=hull)
        for sx in (-0.75, 0.75):
            m.add_cylinder("LogStrap", 0.114, 0.05, (sx, 0.74, HULL_REAR_Z + 0.10), steel, "x", 10, parent=hull)
            m.add_box("LogBracket", (0.06, 0.14, 0.12), (sx, 0.74, HULL_REAR_Z + 0.02), steel, hull)

    # Rear roof reload hatches (for the 4 spare 9M37 missiles stowed inside the MT-LB rear compartment) & ventilator
    if lod < 2:
        for sign in (-1, 1):
            m.add_box("ReloadHatch", (0.52, 0.04, 0.96), (sign * 0.52, DECK_Y + 0.02, 2.34), paint, hull, tint=0.88)
            if lod == 0:
                m.add_box("ReloadHatchLatch", (0.12, 0.025, 0.05), (sign * 0.52, DECK_Y + 0.05, 1.92), steel, hull)
                m.add_box("ReloadHatchHinge", (0.08, 0.03, 0.06), (sign * 0.52, DECK_Y + 0.045, 2.80), steel, hull)
                # Deck side grab rails along the rear hull flank
                rod(m, "RearGrabRail", (sign * 1.12, DECK_Y + 0.08, 1.82), (sign * 1.12, DECK_Y + 0.08, 2.74), 0.014, steel, hull, sides=6)
                for rz in (1.86, 2.30, 2.70):
                    rod(m, "RearGrabPost", (sign * 1.12, DECK_Y, rz), (sign * 1.12, DECK_Y + 0.08, rz), 0.012, steel, hull, sides=6)
        m.add_cylinder("RearVentDome", 0.14, 0.07, (0.0, DECK_Y + 0.035, 2.48), paint, "y", 12, parent=hull)

    # Hull rear radio whip antenna
    if lod < 2:
        ax, az = -1.04, 2.74
        m.add_cylinder("HullAntennaBase", 0.055, 0.10, (ax, DECK_Y + 0.05, az), steel, "y", 8, parent=hull)
        rod(m, "HullAntenna", (ax, DECK_Y + 0.09, az), (ax - 0.04, DECK_Y + 1.95, az + 0.08), 0.008, steel, hull, sides=4)

    # Turret ring collar & stowed travel crutch on forward deck
    m.add_cylinder("TurretRingCollar", 0.64, 0.05, (TURRET_POS[0], DECK_Y + 0.025, TURRET_POS[2]), steel, "y", (28, 18, 12)[lod], parent=hull)
    if lod < 2:
        # Stowed folding travel lock frame on the rear-mid deck ahead of the turret
        m.add_box("TravelLockBase", (0.68, 0.06, 0.12), (0.0, DECK_Y + 0.03, -0.78), steel, hull)


# ----------------------------------------------------------------------------
# Running gear (MT-LB 6-wheel suspension, front sprocket, rear idler)
# ----------------------------------------------------------------------------


def road_wheel(m, sign, z, sides, mats, parent, lod):
    x, y, r = sign * TRACK_X_CENTER, ROAD_WHEEL_Y, ROAD_WHEEL_R
    if lod == 0:
        for offset in (-0.095, 0.095):
            m.add_cylinder("RoadWheelTire", r, 0.12, (x + offset, y, z), mats["rubber"], "x", sides, parent=parent, caps="none")
        m.add_cylinder("RoadWheelGap", r * 0.56, 0.07, (x, y, z), mats["steel"], "x", sides, parent=parent, caps="none")
        m.add_disc("RoadWheelInner", r, (x - sign * 0.035, y, z), mats["rubber"], "x", sides, parent, float(sign))
        m.add_disc("RoadWheelInner", r, (x + sign * 0.035, y, z), mats["rubber"], "x", sides, parent, float(-sign))
    else:
        m.add_cylinder("RoadWheelTire", r, 0.30, (x, y, z), mats["rubber"], "x", sides, parent=parent, caps="none")

    face_x = x + sign * 0.156
    m.add_disc("RoadWheelTireWall", r, (face_x, y, z), mats["rubber"], "x", sides, parent, float(sign))
    m.add_disc("RoadWheelFace", r * 0.82, (face_x + sign * 0.004, y, z), mats["paint"], "x", sides, parent, float(sign))
    if lod < 2:
        m.add_cylinder("RoadWheelHub", r * 0.28, 0.07, (face_x + sign * 0.032, y, z), mats["paint"], "x", max(8, sides // 2), parent=parent, caps="pos" if sign > 0 else "neg")
    if lod == 0:
        # MT-LB stamped 6-rib road wheel face
        m.add_cylinder("WheelRimLip", r * 0.82, 0.025, (face_x + sign * 0.010, y, z), mats["paint"], "x", 14, parent=parent, caps="none")
        for i in range(6):
            a = i * math.tau / 6
            radial = (0.0, math.sin(a), math.cos(a))
            tangential = (0.0, math.cos(a), -math.sin(a))
            mid = r * 0.54
            obox(m, "WheelRib", (face_x + sign * 0.012, y + radial[1] * mid, z + radial[2] * mid), ((1.0, 0.0, 0.0), radial, tangential), (0.010, r * 0.24, r * 0.055), mats["paint"], parent, tint=1.06)
            ha = (i + 0.5) * math.tau / 6
            m.add_disc("WheelDimple", r * 0.11, (face_x + sign * 0.007, y + r * 0.56 * math.sin(ha), z + r * 0.56 * math.cos(ha)), mats["cavity"], "x", 6, parent, float(sign))


def front_sprocket(m, sign, parent, mats, sides, lod):
    z, y, r = SPROCKET
    x = sign * TRACK_X_CENTER
    m.add_cylinder("SprocketHub", r * 0.58, 0.34, (x, y, z), mats["steel"], "x", sides, parent=parent)
    for offset in (-0.11, 0.11) if lod < 2 else ():
        m.add_cylinder("SprocketRing", r * 0.90, 0.045, (x + offset, y, z), mats["steel"], "x", sides, parent=parent)
        if lod == 0:
            teeth = 12
            for i in range(teeth):
                a = i * math.tau / teeth
                radial = (0.0, math.sin(a), math.cos(a))
                tangential = (0.0, math.cos(a), -math.sin(a))
                center = (x + offset, y + radial[1] * r * 0.97, z + radial[2] * r * 0.97)
                obox(m, "SprocketTooth", center, ((1.0, 0.0, 0.0), radial, tangential), (0.022, 0.042, 0.024), mats["steel"], parent)
    m.add_disc("SprocketCap", r * 0.58, (x + sign * 0.175, y, z), mats["paint"], "x", sides, parent, float(sign))


def rear_idler(m, sign, parent, mats, sides, lod):
    z, y, r = IDLER
    x = sign * TRACK_X_CENTER
    m.add_cylinder("IdlerWheel", r, 0.28, (x, y, z), mats["paint"], "x", sides, parent=parent, caps="none")
    m.add_disc("IdlerFace", r, (x + sign * 0.142, y, z), mats["paint"], "x", sides, parent, float(sign))
    if lod < 2:
        m.add_cylinder("IdlerHub", r * 0.32, 0.06, (x + sign * 0.165, y, z), mats["paint"], "x", 10, parent=parent, caps="pos" if sign > 0 else "neg")
    if lod == 0:
        for i in range(6):
            a = (i + 0.5) * math.tau / 6
            m.add_disc("IdlerHole", r * 0.15, (x + sign * 0.146, y + r * 0.58 * math.sin(a), z + r * 0.58 * math.cos(a)), mats["cavity"], "x", 6, parent, float(sign))


def build_running_gear(m, side, sign, mats, lod):
    sides = (16, 10, 6)[lod]
    wheels = [(z, ROAD_WHEEL_Y, ROAD_WHEEL_R) for z in ROAD_WHEEL_Z]
    # Note: vehicle_kit.track_path expects front end-wheel as 3rd arg ("idler") and rear end-wheel as 4th ("sprocket").
    # MT-LB is front-drive, so pass SPROCKET (front) then IDLER (rear).
    path = track_path(wheels, wheels, SPROCKET, IDLER, (10, 6, 4)[lod], sag=0.024 if lod < 2 else 0.0)
    if lod == 0:
        track_links(
            m,
            sign,
            side,
            path,
            TRACK_X_INNER,
            TRACK_X_OUTER,
            TRACK_THICKNESS,
            0.125,
            mats["track"],
            pad_mat=mats["track"],
            pin_mat=mats["steel"],
            pad=(TRACK_PAD_HEIGHT, 0.34),
            horn=(0.08, 0.055),
        )
    else:
        # Match LOD0 outer grouser footprint height cleanly so LOD switches never shift ground contact
        track_belt(m, sign, side, mats["track"], path, TRACK_X_INNER, TRACK_X_OUTER, TRACK_THICKNESS + TRACK_PAD_HEIGHT)

    for z in ROAD_WHEEL_Z:
        first = len(m.parts)
        road_wheel(m, sign, z, sides, mats, side, lod)
        tag_wheel(m, first, (ROAD_WHEEL_Y, z), ROAD_WHEEL_R)

    first = len(m.parts)
    front_sprocket(m, sign, side, mats, sides, lod)
    tag_wheel(m, first, (SPROCKET[1], SPROCKET[0]), SPROCKET[2])

    first = len(m.parts)
    rear_idler(m, sign, side, mats, sides, lod)
    tag_wheel(m, first, (IDLER[1], IDLER[0]), IDLER[2])


# ----------------------------------------------------------------------------
# 9A35M Turret cabin (TurretYaw) and elevating 4-container SAM launcher (GunPitch)
# ----------------------------------------------------------------------------


def build_turret(m, turret, mats, lod):
    paint, steel, glass = mats["paint"], mats["steel"], mats["glass"]

    # Turntable pedestal base sitting on the hull turret ring
    m.add_cylinder("TurntableBase", 0.58, 0.14, (0.0, 0.07, 0.0), steel, "y", (24, 16, 10)[lod], parent=turret)

    # Signature 9A35M central armored operator's booth with sloped armored glass front & side windows
    loft_sections(
        m,
        "OperatorCabin",
        [
            (-0.62, hex_section(0.40, 0.12, 0.44, 0.56, 0.04)),
            (-0.34, hex_section(0.44, 0.12, 0.44, CABIN_HEIGHT, 0.05)),
            (0.48, hex_section(0.44, 0.12, 0.43, CABIN_HEIGHT, 0.05)),
            (0.68, hex_section(0.40, 0.18, 0.38, CABIN_HEIGHT - 0.06, 0.04)),
        ],
        paint,
        turret,
        tint=0.98,
    )

    # Large sloped armored front observation windscreen on operator cabin (-0.62, 0.56 -> -0.34, 1.02)
    f_dz = -0.34 - (-0.62)
    f_dy = CABIN_HEIGHT - 0.56
    f_len = math.hypot(f_dz, f_dy)
    f_along = (0.0, f_dy / f_len, f_dz / f_len)
    f_norm = (0.0, f_dz / f_len, -f_dy / f_len)
    fw_c = (0.0, 0.785, -0.485)
    obox(m, "OperatorFrontFrame", fw_c, ((1.0, 0.0, 0.0), f_norm, f_along), (0.34, 0.025, 0.21), steel, turret)
    obox(m, "OperatorFrontGlass", (fw_c[0], fw_c[1] + f_norm[1] * 0.018, fw_c[2] + f_norm[2] * 0.018), ((1.0, 0.0, 0.0), f_norm, f_along), (0.29, 0.010, 0.17), glass, turret)

    # Side operator observation windows (below the trunnion level so operator has wide sky visibility)
    if lod < 2:
        for sign in (-1, 1):
            m.add_box("OperatorSideFrame", (0.03, 0.26, 0.38), (sign * 0.445, 0.64, -0.14), steel, turret)
            m.add_box("OperatorSideGlass", (0.015, 0.20, 0.32), (sign * 0.456, 0.64, -0.14), glass, turret)

    # Operator roof hatch and rear electronics/cooling bustle
    domed_hatch(m, "OperatorHatch", turret, (0.0, CABIN_HEIGHT, 0.10), 0.24, paint, 12 if lod < 2 else 8, rise=0.04, hinge_mat=steel if lod < 2 else None, hinge_dir=(0.0, 0.0, 1.0))
    m.add_box("TurretBustle", (0.68, 0.46, 0.34), (0.0, 0.58, 0.74), paint, turret, tint=0.90)
    if lod == 0:
        m.add_box("BustleVent", (0.44, 0.18, 0.03), (0.0, 0.62, 0.915), mats["cavity"], turret)
        rod(m, "TurretAntenna", (0.26, 0.82, 0.80), (0.28, 1.95, 0.88), 0.007, steel, turret, sides=4)

    # Heavy external elevation trunnion housings on both sides of the upper operator cabin
    tx_y, tx_z = TRUNNION_LOCAL[1], TRUNNION_LOCAL[2]
    for sign in (-1, 1):
        m.add_box("TrunnionBracket", (0.14, 0.24, 0.30), (sign * 0.49, tx_y - 0.04, tx_z), steel, turret)
        m.add_cylinder("TrunnionBearing", 0.10, 0.16, (sign * 0.56, tx_y, tx_z), steel, "x", (14, 10, 6)[lod], parent=turret)


def build_launcher_cradle(m, pitch, mats, lod):
    paint, pod, radome, steel, cavity = mats["paint"], mats["pod"], mats["radome"], mats["steel"], mats["cavity"]

    # Main horizontal tubular crossbeam yoke linking all 4 missile containers through the side trunnions
    m.add_cylinder("ElevationYoke", 0.070, 2.38, (0.0, 0.0, 0.0), steel, "x", (14, 10, 6)[lod], parent=pitch)
    # High arch bridge clearing the operator cabin roof cleanly over the entire -5..+80 degree elevation range
    m.add_box("UpperCrossbar", (2.28, 0.06, 0.09), (0.0, 0.28, -0.06), steel, pitch)
    for sign in (-1, 1):
        m.add_box("ArchSidePost", (0.07, 0.28, 0.09), (sign * 0.58, 0.14, -0.06), steel, pitch)

    # Side cradle rails supporting each twin-container pair cleanly underneath
    for sign in (-1, 1):
        m.add_box("CradleSideArm", (0.68, 0.055, 0.96), (sign * 0.935, -0.172, -0.34), steel, pitch)
        if lod < 2:
            rod(m, "CradleStrut", (sign * 0.62, 0.0, 0.0), (sign * 0.62, -0.17, -0.68), 0.024, steel, pitch, sides=6)
            rod(m, "CradleStrut", (sign * 1.24, 0.0, 0.0), (sign * 1.24, -0.17, -0.68), 0.024, steel, pitch, sides=6)

    # Four iconic rectangular 9M37 missile launch containers
    mid_z = (CONTAINER_FRONT_Z + CONTAINER_REAR_Z) / 2.0
    for idx, (_launch_name, cx, cy) in enumerate(CONTAINER_SLOTS):
        box_tint = 0.96 if idx % 2 == 0 else 1.02
        loft_sections(
            m,
            "MissileContainer",
            [
                (CONTAINER_FRONT_Z, hex_section(CONTAINER_WIDTH / 2.0, cy - CONTAINER_HEIGHT / 2.0, CONTAINER_WIDTH / 2.0, cy + CONTAINER_HEIGHT / 2.0, 0.025)),
                (CONTAINER_REAR_Z, hex_section(CONTAINER_WIDTH / 2.0, cy - CONTAINER_HEIGHT / 2.0, CONTAINER_WIDTH / 2.0, cy + CONTAINER_HEIGHT / 2.0, 0.025)),
            ],
            pod,
            pitch,
            tint=box_tint,
        )
        node_idx = m.parts[-1][0]
        m.data["nodes"][node_idx]["translation"] = [cx, 0.0, 0.0]

        # Three steel clamping bands / structural ribs along each container
        if lod < 2:
            for bz in (CONTAINER_FRONT_Z + 0.24, mid_z, CONTAINER_REAR_Z - 0.24):
                m.add_box("ContainerBand", (CONTAINER_WIDTH + 0.026, CONTAINER_HEIGHT + 0.026, 0.065), (cx, cy, bz), steel, pitch)

        # Front blast-cap bezel frame + recessed dark launch aperture + 9M37 seeker nose visible inside on LOD0
        m.add_box("ContainerFrontFrame", (CONTAINER_WIDTH + 0.02, CONTAINER_HEIGHT + 0.02, 0.05), (cx, cy, CONTAINER_FRONT_Z + 0.015), steel, pitch)
        m.add_box("ContainerFrontCavity", (CONTAINER_WIDTH - 0.06, CONTAINER_HEIGHT - 0.06, 0.02), (cx, cy, CONTAINER_FRONT_Z - 0.008), cavity, pitch)
        if lod == 0:
            tube(
                m,
                "MissileSeekerNose",
                [(-0.02, 0.028), (0.04, 0.056), (0.10, 0.060)],
                10,
                mats["radome"],
                pitch,
                pos=(cx, cy, CONTAINER_FRONT_Z + 0.02),
                cap_back=False,
            )

        # Rear dark exhaust collar & blast exit opening
        m.add_box("ContainerRearFrame", (CONTAINER_WIDTH + 0.02, CONTAINER_HEIGHT + 0.02, 0.05), (cx, cy, CONTAINER_REAR_Z - 0.015), steel, pitch)
        m.add_box("ContainerRearCavity", (CONTAINER_WIDTH - 0.06, CONTAINER_HEIGHT - 0.06, 0.02), (cx, cy, CONTAINER_REAR_Z + 0.008), cavity, pitch)

    # Central top-mounted 9S86 passive ranging radar housing ("Snap Shot") pitched cleanly above the cabin roof
    rz = -0.10
    ry = 0.46
    m.add_box("RangeRadarStalk", (0.20, 0.14, 0.20), (0.0, 0.34, rz), steel, pitch)
    m.add_box("RangeRadarHousing", (0.38, 0.26, 0.48), (0.0, ry, rz), paint, pitch, tint=0.92)
    m.add_box("RangeRadarRadome", (0.32, 0.20, 0.045), (0.0, ry, rz - 0.245), radome, pitch)
    if lod == 0:
        m.add_box("RangeRadarVisor", (0.40, 0.025, 0.12), (0.0, ry + 0.135, rz - 0.20), steel, pitch)


# ----------------------------------------------------------------------------
# Soviet tactical markings (red star + tactical number on hull sides)
# ----------------------------------------------------------------------------


def build_markings(m, hull):
    atlas = MarkingAtlas(512, 256)
    atlas.star((0.0, 0.0, 0.5, 1.0), fill=(178, 34, 30, 255), outline=(232, 228, 214, 255), outline_width=0.07)
    atlas.text((0.5, 0.14, 1.0, 0.86), "418", fill=(232, 228, 214, 255))
    material = decal_material(m, atlas, "Strela10_Markings")

    # Side upper hull armor slopes gently inward from x = 1.38 at y = 1.22 to x = 1.24 at y = 1.865
    dx = 1.24 - 1.38
    dy = DECK_Y - (CHINE_Y + 0.08)
    slope_len = math.hypot(dx, dy)
    for sign in (-1, 1):
        normal = (sign * dy / slope_len, -dx / slope_len, 0.0)
        y_mid = 1.54
        t = (y_mid - (CHINE_Y + 0.08)) / dy
        x_side = sign * (1.38 + dx * t)
        add_decal(m, "MarkingStar", hull, (x_side, y_mid, -0.78), normal, (0.0, 1.0, 0.0), (0.34, 0.34), (0.0, 0.0, 0.5, 1.0), material)
        add_decal(m, "MarkingNumber", hull, (x_side, y_mid, -0.14), normal, (0.0, 1.0, 0.0), (0.60, 0.32), (0.5, 0.0, 1.0, 1.0), material)


# ----------------------------------------------------------------------------
# Assembly, measurement, and manifest export
# ----------------------------------------------------------------------------


def build_strela10_lod(lod=0, output_dir=None):
    m = GltfModel(f"Strela10_LOD{lod}")
    m.data["asset"]["generator"] = "Dead Ground 9K35 Strela-10 model authoring"
    mats = materials(m)

    hull = m.node("Hull")
    left = m.node("LeftTrack")
    right = m.node("RightTrack")
    turret = m.node("TurretYaw", TURRET_POS)
    pitch = m.node("GunPitch", TRUNNION_LOCAL, turret)
    m.node("Muzzle", (0.0, 0.0, LAUNCH_Z), pitch)
    for launch_name, cx, cy in CONTAINER_SLOTS:
        m.node(launch_name, (cx, cy, LAUNCH_Z), pitch)

    build_hull(m, hull, mats, lod)
    build_running_gear(m, left, -1, mats, lod)
    build_running_gear(m, right, 1, mats, lod)
    build_turret(m, turret, mats, lod)
    build_launcher_cradle(m, pitch, mats, lod)

    bake_vertex_shading(m, gear_nodes=("LeftTrack", "RightTrack"), max_edge=(0.35, 0.70, None)[lod])
    emit_motion_parts(m)
    if lod < 2:
        build_markings(m, hull)

    out_dir = Path(output_dir) if output_dir else DEFAULT_OUTPUT_DIR
    out_dir.mkdir(parents=True, exist_ok=True)
    name = "strela10.glb" if lod == 0 else f"strela10_lod{lod}.glb"
    stats = m.save(out_dir / name)
    stats["lod"] = lod
    stats["triangle_budget"] = LOD_BUDGETS[lod]
    if getattr(m, "track_pattern_cycle", None):
        stats["track_pattern_cycle"] = m.track_pattern_cycle
    stats["role"] = ("close", "rts", "distant")[lod]
    return stats


def read_glb(path):
    blob = Path(path).read_bytes()
    json_len = int.from_bytes(blob[12:16], "little")
    data = json.loads(blob[20 : 20 + json_len])
    offset = 20 + json_len
    bin_len = int.from_bytes(blob[offset : offset + 4], "little")
    return data, blob[offset + 8 : offset + 8 + bin_len]


def accessor_array(data, binary, index):
    accessor = data["accessors"][index]
    view = data["bufferViews"][accessor["bufferView"]]
    width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[accessor["type"]]
    dtype = "<f4" if accessor["componentType"] == 5126 else "<u4"
    raw = np.frombuffer(binary[view["byteOffset"] : view["byteOffset"] + view["byteLength"]], dtype=dtype)
    return raw.reshape(-1, width) if width > 1 else raw


def node_world_positions(data):
    nodes = data["nodes"]
    parent = {}
    for i, node in enumerate(nodes):
        for child in node.get("children", []):
            parent[child] = i
    world = {}
    for i in range(len(nodes)):
        pos = np.zeros(3)
        cursor = i
        while cursor is not None:
            pos += np.asarray(nodes[cursor].get("translation", [0.0, 0.0, 0.0]))
            cursor = parent.get(cursor)
        world[i] = pos
    return world, parent


def measure_glb(path):
    data, binary = read_glb(path)
    world, _ = node_world_positions(data)
    mins, maxs = [], []
    for i, node in enumerate(data["nodes"]):
        if "mesh" not in node:
            continue
        for primitive in data["meshes"][node["mesh"]]["primitives"]:
            pts = accessor_array(data, binary, primitive["attributes"]["POSITION"]) + world[i]
            mins.append(pts.min(axis=0))
            maxs.append(pts.max(axis=0))
    lo, hi = np.min(mins, axis=0), np.max(maxs, axis=0)
    return {"min": [round(float(v), 4) for v in lo], "max": [round(float(v), 4) for v in hi]}


def generate_all(output_dir=None):
    out_dir = Path(output_dir) if output_dir else DEFAULT_OUTPUT_DIR
    out_dir.mkdir(parents=True, exist_ok=True)
    lods = [build_strela10_lod(lod, out_dir) for lod in range(3)]
    bounds = measure_glb(out_dir / "strela10.glb")
    lo, hi = bounds["min"], bounds["max"]
    trunnion_world = [round(TURRET_POS[i] + TRUNNION_LOCAL[i], 4) for i in range(3)]

    launch_points = [
        {"name": name, "local_position": [round(cx, 4), round(cy, 4), round(LAUNCH_Z, 4)]}
        for name, cx, cy in CONTAINER_SLOTS
    ]

    manifest = {
        "asset": "9K35 Strela-10SV (9A35M / SA-13 Gopher) tracked IR SAM launcher on MT-LB chassis",
        "configuration": "Combat-ready raised turret configuration with glazed central operator cabin, 4 rectangular 9M37 launch containers on side trunnions, center 9S86 ranging radar housing, and 9S16 passive RWR pods",
        "units": "meters",
        "up": "+Y",
        "forward": "-Z",
        "right": "+X",
        "ground_y": 0.0,
        "node_scale": "1 on every node; no node rotation in the rest pose",
        "color_authoring": "sRGB hex converted to linear glTF factors",
        "vertex_colors": "rgb: tint x baked ambient occlusion, a: dirt mask",
        "sources": SOURCES,
        "reference_anchors": {
            "overall_length_meters": REFERENCE_LENGTH,
            "overall_width_meters": REFERENCE_WIDTH,
            "hull_roof_height_meters": REFERENCE_HULL_HEIGHT,
            "ground_clearance_meters": REFERENCE_CLEARANCE,
            "track_gauge_meters": TRACK_GAUGE,
            "track_width_meters": TRACK_WIDTH,
            "road_wheels_per_side": len(ROAD_WHEEL_Z),
            "container_length_meters": CONTAINER_LENGTH,
            "missile_count": len(CONTAINER_SLOTS),
        },
        "measured": {
            "bounds_min": lo,
            "bounds_max": hi,
            "overall_length_meters": round(hi[2] - lo[2], 4),
            "overall_width_meters": round(hi[0] - lo[0], 4),
            "combat_rest_height_meters": round(hi[1], 4),
            "lowest_point_meters": round(lo[1], 4),
        },
        "rig": {
            "TurretYaw": {
                "role": "launcher turret ring",
                "position": list(TURRET_POS),
                "axis": "+Y",
                "rotation_y_range_degrees": [-180.0, 180.0],
                "continuous_traverse": True,
                "note": "positive rotation about +Y turns the turret to the left",
            },
            "GunPitch": {
                "role": "missile container and 9S86 radar elevation trunnions",
                "parent": "TurretYaw",
                "local_position": list(TRUNNION_LOCAL),
                "rest_world_position": trunnion_world,
                "axis": "+X",
                "rotation_x_range_degrees": [ELEVATION_MIN_DEG, ELEVATION_MAX_DEG],
                "note": "positive rotation about +X raises the container fronts skyward; clears hull roof over full 360-degree traverse",
            },
            "Muzzle": {
                "parent": "GunPitch",
                "role": "generic effect anchor at launcher front center",
                "local_position": [0.0, 0.0, round(LAUNCH_Z, 4)],
            },
            "launch_points": {
                "parent": "GunPitch",
                "count": len(CONTAINER_SLOTS),
                "bore_axis": "-Z of GunPitch",
                "points": launch_points,
                "firing_order": [name for name, _, _ in CONTAINER_SLOTS],
            },
            "tracks": {
                "left_parent": "LeftTrack",
                "right_parent": "RightTrack",
                "motion_mesh_pattern": "*_Motion_*",
            },
        },
        "lods": lods,
        "track_gauge_meters": TRACK_GAUGE,
        "track_wrap_meters": track_wrap_period(lods[0]["track_pattern_cycle"], ROAD_WHEEL_R),
        "simplifications": [
            "Modeled in raised combat-ready turret pose; folding travel stowage animation is omitted.",
            "Small external latches, spare track links, and periscope guards are simplified for RTS readability.",
            "No reload missile crane or internal hull interior.",
        ],
        "unsupported_motions": [
            "Suspension travel and folding turret pedestal stowage.",
            "Door and crew hatch opening.",
            "No BarrelRecoil node: missile launch uses rocket exhaust/backblast rather than barrel recoil.",
        ],
    }
    (out_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=None)
    args = parser.parse_args()
    result = generate_all(args.output_dir)
    for item in result["lods"]:
        print(f"{item['file']}: {item['triangles']} triangles (budget {item['triangle_budget']}), {item['mesh_parts']} parts, {item['bytes']} bytes")
    print("measured:", json.dumps(result["measured"]))
