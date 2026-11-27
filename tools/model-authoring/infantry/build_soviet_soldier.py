"""Soviet expeditionary infantryman (circa 1980-1985), low-poly faceted game character.

Proportions come from an MPFB (MakeHuman, CC0) body dressed and posed in Blender; full-sleeve
Afghanka field uniform, 6B3 body armor, SSh-68 steel helmet with dust goggles, tactical webbing,
RD-54 pack or PG-7V rocket pack, boots, and modular weapon (AK-74 or RPG-7V) are built here.
Everything is baked, decimated to a game budget, flat shaded, and colored per face corner (COLOR_0)
with baked ambient occlusion. No textures.

Frame while building: MPFB humans face -Y with their left at +X. Finished meshes are turned to face +Y.
Run:
  blender --background --python build_soviet_soldier.py -- --role rifleman out.blend [out.glb]
  blender --background --python build_soviet_soldier.py -- --role rpg out.blend [out.glb]
"""

import argparse
import math
import os
import random
import re
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector, noise
from mathutils.bvhtree import BVHTree

argv = sys.argv[sys.argv.index("--") + 1:]
parser = argparse.ArgumentParser()
parser.add_argument("--role", choices=("rifleman", "rpg"), default="rifleman")
parser.add_argument("out_blend")
parser.add_argument("out_glb", nargs="?", default=None)
cli = parser.parse_args(argv)

ROLE = cli.role
OUT = cli.out_blend
OUT_GLB = cli.out_glb
MESH_NAME = "SovietRifleman" if ROLE == "rifleman" else "SovietRpg"
RIG_NAME = "SovietSoldierRig"

for o in list(bpy.data.objects):
    bpy.data.objects.remove(o)

from bl_ext.blender_org.mpfb.services.humanservice import HumanService  # noqa: E402
from bl_ext.blender_org.mpfb.services.locationservice import LocationService  # noqa: E402

DATA = LocationService.get_user_data()
macro = {
    "gender": 1.0,
    "age": 0.52,
    "muscle": 0.84,
    "weight": 0.64,
    "proportions": 0.62,
    "height": 0.51,
    "cupsize": 0.5,
    "firmness": 0.5,
    "race": {"asian": 0.0, "caucasian": 1.0, "african": 0.0},
}
human = HumanService.create_human(macro_detail_dict=macro)
rig = HumanService.add_builtin_rig(human, "game_engine")
for kind, rel in (
    ("Eyes", "eyes/low-poly/low-poly.mhclo"),
    ("Eyebrows", "eyebrows/eyebrow001/eyebrow001.mhclo"),
    ("Clothes", "clothes/male_casualsuit01/male_casualsuit01.mhclo"),
):
    HumanService.add_mhclo_asset(os.path.join(DATA, rel), human, asset_type=kind, subdiv_levels=0)
suit = bpy.data.objects["Human.male_casualsuit01"]
pb = rig.pose.bones
REST = {b.name: (b.matrix_local.copy(), b.head_local.copy(), b.tail_local.copy()) for b in rig.data.bones}


def update():
    bpy.context.view_layer.update()


def seg_t(p, a, b):
    d = b - a
    return max(-1.0, min(2.0, (p - a).dot(d) / d.length_squared)), d


def near_arm(p, s_, reach=0.085):
    for bone in ("upperarm_", "lowerarm_", "hand_"):
        a, b = REST[bone + s_][1], REST[bone + s_][2]
        t, d = seg_t(p, a, b)
        if (p - (a + d * min(max(t, 0), 1))).length < reach:
            return True
    return False


# ---- Rest pose edits: full Afghanka sleeves trimmed cleanly at wrist, bloused trousers ----
CUT = {}
for s_ in ("l", "r"):
    el, wr = REST["lowerarm_" + s_][1], REST["lowerarm_" + s_][2]
    la = (wr - el).normalized()
    CUT[s_] = (wr - la * 0.018, la)
    bm = bmesh.new()
    bm.from_mesh(suit.data)
    region = [f for f in bm.faces if near_arm(f.calc_center_median(), s_, 0.08)]
    geom = list({e for f in region for e in f.edges}) + list({v for f in region for v in f.verts}) + region
    q, n = CUT[s_]
    bmesh.ops.bisect_plane(bm, geom=geom, plane_co=q, plane_no=n, clear_outer=True)
    bm.to_mesh(suit.data)
    bm.free()

mask = human.vertex_groups.get("Delete.male_casualsuit01")
if mask is not None:
    bare_hands = [
        v.index
        for v in human.data.vertices
        for s_ in ("l", "r")
        if near_arm(v.co, s_, 0.095) and (v.co - CUT[s_][0]).dot(CUT[s_][1]) > -0.025
    ]
    mask.remove(bare_hands)

# Trousers tucked cleanly inside tall Soviet combat boots:
# Bisect suit cleanly at z=0.278 (strictly inside BootShaft) and smoothly round/blouse the lower trouser leg
# in rest pose so one continuous cloth surface drapes naturally into the boot top with zero colliding cuff.
bm = bmesh.new()
bm.from_mesh(suit.data)
all_geom = bm.verts[:] + bm.edges[:] + bm.faces[:]
bmesh.ops.bisect_plane(bm, geom=all_geom, plane_co=(0.0, 0.0, 0.278), plane_no=(0.0, 0.0, 1.0), clear_inner=True)
bm.to_mesh(suit.data)
bm.free()

for v in suit.data.vertices:
    z = v.co.z
    if z >= 0.78:
        continue
    s_leg = "l" if v.co.x > 0.0 else "r"
    a_hip, a_knee = REST["thigh_" + s_leg][1], REST["calf_" + s_leg][1]
    a_ank = REST["foot_" + s_leg][1]
    if z <= a_knee.z:
        t_seg = max(0.0, min(1.0, (z - a_ank.z) / max(a_knee.z - a_ank.z, 1e-6)))
        c_leg = a_ank.lerp(a_knee, t_seg)
    else:
        t_seg = max(0.0, min(1.0, (z - a_knee.z) / max(a_hip.z - a_knee.z, 1e-6)))
        c_leg = a_knee.lerp(a_hip, t_seg)
    dx, dy = v.co.x - c_leg.x, v.co.y - c_leg.y
    r = math.hypot(dx, dy)
    if r < 1e-5:
        continue
    ux, uy = dx / r, dy / r
    if z < 0.342:
        # Strictly inside BootCollar (boot top sits at z~0.335 with radius 0.062) so the crisp leather rim defines the seam
        t_in = max(0.0, min(1.0, (z - 0.278) / 0.064))
        r_target = 0.044 + 0.009 * t_in
        r_new = r * 0.15 + r_target * 0.85
    elif z < 0.445:
        # Smooth natural military trouser blousing rising cleanly above the leather boot collar
        t_bl = (z - 0.342) / 0.103
        t_s = t_bl * t_bl * (3.0 - 2.0 * t_bl)
        r_target = 0.053 + 0.025 * t_s + 0.006 * math.sin(math.pi * t_bl)
        r_new = r * 0.30 + r_target * 0.70
    elif z < 0.58:
        # Full field-cut knee/upper calf
        t_kn = (z - 0.445) / 0.135
        r_target = 0.078 + 0.008 * t_kn
        blend = 0.50 * (1.0 - t_kn) + 0.20 * t_kn
        r_new = r * (1.0 - blend) + max(r * 1.07, r_target) * blend
    else:
        # Slight relaxed military cut on upper thigh fading cleanly into hip
        t_th = (z - 0.58) / 0.20
        r_new = r * (1.06 - 0.06 * t_th)
    v.co.x = c_leg.x + ux * r_new
    v.co.y = c_leg.y + uy * r_new

bm = bmesh.new()
bm.from_mesh(human.data)
bmesh.ops.delete(bm, geom=[f for f in bm.faces if all(v.co.z < 0.32 for v in f.verts)], context="FACES_ONLY")
bm.to_mesh(human.data)
bm.free()


# ---- Pose helpers ----
def head(n):
    return pb[n].head.copy()


def tail(n):
    return pb[n].tail.copy()


def rotate_about(n, rot, pivot):
    m = pb[n].matrix.copy()
    pb[n].matrix = Matrix.Translation(pivot) @ rot.to_4x4() @ Matrix.Translation(-pivot) @ m
    update()


def aim(n, target):
    h, t = head(n), tail(n)
    rotate_about(n, (t - h).rotation_difference(Vector(target) - h).to_matrix(), h)


def roll_to(n, rest_dir, want):
    local = REST[n][0].to_3x3().inverted() @ Vector(rest_dir)
    cur = pb[n].matrix.to_3x3() @ local
    ax = (tail(n) - head(n)).normalized()
    a = (cur - ax * cur.dot(ax)).normalized()
    b = (Vector(want) - ax * Vector(want).dot(ax)).normalized()
    rotate_about(n, Matrix.Rotation(math.atan2(ax.dot(a.cross(b)), a.dot(b)), 3, ax), head(n))


def elbow(a, b, l1, l2, pole):
    d = b - a
    length = min(d.length, l1 + l2 - 1e-3)
    ax = d.normalized()
    x = (l1 * l1 - l2 * l2 + length * length) / (2 * length)
    h = math.sqrt(max(l1 * l1 - x * x, 0.0))
    p = (pole - ax * pole.dot(ax)).normalized()
    return a + ax * x + p * h


def blen(n):
    return (REST[n][2] - REST[n][1]).length


def frame_point(origin, X, Y, Z):
    return lambda x, y, z=0.0: origin + X * x + Y * y + Z * z


# Standing combat stance
for s, ankle in (("l", Vector((0.135, -0.015, 0.075))), ("r", Vector((-0.14, 0.02, 0.075)))):
    hip = head("thigh_" + s)
    knee = elbow(hip, ankle, blen("thigh_" + s), blen("calf_" + s), Vector((0, -1, 0)))
    aim("thigh_" + s, knee)
    aim("calf_" + s, ankle)
    fwd = Vector((0.12 if s == "l" else -0.12, -1, 0)).normalized()
    ball = ankle + fwd * 0.133
    ball.z = 0.009
    aim("foot_" + s, ball)
    toe = ball + fwd * 0.074
    toe.z = 0.015
    aim("ball_" + s, toe)


ARM_BONE_PREFIXES = ("upperarm_", "lowerarm_", "hand_", "thumb_", "index_", "middle_", "ring_", "pinky_")


def torso_tree():
    dgx = bpy.context.evaluated_depsgraph_get()
    verts, polys = [], []
    for o in (human, suit):
        arm_gids = {g.index for g in o.vertex_groups if g.name.startswith(ARM_BONE_PREFIXES)}
        me = o.evaluated_get(dgx).to_mesh()
        # Use evaluated me.vertices (after Mask modifier) so indices match me.polygons exactly!
        arm_v = {
            v.index
            for v in me.vertices
            if sum(g.weight for g in v.groups if g.group in arm_gids) > 0.20
        }
        base = len(verts)
        verts.extend(o.matrix_world @ v.co for v in me.vertices)
        for p in me.polygons:
            if not any(vi in arm_v for vi in p.vertices):
                polys.append([base + i for i in p.vertices])
        o.evaluated_get(dgx).to_mesh_clear()
    return BVHTree.FromPolygons(verts, polys)


