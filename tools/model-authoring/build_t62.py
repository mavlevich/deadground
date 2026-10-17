"""Generate the restrained-realism T-62 study, in meters, with three explicit LODs."""

import argparse
import json
import math
from pathlib import Path

from t62_geometry import GltfModel, vec3_cross, vec3_normalize, vec3_sub

OUTPUT_DIR = Path(__file__).resolve().parents[2] / "src/Game.Godot/Assets/Vehicles/T62"
HULL_LENGTH, HULL_WIDTH, GROUND_CLEARANCE = 6.630, 3.300, 0.430
TURRET_RING_Y, TURRET_RING_Z, TURRET_RING_DIAM = 1.480, -0.380, 2.245
TOTAL_LENGTH_GUN_FWD, TOTAL_HEIGHT_CUPOLA, TURRET_ROOF_Y = 9.335, 2.395, 2.248
GUN_TRUNNION_Y, GUN_TRUNNION_Z, MUZZLE_LOCAL_Z = 1.850, -1.430, -4.590
ROAD_WHEEL_Z_POSITIONS = [-2.15, -1.25, -0.35, 0.70, 1.75]
ROAD_WHEEL_Y, ROAD_WHEEL_RADIUS = 0.470, 0.405
FRONT_IDLER = (-2.77, 0.740, 0.320)
REAR_SPROCKET = (2.73, 0.770, 0.330)
FENDER_Y, HULL_HALF_WIDTH = 1.280, 1.140
SIDE_FUEL_CENTERS = (-1.86, 1.38, 2.43)
REAR_DRUM_Y, REAR_DRUM_Z = 1.460, 3.640


def loft(
    m,
    name,
    rings,
    sides,
    material,
    parent,
    axis="y",
    tint=1.0,
    cap_start=True,
    cap_end=True,
    position=(0, 0, 0),
    faceted=False,
    cast_shape=False,
    stamped_wheel=False,
    face_axis_sign=0,
):
    """Elliptical meridian profile with area-weighted normals and outward caps."""
    points, indices = [], []
    for ring_index, (axial, ra, rb, offset) in enumerate(rings):
        for i in range(sides):
            angle = i * math.tau / sides
            a, b = ra * math.cos(angle), rb * math.sin(angle) + offset
            coordinate = axial
            if cast_shape:
                # A flattened gun opening and broader front cheeks replace a perfect ellipse.
                # The low casting has broad, nearly upright cheeks and a cut-back gun opening.
                front = max(0.0, -math.sin(angle))
                a *= 1 + 0.075 * front * (1 - front)
                b = max(b, offset - rb * 0.88)
                # Keep the roof broad, rather than reducing every ring to a hemisphere.
                if axial > 0.50:
                    a *= 1.025
            if stamped_wheel:
                # Five pressed depressions are part of the dish, with no overlay discs.
                distance_squared = min(
                    (a - 0.235 * math.cos(k * math.tau / 5)) ** 2
                    + (b - 0.235 * math.sin(k * math.tau / 5)) ** 2
                    for k in range(5)
                )
                coordinate -= math.copysign(0.035, axial) * math.exp(
                    -distance_squared / 0.045**2
                )
            points.append(
                (a, coordinate, b)
                if axis == "y"
                else ((coordinate, a, b) if axis == "x" else (a, b, coordinate))
            )
    for ring in range(len(rings) - 1):
        for i in range(sides):
            j = (i + 1) % sides
            corners = [
                ring * sides + i,
                (ring + 1) * sides + i,
                (ring + 1) * sides + j,
                ring * sides + j,
            ]
            a, b, c = [points[v] for v in corners[:3]]
            normal = vec3_cross(vec3_sub(b, a), vec3_sub(c, a))
            center = tuple(sum(points[v][k] for v in corners) / 4 for k in range(3))
            offset = (rings[ring][3] + rings[ring + 1][3]) / 2
            radial = (
                (center[0], 0, center[2] - offset)
                if axis == "y"
                else (
                    (0, center[1], center[2] - offset)
                    if axis == "x"
                    else (center[0], center[1] - offset, 0)
                )
            )
            facing = (
                normal[0] * face_axis_sign
                if face_axis_sign
                else sum(normal[k] * radial[k] for k in range(3))
            )
            if facing < 0:
                corners.reverse()
            a, b, c, d = corners
            indices.extend([a, b, c, a, c, d])
    for ring, enabled, sign in [(0, cap_start, -1), (len(rings) - 1, cap_end, 1)]:
        if not enabled:
            continue
        sign *= 1 if rings[-1][0] >= rings[0][0] else -1
        axial, _, _, offset = rings[ring]
        center = len(points)
        points.append(
            (0, axial, offset)
            if axis == "y"
            else ((axial, 0, offset) if axis == "x" else (0, offset, axial))
        )
        expected = (
            (0, sign, 0)
            if axis == "y"
            else ((sign, 0, 0) if axis == "x" else (0, 0, sign))
        )
        for i in range(sides):
            a, b = ring * sides + i, ring * sides + (i + 1) % sides
            n = vec3_cross(
                vec3_sub(points[a], points[center]), vec3_sub(points[b], points[center])
            )
            indices.extend(
                [center, a, b]
                if sum(n[k] * expected[k] for k in range(3)) >= 0
                else [center, b, a]
            )
    if faceted:
        # Keep cast armor planes readable without adding a normal-map dependency.
        m.add_flat_mesh(
            name,
            points,
            [indices[i : i + 3] for i in range(0, len(indices), 3)],
            material,
            parent,
            position=position,
            tint=tint,
        )
        return
    accumulated = [[0.0, 0.0, 0.0] for _ in points]
    for offset in range(0, len(indices), 3):
        a, b, c = indices[offset : offset + 3]
        normal = vec3_cross(
            vec3_sub(points[b], points[a]), vec3_sub(points[c], points[a])
        )
        for vertex in (a, b, c):
            for k in range(3):
                accumulated[vertex][k] += normal[k]
    m.add_mesh_direct(
        name,
        points,
        [vec3_normalize(n) for n in accumulated],
        indices,
        material,
        parent,
        position,
        tint=tint,
    )


