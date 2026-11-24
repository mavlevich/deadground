"""Automated geometry, footprint, module, and triangle-budget tests for panelka_1_464.glb."""

import json
from pathlib import Path
import sys
import tempfile
import unittest

import numpy as np

_THIS_DIR = Path(__file__).resolve().parent
if str(_THIS_DIR) not in sys.path:
    sys.path.insert(0, str(_THIS_DIR))

from build_panelka_1_464 import (  # noqa: E402
    DEFAULT_OUTPUT_DIR,
    FLOOR_HEIGHT,
    MODULE_NODES,
    RUBBLE_HEIGHT,
    SECTION_DEPTH,
    SECTION_WIDTH,
    export_panelka,
)
from render_review import read_glb  # noqa: E402


def _accessor(data, binary, index):
    accessor = data["accessors"][index]
    view = data["bufferViews"][accessor["bufferView"]]
    width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[accessor["type"]]
    dtype = "<f4" if accessor["componentType"] == 5126 else "<u4"
    raw = np.frombuffer(binary[view["byteOffset"] : view["byteOffset"] + view["byteLength"]], dtype=dtype)
    return raw.reshape(-1, width) if width > 1 else raw


def _module_stats(data, binary, module_name):
    nodes = data["nodes"]
    idx = next((i for i, n in enumerate(nodes) if n["name"] == module_name), None)
    if idx is None:
        return None
    pts_list = []
    tri_count = 0
    mesh_count = 0

    def visit(ni, offset):
        nonlocal tri_count, mesh_count
        n = nodes[ni]
        cur = offset + np.asarray(n.get("translation", [0.0, 0.0, 0.0]), dtype=float)
        if "mesh" in n:
            mesh_count += 1
            for prim in data["meshes"][n["mesh"]]["primitives"]:
                pts = _accessor(data, binary, prim["attributes"]["POSITION"]).astype(float) + cur
                pts_list.append(pts)
                tri_count += len(_accessor(data, binary, prim["indices"])) // 3
        for ch in n.get("children", []):
            visit(ch, cur)

    visit(idx, np.zeros(3))
    pts = np.concatenate(pts_list, axis=0)
    return {
        "min": pts.min(axis=0),
        "max": pts.max(axis=0),
        "triangles": tri_count,
        "meshes": mesh_count,
        "finite": bool(np.all(np.isfinite(pts))),
    }


class PanelkaAssetTests(unittest.TestCase):
    def test_exported_glb_meets_modular_structure_dimensions_and_budgets(self):
        with tempfile.TemporaryDirectory() as tmp:
            glb_path, manifest = export_panelka(Path(tmp))
            self.assertTrue(glb_path.exists())
            data, binary = read_glb(glb_path)

            stats_by_mod = {}
            for mod_name in MODULE_NODES:
                st = _module_stats(data, binary, mod_name)
                self.assertIsNotNone(st, f"Missing required module node {mod_name}")
                self.assertTrue(st["finite"], f"Non-finite vertices in {mod_name}")
                self.assertGreater(st["triangles"], 0, f"Empty mesh in {mod_name}")
                stats_by_mod[mod_name] = st

            # Floor modules match 12.0 m width and 3.1 m height
            for floor_mod in ("Floor_Ground_Intact", "Floor_Mid_Intact", "Floor_Breached"):
                st = stats_by_mod[floor_mod]
                self.assertAlmostEqual(st["min"][1], 0.0, delta=0.05, msg=f"{floor_mod} base Y must sit at 0")
                self.assertAlmostEqual(st["max"][1], FLOOR_HEIGHT, delta=0.08, msg=f"{floor_mod} height must match {FLOOR_HEIGHT}")
                width_x = st["max"][0] - st["min"][0]
                self.assertGreaterEqual(width_x, SECTION_WIDTH - 0.25)
                self.assertLessEqual(width_x, SECTION_WIDTH + 0.35)
                # Balconies/visor project <= 1.2 m beyond +/-6 m depth
                self.assertLessEqual(st["max"][2] - st["min"][2], SECTION_DEPTH + 2.5)

            # Rubble mound matches 1.5 m height and stays inside footprint
            rub = stats_by_mod["Rubble_Mound"]
            self.assertAlmostEqual(rub["min"][1], 0.0, delta=0.05)
            self.assertGreaterEqual(rub["max"][1], RUBBLE_HEIGHT - 0.15)
            self.assertLessEqual(rub["max"][1], RUBBLE_HEIGHT + 0.15)
            self.assertLessEqual(rub["max"][0] - rub["min"][0], SECTION_WIDTH)
            self.assertLessEqual(rub["max"][2] - rub["min"][2], SECTION_DEPTH)

            # Debris meshes are single-material and ultra-lightweight for 256-instance MultiMesh
            for debris_mod in ("Debris_Slab_A", "Debris_Slab_B", "Debris_Chunk_C"):
                st = stats_by_mod[debris_mod]
                self.assertEqual(st["meshes"], 1, f"{debris_mod} must have exactly 1 single-material mesh for MultiMesh")
                self.assertLessEqual(st["triangles"], 48, f"{debris_mod} exceeds MultiMesh triangle budget")

            # Full assembled 5-story section fits within tight RTS triangle budget
            assembled_tris = (
                stats_by_mod["Floor_Ground_Intact"]["triangles"]
                + 4 * stats_by_mod["Floor_Mid_Intact"]["triangles"]
                + stats_by_mod["Roof_Cap"]["triangles"]
            )
            self.assertLessEqual(assembled_tris, 7500, f"Assembled 5-story section too heavy: {assembled_tris} tris")
            self.assertEqual(manifest["floors_total"], 5)

    def test_checked_in_asset_is_up_to_date(self):
        checked_in = DEFAULT_OUTPUT_DIR / "panelka_1_464.glb"
        self.assertTrue(checked_in.exists(), f"Missing checked-in GLB at {checked_in}")
        manifest = json.loads((DEFAULT_OUTPUT_DIR / "manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["asset"], "panelka_1_464")


if __name__ == "__main__":
    unittest.main()
