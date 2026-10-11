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
detection, logistics, a bot and save/load start in later milestones.
`Game.Server` runs an authoritative dedicated server over LiteNetLib UDP.
The Sandbox rule set is a movement-only placeholder for the M1 rules experiment.
Reconnect recovery is pending M1 work.

The technical design and milestone exit criteria are in [docs/tdd.md](docs/tdd.md).
The hosted three-platform CI run on main (Ubuntu, macOS, Windows; run 37940079650)
succeeded, verifying builds, unit tests, the server smoke test and launcher checks.
The hosted export CI run (run 38075713603) succeeded, validating reproducible client
exports for macOS Universal and Windows x64 along with headless export smoke tests.
Native interactive Windows gameplay remains unverified.

## Run the prototype

Install .NET SDK 10.0.401 and Godot 4.7.2 **.NET**. The standard Godot build cannot
run C# projects. Package versions are pinned in the project files.

### Local play

To run a self-contained local session with an in-memory simulation:

On macOS:

```sh
./tools/run-client.sh
```

On Windows:

```powershell
./tools/run-client.ps1 -GodotBin 'C:\Tools\Godot\Godot_v4.7.2-stable_mono_win64.exe'
```

### Dedicated server and two clients

To run one dedicated server and two network clients:

Start the dedicated server on UDP port 7777:

```sh
dotnet run --project src/Game.Server --configuration Release -- --port 7777
```

In a second terminal, start the first client:

```sh
./tools/run-client.sh -- --connect 127.0.0.1:7777
```

In a third terminal, start the second client:

```sh
./tools/run-client.sh -- --connect 127.0.0.1:7777
```

On Windows with PowerShell:

```powershell
./tools/run-client.ps1 -GodotArgs @("--", "--connect", "127.0.0.1:7777")
```

The server assigns player slots automatically. Client 1 controls friendly units,
while Client 2 controls its own units. Each client renders only units allowed by
its fog of war filter.

Alternatively, build `src/Game.Godot/Game.Godot.csproj`, then import
`src/Game.Godot/project.godot` in the Godot .NET editor and run the main scene.

Units move automatically in local demo mode. Left-click selects a unit, Shift + left-click
adds to the selection, and right-click orders a move. WASD or arrow keys pan the
camera. The mouse wheel zooms, Space restores the overview, and Escape clears
the selection. The HUD shows connection state, tick time, allocations and traffic.
The host reports whether a valid command was queued or rejected. Rejections give
no hidden-world details. Queue admission does not guarantee arrival at the target.

### Visual reconnaissance scenario

To run the playable visual reconnaissance scenario with server-side line-of-sight detection and replicated enemy contacts:

Start the dedicated server:

```sh
dotnet run --project src/Game.Server --configuration Release -- --scenario visibility --port 7777
```

In a second terminal, start the first client (Side 1: Recon observers):

```sh
./tools/run-client.sh -- --scenario visibility --connect 127.0.0.1:7777
```

In a third terminal, start the second client (Side 2: Opponent forces):

```sh
./tools/run-client.sh -- --scenario visibility --connect 127.0.0.1:7777
```

For self-contained local inspection without a dedicated server:

```sh
./tools/run-client.sh -- --scenario visibility
```

#### Reconnaissance controls and presentation

- **Own units**:
  - `recon_vehicle`: Olive chassis with a tall surveillance antenna.
  - `prototype_tank`: Olive chassis with a turret and main gun barrel.
- **Enemy contacts**:
  - `Current`: Vibrant red diamond marker with a vertical ground indicator stem, updating with live enemy position.
  - `LastKnown`: Muted translucent red diamond marker with a base ring on the ground, fixed at the position where line of sight was lost.
- **Selection**:
  - Left-click selects an own unit or an enemy contact marker.
  - Selecting an enemy contact shows its contact ID and observation state in the HUD.
  - Enemy contacts cannot be commanded to move; move orders are only accepted for owned units.
- **Scenario lifecycle walkthrough**:
  1. **Initial detection**: Side 1 has two recon vehicles at (20, 25) and (20, 45). Side 2 has a tank at (33, 27) behind a 1.0 m low wall, a second tank at (60, 45) behind a 6.0 m opaque building, and a recon vehicle at (95, 95) behind an 8.0 m hill. Observer 1 detects Tank 1 over the low wall (hull is covered, but upper observation point is clear). Client 1 sees a `Current` red diamond marker.
  2. **Occlusion and track loss**: Select Tank 1 in Client 2 and right-click to move behind the 6.0 m building. As soon as line of sight is broken, Client 1's marker freezes and transitions to a muted `LastKnown` ghost marker.
  3. **Rediscovery and track correlation**: If Tank 1 moves back into view within the correlation window (60 ticks / 3.0 s) and distance gate (25 m), the existing contact track is resumed under the same Contact ID without flicker.
  4. **Contact expiration**: If Tank 1 remains hidden for longer than the memory duration (100 ticks / 5.0 s), the `LastKnown` marker expires and disappears from Client 1's view.