def hull_shell(
    m, name, stations, mat, parent, tint, position=(0, 0, 0), bevel_width=0.065
):
    """Closed armor shell with narrow edge bevels, instead of stacked boxes."""
    vertices, faces = [], []
    for z, lower_w, lower_y, upper_w, upper_y in stations:
        bevel = min(bevel_width, (upper_y - lower_y) * 0.30)
        vertices.extend(
            [
                (-lower_w, lower_y, z),
                (lower_w, lower_y, z),
                (upper_w, upper_y - bevel, z),
                (upper_w - bevel, upper_y, z),
                (-upper_w + bevel, upper_y, z),
                (-upper_w, upper_y - bevel, z),
            ]
        )
    faces.extend(
        [tuple(range(6)), tuple(range((len(stations) - 1) * 6, len(stations) * 6))]
    )
    for station in range(len(stations) - 1):
        for i in range(6):
            j = (i + 1) % 6
            faces.append(
                (
                    station * 6 + i,
                    station * 6 + j,
                    (station + 1) * 6 + j,
                    (station + 1) * 6 + i,
                )
            )
    m.add_flat_mesh(
        name,
        vertices,
        faces,
        mat,
        parent,
        position=position,
        auto_orient=True,
        tint=tint,
    )


def rod(m, name, start, end, radius, material, parent, sides=8):
    direction = vec3_normalize(vec3_sub(end, start))
    helper = (0, 1, 0) if abs(direction[1]) < 0.9 else (1, 0, 0)
    u = vec3_normalize(vec3_cross(direction, helper))
    v = vec3_cross(direction, u)
    points, normals, indices = [], [], []
    for center in (start, end):
        for i in range(sides):
            angle = i * math.tau / sides
            n = tuple(u[k] * math.cos(angle) + v[k] * math.sin(angle) for k in range(3))
            points.append(tuple(center[k] + n[k] * radius for k in range(3)))
            normals.append(n)
    for i in range(sides):
        j = (i + 1) % sides
        indices.extend([i, i + sides, j + sides, i, j + sides, j])
    m.add_mesh_direct(name, points, normals, indices, material, parent)


def wheel_tangent(first, second, upper=False):
    """Outward lower common tangent of two wheel circles in (z, y)."""
    z1, y1, r1 = first
    z2, y2, r2 = second
    dz, dy = z2 - z1, y2 - y1
    length = math.hypot(dz, dy)
    ux, uy = dz / length, dy / length
    cosine = (r1 - r2) / length
    sine = math.sqrt(1 - cosine * cosine)
    candidates = [
        (cosine * ux - sine * uy, cosine * uy + sine * ux),
        (cosine * ux + sine * uy, cosine * uy - sine * ux),
    ]
    return (max if upper else min)(candidates, key=lambda n: n[1])


