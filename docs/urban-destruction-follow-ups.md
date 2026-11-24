# Urban Building Destruction Follow-Ups

This document tracks intentionally deferred work from Stage 1 (PR 1-3) of destructible modular buildings.

## Stage 1 Scope Boundaries
- **Navigation footprint remains impassable after collapse:** Collapsed building sections retain their original `NavigationGrid` obstacle footprint in Stage 1. Heavy concrete panel rubble (~2.2 m high) blocks wheeled and tracked vehicles realistically without requiring runtime `NavigationGrid` rebuilds or hierarchical sector invalidation. Future infantry traversal over low rubble can be layered as a dedicated passability class.
- **Late-joining client facade cosmetics:** `StructureObservation` replicates `Id`, `FloorsStanding`, `FloorBreachMask`, and `Health` (omitting `CurrentHeight`, which is derived identically on server and client). Exact per-panel knockout location is cosmetic from live `StructureDamaged` events; a late-joining client reconstructs breached floors deterministically from `FloorBreachMask`.
- **Client debris physics:** Client falling concrete slabs use deterministic local client-side C# ballistics rendered via `MultiMesh` (never touching match RNG streams and never enabling `PhysicsServer3D` terrain colliders).

## Stage 2 Planned Features
- **Infantry building garrisons:** Allow squads to enter intact/damaged building sections (`ContainerSystem` extension), gain cover protection and elevated sightlines, fire from windows, and take collapse casualties when a floor fails.
- **Helicopter rooftop/courtyard assault:** CH-47D fast-rope insertion onto intact roofs or adjacent courtyards followed by close-quarters assault into enemy-occupied sections.
- **Destructible and burning forests:** Artillery and blast wave tree felling with dynamic `StaticGeometry` vegetation clearings that open real LOS lanes through woods.
