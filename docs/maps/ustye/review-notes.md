# Map concept comparison

This review compares the supplied diagrams and source geometry, not a real-world terrain survey. It records why Ustye is the initial integration target and what should be retained from other proposals. No engine or gameplay behavior was demonstrated by these concept documents.

| Proposal | Useful contribution | Main reservation |
|---|---|---|
| Ustye | Readable coastal identity, compact districts, modest synthetic relief and short final infantry approach | Long reserve transit and a river that may dominate too much; no certified LOS or balance |
| Vacha Corridor | Distinct industrial/urban sites and exposure-versus-crossing tradeoffs | Some crossing markers do not lie on the river in the supplied drawing; source and diagram are not ready to import |
| Cerchov Gate | A memorable observation landmark and timber yard with close-range geometry | Same crossing mismatch, long foot leg, and strong concealment/penetration claims that were not computed or implemented |
| Werra Bend | Clear data registry, explicit hypothetical features and a useful bridge-loss supply consequence | Loss of the main bridge deliberately isolates trucks; this needs a clear mission outcome. A long foot approach and amphibious/depth restrictions need additional mechanics |
| Kuppen screening proposal | Recon, delay, withdrawal and evacuation create different player decisions | A scenario pitch rather than a delivered coordinate-level map; retreat play and paired-round balance still require testing |

## Concrete geometry findings

The supplied Vacha and Cerchov generators draw a mathematical river independently of hard-coded crossing markers. Measuring the nearest point on their generated river centerline gives these approximate offsets:

| Proposal | Marker | Offset from drawn river centerline |
|---|---|---:|
| Vacha Corridor | Northern causeway C1 | 573 m |
| Vacha Corridor | Southern ford F1 | 477 m |
| Cerchov Gate | Main bridge B1 | 230 m |
| Cerchov Gate | Northern causeway C1 | 943 m |
| Cerchov Gate | Southern ford F1 | 602 m |

These are errors in the fictional schematic geometry, not statements about actual bridges in Germany or Czechia. Road lines also cross the depicted water away from some named crossings. Pretty annotations do not establish connectivity.

Ustye uses a single plan file for the river, road polylines, deck endpoints and marker positions. Validation rejects the same class of drift. Route lengths are derived rather than independently typed into separate diagrams, source registries and reports. Distance comparisons must use matching endpoints; a complete map-edge route is not the same measurement as a deployment-to-objective route.

## What is combined now

- Keep Ustye's fictional coastal geography and canonical engine coordinate convention.
- Add the timber-yard idea as a separate forest-edge sawmill near the southern river bank, with local road access and a proposed water tower observation deck. Log stacks are planned geometry only. No bullet absorption or sabot penetration rules are inferred from them.
- Place the panel town on both banks around the central bridge. Keep terrain asymmetric, compare approach distances and later test effective cover, observation and fire positions with swapped sides. Similar road length alone is not balance.
- Differentiate crossings by role, route and width. Keep an initial wheeled alternative after the central bridge closes. Add fording and amphibious class restrictions only with their own mechanics and mission rules.
- Reuse the same landscape for a later screen-and-withdraw scenario with evacuation, rather than rebuilding the map around one symmetric meeting engagement.
- Keep forward observation and dead-ground hypotheses, but compute actual terrain/structure/forest visibility before drawing authoritative sight fans.

## Claims deliberately not adopted

- Guaranteed invisibility from a named tower or a precise first-contact time without LOS and scenario measurements.
- Physical vehicle blocking by individual rendered trees as a substitute for movement-class surface rules.
- A fixed 25 to 35 m forest sight limit without sensing tests and data-driven design.
- Working smoke, support surfaces, infantry, ammunition delivery, bridge demolition or traffic avoidance merely because they appear in the TDD.
- Paired matches, a single win-rate threshold or asymmetric faction forces as sufficient proof of terrain balance.

Historical map-sheet references and detailed landmark reconstructions in the competing proposals were not independently authenticated here. Ustye keeps verified broad landscape references and labels its exact layout as fictional.
