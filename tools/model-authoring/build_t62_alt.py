"""Alternative T-62 study for Dead Ground, built with vehicle_kit, in meters, with three LODs.

Independent of build_t62.py. Same conventions: meters, +Y up, -Z forward, root centered
on the hull footprint at ground level, and the TurretYaw / GunPitch / BarrelRecoil / Muzzle
hierarchy. Vertex colors carry baked ambient occlusion and a dirt mask for the shared
weathering shader (Assets/Vehicles/Shared/vehicle_weathering.gdshader).
"""

import argparse
import json
import math
from pathlib import Path

from vehicle_kit import (
    GltfModel,
    MarkingAtlas,
    add_conformal_decal,
    bake_vertex_shading,
    decal_material,
    domed_hatch,
    hex_section,
    loft_sections,
    obox,
    revolve,
    rod,
    emit_motion_parts,
    surface_heights,
    surface_hit,
    track_belt,
    tag_wheel,
    track_wrap_period,
    track_links,
    track_path,
    tube,
)

OUTPUT_DIR = Path(__file__).resolve().parents[2] / "src/Game.Godot/Assets/Vehicles/T62Alt"

# Public T-62 figures used as anchors. Positions below were measured from the
# 1:35 drawings by Viktor Malginov (side and front views); small fittings are fitted to photos.
HULL_LENGTH = 6.630
TOTAL_LENGTH_GUN_FWD = 9.335
WIDTH_OVER_FENDERS = 3.300
HEIGHT_TO_CUPOLA = 2.395
GROUND_CLEARANCE = 0.430

HULL_FRONT_Z, HULL_REAR_Z = -HULL_LENGTH / 2, HULL_LENGTH / 2
DECK_Y = 1.56
NOSE_Y = 0.97
GLACIS_TOP_Z = -2.31
LOWER_PLATE_Z = -2.55
# The dome rides 3 cm above the roof on its ring; the dark collar shows the seam.
TURRET_RING = (0.0, DECK_Y + 0.03, -0.07)
# Cast dome, round in plan: (radius, height above the deck), bottom to top.
TURRET_PROFILE = [
    (1.310, 0.000),
    (1.305, 0.080),
    (1.290, 0.160),
    (1.265, 0.240),
    (1.230, 0.310),
    (1.180, 0.390),
    (1.110, 0.470),
    (1.020, 0.535),
    (0.900, 0.590),
    (0.750, 0.635),
    (0.550, 0.675),
    (0.300, 0.710),
    (0.000, 0.730),
]
TURRET_SURFACE = {"TurretDome"}
GUN_TRUNNION_LOCAL = (0.0, 0.21, -0.75)
MUZZLE_Z = HULL_REAR_Z - TOTAL_LENGTH_GUN_FWD
TRUNNION_Z = TURRET_RING[2] + GUN_TRUNNION_LOCAL[2]
MUZZLE_LOCAL_Z = MUZZLE_Z - TRUNNION_Z
MANTLET_FRONT_Z = -1.98

TRACK_X_OUTER = WIDTH_OVER_FENDERS / 2
TRACK_X_INNER = TRACK_X_OUTER - 0.58
TRACK_X_CENTER = (TRACK_X_INNER + TRACK_X_OUTER) / 2
TRACK_THICKNESS = 0.085
ROAD_WHEEL_Z = [-1.98, -1.02, -0.05, 1.06, 2.18]
ROAD_WHEEL_Y, ROAD_WHEEL_R = 0.49, 0.405
# Hub between the two tire halves; the OMSh guide horns run under it.
ROAD_WHEEL_GAP_R = 0.22
IDLER = (-2.89, 0.91, 0.28)
SPROCKET = (2.88, 0.79, 0.33)
FENDER_Y = 1.32
# Fender boxes stop below the roof so the turret overhang clears them, as on the drawing.
FENDER_BOX_H = DECK_Y - 0.02 - (FENDER_Y + 0.015)


def glacis_y(z):
    return NOSE_Y + (z - HULL_FRONT_Z) * (DECK_Y - NOSE_Y) / (GLACIS_TOP_Z - HULL_FRONT_Z)


