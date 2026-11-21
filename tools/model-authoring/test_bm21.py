"""Structure, budget, rig, clearance and determinism checks for the procedural BM-21 launcher."""

import hashlib
import math
from pathlib import Path
import sys
import tempfile
import unittest

import numpy as np

_THIS_DIR = Path(__file__).resolve().parent
if str(_THIS_DIR) not in sys.path:
    sys.path.insert(0, str(_THIS_DIR))

import build_bm21 as bm21  # noqa: E402
from vehicle_kit import GltfModel, emit_motion_parts  # noqa: E402

ASSET_DIR = _THIS_DIR.parents[1] / "src/Game.Godot/Assets/Vehicles/BM21"
FILES = ("bm21.glb", "bm21_lod1.glb", "bm21_lod2.glb")
RIG_NODES = ("Hull", "LeftTrack", "RightTrack", "TurretYaw", "GunPitch", "Muzzle")


def launch_names():
    return [bm21.launch_name(r, c) for r in range(bm21.TUBE_ROWS) for c in range(bm21.TUBE_COLUMNS)]


def node_table(path):
    data, binary = bm21.read_glb(path)
    world, parent = bm21.node_world_positions(data)
    by_name = {}
    for i, node in enumerate(data["nodes"]):
        by_name.setdefault(node["name"], []).append(i)
    return data, binary, world, parent, by_name


def mesh_points(data, binary, world, node_index):
    node = data["nodes"][node_index]
    return np.concatenate([bm21.accessor_array(data, binary, p["attributes"]["POSITION"]) + world[node_index] for p in data["meshes"][node["mesh"]]["primitives"]])


def meshes_under(data, parent, root_index):
    """Mesh node indices whose ancestor chain contains root_index."""
    found = []
    for i, node in enumerate(data["nodes"]):
        if "mesh" not in node:
            continue
        cursor = parent.get(i)
        while cursor is not None:
            if cursor == root_index:
                found.append(i)
                break
            cursor = parent.get(cursor)
    return found


def build_parts(lod=0):
    """Unsaved model with named parts, for checks on individual pieces."""
    m = GltfModel("check")
    mats = bm21.materials(m)
    hull = m.node("Hull")
    turret = m.node("TurretYaw", bm21.TURRET_POS)
    pitch = m.node("GunPitch", bm21.TRUNNION_LOCAL, turret)
    bm21.build_chassis(m, hull, mats, lod)
    bm21.build_front(m, hull, mats, lod)
    bm21.build_cab(m, hull, mats, lod)
    bm21.build_rear_body(m, hull, mats, lod)
    bm21.build_turntable(m, turret, mats, lod)
    bm21.build_pack(m, pitch, mats, lod)
    return m, hull, turret, pitch


def part_bounds(m, names, parent):
    nodes = m.data["nodes"]
    pts = [np.asarray(points) + np.asarray(nodes[node_id]["translation"]) for node_id, part_parent, _mat, points, *_ in m.parts if part_parent == parent and nodes[node_id]["name"] in names]
    allp = np.concatenate(pts)
    return allp.min(axis=0), allp.max(axis=0)


def rot_y(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def rot_x(a):
    c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]])


def obb_overlap(center_a, axes_a, half_a, center_b, axes_b, half_b):
    """Separating axis test for two oriented boxes (axes as rows)."""
    t = center_b - center_a
    candidates = list(axes_a) + list(axes_b)
    for a in axes_a:
        for b in axes_b:
            c = np.cross(a, b)
            if np.linalg.norm(c) > 1e-9:
                candidates.append(c / np.linalg.norm(c))
    for axis in candidates:
        ra = sum(half_a[i] * abs(axes_a[i] @ axis) for i in range(3))
        rb = sum(half_b[i] * abs(axes_b[i] @ axis) for i in range(3))
        if abs(t @ axis) > ra + rb:
            return False
    return True


PACK_GROUPS = (
    {"Tube", "BandTop", "BandBottom", "BandSide", "PackRail", "TubeMuzzleRing", "TubeBore", "BreechPlate", "HarnessBox"},
    {"CradleRail", "CradleBeam", "CradleCross"},
    {"ElevationSector"},
)


