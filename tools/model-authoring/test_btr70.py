"""Regression, geometry, budget, and determinism checks for the procedural BTR-70 model."""

import hashlib
import json
import math
from pathlib import Path
import sys
import tempfile
import unittest

import numpy as np

_THIS_DIR = Path(__file__).resolve().parent
if str(_THIS_DIR) not in sys.path:
    sys.path.insert(0, str(_THIS_DIR))

import build_btr70 as btr70  # noqa: E402
from vehicle_kit import GltfModel, emit_motion_parts, surface_heights, surface_hit  # noqa: E402

ASSETS_DIR = _THIS_DIR.parents[1] / "src/Game.Godot/Assets/Vehicles"


def build_parts(lod=0):
    m = GltfModel("check")
    mats = btr70.materials(m)
    hull = m.node("Hull")
    turret = m.node("TurretYaw", btr70.TURRET_RING)
    pitch = m.node("GunPitch", btr70.GUN_TRUNNION_LOCAL, turret)
    recoil = m.node("BarrelRecoil", parent=pitch)
    btr70.build_hull(m, hull, mats, lod)
    btr70.build_turret(m, turret, mats, lod)
    btr70.build_gun(m, pitch, recoil, mats, lod)
    return m, hull, turret


def part_points(m, name, parent=None):
    nodes = m.data["nodes"]
    found = []
    for node_id, part_parent, _mat, points, *_ in m.parts:
        if nodes[node_id]["name"] == name and (parent is None or part_parent == parent):
            found.append(np.asarray(points) + np.asarray(nodes[node_id]["translation"]))
    return found


def read_accessor(m, index):
    accessor = m.data["accessors"][index]
    view = m.data["bufferViews"][accessor["bufferView"]]
    raw = bytes(m.binary[view["byteOffset"] : view["byteOffset"] + view["byteLength"]])
    width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[accessor["type"]]
    dtype = "<f4" if accessor["componentType"] == 5126 else "<u4"
    return np.frombuffer(raw, dtype=dtype).reshape(-1, width) if width > 1 else np.frombuffer(raw, dtype=dtype)


