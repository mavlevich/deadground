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

            # Battle ruins (Rubble_Mound & Rubble_Mound_B) have impassable 2.6-3.7 m corner wall stubs inside footprint
            for rub_name in ("Rubble_Mound", "Rubble_Mound_B"):
                rub = stats_by_mod[rub_name]
                self.assertAlmostEqual(rub["min"][1], 0.0, delta=0.05)
                self.assertGreaterEqual(rub["max"][1], RUBBLE_HEIGHT + 0.5, f"{rub_name} must have standing jagged corner ruins above central rubble saddle")
                self.assertLessEqual(rub["max"][1], 4.0, f"{rub_name} ruin corner walls exceed height budget")
                self.assertLessEqual(rub["max"][0] - rub["min"][0], SECTION_WIDTH + 0.4)
                self.assertLessEqual(rub["max"][2] - rub["min"][2], SECTION_DEPTH + 0.6)

            # Asymmetric jagged top cap sits cleanly at Y = 0 and rises jaggedly for partial collapse skylines
            jagged = stats_by_mod["Top_Jagged_Cap"]
            self.assertAlmostEqual(jagged["min"][1], 0.0, delta=0.05)
            self.assertGreaterEqual(jagged["max"][1], 1.8)
            self.assertLessEqual(jagged["max"][1], 2.8)

            # Every module must merge to exactly 1 single-material mesh (1 draw call per module / MultiMesh batch)
            self.assertEqual(len(data["materials"]), 1, f"Expected 1 unified vertex-colored material, got {len(data['materials'])}")
            for mod_name in MODULE_NODES:
                self.assertEqual(stats_by_mod[mod_name]["meshes"], 1, f"{mod_name} must merge into exactly 1 mesh for single-draw-call rendering")

            # Whole-section LOD progression for sleeping intact MultiMesh rendering
            lod0 = stats_by_mod["Section_Intact_LOD0"]
            lod1 = stats_by_mod["Section_Intact_LOD1"]
            lod2 = stats_by_mod["Section_Intact_LOD2"]
            self.assertGreater(lod0["triangles"], lod1["triangles"], "LOD0 must have more detail than LOD1")
            self.assertGreater(lod1["triangles"], lod2["triangles"], "LOD1 must have more detail than LOD2")
            self.assertLessEqual(lod1["triangles"], 1500, f"LOD1 too heavy: {lod1['triangles']} tris")
            self.assertLessEqual(lod2["triangles"], 250, f"LOD2 too heavy: {lod2['triangles']} tris")
            for lod_name, lod_st in (("Section_Intact_LOD0", lod0), ("Section_Intact_LOD1", lod1), ("Section_Intact_LOD2", lod2)):
                self.assertAlmostEqual(lod_st["min"][1], 0.0, delta=0.05, msg=f"{lod_name} base must rest at Y = 0")
                self.assertGreaterEqual(lod_st["max"][1], 5 * FLOOR_HEIGHT + 0.35, msg=f"{lod_name} must reach full 5-story + parapet height")

            # Forward axis (+Z): front courtyard entrance steps/visor project on +Z, rear -Z ground wall stays flush
            ground = stats_by_mod["Floor_Ground_Intact"]
            self.assertGreater(ground["max"][2], SECTION_DEPTH * 0.5 + 0.5, "Front entrance porch/visor must project along +Z")
            self.assertGreaterEqual(ground["min"][2], -SECTION_DEPTH * 0.5 - 0.3, "Rear -Z ground facade must not have an entrance projection")

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
            self.assertEqual(lod0["triangles"], assembled_tris, "Section_Intact_LOD0 must exactly match assembled 5-story modular triangle count")
            self.assertLessEqual(assembled_tris, 7500, f"Assembled 5-story section too heavy: {assembled_tris} tris")
            self.assertEqual(manifest["floors_total"], 5)

    def test_checked_in_asset_is_up_to_date(self):
        checked_in_glb = DEFAULT_OUTPUT_DIR / "panelka_1_464.glb"
        checked_in_manifest = DEFAULT_OUTPUT_DIR / "manifest.json"
        self.assertTrue(checked_in_glb.exists(), f"Missing checked-in GLB at {checked_in_glb}")
        self.assertTrue(checked_in_manifest.exists(), f"Missing checked-in manifest at {checked_in_manifest}")
        with tempfile.TemporaryDirectory() as tmp:
            rebuilt_glb, _ = export_panelka(Path(tmp))
            rebuilt_manifest = Path(tmp) / "manifest.json"
            self.assertEqual(
                checked_in_glb.read_bytes(),
                rebuilt_glb.read_bytes(),
                "Checked-in panelka_1_464.glb is out of date; re-run build_panelka_1_464.py.",
            )
            self.assertEqual(
                checked_in_manifest.read_text(encoding="utf-8"),
                rebuilt_manifest.read_text(encoding="utf-8"),
                "Checked-in manifest.json is out of date; re-run build_panelka_1_464.py.",
            )
        manifest = json.loads((DEFAULT_OUTPUT_DIR / "manifest.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["asset"], "panelka_1_464")


if __name__ == "__main__":
    unittest.main()
