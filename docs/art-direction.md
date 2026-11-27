# Dead Ground: Art Direction and Visual Guidelines

Shared visual baseline for all artists, engineers, and coding agents working on 3D models, materials, terrain, lighting, weather, VFX, and tactical presentation.

## 1. Visual Identity

- **Style:** Restrained stylized realism. Credible Cold War (~1980 +/- 10 years) military hardware and lived-in environments, simplified cleanly without photo clutter, paired with rich lighting, weather, and unmistakable tactical clarity.
- **Core mood:** A calm, believable world disrupted sharply by combat (brief muzzle flashes, earth fountains, smoke closing off an intersection, craters, and wrecks left behind).
- **Proportions:** Strict 1:1 metric scale across infantry, vehicles, helicopters, roads, and buildings. Never distort base (`LOD0`) proportions to force distant readability.

## 2. Theaters and Regions

- **Multi-region scope:** Dead Ground covers diverse Cold War theaters with distinct geographic and operational twists (not limited to linear Central Europe). Planned directions include Central and Eastern Europe, arid mountain and expeditionary zones, northern/subarctic areas, and coastal/island sectors.
- **Calibration baseline region:** Central/Eastern Europe (cool, clear early-autumn day). Every shared asset, material category, and lighting pipeline must first read cleanly in this European baseline before adding regional biomes or alternate weather profiles.

## 3. Geometry and Shading

Match surface shading to real material construction instead of forcing flat facets across everything:

- **Hard surfaces:** Flat faces and crisp edges on rolled armor plates, APC hulls, concrete panelkas, and rigid equipment.
- **Organic and curved surfaces:** Smooth shading on cast tank turrets (T-62, M60), wheels, steel helmets, human limbs and cloth, and tree canopies.
- **Edge lighting:** Use small bevels on primary silhouette edges so directional light defines form cleanly.
- **Simplification:** Remove sub-pixel details that vanish at the primary gameplay camera. If distant readability needs silhouette reinforcement, apply it in far LODs or tactical icons rather than exaggerating `LOD0`.

## 4. Materials and Textures

Avoid a rigid binary between pure vertex colors and heavy 4K photorealistic textures:

- **Hybrid pipeline:** Vertex colors (`COLOR_0`) with baked ambient occlusion provide clean base tones and broad shading. Compact shared textures, trim atlases, decals, and Blender-baked masks are allowed wherever they communicate form, material, scale, or operational history more clearly (terrain, roads, panelka seams and weathering, vehicle camo, engine grilles, mud near running gear).
- **Low-noise rule:** Keep only surface detail that explains form, material, scale, or story (mud by road turnoffs, darkening under windows, worn paths near doors). Avoid uniform random scratches or high-frequency photo noise.
- **Physically grounded PBR:**
  - Painted armor (4BO olive, CARC, painted steel helmets) is dielectric: start at `metallic = 0.0`. Reserve high `metallic` for bare gunmetal, steel tracks, and unpainted fittings.
  - Configure sky lighting and reflection probes before tuning bare metal materials so high-metallic parts do not render black in Godot.
- **Categories vs draw calls:** Artistic material categories (cloth, webbing, painted armor, bare metal, rubber/leather, wood/bakelite, optics) are distinct from runtime draw calls. Merge or pack surfaces on distant LODs so large formations stay cheap to render.

## 5. Terrain and Environment Priority

Terrain, roads, vegetation, and building roofs occupy 70% to 80% of the RTS viewport and carry primary visual weight:

- **Terrain:** Establish broad, readable macro-variation across fields, verges, courtyards, and worn paths first. Add fine surface grain only after macro patches work cleanly from gameplay altitude.
- **Roads:** Build expressive edges (shoulders, ditches, dirt transitions, courtyard turnoffs) that ground the scene and clarify movement routes.
- **Forests:** Use recognizable tree silhouettes arranged in varied density with sharp, tactically readable forest edges.
- **Buildings:** Prioritize roof detail, massing, and ground seating before fine wall detail. Roofs dominate the tactical view.
- **Battle scars:** Craters, local scorch marks, wrecks, and settling dust preserve the visible history of an engagement.

## 6. Aesthetics, Lighting, and Weather

