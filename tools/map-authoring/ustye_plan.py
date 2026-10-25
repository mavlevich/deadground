"""Validate and render the Ustye design plan. This is not a runtime map exporter."""
import argparse
import html
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_PLAN = ROOT / 'docs/maps/ustye/layout.json'
PACKAGED_PLAN = ROOT / 'src/Game.Godot/Maps/ustye/layout.json'


def distance_to_segment(point, start, end):
    delta = [end[i] - start[i] for i in (0, 1)]
    squared = sum(v * v for v in delta)
    t = max(0, min(1, sum((point[i] - start[i]) * delta[i] for i in (0, 1)) / squared)) if squared else 0
    return math.dist(point, [start[i] + t * delta[i] for i in (0, 1)])


def polyline_length(points):
    return sum(math.dist(a, b) for a, b in zip(points, points[1:]))


def structure_corners(structure):
    x, z = structure['min']
    w, h = structure['size']
    return [[x, z], [x + w, z], [x + w, z + h], [x, z + h]]


def structure_polyline_distance(structure, points):
    corners = structure_corners(structure)
    edges = list(zip(corners, corners[1:] + corners[:1]))
    distances = []
    for a, b in zip(points, points[1:]):
        if point_in_polygon(a, corners) or point_in_polygon(b, corners):
            return 0
        for c, d in edges:
            if intersection(a, b, c, d):
                return 0
            distances.extend((distance_to_segment(a, c, d), distance_to_segment(b, c, d),
                              distance_to_segment(c, a, b), distance_to_segment(d, a, b)))
    return min(distances)


def structure_river_clearance(plan, structure):
    return structure_polyline_distance(structure, plan['water']['river']) - plan['water']['river_width_m'] / 2


def central_approaches(plan):
    road = next(r for r in plan['roads'] if r['id'] == 'central')
    center = next(c['center'] for c in plan['crossings'] if c['id'] == road['requires_crossing'])
    traveled = 0
    for a, b in zip(road['points'], road['points'][1:]):
        if distance_to_segment(center, a, b) < .001:
            first = traveled + math.dist(a, center)
            return {'A': round(first), 'B': round(polyline_length(road['points']) - first)}
        traveled += math.dist(a, b)
    raise ValueError('Central bridge is not on its road.')


def cross(a, b):
    return a[0] * b[1] - a[1] * b[0]


def intersection(a, b, c, d):
    u = [b[i] - a[i] for i in (0, 1)]
    v = [d[i] - c[i] for i in (0, 1)]
    denominator = cross(u, v)
    if abs(denominator) < 1e-9:
        return None
    offset = [c[i] - a[i] for i in (0, 1)]
    t, s = cross(offset, v) / denominator, cross(offset, u) / denominator
    if -1e-9 <= t <= 1 + 1e-9 and -1e-9 <= s <= 1 + 1e-9:
        return [a[i] + t * u[i] for i in (0, 1)]
    return None


def point_in_polygon(point, polygon):
    inside = False
    x, z = point
    for a, b in zip(polygon, polygon[1:] + polygon[:1]):
        if (a[1] > z) != (b[1] > z) and x < (b[0] - a[0]) * (z - a[1]) / (b[1] - a[1]) + a[0]:
            inside = not inside
    return inside


def road_water_intersections(plan, road):
    found = []
    river = plan['water']['river']
    for a, b in zip(road['points'], road['points'][1:]):
        for c, d in zip(river, river[1:]):
            hit = intersection(a, b, c, d)
            if hit and not any(math.dist(hit, old) < 1e-4 for old in found):
                found.append(hit)
    return found


def deck_contains(point, crossing):
    a, b = crossing['deck_endpoints']
    delta = [b[i] - a[i] for i in (0, 1)]
    squared = sum(v * v for v in delta)
    if not squared:
        return False
    t = sum((point[i] - a[i]) * delta[i] for i in (0, 1)) / squared
    return -1e-6 <= t <= 1 + 1e-6 and distance_to_segment(point, a, b) <= crossing['deck_width_m'] / 2 + 1e-6


