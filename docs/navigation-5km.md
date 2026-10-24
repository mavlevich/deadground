# Navigation scale slice: 5 x 5 km

This change enables headless static navigation fixtures at 5 km without a dense full-map
component grid or the old 8192-element portal storage. It does not create a playable map,
water/forest movement rules, bridge decks, traffic avoidance or group movement.

## Storage and load limits

- Open sectors have one implicit component, including map-edge sectors with boundary clearance.
- Only obstacle-adjacent sectors store detailed cells. At default settings they are 0.5 m wide.
  Four movement classes retain separate connectivity.
- Portals, edges and global search scratch use actual extracted counts. An empty 5 km square
  has 10000 sectors and 19800 portals per class, with zero stored detailed cells.
- Default load limits per class: 16384 sectors, 4000000 detailed cells, 131072 portals and
  1000000 intra-sector edges. Invalid limits and excessive dimensions fail explicitly.
  Detailed-cell sizing is checked before allocating its array; portal/edge limits are checked while building.
- `NavigationGrid` and `HierarchicalNavGraph` accept `NavigationBuildLimits` overrides for authoring.
  The grid preflight and each graph use the actual requested sector size. `OpsRules` passes
  `NavigationTuning.SectorSize` to the grid. A 5 km map at 100 m sectors has 2500 sectors per class,
  rather than being incorrectly checked against 10000 default-sized sectors.
- Global output remains 1024 portals per unit. Insufficient output reports `CapacityExceeded`
  with a zero result count. Sector obstacle queries no longer silently stop at 1024 obstacles.

## Constant-time disconnected-region rejection

At load, each graph assigns an index to every (sector, local component) pair. Union-find joins
pairs through portals, then flattens all roots into stored global labels. This also handles two
separate local components inside the same sector. Runtime label comparison is O(1), with no
union-find traversal or allocation. Both the navigation request path and the standalone global
search entry point reject disconnected pairs before starting A* or consulting the corridor cache.

Connectivity is an optimistic coarse test, not an exact geometric path certificate. Portal clearance
is still checked by A*, and fine swept clearance remains authoritative. The union does not filter
portals by the class's maximum radius, because a smaller actual radius can map to that same class.
Filtering there could incorrectly reject a valid smaller vehicle. New static geometry must rebuild
both connectivity labels and corridor cache. Dynamic bridge/building changes remain future work.

The negative fixture has a wall spanning the full map. All 200 orders into the opposite region
resolve `NoPath` in one tick, cache on or off, without starting any global search. A regression
then issues a reachable order requiring a detour and verifies successful movement to its exact goal.

## Reused corridors

A deterministic FIFO cache stores 16 successful global portal plans, keyed by start and goal
sectors/components and exact vehicle radius. Fine paths, swept clearance, exact destinations and
route chunks remain per unit. A cached corridor may be a valid approximation rather than the
shortest global route for the new start position. Set `GlobalPlanCacheEntries` to zero to disable it.
The cache does not merge units into formations or share fine paths.

**The 637-tick p99 figure applies only to the clustered barrier/200 group-order fixture.**
That run reuses 198 of 200 global searches. With cache disabled, the same orders take 2416 ticks
(120.8 simulation seconds) at p99. Scattered orders receive no cache hits in this fixed-seed sample.
They do not inherit the clustered speedup. Their aggregate latency is lower here because only
25/200 town orders and 76/200 barrier orders need a blocked-line detour; most random orders go direct.

## Reproduce

The default user NuGet cache is supported. For an isolated worktree-local cache:

```sh
export NUGET_PACKAGES="$PWD/.nuget/packages"
dotnet restore DeadGround.sln
dotnet run --project tests/Benchmarks --configuration Release --no-restore -- --navigation-5km
```

The cache directory is ignored by Git. Build assets in this worktree now reference `.nuget/packages`
inside the checkout, with no package dependency on Documents. To save structured measurements:

