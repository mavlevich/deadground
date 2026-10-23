"""US Army infantryman, Central Europe about 1980, as a low-poly faceted game character.

Proportions come from an MPFB (MakeHuman, CC0) body dressed and posed in Blender; the kit, M1 helmet,
M16A1 and boots are built here. Everything is then baked, decimated to a game budget, flat shaded
and colored per face (woodland camo blobs, olive drab nylon, black leather and steel) with a small
tone jitter per facet. No textures: colors live in a face corner color attribute, which glTF exports.

Frame while building: MPFB humans face -Y with their left at +X. Finished meshes are turned to face +Y.
Run: blender --background --python build_us_rifleman.py -- out.blend [out.glb]
"""

import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector, noise
from mathutils.bvhtree import BVHTree

args = sys.argv[sys.argv.index("--") + 1:]
OUT = args[0]
OUT_GLB = args[1] if len(args) > 1 else None
for o in list(bpy.data.objects):
    bpy.data.objects.remove(o)

from bl_ext.blender_org.mpfb.services.humanservice import HumanService  # noqa: E402
from bl_ext.blender_org.mpfb.services.locationservice import LocationService  # noqa: E402

DATA = LocationService.get_user_data()
macro = {"gender": 1.0, "age": 0.5, "muscle": 0.8, "weight": 0.6, "proportions": 0.62, "height": 0.5,
         "cupsize": 0.5, "firmness": 0.5, "race": {"asian": 0.0, "caucasian": 1.0, "african": 0.0}}
human = HumanService.create_human(macro_detail_dict=macro)
rig = HumanService.add_builtin_rig(human, "game_engine")
for kind, rel in (("Eyes", "eyes/low-poly/low-poly.mhclo"), ("Eyebrows", "eyebrows/eyebrow001/eyebrow001.mhclo"),
                  ("Clothes", "clothes/male_casualsuit01/male_casualsuit01.mhclo")):
    HumanService.add_mhclo_asset(os.path.join(DATA, rel), human, asset_type=kind, subdiv_levels=0)
suit = bpy.data.objects["Human.male_casualsuit01"]
pb = rig.pose.bones
REST = {b.name: (b.matrix_local.copy(), b.head_local.copy(), b.tail_local.copy()) for b in rig.data.bones}


def update():
    bpy.context.view_layer.update()


def seg_t(p, a, b):
    d = b - a
    return max(-1.0, min(2.0, (p - a).dot(d) / d.length_squared)), d


# ---- Rest pose edits: rolled sleeves, bloused trousers, bare forearms ----
def near_arm(p, s_, reach=0.085):
    for bone in ("upperarm_", "lowerarm_", "hand_"):
        a, b = REST[bone + s_][1], REST[bone + s_][2]
        t, d = seg_t(p, a, b)
        if (p - (a + d * min(max(t, 0), 1))).length < reach:
            return True
    return False


# Sleeves rolled above the elbow: the sleeve is bisected by a plane across the upper arm, 5 cm
# above the elbow, so the cut edge is a clean ring for the roll to sit on.
CUT = {}
for s_ in ("l", "r"):
    sh, el = REST["upperarm_" + s_][1], REST["upperarm_" + s_][2]
    ua = (el - sh).normalized()
    CUT[s_] = (el - ua * 0.05, ua)
    bm = bmesh.new()
    bm.from_mesh(suit.data)
    region = [f for f in bm.faces if near_arm(f.calc_center_median(), s_)]
    geom = list({e for f in region for e in f.edges}) + list({v for f in region for v in f.verts}) + region
    q, n = CUT[s_]
    bmesh.ops.bisect_plane(bm, geom=geom, plane_co=q, plane_no=n, clear_outer=True)
    bm.to_mesh(suit.data)
    bm.free()
mask = human.vertex_groups.get("Delete.male_casualsuit01")
if mask is not None:
    # The skin below the cut must show.
    bare = [v.index for v in human.data.vertices for s_ in ("l", "r")
            if near_arm(v.co, s_, 0.1) and (v.co - CUT[s_][0]).dot(CUT[s_][1]) > -0.02]
    mask.remove(bare)

# Trousers tucked and bloused: the cloth below the boot top goes (nothing can poke out of the
# boot), the last few centimetres above it taper onto the calf.
bm = bmesh.new()
bm.from_mesh(suit.data)
bmesh.ops.delete(bm, geom=[f for f in bm.faces if all(v.co.z < 0.3 for v in f.verts)], context="FACES")
bm.to_mesh(suit.data)
bm.free()
slices = {}
for v in suit.data.vertices:
    if v.co.z < 0.36:
        slices.setdefault(("l" if v.co.x > 0 else "r", int(v.co.z * 100)), []).append(v)
for key, verts in slices.items():
    cx = sum(v.co.x for v in verts) / len(verts)
    cy = sum(v.co.y for v in verts) / len(verts)
    far = max(math.hypot(v.co.x - cx, v.co.y - cy) for v in verts)
    k = min(1.0, 0.058 / max(far, 1e-6))
    for v in verts:
        v.co.x = cx + (v.co.x - cx) * k
        v.co.y = cy + (v.co.y - cy) * k
# Bare feet inside the boots are not needed.
bm = bmesh.new()
bm.from_mesh(human.data)
# Feet and calves up to the blousing are inside the boots: the calf bulge would poke through the shaft.
bmesh.ops.delete(bm, geom=[f for f in bm.faces if all(v.co.z < 0.32 for v in f.verts)], context="FACES_ONLY")
bm.to_mesh(human.data)
bm.free()


# ---- Pose ----
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


# Standing, feet a little apart, head level.
for s, ankle in (("l", Vector((0.13, -0.01, 0.075))), ("r", Vector((-0.14, 0.02, 0.075)))):
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

# M16A1 at the low ready: butt by the right hip, muzzle down and across to the left.
UPW = Vector((0, 0, 1))
axis = Vector((0.72, -0.34, -0.6)).normalized()
r_up = (UPW - axis * axis.dot(UPW)).normalized()
r_left = r_up.cross(axis).normalized()  # the rifle's left side
butt = Vector((-0.24, -0.1, 1.2))


def frame_point(origin, X, Y, Z):
    return lambda x, y, z=0.0: origin + X * x + Y * y + Z * z


