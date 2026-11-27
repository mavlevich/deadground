"""Animation clips for skinned Soviet infantry (AK-74 rifleman and RPG-7V anti-tank gunner).

Generates the six standard locomotion & combat clips on the MPFB game_engine rig:
  Idle, AimStanding, AimKneeling, AimProne, Walk, Run.
Reads rig["role"] ("rifleman" or "rpg") and rig["weapon_bind"] from the bind blend so both
roles keep both hands locked to their exact weapon grips in every pose.

Frame: the soldier faces +Y, his left is -X, up is +Z.
Run: blender --background --python animate_soviet_soldier.py -- in.blend out.blend out.glb
"""

import math
import sys

import bpy
from mathutils import Matrix, Vector

src, out_blend, out_glb = sys.argv[sys.argv.index("--") + 1:][:3]
bpy.ops.wm.open_mainfile(filepath=src)
rig = bpy.data.objects["SovietSoldierRig"]
ROLE = str(rig.get("role", "rifleman"))
MESH_NAME = str(rig.get("mesh_name", "SovietRifleman"))
pb = rig.pose.bones
REST = {b.name: b.matrix_local.copy() for b in rig.data.bones}
HEAD0 = {b.name: b.head_local.copy() for b in rig.data.bones}
TAIL0 = {b.name: b.tail_local.copy() for b in rig.data.bones}
FWD, LEFT, UP = Vector((0, 1, 0)), Vector((-1, 0, 0)), Vector((0, 0, 1))
RIGHT = -LEFT
vals = list(rig["weapon_bind"])
WEAPON_BIND = Matrix([vals[0:4], vals[4:8], vals[8:12], vals[12:16]])
HAND_REL = {s: WEAPON_BIND.inverted() @ REST["hand_" + s] for s in ("l", "r")}


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


def turn(n, axis_, deg):
    rotate_about(n, Matrix.Rotation(math.radians(deg), 3, axis_), head(n))


def lean(n, deg):
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


def place_hands(weapon_mat, poles):
    for s in ("r", "l"):
        target = weapon_mat @ HAND_REL[s]
        wrist = target.translation.copy()
        sh = head("upperarm_" + s)
        el = elbow(sh, wrist, blen("upperarm_" + s), blen("lowerarm_" + s), poles[s])
        aim("upperarm_" + s, el)
        aim("lowerarm_" + s, wrist)
        pb["hand_" + s].matrix = target
        update()


def carried_weapon():
    return pb["spine_03"].matrix @ REST["spine_03"].inverted() @ WEAPON_BIND


def shouldered_weapon(pitch_deg=-1.5):
    p = math.radians(pitch_deg)
    ax = (FWD * math.cos(p) + UP * math.sin(p)).normalized()
    up_a = (UP - ax * ax.dot(UP)).normalized()
    left_a = up_a.cross(ax).normalized()
    if ROLE == "rifleman":
        pocket = head("upperarm_r") + LEFT * 0.065 + FWD * 0.045 - UP * 0.015
        origin = pocket + up_a * 0.042
    else:
        # RPG-7 rests level on top of right shoulder pad, optical sight aligned with right eye
        sh_r = head("upperarm_r")
        rest_pt = sh_r + UP * 0.105 + RIGHT * 0.025 + FWD * 0.045
        origin = rest_pt - ax * 0.34
    return Matrix((
        (ax.x, up_a.x, left_a.x, origin.x),
        (ax.y, up_a.y, left_a.y, origin.y),
        (ax.z, up_a.z, left_a.z, origin.z),
        (0, 0, 0, 1),
    ))


if ROLE == "rifleman":
    CARRY_POLES = {"r": Vector((1.0, -0.6, -0.2)), "l": Vector((-1.0, -0.3, -0.6))}
    AIM_POLES = {"r": Vector((1.0, -0.1, -0.7)), "l": Vector((-0.3, 0.0, -1.0))}
    PRONE_POLES = {"r": Vector((1.0, 0.0, -1.0)), "l": Vector((-1.0, 0.0, -1.0))}
else:
    CARRY_POLES = {"r": Vector((0.9, 0.35, -0.85)), "l": Vector((-0.75, 0.75, -0.70))}
    AIM_POLES = {"r": Vector((0.9, 0.35, -0.85)), "l": Vector((-0.75, 0.75, -0.75))}
    PRONE_POLES = {"r": Vector((1.0, 0.2, -1.0)), "l": Vector((-0.9, 0.4, -1.0))}


def cheek_weld():
    if ROLE == "rifleman":
        lean("neck_01", 8)
        lean("head", 6)
        turn("head", FWD, -6)
    else:
        lean("neck_01", 4)
        lean("head", 2)
        turn("head", FWD, -8)


# ---- Poses ----
def pose_idle(t):
    reset()
    lean("spine_02", 1.2 * math.sin(math.tau * t))
    place_hands(carried_weapon(), CARRY_POLES)


