"""Shared helpers for procedural vehicle assets: geometry, running gear, baked
vertex ambient occlusion with a dirt mask, and textured marking decals.

Vertex color convention written by `bake_vertex_shading`:
    rgb = part tint multiplied by baked ambient occlusion (linear multiplier)
    a   = dirt mask in [0, 1] for the weathering shader
"""

import io
import math
import struct
from pathlib import Path

import numpy as np

from t62_geometry import GltfModel, vec3_cross, vec3_normalize, vec3_sub

__all__ = [
    "GltfModel",
    "vec3_cross",
    "vec3_normalize",
    "vec3_sub",
    "hex_section",
    "cast_section",
    "dome_section",
    "loft_sections",
    "tube",
    "obox",
    "rod",
    "revolve",
    "track_path",
    "track_belt",
    "track_shoes",
    "track_links",
    "tag_wheel",
    "track_wrap_period",
    "emit_motion_parts",
    "bake_vertex_shading",
    "MarkingAtlas",
    "add_decal",
    "add_conformal_decal",
    "surface_heights",
    "surface_hit",
    "domed_hatch",
    "save_model",
]


# ----------------------------------------------------------------------------
# Sections and lofts
# ----------------------------------------------------------------------------


def hex_section(wb, yb, wt, yt, chamfer):
    """Convex armor section, counterclockwise in (x, y)."""
    c = min(chamfer, (yt - yb) * 0.3, wt * 0.3)
    return [(wb, yb), (wt, yt - c), (wt - c, yt), (-wt + c, yt), (-wt, yt - c), (-wb, yb)]


def cast_section(wb, wt, y0, y1, steps, roof_round=0.12, power=1.25):
    """Rounded cast section: sides curving into a rounded roof edge."""
    right = []
    for i in range(steps + 1):
        u = i / steps
        right.append((wb + (wt - wb) * u**power, y0 + (y1 - y0) * u))
    xt, yt = right[-1]
    right[-1] = (xt, yt - (y1 - y0) * 0.07)
    right.append((xt * (1 - roof_round), yt))
    left = [(-x, y) for x, y in reversed(right)]
    return right + left


def dome_section(half_width, height, steps, base=0.0, flat_top=0.0):
    """Upper half ellipse with a flat base, counterclockwise in (x, y)."""
    points = []
    for i in range(steps + 1):
        t = math.pi * i / steps
        x = half_width * math.cos(t)
        y = base + height * math.sin(t)
        if flat_top > 0:
            y = min(y, base + height * (1 - flat_top) + height * flat_top * math.sin(t) ** 4)
        points.append((x, y))
    return points


def loft_sections(m, name, stations, mat, parent, tint=1.0, smooth=False, pos=(0, 0, 0)):
    """Loft convex counterclockwise sections along increasing z, with end caps."""
    count = len(stations[0][1])
    rings = [[(x, y, z) for x, y in pts] for z, pts in stations]
    caps = [list(reversed(rings[0])), rings[-1]]
    if not smooth:
        vertices = [p for ring in rings for p in ring]
        faces = []
        for k in range(len(rings) - 1):
            for i in range(count):
                j = (i + 1) % count
                faces.append((k * count + i, k * count + j, (k + 1) * count + j, (k + 1) * count + i))
        start = len(vertices)
        vertices += caps[0] + caps[1]
        faces.append(tuple(range(start, start + count)))
        faces.append(tuple(range(start + count, start + 2 * count)))
        m.add_flat_mesh(name, vertices, faces, mat, parent, pos, tint=tint)
        return
    points = [p for ring in rings for p in ring]
    accumulated = [[0.0, 0.0, 0.0] for _ in points]
    indices = []
    for k in range(len(rings) - 1):
        for i in range(count):
            j = (i + 1) % count
            a, b, c, d = k * count + i, k * count + j, (k + 1) * count + j, (k + 1) * count + i
            n = vec3_cross(vec3_sub(points[b], points[a]), vec3_sub(points[c], points[a]))
            if sum(v * v for v in n) < 1e-14:
                n = vec3_cross(vec3_sub(points[c], points[a]), vec3_sub(points[d], points[a]))
            for v in (a, b, c, d):
                for axis in range(3):
                    accumulated[v][axis] += n[axis]
            indices.extend([a, b, c, a, c, d])
    m.add_mesh_direct(name, points, [vec3_normalize(n) for n in accumulated], indices, mat, parent, pos, tint=tint)
    m.add_flat_mesh(name + "Caps", caps[0] + caps[1], [tuple(range(count)), tuple(range(count, 2 * count))], mat, parent, pos, tint=tint)


def tube(m, name, rings, sides, mat, parent, pos=(0, 0, 0), cap_front=True, cap_back=True, tint=1.0):
    """Smooth tube along z from (z, radius) rings."""
    points, normals, indices = [], [], []
    for z, r in rings:
        for i in range(sides):
            a = i * math.tau / sides
            points.append((r * math.cos(a), r * math.sin(a), z))
            normals.append((math.cos(a), math.sin(a), 0.0))
    for k in range(len(rings) - 1):
        for i in range(sides):
            j = (i + 1) % sides
            a, b, c, d = k * sides + i, k * sides + j, (k + 1) * sides + j, (k + 1) * sides + i
            indices.extend([a, b, c, a, c, d])
    m.add_mesh_direct(name, points, normals, indices, mat, parent, pos, tint=tint)
    front, back = rings[0], rings[-1]
    if cap_front:
        m.add_disc(name + "Cap", front[1], (pos[0], pos[1], pos[2] + front[0]), mat, "z", sides, parent, -1.0)
    if cap_back:
        m.add_disc(name + "Cap", back[1], (pos[0], pos[1], pos[2] + back[0]), mat, "z", sides, parent, 1.0)