def hold(butt):
    RP = frame_point(butt, axis, r_up, r_left)
    h = dict(RP=RP, grip_top=RP(0.345, -0.04), grip_bottom=RP(0.315, -0.141), guard=RP(0.64, -0.004))
    gax = (h["grip_bottom"] - h["grip_top"]).normalized()
    gfront = (axis - gax * axis.dot(gax)).normalized()
    gc = h["grip_top"].lerp(h["grip_bottom"], 0.4)
    h["r_dir"] = (gfront * 0.75 + r_left * 0.45 - gax * 0.1).normalized()
    h["r_wrist"] = gc - h["r_dir"] * 0.075 - r_left * 0.03
    h["l_dir"] = (-r_left * 0.95 + r_up * 0.15).normalized()
    h["l_wrist"] = h["guard"] - r_up * 0.055 + r_left * 0.05
    return h


# Torso and legs (arms excluded, they still hang in rest) for the rifle clearance check.
def torso_tree():
    dgx = bpy.context.evaluated_depsgraph_get()
    arm_bones = [(head(b), tail(b)) for s_ in ("l", "r") for b in ("upperarm_" + s_, "lowerarm_" + s_, "hand_" + s_)]
    verts, polys = [], []
    for o in (human, suit):
        me = o.evaluated_get(dgx).to_mesh()
        base = len(verts)
        verts.extend(o.matrix_world @ v.co for v in me.vertices)
        for p in me.polygons:
            c = o.matrix_world @ p.center
            near_arm = False
            for a, b in arm_bones:
                d = b - a
                t = max(0.0, min(1.0, (c - a).dot(d) / d.length_squared))
                if (c - (a + d * t)).length < 0.075:
                    near_arm = True
                    break
            if not near_arm:
                polys.append([base + i for i in p.vertices])
        o.evaluated_get(dgx).to_mesh_clear()
    return BVHTree.FromPolygons(verts, polys)


TORSO = torso_tree()
WAIST_Z = head("pelvis").lerp(head("spine_01"), 0.75).z


def rifle_points(RP):
    pts = [RP(x, y, z) for x in [i * 0.05 for i in range(20)] for y in (0.0, -0.03) for z in (-0.016, 0.016)]
    pts += [RP(0.02, -0.1), RP(0.12, -0.075), RP(0.32, -0.14), RP(0.4, -0.17), RP(0.43, -0.17)]
    return pts


def clearance_violation(RP):
    """How far the worst rifle point is inside the wanted clearance (0 when clear)."""
    worst = 0.0
    for p in rifle_points(RP):
        loc, nrm, _, dist = TORSO.find_nearest(p)
        if loc is None:
            continue
        # Belt kit sticks out about 7 cm at the front of the waist; elsewhere keep 3 cm of air.
        want = 0.075 if abs(p.z - WAIST_Z) < 0.11 and p.y < 0.05 else 0.03
        signed = dist if (p - loc).dot(nrm) > 0 else -dist
        worst = max(worst, want - signed)
    return worst


FWD = Vector((0, -1, 0))  # MPFB humans face -Y
reach_l = (blen("upperarm_l") + blen("lowerarm_l")) * 0.94
for _ in range(160):
    hd = hold(butt)
    if clearance_violation(hd["RP"]) > 0.0:
        butt = butt + FWD * 0.006  # out from the body
        continue
    gap = hd["l_wrist"] - head("upperarm_l")
    if gap.length <= reach_l:
        break
    move = -gap.normalized()
    if move.dot(FWD) < 0:  # bring it in reach without pulling it back into the body
        move = move - FWD * move.dot(FWD)
    butt = butt + move.normalized() * 0.006
print("RIFLE_CLEARANCE", round(clearance_violation(hd["RP"]), 4), "LEFT_GAP", round((hd["l_wrist"] - head("upperarm_l")).length - reach_l, 4))
RP = hd["RP"]
for s, wrist, hand_dir, palm, pole in (("r", hd["r_wrist"], hd["r_dir"], r_left, Vector((-1.0, 0.6, -0.2))),
                                       ("l", hd["l_wrist"], hd["l_dir"], r_up, Vector((1.0, 0.3, -0.6)))):
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


def cast(tree, origin, direction, out=0.0):
    loc, nrm, _, _ = tree.ray_cast(Vector(origin), Vector(direction).normalized(), 2.0)
    if loc is None:
        loc, nrm, _, _ = tree.find_nearest(Vector(origin) + Vector(direction).normalized() * 0.6)
    return loc + nrm * out, nrm


# ---- Low-poly builders; every part records its paint scheme ----
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
    bmesh.ops.transform(bm, verts=bm.verts, matrix=Matrix.Translation(Vector(c)) @ Matrix((
        (ax[0].x * half[0], ax[1].x * half[1], ax[2].x * half[2], 0),
        (ax[0].y * half[0], ax[1].y * half[1], ax[2].y * half[2], 0),
        (ax[0].z * half[0], ax[1].z * half[1], ax[2].z * half[2], 0), (0, 0, 0, 1))))
    if chamfer > 0 and min(half) >= 0.02:
        bmesh.ops.bevel(bm, geom=bm.edges[:], offset=min(chamfer, min(half) * 0.45), segments=1, affect="EDGES")
    return new_obj(name, bm, scheme)


def prism(name, scheme, pts, half_t, P):
    """Side profile (x, y) extruded through +-half_t along the frame's third axis."""
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
        rings.append([bm.verts.new(Vector(c) + (u * math.cos(rot + math.tau * k / sides) * squash +
                                                 v * math.sin(rot + math.tau * k / sides)) * radii[i] *
                                   (1.0 + wobble * math.sin(k * 2.7 + i * 1.9) * (1 if k % 2 else -0.6)))
                      for k in range(sides)])
    for r0, r1 in zip(rings, rings[1:]):
        for k in range(sides):
            bm.faces.new((r0[k], r0[(k + 1) % sides], r1[(k + 1) % sides], r1[k]))
    if caps:
        bm.faces.new(list(reversed(rings[0])))
        bm.faces.new(rings[-1])
    return new_obj(name, bm, scheme)