### Line-of-sight sandbox

To launch the standalone visual line-of-sight geometry sandbox:

On macOS:

```sh
./tools/run-client.sh -- --los-sandbox
```

On Windows:

```powershell
./tools/run-client.ps1 -GodotArgs @("--", "--los-sandbox")
```

The sandbox visualizes 3D line-of-sight rays against bilinear terrain and opaque structures:
- Green rays indicate clear line of sight.
- Red rays indicate obstructed line of sight with hit markers at intersection points.
- Keys 1 to 6 select reproducible scenarios: open field, hill occlusion, building occlusion, corner peek, low wall hull-down, and elevated vantage.
- Left bracket `[` and right bracket `]` cycle presets.
- WASD or arrow keys pan the camera. The mouse scroll wheel zooms. Space resets the camera.

To run the automated headless regression test for all sandbox scenarios:

```sh
./tools/run-client.sh --headless -- --los-sandbox --smoke-test
```

Forward+ is the default renderer. It has been run on Apple Silicon using Metal.
For a Compatibility comparison, pass `--rendering-method gl_compatibility` to the
launcher. Renderer and driver must be recorded with graphics performance results.

## Build and check

Run the verification pipeline in order: restore, code quality check, build, and tests:

```sh
dotnet restore DeadGround.sln
dotnet format DeadGround.sln --verify-no-changes --no-restore
dotnet build DeadGround.sln --configuration Release --no-restore
dotnet test DeadGround.sln --configuration Release --no-build
dotnet run --project src/Game.Server --configuration Release --no-build -- --smoke-test
dotnet run --project src/Game.Server --configuration Release --no-build -- --smoke-test --scenario visibility
./tools/run-client.sh --headless -- --smoke-test
```

To automatically format C# code according to the repository rules:

```sh
dotnet format DeadGround.sln
```

EditorConfig sets editor conventions for various file formats. Note that `dotnet format` only formats C# source files, not JSON, YAML, or shell scripts.

Built-in .NET SDK analyzers are enabled in `Directory.Build.props` and configured via `.editorconfig`. They enforce correctness, reliability, resource disposal, and performance best practices. Generated code and build outputs (`bin`, `obj`, `.godot`, `build`, `export`, `TestResults`, and benchmark artifacts) are excluded from analysis and formatting via `generated_code = true` in `.editorconfig`.

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

The line-of-sight microbenchmark measures single-query and batch geometric queries:

```sh
dotnet run --project tests/Benchmarks --configuration Release -- --filter '*LineOfSight*'
```

## Export the client

Godot export templates for version 4.7.2 .NET are required. macOS exports use the
native `codesign` tool for ad-hoc signing and must be created on macOS. Install them once:

```sh
./tools/install-templates.sh
```

On Windows:

```powershell
./tools/install-templates.ps1
```

To export the macOS client (Universal package covering Apple Silicon and Intel):

```sh
./tools/export-client.sh macOS
```

The output package is created at `build/export/macos/DeadGround.zip`.

To export the Windows client (x64 package):

```sh
./tools/export-client.sh "Windows Desktop"
```

On Windows with PowerShell:

```powershell
./tools/export-client.ps1 -Preset "Windows Desktop"
```

The output files are written to `build/export/windows/DeadGround.exe`.

To run the automated smoke test against the exported macOS client:

```sh
mkdir -p build/export/macos/extracted
unzip -q build/export/macos/DeadGround.zip -d build/export/macos/extracted
"./build/export/macos/extracted/Dead Ground.app/Contents/MacOS/Dead Ground" --headless -- --smoke-test
```

On Windows:

```powershell
& "build\export\windows\DeadGround.console.exe" --headless -- --smoke-test
```

## Manual verification checklist

Follow this checklist to verify an exported client interactively:

1. Launch: Start the exported application (`open "Dead Ground.app"` on macOS, or `DeadGround.exe` on Windows).
2. World rendering: Confirm that the window opens, displaying the 128 x 128 m terrain, green grid overlay, directional lighting and HUD panels.
3. Unit spawning: Confirm that 12 friendly units appear on the map. Verify that all 12 units automatically begin moving north (+60 m).
4. Selection:
   - Left-click any unit. Confirm that a yellow selection ring appears, and the status bar displays "1 selected / right-click terrain to move".
   - Shift + left-click another unit to add it to the selection.
   - Left-click empty terrain to deselect. Confirm status displays "No units selected".
   - Press Escape to clear active selections.
5. Movement orders:
   - Select one or more units.
   - Right-click terrain. Confirm that a yellow target ring appears at the click position.
   - Confirm status updates to "Move order sent / N units / X, Z m".
   - Verify that selected units turn and travel toward the target point.
6. Camera:
   - Pan using WASD or arrow keys.
   - Zoom using the mouse scroll wheel.
   - Press Space to return the camera to the default overview position.

## Platforms

- Windows and macOS clients
- Linux dedicated server

## Stack

- Godot 4.7.2 (.NET) for rendering, UI, audio and input
- C# / .NET 10 for simulation, networking, AI and tools
