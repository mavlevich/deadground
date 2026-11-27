"""Checks of exported Soviet infantry GLBs (AK-74 rifleman and RPG-7V gunner):
one skinned mesh, 53-bone game_engine rig, six clips, vertex colors without textures, budget, height.

Run from the repository root:
  python3 -m unittest tools/model-authoring/infantry/test_soviet_soldier.py
Needs only the Python standard library.
"""

import json
from pathlib import Path
import struct
import unittest

ASSETS_DIR = Path(__file__).resolve().parents[3] / "src/Game.Godot/Assets/Infantry/USSR"
MODELS = [
    ("soviet_rifleman.glb", 10000),
    ("soviet_rpg.glb", 10000),
]
CLIPS = ["Idle", "AimStanding", "AimKneeling", "AimProne", "Walk", "Run"]
GAME_ENGINE_JOINTS = 53


def read_gltf(path):
    data = path.read_bytes()
    magic, version, _length = struct.unpack_from("<III", data, 0)
    if magic != 0x46546C67 or version != 2:
        raise ValueError(f"{path} is not a glTF 2.0 binary")
    chunk_length, chunk_type = struct.unpack_from("<II", data, 12)
    if chunk_type != 0x4E4F534A:
        raise ValueError(f"{path} does not start with a JSON chunk")
    return json.loads(data[20:20 + chunk_length])


class SovietSoldierTests(unittest.TestCase):
    def test_all_models_meet_rig_clip_color_and_budget_contracts(self):
        for filename, max_tris in MODELS:
            with self.subTest(model=filename):
                path = ASSETS_DIR / filename
                self.assertTrue(path.is_file(), f"Missing exported GLB: {path}")
                gltf = read_gltf(path)

                mesh_nodes = [n for n in gltf["nodes"] if "mesh" in n]
                self.assertEqual(1, len(gltf["meshes"]))
                self.assertEqual(1, len(mesh_nodes))
                self.assertIn("skin", mesh_nodes[0])
                self.assertEqual(1, len(gltf["skins"]))
                self.assertEqual(GAME_ENGINE_JOINTS, len(gltf["skins"][0]["joints"]))

                names = [a["name"] for a in gltf.get("animations", [])]
                self.assertEqual(sorted(CLIPS), sorted(names))

                accessors = gltf["accessors"]
                triangles = sum(
                    accessors[p["indices"]]["count"] // 3
                    for m in gltf["meshes"]
                    for p in m["primitives"]
                )
                self.assertLess(triangles, max_tris)
                self.assertGreater(triangles, 2500)

                self.assertEqual([], gltf.get("images", []))
                self.assertEqual([], gltf.get("textures", []))
                for mesh in gltf["meshes"]:
                    for primitive in mesh["primitives"]:
                        self.assertIn("COLOR_0", primitive["attributes"])

                top = max(
                    accessors[p["attributes"]["POSITION"]]["max"][1]
                    for m in gltf["meshes"]
                    for p in m["primitives"]
                )
                bottom = min(
                    accessors[p["attributes"]["POSITION"]]["min"][1]
                    for m in gltf["meshes"]
                    for p in m["primitives"]
                )
                self.assertAlmostEqual(0.0, bottom, delta=0.025, msg="boots stand on the ground")
                self.assertTrue(1.75 <= top - bottom <= 2.15, f"height {top - bottom:.2f} m")


if __name__ == "__main__":
    unittest.main()