def pose_stand_aim(t):
    reset()
    for s, dy in (("l", 0.13), ("r", -0.11)):
        a = HEAD0["foot_" + s] + FWD * dy
        leg(s, a, flat_ball(a), FWD)
    if ROLE == "rpg":
        # Natural bladed RPG-7 firing stance: torso rotated slightly left, head aligned with PGO-7 sight
        turn("spine_01", UP, 10)
        turn("spine_02", UP, 6)
        turn("neck_01", UP, -8)
        turn("head", UP, -6)
    lean("spine_01", 5 + 0.4 * math.sin(math.tau * t))
    cheek_weld()
    place_hands(shouldered_weapon(pitch_deg=-1.5 if ROLE == "rifleman" else 0.0), AIM_POLES)


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
    if ROLE == "rpg":
        turn("spine_01", UP, 10)
        turn("spine_02", UP, 6)
        turn("neck_01", UP, -8)
        turn("head", UP, -6)
    lean("spine_01", 6 + 0.4 * math.sin(math.tau * t))
    cheek_weld()
    place_hands(shouldered_weapon(pitch_deg=-1.0 if ROLE == "rifleman" else 0.5), AIM_POLES)


def pose_prone_aim(t):
    reset()
    rotate_about("pelvis", Matrix.Rotation(math.radians(-90), 3, RIGHT), head("pelvis"))
    move("pelvis", Vector((0.0, -0.5, 0.16 - head("pelvis").z)))
    turn("spine_02", RIGHT, 24)
    turn("spine_03", RIGHT, 15)
    turn("neck_01", RIGHT, 25)
    turn("head", RIGHT, 14)
    for s, sx in (("l", -1.0), ("r", 1.0)):
        hip = head("thigh_" + s)
        ankle = Vector((hip.x + sx * 0.14, hip.y - 0.84, 0.085))
        leg(s, ankle, ankle - FWD * 0.03 - UP * 0.07, -UP)
    weapon = shouldered_weapon(pitch_deg=(0.5 if ROLE == "rifleman" else 2.5) + 0.4 * math.sin(math.tau * t))
    place_hands(weapon, PRONE_POLES)


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
    place_hands(carried_weapon(), CARRY_POLES)


def running_weapon(t):
    # Tactical combat-run weapon carry: pulled close to body with bent elbows,
    # stabilized forward with inertial bounce lagging pelvis by ~0.08 cycles.
    bob_phase = math.tau * 2.0 * (t - 0.24)
    inertia_z = -0.018 * math.cos(bob_phase)
    pitch_bob = 2.4 * math.sin(bob_phase)
    sway_yaw = 2.5 * math.sin(math.tau * t)
    spine_pos = head("spine_03")
    if ROLE == "rifleman":
        # Compact tactical cross-chest run: stock tucked near right chest/ribs, bent elbows
        yaw_rad = math.radians(38.0 + sway_yaw)
        pitch_rad = math.radians(-12.0 + pitch_bob)
        ax = (FWD * math.cos(yaw_rad) + LEFT * math.sin(yaw_rad)) * math.cos(pitch_rad) + UP * math.sin(pitch_rad)
        ax.normalize()
        up_a = (UP - ax * ax.dot(UP)).normalized()
        left_a = up_a.cross(ax).normalized()
        origin = spine_pos + RIGHT * 0.14 + FWD * 0.08 - UP * 0.05 + UP * inertia_z
    else:
        # RPG-7V combat run: carried securely at right side/underarm with bent elbows,
        # angled forward-upward ~18 deg so warhead stays clear and natural (never locked straight-armed).
        yaw_rad = math.radians(12.0 + sway_yaw * 0.65)
        pitch_rad = math.radians(18.0 + pitch_bob * 0.7)
        ax = (FWD * math.cos(yaw_rad) + LEFT * math.sin(yaw_rad)) * math.cos(pitch_rad) + UP * math.sin(pitch_rad)
        ax.normalize()
        up_a = (UP - ax * ax.dot(UP)).normalized()
        left_a = up_a.cross(ax).normalized()
        origin = spine_pos + RIGHT * 0.20 - FWD * 0.18 - UP * 0.14 + UP * inertia_z
    return Matrix((
        (ax.x, up_a.x, left_a.x, origin.x),
        (ax.y, up_a.y, left_a.y, origin.y),
        (ax.z, up_a.z, left_a.z, origin.z),
        (0, 0, 0, 1),
    ))


def _eval_hermite(p0, v0, p1, v1, s):
    s2 = s * s
    s3 = s2 * s
    h00 = 2.0 * s3 - 3.0 * s2 + 1.0
    h10 = s3 - 2.0 * s2 + s
    h01 = -2.0 * s3 + 3.0 * s2
    h11 = s3 - s2
    return h00 * p0 + h10 * v0 + h01 * p1 + h11 * v1


