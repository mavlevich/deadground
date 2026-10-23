# Dead Ground: Technical Design

Status: draft v0.6, M0 client exports implemented, native Windows gameplay check pending

This document fixes the technical decisions that are expensive to change later. It does not describe balance, unit stats or content. Each decision should be either fixed, or marked as open with the experiment that will decide it.

Good architecture does not mean "we can add anything without rewriting". It means a new feature touches the systems we expect, and does not break networking, saves, AI and UI at the same time.

## 1. Game in one paragraph

A real-time tactical RTS set in an alternative Cold War (1960s to 1980s). Each player commands a limited battlegroup of about 20 to 40 units, with a small reserve. There is no base building and no unit production. Victory comes from reconnaissance, use of terrain, combined arms and supply, not from out-producing the enemy. Units counter each other. Multiplayer PvP (1v1, later 2v2) and games against a bot are the primary modes.

Reference games: World in Conflict, Broken Arrow, Company of Heroes. The goal is our own mix with a stronger focus on logistics and tactics.

## 2. Goals and non-goals

Goals:

- Server-authoritative multiplayer with server-side fog of war.
- Windows and macOS clients that play against each other.
- Dedicated server on Linux.
- A bot that plays by the same rules and sees only what its side knows.
- Content defined by data. A new tank is a data file, not a class.
- Clear seams so rules can be disabled or replaced (see section 19).

Non-goals for now:

- Deterministic lockstep.
- Maps larger than 15 x 15 km.
- Thousands of individually simulated soldiers.
- Full physics simulation of vehicles or projectiles.
- Streaming, floating origin, simulation LOD, sleeping entities, multithreaded systems. We keep a place for them in the architecture but do not build them until a profiler asks for them.

## 3. Fixed decisions

1. 1 world unit = 1 meter. Time in the simulation is measured in ticks.
2. The simulation core (`Engine.Core`, `Game.Rules.*`) has no dependency on Godot. This is enforced by project references.
3. Server-authoritative. Clients send commands, the server sends state.
4. Networking uses snapshots and interpolation, not lockstep.
5. Single player runs a local server in the same process and talks to it through the same transport interface (in-memory implementation).
6. Fixed simulation tick of 20 Hz. Slower systems run on a schedule inside it.
7. Stable entity IDs that are never reused within a match.
8. Three separate views of the game: `WorldState`, `PlayerKnowledge`, `ClientReplica` (section 7).
9. Persistent facts live in state. Events are hints for effects and may be lost (section 13).
10. The simulation world is 2.5D: heightmap plus raster layers plus structures with height (section 8).
11. Infantry squads are one entity. Soldiers are data inside the squad (section 11).
12. One generic container component for transports and buildings.
13. The bot uses the same command API as players and reads only its own `PlayerKnowledge`.
14. Components are registered with save, replication and quantization rules (section 6.2).
15. Determinism is required only for the same build on the same machine. Replays are recorded snapshot streams.
16. No allocations in the per-tick hot path.
17. Path planning uses our own cost grid and graphs in `Engine.Core`, not Godot navigation (section 9). Reason: per-side knowledge layers (known minefields) and cheap updates when the world changes.
18. The dedicated server is a plain .NET console app. Godot is never needed on the server.
19. Clients never receive enemy `EntityId` values, not even for identified units. Enemies are always contacts (section 7.1).
20. Ground units have a support surface: terrain or a structure deck such as a bridge (section 8.3).
21. Every save, replay and command log starts with a `MatchSetup` header that pins all match inputs (section 15).

## 4. Open questions

| Question | Options | How we decide |
|---|---|---|
| Entity storage | Arch ECS (provisional choice), or plain arrays per component | Start with Arch. Storage stays hidden inside `Engine.Core`, so a switch is local. Revisit if it gets in the way of saves or snapshots. |
| Allied intel in 2v2 | Full sharing, or delayed or limited sharing | Game design decision. Hostility is evaluated by team mapping (allies on the same team are never tracked as enemy contacts), but how contact intelligence is shared or grouped across allied players is deferred to 2v2. |
| Contact correlation tuning | Time window and dynamic speed-based distance gate for keeping the same contact ID | Play testing in M1 (section 7.1). |
| Save format | MemoryPack in version-tolerant mode, or our own binary writer | Decided in M1 when the first save is implemented. |
| Automatic reverse | Reverse only on explicit order, or also when the destination is close behind the vehicle | Play testing after heading lands (section 9.5). |
| Direct fire numbers | Hit probability curves, movement penalties, ranging bonus, armor values per side | First values from period sources, then play testing (section 12.3). |
| Historical estimates | Off-road and reverse speeds, light weapon ranges and visual detection ranges without a period source | Replace with period sources when found (`docs/unit-sources.md`); until then tune in play inside the stated range (section 17.1). |
| Blind fire feedback | What the shooter learns from area fire at an unseen target | Must never reveal an unobserved hit; decided with section 12.3. |

## 5. Architecture overview

### 5.1 Layers

```
Content          data files, models, sounds, maps, UI theme
Presentation     Godot: views, effects, audio, camera, UI
Game rules       combat, detection rules, logistics, objectives
Engine           entities, time, commands, space, terrain, navigation,
                 LOS, networking, saves, replays
```

The engine knows nothing about the Cold War. No "tank", "diesel" or "Soviet" in engine code.

### 5.2 Projects

```
deadground/
  src/
    Engine.Core/          entities, components registry, scheduler, time,
                          RNG, commands, events, spatial index, terrain,
                          navigation, LOS, knowledge model, serialization
    Engine.Net/           transport interface, LiteNetLib and in-memory
                          transports, snapshot building, knowledge filter
    Game.Rules.Ops/       main rule set: combat, damage, detection rules,
                          logistics, artillery, objectives, AI
    Game.Rules.Sandbox/   test rule set: no logistics, infinite ammo
    Game.Server/          dedicated server host (console app)
    Game.Godot/           Godot client: presentation, UI, input, local host
  content/
    coldwar/              unit, weapon, sensor, faction definitions
    prototype/            test unit data for the small test maps
  maps/                   exported map data for the simulation
  tests/
    Engine.Core.Tests/
    Game.Rules.Tests/
    Benchmarks/
  tools/
    MapExport/            exports map data from the editor
  docs/
```

### 5.3 Dependency rules

- `Engine.*` must not reference `Game.*` or Godot.
- `Game.Rules.*` may reference `Engine.*`, never Godot.
- `Game.Godot` may reference everything.
- `Game.Server` must not reference Godot.
- Core math uses `System.Numerics`. Conversion to Godot types happens only in `Game.Godot`.

### 5.4 Data flow

```
input / bot / network
        |
     command
        |
   simulation tick  ---->  WorldState
        |
   knowledge update ---->  PlayerKnowledge (per side)
        |
   snapshot + events (filtered by knowledge)
        |
   ClientReplica  ---->  presentation
```

Presentation never writes to the simulation. Deleting a view does not change the game.

## 6. Simulation model

### 6.1 Entities

- `EntityId` is a 32-bit integer, assigned by the server, never reused within a match.
- Internal storage handles (for example Arch entities) are separate and never leave the core.
- An entity is a set of components. No class hierarchy like `Vehicle > Tank > T62`.

### 6.2 Component registry

Each component type is registered with its rules:

```csharp
Components.Register<Fuel>(
    save: SaveMode.Full,
    replicate: Replicate.OwnerOnly,
    quantize: Quantize.Byte(0, capacity),
    changeRate: ChangeRate.Low);

Components.Register<Position>(
    save: SaveMode.Full,
    replicate: Replicate.OwnerAndObservers,
    quantize: Quantize.Grid(0.1f),
    changeRate: ChangeRate.High);
```

Networking and saves work through the registry. They never know what `Fuel` means. Fog of war applies per field: the enemy can know a unit is a T-62 without knowing its ammo or exact health.

### 6.3 Systems and scheduling

- A system is a plain class with `Update(tick)` over the components it needs.
- A rule set registers its systems and their rate.
- Slow systems are spread across ticks by entity bucket (`id % N == tick % N`), not run all at once every N ticks.

### 6.4 Time

- The base tick is 20 Hz (50 ms).
- The simulation clock is `int Tick`. No `DateTime`, no accumulated float time.
- Data files use seconds. They are converted to ticks on load.

Tick order:

1. Apply validated commands.
2. Orders and AI decisions (scheduled).
3. Movement.
4. Detection (scheduled).
5. Knowledge update.
6. Weapons fire.
7. Damage resolution.
8. Logistics.
9. Objectives.
10. Destroy and cleanup.
11. Build snapshots.

The order is fixed and covered by a test.

### 6.5 Randomness

Status: landed in Engine.Core (DeterministicRandom based on SplitMix64).

- One match seed, set when the simulation is created (stored in Simulation.MatchSeed).
- Separate streams per domain: combat, artillery, AI, map events. Changing the AI does not change artillery spread or combat rolls.
- Derived domain streams use domain-specific 64-bit prime salts.
- The PRNG is allocation-free and exposes explicit state (ulong State) that can be read and restored for saves and replays.
- No System.Random, no Random.Shared, no GetHashCode based logic, no dependency on thread completion order.

## 7. Three views of the game

| View | Lives on | Contains |
|---|---|---|
| `WorldState` | Server | The full truth: all units, mines, orders, ammo, timers, AI state |
| `PlayerKnowledge` | Server, one per side | What a side is allowed to know: own forces, enemy contacts, last known map object states, objective state and observed area effects |
| `ClientReplica` | Client | What arrived over the network, enough for UI and rendering |

Rules:

- Saves are built from `WorldState` plus all internal state needed to continue.
- Snapshots are built from `PlayerKnowledge`, including allowed map object states, objective state and area effects (section 14.1).
- In single player the UI also reads only `ClientReplica`. Fog of war bugs must show up without a network.
- The bot reads only its side's `PlayerKnowledge`.

### 7.1 Contacts

Clients see enemies only as contacts. A contact has its own `ContactId`, issued per side. Enemy `EntityId` values never reach a client, even for a fully identified unit. Otherwise a modified client could follow an identified tank by its ID after it hides again.

A contact holds: vehicle type (definition index), classification (`Vehicle`, `Tank`, `Recon`, `CommandVehicle`, `Apc`), state (`Current`, `LastKnown`), last known position, last seen tick, and what the side may know about its state (for example "damaged", never exact health).

Seen means known:

- A contact has no "detected but unknown" stage. As soon as a side sees an enemy unit, its contact carries the classification (role) and the vehicle type. Fog of war stays: only what own units see.
- There is no separate identification progress counter, no identify range, and no identify duration.
- When a contact is lost (`Current` becomes `LastKnown`), it retains the vehicle type and classification last seen without updating while unseen.
- When a contact is re-correlated, its track resumes under the existing `ContactId` and retains its vehicle type and classification.

Contact lifecycle:

- A contact stays the same while the side keeps a continuous track.
- When the track is lost, the contact becomes a last known contact and ages.
- When an enemy is detected again, the server reuses the old contact only if a real observer could make that link: inside a correlation time window (default 60 ticks / 3.0 s), and within a dynamic distance gate bounded by plausible speed: `min(MaxGate, Slack + elapsedSeconds * MaxCorrelationSpeed)`. Global plausible speed comes from tuning (`MaxCorrelationSpeed`), never from the true entity speed, to prevent truth information leaks. Otherwise it creates a new contact. The server knows the truth, but it must not use it to link contacts beyond this rule, or it leaks information.
- Old contacts expire after a time set in data (`MemoryDurationTicks`, default 100 ticks / 5.0 s).

The window, slack, and speed gate are tuned in data.

### 7.2 Known world state

`PlayerKnowledge` also holds persistent facts about the world, not just units and contacts:

- Known states of bridges, buildings and other map objects, keyed by stable `MapObjectId` from the map package. This is not an enemy `EntityId`.
- Objective ownership, progress and other fields that the rule set allows the side to know.
- Observed area effects such as smoke, with the allowed position, extent and lifetime.

Initial knowledge comes from the map and rule set. Later observations update known fields and their observation tick. A known bridge state stays in knowledge when the bridge leaves view; an unseen change does not update it. Hidden world state is never copied into knowledge just to make a snapshot complete.

Observable changes during damage resolution, objectives or cleanup update knowledge before the snapshot is built. A presentation event may be lost; the corresponding allowed fact still reaches the client as state.

## 8. Map contract

The simulation owns the terrain. Godot builds the visual terrain from the same source data.

### 8.1 Queries

What the simulation can ask the map:

- surface height at (x, z) on a given support surface
- terrain type and movement cost per movement class
- obstacles and cover zones
- line of sight between two points
- state of bridges, buildings and other changeable objects

### 8.2 Map file

A map export tool writes a binary map package from the editor scene, so the server checks the same geometry the player sees. Vector data from the editor (forest, road and water polygons) is rasterized at export. Raster layers are fast to read for path finding and LOS.

