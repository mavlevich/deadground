"""Checks of the exported US rifleman: one skinned mesh, the game_engine rig, the six clips and the budget.

Run from the repository root: python3 -m unittest tools/model-authoring/infantry/test_us_rifleman.py
Needs only the Python standard library.
"""

import json
from pathlib import Path
import struct
import unittest

GLB = Path(__file__).resolve().parents[3] / "src/Game.Godot/Assets/Infantry/US/us_rifleman.glb"
CLIPS = ["Idle", "AimStanding", "AimKneeling", "AimProne", "Walk", "Run"]
GAME_ENGINE_JOINTS = 53
MAX_TRIANGLES = 7000


def read_gltf(path):
    data = path.read_bytes()
    magic, version, _length = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67 or version != 2:
        raise ValueError(f"{path} is not a glTF 2.0 binary")
    chunk_length, chunk_type = struct.unpack_from("<II", data, 12)
    if chunk_type != 0x4E4F534A:
        raise ValueError(f"{path} does not start with a JSON chunk")
    return json.loads(data[20:20 + chunk_length])


class UsRiflemanTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.gltf = read_gltf(GLB)

    def test_one_skinned_mesh_on_the_game_engine_rig(self):
        mesh_nodes = [n for n in self.gltf["nodes"] if "mesh" in n]
        self.assertEqual(1, len(self.gltf["meshes"]))
        self.assertEqual(1, len(mesh_nodes))
        self.assertIn("skin", mesh_nodes[0])
        self.assertEqual(1, len(self.gltf["skins"]))
        self.assertEqual(GAME_ENGINE_JOINTS, len(self.gltf["skins"][0]["joints"]))

    def test_all_clips_are_present(self):
        names = [a["name"] for a in self.gltf.get("animations", [])]
        self.assertEqual(sorted(CLIPS), sorted(names))

    def test_triangle_budget(self):
        accessors = self.gltf["accessors"]
        triangles = sum(accessors[p["indices"]]["count"] // 3
                        for m in self.gltf["meshes"] for p in m["primitives"])
        self.assertLess(triangles, MAX_TRIANGLES)
        self.assertGreater(triangles, 2000, "the faceted look needs some detail")

    def test_colors_without_textures(self):
        self.assertEqual([], self.gltf.get("images", []))
        self.assertEqual([], self.gltf.get("textures", []))
        for mesh in self.gltf["meshes"]:
            for primitive in mesh["primitives"]:
                self.assertIn("COLOR_0", primitive["attributes"], "colors live per face corner")

    def test_standing_height_with_helmet(self):
        accessors = self.gltf["accessors"]
        top = max(accessors[p["attributes"]["POSITION"]]["max"][1]
                  for m in self.gltf["meshes"] for p in m["primitives"])
        bottom = min(accessors[p["attributes"]["POSITION"]]["min"][1]
                     for m in self.gltf["meshes"] for p in m["primitives"])
        self.assertAlmostEqual(0.0, bottom, delta=0.02, msg="boots stand on the ground")
        self.assertTrue(1.75 <= top - bottom <= 1.9, f"height {top - bottom:.2f} m")


if __name__ == "__main__":
    unittest.main()