TORSO = torso_tree()
WAIST_Z = head("pelvis").lerp(head("spine_01"), 0.75).z
CHEST_Z = head("spine_03").lerp(tail("spine_03"), 0.35).z
FWD = Vector((0, -1, 0))
UPW = Vector((0, 0, 1))
LEFT = Vector((1, 0, 0))

if ROLE == "rifleman":
    # AK-74 carried at combat low-ready across chest (clear of 6B3 armor + chest rig)
    axis = Vector((0.72, -0.36, -0.58)).normalized()
    r_up = (UPW - axis * axis.dot(UPW)).normalized()
    r_left = r_up.cross(axis).normalized()
    butt = Vector((-0.25, -0.14, 1.22))

    def hold_ak(origin):
        RP_ = frame_point(origin, axis, r_up, r_left)
        h = dict(
            RP=RP_,
            grip_top=RP_(0.325, -0.040),
            grip_bottom=RP_(0.295, -0.135),
            guard=RP_(0.585, -0.010),
        )
        gax = (h["grip_bottom"] - h["grip_top"]).normalized()
        gfront = (axis - gax * axis.dot(gax)).normalized()
        gc = h["grip_top"].lerp(h["grip_bottom"], 0.42)
        h["r_dir"] = (gfront * 0.75 + r_left * 0.45 - gax * 0.10).normalized()
        h["r_wrist"] = gc - h["r_dir"] * 0.075 - r_left * 0.030
        h["l_dir"] = (-r_left * 0.95 + r_up * 0.15).normalized()
        h["l_wrist"] = h["guard"] - r_up * 0.055 + r_left * 0.050
        return h

    def ak_points(RP_):
        pts = [RP_(x, y, z) for x in [i * 0.05 for i in range(19)] for y in (0.01, -0.03) for z in (-0.018, 0.018)]
        pts += [RP_(0.02, -0.095), RP_(0.31, -0.13), RP_(0.43, -0.19), RP_(0.48, -0.22)]
        return pts

    def clearance_violation(RP_):
        worst = 0.0
        for p in ak_points(RP_):
            loc, nrm, _, dist = TORSO.find_nearest(p)
            if loc is None:
                continue
            # Clear 6B3 armor + Lifchik chest rig pouches (~9 cm front chest/waist thickness)
            want = 0.095 if (WAIST_Z - 0.10 < p.z < CHEST_Z + 0.14 and p.y < 0.05) else 0.04
            signed = dist if (p - loc).dot(nrm) > 0 else -dist
            worst = max(worst, want - signed)
        return worst

    reach_l = (blen("upperarm_l") + blen("lowerarm_l")) * 0.93
    for _ in range(180):
        hd = hold_ak(butt)
        if clearance_violation(hd["RP"]) > 0.0:
            butt = butt + FWD * 0.006
            continue
        gap = hd["l_wrist"] - head("upperarm_l")
        if gap.length <= reach_l:
            break
        move = -gap.normalized()
        if move.dot(FWD) < 0:
            move = move - FWD * move.dot(FWD)
        butt = butt + move.normalized() * 0.006
    print("AK74_CLEARANCE", round(clearance_violation(hd["RP"]), 4),
          "LEFT_GAP", round((hd["l_wrist"] - head("upperarm_l")).length - reach_l, 4))
    RP = hd["RP"]
    arm_targets = (
        ("r", hd["r_wrist"], hd["r_dir"], r_left, Vector((-1.0, 0.6, -0.2))),
        ("l", hd["l_wrist"], hd["l_dir"], r_up, Vector((1.0, 0.3, -0.6))),
    )
else:
    # RPG-7V resting on top of right shoulder pad, angled slightly up-forward in ready carry.
    # Slight combat turn of torso (+10 deg left) gives both arms a completely natural, relaxed grip.
    rotate_about("spine_01", Matrix.Rotation(math.radians(6.0), 3, UPW), head("spine_01"))
    rotate_about("spine_02", Matrix.Rotation(math.radians(4.0), 3, UPW), head("spine_02"))
    rotate_about("neck_01", Matrix.Rotation(math.radians(-5.0), 3, UPW), head("neck_01"))
    rotate_about("head", Matrix.Rotation(math.radians(-5.0), 3, UPW), head("head"))
    axis = Vector((0.02, -0.97, 0.24)).normalized()
    r_up = (UPW - axis * axis.dot(UPW)).normalized()
    r_left = r_up.cross(axis).normalized()
    sh_r = head("upperarm_r")
    shoulder_rest = sh_r + UPW * 0.098 - LEFT * 0.020 + FWD * 0.045
    butt = shoulder_rest - axis * 0.34
    RP = frame_point(butt, axis, r_up, r_left)
    # Front trigger grip (x=0.73) for right hand, rear support grip (x=0.57) for left hand.
    f_grip_top, f_grip_bot = RP(0.73, -0.024), RP(0.71, -0.135)
    r_grip_top, r_grip_bot = RP(0.57, -0.024), RP(0.555, -0.125)
    fg_ax = (f_grip_bot - f_grip_top).normalized()
    fg_fwd = (axis - fg_ax * axis.dot(fg_ax)).normalized()
    r_dir = (fg_fwd * 0.72 + r_left * 0.50 - fg_ax * 0.10).normalized()
    r_wrist = f_grip_top.lerp(f_grip_bot, 0.45) - r_dir * 0.072 - r_left * 0.028
    l_dir = (-r_left * 0.85 + fg_fwd * 0.45 + r_up * 0.10).normalized()
    l_wrist = r_grip_top.lerp(r_grip_bot, 0.45) - l_dir * 0.072 + r_left * 0.020
    arm_targets = (
        ("r", r_wrist, r_dir, r_left, Vector((-0.9, -0.30, -0.90))),
        ("l", l_wrist, l_dir, r_up, Vector((0.70, -0.70, -0.80))),
    )

for s, wrist, hand_dir, palm, pole in arm_targets:
    sh = head("upperarm_" + s)
    el = elbow(sh, wrist, blen("upperarm_" + s), blen("lowerarm_" + s), pole)
    aim("upperarm_" + s, el)
    aim("lowerarm_" + s, wrist)
    aim("hand_" + s, head("hand_" + s) + hand_dir)
    roll_to("hand_" + s, (-1.0, 0.0, 0.0) if s == "l" else (1.0, 0.0, 0.0), palm)

for s in ("l", "r"):
    for f in ("index", "middle", "ring", "pinky"):
        for k, ang in ((1, 55), (2, 70), (3, 45)):
            pb["%s_0%d_%s" % (f, k, s)].rotation_euler = (math.radians(ang), 0.0, 0.0)
    for k, ang in ((1, 15), (2, 25), (3, 20)):
        pb["thumb_0%d_%s" % (k, s)].rotation_euler = (math.radians(ang), 0.0, 0.0)
update()

dg = bpy.context.evaluated_depsgraph_get()
SUIT = BVHTree.FromObject(suit, dg)
BODY = BVHTree.FromObject(human, dg)
# Refresh TORSO after any subtle torso twist (e.g. RPG ready stance) while keeping arms excluded
TORSO = torso_tree()


def cast(tree, origin, direction, out=0.0):
    loc, nrm, _, _ = tree.ray_cast(Vector(origin), Vector(direction).normalized(), 2.0)
    if loc is None:
        loc, nrm, _, _ = tree.find_nearest(Vector(origin) + Vector(direction).normalized() * 0.6)
    return loc + nrm * out, nrm


# ---- Low-poly part builders ----
PARTS = []


def new_obj(name, bm, scheme):
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    obj["scheme"] = scheme
    PARTS.append(obj)
    return obj


def box(name, scheme, c, axes, half, chamfer=0.0):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=2.0)
    ax = [Vector(a) for a in axes]
    bmesh.ops.transform(
        bm,
        verts=bm.verts,
        matrix=Matrix.Translation(Vector(c)) @ Matrix((
            (ax[0].x * half[0], ax[1].x * half[1], ax[2].x * half[2], 0),
            (ax[0].y * half[0], ax[1].y * half[1], ax[2].y * half[2], 0),
            (ax[0].z * half[0], ax[1].z * half[1], ax[2].z * half[2], 0),
            (0, 0, 0, 1),
        )),
    )
    if chamfer > 0 and min(half) >= 0.015:
        bmesh.ops.bevel(bm, geom=bm.edges[:], offset=min(chamfer, min(half) * 0.45), segments=1, affect="EDGES")
    return new_obj(name, bm, scheme)


def prism(name, scheme, pts, half_t, P):
    bm = bmesh.new()
    a = [bm.verts.new(P(x, y, half_t)) for x, y in pts]
    b = [bm.verts.new(P(x, y, -half_t)) for x, y in pts]
    bm.faces.new(a)
    bm.faces.new(list(reversed(b)))
    n = len(pts)
    for i in range(n):
        bm.faces.new((a[i], a[(i + 1) % n], b[(i + 1) % n], b[i]))
    bmesh.ops.triangulate(bm, faces=[f for f in bm.faces if len(f.verts) > 4])
    return new_obj(name, bm, scheme)


def ring_loft(name, scheme, centres, radii, sides=8, up_hint=None, squash=1.0, caps=True, rot=0.0, wobble=0.0):
    bm = bmesh.new()
    rings = []
    for i, c in enumerate(centres):
        d = (Vector(centres[min(i + 1, len(centres) - 1)]) - Vector(centres[max(i - 1, 0)])).normalized()
        h = Vector(up_hint) if up_hint is not None else (Vector((0, 0, 1)) if abs(d.z) < 0.9 else Vector((1, 0, 0)))
        u = d.cross(h).normalized()
        v = d.cross(u).normalized()
        rings.append([
            bm.verts.new(
                Vector(c)
                + (u * math.cos(rot + math.tau * k / sides) * squash + v * math.sin(rot + math.tau * k / sides))
                * radii[i]
                * (1.0 + wobble * math.sin(k * 2.7 + i * 1.9) * (1 if k % 2 else -0.6))
            )
            for k in range(sides)
        ])
    for r0, r1 in zip(rings, rings[1:]):
        for k in range(sides):
            bm.faces.new((r0[k], r0[(k + 1) % sides], r1[(k + 1) % sides], r1[k]))
    if caps:
        bm.faces.new(list(reversed(rings[0])))
        bm.faces.new(rings[-1])
    return new_obj(name, bm, scheme)