| Block | Content |
|---|---|
| Header | Format version, map ID, content hash, size W x H, cell size in meters, height range |
| Heights | `ushort` per point, scaled to the height range. 10 x 10 km at 5 m is 4M points, 8 MB. |
| Surface | 1 byte per cell: ground type (grass, mud, asphalt, water, swamp...) |
| Cover | 1 byte per cell: forest or urban density, used for LOS attenuation and cover |
| Road graph | Nodes and edges with road class |
| Structures | Bridges, buildings, walls: footprint, height, deck profile for bridges, initial state |

Layers that change during a match (craters, mines, destroyed structures) live in `WorldState`, not in the map file. The map file is read-only after load.

### 8.3 Support surfaces

A heightmap cannot tell "on the bridge" from "under the bridge". Every ground unit has a support surface:

- `Terrain`: height comes from the heightmap.
- `Structure(id)`: height comes from that structure's deck, for example a bridge.

Rules:

- Path finding treats a bridge as a link in the graph between its two ends. Cells under the bridge stay normal terrain, so a road or river under the bridge works.
- Movement switches the support surface when a unit enters or leaves the link.
- Snapshots send the support surface with the position. The client computes height from the heightmap or from the deck profile.
- When a bridge is destroyed, units on its deck fall to the surface below and take damage, or are destroyed. The rule is set in data.
- LOS treats bridge decks as obstacles with height.

Test scenes: observing through a forest edge, firing over a ridge, a road under a bridge, a unit on a bridge above another unit, a bridge destroyed with units on it.

## 9. Navigation and movement

### 9.1 Planning

- Global level: road graph, region graph and structure links (bridges).
- Local level: cost grid at 5 to 10 m per cell, one cost layer per movement class (tracked, wheeled, foot, amphibious). 10 x 10 km at 10 m is 1M cells, 1 MB per layer.
- Per-side layers on top of the base costs: known minefields, known destroyed bridges, known danger areas.
- Precise vehicle movement uses obstacle footprints and local steering in addition to the grid.
- Planning uses what the side knows. Actual movement and collisions use the real world. A route must not silently avoid a minefield the player has not found.
- Group orders produce one route plus formation slots, not one route per unit.
- Group routes respect the most limited member (width, fording, bridge weight).
- Path requests are synchronous with a per-tick work budget. Async comes later, only with a clear rule for late results.
- M1 static navigation prototype slice:
  - Flat grid resolution: 0.5 m cells with continuous clearance field built from static grounded obstacles (IsGrounded == true).
  - Allocation limit: flat prototype grid enforces MaxSupportedCells = 262_144 cells (e.g. 512x512 cells or 256 m x 256 m). Maps larger than 256 m require hierarchical navigation.
  - Swept-capsule safety checks: continuous segment clearance prevents corner cutting and wall tunneling.
  - Time-sliced A* planning: bounded per-tick node expansion budget with an indexed min-heap supporting decrease-key for admissible shortest path extraction without priority corruption.
  - Virtual goal node: connects exact destinations to nearby passable anchor cells via continuous swept clearance, incorporating exact final segment costs.
  - Direct order decoupling: unobstructed straight-line orders are satisfied immediately and do not block behind multi-tick search expansions.
  - Authoritative slot reclamation: navigation slots are recycled using world entity removal version tracking (WorldState.RemovalCount).
  - In-flight route retention: moving units continue following their previous route during multi-tick search until the new route is ready.
  - Stand-off destination snapping: obstructed destinations snap to the nearest passable cell center within 3 m; deep interior destinations report InvalidDestination and halt.
  - Owner-only status replication: snapshots carry navigation outcome to the unit owner without leaking intent to enemy contacts.

### 9.2 Hierarchical navigation slice for kilometer-scale maps

- Movement classes and dedicated coarse graphs:
  - Four explicit movement classes: `Foot` (radius 0.4 m), `Wheeled` (radius 0.8 m), `Medium` (radius 1.0 m), and `Tracked` (radius 1.4 m).
  - Dedicated region-portal graph per movement class: narrow passages (such as a 2 m gap in a wall) correctly split sectors into disconnected components for heavy vehicles (forcing detour routing), while remaining passable in a single component for light vehicles.
  - Graph build performance: ~0.9 s total build time across all four movement class graphs for a dense 900-house town on a 2x2 km map (well below the 3.0 s budget target).
- Region-portal graph (`HierarchicalNavGraph`):
  - Partitions static geometry into uniform 50 m sectors across 2x2 km maps (`navigation-large`).
  - Boundary portal extraction: samples sector border clearance at 0.5 m intervals and extracts centered portal nodes with bottleneck clearance. Fast-path midpoint portal emission for obstacle-free boundaries.
  - Dynamic portal capacity: uses dynamic lists converted to CSR indexing per sector, eliminating arbitrary fixed portal caps.
  - Intra-sector connected components: flood-fill labeling inside sectors assigns component IDs using `ushort` (supporting up to 65,534 components per sector), preventing false edges across interior dividing walls.
  - Reconstruct portal trace buffer sized to `MaxPortals = 8192` with `MaxGlobalPlanPortals = 1024`.
- Resumable time-sliced planning stages (`NavigationSystem`):
  - `GlobalPlanning`: A* search over the region-portal graph using min-heap and decrease-key with `MaxGlobalExpansionsPerTick = 500`. Deduplicates consecutive identical portals and skips portals within 1.0 m of start or target.
  - `AreaPreparation`: Row-by-column clearance evaluation over bounded local window (up to `MaxLocalWindowSize = 160 m`, `MaxAreaPrepCellsPerTick = 40000`). Dynamic obstacle buffer scales to total map obstacles.
  - `LocalPlanning`: Fine grid A* in pre-allocated scratch buffers (`MaxLocalExpansionsPerTick = 1000`).
  - `PathSmoothing`: Raycasting and shortcutting (`MaxSmoothingRaycastsPerTick = 200`). Robust fallback ensures valid raw waypoints are never discarded with false capacity errors.
- Multi-chunk sequential traversal:
  - Long routes (>64 waypoints) stream across multiple chunks without exceeding waypoint buffer limits.
  - Lookahead prefetching: enqueues the next chunk before the current chunk completes (`ChunkLookaheadWaypoints = 2`), guarded against redundant duplicate requests while a chunk is staged.
  - Seamless swapping: when the vehicle reaches the end of its active chunk, staged waypoints swap in immediately, avoiding motion interruption.
  - Final target preservation: preserves initial user destination across intermediate chunks without premature stopping.
- Memory and allocations:
  - Bounded memory footprint (~12.7 MB estimated memory for 1000 units on 2x2 km map).
  - Fine memory depends on explicitly bounded local regions and searches, not flat full-map cell allocation.
  - 0 B allocations in steady state during simulation ticks.
- Performance gate:
  - Bounded tick time: p99 < 15 ms under 1000 active units (measured at ~1.1 ms).
- Prototype limitations for this slice:
  - Static geometry only. Vehicle-vehicle dynamic avoidance, formation spacing, and moving obstacles are deferred to subsequent slices. Maps up to 2x2 km supported; 50x50 km streaming is not claimed.

### 9.3 Following the path

Path following is a separate problem from path planning and needs as much attention:

- turning radius and reversing for vehicles
- columns with spacing on roads
- stopping behind friendly units
- local avoidance

### 9.4 Required test scenarios

1. Two columns meet on a narrow road.
2. A group crosses a bridge.
3. The lead vehicle of a column is destroyed.
4. A bridge is destroyed after the move order is given.
5. A group turns around and retreats.

### 9.5 Vehicle heading and steering

Status: stage 0 landed, stage 1 is the next movement change. Stage 0: every unit has a hull heading
in the simulation, set to the direction of its last step along the route and turning at once (no turn
rate). It is replicated as described below, and the client draws hulls from it. A unit still moves in
a straight line toward its next waypoint. Facing at the end of a move, reversing and armor by side
need the steering of stage 1.

State and convention:

- Vehicles get a `HullHeading` component: an angle in radians, wrapped to (-pi, pi]. Heading 0 faces
  -Z (map north); positive angles turn counterclockwise seen from above. The forward vector on the
  (X, Z) plane is `(-sin h, -cos h)`. This matches Godot `rotation.y`, so the client applies it as is.
- Spawns set an initial heading. Scenarios state it explicitly instead of relying on a default.
- Infantry squads do not use hull heading.

Turret yaw and traverse:

- Armed units have a turret yaw as simulation state, stored as an angle in radians wrapped to (-pi, pi].
  Heading 0 faces -Z (map north); positive angles turn counterclockwise seen from above. The forward
  vector on the (X, Z) plane is `(-sin yaw, -cos yaw)`.
- Today the simulation has no hull heading, so turret yaw is stored as a world angle. Once hull heading
  lands (section 9.5), turret yaw will become hull-relative: world turret yaw will be `HullHeading + TurretYaw`.
- Turret turning follows the shorter arc across the -pi / +pi wrap.
- Armed units specify a powered turret traverse rate in degrees per second from period sources:
  - T-62: 18.0 deg/s (full 360 degree powered traverse in 20.0 s, US Army TRADOC Bulletin 10 / DTIC ADA392790).
  - M60A1: 22.5 deg/s (full 360 degree powered traverse in 16.0 s, TM 9-2350-215-10).
  - Prototype tank: 24.0 deg/s (full 360 degree turn in 15.0 s, plausible estimate for 128 m prototype test map).

Unit data (section 17):

- `turn_rate`: degrees per second for a turn in place.
- `reverse_speed`: meters per second, lower than `speed`.

Stages:

- Stage 1 (first heading change): every vehicle turns in place at its `turn_rate`, wheeled ones
  included. This is a known simplification for wheeled vehicles, accepted so that final facing,
  reverse and armor by side work for all units. Collision stays the current circle model.
- Stage 2 (later): wheeled kinematics. A vehicle without pivot steering gets `pivot_steer = false`
  and a minimum `turning_radius` R, and turns only while moving, with `|omega| <= |v| / R`. Slowing
  down never tightens the radius below R. Final facing then needs a planned maneuver ending on the
  required heading, not a turn in place. The current planner builds polyline routes, so this stage
  needs its own path following tests: a corner next to a wall, a destination directly behind the
  vehicle, and reversing through a turn. Rectangular hull collision and track physics are separate
  later steps.

Steering, each tick (stage 1):

- The desired direction points to the next waypoint, or away from it when reversing.
- If the heading error is above a threshold from data (start value 60 degrees), a pivot steer vehicle
  stops and turns in place at `turn_rate`. Below it, it drives and turns at the same time, with speed
  scaled down by the heading error. Turning never exceeds `turn_rate * TickSeconds` per tick.
- On arrival, if the order has a final facing, the vehicle turns in place to it before the order
  completes. In stage 2 a vehicle without pivot steering reaches it by a planned maneuver instead.
- Reversing keeps the hull facing away from the travel direction and caps speed at `reverse_speed`.
  Path planning is the same; only the steering target flips.
- Following the path, stopping and local avoidance (section 9.3) work on the steered motion, not on
  instant direction changes.

Replication (sections 7.1, 14.1):

- Own units carry heading in every snapshot, quantized to 16 bits (about 0.0055 degrees).
- Contacts carry heading only while `Current`; a real observer sees which way a visible hull points.
  The heading is copied into the side's contact tracker together with the position, at the moment of
  an observation. The snapshot builder reads contact heading only from the tracker, never from the
  world, even while the contact is `Current`. A `LastKnown` contact keeps the last observed heading.
  This adds no knowledge beyond what the observer saw.
- About 2 bytes per unit or contact record. The protocol version increases by one.

Client:

- Uses the replicated heading, interpolated along the shorter arc, instead of inferring it from
  movement. The turret yaw stays relative to the hull.
- Track motion (section 18.7) uses the real heading change for the turn differential, so a pivot
  turn runs the tracks in opposite directions.

Determinism: heading is server-authoritative state like position. It is saved, replayed and restored
on reconnect with the rest of the unit state.

Required tests: turn rate is never exceeded; a large heading error stops and pivots before moving;
reversing keeps the hull opposite to travel and respects `reverse_speed`; final facing is reached
before the order completes; angles wrap correctly across pi; snapshot round trip and quantization
error; contacts expose heading only while `Current` and freeze it as `LastKnown`; contact heading
comes from the tracker even when the world heading changes after the last observation; replay of the
same commands gives the same headings. Stage 2 adds the wheeled tests listed above.

## 10. Detection and fog of war

