"""Generator contract checks, independent of the Godot import cache."""

import contextlib
import io
import tempfile
import unittest
from pathlib import Path

from build_t62 import OUTPUT_DIR, generate_all
from t62_geometry import GltfModel


class T62GenerationTests(unittest.TestCase):
    def test_shared_geometry_preserves_plain_tints_by_default(self):
        model = GltfModel("OtherVehicle")
        material = model.material("OtherPaint", "77745e")
        model.add_box("Hull", (2, 1, 3), (0, 0.5, 0), material, tint=0.8)
        self.assertTrue(
            all(color == (0.8, 0.8, 0.8, 1.0) for color in model.parts[0][-1])
        )

    def test_checked_in_assets_match_the_generator(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            with contextlib.redirect_stdout(io.StringIO()):
                generate_all(output)
            for generated in output.iterdir():
                self.assertEqual(
                    generated.read_bytes(),
                    (OUTPUT_DIR / generated.name).read_bytes(),
                    generated.name,
                )

    def test_generation_is_reproducible_in_different_output_directories(self):
        with tempfile.TemporaryDirectory() as temporary:
            first = Path(temporary) / "first"
            second = Path(temporary) / "second"
            with contextlib.redirect_stdout(io.StringIO()):
                generate_all(first)
                generate_all(second)
            expected = {"t62.glb", "t62_lod1.glb", "t62_lod2.glb", "manifest.json"}
            self.assertEqual({p.name for p in first.iterdir()}, expected)
            self.assertEqual({p.name for p in second.iterdir()}, expected)
            for name in expected:
                self.assertEqual(
                    (first / name).read_bytes(), (second / name).read_bytes(), name
                )


if __name__ == "__main__":
    unittest.main()