def pose_run(t):
    reset()
    ph = math.tau * t

    # Athletic military combat run (22 frames @ 30 fps = 0.733 s per full L+R stride):
    # - Stance covers u in [0.00, 0.34] with linear ground sweep (zero foot skating).
    # - Springy vertical pelvis arc (no duck-squat): low cushion at mid-stance (u=0.16),
    #   upright extension at push-off/flight (u=0.41).
    # - Clear runner's heel kick behind the knee during early swing + high forward knee drive.
    stance_end = 0.34
    y_front = 0.33
    y_back = -0.39
    ground_vel = (y_back - y_front) / stance_end

    bob_ph = math.tau * 2.0 * (t - 0.16)
    pelvis_drop = 0.044 + 0.030 * math.cos(bob_ph)
    move("pelvis", Vector((0.0, 0.0, -pelvis_drop)))
    turn("pelvis", UP, 7.0 * math.cos(ph))
    turn("pelvis", FWD, 2.5 * math.sin(ph))

    for s, phase_shift, sx in (("l", 0.0, -1.0), ("r", 0.5, 1.0)):
        base = HEAD0["foot_" + s]
        u = (t + phase_shift) % 1.0
        if u < stance_end:
            su = u / stance_end
            y_off = y_front + (y_back - y_front) * su
            push = max(0.0, (su - 0.60) / 0.40)
            z_ankle = 0.075 + 0.095 * (push * push)
            toe_down = -0.025 * max(0.0, 1.0 - su / 0.20) + 0.068 * (push ** 1.2)
            x_inward = -sx * 0.026 * math.sin(math.pi * su)
        else:
            su = (u - stance_end) / (1.0 - stance_end)
            swing_dur = 1.0 - stance_end
            v_stance = ground_vel * swing_dur
            # 3-part smooth swing: (1) heel-fold behind knee -> (2) forward knee drive -> (3) paw-back plant
            if su < 0.34:
                k = su / 0.34
                y_off = _eval_hermite(y_back, v_stance * 0.34, -0.36, 0.25, k)
                z_ankle = _eval_hermite(0.170, 0.42, 0.355, 0.04, k)
                toe_down = 0.068 * (1.0 - 0.25 * k)
            elif su < 0.76:
                k = (su - 0.34) / 0.42
                y_off = _eval_hermite(-0.36, 0.30, 0.28, 0.48, k)
                z_ankle = _eval_hermite(0.355, -0.02, 0.215, -0.22, k)
                toe_down = 0.051 * (1.0 - k) - 0.010 * k
            else:
                k = (su - 0.76) / 0.24
                y_off = _eval_hermite(0.28, 0.26, y_front, v_stance * 0.24 * 0.42, k)
                z_ankle = _eval_hermite(0.215, -0.14, 0.076, -0.22, k)
                toe_down = -0.010 * (1.0 - k) - 0.025 * k
            x_inward = -sx * 0.014 * math.sin(math.pi * su)

        a = Vector((base.x + x_inward, base.y + y_off, z_ankle))
        leg(s, a, flat_ball(a, toe_down=toe_down), FWD)

    # Athletic forward sprint lean + smooth shoulder counter-rotation + eyes level ahead
    bounce_lean = 1.6 * math.cos(bob_ph)
    lean("spine_01", (13.0 if ROLE == "rifleman" else 11.5) + bounce_lean)
    lean("spine_02", (6.5 if ROLE == "rifleman" else 5.5) + 0.5 * bounce_lean)
    turn("spine_02", UP, -8.5 * math.cos(ph))
    turn("spine_03", UP, -3.0 * math.cos(ph))
    lean("neck_01", -8.5 if ROLE == "rifleman" else -7.5)
    lean("head", -7.5 if ROLE == "rifleman" else -6.5)
    turn("head", UP, 4.5 * math.cos(ph))
    run_poles = CARRY_POLES if ROLE == "rifleman" else {"r": Vector((1.0, -0.55, -0.65)), "l": Vector((-0.85, 0.25, -0.85))}
    place_hands(running_weapon(t), run_poles)


CLIPS = [
    ("Idle", 60, pose_idle),
    ("AimStanding", 40, pose_stand_aim),
    ("AimKneeling", 40, pose_kneel_aim),
    ("AimProne", 40, pose_prone_aim),
    ("Walk", 32, lambda t: pose_gait(t, 0.27, 0.07, 0.02, 0.012, 3, 4)),
    ("Run", 22, pose_run),
]
KEY_STEP = {"Idle": 10, "AimStanding": 10, "AimKneeling": 10, "AimProne": 10, "Walk": 2, "Run": 1}

rig.animation_data_create()
scene = bpy.context.scene
scene.render.fps = 30
scene.frame_set(0)
actions = []
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
    print("CLIP", ROLE, name, length)

for act in actions:
    track = rig.animation_data.nla_tracks.new()
    track.name = act.name
    track.strips.new(act.name, 0, act)
    track.mute = True
reset()
bpy.ops.wm.save_as_mainfile(filepath=out_blend)

for b in pb:
    b.custom_shape = None
for o in list(bpy.data.objects):
    if o.type == "MESH" and o.name != MESH_NAME:
        bpy.data.objects.remove(o)
bpy.ops.object.select_all(action="DESELECT")
rig.select_set(True)
bpy.data.objects[MESH_NAME].select_set(True)
bpy.ops.export_scene.gltf(
    filepath=out_glb,
    export_format="GLB",
    use_selection=True,
    export_skins=True,
    export_animations=True,
    export_animation_mode="NLA_TRACKS",
)
print("EXPORTED", out_glb)