- Sensors and signatures, not `canSee()`. Start with visual only. Radar, thermal and SIGINT are new sensor types later.
- Line of sight geometric foundation: checks finite 3D segments against continuous bilinear terrain and opaque structures without engine dependencies or Godot calls.
- Exact bilinear surface intersection: solves the quadratic equation A*tau^2 + B*tau + C <= 0 across traversed heightmap cells using numerically stable formulations. Convex curves dip below zero only when the vertex lies inside the traversed interval. This avoids discrete step sampling artifacts and catastrophic cancellation.
- Surface contact rule: Contact with a surface does not block. Only penetration deeper than the tolerance (1 cm) blocks. Targets on the terrain surface (within 5 cm endpoint contact tolerance of the ground) remain visible to observers above without false obstruction.
- Start-point interior rule: Start points on boundaries (such as rooftop observation posts or units flush against a wall) use a strict interior test (`ContainsInterior`). Exterior contact does not make an observer blind. Penetration entering a structure volume blocks at distance zero.
- Grounded structures: Structures marked `IsGrounded` (the default) verify at construction that their base height does not exceed the lowest terrain point under their horizontal footprint, evaluating footprint corners, interior grid vertices, and edge intersections with terrain grid lines. Elevated structures such as bridge decks declare `IsGrounded = false`.
- Spatial index: flat uniform grid with compressed sparse row (CSR) indexing caches structure bounding boxes.
- Zero allocation query hot paths: returns readonly struct `LineOfSightResult`.
- Current geometric limitations: structures are modeled as axis-aligned bounding boxes (AABBs). Rotated bounding boxes (OBBs), triangle meshes, vegetation density attenuation, and dynamic entity obstacles will be integrated in later milestones.
- Scheduled server-side detection: visual sensors evaluate at 10 Hz (every 2 ticks on the 20 Hz simulation clock), distributing observers across ticks using `EntityId.Value % EvaluationIntervalTicks == tick % EvaluationIntervalTicks`. This avoids CPU spikes on any single tick while maintaining timely detection updates.
- Spatial candidate lookup: candidate targets come from an allocation-free uniform 2D grid (`UnitSpatialGrid`), never from iterating all entities in the world. Observers query hostile candidates within their maximum sensor range using team mapping (`AreHostile` between players of different teams; friendly units on the same team are never tracked as enemy contacts).
- Target observation points: each unit definition specifies vertical target observation points (such as lower hull and upper turret/roof). A target is considered detected if any point has an unobstructed line of sight to the observer.
- Strongly typed contact identity: each perceived enemy track is identified by a strongly typed `ContactId`, generated by the server and strictly decoupled from the underlying `EntityId`. Clients and replicas never learn truth entity IDs for enemy contacts.
- Authoritative per-side tracking (`SideContactTracker`):
  - `Current`: live observation continuously updates position and `LastSeenTick`.
  - `LastKnown`: when all line-of-sight checks to a target fail, the contact transitions to `LastKnown` with frozen coordinates.
  - Correlation on rediscovery: when an enemy is spotted near an existing `LastKnown` marker within the correlation time window (default 60 ticks / 3.0 s) and dynamic distance gate `min(MaxGate, Slack + elapsedSeconds * MaxCorrelationSpeed)` (using global plausible speed from tuning, default 10.0 m/s, slack 5.0 m, max gate 35.0 m), the track is correlated and resumes `Current` state under the existing `ContactId`. Detections outside the dynamic gate allocate a new contact track.
  - Expiration: `LastKnown` contacts age out and are permanently removed once unobserved for longer than `MemoryDurationTicks` (default 100 ticks / 5.0 s).
  - Overflow eviction policy: each side supports up to 256 contacts. If capacity is exceeded, the oldest `LastKnown` contact is evicted first. `Current` contacts are never evicted in favor of `LastKnown` markers.
  - Canonical export: exported contacts are sorted deterministically by `ContactId` for consistent network serialization.
- Snapshot wire protocol:
  - Protocol version is 14.
  - Header is 48 bytes: `Version` (1 byte), `SnapshotKind` (1 byte), `uint Sequence` (4 bytes), `int ServerTick` (4 bytes), `ushort UnitCount` (2 bytes), `ushort ContactCount` (2 bytes), `ushort EventCount` (2 bytes), plus session header fields.
  - Own unit record is 35 bytes: `uint Id` (4 bytes), `ushort Definition` (2 bytes), `Vector2 Position` (8 bytes), `uint SupportSurface` (4 bytes), `byte NavStatus` (1 byte), `ushort Health` (2 bytes), `ushort Ammunition` (2 bytes), `ushort ReloadTicksRemaining` (2 bytes), `byte AttackStatus` (1 byte), `uint TargetContactId` (4 bytes), `byte Stance` (1 byte), `ushort TurretYaw` (2 bytes), `ushort HullHeading` (2 bytes).
  - Contact record is 24 bytes: `uint ContactId` (4 bytes), `byte State` (1 byte), `byte Classification` (1 byte), `ushort Type` (2 bytes), `Vector2 Position` (8 bytes), `int LastSeenTick` (4 bytes), `ushort TurretYaw` (2 bytes), `ushort HullHeading` (2 bytes).
  - Combat event record is 40 bytes: `uint EventId` (4 bytes), `int ServerTick` (4 bytes), `byte Kind` (1 byte), `ushort PresentationId` (2 bytes), `Vector3 Origin` (12 bytes), `Vector3 Target` (12 bytes), `uint ShooterUnitId` (4 bytes), `byte Result` (1 byte: 0 None, 1 Penetrated, 2 Deflected).
  - Commands: `MoveCommand` is 22 bytes (`MoveKind = 1`), `AttackCommand` is 18 bytes (`AttackKind = 7`: `Version` (1), `Kind` (1), `Sequence` (4), `ClientTick` (4), `Unit` (4), `TargetContactId` (4)), `SetFireStanceCommand` is 15 bytes (`SetFireStanceKind = 8`). All commands share a single sequence stream per player.
  - Information isolation: server-filtered snapshots only contain permitted observations and knowledge-filtered events for the recipient player. Foreign truth state and hidden firing origins are never transmitted.

Detection is a measured CPU hotspot that fits the per-tick simulation budget: steady-state detection, combat resolution, and snapshot serialization execute with zero managed heap allocations.

## 11. Infantry and containers

### 11.1 Squads

A squad is one entity. Soldiers are data inside it:

```
Squad
  members:
    - role: rifleman,   hp, weapon
    - role: at_soldier, hp, weapon: rpg7
    - role: mg,         hp, weapon: pkm
```

- Losing the AT soldier removes the squad's AT capability.
- `Split` and `Merge` commands create and remove squad entities.
- A single sniper or observer is a squad of one.
- Clients draw individual soldiers around the squad position.
- The same pattern works for any group with many visual members.
- Soldier models: `Assets/Infantry/US/us_rifleman.glb` (low-poly, faceted, one skinned mesh, clips
  for idle, aiming standing, kneeling and prone, walk and run) is built by
  `tools/model-authoring/infantry`. It is not used in the game yet. A Soviet rifleman in the same
  style comes next.

### 11.2 Containers

One component for transports and buildings:

```
Container { capacity_seats, passengers: [EntityId] }
```

- Capacity counts people. An 8-person squad must split to fit a 4-seat jeep.
- Passengers leave the spatial index and get a "contained in X" state.
- When a container is destroyed, passengers take damage and are ejected.
- Passengers of an enemy container are not sent to clients unless detected.

## 12. Commands and orders

- A command is a network message. An order is persistent state on a unit (an order queue).
- Commands carry player ID, sequence number and the client tick as a hint. The server assigns the execution tick.
- The server validates: the player owns the units, targets are known contacts, parameters are in range.
- Commands are delivered reliably and executed once (deduplicated by sequence number).
- Clients give instant UI feedback (marker, radio reply). Unit positions are not predicted.

M0 sends a sequence-numbered queued/rejected reply for a well-formed command.
Queued means admission to the next simulation tick, not successful execution or
arrival. Rejections include no unit ID or reason that could expose hidden state.
Malformed packets are discarded. These replies use the bounded local transport;
reliable acknowledgement, retries and reconnect reconciliation remain M1 work.

### 12.1 Attack orders and fire stances

An attack order targets either a `ContactId` or a ground position. Never an enemy entity ID.

#### Fire stances and auto-fire

Every unit has a fire stance: `FireAtWill` (default), `ReturnFire`, or `HoldFire`. Players set stances per unit via commands, and stances are replicated in own unit snapshots.

- `FireAtWill`: An armed unit without an active attack order, and not moving, engages autonomously. It picks a `Current` hostile contact of its side within weapon range that it has its own line of sight to. Firing checks follow the same rules as an attack order.
- Target prioritization: The unit evaluates whether its weapon can penetrate the candidate contact at current range against the observed aspect and catalog armor (seen means known). Candidate targets that the weapon cannot penetrate are skipped for auto-fire, avoiding wasted bursts against impenetrable armor (such as heavy machine guns against tank fronts). Among penetrable candidates, the unit prefers tanks over APCs over recon over other classes, then the nearest contact. Explicit attack orders given by a player still engage and fire even against impenetrable targets (producing deflection effects).
- Threat-based re-evaluation: When the weapon is ready to fire, if a visible, penetrable contact of strictly higher priority exists (such as an enemy tank appearing while engaging an APC), auto-fire switches to it immediately. When candidates have equal priority, the unit keeps its current target to prevent flip-flopping. Switching occurs at most once per reload cycle. Explicit attack orders given by a player never switch autonomously. Target evaluation uses only observed contact knowledge (position, type classification, observed heading and catalog armor), never world truth.
- Target retention: The unit keeps its chosen auto-fire target until a higher-priority threat is selected during ready-to-fire re-evaluation, the contact is lost, moves out of weapon range, is destroyed, or maneuvers such that its observed aspect becomes impenetrable to the weapon.
- Auto-fire never moves the unit.
- `ReturnFire`: Behaves like `FireAtWill`, engaging only contacts that fired at this unit (`TookFire`), that the unit can see with its own line of sight, and that its weapon can penetrate based on the observed aspect.
- `HoldFire`: Never fires autonomously.

#### Attack orders and pursuit

- Attack orders out of range are accepted. The unit drives toward the target position until it is within weapon range (with a 0.5 m margin) and has its own line of sight, then halts and fires.
- If the target transitions to `LastKnown`, the unit drives to the last known position and holds there.
- The attack order is dropped when the target contact expires from the tracker (after `MemoryDurationTicks`) or when an observed target is destroyed. If a target is destroyed outside line of sight, the unit holds at the last known position until contact expiry without leaking unobserved destruction.
- A new movement order cancels an active attack order.
- Firing line of sight and weapon range are rechecked before each shot. Moving units do not fire.
- Invalid commands are rejected and never cancel an existing valid order.

#### Turret traverse and alignment

- With an active target (attack order or auto-fire), the turret turns toward the target at most at its traverse rate per tick, along the shorter arc.
- A shot is fired only when the turret points at the target within `TurretAlignmentTolerance` (0.05 radians, about 2.86 degrees).
- Reload continues while turning. If the weapon is loaded and line of sight is clear, firing occurs on the tick the turret enters alignment tolerance.
- The hull heading (section 9.5, stage 0) is the direction of the unit's last step along its route, turning at once (no turn rate yet). Spawned units start with hull and gun along the scenario facing (toward the enemy side by default in battle and combat scenarios).
- The turret rides on the hull. With no target, a hull turn carries the gun along, so a tank driving without a target keeps its gun over the front. With a target, the gunner keeps the gun on it through hull turns (a real hull turns over seconds and the gunner tracks through it).
- Without a target the gunner lays the gun back over the front at the traverse rate.
- A move order drops an ordered attack target, not a target picked by auto-fire. A unit driving off keeps its gun on that target while it stays valid (in sight and range), for example watching the enemy over the rear deck when pulling back. Moving units still do not fire; mobile fire is a later rule.
- Turret yaw goes over the wire in 65536 steps per turn, so 0 and the quarter turns are exact.
- The client takes both the hull heading and the turret yaw from the same snapshots with the same interpolation, so a gun riding on the hull stays fixed to it on screen.

#### Cued fire

A unit may fire at any `Current` contact known to its side, including targets spotted only by an allied unit, provided the target is within weapon range and the shooter has its own line of sight to it. The shooter's own sensor range does not limit weapon fire (a gunner sight reaches further when told where to look).

#### Area fire and limitations

- Area fire on a ground position (suppression, artillery) does not need a contact. When a lost contact is linked again (section 7.1), the order continues.
- Prototype limitation: instantaneous shot resolution without ballistics simulation or dispersion. Visual tracers are presentation effects and do not determine damage.

### 12.2 Move orders

Status: planned, together with or after section 9.5.

- Final facing: a move order may carry a facing angle. The unit turns to it on arrival. The server
  validates that the value is finite.
- Reverse: a move order may ask the unit to drive in reverse (section 9.5).
- Waypoint queue: a move order may append to the unit's order queue instead of replacing it. A plain
  order clears the queue. The queue length is capped in data (start value 16); longer queues are
  rejected without cancelling the current orders.