def strap(name, scheme, pts, nrms, width, thick, flat=False):
    bm = bmesh.new()
    rows = []
    if flat:
        for i, (p, n) in enumerate(zip(pts, nrms)):
            d = (Vector(pts[min(i + 1, len(pts) - 1)]) - Vector(pts[max(i - 1, 0)])).normalized()
            w = d.cross(Vector(n)).normalized() * width / 2
            o = Vector(n).normalized() * thick
            rows.append([bm.verts.new(Vector(p) - w + o), bm.verts.new(Vector(p) + w + o)])
        for a, b in zip(rows, rows[1:]):
            bm.faces.new((a[0], a[1], b[1], b[0]))
        return new_obj(name, bm, scheme)
    for i, (p, n) in enumerate(zip(pts, nrms)):
        d = (Vector(pts[min(i + 1, len(pts) - 1)]) - Vector(pts[max(i - 1, 0)])).normalized()
        w = d.cross(Vector(n)).normalized() * width / 2
        o = Vector(n).normalized() * thick
        rows.append([
            bm.verts.new(Vector(p) - w),
            bm.verts.new(Vector(p) + w),
            bm.verts.new(Vector(p) + w + o),
            bm.verts.new(Vector(p) - w + o),
        ])
    for a, b in zip(rows, rows[1:]):
        for k in range(4):
            bm.faces.new((a[k], a[(k + 1) % 4], b[(k + 1) % 4], b[k]))
    bm.faces.new(list(reversed(rows[0])))
    bm.faces.new(rows[-1])
    return new_obj(name, bm, scheme)


def surface_frame(n, up_hint):
    n = Vector(n).normalized()
    u = (Vector(up_hint) - n * Vector(up_hint).dot(n)).normalized()
    return u.cross(n).normalized(), u, n


SUIT_POSED = bpy.data.meshes.new_from_object(suit.evaluated_get(bpy.context.evaluated_depsgraph_get()))


def edge_loop(near, radius):
    bm = bmesh.new()
    bm.from_mesh(SUIT_POSED)
    pts = [v.co.copy() for e in bm.edges if e.is_boundary for v in e.verts if (v.co - Vector(near)).length < radius]
    bm.free()
    return pts


def loop_ring(pts, axis_):
    c = sum(pts, Vector()) / len(pts)
    r = max((p - c - axis_ * (p - c).dot(axis_)).length for p in pts)
    return c, r


def conform_patch(name, scheme, centre, r, u, n, half_r, half_u, lift=0.009, thick=0.005):
    bm = bmesh.new()
    bm.from_mesh(SUIT_POSED)
    keep = set()
    for f in bm.faces:
        c = f.calc_center_median() - Vector(centre)
        if abs(c.dot(r)) < half_r and abs(c.dot(u)) < half_u and f.normal.dot(n) > 0.25 and abs(c.dot(n)) < 0.08:
            keep.add(f)
    if not keep:
        bm.free()
        return None
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f not in keep], context="FACES")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    bm.normal_update()
    for v in bm.verts:
        v.co += v.normal * lift
    ext = bmesh.ops.extrude_face_region(bm, geom=bm.faces[:])
    for v in [e for e in ext["geom"] if isinstance(e, bmesh.types.BMVert)]:
        v.co -= v.normal * thick
    return new_obj(name, bm, scheme)


def build_torus_armor_panel(name, scheme, z_min, z_max, ang_half_deg, center_dir, lift=0.022, thick=0.017, nx=12, nz=9):
    """Build a clean, seam-free parametric 6B3 armor shell cast onto TORSO.
    Eliminates the underlying suit shirt-placket crease and yields crisp uniform topology.
    """
    spine_mid = head("spine_02").lerp(head("spine_03"), 0.35)
    base_ang = math.atan2(center_dir.x, center_dir.y)
    bm = bmesh.new()
    outer_grid = []
    inner_grid = []
    for iz in range(nz):
        tz = iz / (nz - 1)
        z = z_min + (z_max - z_min) * tz
        # Natural 6B3 vest silhouette: tapers slightly narrower across upper chest for free arm movement
        width_scale = 1.0 - 0.24 * max(0.0, (tz - 0.58) / 0.42)
        half_rad = math.radians(ang_half_deg * width_scale)
        row_o, row_i = [], []
        for ix in range(nx):
            tx = ix / (nx - 1)
            ang = base_ang + (-1.0 + 2.0 * tx) * half_rad
            d = Vector((math.sin(ang), math.cos(ang), 0.0)).normalized()
            origin = Vector((spine_mid.x, spine_mid.y, z))
            p_hit, n_hit = cast(TORSO, origin + d * 0.55, -d, 0.0)
            # Blend radial outward vector with surface normal so the shell stays taut and wrinkle-free
            out_n = (d * 0.72 + n_hit * 0.28).normalized()
            # Subtle soft bevel at perimeter edges
            edge_dist = min(tx, 1.0 - tx, tz, 1.0 - tz)
            edge_taper = min(1.0, edge_dist / 0.14)
            loc_lift = lift * (0.72 + 0.28 * edge_taper)
            # Subtle sculpted horizontal 6B3 titanium-plate quilting on front/back
            quilt = 0.0028 * math.sin(tz * math.pi * 4.0) * edge_taper if 0.12 < tz < 0.85 else 0.0
            p_out = p_hit + out_n * (loc_lift + quilt)
            p_in = p_hit + out_n * max(0.002, loc_lift - thick)
            row_o.append(bm.verts.new(p_out))
            row_i.append(bm.verts.new(p_in))
        outer_grid.append(row_o)
        inner_grid.append(row_i)

    # Smooth outer & inner vertices slightly in Z/tangent to remove any tiny torso cloth bumps
    all_v = bm.verts[:]
    for _ in range(2):
        bmesh.ops.smooth_vert(bm, verts=all_v, factor=0.35, use_axis_x=True, use_axis_y=True, use_axis_z=False)

    # Outer and inner faces
    for iz in range(nz - 1):
        for ix in range(nx - 1):
            bm.faces.new((outer_grid[iz][ix], outer_grid[iz][ix + 1], outer_grid[iz + 1][ix + 1], outer_grid[iz + 1][ix]))
            bm.faces.new((inner_grid[iz][ix], inner_grid[iz + 1][ix], inner_grid[iz + 1][ix + 1], inner_grid[iz][ix + 1]))
    # Closed perimeter walls
    for ix in range(nx - 1):
        bm.faces.new((inner_grid[0][ix], inner_grid[0][ix + 1], outer_grid[0][ix + 1], outer_grid[0][ix]))
        bm.faces.new((outer_grid[-1][ix], outer_grid[-1][ix + 1], inner_grid[-1][ix + 1], inner_grid[-1][ix]))
    for iz in range(nz - 1):
        bm.faces.new((outer_grid[iz][0], outer_grid[iz + 1][0], inner_grid[iz + 1][0], inner_grid[iz][0]))
        bm.faces.new((inner_grid[iz][-1], inner_grid[iz + 1][-1], outer_grid[iz + 1][-1], outer_grid[iz][-1]))
    return new_obj(name, bm, scheme)


UP = Vector((0, 0, 1))
waist = head("pelvis").lerp(head("spine_01"), 0.75)
chest = head("spine_03").lerp(tail("spine_03"), 0.35)
neck = head("neck_01")

# ---- Continuous folded Afghanka tunic collar sweeping seamlessly around back/sides and down into a crisp front V ----
tunic_pts, tunic_nrms = [], []
for i in range(17):
    t = i / 16
    # Start at right front V-tip (ang=-177°), sweep up/around back (0°), end at left front V-tip (+177°)
    ang_deg = -177.0 + 354.0 * t
    ang = math.radians(ang_deg)
    d = Vector((math.sin(ang), math.cos(ang), 0.0)).normalized()
    frontness = max(0.0, -math.cos(ang)) ** 1.65
    z_tc = neck.z - 0.022 - 0.072 * frontness
    p_tc, n_tc = cast(TORSO, Vector((neck.x, neck.y, z_tc)) + d * 0.35, -d, 0.0075)
    tunic_pts.append(p_tc)
    tunic_nrms.append((d * 0.72 + n_tc * 0.28 + UP * (0.16 * (1.0 - frontness))).normalized())
strap("AfghankaCollar", "afghanka_dark", tunic_pts, tunic_nrms, 0.022, 0.007, flat=False)

# ---- 6B3 Soviet Body Armor Vest (seam-free parametric front & back armor shells + clean stitched plate ribs) ----
build_torus_armor_panel("ArmorFrontCarrier", "vest_od", waist.z + 0.030, chest.z + 0.105, 52.0, FWD, lift=0.022, thick=0.017, nx=10, nz=9)
build_torus_armor_panel("ArmorBackCarrier", "vest_od", waist.z + 0.030, chest.z + 0.115, 56.0, -FWD, lift=0.021, thick=0.016, nx=10, nz=9)

# Crisp stitched horizontal 6B3 titanium-plate pocket lines curving cleanly across the front shell
spine_mid_v = head("spine_02").lerp(head("spine_03"), 0.35)
for z_frac in (0.24, 0.45, 0.66):
    z_r = (waist.z + 0.030) + ((chest.z + 0.105) - (waist.z + 0.030)) * z_frac
    r_pts, r_nrms = [], []
    for k in range(7):
        ang = math.radians(-34 + 68 * k / 6)
        d = Vector((math.sin(ang), -math.cos(ang), 0.0)).normalized()
        ph, nh = cast(TORSO, Vector((spine_mid_v.x, spine_mid_v.y, z_r)) + d * 0.55, -d, 0.0)
        out_n = (d * 0.72 + nh * 0.28).normalized()
        r_pts.append(ph + out_n * 0.0235)
        r_nrms.append(out_n)
    strap("ArmorTileSeam", "vest_dark", r_pts, r_nrms, 0.008, 0.004, flat=False)

# Smooth anatomical armored shoulder straps connecting front & back of 6B3
for sx in (-1, 1):
    s = "l" if sx > 0 else "r"
    sh_c = head("clavicle_" + s).lerp(head("upperarm_" + s), 0.48)
    front_anchor = chest + LEFT * (sx * 0.092) + UP * 0.065
    back_anchor = chest + LEFT * (sx * 0.092) + UP * 0.065
    pts, nrms = [], []
    for i in range(9):
        t = i / 8
        if t < 0.40:
            q = front_anchor.lerp(sh_c + UP * 0.038, t / 0.40)
            o, d = q + FWD * 0.45, -FWD
        elif t < 0.62:
            q = sh_c + UP * 0.040 - FWD * ((t - 0.40) * 0.22)
            o, d = q + UP * 0.45, -UP
        else:
            q = (sh_c + UP * 0.032 - FWD * 0.045).lerp(back_anchor, (t - 0.62) / 0.38)
            o, d = q - FWD * 0.45, FWD
        pp, nn = cast(TORSO, o, d, 0.011)
        pts.append(pp)
        nrms.append(nn)
    strap("ArmorShoulder", "vest_od", pts, nrms, 0.066, 0.012, flat=False)

# Rounded low-profile ballistic neck pad at back of 6B3 (tapers smoothly to zero at sides)
collar_c = []
collar_r = []
for i in range(9):
    t = i / 8
    ang = math.radians(-78 + 156 * t)
    d = Vector((math.sin(ang), math.cos(ang), 0.0)).normalized()
    p_col, _ = cast(TORSO, neck - UP * 0.022 + d * 0.35, -d, 0.016)
    collar_c.append(p_col + UP * 0.008)
    taper = math.sin(math.pi * t) ** 0.55
    collar_r.append(0.010 + 0.014 * taper)