def strap(name, scheme, pts, nrms, width, thick, flat=False):
    if flat:
        # Lying on a surface: one strip of faces, lifted off it.
        bm = bmesh.new()
        rows = []
        for i, (p, n) in enumerate(zip(pts, nrms)):
            d = (Vector(pts[min(i + 1, len(pts) - 1)]) - Vector(pts[max(i - 1, 0)])).normalized()
            w = d.cross(Vector(n)).normalized() * width / 2
            o = Vector(n).normalized() * thick
            rows.append([bm.verts.new(Vector(p) - w + o), bm.verts.new(Vector(p) + w + o)])
        for a, b in zip(rows, rows[1:]):
            bm.faces.new((a[0], a[1], b[1], b[0]))
        return new_obj(name, bm, scheme)
    bm = bmesh.new()
    rows = []
    for i, (p, n) in enumerate(zip(pts, nrms)):
        d = (Vector(pts[min(i + 1, len(pts) - 1)]) - Vector(pts[max(i - 1, 0)])).normalized()
        w = d.cross(Vector(n)).normalized() * width / 2
        o = Vector(n).normalized() * thick
        rows.append([bm.verts.new(Vector(p) - w), bm.verts.new(Vector(p) + w),
                     bm.verts.new(Vector(p) + w + o), bm.verts.new(Vector(p) - w + o)])
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
    """Points of the suit's open edges (a cut sleeve, a trouser bottom) near a point."""
    bm = bmesh.new()
    bm.from_mesh(SUIT_POSED)
    pts = [v.co.copy() for e in bm.edges if e.is_boundary for v in e.verts if (v.co - Vector(near)).length < radius]
    bm.free()
    return pts


def loop_ring(pts, axis):
    """Centre and outer radius of an edge loop around an axis."""
    c = sum(pts, Vector()) / len(pts)
    r = max((p - c - axis * (p - c).dot(axis)).length for p in pts)
    return c, r


def conform_patch(name, scheme, centre, r, u, n, half_r, half_u, lift=0.008, thick=0.004):
    """A pocket that follows the cloth: the trouser faces under a rectangle, lifted and given thickness."""
    bm = bmesh.new()
    bm.from_mesh(SUIT_POSED)
    keep = set()
    for f in bm.faces:
        c = f.calc_center_median() - Vector(centre)
        if abs(c.dot(r)) < half_r and abs(c.dot(u)) < half_u and f.normal.dot(n) > 0.3 and abs(c.dot(n)) < 0.06:
            keep.add(f)
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f not in keep], context="FACES")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    bm.normal_update()
    for v in bm.verts:
        v.co += v.normal * lift
    ext = bmesh.ops.extrude_face_region(bm, geom=bm.faces[:])
    for v in [e for e in ext["geom"] if isinstance(e, bmesh.types.BMVert)]:
        v.co -= v.normal * thick
    return new_obj(name, bm, scheme)


# Body directions.
FWD, UP, LEFT = Vector((0, -1, 0)), Vector((0, 0, 1)), Vector((1, 0, 0))
waist = head("pelvis").lerp(head("spine_01"), 0.75)
chest = head("spine_03").lerp(tail("spine_03"), 0.35)

# ---- LC-2 pistol belt and buckle ----
belt_pts = []
for k in range(17):
    a = math.tau * k / 16
    d = Vector((math.sin(a), -math.cos(a), 0))
    p, n = cast(SUIT, waist + d * 0.6, -d, 0.012)
    belt_pts.append(p)
ring_loft("PistolBelt", "od", [waist - UP * 0.025, waist + UP * 0.025],
          [max((p - waist).length for p in belt_pts) * 1.0] * 2, sides=16, squash=0.8, up_hint=FWD, caps=False)
belt = PARTS[-1]
sol = belt.modifiers.new("Solid", "SOLIDIFY")
sol.thickness = 0.008
p, n = cast(SUIT, waist + FWD * 0.6, -FWD, 0.03)
r, u, n = surface_frame(n, UP)
box("BeltBuckle", "steel", p, (r, u, n), (0.03, 0.022, 0.006), 0.004)

# ---- Triple ammo pouches either side of the buckle ----
for sx in (-1, 1):
    p, n = cast(SUIT, waist + FWD * 0.6 + LEFT * sx * 0.11, -FWD, 0.035)
    r, u, n = surface_frame(n, UP)
    c = p - u * 0.04
    for i in (-1, 0, 1):
        cc = c + r * i * 0.044
        box("AmmoCell", "od", cc, (r, u, n), (0.021, 0.06, 0.03), 0.006)
        box("AmmoFlap", "od_dark", cc + u * 0.05 + n * 0.007, (r, u, n), (0.022, 0.017, 0.031), 0.005)
        box("PouchClip", "steel", cc + u * 0.036 + n * 0.034, (r, u, n), (0.007, 0.011, 0.003))

# ---- Canteens on both hips, entrenching tool at the back ----
for sx in (-1, 1):
    p, n = cast(SUIT, waist + LEFT * sx * 0.6 + UP * -0.05 - FWD * 0.05, -LEFT * sx, 0.045)
    r, u, n = surface_frame(n, UP)
    box("CanteenCover", "od", p - u * 0.05, (r, u, n), (0.06, 0.08, 0.04), 0.02)
    ring_loft("CanteenCap", "od_dark", [p + u * 0.03, p + u * 0.05], [0.014, 0.012], sides=6)
p, n = cast(SUIT, waist - FWD * 0.6 - LEFT * 0.06, FWD, 0.03)
r, u, n = surface_frame(n, UP)
box("ShovelCarrier", "od", p - u * 0.07, (r, u, n), (0.06, 0.1, 0.02), 0.012)
ring_loft("ShovelHandle", "wood", [p - u * 0.17, p - u * 0.3], [0.013, 0.013], sides=6)

# ---- Y suspenders: front belt, over the shoulders, crossing at the back ----
def harness(sx):
    pts, nrms = [], []
    sh = head("clavicle_" + ("l" if sx > 0 else "r")).lerp(head("upperarm_" + ("l" if sx > 0 else "r")), 0.45)
    for i in range(9):
        t = i / 8
        if t < 0.55:  # front, from the belt up to the shoulder top
            q = (waist + LEFT * sx * 0.09).lerp(sh + UP * 0.03, t / 0.55)
            o, d = q + FWD * 0.5, -FWD
            if t > 0.45:
                o, d = q + UP * 0.5 + FWD * 0.05, -UP
        else:  # back, down to the middle of the back
            q = (sh + UP * 0.02).lerp(chest - FWD * 0.0 - UP * 0.08, (t - 0.55) / 0.45)
            o, d = q - FWD * 0.5, FWD
        p, n = cast(SUIT, o, d, 0.004)
        pts.append(p)
        nrms.append(n)
    strap("Suspender", "od", pts, nrms, 0.04, 0.006, flat=True)
    q, nn = pts[4], nrms[4]
    rr, uu, nn = surface_frame(nn, pts[5] - pts[3])
    box("SuspenderBuckle", "steel", q + nn * 0.008, (rr, uu, nn), (0.024, 0.012, 0.003), 0.002)


