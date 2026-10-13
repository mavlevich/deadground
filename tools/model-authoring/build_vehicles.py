"""Create original, deliberately exaggerated RTS vehicle blockouts as glTF 2.0."""
import json
import math
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "src/Game.Godot/Assets/Vehicles"
ROOT.mkdir(parents=True, exist_ok=True)


class Model:
    def __init__(self, name):
        self.data = {"asset": {"version": "2.0", "generator": "Dead Ground silhouette study"},
                     "scene": 0, "scenes": [{"nodes": [0]}], "nodes": [{"name": name, "children": []}],
                     "meshes": [], "materials": [], "accessors": [], "bufferViews": []}
        self.binary = bytearray()
        self.triangles = 0
        self.parts = []

    def material(self, name, color, metal=0, rough=0.8):
        self.data["materials"].append({"name": name, "pbrMetallicRoughness": {
            "baseColorFactor": [*color, 1], "metallicFactor": metal, "roughnessFactor": rough}})
        return len(self.data["materials"]) - 1

    def node(self, name, position=(0, 0, 0), parent=0):
        index = len(self.data["nodes"])
        self.data["nodes"].append({"name": name, "translation": list(position), "children": []})
        self.data["nodes"][parent]["children"].append(index)
        return index

    def accessor(self, values, width, kind, target):
        while len(self.binary) % 4:
            self.binary.append(0)
        offset = len(self.binary)
        flat = [v for row in values for v in row] if width > 1 else values
        code = "f" if kind == 5126 else "I"
        self.binary += struct.pack("<" + code * len(flat), *flat)
        view = len(self.data["bufferViews"])
        self.data["bufferViews"].append({"buffer": 0, "byteOffset": offset,
                                         "byteLength": len(self.binary) - offset, "target": target})
        item = {"bufferView": view, "componentType": kind, "count": len(values),
                "type": {1: "SCALAR", 3: "VEC3"}[width]}
        if width == 3:
            item["min"] = [min(v[i] for v in values) for i in range(3)]
            item["max"] = [max(v[i] for v in values) for i in range(3)]
        self.data["accessors"].append(item)
        return len(self.data["accessors"]) - 1

    def mesh(self, name, vertices, faces, material, parent=0, position=(0, 0, 0)):
        points, normals, indices = [], [], []
        center = [sum(p[i] for p in vertices) / len(vertices) for i in range(3)]
        for face in faces:
            face = list(face)
            a, b, c = [vertices[i] for i in face[:3]]
            u, v = [b[i] - a[i] for i in range(3)], [c[i] - a[i] for i in range(3)]
            n = [u[1]*v[2]-u[2]*v[1], u[2]*v[0]-u[0]*v[2], u[0]*v[1]-u[1]*v[0]]
            fc = [sum(vertices[j][i] for j in face) / len(face) for i in range(3)]
            if sum(n[i] * (fc[i] - center[i]) for i in range(3)) < 0:
                face.reverse()
                n = [-x for x in n]
            length = math.sqrt(sum(x*x for x in n))
            n = [x / length for x in n]
            start = len(points)
            points.extend(vertices[i] for i in face)
            normals.extend([n] * len(face))
            for i in range(1, len(face)-1):
                indices.extend([start, start+i, start+i+1])
        self.triangles += len(indices) // 3
        node_id = self.node(name, position, parent)
        self.parts.append((node_id, parent, material, points, normals, indices))
        return node_id

    def combine_static_parts(self):
        groups = {}
        for node_id, parent, material, points, normals, indices in self.parts:
            group = groups.setdefault((parent, material), ([], [], []))
            translation = self.data["nodes"][node_id]["translation"]
            offset = len(group[0])
            group[0].extend(tuple(point[i] + translation[i] for i in range(3)) for point in points)
            group[1].extend(normals)
            group[2].extend(index + offset for index in indices)
            self.data["nodes"][parent]["children"].remove(node_id)
        for (parent, material), (points, normals, indices) in groups.items():
            name = self.data["nodes"][parent]["name"] + "_" + self.data["materials"][material]["name"]
            primitive = {"attributes": {"POSITION": self.accessor(points, 3, 5126, 34962),
                                         "NORMAL": self.accessor(normals, 3, 5126, 34962)},
                         "indices": self.accessor(indices, 1, 5125, 34963), "material": material}
            mesh_id = len(self.data["meshes"])
            self.data["meshes"].append({"name": name, "primitives": [primitive]})
            node_id = self.node(name, parent=parent)
            self.data["nodes"][node_id]["mesh"] = mesh_id
        reachable = []
        def visit(index):
            reachable.append(index)
            for child in self.data["nodes"][index]["children"]:
                visit(child)
        visit(0)
        remap = {old: new for new, old in enumerate(reachable)}
        nodes = [self.data["nodes"][old] for old in reachable]
        for node in nodes:
            node["children"] = [remap[child] for child in node["children"]]
        self.data["nodes"] = nodes

    def box(self, name, size, pos, mat, parent=0):
        x, y, z = [s/2 for s in size]
        verts = [(a*x, b*y, c*z) for a,b,c in
                 [(-1,-1,-1),(1,-1,-1),(1,-1,1),(-1,-1,1),(-1,1,-1),(1,1,-1),(1,1,1),(-1,1,1)]]
        return self.mesh(name, verts, [(0,1,2,3),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)], mat, parent, pos)

    def cylinder(self, name, radius, length, pos, mat, axis="y", sides=12, parent=0):
        verts = []
        for end in (-length/2, length/2):
            for i in range(sides):
                a = i * math.tau / sides
                p = (radius*math.cos(a), end, radius*math.sin(a))
                verts.append(p if axis == "y" else ((p[1],p[0],p[2]) if axis == "x" else (p[0],p[2],p[1])))
        faces = [list(range(sides)), list(range(sides, sides*2))]
        faces += [(i,(i+1)%sides,(i+1)%sides+sides,i+sides) for i in range(sides)]
        return self.mesh(name, verts, faces, mat, parent, pos)

    def hull(self, name, lower, upper, y0, y1, mat, parent=0, pos=(0,0,0)):
        vertices = [(x,y0,z) for x,z in lower] + [(x,y1,z) for x,z in upper]
        return self.mesh(name, vertices, [(0,1,2,3),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)], mat,parent,pos)

    def save(self, name):
        self.combine_static_parts()
        self.data["buffers"] = [{"byteLength": len(self.binary)}]
        encoded = json.dumps(self.data, separators=(",", ":")).encode()
        encoded += b" " * (-len(encoded) % 4)
        self.binary += b"\0" * (-len(self.binary) % 4)
        total = 12 + 8 + len(encoded) + 8 + len(self.binary)
        blob = struct.pack("<III", 0x46546C67, 2, total)
        blob += struct.pack("<II",len(encoded),0x4E4F534A)+encoded
        blob += struct.pack("<II",len(self.binary),0x004E4942)+self.binary
        (ROOT / name).write_bytes(blob)
        return {"file": name, "triangles": self.triangles, "mesh_parts": len(self.data["meshes"]), "bytes": len(blob)}