- The order queue and the navigation route are different things. Each queued move order is one user
  destination. Navigation plans its own internal waypoints and chunks toward the current destination
  (section 9). Reaching an internal waypoint or finishing a navigation chunk never completes a user
  order; only arriving at the destination (and the final facing, if any) does, and then the next
  queued order starts.
- Group moves keep the existing per-unit commands. Formation spreading of group destinations is later
  work (M3).

### 12.3 Direct fire accuracy and armor

Status: armor by side, penetration falloff, deflection, and static direct fire accuracy landed. Fire on the move, move-and-engage orders, and short halts are planned for a later PR.

- Hit probability depends on range, target movement, the visible share of the target, and a ranging bonus for repeated shots at the same target:
  - Base hit chance: defined as a piece-wise linear curve in weapon data (`accuracy_curve`), clamped to the near value at short range and far value at long range.
  - Target movement penalty: if the target moved during the last `TargetRecentlyMovedWindowTicks` (20 ticks / 1.0 s), weapon `target_movement_penalty` is subtracted from the hit chance. Movement tracking updates on real displacement in both `MovementSystem` and `WorldState.UpdateUnitNavigation`.
  - Visible share: computed from the weapon muzzle height to the target observation points (`target_points`). Both visible share and line of sight checks evaluate from the weapon muzzle position (`weapon.MuzzleHeight`), ensuring consistency. If all target points are blocked (visible share is 0), firing is skipped without expending ammunition and weapon status reports `AttackExecutionStatus.Obstructed`. Obscured points reduce visible share proportionally (`baseAccuracy * visibleShare`), so a hull-down tank showing only its turret is significantly harder to hit.
  - Ranging bonus: consecutive shots at the same target accumulate `ranging_bonus` up to `max_ranging_bonus`. This bonus resets to 0 if the target changes, the target moves more than `TargetMovedRangingResetDistance` (10.0 meters), or the shooter moves (real displacement in `MovementSystem` or `UpdateUnitNavigation`).
  - Clamping: when visible share is positive, final hit probability is clamped to [`MinHitProbability` (0.02), `MaxHitProbability` (0.98)].
  - Machine gun bursts: one accuracy roll is made per burst using its own curve.
  - Rolls use the combat random stream only, guaranteeing deterministic replays across clients and servers.
- Misses:
  - A miss calculates an angular dispersed impact point along the line of fire and lateral offset based on weapon `dispersion_mils`. One mil corresponds to 1 meter lateral or vertical displacement per 1000 meters range, so dispersion grows linearly with range. The impact lands strictly outside the target bounding radius (`targetRadius + 0.3 m` margin), hitting the ground or an obstacle.
  - Misses deal zero damage and produce a miss result (`HitResult.Miss`). Target retention is preserved so units and bots continue firing without dropping the target.
  - Combat events carry `HitResult.Miss` and the real impact point. Protocol version is 15.
  - Client presentation shows a ground impact dust puff with synthesized dull ground thud audio (filtered noise thump distinct from metallic armor impacts), and flies tracers to the true miss coordinates, with zero vehicle sparks and zero penetration mesh.
- Fire on the move is allowed with a large accuracy penalty. A "short halt" behavior stops, fires and
  continues, as period crews did with weak stabilizers.
- Moving and firing changes the section 12.1 contract, so the new rules replace these points of it:
  - A new "move and engage" order carries a destination and a target contact. The unit follows its
    route and fires at the target whenever it is in range and in its own line of sight.
  - A per-unit fire mode chooses between firing on the move (with the penalty) and short halts. A short
    halt stops for at most a data-set number of ticks to fire one round, then resumes the same route;
    the route and the queued orders are kept.
  - The plain attack order keeps the 12.1 behavior: it halts the unit and fires from the spot.
  - A plain move order cancels the target of a move and engage order, as it cancels an attack order
    today. A new move and engage order replaces both route and target.
  - Target loss follows 12.1: `OutOfRange` and `TargetLost` hold fire without stopping the move, and
    the target is dropped when the contact expires or the observed target is destroyed.
- Armor by side (landed):
  - Unit data specifies armor in mm RHA equivalent for front, side and rear (`armor_front`, `armor_side`, `armor_rear`). In M1, hull and turret share values as a documented simplification.
  - The hit side is chosen from the angle between the shot direction and the target's hull heading (`CombatState.HullHeading`). Sector widths come from data with default front 90 degrees (+/- 45 deg), rear 90 degrees (+/- 45 deg), flanks 90 degrees each side.
  - Weapon data defines penetration at two ranges (`penetration_near` at `penetration_near_range`, `penetration_far` at `penetration_far_range`), linearly interpolated with 2D distance.
  - Penetration exceeding the target's armor applies weapon damage. Non-penetrating hits do zero damage and produce a deflection result (`HitResult.Deflected`).
  - Burst weapons: heavy machine guns and light machine guns fire in bursts (`rounds_per_burst`, burst reload interval, ammunition). One combat event is recorded per burst. Partial bursts scale damage proportionally to rounds fired: `Math.Max(1, (damage * roundsFired) / burstRounds)`.
  - Presentation kinds: weapon catalog definitions require explicit `presentation_kind` (`"cannon"` or `"machine_gun"`), encoded into opaque presentation IDs (`WeaponPresentationKind`). Content validation rejects missing, invalid kinds, or non-positive penetration values.
  - Combat events carry `HitResult` (1 Penetrated, 2 Deflected, 3 Miss) replicated over the wire protocol (offset 39 of the 40-byte event record, protocol version 15).
  - Information isolation: enemy armor thickness and remaining health are never leaked to contacts or foreign players. Observers receive only the visual hit result at the impact point. Deflections produce ricochet sparks and deflected tracer lines angled forward and away from the target; audio priorities ensure heavy cannon shots are never evicted by rapid machine gun bursts.
- Blind fire: area fire at a ground point or at a `LastKnown` position has a low hit chance and needs
  no contact. The shooter sees only impacts it can observe. It never learns that an unseen target was
  hit or destroyed (sections 7 and 12.1).

### 12.4 Smoke

Status: planned, part of the M1 scope.

- Smoke is an observed area effect (section 7.2) with position, extent and lifetime, and a dynamic
  blocker for line of sight (section 10).
- Sources from data: the T-62 engine smoke system lays a screen while driving; early M60A1 tanks had
  no smoke dischargers. Grenade launchers and artillery smoke come with the units that carry them.
- Smoke blocks sight both ways, so it hides the layer as well as the target.

### 12.5 Weapon slots

Status: planned for M2. M1 units keep one weapon each, so the M1 exit does not depend on this.

- A unit has several weapons, each with its own target, allowed target classes, ammunition and
  reload. The main gun takes vehicles; machine guns take infantry and soft targets.