def turret_radius(y):
    """Dome radius at height y above the deck, from the measured profile."""
    for (r0, y0), (r1, y1) in zip(TURRET_PROFILE, TURRET_PROFILE[1:]):
        if y <= y1:
            t = (y - y0) / (y1 - y0)
            return r0 + (r1 - r0) * t
    return 0.0


def turret_side_point(z, y, sign):
    """Point and outward normal on the dome side at turret-local z and height y."""
    r = turret_radius(y)
    x = sign * math.sqrt(max(0.0, r * r - z * z))
    dr = (turret_radius(y + 0.01) - turret_radius(y - 0.01)) / 0.02
    nr, ny = 1.0, -dr
    length = math.hypot(nr, ny)
    return (x, y, z), (nr / length * x / r, ny / length, nr / length * z / r)


def materials(m):
    return {
        "paint": m.material("T62A_Paint", "626342", metal=0.08, rough=0.84),
        "rubber": m.material("T62A_Rubber", "2f2f2b", rough=0.96),
        "track": m.material("T62A_TrackSteel", "3f3e37", metal=0.40, rough=0.78),
        "steel": m.material("T62A_DarkSteel", "34372f", metal=0.30, rough=0.70),
        "glass": m.material("T62A_Optics", "26343a", metal=0.15, rough=0.22),
        "canvas": m.material("T62A_Canvas", "5c5840", rough=0.96),
        "cavity": m.material("T62A_Cavity", "161712", rough=1.0),
        "soot": m.material("T62A_Soot", "23221d", rough=1.0),
    }


# ----------------------------------------------------------------------------
# Hull
# ----------------------------------------------------------------------------