def obox(m, name, center, axes, half, mat, parent, tint=1.0):
    """Oriented box from three orthonormal axes and half extents."""
    corners = []
    for sx, sy, sz in [(-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1), (-1, 1, -1), (1, 1, -1), (1, 1, 1), (-1, 1, 1)]:
        corners.append(tuple(center[k] + sx * axes[0][k] * half[0] + sy * axes[1][k] * half[1] + sz * axes[2][k] * half[2] for k in range(3)))
    faces = [(0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]
    m.add_flat_mesh(name, corners, faces, mat, parent, auto_orient=True, tint=tint)


def rod(m, name, start, end, radius, mat, parent, sides=6):
    direction = vec3_normalize(vec3_sub(end, start))
    helper = (0, 1, 0) if abs(direction[1]) < 0.9 else (1, 0, 0)
    u = vec3_normalize(vec3_cross(direction, helper))
    v = vec3_cross(direction, u)
    points, normals, indices = [], [], []
    for center in (start, end):
        for i in range(sides):
            a = i * math.tau / sides
            n = tuple(u[k] * math.cos(a) + v[k] * math.sin(a) for k in range(3))
            points.append(tuple(center[k] + n[k] * radius for k in range(3)))
            normals.append(n)
    for i in range(sides):
        j = (i + 1) % sides
        indices.extend([i, i + sides, j + sides, i, j + sides, j])
    m.add_mesh_direct(name, points, normals, indices, mat, parent)


def revolve(m, name, profile, sides, mat, parent, pos=(0, 0, 0), tint=1.0, scale_x=1.0):
    """Smooth surface of revolution around +Y from (radius, y) points, bottom to top.

    A last point with radius 0 closes the top. scale_x stretches the plan into an ellipse.
    """
    points, normals, indices = [], [], []
    count = len(profile)
    for k, (r, y) in enumerate(profile):
        prev = profile[max(0, k - 1)]
        nxt = profile[min(count - 1, k + 1)]
        dr, dy = nxt[0] - prev[0], nxt[1] - prev[1]
        length = math.hypot(dr, dy) or 1.0
        nr, ny = dy / length, -dr / length
        for i in range(sides):
            a = i * math.tau / sides
            c, s = math.cos(a), math.sin(a)
            points.append((r * c * scale_x, y, r * s))
            normals.append(vec3_normalize((nr * c / scale_x, ny, nr * s)))
    for k in range(count - 1):
        for i in range(sides):
            j = (i + 1) % sides
            a, b, c, d = k * sides + i, k * sides + j, (k + 1) * sides + j, (k + 1) * sides + i
            if profile[k + 1][0] < 1e-6:
                indices.extend([a, b, c])
            else:
                indices.extend([a, b, c, a, c, d])
    m.add_mesh_direct(name, points, normals, indices, mat, parent, pos, tint=tint)


# ----------------------------------------------------------------------------
# Tracks
# ----------------------------------------------------------------------------


def _common_tangent(first, second, upper):
    """Normal of the outer common tangent of two circles given as (z, y, r)."""
    z1, y1, r1 = first
    z2, y2, r2 = second
    dz, dy = z2 - z1, y2 - y1
    length = math.hypot(dz, dy)
    ux, uy = dz / length, dy / length
    cosine = (r1 - r2) / length
    sine = math.sqrt(max(0.0, 1 - cosine * cosine))
    candidates = [
        (cosine * ux - sine * uy, cosine * uy + sine * ux),
        (cosine * ux + sine * uy, cosine * uy - sine * ux),
    ]
    return (max if upper else min)(candidates, key=lambda n: n[1])


def track_path(wheels, top_supports, idler, sprocket, arc_steps, sag=0.0):
    """Inner track run (z, y), counterclockwise.

    wheels: road wheels front to rear, as (z, y, r).
    top_supports: circles carrying the top run, front to rear (return rollers,
    or the road wheels themselves for designs without rollers).
    sag: downward offset of the top run between supports.
    """
    first, last = wheels[0], wheels[-1]
    path = []

    def on(circle, n):
        return (circle[0] + circle[2] * n[0], circle[1] + circle[2] * n[1])

    def arc(circle, a0, a1, steps):
        while a1 < a0:
            a1 += math.tau
        return [
            (circle[0] + circle[2] * math.cos(a0 + (a1 - a0) * i / steps), circle[1] + circle[2] * math.sin(a0 + (a1 - a0) * i / steps))
            for i in range(steps + 1)
        ]

    def angle(n):
        return math.atan2(n[1], n[0])

    for wheel in wheels:
        path.append((wheel[0], wheel[1] - wheel[2]))
    rear_low = _common_tangent(last, sprocket, upper=False)
    path += arc(last, -math.pi / 2, angle(rear_low), 2)[1:]
    rear_up = _common_tangent(top_supports[-1], sprocket, upper=True)
    path += arc(sprocket, angle(rear_low), angle(rear_up), arc_steps)
    chain = [sprocket] + list(reversed(top_supports)) + [idler]
    # Touch points of the top run: where it arrives on and leaves each support, rear to front.
    arrive, leave = {}, {}
    for i, (a, b) in enumerate(zip(chain[:-1], chain[1:])):
        n = _common_tangent(b, a, upper=True)
        leave[i] = on(a, n)
        arrive[i + 1] = on(b, n)
    # A support below the line from its neighbours would be touched in front of where the run
    # leaves it, so the path would loop back. The run then rests on top of that support instead.
    for i in range(1, len(chain) - 1):
        if arrive[i][0] < leave[i][0]:
            top = (chain[i][0], chain[i][1] + chain[i][2])
            arrive[i] = leave[i] = top
    for i in range(len(chain) - 1):
        a, b = chain[i], chain[i + 1]
        start, end = leave[i], arrive[i + 1]
        if a is not sprocket:
            path.append(start)
        if sag > 0 and a is not sprocket and b is not idler:
            mid = ((start[0] + end[0]) / 2, (start[1] + end[1]) / 2 - sag)
            path.append(mid)
        if b is not idler:
            path.append(end)
    front_up = _common_tangent(idler, top_supports[0], upper=True)
    front_low = _common_tangent(idler, first, upper=False)
    path += arc(idler, angle(front_up), angle(front_low), arc_steps)
    path += arc(first, angle(front_low), -math.pi / 2, 2)[:-1]
    cleaned = []
    for p in path:
        if not cleaned or math.hypot(p[0] - cleaned[-1][0], p[1] - cleaned[-1][1]) > 1e-3:
            cleaned.append(p)
    if math.hypot(cleaned[0][0] - cleaned[-1][0], cleaned[0][1] - cleaned[-1][1]) < 1e-3:
        cleaned.pop()
    return cleaned


def _outward_normals(path):
    normals = []
    count = len(path)
    for i in range(count):
        prev, nxt = path[i - 1], path[(i + 1) % count]
        dz, dy = nxt[0] - prev[0], nxt[1] - prev[1]
        length = math.hypot(dz, dy)
        normals.append((dy / length, -dz / length))  # right-hand normal of a CCW loop
    return normals


def track_belt(m, sign, parent, mat, path, x_inner, x_outer, thickness):
    normals = _outward_normals(path)
    outer = [(z + n[0] * thickness, y + n[1] * thickness) for (z, y), n in zip(path, normals)]
    xi, xo = sign * x_inner, sign * x_outer
    points, vnormals, indices = [], [], []

    def quad(a, b, c, d, n):
        start = len(points)
        points.extend([a, b, c, d])
        vnormals.extend([n] * 4)
        indices.extend([start, start + 1, start + 2, start, start + 2, start + 3])

    count = len(path)
    for i in range(count):
        j = (i + 1) % count
        (z0, y0), (z1, y1) = path[i], path[j]
        (oz0, oy0), (oz1, oy1) = outer[i], outer[j]
        dz, dy = z1 - z0, y1 - y0
        length = math.hypot(dz, dy) or 1.0
        n = (0.0, -dz / length, dy / length)
        quad((xi, oy0, oz0), (xo, oy0, oz0), (xo, oy1, oz1), (xi, oy1, oz1), n)
        quad((xi, y0, z0), (xo, y0, z0), (xo, y1, z1), (xi, y1, z1), (0.0, -n[1], -n[2]))
        quad((xo, oy0, oz0), (xo, y0, z0), (xo, y1, z1), (xo, oy1, oz1), (float(sign), 0.0, 0.0))
        quad((xi, oy0, oz0), (xi, y0, z0), (xi, y1, z1), (xi, oy1, oz1), (float(-sign), 0.0, 0.0))
    m.add_mesh_direct("TrackBelt", points, vnormals, indices, mat, parent)
    return outer, normals


def track_shoes(m, sign, parent, mat, outer, x_center, half_width, spacing=0.155, depth=0.016, length=0.045, guide=None):
    """Grousers on the outer face; `guide` adds center guide horns on the inner face."""
    count = len(outer)
    lengths = [0.0]
    for i in range(count):
        a, b = outer[i], outer[(i + 1) % count]
        lengths.append(lengths[-1] + math.hypot(b[0] - a[0], b[1] - a[1]))
    total = lengths[-1]
    shoes = int(total / spacing)
    segment = 0
    for s in range(shoes):
        d = s * total / shoes
        while lengths[segment + 1] < d:
            segment += 1
        a, b = outer[segment], outer[(segment + 1) % count]
        t = (d - lengths[segment]) / max(1e-6, lengths[segment + 1] - lengths[segment])
        z, y = a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t
        seg_len = math.hypot(b[0] - a[0], b[1] - a[1]) or 1.0
        tangent = (0.0, (b[1] - a[1]) / seg_len, (b[0] - a[0]) / seg_len)
        normal = (0.0, -tangent[2], tangent[1])
        center = (sign * x_center, y + normal[1] * depth * 0.9, z + normal[2] * depth * 0.9)
        obox(m, "TrackShoe", center, ((1.0, 0.0, 0.0), normal, tangent), (half_width, depth, length), mat, parent)


def track_links(
    m,
    sign,
    parent,
    path,
    x_inner,
    x_outer,
    thickness,
    pitch,
    body_mat,
    pad_mat=None,
    pin_mat=None,
    pad=(0.025, 0.45),
    gap=0.16,
    horn=None,
    horn_every=2,
):
    """Individual track links along the inner run `path` (CCW (z, y) loop).

    Each link is a plate shorter than the pitch, so the run shows the gaps between links.
    pad = (height, length fraction) of the grouser or rubber pad on the outer face.
    pin_mat adds the pin boss or end connector at the outer edge, seen from the side.
    horn = (height, width) adds a center guide horn on the inner face every `horn_every` links.
    Returns the number of links.
    """
    count = len(path)
    lengths = [0.0]
    for i in range(count):
        a, b = path[i], path[(i + 1) % count]
        lengths.append(lengths[-1] + math.hypot(b[0] - a[0], b[1] - a[1]))
    total = lengths[-1]
    # Whole horn periods around the loop, or the horn pattern would break where the loop closes.
    group = horn_every if horn is not None else 1
    links = max(8, round(total / (pitch * group)) * group)
    step = total / links

    def sample(d):
        d %= total
        lo, hi = 0, count
        while hi - lo > 1:
            mid = (lo + hi) // 2
            if lengths[mid] <= d:
                lo = mid
            else:
                hi = mid
        a, b = path[lo], path[(lo + 1) % count]
        t = (d - lengths[lo]) / max(1e-9, lengths[lo + 1] - lengths[lo])
        return a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t

    width = abs(x_outer - x_inner)
    xc = sign * (x_inner + x_outer) / 2
    # Plates, pads and pins repeat every link; guide horns repeat every `horn_every` links.
    # Each part moves through its own cycle, so plates interpolate over one pitch only.
    horn_cycle = step * (horn_every if horn is not None else 1)
    m.track_pattern_cycle = horn_cycle

    def frame(d):
        z0, y0 = sample(d - step / 2)
        z1, y1 = sample(d + step / 2)
        tz, ty = z1 - z0, y1 - y0
        length = math.hypot(tz, ty) or 1.0
        tz, ty = tz / length, ty / length
        return (z0 + z1) / 2, (y0 + y1) / 2, tz, ty

    for k in range(links):
        d = k * step
        z, y, tz, ty = frame(d)
        nz, ny = ty, -tz  # outward normal of a CCW loop
        first_part = len(m.parts)
        axes = ((1.0, 0.0, 0.0), (0.0, ny, nz), (0.0, ty, tz))
        half_len = step * (1 - gap) / 2
        obox(m, "TrackLink", (xc, y + ny * thickness / 2, z + nz * thickness / 2), axes, (width / 2, thickness / 2, half_len), body_mat, parent)
        if pad_mat is not None and pad[0] > 0:
            h = pad[0]
            obox(m, "TrackPad", (xc, y + ny * (thickness + h / 2), z + nz * (thickness + h / 2)), axes, (width * 0.46, h / 2, step * pad[1] / 2), pad_mat, parent)
        if pin_mat is not None:
            ez, ey = z + tz * step / 2, y + ty * step / 2
            px = sign * (x_outer + 0.012)
            obox(m, "TrackPin", (px, ey + ny * thickness / 2, ez + nz * thickness / 2), axes, (0.016, thickness * 0.42, step * 0.13), pin_mat, parent)
        first_horn = len(m.parts)
        if horn is not None and k % horn_every == 0:
            hh, hw = horn
            obox(m, "TrackHorn", (xc, y - ny * hh / 2, z - nz * hh / 2), axes, (hw / 2, hh / 2, step * 0.22), body_mat, parent)
        motion = getattr(m, "motion", None)
        if motion is None:
            motion = m.motion = {}
        current = (z, y, tz, ty)
        after_step, after_horn = frame(d + step), frame(d + horn_cycle)
        for index in range(first_part, len(m.parts)):
            horn_part = index >= first_horn
            motion[m.parts[index][0]] = ("link", current, after_horn if horn_part else after_step, horn_cycle if horn_part else step)
    return links


def track_wrap_period(pattern_cycle, wheel_radius, target=1000.0, spread=200.0):
    """Travel period, in model meters, at which the client wraps track travel.

    A whole number of track pattern cycles, so links never jump. Among those near `target`,
    the one closest to a whole number of road wheel turns, so wheels barely snap either.
    Wrapping keeps the shader's fract and sin arguments small on long drives.
    """
    turn = math.tau * wheel_radius
    best = None
    for n in range(int((target - spread) / pattern_cycle), int((target + spread) / pattern_cycle) + 1):
        period = n * pattern_cycle
        error = abs(period / turn - round(period / turn)) * turn
        if best is None or error < best[0]:
            best = (error, period)
    return best[1]


def tag_wheel(m, first_part, center_yz, radius):
    """Mark parts added since `first_part` as one wheel turning about the x axis through center_yz."""
    motion = getattr(m, "motion", None)
    if motion is None:
        motion = m.motion = {}
    for part in m.parts[first_part:]:
        motion[part[0]] = ("wheel", center_yz, radius)


def emit_motion_parts(m):
    """Move tagged wheel and track parts into meshes that carry their motion for the vertex shader.

    Call after bake_vertex_shading. Static parts keep no UVs, so the shader leaves them alone.
      wheel: UV = axle center (y, z), UV2 = (rolling radius, 0)
      link:  UV = vertex position one motion cycle later (y, z), UV2 = (link turn over the cycle, cycle length)
    One mesh per (parent, material) keeps the draw call count the same as for static parts.
    """
    motion = getattr(m, "motion", {})
    nodes = m.data["nodes"]
    groups = {}
    kept = []
    for part in m.parts:
        node_id, parent, mat, points, normals, indices, colors = part
        info = motion.get(node_id)
        if info is None:
            kept.append(part)
            continue
        offset = nodes[node_id]["translation"]
        group = groups.setdefault((parent, mat), ([], [], [], [], [], []))
        base = len(group[0])
        for point, normal, color in zip(points, normals, colors):
            x, y, z = (point[k] + offset[k] for k in range(3))
            group[0].append((x, y, z))
            group[1].append(tuple(normal))
            group[2].append(tuple(color))
            if info[0] == "wheel":
                group[3].append((info[1][0], info[1][1]))
                group[4].append((info[2], 0.0))
            else:
                (z0, y0, tz, ty), (z1, y1, tz1, ty1), cycle = info[1], info[2], info[3]
                nz, ny = ty, -tz
                nz1, ny1 = ty1, -tz1
                a = (z - z0) * tz + (y - y0) * ty
                b = (z - z0) * nz + (y - y0) * ny
                group[3].append((y1 + a * ty1 + b * ny1, z1 + a * tz1 + b * nz1))
                turn = math.atan2(ty1, tz1) - math.atan2(ty, tz)
                turn = (turn + math.pi) % math.tau - math.pi
                group[4].append((turn, cycle))
        group[5].extend(i + base for i in indices)
        if node_id in nodes[parent]["children"]:
            nodes[parent]["children"].remove(node_id)
    m.parts = kept
    for (parent, mat), (points, normals, colors, uv0, uv1, indices) in groups.items():
        name = f"{nodes[parent]['name']}_Motion_{m.data['materials'][mat]['name']}"
        primitive = {
            "attributes": {
                "POSITION": _accessor(m, points, 3, 5126, 34962),
                "NORMAL": _accessor(m, normals, 3, 5126, 34962),
                "COLOR_0": _accessor(m, colors, 4, 5126, 34962),
                "TEXCOORD_0": _accessor(m, uv0, 2, 5126, 34962),
                "TEXCOORD_1": _accessor(m, uv1, 2, 5126, 34962),
            },
            "indices": _accessor(m, indices, 1, 5125, 34963),
            "material": mat,
        }
        m.data["meshes"].append({"name": name, "primitives": [primitive]})
        node = m.node(name, parent=parent)
        m.data["nodes"][node]["mesh"] = len(m.data["meshes"]) - 1


# ----------------------------------------------------------------------------
# Baked vertex shading
# ----------------------------------------------------------------------------


def _hemisphere_directions(count, seed=7):
    """Cosine-weighted directions around +Z (Fibonacci spiral, deterministic)."""
    golden = math.pi * (3 - math.sqrt(5))
    dirs = []
    for i in range(count):
        u = (i + 0.5) / count
        r = math.sqrt(u)
        phi = i * golden + seed
        dirs.append((r * math.cos(phi), r * math.sin(phi), math.sqrt(max(0.0, 1 - u))))
    return np.asarray(dirs)


def _refine(points, normals, colors, indices, max_edge, min_height=0.12):
    """Split edges longer than max_edge until none remain, without cracks.

    Shared edges get one midpoint, and each triangle is split by a template for one,
    two or three split edges, so neighbors stay conforming. Gives the vertex AO bake
    enough samples on big plates to show contact shadows.
    """
    points, normals, colors = list(points), list(normals), list(colors)
    tris = [tuple(indices[i : i + 3]) for i in range(0, len(indices), 3)]

    def length(i, j):
        return math.dist(points[i], points[j])

    for _ in range(24):
        long_edges = set()
        for a, b, c in tris:
            edge = max(((a, b), (b, c), (c, a)), key=lambda e: length(*e))
            longest = length(*edge)
            cross = vec3_cross(vec3_sub(points[b], points[a]), vec3_sub(points[c], points[a]))
            height = math.sqrt(sum(v * v for v in cross)) / longest if longest > 0 else 0.0
            # Thin strips (track shoes, plate edges, barrels) gain nothing from more AO samples.
            if longest > max_edge and height > min_height:
                long_edges.add(tuple(sorted(edge)))
        if not long_edges:
            break
        mid = {}
        for i, j in long_edges:
            mid[(i, j)] = len(points)
            points.append(tuple((points[i][k] + points[j][k]) / 2 for k in range(3)))
            normals.append(vec3_normalize(tuple(normals[i][k] + normals[j][k] for k in range(3))))
            colors.append(tuple((colors[i][k] + colors[j][k]) / 2 for k in range(4)))

        def split(i, j):
            return mid.get(tuple(sorted((i, j))))

        result = []
        for tri in tris:
            flags = [split(tri[k], tri[(k + 1) % 3]) is not None for k in range(3)]
            count = sum(flags)
            if count == 0:
                result.append(tri)
                continue
            # Rotate so the split edges are ab (one split) or ab and bc (two splits).
            for rot in range(3):
                f = flags[rot:] + flags[:rot]
                if count == 3 or (count == 1 and f[0]) or (count == 2 and f[0] and f[1]):
                    break
            a, b, c = tri[rot:] + tri[:rot]
            ab, bc, ca = split(a, b), split(b, c), split(c, a)
            if count == 3:
                result += [(a, ab, ca), (ab, b, bc), (ca, bc, c), (ab, bc, ca)]
            elif count == 2:
                result.append((ab, b, bc))
                result += [(a, ab, bc), (a, bc, c)] if length(a, bc) < length(ab, c) else [(a, ab, c), (ab, bc, c)]
            else:
                result += [(a, ab, c), (ab, b, c)]
        tris = result
    return points, normals, colors, [i for tri in tris for i in tri]


def bake_vertex_shading(m, cell=0.035, rays=28, max_dist=1.1, strength=0.95, dirt_height=1.35, gear_nodes=(), max_edge=None):
    """Bake ambient occlusion into vertex color rgb and a dirt mask into alpha.

    Runs on the neutral pose. All geometry, plus a ground plane at y = 0, occludes.
    With max_edge, big triangles are split first so plates get contact shadows.
    """
    if max_edge:
        moving = getattr(m, "motion", {})
        for index, (node_id, parent, mat, points, normals, indices, colors) in enumerate(m.parts):
            if node_id in moving:
                continue
            p2, n2, c2, i2 = _refine(points, normals, colors, indices, max_edge)
            m.triangles += len(i2) // 3 - len(indices) // 3
            m.vertices_count += len(p2) - len(points)
            m.parts[index] = (node_id, parent, mat, p2, n2, i2, c2)
    nodes = m.data["nodes"]
    parent_of = {}
    for index, node in enumerate(nodes):
        for child in node.get("children", []):
            parent_of[child] = index

    def world_offset(node_id):
        offset = np.zeros(3)
        while node_id is not None:
            offset += np.asarray(nodes[node_id].get("translation", [0.0, 0.0, 0.0]))
            node_id = parent_of.get(node_id)
        return offset

    def under(node_id, names):
        while node_id is not None:
            if nodes[node_id]["name"] in names:
                return True
            node_id = parent_of.get(node_id)
        return False

    world_parts = []
    triangles = []
    for part in m.parts:
        node_id, _parent, _mat, points, normals, indices, colors = part
        world = np.asarray(points, dtype=float) + world_offset(node_id)
        world_parts.append((world, np.asarray(normals, dtype=float)))
        tri = np.asarray(indices, dtype=int).reshape(-1, 3)
        triangles.append(world[tri])
    tris = np.concatenate(triangles)

    lo = tris.reshape(-1, 3).min(axis=0) - 0.2
    hi = tris.reshape(-1, 3).max(axis=0) + 0.2
    dims = np.ceil((hi - lo) / cell).astype(int) + 1
    grid = np.zeros(dims, dtype=bool)

    a, b, c = tris[:, 0], tris[:, 1], tris[:, 2]
    area = 0.5 * np.linalg.norm(np.cross(b - a, c - a), axis=1)
    samples = np.clip((area / (cell * cell * 0.2)).astype(int) + 3, 3, 20000)
    rng = np.random.default_rng(60)
    rep = np.repeat(np.arange(len(tris)), samples)
    r1, r2 = rng.random(len(rep)), rng.random(len(rep))
    s = np.sqrt(r1)
    surface = (1 - s)[:, None] * a[rep] + (s * (1 - r2))[:, None] * b[rep] + (s * r2)[:, None] * c[rep]
    ijk = np.clip(((surface - lo) / cell).astype(int), 0, dims - 1)
    grid[ijk[:, 0], ijk[:, 1], ijk[:, 2]] = True

    directions = _hemisphere_directions(rays)
    steps = np.linspace(cell * 2.2, max_dist, 22)

    for part_index, (part, (world, normals)) in enumerate(zip(m.parts, world_parts)):
        node_id, parent, mat, points, part_normals, indices, colors = part
        n = normals / np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-9)
        helper = np.where(np.abs(n[:, 1:2]) < 0.9, np.array([[0.0, 1.0, 0.0]]), np.array([[1.0, 0.0, 0.0]]))
        t = np.cross(helper, n)
        t /= np.maximum(np.linalg.norm(t, axis=1, keepdims=True), 1e-9)
        bvec = np.cross(n, t)
        dirs = directions[None, :, 0:1] * t[:, None, :] + directions[None, :, 1:2] * bvec[:, None, :] + directions[None, :, 2:3] * n[:, None, :]
        origin = world + n * cell * 1.6
        occluded = np.zeros(dirs.shape[:2], dtype=bool)
        for distance in steps:
            q = origin[:, None, :] + dirs * distance
            hit = q[..., 1] < 0.0
            idx = ((q - lo) / cell).astype(int)
            inside = np.all((idx >= 0) & (idx < dims), axis=-1)
            sel = np.nonzero(inside)
            hit[sel] |= grid[idx[sel][:, 0], idx[sel][:, 1], idx[sel][:, 2]]
            occluded |= hit
        ao = 1.0 - strength * occluded.mean(axis=1)
        ao = np.clip(ao / 0.92, 0.28, 1.0)
        height = world[:, 1]
        dirt = np.clip((dirt_height - height) / (dirt_height - 0.15), 0.0, 1.0) ** 1.6 * 0.6
        dirt += (1.0 - ao) * 0.35
        if under(node_id, gear_nodes):
            dirt += 0.12
        dirt = np.clip(dirt, 0.0, 1.0)
        new_colors = [(col[0] * ao[i], col[1] * ao[i], col[2] * ao[i], float(dirt[i])) for i, col in enumerate(colors)]
        m.parts[part_index] = (node_id, parent, mat, points, part_normals, indices, new_colors)