def track_profile(lod):
    # The top run rests on the road wheels; no invented return rollers.
    first = (ROAD_WHEEL_Z_POSITIONS[0], ROAD_WHEEL_Y, ROAD_WHEEL_RADIUS)
    last = (ROAD_WHEEL_Z_POSITIONS[-1], ROAD_WHEEL_Y, ROAD_WHEEL_RADIUS)
    front_normal = wheel_tangent(FRONT_IDLER, first)
    rear_normal = wheel_tangent(REAR_SPROCKET, last)
    front_angle = math.atan2(front_normal[1], front_normal[0])
    rear_angle = math.atan2(rear_normal[1], rear_normal[0])
    steps = (10, 7, 6)[lod]
    front_upper = wheel_tangent(FRONT_IDLER, first, upper=True)
    rear_upper = wheel_tangent(REAR_SPROCKET, last, upper=True)
    front_top_angle = math.atan2(front_upper[1], front_upper[0])
    rear_top_angle = math.atan2(rear_upper[1], rear_upper[0])
    inner = [
        (
            FRONT_IDLER[0] + FRONT_IDLER[2] * front_upper[0],
            FRONT_IDLER[1] + FRONT_IDLER[2] * front_upper[1],
        )
    ]
    for i, z in enumerate(ROAD_WHEEL_Z_POSITIONS):
        normal = front_upper if i == 0 else rear_upper if i == 4 else (0, 1)
        inner.append(
            (
                z + ROAD_WHEEL_RADIUS * normal[0],
                ROAD_WHEEL_Y + ROAD_WHEEL_RADIUS * normal[1],
            )
        )
        if i < 4 and lod == 0:
            inner.append(
                (
                    (z + ROAD_WHEEL_Z_POSITIONS[i + 1]) / 2,
                    ROAD_WHEEL_Y + ROAD_WHEEL_RADIUS - 0.025,
                )
            )

    def arc(circle, start, end, count):
        z, y, radius = circle
        return [
            (
                z + radius * math.cos(start + (end - start) * i / count),
                y + radius * math.sin(start + (end - start) * i / count),
            )
            for i in range(1, count + 1)
        ]

    inner.append(
        (
            REAR_SPROCKET[0] + REAR_SPROCKET[2] * rear_upper[0],
            REAR_SPROCKET[1] + REAR_SPROCKET[2] * rear_upper[1],
        )
    )
    inner += arc(REAR_SPROCKET, rear_top_angle, rear_angle, steps)
    inner.append(
        (last[0] + last[2] * rear_normal[0], last[1] + last[2] * rear_normal[1])
    )
    inner += arc(last, rear_angle, -math.pi / 2, 3)
    inner.append((first[0], first[1] - first[2]))
    inner += arc(first, -math.pi / 2, front_angle, 3)
    inner.append(
        (
            FRONT_IDLER[0] + FRONT_IDLER[2] * front_normal[0],
            FRONT_IDLER[1] + FRONT_IDLER[2] * front_normal[1],
        )
    )
    inner += arc(FRONT_IDLER, front_angle, front_top_angle - math.tau, steps)[:-1]
    outer = []
    for i, (z, y) in enumerate(inner):
        before, after = inner[(i - 1) % len(inner)], inner[(i + 1) % len(inner)]
        dz, dy = after[0] - before[0], after[1] - before[1]
        length = math.hypot(dz, dy)
        outer.append((z - dy / length * 0.065, y + dz / length * 0.065))
    return inner, outer


def sprocket(m, sign, parent, mat, lod):
    z, y, radius = REAR_SPROCKET
    # Separate toothed drive wheel, not a small disc inside an uncapped cylinder.
    teeth = 13
    profile = []
    tooth_samples = (
        ((0.0, radius - 0.028), (0.26, radius), (0.62, radius), (0.88, radius - 0.028))
        if lod == 0
        else ((0.0, radius - 0.025), (0.5, radius))
    )
    if lod == 2:
        profile = [
            (radius * math.cos(i * math.tau / 12), radius * math.sin(i * math.tau / 12))
            for i in range(12)
        ]
    else:
        for tooth in range(teeth):
            for fraction, r in tooth_samples:
                angle = (tooth + fraction) * math.tau / teeth
                profile.append((r * math.cos(angle), r * math.sin(angle)))
    vertices = []
    for x in (sign * 1.14, sign * 1.60):
        vertices.extend((x, y + dy, z + dz) for dz, dy in profile)
    count = len(profile)
    faces = [tuple(range(count)), tuple(range(count, 2 * count))]
    faces += [
        (i, (i + 1) % count, (i + 1) % count + count, i + count) for i in range(count)
    ]
    m.add_flat_mesh(
        "DriveSprocket",
        vertices,
        faces,
        mat["track"],
        parent,
        auto_orient=True,
        tint=0.90,
    )
    loft(
        m,
        "SprocketDish",
        [
            (sign * 1.603, 0.255, 0.255, 0),
            (sign * 1.622, 0.215, 0.215, 0),
            (sign * 1.613, 0.09, 0.09, 0),
        ],
        (16, 12, 8)[lod],
        mat["paint"],
        parent,
        axis="x",
        cap_start=False,
        cap_end=True,
        position=(0, y, z),
        tint=0.82,
        face_axis_sign=sign,
    )


def wave_deflector(m, mat, hull):
    # Thin upright plates welded to the sloped glacis, not a triangular armor wedge.
    base = [(-0.78, 1.245, -2.39), (0, 1.13, -2.64), (0.78, 1.245, -2.39)]
    for i in range(2):
        a, b = base[i], base[i + 1]
        vertices = [
            (x, y + height, z + depth)
            for depth in (-0.007, 0.007)
            for height in (0, 0.080)
            for x, y, z in (a, b)
        ]
        m.add_flat_mesh(
            "WaveDeflector",
            vertices,
            [
                (0, 1, 3, 2),
                (4, 6, 7, 5),
                (0, 4, 5, 1),
                (2, 3, 7, 6),
                (0, 2, 6, 4),
                (1, 5, 7, 3),
            ],
            mat,
            hull,
            auto_orient=True,
            tint=0.88,
        )


def materials(m):
    # Hex values are sRGB authoring colors, converted to linear glTF factors.
    return {
        "paint": m.material("T62_Paint", "77745e", metal=0.08, rough=0.84),
        "rubber": m.material("T62_Rubber", "4a493f", rough=0.97),
        "track": m.material("T62_TrackSteel", "6a685e", metal=0.52, rough=0.76),
        "steel": m.material("T62_DarkSteel", "30372f", metal=0.30, rough=0.70),
        "glass": m.material("T62_Optics", "243a37", metal=0.10, rough=0.24),
        "wood": m.material("T62_Timber", "746447", rough=0.94),
    }