harness(1)
harness(-1)

# ---- ALICE medium pack with bedroll ----
p, _ = cast(SUIT, chest - FWD * 0.6 - UP * 0.05, FWD, 0.09)
r, u, n = surface_frame(-FWD, UP)
box("AlicePack", "od", p, (r, u, n), (0.16, 0.16, 0.08), 0.03)
for i in (-1, 0, 1):
    box("PackPocket", "od", p - u * 0.06 + n * 0.09 + r * i * 0.1, (r, u, n), (0.045, 0.06, 0.025), 0.012)
    box("PackPocketFlap", "od_dark", p - u * 0.005 + n * 0.095 + r * i * 0.1, (r, u, n), (0.047, 0.014, 0.027), 0.006)
box("PackFlap", "od_dark", p + u * 0.12 + n * 0.02, (r, u, n), (0.162, 0.05, 0.075), 0.02)
for sx in (-1, 1):
    box("FlapStrap", "od_dark", p + u * 0.05 + n * 0.093 + r * sx * 0.1, (r, u, n), (0.012, 0.11, 0.004), 0.002)
    box("FlapBuckle", "steel", p - u * 0.005 + n * 0.123 + r * sx * 0.1, (r, u, n), (0.014, 0.01, 0.003), 0.002)
roll_c = p + u * 0.24 + n * 0.012
ring_loft("Bedroll", "bedroll", [roll_c - r * 0.21, roll_c - r * 0.2, roll_c + r * 0.2, roll_c + r * 0.21],
          [0.06, 0.072, 0.072, 0.06], sides=12, up_hint=n)
for sx in (-1, 1):
    ring_loft("BedrollStrap", "od_dark", [roll_c + r * sx * 0.12 - r * 0.012, roll_c + r * sx * 0.12 + r * 0.012],
              [0.075, 0.075], sides=12, up_hint=n)
    # Pack shoulder straps from the pack top over the shoulders.
    s = "l" if sx > 0 else "r"
    sh = head("clavicle_" + s).lerp(head("upperarm_" + s), 0.35)
    pts, nrms = [], []
    for i in range(9):
        t = i / 8
        q = (p + u * 0.14 + r * sx * 0.07).lerp(sh + UP * 0.03 + FWD * 0.03, min(t * 1.6, 1.0))
        if t > 0.62:
            q = (sh + UP * 0.03 + FWD * 0.03).lerp(head("upperarm_" + s) + FWD * 0.1 - UP * 0.12, (t - 0.62) / 0.38)
        o = q + UP * 0.5 if 0.35 < t < 0.75 else (q - FWD * 0.5 if t <= 0.35 else q + FWD * 0.5)
        dd = (q - o)
        pp, nn = cast(SUIT, o, dd, 0.009)
        pts.append(pp)
        nrms.append(nn)
    strap("PackStrap", "od_dark", pts, nrms, 0.045, 0.007, flat=True)

# ---- Cargo pockets and chest pockets ----
for s, sx in (("l", 1), ("r", -1)):
    th = head("thigh_" + s).lerp(tail("thigh_" + s), 0.55)
    # Narrow enough to sit on the round thigh without standing off at the edges.
    side_dir = (LEFT * sx * 0.9 + FWD * 0.3).normalized()
    p, n = cast(SUIT, th + side_dir * 0.5, -side_dir, 0.006)
    r, u, n = surface_frame(n, (tail("thigh_" + s) - head("thigh_" + s)) * -1)
    conform_patch("CargoPocket", "camo", p, r, u, n, 0.06, 0.085, 0.009)
    conform_patch("CargoFlap", "camo", p + u * 0.085, r, u, n, 0.064, 0.022, 0.016)
    p, n = cast(SUIT, chest + UP * 0.03 + LEFT * sx * 0.1 + FWD * 0.6, -FWD, 0.006)
    r, u, n = surface_frame(n, UP)
    box("ChestPocket", "camo", p, (r, u, n), (0.05, 0.06, 0.01), 0.008)
    box("ChestFlap", "camo", p + u * 0.055 + n * 0.004, (r, u, n), (0.053, 0.016, 0.011), 0.005)

# ---- Rolled sleeves: a thick camo roll over the cut edge of each sleeve ----
for s_ in ("l", "r"):
    el = head("lowerarm_" + s_)
    ua = (el - head("upperarm_" + s_)).normalized()
    pts = edge_loop(el - ua * 0.04, 0.12)
    if len(pts) < 6:
        continue
    c, rr = loop_ring(pts, ua)
    rr += 0.004
    ring_loft("SleeveRoll", "camo", [c - ua * 0.022, c - ua * 0.004, c + ua * 0.016], [rr * 0.98, rr * 1.02, rr * 0.99],
              sides=12, up_hint=FWD)

