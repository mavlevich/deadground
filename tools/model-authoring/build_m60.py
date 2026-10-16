"""Generate an M60A1 study for Dead Ground, in meters, with three explicit LODs.

Conventions match the T-62 asset: meters, +Y up, -Z forward, root centered on the
hull footprint at ground level. Node hierarchy:

    M60
    |-- Hull
    |-- LeftTrack
    |-- RightTrack
    `-- TurretYaw
        |-- GunPitch
        |   `-- BarrelRecoil
        |       `-- Muzzle
        `-- (cupola, hatches, bustle rack: static on the turret)
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
    cast_section,
    decal_material,
    domed_hatch,
    hex_section,
    loft_sections,
    obox,
    rod,
    emit_motion_parts,
    surface_heights,
    track_belt,
    tag_wheel,
    track_wrap_period,
    track_links,
    track_path,
    tube,
)

OUTPUT_DIR = Path(__file__).resolve().parents[2] / "src/Game.Godot/Assets/Vehicles/M60"

# Public M60A1 figures used as anchors. Positions below were measured from the
# M60A1 (1962) drawing by Dr Dan Saranga (side and front views); small fittings follow photos.
HULL_LENGTH = 6.946
TOTAL_LENGTH_GUN_FWD = 9.436
WIDTH_OVER_TRACKS = 3.631
HEIGHT_TO_CUPOLA = 3.27
GROUND_CLEARANCE = 0.46

HULL_FRONT_Z = -HULL_LENGTH / 2
HULL_REAR_Z = HULL_LENGTH / 2
DECK_Y = 1.72
SPONSON_Y = 1.47

TURRET_RING = (0.0, DECK_Y, -0.45)
GUN_TRUNNION_LOCAL = (0.0, 0.50, -1.20)  # relative to TurretYaw
MUZZLE_Z = HULL_REAR_Z - TOTAL_LENGTH_GUN_FWD
MUZZLE_LOCAL_Z = MUZZLE_Z - (TURRET_RING[2] + GUN_TRUNNION_LOCAL[2])

TRACK_X_INNER, TRACK_X_OUTER = 1.105, WIDTH_OVER_TRACKS / 2
TRACK_X_CENTER = (TRACK_X_INNER + TRACK_X_OUTER) / 2
TRACK_THICKNESS = 0.07

ROAD_WHEEL_Z = [-2.20, -1.365, -0.53, 0.305, 1.155, 2.06]
ROAD_WHEEL_Y, ROAD_WHEEL_R = 0.40, 0.33
# Hub between the two tire halves; small enough for the track center guides to pass under it.
ROAD_WHEEL_GAP_R = 0.22
RETURN_ROLLER_Z = [-2.05, -0.19, 1.26]
RETURN_ROLLER_Y, RETURN_ROLLER_R = 1.18, 0.13
IDLER = (-2.99, 0.98, 0.29)
SPROCKET = (2.86, 1.01, 0.33)
FENDER_Y = SPONSON_Y


# ----------------------------------------------------------------------------
# Running gear
# ----------------------------------------------------------------------------


def road_wheel(m, sign, z, y, radius, sides, mats, parent, lod):
    x = sign * TRACK_X_CENTER
    if lod == 0:
        for offset in (-0.135, 0.135):
            m.add_cylinder("RoadWheelTire", radius, 0.19, (x + offset, y, z), mats["rubber"], "x", sides, parent=parent, caps="none")
        m.add_cylinder("RoadWheelGap", ROAD_WHEEL_GAP_R, 0.09, (x, y, z), mats["steel"], "x", sides, parent=parent, caps="none")
    else:
        m.add_cylinder("RoadWheelTire", radius, 0.46, (x, y, z), mats["rubber"], "x", sides, parent=parent, caps="none")
    face_x = x + sign * 0.232
    m.add_disc("RoadWheelTireWall", radius, (face_x, y, z), mats["rubber"], "x", sides, parent, float(sign))
    m.add_disc("RoadWheelFace", radius * 0.80, (face_x + sign * 0.004, y, z), mats["paint"], "x", sides, parent, float(sign))
    if lod < 2:
        m.add_cylinder("RoadWheelHub", radius * 0.32, 0.07, (face_x + sign * 0.035, y, z), mats["paint"], "x", max(8, sides // 2), parent=parent, caps="pos" if sign > 0 else "neg")
    if lod == 0:
        # Pressed aluminum disc: two dished steps toward the hub, a bolt circle and a hub cap.
        cap = "pos" if sign > 0 else "neg"
        m.add_cylinder("RoadWheelDish", radius * 0.66, 0.02, (face_x + sign * 0.012, y, z), mats["paint"], "x", sides, parent=parent, caps=cap)
        m.add_cylinder("RoadWheelDish", radius * 0.48, 0.02, (face_x + sign * 0.030, y, z), mats["paint"], "x", sides, parent=parent, caps=cap)
        for i in range(8):
            a = i * math.tau / 8
            m.add_disc("RoadWheelBolt", 0.013, (face_x + sign * 0.041, y + radius * 0.39 * math.sin(a), z + radius * 0.39 * math.cos(a)), mats["steel"], "x", 6, parent, float(sign))
        m.add_disc("RoadWheelHubCap", radius * 0.16, (face_x + sign * 0.071, y, z), mats["steel"], "x", 10, parent, float(sign))


def sprocket(m, sign, parent, mats, sides, lod):
    z, y, r = SPROCKET
    x = sign * TRACK_X_CENTER
    m.add_cylinder("SprocketHub", r * 0.55, 0.50, (x, y, z), mats["steel"], "x", sides, parent=parent)
    for offset in (-0.17, 0.17) if lod < 2 else ():
        m.add_cylinder("SprocketRing", r * 0.92, 0.07, (x + offset, y, z), mats["steel"], "x", sides, parent=parent)
        if lod == 0:
            teeth = 11
            for i in range(teeth):
                a = i * math.tau / teeth
                radial = (0.0, math.sin(a), math.cos(a))
                tangential = (0.0, math.cos(a), -math.sin(a))
                center = (x + offset, y + radial[1] * r * 0.96, z + radial[2] * r * 0.96)
                obox(m, "SprocketTooth", center, ((1.0, 0.0, 0.0), radial, tangential), (0.035, 0.05, 0.035), mats["steel"], parent)
    m.add_disc("SprocketFace", r * 0.55, (x + sign * 0.255, y, z), mats["steel"], "x", sides, parent, float(sign))


# ----------------------------------------------------------------------------
# Model
# ----------------------------------------------------------------------------


def materials(m):
    # sRGB authoring colors, converted to linear factors by GltfModel.material.
    return {
        "paint": m.material("M60_Paint", "51553e", metal=0.08, rough=0.82),
        "rubber": m.material("M60_Rubber", "2f2f2b", rough=0.96),
        "track": m.material("M60_TrackSteel", "4a4a42", metal=0.35, rough=0.80),
        "steel": m.material("M60_DarkSteel", "33372f", metal=0.30, rough=0.70),
        "glass": m.material("M60_Optics", "26343a", metal=0.15, rough=0.22),
        "canvas": m.material("M60_Canvas", "6f6a4c", rough=0.96),
        "cavity": m.material("M60_Cavity", "161712", rough=1.0),
    }


def build_hull(m, hull, mats, lod):
    paint, steel = mats["paint"], mats["steel"]
    # Lower tub between the tracks.
    loft_sections(
        m,
        "LowerTub",
        [
            (-3.40, hex_section(0.92, 0.82, 1.02, 1.00, 0.05)),
            (-3.10, hex_section(0.98, 0.56, 1.06, 1.14, 0.05)),
            (-2.70, hex_section(1.00, GROUND_CLEARANCE, 1.07, 1.40, 0.05)),
            (-2.40, hex_section(1.00, GROUND_CLEARANCE, 1.07, SPONSON_Y, 0.05)),
            (3.00, hex_section(1.00, GROUND_CLEARANCE, 1.07, SPONSON_Y, 0.05)),
            (3.45, hex_section(1.00, 0.64, 1.07, SPONSON_Y, 0.05)),
        ],
        paint,
        hull,
        tint=0.74,
    )
    # Upper hull: rounded cast nose sweeping into the sponsons and flat engine deck.
    nose = [
        (HULL_FRONT_Z, 0.92, 0.88, 0.86, 1.08),
        (-3.40, 1.24, 0.92, 1.12, 1.24),
        (-3.24, 1.44, 1.00, 1.26, 1.40),
        (-3.02, 1.52, 1.14, 1.34, 1.55),
        (-2.74, 1.54, 1.30, 1.39, 1.66),
        (-2.40, 1.54, SPONSON_Y, 1.42, DECK_Y),
    ]
    # The cast nose is smooth-shaded; the welded rest of the hull keeps hard plate edges.
    stations = [(z, hex_section(wb, yb, wt, yt, 0.07)) for z, wb, yb, wt, yt in nose]
    loft_sections(m, "UpperHullNose", stations, paint, hull, tint=0.95, smooth=lod < 2)
    stations = [
        stations[-1],
        (3.10, hex_section(1.54, SPONSON_Y, 1.42, DECK_Y, 0.07)),
        (HULL_REAR_Z, hex_section(1.54, SPONSON_Y, 1.42, 1.63, 0.07)),
    ]
    loft_sections(m, "UpperHull", stations, paint, hull, tint=0.95)

    for sign in (-1, 1):
        # Full-length fenders with angled front and rear guards.
        m.add_box("Fender", (0.46, 0.035, 6.40), (sign * 1.60, FENDER_Y, 0.03), paint, hull, tint=0.86)
        for z0, z1, y1 in [(-3.17, -3.62, 1.22), (3.23, 3.56, 1.30)]:
            mid = ((z0 + z1) / 2, (FENDER_Y + y1) / 2)
            length = math.hypot(z1 - z0, y1 - FENDER_Y)
            tangent = (0.0, (y1 - FENDER_Y) / length, (z1 - z0) / length)
            normal = (0.0, -tangent[2], tangent[1])
            obox(m, "FenderGuard", (sign * 1.60, mid[1], mid[0]), ((1, 0, 0), normal, tangent), (0.23, 0.017, length / 2), paint, hull, tint=0.86)
        # Fender stowage boxes.
        for z, length in ([(-1.75, 0.95), (-0.55, 0.95), (2.35, 0.80)] if lod < 2 else [(-1.15, 2.15)]):
            m.add_box("FenderBox", (0.26, 0.24, length), (sign * 1.68, FENDER_Y + 0.138, z), paint, hull, tint=0.80)
        # Headlight clusters on the front fenders.
        m.add_box("HeadlightHousing", (0.22, 0.18, 0.16), (sign * 1.42, 1.56, -3.20), steel, hull)
        if lod < 2:
            m.add_disc("HeadlightLens", 0.065, (sign * 1.42, 1.57, -3.281), mats["glass"], "z", 10, hull, -1.0)
        if lod == 0:
            for dx in (-0.12, 0.12):
                rod(m, "HeadlightGuard", (sign * 1.42 + dx, 1.46, -3.12), (sign * 1.42 + dx, 1.70, -3.34), 0.012, steel, hull)
            rod(m, "HeadlightGuard", (sign * 1.42 - 0.12, 1.70, -3.34), (sign * 1.42 + 0.12, 1.70, -3.34), 0.012, steel, hull)
        # Front tow hooks and rear lights.
        m.add_box("TowHook", (0.10, 0.12, 0.14), (sign * 0.62, 0.86, -3.43), steel, hull)
        m.add_box("TailLight", (0.16, 0.12, 0.06), (sign * 1.30, 1.52, HULL_REAR_Z + 0.03), steel, hull)

    # Turret ring guard: the cast turret overhangs it by 0.1 m, which reads as a shadowed seam.
    m.add_cylinder("TurretRingGuard", 1.14, 0.13, (0.0, DECK_Y + 0.06, TURRET_RING[2]), steel, "y", (32, 20, 12)[lod], parent=hull)
    # Driver hatch and periscopes at the front center of the deck.
    m.add_cylinder("DriverHatch", 0.30, 0.05, (0, DECK_Y + 0.025, -2.08), paint, "y", 14 if lod < 2 else 8, parent=hull)
    if lod < 2:
        m.add_box("DriverPeriscope", (0.36, 0.06, 0.08), (0, DECK_Y + 0.05, -2.36), mats["glass"], hull)
    # Engine deck grilles and rear grille doors.
    for sign in (-1, 1):
        m.add_box("EngineGrille", (1.05, 0.03, 1.55), (sign * 0.62, DECK_Y + 0.015, 2.20), steel, hull)
        if lod == 0:
            for i in range(7):
                m.add_box("GrilleSlat", (0.98, 0.025, 0.04), (sign * 0.62, DECK_Y + 0.04, 1.55 + i * 0.21), paint, hull, tint=0.82)
    m.add_box("RearGrille", (2.40, 0.42, 0.05), (0, 1.27, HULL_REAR_Z + 0.025), steel, hull)
    if lod == 0:
        for i in range(5):
            m.add_box("RearGrilleBar", (2.30, 0.03, 0.04), (0, 1.11 + i * 0.08, HULL_REAR_Z + 0.06), paint, hull, tint=0.82)


def build_running_gear(m, side, sign, mats, lod):
    wheel_sides = (16, 12, 8)[lod]
    wheels = [(z, ROAD_WHEEL_Y, ROAD_WHEEL_R) for z in ROAD_WHEEL_Z]
    rollers = [(z, RETURN_ROLLER_Y, RETURN_ROLLER_R) for z in RETURN_ROLLER_Z]
    path = track_path(wheels, rollers, IDLER, SPROCKET, (10, 7, 5)[lod])
    if lod == 0:
        # T142 double-pin track: 0.176 m pitch, rubber pads, end connectors at the edge, center guides.
        track_links(m, sign, side, path, TRACK_X_INNER, TRACK_X_OUTER, 0.04, 0.176, mats["track"], pad_mat=mats["rubber"], pin_mat=mats["steel"], pad=(0.03, 0.62), gap=0.12, horn=(0.07, 0.06), horn_every=1)
    else:
        track_belt(m, sign, side, mats["track"], path, TRACK_X_INNER, TRACK_X_OUTER, TRACK_THICKNESS)
    for z in ROAD_WHEEL_Z:
        first = len(m.parts)
        road_wheel(m, sign, z, ROAD_WHEEL_Y, ROAD_WHEEL_R, wheel_sides, mats, side, lod)
        tag_wheel(m, first, (ROAD_WHEEL_Y, z), ROAD_WHEEL_R)
    for z in RETURN_ROLLER_Z if lod < 2 else []:
        first = len(m.parts)
        if lod == 0:
            for offset in (-0.13, 0.13):
                m.add_cylinder("ReturnRoller", RETURN_ROLLER_R, 0.15, (sign * TRACK_X_CENTER + offset, RETURN_ROLLER_Y, z), mats["steel"], "x", 10, parent=side)
        else:
            m.add_cylinder("ReturnRoller", RETURN_ROLLER_R, 0.42, (sign * TRACK_X_CENTER, RETURN_ROLLER_Y, z), mats["steel"], "x", max(8, wheel_sides // 2), parent=side)
        tag_wheel(m, first, (RETURN_ROLLER_Y, z), RETURN_ROLLER_R)
    iz, iy, ir = IDLER
    first = len(m.parts)
    if lod == 0:
        for offset in (-0.15, 0.15):
            m.add_cylinder("Idler", ir, 0.17, (sign * TRACK_X_CENTER + offset, iy, iz), mats["steel"], "x", wheel_sides, parent=side)
        m.add_cylinder("IdlerGap", ir * 0.5, 0.14, (sign * TRACK_X_CENTER, iy, iz), mats["steel"], "x", wheel_sides, parent=side, caps="none")
    else:
        m.add_cylinder("Idler", ir, 0.46, (sign * TRACK_X_CENTER, iy, iz), mats["steel"], "x", wheel_sides, parent=side)
    m.add_disc("IdlerFace", ir * 0.75, (sign * (TRACK_X_CENTER + 0.235), iy, iz), mats["steel"], "x", wheel_sides, side, float(sign))
    if lod == 0:
        for i in range(6):
            a = (i + 0.5) * math.tau / 6
            m.add_disc("IdlerOpening", ir * 0.15, (sign * (TRACK_X_CENTER + 0.237), iy + ir * 0.5 * math.sin(a), iz + ir * 0.5 * math.cos(a)), mats["cavity"], "x", 8, side, float(sign))
    tag_wheel(m, first, (iy, iz), ir)
    first = len(m.parts)
    sprocket(m, sign, side, mats, wheel_sides, lod)
    tag_wheel(m, first, (SPROCKET[1], SPROCKET[0]), SPROCKET[2])
    if lod == 0:
        # Road arm stubs and bump stops visible between wheels.
        for z in ROAD_WHEEL_Z:
            m.add_box("RoadArm", (0.10, 0.10, 0.36), (sign * 1.06, ROAD_WHEEL_Y + 0.12, z + 0.16), mats["steel"], side)
        for z in ROAD_WHEEL_Z[:2] + ROAD_WHEEL_Z[-1:]:
            m.add_box("ShockAbsorber", (0.10, 0.44, 0.10), (sign * 1.08, 0.92, z - 0.25), mats["steel"], side)


def build_turret(m, turret, mats, lod):
    paint, steel, canvas = mats["paint"], mats["steel"], mats["canvas"]
    steps = (6, 4, 3)[lod]
    # Elongated "needle nose" cast turret: narrow front, wide center, overhanging bustle.
    profile = [
        (-1.53, 0.42, 0.34, 0.25, 0.58),
        (-1.34, 0.64, 0.50, 0.18, 0.76),
        (-1.05, 0.94, 0.72, 0.14, 0.88),
        (-0.58, 1.20, 0.90, 0.12, 0.97),
        (-0.10, 1.34, 1.02, 0.10, 1.005),
        (0.70, 1.34, 1.04, 0.10, 1.005),
        (1.30, 1.26, 1.00, 0.17, 0.99),
        (1.80, 1.11, 0.92, 0.27, 0.95),
        (2.03, 0.98, 0.82, 0.33, 0.91),
    ]
    if lod == 2:
        profile = [profile[i] for i in (0, 2, 3, 5, 7, 8)]
    loft_sections(m, "TurretShell", [(z, cast_section(wb, wt, y0, y1, steps)) for z, wb, wt, y0, y1 in profile], paint, turret, tint=1.0, smooth=lod < 2)

    def seat(x, z, radius):
        return surface_heights(m, turret, (x, z), radius, {"TurretShell"})

    # M19 commander's cupola on the right with its .50 caliber machine gun, seated on the roof.
    cx, cz = 0.48, 0.50
    cupola_sides = (16, 12, 8)[lod]
    low, roof = seat(cx, cz, 0.44)
    base = low - 0.02
    m.add_cylinder("Cupola", 0.44, roof + 0.42 - base, (cx, (base + roof + 0.42) / 2, cz), paint, "y", cupola_sides, parent=turret)
    if lod < 2:
        m.add_cylinder("CupolaVisionBlocks", 0.445, 0.07, (cx, roof + 0.32, cz), mats["glass"], "y", cupola_sides, parent=turret)
    domed_hatch(m, "CupolaHatch", turret, (cx, roof + 0.42, cz), 0.37, paint, cupola_sides, rise=0.06, hinge_mat=steel if lod < 2 else None, hinge_dir=(0.0, 0.0, 1.0))
    m.add_box("CupolaGunMount", (0.24, 0.16, 0.42), (cx, roof + 0.40, cz - 0.38), steel, turret)
    if lod < 2:
        rod(m, "CupolaGunBarrel", (cx, roof + 0.41, cz - 0.58), (cx, roof + 0.41, cz - 1.30), 0.026, steel, turret)
    # Loader's hatch, ventilator and turret-mounted stowage.
    low, high = seat(-0.50, 0.35, 0.30)
    m.add_cylinder("LoaderHatch", 0.30, high - low + 0.05, (-0.50, (low + high) / 2 + 0.01, 0.35), paint, "y", 12 if lod < 2 else 8, parent=turret)
    if lod < 2:
        domed_hatch(m, "LoaderHatchLid", turret, (-0.50, high + 0.035, 0.35), 0.27, paint, 16, rise=0.04, hinge_mat=steel, hinge_dir=(0.0, 0.0, 1.0))
    if lod < 2:
        low, high = seat(-0.10, 1.05, 0.13)
        m.add_cylinder("Ventilator", 0.13, high - low + 0.10, (-0.10, (low + high) / 2 + 0.04, 1.05), paint, "y", 10, parent=turret)
        low, high = seat(0.35, -0.70, 0.17)
        m.add_box("GunnerSight", (0.26, high - low + 0.14, 0.24), (0.35, (low + high) / 2 + 0.05, -0.70), steel, turret)
    roof = seat(-0.95, 1.35, 0.06)[1]
    # Bustle stowage rack: tubular frame with rolled canvas and boxes.
    rack_y0, rack_y1, rack_z0, rack_z1, rack_x = 0.38, 0.78, 1.05, 2.42, 1.28
    if lod < 2:
        for y in (rack_y0, rack_y1):
            for sign in (-1, 1):
                rod(m, "BustleRail", (sign * rack_x, y, rack_z0), (sign * rack_x, y, rack_z1), 0.022, steel, turret)
            rod(m, "BustleRail", (-rack_x, y, rack_z1), (rack_x, y, rack_z1), 0.022, steel, turret)
        for x in (-rack_x, -0.43, 0.43, rack_x):
            rod(m, "BustlePost", (x, rack_y0, rack_z1), (x, rack_y1, rack_z1), 0.02, steel, turret)
        for sign in (-1, 1):
            rod(m, "BustlePost", (sign * rack_x, rack_y0, rack_z0 + 0.6), (sign * rack_x, rack_y1, rack_z0 + 0.6), 0.02, steel, turret)
    m.add_box("BustleFloor", (2.56, 0.04, 0.55), (0, rack_y0 - 0.02, 2.15), steel, turret)
    m.add_cylinder("RolledTarp", 0.17, 2.10, (0, rack_y0 + 0.17, 2.22), canvas, "x", (12, 8, 6)[lod], parent=turret)
    m.add_box("StowageBox", (0.62, 0.30, 0.38), (0.72, rack_y0 + 0.15, 2.05), canvas, turret, tint=0.85)
    m.add_box("StowageBox", (0.52, 0.26, 0.34), (-0.80, rack_y0 + 0.13, 2.06), canvas, turret, tint=0.92)
    # Radio antenna on the turret rear.
    if lod < 2:
        m.add_cylinder("AntennaBase", 0.06, 0.12, (-0.95, roof - 0.02, 1.35), steel, "y", 8, parent=turret)
        rod(m, "Antenna", (-0.95, roof + 0.04, 1.35), (-0.95, roof + 2.30, 1.40), 0.008, steel, turret, sides=4)


def build_gun(m, pitch, recoil, mats, lod):
    paint, steel = mats["paint"], mats["steel"]
    sides = (16, 10, 6)[lod]
    # Mantlet face and canvas dust cover stay on the pitching mount.
    tube(m, "Mantlet", [(-0.40, 0.30), (-0.54, 0.28)], sides, paint, pitch, tint=0.9)
    tube(m, "DustCover", [(-0.54, 0.27), (-0.62, 0.23), (-0.72, 0.19), (-0.81, 0.14)], sides, mats["canvas"], pitch, cap_front=False)
    # AN/VSS-1 searchlight above the gun.
    m.add_box("SearchlightMount", (0.30, 0.12, 0.26), (0, 0.33, -0.52), steel, pitch)
    m.add_box("SearchlightHousing", (0.58, 0.46, 0.40), (0, 0.62, -0.54), steel, pitch)
    m.add_disc("SearchlightLens", 0.19, (0, 0.62, -0.745), mats["glass"], "z", sides, pitch, -1.0)
    # M68 105 mm barrel with bore evacuator; only this part recoils.
    rings = [(-0.78, 0.110), (-2.40, 0.096), (-2.45, 0.128), (-3.05, 0.128), (-3.10, 0.090), (MUZZLE_LOCAL_Z + 0.05, 0.082), (MUZZLE_LOCAL_Z, 0.088)]
    tube(m, "Barrel", rings, sides, paint, recoil, tint=0.92)
    m.add_disc("MuzzleBore", 0.055, (0, 0, MUZZLE_LOCAL_Z - 0.002), steel, "z", sides, recoil, -1.0)


def build_markings(m, turret):
    """White star and a company/vehicle code on both turret sides."""
    atlas = MarkingAtlas(512, 256)
    atlas.star((0.0, 0.0, 0.5, 1.0), fill=(236, 234, 222, 255))
    atlas.text((0.5, 0.18, 1.0, 0.82), "B21", fill=(236, 234, 222, 255))
    material = decal_material(m, atlas, "M60_Markings")
    # Projected sideways onto the cast shell, so the paint follows its curve.
    surface = {"TurretShell"}
    for sign in (-1, 1):
        normal = (sign * 0.936, 0.351, 0.0)
        up = (0.0, 1.0, 0.0)
        add_conformal_decal(m, "MarkingStar", turret, (sign * 1.20, 0.60, -0.15), normal, up, (0.52, 0.52), (0.0, 0.0, 0.5, 1.0), material, surface)
        add_conformal_decal(m, "MarkingCode", turret, (sign * 1.20, 0.60, 0.62), normal, up, (0.62, 0.40), (0.5, 0.0, 1.0, 1.0), material, surface)


def build_m60_lod(lod=0, output_dir=None):
    m = GltfModel(f"M60_LOD{lod}")
    m.data["asset"]["generator"] = "Dead Ground M60A1 model authoring"
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

    name = "m60.glb" if lod == 0 else f"m60_lod{lod}.glb"
    stats = m.save((output_dir or OUTPUT_DIR) / name)
    stats["lod"] = lod
    if getattr(m, "track_pattern_cycle", None):
        stats["track_pattern_cycle"] = m.track_pattern_cycle
    stats["role"] = ("close", "rts", "distant")[lod]
    return stats


def generate_all(output_dir=None):
    output_dir = Path(output_dir) if output_dir else OUTPUT_DIR
    lods = [build_m60_lod(lod, output_dir) for lod in range(3)]
    manifest = {
        "asset": "M60A1 RTS visual study",
        "configuration": "M60A1 with M19 cupola, AN/VSS-1 searchlight and bustle rack, no ERA or smoke dischargers",
        "units": "meters",
        "up": "+Y",
        "forward": "-Z",
        "color_authoring": "sRGB hex converted to linear glTF factors",
        "reference_anchors": {
            "hull_length_meters": HULL_LENGTH,
            "overall_length_gun_fwd_meters": TOTAL_LENGTH_GUN_FWD,
            "width_over_tracks_meters": WIDTH_OVER_TRACKS,
            "height_to_cupola_meters": HEIGHT_TO_CUPOLA,
            "ground_clearance_meters": GROUND_CLEARANCE,
            "road_wheels_per_side": len(ROAD_WHEEL_Z),
            "return_rollers_per_side": len(RETURN_ROLLER_Z),
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