class Bm21GeneratedAssetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls._tmp = tempfile.TemporaryDirectory()
        cls.out = Path(cls._tmp.name)
        cls.manifest = bm21.generate_all(cls.out)
        cls.tables = [node_table(cls.out / name) for name in FILES]

    @classmethod
    def tearDownClass(cls):
        cls._tmp.cleanup()

    def test_triangle_budgets_and_monotonicity(self):
        tris = [item["triangles"] for item in self.manifest["lods"]]
        for lod, (count, budget) in enumerate(zip(tris, bm21.LOD_BUDGETS)):
            self.assertLessEqual(count, budget, f"LOD{lod} has {count} triangles, budget {budget}")
        self.assertGreater(tris[0], tris[1])
        self.assertGreater(tris[1], tris[2])

    def test_required_nodes_exist_once_in_every_lod(self):
        for lod, (_, _, _, _, by_name) in enumerate(self.tables):
            for name in RIG_NODES + tuple(launch_names()):
                self.assertEqual(len(by_name.get(name, [])), 1, f"LOD{lod} must have exactly one {name}")

    def test_rig_hierarchy(self):
        for lod, (data, _, _, parent, by_name) in enumerate(self.tables):
            nodes = data["nodes"]

            def parent_name(name):
                return nodes[parent[by_name[name][0]]]["name"]

            self.assertEqual(parent_name("GunPitch"), "TurretYaw", f"LOD{lod}")
            self.assertEqual(parent_name("Muzzle"), "GunPitch", f"LOD{lod}")
            for name in launch_names():
                self.assertEqual(parent_name(name), "GunPitch", f"LOD{lod} {name}")
            self.assertTrue(meshes_under(data, parent, by_name["TurretYaw"][0]), f"LOD{lod} turntable has no geometry")
            self.assertTrue(meshes_under(data, parent, by_name["GunPitch"][0]), f"LOD{lod} pack has no geometry")

    def test_unit_scale_and_no_rest_rotation(self):
        # Launch point bores are parallel because no node carries a rotation or scale.
        for lod, (data, *_rest) in enumerate(self.tables):
            for node in data["nodes"]:
                self.assertNotIn("rotation", node, f"LOD{lod} {node['name']} has a rotation")
                self.assertNotIn("scale", node, f"LOD{lod} {node['name']} has a scale")
                self.assertNotIn("matrix", node, f"LOD{lod} {node['name']} has a matrix")

    def test_rig_transforms_match_across_lods(self):
        names = RIG_NODES[3:] + tuple(launch_names())
        reference = self.tables[0]
        for lod in (1, 2):
            table = self.tables[lod]
            for name in names:
                a = reference[2][reference[4][name][0]]
                b = table[2][table[4][name][0]]
                self.assertLess(float(np.linalg.norm(a - b)), 0.001, f"{name} moved between LOD0 and LOD{lod}")

    def test_launch_points_sit_on_tube_mouths(self):
        names = launch_names()
        self.assertEqual(len(set(names)), 40)
        data, binary, world, parent, by_name = self.tables[0]
        pitch_world = world[by_name["GunPitch"][0]]
        pack = np.concatenate([mesh_points(data, binary, world, i) for i in meshes_under(data, parent, by_name["GunPitch"][0])]) - pitch_world
        front = pack[np.abs(pack[:, 2] - bm21.PACK_FRONT_Z) < 0.002]
        for name in names:
            local = world[by_name[name][0]] - pitch_world
            self.assertLessEqual(abs(local[0]), bm21.PACK_HALF_WIDTH, name)
            self.assertTrue(bm21.PACK_BOTTOM_Y < local[1] < bm21.PACK_TOP_Y, name)
            self.assertAlmostEqual(float(local[2]), bm21.LAUNCH_Z, places=4, msg=name)
            ring = np.hypot(front[:, 0] - local[0], front[:, 1] - local[1])
            hits = front[(ring > bm21.BORE_RADIUS - 0.002) & (ring < bm21.TUBE_RADIUS + 0.002)]
            self.assertGreater(len(hits), 6, f"{name} is not centered on a tube mouth")
        # Points are spaced by one tube pitch in a 4 x 10 grid
        xs = sorted({round(float(world[by_name[n][0]][0]), 4) for n in names})
        ys = sorted({round(float(world[by_name[n][0]][1]), 4) for n in names})
        self.assertEqual((len(xs), len(ys)), (10, 4))
        self.assertTrue(np.allclose(np.diff(xs), bm21.TUBE_PITCH, atol=1e-4))
        self.assertTrue(np.allclose(np.diff(ys), bm21.TUBE_PITCH, atol=1e-4))

    def test_launch_point_numbering_and_firing_order(self):
        data, _, world, _, by_name = self.tables[0]
        first, last = world[by_name["Launch_01"][0]], world[by_name["Launch_40"][0]]
        self.assertLess(first[0], 0.0, "Launch_01 is the top left tube")
        self.assertGreater(first[1], last[1], "Row 0 is the top row")
        self.assertGreater(last[0], 0.0, "Launch_40 is the bottom right tube")
        order = self.manifest["rig"]["launch_points"]["firing_order"]
        self.assertEqual(sorted(order), sorted(launch_names()), "Firing order must list every tube once")
        listed = {p["name"]: p["local_position"] for p in self.manifest["rig"]["launch_points"]["points"]}
        pitch_world = world[by_name["GunPitch"][0]]
        for name, local in listed.items():
            self.assertTrue(np.allclose(np.asarray(local), world[by_name[name][0]] - pitch_world, atol=1e-4), name)

    def test_wheels_rest_on_the_ground(self):
        for lod, (data, binary, world, parent, by_name) in enumerate(self.tables):
            lowest = min(float(mesh_points(data, binary, world, i)[:, 1].min()) for i, n in enumerate(data["nodes"]) if "mesh" in n)
            self.assertGreaterEqual(lowest, -0.001, f"LOD{lod} geometry goes below the ground")
            for side in ("LeftTrack", "RightTrack"):
                wheels = meshes_under(data, parent, by_name[side][0])
                self.assertTrue(wheels, f"LOD{lod} {side} has no wheel meshes")
                self.assertTrue(all("_Motion_" in data["nodes"][i]["name"] for i in wheels), f"LOD{lod} {side} wheels must be motion meshes")
                low = min(float(mesh_points(data, binary, world, i)[:, 1].min()) for i in wheels)
                self.assertLess(low, 0.01, f"LOD{lod} {side} wheels float at {low:.3f} m")

    def test_dimensions_match_references_and_manifest(self):
        measured = self.manifest["measured"]
        self.assertAlmostEqual(measured["overall_length_meters"], bm21.REFERENCE_LENGTH, delta=0.08)
        # The drawing and photos put the pack top about level with the cab roof; 3.09 m is a published upper bound
        self.assertLessEqual(measured["travel_height_meters"], bm21.REFERENCE_TRAVEL_HEIGHT)
        pack_top = bm21.TURRET_POS[1] + bm21.TRUNNION_LOCAL[1] + bm21.PACK_TOP_Y + bm21.BAND_THICKNESS
        self.assertAlmostEqual(pack_top, bm21.PHOTO_PACK_TOP_Y, delta=0.10, msg="Pack top in travel position")
        self.assertAlmostEqual(measured["lowest_point_meters"], 0.0, delta=0.001)
        # Body width excludes the mirrors, as published figures do
        m, hull, _, _ = build_parts(0)
        names = {"Cab", "WingFront", "WingArch", "RearMudguard", "PlatformDeck", "Bumper"}
        lo, hi = part_bounds(m, names, hull)
        self.assertAlmostEqual(float(hi[0] - lo[0]), bm21.REFERENCE_WIDTH, delta=0.02)
        self.assertLess(measured["overall_width_meters"], 2.70, "Mirrors must stay close to the body")
        cab_lo, cab_hi = part_bounds(m, {"Cab"}, hull)
        self.assertAlmostEqual(float(cab_hi[1]), bm21.REFERENCE_CAB_HEIGHT, delta=0.01, msg="Cab roof height")
        nodes = m.data["nodes"]
        lowest_body = min(float((np.asarray(points) + np.asarray(nodes[node_id]["translation"]))[:, 1].min()) for node_id, parent, _m, points, *_ in m.parts if parent == hull)
        self.assertAlmostEqual(lowest_body, bm21.REFERENCE_CLEARANCE, delta=0.01, msg="Ground clearance under the axles")
        anchors = self.manifest["reference_anchors"]
        self.assertAlmostEqual(anchors["wheelbase_front_to_first_rear_meters"], 3.525, places=3)
        self.assertAlmostEqual(anchors["rear_axle_spacing_meters"], 1.40, places=3)

    def test_pack_clears_chassis_over_full_traverse_and_elevation(self):
        m, hull, turret, pitch = build_parts(0)
        nodes = m.data["nodes"]
        # Every chassis part that reaches above the platform is an obstacle, tested as its own box
        obstacles = []
        for node_id, parent, _m, points, *_ in m.parts:
            if parent != hull:
                continue
            pts = np.asarray(points) + np.asarray(nodes[node_id]["translation"])
            lo, hi = pts.min(axis=0), pts.max(axis=0)
            if hi[1] > bm21.DECK_Y + 0.05:
                obstacles.append((nodes[node_id]["name"], (lo + hi) / 2, (hi - lo) / 2 + 1e-4))
        self.assertTrue(any(name == "Cab" for name, *_ in obstacles))
        # The tube block and the cradle beam are tested as separate boxes, so one box does not
        # inflate the other into a false collision.
        groups = PACK_GROUPS
        boxes = []
        for names in groups:
            lo, hi = part_bounds(m, names, pitch)
            boxes.append(((lo + hi) / 2, (hi - lo) / 2))
        turret_pos, trunnion = np.asarray(bm21.TURRET_POS), np.asarray(bm21.TRUNNION_LOCAL)
        identity = np.eye(3)
        corners = np.array([[sx, sy, sz] for sx in (-1, 1) for sy in (-1, 1) for sz in (-1, 1)])
        for yaw in np.arange(-bm21.TRAVERSE_RIGHT_DEG, bm21.TRAVERSE_LEFT_DEG + 0.01, 2.0):
            for elevation in (0.0, 15.0, 30.0, bm21.ELEVATION_MAX_DEG):
                r = rot_y(math.radians(yaw)) @ rot_x(math.radians(elevation))
                axes = r.T  # rows are the box axes in world space
                for local_center, half in boxes:
                    center = turret_pos + rot_y(math.radians(yaw)) @ trunnion + r @ local_center
                    for name, obstacle_center, obstacle_half in obstacles:
                        self.assertFalse(
                            obb_overlap(center, axes, half, obstacle_center, identity, obstacle_half),
                            f"Pack hits {name} at yaw {yaw:.0f} deg, elevation {elevation:.0f} deg",
                        )
                    lowest = float((center + (corners * half) @ axes)[:, 1].min())
                    self.assertGreater(lowest, bm21.DECK_Y + 0.05, f"Pack hits the deck at yaw {yaw:.0f}, elevation {elevation:.0f}")

    def test_pack_clears_the_rotating_carriage_over_elevation(self):
        # The carriage, sight and trunnion supports turn with the pack, so only elevation moves them apart
        m, _, turret, pitch = build_parts(0)
        nodes = m.data["nodes"]
        parts = []
        for node_id, parent, _m, points, *_ in m.parts:
            if parent == turret:
                pts = np.asarray(points) + np.asarray(nodes[node_id]["translation"])
                lo, hi = pts.min(axis=0), pts.max(axis=0)
                parts.append((nodes[node_id]["name"], (lo + hi) / 2, (hi - lo) / 2))
        boxes = []
        for names in PACK_GROUPS:
            lo, hi = part_bounds(m, names, pitch)
            boxes.append(((lo + hi) / 2, (hi - lo) / 2))
        trunnion = np.asarray(bm21.TRUNNION_LOCAL)
        for elevation in np.arange(0.0, bm21.ELEVATION_MAX_DEG + 0.01, 2.5):
            r = rot_x(math.radians(elevation))
            for local_center, half in boxes:
                center = trunnion + r @ local_center
                for name, part_center, part_half in parts:
                    if name == "Turntable":
                        continue
                    self.assertFalse(obb_overlap(center, r.T, half, part_center, np.eye(3), part_half), f"Pack hits {name} at elevation {elevation:.1f} deg")

    def test_documented_over_cab_limit_matches_the_geometry(self):
        # Within 34 degrees either side of the axis the published minimum elevation is 11 degrees.
        # The model must agree: at 11 degrees every bore clears the chassis, at 0 degrees some do not,
        # and just outside the sector zero elevation is clear again, so the sector is not too narrow.
        m, hull, _, _ = build_parts(0)
        nodes = m.data["nodes"]
        boxes = []
        for node_id, parent, _m, points, *_ in m.parts:
            if parent == hull:
                pts = np.asarray(points) + np.asarray(nodes[node_id]["translation"])
                if pts[:, 1].max() > bm21.DECK_Y + 0.05:
                    boxes.append((nodes[node_id]["name"], pts.min(axis=0), pts.max(axis=0)))
        launch = [np.array([*bm21.tube_center(r, c), bm21.LAUNCH_Z]) for r in range(bm21.TUBE_ROWS) for c in range(bm21.TUBE_COLUMNS)]
        turret_pos, trunnion = np.asarray(bm21.TURRET_POS), np.asarray(bm21.TRUNNION_LOCAL)

        def blocked(yaw, elevation):
            ry, rx = rot_y(math.radians(yaw)), rot_x(math.radians(elevation))
            direction = ry @ rx @ np.array([0.0, 0.0, -1.0])
            hits = []
            for point in launch:
                origin = turret_pos + ry @ (trunnion + rx @ point)
                for name, lo, hi in boxes:
                    with np.errstate(divide="ignore", invalid="ignore"):
                        t0, t1 = (lo - origin) / direction, (hi - origin) / direction
                    near = np.nanmax(np.where(np.isfinite(np.minimum(t0, t1)), np.minimum(t0, t1), -np.inf))
                    far = np.nanmin(np.where(np.isfinite(np.maximum(t0, t1)), np.maximum(t0, t1), np.inf))
                    inside = np.all((origin >= lo) | (direction != 0)) and np.all((origin <= hi) | (direction != 0))
                    if inside and near <= far and far > 0.0:
                        hits.append(name)
            return hits

        half = bm21.OVER_CAB_HALF_SECTOR_DEG
        for yaw in np.arange(-half, half + 0.01, 2.0):
            self.assertEqual(blocked(yaw, bm21.OVER_CAB_MIN_ELEVATION_DEG), [], f"Rockets hit the chassis at yaw {yaw:.0f}, elevation 11")
        self.assertIn("Cab", blocked(0.0, 0.0), "At zero elevation over the cab the limit must be needed")
        for yaw in (-half - 1.0, half + 1.0, -bm21.TRAVERSE_RIGHT_DEG, bm21.TRAVERSE_LEFT_DEG):
            self.assertEqual(blocked(yaw, 0.0), [], f"Zero elevation at yaw {yaw:.0f}, outside the sector, must clear the chassis")

    def test_travel_position_pack_ends_behind_the_cab(self):
        # The cab is 2.68 m and the vehicle 3.09 m high, so four tube rows cannot pass over the cab.
        m, hull, _, pitch = build_parts(0)
        cab_lo, cab_hi = part_bounds(m, {"Cab"}, hull)
        pack_lo, _ = part_bounds(m, {"Tube", "BandTop", "BandBottom", "BandSide", "CradleRail", "CradleBeam"}, pitch)
        pack_front_world = bm21.TURRET_POS[2] + bm21.TRUNNION_LOCAL[2] + float(pack_lo[2])
        self.assertGreater(pack_front_world, float(cab_hi[2]) + 0.05, "In travel position the pack front must stay behind the cab")
        self.assertLess(bm21.pack_reach(), bm21.TURRET_POS[2] - float(cab_hi[2]), "Pack reach must not get the front corners over the cab at any traverse")
        # The reach formula matches the built geometry: the farthest front corner from the axis
        corner = np.array([float(pack_lo[0]), float(pack_lo[2]) + bm21.TRUNNION_LOCAL[2]])
        self.assertAlmostEqual(bm21.pack_reach(), float(np.hypot(*corner)), delta=0.03)
        pack_bottom = bm21.TURRET_POS[1] + bm21.TRUNNION_LOCAL[1] + bm21.PACK_BOTTOM_Y
        self.assertLess(pack_bottom, float(cab_hi[1]), "This layout is only needed because the pack sits below the cab roof")

    def test_manifest_rig_section(self):
        rig = self.manifest["rig"]
        self.assertEqual(rig["TurretYaw"]["rotation_y_range_degrees"], [-70.0, 102.0])
        self.assertEqual(rig["GunPitch"]["rotation_x_range_degrees"], [0.0, 55.0])
        self.assertEqual(rig["launch_points"]["count"], 40)
        self.assertTrue(self.manifest["simplifications"])
        self.assertTrue(self.manifest["unsupported_motions"])
        self.assertTrue(rig["TurretYaw"]["traverse_verified"])
        self.assertIn(rig["TurretYaw"]["traverse_source"], self.manifest["sources"])
        (sector,) = rig["GunPitch"]["min_elevation_sectors"]
        self.assertEqual(sector["rotation_y_range_degrees"], [-34.0, 34.0])
        self.assertEqual(sector["min_elevation_degrees"], 11.0)
        self.assertTrue(all(url.startswith("https://") for url in self.manifest["sources"]))
        self.assertTrue(self.manifest["reference_anchors"]["conflicts"])

    def test_generation_is_deterministic_and_assets_are_current(self):
        with tempfile.TemporaryDirectory() as tmp:
            bm21.generate_all(Path(tmp))
            for name in FILES + ("manifest.json",):
                fresh = hashlib.sha256((Path(tmp) / name).read_bytes()).hexdigest()
                first = hashlib.sha256((self.out / name).read_bytes()).hexdigest()
                self.assertEqual(fresh, first, f"{name} is not byte deterministic")
                committed = ASSET_DIR / name
                self.assertTrue(committed.exists(), f"{committed} is missing; run build_bm21.py")
                self.assertEqual(hashlib.sha256(committed.read_bytes()).hexdigest(), fresh, f"{name} is stale; run build_bm21.py")