# ---- Bloused trousers over the boot tops, and laced combat boots with lug soles ----
for s_, sx in (("l", 1), ("r", -1)):
    a, k = head("foot_" + s_), head("calf_" + s_)
    shin = (k - a).normalized()
    fwd = (tail("ball_" + s_) - a)
    fwd.z = 0
    fwd.normalize()
    lat = Vector((0, 0, 1)).cross(fwd).normalized()
    pts = edge_loop(a + shin * 0.23, 0.13)
    tc, tr = loop_ring(pts, shin) if len(pts) >= 6 else (a + shin * 0.2, 0.058)
    top = tc - shin * 0.012
    # The blousing: trouser cloth puffed out over the boot top, centred on the trouser leg itself.
    # The cloth balloons a little over the boot top and turns in under it.
    ring_loft("Blouse", "camo", [top - shin * 0.01, top + shin * 0.012, top + shin * 0.04, top + shin * 0.075],
              [tr * 0.88, tr * 1.07, tr * 1.12, tr * 1.03], sides=12, up_hint=fwd, wobble=0.045, caps=False)
    # Shaft: snug at the ankle, rising to meet the blousing.
    # The shaft top tucks up inside the blousing, so no gap shows between them from any side.
    # Open at the bottom: the shaft starts at the ankle and the foot's heel counter closes it.
    ring_loft("BootShaft", "boot", [a + shin * 0.01, a + shin * 0.06, a.lerp(top, 0.65), top + shin * 0.008],
              [0.049, 0.05, 0.054, max(tr * 0.97, 0.055)], sides=10, up_hint=fwd, squash=1.05, caps=False)
    ground = Vector((a.x, a.y, 0.0))
    # Foot: sections from the heel counter to the toe cap, flat on the sole, squared at the heel.
    def section(x, top, hw, n, bottom=0.027, k=12):
        pts_ = []
        for j in range(k):
            ang = math.tau * j / k
            cx, sy = math.cos(ang), math.sin(ang)
            e = 2.0 / n
            y = hw * math.copysign(abs(cx) ** e, cx)
            z = (bottom + top) / 2 + (top - bottom) / 2 * math.copysign(abs(sy) ** e, sy)
            pts_.append(ground + fwd * x + lat * y + Vector((0, 0, z)))
        return pts_
    secs = [section(-0.044, 0.095, 0.043, 4.5), section(-0.03, 0.118, 0.048, 4), section(0.0, 0.12, 0.049, 3.5),
            section(0.06, 0.092, 0.049, 3), section(0.12, 0.068, 0.05, 3), section(0.175, 0.055, 0.046, 2.8),
            section(0.21, 0.048, 0.037, 2.6), section(0.232, 0.042, 0.024, 2.4)]
    fbm = bmesh.new()
    rings_ = [[fbm.verts.new(p) for p in sec] for sec in secs]
    for r0, r1 in zip(rings_, rings_[1:]):
        for j in range(12):
            fbm.faces.new((r0[j], r0[(j + 1) % 12], r1[(j + 1) % 12], r1[j]))
    fbm.faces.new(list(reversed(rings_[0])))
    fbm.faces.new(rings_[-1])
    new_obj("BootFoot", fbm, "boot")
    # Lug sole: a midsole slab, a thicker heel, tread blocks along the edges.
    sole = [(-0.054, 0.043), (-0.035, 0.05), (0.05, 0.053), (0.13, 0.052), (0.2, 0.044), (0.24, 0.028), (0.25, 0.0),
            (0.24, -0.028), (0.2, -0.044), (0.13, -0.052), (0.05, -0.053), (-0.035, -0.05), (-0.054, -0.043)]
    SP_ = lambda z0: (lambda x, y, z: ground + fwd * x + lat * y + Vector((0, 0, z0 + z)))
    prism("BootSole", "sole", sole, 0.009, SP_(0.019))
    prism("BootHeel", "sole", [(-0.056, 0.044), (-0.056, -0.044), (-0.005, -0.051), (-0.005, 0.051)], 0.01, SP_(0.01))
    for x in (-0.035, 0.07, 0.13, 0.19):
        w = 0.05 if x < 0.2 else 0.035
        for yy in (-w, w):
            box("Lug", "sole", ground + fwd * x + lat * yy * 0.8 + Vector((0, 0, 0.005)), (fwd, lat, Vector((0, 0, 1))),
                (0.014, 0.009, 0.005))
    # Lacing laid on the boot itself: a guide line up the instep and the shaft front, each point cast
    # onto the boot surface, eyelets either side, laces crossing between rows, a tongue under them.
    boot_parts = [o for o in PARTS[-40:] if o.name.startswith(("BootShaft", "BootFoot")) and "laced" not in o]
    boot_parts = boot_parts[-2:]
    verts, polys = [], []
    for o in boot_parts:
        base = len(verts)
        verts.extend(o.matrix_world @ v.co for v in o.data.vertices)
        polys.extend([[base + i for i in p.vertices] for p in o.data.polygons])
        o["laced"] = True
    btree = BVHTree.FromPolygons(verts, polys)
    guide = [ground + fwd * 0.105 + Vector((0, 0, 0.05)), a + fwd * 0.04 - shin * 0.01, a + shin * 0.15 + fwd * 0.02]
    rows = []
    for i in range(6):
        t = i / 5
        g = guide[0].lerp(guide[1], t * 2) if t < 0.5 else guide[1].lerp(guide[2], (t - 0.5) * 2)
        out = (fwd + Vector((0, 0, 1)) * (0.9 * (1 - t))).normalized()
        loc, nrm, _, _ = btree.ray_cast(g + out * 0.2, -out, 0.3)
        if loc is not None:
            rows.append((loc, nrm.normalized()))
    for i, (p, n) in enumerate(rows):
        nxt = rows[min(i + 1, len(rows) - 1)][0] - rows[max(i - 1, 0)][0]
        side = n.cross(nxt).normalized()
        rows[i] = (p, n, side)
    for i in range(len(rows) - 1):
        (p0, n0, s0), (p1, n1, s1) = rows[i], rows[i + 1]
        for sgn in (-1, 1):
            a0 = p0 + s0 * sgn * 0.016 + n0 * 0.002
            a1 = p1 - s1 * sgn * 0.016 + n1 * 0.002
            strap("Lace", "lace", [a0, a1], [n0, n1], 0.005, 0.0025, flat=True)

# ---- Name tapes over the chest pockets and a subdued patch on the left sleeve ----
for s_, sx, scheme in (("l", 1, "tape"), ("r", -1, "tape")):
    p, n = cast(SUIT, chest + UP * 0.105 + LEFT * sx * 0.1 + FWD * 0.6, -FWD, 0.007)
    r, u, n = surface_frame(n, UP)
    box("NameTape", scheme, p, (r, u, n), (0.05, 0.011, 0.002))
ua = head("upperarm_l").lerp(tail("upperarm_l"), 0.28)
p, n = cast(SUIT, ua + LEFT * 0.4, -LEFT, 0.006)
r, u, n = surface_frame(n, (head("upperarm_l") - tail("upperarm_l")))
box("SleevePatch", "patch", p, (r, u, n), (0.028, 0.034, 0.002), 0.004)

# ---- M1 helmet with band and chin strap ----
skull, _ = cast(BODY, head("head") + UP * 0.5, -UP)
rim_c = skull - UP * 0.1
probe = rim_c + UP * 0.03
reach = {}
for key, d in (("l", LEFT), ("r", -LEFT), ("f", FWD), ("b", -FWD)):
    loc, _, _, _ = BODY.ray_cast(probe + d * 0.3, -d, 0.3)
    reach[key] = (loc - probe).dot(d) if loc is not None else 0.08