def tank():
    m = Model("Tank")
    armor = m.material("Armor",(.20,.25,.15))
    upper = m.material("ArmorLight",(.31,.36,.21))
    dark = m.material("Tracks",(.055,.065,.05))
    wheel = m.material("RoadWheels",(.16,.19,.12))
    steel = m.material("Steel",(.13,.15,.12),.55,.65)
    marking = m.material("TeamMarkings",(.72,.58,.25))
    lamp = m.material("Headlights",(.65,.65,.43))
    m.hull("LowerHull",[(-1.24,-2.55),(1.24,-2.55),(1.24,2.6),(-1.24,2.6)],
           [(-1.42,-2.8),(1.42,-2.8),(1.42,2.7),(-1.42,2.7)],.6,1.38,armor)
    m.hull("SlopedUpperHull",[(-1.42,-2.8),(1.42,-2.8),(1.42,2.7),(-1.42,2.7)],
           [(-1.15,-1.8),(1.15,-1.8),(1.15,2.35),(-1.15,2.35)],1.38,1.85,upper)
    for s in (-1,1):
        # Polygonal track belt silhouette, with inset road wheels and visible tread pads.
        profile = [(-2.8,.42),(-2.4,.14),(2.45,.14),(2.85,.52),(2.85,1.05),(2.45,1.36),(-2.4,1.36),(-2.8,1.05)]
        verts = [(s*1.44,y,z) for z,y in profile] + [(s*1.94,y,z) for z,y in profile]
        faces = [list(range(8)),list(range(8,16))]+[(i,(i+1)%8,(i+1)%8+8,i+8) for i in range(8)]
        m.mesh(f"TrackBelt_{s}",verts,faces,dark)
        for i,z in enumerate([-2.1,-1.25,-.42,.42,1.25,2.1]):
            m.cylinder(f"RoadWheel_{s}_{i}",.46,.12,(s*1.96,.70,z),wheel,"x")
            m.cylinder(f"WheelHub_{s}_{i}",.16,.14,(s*2.02,.70,z),steel,"x",8)
        m.box(f"Fender_{s}",(.68,.12,5.5),(s*1.69,1.44,0),armor)
        for i in range(20):
            m.box(f"Tread_{s}_{i}",(.55,.055,.16),(s*1.7,1.38,-2.48+i*.26),steel)
        m.box(f"RearStorage_{s}",(.6,.42,1.25),(s*1.42,1.78,1.6),armor)
        m.box(f"Headlight_{s}",(.25,.16,.12),(s*1.04,1.6,-2.5),lamp)
    turret = m.node("TurretYaw",(0,1.9,-.35))
    m.hull("TurretArmor",[(-1.14,-1.2),(1.14,-1.2),(1.12,1.25),(-1.12,1.25)],
           [(-.8,-.80),(.8,-.80),(.84,.9),(-.84,.9)],0,.78,armor,turret)
    m.box("TurretRearRack",(1.65,.40,.38),(0,.32,1.32),steel,turret)
    m.cylinder("CommanderCupola",.38,.18,(.48,.84,.25),upper,parent=turret)
    m.cylinder("LoaderHatch",.30,.07,(-.50,.82,.10),steel,parent=turret)
    for s in (-1,1):
        m.box(f"TurretTeamPanel_{s}",(.035,.25,.62),(s*1.03,.35,.35),marking,turret)
    pitch = m.node("GunPitch",(0,.35,-1.05),turret)
    recoil = m.node("BarrelRecoil",parent=pitch)
    m.box("Mantlet",(.85,.56,.60),(0,0,-.14),upper,recoil)
    m.cylinder("GunBarrel",.105,3.1,(0,0,-1.85),steel,"z",12,recoil)
    m.cylinder("FumeExtractor",.16,.42,(0,0,-2.04),armor,"z",12,recoil)
    m.cylinder("MuzzleCollar",.14,.22,(0,0,-3.43),steel,"z",12,recoil)
    m.cylinder("MuzzleOpening",.092,.006,(0,0,-3.545),dark,"z",12,recoil)
    m.node("Muzzle",(0,0,-3.55),recoil)
    m.cylinder("RadioAntenna",.025,1.25,(-.75,1.34,.75),steel,parent=turret,sides=6)
    for i in range(8):
        m.box(f"EngineVent_{i}",(1.5,.035,.08),(0,1.87,1.2+i*.12),dark)
    return m.save("tank.glb")