ring_loft("ArmorCollar", "vest_dark", collar_c, collar_r, sides=8, up_hint=UP, squash=0.75, caps=True)

# Side cummerbund straps of 6B3
for sx in (-1, 1):
    pts, nrms = [], []
    for i in range(6):
        ang = math.radians(sx * (48 + 84 * i / 5))
        d = Vector((math.sin(ang), -math.cos(ang), 0.0)).normalized()
        pp, nn = cast(TORSO, waist + UP * 0.08 + d * 0.5, -d, 0.013)
        pts.append(pp)
        nrms.append(nn)
    strap("ArmorSideStrap", "vest_dark", pts, nrms, 0.038, 0.007, flat=False)

# ---- Soviet leather combat belt and clean proud brass buckle (zero Z-fighting with belt!) ----
belt_pts = []
for k in range(17):
    a = math.tau * k / 16
    d = Vector((math.sin(a), -math.cos(a), 0))
    p, _ = cast(TORSO, waist + d * 0.6, -d, 0.016)
    belt_pts.append(p)
belt_r = max((p - waist).length for p in belt_pts) * 1.02
ring_loft(
    "CombatBelt",
    "leather",
    [waist - UP * 0.026, waist + UP * 0.026],
    [belt_r, belt_r],
    sides=20,
    squash=0.82,
    up_hint=FWD,
    caps=False,
)
belt = PARTS[-1]
sol = belt.modifiers.new("Solid", "SOLIDIFY")
sol.thickness = 0.008
# Position brass buckle cleanly outside the belt's outer front surface
buckle_p = waist + FWD * (belt_r + 0.009)
box("BeltBuckle", "brass", buckle_p, (LEFT, UP, FWD), (0.031, 0.025, 0.005), 0.003)
box("BeltBuckleInset", "brass_dark", buckle_p + FWD * 0.004, (LEFT, UP, FWD), (0.022, 0.016, 0.002), 0.001)

# ---- Chest rig ("Lifchik" / Poyas-A) for rifleman, or utility pouches for RPG gunner ----
if ROLE == "rifleman":
    for idx_cell, x_off in enumerate((-0.092, -0.031, 0.031, 0.092)):
        d_c = (FWD + LEFT * (x_off * 1.6)).normalized()
        pc, nc = cast(TORSO, chest - UP * 0.145 + d_c * 0.6, -d_c, 0.054)
        rc, uc, nc = surface_frame(nc, UP)
        box("LifchikCell", "webbing_khaki", pc, (rc, uc, nc), (0.025, 0.070, 0.023), 0.006)
        box("LifchikFlap", "webbing_dark", pc + uc * 0.054 + nc * 0.008, (rc, uc, nc), (0.026, 0.019, 0.025), 0.005)
        box("LifchikLatch", "steel", pc + uc * 0.035 + nc * 0.027, (rc, uc, nc), (0.006, 0.009, 0.003))
    for sx in (-1, 1):
        d_g = (FWD * 0.72 + LEFT * sx * 0.70).normalized()
        pg, ng = cast(TORSO, chest - UP * 0.15 + d_g * 0.6, -d_g, 0.042)
        rg, ug, ng = surface_frame(ng, UP)
        box("GrenadePouch", "webbing_khaki", pg, (rg, ug, ng), (0.024, 0.046, 0.023), 0.007)
        box("GrenadeFlap", "webbing_dark", pg + ug * 0.036 + ng * 0.006, (rg, ug, ng), (0.025, 0.015, 0.024), 0.005)
else:
    for sx in (-1, 1):
        d_g = (FWD * 0.80 + LEFT * sx * 0.60).normalized()
        pg, ng = cast(TORSO, waist + UP * 0.01 + d_g * 0.6, -d_g, 0.044)
        rg, ug, ng = surface_frame(ng, UP)
        box("UtilityPouch", "webbing_khaki", pg, (rg, ug, ng), (0.040, 0.056, 0.026), 0.008)
        box("UtilityFlap", "webbing_dark", pg + ug * 0.046 + ng * 0.006, (rg, ug, ng), (0.042, 0.015, 0.027), 0.005)

# ---- Belt kit: Soviet canteen on right hip, bayonet tucked cleanly on left hip, MPL-50 shovel at back ----
p_can, n_can = cast(TORSO, waist - LEFT * 0.6 - UP * 0.04 - FWD * 0.05, LEFT, 0.042)
rc, uc, nc = surface_frame(n_can, UP)
box("CanteenCover", "webbing_khaki", p_can - uc * 0.045, (rc, uc, nc), (0.054, 0.072, 0.034), 0.016)
ring_loft("CanteenCap", "steel", [p_can + uc * 0.026, p_can + uc * 0.048], [0.014, 0.012], sides=6)

p_bay, n_bay = cast(TORSO, waist + LEFT * 0.6 - UP * 0.04 - FWD * 0.04, -LEFT, 0.022)
rb, ub, nb = surface_frame(n_bay, (UP - FWD * 0.18).normalized())
box("BayonetScabbard", "bakelite", p_bay - ub * 0.068, (rb, ub, nb), (0.016, 0.095, 0.010), 0.004)
box("BayonetHandle", "bakelite_mag", p_bay + ub * 0.045, (rb, ub, nb), (0.014, 0.036, 0.011), 0.004)

p_sh, n_sh = cast(TORSO, waist - FWD * 0.6 + LEFT * 0.07, FWD, 0.034)
rs, us, ns = surface_frame(n_sh, UP)
box("ShovelCarrier", "webbing_khaki", p_sh - us * 0.065, (rs, us, ns), (0.060, 0.092, 0.017), 0.010)
ring_loft("ShovelHandle", "wood_ak", [p_sh - us * 0.15, p_sh - us * 0.285], [0.012, 0.012], sides=6)

# ---- Back pack: RD-54 assault pack (rifleman) OR PG-7V 3-rocket pack (rpg) ----
if ROLE == "rifleman":
    p_pk, _ = cast(TORSO, chest - FWD * 0.6 - UP * 0.05, FWD, 0.095)
    rp, up_p, np_ = surface_frame(-FWD, UP)
    # Main RD-54 rectangular airborne pack
    box("Rd54Main", "webbing_khaki", p_pk, (rp, up_p, np_), (0.145, 0.155, 0.072), 0.022)
    box("Rd54Flap", "webbing_dark", p_pk + up_p * 0.118 + np_ * 0.015, (rp, up_p, np_), (0.148, 0.046, 0.070), 0.016)
    for sx in (-1, 1):
        # Twin vertical side pockets characteristic of RD-54
        box("Rd54SidePocket", "webbing_khaki", p_pk + rp * sx * 0.165 - up_p * 0.02, (rp, up_p, np_),
            (0.034, 0.105, 0.046), 0.012)
        box("Rd54SideFlap", "webbing_dark", p_pk + rp * sx * 0.165 + up_p * 0.088, (rp, up_p, np_),
            (0.036, 0.020, 0.048), 0.008)
        box("Rd54Strap", "leather", p_pk + up_p * 0.03 + np_ * 0.076 + rp * sx * 0.075, (rp, up_p, np_),
            (0.013, 0.115, 0.004), 0.002)
        box("Rd54Buckle", "steel", p_pk - up_p * 0.03 + np_ * 0.080 + rp * sx * 0.075, (rp, up_p, np_),
            (0.015, 0.011, 0.003), 0.002)
    # Neat olive shelter-half / poncho roll strapped atop RD-54
    roll_c = p_pk + up_p * 0.215 + np_ * 0.008
    ring_loft(
        "ShelterRoll",
        "bedroll_od",
        [roll_c - rp * 0.185, roll_c - rp * 0.175, roll_c + rp * 0.175, roll_c + rp * 0.185],
        [0.052, 0.064, 0.064, 0.052],
        sides=12,
        up_hint=np_,
    )
    for sx in (-1, 1):
        ring_loft(
            "RollStrap",
            "leather",
            [roll_c + rp * sx * 0.10 - rp * 0.011, roll_c + rp * sx * 0.10 + rp * 0.011],
            [0.067, 0.067],
            sides=12,
            up_hint=np_,
        )
else:
    # Iconic Soviet RPG-7 3-round canvas rocket pack shifted slightly left so right shoulder exhaust stays clear
    p_pk, _ = cast(TORSO, chest - FWD * 0.6 - UP * 0.06 + LEFT * 0.045, FWD, 0.092)
    rp, up_p, np_ = surface_frame(-FWD, UP)
    # Slight tilt towards left shoulder so protruding PG-7V warheads frame the soldier's left side cleanly
    tilt = Matrix.Rotation(math.radians(-11.0), 3, np_)
    rp_t = (tilt @ rp).normalized()
    up_t = (tilt @ up_p).normalized()
    box("RpgPackBody", "webbing_khaki", p_pk - up_t * 0.03, (rp_t, up_t, np_), (0.125, 0.195, 0.062), 0.018)
    for y_band in (-0.11, 0.07):
        box("RpgPackBand", "webbing_dark", p_pk + up_t * y_band + np_ * 0.056, (rp_t, up_t, np_),
            (0.128, 0.016, 0.010), 0.003)
    # 3 protruding PG-7V warheads (unmistakable silhouette from RTS camera!)
    for idx_r, x_r in enumerate((-0.072, 0.0, 0.072)):
        base_r = p_pk + rp_t * x_r + up_t * 0.155 + np_ * 0.005
        ring_loft(
            "SparePg7Warhead",
            "pg7_olive",
            [
                base_r,
                base_r + up_t * 0.045,
                base_r + up_t * 0.105,
                base_r + up_t * 0.175,
                base_r + up_t * 0.215,
            ],
            [0.022, 0.038, 0.041, 0.015, 0.009],
            sides=8,
            up_hint=np_,
        )
        ring_loft(
            "SparePg7Fuze",
            "steel",
            [base_r + up_t * 0.215, base_r + up_t * 0.242],
            [0.009, 0.006],
            sides=6,
            up_hint=np_,
        )