def sample_height(plan, x, z):
    model = plan['height_model']
    return model['base_m'] + sum(p['amplitude_m'] * math.exp(
        -((x - p['center_m'][0]) / p['sigma_x_m']) ** 2
        -((z - p['center_m'][1]) / p['sigma_z_m']) ** 2) for p in model['peaks'])


def route_rows(plan):
    rows = []
    for road in plan['roads']:
        meters = polyline_length(road['points'])
        speed = plan['timing_assumptions'][road['mode'] + '_kmh']
        rows.append({'id': road['id'], 'name': road['name'], 'mode': road['mode'],
                     'length_m': round(meters), 'minutes': round(meters / (speed * 1000 / 60), 1),
                     'crossing': road['requires_crossing']})
    return rows


def available_vehicle_routes(plan, blocked):
    return [road['id'] for road in plan['roads'] if road['mode'] == 'vehicle'
            and road['requires_crossing'] not in blocked]


def validate(plan):
    if plan.get('kind') != 'map_design' or plan.get('status') != 'schematic_only':
        raise ValueError('This tool accepts design schematics only.')
    if plan['schema_version'] != 1 or plan['extent_m'] != [5000, 5000]:
        raise ValueError('Expected version 1 and a 5000 m square.')
    if plan['axes'] != {'origin': 'northwest', 'x': 'east', 'z': 'south', 'north': '-Z', 'up': '+Y'}:
        raise ValueError('Coordinate convention does not match the engine.')
    ids = []
    for group in ('regions', 'roads', 'access_roads', 'crossings', 'sites', 'deployments', 'structures', 'scenarios'):
        ids.extend(item['id'] for item in plan[group])
    if len(ids) != len(set(ids)):
        raise ValueError('Feature IDs must be unique.')

    def check_point(point):
        if len(point) != 2 or any(not math.isfinite(v) or not 0 <= v <= 5000 for v in point):
            raise ValueError('Nonfinite or out-of-bounds coordinate.')

    geometries = [plan['water']['river'], plan['water']['bay']]
    geometries.extend(item['polygon'] for group in ('regions', 'deployments') for item in plan[group])
    geometries.extend(item['points'] for item in plan['roads'] + plan['access_roads'])
    geometries.extend(item['deck_endpoints'] for item in plan['crossings'])
    for geometry in geometries:
        for point in geometry:
            check_point(point)
    for site in plan['sites']:
        check_point(site['position'])
        if point_in_polygon(site['position'], plan['water']['bay']):
            raise ValueError(f"Site {site['id']} is in the bay.")
    for structure in plan['structures']:
        check_point(structure['min'])
        check_point([structure['min'][i] + structure['size'][i] for i in (0, 1)])
        if any(v <= 0 for v in structure['size']) or structure['height_m'] <= 0:
            raise ValueError('Invalid structure size.')
        if structure_river_clearance(plan, structure) < 20:
            raise ValueError('Structure encroaches on the river bank setback.')
        region = next((r for r in plan['regions'] if r['id'] == structure['region_id']), None)
        if region is None or not all(point_in_polygon(p, region['polygon']) for p in structure_corners(structure)):
            raise ValueError('Structure is outside its declared district.')
    if plan['water']['river_width_m'] <= 0:
        raise ValueError('Invalid river width.')
    crossings = {c['id']: c for c in plan['crossings']}
    sites = {site['id']: site for site in plan['sites']}
    for site in sites.values():
        if 'crossing_id' in site:
            target = crossings.get(site['crossing_id'])
            if target is None or math.dist(site['position'], target['center']) > 1:
                raise ValueError('Crossing marker does not match its deck.')
    roads = {r['id']: r for r in plan['roads']}
    for crossing in crossings.values():
        check_point(crossing['center'])
        a, b = crossing['deck_endpoints']
        if crossing['deck_width_m'] <= 0 or math.dist(a, b) <= 0:
            raise ValueError('Invalid crossing deck.')
        offset = min(distance_to_segment(crossing['center'], a, b)
                     for a, b in zip(plan['water']['river'], plan['water']['river'][1:]))
        if offset > 1:
            raise ValueError(f"{crossing['id']} is not on the river centerline.")
        for end in crossing['deck_endpoints']:
            bank_distance = min(distance_to_segment(end, a, b) for a, b in zip(plan['water']['river'], plan['water']['river'][1:]))
            if bank_distance < plan['water']['river_width_m'] / 2 + 5:
                raise ValueError('Crossing deck does not reach both banks.')
        if not deck_contains(crossing['center'], crossing):
            raise ValueError('Crossing center is outside its deck.')
        if crossing['road_id'] not in roads or roads[crossing['road_id']]['requires_crossing'] != crossing['id']:
            raise ValueError('Crossing-road reference mismatch.')
    for road in plan['roads'] + plan['access_roads']:
        if road['mode'] not in ('vehicle', 'foot') or road['width_m'] <= 0:
            raise ValueError('Invalid road mode or width.')
        endpoints = [('end_site', road['points'][-1])]
        if 'start_road' in road:
            parent = roads.get(road['start_road'])
            if parent is None or min(distance_to_segment(road['points'][0], a, b)
                                     for a, b in zip(parent['points'], parent['points'][1:])) > 1:
                raise ValueError('Access road does not join its declared route.')
        else:
            endpoints.append(('start_site', road['points'][0]))
        for field, endpoint in endpoints:
            target = sites.get(road[field])
            if target is None or math.dist(endpoint, target['position']) > 1:
                raise ValueError('Road endpoint does not match its declared site.')
        hits = road_water_intersections(plan, road)
        required = road['requires_crossing']
        if required:
            if required not in crossings or len(hits) != 1 or not deck_contains(hits[0], crossings[required]):
                raise ValueError(f"Road {road['id']} does not cross through its declared deck.")
        elif hits:
            raise ValueError('Undeclared water crossing.')
        for a, b in zip(road['points'], road['points'][1:]):
            # Conservative planning samples, not a runtime collision or slope test.
            steps = max(1, math.ceil(math.dist(a, b) / 10))
            if any(point_in_polygon([a[k] + (b[k] - a[k]) * i / steps for k in (0, 1)], plan['water']['bay']) for i in range(steps + 1)):
                raise ValueError('Road enters the bay.')
    for crossing in crossings:
        if not available_vehicle_routes(plan, {crossing}):
            raise ValueError('Single crossing closure isolates vehicle entries.')
    for structure in plan['structures']:
        for road in plan['roads'] + plan['access_roads']:
            if structure_polyline_distance(structure, road['points']) < road['width_m'] / 2 + 3:
                raise ValueError(f"Structure {structure['id']} encroaches on road {road['id']}.")
    for speed in ('vehicle_kmh', 'foot_kmh'):
        if not math.isfinite(plan['timing_assumptions'][speed]) or plan['timing_assumptions'][speed] <= 0:
            raise ValueError('Invalid timing assumption.')