def recon():
    m = Model("Recon")
    armor = m.material("Armor",(.27,.32,.20))
    top = m.material("Roof",(.38,.42,.27))
    tire = m.material("Tires",(.045,.055,.045))
    rim = m.material("WheelRims",(.19,.22,.14),.3)
    glass = m.material("ArmoredGlass",(.035,.12,.13),.3,.22)
    metal = m.material("SensorMetal",(.12,.15,.13),.4)
    marking = m.material("TeamMarkings",(.72,.58,.25))
    m.hull("BoatHull",[(-.76,-1.85),(.76,-1.85),(.76,2.1),(-.76,2.1)],
           [(-1.03,-2.45),(1.03,-2.45),(1.03,2.35),(-1.03,2.35)],.6,1.35,armor)
    m.hull("SlopedCabin",[(-1.03,-2.45),(1.03,-2.45),(1.03,2.35),(-1.03,2.35)],
           [(-.83,-1.2),(.83,-1.2),(.83,1.55),(-.83,1.55)],1.35,2.05,top)
    for s in (-1,1):
        for i,z in enumerate([-1.45,1.42]):
            m.cylinder(f"Wheel_{s}_{i}",.62,.34,(s*1.04,.62,z),tire,"x",16)
            m.cylinder(f"WheelRim_{s}_{i}",.36,.035,(s*1.22,.62,z),rim,"x",12)
            m.cylinder(f"Hub_{s}_{i}",.13,.05,(s*1.24,.62,z),metal,"x",8)
        m.box(f"Fender_{s}",(.36,.10,4.42),(s*1.02,1.36,0),armor)
        m.box(f"Door_{s}",(.025,.38,.83),(s*.90,1.70,-.05),armor)
        m.box(f"TeamPanel_{s}",(.035,.25,.55),(s*.95,1.5,.65),marking)
    # Window panels use the same slope as the front armor, not a heavy tank turret.
    for s in (-1,1):
        x0,x1 = sorted((s*.12,s*.72))
        m.mesh(f"Windshield_{s}",[(x0,1.63,-1.95),(x1,1.63,-1.95),(x1,1.95,-1.38),(x0,1.95,-1.38),
                                 (x0,1.63,-1.93),(x1,1.63,-1.93),(x1,1.95,-1.36),(x0,1.95,-1.36)],
               [(0,1,2,3),(4,5,6,7),(0,1,5,4),(1,2,6,5),(2,3,7,6),(3,0,4,7)],glass)
    m.box("FrontBumper",(1.9,.15,.20),(0,.9,-2.38),metal)
    for s in (-1,1):
        m.box(f"Headlight_{s}",(.24,.14,.035),(s*.72,1.32,-2.43),marking)
    sensor = m.node("SensorYaw",(0,2.07,.3))
    m.cylinder("SensorPedestal",.25,.24,(0,.12,0),metal,parent=sensor)
    m.box("OpticalHead",(.66,.38,.58),(0,.43,0),armor,sensor)
    m.box("OpticalLens",(.40,.17,.025),(0,.43,-.305),glass,sensor)
    m.cylinder("Mast",.04,1.5,(-.55,2.8,.95),metal,sides=6)
    m.box("MastCrossbar",(.8,.055,.055),(-.55,3.45,.95),metal)
    m.cylinder("WhipAntenna",.02,1.9,(.65,2.94,1.12),metal,sides=6)
    m.cylinder("RoofHatch",.32,.055,(.43,2.08,-.5),armor)
    for i in range(6):
        m.box(f"EngineLouver_{i}",(1.1,.035,.08),(0,2.08,.95+i*.10),metal)
    return m.save("recon.glb")


if __name__ == "__main__":
    report = {"units": "meters", "up": "+Y", "forward": "-Z", "models": [tank(), recon()]}
    (ROOT/"manifest.json").write_text(json.dumps(report,indent=2)+"\n")
    print(json.dumps(report,indent=2))