```sh
dotnet run --project tests/Benchmarks --configuration Release --no-restore -- --navigation-5km --json logs/navigation-5km-results.json
```

Create `logs/` first if needed. The checked-in [raw measurements](navigation-5km-results.json)
include every run, hash, seed, memory measure, work maximum and failure counter.

## Measurements after review fixes

Measured on the development macOS ARM64 machine, .NET SDK 10.0.401, Release, 2026-10-24.
A representative warm-up exercises global/local planning, smoothing and movement first. Every run
builds four class graphs and a planner with 1024 unit slots, issues 100 or 200 move commands in one
burst, then measures at least 1000 ticks and up to 8000 if initial requests have not all resolved.

- Open terrain, a 900-building town, and a barrier with a 600 m opening use clustered and scattered orders.
- The disconnected fixture uses scattered starts on the left and targets on the right of a full-height wall.
- Scattered orders use SplitMix64 seed `0x50002024` (1342185508), rejection-sampling clear points
  across the entire 5 km square, at least 250 m apart. Each cache pair has exactly matching order hashes.
- All 28 runs passed. Measured tick allocations were zero, stage work budgets held, and no capacity
  failures occurred. Reachable fixtures produced all requested initial paths. Negative fixtures
  produced only the expected `NoPath` outcomes.
- The randomized town exposed unbounded initial and inner smoothing scans. Both now resume at the
  saved candidate index and respect the per-tick ray budget, including a regression with a limit of one.
  The existing 34-wall snake test now waits for budgeted completion instead of relying on 300 ticks
  of unbounded scans. Both capacity outcomes and per-tick ray limits remain asserted.

In each paired column below, the order is **cache on (16 entries) / cache off (0 entries)**.
Initial-route p99 is in ticks at 20 Hz; divide by 20 for simulation seconds. For disconnected rows
it is the time to `NoPath`, not a successful path. Latency is reported separately from the CPU gate.

| Scene / orders | Units | Blocked lines | Initial p99 ticks on/off | Cache hits/misses on | Tick p99 ms on/off | Load ms on/off | Array payload MiB on/off |
|---|---:|---:|---:|---:|---:|---:|---:|
| open / clustered | 100 | 0 | 1 / 1 | 0/0 | 0.092 / 0.096 | 36.1 / 15.9 | 25.89 / 25.76 |
| open / clustered | 200 | 0 | 1 / 1 | 0/0 | 0.165 / 0.190 | 15.7 / 15.5 | 25.89 / 25.76 |
| open / scattered | 100 | 0 | 1 / 1 | 0/0 | 0.013 / 0.012 | 13.9 / 9.6 | 25.89 / 25.76 |
| open / scattered | 200 | 0 | 1 / 1 | 0/0 | 0.039 / 0.027 | 9.4 / 9.4 | 25.89 / 25.76 |
| town / clustered | 100 | 80 | 262 / 291 | 79/1 | 1.172 / 0.812 | 125.7 / 104.4 | 39.45 / 39.32 |
| town / clustered | 200 | 150 | 467 / 516 | 148/2 | 0.749 / 0.808 | 68.1 / 85.5 | 39.45 / 39.32 |
| town / scattered | 100 | 13 | 49 / 49 | 0/13 | 0.859 / 0.863 | 66.5 / 67.2 | 39.45 / 39.32 |
| town / scattered | 200 | 25 | 80 / 80 | 0/25 | 2.641 / 2.510 | 68.0 / 67.1 | 39.45 / 39.32 |
| barrier / clustered | 100 | 100 | 319 / 1210 | 99/1 | 0.722 / 0.668 | 57.8 / 58.1 | 39.55 / 39.43 |
| barrier / clustered | 200 | 200 | 637 / 2416 | 198/2 | 0.763 / 0.684 | 58.5 / 57.8 | 39.55 / 39.43 |
| barrier / scattered | 100 | 35 | 214 / 214 | 0/35 | 0.576 / 0.586 | 58.2 / 58.4 | 39.55 / 39.43 |
| barrier / scattered | 200 | 76 | 464 / 464 | 0/76 | 0.654 / 0.652 | 57.7 / 57.7 | 39.55 / 39.43 |
| disconnected / scattered | 100 | 100 | 1 / 1 | 0/0 | 0.001 / 0.001 | 62.9 / 63.3 | 41.07 / 40.94 |
| disconnected / scattered | 200 | 200 | 1 / 1 | 0/0 | 0.003 / 0.003 | 62.7 / 62.7 | 41.07 / 40.94 |