# ---- Afghanka field pockets: thigh cargo pockets & upper-arm sleeve pockets + wrist cuffs ----
for s, sx in (("l", 1), ("r", -1)):
    th = head("thigh_" + s).lerp(tail("thigh_" + s), 0.55)
    side_dir = (LEFT * sx * 0.9 + FWD * 0.3).normalized()
    p, n = cast(SUIT, th + side_dir * 0.5, -side_dir, 0.006)
    r, u, n = surface_frame(n, (tail("thigh_" + s) - head("thigh_" + s)) * -1)
    conform_patch("CargoPocket", "afghanka", p, r, u, n, 0.060, 0.085, 0.009)
    conform_patch("CargoFlap", "afghanka_dark", p + u * 0.085, r, u, n, 0.064, 0.022, 0.016)

    # Signature angled upper-arm Afghanka pockets (cast along actual upperarm outward normal so raised arms stay flush)
    ua_pt = head("upperarm_" + s).lerp(tail("upperarm_" + s), 0.36)
    arm_ax = (tail("upperarm_" + s) - head("upperarm_" + s)).normalized()
    arm_out = (LEFT * sx - arm_ax * (LEFT * sx).dot(arm_ax))
    arm_out = arm_out.normalized() if arm_out.length > 1e-3 else (LEFT * sx)
    pa, na = cast(SUIT, ua_pt + arm_out * 0.35, -arm_out, 0.003)
    ra, ua_u, na = surface_frame(na, -arm_ax)
    box("ArmPocket", "afghanka", pa, (ra, ua_u, na), (0.034, 0.052, 0.007), 0.004)
    box("ArmPocketFlap", "afghanka_dark", pa + ua_u * 0.044 + na * 0.002, (ra, ua_u, na), (0.036, 0.014, 0.008), 0.003)

    # Clean wrist cuff closing full Afghanka sleeve
    wr = head("hand_" + s)
    la = (wr - head("lowerarm_" + s)).normalized()
    pts = edge_loop(wr - la * 0.025, 0.09)
    if len(pts) >= 5:
        c_cf, r_cf_ = loop_ring(pts, la)
        r_cf_ += 0.0015
        ring_loft(
            "SleeveCuff",
            "afghanka_dark",
            [c_cf - la * 0.022, c_cf, c_cf + la * 0.012],
            [r_cf_ * 1.01, r_cf_ * 0.99, r_cf_ * 0.92],
            sides=10,
            up_hint=FWD,
        )

# ---- Natural Afghanka trousers tucked cleanly into high Soviet combat boots ----
for s_, sx in (("l", 1), ("r", -1)):
    a, k = head("foot_" + s_), head("calf_" + s_)
    shin = (k - a).normalized()
    fwd = tail("ball_" + s_) - a
    fwd.z = 0
    fwd.normalize()
    lat = Vector((0, 0, 1)).cross(fwd).normalized()
    top = a + shin * 0.242
    tr = 0.058

    # Clean parametric reinforced Afghanka knee patch (smooth rounded rectangle cast onto leg, zero poke-through)
    bm_kp = bmesh.new()
    kp_o, kp_i = [], []
    for iz in range(5):
        tz = -1.0 + 2.0 * iz / 4
        row_o, row_i = [], []
        for ix in range(5):
            tx = -1.0 + 2.0 * ix / 4
            ang_k = math.radians(32.0 * tx)
            dir_k = (fwd * math.cos(ang_k) + lat * math.sin(ang_k)).normalized()
            orig_k = k + shin * (0.010 + 0.060 * tz)
            pk_h, nk_h = cast(SUIT, orig_k + dir_k * 0.35, -dir_k, 0.0)
            out_k = (dir_k * 0.68 + nk_h * 0.32).normalized()
            edge_k = min(1.0 - abs(tx), 1.0 - abs(tz))
            lift_k = 0.0090 if edge_k > 0.01 else 0.0052
            row_o.append(bm_kp.verts.new(pk_h + out_k * lift_k))
            row_i.append(bm_kp.verts.new(pk_h - out_k * 0.002))
        kp_o.append(row_o)
        kp_i.append(row_i)
    for iz in range(4):
        for ix in range(4):
            bm_kp.faces.new((kp_o[iz][ix], kp_o[iz][ix + 1], kp_o[iz + 1][ix + 1], kp_o[iz + 1][ix]))
    for ix in range(4):
        bm_kp.faces.new((kp_i[0][ix], kp_i[0][ix + 1], kp_o[0][ix + 1], kp_o[0][ix]))
        bm_kp.faces.new((kp_o[-1][ix], kp_o[-1][ix + 1], kp_i[-1][ix + 1], kp_i[-1][ix]))
    for iz in range(4):
        bm_kp.faces.new((kp_o[iz][0], kp_o[iz + 1][0], kp_i[iz + 1][0], kp_i[iz][0]))
        bm_kp.faces.new((kp_i[iz][-1], kp_i[iz + 1][-1], kp_o[iz + 1][-1], kp_o[iz][-1]))
    new_obj("KneePatch", bm_kp, "afghanka_dark")

    # High Soviet combat boot shaft: seamless overlap into vamp/heel, soft ankle crease, tailored padded collar
    ring_loft(
        "BootShaft",
        "boot",
        [
            a - shin * 0.012,
            a + shin * 0.025,
            a + shin * 0.058,
            a + shin * 0.095,
            a.lerp(top, 0.72),
            top + shin * 0.008,
        ],
        [0.052, 0.051, 0.047, 0.050, 0.056, tr],
        sides=16,
        up_hint=fwd,
        squash=1.08,
        caps=False,
    )
    # Soft padded leather collar rim at the top of the boot shaft (cleanly receives the bloused trouser tuck)
    ring_loft(
        "BootCollar",
        "boot_strap",
        [top - shin * 0.002, top + shin * 0.010, top + shin * 0.022, top + shin * 0.026],
        [tr * 1.00, tr * 1.07, tr * 1.05, tr * 0.86],
        sides=16,
        up_hint=fwd,
        squash=1.08,
        caps=False,
    )
    # Top cinch strap + side metal buckle just below collar
    strap_z = top - shin * 0.022
    ring_loft(
        "BootStrap",
        "boot_strap",
        [strap_z - shin * 0.008, strap_z + shin * 0.008],
        [tr * 1.01, tr * 1.01],
        sides=16,
        up_hint=fwd,
        squash=1.08,
        caps=False,
    )
    box(
        "BootBuckle",
        "steel",
        strap_z + lat * (sx * (tr * 1.02)),
        (fwd, shin, lat * sx),
        (0.010, 0.009, 0.003),
        0.001,
    )

    ground = Vector((a.x, a.y, 0.0))

    def section(x, top_z, hw, n_pow, bottom=0.025, k_s=16):
        pts_ = []
        for j in range(k_s):
            ang = math.tau * j / k_s
            cx, sy = math.cos(ang), math.sin(ang)
            e = 2.0 / n_pow
            y = hw * math.copysign(abs(cx) ** e, cx)
            z = (bottom + top_z) / 2 + (top_z - bottom) / 2 * math.copysign(abs(sy) ** e, sy)
            pts_.append(ground + fwd * x + lat * y + Vector((0, 0, z)))
        return pts_

    # Refined anatomical boot vamp, rounded reinforced toe cap, and sculpted heel counter
    secs = [
        section(-0.048, 0.100, 0.044, 3.8),
        section(-0.030, 0.122, 0.048, 3.6),
        section(0.005, 0.124, 0.049, 3.2),
        section(0.055, 0.098, 0.048, 2.8),
        section(0.105, 0.074, 0.049, 2.7),
        section(0.155, 0.062, 0.047, 2.6),
        section(0.195, 0.056, 0.040, 2.5),
        section(0.222, 0.047, 0.028, 2.3),
        section(0.236, 0.036, 0.016, 2.2),
    ]
    fbm = bmesh.new()
    rings_ = [[fbm.verts.new(p) for p in sec] for sec in secs]
    for r0, r1 in zip(rings_, rings_[1:]):
        for j in range(16):
            fbm.faces.new((r0[j], r0[(j + 1) % 16], r1[(j + 1) % 16], r1[j]))
    fbm.faces.new(list(reversed(rings_[0])))
    fbm.faces.new(rings_[-1])
    new_obj("BootFoot", fbm, "boot")

    # Front lacing up the instep & shaft
    boot_parts = [o for o in PARTS[-8:] if o.name.startswith(("BootShaft", "BootFoot"))]
    b_verts, b_polys = [], []
    for bo in boot_parts:
        base_i = len(b_verts)
        b_verts.extend(bo.matrix_world @ v.co for v in bo.data.vertices)
        b_polys.extend([[base_i + i for i in p.vertices] for p in bo.data.polygons])
    btree = BVHTree.FromPolygons(b_verts, b_polys)
    guide = [
        ground + fwd * 0.105 + Vector((0, 0, 0.056)),
        a + fwd * 0.042 - shin * 0.005,
        a + shin * 0.155 + fwd * 0.026,
    ]
    l_rows = []
    for i in range(6):
        t = i / 5
        g = guide[0].lerp(guide[1], t * 2) if t < 0.5 else guide[1].lerp(guide[2], (t - 0.5) * 2)
        out = (fwd + Vector((0, 0, 1)) * (0.85 * (1 - t))).normalized()
        loc, nrm, _, _ = btree.ray_cast(g + out * 0.2, -out, 0.3)
        if loc is not None:
            l_rows.append((loc, nrm.normalized()))
    for i, (lp, ln) in enumerate(l_rows):
        nxt = l_rows[min(i + 1, len(l_rows) - 1)][0] - l_rows[max(i - 1, 0)][0]
        side_v = ln.cross(nxt).normalized()
        l_rows[i] = (lp, ln, side_v)
    for i in range(len(l_rows) - 1):
        (p0, n0, s0), (p1, n1, s1) = l_rows[i], l_rows[i + 1]
        for sgn in (-1, 1):
            a0 = p0 + s0 * sgn * 0.015 + n0 * 0.0025
            a1 = p1 - s1 * sgn * 0.015 + n1 * 0.0025
            strap("BootLace", "boot_lace", [a0, a1], [n0, n1], 0.0045, 0.0025, flat=True)

    # Rounded anatomical welt sole + curved heel block (flush with heel counter, zero square shelf)
    sole = [
        (-0.051, 0.020), (-0.046, 0.038), (-0.028, 0.048), (0.048, 0.050), (0.125, 0.049), (0.195, 0.041),
        (0.231, 0.025), (0.243, 0.0), (0.231, -0.025), (0.195, -0.041), (0.125, -0.049), (0.048, -0.050),
        (-0.028, -0.048), (-0.046, -0.038), (-0.051, -0.020),
    ]
    heel = [
        (-0.051, 0.020), (-0.046, 0.038), (-0.028, 0.048), (-0.004, 0.048),
        (-0.004, -0.048), (-0.028, -0.048), (-0.046, -0.038), (-0.051, -0.020),
    ]
    SP_ = lambda z0: (lambda x, y, z: ground + fwd * x + lat * y + Vector((0, 0, z0 + z)))
    prism("BootSole", "sole", sole, 0.009, SP_(0.018))
    prism("BootHeel", "sole", heel, 0.0095, SP_(0.0095))
    # Subtle inset tread bars on sole underside
    for x_t in (0.065, 0.115, 0.165, 0.205):
        hw_t = 0.042 if x_t < 0.19 else 0.028
        box("BootTread", "sole", ground + fwd * x_t + Vector((0, 0, 0.0045)), (fwd, lat, UP), (0.012, hw_t, 0.0045))

# ---- Soviet SSh-68 Steel Helmet with Dust Goggles & Chin Strap ----
skull, _ = cast(BODY, head("head") + UP * 0.5, -UP)
rim_c = skull - UP * 0.102
probe = rim_c + UP * 0.03
reach = {}
for key, d in (("l", LEFT), ("r", -LEFT), ("f", FWD), ("b", -FWD)):
    loc, _, _, _ = BODY.ray_cast(probe + d * 0.3, -d, 0.3)
    reach[key] = (loc - probe).dot(d) if loc is not None else 0.08
