"""Preview frames of every clip of the animated rifleman, written as PNG sequences for GIFs.

Usage: blender --background --python preview_clips.py -- anim.blend out_dir [clip,clip...]
"""
import math
import os
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
bpy.ops.wm.open_mainfile(filepath=argv[0])
out_dir = argv[1]
only = argv[2].split(",") if len(argv) > 2 else None
os.makedirs(out_dir, exist_ok=True)
scene = bpy.context.scene
for engine in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
    try:
        scene.render.engine = engine
        break
    except TypeError:
        pass
scene.render.resolution_x = 420
scene.render.resolution_y = 520
scene.view_settings.view_transform = "AgX"
world = bpy.data.worlds.new("World")
scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.66, 0.65, 0.62, 1)
mat = bpy.data.materials.new("Floor")
mat.use_nodes = True
mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.5, 0.5, 0.48, 1)
bpy.ops.mesh.primitive_plane_add(size=30)
bpy.context.active_object.data.materials.append(mat)
for name, energy, rot in (("Key", 3.6, (48, 0, 205)), ("Fill", 0.8, (60, 0, 120)), ("Rim", 2.2, (50, 0, 20))):
    d = bpy.data.lights.new(name, "SUN")
    d.energy = energy
    d.angle = math.radians(8)
    o = bpy.data.objects.new(name, d)
    scene.collection.objects.link(o)
    o.rotation_euler = [math.radians(a) for a in rot]
cam_d = bpy.data.cameras.new("Cam")
cam_d.lens = 50
cam = bpy.data.objects.new("Cam", cam_d)
scene.collection.objects.link(cam)
scene.camera = cam

rig = bpy.data.objects["USRiflemanRig"]
for tr in rig.animation_data.nla_tracks:
    tr.mute = True
for act in bpy.data.actions:
    if only and act.name not in only:
        continue
    rig.animation_data.action = act
    if hasattr(rig.animation_data, "action_slot") and act.slots:
        rig.animation_data.action_slot = act.slots[0]
    start, end = (int(x) for x in act.frame_range)
    prone = act.name == "AimProne"
    target = Vector((0, 0.1, 0.35)) if prone else Vector((0, 0, 0.85))
    loc = Vector((3.0, 3.6, 1.6)) if prone else Vector((2.6, 3.6, 1.5))
    cam.location = loc
    cam.rotation_euler = (target - loc).to_track_quat("-Z", "Y").to_euler()
    step = 2 if act.name in ("Walk", "Run") else 4
    for f in range(start, end, step):
        scene.frame_set(f)
        scene.render.filepath = os.path.join(out_dir, "%s_%03d.png" % (act.name, f))
        bpy.ops.render.render(write_still=True)
    print("RENDERED", act.name)
