"""Tests for the procedural 9K35 Strela-10SV (9A35M) tracked SAM model.

Run from the repository root:
    python3 -B -m unittest tools/model-authoring/test_strela10.py
"""

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

import build_strela10 as bs  # noqa: E402

REPO = _THIS_DIR.parents[1]
ASSET_DIR = REPO / "src/Game.Godot/Assets/Vehicles/Strela10"
LOD_FILES = ("strela10.glb", "strela10_lod1.glb", "strela10_lod2.glb")


def _rot_y(deg):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return np.array([[c, 0, s, 0], [0, 1, 0, 0], [-s, 0, c, 0], [0, 0, 0, 1]], dtype=float)


def _rot_x(deg):
    a = math.radians(deg)
    c, s = math.cos(a), math.sin(a)
    return np.array([[1, 0, 0, 0], [0, c, -s, 0], [0, s, c, 0], [0, 0, 0, 1]], dtype=float)


def _translate(t):
    m = np.eye(4)
    m[:3, 3] = t
    return m


def _node_map(data):
    return {node["name"]: (idx, node) for idx, node in enumerate(data["nodes"])}


def _parent_map(data):
    parents = {}
    for idx, node in enumerate(data["nodes"]):
        for child in node.get("children", []):
            parents[child] = idx
    return parents


def _mesh_points_under(data, binary, root_name):
    nodes = data["nodes"]
    by_name = _node_map(data)
    root_idx = by_name[root_name][0]
    pts_all = []

    def visit(idx, mat):
        node = nodes[idx]
        world = mat @ _translate(node.get("translation", [0.0, 0.0, 0.0]))
        if "mesh" in node:
            for prim in data["meshes"][node["mesh"]]["primitives"]:
                pts = bs.accessor_array(data, binary, prim["attributes"]["POSITION"]).astype(float)
                pts = (world[:3, :3] @ pts.T).T + world[:3, 3]
                pts_all.append(pts)
        for child in node.get("children", []):
            visit(child, world)

    visit(root_idx, np.eye(4))
    return np.concatenate(pts_all, axis=0)


class Strela10AssetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.manifest = json.loads((ASSET_DIR / "manifest.json").read_text())
        cls.lods = [bs.read_glb(ASSET_DIR / name) for name in LOD_FILES]

    def test_triangle_budgets_and_lods(self):
        for lod_idx, entry in enumerate(self.manifest["lods"]):
            self.assertEqual(entry["file"], LOD_FILES[lod_idx])
            self.assertLessEqual(
                entry["triangles"],
                bs.LOD_BUDGETS[lod_idx],
                f"{entry['file']} exceeded budget ({entry['triangles']} > {bs.LOD_BUDGETS[lod_idx]})",
            )
        self.assertGreater(self.manifest["lods"][0]["triangles"], self.manifest["lods"][1]["triangles"])
        self.assertGreater(self.manifest["lods"][1]["triangles"], self.manifest["lods"][2]["triangles"])

    def test_rig_hierarchy_and_anchors_match_across_lods(self):
        required_nodes = ["Hull", "LeftTrack", "RightTrack", "TurretYaw", "GunPitch", "Muzzle", "Launch_01", "Launch_02", "Launch_03", "Launch_04"]
        base_world, _ = bs.node_world_positions(self.lods[0][0])
        base_by_name = _node_map(self.lods[0][0])

        for lod_idx, (data, _binary) in enumerate(self.lods):
            by_name = _node_map(data)
            parents = _parent_map(data)
            world, _ = bs.node_world_positions(data)

            for name in required_nodes:
                self.assertIn(name, by_name, f"LOD{lod_idx} missing rig node {name}")
                idx, node = by_name[name]
                self.assertNotIn("rotation", node, f"LOD{lod_idx} node {name} must not have rest rotation")
                self.assertNotIn("scale", node, f"LOD{lod_idx} node {name} must have unit scale")
                np.testing.assert_allclose(
                    world[idx],
                    base_world[base_by_name[name][0]],
                    atol=1e-4,
                    err_msg=f"LOD{lod_idx} anchor {name} shifted relative to LOD0",
                )

            self.assertEqual(data["nodes"][parents[by_name["GunPitch"][0]]]["name"], "TurretYaw")
            self.assertEqual(data["nodes"][parents[by_name["Muzzle"][0]]]["name"], "GunPitch")
            for k in range(1, 5):
                self.assertEqual(data["nodes"][parents[by_name[f"Launch_0{k}"][0]]]["name"], "GunPitch")

    def test_running_gear_motion_meshes_exist(self):
        for lod_idx, (data, _binary) in enumerate(self.lods):
            by_name = _node_map(data)
            for side in ("LeftTrack", "RightTrack"):
                side_children = [data["nodes"][c]["name"] for c in by_name[side][1].get("children", [])]
                motion = [n for n in side_children if "_Motion_" in n]
                self.assertGreater(len(motion), 0, f"LOD{lod_idx} {side} missing *_Motion_* running gear meshes")

    def test_ground_contact_and_reference_dimensions(self):
        measured = self.manifest["measured"]
        self.assertAlmostEqual(measured["lowest_point_meters"], 0.0, delta=0.005)
        self.assertAlmostEqual(measured["overall_length_meters"], bs.REFERENCE_LENGTH, delta=0.35)
        self.assertAlmostEqual(measured["overall_width_meters"], bs.REFERENCE_WIDTH, delta=0.15)
        self.assertGreater(measured["combat_rest_height_meters"], 3.0)
        self.assertLess(measured["combat_rest_height_meters"], 4.1)

    def test_launcher_clears_hull_roof_across_full_envelope(self):
        data, binary = self.lods[0]
        gun_local_pts = _mesh_points_under(data, binary, "GunPitch")
        turret_t = _translate(bs.TURRET_POS)
        trunnion_t = _translate(bs.TRUNNION_LOCAL)

        for yaw_deg in range(-180, 180, 30):
            for pitch_deg in (-5.0, 0.0, 20.0, 45.0, 70.0, 80.0):
                mat = turret_t @ _rot_y(yaw_deg) @ trunnion_t @ _rot_x(pitch_deg)
                world_pts = (mat[:3, :3] @ gun_local_pts.T).T + mat[:3, 3]
                min_y = float(world_pts[:, 1].min())
                self.assertGreater(
                    min_y,
                    bs.DECK_Y + 0.05,
                    f"Launcher clipped near hull roof at yaw={yaw_deg}, pitch={pitch_deg}: min_y={min_y:.3f} vs deck={bs.DECK_Y}",
                )

    def test_committed_assets_match_deterministic_generator(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            bs.generate_all(tmp_dir)
            for fname in (*LOD_FILES, "manifest.json"):
                committed = (ASSET_DIR / fname).read_bytes()
                regenerated = (tmp_dir / fname).read_bytes()
                self.assertEqual(committed, regenerated, f"{fname} differs from generator output")


if __name__ == "__main__":
    unittest.main()