hw = max(reach["l"], reach["r"]) + 0.045
hl = (reach["f"] + reach["b"]) / 2 + 0.05
hc = probe + FWD * (reach["f"] - reach["b"]) / 2
rim_c = Vector((hc.x, hc.y, rim_c.z))
# M1 profile: flared brim all round, straight skirt, low wide dome (r relative to the brim, h in metres).
prof = [(1.0, -0.004), (0.985, 0.004), (0.9, 0.014), (0.86, 0.045), (0.83, 0.085), (0.76, 0.118), (0.62, 0.145),
        (0.42, 0.161), (0.0, 0.168)]
segs = 16
bm = bmesh.new()
rings = []
for rr, hh in prof:
    rings.append([bm.verts.new(rim_c + UP * (hh + 0.006 * -math.cos(math.tau * k / segs)) +
                               LEFT * (rr * hw * 1.08 * math.sin(math.tau * k / segs)) +
                               FWD * (rr * hl * 1.08 * math.cos(math.tau * k / segs)))
                  for k in range(segs)])
for r0, r1 in zip(rings, rings[1:]):
    for k in range(segs):
        bm.faces.new((r0[k], r0[(k + 1) % segs], r1[(k + 1) % segs], r1[k]))
bm.faces.new(rings[-1])
helm = new_obj("Helmet", bm, "camo_helmet")
HELMET_RIM_Z = rim_c.z
band = []
for frac in (0.05, 0.075):
    band.append(rim_c + UP * frac)
ring_loft("HelmetBand", "band", band, [hl * 1.08 * 0.862, hl * 1.08 * 0.85], sides=16, squash=hw / hl,
          up_hint=FWD, caps=False)
bandobj = PARTS[-1]
sol = bandobj.modifiers.new("Solid", "SOLIDIFY")
sol.thickness = 0.006
sol.offset = 1.0
head_c = rim_c - UP * 0.075 + FWD * 0.01
strap_pts, strap_nrm = [], []
for i in range(11):
    t = i / 10
    ang = math.pi * t  # left temple, under the chin, right temple
    target = head_c + LEFT * math.cos(ang) * 0.075 - UP * (0.0 + 0.085 * math.sin(ang)) + FWD * (0.02 + 0.04 * math.sin(ang))
    d = (target - head_c).normalized()
    loc, nn, _, _ = BODY.ray_cast(target + d * 0.2, -d, 0.3)
    if loc is not None:
        strap_pts.append(loc + nn * 0.003)
        strap_nrm.append(nn)
top_l = rim_c + LEFT * (hw * 0.86) + UP * 0.012
top_r = rim_c - LEFT * (hw * 0.86) + UP * 0.012
strap("ChinStrap", "band", [top_l] + strap_pts + [top_r], [LEFT] + strap_nrm + [-LEFT], 0.012, 0.0025, flat=True)

# ---- M16A1 from side profiles at real dimensions (990 mm), bore along x from the butt ----
P = RP
Pz = lambda dz: (lambda x, y, z=0.0: RP(x, y, z + dz))
prism("Stock", "rifle", [(0.0, 0.016), (0.0, -0.105), (0.012, -0.106), (0.07, -0.096), (0.25, -0.036), (0.25, 0.012)],
      0.018, P)
prism("Buttplate", "rifle_dark", [(-0.008, 0.017), (-0.008, -0.106), (0.001, -0.107), (0.001, 0.017)], 0.0195, P)
prism("LowerReceiver", "rifle", [(0.25, 0.0), (0.25, -0.036), (0.29, -0.04), (0.355, -0.04), (0.36, -0.068),
                                 (0.435, -0.068), (0.44, -0.04), (0.47, -0.036), (0.47, 0.0)], 0.0145, P)
prism("UpperReceiver", "rifle", [(0.255, 0.0), (0.255, 0.022), (0.27, 0.024), (0.505, 0.024), (0.515, 0.012),
                                 (0.515, 0.0)], 0.015, P)
# Carry handle: rear sight tower, top rail, front post, with the sighting gap underneath.
prism("CarryHandle", "rifle", [(0.268, 0.024), (0.27, 0.082), (0.285, 0.09), (0.3, 0.09), (0.305, 0.078), (0.46, 0.078),
                               (0.48, 0.06), (0.48, 0.024), (0.458, 0.024), (0.455, 0.064), (0.31, 0.066),
                               (0.305, 0.024)], 0.0085, P)
prism("ChargingHandle", "rifle_dark", [(0.252, 0.022), (0.252, 0.031), (0.275, 0.031), (0.275, 0.022)], 0.012, P)
prism("EjectionPort", "rifle_dark", [(0.36, 0.003), (0.36, 0.018), (0.43, 0.018), (0.43, 0.003)], 0.0012, Pz(-0.0155))
ring_loft("ForwardAssist", "rifle", [RP(0.44, 0.013, -0.012), RP(0.425, 0.014, -0.03)], [0.007, 0.0065], sides=6)
prism("PistolGrip", "rifle", [(0.33, -0.04), (0.36, -0.04), (0.352, -0.06), (0.33, -0.14), (0.3, -0.142),
                              (0.31, -0.06)], 0.0135, P)
prism("TriggerGuard", "rifle", [(0.36, -0.04), (0.362, -0.064), (0.43, -0.064), (0.432, -0.04), (0.425, -0.04),
                                (0.424, -0.057), (0.368, -0.057), (0.367, -0.04)], 0.004, P)
prism("Magazine", "rifle_dark", [(0.37, -0.06), (0.432, -0.06), (0.436, -0.168), (0.374, -0.17)], 0.0115, P)
prism("MagFloor", "rifle_dark", [(0.371, -0.166), (0.438, -0.164), (0.439, -0.176), (0.372, -0.178)], 0.0125, P)
ring_loft("DeltaRing", "rifle_dark", [P(0.505, -0.004), P(0.52, -0.004)], [0.032, 0.032], sides=8, up_hint=r_left,
          rot=math.pi / 8)
ring_loft("Handguard", "rifle", [P(0.52, -0.004), P(0.79, -0.004)], [0.0285, 0.0245], sides=8, up_hint=r_left,
          rot=math.pi / 8)
ring_loft("HandguardCap", "rifle_dark", [P(0.79, -0.004), P(0.8, -0.004)], [0.026, 0.024], sides=8, up_hint=r_left,
          rot=math.pi / 8)