class Bm21WheelMotionTests(unittest.TestCase):
    def test_wheel_motion_export_and_wrap_period(self):
        m = GltfModel("check")
        mats = bm21.materials(m)
        side = m.node("LeftTrack")
        bm21.build_running_gear(m, side, -1, mats, 0)
        emit_motion_parts(m)
        self.assertEqual(len(m.parts), 0, "Every wheel part must export as a motion mesh")
        for mesh in m.data["meshes"]:
            attrs = mesh["primitives"][0]["attributes"]
            pos = bm21.accessor_array(m.data, bytes(m.binary), attrs["POSITION"])
            uv0 = bm21.accessor_array(m.data, bytes(m.binary), attrs["TEXCOORD_0"])
            uv1 = bm21.accessor_array(m.data, bytes(m.binary), attrs["TEXCOORD_1"])
            self.assertTrue(np.allclose(uv1[:, 0], bm21.WHEEL_R))
            self.assertTrue(np.all(np.linalg.norm(pos[:, 1:] - uv0, axis=1) <= bm21.WHEEL_R + 0.01))
        wrap = bm21.wheel_wrap_period(bm21.WHEEL_R)
        turns = wrap / (math.tau * bm21.WHEEL_R)
        self.assertAlmostEqual(turns, round(turns), places=9)
        self.assertLess(abs(wrap - 1000.0), 5.0)


if __name__ == "__main__":
    unittest.main()