def contour_segments(plan, spacing=100):
    values = [[sample_height(plan, x, z) for x in range(0, 5001, spacing)] for z in range(0, 5001, spacing)]
    result = []
    for level in (10, 20, 30, 40):
        for row in range(len(values) - 1):
            for col in range(len(values[0]) - 1):
                points = [[col * spacing, row * spacing], [(col + 1) * spacing, row * spacing],
                          [(col + 1) * spacing, (row + 1) * spacing], [col * spacing, (row + 1) * spacing]]
                heights = [values[row][col], values[row][col + 1], values[row + 1][col + 1], values[row + 1][col]]
                hits = []
                for a, b in ((0, 1), (1, 2), (2, 3), (3, 0)):
                    if (heights[a] < level) != (heights[b] < level):
                        t = (level - heights[a]) / (heights[b] - heights[a])
                        hits.append([points[a][i] + t * (points[b][i] - points[a][i]) for i in (0, 1)])
                if len(hits) == 2:
                    result.append((level, hits))
                elif len(hits) == 4:
                    # Saddle cells are a schematic contour ambiguity, not LOS evidence.
                    result.extend([(level, hits[:2]), (level, hits[2:])])
    return result


def render_svg(plan):
    scale, cx, cy = .14, 600, 590

    def point(p):
        x, z = p
        return cx + (x - z) / math.sqrt(2) * scale, cy + (x + z - 5000) / math.sqrt(2) * scale

    def coords(points):
        return ' '.join(f'{x:.2f},{y:.2f}' for x, y in map(point, points))

    def path(points):
        return 'M' + ' L'.join(f'{x:.2f},{y:.2f}' for x, y in map(point, points))

    def esc(value):
        return html.escape(str(value), quote=True)

    out = ['<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1200 1260" role="img" aria-labelledby="title desc">',
           '<title id="title">Ustye: Backwater Crossing, 5 km design plan</title>',
           '<desc id="desc">Fictional coastal map. Riverside town on both banks of the central bridge, three crossings, woodland, depot, sawmill and proposed water tower observation deck. Design only.</desc>',
           '<defs><clipPath id="map-clip"><polygon points="' + coords([[0, 0], [5000, 0], [5000, 5000], [0, 5000]]) + '"/></clipPath>',
           '<pattern id="forest-hatch" width="9" height="9" patternUnits="userSpaceOnUse"><path d="M0 9 L9 0" stroke="#4c6548" stroke-width=".6" opacity=".5"/></pattern></defs>',
           '<style>text{font-family:Arial,Helvetica,sans-serif;fill:#30382e} .title{font-size:27px;font-weight:bold}.meta{font-size:14px}.label{font-size:15px;font-weight:bold}.number{font-size:14px;text-anchor:middle;dominant-baseline:central}.small{font-size:13px}</style>',
           '<rect width="1200" height="1260" fill="#f4f1e8"/>',
           '<text x="600" y="40" text-anchor="middle" class="title">USTYE: BACKWATER CROSSING</text>',
           '<text x="600" y="68" text-anchor="middle" class="meta">Fictional design | 5000 x 5000 m | 25 km2 | north = -Z | no verified LOS or gameplay</text>',
           '<polygon points="' + coords([[0, 0], [5000, 0], [5000, 5000], [0, 5000]]) + '" fill="#e4dfc6" stroke="#59614f" stroke-width="1.5"/>',
           '<g clip-path="url(#map-clip)">', '<g id="layer-terrain">']
    colors = {'dense_forest': '#819573', 'sparse_forest': '#b1c1a0', 'urban': '#c1bcb2', 'industrial': '#c4ad83', 'candidate_dead_ground': '#d4d4b1'}
    for region in plan['regions']:
        out.append(f'<polygon points="{coords(region["polygon"])}" fill="{colors[region["kind"]]}" stroke="#718166" stroke-width=".6"><title>{esc(region["id"])}</title></polygon>')
    out.append(f'<polygon points="{coords(plan["water"]["bay"])}" fill="#a8c7cf"/>')
    out.append(f'<path d="{path(plan["water"]["river"])}" fill="none" stroke="#91b7c4" stroke-width="{plan["water"]["river_width_m"] * scale}" stroke-linejoin="round"/>')
    out.append('</g><g id="layer-height" opacity=".5">')
    for level, segment in contour_segments(plan):
        out.append(f'<path d="{path(segment)}" fill="none" stroke="#978a66" stroke-width=".8"/>')
    out.append('</g><g id="layer-mobility" style="display:none">')
    for region in plan['regions']:
        if region['kind'] == 'dense_forest':
            out.append(f'<polygon points="{coords(region["polygon"])}" fill="url(#forest-hatch)"/>')
    out.append('</g><g id="layer-roads">')
    for road in plan['roads'] + plan['access_roads']:
        if road['mode'] == 'vehicle':
            out.append(f'<path d="{path(road["points"])}" fill="none" stroke="#f9f3df" stroke-width="7" stroke-linejoin="round"/>')
            out.append(f'<path d="{path(road["points"])}" fill="none" stroke="#7c7868" stroke-width="2" stroke-linejoin="round"/>')
        else:
            out.append(f'<path d="{path(road["points"])}" fill="none" stroke="#9b5142" stroke-width="2" stroke-dasharray="4 5"/>')
    for crossing in plan['crossings']:
        out.append(f'<path d="{path(crossing["deck_endpoints"])}" fill="none" stroke="#6b6254" stroke-width="{max(3, crossing["deck_width_m"] * scale)}"><title>{esc(crossing["id"])}: explicit deck axis</title></path>')
    out.append('</g><g id="layer-structures">')
    for structure in plan['structures']:
        x, z = structure['min']; w, h = structure['size']
        fill = '#9a9c96' if structure['kind'] == 'panel_block' else '#ae9271'
        out.append(f'<polygon points="{coords([[x,z],[x+w,z],[x+w,z+h],[x,z+h]])}" fill="{fill}" stroke="#535b4c" stroke-width=".6"><title>{esc(structure["id"])}</title></polygon>')
    out.append('</g><g id="layer-deployments">')
    for deploy in plan['deployments']:
        out.append(f'<polygon points="{coords(deploy["polygon"])}" fill="none" stroke="#716f67" stroke-width="1.3" stroke-dasharray="5 5"/>')
    out.append('</g><g id="layer-labels">')
    for site in plan['sites']:
        x, y = point(site['position'])
        fill = '#557c91' if site['label'] == 'A' else '#9f6254' if site['label'] == 'B' else '#f4f1e8'
        text = '#ffffff' if site['kind'] == 'spawn' else '#30382e'
        out.append(f'<g><title>{esc(site["id"])}: {esc(site["name"])}</title><circle cx="{x:.2f}" cy="{y:.2f}" r="12" fill="{fill}" stroke="#394c3e"/><text x="{x:.2f}" y="{y:.2f}" class="number" style="fill:{text}">{esc(site["label"])}</text></g>')
        if 'caption' in site:
            dx, dy = site['caption_offset_px']
            anchor = 'end' if dx < 0 else 'start'
            out.append(f'<text x="{x + dx:.2f}" y="{y + dy:.2f}" text-anchor="{anchor}" class="small" style="paint-order:stroke;stroke:#f4f1e8;stroke-width:4;stroke-linejoin:round">{esc(site["caption"])}</text>')
    out.extend(['</g></g>', '<path d="M1045 150 L1085 110 l-10 3 m10-3 l-3 10" fill="none" stroke="#30382e" stroke-width="2"/>',
                '<text x="1030" y="170" class="label">N</text>',
                '<path d="M100 1110 h140" stroke="#30382e" stroke-width="3"/><text x="170" y="1098" text-anchor="middle" class="meta">1 km</text>',
                '<text x="600" y="1135" text-anchor="middle" class="meta">1 north crossing | 2 central bridge | 3 south crossing | 4 church | 5 depot</text>',
                '<text x="600" y="1160" text-anchor="middle" class="meta">6 riverside town | 7 radio hill | 8 lighthouse | 9 dismount | 10 sawmill | 11 water tower</text>',
                '<text x="600" y="1190" text-anchor="middle" class="small">Dense woods: foot off-road, vehicles on declared roads. Water: declared crossings only.</text>',
                '<text x="600" y="1215" text-anchor="middle" class="small">Contours: synthetic 10 / 20 / 30 / 40 m. Roads and markers are schematic; structure footprints use meters.</text>',
                '<text x="600" y="1240" text-anchor="middle" class="small">Planning geometry only. No support surfaces, traffic, sensing, occupancy, supply or destruction implementation.</text>', '</svg>'])
    return '\n'.join(out) + '\n'