# ----------------------------------------------------------------------------
# Marking decals
# ----------------------------------------------------------------------------


class MarkingAtlas:
    """A small RGBA atlas drawn with Pillow. Regions are (u0, v0, u1, v1) in [0, 1]."""

    def __init__(self, width=512, height=256, supersample=4):
        from PIL import Image, ImageDraw

        self._image_module = Image
        self.width, self.height, self.ss = width, height, supersample
        self.image = Image.new("RGBA", (width * supersample, height * supersample), (0, 0, 0, 0))
        self.draw = ImageDraw.Draw(self.image)

    def _box(self, region):
        u0, v0, u1, v1 = region
        return u0 * self.image.width, v0 * self.image.height, u1 * self.image.width, v1 * self.image.height

    def star(self, region, fill, outline=None, outline_width=0.08):
        x0, y0, x1, y1 = self._box(region)
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2 + (y1 - y0) * 0.04
        radius = min(x1 - x0, y1 - y0) * 0.47

        def points(r):
            pts = []
            for i in range(10):
                rr = r if i % 2 == 0 else r * 0.382
                a = -math.pi / 2 + i * math.pi / 5
                pts.append((cx + rr * math.cos(a), cy + rr * math.sin(a)))
            return pts

        if outline is not None:
            self.draw.polygon(points(radius), fill=outline)
            self.draw.polygon(points(radius * (1 - outline_width * 1.8)), fill=fill)
        else:
            self.draw.polygon(points(radius), fill=fill)

    def text(self, region, text, fill, font_names=("DIN Condensed Bold.ttf", "Arial Narrow Bold.ttf", "Arial Bold.ttf")):
        from PIL import ImageFont

        x0, y0, x1, y1 = self._box(region)
        size = int((y1 - y0) * 0.92)
        font = None
        for name in font_names:
            for folder in ("/System/Library/Fonts/Supplemental", "/System/Library/Fonts", "/Library/Fonts"):
                path = Path(folder) / name
                if path.exists():
                    font = ImageFont.truetype(str(path), size)
                    break
            if font:
                break
        if font is None:
            font = ImageFont.load_default(size=size)
        bbox = self.draw.textbbox((0, 0), text, font=font)
        w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
        scale = min(1.0, (x1 - x0) * 0.94 / max(1, w))
        if scale < 1.0:
            font = font.font_variant(size=int(size * scale))
            bbox = self.draw.textbbox((0, 0), text, font=font)
            w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
        self.draw.text(((x0 + x1 - w) / 2 - bbox[0], (y0 + y1 - h) / 2 - bbox[1]), text, font=font, fill=fill)

    def png_bytes(self):
        out = self.image.resize((self.width, self.height), self._image_module.LANCZOS)
        buffer = io.BytesIO()
        out.save(buffer, format="PNG", optimize=True)
        return buffer.getvalue()