def build_hull(m, hull, mats, lod):
    paint, steel = mats["paint"], mats["steel"]
    loft_sections(
        m,
        "HullBody",
        [
            (HULL_FRONT_Z, hex_section(1.00, NOSE_Y - 0.03, 1.02, NOSE_Y + 0.02, 0.01)),
            (LOWER_PLATE_Z, hex_section(1.05, GROUND_CLEARANCE, 1.10, glacis_y(LOWER_PLATE_Z), 0.04)),
            (GLACIS_TOP_Z, hex_section(1.05, GROUND_CLEARANCE, 1.12, DECK_Y, 0.05)),
            (3.05, hex_section(1.05, GROUND_CLEARANCE, 1.12, DECK_Y, 0.05)),
            (HULL_REAR_Z, hex_section(1.05, 0.62, 1.12, 1.48, 0.05)),
        ],
        paint,
        hull,
        tint=0.93,
    )
    # Straight transverse wave deflector standing on the upper glacis.
    slope = math.atan2(DECK_Y - NOSE_Y, GLACIS_TOP_Z - HULL_FRONT_Z)
    along = (0.0, math.sin(slope), math.cos(slope))
    up = (0.0, math.cos(slope), -math.sin(slope))
    z = -2.85
    base = (0.0, glacis_y(z), z)
    center = tuple(base[k] + up[k] * 0.055 for k in range(3))
    obox(m, "WaveDeflector", center, ((1.0, 0.0, 0.0), up, along), (0.96, 0.065, 0.012), paint, hull, tint=0.86)

    for sign in (-1, 1):
        m.add_box("Fender", (0.60, 0.03, 6.18), (sign * TRACK_X_CENTER, FENDER_Y, 0.11), paint, hull, tint=0.84)
        for z0, z1, y1 in [(-2.98, -3.34, 1.07), (3.20, 3.56, 1.16)]:
            mid = ((z0 + z1) / 2, (FENDER_Y + y1) / 2)
            length = math.hypot(z1 - z0, y1 - FENDER_Y)
            tangent = (0.0, (y1 - FENDER_Y) / length, (z1 - z0) / length)
            normal = (0.0, -tangent[2], tangent[1])
            obox(m, "FenderGuard", (sign * TRACK_X_CENTER, mid[1], mid[0]), ((1, 0, 0), normal, tangent), (0.30, 0.015, length / 2), paint, hull, tint=0.84)
        # Headlight clusters with brush guards on the front fenders.
        m.add_box("HeadlightHousing", (0.20, 0.16, 0.16), (sign * 1.30, FENDER_Y + 0.10, -2.86), steel, hull)
        if lod < 2:
            m.add_disc("HeadlightLens", 0.06, (sign * 1.30, FENDER_Y + 0.11, -2.941), mats["glass"], "z", 10, hull, -1.0)
        if lod == 0:
            rod(m, "HeadlightGuard", (sign * 1.18, FENDER_Y, -2.80), (sign * 1.18, FENDER_Y + 0.24, -3.00), 0.012, steel, hull)
            rod(m, "HeadlightGuard", (sign * 1.42, FENDER_Y, -2.80), (sign * 1.42, FENDER_Y + 0.24, -3.00), 0.012, steel, hull)
            rod(m, "HeadlightGuard", (sign * 1.18, FENDER_Y + 0.24, -3.00), (sign * 1.42, FENDER_Y + 0.24, -3.00), 0.012, steel, hull)
        m.add_box("TowHook", (0.10, 0.12, 0.14), (sign * 0.62, 0.74, HULL_FRONT_Z - 0.05), steel, hull)
        m.add_box("TailLight", (0.14, 0.10, 0.06), (sign * 0.95, 1.30, HULL_REAR_Z + 0.03), steel, hull)

    # Right fender: three exposed fuel cells, then a stowage box. Left: spares boxes and exhaust.
    for z, length in ([(-1.90, 0.95), (-0.86, 0.95), (0.18, 0.95)] if lod < 2 else [(-0.86, 3.0)]):
        m.add_box("FuelCell", (0.48, FENDER_BOX_H, length), (TRACK_X_CENTER, FENDER_Y + 0.015 + FENDER_BOX_H / 2, z), paint, hull, tint=0.95)
        if lod == 0:
            m.add_cylinder("FuelCap", 0.05, 0.04, (TRACK_X_CENTER, FENDER_Y + 0.015 + FENDER_BOX_H + 0.02, z - length * 0.3), steel, "y", 8, parent=hull)
    m.add_box("StowageBox", (0.48, FENDER_BOX_H, 0.80), (TRACK_X_CENTER, FENDER_Y + 0.015 + FENDER_BOX_H / 2, 1.35), paint, hull, tint=0.82)
    for z, length in ([(-1.95, 0.85), (-0.85, 0.85), (0.60, 0.70)] if lod < 2 else [(-1.0, 2.4)]):
        m.add_box("SparesBox", (0.48, FENDER_BOX_H, length), (-TRACK_X_CENTER, FENDER_Y + 0.015 + FENDER_BOX_H / 2, z), paint, hull, tint=0.80)
    # Exhaust outlet on the left fender, with soot blown out over the fender behind it.
    m.add_box("Exhaust", (0.36, 0.14, 0.42), (-TRACK_X_CENTER, FENDER_Y + 0.085, 2.40), steel, hull)
    if lod < 2:
        m.add_box("ExhaustGrille", (0.01, 0.10, 0.36), (-TRACK_X_CENTER - 0.18, FENDER_Y + 0.085, 2.40), mats["cavity"], hull)
        m.add_box("ExhaustSoot", (0.56, 0.004, 0.90), (-TRACK_X_CENTER, FENDER_Y + 0.017, 2.95), mats["soot"], hull)

    m.add_cylinder("TurretRingCollar", 1.24, 0.05, (0.0, DECK_Y + 0.02, TURRET_RING[2]), steel, "y", (32, 20, 12)[lod], parent=hull)
    # Driver hatch at front left of the deck.
    m.add_cylinder("DriverHatch", 0.27, 0.05, (-0.55, DECK_Y + 0.025, -1.98), paint, "y", 14 if lod < 2 else 8, parent=hull)
    if lod < 2:
        m.add_box("DriverPeriscope", (0.22, 0.06, 0.08), (-0.55, DECK_Y + 0.05, -2.26), mats["glass"], hull)
    # Engine deck: transverse louvers and the radiator grille.
    m.add_box("EngineLouvers", (1.70, 0.03, 1.10), (0.0, DECK_Y + 0.015, 2.35), steel, hull)
    if lod == 0:
        for i in range(8):
            m.add_box("LouverSlat", (1.62, 0.03, 0.05), (0.0, DECK_Y + 0.04, 1.86 + i * 0.14), paint, hull, tint=0.84)
    # Radiator air outlet across the rear of the deck: dark mesh under a frame.
    m.add_box("RadiatorOutlet", (2.00, 0.02, 0.12), (0.0, DECK_Y + 0.01, 2.97), mats["cavity"], hull)
    if lod == 0:
        for i in range(12):
            m.add_box("OutletBar", (0.03, 0.03, 0.12), (-0.94 + i * 0.171, DECK_Y + 0.025, 2.97), steel, hull)
        m.add_box("OutletFrame", (2.06, 0.04, 0.03), (0.0, DECK_Y + 0.02, 3.045), paint, hull, tint=0.9)
    # Rolled tarpaulin strapped across the deck behind the turret.
    m.add_cylinder("RolledTarp", 0.15, 1.30, (0.0, DECK_Y + 0.15, 1.52), mats["canvas"], "x", (12, 8, 6)[lod], parent=hull)

    # Two jettisonable fuel drums on brackets behind the rear plate.
    drum_sides = (16, 12, 8)[lod]
    for sign in (-1, 1):
        m.add_cylinder("RearDrum", 0.29, 0.80, (sign * 0.52, 1.42, HULL_REAR_Z + 0.34), paint, "x", drum_sides, parent=hull)
        if lod < 2:
            for offset in (-0.22, 0.22):
                m.add_cylinder("DrumRib", 0.30, 0.04, (sign * 0.52 + offset, 1.42, HULL_REAR_Z + 0.34), steel, "x", drum_sides, parent=hull)
        m.add_box("DrumBracket", (0.08, 0.30, 0.34), (sign * 0.20, 1.30, HULL_REAR_Z + 0.16), steel, hull)
        m.add_box("DrumBracket", (0.08, 0.30, 0.34), (sign * 0.86, 1.30, HULL_REAR_Z + 0.16), steel, hull)


