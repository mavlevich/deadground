"""Validation checks for the procedural CH-47D Chinook model assets and manifest."""

import json
import math
import struct
import unittest
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
ASSET_DIR = REPO_ROOT / "src" / "Game.Godot" / "Assets" / "Vehicles" / "CH47"
MANIFEST_PATH = ASSET_DIR / "manifest.json"
REQUIRED_CHILDREN = {
    "Fuselage",
    "FrontRotor",
    "RearRotor",
    "FrontRotorBlur",
    "RearRotorBlur",
    "RearRamp",
}


def load_glb_json(path: Path) -> dict:
    raw = path.read_bytes()
    if len(raw) < 20:
        raise AssertionError(f"{path.name} is too small to be a valid GLB")
    magic, version, total_len = struct.unpack_from("<4sII", raw, 0)
    if magic != b"glTF" or version != 2 or total_len != len(raw):
        raise AssertionError(f"{path.name} has invalid GLB2 header")
    chunk_len, chunk_type = struct.unpack_from("<II", raw, 12)
    if chunk_type != 0x4E4F534A:
        raise AssertionError(f"{path.name} first chunk is not JSON")
    return json.loads(raw[20 : 20 + chunk_len].decode("utf-8"))


def node_by_name(gltf: dict) -> dict[str, dict]:
    return {node["name"]: node for node in gltf["nodes"] if "name" in node}


def mesh_bounds(gltf: dict, mesh_index: int) -> tuple[list[float], list[float]]:
    mins = [math.inf, math.inf, math.inf]
    maxs = [-math.inf, -math.inf, -math.inf]
    for prim in gltf["meshes"][mesh_index]["primitives"]:
        acc = gltf["accessors"][prim["attributes"]["POSITION"]]
        for axis in range(3):
            mins[axis] = min(mins[axis], acc["min"][axis])
            maxs[axis] = max(maxs[axis], acc["max"][axis])
    return mins, maxs


def mesh_triangle_count(gltf: dict) -> int:
    total = 0
    for mesh in gltf["meshes"]:
        for prim in mesh["primitives"]:
            if "indices" in prim:
                total += gltf["accessors"][prim["indices"]]["count"] // 3
            else:
                total += gltf["accessors"][prim["attributes"]["POSITION"]]["count"] // 3
    return total


