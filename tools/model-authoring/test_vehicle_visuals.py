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

    def test_client_wraps_wheeled_recon_travel(self):
        visuals = json.loads((ASSETS / "visuals.json").read_text())["recon_vehicle"]["factions"]
        manifest = json.loads((ASSETS / "BTR70" / "manifest.json").read_text())
        period = manifest["track_wrap_meters"]
        self.assertAlmostEqual(visuals["soviet"]["track_wrap"], period, places=6)
        radius = manifest["reference_anchors"]["wheel_radius_meters"]
        turns = period / (2 * 3.141592653589793 * radius)
        self.assertAlmostEqual(turns, round(turns), places=6)


if __name__ == "__main__":
    unittest.main()