ring_loft("Barrel", "rifle_dark", [P(0.8, 0.0), P(0.94, 0.0)], [0.0085, 0.0085], sides=8, up_hint=r_left)
prism("FrontSight", "rifle", [(0.8, -0.012), (0.8, 0.012), (0.81, 0.058), (0.822, 0.058), (0.83, 0.012), (0.835, -0.012),
                              (0.83, -0.03), (0.805, -0.03)], 0.0095, P)
prism("BayonetLug", "rifle_dark", [(0.808, -0.03), (0.808, -0.04), (0.83, -0.04), (0.83, -0.03)], 0.004, P)
ring_loft("FlashHider", "rifle_dark", [P(0.94, 0.0), P(0.99, 0.0)], [0.011, 0.0105], sides=8, up_hint=r_left)
for k in range(3):
    a = math.tau * k / 3
    off = r_up * math.cos(a) * 0.0108 + r_left * math.sin(a) * 0.0108
    box("FlashSlot", "steel_dark", P(0.97, 0.0) + off, (axis, r_up, r_left), (0.014, 0.002, 0.002))
sa, sb = P(0.815, -0.035), P(0.03, -0.1)
sl = [sa.lerp(sb, t) + Vector((0, 0, -0.16 * 4 * t * (1 - t))) for t in [i / 10 for i in range(11)]]
strap("Sling", "od_dark", sl, [r_left] * len(sl), 0.025, 0.003)

# ---- Bake, decimate, flat shade and paint ----
bpy.data.objects["Human.eyebrow001"]["scheme"] = "brow"
bpy.data.objects["Human.low-poly"]["scheme"] = "eye"
human["scheme"] = "skin"
suit["scheme"] = "camo"
BUDGET = {"skin": 0.085, "camo": 0.085, "brow": 0.35, "eye": 1.0}


def lin(h):
    c = [int(h[i:i + 2], 16) / 255 for i in (0, 2, 4)]
    return [x / 12.92 if x <= 0.04045 else ((x + 0.055) / 1.055) ** 2.4 for x in c]


WOOD = {"light": lin("7b8448"), "dark": lin("405336"), "brown": lin("7c5e3c"), "black": lin("27251f")}
SOLID = {
    "skin": lin("c58f69"), "brow": lin("3b2a1e"), "eye": lin("2a2420"), "od": lin("626742"), "od_dark": lin("4d5134"),
    "steel": lin("6c6e6a"), "wood": lin("6a4a2c"), "bedroll": lin("4a4a42"), "boot": lin("1e1c1a"), "sole": lin("141312"),
    "lace": lin("2f2a24"), "tshirt": lin("4e5236"), "tape": lin("5a5e40"), "patch": lin("4c5034"),
    "patch_mark": lin("1f1f1c"), "stencil": lin("26261f"), "steel_dark": lin("0e0e0e"), "band": lin("3f4430"), "rifle": lin("262626"), "rifle_dark": lin("1a1a1a"),
}


def camo(p):
    # Woodland: two noise fields, black and brown as smaller streaks over two greens.
    q = Vector(p) * 13.0
    q.z *= 0.65
    a = noise.noise(q) + 0.35 * noise.noise(q * 3.3 + Vector((3.1, 7.7, 1.3)))
    b = noise.noise(q * 1.3 + Vector((17.3, 4.1, 9.7))) + 0.3 * noise.noise(q * 3.9 + Vector((9.2, 2.2, 5.5)))
    if a > 0.42:
        return WOOD["black"]
    if b > 0.22:
        return WOOD["brown"]
    if a < -0.12:
        return WOOD["dark"]
    return WOOD["light"]


mat_cloth = bpy.data.materials.new("Facets")
mat_cloth.use_nodes = True
nt = mat_cloth.node_tree
attr = nt.nodes.new("ShaderNodeVertexColor")
attr.layer_name = "Col"
nt.links.new(attr.outputs["Color"], nt.nodes["Principled BSDF"].inputs["Base Color"])
nt.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.85
mat_hard = mat_cloth.copy()
mat_hard.name = "FacetsHard"
mat_hard.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.45

PART_TRIS = {}
rot = Matrix.Rotation(math.pi, 4, "Z")
NECK_Z = head("neck_01").z
NECK_Y = (rot @ head("neck_01")).y
# Face frame in the final (turned) space: eye level centre, forward is +Y after the turn.
EYE_C = rot @ (head("head") + (tail("head") - head("head")) * 0.45)
F_FWD, F_UP, F_LEFT = Vector((0, 1, 0)), Vector((0, 0, 1)), Vector((-1, 0, 0))


def mixc(a, b, t):
    return [a[i] + (b[i] - a[i]) * t for i in range(3)]


def face_tone(c):
    rel = c - EYE_C
    f, u, sd = rel.dot(F_FWD), rel.dot(F_UP), abs(rel.dot(F_LEFT))
    tone = SOLID["skin"]
    if f > 0.02 and -0.115 < u < -0.045:  # stubble over jaw, chin and upper lip
        tone = mixc(tone, lin("8a7466"), 0.45)
    if f > 0.06 and -0.083 < u < -0.064 and sd < 0.024:  # lips
        tone = mixc(SOLID["skin"], lin("b5675a"), 0.55)
    elif f > 0.035 and -0.045 < u < -0.01 and 0.025 < sd < 0.06:  # cheeks
        tone = mixc(tone, lin("cf8a72"), 0.35)
    if sd > 0.065 and f < 0.03 and -0.06 < u < 0.02:  # ears
        tone = mixc(tone, lin("c97c64"), 0.4)
    return tone
