"""Regression checks for vehicle_kit and the T-62 / M60 study models.

Covers bugs found in review: markings that floated beside the armor, hatches and
cupolas hovering over curved roofs, fender boxes cutting into the turret, and the
mesh refinement used for baked contact shadows.
"""

import math
import unittest

import numpy as np

import build_m60
import build_t62_alt as t62
from vehicle_kit import GltfModel, _refine, surface_heights


def build_parts(module, lod=0, ring=None):
    m = GltfModel("check")
    mats = module.materials(m)
    hull = m.node("Hull")
    turret = m.node("TurretYaw", ring or module.TURRET_RING)
    pitch = m.node("GunPitch", module.GUN_TRUNNION_LOCAL, turret)
    recoil = m.node("BarrelRecoil", parent=pitch)
    module.build_hull(m, hull, mats, lod)
    module.build_turret(m, turret, mats, lod)
    module.build_gun(m, pitch, recoil, mats, lod)
    return m, hull, turret


def part_points(m, name, parent=None):
    nodes = m.data["nodes"]
    found = []
    for node_id, part_parent, _mat, points, *_ in m.parts:
        if nodes[node_id]["name"] == name and (parent is None or part_parent == parent):
            found.append(np.asarray(points) + np.asarray(nodes[node_id]["translation"]))
    return found