def _accessor(m, values, width, kind, target=None):
    while len(m.binary) % 4 != 0:
        m.binary.append(0)
    offset = len(m.binary)
    flat = [v for row in values for v in row] if width > 1 else values
    code = "f" if kind == 5126 else "I"
    m.binary += struct.pack("<" + code * len(flat), *flat)
    view = {"buffer": 0, "byteOffset": offset, "byteLength": len(m.binary) - offset}
    if target is not None:
        view["target"] = target
    m.data["bufferViews"].append(view)
    item = {
        "bufferView": len(m.data["bufferViews"]) - 1,
        "componentType": kind,
        "count": len(values),
        "type": {1: "SCALAR", 2: "VEC2", 3: "VEC3", 4: "VEC4"}[width],
    }
    if width == 3 and values:
        item["min"] = [min(v[i] for v in values) for i in range(3)]
        item["max"] = [max(v[i] for v in values) for i in range(3)]
    m.data["accessors"].append(item)
    return len(m.data["accessors"]) - 1


def decal_material(m, atlas, name="Markings"):
    """Embed the atlas PNG and create an alpha-masked material that samples it."""
    png = atlas.png_bytes()
    while len(m.binary) % 4 != 0:
        m.binary.append(0)
    offset = len(m.binary)
    m.binary += png
    m.data["bufferViews"].append({"buffer": 0, "byteOffset": offset, "byteLength": len(png)})
    m.data.setdefault("images", []).append({"bufferView": len(m.data["bufferViews"]) - 1, "mimeType": "image/png"})
    m.data.setdefault("samplers", []).append({"magFilter": 9729, "minFilter": 9987, "wrapS": 33071, "wrapT": 33071})
    m.data.setdefault("textures", []).append({"sampler": len(m.data["samplers"]) - 1, "source": len(m.data["images"]) - 1})
    m.data["materials"].append(
        {
            "name": name,
            "pbrMetallicRoughness": {
                "baseColorTexture": {"index": len(m.data["textures"]) - 1},
                "metallicFactor": 0.0,
                "roughnessFactor": 0.78,
            },
            "alphaMode": "MASK",
            "alphaCutoff": 0.5,
        }
    )
    return len(m.data["materials"]) - 1