- Period fits: T-62 with a coaxial PKT (the 1972 model adds a DShK on the loader's hatch); M60A1 with
  a coaxial M73 and an M85 .50 in the commander's cupola.
- Anti-aircraft fire waits for aviation (section 22, Later). Heavy machine guns would then get a small
  chance against helicopters at short range.

## 13. State and events

- Anything that must survive reconnect, save or late join is state. Example: `Bridge.State = Destroyed`, unit health, ammunition, reload timers.
- Events are hints for presentation. Example: `BridgeDestroyed` triggers the explosion effect; `ShotFired` triggers muzzle flash and tracer; `TookFire` triggers target impact effect; `UnitDestroyed` triggers explosion and debris.
- Presentation events are optional hints: dropped events never alter authoritative combat outcomes.
- Duplicate events are ignored by clients using a ring buffer of processed event IDs.
- Events have stable sequential identifiers and a server tick.
- Every event sent to a client passes the recipient knowledge filter:
  - `ShotFired`: sent to the attacker owner and to any opponent who currently observes the attacker (`State == ContactState.Current`). If the shooter is unobserved, the event is omitted entirely to prevent disclosing hidden firing coordinates.
  - `TookFire`: sent to the victim owner when hit by an unobserved shooter. Carries the victim impact position and a coarse quantized 16-sector incoming bearing, without revealing the shooter ID or exact firing coordinates.
  - `UnitDestroyed`: sent to the owner, and sent to opponents only if they currently observe the target when it is destroyed. Destroying an unobserved enemy never provides the opponent with a destruction event.
  - Bounded ring buffers per player (capacity 64 events per snapshot) with documented overflow drop behavior.
- Known leaks to avoid:
  - weapon fire from a hidden unit revealing its exact position
  - artillery impact point sent before impact
  - exact enemy health
  - destruction of hidden objects
  - command rejection reasons that reveal hidden enemies

## 14. Networking

- Transport behind `ITransport`. Implementations: in-memory (single player, tests) and LiteNetLib (UDP). Steam Networking Sockets later.
- No Godot high-level multiplayer or RPC.
- Handshake compares the `MatchSetup` header (section 15.1): protocol version, content hash, map hash, rule set.
- Handshake timeout: connections that do not complete admission within the timeout window (default 5.0 s) are disconnected with `HandshakeReason.Timeout`, freeing the slot for other clients.
- Reserved bot slots reject incoming client handshakes with `HandshakeReason.ServerFull` without resetting the local bot simulation state.
- Commands, snapshots and saves use explicit message types, not serialized game classes.

### 14.1 Snapshots

- 10 Hz to start, with a sequence number and server tick.
- Snapshots contain own units, enemy contacts, known map object states, allowed objective state and observed area effects. Every field follows its registry rules and the side's knowledge.
- M1 sends sparse full snapshots: the complete allowed view each time, no deltas. A unit or contact missing from a complete snapshot is gone from the client's view. World state blocks are also complete for that side, including facts kept from earlier observations.
- Map objects use `MapObjectId`. Their static geometry stays in the map package; snapshots carry allowed mutable fields such as bridge condition and objective ownership. A missing knowledge entry does not mean the object was destroyed or restored to its initial state.
- Clients apply a full snapshot only after the complete message is received. On reconnect the same state blocks restore the allowed view without needing earlier presentation events.
- Protocol version 15: own unit records are 35 bytes (`ushort TurretYaw` (2) at offset 31, `ushort HullHeading` (2) at offset 33). Contact records are 24 bytes (`ushort TurretYaw` (2) at offset 20, `ushort HullHeading` (2) at offset 22). Both angles are quantized to 65536 steps per turn. Contacts carry the turret yaw and hull heading observed at the moment of sighting; `LastKnown` contacts freeze the last observed values. Combat event records are 40 bytes (`byte Result` (1) at offset 39: 0 None, 1 Penetrated, 2 Deflected, 3 Miss).
- Delta compression (acknowledged baselines, dirty fields) is added only if measurements exceed the budget.
- Quantization: ground units send X, Z and support surface. Height is computed on the client (section 8.3). 10 cm precision on a 16 km map is 18 bits per axis.
- Clients render about 100 to 150 ms behind the server and interpolate.
- Later option: replicate movement as intent (path, speed, start tick) instead of positions. The snapshot format must allow adding this.

### 14.2 Reconnect

- The player gets a session token at connect. Reconnect uses it to identify the player, not the network address.
- In the reconnect handshake the server sends the last command sequence number it executed for this player.
- The client drops acknowledged commands and resends the rest. Deduplication by sequence number prevents double execution.
- Commands older than a short limit are dropped instead of resent, because the situation has changed.
- The server then sends a full snapshot of the player's allowed view and current orders.

### 14.3 Transport receive buffers and overload policy

Known dependency limitation: LiteNetLib 1.3.5 can discard unread native events when its polling
budget is exhausted. The bounded application rings below do not prevent that loss. The reproduction
and required networking follow-up are recorded in [network-polling-follow-ups.md](network-polling-follow-ups.md).

- Application-owned transport receive storage is strictly bounded and preallocated. Steady-state packet reception and queue operations perform zero heap allocations in application receive storage. The configured limits bound application-owned receive queues and buffer pools; they do not bound internal LiteNetLib library buffers (such as socket receive buffers, reliable retransmission state, or internal packet pools).
- Fixed storage allocation per peer:
  - Server maintains an isolated `ReceiveBufferQueue` for each player slot (default 64 packets, 64 KB payload budget per peer).
  - Client transport maintains a dedicated `ReceiveBufferQueue` (default 128 packets, 256 KB payload budget).
  - Preallocated pools use standard buffers (1,280 bytes) and large buffers (44,592 bytes for `WireProtocol.MaxMessageSize`).
  - Total memory footprint:
    - Server per peer: 64 standard buffers (81,920 bytes) + 2 large buffers (89,184 bytes) + queue descriptors (~6 KB) = ~177 KB. For 2 players: ~354 KB.
    - Client: 128 standard buffers (163,840 bytes) + 4 large buffers (178,368 bytes) + queue descriptors (~8 KB) = ~350 KB.
- Overload behavior:
  - When either packet count limit or payload byte limit is reached, incoming packets cannot be queued.
  - Unreliable packets (such as state snapshots) are dropped. Counters `DroppedUnreliablePackets` and `DroppedUnreliableBytes` are incremented.
  - Reliable packets (such as handshakes, commands, and command results) trigger reliable overflow handling: the offending peer is disconnected to prevent protocol desynchronization, and counters `ReliableOverflowFailures` and `ReliableOverflowDisconnects` are incremented.
- Fair scheduling:
  - `LiteNetLibServerTransport.TryReceive` polls connected player queues using round-robin scheduling. A flood of traffic on one slot cannot starve reception from another player.
- Slot reuse and stale connection protection:
  - Player disconnection immediately clears the slot queue and returns all buffers to the pool.
  - Any packets delivered with an obsolete connection ID on slot reassignment are discarded before surfacing to the game simulation.
- Bounded processing loops:
  - `SimulationHost.Advance` bounds incoming transport messages to `MaxMessagesPerAdvance = 128` per call.
  - `ClientSession.Poll` bounds incoming transport messages to `MaxMessagesPerPoll = 128` per call.
  - Remaining messages stay queued in the bounded receive ring for subsequent calls, preventing frame stalling during network bursts.
- Diagnostic counters:
  - Transports expose diagnostic counters for queued packets, queued payload bytes, peak watermarks, dropped unreliable packets and bytes, and reliable overflow events.

## 15. Saves, replays, debug logs

Three different goals with three formats:

| Goal | Format |
|---|---|
| Continue a match | Full `WorldState` plus internal state: orders, timers, RNG state, AI state. Versioned. |
| Watch a match | Recorded snapshot stream from an observer who sees everything. Versioned by protocol. |
| Reproduce a bug | Initial state, command log and periodic state hashes, same build on the same machine. Verified by tests, not assumed. |

AI state and order queues must be plain data so they can be saved. No `async` or coroutine-based AI.

### 15.1 MatchSetup

Every save, replay and command log starts with the same header:

- build version and protocol version
- content hash
- map ID and map file hash
- rule set ID and version
- match seed
- players, sides, factions and starting forces
- match settings

Loading checks the header first. A mismatch is an explicit error, never a silent load with different rules or data. The network handshake uses the same header.

Implemented now: `MatchSetup.PlayerFactions` holds one faction content ID per player, next to `PlayerTeams`. Scenarios assign `soviet` to player 1 and `us` to player 2, and fail if the catalog lacks either faction. Like teams, factions are compared when both setups carry them, but the network handshake does not send them yet. Factions are public match information: a client may use the enemy faction for presentation without learning anything a contact does not already reveal. The match seed is chosen per match by the server, exposed on `Simulation.MatchSeed`, and logged at startup; full save and replay format serialization of `MatchSeed` is reserved for when disk persistence lands.

## 16. AI

Three levels, each with its own rate:

- Operational (0.5 to 1 Hz): goals such as "take the bridge".
- Tactical (1 to 2 Hz): plans for groups such as "recon north, tanks through the west forest".
- Unit (2 to 5 Hz): targets, positions, small movements.

The AI sends normal commands and reads only its side's knowledge. Headless bot vs bot matches are used for balance and regression tests. The technique (behavior trees, utility, HTN) is decided later. Its state must be serializable whatever we pick.

## 17. Data and content

- Simulation definitions live in `content/` as JSON files, read with `System.Text.Json` source generators. Comments and trailing commas are allowed. Data: speed, armor, weapons, sensors, crew, capacity.
- Presentation definitions live in the Godot project: model, materials, sounds, icon. They link to simulation definitions by `def_id`.
- Roles and types are strings or tags from data, never engine enums.
- Factions are data: `content/coldwar/units.json` lists them under `factions` (ID and display name). The catalog rejects blank or duplicate IDs.
- Resource types (fuel, ammo) are defined in data.
- Every data file has `schema_version`.
- A test loads and validates all content.
- Data hot reload in development builds.
- Mods are data only for now. Code mods are a security question for later.

### 17.1 Historical unit data

Status: step 1 of the implementation order below is done. `content/coldwar/units.json` holds the
historical types and the `battle` scenario uses them. The prototype units (`prototype_tank`,
`recon_vehicle`, `apc`) moved to `content/prototype/units.json`: their numbers are tuned for 128 m
test maps (a tank gun reaches 60 m, a tank drives at 5 m/s) and faction models only change their look.

Scale and time:

- Distances and speeds are 1:1 (section 3: 1 world unit = 1 meter). Weapon and sensor ranges are
  the period values, not shortened for the map. Gameplay changes come from rules (section 12.3),
  not from shrinking the numbers.
- Time is real time at the fixed 20 Hz tick. A tank crossing 2 km of open ground takes about four
  minutes. A faster game speed may exist later for single player and tests only; it never changes
  the data.
- Battles need maps of at least 2 x 2 km, up to the 15 x 15 km limit of section 2. The 128 m maps
  stay as test maps with their own test unit data, loaded through `PrototypeScenario.LoadCatalog`;
  historical data loads through `ColdWarContent.LoadCatalog`. The client picks the catalog by scenario.

Vehicle types:

- Each unit type is one real vehicle of one faction: `t62`, `m60a1`, `btr70`, `m113a1`, `brdm2`, and
  later `m151a2`, `brdm2u`, `m577`, `ural375d`, `m35a2` (era about 1976 to 1982). A type replaces the
  "prototype unit plus faction model" pairs; `visuals.json` maps each type to its model directly.
- Unit data gains `name` (display name, for example "T-62") and `faction`. The existing
  `classification` field holds the role (`tank`, `apc`, `recon`, `command`, `truck`), which becomes
  the contact classification when the unit is seen. The catalog rejects a blank name or an unknown
  faction.
- Scenarios spawn the types of each player's faction. A scenario names the types it uses, so a
  missing model or a missing type fails at load, not in play.
- The client shows the type name in the selection card ("T-62 [Tank]") and on the badge of
  own units and enemy contacts ("T-62", "BTR-70") immediately upon sighting. Prototype units show
  their role ("TANK", "RECON", "CMD").

Numbers and sources:

- Every value comes from `docs/unit-sources.md`, with a numbered source. A value without a period
  source is marked as an estimate there, with the reason for its choice.
- Mobility: `speed_road`, `speed_offroad` and `reverse_speed` in m/s, plus water speed for amphibious
  types. Until the map has a road layer, units move at `speed_offroad`; a type gives `speed_offroad`
  instead of `speed`, never both. The catalog rejects a type that is faster off-road than on the road
  or reverses as fast as it drives. `turn_rate` comes with hull heading (section 9.5).
- Weapons: effective range from period sources. Hit probability by range is section 12.3, not the
  range field. Penetration at near and far ranges is a game value tuned for balance; the sourced or estimated period values sit next to it in `docs/unit-sources.md`. Tank guns fire single kinetic rounds (Soviet 115 mm 3BM-6 APFSDS, US 105 mm M392A2 APDS); APCs and scout vehicles fire heavy machine guns in 5-round bursts (Soviet 14.5 mm KPVT on BTR-70 and BRDM-2, US 12.7 mm M2HB on M113A1); M151A2 armed recon fires 7.62 mm M60 in 5-round bursts.
- Armor by side: `armor_front`, `armor_side`, `armor_rear` in mm RHA equivalent from period sources (`docs/unit-sources.md`). In M1, hull and turret share values as a documented simplification.
- Turret traverse: traverse rate in degrees per second from period sources (`docs/unit-sources.md`). T-62 has 18.0 deg/s electric powered traverse (full turn in 20.0 s, source [15]), M60A1 has 22.5 deg/s hydraulic powered traverse (full turn in 16.0 s, sources [6] [19]), BPU-1 turret (BTR-70, BRDM-2) has 15.0 deg/s manual traverse estimate (source [20]), M113A1 cupola has 30.0 deg/s manual traverse estimate (source [21]), M151A2 pedestal mount has 60.0 deg/s manual traverse estimate (source [22]), prototype tank has 24.0 deg/s estimate.
- Sensors: visual sensor range per observer type, for a clear day, open ground and a moving target.
  Cover, a halted target, dust, smoke and weather reduce them later (section 10). Seen means known:
  any visible unit reveals its vehicle type at once.

Seen means known (section 7.1):

- A visible enemy shows its vehicle type at once. The separate identification stage and progress
  counter (identify range, identify duration, reset on loss) are removed.
- The contact record carries the vehicle definition index (`ushort Type`), revealing the vehicle type
  and role immediately. Protocol version is 10.
- Enemy contacts render with their vehicle type's model (1:1 scale with hostile affiliation marker)
  instead of an unknown diamond marker.

Presentation:

- The camera must zoom out far enough to see about 3 km. At that zoom a 7 m tank is a few pixels, so
  units and contacts read through role badges, and later NATO symbols (section 18).
- Effects and models are already in meters. Sound gets a delay from the speed of sound and a profile
  per weapon in a later client audio change.

Implementation order:

1. Done. Unit types, names, period numbers and the 2 km `battle` scenario (a meeting engagement: a
   ridge with two saddles hides the start areas from each other, and a village sits on one saddle).
   The client shows type names for own units, draws historical models at 1:1 scale and a 100 m grid
   on kilometer maps.
2. Done. Seen means known: contacts carry vehicle type immediately, protocol version 10, real models
   and type badges on enemy contacts without identification delay.
3. The 2 x 2 km recon raid battlefield (milestone M1) with the new data.

## 18. Presentation

- Godot reads `ClientReplica` and interpolates. It never changes the simulation.
- Animation is driven by state (velocity, turret angle), never the other way around.
- Level of detail by distance (distances are starting guesses):

| Level | Distance | Rendering |
|---|---|---|
| LOD0 | up to about 150 m | Light `Node3D` per unit with parts (hull, turret, gun), visuals and audio only |
| LOD1 | about 150 to 600 m | Simplified meshes, wheels turn in the vertex shader, no individual track links |
| LOD2 | beyond about 600 m | `MultiMesh`, one mesh per unit type |
| Strategic zoom | camera height | NATO-style symbols as instanced billboards |

- Views never run game logic. Transforms come from the `ClientReplica` interpolator.
- Static and identical objects (trees, wrecks, far units) use `MultiMesh`, split by map chunk because a MultiMesh is culled as a whole.
- Infantry: skeletons close up, vertex animation textures with MultiMesh at a distance.
- Strategic zoom is also a performance tool, not just style.
- Terrain: Terrain3D or a custom clipmap, decided in milestone 1. Pin an engine and plugin version pair that works on both platforms.

### 18.1 Camera controls

- Orthogonal projection camera.
- Unified WASD and arrow key panning with normalized diagonal displacement, speed scaling proportionally to `CurrentSize / ReferenceSize`, clamped to map boundaries `[0, MapWidth]` and `[0, MapDepth]`.
- Immediate stop on key release without inertia or drift.
- Exponential zoom smoothing: `1 - exp(-12 * delta)`, frame-rate independent over arbitrary frame rates.
- Space key restores default focus and zoom size.
- The camera looks down at the focus along a fixed direction and backs off as the orthographic size grows, with the far plane following it. At a wide zoom the near rows of the view would otherwise start behind the camera and the near half of the map would be clipped.
- Panning and zooming remain active while issuing move/attack orders and selecting units.

Selection and control groups (client presentation only):

- Click selection on release: releasing the left mouse button without dragging selects a hit role badge, or the nearest own unit or enemy contact within 24 px of its projected center. Shift adds to the selection; clicking empty ground without Shift clears the selection.
- Box selection: dragging the left mouse button more than 6 px draws a translucent rectangle and selects all own units whose projected centers (Root + 1 m up, in front of the camera) lie inside the rectangle on release. Shift adds to the current selection; without Shift it replaces it. Contacts are never box selected, and box selection clears contact selection.
- Same-type selection: double-clicking an own unit selects all on-screen own units with the same `UnitId` (Shift adds). Double-clicking a contact or empty ground behaves like a normal click.
- Control groups: `Ctrl+1..9` or `Cmd+1..9` on every platform stores the current own selection in group 1..9, `Shift+1..9` adds to the group, and `1..9` recalls it. Pressing the same group number again within 0.35 s centers the camera on the group's average position (`CameraRig.CenterOn`) without changing zoom. Disappearing units are removed from all groups in `UnitViewSet.Sync`.
- Selection display: each unit's role badge shows its lowest control group number, and the selection card and status line show the selected count and `"Group N"` when the selection matches a group.

Planned controls (client only unless a section is named):

- Right drag sets the final facing of a move, with an arrow preview (sections 9.5, 12.2).
- A modifier with right click orders a reverse move (section 12.2).
- Shift with right click appends a waypoint, with markers along the queued route (section 12.2).

### 18.2 Display settings and frame pacing

- 4x MSAA is the default for 3D geometry edges. It is a rendering setting and does not affect simulation ticks.
- VSync toggle (On/Off) and Max FPS cap (0 = Unlimited, 30, 60, 120, 144 FPS).
- Display settings persisted to `user://display_settings.cfg`.
- Command line overrides: `--vsync <on|off>`, `--max-fps <fps>`, `--benchmark-frames <N>`.
- In-game settings overlay panel (F10 / O / HUD button), with Escape key closing the panel first before clearing unit selections.
- Unfocused window throttling to 30 FPS, restoring saved settings when window focus returns.
- Clamped delta (max 0.1 s) on window focus return and long frame hitches to prevent camera teleportation.
- Simulation invariance: simulation runs at fixed 20 Hz, snapshots at 10 Hz; rendering at 30, 60, 120, 144 FPS only interpolates client replicas (`ClientReplica.SamplePosition`), with `LastKnown` contact markers strictly motionless.
- Zero steady-state heap allocations during active camera model updates and frame pacing metric collection.

### 18.3 Prototype vehicle presentation

- Own tanks and reconnaissance vehicles load original GLB assets through the client-only
  `Assets/Vehicles/visuals.json` mapping, keyed by content unit ID rather than numeric catalog order.
- Imported scenes are cached and instantiated per unit. Static parts are combined by material
  within each functional pivot. The tank has 14 mesh surfaces and the recon vehicle has 10.
- Presentation takes hull facing from the replicated hull heading (section 9.5) and the turret
  from the replicated turret yaw. This does not change server movement or combat.
- Selection, health bars, event recoil and view cleanup remain independent of imported meshes.
- Enemy contacts retain their existing knowledge-filtered markers. Unknown enemy types are
  never resolved through the own-unit visual mapping.
- The client launchers import Godot assets before running. Export presets include the visual mapping.
- Gun pitch, dedicated barrel recoil and destruction models remain future work. Faction tanks
  turn their wheels and run their tracks (section 18.7); the prototype assets stay static.
  These assets are a first playable visual prototype, not a validated large-battle rendering budget.

### 18.4 Unit, contact, and combat readability

- Visual contact classification and identity presentation ("seen means known"):
  - A visible enemy contact (`ContactState.Current`) is drawn directly with the 3D model of its vehicle type, or its classification fallback model if the type is outside the visual catalog.
  - To prevent sharing or mutating shared material instances, enemy vehicle models are distinguished with hostile affiliation ground markers (`HostileAffiliationMarker`) and distinct badge themes.
  - Lost contacts (`ContactState.LastKnown`) immediately hide live 3D models and display stationary uncertainty ghost markers at frozen coordinates with observation age, showing the ghost base and the last seen type on the badge.
  - State transitions strictly clear stale models: transitioning to `LastKnown` hides the vehicle model and shows the ghost base; re-establishing line of sight restores the 3D model for the visible vehicle type.
- Screen-space role badges:
  - Role and vehicle name indicators (for example "T-62", "BTR-70", or prototype roles "TANK", "RECON", "CMD") anchored in screen space above units and contacts.
  - Text and badge shapes distinguish units without relying solely on color tinting.
  - Dynamic culling hides badges that are behind the camera, off-screen, or occluded by main UI panels (`_topHud`, `_bottomHud`, `_selectionCard`, `_settingsPanel`, `_missionPanel`).
  - Opacity scales smoothly with camera zoom to prevent popping.
  - Direct interaction: left-clicking a visible badge selects the unit or contact; right-clicking an enemy badge issues an attack order.
- Tactical selection card and order feedback:
  - Persistent bottom-left `SelectionCard` details current selection:
    - Single friendly unit: HP bar with percentage, weapon readiness distinguishing unarmed scouts (`Armament: Unarmed`) from tanks with depleted ammo (`0 / 10 (Depleted)`), reload timer in seconds (`0.8s`), and server attack execution status (`Aiming`, `OutOfRange`, `Obstructed`, `NoAmmo`, `TargetLost`, `None`).
    - Multiple friendly units: composition breakdown (`2 Tanks, 1 Recon`) without aggregated weapon readiness.
    - Enemy contact: ContactId, observation state (`Current` vs `Last Known`), classification, and age in seconds without leaking health or ammunition.
  - Independent `OrderNotification` displays queued or rejected orders for 3.0 seconds, independent of selection card updates.
- Cached presentation state minimizes heap allocations during badge updates, refreshing label strings and styles only when contact state, role, or integer seconds elapsed change.

### 18.5 Weapon and impact feedback

- Event presentation semantics and information privacy:
  - `ShotFired`: Presentation effect attached to firing unit only when observed by client. Queries shooter view for muzzle transform (`TryGetMuzzleTransform`); if present, spawns directional muzzle flash and expanding smoke puff aligned with weapon bore, and triggers local barrel recoil. If shooter is not in client view, effect begins from server-provided event origin without revealing hidden unit node.
  - `TookFire`: Target hit feedback only (impact flash, spark burst, rising dust puff, metallic strike sound). Does not display synthetic muzzle flash or trace line from unseen firer, preserving shooter location secrecy.
  - `UnitDestroyed`: Triggers distinct destruction explosion, temporary ground scorch residue, and destruction rumble. Cancels any pending in-flight cosmetic impacts within 3.0 m to avoid double-hitting dead vehicles. Live views are removed authoritatively via snapshots and never recreated or revived by visual effects.
- Visual feedback components:
  - Barrel recoil: Dedicated `BarrelRecoil` node translates along local +Z axis into turret mantlet (-0.35 m max displacement) and smoothly interpolates back to bind pose over ~0.10 s at 3.5 m/s. Fallback models displace `Model` node only. The `Root` node remains completely stationary, ensuring health bars, role badges, and selection rings never jolt.
  - Directional muzzle flash: Unshaded bright yellow sphere scaled along firing vector (0.08 s lifetime, shrinks to zero).
  - Muzzle smoke puff: Semi-transparent expanding smoke (0.40 s lifetime, drifts upward at 0.7 m/s, scales from 0.55 m to 1.7 m, fades alpha).
  - Moving shell streak: Timed transient tracer mesh traveling along flight path at 160 m/s with compact 3.2 m streak length and bounded duration [0.08 s, 0.22 s]. Avoids full-length static laser lines across the map.
  - Impact flash and sparks: Unshaded orange flash (0.16 s lifetime) and spark burst (0.20 s lifetime, 2.0 m/s upward velocity, 0.70 m radius).
  - Impact dust and smoke: Expanding dust cloud (0.40 s lifetime, rises at 1.2 m/s, expands to 2.2 m).
  - Destruction fireball: Large unshaded orange explosion sphere (0.60 s lifetime, rises at 0.9 m/s, scales up to 3.6 m radius).
  - Ground scorch residue: Dark circular scorch disk (`CylinderMesh`, radius 1.9 m, height 0.02 m) clamped to terrain surface +0.03 m. Persists for 15.0 s, smoothly fading out over its final 4.0 s.
- Dedicated object pooling and memory budget:
  - Fixed pre-allocated pools: 16 Tracers, 16 Muzzle Flashes, 24 Smoke Puffs, 16 Sparks, 16 Impacts, 8 Explosions, 16 Ground Residues, 16 Pending Impacts, 6 Audio Players.
  - Over-budget handling: Oldest active instance is reclaimed gracefully without visual pops or log errors.
  - Zero heap allocations: All pool elements use pre-allocated node arrays and value-type struct slots (`TimedMesh`, `TimedTracer`, `PendingImpact`, `ActiveVoice`). The warmed update loop produces strictly 0 bytes of managed heap allocations.
- Spatial audio and voice stealing:
  - `AudioListener3D` is positioned at the camera ground focus point (`CameraRig.Focus`), completely eliminating volume loss from RTS camera altitude offset (~140 m) while maintaining horizontal stereo panning.
  - Priority-based voice stealing: `Explosion (3) > Cannon (2) > TookFire (1)`. When all 6 audio channels are occupied, incoming requests can only preempt active voices of strictly lower priority; equal or lower priority requests are dropped cleanly without disrupting playing voices.
  - Procedural sound synthesis: `CannonSound` builds the cannon report from filtered noise: a blast wave, a short crack, a boom that sweeps from bright to dull, a short low thump, two quiet dull echoes and a rolling rumble over 3 s. Ground impact thuds are likewise built from swept filtered noise with punch, thud and debris layers, no tonal sine oscillators, and over 99% of energy above 60 Hz so small speakers can play them. There are no sine beeps, and most energy sits above 60 Hz so small speakers can play it. Three seeds give three variants, picked at random per shot. Metallic impact snap and explosion rumble are synthesized in `CombatEffects`. All sounds get random pitch variation (+-4%), and Godot's default distance filter on `AudioStreamPlayer3D` makes far shots duller.
- Frame pacing and stall resilience:
  - Frame delta clamping and elapsed overshoot compensation prevent stuck effects or visual artifacts after large frame stalls or backgrounding. If overshoot exceeds effect lifetime during stalls, mesh instances are discarded immediately without spawning.

### 18.6 T-62 asset study

The T-62 preview explores restrained stylized realism with physical meter-scale
proportions, rounded cast-turret profiles, hard armor plate edges and matte materials.
There is no deliberate pixelation. Map palette and atmosphere can vary independently;
vehicle scale, presentation pivots and readable shot feedback remain consistent.

The asset is generated from original source with three explicit LODs. Shared material
palettes and linear per-part vertex tints keep material/surface counts bounded.
sRGB authoring colors are converted to glTF linear factors, with an import script
enabling vertex-color multiplication, seeded triplanar paint variation and roughness.
Broad height-dependent dust is baked into the linear vertex colors.
The generated manifest records actual counts and close/RTS/distant roles.
The current models share a broad cast-turret roof, more upright cheeks, rolled front
mudguards and lower fender stowage. Close detail adds pressed wheel depressions,
towing loops, turret rails, individual track shoes and procedural surface relief.
The RTS material omits that normal texture, and its mesh removes those small fittings.
The model includes dished wheels, a toothed rear sprocket, tangent track runs,
exposed fender fuel cells and removable rear drums behind the armor. External layout is checked against operator-manual
views and museum photographs; local dimensions and curvature remain approximations.
Automatic import LOD generation is disabled for this three-LOD authored asset.
The concept image is an art reference; its printed polygon count is not an asset measurement.

The isolated preview uses fitted orthographic cameras, gameplay camera sizes 38/75/160,
optional scale/grid/comparison views, articulation and shot feedback. Normal mission
visual mappings and authoritative simulation data are unchanged. These asset studies
still require large-battle render validation before setting production GPU budgets.

### 18.7 Faction vehicle presentation

- Own tanks use the faction of their player from `MatchSetup`: player 1 (Soviet) gets the T-62,
  player 2 (US) the M60A1. `visuals.json` holds per-faction overrides of a unit's scene, LODs and
  track gauge. A match without factions, or an unknown faction, keeps the base prototype scene.
- Identified enemy tank contacts use the RTS model of the single hostile faction from the setup
  (`contact_tank` in `visuals.json`). The contact classification decides whether a model is shown at
  all, as before; with mixed hostile factions the generic model is used.
- Both tanks come from `tools/model-authoring` (`build_t62_alt.py`, `build_m60.py`, shared
  `vehicle_kit.py`). Proportions were checked by overlaying orthographic renders on published 1:35
  and dimensioned drawings. Vertex colors carry baked ambient occlusion and a dirt mask; markings are
  textured decals projected onto the armor.
- `Assets/Vehicles/Shared/vehicle_import.gd` puts the shared `vehicle_weathering.gdshader` on these
  assets at import: procedural mottling, dirt, streaks, sun fade and, for the M60, MERDC Winter Verdant
  camouflage, all without UVs. Import compression, automatic LODs and shadow meshes are disabled for
  them because running gear meshes carry motion data in UV and UV2.
- Each unit view holds the three authored LODs and switches by orthographic camera size: LOD0 below
  45, LOD1 below 95, LOD2 beyond. Turret aim and barrel recoil are applied to every LOD.
- Track motion is presentation only. The view accumulates travel per side from interpolated
  displacement, with a turn differential from the change in replicated heading, clamped so heading snaps
  do not spin the tracks. The travel goes to the shader as a per-instance parameter. Wheels rotate
  about their axles; LOD0 track links move along the run and repeat seamlessly after one pattern cycle.
  No nodes or draw calls are added for this.
- LOD0 is about 24k triangles per tank and is visible at the close RTS zoom. A large-battle render
  budget has not been measured yet.
- The `apc` unit is an armored personnel carrier. Soviet APCs use the BTR-70 from `build_btr70.py`,
  with three LODs (LOD0 about 11.5k triangles). Its bow, roof and turret were checked against the
  V. Brovkin 1:35 drawing in side, front and top views. US APCs keep a placeholder until the M113A1
  exists. Identified enemy APC contacts use `contact_apc` in the same way as tanks.
- The APC carries no weapon yet; its 14.5 mm KPVT comes with machine guns in M2. Troop capacity
  comes with infantry and containers (section 11.2). Its speed and sensor values use the same
  prototype scale as the other units.
- The Soviet `brdm2` type uses the BRDM-2 model from `build_brdm2.py` (three LODs, checked against a
  dimensioned drawing). Identified Soviet recon contacts use its LOD1 through `contact_recon`. The
  prototype `recon_vehicle` on the test maps keeps the prototype model.
- Wheels use the same motion data as track wheels: they turn with the travel of their side, and the
  wrap period is a whole number of wheel turns.

### 18.8 Vehicle effects

- All vehicle effects are presentation only. They read the same events and interpolated positions
  as the rest of the view and never feed back into the simulation.
- Particles are `GpuParticles3D` emitters built in code (`ParticleEffects`): one procedural soft
  round sprite, a color ramp and a size curve per effect, unshaded billboards that keep the
  per-particle scale, soft edges against the ground, world-space emission and no shadows.
- Muzzle blast dust: a `ShotFired` event raises a low fan of brown dust in front of the muzzle,
  starting on the ground just behind it. Strength falls with muzzle height above the terrain and the dust
  is skipped above 3.5 m. It starts at the same event origin as the muzzle flash, so it shows nothing
  the flash does not already show.
- Barrel smoke: gray gun gas drifts up from the muzzle for about 1.5 s after each shot.
- Both shot effects use fixed pools of 6 emitters each in `CombatEffects`; the oldest is restarted
  when all are busy. `Clear` stops them. Shot events older than 0.2 s skip them; younger events
  fast-forward the particles by their age, like the muzzle flash.
- All emitters share one draw pass (quad and material), and the shot effects share their process
  materials. Movement dust only has its own emission box per vehicle type.
- Hull rock: barrel recoil also starts a damped spring (1.8 Hz, damping ratio 0.28) that tilts the
  model about the axis across the shot direction, so a shot over the front lifts the nose. The spring
  runs in fixed 1/120 s substeps with a frame cap and settles back to the pure heading pose. `Root`
  stays still, as with barrel recoil.
- Movement dust: tracked vehicle models get one continuous emitter per side, sized from the bounds of
  that side's running gear in LOD0. The emission rate follows the smoothed ground speed (full at
  6 m/s), stops when the vehicle halts and is off at LOD2. A step longer than 2 m between two
  position updates is a correction, not driving, and raises no dust.
