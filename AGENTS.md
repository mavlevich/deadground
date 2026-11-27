# AGENTS.md

Shared instructions for all coding agents working in this repository.

## Project

Dead Ground (codename) is a Cold War tactical RTS. Limited forces, reconnaissance, fog of war, logistics and maneuver. Multiplayer PvP and games against a bot.

The technical design is in [docs/tdd.md](docs/tdd.md). Read it before changing architecture. If a change conflicts with it, update the TDD in the same change or ask first.

Visual style, materials, lighting, weather, terrain, and VFX rules are in [docs/art-direction.md](docs/art-direction.md). Read and verify against it before creating or modifying 3D models, shaders, maps, lighting/weather profiles, VFX, or tactical markers.

Current status is in the Status section of [README.md](README.md).

## Stack

- C# with the .NET 10 SDK. Projects target `net10.0` by default.
- If the Godot client needs the `net8.0` fallback, `Engine.Core`, `Engine.Net` and `Game.Rules.*` also target `net8.0`. Server, tools and tests may stay on `net10.0`. Follow TDD section 20; never make a `net8.0` client reference a `net10.0`-only library.
- Godot .NET (version pinned in `src/Game.Godot/Game.Godot.csproj`) for rendering, UI, audio and input.
- Clients: macOS and Windows. Dedicated server: Linux.

## Layout

Structure (TDD section 5.2):

```
src/Engine.Core/        simulation core, no Godot
src/Engine.Net/         transport, snapshots, knowledge filter
src/Game.Rules.Ops/     main rule set
src/Game.Rules.Sandbox/ test rule set
src/Game.Server/        dedicated server, console app
src/Game.Godot/         Godot client
content/coldwar/        unit, weapon and faction data (JSON)
content/prototype/      test unit data for the small test maps
maps/                   exported map data
tests/                  test and benchmark projects
tools/                  map export and other tools
docs/                   design documents
```

## Dependency rules

These are hard rules, enforced for project references, SDK use and resolved compile/runtime assemblies (see `ArchitectureBuildTests`):

- `Engine.*` never references `Game.*` or Godot.
- `Game.Rules.*` never references Godot.
- `Game.Server` never references Godot.
- Only `Game.Godot` may use `using Godot`.
- Core math uses `System.Numerics`. Conversion to Godot types happens only in `Game.Godot`.

## Code rules

- No gameplay logic in Godot nodes. Game rules live in simulation and data. UI and views only show allowed observations and send existing commands.
- New mechanics must not depend on a map name or a specific model. Drive behavior from terrain layers, unit capabilities, and data files.
- No allocations in per-tick hot paths: no LINQ, no capturing lambdas, no boxing, no new collections per tick. A change to a hot path ships with an allocation check (for example `GC.GetAllocatedBytesForCurrentThread` before and after N ticks) or a benchmark in `tests/Benchmarks`.
- Simulation changes must stay deterministic: the same seed and the same command log give the same state hash. Changes in `Engine.Core` or `Game.Rules.*` keep the determinism tests green and add a replay or hash check for new mechanics.
- Simulation time is `int Tick`. No `DateTime` and no accumulated float time in the simulation.
- No `Random.Shared`. Use the match RNG streams.
- No logic that depends on `GetHashCode` values or thread completion order.
- Clients never receive enemy `EntityId` values. Enemies are contacts. Changes to snapshots or the knowledge filter ship with a test that a client snapshot has no enemy `EntityId` and no data outside what that client may know (fog of war).
- Unit, weapon and faction types are data, not classes or enums. Content JSON is covered by validation tests: unique and stable IDs, and no broken references between units, weapons and factions.
- For asynchronous integration tests, wait for explicit conditions with bounded timeouts instead of assuming readiness after a fixed number of ticks or sleeps. Fixed tick counts are valid for deterministic simulation tests.

## Parallel work and contracts

- Use a separate feature branch and `git worktree` for each parallel task. Never share one working tree across concurrent agents.
- Declare the change scope (paths to edit) and one owner for shared files (such as `Main.cs`, `WireProtocol`, `content/coldwar/units.json`, `DeadGround.sln`, or `docs/tdd.md`) before coding. Write them in the task brief and repeat them in the PR description.
- A change to `WireProtocol` bumps the protocol version and has a test for a version mismatch (old client rejected or handled explicitly).
- In a shared repository, never run `git reset --hard`, `git clean` or `git push --force`, and never touch another agent's worktree.
- Agree on shared contracts before parallel implementation starts: commands, snapshots and observations, unit capabilities, content IDs, and model node or anchor names.
- Keep contract changes additive while other branches are active. Do not rename existing IDs or anchor nodes without coordinating with affected owners.
- Keep parallel tracks decoupled: UI work consumes observations and existing commands, simulation work stays in `Engine.*`, `Game.Rules.*`, and `content/`, and model work targets agreed asset contracts.

