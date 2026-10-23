"""Eevee renders of the low-poly soldier on a light grey studio backdrop, like a character sheet.

Usage: blender --background --python preview_stills.py -- in.blend out_prefix view[,view...]
Views: front left back right hero face rifle
"""
import math
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
bpy.ops.wm.open_mainfile(filepath=argv[0])
prefix, views = argv[1], argv[2].split(",")
scene = bpy.context.scene
for engine in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE"):
    try:
        scene.render.engine = engine
        break
    except TypeError:
        pass
scene.render.resolution_x = 800
scene.render.resolution_y = 1200
scene.view_settings.view_transform = "AgX"
looks = [i.identifier for i in scene.view_settings.bl_rna.properties["look"].enum_items]
if "AgX - Medium High Contrast" in looks:
    scene.view_settings.look = "AgX - Medium High Contrast"
for attr, val in (("use_raytracing", True), ("use_shadows", True)):
    try:
        setattr(scene.eevee, attr, val)
    except AttributeError:
        pass
world = bpy.data.worlds.new("World")
scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.66, 0.65, 0.62, 1)
world.node_tree.nodes["Background"].inputs[1].default_value = 0.9
mat = bpy.data.materials.new("Floor")
mat.use_nodes = True
mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.5, 0.5, 0.48, 1)
mat.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 1.0
bpy.ops.mesh.primitive_plane_add(size=30)
bpy.context.active_object.data.materials.append(mat)
for name, energy, rot, ang in (("Key", 3.6, (48, 0, 205), 6), ("Fill", 0.8, (60, 0, 120), 25), ("Rim", 2.2, (50, 0, 20), 8)):
    d = bpy.data.lights.new(name, "SUN")
    d.energy = energy
    d.color = {"Key": (1.0, 0.92, 0.82), "Fill": (0.85, 0.9, 1.0), "Rim": (0.9, 0.95, 1.0)}[name]
    d.angle = math.radians(ang)
    o = bpy.data.objects.new(name, d)
    scene.collection.objects.link(o)
    o.rotation_euler = [math.radians(a) for a in rot]


def shoot(name, loc, tgt, lens=85):
    cd = bpy.data.cameras.new(name)
    cd.lens = lens
    cam = bpy.data.objects.new(name, cd)
    scene.collection.objects.link(cam)
    cam.location = loc
    cam.rotation_euler = (Vector(tgt) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()
    scene.camera = cam
    scene.render.filepath = "%s_%s.png" % (prefix, name)
    bpy.ops.render.render(write_still=True)


c = Vector((0, 0, 0.9))
D = 7.0
for v in views:
    if v == "front":
        shoot(v, (0, D, 0.95), c)
    elif v == "back":
        shoot(v, (0, -D, 0.95), c)
    elif v == "left":
        shoot(v, (-D, 0, 0.95), c)
    elif v == "right":
        shoot(v, (D, 0, 0.95), c)
    elif v == "hero":
        shoot(v, (3.2, 5.6, 1.6), c)
    elif v == "face":
        shoot(v, (0.6, 1.6, 1.65), Vector((0, 0, 1.55)), lens=85)
    elif v == "riflefront":
        shoot(v, (0.0, 1.5, 1.0), Vector((0, 0, 0.95)), lens=45)
    elif v == "packback":
        shoot(v, (0.4, -1.6, 1.35), Vector((0, 0, 1.2)), lens=45)
    elif v == "packside":
        shoot(v, (1.6, -0.2, 1.3), Vector((0, 0, 1.2)), lens=45)
    elif v == "shins":
        shoot(v, (0.9, 1.0, 0.45), Vector((0, 0, 0.3)), lens=55)
    elif v == "shinsback":
        shoot(v, (-0.6, -1.2, 0.45), Vector((0, 0, 0.3)), lens=55)
    elif v == "armr":
        shoot(v, (0.9, 0.9, 1.25), Vector((0.2, 0.05, 1.15)), lens=60)
    elif v == "arml":
        shoot(v, (-1.0, 0.8, 1.25), Vector((-0.2, 0.05, 1.15)), lens=60)
    elif v == "heels":
        shoot(v, (0.7, -0.9, 0.25), Vector((0, 0, 0.1)), lens=60)
    elif v == "bootside":
        shoot(v, (1.1, 0.2, 0.2), Vector((0, 0, 0.12)), lens=60)
    elif v == "hands":
        shoot(v, (0.25, 1.0, 1.15), Vector((0.0, 0.15, 1.08)), lens=55)
    elif v == "strap":
        shoot(v, (0.45, 0.55, 1.62), Vector((0, 0.03, 1.6)), lens=70)
    elif v == "boots":
        shoot(v, (0.7, 1.4, 0.35), Vector((0, 0, 0.15)), lens=70)
    elif v == "rifle":
        shoot(v, (0.4, 1.8, 1.2), Vector((0, 0.15, 0.95)), lens=70)