- `--vehicle-effects-capture <directory>` drives one tank, fires it and saves frames of the dust,
  shot, blast and smoke for visual review.
- Open rules questions, not implemented: whether a shot briefly reveals the shooter, and whether
  muzzle dust briefly blinds the shooter on dry ground. Both need a TDD rules change first.

### 18.9 Fog of war and relief

- Terrain relief shading in `TerrainView` scales height tinting to the map height range (`MinHeight` to `MaxHeight`) rather than a fixed ceiling, keeping height variations discernible across maps of all sizes.
- Hillshade lighting from terrain normals uses conventional cartographic northwest light (azimuth 315 deg, altitude 45 deg, direction `(-0.5, 0.70710678, -0.5)`). Normal dot lighting scales with increased slope contrast, rendered unshaded so scene lights do not wash out subtle relief.
- Fog of war on the map (`FogOfWarView`) visualizes terrain currently observed by friendly units. It operates strictly on client-known state: own units from `ClientReplica`, perception limits (`sensor_range`, `eye_height`) from the unit catalog, the terrain heightmap, and scenario static geometry. It never inspects server simulation state or hidden enemy truth.
- A 2D visibility grid covers the terrain: 20 m cells on kilometer maps and 4 m cells on small test maps. Visibility is evaluated using a per-cell viewshed (XDraw) that visits every cell in range exactly once in square rings outward from the observer. Each cell interpolates its horizon from closer neighbor cells, and ground points not below that horizon are seen. Structures raise the horizon of cells they cover to their top.
- Recomputation runs at approximately 4 Hz (every 0.25 s). Units are swept within a frame time budget of 0.85 ms and the sweep continues in the next frame, so per-frame CPU time stays under 2 ms.
- Each own unit keeps the cells it saw at its last sweep, and the grid counts how many units see each cell. A unit that moved less than 2 m keeps its viewshed and is not swept again; a moved unit removes its old cells and adds the new ones; a unit that left the replica removes its cells. A force that holds its ground costs almost nothing per cycle, and only movers are swept.
- When publishing visibility, the fog texture is blurred by one cell into a preallocated R8 image and texture, ensuring zero managed heap allocations in steady state while providing smooth, soft boundaries at any camera distance.
- Vehicles, contacts, ghost markers, role badges, projectile tracers, and combat effects remain fully visible regardless of map fog.
- The overlay can be toggled in game using F9 or V for comparative inspection and debugging.

