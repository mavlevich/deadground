# Urban Building Destruction Follow-Ups

This document tracks intentionally deferred work from Stage 1 (PR 1-3) of destructible modular buildings.

## Stage 1 Scope Boundaries
- **Navigation footprint remains impassable after collapse:** Collapsed building sections retain their original `NavigationGrid` obstacle footprint in Stage 1. Heavy concrete panel rubble (~2.2 m high) blocks wheeled and tracked vehicles realistically without requiring runtime `NavigationGrid` rebuilds or hierarchical sector invalidation. Future infantry traversal over low rubble can be layered as a dedicated passability class.
- **Late-joining client facade cosmetics:** `StructureObservation` replicates `Id`, `FloorsStanding`, `FloorBreachMask`, and `Health` (omitting `CurrentHeight`, which is derived identically on server and client from content/map-hashed `StructureDestructionProfile`). Exact per-panel knockout location is cosmetic from live `StructureDamaged` events (which carry `StructureId`); a late-joining client reconstructs breached floors deterministically from `FloorBreachMask`.
- **Full-state structure snapshot budget:** Damaged or collapsed structures are sent as full 8-byte `StructureObservation` records per snapshot rather than delta-compressed. At 120 damaged sections (30 four-section panelkas), this adds ~960 bytes per snapshot (~1 KB), well within fragment assembly budgets; delta encoding can be added in Stage 2 if dense city maps exceed 256 sections.
- **Event vs. snapshot cadence:** Structure combat events (`StructureDamaged`, `StructureFloorCollapsed`, `StructureCollapsed`) are recorded on impact tick `T`, while `DetectionSystem` refreshes per-side `SideStructureTracker` entries on its next evaluation pass. Client impact VFX/audio should trigger directly from events keyed by `CombatEventObservation.StructureId`, while persistent floor height and FoW state track `ClientReplica.Structures`.
- **Client debris physics:** Client falling concrete slabs use deterministic local client-side C# ballistics rendered via `MultiMesh` (never touching match RNG streams and never enabling `PhysicsServer3D` terrain colliders).

## Stage 2 Planned Features
- **Infantry building garrisons:** Allow squads to enter intact/damaged building sections (`ContainerSystem` extension), gain cover protection and elevated sightlines, fire from windows, and take collapse casualties when a floor fails.
- **Helicopter rooftop/courtyard assault:** CH-47D fast-rope insertion onto intact roofs or adjacent courtyards followed by close-quarters assault into enemy-occupied sections.
- **Destructible and burning forests:** Artillery and blast wave tree felling with dynamic `StaticGeometry` vegetation clearings that open real LOS lanes through woods.