def add_decal(m, name, parent, center, normal, up, size, region, material):
    """A textured quad facing `normal`, slightly above the surface it marks."""
    n = vec3_normalize(normal)
    up_proj = vec3_sub(up, tuple(n[i] * sum(up[k] * n[k] for k in range(3)) for i in range(3)))
    u = vec3_normalize(up_proj)
    r = vec3_cross(u, n)
    w, h = size[0] / 2, size[1] / 2
    c = tuple(center[i] + n[i] * 0.006 for i in range(3))
    corners = [
        tuple(c[i] - r[i] * w - u[i] * h for i in range(3)),
        tuple(c[i] + r[i] * w - u[i] * h for i in range(3)),
        tuple(c[i] + r[i] * w + u[i] * h for i in range(3)),
        tuple(c[i] - r[i] * w + u[i] * h for i in range(3)),
    ]
    u0, v0, u1, v1 = region
    uvs = [(u0, v1), (u1, v1), (u1, v0), (u0, v0)]
    primitive = {
        "attributes": {
            "POSITION": _accessor(m, corners, 3, 5126, 34962),
            "NORMAL": _accessor(m, [n] * 4, 3, 5126, 34962),
            "TEXCOORD_0": _accessor(m, uvs, 2, 5126, 34962),
        },
        "indices": _accessor(m, [0, 1, 2, 0, 2, 3], 1, 5125, 34963),
        "material": material,
    }
    m.data["meshes"].append({"name": name, "primitives": [primitive]})
    node = m.node(name, parent=parent)
    m.data["nodes"][node]["mesh"] = len(m.data["meshes"]) - 1
    m.triangles += 2
    m.vertices_count += 4
    return node