finals = []
painted = [o for o in list(bpy.data.objects) if o.type == "MESH" and "scheme" in o]
dg = bpy.context.evaluated_depsgraph_get()
rng = random.Random(7)
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
            # Face and hands are what the eye goes to: they keep more of their triangles.
            keep = obj.vertex_groups.new(name="Keep")
            neck_z = (rot @ head("neck_01")).z
            hands = [rot @ head("hand_l"), rot @ head("hand_r")]
            keep.add([v.index for v in me.vertices if v.co.z > neck_z - 0.02], 1.0, "REPLACE")
            keep.add([v.index for v in me.vertices if min((v.co - h).length for h in hands) < 0.15], 1.0, "REPLACE")
            d.vertex_group = "Keep"
            d.invert_vertex_group = False
            d.vertex_group_factor = 1.5
        dg2 = bpy.context.evaluated_depsgraph_get()
        m2 = bpy.data.meshes.new_from_object(obj.evaluated_get(dg2))
        obj.modifiers.clear()
        obj.data = m2
    me = obj.data
    # Every part in triangles, so camo and tone jitter read as facets everywhere, not as squares.
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.triangulate(bm, faces=bm.faces[:], quad_method="ALTERNATE")
    bm.to_mesh(me)
    bm.free()
    me.materials.clear()
    me.materials.append(mat_hard if scheme in ("steel", "rifle", "rifle_dark", "boot") else mat_cloth)
    col = me.color_attributes.new("Col", "FLOAT_COLOR", "CORNER")
    for poly in me.polygons:
        poly.use_smooth = False
        if scheme == "camo_helmet" and poly.center.z < HELMET_RIM_Z + 0.011:
            base = SOLID["band"]  # the cover folded under the brim
        elif scheme in ("camo", "camo_helmet"):
            base = camo(rot.inverted() @ poly.center)
        elif scheme == "skin" and NECK_Z - 0.22 < poly.center.z < NECK_Z - 0.035 and abs(poly.center.x) < 0.11 \
                and abs(poly.center.y - NECK_Y) < 0.16:
            base = SOLID["tshirt"]
        elif scheme == "skin" and poly.center.z > NECK_Z:
            base = face_tone(poly.center)
        else:
            base = SOLID[scheme]
        k = rng.uniform(0.86, 1.1)
        c = [min(x * k, 1.0) for x in base] + [1.0]
        for li in poly.loop_indices:
            col.data[li].color = c
    finals.append(obj)
    import re as _re
    key = _re.sub(r"[._]\d+|_lp$|\d+$", "", o.name.replace("_lp", ""))
    PART_TRIS[key] = PART_TRIS.get(key, 0) + sum(len(p.vertices) - 2 for p in obj.data.polygons)
# ---- Skinning weights for every part ----
# Body, uniform, eyes and brows carry their MPFB weights. Helmet and chin strap ride the head, the
# rifle and its sling the right hand, boots the feet and calves; the rest of the kit copies the weights
# of the nearest point of the uniform.
import re as _re2
RIFLE_PARTS = {"Stock", "Buttplate", "LowerReceiver", "UpperReceiver", "CarryHandle", "ChargingHandle", "EjectionPort",
               "ForwardAssist", "PistolGrip", "TriggerGuard", "Magazine", "MagFloor", "DeltaRing", "Handguard",
               "HandguardCap", "Barrel", "FrontSight", "BayonetLug", "FlashHider", "FlashSlot", "Sling"}
BOOT_PARTS = {"BootShaft", "BootFoot", "BootSole", "BootHeel", "Lug", "Lace"}
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
    key = _re2.sub(r"[._]\d+$", "", obj.name[:-3])
    for v in obj.data.vertices:
        m = rot.inverted() @ v.co
        sd = "l" if m.x > 0 else "r"
        if key.startswith(("Helmet", "ChinStrap")):
            put(obj, v.index, "head", 1.0)
        elif key in RIFLE_PARTS:
            put(obj, v.index, "hand_r", 1.0)
        elif key in BOOT_PARTS:
            put(obj, v.index, ("foot_" if m.z < ANKLE_Z + 0.03 else "calf_") + sd, 1.0)
        elif key == "Blouse":
            put(obj, v.index, "calf_" + sd, 1.0)
        elif key == "SleeveRoll":
            put(obj, v.index, "upperarm_" + sd, 1.0)
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
# One object for the game: a single mesh with two materials and the face colors.
bpy.ops.object.select_all(action="DESELECT")
for o in finals:
    o.select_set(True)
bpy.context.view_layer.objects.active = finals[0]
bpy.ops.object.join()
soldier = bpy.context.view_layer.objects.active
soldier.name = soldier.data.name = "USRifleman"
finals = [soldier]

# Bake ambient occlusion and a soft top light into the face colors: shading that survives any
# engine lighting (under the helmet brim, between pouches, in the armpits and folds).
from mathutils.bvhtree import BVHTree as _BVH
_tree = _BVH.FromObject(soldier, bpy.context.evaluated_depsgraph_get())
_dirs = []
for i in range(24):
    # Fixed cosine weighted hemisphere samples around +Z.
    a = math.tau * ((i * 0.618034) % 1.0)
    r2 = (i + 0.5) / 24
    _dirs.append(Vector((math.cos(a) * math.sqrt(r2), math.sin(a) * math.sqrt(r2), math.sqrt(1 - r2))))
_col = soldier.data.color_attributes["Col"]
for poly in soldier.data.polygons:
    n = poly.normal
    t = n.orthogonal().normalized()
    b = n.cross(t)
    origin = poly.center + n * 0.003
    # Short rays: contact shadow between parts, not the face shading itself with its own nose.
    occ = 0.0
    for d in _dirs:
        w = t * d.x + b * d.y + n * d.z
        hit = _tree.ray_cast(origin, w, 0.09)
        if hit[0] is not None:
            occ += 1.0 - hit[3] / 0.09
    occ /= len(_dirs)
    shade = (1.0 - 0.75 * occ) * (0.93 + 0.1 * max(0.0, n.z))
    for li in poly.loop_indices:
        c = _col.data[li].color
        _col.data[li].color = (c[0] * shade, c[1] * shade, c[2] * shade, 1.0)
# ---- Bind: the held pose becomes the rest pose, the rig turns like the meshes, the soldier is skinned ----
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
rig.name = rig.data.name = "USRiflemanRig"
soldier.parent = rig
arm_mod = soldier.modifiers.new("Armature", "ARMATURE")
arm_mod.object = rig
# The rifle frame in the bind pose (origin at the butt, x along the bore, y up, z the rifle's left),
# for the animation script to keep both hands on the rifle.
rifle_bind = rot @ Matrix((
    (axis.x, r_up.x, r_left.x, butt.x),
    (axis.y, r_up.y, r_left.y, butt.y),
    (axis.z, r_up.z, r_left.z, butt.z),
    (0.0, 0.0, 0.0, 1.0)))
rig["rifle_bind"] = [rifle_bind[i][j] for i in range(4) for j in range(4)]
tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in finals)
print("LOWPOLY_TRIS", tris)
for k_, v_ in sorted(PART_TRIS.items(), key=lambda kv: -kv[1])[:30]:
    print("PART", k_, v_)
bpy.ops.wm.save_as_mainfile(filepath=OUT)
if OUT_GLB:
    bpy.ops.export_scene.gltf(filepath=OUT_GLB, export_format="GLB", export_skins=True, export_animations=False)