## Models and visual assets

- Follow [docs/art-direction.md](docs/art-direction.md) for visual style (restrained stylized realism, 1:1 metric scale, shape-driven shading, physically grounded PBR, lighting/weather calibration, and VFX/FoW clarity).
- Every 3D unit or prop model must have a reproducible generator script (for example under `tools/model-authoring/`) and automated validation checks. At minimum validate scale, forward axis, required nodes and anchors, and the triangle budget per LOD.
- Define and verify metric scale, coordinate axes and forward facing, required node hierarchy and attachment points (hull, turret, muzzle, wheels or tracks, mount points), and LOD levels.
- Record measured asset budgets (triangles per LOD, draw calls, materials, shader instance usage) and source or license details in [docs/unit-sources.md](docs/unit-sources.md) or the asset notes.

## Rendering and performance rules

- Keep steady-state frame and camera updates allocation-free: no per-frame LINQ, boxing, mesh rebuilds, or new collections in render updates.
- Partition large terrain and scene geometry into chunks with frustum culling, distance or screen-space LOD, and bounded per-frame instantiation.
- Disable shadow casting on flat surface layers (water, roads, ground pads) and keep terrain/prop shadow proxies decoupled from specific map views.
- Scale only the 3D scene buffer (`Scaling3DScale`) so 2D UI, badges, text, and minimap stay at native resolution across aspect ratios.
- Back optimizations with comparable before and after measurements. When changing rendering, geometry, or shadows, compare both windowed and fullscreen modes with identical scenario, camera zoom, resolution, display scale, and power mode within each comparison, and check visual quality.
- Report performance results in four separate parts: load or startup time, steady-state frame or tick pacing (P50, P95, P99, max), allocations, and any visual trade-offs or losses. State display scale (`1x` vs `2x` HiDPI) and run length explicitly.

## Build and test

```sh
dotnet restore DeadGround.sln
dotnet format DeadGround.sln --verify-no-changes --no-restore
dotnet build DeadGround.sln --configuration Release --no-restore
dotnet test DeadGround.sln --configuration Release --no-build
dotnet run --project src/Game.Server --configuration Release --no-build -- --smoke-test
./tools/run-client.sh --headless -- --smoke-test
```

Use `tools/run-client.ps1` on Windows. See [README.md](README.md) for prerequisites and controls.
The client smoke test requires the Godot .NET runtime. Build and xUnit checks
require only the .NET SDK and restored packages.
`dotnet format` only formats C# source files. EditorConfig defines editor rules for other file formats.
Run `git diff --check` and verify zero en dashes or em dashes before review. The repository hook in [tools/git-hooks/pre-push](tools/git-hooks/pre-push) checks C# formatting, dashes, build, and core tests.
Benchmark output (`BenchmarkDotNet.Artifacts/`) is git-ignored. Put result summaries in `docs/`, not the raw output.

## Writing style

- All code, comments, docs and commit messages are in English.
- Plain, simple English. Short sentences.
- No em dashes or en dashes. Use commas, colons or separate sentences.

## Workflow

- Work on a feature branch, never commit directly to `main` after the initial commit. Remove merged branches and temporary worktrees after landing.
- Obey user execution limits literally (for example: do not run Godot, read-only review, or do not commit or push).
- Run applicable quality checks and tests after the final edit before review. Fix C# formatting with `dotnet format DeadGround.sln`. Report only checks actually run, and list skipped checks explicitly with the reason.
- Show the diff and wait for review before committing or pushing, unless the user explicitly asks you to commit, push, or merge.
- The maintainer creates pull requests manually. Agents only prepare a PR title and description unless explicitly asked otherwise.
- Keep review-fix rounds scoped to reported defects. Do not bundle new optimizations or unrelated refactors into a fix pass.
- Reviewers do not edit. Each finding has a severity (P1, P2), a `file:line`, and a failure scenario.
- Work found outside the current scope goes into the matching `docs/*-follow-ups.md` or the PR description. Do not do it silently and do not drop it.
- A final agent report lists: changed files, checks run, checks skipped with the reason, open risks, and the diff.
- Commit messages are short. No AI attribution or co-author lines in commits or PRs.
- Refresh a branch by merging `main` into it, not by rebasing.
- Every bug found in review or testing ships with a regression test.
