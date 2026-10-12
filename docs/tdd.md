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

- One match seed.
- Separate streams per domain: combat, artillery, AI, map events. Changing the AI must not change artillery spread.
- No `Random.Shared`, no `GetHashCode` based logic, no dependency on thread completion order.

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

A contact holds: detection level (detected, identified), estimated type and strength, last known position, last seen tick, and what the side may know about its state (for example "damaged", never exact health).

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

### 9.2 Following the path

Path following is a separate problem from path planning and needs as much attention:

- turning radius and reversing for vehicles
- columns with spacing on roads
- stopping behind friendly units
- local avoidance

### 9.3 Required test scenarios

1. Two columns meet on a narrow road.
2. A group crosses a bridge.
3. The lead vehicle of a column is destroyed.
4. A bridge is destroyed after the move order is given.
5. A group turns around and retreats.

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
- Snapshot wire protocol v3:
  - Protocol version incremented to 3.
  - Header is 14 bytes (added `ushort ContactCount` at offset 12).
  - Own unit record is 19 bytes: `uint Id` (4 bytes), `ushort Definition` (2 bytes), `Vector2 Position` (8 bytes), `uint SupportSurface` (4 bytes), `byte NavStatus` (1 byte).
  - Contact record is 18 bytes: `uint ContactId` (4 bytes), `byte State` (1 byte), `byte Classification` (1 byte), `Vector2 Position` (8 bytes), `int LastSeenTick` (4 bytes).
  - Maximum snapshot payload size is 24,078 bytes (`14 + 1024 * 19 + 256 * 18`).
  - Information isolation: server-filtered snapshots only contain permitted observations for the recipient player. Foreign truth state is never transmitted.

Detection is a measured CPU hotspot that fits the per-tick simulation budget: steady-state detection and snapshot serialization execute with zero managed heap allocations.

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

### 12.1 Attack orders

An attack order targets either a `ContactId` or a ground position. Never an enemy entity ID.

When the target contact is lost, the unit acts by its stance:

| Stance | Behavior |
|---|---|
| Aggressive | Move to the last known position and search |
| Hold | Keep position, keep aiming at the last known position for a short time, then drop the target |
| Fire at will | Drop the target and pick a new one |

Area fire on a ground position (suppression, artillery) does not need a contact. When a lost contact is linked again (section 7.1), the order continues.

## 13. State and events

- Anything that must survive reconnect, save or late join is state. Example: `Bridge.State = Destroyed`.
- Events are hints for presentation. Example: `BridgeDestroyed` triggers the explosion effect.
- Every event sent to a client passes the knowledge filter. The result can be the exact event, an approximate one (sound or flash near a position), or nothing.
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
- Commands, snapshots and saves use explicit message types, not serialized game classes.

### 14.1 Snapshots

- 10 Hz to start, with a sequence number and server tick.
- Snapshots contain own units, enemy contacts, known map object states, allowed objective state and observed area effects. Every field follows its registry rules and the side's knowledge.
- M1 sends sparse full snapshots: the complete allowed view each time, no deltas. A unit or contact missing from a complete snapshot is gone from the client's view. World state blocks are also complete for that side, including facts kept from earlier observations.
- Map objects use `MapObjectId`. Their static geometry stays in the map package; snapshots carry allowed mutable fields such as bridge condition and objective ownership. A missing knowledge entry does not mean the object was destroyed or restored to its initial state.
- Clients apply a full snapshot only after the complete message is received. On reconnect the same state blocks restore the allowed view without needing earlier presentation events.
- Budget estimate: 100 unit records at about 16 bytes are 1.6 KB, or 16 KB/s at 10 Hz. This estimates only unit records. Measurements include contacts, world state, headers and transport overhead against the 30 KB/s budget.
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
- Resource types (fuel, ammo) are defined in data.
- Every data file has `schema_version`.
- A test loads and validates all content.
- Data hot reload in development builds.
- Mods are data only for now. Code mods are a security question for later.

## 18. Presentation

- Godot reads `ClientReplica` and interpolates. It never changes the simulation.
- Animation is driven by state (velocity, turret angle), never the other way around.
- Level of detail by distance (distances are starting guesses):

| Level | Distance | Rendering |
|---|---|---|
| LOD0 | up to about 150 m | Light `Node3D` per unit with parts (hull, turret, gun), visuals and audio only |
| LOD1 | about 150 to 600 m | Simplified meshes, no wheel or track animation |
| LOD2 | beyond about 600 m | `MultiMesh`, one mesh per unit type |
| Strategic zoom | camera height | NATO-style symbols as instanced billboards |

- Views never run game logic. Transforms come from the `ClientReplica` interpolator.
- Static and identical objects (trees, wrecks, far units) use `MultiMesh`, split by map chunk because a MultiMesh is culled as a whole.
- Infantry: skeletons close up, vertex animation textures with MultiMesh at a distance.
- Strategic zoom is also a performance tool, not just style.
- Terrain: Terrain3D or a custom clipmap, decided in milestone 1. Pin an engine and plugin version pair that works on both platforms.

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

### M0. Skeleton

- Solution with the projects from section 5.2. Dependency rules enforced.
- Core and rules test projects run in CI on three platforms.
- Godot project shows a heightmap and moving cubes driven by the core through the in-memory transport.
- The selected framework targets build together. The exported Godot client runs on macOS and Windows. If the fallback is needed, all client-referenced shared libraries use the compatible target from section 20.

Implementation checkpoint:

- .NET SDK 10.0.401 and Godot 4.7.2 .NET are pinned. All projects target `net10.0`.
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
- Navigation on the 2 km map requires the coarse cost grid (section 9.1).
- Scope of the next PR (coarse cost grid):
  - Narrow corridor passability per movement class (tracked, wheeled, foot, amphibious).
  - Routes > 64 waypoints re-planning and hierarchical path chunking.
  - Spatial candidate queries for segment checks and smoothing budget.
  - Once-only direct check for queued requests.
  - Coarse 5 m grid design with per-side overlays and group formation paths.
- Reconnect recovery, combat, weapons, bots and save/load remain pending M1 work.

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

Fuel, ammo, supply trucks with supply radius, artillery fire missions with dispersion and observers, counter-battery, infantry in buildings, repair.

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
