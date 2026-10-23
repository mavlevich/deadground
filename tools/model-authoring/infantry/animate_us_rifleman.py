"""Animation clips for the skinned US rifleman: idle, aim standing, aim kneeling, aim prone, walk, run.

Poses are set bone by bone in armature space, the same way as the bind pose: legs solved to ankle
and toe targets, the spine bent and turned, the rifle placed (carried with the chest or shouldered),
then both arms solved so each hand keeps its bind grip on the rifle. Walk and run are cycles in place;
the game moves the unit and scales the playback to its speed.

Frame: the soldier faces +Y, his left is -X, up is +Z.
Run: blender --background --python animate_us_rifleman.py -- in.blend out.blend out.glb
"""

import math
import sys

import bpy
from mathutils import Matrix, Vector

src, out_blend, out_glb = sys.argv[sys.argv.index("--") + 1:][:3]
bpy.ops.wm.open_mainfile(filepath=src)
rig = bpy.data.objects["USRiflemanRig"]
pb = rig.pose.bones
REST = {b.name: b.matrix_local.copy() for b in rig.data.bones}
HEAD0 = {b.name: b.head_local.copy() for b in rig.data.bones}
TAIL0 = {b.name: b.tail_local.copy() for b in rig.data.bones}
FWD, LEFT, UP = Vector((0, 1, 0)), Vector((-1, 0, 0)), Vector((0, 0, 1))
RIGHT = -LEFT
vals = list(rig["rifle_bind"])
RIFLE_BIND = Matrix([vals[0:4], vals[4:8], vals[8:12], vals[12:16]])
HAND_REL = {s: RIFLE_BIND.inverted() @ REST["hand_" + s] for s in ("l", "r")}


def update():
    bpy.context.view_layer.update()


def reset():
    for b in pb:
        b.rotation_mode = "QUATERNION"
        b.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
        b.location = (0.0, 0.0, 0.0)
        b.scale = (1.0, 1.0, 1.0)
    update()


def head(n):
    return pb[n].head.copy()


def tail(n):
    return pb[n].tail.copy()


def blen(n):
    return (TAIL0[n] - HEAD0[n]).length


def rotate_about(n, rot3, pivot):
    m = pb[n].matrix.copy()
    pb[n].matrix = Matrix.Translation(pivot) @ rot3.to_4x4() @ Matrix.Translation(-pivot) @ m
    update()


def turn(n, axis, deg):
    rotate_about(n, Matrix.Rotation(math.radians(deg), 3, axis), head(n))


def lean(n, deg):
    """Bend a bone forward (positive) or back about the soldier's right axis."""
    turn(n, RIGHT, -deg)


def move(n, delta):
    pb[n].matrix = Matrix.Translation(delta) @ pb[n].matrix
    update()


def aim(n, target):
    h, t = head(n), tail(n)
    rotate_about(n, (t - h).rotation_difference(Vector(target) - h).to_matrix(), h)


def elbow(a, b, l1, l2, pole):
    d = b - a
    length = min(d.length, l1 + l2 - 1e-3)
    ax = d.normalized()
    x = (l1 * l1 - l2 * l2 + length * length) / (2 * length)
    h = math.sqrt(max(l1 * l1 - x * x, 0.0))
    p = (Vector(pole) - ax * Vector(pole).dot(ax)).normalized()
    return a + ax * x + p * h


def leg(s, ankle, ball, pole, knee=None):
    hip = head("thigh_" + s)
    knee = knee or elbow(hip, ankle, blen("thigh_" + s), blen("calf_" + s), pole)
    aim("thigh_" + s, knee)
    aim("calf_" + s, ankle)
    aim("foot_" + s, ball)
    toe_dir = ball - ankle
    toe_dir.z = 0
    aim("ball_" + s, ball + (toe_dir.normalized() if toe_dir.length > 1e-4 else FWD) * 0.074)


def flat_ball(ankle, forward=FWD, toe_down=0.0):
    b = ankle + forward * 0.133
    b.z = ankle.z - 0.066 - toe_down
    return b


def place_hands(rifle, poles):
    """Both hands back on their bind grips for a rifle frame."""
    for s in ("r", "l"):
        target = rifle @ HAND_REL[s]
        wrist = target.translation.copy()
        sh = head("upperarm_" + s)
        el = elbow(sh, wrist, blen("upperarm_" + s), blen("lowerarm_" + s), poles[s])
        aim("upperarm_" + s, el)
        aim("lowerarm_" + s, wrist)
        pb["hand_" + s].matrix = target
        update()


def carried_rifle():
    """The rifle where the bind hold puts it, moved with the chest."""
    return pb["spine_03"].matrix @ REST["spine_03"].inverted() @ RIFLE_BIND


def shouldered_rifle(pitch_deg=-2.0):
    """Rifle in the right shoulder pocket, bore along the facing, butt plate centre in the pocket."""
    p = math.radians(pitch_deg)
    ax = (FWD * math.cos(p) + UP * math.sin(p)).normalized()
    up_a = (UP - ax * ax.dot(UP)).normalized()
    left_a = up_a.cross(ax).normalized()
    pocket = head("upperarm_r") + LEFT * 0.07 + FWD * 0.03 - UP * 0.02
    origin = pocket + up_a * 0.045
    return Matrix(((ax.x, up_a.x, left_a.x, origin.x), (ax.y, up_a.y, left_a.y, origin.y),
                   (ax.z, up_a.z, left_a.z, origin.z), (0, 0, 0, 1)))


CARRY_POLES = {"r": Vector((1.0, -0.6, -0.2)), "l": Vector((-1.0, -0.3, -0.6))}
AIM_POLES = {"r": Vector((1.0, -0.1, -0.7)), "l": Vector((-0.3, 0.0, -1.0))}
PRONE_POLES = {"r": Vector((1.0, 0.0, -1.0)), "l": Vector((-1.0, 0.0, -1.0))}