def _local_triangles(m, parent, names):
    """Triangles of the named parts attached directly to `parent`, in parent space."""
    nodes = m.data["nodes"]
    found = []
    for node_id, part_parent, _mat, points, _normals, indices, _colors in m.parts:
        if part_parent != parent or nodes[node_id]["name"] not in names:
            continue
        pts = np.asarray(points, dtype=float) + np.asarray(nodes[node_id].get("translation", [0.0, 0.0, 0.0]))
        found.append(pts[np.asarray(indices, dtype=int).reshape(-1, 3)])
    if not found:
        raise ValueError(f"no parts named {sorted(names)} under node {parent}")
    return np.concatenate(found)


def _raycast(tris, origins, direction):
    """Nearest hit distance and facing normal per ray; all rays share one direction."""
    d = np.asarray(direction, dtype=float)
    a = tris[:, 0]
    e1, e2 = tris[:, 1] - a, tris[:, 2] - a
    p = np.cross(d, e2)
    det = np.einsum("ij,ij->i", e1, p)
    valid = np.abs(det) > 1e-12
    inv = np.where(valid, 1.0 / np.where(valid, det, 1.0), 0.0)
    s = origins[:, None, :] - a[None, :, :]
    u = np.einsum("rtk,tk->rt", s, p) * inv
    q = np.cross(s, e1[None, :, :])
    v = (q @ d) * inv
    t = np.einsum("rtk,tk->rt", q, e2) * inv
    hit = valid & (u >= 0) & (v >= 0) & (u + v <= 1) & (t > 1e-6)
    t = np.where(hit, t, np.inf)
    best = t.argmin(axis=1)
    dist = t[np.arange(len(origins)), best]
    n = np.cross(e1, e2)[best]
    n /= np.maximum(np.linalg.norm(n, axis=1, keepdims=True), 1e-12)
    n = np.where((n @ d)[:, None] > 0, -n, n)
    return dist, n


