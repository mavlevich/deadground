"""The client visual mapping must match the generated faction tank assets."""

import json
import unittest
from pathlib import Path

ASSETS = Path(__file__).resolve().parents[2] / "src/Game.Godot/Assets/Vehicles"


class VehicleVisualsTests(unittest.TestCase):
    def test_client_wraps_track_travel_at_the_exported_period(self):
        visuals = json.loads((ASSETS / "visuals.json").read_text())["prototype_tank"]["factions"]
        for faction, folder in (("soviet", "T62Alt"), ("us", "M60")):
            manifest = json.loads((ASSETS / folder / "manifest.json").read_text())
            period = manifest["track_wrap_meters"]
            cycle = manifest["lods"][0]["track_pattern_cycle"]
            self.assertAlmostEqual(visuals[faction]["track_wrap"], period, places=6, msg=faction)
            # A whole number of pattern cycles, so track links never jump when travel wraps.
            self.assertAlmostEqual(period / cycle, round(period / cycle), places=6, msg=faction)

    def test_client_wraps_wheeled_apc_travel(self):
        visuals = json.loads((ASSETS / "visuals.json").read_text())["apc"]["factions"]
        manifest = json.loads((ASSETS / "BTR70" / "manifest.json").read_text())
        period = manifest["track_wrap_meters"]
        self.assertAlmostEqual(visuals["soviet"]["track_wrap"], period, places=6)
        radius = manifest["reference_anchors"]["wheel_radius_meters"]
        turns = period / (2 * 3.141592653589793 * radius)
        self.assertAlmostEqual(turns, round(turns), places=6)

    def test_historical_types_match_their_assets(self):
        # Historical types map straight to their models at 1:1 scale (TDD section 17.1).
        # Tank manifests carry no gauge, so it comes from the generators that build the tracks.
        import build_m60
        import build_t62_alt

        visuals = json.loads((ASSETS / "visuals.json").read_text())
        btr_manifest = json.loads((ASSETS / "BTR70" / "manifest.json").read_text())
        expected_gauge = {
            "t62": 2 * build_t62_alt.TRACK_X_CENTER,
            "m60a1": 2 * build_m60.TRACK_X_CENTER,
            "btr70": btr_manifest["track_gauge_meters"],
        }
        for unit, folder in (("t62", "T62Alt"), ("m60a1", "M60"), ("btr70", "BTR70")):
            manifest = json.loads((ASSETS / folder / "manifest.json").read_text())
            entry = visuals[unit]
            self.assertEqual(entry["scale"], 1.0, msg=unit)
            self.assertAlmostEqual(entry["track_wrap"], manifest["track_wrap_meters"], places=6, msg=unit)
            self.assertAlmostEqual(entry["track_gauge"], expected_gauge[unit], places=2, msg=unit)
            self.assertEqual(len(entry["lods"]), 2, msg=unit)

if __name__ == "__main__":
    unittest.main()