class Ch47AssetTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(MANIFEST_PATH.exists(), "CH47 manifest.json is missing")
        self.manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))

    def test_manifest_conventions_and_official_anchors(self):
        self.assertEqual(self.manifest["units"], "meters")
        self.assertEqual(self.manifest["up"], "+Y")
        self.assertEqual(self.manifest["forward"], "-Z")
        self.assertEqual(set(self.manifest["articulated_nodes"]), REQUIRED_CHILDREN)

        anchors = self.manifest["reference_anchors"]
        self.assertAlmostEqual(anchors["fuselage_length_meters"], 15.54, places=2)
        self.assertAlmostEqual(anchors["width_over_sponsons_meters"], 3.78, places=2)
        self.assertAlmostEqual(anchors["height_to_rear_rotor_hub_meters"], 5.77, places=2)
        self.assertAlmostEqual(anchors["rotor_diameter_meters"], 18.29, places=2)
        self.assertAlmostEqual(anchors["rotor_hub_spacing_meters"], 11.94, places=2)
        self.assertAlmostEqual(anchors["wheelbase_meters"], 7.87, places=2)

    def test_three_lods_exist_with_monotonic_budgets(self):
        lods = self.manifest["lods"]
        self.assertEqual(len(lods), 3)
        self.assertEqual([item["file"] for item in lods], ["ch47.glb", "ch47_lod1.glb", "ch47_lod2.glb"])

        tris = []
        for item in lods:
            glb_path = ASSET_DIR / item["file"]
            self.assertTrue(glb_path.exists(), f"Missing {item['file']}")
            gltf = load_glb_json(glb_path)
            measured_tris = mesh_triangle_count(gltf)
            self.assertEqual(measured_tris, item["triangles"])
            tris.append(measured_tris)

        self.assertGreater(tris[0], tris[1])
        self.assertGreater(tris[1], tris[2])
        self.assertLessEqual(tris[0], 12000)
        self.assertLessEqual(tris[1], 8000)
        self.assertLessEqual(tris[2], 5000)

    def test_hierarchy_pivots_and_forward_tilts(self):
        for fname in ("ch47.glb", "ch47_lod1.glb", "ch47_lod2.glb"):
            gltf = load_glb_json(ASSET_DIR / fname)
            nodes = node_by_name(gltf)
            self.assertIn("CH47", nodes)
            root = nodes["CH47"]
            child_names = {gltf["nodes"][idx]["name"] for idx in root["children"]}
            self.assertEqual(child_names, REQUIRED_CHILDREN)

            front_t = nodes["FrontRotor"]["translation"]
            rear_t = nodes["RearRotor"]["translation"]
            ramp_t = nodes["RearRamp"]["translation"]

            # Front rotor sits forward (-Z), rear rotor sits aft (+Z) and higher (+Y)
            self.assertAlmostEqual(front_t[0], 0.0, places=3)
            self.assertAlmostEqual(front_t[1], 4.72, places=2)
            self.assertAlmostEqual(front_t[2], -5.09, places=2)

            self.assertAlmostEqual(rear_t[0], 0.0, places=3)
            self.assertAlmostEqual(rear_t[1], 5.77, places=2)
            self.assertAlmostEqual(rear_t[2], 6.85, places=2)
            self.assertAlmostEqual(rear_t[2] - front_t[2], 11.94, places=2)

            # Blur discs match exact hub pivots and mast tilts
            for a, b in (("FrontRotor", "FrontRotorBlur"), ("RearRotor", "RearRotorBlur")):
                for k in range(3):
                    self.assertAlmostEqual(nodes[a]["translation"][k], nodes[b]["translation"][k], places=4)
                for k in range(4):
                    self.assertAlmostEqual(nodes[a]["rotation"][k], nodes[b]["rotation"][k], places=4)

            # Rear ramp hinge sits aft (+Z) near cabin floor height
            self.assertAlmostEqual(ramp_t[0], 0.0, places=3)
            self.assertAlmostEqual(ramp_t[1], 0.78, places=2)
            self.assertAlmostEqual(ramp_t[2], 3.55, places=2)

            # Forward mast tilts 9 deg forward around X, rear mast tilts 4 deg forward around X
            front_rx = -math.sin(math.radians(9.0) * 0.5)
            rear_rx = -math.sin(math.radians(4.0) * 0.5)
            self.assertAlmostEqual(nodes["FrontRotor"]["rotation"][0], front_rx, places=3)
            self.assertAlmostEqual(nodes["RearRotor"]["rotation"][0], rear_rx, places=3)

    def test_scale_grounding_rotor_span_and_packed_textures(self):
        gltf = load_glb_json(ASSET_DIR / "ch47.glb")
        nodes = node_by_name(gltf)

        # Fuselage bounds: grounded at Y=0, length ~15.5-16.2 m along Z, nose toward -Z
        f_min, f_max = mesh_bounds(gltf, nodes["Fuselage"]["mesh"])
        self.assertGreaterEqual(f_min[1], -0.02)
        self.assertLessEqual(f_min[1], 0.03)
        # LOD0 includes protruding twin M60D door-gun barrels beyond the 3.78 m sponsons
        self.assertGreaterEqual(f_max[0] - f_min[0], 3.78)
        self.assertLessEqual(f_max[0] - f_min[0], 4.70)
        self.assertGreaterEqual(f_max[2] - f_min[2], 15.50)
        self.assertLessEqual(f_max[2] - f_min[2], 16.20)
        self.assertLess(f_min[2], -7.50)
        self.assertGreater(f_max[2], 7.50)

        # LOD2 omits door guns, so its X span directly matches the 3.78 m fuel sponsons + nav lights
        gltf_lod2 = load_glb_json(ASSET_DIR / "ch47_lod2.glb")
        nodes_lod2 = node_by_name(gltf_lod2)
        f2_min, f2_max = mesh_bounds(gltf_lod2, nodes_lod2["Fuselage"]["mesh"])
        self.assertAlmostEqual(f2_max[0] - f2_min[0], 3.84, delta=0.12)

        # Rotor blur disc diameter matches official 18.29 m span
        b_min, b_max = mesh_bounds(gltf, nodes["FrontRotorBlur"]["mesh"])
        self.assertAlmostEqual(b_max[0] - b_min[0], 18.29, delta=0.15)
        self.assertAlmostEqual(b_max[2] - b_min[2], 18.29, delta=0.15)

        # Packed embedded images (CARC albedo, roughness, rotor blur, stencil decals) + vertex colors
        self.assertGreaterEqual(len(gltf.get("images", [])), 4)
        for img in gltf["images"]:
            self.assertIn("bufferView", img, "GLB textures must be embedded, not external URIs")
        has_vcol = any(
            "COLOR_0" in prim["attributes"]
            for prim in gltf["meshes"][nodes["Fuselage"]["mesh"]]["primitives"]
        )
        self.assertTrue(has_vcol, "Fuselage must include baked COLOR_0 vertex shading")


if __name__ == "__main__":
    unittest.main()