class RefineTests(unittest.TestCase):
    def test_long_plate_is_split_without_losing_area_or_flipping(self):
        points = [(0, 0, 0), (6, 0, 0), (6, 0, 0.8), (0, 0, 0.8)]
        p, _, _, idx = _refine(points, [(0, 1, 0)] * 4, [(1, 1, 1, 1)] * 4, [0, 1, 2, 0, 2, 3], 0.5)
        area, signs = 0.0, set()
        for i in range(0, len(idx), 3):
            a, b, c = (np.asarray(p[k]) for k in idx[i : i + 3])
            cross = np.cross(b - a, c - a)
            area += 0.5 * np.linalg.norm(cross)
            signs.add(bool(cross[1] > 0))
        self.assertAlmostEqual(area, 4.8, places=6)
        self.assertEqual(len(signs), 1)
        self.assertGreater(len(idx) // 3, 2)

    def test_thin_strips_are_left_alone(self):
        points = [(0, 0, 0), (6, 0, 0), (6, 0, 0.03), (0, 0, 0.03)]
        _, _, _, idx = _refine(points, [(0, 1, 0)] * 4, [(1, 1, 1, 1)] * 4, [0, 1, 2, 0, 2, 3], 0.5)
        self.assertEqual(len(idx), 6)


class T62SeatingTests(unittest.TestCase):
    def test_markings_lie_on_the_dome(self):
        m = GltfModel("check")
        mats = t62.materials(m)
        turret = m.node("TurretYaw", t62.TURRET_RING)
        t62.build_turret(m, turret, mats, 0)
        t62.build_markings(m, turret)
        accessor_ids = [mesh["primitives"][0]["attributes"]["POSITION"] for mesh in m.data["meshes"] if mesh["name"] == "Marking"]
        self.assertEqual(len(accessor_ids), 4)
        for accessor_id in accessor_ids:
            accessor = m.data["accessors"][accessor_id]
            view = m.data["bufferViews"][accessor["bufferView"]]
            raw = bytes(m.binary[view["byteOffset"] : view["byteOffset"] + view["byteLength"]])
            for x, y, z in np.frombuffer(raw, dtype="<f4").reshape(-1, 3):
                gap = math.hypot(x, z) - t62.turret_radius(y)
                self.assertLess(abs(gap), 0.02, f"marking vertex {x:.2f} {y:.2f} {z:.2f} is {gap:.3f} m off the dome")

    def test_hatches_and_cupola_reach_into_the_dome(self):
        m, _, turret = build_parts(t62)
        for name in ("Cupola", "LoaderHatch", "GunnerSight", "Ventilator"):
            (points,) = part_points(m, name, turret)
            low, _ = surface_heights(m, turret, (points[:, 0].mean(), points[:, 2].mean()), 0.03, {"TurretDome"})
            self.assertLess(points[:, 1].min(), low, name)

    def test_fender_stowage_clears_the_turret_overhang(self):
        m, hull, _ = build_parts(t62)
        turret_base = t62.TURRET_RING[1]
        for name in ("FuelCell", "StowageBox", "SparesBox"):
            for points in part_points(m, name, hull):
                self.assertLess(points[:, 1].max(), turret_base, name)


class TrackLinkTests(unittest.TestCase):
    def guide_clearance(self, module, gap_radius):
        m = GltfModel("check")
        mats = module.materials(m)
        side = m.node("RightTrack")
        module.build_running_gear(m, side, 1, mats, 0)
        horns = part_points(m, "TrackHorn", side)
        self.assertTrue(horns, "LOD0 track has no guide horns")
        hub_bottom = module.ROAD_WHEEL_Y - gap_radius
        for points in horns:
            if points[:, 1].max() < module.ROAD_WHEEL_Y and module.ROAD_WHEEL_Z[0] < points[:, 2].mean() < module.ROAD_WHEEL_Z[-1]:
                self.assertLess(points[:, 1].max(), hub_bottom, "guide horn cuts into the road wheel hub")

    def test_t62_guide_horns_pass_between_wheel_halves(self):
        self.guide_clearance(t62, t62.ROAD_WHEEL_GAP_R)

    def test_m60_guide_horns_pass_between_wheel_halves(self):
        self.guide_clearance(build_m60, build_m60.ROAD_WHEEL_GAP_R)


def read_accessor(m, index):
    accessor = m.data["accessors"][index]
    view = m.data["bufferViews"][accessor["bufferView"]]
    raw = bytes(m.binary[view["byteOffset"] : view["byteOffset"] + view["byteLength"]])
    width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[accessor["type"]]
    dtype = "<f4" if accessor["componentType"] == 5126 else "<u4"
    return np.frombuffer(raw, dtype=dtype).reshape(-1, width) if width > 1 else np.frombuffer(raw, dtype=dtype)


class TrackPathTests(unittest.TestCase):
    def test_top_run_never_runs_backwards(self):
        # A road wheel below the line from the sprocket used to make the run loop back over it.
        from vehicle_kit import track_path

        for module, supports in ((t62, None), (build_m60, "rollers")):
            wheels = [(z, module.ROAD_WHEEL_Y, module.ROAD_WHEEL_R) for z in module.ROAD_WHEEL_Z]
            tops = wheels if supports is None else [(z, module.RETURN_ROLLER_Y, module.RETURN_ROLLER_R) for z in module.RETURN_ROLLER_Z]
            path = track_path(wheels, tops, module.IDLER, module.SPROCKET, 10, sag=0.03)
            count = len(path)
            for i in range(count):
                a, b, c = path[i - 1], path[i], path[(i + 1) % count]
                turn = math.atan2(c[1] - b[1], c[0] - b[0]) - math.atan2(b[1] - a[1], b[0] - a[0])
                turn = (turn + math.pi) % math.tau - math.pi
                self.assertLess(abs(turn), 1.2, f"{module.__name__} path folds back at {b}")


class MotionExportTests(unittest.TestCase):
    def motion_meshes(self, module):
        m = GltfModel("check")
        mats = module.materials(m)
        side = m.node("LeftTrack")
        module.build_running_gear(m, side, -1, mats, 0)
        from vehicle_kit import emit_motion_parts

        emit_motion_parts(m)
        # Suspension arms and dampers are fixed to the hull; everything else on the side turns or runs.
        static = {m.data["nodes"][part[0]]["name"] for part in m.parts}
        self.assertLessEqual(static, {"RoadArm", "ShockAbsorber"})
        meshes = []
        for mesh in m.data["meshes"]:
            attributes = mesh["primitives"][0]["attributes"]
            meshes.append(tuple(read_accessor(m, attributes[key]) for key in ("POSITION", "TEXCOORD_0", "TEXCOORD_1")))
        return meshes

    def check(self, module):
        links = wheels = 0
        for position, uv0, uv1 in self.motion_meshes(module):
            is_link = uv1[:, 1] > 0
            if is_link.any():
                # After one full cycle every link vertex lands on a rest vertex: the loop is seamless.
                rest = position[is_link][:, 1:]
                after = uv0[is_link]
                nearest = np.min(np.linalg.norm(after[:, None, :] - rest[None, :, :], axis=2), axis=1)
                self.assertLess(nearest.max(), 0.004)
                links += int(is_link.sum())
            wheel = ~is_link
            if wheel.any():
                radius = uv1[wheel, 0]
                self.assertTrue(np.all(radius > 0.05))
                # Axle centers lie inside each wheel: no vertex is farther than its radius plus the tire wall.
                reach = np.linalg.norm(position[wheel][:, 1:] - uv0[wheel], axis=1)
                self.assertTrue(np.all(reach <= radius + 0.06))
                wheels += int(wheel.sum())
        self.assertGreater(links, 0)
        self.assertGreater(wheels, 0)

    def check_mid_cycle_sag(self, module):
        # Links lerp between two placements; at mid cycle they cut the chord of their arc.
        # Plates (the shortest cycle) must stay within 2 cm of the run, even on the sprocket wrap.
        meshes = [(position, uv0, uv1) for position, uv0, uv1 in self.motion_meshes(module) if (uv1[:, 1] > 0).any()]
        plate_cycle = min(float(uv1[uv1[:, 1] > 0, 1].min()) for _, _, uv1 in meshes)
        worst = 0.0
        for position, uv0, uv1 in meshes:
            plates = (uv1[:, 1] > 0) & (uv1[:, 1] < plate_cycle + 1e-6)
            chord = np.linalg.norm(uv0[plates] - position[plates][:, 1:], axis=1)
            sag = chord / 2 * np.tan(np.abs(uv1[plates, 0]) / 4)
            worst = max(worst, float(sag.max()))
        self.assertLess(plate_cycle, 0.2)
        self.assertLess(worst, 0.02)

    def test_t62_plates_stay_on_the_run_mid_cycle(self):
        self.check_mid_cycle_sag(t62)

    def test_m60_plates_stay_on_the_run_mid_cycle(self):
        self.check_mid_cycle_sag(build_m60)

    def test_t62_running_gear_motion_is_seamless(self):
        self.check(t62)

    def test_m60_running_gear_motion_is_seamless(self):
        self.check(build_m60)


class M60SeatingTests(unittest.TestCase):
    def test_cupola_and_hatch_reach_into_the_roof(self):
        m, _, turret = build_parts(build_m60)
        for name in ("Cupola", "LoaderHatch"):
            (points,) = part_points(m, name, turret)
            low, _ = surface_heights(m, turret, (points[:, 0].mean(), points[:, 2].mean()), 0.03, {"TurretShell"})
            self.assertLess(points[:, 1].min(), low, name)

    def test_turret_sits_on_its_ring_guard(self):
        m, hull, _ = build_parts(build_m60)
        (ring,) = part_points(m, "TurretRingGuard", hull)
        turret_bottom = build_m60.TURRET_RING[1] + 0.10
        self.assertGreaterEqual(ring[:, 1].max(), turret_bottom)


if __name__ == "__main__":
    unittest.main()