class Btr70GeometryTests(unittest.TestCase):
    def test_proportions_and_dimensions(self):
        manifest = btr70.generate_all()
        anchors = manifest["reference_anchors"]

        # Reference dimensions from factory drawings
        self.assertAlmostEqual(anchors["overall_length_meters"], 7.540, delta=0.01)
        self.assertAlmostEqual(anchors["width_over_hull_meters"], 2.800, delta=0.01)
        self.assertAlmostEqual(anchors["height_to_turret_top_meters"], 2.235, delta=0.02)
        self.assertAlmostEqual(anchors["ground_clearance_meters"], 0.475, delta=0.01)

        # 13.00-18 tires: diameter 1.12 m (radius 0.56 m)
        self.assertAlmostEqual(btr70.WHEEL_R, 0.560, delta=0.001)
        self.assertAlmostEqual(anchors["wheel_radius_meters"], 0.560, delta=0.001)

        # Verify world bounding box of generated LOD0
        lod0_path = ASSETS_DIR / "BTR70" / "btr70.glb"
        self.assertTrue(lod0_path.exists())
        data = lod0_path.read_bytes()
        json_len, _ = np.frombuffer(data[12:20], dtype="<u4")
        gltf = json.loads(data[20 : 20 + json_len].decode("utf-8"))

        nodes = gltf["nodes"]
        parent_of = {}
        for i, n in enumerate(nodes):
            for c in n.get("children", []):
                parent_of[c] = i

        def get_world_pos(nid):
            cur = nid
            pos = [0.0, 0.0, 0.0]
            while cur is not None:
                t = nodes[cur].get("translation")
                if t:
                    for k in range(3):
                        pos[k] += t[k]
                cur = parent_of.get(cur)
            return pos

        w_min = [float("inf")] * 3
        w_max = [-float("inf")] * 3
        for nid, n in enumerate(nodes):
            if "mesh" not in n:
                continue
            mesh = gltf["meshes"][n["mesh"]]
            w_offset = get_world_pos(nid)
            for prim in mesh["primitives"]:
                acc = gltf["accessors"][prim["attributes"]["POSITION"]]
                for k in range(3):
                    w_min[k] = min(w_min[k], acc["min"][k] + w_offset[k])
                    w_max[k] = max(w_max[k], acc["max"][k] + w_offset[k])

        overall_width = w_max[0] - w_min[0]
        overall_length = w_max[2] - w_min[2]
        self.assertAlmostEqual(overall_width, 2.800, delta=0.01, msg=f"Width {overall_width} must be ~2.80 m")
        self.assertAlmostEqual(overall_length, 7.540, delta=0.01, msg=f"Length {overall_length} must be ~7.54 m")
        self.assertAlmostEqual(w_min[1], 0.0, delta=0.01, msg="Bottom of tires must touch ground Y=0")

    def test_axle_positions_and_spacing(self):
        z = btr70.WHEEL_Z
        self.assertEqual(len(z), 4)

        # Spacings: about 1.39 / 1.72 / 1.37 m
        s12 = z[1] - z[0]
        s23 = z[2] - z[1]
        s34 = z[3] - z[2]
        self.assertAlmostEqual(s12, 1.385, delta=0.02, msg="Axle 1-2 spacing ~1.39 m")
        self.assertAlmostEqual(s23, 1.715, delta=0.02, msg="Axle 2-3 spacing ~1.72 m")
        self.assertAlmostEqual(s34, 1.370, delta=0.02, msg="Axle 3-4 spacing ~1.37 m")

        # Track gauge: 2.40 m
        self.assertAlmostEqual(btr70.TRACK_GAUGE, 2.400, delta=0.001)
        self.assertAlmostEqual(btr70.WHEEL_X_CENTER, 1.200, delta=0.001)

    def test_roof_has_no_stowage_boxes_or_barrels(self):
        m, hull, _turret = build_parts(0)
        forbidden_names = ("StowageBox", "RolledTarp", "RearRack", "BoxStrap", "TarpStrap", "Barrel")
        for node_id, part_parent, _mat, points, *_ in m.parts:
            if part_parent == hull:
                part_name = m.data["nodes"][node_id]["name"]
                for forbidden in forbidden_names:
                    self.assertNotIn(forbidden, part_name, f"Roof stowage part {part_name} must not exist on hull")

    def test_hull_and_turret_seating(self):
        m, hull, turret = build_parts(0)
        for points in part_points(m, "PeriscopeHood", hull):
            low, _ = surface_heights(m, hull, (points[:, 0].mean(), points[:, 2].mean()), 0.03, btr70.HULL_SURFACE)
            self.assertLess(points[:, 1].min(), low + 0.01, "PeriscopeHood floats above HullBody")

        (points,) = part_points(m, "TurretHatch", turret)
        low, _ = surface_heights(m, turret, (points[:, 0].mean(), points[:, 2].mean()), 0.03, btr70.TURRET_SURFACE)
        self.assertLess(points[:, 1].min(), low, "TurretHatch floats above TurretCone")

    def test_markings_lie_flush_on_sloped_hull_sides(self):
        m = GltfModel("check")
        mats = btr70.materials(m)
        hull = m.node("Hull")
        btr70.build_hull(m, hull, mats, 0)
        btr70.build_markings(m, hull, lod=0)
        accessor_ids = [
            mesh["primitives"][0]["attributes"]["POSITION"]
            for mesh in m.data["meshes"]
            if mesh["name"] == "Marking"
        ]
        self.assertEqual(len(accessor_ids), 4)
        for accessor_id in accessor_ids:
            pts = read_accessor(m, accessor_id)
            for x, y, z in pts:
                sign = 1.0 if x > 0 else -1.0
                hit = surface_hit(m, hull, (sign * 2.0, float(y), float(z)), (-sign, 0.0, 0.0), btr70.HULL_SURFACE)
                self.assertIsNotNone(hit)
                (hx, hy, hz), _ = hit
                dist = math.dist((x, y, z), (hx, hy, hz))
                self.assertLess(dist, 0.015, f"Marking vertex {(x, y, z)} is {dist:.4f} m off HullBody")

    def test_wheel_motion_export_and_wrap_period(self):
        m = GltfModel("check")
        mats = btr70.materials(m)
        side = m.node("LeftTrack")
        btr70.build_running_gear(m, side, -1, mats, 0)
        emit_motion_parts(m)
        self.assertEqual(len(m.parts), 0, "All parts on LeftTrack should be exported as motion meshes")

        wheel_vertices = 0
        for mesh in m.data["meshes"]:
            attrs = mesh["primitives"][0]["attributes"]
            pos = read_accessor(m, attrs["POSITION"])
            uv0 = read_accessor(m, attrs["TEXCOORD_0"])
            uv1 = read_accessor(m, attrs["TEXCOORD_1"])
            self.assertTrue(np.allclose(uv1[:, 1], 0.0))
            self.assertTrue(np.allclose(uv1[:, 0], btr70.WHEEL_R))
            reach = np.linalg.norm(pos[:, 1:] - uv0, axis=1)
            self.assertTrue(np.all(reach <= btr70.WHEEL_R + 0.04))
            wheel_vertices += len(pos)
        self.assertGreater(wheel_vertices, 0)

        wrap = btr70.wheel_wrap_period(btr70.WHEEL_R)
        turns = wrap / (math.tau * btr70.WHEEL_R)
        self.assertAlmostEqual(turns, round(turns), places=9)
        self.assertLess(abs(wrap - 1000.0), 5.0, "Wrap period must be close to 1000 m")

    def test_triangle_budgets_and_monotonicity(self):
        manifest = btr70.generate_all()
        tris = [item["triangles"] for item in manifest["lods"]]
        self.assertEqual(len(tris), 3)

        # Budgets: LOD0 <= 12k, LOD1 <= 6k, LOD2 <= 2k
        self.assertLessEqual(tris[0], 12000, f"LOD0 has {tris[0]} triangles; budget is <= 12k")
        self.assertLessEqual(tris[1], 6000, f"LOD1 has {tris[1]} triangles; budget is <= 6k")
        self.assertLessEqual(tris[2], 2000, f"LOD2 has {tris[2]} triangles; budget is <= 2k")

        # Monotonicity
        self.assertGreater(tris[0], tris[1])
        self.assertGreater(tris[1], tris[2])

    def test_generation_determinism(self):
        with tempfile.TemporaryDirectory() as tmp1, tempfile.TemporaryDirectory() as tmp2:
            p1, p2 = Path(tmp1), Path(tmp2)
            btr70.generate_all(p1)
            btr70.generate_all(p2)

            for name in ("btr70.glb", "btr70_lod1.glb", "btr70_lod2.glb", "manifest.json"):
                h1 = hashlib.sha256((p1 / name).read_bytes()).hexdigest()
                h2 = hashlib.sha256((p2 / name).read_bytes()).hexdigest()
                self.assertEqual(h1, h2, f"{name} generation is not byte-deterministic across runs")

    def test_manifest_matches_visuals_json(self):
        visuals_path = ASSETS_DIR / "visuals.json"
        if not visuals_path.exists():
            self.skipTest("visuals.json not yet written")

        visuals = json.loads(visuals_path.read_text())
        manifest = json.loads((ASSETS_DIR / "BTR70" / "manifest.json").read_text())

        recon_factions = visuals.get("recon_vehicle", {}).get("factions", {})
        self.assertIn("soviet", recon_factions)
        soviet_recon = recon_factions["soviet"]

        self.assertAlmostEqual(soviet_recon["track_wrap"], manifest["track_wrap_meters"], places=6)
        self.assertAlmostEqual(soviet_recon["track_gauge"], manifest["track_gauge_meters"], places=3)


if __name__ == "__main__":
    unittest.main()