def surface_heights(m, parent, center_xz, radius, names, rings=2, per_ring=12):
    """Lowest and highest surface y of the named parts under a circular footprint.

    Used to seat hatches and cupolas on curved armor so that no part floats.
    """
    x, z = center_xz
    samples = [(x, z)]
    for k in range(1, rings + 1):
        r = radius * k / rings
        samples += [(x + r * math.cos(i * math.tau / per_ring), z + r * math.sin(i * math.tau / per_ring)) for i in range(per_ring)]
    origins = np.asarray([(sx, 50.0, sz) for sx, sz in samples])
    dist, _ = _raycast(_local_triangles(m, parent, names), origins, (0.0, -1.0, 0.0))
    if not np.all(np.isfinite(dist)):
        raise ValueError(f"footprint at {center_xz} r={radius} leaves the surface of {sorted(names)}")
    heights = 50.0 - dist
    return float(heights.min()), float(heights.max())


def surface_hit(m, parent, origin, direction, names):
    """First hit of a ray on the named parts: (point, outward normal), or None."""
    dist, normals = _raycast(_local_triangles(m, parent, names), np.asarray([origin], dtype=float), direction)
    if not np.isfinite(dist[0]):
        return None
    point = tuple(float(origin[k] + direction[k] * dist[0]) for k in range(3))
    return point, tuple(float(v) for v in normals[0])


