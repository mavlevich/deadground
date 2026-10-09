# Dead Ground

Codename for a Cold War tactical RTS. The final title is not decided: "Dead Ground" is already used by other games on Steam and Xbox.

The game is about limited forces, reconnaissance, terrain, logistics and maneuver. No base building, no unit spam.

```
Information -> Movement -> Position -> Logistics -> Firepower
```

## Status

M0 skeleton implemented. A 128 x 128 m procedural heightmap, 12 controllable cubes,
20 Hz simulation and 10 Hz snapshots. The client reads an interpolated replica and
sends serialized commands through a bounded in-memory transport. It receives only
its own units. Four enemy units exist on the host and stay hidden.
Views follow additions and removals in complete snapshots. Movement is a separate
Arch query system selected by the rule set. M0 sessions support worlds with a
capacity up to 1024 units; larger configurations are rejected at startup.

Movement is straight-line, without collision avoidance or pathfinding. Combat,
detection, logistics, a bot and remote networking start in later milestones.
`Game.Server` is currently a console simulation runner, not a network server.
The Sandbox rule set is a movement-only placeholder for the M1 rules experiment.

The technical design and milestone exit criteria are in [docs/tdd.md](docs/tdd.md).
The macOS development client has been built and run with .NET 10. Exported clients
and Windows runtime validation are still required before closing M0.

## Run the prototype

Install .NET SDK 10.0.401 and Godot 4.7.2 **.NET**. The standard Godot build cannot
run C# projects. Package versions are pinned in the project files.

On macOS, the launcher finds `/Applications/Godot_mono.app` automatically:

```sh
./tools/run-client.sh
```

For a different installation, set `GODOT_BIN` to the executable before launching.
On Windows, use PowerShell:

```powershell
./tools/run-client.ps1 -GodotBin 'C:\Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe'
```

Alternatively, build `src/Game.Godot/Game.Godot.csproj`, then import
`src/Game.Godot/project.godot` in the Godot .NET editor and run the main scene.

Units move automatically at startup. Left-click selects a unit, Shift + left-click
adds to the selection, and right-click orders a move. WASD or arrow keys pan the
camera. The mouse wheel zooms, Space restores the overview, and Escape clears
the selection. The HUD shows tick time, managed tick allocations and snapshot traffic.
The host reports whether a valid command was queued or rejected. Rejections give
no hidden-world details. Queue admission does not guarantee arrival at the target.

Forward+ is the default renderer. It has been run on Apple Silicon using Metal.
For a Compatibility comparison, pass `--rendering-method gl_compatibility` to the
launcher. Renderer and driver must be recorded with graphics performance results.

## Build and check

```sh
dotnet build DeadGround.sln --configuration Release
dotnet test DeadGround.sln --configuration Release --no-build
dotnet run --project src/Game.Server --configuration Release --no-build -- --smoke-test
```

Run the console host without `--smoke-test` to watch it advance until Ctrl+C.
It measures elapsed monotonic time between wakeups. Each update runs at most five
ticks and retains remaining time for later updates. Console diagnostics include
the current backlog in seconds.
Run the client smoke test with `./tools/run-client.sh --headless -- --smoke-test`
on macOS, or pass the same Godot arguments to the PowerShell launcher.
The client smoke test also checks terrain triangle winding, view creation and
removal, selection cleanup and grids on both wide and tall rectangular maps.

Tests cover command validation, stable IDs, hidden enemy state, height queries,
transport bounds, corrupt and stale snapshots, interpolation, frame-rate independence,
the fixed command-log state hash and zero allocations in warmed local-session ticks.
They also cover delayed host wakeups and catch-up, snapshot capacity boundaries,
command responses and configurable picking distance. Build regression fixtures
check forbidden SDK use, direct assembly references and transitive Godot dependencies.
GitHub Actions is configured to build and test on Linux, macOS and Windows.
The solution's Release configuration maps the Godot project to ExportRelease.
This builds its managed code; it does not create an exported game package.

The movement and snapshot microbenchmark uses 100 and 1000 active units:

```sh
dotnet run --project tests/Benchmarks --configuration Release -- --filter '*MovingWorldAndSnapshot*'
```

This measures M0 movement and serialization, not the later combat workload or its budget.

## Platforms

- Windows and macOS clients
- Linux dedicated server

## Stack

- Godot 4.7.2 (.NET) for rendering, UI, audio and input
- C# / .NET 10 for simulation, networking, AI and tools