def build_t62_lod(lod_level=0, output_dir=None):
    m = GltfModel(f"T62_LOD{lod_level}", surface_weathering=True)
    mat = materials(m)
    mat["steel"] = mat["track"]
    wheel_sides, dome_sides, barrel_sides = ((20, 32, 12), (12, 16, 8), (8, 12, 6))[
        lod_level
    ]
    hull = m.node("Hull")
    left = m.node("LeftTrack")
    right = m.node("RightTrack")
    turret = m.node("TurretYaw", (0, TURRET_RING_Y, TURRET_RING_Z))
    pitch = m.node(
        "GunPitch",
        (0, GUN_TRUNNION_Y - TURRET_RING_Y, GUN_TRUNNION_Z - TURRET_RING_Z),
        turret,
    )
    recoil = m.node("BarrelRecoil", parent=pitch)
    m.node("Muzzle", (0, 0, MUZZLE_LOCAL_Z), recoil)
    hull_shell(
        m,
        "LowerTub",
        [
            (-3.10, 0.98, 0.70, 1.06, 0.76),
            (-2.80, 0.98, 0.43, 1.10, 1.06),
            (-1.90, 0.98, 0.43, 1.12, 1.26),
            (0.80, 0.98, 0.43, 1.12, 1.26),
            (3.315, 0.98, 0.63, 1.12, 1.26),
        ],
        mat["paint"],
        hull,
        0.72,
    )
    hull_shell(
        m,
        "UpperArmor",
        [
            (-3.315, 1.04, 0.76, 1.04, 0.79),
            (-1.90, 1.12, 1.06, HULL_HALF_WIDTH, 1.48),
            (0.80, 1.12, 1.06, HULL_HALF_WIDTH, 1.48),
            (3.315, 1.12, 1.06, HULL_HALF_WIDTH, 1.43),
        ],
        mat["paint"],
        hull,
        0.92,
    )
    for sign in (-1, 1):
        m.add_box(
            "Fender",
            (0.58, 0.035, 5.91),
            (sign * 1.36, FENDER_Y, 0),
            mat["paint"],
            hull,
            tint=0.83,
        )
        # Thin rolled sheet wraps over the idler, with an exposed outer lip.
        # Local stations are an artistic fit to photographs, not factory sections.
        front_guard = [
            (-2.955, FENDER_Y),
            (-3.08, 1.265),
            (-3.22, 1.205),
            (-3.32, 1.105),
            (-3.37, 0.985),
        ]
        if lod_level == 2:
            front_guard = [front_guard[i] for i in (0, 2, 4)]
        for (z0, y0), (z1, y1) in zip(front_guard, front_guard[1:]):
            vertices = [
                (sign * x, y, z)
                for x in ((1.07, 1.65) if sign > 0 else (1.65, 1.07))
                for z, y in ((z0, y0), (z1, y1))
            ]
            m.add_flat_mesh(
                "RolledFrontMudguard",
                vertices,
                [(0, 2, 3, 1)],
                mat["paint"],
                hull,
                auto_orient=False,
                tint=0.94,
            )
            # Back face and folded lip make the sheet visible from either side.
            m.add_flat_mesh(
                "MudguardUnderside",
                vertices,
                [(1, 3, 2, 0)],
                mat["paint"],
                hull,
                tint=0.62,
            )
            lip = [
                (sign * 1.65, y0, z0),
                (sign * 1.65, y1, z1),
                (sign * 1.65, y1 - 0.035, z1),
                (sign * 1.65, y0 - 0.035, z0),
            ]
            m.add_flat_mesh(
                "MudguardLip",
                lip,
                [(3, 2, 1, 0) if sign > 0 else (0, 1, 2, 3)],
                mat["paint"],
                hull,
                tint=0.80,
            )
        rear = [
            (sign * x, y, z)
            for x in ((1.07, 1.65) if sign > 0 else (1.65, 1.07))
            for z, y in ((2.955, FENDER_Y), (3.315, 1.16))
        ]
        m.add_flat_mesh(
            "RearMudguard",
            rear,
            [(0, 1, 3, 2), (2, 3, 1, 0)],
            mat["paint"],
            hull,
            tint=0.86,
        )
        if lod_level < 2:
            for z in (-2.1, -0.7, 0.7, 2.1):
                m.add_box(
                    "FenderJoin",
                    (0.58, 0.018, 0.025),
                    (sign * 1.36, FENDER_Y + 0.026, z),
                    mat["steel"],
                    hull,
                )
    m.add_box(
        "EnginePanel",
        (2.32, 0.025, 1.67),
        (0, 1.458, 2.13),
        mat["paint"],
        hull,
        tint=0.80,
    )
    for z in SIDE_FUEL_CENTERS:
        hull_shell(
            m,
            "FuelPannier",
            [(-0.49, 0.23, 1.335, 0.23, 1.545), (0.49, 0.23, 1.335, 0.23, 1.545)],
            mat["paint"],
            hull,
            0.88,
            position=(1.40, 0, z),
            bevel_width=0.020,
        )
        if lod_level == 0:
            m.add_box(
                "PannierLid",
                (0.47, 0.018, 0.99),
                (1.40, 1.554, z),
                mat["paint"],
                hull,
                tint=0.95,
            )
            for dz in (-0.32, 0.32):
                m.add_box(
                    "FuelCellFoot",
                    (0.36, 0.035, 0.055),
                    (1.40, 1.314, z + dz),
                    mat["steel"],
                    hull,
                )
            m.add_cylinder(
                "FuelCellCap",
                0.034,
                0.012,
                (1.49, 1.570, z + 0.27),
                mat["steel"],
                sides=6,
                parent=hull,
            )
    if lod_level < 2:
        m.add_box(
            "FrontToolLocker",
            (0.40, 0.14, 1.02),
            (-1.43, 1.375, -1.91),
            mat["paint"],
            hull,
            tint=0.87,
        )
    if lod_level < 2:
        for z in (1.46, 1.62, 1.78, 1.94, 2.10, 2.26, 2.42, 2.58, 2.74):
            m.add_box(
                "EngineLouvre",
                (1.14, 0.012, 0.065),
                (0.32, 1.478, z),
                mat["steel"],
                hull,
            )
        m.add_cylinder(
            "FanGuard",
            0.39,
            0.035,
            (-0.72, 1.47, 1.64),
            mat["steel"],
            sides=12,
            parent=hull,
        )
        m.add_cylinder(
            "FanCenter",
            0.29,
            0.038,
            (-0.72, 1.474, 1.64),
            mat["paint"],
            sides=12,
            parent=hull,
        )
        m.add_box(
            "ToolLocker",
            (0.40, 0.14, 1.40),
            (-1.43, 1.375, 0.50),
            mat["paint"],
            hull,
            tint=0.78,
        )
        m.add_box(
            "ToolLockerLid",
            (0.42, 0.02, 1.42),
            (-1.43, 1.455, 0.50),
            mat["paint"],
            hull,
            tint=0.90,
        )
        m.add_box(
            "Exhaust", (0.10, 0.15, 0.44), (-1.21, 1.40, 2.25), mat["steel"], hull
        )
        m.add_cylinder(
            "UnditchingLog",
            0.095,
            2.62,
            (0, 1.00, 3.44),
            mat["wood"],
            axis="x",
            sides=8,
            parent=hull,
        )
        m.add_cylinder(
            "DriverHatch",
            0.26,
            0.045,
            (-0.55, 1.495, -1.62),
            mat["paint"],
            sides=12,
            parent=hull,
        )
        for x in (-0.67, -0.44):
            m.add_box(
                "DriverPeriscope",
                (0.14, 0.055, 0.085),
                (x, 1.49, -1.85),
                mat["glass"],
                hull,
            )
    if lod_level == 0:
        for x in (0.73, 0.95):
            m.add_cylinder(
                "Headlamp",
                0.085,
                0.10,
                (x, 1.20, -2.53),
                mat["steel"],
                axis="z",
                sides=10,
                parent=hull,
            )
            m.add_cylinder(
                "HeadlampLens",
                0.065,
                0.012,
                (x, 1.20, -2.586),
                mat["glass"],
                axis="z",
                sides=10,
                parent=hull,
            )
        rod(
            m,
            "LightGuard",
            (0.60, 1.31, -2.57),
            (1.08, 1.31, -2.57),
            0.014,
            mat["steel"],
            hull,
        )
        for x in (0.60, 1.08):
            rod(
                m,
                "GuardLeg",
                (x, 1.10, -2.55),
                (x, 1.31, -2.57),
                0.014,
                mat["steel"],
                hull,
            )
        wave_deflector(m, mat["paint"], hull)
    if lod_level == 0:
        for sign in (-1, 1):
            # Low grab rails follow the side of the casting.
            rail = [
                (sign * 1.18, 0.42, -0.52),
                (sign * 1.29, 0.38, 0.02),
                (sign * 1.17, 0.40, 0.70),
            ]
            for a, b in zip(rail, rail[1:]):
                rod(m, "TurretSideRail", a, b, 0.018, mat["paint"], turret, sides=6)
            # Front towing loops are bent steel, not solid square bumps.
            path = [
                (sign * 0.83, 0.93, -3.15),
                (sign * 0.83, 1.02, -3.24),
                (sign * 0.83, 1.10, -3.15),
                (sign * 0.83, 1.06, -3.04),
            ]
            for a, b in zip(path, path[1:]):
                rod(m, "TowEye", a, b, 0.026, mat["steel"], hull, sides=6)
        # Hatch rim and hinge break the uninterrupted roof silhouette.
        for x in (-0.73, -0.38):
            m.add_box(
                "CupolaHinge",
                (0.065, 0.05, 0.13),
                (x, 0.88, 0.36),
                mat["paint"],
                turret,
                tint=0.83,
            )
    # Both drums sit behind the rear armor, with transverse axes and separate brackets.
    for x in (-0.53, 0.53):
        m.add_cylinder(
            "RearFuelDrum",
            0.285,
            1.00,
            (x, REAR_DRUM_Y, REAR_DRUM_Z),
            mat["paint"],
            axis="x",
            sides=(16, 12, 8)[lod_level],
            parent=hull,
        )
        if lod_level < 2:
            for band_x in (x - 0.32, x + 0.32):
                loft(
                    m,
                    "DrumRetainingBand",
                    [
                        (band_x - 0.018, 0.297, 0.297, 0),
                        (band_x + 0.018, 0.297, 0.297, 0),
                    ],
                    (16, 12)[lod_level],
                    mat["paint"],
                    hull,
                    axis="x",
                    cap_start=False,
                    cap_end=False,
                    position=(0, REAR_DRUM_Y, REAR_DRUM_Z),
                    tint=0.65,
                )
            for bracket_x in (x - 0.32, x + 0.32):
                m.add_box(
                    "DrumBracket",
                    (0.055, 0.075, 0.64),
                    (bracket_x, 1.14, 3.48),
                    mat["steel"],
                    hull,
                )
    if lod_level < 2:
        for x in (-0.83, 0.83):
            m.add_box(
                "FrontTowMount",
                (0.13, 0.12, 0.13),
                (x, 0.89, -3.13),
                mat["paint"],
                hull,
                tint=0.65,
            )
        # Raised hatch hinges and latches remain legible at a medium camera distance.
        for x in (-0.74, -0.35):
            m.add_box(
                "DriverHinge",
                (0.08, 0.045, 0.11),
                (x, 1.524, -1.51),
                mat["steel"],
                hull,
            )
        for z in (-0.12, 0.65, 1.15):
            m.add_box(
                "LockerLatch",
                (0.035, 0.065, 0.055),
                (-1.638, 1.375, z),
                mat["steel"],
                hull,
            )
        for x in (-0.56, 0.56):
            rod(
                m,
                "HatchHandle",
                (x - 0.08, 0.815, 0.10),
                (x + 0.08, 0.815, 0.10),
                0.014,
                mat["steel"],
                turret,
                sides=6,
            )
    inner, outer = track_profile(lod_level)
    for sign, parent in [(-1, left), (1, right)]:
        m.add_extruded_track(
            "TrackBelt", outer, inner, sign * 1.06, sign * 1.64, mat["track"], parent
        )
        for z in ROAD_WHEEL_Z_POSITIONS:
            m.add_cylinder(
                "RubberTire",
                ROAD_WHEEL_RADIUS,
                0.47,
                (sign * 1.355, 0.470, z),
                mat["rubber"],
                axis="x",
                sides=wheel_sides,
                parent=parent,
                caps="pos" if sign > 0 else "neg",
            )
            if lod_level < 2:
                profile = [
                    (1.592, 0.362),
                    (1.613, 0.350),
                    (1.600, 0.255),
                    (1.642, 0.118),
                ]
                if lod_level == 0:
                    profile = [
                        (1.592, 0.362),
                        (1.613, 0.350),
                        (1.605, 0.290),
                        (1.604, 0.235),
                        (1.622, 0.180),
                        (1.642, 0.118),
                    ]
                rings = [(sign * axial, radius, radius, 0) for axial, radius in profile]
                loft(
                    m,
                    "DishedWheel",
                    rings,
                    wheel_sides,
                    mat["paint"],
                    parent,
                    axis="x",
                    tint=1.0,
                    cap_start=False,
                    cap_end=False,
                    position=(0, 0.470, z),
                    stamped_wheel=lod_level == 0,
                    face_axis_sign=sign,
                )
            else:
                m.add_disc(
                    "WheelDisc",
                    0.36,
                    (sign * 1.615, 0.470, z),
                    mat["paint"],
                    axis="x",
                    sides=wheel_sides,
                    parent=parent,
                    normal_dir=sign,
                )
            if lod_level < 2:
                m.add_cylinder(
                    "WheelHub",
                    0.105,
                    0.035,
                    (sign * 1.6325, 0.470, z),
                    mat["paint"],
                    axis="x",
                    sides=8,
                    parent=parent,
                    caps=("pos" if sign > 0 else "neg"),
                )
        z, y, radius = FRONT_IDLER
        m.add_cylinder(
            "IdlerRim",
            radius,
            0.45,
            (sign * 1.35, y, z),
            mat["track"],
            axis="x",
            sides=wheel_sides,
            parent=parent,
            caps="pos" if sign > 0 else "neg",
        )
        loft(
            m,
            "IdlerDish",
            [
                (sign * 1.578, radius * 0.94, radius * 0.94, 0),
                (sign * 1.601, radius * 0.78, radius * 0.78, 0),
                (sign * 1.586, radius * 0.35, radius * 0.35, 0),
            ],
            wheel_sides,
            mat["paint"],
            parent,
            axis="x",
            cap_start=False,
            cap_end=True,
            position=(0, y, z),
            tint=0.88,
            face_axis_sign=sign,
        )
        sprocket(m, sign, parent, mat, lod_level)
        if lod_level == 0:
            perimeter = sum(
                math.dist(a, outer[(i + 1) % len(outer)]) for i, a in enumerate(outer)
            )
            spacing = perimeter / max(1, round(perimeter / 0.17))
            walked, next_shoe = 0.0, spacing / 2
            for i, a in enumerate(outer):
                b = outer[(i + 1) % len(outer)]
                length = math.dist(a, b)
                dz, dy = (b[0] - a[0]) / length, (b[1] - a[1]) / length
                while next_shoe < walked + length:
                    t = (next_shoe - walked) / length
                    z = a[0] + (b[0] - a[0]) * t
                    y = a[1] + (b[1] - a[1]) * t
                    next_shoe += spacing
                    if y < 0.12:
                        continue
                    # Three exposed faces per shoe, no invisible underside boxes.
                    vertices = []
                    for x in (sign * 1.075, sign * 1.635):
                        for along, up in (
                            (-0.065, 0.0),
                            (0.065, 0.0),
                            (0.065, 0.025),
                            (-0.065, 0.025),
                        ):
                            vertices.append(
                                (x, y + dy * along + dz * up, z + dz * along - dy * up)
                            )
                    m.add_flat_mesh(
                        "TrackShoe",
                        vertices,
                        [(0, 3, 7, 4), (3, 2, 6, 7), (4, 7, 6, 5)],
                        mat["track"],
                        parent,
                        auto_orient=True,
                        tint=0.82,
                    )
                walked += length
    all_rings = [
        (0.00, 1.122, 1.27, 0.02),
        (0.08, 1.25, 1.38, 0.025),
        (0.23, 1.30, 1.42, 0.035),
        (0.39, 1.28, 1.38, 0.015),
        (0.54, 1.20, 1.29, -0.005),
        (0.66, 1.06, 1.14, -0.015),
        (0.735, 0.87, 0.98, -0.025),
        (0.768, 0.77, 0.88, -0.03),
    ]
    rings = (
        all_rings
        if lod_level == 0
        else (
            [all_rings[i] for i in (0, 1, 3, 5, 7)]
            if lod_level == 1
            else [all_rings[i] for i in (0, 2, 4, 7)]
        )
    )
    loft(
        m,
        "CastTurret",
        rings,
        dome_sides,
        mat["paint"],
        turret,
        tint=0.92,
        cast_shape=True,
    )
    # A recessed bearing under the casting gives the turret a mechanical base.
    m.add_cylinder(
        "TurretBearing",
        1.11,
        0.045,
        (0, 0.012, 0),
        mat["steel"],
        sides=dome_sides,
        parent=turret,
    )
    m.add_cylinder(
        "CommanderCupola",
        0.365,
        0.13,
        (-0.55, 0.82, 0.10),
        mat["paint"],
        sides=wheel_sides,
        parent=turret,
    )
    m.add_cylinder(
        "CommanderHatchSeal",
        0.345,
        0.009,
        (-0.55, 0.888, 0.10),
        mat["steel"],
        sides=wheel_sides,
        parent=turret,
    )
    loft(
        m,
        "CommanderHatch",
        [(0.894, 0.335, 0.335, 0), (0.915, 0.325, 0.325, 0)],
        wheel_sides,
        mat["paint"],
        turret,
        position=(-0.55, 0, 0.10),
        tint=0.83,
    )
    m.add_cylinder(
        "LoaderHatchSeal",
        0.313,
        0.009,
        (0.56, 0.772, 0.15),
        mat["steel"],
        sides=wheel_sides,
        parent=turret,
    )
    loft(
        m,
        "LoaderHatch",
        [(0.777, 0.305, 0.305, 0), (0.803, 0.290, 0.290, 0)],
        wheel_sides,
        mat["paint"],
        turret,
        position=(0.56, 0, 0.15),
        tint=0.85,
    )
    if lod_level < 2:
        # Sloped rear roof hatch for spent-case ejection, a T-62 recognition feature.
        hull_shell(
            m,
            "CaseEjectionFrame",
            [(0.80, 0.22, 0.763, 0.22, 0.783), (1.16, 0.22, 0.618, 0.22, 0.638)],
            mat["steel"],
            turret,
            1.0,
            bevel_width=0.005,
        )
        hull_shell(
            m,
            "CaseEjectionHatch",
            [(0.825, 0.19, 0.784, 0.19, 0.800), (1.135, 0.19, 0.659, 0.19, 0.675)],
            mat["paint"],
            turret,
            0.80,
            bevel_width=0.005,
        )
        for x in (-0.72, -0.40):
            m.add_box(
                "CupolaVision",
                (0.10, 0.065, 0.075),
                (x, 0.865, -0.15),
                mat["glass"],
                turret,
            )
        m.add_box(
            "GunnerSight",
            (0.18, 0.13, 0.19),
            (-0.44, 0.56, -0.83),
            mat["paint"],
            turret,
            tint=0.82,
        )
        m.add_box(
            "GunnerLens",
            (0.105, 0.065, 0.02),
            (-0.44, 0.57, -0.933),
            mat["glass"],
            turret,
        )
        m.add_cylinder(
            "AntennaBase",
            0.045,
            0.10,
            (-0.84, 0.70, 0.44),
            mat["steel"],
            sides=8,
            parent=turret,
        )
        rod(
            m,
            "Antenna",
            (-0.84, 0.75, 0.44),
            (-0.84, 2.05, 0.44),
            0.009,
            mat["steel"],
            turret,
            sides=6,
        )
    if lod_level == 0:
        for sign in (-1, 1):
            rod(
                m,
                "GrabHandle",
                (sign * 1.15, 0.43, -0.03),
                (sign * 1.15, 0.43, 0.62),
                0.014,
                mat["steel"],
                turret,
            )
            for z in (-0.03, 0.62):
                rod(
                    m,
                    "HandleFoot",
                    (sign * 1.10, 0.39, z),
                    (sign * 1.15, 0.43, z),
                    0.014,
                    mat["steel"],
                    turret,
                )
    # Cover and optical housing pitch with the mount; only the barrel tube recoils.
    loft(
        m,
        "MantletCover",
        [
            (0.12, 0.38, 0.25, 0),
            (-0.08, 0.39, 0.24, 0),
            (-0.25, 0.31, 0.205, -0.01),
            (-0.40, 0.26, 0.18, -0.02),
            (-0.49, 0.18, 0.155, -0.02),
        ],
        12 if lod_level == 0 else 8,
        mat["paint"],
        pitch,
        axis="z",
        tint=0.58,
        faceted=False,
    )
    if lod_level < 2:
        m.add_cylinder(
            "IRHousing",
            0.205,
            0.16,
            (0.49, 0.33, -0.08),
            mat["paint"],
            axis="z",
            sides=16,
            parent=pitch,
        )
        m.add_cylinder(
            "IRLens",
            0.169,
            0.012,
            (0.49, 0.33, -0.166),
            mat["glass"],
            axis="z",
            sides=16,
            parent=pitch,
        )
        for x in (0.37, 0.60):
            rod(
                m,
                "IRSupport",
                (x, 0.08, 0.0),
                (x, 0.20, -0.04),
                0.017,
                mat["steel"],
                pitch,
                sides=6,
            )
    loft(
        m,
        "PaintedBarrel",
        [(-0.30, 0.115, 0.115, 0), (-2.28, 0.096, 0.096, 0), (-4.59, 0.085, 0.085, 0)],
        barrel_sides,
        mat["paint"],
        recoil,
        axis="z",
        tint=0.82,
        cap_start=False,
        cap_end=False,
    )
    loft(
        m,
        "BoreEvacuator",
        [
            (-2.28, 0.105, 0.105, 0),
            (-2.34, 0.157, 0.157, 0),
            (-2.83, 0.157, 0.157, 0),
            (-2.90, 0.095, 0.095, 0),
        ],
        barrel_sides,
        mat["paint"],
        recoil,
        axis="z",
        tint=0.90,
    )
    points, normals, indices = [], [], []
    for radius, z in [(0.085, -4.59), (0.0575, -4.59), (0.0575, -4.42)]:
        for i in range(barrel_sides):
            a = i * math.tau / barrel_sides
            points.append((radius * math.cos(a), radius * math.sin(a), z))
            normals.append(
                (0, 0, -1) if z == -4.59 else (-math.cos(a), -math.sin(a), 0)
            )
    for ring in range(2):
        for i in range(barrel_sides):
            j = (i + 1) % barrel_sides
            a = ring * barrel_sides + i
            b = ring * barrel_sides + j
            c = (ring + 1) * barrel_sides + j
            d = (ring + 1) * barrel_sides + i
            indices.extend([a, b, c, a, c, d])
    m.add_mesh_direct(
        "MuzzleBore", points, normals, indices, mat["paint"], recoil, tint=0.12
    )
    filename = "t62.glb" if lod_level == 0 else f"t62_lod{lod_level}.glb"
    stats = m.save((output_dir or OUTPUT_DIR) / filename)
    stats["lod"] = lod_level
    stats["role"] = ("close", "rts", "distant")[lod_level]
    return stats


def generate_all(output_dir=None):
    destination = output_dir or OUTPUT_DIR
    lods = [build_t62_lod(lod, destination) for lod in range(3)]
    report = {
        "asset": "T-62 RTS visual study",
        "configuration": "Basic T-62 silhouette, without a roof AA machine gun or applique armor",
        "units": "meters",
        "up": "+Y",
        "forward": "-Z",
        "color_authoring": "sRGB hex converted to linear glTF factors",
        "reference_anchors": {
            "hull_length_meters": HULL_LENGTH,
            "overall_length_gun_fwd_meters": TOTAL_LENGTH_GUN_FWD,
            "width_over_fenders_meters": HULL_WIDTH,
            "height_to_cupola_meters": TOTAL_HEIGHT_CUPOLA,
            "height_to_turret_roof_meters": TURRET_ROOF_Y,
            "ground_clearance_meters": GROUND_CLEARANCE,
            "turret_ring_diameter_meters": TURRET_RING_DIAM,
            "road_wheels_per_side": 5,
        },
        "lods": lods,
    }
    (destination / "manifest.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=OUTPUT_DIR)
    generate_all(parser.parse_args().output_dir)