def cheek_weld():
    lean("neck_01", 8)
    lean("head", 6)
    turn("head", FWD, -6)  # tilt toward the stock on the right


# ---- Poses ----
def pose_idle(t):
    reset()
    lean("spine_02", 1.2 * math.sin(math.tau * t))
    place_hands(carried_rifle(), CARRY_POLES)


def pose_stand_aim(t):
    reset()
    for s, dy in (("l", 0.12), ("r", -0.1)):
        a = HEAD0["foot_" + s] + FWD * dy
        leg(s, a, flat_ball(a), FWD)
    lean("spine_01", 5 + 0.4 * math.sin(math.tau * t))
    cheek_weld()
    place_hands(shouldered_rifle(), AIM_POLES)


def pose_kneel_aim(t):
    reset()
    move("pelvis", Vector((0.0, -0.06, -0.43)))
    hip_r = head("thigh_r")
    knee_r = hip_r + (FWD * -0.06 - UP).normalized() * blen("thigh_r")
    knee_r.z = max(knee_r.z, 0.06)
    ankle_r = knee_r - FWD * blen("calf_r") * 0.96 + UP * 0.05
    leg("r", ankle_r, ankle_r + FWD * 0.03 - UP * 0.08, -FWD, knee=knee_r)
    ankle_l = Vector((HEAD0["foot_l"].x, head("thigh_l").y + 0.42, 0.075))
    leg("l", ankle_l, flat_ball(ankle_l), FWD + UP)
    lean("spine_01", 6 + 0.4 * math.sin(math.tau * t))
    cheek_weld()
    place_hands(shouldered_rifle(), AIM_POLES)


def pose_prone_aim(t):
    reset()
    rotate_about("pelvis", Matrix.Rotation(math.radians(-90), 3, RIGHT), head("pelvis"))
    move("pelvis", Vector((0.0, -0.5, 0.15 - head("pelvis").z)))
    turn("spine_02", RIGHT, 24)
    turn("spine_03", RIGHT, 14)
    turn("neck_01", RIGHT, 26)
    turn("head", RIGHT, 14)
    for s, sx in (("l", -1.0), ("r", 1.0)):
        hip = head("thigh_" + s)
        ankle = Vector((hip.x + sx * 0.14, hip.y - 0.84, 0.085))
        leg(s, ankle, ankle - FWD * 0.03 - UP * 0.07, -UP)
    rifle = shouldered_rifle(pitch_deg=0.5 * math.sin(math.tau * t))
    place_hands(rifle, PRONE_POLES)


def pose_gait(t, stride, lift, drop, bob, lean_deg, twist):
    reset()
    ph = math.tau * t
    move("pelvis", Vector((0.0, 0.0, -(drop + bob * math.cos(2 * ph)))))
    turn("pelvis", UP, twist * math.sin(ph))
    for s, p in (("l", ph), ("r", ph + math.pi)):
        base = HEAD0["foot_" + s]
        swing = max(0.0, -math.sin(p))
        a = Vector((base.x, base.y + stride * math.cos(p), 0.075 + lift * swing))
        leg(s, a, flat_ball(a, toe_down=0.03 * swing), FWD)
    lean("spine_01", lean_deg)
    turn("spine_02", UP, -twist * 1.3 * math.sin(ph))
    place_hands(carried_rifle(), CARRY_POLES)


CLIPS = [
    ("Idle", 60, pose_idle),
    ("AimStanding", 40, pose_stand_aim),
    ("AimKneeling", 40, pose_kneel_aim),
    ("AimProne", 40, pose_prone_aim),
    ("Walk", 32, lambda t: pose_gait(t, 0.27, 0.07, 0.02, 0.012, 3, 4)),
    ("Run", 20, lambda t: pose_gait(t, 0.4, 0.16, 0.07, 0.025, 11, 6)),
]
KEY_STEP = {"Idle": 10, "AimStanding": 10, "AimKneeling": 10, "AimProne": 10, "Walk": 2, "Run": 2}

rig.animation_data_create()
scene = bpy.context.scene
scene.render.fps = 30
scene.frame_set(0)
actions = []
# Author every clip with no NLA tracks around: a track of an earlier clip would be evaluated over
# the pose being keyed (it leaked the prone pose into frame 0 of the walk).
for name, length, pose in CLIPS:
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    rig.animation_data.action = act
    for f in list(range(0, length, KEY_STEP[name])) + [length]:
        pose((f % length) / length)
        for b in pb:
            b.keyframe_insert("location", frame=f)
            b.keyframe_insert("rotation_quaternion", frame=f)
    rig.animation_data.action = None
    actions.append(act)
    print("CLIP", name, length)
# One muted track per clip, for the exporter (one glTF animation per track).
for act in actions:
    track = rig.animation_data.nla_tracks.new()
    track.name = act.name
    track.strips.new(act.name, 0, act)
    track.mute = True
reset()
bpy.ops.wm.save_as_mainfile(filepath=out_blend)
# MPFB bone display shapes are not part of the model: the exporter would carry them along.
for b in pb:
    b.custom_shape = None
for o in list(bpy.data.objects):
    if o.type == "MESH" and o.name != "USRifleman":
        bpy.data.objects.remove(o)
bpy.ops.object.select_all(action="DESELECT")
rig.select_set(True)
bpy.data.objects["USRifleman"].select_set(True)
bpy.ops.export_scene.gltf(filepath=out_glb, export_format="GLB", use_selection=True, export_skins=True,
                          export_animations=True, export_animation_mode="NLA_TRACKS")
print("EXPORTED", out_glb)
