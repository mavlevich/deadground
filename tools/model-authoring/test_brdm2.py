"""Regression, geometry, budget, and determinism checks for the procedural BRDM-2 model."""

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

import build_brdm2 as brdm2  # noqa: E402
import build_btr70 as btr70  # noqa: E402
from vehicle_kit import GltfModel, emit_motion_parts, surface_heights, surface_hit  # noqa: E402

ASSETS_DIR = _THIS_DIR.parents[1] / "src/Game.Godot/Assets/Vehicles"


def build_parts(lod=0):
    m = GltfModel("check")
    mats = brdm2.materials(m)
    hull = m.node("Hull")
    turret = m.node("TurretYaw", brdm2.TURRET_RING)
    pitch = m.node("GunPitch", btr70.GUN_TRUNNION_LOCAL, turret)
    recoil = m.node("BarrelRecoil", parent=pitch)
    brdm2.build_hull(m, hull, mats, lod)
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


class Brdm2DrawingTests(unittest.TestCase):
    """Heights, stations, and silhouette checked against the published dimensioned drawing."""

    def test_hull_roof_and_chine_heights(self):
        m, hull, _ = build_parts()
        (body,) = part_points(m, "HullBody", hull)
        mid = body[(body[:, 2] >= brdm2.CAB_ROOF_Z - 0.01) & (body[:, 2] <= brdm2.FIGHTING_REAR_Z + 0.01)]
        self.assertAlmostEqual(float(mid[:, 1].max()), 1.780, delta=0.02, msg="Roof height measured from HullBody vertices must sit at about 1.78 m")
        chine_pts = body[np.abs(np.abs(body[:, 0]) - 1.175) < 0.01]
        self.assertAlmostEqual(float(chine_pts[:, 1].max()), 1.380, delta=0.02, msg="Chine height measured from HullBody vertices")

    def test_turret_top_and_searchlight_height(self):
        m, _, turret = build_parts()
        (cone,) = part_points(m, "TurretCone", turret)
        cone_top = float(m.data["nodes"][turret]["translation"][1] + cone[:, 1].max())
        self.assertAlmostEqual(cone_top, 2.170, delta=0.02, msg=f"Turret cone top is {cone_top:.3f} m")

        # Searchlight rim summit reaches the published 2.310 m height
        (rim,) = part_points(m, "SearchlightRim", turret)
        searchlight_top = float(m.data["nodes"][turret]["translation"][1] + rim[:, 1].max())
        self.assertAlmostEqual(searchlight_top, 2.310, delta=0.02, msg=f"Searchlight top is {searchlight_top:.3f} m")

    def test_turret_and_muzzle_stations(self):
        m, _, turret = build_parts()
        (cone,) = part_points(m, "TurretCone", turret)
        turret_pos = np.asarray(m.data["nodes"][turret]["translation"])
        cone_center_z = float(turret_pos[2] + cone[:, 2].mean())
        self.assertAlmostEqual(cone_center_z, 0.180, delta=0.03, msg="Turret cone center Z measured from geometry at ~0.18 m")
        muzzle_z = float(turret_pos[2] + btr70.GUN_TRUNNION_LOCAL[2] + btr70.MUZZLE_LOCAL_Z)
        self.assertAlmostEqual(muzzle_z, -1.580, delta=0.05, msg=f"KPVT muzzle at z {muzzle_z:.2f} m")
        gun_axis = float(turret_pos[1] + btr70.GUN_TRUNNION_LOCAL[1])
        self.assertAlmostEqual(gun_axis, 2.005, delta=0.03, msg=f"Gun axis at {gun_axis:.3f} m")

    def test_windshield_visors_geometry_and_seating(self):
        m, hull, _ = build_parts(0)
        visors = part_points(m, "WindshieldVisor", hull)
        self.assertEqual(len(visors), 2, "Must have two windshield visors")
        for pts in visors:
            x_span = float(pts[:, 0].max() - pts[:, 0].min())
            self.assertAlmostEqual(x_span, 0.580, delta=0.01, msg=f"Visor width {x_span:.3f} must be 0.58 m")
            abs_x_min = float(np.abs(pts[:, 0]).min())
            abs_x_max = float(np.abs(pts[:, 0]).max())
            self.assertGreaterEqual(abs_x_min, 0.069, "Visor inner edge at 0.07 m from centerline")
            self.assertLessEqual(abs_x_max, 0.651, "Visor outer edge at 0.65 m from centerline")
            z_min = float(pts[:, 2].min())
            z_max = float(pts[:, 2].max())
            self.assertGreaterEqual(z_min, -2.10, "Visor Z within cab front plate")
            self.assertLessEqual(z_max, -1.70, "Visor Z within cab front plate")
            # Check seating flushness via surface_hit (distance <= 0.010 m / 1 cm)
            center_x = float(pts[:, 0].mean())
            center_z = float(pts[:, 2].mean())
            hit = surface_hit(m, hull, (center_x, 2.50, center_z), (0.0, -1.0, 0.0), brdm2.HULL_SURFACE)
            self.assertIsNotNone(hit, "Visor center ray must hit HullBody")
            pt, norm = hit
            normal_dists = [abs(np.dot(np.asarray(p) - np.asarray(pt), np.asarray(norm))) for p in pts]
            self.assertLessEqual(min(normal_dists), 0.010, "Visor inner face must be seated within 1 cm of hull plate")

        hinges = part_points(m, "WindshieldVisorHinge", hull)
        self.assertEqual(len(hinges), 2, "Must have two windshield visor hinges")
        for h_pts in hinges:
            self.assertAlmostEqual(float(h_pts[:, 0].max() - h_pts[:, 0].min()), 0.580, delta=0.02)
            self.assertGreater(float(h_pts[:, 1].mean()), 1.70, "Visor hinge must be at the top edge")

    def test_crew_hatches_station_and_radius(self):
        m, hull, _ = build_parts(0)
        hatches = part_points(m, "CrewHatch", hull)
        self.assertEqual(len(hatches), 2, "Must have two crew hatches")
        for pts in hatches:
            cx = float(pts[:, 0].mean())
            cz = float(pts[:, 2].mean())
            self.assertAlmostEqual(abs(cx), 0.400, delta=0.03, msg="Hatch center X at ~0.40 m")
            self.assertAlmostEqual(cz, -1.100, delta=0.03, msg="Hatch center Z at ~-1.10 m")
            r = float(np.hypot(pts[:, 0] - cx, pts[:, 2] - cz).max())
            self.assertAlmostEqual(r, 0.330, delta=0.03, msg="Hatch radius at ~0.33 m")

    def test_engine_louvres_extent(self):
        m, hull, _ = build_parts(0)
        grilles = part_points(m, "EngineGrille", hull)
        self.assertEqual(len(grilles), 2, "Must have two large engine louvre grilles")
        for pts in grilles:
            z_min = float(pts[:, 2].min())
            z_max = float(pts[:, 2].max())
            abs_x_min = float(np.abs(pts[:, 0]).min())
            abs_x_max = float(np.abs(pts[:, 0]).max())
            self.assertAlmostEqual(z_min, 2.070, delta=0.02, msg=f"Louvre Z min {z_min:.3f} must be ~2.07 m")
            self.assertAlmostEqual(z_max, 2.800, delta=0.02, msg=f"Louvre Z max {z_max:.3f} must be ~2.80 m")
            self.assertAlmostEqual(abs_x_min, 0.250, delta=0.02, msg=f"Louvre X min {abs_x_min:.3f} must be ~0.25 m")
            self.assertAlmostEqual(abs_x_max, 0.700, delta=0.02, msg=f"Louvre X max {abs_x_max:.3f} must be ~0.70 m")

    def test_bow_plates_follow_the_drawing(self):
        m, hull, _ = build_parts()
        (body,) = part_points(m, "HullBody", hull)
        prow = body[np.abs(body[:, 2] - brdm2.HULL_FRONT_Z) < 0.01]
        self.assertAlmostEqual(float(prow[:, 1].max()), brdm2.NOSE_TIP_Y, delta=0.03, msg="Prow tip height must match drawing")
        self.assertLess(float(np.abs(prow[:, 0]).max()), 0.82, "Prow cutwater must taper narrower than full hull width")

        crease = body[np.abs(body[:, 2] - brdm2.WINDSHIELD_BASE_Z) < 0.01]
        self.assertAlmostEqual(float(crease[:, 1].max()), brdm2.WINDSHIELD_BASE_Y, delta=0.03, msg="Nose meets windshield plate at 1.46 m")

        roof_y = float(body[:, 1].max())
        front_roof = body[(np.abs(body[:, 2] - brdm2.CAB_ROOF_Z) < 0.01) & (body[:, 1] > roof_y - 0.03)]
        self.assertAlmostEqual(float(np.abs(front_roof[:, 0]).max()), brdm2.CAB_ROOF_W, delta=0.03, msg="Cab roof front width")

    def test_turret_plinth_sits_on_the_roof(self):
        m, hull, _ = build_parts()
        (plinth,) = part_points(m, "TurretPlinth", hull)
        low, _ = surface_heights(m, hull, (0.0, brdm2.TURRET_RING[2]), 0.45, brdm2.HULL_SURFACE)
        self.assertLess(float(plinth[:, 1].min()), low + 0.01, "Turret plinth floats above roof")
        self.assertGreaterEqual(float(plinth[:, 1].max()) + 0.01, brdm2.TURRET_RING[1], "Turret ring floats above plinth")

    def test_belly_wheels_retracted(self):
        # Auxiliary belly wheels must be retracted well clear of the ground (Y = 0)
        self.assertGreater(brdm2.BELLY_WHEEL_Y - brdm2.BELLY_WHEEL_R, 0.08, "Belly wheels must retract clear of ground")
        self.assertEqual(len(brdm2.BELLY_WHEEL_Z), 2, "BRDM-2 must have two belly wheel stations per side")
        self.assertGreater(brdm2.BELLY_WHEEL_Z[0], brdm2.WHEEL_Z_FRONT, "Belly wheel 1 behind front axle")
        self.assertLess(brdm2.BELLY_WHEEL_Z[1], brdm2.WHEEL_Z_REAR, "Belly wheel 2 ahead of rear axle")