# ----------------------------------------------------------------------------
# Running gear
# ----------------------------------------------------------------------------


def road_wheel(m, sign, z, sides, mats, parent, lod):
    x, y, r = sign * TRACK_X_CENTER, ROAD_WHEEL_Y, ROAD_WHEEL_R
    if lod == 0:
        # Double wheel: two tires with a gap that the track guide horns run through.
        for offset in (-0.15, 0.15):
            m.add_cylinder("RoadWheelTire", r, 0.20, (x + offset, y, z), mats["rubber"], "x", sides, parent=parent, caps="none")
        m.add_cylinder("RoadWheelGap", ROAD_WHEEL_GAP_R, 0.10, (x, y, z), mats["steel"], "x", sides, parent=parent, caps="none")
        m.add_disc("RoadWheelInnerWall", r, (x - sign * 0.05, y, z), mats["rubber"], "x", sides, parent, float(sign))
        m.add_disc("RoadWheelInnerWall", r, (x + sign * 0.05, y, z), mats["rubber"], "x", sides, parent, float(-sign))
    else:
        m.add_cylinder("RoadWheelTire", r, 0.50, (x, y, z), mats["rubber"], "x", sides, parent=parent, caps="none")
    face_x = x + sign * 0.252
    m.add_disc("RoadWheelTireWall", r, (face_x, y, z), mats["rubber"], "x", sides, parent, float(sign))
    m.add_disc("RoadWheelFace", r * 0.84, (face_x + sign * 0.004, y, z), mats["paint"], "x", sides, parent, float(sign))
    if lod < 2:
        m.add_cylinder("RoadWheelHub", r * 0.26, 0.08, (face_x + sign * 0.04, y, z), mats["paint"], "x", max(8, sides // 2), parent=parent, caps="pos" if sign > 0 else "neg")
    if lod == 0:
        spider_face(m, sign, (face_x, y, z), r, 8, mats, parent)


def spider_face(m, sign, center, r, spokes, mats, parent):
    """Cast "starfish" disc: raised spokes with teardrop openings between them, a rim lip and hub bolts."""
    fx, y, z = center
    m.add_cylinder("WheelRimLip", r * 0.84, 0.03, (fx + sign * 0.012, y, z), mats["paint"], "x", 16, parent=parent, caps="none")
    for i in range(spokes):
        a = (i + 0.5) * math.tau / spokes
        # Teardrop: a big hole near the rim tapering toward the hub.
        for rad, size in ((0.64, 0.135), (0.50, 0.10), (0.38, 0.07)):
            m.add_disc("WheelOpening", r * size, (fx + sign * 0.007, y + r * rad * math.sin(a), z + r * rad * math.cos(a)), mats["cavity"], "x", 6, parent, float(sign))
        b = i * math.tau / spokes
        radial = (0.0, math.sin(b), math.cos(b))
        tangential = (0.0, math.cos(b), -math.sin(b))
        mid = r * 0.52
        obox(m, "WheelSpoke", (fx + sign * 0.014, y + radial[1] * mid, z + radial[2] * mid), ((1.0, 0.0, 0.0), radial, tangential), (0.012, r * 0.27, r * 0.05), mats["paint"], parent, tint=1.05)
    for i in range(6):
        a = i * math.tau / 6
        m.add_disc("HubBolt", 0.014, (fx + sign * 0.082, y + r * 0.15 * math.sin(a), z + r * 0.15 * math.cos(a)), mats["steel"], "x", 6, parent, float(sign))


def sprocket(m, sign, parent, mats, sides, lod):
    z, y, r = SPROCKET
    x = sign * TRACK_X_CENTER
    m.add_cylinder("SprocketHub", r * 0.55, 0.44, (x, y, z), mats["steel"], "x", sides, parent=parent)
    for offset in (-0.15, 0.15) if lod < 2 else ():
        m.add_cylinder("SprocketRing", r * 0.92, 0.06, (x + offset, y, z), mats["steel"], "x", sides, parent=parent)
        if lod == 0:
            teeth = 13
            for i in range(teeth):
                a = i * math.tau / teeth
                radial = (0.0, math.sin(a), math.cos(a))
                tangential = (0.0, math.cos(a), -math.sin(a))
                center = (x + offset, y + radial[1] * r * 0.97, z + radial[2] * r * 0.97)
                obox(m, "SprocketTooth", center, ((1.0, 0.0, 0.0), radial, tangential), (0.03, 0.05, 0.03), mats["steel"], parent)
    m.add_disc("SprocketFace", r * 0.55, (x + sign * 0.225, y, z), mats["steel"], "x", sides, parent, float(sign))


def build_running_gear(m, side, sign, mats, lod):
    sides = (16, 12, 8)[lod]
    wheels = [(z, ROAD_WHEEL_Y, ROAD_WHEEL_R) for z in ROAD_WHEEL_Z]
    # No return rollers: the top run rests on the road wheels and sags between them.
    path = track_path(wheels, wheels, IDLER, SPROCKET, (10, 7, 5)[lod], sag=0.03 if lod < 2 else 0.0)
    if lod == 0:
        # OMSh single-pin track: 0.137 m pitch, grousers, pin bosses at the edge, a guide horn on every other link.
        track_links(m, sign, side, path, TRACK_X_INNER, TRACK_X_OUTER, 0.05, 0.137, mats["track"], pad_mat=mats["track"], pin_mat=mats["steel"], pad=(0.032, 0.32), horn=(0.10, 0.07))
    else:
        track_belt(m, sign, side, mats["track"], path, TRACK_X_INNER, TRACK_X_OUTER, TRACK_THICKNESS)
    for z in ROAD_WHEEL_Z:
        first = len(m.parts)
        road_wheel(m, sign, z, sides, mats, side, lod)
        tag_wheel(m, first, (ROAD_WHEEL_Y, z), ROAD_WHEEL_R)
    iz, iy, ir = IDLER
    first = len(m.parts)
    if lod == 0:
        for offset in (-0.145, 0.145):
            m.add_cylinder("Idler", ir, 0.17, (sign * TRACK_X_CENTER + offset, iy, iz), mats["paint"], "x", sides, parent=side)
        m.add_cylinder("IdlerGap", ir * 0.5, 0.12, (sign * TRACK_X_CENTER, iy, iz), mats["steel"], "x", sides, parent=side, caps="none")
    else:
        m.add_cylinder("Idler", ir, 0.46, (sign * TRACK_X_CENTER, iy, iz), mats["paint"], "x", sides, parent=side, caps="none")
    m.add_disc("IdlerFace", ir, (sign * (TRACK_X_CENTER + 0.23), iy, iz), mats["paint"], "x", sides, side, float(sign))
    if lod < 2:
        m.add_cylinder("IdlerHub", ir * 0.32, 0.08, (sign * (TRACK_X_CENTER + 0.27), iy, iz), mats["paint"], "x", 10, parent=side, caps="pos" if sign > 0 else "neg")
    if lod == 0:
        for i in range(6):
            a = (i + 0.5) * math.tau / 6
            m.add_disc("IdlerOpening", ir * 0.17, (sign * (TRACK_X_CENTER + 0.235), iy + ir * 0.6 * math.sin(a), iz + ir * 0.6 * math.cos(a)), mats["cavity"], "x", 8, side, float(sign))
    tag_wheel(m, first, (iy, iz), ir)
    first = len(m.parts)
    sprocket(m, sign, side, mats, sides, lod)
    tag_wheel(m, first, (SPROCKET[1], SPROCKET[0]), SPROCKET[2])


# ----------------------------------------------------------------------------
# Turret and gun
# ----------------------------------------------------------------------------


def seat(m, turret, x, z, radius):
    """Lowest and highest dome height under a round footprint."""
    return surface_heights(m, turret, (x, z), radius, TURRET_SURFACE)


def build_turret(m, turret, mats, lod):
    paint, steel = mats["paint"], mats["steel"]
    profile = TURRET_PROFILE if lod < 2 else [TURRET_PROFILE[i] for i in (0, 2, 4, 6, 8, 10, 12)]
    revolve(m, "TurretDome", profile, (32, 20, 12)[lod], paint, turret)

    # Commander's cupola on the left, rear of center, seated on the dome.
    sides = (16, 12, 8)[lod]
    cx, cz, cr = -0.50, 0.05, 0.36
    low, high = seat(m, turret, cx, cz, cr)
    base, top = low - 0.02, high + 0.10
    m.add_cylinder("Cupola", cr, top - base, (cx, (base + top) / 2, cz), paint, "y", sides, parent=turret)
    domed_hatch(m, "CupolaHatch", turret, (cx, top, cz), 0.30, paint, sides, rise=0.06, hinge_mat=steel if lod < 2 else None, hinge_dir=(0.0, 0.0, 1.0))
    if lod < 2:
        for angle in (-0.5, 0.0, 0.5):
            px, pz = cx + math.sin(angle) * (cr - 0.02), cz - math.cos(angle) * (cr - 0.02)
            m.add_box("CupolaPeriscope", (0.14, 0.06, 0.06), (px, top - 0.03, pz), mats["glass"], turret)
        m.add_box("CupolaIrBracket", (0.06, 0.12, 0.06), (cx + 0.20, top + 0.06, cz - 0.30), steel, turret)
        m.add_cylinder("CupolaIrLight", 0.08, 0.14, (cx + 0.20, top + 0.16, cz - 0.30), steel, "z", 10, parent=turret)
    # Loader's hatch on the right.
    lx, lz, lr = 0.50, 0.15, 0.30
    low, high = seat(m, turret, lx, lz, lr)
    m.add_cylinder("LoaderHatch", lr, high - low + 0.05, (lx, (low + high) / 2 + 0.01, lz), paint, "y", 12 if lod < 2 else 8, parent=turret)
    if lod < 2:
        domed_hatch(m, "LoaderHatchLid", turret, (lx, high + 0.035, lz), lr - 0.03, paint, 16, rise=0.045, hinge_mat=steel, hinge_dir=(0.0, 0.0, 1.0))
        # Gunner's sight aperture left of the gun and the coaxial machine gun port right of it.
        hit = surface_hit(m, turret, (-0.40, 0.40, -3.0), (0.0, 0.0, 1.0), TURRET_SURFACE)
        if hit:
            (px, py, pz), _ = hit
            m.add_box("SightAperture", (0.18, 0.14, 0.16), (px, py, pz + 0.03), steel, turret)
            m.add_box("SightWindow", (0.12, 0.06, 0.01), (px, py + 0.01, pz - 0.051), mats["glass"], turret)
        hit = surface_hit(m, turret, (0.30, 0.22, -3.0), (0.0, 0.0, 1.0), TURRET_SURFACE)
        if hit:
            (px, py, pz), _ = hit
            m.add_cylinder("CoaxPort", 0.05, 0.10, (px, py, pz + 0.02), steel, "z", 10, parent=turret)
            m.add_disc("CoaxBore", 0.022, (px, py, pz - 0.031), mats["cavity"], "z", 8, turret, -1.0)
    if lod < 2:
        # Gunner's sight hood ahead of the cupola, ventilator dome and antenna mount at the rear.
        low, high = seat(m, turret, -0.42, -0.55, 0.14)
        m.add_box("GunnerSight", (0.20, high - low + 0.14, 0.26), (-0.42, (low + high) / 2 + 0.05, -0.55), steel, turret)
        low, high = seat(m, turret, 0.20, 0.72, 0.14)
        m.add_cylinder("Ventilator", 0.14, high - low + 0.08, (0.20, (low + high) / 2 + 0.03, 0.72), paint, "y", 12, parent=turret)
        low, high = seat(m, turret, 0.78, 0.55, 0.06)
        m.add_cylinder("AntennaBase", 0.06, high - low + 0.10, (0.78, (low + high) / 2 + 0.04, 0.55), steel, "y", 8, parent=turret)
        rod(m, "Antenna", (0.78, high + 0.08, 0.55), (0.78, high + 2.10, 0.60), 0.008, steel, turret, sides=4)
    # Curved handrails on standoffs, following the dome on both sides.
    if lod == 0:
        rail_y, gap = 0.50, 0.07
        radius = turret_radius(rail_y) + gap
        for sign in (-1, 1):
            zs = [-0.55 + i * 0.125 for i in range(9)]
            rail = [(sign * math.sqrt(radius * radius - z * z), rail_y, z) for z in zs]
            for a, b in zip(rail, rail[1:]):
                rod(m, "Handrail", a, b, 0.015, steel, turret)
            for z in (zs[0], zs[4], zs[-1]):
                (sx, sy, sz), _ = turret_side_point(z, rail_y, sign)
                rx = sign * math.sqrt(radius * radius - z * z)
                rod(m, "HandrailPost", (sx - sign * 0.02, rail_y, z), (rx, rail_y, z), 0.013, steel, turret)


def build_gun(m, pitch, recoil, mats, lod):
    paint, steel = mats["paint"], mats["steel"]
    sides = (16, 10, 6)[lod]
    mantlet_front = MANTLET_FRONT_Z - TRUNNION_Z
    tube(m, "MantletCover", [(-0.45, 0.30), (-0.75, 0.25), (-1.00, 0.20), (mantlet_front, 0.17)], sides, mats["canvas"], pitch, cap_back=False)
    # L-2G "Luna" infrared searchlight right of the gun, linked to it, about level with the turret roof.
    light = (0.48, 0.48, -0.62)
    tube(m, "Searchlight", [(-0.48, 0.15), (-0.70, 0.17), (-0.76, 0.165)], sides, paint, pitch, pos=(light[0], light[1], 0.0))
    m.add_disc("SearchlightLens", 0.15, (light[0], light[1], light[2] - 0.145), mats["glass"], "z", sides, pitch, -1.0)
    m.add_box("SearchlightCradle", (0.10, 0.10, 0.22), (light[0], light[1] - 0.20, light[2]), steel, pitch)
    # Bracket arm back to the dome face and the link rod to the mantlet.
    rod(m, "SearchlightBracket", (light[0], light[1] - 0.22, light[2] + 0.08), (light[0] - 0.02, 0.20, -0.22), 0.045, steel, pitch)
    rod(m, "SearchlightLink", (0.16, 0.12, -0.95), (light[0], light[1] - 0.22, light[2] - 0.06), 0.022, steel, pitch)
    # U-5TS 115 mm smoothbore with the bore evacuator about two thirds along.
    rings = [
        (mantlet_front + 0.06, 0.100),
        (-2.30, 0.090),
        (-3.55, 0.084),
        (-3.60, 0.115),
        (-4.35, 0.115),
        (-4.40, 0.082),
        (MUZZLE_LOCAL_Z + 0.04, 0.078),
        (MUZZLE_LOCAL_Z, 0.084),
    ]
    tube(m, "Barrel", rings, sides, paint, recoil, tint=0.92)
    m.add_disc("MuzzleBore", 0.058, (0, 0, MUZZLE_LOCAL_Z - 0.002), steel, "z", sides, recoil, -1.0)


def build_markings(m, turret):
    """Red star with a white border and a white tactical number, painted on the dome sides."""
    atlas = MarkingAtlas(512, 256)
    atlas.star((0.0, 0.0, 0.5, 1.0), fill=(178, 34, 30, 255), outline=(232, 228, 214, 255), outline_width=0.07)
    atlas.text((0.5, 0.14, 1.0, 0.86), "312", fill=(232, 228, 214, 255))
    material = decal_material(m, atlas, "T62A_Markings")
    for sign in (-1, 1):
        for z, size, region in [(-0.40, (0.36, 0.36), (0.0, 0.0, 0.5, 1.0)), (0.40, (0.62, 0.34), (0.5, 0.0, 1.0, 1.0))]:
            center, normal = turret_side_point(z, 0.30, sign)
            add_conformal_decal(m, "Marking", turret, center, normal, (0.0, 1.0, 0.0), size, region, material, TURRET_SURFACE)


def build_t62_alt_lod(lod=0, output_dir=None):
    m = GltfModel(f"T62Alt_LOD{lod}")
    m.data["asset"]["generator"] = "Dead Ground T-62 alternative model authoring"
    mats = materials(m)
    hull = m.node("Hull")
    left = m.node("LeftTrack")
    right = m.node("RightTrack")
    turret = m.node("TurretYaw", TURRET_RING)
    pitch = m.node("GunPitch", GUN_TRUNNION_LOCAL, turret)
    recoil = m.node("BarrelRecoil", parent=pitch)
    m.node("Muzzle", (0, 0, MUZZLE_LOCAL_Z), recoil)

    build_hull(m, hull, mats, lod)
    build_running_gear(m, left, -1, mats, lod)
    build_running_gear(m, right, 1, mats, lod)
    build_turret(m, turret, mats, lod)
    build_gun(m, pitch, recoil, mats, lod)
    bake_vertex_shading(m, gear_nodes=("LeftTrack", "RightTrack"), max_edge=(0.35, 0.7, None)[lod])
    emit_motion_parts(m)
    if lod < 2:
        build_markings(m, turret)

    name = "t62alt.glb" if lod == 0 else f"t62alt_lod{lod}.glb"
    stats = m.save((output_dir or OUTPUT_DIR) / name)
    stats["lod"] = lod
    if getattr(m, "track_pattern_cycle", None):
        stats["track_pattern_cycle"] = m.track_pattern_cycle
    stats["role"] = ("close", "rts", "distant")[lod]
    return stats


def generate_all(output_dir=None):
    output_dir = Path(output_dir) if output_dir else OUTPUT_DIR
    lods = [build_t62_alt_lod(lod, output_dir) for lod in range(3)]
    manifest = {
        "asset": "T-62 alternative RTS visual study",
        "configuration": "Basic T-62 with L-2G searchlight, rear drums and right fender fuel cells, no roof AA machine gun",
        "units": "meters",
        "up": "+Y",
        "forward": "-Z",
        "color_authoring": "sRGB hex converted to linear glTF factors",
        "vertex_colors": "rgb: tint x baked ambient occlusion, a: dirt mask",
        "reference_anchors": {
            "hull_length_meters": HULL_LENGTH,
            "overall_length_gun_fwd_meters": TOTAL_LENGTH_GUN_FWD,
            "width_over_fenders_meters": WIDTH_OVER_FENDERS,
            "height_to_cupola_meters": HEIGHT_TO_CUPOLA,
            "ground_clearance_meters": GROUND_CLEARANCE,
            "road_wheels_per_side": len(ROAD_WHEEL_Z),
        },
        "lods": lods,
        # Copy into visuals.json as track_wrap: the client wraps track travel at this period.
        "track_wrap_meters": track_wrap_period(lods[0]["track_pattern_cycle"], ROAD_WHEEL_R),
    }
    (output_dir / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=None)
    args = parser.parse_args()
    result = generate_all(args.output_dir)
    for item in result["lods"]:
        print(f"{item['file']}: {item['triangles']} triangles, {item['mesh_parts']} parts, {item['bytes']} bytes")