def render_html(plan, svg):
    controls = ''.join(f'<label><input type="checkbox" data-layer="layer-{name}" {"" if name == "mobility" else "checked"}> {name.title()}</label>' for name in ('terrain', 'height', 'mobility', 'roads', 'structures', 'deployments', 'labels'))
    rows = ''.join(f'<tr><td>{html.escape(r["name"])}</td><td>{r["length_m"]} m</td><td>{r["minutes"]} min</td></tr>' for r in route_rows(plan))
    approaches = central_approaches(plan)
    access_note = f'<p>Central bridge approach: A {approaches["A"]} m; B {approaches["B"]} m. Road distance only, not a balance or LOS result. Depot and sawmill spurs are local access roads, not additional crossing routes.</p>'
    return '''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Ustye map plan</title>
<style>body{max-width:1000px;margin:24px auto;padding:0 16px;font:16px/1.5 system-ui;background:#f4f1e8;color:#30382e}nav{display:flex;flex-wrap:wrap;gap:12px}svg{display:block;width:100%;height:auto}table{width:100%;border-collapse:collapse}td,th{text-align:left;padding:8px;border-bottom:1px solid #c5c5b6}label{display:flex;align-items:center;gap:4px}</style>
<h1>Ustye map plan</h1><p>Schematic only. These controls change the diagram, not game rules.</p><nav>''' + controls + '</nav>' + svg + '''<h2>Geometric route estimates</h2><p>Vehicles: constant 30 km/h. Foot: constant 6 km/h. These are complete polylines, not first contact or game measurements. Terrain, steering and combat costs are omitted.</p><table><thead><tr><th>Route</th><th>Length</th><th>Time</th></tr></thead><tbody>''' + rows + '''</tbody></table><p>Closing the central bridge leaves both vehicle bypasses in the plan. Runtime connectivity still requires implementation and tests.</p>
''' + access_note + '''<script>for(const input of document.querySelectorAll('[data-layer]'))input.addEventListener('change',()=>{document.getElementById(input.dataset.layer).style.display=input.checked?'':'none';});</script></html>
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--plan', type=Path, default=DEFAULT_PLAN)
    parser.add_argument('--out', type=Path, default=DEFAULT_PLAN.parent)
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--png', action='store_true', help='Also render PNG; requires matplotlib.')
    args = parser.parse_args()
    plan = json.loads(args.plan.read_text())
    validate(plan)
    if args.check:
        svg = render_svg(plan)
        expected_svg = args.out / 'overview.svg'
        if not expected_svg.exists() or expected_svg.read_text() != svg:
            raise ValueError('overview.svg is missing or out of date. Run without --check to regenerate.')
        expected_html = args.out / 'viewer.html'
        if not expected_html.exists() or expected_html.read_text() != render_html(plan, svg):
            raise ValueError('viewer.html is missing or out of date. Run without --check to regenerate.')
        expected_routes = args.out / 'routes.json'
        if not expected_routes.exists() or json.loads(expected_routes.read_text()) != route_rows(plan):
            raise ValueError('routes.json is missing or out of date. Run without --check to regenerate.')
        if not PACKAGED_PLAN.exists() or PACKAGED_PLAN.read_text() != args.plan.read_text():
            raise ValueError(f'Packaged plan {PACKAGED_PLAN} is missing or out of date. Run without --check to regenerate.')
    else:
        args.out.mkdir(parents=True, exist_ok=True)
        svg = render_svg(plan)
        (args.out / 'overview.svg').write_text(svg)
        (args.out / 'viewer.html').write_text(render_html(plan, svg))
        (args.out / 'routes.json').write_text(json.dumps(route_rows(plan), indent=2) + '\n')
        PACKAGED_PLAN.parent.mkdir(parents=True, exist_ok=True)
        PACKAGED_PLAN.write_text(args.plan.read_text())
        if args.png:
            from render_ustye_png import render_png
            render_png(plan, args.out / 'overview.png')
    print(f"USTYE_PLAN_OK schematic_only=1 crossings={len(plan['crossings'])}")
    for row in route_rows(plan):
        print(f"{row['id']}: {row['length_m']} m, {row['minutes']} min")


if __name__ == '__main__':
    main()