def domed_hatch(m, name, parent, center, radius, mat, sides, rise=0.07, hinge_mat=None, hinge_dir=(0.0, 0.0, 1.0)):
    """A round hatch lid with a shallow dome and a hinge block, sitting on `center`."""
    profile = [(radius, 0.0), (radius, 0.025), (radius * 0.85, 0.025 + rise * 0.55), (radius * 0.5, 0.025 + rise * 0.9), (0.0, 0.025 + rise)]
    revolve(m, name, profile, sides, mat, parent, pos=center)
    if hinge_mat is not None:
        hx, hz = hinge_dir[0] * radius, hinge_dir[2] * radius
        m.add_box(name + "Hinge", (0.16 if abs(hinge_dir[2]) > 0.5 else 0.08, 0.06, 0.08 if abs(hinge_dir[2]) > 0.5 else 0.16), (center[0] + hx, center[1] + 0.03, center[2] + hz), hinge_mat, parent)


def add_conformal_decal(m, name, parent, center, normal, up, size, region, material, names, grid=(14, 10), lift=0.007, depth=0.5):
    """A textured decal projected along `normal` onto the named parts, so it follows curved armor.

    The decal is a grid laid out in the plane through `center`. Each vertex is cast back onto
    the surface and lifted by `lift` along the surface normal. UVs stay planar, like a spray stencil.
    """
    n = vec3_normalize(normal)
    up_proj = vec3_sub(up, tuple(n[i] * sum(up[k] * n[k] for k in range(3)) for i in range(3)))
    u_axis = np.asarray(vec3_normalize(up_proj))
    r_axis = np.asarray(vec3_cross(tuple(u_axis), n))
    n = np.asarray(n)
    cols, rows = grid
    u0, v0, u1, v1 = region
    plane, uvs = [], []
    for j in range(rows + 1):
        for i in range(cols + 1):
            s, t = i / cols, j / rows
            plane.append(np.asarray(center) + r_axis * (s - 0.5) * size[0] + u_axis * (t - 0.5) * size[1])
            uvs.append((u0 + (u1 - u0) * s, v1 - (v1 - v0) * t))
    plane = np.asarray(plane)
    dist, hit_normals = _raycast(_local_triangles(m, parent, names), plane + n * depth, -n)
    if not np.all(np.isfinite(dist)):
        raise ValueError(f"decal {name} hangs off the surface of {sorted(names)}")
    points = plane + n * depth - n * dist[:, None] + hit_normals * lift
    indices = []
    for j in range(rows):
        for i in range(cols):
            a = j * (cols + 1) + i
            b, c, d = a + 1, a + cols + 2, a + cols + 1
            indices.extend([a, b, c, a, c, d])
    primitive = {
        "attributes": {
            "POSITION": _accessor(m, [tuple(map(float, p)) for p in points], 3, 5126, 34962),
            "NORMAL": _accessor(m, [tuple(map(float, q)) for q in hit_normals], 3, 5126, 34962),
            "TEXCOORD_0": _accessor(m, uvs, 2, 5126, 34962),
        },
        "indices": _accessor(m, indices, 1, 5125, 34963),
        "material": material,
    }
    m.data["meshes"].append({"name": name, "primitives": [primitive]})
    node = m.node(name, parent=parent)
    m.data["nodes"][node]["mesh"] = len(m.data["meshes"]) - 1
    m.triangles += len(indices) // 3
    m.vertices_count += len(points)
    return node


def save_model(m, path):
    """Same as GltfModel.save; kept as a hook so decals and textures stay consistent."""
    return m.save(path)