hw = max(reach["l"], reach["r"]) + 0.046
hl = (reach["f"] + reach["b"]) / 2 + 0.052
hc = probe + FWD * (reach["f"] - reach["b"]) / 2
rim_c = Vector((hc.x, hc.y, rim_c.z))

# SSh-68 smooth stamped steel profile (24-sided dome for clean silhouette)
ssh_prof = [
    (1.03, -0.008),
    (0.995, 0.003),
    (0.92, 0.020),
    (0.875, 0.052),
    (0.825, 0.090),
    (0.73, 0.126),
    (0.57, 0.152),
    (0.36, 0.168),
    (0.14, 0.175),
    (0.0, 0.177),
]
segs = 24
bm = bmesh.new()
rings = []
for rr, hh in ssh_prof:
    ring_v = []
    for k in range(segs):
        ang = math.tau * k / segs
        cos_a = math.cos(ang)
        sin_a = math.sin(ang)
        z_flare = 0.010 * cos_a if hh < 0.03 else 0.004 * cos_a
        front_slope = -0.010 * max(0.0, cos_a) * (hh / 0.177)
        r_lat = rr * hw * (1.10 if hh < 0.025 else 1.05)
        r_lon = rr * hl * (1.12 if (hh < 0.025 and cos_a < 0) else 1.07)
        ring_v.append(
            bm.verts.new(
                rim_c
                + UP * (hh + z_flare)
                + LEFT * (r_lat * sin_a)
                + FWD * (r_lon * cos_a + front_slope)
            )
        )
    rings.append(ring_v)
for r0, r1 in zip(rings, rings[1:]):
    for k in range(segs):
        bm.faces.new((r0[k], r0[(k + 1) % segs], r1[(k + 1) % segs], r1[k]))
bm.faces.new(rings[-1])
helm = new_obj("Helmet", bm, "helmet_soviet")

# Subtle side rivets on SSh-68 steel shell
for sx in (-1, 1):
    for ang_deg in (28, -42):
        rad = math.radians(ang_deg)
        rv_dir = (LEFT * sx * math.cos(rad) + FWD * math.sin(rad)).normalized()
        rv_p = rim_c + UP * 0.036 + LEFT * (sx * hw * 0.94 * math.cos(rad)) + FWD * (hl * 0.96 * math.sin(rad))
        box("HelmetRivet", "steel", rv_p, surface_frame(rv_dir, UP), (0.004, 0.004, 0.003))

# Elastic goggle band wrapping SSh-68
gband_c = [rim_c + UP * 0.052, rim_c + UP * 0.075]
ring_loft(
    "HelmetGoggleStrap",
    "goggle_strap",
    gband_c,
    [hl * 1.07 * 0.875, hl * 1.07 * 0.848],
    sides=24,
    squash=hw / hl,
    up_hint=FWD,
    caps=False,
)
gband_obj = PARTS[-1]
gsol = gband_obj.modifiers.new("Solid", "SOLIDIFY")
gsol.thickness = 0.005
gsol.offset = 1.0

# Rounded oval dust goggles perched cleanly on the brow of SSh-68
g_center = rim_c + FWD * (hl * 0.945) + UP * 0.066
g_up = (UP * 0.93 - FWD * 0.36).normalized()
g_fwd = (FWD * 0.93 + UP * 0.36).normalized()
g_left = g_up.cross(g_fwd).normalized()
box("HelmetGoggleBridge", "goggle_frame", g_center, (g_left, g_up, g_fwd), (0.015, 0.009, 0.009), 0.003)
for sx in (-1, 1):
    ang_g = math.radians(-18.0 * sx)
    rot_g = Matrix.Rotation(ang_g, 3, g_up)
    gf = (rot_g @ g_fwd).normalized()
    cup_c = g_center + g_left * (sx * 0.035) - g_fwd * 0.008
    ring_loft(
        "HelmetGoggleFrame",
        "goggle_frame",
        [cup_c - gf * 0.012, cup_c + gf * 0.002, cup_c + gf * 0.014],
        [0.024, 0.026, 0.024],
        sides=12,
        up_hint=g_up,
        squash=1.22,
    )
    ring_loft(
        "HelmetGoggleLens",
        "goggle_lens",
        [cup_c + gf * 0.010, cup_c + gf * 0.016],
        [0.020, 0.019],
        sides=12,
        up_hint=g_up,
        squash=1.22,
    )

# Leather chin strap
head_c = rim_c - UP * 0.075 + FWD * 0.01
strap_pts, strap_nrm = [], []
for i in range(11):
    t = i / 10
    ang = math.pi * t
    target = head_c + LEFT * math.cos(ang) * 0.075 - UP * (0.085 * math.sin(ang)) + FWD * (0.02 + 0.04 * math.sin(ang))
    d = (target - head_c).normalized()
    loc, nn, _, _ = BODY.ray_cast(target + d * 0.2, -d, 0.3)
    if loc is not None:
        strap_pts.append(loc + nn * 0.003)
        strap_nrm.append(nn)
top_l = rim_c + LEFT * (hw * 0.87) + UP * 0.012
top_r = rim_c - LEFT * (hw * 0.87) + UP * 0.012
strap("ChinStrap", "leather", [top_l] + strap_pts + [top_r], [LEFT] + strap_nrm + [-LEFT], 0.011, 0.0025, flat=True)

# ---- Modular Weapon Geometry (AK-74 or RPG-7V) ----
P = RP
Pz = lambda dz: (lambda x, y, z=0.0: RP(x, y, z + dz))
WEAPON_PARTS = set()


def track_weapon(obj):
    key = re.sub(r"[._]\d+$", "", obj.name)
    WEAPON_PARTS.add(key)
    return obj


if ROLE == "rifleman":
    # ---- AK-74 (943 mm real length, warm lacquered wood + stamped steel + iconic orange-brown bakelite mag) ----
    # Buttstock (wood) + steel buttplate
    track_weapon(prism(
        "AkStock", "wood_ak",
        [(0.0, 0.012), (0.0, -0.102), (0.025, -0.104), (0.14, -0.070), (0.245, -0.034), (0.245, 0.010)],
        0.0175, P,
    ))
    track_weapon(prism(
        "AkButtplate", "gunmetal_dark",
        [(-0.008, 0.013), (-0.008, -0.103), (0.001, -0.104), (0.001, 0.013)],
        0.0185, P,
    ))
    # Stamped receiver + ribbed dust cover + rear sight block
    track_weapon(prism(
        "AkReceiver", "gunmetal",
        [(0.245, -0.036), (0.495, -0.036), (0.495, 0.012), (0.475, 0.026), (0.265, 0.024), (0.245, 0.012)],
        0.016, P,
    ))
    track_weapon(prism(
        "AkDustCover", "gunmetal_dark",
        [(0.255, 0.014), (0.455, 0.016), (0.450, 0.032), (0.265, 0.029)],
        0.0135, P,
    ))
    track_weapon(prism(
        "AkRearSight", "gunmetal_dark",
        [(0.452, 0.014), (0.500, 0.014), (0.498, 0.040), (0.455, 0.038)],
        0.011, P,
    ))
    # Charging handle & fire selector on right side (-r_left)
    track_weapon(box(
        "AkChargingHandle", "gunmetal_dark",
        RP(0.415, 0.008, -0.024), (axis, r_up, r_left), (0.010, 0.007, 0.013), 0.002,
    ))
    # Bakelite pistol grip + trigger guard
    track_weapon(prism(
        "AkPistolGrip", "bakelite",
        [(0.305, -0.036), (0.338, -0.036), (0.312, -0.136), (0.282, -0.132)],
        0.0135, P,
    ))
    track_weapon(prism(
        "AkTriggerGuard", "gunmetal_dark",
        [(0.338, -0.036), (0.340, -0.064), (0.398, -0.064), (0.400, -0.036), (0.392, -0.036),
         (0.391, -0.056), (0.347, -0.056), (0.346, -0.036)],
        0.004, P,
    ))
    # Iconic curved 30-round orange-brown bakelite magazine
    track_weapon(prism(
        "AkMagazine", "bakelite_mag",
        [(0.395, -0.035), (0.458, -0.035), (0.472, -0.105), (0.500, -0.175), (0.530, -0.228),
         (0.472, -0.246), (0.440, -0.185), (0.412, -0.112)],
        0.0125, P,
    ))
    track_weapon(prism(
        "AkMagFloor", "gunmetal_dark",
        [(0.470, -0.244), (0.533, -0.226), (0.537, -0.236), (0.474, -0.254)],
        0.0132, P,
    ))
    # Wooden lower handguard + upper gas-tube wood cover
    track_weapon(prism(
        "AkLowerHandguard", "wood_ak",
        [(0.495, -0.034), (0.665, -0.028), (0.665, 0.004), (0.495, 0.006)],
        0.0175, P,
    ))
    track_weapon(ring_loft(
        "AkUpperHandguard", "wood_ak",
        [P(0.502, 0.020), P(0.655, 0.020)],
        [0.0155, 0.0145],
        sides=8,
        up_hint=r_left,
    ))
    # Gas tube + 90-degree AK-74 gas block
    track_weapon(ring_loft(
        "AkGasTube", "gunmetal",
        [P(0.655, 0.021), P(0.720, 0.021)],
        [0.010, 0.010],
        sides=8,
        up_hint=r_left,
    ))
    track_weapon(prism(
        "AkGasBlock", "gunmetal_dark",
        [(0.706, -0.010), (0.732, -0.010), (0.730, 0.034), (0.708, 0.034)],
        0.011, P,
    ))
    # Barrel + cleaning rod underneath
    track_weapon(ring_loft("AkBarrel", "gunmetal_dark", [P(0.665, -0.002), P(0.860, -0.002)], [0.0095, 0.0088], sides=8, up_hint=r_left))
    track_weapon(ring_loft("AkCleanRod", "gunmetal", [P(0.665, -0.016), P(0.840, -0.016)], [0.0032, 0.0032], sides=6, up_hint=r_left))
    # Front sight tower with protective hood
    track_weapon(prism(
        "AkFrontSight", "gunmetal_dark",
        [(0.832, -0.014), (0.858, -0.014), (0.854, 0.044), (0.838, 0.044)],
        0.0095, P,
    ))
    # Signature long two-chamber AK-74 muzzle brake
    track_weapon(ring_loft(
        "AkMuzzleBrake", "gunmetal_dark",
        [P(0.860, -0.002), P(0.875, -0.002), P(0.943, -0.002)],
        [0.011, 0.0145, 0.0145],
        sides=8,
        up_hint=r_left,
    ))
    # Canvas rifle sling
    sa, sb = P(0.665, -0.032), P(0.04, -0.095)
    sl = [sa.lerp(sb, t) + Vector((0, 0, -0.14 * 4 * t * (1 - t))) for t in [i / 10 for i in range(11)]]
    track_weapon(strap("AkSling", "webbing_dark", sl, [r_left] * len(sl), 0.025, 0.003))
