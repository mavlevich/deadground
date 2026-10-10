# AGENTS.md

Instructions for coding agents working in this repository.

## Project

Dead Ground (codename) is a Cold War tactical RTS. Limited forces, reconnaissance, fog of war, logistics and maneuver. Multiplayer PvP and games against a bot.

The technical design is in [docs/tdd.md](docs/tdd.md). Read it before changing architecture. If a change conflicts with it, update the TDD in the same change or ask first.

Status: M1 network session implemented. Dedicated server runs two clients over LiteNetLib UDP. Reconnect recovery and native interactive Windows gameplay check remain open.

## Stack

- C# with the .NET 10 SDK. Projects target `net10.0` by default.
- If the Godot client needs the `net8.0` fallback, `Engine.Core`, `Engine.Net` and `Game.Rules.*` also target `net8.0`. Server, tools and tests may stay on `net10.0`. Follow TDD section 20; never make a `net8.0` client reference a `net10.0`-only library.
- Godot 4.7.2 (.NET build) for rendering, UI, audio and input.
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
maps/                   exported map data
tests/                  test and benchmark projects
tools/                  map export and other tools
docs/                   design documents
```

## Dependency rules

These are hard rules, enforced for project references, SDK use and resolved compile/runtime assemblies:

- `Engine.*` never references `Game.*` or Godot.
- `Game.Rules.*` never references Godot.
- `Game.Server` never references Godot.
- Only `Game.Godot` may use `using Godot`.
- Core math uses `System.Numerics`. Conversion to Godot types happens only in `Game.Godot`.

## Code rules

- No gameplay logic in Godot nodes. Views read state, they never write it.
- No allocations in per-tick hot paths: no LINQ, no capturing lambdas, no boxing, no new collections per tick.
- Simulation time is `int Tick`. No `DateTime` and no accumulated float time in the simulation.
- No `Random.Shared`. Use the match RNG streams.
- No logic that depends on `GetHashCode` values or thread completion order.
- Clients never receive enemy `EntityId` values. Enemies are contacts.
- Unit, weapon and faction types are data, not classes or enums.

## Build and test

```sh
dotnet restore DeadGround.sln
dotnet format DeadGround.sln --verify-no-changes --no-restore
dotnet build DeadGround.sln --configuration Release --no-restore
dotnet test DeadGround.sln --configuration Release --no-build
dotnet run --project src/Game.Server --configuration Release --no-build -- --smoke-test
./tools/run-client.sh --headless -- --smoke-test
```

Use `tools/run-client.ps1` on Windows. See README for prerequisites and controls.
The client smoke test requires the Godot .NET runtime. Build and xUnit checks
require only the .NET SDK and restored packages.

## Writing style

- All code, comments, docs and commit messages are in English.
- Plain, simple English. Short sentences.
- No em dashes or en dashes. Use commas, colons or separate sentences.

## Workflow

- Work on a feature branch, never commit directly to `main` after the initial commit.
- Run code quality checks and tests before submitting changes for review. Fix formatting with `dotnet format DeadGround.sln`.
- EditorConfig defines editor rules for multiple file formats, but `dotnet format` only formats C# source files.
- Show the diff and wait for review before committing or pushing.
- The maintainer creates pull requests manually. Agents only prepare a PR title and description.
- Commit messages are short. No AI attribution or co-author lines in commits or PRs.
- Refresh a branch by merging `main` into it, not by rebasing.
- Every bug found in review or testing ships with a regression test.