class Brdm2GeometryTests(unittest.TestCase):
    def test_proportions_and_dimensions(self):
        manifest = brdm2.generate_all()
        anchors = manifest["reference_anchors"]

        # Reference dimensions from drawing and published specifications
        self.assertAlmostEqual(anchors["overall_length_meters"], 5.750, delta=0.01)
        self.assertAlmostEqual(anchors["width_over_hull_meters"], 2.350, delta=0.01)
        self.assertAlmostEqual(anchors["height_to_searchlight_meters"], 2.310, delta=0.02)
        self.assertAlmostEqual(anchors["ground_clearance_meters"], 0.430, delta=0.01)
        self.assertAlmostEqual(anchors["wheelbase_meters"], 3.100, delta=0.01)
        self.assertAlmostEqual(anchors["track_gauge_front_meters"], 1.840, delta=0.01)
        self.assertAlmostEqual(anchors["track_gauge_rear_meters"], 1.790, delta=0.01)

        # 13.00-18 tires: diameter 1.120 m (radius 0.560 m)
        self.assertAlmostEqual(anchors["wheel_radius_meters"], 0.560, delta=0.001)

        # Verify world bounding box of generated LOD0
        lod0_path = ASSETS_DIR / "BRDM2" / "brdm2.glb"
        self.assertTrue(lod0_path.exists())
        data = lod0_path.read_bytes()
        json_len, = np.frombuffer(data[12:16], dtype="<u4")
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
        self.assertAlmostEqual(overall_width, 2.350, delta=0.01, msg=f"Width {overall_width} must be 2.35 m")
        self.assertAlmostEqual(overall_length, 5.750, delta=0.01, msg=f"Length {overall_length} must be 5.75 m")
        self.assertAlmostEqual(w_min[1], 0.0, delta=0.01, msg="Bottom of road tires must touch ground Y=0")

    def test_wheelbase_and_axle_positions(self):
        m = GltfModel("check")
        mats = brdm2.materials(m)
        left = m.node("LeftTrack")
        right = m.node("RightTrack")
        brdm2.build_running_gear(m, left, -1, mats, 0)
        brdm2.build_running_gear(m, right, 1, mats, 0)

        # Measure axle stations and gauge from wheel hub nodes
        nodes = m.data["nodes"]
        hubs = [
            nodes[nid]["translation"]
            for nid, part_parent, _mat, _pts, *_ in m.parts
            if "WheelHubDrum" in nodes[nid]["name"]
        ]
        self.assertEqual(len(hubs), 4)
        z_coords = sorted(set(round(h[2], 3) for h in hubs))
        self.assertEqual(len(z_coords), 2)
        wheelbase = z_coords[1] - z_coords[0]
        self.assertAlmostEqual(wheelbase, 3.100, delta=0.005, msg="Wheelbase measured from wheel hubs must be 3.100 m")
        self.assertAlmostEqual(z_coords[0], -1.385, delta=0.01, msg="Front axle station Z")
        self.assertAlmostEqual(z_coords[1], 1.715, delta=0.01, msg="Rear axle station Z")
        wheel_r = float(hubs[0][1])
        self.assertAlmostEqual(wheel_r, 0.560, delta=0.005, msg="Wheel hub height (radius) must be 0.560 m")

    def test_roof_has_no_stowage_boxes_or_barrels(self):
        m, hull, _turret = build_parts(0)
        forbidden_names = ("StowageBox", "RolledTarp", "RearRack", "BoxStrap", "TarpStrap", "Barrel")
        for node_id, part_parent, _mat, points, *_ in m.parts:
            if part_parent == hull:
                part_name = m.data["nodes"][node_id]["name"]
                for forbidden in forbidden_names:
                    self.assertNotIn(forbidden, part_name, f"Forbidden stowage part {part_name} on hull")

    def test_hull_and_turret_seating(self):
        m, hull, turret = build_parts(0)
        for points in part_points(m, "PeriscopeHood", hull):
            low, _ = surface_heights(m, hull, (points[:, 0].mean(), points[:, 2].mean()), 0.03, brdm2.HULL_SURFACE)
            self.assertLess(points[:, 1].min(), low + 0.01, "PeriscopeHood floats above HullBody")

        (points,) = part_points(m, "TurretHatch", turret)
        low, _ = surface_heights(m, turret, (points[:, 0].mean(), points[:, 2].mean()), 0.03, brdm2.TURRET_SURFACE)
        self.assertLess(points[:, 1].min(), low, "TurretHatch floats above TurretCone")

    def test_markings_lie_flush_on_sloped_hull_sides(self):
        m = GltfModel("check")
        mats = brdm2.materials(m)
        hull = m.node("Hull")
        brdm2.build_hull(m, hull, mats, 0)
        brdm2.build_markings(m, hull, lod=0)
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
                hit = surface_hit(m, hull, (sign * 2.0, float(y), float(z)), (-sign, 0.0, 0.0), brdm2.HULL_SURFACE)
                self.assertIsNotNone(hit)
                (hx, hy, hz), _ = hit
                dist = math.dist((x, y, z), (hx, hy, hz))
                self.assertLess(dist, 0.015, f"Marking vertex {(x, y, z)} is {dist:.4f} m off HullBody")

    def test_wheel_motion_export_and_wrap_period(self):
        m = GltfModel("check")
        mats = brdm2.materials(m)
        side = m.node("LeftTrack")
        brdm2.build_running_gear(m, side, -1, mats, 0)
        emit_motion_parts(m)
        self.assertEqual(len(m.parts), 0, "All parts on LeftTrack should be exported as motion meshes")

        wheel_vertices = 0
        for mesh in m.data["meshes"]:
            attrs = mesh["primitives"][0]["attributes"]
            pos = read_accessor(m, attrs["POSITION"])
            uv0 = read_accessor(m, attrs["TEXCOORD_0"])
            uv1 = read_accessor(m, attrs["TEXCOORD_1"])
            self.assertTrue(np.allclose(uv1[:, 1], 0.0))
            self.assertTrue(np.allclose(uv1[:, 0], brdm2.WHEEL_R))
            reach = np.linalg.norm(pos[:, 1:] - uv0, axis=1)
            self.assertTrue(np.all(reach <= brdm2.WHEEL_R + 0.04))
            wheel_vertices += len(pos)
        self.assertGreater(wheel_vertices, 0)

        wrap = btr70.wheel_wrap_period(brdm2.WHEEL_R)
        turns = wrap / (math.tau * brdm2.WHEEL_R)
        self.assertAlmostEqual(turns, round(turns), places=9)
        self.assertLess(abs(wrap - 1000.0), 5.0, "Wrap period must be close to 1000 m")

    def test_triangle_budgets_and_monotonicity(self):
        manifest = brdm2.generate_all()
        tris = [item["triangles"] for item in manifest["lods"]]
        self.assertEqual(len(tris), 3)

        # Budgets: LOD0 <= 10k, LOD1 <= 5k, LOD2 <= 1.5k
        self.assertLessEqual(tris[0], 10000, f"LOD0 has {tris[0]} triangles; budget is <= 10k")
        self.assertLessEqual(tris[1], 5000, f"LOD1 has {tris[1]} triangles; budget is <= 5k")
        self.assertLessEqual(tris[2], 1500, f"LOD2 has {tris[2]} triangles; budget is <= 1.5k")

        # Monotonicity
        self.assertGreater(tris[0], tris[1])
        self.assertGreater(tris[1], tris[2])

    def test_generation_determinism(self):
        with tempfile.TemporaryDirectory() as tmp1, tempfile.TemporaryDirectory() as tmp2:
            p1, p2 = Path(tmp1), Path(tmp2)
            brdm2.generate_all(p1)
            brdm2.generate_all(p2)

            for name in ("brdm2.glb", "brdm2_lod1.glb", "brdm2_lod2.glb", "manifest.json"):
                h1 = hashlib.sha256((p1 / name).read_bytes()).hexdigest()
                h2 = hashlib.sha256((p2 / name).read_bytes()).hexdigest()
                self.assertEqual(h1, h2, f"{name} generation is not byte-deterministic across runs")


if __name__ == "__main__":
    unittest.main()
