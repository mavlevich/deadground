"""Regression checks for the wheel envelope and non-intersecting external stowage."""

import contextlib
import io
import math
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from build_t62 import (
    FRONT_IDLER,
    REAR_SPROCKET,
    ROAD_WHEEL_RADIUS,
    ROAD_WHEEL_Y,
    ROAD_WHEEL_Z_POSITIONS,
    build_t62_lod,
    track_profile,
)
from t62_geometry import GltfModel


def distance_to_segment(point, a, b):
    dz, dy = b[0] - a[0], b[1] - a[1]
    length_squared = dz * dz + dy * dy
    t = max(
        0, min(1, ((point[0] - a[0]) * dz + (point[1] - a[1]) * dy) / length_squared)
    )
    return math.hypot(point[0] - a[0] - dz * t, point[1] - a[1] - dy * t)


class T62LayoutTests(unittest.TestCase):
    def test_track_run_encloses_wheels_without_cutting_through_them(self):
        wheels = [FRONT_IDLER, REAR_SPROCKET] + [
            (z, ROAD_WHEEL_Y, ROAD_WHEEL_RADIUS) for z in ROAD_WHEEL_Z_POSITIONS
        ]
        for lod in range(3):
            inner, outer = track_profile(lod)
            for z, y, radius in wheels:
                with self.subTest(lod=lod, wheel=z):
                    clearance = min(
                        distance_to_segment((z, y), a, inner[(i + 1) % len(inner)])
                        for i, a in enumerate(inner)
                    )
                    # Polygon chords approximate circular contact. Dense LOD0 needs
                    # under 1 cm of approximation; lower LODs allow their sagitta.
                    self.assertGreaterEqual(
                        clearance, radius - (0.010, 0.018, 0.030)[lod]
                    )
            self.assertAlmostEqual(min(y for _, y in outer), 0, delta=0.010)

    def test_road_wheels_do_not_overlap(self):
        for a, b in zip(ROAD_WHEEL_Z_POSITIONS, ROAD_WHEEL_Z_POSITIONS[1:]):
            self.assertGreater(b - a, ROAD_WHEEL_RADIUS * 2 + 0.04)

    def test_concave_wheel_faces_point_outward_on_both_sides(self):
        original = GltfModel.save

        def inspect(model, path):
            checked = set()
            for node, parent, material, points, normals, indices, colors in model.parts:
                name = model.data["nodes"][node]["name"]
                if name not in ("DishedWheel", "IdlerDish", "SprocketDish"):
                    continue
                sign = 1 if points[0][0] > 0 else -1
                checked.add((name, sign))
                self.assertTrue(all(n[0] * sign >= -0.00001 for n in normals), name)
            expected = {
                (name, sign)
                for name in ("IdlerDish", "SprocketDish")
                for sign in (-1, 1)
            }
            if model.name != "T62_LOD2":
                expected |= {("DishedWheel", sign) for sign in (-1, 1)}
            self.assertEqual(checked, expected)
            return original(model, path)

        with tempfile.TemporaryDirectory() as temporary, patch.object(
            GltfModel, "save", inspect
        ):
            for lod in range(3):
                with self.subTest(lod=lod), contextlib.redirect_stdout(io.StringIO()):
                    build_t62_lod(lod, Path(temporary))

    def test_front_mudguards_face_up_on_both_sides(self):
        original = GltfModel.save

        def inspect(model, path):
            sides = set()
            for node, parent, material, points, normals, indices, colors in model.parts:
                if model.data["nodes"][node]["name"] != "RolledFrontMudguard":
                    continue
                sides.add(1 if points[0][0] > 0 else -1)
                self.assertTrue(all(n[1] > 0 for n in normals))
                self.assertLess(
                    max(p[1] for p in points) - min(p[1] for p in points), 0.30
                )
            self.assertEqual(sides, {-1, 1})
            return original(model, path)

        with tempfile.TemporaryDirectory() as temporary, patch.object(
            GltfModel, "save", inspect
        ):
            for lod in range(3):
                with self.subTest(lod=lod), contextlib.redirect_stdout(io.StringIO()):
                    build_t62_lod(lod, Path(temporary))

    def test_external_fuel_cells_clear_armor_and_are_supported_above_fenders(self):
        self._check_parts()

    def _check_parts(self):
        original = GltfModel.save

        def inspect(model, path):
            parts = {}
            for node, parent, material, points, normals, indices, colors in model.parts:
                name = model.data["nodes"][node]["name"]
                translation = model.data["nodes"][node]["translation"]
                points = [
                    tuple(p[i] + translation[i] for i in range(3)) for p in points
                ]
                bounds = tuple(
                    (min(p[i] for p in points), max(p[i] for p in points))
                    for i in range(3)
                )
                parts.setdefault(name, []).append(bounds)
            armor = parts["UpperArmor"][0]
            fender_top = max(b[1][1] for b in parts["Fender"])
            self.assertEqual(len(parts["FuelPannier"]), 3)
            for bounds in parts["FuelPannier"]:
                self.assertGreater(bounds[0][0], armor[0][1] + 0.025)
                self.assertGreaterEqual(bounds[1][0], fender_top + 0.025)
                self.assertLessEqual(bounds[1][0] - fender_top, 0.075)
                self.assertLessEqual(bounds[0][1], 1.65)
            self.assertEqual(len(parts["RearFuelDrum"]), 2)
            for bounds in parts["RearFuelDrum"]:
                self.assertGreater(bounds[2][0], armor[2][1] + 0.025)
            return original(model, path)

        with tempfile.TemporaryDirectory() as temporary, patch.object(
            GltfModel, "save", inspect
        ):
            for lod in range(3):
                with self.subTest(lod=lod), contextlib.redirect_stdout(io.StringIO()):
                    build_t62_lod(lod, Path(temporary))


if __name__ == "__main__":
    unittest.main()