else:
    # ---- RPG-7V Launcher (950 mm tube) + PG-7V Anti-Tank Warhead (protruding to 1.23 m) ----
    # Rear venturi exhaust cone
    track_weapon(ring_loft(
        "RpgVenturi", "gunmetal_dark",
        [P(0.00, 0.0), P(0.03, 0.0), P(0.14, 0.0)],
        [0.048, 0.045, 0.024],
        sides=10,
        up_hint=r_left,
    ))
    # Rear steel tube
    track_weapon(ring_loft("RpgRearTube", "gunmetal", [P(0.14, 0.0), P(0.28, 0.0)], [0.024, 0.024], sides=10, up_hint=r_left))
    # Warm wooden heat shield wrapping mid-section (instant RPG-7 trademark!)
    track_weapon(ring_loft(
        "RpgWoodShield", "wood_ak",
        [P(0.27, 0.0), P(0.30, 0.0), P(0.54, 0.0), P(0.57, 0.0)],
        [0.026, 0.033, 0.033, 0.028],
        sides=10,
        up_hint=r_left,
    ))
    for x_cl in (0.29, 0.55):
        track_weapon(ring_loft("RpgClamp", "gunmetal_dark", [P(x_cl - 0.008, 0.0), P(x_cl + 0.008, 0.0)],
                               [0.035, 0.035], sides=10, up_hint=r_left))
    # Bulged central firing chamber & forward tube
    track_weapon(ring_loft(
        "RpgChamber", "gunmetal",
        [P(0.57, 0.0), P(0.61, 0.0), P(0.68, 0.0), P(0.72, 0.0)],
        [0.028, 0.034, 0.033, 0.024],
        sides=10,
        up_hint=r_left,
    ))
    track_weapon(ring_loft(
        "RpgFrontTube", "gunmetal",
        [P(0.72, 0.0), P(0.93, 0.0), P(0.95, 0.0)],
        [0.024, 0.024, 0.027],
        sides=10,
        up_hint=r_left,
    ))
    # Front trigger grip (right hand) + trigger guard + rear support grip (left hand)
    track_weapon(prism(
        "RpgFrontGrip", "bakelite",
        [(0.710, -0.022), (0.748, -0.022), (0.725, -0.142), (0.692, -0.138)],
        0.015, P,
    ))
    track_weapon(prism(
        "RpgTriggerGuard", "gunmetal_dark",
        [(0.746, -0.024), (0.748, -0.062), (0.795, -0.062), (0.795, -0.024),
         (0.788, -0.024), (0.788, -0.054), (0.754, -0.054), (0.754, -0.024)],
        0.005, P,
    ))
    track_weapon(prism(
        "RpgRearGrip", "bakelite",
        [(0.555, -0.026), (0.588, -0.026), (0.570, -0.132), (0.540, -0.128)],
        0.014, P,
    ))
    # PGO-7 optical sight mounted on left side of launcher (+r_left)
    track_weapon(box("RpgSightBracket", "gunmetal_dark", RP(0.64, 0.028, 0.035), (axis, r_up, r_left), (0.030, 0.024, 0.012), 0.003))
    track_weapon(ring_loft(
        "RpgPgo7Scope", "gunmetal_dark",
        [RP(0.56, 0.048, 0.050), RP(0.60, 0.048, 0.050), RP(0.70, 0.048, 0.050)],
        [0.016, 0.018, 0.022],
        sides=8,
        up_hint=r_left,
    ))
    # Iron front & rear folding sights
    track_weapon(prism("RpgFrontSight", "gunmetal_dark", [(0.885, 0.022), (0.905, 0.022), (0.900, 0.072), (0.890, 0.072)], 0.005, P))
    # Loaded PG-7V anti-tank rocket warhead protruding from muzzle!
    track_weapon(ring_loft(
        "RpgWarhead", "pg7_olive",
        [P(0.95, 0.0), P(0.99, 0.0), P(1.04, 0.0), P(1.10, 0.0), P(1.18, 0.0)],
        [0.022, 0.026, 0.046, 0.045, 0.013],
        sides=10,
        up_hint=r_left,
    ))
    track_weapon(ring_loft("RpgWarheadBand", "pg7_band", [P(1.06, 0.0), P(1.08, 0.0)], [0.047, 0.047], sides=10, up_hint=r_left))
    track_weapon(ring_loft("RpgFuze", "steel", [P(1.18, 0.0), P(1.23, 0.0)], [0.011, 0.007], sides=8, up_hint=r_left))

# ---- Bake, decimate, Clean Tactical Diorama shading (smooth + crisp sharp edges + 5 PBR materials) ----
bpy.data.objects["Human.eyebrow001"]["scheme"] = "brow"
bpy.data.objects["Human.low-poly"]["scheme"] = "eye"
human["scheme"] = "skin"
suit["scheme"] = "afghanka"
# Slightly higher clothing/skin budget so smooth shading drapes cleanly without jagged diagonal creases
BUDGET = {"skin": 0.11, "afghanka": 0.105, "brow": 0.45, "eye": 1.0}
CARRIER_DECIMATE_NAMES = ("ArmorFrontCarrier", "ArmorBackCarrier")


def lin(h):
    c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    return [x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4 for x in c]


SOLID = {
    "skin": lin("c7936e"),
    "brow": lin("3b2a1e"),
    "eye": lin("26201c"),
    "undershirt": lin("5b6a78"),      # muted blue-grey telnyashka/undershirt collar peek
    "afghanka": lin("b39c72"),        # clean warm sand-khaki Soviet Afghanka field uniform
    "afghanka_dark": lin("978159"),   # collar, flaps, cuffs, shadowed trim
    "vest_od": lin("515b3d"),         # 6B3 olive drab armor carrier
    "vest_dark": lin("3d452d"),       # 6B3 neck pad, plate ribs, side straps
    "webbing_khaki": lin("8f865e"),   # Soviet canvas Lifchik / RD-54 / RPG rocket pack
    "webbing_dark": lin("6c6545"),    # pouch flaps, pack straps, sling
    "bedroll_od": lin("495138"),      # shelter-half roll
    "helmet_soviet": lin("435234"),   # SSh-68 painted Soviet olive steel
    "goggle_strap": lin("272923"),
    "goggle_frame": lin("222420"),
    "goggle_lens": lin("253642"),     # deep smoked dust goggle glass
    "leather": lin("4b311e"),         # Soviet leather belt & chinstrap
    "brass": lin("b89642"),           # Soviet brass belt buckle
    "brass_dark": lin("7a6128"),      # recessed buckle center
    "steel": lin("747875"),
    "boot": lin("1f1d1a"),            # matte-satin worn combat leather (not glossy rubber!)
    "boot_strap": lin("282420"),
    "boot_lace": lin("35302a"),
    "sole": lin("131211"),
    "gunmetal": lin("2c2e32"),        # blued Soviet stamped steel
    "gunmetal_dark": lin("1c1d20"),
    "wood_ak": lin("7a4421"),         # warm lacquered Soviet laminate wood
    "bakelite": lin("662c18"),        # dark maroon bakelite grip
    "bakelite_mag": lin("a64b22"),    # authentic deep orange-brown AK-74 bakelite magazine
    "pg7_olive": lin("495a32"),       # PG-7V warhead olive
    "pg7_band": lin("23281c"),
}


def make_pbr_mat(name, roughness, metallic=0.0, specular=0.45):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    attr = nt.nodes.new("ShaderNodeVertexColor")
    attr.layer_name = "Col"
    nt.links.new(attr.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = metallic
    if "Specular IOR Level" in bsdf.inputs:
        bsdf.inputs["Specular IOR Level"].default_value = specular
    return mat


mat_cloth = make_pbr_mat("DioramaCloth", roughness=0.91, metallic=0.0, specular=0.22)
mat_webbing = make_pbr_mat("DioramaWebbing", roughness=0.80, metallic=0.0, specular=0.32)
mat_painted_steel = make_pbr_mat("DioramaPaintedSteel", roughness=0.44, metallic=0.22, specular=0.55)
mat_gunmetal = make_pbr_mat("DioramaGunmetal", roughness=0.34, metallic=0.68, specular=0.62)
mat_leather = make_pbr_mat("DioramaLeather", roughness=0.56, metallic=0.0, specular=0.36)
mat_satin = make_pbr_mat("DioramaSatin", roughness=0.38, metallic=0.05, specular=0.55)

MAT_BY_SCHEME = {
    "afghanka": mat_cloth,
    "afghanka_dark": mat_cloth,
    "skin": mat_cloth,
    "brow": mat_cloth,
    "undershirt": mat_cloth,
    "vest_od": mat_webbing,
    "vest_dark": mat_webbing,
    "webbing_khaki": mat_webbing,
    "webbing_dark": mat_webbing,
    "bedroll_od": mat_webbing,
    "goggle_strap": mat_webbing,
    "goggle_frame": mat_webbing,
    "sole": mat_webbing,
    "boot_lace": mat_webbing,
    "helmet_soviet": mat_painted_steel,
    "pg7_olive": mat_painted_steel,
    "pg7_band": mat_painted_steel,
    "gunmetal": mat_gunmetal,
    "gunmetal_dark": mat_gunmetal,
    "steel": mat_gunmetal,
    "brass": mat_gunmetal,
    "brass_dark": mat_gunmetal,
    "leather": mat_leather,
    "boot": mat_leather,
    "boot_strap": mat_leather,
    "wood_ak": mat_satin,
    "bakelite": mat_satin,
    "bakelite_mag": mat_satin,
    "goggle_lens": mat_satin,
    "eye": mat_satin,
}

PART_TRIS = {}
rot = Matrix.Rotation(math.pi, 4, "Z")
NECK_Z = head("neck_01").z
NECK_Y = (rot @ head("neck_01")).y
EYE_C = rot @ (head("head") + (tail("head") - head("head")) * 0.45)
F_FWD, F_UP, F_LEFT = Vector((0, 1, 0)), Vector((0, 0, 1)), Vector((-1, 0, 0))


def mixc(a, b, t):
    return [a[i] + (b[i] - a[i]) * t for i in range(3)]


def face_tone(c):
    rel = c - EYE_C
    f, u, sd = rel.dot(F_FWD), rel.dot(F_UP), abs(rel.dot(F_LEFT))
    tone = SOLID["skin"]
    if f > 0.02 and -0.115 < u < -0.045:
        tone = mixc(tone, lin("877163"), 0.42)
    if f > 0.06 and -0.083 < u < -0.064 and sd < 0.024:
        tone = mixc(SOLID["skin"], lin("b5675a"), 0.48)
    elif f > 0.035 and -0.045 < u < -0.01 and 0.025 < sd < 0.06:
        tone = mixc(tone, lin("cf8a72"), 0.30)
    if sd > 0.065 and f < 0.03 and -0.06 < u < 0.02:
        tone = mixc(tone, lin("c97c64"), 0.35)
    return tone


def afghanka_vertex_tone(p):
    # Subtle smooth low-frequency cloth modulation + dusty lower-leg weathering (zero noisy triangle mosaic)
    q = Vector(p) * 3.8
    n_val = 0.5 + 0.5 * noise.noise(q)
    base = mixc(SOLID["afghanka_dark"], lin("beaa82"), 0.38 + 0.36 * n_val)
    # Gentle ground dust warming/lightening near boot tops (z < 0.55 m)
    if p.z < 0.55:
        dust = max(0.0, (0.55 - p.z) / 0.55) * 0.22
        base = mixc(base, lin("a6977a"), dust)
    return base


finals = []
painted = [o for o in list(bpy.data.objects) if o.type == "MESH" and "scheme" in o]
dg = bpy.context.evaluated_depsgraph_get()

for o in painted:
    me = bpy.data.meshes.new_from_object(o.evaluated_get(dg))
    me.transform(o.matrix_world)
    me.transform(rot)
    obj = bpy.data.objects.new(o.name + "_lp", me)
    bpy.context.scene.collection.objects.link(obj)
    scheme = o["scheme"]
    ratio = BUDGET.get(scheme, 1.0)
    if ratio < 1.0:
        d = obj.modifiers.new("Decimate", "DECIMATE")
        d.ratio = ratio
        if scheme == "skin":
            keep = obj.vertex_groups.new(name="Keep")
            neck_z = (rot @ head("neck_01")).z
            hands = [rot @ head("hand_l"), rot @ head("hand_r")]
            keep.add([v.index for v in me.vertices if v.co.z > neck_z - 0.02], 1.0, "REPLACE")
            keep.add([v.index for v in me.vertices if min((v.co - h).length for h in hands) < 0.14], 1.0, "REPLACE")
            d.vertex_group = "Keep"
            d.invert_vertex_group = False
            d.vertex_group_factor = 1.6
        dg2 = bpy.context.evaluated_depsgraph_get()
        m2 = bpy.data.meshes.new_from_object(obj.evaluated_get(dg2))
        obj.modifiers.clear()
        obj.data = m2

    # Smooth shading + crisp angle-based Edge Split (silky cloth & steel dome, razor-sharp hardware edges)
    me = obj.data
    for poly in me.polygons:
        poly.use_smooth = True
    split_deg = 58.0 if scheme in ("skin", "afghanka") else (42.0 if scheme in ("helmet_soviet", "boot") else 36.0)
    es = obj.modifiers.new("EdgeSplit", "EDGE_SPLIT")
    es.use_edge_angle = True
    es.split_angle = math.radians(split_deg)
    es.use_edge_sharp = True
    dg3 = bpy.context.evaluated_depsgraph_get()
    m3 = bpy.data.meshes.new_from_object(obj.evaluated_get(dg3))
    obj.modifiers.clear()
    obj.data = m3

    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method="BEAUTY")
    bm.to_mesh(me)
    bm.free()
    for poly in me.polygons:
        poly.use_smooth = True

    me.materials.clear()
    me.materials.append(MAT_BY_SCHEME.get(scheme, mat_cloth))
    col = me.color_attributes.new("Col", "FLOAT_COLOR", "CORNER")
    # Paint smoothly per vertex (zero random triangle noise)
    v_colors = []
    for v in me.vertices:
        c_pos = v.co
        if scheme == "afghanka":
            base = afghanka_vertex_tone(c_pos)
        elif scheme == "skin" and c_pos.z > NECK_Z:
            base = face_tone(c_pos)
        elif scheme == "boot" and c_pos.z < 0.14:
            # Subtle field dust on lower boots
            base = mixc(SOLID["boot"], lin("3d362c"), max(0.0, (0.14 - c_pos.z) / 0.14) * 0.35)
        else:
            base = SOLID[scheme]
        v_colors.append((base[0], base[1], base[2], 1.0))
    for poly in me.polygons:
        for li in poly.loop_indices:
            vi = me.loops[li].vertex_index
            col.data[li].color = v_colors[vi]
    finals.append(obj)
    key = re.sub(r"[._]\d+|_lp$|\d+$", "", o.name.replace("_lp", ""))
    PART_TRIS[key] = PART_TRIS.get(key, 0) + sum(len(p.vertices) - 2 for p in obj.data.polygons)