### 18.10 Local tank battle observer experiment

The separate `TankBattleDemo.tscn` scene runs 100 T-62 against 100 M60A1 on an open 3.2 x 3.2 km map.
The Game.Rules.Ops scenario owns spawning, bot decisions and elimination/time-limit termination.
Godot owns presentation and a local composition root with two normal ClientSessions and one
SimulationHost. The two private side views are shown together only in this local observer scene;
network player knowledge and wire messages are unchanged. Each bot consults only its own
PlayerKnowledge and submits ordinary movement commands. Auto-fire uses the existing combat rules.
A fixed seed and deterministic initial reload offsets provide a reproducible opening. Restart uses
the same seed. Pause freezes simulation and effects while leaving the camera active. Cosmetic
lighting presets do not introduce night detection rules. The observer shares fractional wheel/pinch
handling with the normal client. Middle drag, Alt+mouse or Q/E rotates the view; T/G changes pitch within
8 to 85 degrees. Right/down mouse motion increases yaw/pitch. V toggles perspective for
cinematic horizon views and the orthographic RTS view. Perspective camera height is clamped
above the visual terrain. Health bars face the camera, preserving their left fill anchor. Panning follows the rotated screen axes. Zoom size is limited to 35 to 3200 m.
A visual terrain apron continues beyond the playable boundary and fades into atmospheric haze;
it supplies neither navigation nor spotting cover. The normal client's initial fixed view is preserved.
Two staggered rows of 50 tanks per side start 2.5 km apart, outside catalog sensor range.
Bots advance each formation slot up to 900 m toward the center. They interrupt movement with a
normal move-to-current-position command when a Current contact is within their weapon range,
penetrable according to the existing observed-contact armor check, and has clear LOS from the
muzzle to a known target observation point. These checks use only PlayerKnowledge, catalog data
and static geometry. Normal auto-fire retains target selection, aiming, reload and damage rules.
Tanks hold through reloads, without repeating accepted halt commands. After one second without
a usable target, they resume their original formation destination. The planned front-row destinations
are 700 m apart, but engagement can halt units earlier. The existing rules prevent fire on the move.
The 3.2 km extent stays below the current 8192-portal hierarchical navigation limit;
a 4 km map exceeds it with the default 50 m sectors. This is a scripted formation experiment, without dynamic
formation management or coordinated target assignment.
Two existing FogOfWarView instances consume only each side's own replica, catalog and static
geometry at 40 m resolution. The ground shader shows amber/cyan viewshed boundaries and dims
unseen ground. F cycles combined, Soviet, US and off. This is an approximate ground viewshed;
unit detection still comes from authoritative contacts. All vehicle models remain visible to this
local observer regardless of the selected ground layer. Lighting haze is separate from visibility.
The renderer reserves 262144 sixteen-byte global shader slots (4 MiB). Each instance using
instance uniforms reserves 16 slots, giving an upper bound of 16384 instances before other
consumers. The real-renderer regression loads all vehicle LODs and three deferred restarts:
200 tanks retain 4400 motion mesh instances and peak at 8800 during restart overlap. It requires
25 percent slot headroom and fails on engine errors. Headless smoke does not replace this check.
The separate `tests/vehicle-rendering-smoke.sh` opens an actual rendering window.
The demo does not implement dynamic
avoidance, a network spectator protocol, unlimited ammunition, artillery or aircraft.

## 19. Rule sets and modularity

A rule set is a list of systems plus data. Replaceable points:

- movement and navigation
- detection
- shot resolution and damage
- supply or economy
- victory conditions

Turning a system off means not registering it. A unit without a `Fuel` component does not use fuel, without any `if (sandboxMode)`.

We do not write five versions of each system up front. Two cheap experiments check the seams:

- Sandbox rule set (milestone 1): logistics off, infinite ammo. Proves a system can be removed.
- A test unit with different movement and weapon logic (after milestone 2), for example a walker with an energy weapon. Proves behavior can be replaced. If this needs changes in networking, UI and entity code at the same time, the seams are wrong.

## 20. Platforms and tooling

- Use the .NET 10 SDK (LTS). Projects target `net10.0` by default. Use the Godot 4.7 .NET build, version pinned per milestone.
- M0 step 1 verifies that the Godot client can build, export and run with `net10.0` on macOS and Windows.
- If that target is not supported, the fallback is `net8.0` for `Game.Godot` and every shared library it references: `Engine.Core`, `Engine.Net` and `Game.Rules.*`. `Game.Server`, tools and tests may stay on `net10.0` and reference those `net8.0` libraries. A `net8.0` client must never reference a library that targets only `net10.0`.
- The fallback is selected for the solution, not just the client. CI builds the server and the Godot client using the selected targets; exported client builds are tested on both platforms.
- Libraries:

| Need | Choice |
|---|---|
| Entity storage | Arch (provisional, section 4) |
| Content data | `System.Text.Json` with source generators |
| Transport | LiteNetLib behind `ITransport` |
| Math | `System.Numerics` |
| Snapshots and commands | Own bit writer on `BinaryPrimitives`, no reflection |
| Saves | MemoryPack or own writer (section 4) |
| Tests | xUnit, BenchmarkDotNet |
- Test exported builds on macOS (Apple Silicon) and Windows from the first month.
- CI on GitHub Actions: build and test the core on macOS, Windows and Linux.
- Release builds on macOS need signing and notarization. Windows builds should be signed to avoid SmartScreen warnings.
- macOS input: trackpad camera gestures, Cmd instead of Ctrl for control groups, HiDPI UI scaling.
- Debug overlays: navigation, spatial grid, LOS, sensor ranges, AI state, knowledge per side, current paths.
- Performance counters per system, visible in a debug HUD: time per system, allocations per tick, path requests, LOS queries, snapshot size, bytes per second.

## 21. Performance budgets

These are starting hypotheses. They are revised after the first measurements.

| Item | Target |
|---|---|
| Simulation tick | 20 Hz, under 15 ms on the target machine, measured as p99, not average |
| Client rendering | 60 FPS on a mid-range Mac and PC at 1080p |
| Battle size | 1v1 and 2v2, about 20 to 40 units per player plus reserves |
| Stress test | 1000 active entities, separate scene from the gameplay slice |
| Bandwidth | under 30 KB/s per client in a heavy battle |
| Maps | 2 x 2 km test map, 10 x 10 km main maps, 15 x 15 km large |

Heavy scenario for measurements: all units observing, firing, changing orders and requesting paths at the same time. Large empty map and dense battle are measured separately.

## 22. Milestones

No dates. Each milestone ends with something playable.

### Agreed M1 order (vertical slice)
1. Orders: fire stances with auto-fire, attack orders that drive into range, range circles,
   cued fire at targets spotted by allies. Then turret traverse at period rates.
2. Readability: contour lines, adjustable camera pitch, compact zoom-aware badges, health and
   reload over units.
3. Armor and the BTR-70 and BRDM-2 KPVT.
4. Wrecks of destroyed vehicles as known world facts, and area fire with dispersion.
5. Terrain layers: forest (infantry only), roads (speed_road), towns, water with fords and
   bridges (amphibious vehicles swim), buildings with footprint and height.
6. A map builder from open data (Copernicus DEM and OpenStreetMap, licenses recorded) and the
   first real map: Huenfeld and the Haune valley, 5 x 5 km, Fulda Gap 1979 to 1982.
7. A bot for the battle scenario.
8. Atmosphere: engine and track sounds, ambient sound, weather (fog and rain also reduce
   visibility in the rules), sirens.
Then a pause to play full battles. After it: infantry (mounting APCs, occupying buildings),
hull heading and collision, artillery, air, minimap and interface.

### M0. Skeleton

- Solution with the projects from section 5.2. Dependency rules enforced.
- Core and rules test projects run in CI on three platforms.
- Godot project shows a heightmap and moving cubes driven by the core through the in-memory transport.
- The selected framework targets build together. The exported Godot client runs on macOS and Windows. If the fallback is needed, all client-referenced shared libraries use the compatible target from section 20.

Implementation checkpoint:

- The .NET SDK is pinned in `global.json` and Godot .NET in the client project SDK attribute. All projects target `net10.0`.
  The macOS Apple Silicon development client runs on .NET runtime 10.0.12.
  No framework fallback is needed on this machine.
