"""Reusable glTF 2.0 geometry authoring utilities for Dead Ground vehicles.

Supports selective smooth and flat normals, functional node hierarchies,
PBR metallic-roughness materials, and static part combining by parent node.
"""

import json
import math
import struct
from pathlib import Path


def vec3_cross(u, v):
    return (
        u[1] * v[2] - u[2] * v[1],
        u[2] * v[0] - u[0] * v[2],
        u[0] * v[1] - u[1] * v[0],
    )


def vec3_normalize(v):
    length = math.sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2])
    if length <= 1e-9:
        return (0.0, 1.0, 0.0)
    inv = 1.0 / length
    return (v[0] * inv, v[1] * inv, v[2] * inv)


def vec3_add(a, b):
    return (a[0] + b[0], a[1] + b[1], a[2] + b[2])


def vec3_sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


class GltfModel:
    def __init__(self, name="Vehicle", surface_weathering=False):
        self.name = name
        self.surface_weathering = surface_weathering
        self.data = {
            "asset": {
                "version": "2.0",
                "generator": "Dead Ground T-62 model authoring",
            },
            "scene": 0,
            "scenes": [{"nodes": [0]}],
            "nodes": [{"name": name, "children": []}],
            "meshes": [],
            "materials": [],
            "accessors": [],
            "bufferViews": [],
        }
        self.binary = bytearray()
        self.parts = []  # (node_id, parent, material, points, normals, indices, colors)
        self.triangles = 0
        self.vertices_count = 0

    def material(self, name, color, metal=0.0, rough=0.8, double_sided=False):
        # Author colors as sRGB hex; glTF factors store linear RGB.
        if isinstance(color, str):
            value = color.lstrip("#")
            srgb = [int(value[i : i + 2], 16) / 255 for i in (0, 2, 4)]
            color = [
                c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4
                for c in srgb
            ]
        mat = {
            "name": name,
            "pbrMetallicRoughness": {
                "baseColorFactor": [*color, 1.0],
                "metallicFactor": float(metal),
                "roughnessFactor": float(rough),
            },
        }
        if double_sided:
            mat["doubleSided"] = True
        self.data["materials"].append(mat)
        return len(self.data["materials"]) - 1

    def node(self, name, position=(0.0, 0.0, 0.0), parent=0):
        index = len(self.data["nodes"])
        self.data["nodes"].append(
            {
                "name": name,
                "translation": [float(p) for p in position],
                "children": [],
            }
        )
        self.data["nodes"][parent]["children"].append(index)
        return index

    def accessor(self, values, width, kind, target):
        while len(self.binary) % 4 != 0:
            self.binary.append(0)
        offset = len(self.binary)
        flat = [v for row in values for v in row] if width > 1 else values
        code = "f" if kind == 5126 else "I"
        self.binary += struct.pack("<" + code * len(flat), *flat)
        view = len(self.data["bufferViews"])
        self.data["bufferViews"].append(
            {
                "buffer": 0,
                "byteOffset": offset,
                "byteLength": len(self.binary) - offset,
                "target": target,
            }
        )
        item = {
            "bufferView": view,
            "componentType": kind,
            "count": len(values),
            "type": {1: "SCALAR", 3: "VEC3", 4: "VEC4"}[width],
        }
        if width == 3 and values:
            item["min"] = [min(v[i] for v in values) for i in range(3)]
            item["max"] = [max(v[i] for v in values) for i in range(3)]
        self.data["accessors"].append(item)
        return len(self.data["accessors"]) - 1

    def add_mesh_direct(
        self,
        name,
        points,
        normals,
        indices,
        material,
        parent=0,
        position=(0, 0, 0),
        tint=1.0,
    ):
        """Add precomputed points, normals and triangle indices directly."""
        # Match glTF CCW winding to the supplied outward normals for every axis.
        indices = list(indices)
        for offset in range(0, len(indices), 3):
            a, b, c = indices[offset : offset + 3]
            face = vec3_cross(
                vec3_sub(points[b], points[a]), vec3_sub(points[c], points[a])
            )
            normal = tuple(
                normals[a][axis] + normals[b][axis] + normals[c][axis]
                for axis in range(3)
            )
            if sum(face[axis] * normal[axis] for axis in range(3)) < 0:
                indices[offset + 1], indices[offset + 2] = c, b
        node_id = self.node(name, position, parent)
        self.triangles += len(indices) // 3
        self.vertices_count += len(points)
        self.parts.append(
            (
                node_id,
                parent,
                material,
                points,
                normals,
                indices,
                self.surface_tints(
                    name, points, normals, position, parent, tint, material
                ),
            )
        )
        return node_id

    def surface_tints(self, name, points, normals, position, parent, tint, material):
        """Bake broad dust masks into colors; fine surface detail is sampled at import."""
        if not self.surface_weathering:
            return [(tint, tint, tint, 1.0)] * len(points)
        origin = list(position)
        cursor = parent
        while cursor != 0:
            node = self.data["nodes"][cursor]
            for axis in range(3):
                origin[axis] += node.get("translation", (0, 0, 0))[axis]
            cursor = next(
                (
                    i
                    for i, n in enumerate(self.data["nodes"])
                    if cursor in n.get("children", [])
                ),
                0,
            )
        phase = sum((i + 1) * ord(c) for i, c in enumerate(name)) * 0.013
        kind = self.data["materials"][material]["name"]
        result = []
        for point, normal in zip(points, normals):
            x, y, z = (point[i] + origin[i] for i in range(3))
            patch = 0.5 + 0.5 * math.sin(z * 4.1 + phase) * math.sin(x * 3.7 + y * 5.3)
            dust = max(0.0, min(1.0, (1.10 - y) / 0.85)) * (0.20 + 0.15 * patch)
            tone = tint * (0.94 + 0.06 * patch)
            if kind in ("T62_Optics", "T62_Timber") or name == "MuzzleBore":
                result.append((tint, tint, tint, 1.0))
                continue
            # Warm ochre dust follows height, without making the tires pitch black.
            target = (0.94, 0.83, 0.62)
            result.append(
                tuple(
                    min(1.0, max(0.0, tone * (1 - dust) + target[i] * dust))
                    for i in range(3)
                )
                + (1.0,)
            )
        return result

    def add_flat_mesh(
        self,
        name,
        vertices,
        faces,
        material,
        parent=0,
        position=(0, 0, 0),
        auto_orient=False,
        tint=1.0,
    ):
        """Add polygonal faces with hard face normals (armor plates, boxes)."""
        points, normals, indices = [], [], []
        center = (
            [sum(p[i] for p in vertices) / len(vertices) for i in range(3)]
            if auto_orient
            else None
        )
        for face in faces:
            face = list(face)
            a, b, c = [vertices[i] for i in face[:3]]
            u = vec3_sub(b, a)
            v = vec3_sub(c, a)
            n = vec3_cross(u, v)
            if auto_orient and center is not None:
                fc = [sum(vertices[j][i] for j in face) / len(face) for i in range(3)]
                if sum(n[i] * (fc[i] - center[i]) for i in range(3)) < 0:
                    face.reverse()
                    n = (-n[0], -n[1], -n[2])
            n = vec3_normalize(n)
            start = len(points)
            points.extend(vertices[i] for i in face)
            normals.extend([n] * len(face))
            for i in range(1, len(face) - 1):
                indices.extend([start, start + i, start + i + 1])
        return self.add_mesh_direct(
            name, points, normals, indices, material, parent, position, tint=tint
        )

    def add_quad(self, name, p0, p1, p2, p3, mat, parent=0, pos=(0, 0, 0)):
        """Add a single quad (p0, p1, p2, p3) in CCW winding order."""
        u = vec3_sub(p1, p0)
        v = vec3_sub(p3, p0)
        n = vec3_normalize(vec3_cross(u, v))
        points = [p0, p1, p2, p3]
        normals = [n, n, n, n]
        indices = [0, 1, 2, 0, 2, 3]
        return self.add_mesh_direct(name, points, normals, indices, mat, parent, pos)

    def add_extruded_track(
        self, name, outer_pts, inner_pts, x_inner, x_outer, mat, parent=0
    ):
        """
        Extrude a solid 3D closed track belt with outer tread, inner wheel run,
        and both outer and inner sidewalls (showing track shoe thickness).
        outer_pts: list of (z, y)
        inner_pts: list of (z, y)
        """
        points, normals, indices = [], [], []
        n_pts = len(outer_pts)

        for i in range(n_pts):
            nxt = (i + 1) % n_pts
            z_o0, y_o0 = outer_pts[i]
            z_o1, y_o1 = outer_pts[nxt]
            z_i0, y_i0 = inner_pts[i]
            z_i1, y_i1 = inner_pts[nxt]

            # 4 vertices on outer tread:
            v_o_in0 = (x_inner, y_o0, z_o0)
            v_o_out0 = (x_outer, y_o0, z_o0)
            v_o_out1 = (x_outer, y_o1, z_o1)
            v_o_in1 = (x_inner, y_o1, z_o1)

            # 4 vertices on inner track run:
            v_i_in0 = (x_inner, y_i0, z_i0)
            v_i_out0 = (x_outer, y_i0, z_i0)
            v_i_out1 = (x_outer, y_i1, z_i1)
            v_i_in1 = (x_inner, y_i1, z_i1)

            # 1. Outer tread surface (facing ground/outward)
            u_out = vec3_sub(v_o_out0, v_o_in0)
            v_out = vec3_sub(v_o_in1, v_o_in0)
            n_out = vec3_normalize(vec3_cross(u_out, v_out))
            idx = len(points)
            points.extend([v_o_in0, v_o_out0, v_o_out1, v_o_in1])
            normals.extend([n_out, n_out, n_out, n_out])
            indices.extend([idx, idx + 1, idx + 2, idx, idx + 2, idx + 3])

            # 2. Inner track surface (facing road wheels)
            u_in = vec3_sub(v_i_in1, v_i_in0)
            v_in = vec3_sub(v_i_out0, v_i_in0)
            n_in = vec3_normalize(vec3_cross(u_in, v_in))
            idx = len(points)
            points.extend([v_i_in0, v_i_in1, v_i_out1, v_i_out0])
            normals.extend([n_in, n_in, n_in, n_in])
            indices.extend([idx, idx + 1, idx + 2, idx, idx + 2, idx + 3])

            # 3. Outer sidewall (visible from outside of vehicle)
            is_pos_x = x_outer > 0
            n_side_out = (1.0, 0.0, 0.0) if is_pos_x else (-1.0, 0.0, 0.0)
            idx = len(points)
            if is_pos_x:
                # Looking from +X: +Z is right, +Y is up. CCW winding:
                points.extend([v_o_out0, v_i_out0, v_i_out1, v_o_out1])
            else:
                # Looking from -X: -Z is right, +Y is up. CCW winding:
                points.extend([v_o_out0, v_o_out1, v_i_out1, v_i_out0])
            normals.extend([n_side_out, n_side_out, n_side_out, n_side_out])
            indices.extend([idx, idx + 1, idx + 2, idx, idx + 2, idx + 3])

            # 4. Inner sidewall (facing hull tub)
            n_side_in = (-1.0, 0.0, 0.0) if is_pos_x else (1.0, 0.0, 0.0)
            idx = len(points)
            if is_pos_x:
                points.extend([v_o_in0, v_o_in1, v_i_in1, v_i_in0])
            else:
                points.extend([v_o_in0, v_i_in0, v_i_in1, v_o_in1])
            normals.extend([n_side_in, n_side_in, n_side_in, n_side_in])
            indices.extend([idx, idx + 1, idx + 2, idx, idx + 2, idx + 3])

        return self.add_mesh_direct(name, points, normals, indices, mat, parent)

    def add_box(self, name, size, pos, mat, parent=0, tint=1.0):
        x, y, z = [s / 2.0 for s in size]
        verts = [
            (a * x, b * y, c * z)
            for a, b, c in [
                (-1, -1, -1),
                (1, -1, -1),
                (1, -1, 1),
                (-1, -1, 1),
                (-1, 1, -1),
                (1, 1, -1),
                (1, 1, 1),
                (-1, 1, 1),
            ]
        ]
        faces = [
            (0, 1, 2, 3),
            (4, 7, 6, 5),
            (0, 4, 5, 1),
            (1, 5, 6, 2),
            (2, 6, 7, 3),
            (3, 7, 4, 0),
        ]
        return self.add_flat_mesh(name, verts, faces, mat, parent, pos, tint=tint)

    def add_cylinder(
        self,
        name,
        radius,
        length,
        pos,
        mat,
        axis="y",
        sides=16,
        smooth=True,
        parent=0,
        caps="both",
    ):
        """Add cylinder with smooth lateral normals and flat end caps."""
        points, normals, indices = [], [], []
        half = length / 2.0

        # Lateral surface
        start_lat = len(points)
        for i in range(sides):
            angle = i * math.tau / sides
            ca = math.cos(angle)
            sa = math.sin(angle)

            if axis == "y":
                p0 = (radius * ca, -half, radius * sa)
                p1 = (radius * ca, half, radius * sa)
                n = (ca, 0.0, sa)
            elif axis == "z":
                p0 = (radius * ca, radius * sa, -half)
                p1 = (radius * ca, radius * sa, half)
                n = (ca, sa, 0.0)
            else:  # "x"
                p0 = (-half, radius * ca, radius * sa)
                p1 = (half, radius * ca, radius * sa)
                n = (0.0, ca, sa)

            points.extend([p0, p1])
            normals.extend([n, n])

        for i in range(sides):
            nxt = (i + 1) % sides
            i0 = start_lat + i * 2
            i1 = start_lat + i * 2 + 1
            i2 = start_lat + nxt * 2 + 1
            i3 = start_lat + nxt * 2
            indices.extend([i0, i1, i2, i0, i2, i3])

        # Caps
        if caps == "both":
            cap_dirs = (-1.0, 1.0)
        elif caps in ("pos", 1, 1.0, "+"):
            cap_dirs = (1.0,)
        elif caps in ("neg", -1, -1.0, "-"):
            cap_dirs = (-1.0,)
        else:
            cap_dirs = ()

        for cap_dir in cap_dirs:
            cap_start = len(points)
            cap_n = (
                (0.0, cap_dir, 0.0)
                if axis == "y"
                else ((0.0, 0.0, cap_dir) if axis == "z" else (cap_dir, 0.0, 0.0))
            )
            # Center vertex
            center_p = (
                (0.0, cap_dir * half, 0.0)
                if axis == "y"
                else (
                    (0.0, 0.0, cap_dir * half)
                    if axis == "z"
                    else (cap_dir * half, 0.0, 0.0)
                )
            )
            points.append(center_p)
            normals.append(cap_n)

            for i in range(sides):
                angle = i * math.tau / sides
                ca = math.cos(angle)
                sa = math.sin(angle)
                if axis == "y":
                    p = (radius * ca, cap_dir * half, radius * sa)
                elif axis == "z":
                    p = (radius * ca, radius * sa, cap_dir * half)
                else:
                    p = (cap_dir * half, radius * ca, radius * sa)
                points.append(p)
                normals.append(cap_n)

            for i in range(sides):
                nxt = (i + 1) % sides
                if cap_dir > 0:
                    indices.extend([cap_start, cap_start + 1 + i, cap_start + 1 + nxt])
                else:
                    indices.extend([cap_start, cap_start + 1 + nxt, cap_start + 1 + i])

        if not smooth:
            flat_points, flat_normals = [], []
            for offset in range(0, len(indices), 3):
                triangle = indices[offset : offset + 3]
                a, b, c = [points[index] for index in triangle]
                normal = vec3_cross(vec3_sub(b, a), vec3_sub(c, a))
                outward = tuple(
                    sum(normals[index][axis] for index in triangle) for axis in range(3)
                )
                if sum(normal[axis] * outward[axis] for axis in range(3)) < 0:
                    triangle.reverse()
                    normal = tuple(-v for v in normal)
                flat_points.extend(points[index] for index in triangle)
                flat_normals.extend([vec3_normalize(normal)] * 3)
            points, normals, indices = (
                flat_points,
                flat_normals,
                list(range(len(flat_points))),
            )
        return self.add_mesh_direct(name, points, normals, indices, mat, parent, pos)

    def add_disc(
        self, name, radius, pos, mat, axis="x", sides=12, parent=0, normal_dir=1.0
    ):
        """Add a flat circular disc with given normal direction."""
        points, normals, indices = [], [], []
        center_idx = 0
        norm = (
            (normal_dir, 0.0, 0.0)
            if axis == "x"
            else ((0.0, normal_dir, 0.0) if axis == "y" else (0.0, 0.0, normal_dir))
        )
        points.append((0.0, 0.0, 0.0))
        normals.append(norm)

        for i in range(sides):
            angle = i * math.tau / sides
            ca = math.cos(angle)
            sa = math.sin(angle)
            if axis == "x":
                p = (0.0, radius * ca, radius * sa)
            elif axis == "y":
                p = (radius * ca, 0.0, radius * sa)
            else:
                p = (radius * ca, radius * sa, 0.0)
            points.append(p)
            normals.append(norm)

        for i in range(sides):
            nxt = (i + 1) % sides
            if normal_dir > 0:
                indices.extend([center_idx, 1 + i, 1 + nxt])
            else:
                indices.extend([center_idx, 1 + nxt, 1 + i])

        return self.add_mesh_direct(name, points, normals, indices, mat, parent, pos)

    def combine_static_parts(self):
        """Merge static geometry sharing (parent, material) while preserving functional hierarchy."""
        groups = {}
        # Collect static parts
        for node_id, parent, material, points, normals, indices, colors in self.parts:
            group = groups.setdefault((parent, material), ([], [], [], []))
            translation = self.data["nodes"][node_id]["translation"]
            offset = len(group[0])
            group[0].extend(
                tuple(point[i] + translation[i] for i in range(3)) for point in points
            )
            group[1].extend(normals)
            group[2].extend(index + offset for index in indices)
            group[3].extend(colors)
            if node_id in self.data["nodes"][parent]["children"]:
                self.data["nodes"][parent]["children"].remove(node_id)

        # Build merged primitives under each parent
        for (parent, material), (points, normals, indices, colors) in groups.items():
            if not points or not indices:
                continue
            parent_name = self.data["nodes"][parent]["name"]
            mat_name = self.data["materials"][material]["name"]
            mesh_name = f"{parent_name}_{mat_name}"
            primitive = {
                "attributes": {
                    "POSITION": self.accessor(points, 3, 5126, 34962),
                    "NORMAL": self.accessor(normals, 3, 5126, 34962),
                    "COLOR_0": self.accessor(colors, 4, 5126, 34962),
                },
                "indices": self.accessor(indices, 1, 5125, 34963),
                "material": material,
            }
            mesh_id = len(self.data["meshes"])
            self.data["meshes"].append({"name": mesh_name, "primitives": [primitive]})
            node_id = self.node(mesh_name, parent=parent)
            self.data["nodes"][node_id]["mesh"] = mesh_id

        # Remove unused palette entries, keeping generated metadata truthful.
        used_materials = sorted(
            {
                primitive["material"]
                for mesh in self.data["meshes"]
                for primitive in mesh["primitives"]
            }
        )
        material_map = {old: new for new, old in enumerate(used_materials)}
        self.data["materials"] = [self.data["materials"][old] for old in used_materials]
        for mesh in self.data["meshes"]:
            for primitive in mesh["primitives"]:
                primitive["material"] = material_map[primitive["material"]]

        # Clean unreachable nodes and compact node array
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

    def save(self, filepath):
        path = Path(filepath)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.combine_static_parts()
        self.data["buffers"] = [{"byteLength": len(self.binary)}]
        encoded = json.dumps(self.data, separators=(",", ":")).encode("utf-8")
        encoded += b" " * (-len(encoded) % 4)
        self.binary += b"\0" * (-len(self.binary) % 4)
        total = 12 + 8 + len(encoded) + 8 + len(self.binary)
        blob = struct.pack("<III", 0x46546C67, 2, total)
        blob += struct.pack("<II", len(encoded), 0x4E4F534A) + encoded
        blob += struct.pack("<II", len(self.binary), 0x004E4942) + self.binary
        path.write_bytes(blob)
        return {
            "file": path.name,
            "triangles": self.triangles,
            "vertices": self.vertices_count,
            "mesh_parts": len(self.data["meshes"]),
            "materials": len(self.data["materials"]),
            "bytes": len(blob),
        }