### Disconnected-area load and rejection cost

| Orders | Cache entries | NoPath | Max latency ticks | Global searches / expansions | Load ms | Array payload MiB | Retained delta MiB | Load allocation MiB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 100 | 16 | 100 | 1 | 0 / 0 | 62.9 | 41.07 | 46.74 | 61.52 |
| 100 | 0 | 100 | 1 | 0 / 0 | 63.3 | 40.94 | 46.62 | 61.39 |
| 200 | 16 | 200 | 1 | 0 / 0 | 62.7 | 41.07 | 46.74 | 61.52 |
| 200 | 0 | 200 | 1 | 0 / 0 | 62.7 | 40.94 | 46.62 | 61.39 |

The optional probe exits nonzero for unexpected outcomes, unresolved initial requests, any capacity
failure (including continuation failures), measured allocations, exceeded stage budgets, load >= 3 s,
array payload >= 128 MiB or navigation tick p99 >= 15 ms. It does not treat long request latency as
proof of responsive gameplay merely because the per-tick CPU gate passes.

These are navigation-only measurements. Sensing, combat, networking, heightmap-resolution costs,
rendering and full-match memory are excluded. The flat heightmap has four samples to isolate navigation.
Array payload includes the grid, four graphs, connectivity labels, search scratch, route storage and
cache, excluding object headers and load-time temporaries. Retained heap is a GC delta, not process
peak memory. Cumulative load allocation includes temporary build data, not peak live memory.

## Remaining limitations

- Clustered and scattered orders are different workloads. The cache helps repeated connectivity keys;
  unrelated orders can still pay a full budgeted global search each. Hundreds of independent detours
  are not certified responsive. Group routing, local search reuse or fairer scheduling need a separate
  measured change before making that gameplay promise.
- Early rejection proves coarse disconnectedness. A narrow opening can still be connected in the
  optimistic graph but unusable for the exact radius, requiring a normal failed search.
- Initial path latency means a first usable chunk, not final arrival. The probe does not necessarily
  run until every long journey finishes. A separate test follows a complete safe 5 km-scale detour.
- Only the tested static layouts are measured. Dense maps can hit a load budget even below 5 km.
- 10 km squares exceed the default sector budget. Larger maps and streaming remain unverified.
- Forest, water, passable bridge surfaces and terrain costs require separate authoritative rules.

## Regression coverage

Scale tests cover four class graphs on a 5 km open map, sparse detail storage, each load budget,
deterministic long searches, undersized-output rejection, disconnected regions and cancel/restart,
a complete safe detour, more than 1024 obstacles per sector, cache enabled/disabled, exact per-unit
goals, zero-allocation reuse, clearance-class separation, 200 early rejections with no search,
multiple components inside one sector, requested sector sizes, and budgeted smoothing with safe arrival.
Existing narrow-gap, chunk, allocation and network tests remain in the solution checks.

## Verification after review fixes

- Restore succeeds with the worktree-local `.nuget/packages` cache.
- `dotnet format DeadGround.sln --verify-no-changes --no-restore`: pass.
- Release solution build: pass, zero warnings and errors.
- Release solution tests: 229 core plus 287 rules tests, 516 total, all pass.
- Server and Godot headless smoke tests: pass.
- The expanded 5 km probe: 28/28 configurations pass. Paired cache runs have matching order hashes.