- Arch stores entities behind `WorldState`. Public IDs do not expose Arch handles.
  `MovementSystem` runs an allocation-free inline Arch query. Ops and Sandbox
  select systems through `RuleSet`; a rule set can omit or replace movement.
  MSBuild and regression fixtures check project references, SDK use and resolved
  compile/runtime dependencies, including transitive Godot assemblies.
- The procedural map is 128 x 128 m. Movement is straight-line at 20 Hz.
  Height queries and input raycasts use the same immutable heightmap as the view.
  Picking takes an explicit range from the camera and clips traversal to map bounds.
  Visual grids support rectangular maps.
- Commands and full snapshots cross a bounded byte transport. Snapshots run at
  10 Hz. The client interpolates its replica with a 150 ms delay.
  The M0 wire format uses little-endian fields and float coordinates. Bit packing,
  quantization, contacts, map-object state and protocol negotiation are M1 work.
  M0 session startup rejects a world capacity above the 1024-record snapshot limit.
  The view set adds and removes nodes from full snapshots and clears removed selections.
- The console host measures monotonic elapsed time, rather than counting timer
  notifications. Each advance runs at most five ticks. Remaining time is retained
  and reported as backlog; a delayed wakeup does not silently discard simulation time.
- Forward+ is the default client renderer and has been run through Metal on Apple
  Silicon. Compatibility remains available by command-line override. CPU-only
  movement/snapshot microbenchmarks have no dependency on either renderer.
- Player knowledge currently includes own units only. Hidden enemy IDs and positions
  never enter the client replica. Sensor-driven contacts are M1 work.
- JSON content provides the prototype unit speed. Ops and Sandbox both register
  movement only. This does not yet prove the removable logistics system from section 19.
- The console runner, client smoke test, xUnit projects, allocation checks and
  movement/snapshot microbenchmark are available. CI is configured for three OSes.
  No remote peer, reconnect, save/load or replay implementation is included yet.

- The hosted three-platform CI run on main (Ubuntu, macOS, Windows; run 37940079650)
  succeeded, verifying builds, unit tests, the server smoke test and launcher checks.
  That run did not include export jobs or packaged game artifacts.
- Godot 4.7.2 .NET release export presets and scripts are configured for macOS
  (Universal, covering Apple Silicon and Intel) and Windows x64. The pinned export
  templates package `4.7.2.stable.mono` is installed via automation scripts.
- The exported macOS Universal package was validated locally with ad-hoc code signature
  checks and the headless client smoke test, passing view lifecycle and rectangular grid
  regression assertions.
- Dedicated CI jobs (`export-macos` and `export-windows`) are configured to package the
  clients, run the headless smoke test on the exported executables and upload game artifacts.
  The hosted export CI run (run 38075713603) succeeded, validating packages and headless
  smoke tests on macOS and Windows.

Open exit check: native interactive Windows gameplay on a desktop Windows machine. M0 remains open
until this check passes.

### M1. First battle

- 2 x 2 km map: open ground, cover, a road, a bridge.
- 10 to 20 units per side: recon, tank, infantry squad, AT team, mortar, transport truck.
- Detection, cover, simple combat, smoke, one objective to take and hold.
- Two clients over the network from the start. Plus a simple bot.
- Sandbox rule set.
- Separate stress scene.
- Movement test scenarios from section 9.3 pass.
- Open questions from section 4 that block M1 are closed.

Implementation checkpoint:

- Network session slice implemented. `Game.Server` runs an authoritative dedicated server
  ticking at 20 Hz on a single monotonic clock.
- Two independent Godot clients connect over LiteNetLib UDP transport.
- The server assigns player slots and binds movement commands to the connected peer.
  Client-supplied player identity is never trusted.
- Handshake protocol validates protocol version, content hash, map hash and rule set.
- Rejection of foreign unit commands and duplicate sequence numbers is enforced.
- Fog of war filters full snapshots per player at 10 Hz; hidden enemy units never reach
  the client replica.
- Snapshots larger than one UDP packet are fragmented and reassembled; only complete,
  validated snapshots are applied.
- Automated integration tests cover two-client loopback sessions, foreign unit rejection,
  hidden state preservation, handshake mismatch and multi-packet snapshot reassembly.
- Geometric line-of-sight query foundation implemented in `Engine.Core`.
  `StaticGeometry` provides fast ray obstruction checks against continuous bilinear
  terrain and opaque axis-aligned structures.
- Exact quadratic intersection solves bilinear terrain patches analytically without
  sampling step errors.
- Flat uniform spatial grid indexes structures with zero heap allocation per query.
- Diagnostic `LineOfSightResult` reports obstruction kind, hit coordinate, distance,
  and obstructing structure identifier.
- Standalone visual sandbox in `Game.Godot` (`--los-sandbox`) allows interactive inspection
  and headless automated regression testing across six reproducible scenarios:
  open field, hill occlusion, building occlusion, corner peek, low wall hull-down,
  and elevated vantage.
- Visual reconnaissance slice implemented in `Engine.Core`, `Engine.Net`, `Game.Rules.Ops`, and `Game.Godot`:
  - Data-driven visual sensors (`VisualSensorProfile`) and multi-point target profiles (`TargetObservationPoints`) loaded from `content/coldwar/units.json`.
  - Authoritative scheduled detection running at 10 Hz (interleaved across ticks) with allocation-free spatial candidate lookup (`UnitSpatialGrid`).
  - Authoritative per-side tracking (`SideContactTracker`) with strongly typed `ContactId` decoupled from `EntityId`.
  - Continuous track maintenance (`Current`), track loss to frozen coordinates (`LastKnown`), track correlation on rediscovery within distance and time gates, and expiration after memory duration.
  - Wire protocol v3 snapshot encoding with dedicated 18-byte contact records, 19-byte own unit records with navigation status, and strict information isolation.
  - Client replica contact management (`ClientReplica.Contacts`, `TryGetContact`) with distinct Godot views for own units (recon antenna, tank turret) and contacts (vibrant red diamond vs muted ghost marker with ground ring).
  - Repeatable two-client reconnaissance scenario (`--scenario visibility`) running over dedicated server UDP loopback and local session.
  - Automated test coverage across engine core, rules validation, protocol serialization, UDP loopback network integration, and Godot client lifecycle.
- Server-side static navigation slice implemented in `Engine.Core`, `Engine.Net`, `Game.Rules.Ops`, and `Game.Godot`:
  - Time-sliced A* planning over 0.5 m static clearance grid with per-tick node expansion budget and indexed min-heap.
  - Continuous swept-capsule segment clearance preventing wall tunneling and corner clipping.
  - In-flight route retention: moving units keep moving along their old route during multi-tick search until the new route is found.
  - Obstacle stand-off snapping: targets within 3 m of obstacles snap to the closest passable clearance point; deep interior destinations report InvalidDestination and halt.
  - Owner-only status reporting: snapshots convey navigation outcome to unit owner without leaking to enemy views.
  - Zero steady-state heap allocations during simulation ticks.
- Server-authoritative direct-fire combat slice implemented in `Engine.Core`, `Engine.Net`, `Game.Rules.Ops`, and `Game.Godot`:
  - Data-driven weapon and unit combat profiles loaded from `content/coldwar/units.json`.
  - Explicit attack orders targeting perceived `ContactId`, using a unified sequence stream with movement commands.
  - Stationary firing only: attack orders cancel movement, and movement orders cancel attacks.
  - Tick-based reload durations, finite ammunition capacity, and single-application fixed damage.
  - Per-shot revalidation of range and 3D line-of-sight clearance against terrain and opaque structures from weapon muzzle to vertical target points.
  - Clean authoritative unit destruction: removed from world, navigation, combat, and perception, with deterministic destruction execution order.
  - Recipient knowledge filtering: presentation events (`ShotFired`, `UnitDestroyed`) sent only to observers who perceive the event entities. Hidden shooters omit events to prevent leaking truth coordinates.
  - Wire protocol v6 with 30-byte own unit records (health, ammo, reload progress, attack status, target contact), 39-byte combat events (with 3D endpoints, TookFire coarse bearing, and shooter attribution), and 18-byte attack commands.
  - Presentation effects in Godot: muzzle flash, smoke burst, vehicle recoil, tracer beam, impact effect, and synthesized firing audio.
  - Zero steady-state heap allocations during combat simulation ticks.
  - Prototype limitations for this slice: instantaneous shot resolution without physical projectile simulation, stationary firing only, fixed damage without armor penetration or deflection, no accuracy dispersion, visual tracers are visual effects only, and no persistent wreck obstacles yet.
- Playable reconnaissance raid mission slice implemented in `Engine.Core`, `Engine.Net`, `Game.Rules.Ops`, and `Game.Godot`:
  - Mission observation model (`IMissionSystem`, `MissionObservation`, `MissionStatus`, `MissionEndReason`) with privacy-preserving target representation.
  - Termination rules: Victory upon destroying enemy command vehicle with recon vehicle preserved; Defeat on recon vehicle destroyed, all combat tanks destroyed, or 12:00 time limit expired. Defeat strictly takes precedence on simultaneous conditions. Immutability of announced results.
  - Simulation command gating and halting: game systems halt movement and clear combat orders upon finish; subsequent client orders rejected.
  - Server-side bot (`ReconRaidBot`) executing only through `PlayerKnowledge` and standard commands, rate-limited to 10 ticks (2 Hz), with deterministic target acquisition, stationary firing, approach, and bounded patrol retries. Reacts to unseen `TookFire` combat events and acknowledges events without leaking opponent state.
  - Dedicated server hosting with reserved bot slot rejecting unauthorized clients (`HandshakeReason.ServerFull`).
  - Wire protocol v8: 32-byte `MissionObservation` header in snapshots (status, end reason, time limit, finished tick, search area, detected contact id, flags) and 19-byte contact records (with `DetectionLevel` and `ContactClassification`). Target contact identity is resolved strictly via earned classification without truth links.
  - Client UI in Godot: `MissionPanel` with briefing card, dynamic objective status, 3D search ring, end-game modal, and retry button.
  - Complete local retry cycle (`LocalSession`) re-creating fresh simulation state and clearing visual effects without leaking memory or carrying over old contacts.
  - Zero steady-state heap allocations during simulation ticks.
- Revised M1 implementation order:
  - Visual reconnaissance and sensor line-of-sight (implemented).
  - Server-side static obstacle navigation prototype slice (implemented).
  - Server-authoritative direct-fire combat slice (implemented).
  - Hierarchical navigation for kilometer-scale maps (implemented).
  - Playable reconnaissance raid mission (implemented).
  - Coarse cost layers, formations, multi-unit collisions, dynamic obstacles, and destruction.
  - Reconnect recovery, bots, and save/load.
- Out of scope for M1 combat: artillery, missiles, aviation, armor penetration beyond direct fire armor by side (section 12.3), suppression, building destruction, production, and logistics replenishment.
- Planned order of the next M1 work:
  1. Box selection, control groups and same-type selection (section 18.1, implemented).
  2. Hull heading stage 1: steering, reverse and final facing, with the current circle collision
     (sections 9.5, 12.2).
  3. Order queue for user waypoints (section 12.2).
  4. Play the existing missions, then order the rest by what combat lacks most: hit probability and
     fire on the move, armor by side (section 12.3), smoke (section 12.4), area and blind fire
     (section 12.3).

Exit criteria (technical):

1. Removing a view does not change the simulation.
2. Reconnect restores the state and reveals no hidden enemy.
3. A save in the middle of movement and combat continues correctly after load.
4. The heavy scenario fits the budget on the chosen Mac and Windows machines.
5. With presentation events dropped, snapshots still restore an observed destroyed bridge and allowed objective state, including after reconnect. An unobserved bridge change stays hidden, and the last known state remains when the bridge leaves view.

Exit criteria (gameplay):

1. Players use recon and flanking on their own.
2. Force composition affects the result.
3. After a loss the player can explain why and suggest another plan.
4. Controls let the player carry out that plan.
5. Players want to try a different tactic after a few matches.

### M2. Logistics and fire support

Fuel, ammo, supply trucks with supply radius, artillery fire missions with dispersion and observers, counter-battery, infantry in buildings, repair. Weapon slots and vehicle machine guns (section 12.5). Wheeled vehicle kinematics (section 9.5, stage 2) if M1 play testing does not pull it earlier.

### M3. Operational scale

10 x 10 km map, road network, columns, formations, strategic zoom, reinforcements, map chunks, better bot.

### M4. Online

Internet 1v1, reconnect, replays, dedicated server on Linux. Then 2v2.

### Later

Helicopters, off-map air support, SAM, radar and EW, engineers and mines, destruction, night and weather, chemical and nuclear weapons, amphibious operations.

## 23. Code rules

- No gameplay logic in Godot nodes.
- No `using Godot` outside `Game.Godot`.
- No LINQ, closures with captures, boxing or per-tick collections in hot paths.
- Every bug fixed from review or play testing ships with a regression test.
- Golden tests: a fixed seed and command log for N ticks must produce a known state hash on the same build.
