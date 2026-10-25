"""Regressions for design-data drift, not a substitute for runtime map tests."""
import copy
import json
from pathlib import Path
import sys
import unittest
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ustye_plan as plan_tool


class UstyePlanTests(unittest.TestCase):
    def setUp(self):
        self.plan = json.loads(plan_tool.DEFAULT_PLAN.read_text())

    def test_checked_in_plan_and_artifacts_agree(self):
        plan_tool.validate(self.plan)
        folder = plan_tool.DEFAULT_PLAN.parent
        svg = plan_tool.render_svg(self.plan)
        self.assertEqual((folder / 'overview.svg').read_text(), svg)
        self.assertEqual((folder / 'viewer.html').read_text(), plan_tool.render_html(self.plan, svg))
        self.assertEqual(json.loads((folder / 'routes.json').read_text()), plan_tool.route_rows(self.plan))
        self.assertEqual(plan_tool.PACKAGED_PLAN.read_text(), plan_tool.DEFAULT_PLAN.read_text())
        ET.fromstring(svg)

    def test_check_flag_rejects_outdated_artifacts(self):
        # Verify that ustye_plan --check fails when packaged plan does not match
        import subprocess
        import tempfile
        with tempfile.TemporaryDirectory() as tmpdir:
            tmppath = Path(tmpdir) / 'layout.json'
            bad_plan = copy.deepcopy(self.plan)
            bad_plan['roads'][0]['name'] = 'Renamed Central Road'
            tmppath.write_text(json.dumps(bad_plan))
            res = subprocess.run([sys.executable, str(Path(plan_tool.__file__)), '--plan', str(tmppath), '--check'],
                                 capture_output=True, text=True)
            self.assertNotEqual(res.returncode, 0)
            self.assertIn('out of date', res.stderr + res.stdout)

    def test_north_and_extent_cannot_silently_change(self):
        for key, value in [('axes', {'north': '+Z'}), ('extent_m', [5000, 2500])]:
            bad = copy.deepcopy(self.plan)
            bad[key] = value
            with self.assertRaises(ValueError):
                plan_tool.validate(bad)

    def test_crossing_moved_away_from_river_is_rejected(self):
        self.plan['crossings'][0]['center'][0] -= 500
        with self.assertRaises(ValueError):
            plan_tool.validate(self.plan)

    def test_visual_marker_cannot_drift_from_crossing(self):
        marker = next(s for s in self.plan['sites'] if s.get('crossing_id') == 'CROSS-N')
        marker['position'][0] -= 500
        with self.assertRaises(ValueError):
            plan_tool.validate(self.plan)

    def test_deck_must_cover_road_crossing_and_banks(self):
        crossing = self.plan['crossings'][0]
        crossing['deck_endpoints'] = [[2390, 1000], [2410, 1000]]
        with self.assertRaisesRegex(ValueError, 'both banks'):
            plan_tool.validate(self.plan)

    def test_road_cannot_cross_river_outside_declared_deck(self):
        road = self.plan['roads'][0]
        road['points'] = [road['points'][0], [2300, 1800], [2600, 1800], road['points'][-1]]
        with self.assertRaisesRegex(ValueError, 'declared deck'):
            plan_tool.validate(self.plan)

    def test_vehicle_routes_share_measured_endpoints(self):
        road = self.plan['roads'][0]
        road['points'] = road['points'][1:]
        with self.assertRaisesRegex(ValueError, 'endpoint'):
            plan_tool.validate(self.plan)

    def test_single_bridge_closure_keeps_a_vehicle_alternative(self):
        self.assertEqual(plan_tool.available_vehicle_routes(self.plan, {'CROSS-C'}), ['north', 'south'])
        for crossing in self.plan['crossings']:
            self.assertEqual(len(plan_tool.available_vehicle_routes(self.plan, {crossing['id']})), 2)
        self.assertEqual(plan_tool.available_vehicle_routes(self.plan, {'CROSS-N', 'CROSS-C', 'CROSS-S'}), [])

    def test_foot_approach_stays_on_its_bank_and_is_short(self):
        foot = next(r for r in self.plan['roads'] if r['mode'] == 'foot')
        self.assertEqual(plan_tool.road_water_intersections(self.plan, foot), [])
        self.assertLess(plan_tool.polyline_length(foot['points']), 600)
        self.assertEqual(foot['start_site'], 'DROP-N')

    def test_lighthouse_is_on_land_and_bad_coordinates_fail(self):
        site = next(s for s in self.plan['sites'] if s['id'] == 'LIGHT')
        self.assertFalse(plan_tool.point_in_polygon(site['position'], self.plan['water']['bay']))
        for point in ([float('nan'), 0], [5001, 0], [4700, 4700]):
            site['position'] = point
            with self.assertRaises(ValueError):
                plan_tool.validate(self.plan)

    def test_duplicate_ids_are_rejected(self):
        self.plan['sites'][0]['id'] = self.plan['sites'][1]['id']
        with self.assertRaisesRegex(ValueError, 'unique'):
            plan_tool.validate(self.plan)

    def test_building_cannot_span_river_even_with_dry_corners(self):
        structure = self.plan['structures'][0]
        structure['min'] = [2100, 2380]
        structure['size'] = [1000, 18]
        self.assertLess(plan_tool.structure_river_clearance(self.plan, structure), 0)
        with self.assertRaisesRegex(ValueError, 'river bank'):
            plan_tool.validate(self.plan)

    def test_building_cannot_block_central_street(self):
        structure = next(s for s in self.plan['structures'] if s['id'] == 'BLOCK-WEST-3-1')
        structure['min'] = [2040, 2580]
        with self.assertRaisesRegex(ValueError, 'encroaches on road'):
            plan_tool.validate(self.plan)

    def test_panel_districts_are_on_both_banks(self):
        for district in ('TOWN-WEST', 'TOWN-EAST'):
            blocks = [s for s in self.plan['structures'] if s.get('region_id') == district]
            self.assertEqual(len(blocks), 6)
            for block in blocks:
                x, z = block['min']
                river = self.plan['water']['river']
                a, b = next((a, b) for a, b in zip(river, river[1:]) if a[1] <= z <= b[1])
                river_x = a[0] + (z - a[1]) * (b[0] - a[0]) / (b[1] - a[1])
                self.assertEqual(x < river_x, district == 'TOWN-WEST')

    def test_central_approaches_partition_complete_route(self):
        approaches = plan_tool.central_approaches(self.plan)
        route = next(r for r in plan_tool.route_rows(self.plan) if r['id'] == 'central')
        self.assertAlmostEqual(sum(approaches.values()), route['length_m'], delta=1)

    def test_access_spur_must_join_parent_and_is_not_a_crossing_bypass(self):
        self.assertEqual(len(plan_tool.available_vehicle_routes(self.plan, set())), 3)
        self.plan['access_roads'][0]['points'][0][0] += 200
        with self.assertRaisesRegex(ValueError, 'join its declared route'):
            plan_tool.validate(self.plan)


if __name__ == '__main__':
    unittest.main()
