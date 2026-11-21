"""Procedural BM-21 (2B5) 122 mm multiple rocket launcher on a Ural-375D 6x6 truck, for artillery tests.

Three explicit LODs. The goal is recognizable proportions and readability from the game camera.
Small details are simplified.

Sources (URLs in SOURCES and in the manifest):
- Combat vehicle in travel position: length 7,350 mm, width 2,400 mm, height 3,090 mm, ground
  clearance 400 mm, maximum elevation 55 degrees (Russian Wikipedia article on the Grad MLRS).
- Ural-375D: wheelbase 3,525 + 1,400 mm, track 2,000 mm, height over the cab 2,680 mm, all-metal
  cab, spare wheel between the cab and the body (Russian Wikipedia article on the Ural-375D).
- Barrel length 3,000 mm, 40 tubes in 4 rows of 10, rear stabilizing jacks (English Wikipedia,
  truck-encyclopedia.com).
- Layout and silhouette: side, front and rear views of the Ural-4320 BM-21 drawing on
  the-blueprints.com (same cab, hood and launcher as the 375D), and museum photos of the BM-21 on
  the Ural-375D on Wikimedia Commons. Stations along the length are scaled from the drawing to the
  published length and wheelbase.

What the drawing and photos show, and this model follows: the turntable sits over the rear bogie
and carries a low upper carriage, with the trunnions at the rear end of the cradle about 1.8 m above
the ground. In travel position the pack lies on the carriage and points forward; its top is about
level with the cab roof and the tube mouths sit behind the upright spare wheel (travel position photo
at the Lesany museum). The published 3.09 m travel height is higher than that; the manifest records
the conflict. The Ural
front end is a tall wide hood between big flat-topped wings with the headlights in the wing fronts,
a high square bumper and a short tall cab with a two-pane windscreen.

Laying limits from a description of the launcher (vk.ru article listed in SOURCES): elevation 0 to
55 degrees, traverse 102 degrees left and 70 degrees right, and a minimum elevation of 11 degrees
within 34 degrees either side of the axis, where the rockets would otherwise pass over the cab. The elevating gear sits at the center of the base and
meshes with a toothed sector on the cradle; the traverse gear sits on the left of the base.

Coordinates: meters, +Y up, -Z forward, +X right, ground plane at Y = 0. Every node has unit scale and
no rotation in the rest pose. Rig nodes match the other vehicles, so the client finds them by name:
TurretYaw (launcher turntable), GunPitch (pack trunnions) and Muzzle (pack front center), plus
Launch_01 to Launch_40, one per tube mouth. The tube bores point along local -Z of GunPitch.
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
    emit_motion_parts,
    hex_section,
    loft_sections,
    obox,
    tag_wheel,
)

DEFAULT_OUTPUT_DIR = _THIS_DIR.parents[1] / "src/Game.Godot/Assets/Vehicles/BM21"

SOURCES = [
    "https://ru.wikipedia.org/wiki/%D0%93%D1%80%D0%B0%D0%B4_%28%D0%A0%D0%A1%D0%97%D0%9E%29",
    "https://ru.wikipedia.org/wiki/%D0%A3%D1%80%D0%B0%D0%BB-375%D0%94",
    "https://en.wikipedia.org/wiki/BM-21_Grad",
    "https://truck-encyclopedia.com/coldwar/ussr/BM-21-Grad.php",
    "https://the-blueprints.com/blueprints/trucks/ural/86698/view/ural-4320_bm-21_grad_122mm",
    "https://commons.wikimedia.org/wiki/Category:BM-21_Grad_in_museums",
    "https://commons.wikimedia.org/wiki/File:BM-21_Grad_(3).jpg",
    "https://vk.ru/@oo_svk-rszo-bm-21-grad",
]

# ----------------------------------------------------------------------------
# Reference layout in meters (+Y up, -Z forward, ground at Y = 0)
# ----------------------------------------------------------------------------

REFERENCE_LENGTH = 7.35
REFERENCE_WIDTH = 2.40
REFERENCE_TRAVEL_HEIGHT = 3.09
REFERENCE_CAB_HEIGHT = 2.68
REFERENCE_CLEARANCE = 0.40
PHOTO_PACK_TOP_Y = 2.68                     # travel position: pack top about level with the cab roof

# Stations along the length come from the side view of the Ural-4320 BM-21 drawing (same cab, hood
# and launcher as the 375D), scaled to the published 7.35 m length and 3.525 + 1.40 m wheelbase.
FRONT_Z = -3.675                    # front of the bumper
NOSE_Z = -3.52                      # front face of the hood, wings and grille
REAR_Z = 3.675                      # rear of the platform and tail fittings
HALF_WIDTH = 1.20                   # wings, bumper and platform: published 2.40 m width

WHEEL_R = 0.61                      # 14.00-20 tires, about 1.22 m across on the drawing
TIRE_WIDTH = 0.38
TRACK = 2.00                        # distance between left and right wheel centers
WHEEL_X = TRACK / 2.0
WHEEL_Y = WHEEL_R
AXLE_Z = (-2.375, 1.150, 2.550)     # front axle, then the rear tandem (3.525 m and 1.40 m spacing)
AXLE_DROP = WHEEL_Y - REFERENCE_CLEARANCE   # differential housings reach down to the ground clearance

FRAME_Y = (0.90, 1.15)              # chassis rail bottom and top
DECK_Y = 1.45                       # top of the launcher platform deck (about 1.5 m on the photos)
BUMPER_Y = (0.94, 1.18)             # the Ural bumper sits high

HOOD_HALF_WIDTH = 0.64              # tall hood between the wings, grille about 0.9 m wide
HOOD_FRONT_TOP_Y = 1.98
HOOD_REAR_TOP_Y = 2.07
WING_TOP_Y = 1.80                   # flat-topped front wings
ARCH_TOP_Y = WHEEL_Y + WHEEL_R + 0.12
ARCH_HALF_LENGTH = 0.74
GRILLE_Y = (1.30, 1.94)
HEADLIGHT_X = 0.93
HEADLIGHT_Y = 1.44

CAB_FRONT_Z = -1.81
CAB_REAR_Z = -0.645
CAB_BOTTOM_Y = 1.02
CAB_BELT_Y = 2.09                   # bottom edge of the windscreen, just above the hood
CAB_ROOF_Y = REFERENCE_CAB_HEIGHT
CAB_HALF_WIDTH = 0.98
CAB_RAKE = 0.10                     # the windscreen leans back this far from belt to roof
MIRROR_X = 1.30

SPARE_CENTER = (0.0, FRAME_Y[1] + 0.02 + WHEEL_R, -0.33)   # spare wheel stands upright on the frame behind the cab, axle along Z
DECK_START_Z = SPARE_CENTER[2] + 0.30         # the platform starts behind the spare wheel
DECK_EQUIPMENT_Z = (DECK_START_Z + 0.05, 0.85)
DECK_EQUIPMENT_TOP_Y = 1.72
REAR_GUARD_Y = WHEEL_Y + WHEEL_R + 0.12

# Launcher rig. The turntable sits over the rear bogie and carries a low box-shaped upper carriage.
# The trunnions are at the rear end of the cradle, about 1.8 m above the ground on the drawing and the
# side photo. In travel position the pack lies on the carriage, points forward, and its top is about
# level with the cab roof, with the tube mouths just behind the cab (travel photo, Lesany museum).
TURRET_POS = (0.0, DECK_Y, 2.40)            # turntable center on the deck
TRUNNION_LOCAL = (0.0, 0.35, 0.62)          # elevation axis in TurretYaw space, world y 1.80, z 3.02
CARRIAGE_TOP_LOCAL = 0.38                   # top of the upper carriage box the pack rests on
TRAVERSE_LEFT_DEG = 102.0                   # positive rotation about +Y turns the pack to the left
TRAVERSE_RIGHT_DEG = 70.0
ELEVATION_MAX_DEG = 55.0
OVER_CAB_HALF_SECTOR_DEG = 34.0             # firing sector over the cab, up to 34 degrees each side of the axis
OVER_CAB_MIN_ELEVATION_DEG = 11.0           # minimum elevation inside that sector, so rockets clear the cab
SECTOR_GEAR_RADIUS = 0.25                   # toothed elevation sector under the cradle, at the base center
CARRIAGE_SLOT_START = 0.20                  # the carriage is split behind this station for the sector

TUBE_ROWS = 4
TUBE_COLUMNS = 10
TUBE_PITCH = 0.158                          # 10 tubes across about 1.58 m on the front view
TUBE_RADIUS = 0.071
BORE_RADIUS = 0.061                         # 122 mm caliber
PACK_REAR_Z = 0.23                          # pack ends in GunPitch space; tubes are 3.0 m long
PACK_FRONT_Z = PACK_REAR_Z - 3.0
PACK_BOTTOM_Y = 0.17                        # tubes start above the cradle, which hangs from the trunnions
PACK_HALF_WIDTH = 0.82
PACK_TOP_Y = PACK_BOTTOM_Y + 0.03 + TUBE_ROWS * TUBE_PITCH + 0.005
LAUNCH_Z = PACK_FRONT_Z - 0.02              # launch points sit just ahead of each tube mouth
BAND_THICKNESS = 0.022
CRADLE_DEPTH = 0.12                         # cradle frame under the tubes, down to just above the trunnion axis

LOD_BUDGETS = (14000, 4000, 800)


def tube_center(row, column):
    """Tube axis position (x, y) in GunPitch space. Row 0 is the top row, column 0 the left column."""
    x = (column - (TUBE_COLUMNS - 1) / 2.0) * TUBE_PITCH
    y = PACK_BOTTOM_Y + 0.03 + TUBE_PITCH / 2.0 + (TUBE_ROWS - 1 - row) * TUBE_PITCH
    return x, y


def pack_y(fraction):
    """Height in GunPitch space at a fraction of the tube block, from its bottom to its top."""
    return PACK_BOTTOM_Y + (PACK_TOP_Y - PACK_BOTTOM_Y) * fraction


def launch_name(row, column):
    return f"Launch_{row * TUBE_COLUMNS + column + 1:02d}"


def firing_order():
    """Authoring choice, not historical data: rows top to bottom, alternating outer left and right tubes inward."""
    order = []
    for row in range(TUBE_ROWS):
        for k in range(TUBE_COLUMNS // 2):
            order.append(launch_name(row, k))
            order.append(launch_name(row, TUBE_COLUMNS - 1 - k))
    return order


def materials(m):
    return {
        "paint": m.material("BM21_Paint", "5a5d3d", metal=0.14, rough=0.74),
        "rubber": m.material("BM21_Rubber", "252522", metal=0.02, rough=0.92),
        "steel": m.material("BM21_DarkSteel", "33362e", metal=0.48, rough=0.52),
        "glass": m.material("BM21_Optics", "1d2a30", metal=0.25, rough=0.14),
        "cavity": m.material("BM21_Cavity", "121310", metal=0.05, rough=0.98),
        "marker_red": m.material("BM21_TailRed", "96261c", metal=0.10, rough=0.42),
        "lamp": m.material("BM21_Lamp", "b9b49a", metal=0.30, rough=0.25),
    }


# ----------------------------------------------------------------------------
# Small geometry helpers
# ----------------------------------------------------------------------------


def box(m, name, x0, x1, y0, y1, z0, z1, mat, parent, tint=1.0):
    """Axis aligned box from bounds in the space of `parent`."""
    m.add_box(name, (x1 - x0, y1 - y0, z1 - z0), ((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), mat, parent, tint=tint)


def prism_x(m, name, profile_zy, x0, x1, mat, parent, tint=1.0):
    """Convex polygon in (z, y) extruded along x from x0 to x1."""
    left = [(x0, y, z) for z, y in profile_zy]
    right = [(x1, y, z) for z, y in profile_zy]
    count = len(profile_zy)
    faces = [tuple(range(count)), tuple(range(count, 2 * count))]
    for i in range(count):
        j = (i + 1) % count
        faces.append((i, j, count + j, count + i))
    m.add_flat_mesh(name, left + right, faces, mat, parent, auto_orient=True, tint=tint)


def tube_x(m, name, radius, x0, x1, center_yz, mat, parent, sides, caps="both"):
    m.add_cylinder(name, radius, abs(x1 - x0), ((x0 + x1) / 2, center_yz[0], center_yz[1]), mat, "x", sides, parent=parent, caps=caps)


def tube_y(m, name, radius, y0, y1, center_xz, mat, parent, sides, caps="both"):
    m.add_cylinder(name, radius, abs(y1 - y0), (center_xz[0], (y0 + y1) / 2, center_xz[1]), mat, "y", sides, parent=parent, caps=caps)


def tube_z(m, name, radius, z0, z1, center_xy, mat, parent, sides, caps="both"):
    m.add_cylinder(name, radius, abs(z1 - z0), (center_xy[0], center_xy[1], (z0 + z1) / 2), mat, "z", sides, parent=parent, caps=caps)


def annulus_z(m, name, inner, outer, center, mat, parent, sides, normal_z):
    """Flat ring facing +Z or -Z."""
    points, normals, indices = [], [], []
    for i in range(sides):
        a = i * math.tau / sides
        c, s = math.cos(a), math.sin(a)
        points.append((center[0] + inner * c, center[1] + inner * s, center[2]))
        points.append((center[0] + outer * c, center[1] + outer * s, center[2]))
        normals += [(0.0, 0.0, normal_z)] * 2
    for i in range(sides):
        j = (i + 1) % sides
        a, b, c, d = 2 * i, 2 * i + 1, 2 * j + 1, 2 * j
        indices += [a, b, c, a, c, d]
    m.add_mesh_direct(name, points, normals, indices, mat, parent)


def arch_fender(m, name, x0, x1, center_zy, radius, thickness, start_deg, end_deg, steps, mat, parent):
    """Curved fender over a wheel: plate segments along an arc in the (z, y) plane."""
    zc, yc = center_zy
    for i in range(steps):
        a0 = math.radians(start_deg + (end_deg - start_deg) * i / steps)
        a1 = math.radians(start_deg + (end_deg - start_deg) * (i + 1) / steps)
        am = (a0 + a1) / 2
        r = radius + thickness / 2
        center = ((x0 + x1) / 2, yc + r * math.sin(am), zc + r * math.cos(am))
        radial = (0.0, math.sin(am), math.cos(am))
        tangent = (0.0, math.cos(am), -math.sin(am))
        half_len = radius * abs(a1 - a0) / 2 * 1.04
        obox(m, name, center, ((1.0, 0.0, 0.0), radial, tangent), ((x1 - x0) / 2, thickness / 2, half_len), mat, parent)


def tire_band(m, name, x, y, z, axis, sign, profile, sides, mat, parent, tint=1.0):
    """Tire surface revolved about an axle along `axis` ("x" or "z") from (axial offset, radius) points.

    For an x axle, angle 0 sits at the bottom, so the lowest vertex touches the ground on every LOD.
    """
    points, normals, indices = [], [], []
    count = len(profile)
    for k, (ax, rad) in enumerate(profile):
        prev, nxt = profile[max(0, k - 1)], profile[min(count - 1, k + 1)]
        dax, drad = nxt[0] - prev[0], nxt[1] - prev[1]
        length = math.hypot(dax, drad) or 1.0
        na, nr = -drad / length, dax / length
        for i in range(sides):
            a = -math.pi / 2 + i * math.tau / sides
            sa, ca = math.sin(a), math.cos(a)
            if axis == "x":
                points.append((x + sign * ax, y + rad * sa, z + rad * ca))
                normals.append((sign * na, nr * sa, nr * ca))
            else:
                points.append((x + rad * ca, y + rad * sa, z + sign * ax))
                normals.append((nr * ca, nr * sa, sign * na))
    for k in range(count - 1):
        for i in range(sides):
            j = (i + 1) % sides
            a, b, c, d = k * sides + i, k * sides + j, (k + 1) * sides + j, (k + 1) * sides + i
            indices += [a, b, c, a, c, d]
    m.add_mesh_direct(name, points, normals, indices, mat, parent, tint=tint)


def tire_profile(lod, hw, r, rim_r):
    tread = 0.98 if lod == 0 else 1.0
    return [(-hw * 0.80, rim_r), (-hw, r * 0.80), (-hw * 0.90, r * tread), (hw * 0.90, r * tread), (hw, r * 0.80), (hw * 0.80, rim_r)]


# ----------------------------------------------------------------------------
# Chassis, hood and cab
# ----------------------------------------------------------------------------


def build_chassis(m, hull, mats, lod):
    paint, steel = mats["paint"], mats["steel"]
    for sign in (-1, 1):
        box(m, "FrameRail", sign * 0.50 - 0.06, sign * 0.50 + 0.06, FRAME_Y[0], FRAME_Y[1], NOSE_Z + 0.05, REAR_Z - 0.12, steel, hull)
    if lod < 2:
        for z in (-3.30, -1.0, 0.5, 1.85, 3.35):
            box(m, "FrameCross", -0.44, 0.44, FRAME_Y[0] + 0.05, FRAME_Y[1] - 0.04, z - 0.05, z + 0.05, steel, hull)
        for z in AXLE_Z:
            tube_x(m, "Axle", 0.08, -WHEEL_X + 0.15, WHEEL_X - 0.15, (WHEEL_Y, z), steel, hull, (10, 6)[lod])
            box(m, "AxleDiff", -0.22, 0.22, WHEEL_Y - AXLE_DROP, WHEEL_Y + 0.18, z - 0.18, z + 0.18, steel, hull)
        for sign in (-1, 1):
            box(m, "BogieSpring", sign * 0.72 - 0.06, sign * 0.72 + 0.06, 0.78, 0.90, AXLE_Z[1] - 0.55, AXLE_Z[2] + 0.55, steel, hull)
            box(m, "FrontSpring", sign * 0.55 - 0.05, sign * 0.55 + 0.05, 0.80, 0.92, AXLE_Z[0] - 0.65, AXLE_Z[0] + 0.65, steel, hull)
    # High square bumper with towing hooks and a skid guard below it
    box(m, "Bumper", -HALF_WIDTH, HALF_WIDTH, BUMPER_Y[0], BUMPER_Y[1], FRONT_Z, FRONT_Z + 0.16, paint, hull, tint=0.92)
    if lod < 2:
        box(m, "SkidGuard", -0.85, 0.85, 0.58, BUMPER_Y[0], FRONT_Z + 0.06, FRONT_Z + 0.12, steel, hull)
    if lod == 0:
        for sign in (-1, 1):
            box(m, "TowHook", sign * 0.55 - 0.05, sign * 0.55 + 0.05, BUMPER_Y[0] + 0.04, BUMPER_Y[0] + 0.16, FRONT_Z - 0.03, FRONT_Z + 0.02, steel, hull)
        for i in range(5):
            x = -0.68 + i * 0.34
            box(m, "SkidGuardBar", x - 0.02, x + 0.02, 0.58, BUMPER_Y[0], FRONT_Z + 0.04, FRONT_Z + 0.06, steel, hull)


def wing_section(sign, bottom_y, lod):
    """Front wing cross-section in (x, y), counterclockwise, with a rounded outer top edge."""
    x0, x1, top = HOOD_HALF_WIDTH, HALF_WIDTH, WING_TOP_Y
    c = (0.16, 0.12, 0.0)[lod]
    if sign > 0:
        return [(x0, bottom_y), (x1, bottom_y), (x1, top - c), (x1 - c, top), (x0, top)]
    return [(-x1, bottom_y), (-x0, bottom_y), (-x0, top), (-x1 + c, top), (-x1, top - c)]


def build_front(m, hull, mats, lod):
    """Ural front end: a tall wide hood between big flat-topped wings, grille and headlights in front."""
    paint, steel, cavity, lamp = mats["paint"], mats["steel"], mats["cavity"], mats["lamp"]
    chamfer = (0.16, 0.12, 0.0)[lod]
    w = HOOD_HALF_WIDTH
    front = hex_section(w, WING_TOP_Y - 0.02, w - 0.02, HOOD_FRONT_TOP_Y, chamfer)
    rear = hex_section(w, WING_TOP_Y - 0.02, w - 0.02, HOOD_REAR_TOP_Y, chamfer)
    loft_sections(m, "Hood", [(NOSE_Z, front), (CAB_FRONT_Z, rear)], paint, hull)
    # Grille panel and radiator face under the hood front, between the wings
    box(m, "GrilleFace", -w + 0.02, w - 0.02, BUMPER_Y[1], WING_TOP_Y, NOSE_Z, NOSE_Z + 0.30, paint, hull, tint=0.93)
    box(m, "Grille", -0.46, 0.46, GRILLE_Y[0], GRILLE_Y[1], NOSE_Z - 0.02, NOSE_Z + 0.005, cavity, hull)
    if lod == 0:
        for i in range(10):
            x = -0.405 + i * 0.09
            box(m, "GrilleBar", x - 0.016, x + 0.016, GRILLE_Y[0] + 0.02, GRILLE_Y[1] - 0.02, NOSE_Z - 0.035, NOSE_Z - 0.01, paint, hull)
        for sign in (-1, 1):
            # Side bead along the hood and the hood hinge line read from the side and from above
            box(m, "HoodBead", sign * (w + 0.002) - 0.01, sign * (w + 0.002) + 0.01, HOOD_REAR_TOP_Y - 0.30, HOOD_REAR_TOP_Y - 0.26, NOSE_Z + 0.25, CAB_FRONT_Z - 0.05, paint, hull, tint=0.9)
    arch_front, arch_rear = AXLE_Z[0] - ARCH_HALF_LENGTH, AXLE_Z[0] + ARCH_HALF_LENGTH
    for sign in (-1, 1):
        # Wings: a full height block ahead of the wheel and a high block over the wheel arch
        loft_sections(m, "WingFront", [(NOSE_Z, wing_section(sign, BUMPER_Y[1], lod)), (arch_front, wing_section(sign, BUMPER_Y[1], lod))], paint, hull)
        loft_sections(m, "WingArch", [(arch_front, wing_section(sign, ARCH_TOP_Y, lod)), (CAB_FRONT_Z, wing_section(sign, ARCH_TOP_Y, lod))], paint, hull)
        if lod < 2:
            build_wheel_arch(m, sign, hull, mats, lod)
        lamp_x = sign * HEADLIGHT_X
        sides = (12, 8, 6)[lod]
        tube_z(m, "Headlight", 0.13, NOSE_Z - 0.08, NOSE_Z + 0.02, (lamp_x, HEADLIGHT_Y), paint, hull, sides)
        m.add_disc("HeadlightLens", 0.105, (lamp_x, HEADLIGHT_Y, NOSE_Z - 0.085), lamp, "z", sides, hull, -1.0)
        if lod == 0:
            box(m, "SideLight", lamp_x - 0.06, lamp_x + 0.06, HEADLIGHT_Y + 0.20, HEADLIGHT_Y + 0.27, NOSE_Z - 0.03, NOSE_Z + 0.01, lamp, hull)


def build_wheel_arch(m, sign, hull, mats, lod):
    """Rounded front wheel arch: the wing side panel comes down around the tire, with a curved lip."""
    zc, yc = AXLE_Z[0], WHEEL_Y
    radius = ARCH_TOP_Y - WHEEL_Y
    steps = (10, 6)[lod]
    # Arc angles from the cab front (rear end of the wing) round to the wing front block
    a_rear = math.degrees(math.acos(max(-1.0, min(1.0, (CAB_FRONT_Z - zc) / radius))))
    angles = [a_rear + (180.0 - a_rear) * i / steps for i in range(steps + 1)]
    x = sign * (HALF_WIDTH + 0.002)
    points = []
    for a in angles:
        z = zc + radius * math.cos(math.radians(a))
        y = max(yc + radius * math.sin(math.radians(a)), BUMPER_Y[1])
        points.append((z, y))
    faces, vertices = [], []
    for (z0, y0), (z1, y1) in zip(points[:-1], points[1:]):
        if y0 >= ARCH_TOP_Y - 1e-4 and y1 >= ARCH_TOP_Y - 1e-4:
            continue
        start = len(vertices)
        vertices += [(x, y0, z0), (x, y1, z1), (x, ARCH_TOP_Y + 0.01, z1), (x, ARCH_TOP_Y + 0.01, z0)]
        faces.append((start, start + 1, start + 2, start + 3))
    if faces:
        m.add_flat_mesh("WingSkirt", vertices, faces, mats["paint"], hull)
    x0, x1 = sorted((sign * (HOOD_HALF_WIDTH + 0.04), sign * HALF_WIDTH))
    lip_start = max(a_rear, 25.0)
    arch_fender(m, "ArchLip", x0, x1, (zc, yc), radius - 0.03, 0.03, lip_start, 180.0 - 15.0, steps, mats["cavity"], hull)


def cab_section(top_y, lod):
    """Cab cross-section in (x, y): slight tumblehome and rounded roof edges."""
    chamfer = (0.14, 0.11, 0.0)[lod]
    return hex_section(CAB_HALF_WIDTH, CAB_BOTTOM_Y, CAB_HALF_WIDTH - 0.04, top_y, chamfer)


def build_cab(m, hull, mats, lod):
    paint, glass, steel, cavity = mats["paint"], mats["glass"], mats["steel"], mats["cavity"]
    w = CAB_HALF_WIDTH - 0.04
    chamfer = (0.14, 0.11, 0.0)[lod]
    # Short all-metal cab: the first station stops at the belt line, the second at the roof, which rakes the windscreen.
    stations = [(CAB_FRONT_Z, cab_section(CAB_BELT_Y, lod)), (CAB_FRONT_Z + CAB_RAKE, cab_section(CAB_ROOF_Y, lod)), (CAB_REAR_Z, cab_section(CAB_ROOF_Y, lod))]
    loft_sections(m, "Cab", stations, paint, hull)
    # Two flat windscreen panes split by a center pillar
    y0, y1 = CAB_BELT_Y + 0.05, CAB_ROOF_Y - chamfer - 0.05
    z0 = CAB_FRONT_Z + (y0 - CAB_BELT_Y) / (CAB_ROOF_Y - CAB_BELT_Y) * CAB_RAKE - 0.01
    z1 = CAB_FRONT_Z + (y1 - CAB_BELT_Y) / (CAB_ROOF_Y - CAB_BELT_Y) * CAB_RAKE - 0.01
    panes = [(-(w - chamfer - 0.04), w - chamfer - 0.04)] if lod == 2 else [(-(w - chamfer - 0.04), -0.04), (0.04, w - chamfer - 0.04)]
    for x0, x1 in panes:
        m.add_flat_mesh("Windscreen", [(x0, y0, z0), (x1, y0, z0), (x1, y1, z1), (x0, y1, z1)], [(0, 1, 2, 3)], glass, hull)
    # Air intake pipe up the right front corner of the cab, with a mushroom cap
    intake_x, intake_z = HOOD_HALF_WIDTH + 0.16, CAB_FRONT_Z - 0.10
    tube_y(m, "AirIntake", 0.07, WING_TOP_Y, CAB_ROOF_Y - 0.12, (intake_x, intake_z), steel, hull, (10, 6, 6)[lod], caps="neg")
    m.add_cylinder("AirIntakeCap", 0.11, 0.10, (intake_x, CAB_ROOF_Y - 0.08, intake_z), steel, "y", (10, 6, 6)[lod], parent=hull)
    if lod == 2:
        return

    def side_x(y):
        # Cab sides lean in toward the roof; windows follow the wall
        yb, yt = CAB_BOTTOM_Y, CAB_ROOF_Y - chamfer
        return CAB_HALF_WIDTH + (w - CAB_HALF_WIDTH) * (y - yb) / (yt - yb) + 0.006

    wy0, wy1 = CAB_BELT_Y + 0.05, CAB_ROOF_Y - chamfer - 0.05
    for sign in (-1, 1):
        xa, xb = sign * side_x(wy0), sign * side_x(wy1)
        z0, z1 = CAB_FRONT_Z + CAB_RAKE + 0.08, CAB_REAR_Z - 0.16
        m.add_flat_mesh("DoorWindow", [(xa, wy0, z0), (xa, wy0, z1), (xb, wy1, z1), (xb, wy1, z0)], [(0, 1, 2, 3)], glass, hull)
        # Two step plates under the door
        x0, x1 = sorted((sign * (CAB_HALF_WIDTH - 0.10), sign * (CAB_HALF_WIDTH + 0.05)))
        for step_y in (0.55, 0.82):
            box(m, "CabStep", x0, x1, step_y, step_y + 0.04, CAB_FRONT_Z + 0.25, CAB_FRONT_Z + 0.70, steel, hull)
        # Large mirror frames on long arms
        mx = sign * MIRROR_X
        box(m, "MirrorArm", min(sign * CAB_HALF_WIDTH, mx), max(sign * CAB_HALF_WIDTH, mx), CAB_BELT_Y + 0.02, CAB_BELT_Y + 0.05, CAB_FRONT_Z + 0.06, CAB_FRONT_Z + 0.09, steel, hull)
        box(m, "Mirror", mx - 0.03, mx + 0.03, CAB_BELT_Y - 0.05, CAB_BELT_Y + 0.32, CAB_FRONT_Z + 0.03, CAB_FRONT_Z + 0.12, steel, hull)
    m.add_flat_mesh("RearWindow", [(-0.40, CAB_BELT_Y + 0.08, CAB_REAR_Z + 0.006), (0.40, CAB_BELT_Y + 0.08, CAB_REAR_Z + 0.006), (0.40, CAB_ROOF_Y - 0.24, CAB_REAR_Z + 0.006), (-0.40, CAB_ROOF_Y - 0.24, CAB_REAR_Z + 0.006)], [(0, 1, 2, 3)], glass, hull)
    if lod == 0:
        box(m, "WindscreenPillar", -0.04, 0.04, y0, y1, z0 - 0.005, z1 - 0.005, paint, hull)
        for sign in (-1, 1):
            seam_x = sign * (CAB_HALF_WIDTH + 0.004)
            box(m, "DoorHandle", seam_x - 0.012, seam_x + 0.012, CAB_BELT_Y - 0.16, CAB_BELT_Y - 0.12, CAB_REAR_Z - 0.40, CAB_REAR_Z - 0.28, steel, hull)
            box(m, "DoorSeam", seam_x - 0.005, seam_x + 0.005, CAB_BOTTOM_Y + 0.05, CAB_BELT_Y - 0.02, CAB_REAR_Z - 0.14, CAB_REAR_Z - 0.12, cavity, hull)
            gx = sign * (w - chamfer + 0.01)
            box(m, "RainGutter", gx - 0.015, gx + 0.015, CAB_ROOF_Y - 0.012, CAB_ROOF_Y + 0.004, CAB_FRONT_Z + CAB_RAKE + 0.02, CAB_REAR_Z - 0.02, steel, hull)
        box(m, "RoofLamp", -0.08, 0.08, CAB_ROOF_Y, CAB_ROOF_Y + 0.05, CAB_FRONT_Z + CAB_RAKE + 0.06, CAB_FRONT_Z + CAB_RAKE + 0.14, mats["lamp"], hull)


def build_rear_body(m, hull, mats, lod):
    paint, steel, red, rubber = mats["paint"], mats["steel"], mats["marker_red"], mats["rubber"]
    box(m, "PlatformDeck", -1.10, 1.10, FRAME_Y[1], DECK_Y, DECK_START_Z, REAR_Z - 0.06, paint, hull)
    for sign in (-1, 1):
        if lod < 2:
            tube_z(m, "FuelTank", 0.24, AXLE_Z[0] + WHEEL_R + 0.12, CAB_REAR_Z + 0.20, (sign * 0.90, 0.78), paint, hull, (12, 8)[lod])
        x0, x1 = sorted((sign * 0.62, sign * 1.14))
        box(m, "Locker", x0, x1, 0.72, FRAME_Y[1], CAB_REAR_Z + 0.30, AXLE_Z[1] - WHEEL_R - 0.10, paint, hull, tint=0.94)
        x0, x1 = sorted((sign * 0.78, sign * HALF_WIDTH))
        box(m, "RearMudguard", x0, x1, REAR_GUARD_Y, REAR_GUARD_Y + 0.04, AXLE_Z[1] - WHEEL_R - 0.10, AXLE_Z[2] + WHEEL_R + 0.10, paint, hull)
        if lod < 2:
            box(m, "MudFlap", x0, x1, 0.45, REAR_GUARD_Y, AXLE_Z[2] + WHEEL_R + 0.08, AXLE_Z[2] + WHEEL_R + 0.11, rubber, hull)
            box(m, "MudguardBrace", sign * 0.90 - 0.03, sign * 0.90 + 0.03, FRAME_Y[1], REAR_GUARD_Y, (AXLE_Z[1] + AXLE_Z[2]) / 2 - 0.03, (AXLE_Z[1] + AXLE_Z[2]) / 2 + 0.03, steel, hull)
    # Equipment boxes on the deck between the spare wheel and the launcher; the pack passes above them
    box(m, "DeckEquipment", -0.62, 0.62, DECK_Y, DECK_EQUIPMENT_TOP_Y - 0.25, DECK_EQUIPMENT_Z[0], DECK_EQUIPMENT_Z[1], paint, hull, tint=0.92)
    if lod < 2:
        box(m, "DeckEquipmentTop", -0.40, 0.30, DECK_EQUIPMENT_TOP_Y - 0.25, DECK_EQUIPMENT_TOP_Y, DECK_EQUIPMENT_Z[0] + 0.15, DECK_EQUIPMENT_Z[1] - 0.20, steel, hull)
        for sign in (-1, 1):
            bx0, bx1 = sorted((sign * 0.78, sign * 1.10))
            box(m, "DeckBox", bx0, bx1, DECK_Y, DECK_Y + 0.34, DECK_EQUIPMENT_Z[0], DECK_EQUIPMENT_Z[1] + 0.30, paint, hull, tint=0.88)
    box(m, "RearBeam", -1.10, 1.10, 0.90, FRAME_Y[1], REAR_Z - 0.16, REAR_Z - 0.04, steel, hull)
    # Rear stabilizing jacks, stowed: a vertical screw jack at each rear corner
    for sign in (-1, 1):
        jx = sign * 0.95
        if lod < 2:
            tube_y(m, "RearJack", 0.07, 0.46, 1.20, (jx, REAR_Z - 0.30), steel, hull, (10, 6)[lod])
            box(m, "RearJackFoot", jx - 0.14, jx + 0.14, 0.42, 0.46, REAR_Z - 0.44, REAR_Z - 0.16, steel, hull)
        else:
            box(m, "RearJack", jx - 0.07, jx + 0.07, 0.44, 1.20, REAR_Z - 0.37, REAR_Z - 0.23, steel, hull)
    if lod < 2:
        for sign in (-1, 1):
            box(m, "TailLight", sign * 0.70 - 0.07, sign * 0.70 + 0.07, 0.96, 1.08, REAR_Z - 0.04, REAR_Z, red, hull)
        box(m, "TowPintle", -0.08, 0.08, 0.92, 1.04, REAR_Z - 0.04, REAR_Z, steel, hull)
        m.add_cylinder("TurntableBase", 0.78, 0.04, (TURRET_POS[0], DECK_Y - 0.01, TURRET_POS[2]), steel, "y", (24, 12)[lod], parent=hull, caps="pos")
    build_spare_wheel(m, hull, mats, lod)


def build_spare_wheel(m, hull, mats, lod):
    """Spare wheel standing upright behind the cab in a tall frame, as on the Ural-375D."""
    x, y, z = SPARE_CENTER
    hw = TIRE_WIDTH / 2
    sides = (16, 10, 8)[lod]
    rim_r = WHEEL_R * 0.55
    if lod == 2:
        m.add_cylinder("SpareTire", WHEEL_R, TIRE_WIDTH, (x, y, z), mats["rubber"], "z", sides, parent=hull)
        return
    tire_band(m, "SpareTire", x, y, z, "z", 1, tire_profile(1, hw, WHEEL_R, rim_r), sides, mats["rubber"], hull, tint=0.9)
    m.add_disc("SpareRim", rim_r * 1.02, (x, y, z + hw * 0.6), mats["paint"], "z", sides, hull, 1.0)
    m.add_disc("SpareRimBack", rim_r * 1.02, (x, y, z - hw * 0.6), mats["paint"], "z", sides, hull, -1.0)
    for sign in (-1, 1):
        box(m, "SpareFrame", sign * 0.50 - 0.04, sign * 0.50 + 0.04, FRAME_Y[1], y + 0.30, z + hw + 0.01, z + hw + 0.07, mats["steel"], hull)
    box(m, "SpareFrameTop", -0.54, 0.54, y + 0.26, y + 0.32, z + hw + 0.01, z + hw + 0.07, mats["steel"], hull)


# ----------------------------------------------------------------------------
# Running gear
# ----------------------------------------------------------------------------


def road_wheel(m, sign, z, mats, parent, lod):
    x = sign * WHEEL_X
    y, r = WHEEL_Y, WHEEL_R
    hw = TIRE_WIDTH / 2
    sides = (18, 10, 8)[lod]
    rim_r = r * 0.55
    if lod == 2:
        tire_band(m, "Tire", x, y, z, "x", sign, [(-hw, r), (hw, r)], sides, mats["rubber"], parent)
        m.add_disc("TireWall", r * 0.95, (x + sign * hw, y, z), mats["rubber"], "x", sides, parent, float(sign))
        return
    tire_band(m, "TireCarcass", x, y, z, "x", sign, tire_profile(lod, hw, r, rim_r), sides, mats["rubber"], parent, tint=0.92)
    rim_x = x + sign * hw * 0.55
    m.add_disc("RimShadow", rim_r * 1.02, (rim_x, y, z), mats["cavity"], "x", sides, parent, float(sign))
    m.add_disc("RimDish", rim_r * 0.92, (rim_x + sign * 0.010, y, z), mats["paint"], "x", sides, parent, float(sign))
    m.add_cylinder("Hub", r * 0.22, 0.08, (rim_x + sign * 0.050, y, z), mats["steel"], "x", max(6, sides // 2), parent=parent, caps="pos" if sign > 0 else "neg")
    if lod == 0:
        # One lug bar across the tread per step: enough to read as an off-road tire at close zoom
        lugs = 14
        for i in range(lugs):
            a = -math.pi / 2 + i * math.tau / lugs
            radial = (0.0, math.sin(a), math.cos(a))
            tangent = (0.0, math.cos(a), -math.sin(a))
            center = (x, y + radial[1] * (r - 0.016), z + radial[2] * (r - 0.016))
            obox(m, "TireLug", center, ((1.0, 0.0, 0.0), radial, tangent), (TIRE_WIDTH * 0.42, 0.016, 0.05), mats["rubber"], parent, tint=0.82)
        for i in range(6):
            a = i * math.tau / 6
            m.add_disc("RimBolt", 0.016, (rim_x + sign * 0.018, y + rim_r * 0.62 * math.sin(a), z + rim_r * 0.62 * math.cos(a)), mats["steel"], "x", 4, parent, float(sign))


def build_running_gear(m, side, sign, mats, lod):
    for z in AXLE_Z:
        first = len(m.parts)
        road_wheel(m, sign, z, mats, side, lod)
        tag_wheel(m, first, (WHEEL_Y, z), WHEEL_R)


# ----------------------------------------------------------------------------
# Launcher: turntable, cradle and the 40 tube pack
# ----------------------------------------------------------------------------


def build_turntable(m, turret, mats, lod):
    """Rotating part: turntable, the low upper carriage the pack rests on, trunnion supports at the rear."""
    paint, steel, glass = mats["paint"], mats["steel"], mats["glass"]
    sides = (24, 12, 8)[lod]
    m.add_cylinder("Turntable", 0.72, 0.08, (0.0, 0.04, 0.0), paint, "y", sides, parent=turret, caps="pos" if lod < 2 else "both")
    tx, ty, tz = TRUNNION_LOCAL
    # Upper carriage: a long low box from the turntable forward, with a slanted front. Behind
    # CARRIAGE_SLOT_START it splits in two, leaving a center slot for the elevation sector.
    carriage = [(-1.35, 0.08), (CARRIAGE_SLOT_START, 0.08), (CARRIAGE_SLOT_START, CARRIAGE_TOP_LOCAL), (-1.20, CARRIAGE_TOP_LOCAL), (-1.35, 0.22)]
    prism_x(m, "Carriage", carriage, -0.74, 0.74, paint, turret, tint=0.95)
    rear = [(CARRIAGE_SLOT_START, 0.08), (tz - 0.20, 0.08), (tz - 0.20, CARRIAGE_TOP_LOCAL), (CARRIAGE_SLOT_START, CARRIAGE_TOP_LOCAL)]
    for sign in (-1, 1):
        x0, x1 = sorted((sign * 0.08, sign * 0.74))
        prism_x(m, "CarriageRear", rear, x0, x1, paint, turret, tint=0.95)
        # Trunnion supports with bearing housings, outside the pack
        x0, x1 = sorted((sign * (PACK_HALF_WIDTH + 0.04), sign * (PACK_HALF_WIDTH + 0.12)))
        box(m, "TrunnionSupport", x0, x1, 0.08, ty + 0.08, tz - 0.14, tz + 0.14, paint, turret, tint=0.92)
        if lod < 2:
            gx0, gx1 = sorted((sign * (PACK_HALF_WIDTH + 0.12), sign * (PACK_HALF_WIDTH + 0.22)))
            box(m, "TrunnionBearing", gx0, gx1, ty - 0.10, ty + 0.10, tz - 0.10, tz + 0.10, steel, turret)
    if lod < 2:
        # The traverse mechanism sits on the left side of the base
        box(m, "TraverseGearbox", -0.92, -0.76, 0.08, 0.30, -0.25, 0.15, steel, turret)
        # Gun layer's sight box on the left front of the carriage, outside the pack
        box(m, "SightBox", -1.06, -0.90, 0.08, 0.62, -1.20, -0.86, paint, turret, tint=0.9)
        box(m, "SightHead", -1.02, -0.92, 0.62, 0.76, -1.10, -0.98, steel, turret)
        if lod == 0:
            box(m, "SightLens", -1.01, -0.93, 0.66, 0.72, -1.106, -1.10, glass, turret)
            for z in (-1.00, -0.40, 0.10):
                box(m, "CarriageRib", -0.745, 0.745, 0.10, CARRIAGE_TOP_LOCAL - 0.02, z - 0.02, z + 0.02, paint, turret, tint=0.88)


def build_pack(m, pitch, mats, lod):
    paint, steel, cavity = mats["paint"], mats["steel"], mats["cavity"]
    length = PACK_REAR_Z - PACK_FRONT_Z
    tube_x(m, "Trunnion", 0.07, -(PACK_HALF_WIDTH + 0.12), PACK_HALF_WIDTH + 0.12, (0.0, 0.0), steel, pitch, (12, 8, 6)[lod])
    # Toothed elevation sector fixed under the cradle; it turns down into the carriage slot
    if lod < 2:
        sector_steps = (8, 4)[lod]
        fan = [(0.0, 0.0)] + [(-SECTOR_GEAR_RADIUS * math.sin(math.radians(70.0 * i / sector_steps)), -SECTOR_GEAR_RADIUS * math.cos(math.radians(70.0 * i / sector_steps))) for i in range(sector_steps + 1)]
        prism_x(m, "ElevationSector", fan, -0.04, 0.04, steel, pitch)
    # Cradle frame under the tubes: two side rails and cross members, the rear end on the trunnions
    for sign in (-1, 1):
        x0, x1 = sorted((sign * 0.52, sign * 0.66))
        box(m, "CradleRail", x0, x1, PACK_BOTTOM_Y - CRADLE_DEPTH, PACK_BOTTOM_Y, PACK_FRONT_Z + 0.60, PACK_REAR_Z, steel, pitch)
    box(m, "CradleBeam", -0.66, 0.66, PACK_BOTTOM_Y - CRADLE_DEPTH, PACK_BOTTOM_Y, PACK_REAR_Z - 0.40, PACK_REAR_Z, steel, pitch)
    if lod < 2:
        for z in (PACK_FRONT_Z + 0.70, PACK_FRONT_Z + 1.60):
            box(m, "CradleCross", -0.52, 0.52, PACK_BOTTOM_Y - CRADLE_DEPTH * 0.6, PACK_BOTTOM_Y, z - 0.05, z + 0.05, steel, pitch)
    if lod == 2:
        box(m, "PackBlock", -PACK_HALF_WIDTH, PACK_HALF_WIDTH, PACK_BOTTOM_Y, PACK_TOP_Y, PACK_FRONT_Z, PACK_REAR_Z, paint, pitch)
        for row in range(TUBE_ROWS):
            _, y = tube_center(row, 0)
            box(m, "TubeMouths", -PACK_HALF_WIDTH + 0.03, PACK_HALF_WIDTH - 0.03, y - 0.05, y + 0.05, PACK_FRONT_Z - 0.006, PACK_FRONT_Z - 0.002, cavity, pitch)
        return

    sides = (12, 8)[lod]
    for row in range(TUBE_ROWS):
        for column in range(TUBE_COLUMNS):
            x, y = tube_center(row, column)
            tube_z(m, "Tube", TUBE_RADIUS, PACK_FRONT_Z, PACK_REAR_Z, (x, y), paint, pitch, sides, caps="none")
            if lod == 0:
                annulus_z(m, "TubeMuzzleRing", BORE_RADIUS, TUBE_RADIUS, (x, y, PACK_FRONT_Z), steel, pitch, sides, -1.0)
                m.add_disc("TubeBore", BORE_RADIUS, (x, y, PACK_FRONT_Z + 0.08), cavity, "z", sides, pitch, -1.0)
            else:
                m.add_disc("TubeBore", TUBE_RADIUS * 0.98, (x, y, PACK_FRONT_Z - 0.001), cavity, "z", sides, pitch, -1.0)
    # The rear ends read as one breech plate with the firing harness box
    box(m, "BreechPlate", -PACK_HALF_WIDTH + 0.02, PACK_HALF_WIDTH - 0.02, PACK_BOTTOM_Y + 0.03, PACK_TOP_Y - 0.01, PACK_REAR_Z, PACK_REAR_Z + 0.02, steel, pitch)
    if lod == 0:
        box(m, "HarnessBox", -0.30, 0.30, pack_y(0.30), pack_y(0.70), PACK_REAR_Z + 0.02, PACK_REAR_Z + 0.10, steel, pitch)

    bands = (PACK_FRONT_Z + 0.25, PACK_FRONT_Z + length * 0.48, PACK_REAR_Z - 0.25) if lod == 0 else (PACK_FRONT_Z + 0.25, PACK_REAR_Z - 0.25)
    t = BAND_THICKNESS
    for z in bands:
        box(m, "BandTop", -PACK_HALF_WIDTH, PACK_HALF_WIDTH, PACK_TOP_Y - 0.005, PACK_TOP_Y + t, z - 0.05, z + 0.05, paint, pitch, tint=0.92)
        box(m, "BandBottom", -PACK_HALF_WIDTH, PACK_HALF_WIDTH, PACK_BOTTOM_Y, PACK_BOTTOM_Y + 0.03, z - 0.05, z + 0.05, paint, pitch, tint=0.92)
        for sign in (-1, 1):
            x0, x1 = sorted((sign * (PACK_HALF_WIDTH - 0.005), sign * (PACK_HALF_WIDTH + t)))
            box(m, "BandSide", x0, x1, PACK_BOTTOM_Y, PACK_TOP_Y + t, z - 0.05, z + 0.05, paint, pitch, tint=0.92)
    if lod == 0:
        for sign in (-1, 1):
            x0, x1 = sorted((sign * (PACK_HALF_WIDTH - 0.01), sign * (PACK_HALF_WIDTH + 0.012)))
            box(m, "PackRail", x0, x1, pack_y(0.45), pack_y(0.55), PACK_FRONT_Z + 0.20, PACK_REAR_Z - 0.20, steel, pitch)


# ----------------------------------------------------------------------------
# Weathering and assembly
# ----------------------------------------------------------------------------


def bake_wear_and_grime(m, glass=None):
    """Wheel AO is made radial so turning wheels show no static shadow wedge. Other parts get a
    small panel variation, brighter upward edges and mud splash low on the body, like the BTR-70."""
    motion = getattr(m, "motion", {})
    for index, (node_id, parent, mat, points, normals, indices, colors) in enumerate(m.parts):
        pts = np.asarray(points, dtype=float)
        info = motion.get(node_id)
        if mat == glass:
            colors = [(0.9, 0.9, 0.9, 0.0)] * len(points)
        elif info is not None and info[0] == "wheel":
            _, (cy, cz), r = info
            rad = np.hypot(pts[:, 1] - cy, pts[:, 2] - cz) / max(r, 1e-6)
            ao = np.clip(0.70 + 0.26 * rad, 0.65, 0.96)
            dirt = np.clip(0.30 + 0.35 * rad**1.5, 0.25, 0.70)
            colors = [(float(a), float(a), float(a), float(d)) for a, d in zip(ao, dirt)]
        else:
            cols = np.asarray(colors, dtype=float)
            nrms = np.asarray(normals, dtype=float)
            hx = np.sin(pts[:, 0] * 6.3 + pts[:, 2] * 4.7 + pts[:, 1] * 8.1)
            hy = np.cos(pts[:, 0] * 13.7 - pts[:, 2] * 11.3 + pts[:, 1] * 5.9)
            micro = 1.0 + 0.040 * hx + 0.020 * hy
            upward = np.clip(nrms[:, 1], 0.0, 1.0)
            rgb = np.clip(cols[:, :3] * (micro * (1.0 + 0.05 * upward))[:, None], 0.18, 1.15)
            splash = np.clip((1.45 - pts[:, 1]) / 1.15, 0.0, 1.0) * (0.18 + 0.14 * np.abs(hx))
            alpha = np.clip(cols[:, 3] + splash, 0.0, 1.0)
            colors = [(float(c[0]), float(c[1]), float(c[2]), float(a)) for c, a in zip(rgb, alpha)]
        m.parts[index] = (node_id, parent, mat, points, normals, indices, colors)


def build_bm21_lod(lod=0, output_dir=None):
    m = GltfModel(f"BM21_LOD{lod}")
    m.data["asset"]["generator"] = "Dead Ground BM-21 model authoring"
    mats = materials(m)
    hull = m.node("Hull")
    left = m.node("LeftTrack")
    right = m.node("RightTrack")
    turret = m.node("TurretYaw", TURRET_POS)
    pitch = m.node("GunPitch", TRUNNION_LOCAL, turret)
    m.node("Muzzle", (0.0, PACK_BOTTOM_Y + (PACK_TOP_Y - PACK_BOTTOM_Y) / 2, LAUNCH_Z), pitch)
    for row in range(TUBE_ROWS):
        for column in range(TUBE_COLUMNS):
            x, y = tube_center(row, column)
            m.node(launch_name(row, column), (x, y, LAUNCH_Z), pitch)

    build_chassis(m, hull, mats, lod)
    build_front(m, hull, mats, lod)
    build_cab(m, hull, mats, lod)
    build_rear_body(m, hull, mats, lod)
    build_running_gear(m, left, -1, mats, lod)
    build_running_gear(m, right, 1, mats, lod)
    build_turntable(m, turret, mats, lod)
    build_pack(m, pitch, mats, lod)

    bake_vertex_shading(m, gear_nodes=("LeftTrack", "RightTrack"), max_edge=(0.60, None, None)[lod])
    bake_wear_and_grime(m, glass=mats["glass"])
    emit_motion_parts(m)

    name = "bm21.glb" if lod == 0 else f"bm21_lod{lod}.glb"
    out_dir = Path(output_dir) if output_dir else DEFAULT_OUTPUT_DIR
    out_dir.mkdir(parents=True, exist_ok=True)
    stats = m.save(out_dir / name)
    stats["lod"] = lod
    stats["role"] = ("close", "rts", "distant")[lod]
    stats["triangle_budget"] = LOD_BUDGETS[lod]
    return stats


def wheel_wrap_period(wheel_radius, target=1000.0):
    """Whole number of wheel turns near `target` meters, so wheel rotation wraps without a jump."""
    circumference = math.tau * wheel_radius
    return max(1, round(target / circumference)) * circumference


def pack_reach():
    """Horizontal distance from the traverse axis to the farthest front corner of the pack at zero elevation."""
    front = -(TRUNNION_LOCAL[2] + PACK_FRONT_Z - 0.01)  # the pack front lies ahead (-Z) of the axis
    return math.hypot(PACK_HALF_WIDTH + BAND_THICKNESS, max(front, 0.0))


# ----------------------------------------------------------------------------
# Measurements from the written GLB files
# ----------------------------------------------------------------------------


def read_glb(path):
    """Return (gltf json, binary chunk) of a GLB file."""
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
    """World translation of every node in the rest pose (nodes carry translations only)."""
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
    """Rest pose bounds measured from the exported file."""
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
    lods = [build_bm21_lod(lod, out_dir) for lod in range(3)]
    bounds = measure_glb(out_dir / "bm21.glb")
    lo, hi = bounds["min"], bounds["max"]
    trunnion_world = [TURRET_POS[i] + TRUNNION_LOCAL[i] for i in range(3)]
    launch_points = []
    for row in range(TUBE_ROWS):
        for column in range(TUBE_COLUMNS):
            x, y = tube_center(row, column)
            launch_points.append({"name": launch_name(row, column), "row": row, "column": column, "local_position": [round(x, 4), round(y, 4), round(LAUNCH_Z, 4)]})
    manifest = {
        "asset": "BM-21 (2B5) 122 mm MRL on a Ural-375D 6x6 truck, RTS visual study",
        "configuration": "Simplified launcher for artillery tests: Ural-375D 6x6 chassis with a tall hood between flat-topped wings, high bumper, short all-metal cab, upright spare wheel and deck equipment behind the cab, turntable over the rear bogie with rear jacks, and a 40 tube pack in 4 rows of 10 pointing forward",
        "units": "meters",
        "up": "+Y",
        "forward": "-Z",
        "right": "+X",
        "ground_y": 0.0,
        "node_scale": "1 on every node; no node rotation in the rest pose",
        "color_authoring": "sRGB hex converted to linear glTF factors",
        "vertex_colors": "rgb: tint x baked ambient occlusion x panel variation, a: dirt mask",
        "sources": SOURCES,
        "reference_anchors": {
            "note": "published figures; stations along the length are scaled from a non-factory side view drawing (the-blueprints.com) to these figures",
            "overall_length_meters": REFERENCE_LENGTH,
            "overall_width_meters": REFERENCE_WIDTH,
            "travel_height_meters": REFERENCE_TRAVEL_HEIGHT,
            "cab_height_meters": REFERENCE_CAB_HEIGHT,
            "ground_clearance_meters": REFERENCE_CLEARANCE,
            "wheelbase_front_to_first_rear_meters": round(AXLE_Z[1] - AXLE_Z[0], 4),
            "rear_axle_spacing_meters": round(AXLE_Z[2] - AXLE_Z[1], 4),
            "wheel_track_meters": TRACK,
            "tube_length_meters": 3.0,
            "caliber_meters": 0.122,
            "conflicts": [
                "Width: 2,400 mm for the combat vehicle (Russian Wikipedia, Grad MLRS) against 2,690 mm for the Ural-375D truck (Russian Wikipedia, Ural-375D). The body uses 2.40 m; mirrors reach past it.",
                "Wheel radius 0.61 m comes from the drawing; the tire size for the 375D was not confirmed in text sources.",
                "Travel height: 3,090 mm is published, but the drawing, a side photo and a travel position photo show the pack top about level with the 2.68 m cab roof. The model follows the drawing and photos, so its measured height is lower than the published figure.",
            ],
        },
        "measured": {
            "bounds_min": lo,
            "bounds_max": hi,
            "overall_length_meters": round(hi[2] - lo[2], 4),
            "overall_width_meters": round(hi[0] - lo[0], 4),
            "travel_height_meters": round(hi[1], 4),
            "lowest_point_meters": round(lo[1], 4),
            "cab_roof_meters": CAB_ROOF_Y,
            "tube_length_meters": round(PACK_REAR_Z - PACK_FRONT_Z, 4),
        },
        "rig": {
            "TurretYaw": {
                "role": "launcher turntable",
                "position": list(TURRET_POS),
                "axis": "+Y",
                "traverse_left_degrees": TRAVERSE_LEFT_DEG,
                "traverse_right_degrees": TRAVERSE_RIGHT_DEG,
                "rotation_y_range_degrees": [-TRAVERSE_RIGHT_DEG, TRAVERSE_LEFT_DEG],
                "traverse_verified": True,
                "traverse_source": "https://vk.ru/@oo_svk-rszo-bm-21-grad",
                "traverse_conflict": "the same source also gives 110 degrees left in a later paragraph; 102 left and 70 right appear in more sources",
                "note": "positive rotation about +Y turns the pack to the left",
            },
            "GunPitch": {
                "role": "pack elevation trunnions",
                "parent": "TurretYaw",
                "local_position": list(TRUNNION_LOCAL),
                "rest_world_position": [round(v, 4) for v in trunnion_world],
                "axis": "+X",
                "rotation_x_range_degrees": [0.0, ELEVATION_MAX_DEG],
                "note": "positive rotation about +X raises the pack front; 0 to 55 degrees is published",
                "min_elevation_sectors": [
                    {
                        "rotation_y_range_degrees": [-OVER_CAB_HALF_SECTOR_DEG, OVER_CAB_HALF_SECTOR_DEG],
                        "min_elevation_degrees": OVER_CAB_MIN_ELEVATION_DEG,
                        "reason": "firing sector over the cab: below this elevation the rockets would pass through the cab",
                        "source_wording": "the source gives a horizontal sector of up to 34 degrees over the cab; read as 34 degrees each side, which matches this geometry (zero elevation clears the cab from about 32 degrees off the axis)",
                    }
                ],
            },
            "Muzzle": {"parent": "GunPitch", "role": "generic effect anchor at the pack front center", "local_position": [0.0, round(PACK_BOTTOM_Y + (PACK_TOP_Y - PACK_BOTTOM_Y) / 2, 4), round(LAUNCH_Z, 4)]},
            "launch_points": {
                "parent": "GunPitch",
                "count": TUBE_ROWS * TUBE_COLUMNS,
                "bore_axis": "-Z of GunPitch",
                "naming": "Launch_NN, NN = row * 10 + column + 1; row 0 is the top row, column 0 is the left column",
                "points": launch_points,
                "firing_order": firing_order(),
                "firing_order_note": "authoring choice for effects, not historical data: rows top to bottom, outer tubes inward, alternating left and right",
            },
            "wheels": {"left_parent": "LeftTrack", "right_parent": "RightTrack", "axle_z": list(AXLE_Z), "motion_mesh_pattern": "*_Motion_*"},
        },
        "lods": lods,
        "track_gauge_meters": TRACK,
        "track_wrap_meters": wheel_wrap_period(WHEEL_R),
        "simplifications": [
            "Layout follows published dimensions; part shapes and small details are simplified for readability.",
            "Cab, hood, wings and launcher base follow the drawing and photos in simplified flat panels; no curved stampings.",
            "Tube spacing and pack width are measured from the drawing front view (about 1.58 m for 10 tubes), not from a published figure.",
            "No markings, number plates, crew, tarps or stowed rockets.",
            "LOD2 renders the tube pack as one block with dark tube mouth strips.",
            "Single tires on all axles; tread lugs on LOD0 only.",
        ],
        "unsupported_motions": [
            "Suspension travel and front wheel steering.",
            "Doors.",
            "Rear stabilizing jacks are modeled stowed and do not deploy. Sources disagree whether the BM-21 has rear jacks or only a spring lockout; the photos show vertical fittings at the rear corners.",
            "No recoil node: the client fallback recoil would shift the whole model, so firing effects should not call it for this launcher.",
            "No rocket geometry or reload animation.",
        ],
        "clearance": {
            "pack_reach_meters": round(pack_reach(), 4),
            "turntable_to_spare_wheel_meters": round(TURRET_POS[2] - (SPARE_CENTER[2] + TIRE_WIDTH / 2 + 0.07), 4),
            "rule": "In travel position the pack lies on the carriage, points forward and ends behind the spare wheel. Its front corners stay clear of the spare wheel and cab at every traverse angle, because the reach from the traverse axis is shorter than the distance to them.",
        },
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