Strong atmosphere, light, and weather are core pillars of the visual experience, built on an honest neutral baseline:

- **Calibration light:** Cool, clear daylight with moderate side sun at **45 to 55 degrees elevation** (avoids hiding units in long shadows and preserves shadow cascade quality on 2 to 5 km maps). Pair a warm directional sun key with cool sky ambient fill, light atmospheric haze, ACES/AgX tonemapping, and restrained bloom reserved strictly for HDR combat flashes and tracers.
- **Weather and time of day:** Once models and terrain validate cleanly in baseline daylight, layer expressive weather and lighting profiles (overcast, dawn, golden hour, fog/mist, dust, rain, snow) tailored to each theater. Atmosphere must enhance mood without obscuring tactical state.
- **Post-processing discipline:** Reserve strong depth-of-field, heavy vignette, and film grain for showcase or photo modes rather than default tactical play.

## 7. Camera Tiers, Side Identification, and Color Hierarchy

Design visual readability explicitly across three camera distances:

- **Close zoom:** Material character, soldier posture and weapon handling, vehicle mechanical details.
- **Primary gameplay zoom:** Unit class, facing, movement/firing action, cover, and routes.
- **Strategic far zoom:** Tactical symbols, group cohesion, and order lines, with 3D meshes supporting context.

Identification and color rules:

- **Historical models:** Keep infantry and vehicle models historically authentic (distinct silhouettes, helmet shapes, real tactical stripes or hull numbers). Do not paint toy-like bright team patches onto uniforms.
- **Markers:** Use reliable tactical markers differentiated by both **shape and color** for instant side recognition on gameplay and far zooms.
- **Color hierarchy:** Terrain and buildings remain restrained in saturation. Only combat VFX and key active signals push into HDR brightness.
- **Channel separation:** Keep UI colors strictly separated across four distinct channels: faction identity, friendly vs hostile relation, selection, and combat state (suppression, low ammo, danger). Never overload one accent color across conflicting meanings.

## 8. VFX, Fog of War, and Combat Readability

- **Event chain first:** Every engagement must communicate **who fired -> where it hit -> what changed** before subtle simulation details.
- **Caliber-distinct VFX:**
  - Small arms: crisp short tracer bursts, small dry dust/spark hits, minimal smoke.
  - Anti-tank rockets / HEAT: clear rear backblast, readable burning tracer and thin contrail, dry compact detonation flash, directional spark spray, and dark RDX soot puff.
  - Tank cannons and artillery: heavy muzzle blast dust, tall earth plumes, and sustained smoke columns.
- **Hit vs destruction:** Non-lethal hits must never look like vehicle kills. Vehicle destruction should vary across immobilized wrecks, fires, and occasional secondary ammo cook-offs rather than identical fireballs.
- **Smoke and fog-of-war honesty:** Dense visual smoke clouds and fog-of-war shading must match the actual LOS-blocking volume in simulation so players can trust the screen.

## 9. Performance Measurement

Low polygon counts alone do not guarantee smooth frame pacing or zero allocations. Always profile actual scenes in Godot across:

- Shadow cascades and directional light cost.
- Skeletal skinning and animation updates.
- Draw calls and MultiMesh/chunk batching.
- Transparent overdraw from smoke layers and foliage.

Follow the allocation and benchmark reporting rules in [AGENTS.md](../AGENTS.md).

## 10. Validation Workflow and Open Parameters

Before locking final numeric triangle, texture, and LOD budgets:

1. **Vertical Slice first:** Build one self-contained ~300 m x 300 m European baseline slice (field, road with shoulders/turnoffs, tree line, two panelkas, tank, APC, rifle/RPG squad) and review it in stillness, movement, combat, smoke, and destruction across all three camera distances.
2. **Calibration Lineup:** Maintain a standing comparison scene placing representative infantry, vehicles, and buildings side by side under identical baseline light and camera zoom. Check every new asset against this lineup before merge.
3. **Open parameters to lock after the Vertical Slice:**
   - Exact min, primary gameplay, and max camera zoom thresholds.
   - Final per-LOD triangle, surface/draw-call, and texture-atlas budgets backed by Godot measurements.
   - Final visual grammar for fog-of-war boundaries, current contacts, and last-known contact markers.