# ---- Skinning weights ----
BOOT_PARTS = {"BootShaft", "BootCollar", "BootFoot", "BootSole", "BootHeel", "BootStrap", "BootBuckle", "BootToeCap", "BootLace", "BootTread"}
BONES = {b.name for b in rig.data.bones}
ANKLE_Z = head("foot_l").z
suit_lp = next(o for o in finals if o.name.startswith("Human.male_casualsuit01"))
src_tree = BVHTree.FromPolygons([v.co.copy() for v in suit_lp.data.vertices], [list(p.vertices) for p in suit_lp.data.polygons])
src_names = {g.index: g.name for g in suit_lp.vertex_groups}


def put(obj, idx, name, w):
    vg = obj.vertex_groups.get(name) or obj.vertex_groups.new(name=name)
    vg.add([idx], w, "REPLACE")


for obj in finals:
    if obj.name.startswith("Human"):
        continue
    key = re.sub(r"[._]\d+$", "", obj.name[:-3])
    for v in obj.data.vertices:
        m = rot.inverted() @ v.co
        sd = "l" if m.x > 0 else "r"
        if key.startswith(("Helmet", "ChinStrap")):
            put(obj, v.index, "head", 1.0)
        elif key in WEAPON_PARTS:
            put(obj, v.index, "hand_r", 1.0)
        elif key in BOOT_PARTS:
            put(obj, v.index, ("foot_" if m.z < ANKLE_Z + 0.03 else "calf_") + sd, 1.0)
        elif key == "Blouse":
            put(obj, v.index, "calf_" + sd, 1.0)
        elif key == "SleeveCuff":
            put(obj, v.index, "lowerarm_" + sd, 1.0)
        elif key.startswith("ArmPocket"):
            put(obj, v.index, "upperarm_" + sd, 1.0)
        elif key.startswith(("Rd54", "ShelterRoll", "RollStrap", "RpgPack", "SparePg7")):
            put(obj, v.index, "spine_02", 0.5)
            put(obj, v.index, "spine_03", 0.5)
        elif key in ("ArmorCollar", "AfghankaCollar", "AfghankaLapel", "UndershirtPanel"):
            put(obj, v.index, "spine_03", 0.78)
            put(obj, v.index, "neck_01", 0.22)
        elif key == "KneePatch":
            put(obj, v.index, "thigh_" + sd, 0.55)
            put(obj, v.index, "calf_" + sd, 0.45)
        elif key.startswith(("Armor", "Lifchik", "Grenade")):
            if m.z > chest.z - 0.04:
                put(obj, v.index, "spine_03", 0.8)
                put(obj, v.index, "spine_02", 0.2)
            else:
                put(obj, v.index, "spine_02", 0.65)
                put(obj, v.index, "spine_01", 0.35)
        elif key.startswith(("CombatBelt", "BeltBuckle", "Canteen", "Bayonet", "Shovel", "Utility")):
            put(obj, v.index, "spine_01", 0.55)
            put(obj, v.index, "pelvis", 0.45)
        else:
            _, _, fi, _ = src_tree.find_nearest(v.co)
            acc = {}
            for vi in suit_lp.data.polygons[fi].vertices:
                for g in suit_lp.data.vertices[vi].groups:
                    name = src_names.get(g.group)
                    if name in BONES:
                        acc[name] = acc.get(name, 0.0) + g.weight
            top = sorted(acc.items(), key=lambda kv: -kv[1])[:4]
            total = sum(w for _, w in top) or 1.0
            for name, w in top:
                put(obj, v.index, name, w / total)

for o in list(bpy.data.objects):
    if o not in finals and o is not rig:
        bpy.data.objects.remove(o)

bpy.ops.object.select_all(action="DESELECT")
for o in finals:
    o.select_set(True)
bpy.context.view_layer.objects.active = finals[0]
bpy.ops.object.join()
soldier = bpy.context.view_layer.objects.active
soldier.name = soldier.data.name = MESH_NAME
soldier.data.validate(clean_customdata=False)
finals = [soldier]

# Bake smooth per-vertex contact AO + soft top-down diorama key gradient into vertex colors
_tree = BVHTree.FromObject(soldier, bpy.context.evaluated_depsgraph_get())
_dirs = []
for i in range(32):
    a = math.tau * ((i * 0.618034) % 1.0)
    r2 = (i + 0.5) / 32
    _dirs.append(Vector((math.cos(a) * math.sqrt(r2), math.sin(a) * math.sqrt(r2), math.sqrt(1 - r2))))
_col = soldier.data.color_attributes["Col"]
_v_shade = {}
for v in soldier.data.vertices:
    n = v.normal.normalized() if v.normal.length > 1e-6 else Vector((0, 0, 1))
    t = n.orthogonal().normalized()
    b = n.cross(t)
    origin = v.co + n * 0.004
    occ = 0.0
    for d in _dirs:
        w = t * d.x + b * d.y + n * d.z
        hit = _tree.ray_cast(origin, w, 0.11)
        if hit[0] is not None:
            occ += 1.0 - hit[3] / 0.11
    occ /= len(_dirs)
    # Soft diorama top-light + gentle vertical focus towards chest/head
    height_lift = 0.94 + 0.09 * min(1.0, max(0.0, v.co.z / 1.75))
    _v_shade[v.index] = (1.0 - 0.68 * occ) * (0.94 + 0.09 * max(0.0, n.z)) * height_lift

for poly in soldier.data.polygons:
    for li in poly.loop_indices:
        vi = soldier.data.loops[li].vertex_index
        shade = _v_shade[vi]
        c = _col.data[li].color
        _col.data[li].color = (min(c[0] * shade, 1.0), min(c[1] * shade, 1.0), min(c[2] * shade, 1.0), 1.0)

# ---- Bind: held pose becomes rest pose, turn to +Y forward, attach skin ----
for g in list(soldier.vertex_groups):
    if g.name not in BONES:
        soldier.vertex_groups.remove(g)
bpy.ops.object.select_all(action="DESELECT")
rig.select_set(True)
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode="POSE")
bpy.ops.pose.select_all(action="SELECT")
bpy.ops.pose.armature_apply(selected=False)
bpy.ops.object.mode_set(mode="OBJECT")
rig.rotation_euler = (0.0, 0.0, math.pi)
bpy.ops.object.transform_apply(location=False, rotation=True, scale=False)
rig.name = rig.data.name = RIG_NAME
soldier.parent = rig
arm_mod = soldier.modifiers.new("Armature", "ARMATURE")
arm_mod.object = rig

weapon_bind = rot @ Matrix((
    (axis.x, r_up.x, r_left.x, butt.x),
    (axis.y, r_up.y, r_left.y, butt.y),
    (axis.z, r_up.z, r_left.z, butt.z),
    (0.0, 0.0, 0.0, 1.0),
))
rig["weapon_bind"] = [weapon_bind[i][j] for i in range(4) for j in range(4)]
rig["role"] = ROLE
rig["mesh_name"] = MESH_NAME

tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in finals)
print("ROLE", ROLE, "LOWPOLY_TRIS", tris)
for k_, v_ in sorted(PART_TRIS.items(), key=lambda kv: -kv[1])[:25]:
    print("PART", k_, v_)
bpy.ops.wm.save_as_mainfile(filepath=OUT)
if OUT_GLB:
    bpy.ops.export_scene.gltf(filepath=OUT_GLB, export_format="GLB", export_skins=True, export_animations=False)
