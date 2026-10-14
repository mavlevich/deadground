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
adds to the selection, and right-click orders a move. WASD or arrow keys smoothly pan the
camera with normalized diagonal speed scaled by zoom. The mouse wheel smoothly zooms, Space restores
the overview, and Escape closes open dialogs or clears unit selection. F10, O, or the HUD SETTINGS button
opens display settings (VSync toggle and FPS cap). The HUD shows connection state, tick time, allocations and traffic.
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

### Navigation scenario

To run the playable navigation scenario with server-side A* pathfinding around static obstacles:

Start the dedicated server:

```sh
dotnet run --project src/Game.Server --configuration Release -- --scenario navigation --port 7777
```

In a second terminal, start the first client (Side 1: Friendly units):

```sh
./tools/run-client.sh -- --scenario navigation --connect 127.0.0.1:7777
```

In a third terminal, start the second client (Side 2: Opponent forces):

```sh
./tools/run-client.sh -- --scenario navigation --connect 127.0.0.1:7777
```

For self-contained local inspection without a dedicated server:

```sh
./tools/run-client.sh -- --scenario navigation
```

On Windows, use `run-client.ps1`:

```powershell
./tools/run-client.ps1 -GodotArgs @("--", "--scenario", "navigation", "--connect", "127.0.0.1:7777")
```

#### Obstacles and clearance verification

The navigation scenario map includes five obstacle topologies:
- **Standalone building**: A 14 m x 14 m opaque block at (30..44, 30..44), height 6.0 m. Units smoothly navigate around both sides without clipping corners or penetrating walls.
- **Wall with bypass**: A 30 m long wall at X=58..60, Z=10..40. Ordering a move to the opposite side plans a clean detour around the wall ends.
- **L-shaped obstacle**: Two perpendicular joined walls at (75..77, 25..50) and (75..95, 48..50). Units traverse around the concave or convex corner without getting trapped or cutting corners.
- **Narrow passage**: A 2.2 m gap between X=55 and X=57.2 (Z=70..85):
  - Passable for `recon_vehicle` (radius 0.8 m, width clearance 1.6 m) and `prototype_vehicle` (radius 1.0 m, width clearance 2.0 m).
  - Blocked for `prototype_tank` (radius 1.4 m, width clearance 2.8 m). Ordering the tank across the gap routes it around the exterior perimeter rather than through the choke point.
- **Enclosed courtyard**: A 4-wall perimeter enclosing interior [92..108, 77..95]. Ordering a unit inside an unreachable enclosure fails pathfinding gracefully without stalling the tick budget or freezing the simulation.

### Large navigation scenario (kilometer-scale)

To run the playable hierarchical navigation scenario across a 2x2 km map with sector partitioning and multi-chunk routes:

Start the dedicated server:

```sh
dotnet run --project src/Game.Server --configuration Release -- --scenario navigation-large --port 7777
```

In a second terminal, start the first client (Side 1: Western forces):

```sh
./tools/run-client.sh -- --scenario navigation-large --connect 127.0.0.1:7777
```

In a third terminal, start the second client (Side 2: Eastern forces):

```sh
./tools/run-client.sh -- --scenario navigation-large --connect 127.0.0.1:7777
```

For self-contained local inspection without a dedicated server:

```sh
./tools/run-client.sh -- --scenario navigation-large
```

On Windows, use `run-client.ps1`:

```powershell
./tools/run-client.ps1 -GodotArgs @("--", "--scenario", "navigation-large", "--connect", "127.0.0.1:7777")
```

#### Large map topologies

The 2000 m x 2000 m scenario map (`navigation-large`) includes:
- **Urban block district**: 3x3 layout of 50 m x 50 m city blocks around [500..800, 500..800].
- **500 m barrier detour**: Continuous wall at X in [950..956], Z in [300..800]. Moving across the barrier plans an admissible global detour around the ends without false no-path failures.
- **Narrow 2.2 m passage**: Gap at X=1200 between buildings at Z in [500..540]. Reconnaissance vehicles fit through the gap; tanks route around the perimeter.
- **Enclosed unreachable courtyard**: 4-wall perimeter [1400..1480, 1400..1480]. Ordering a move into the interior fails gracefully without stalling the tick budget.
- **Winding slalom course**: 8 alternating barrier walls at [1600..1900, 200..700] requiring >64 waypoints. Vehicles navigate seamlessly across multiple consecutive chunks.

### Recon Raid mission (recon-raid)

To run the playable reconnaissance raid mission with authoritative objectives, enemy AI bot, and complete attempt lifecycle:

For self-contained local play with retry:

```sh
./tools/run-client.sh -- --scenario recon-raid
```

On Windows with PowerShell:

```powershell
./tools/run-client.ps1 -GodotArgs @("--", "--scenario", "recon-raid")
```

For dedicated server hosting (1 human player vs server bot):

```sh
dotnet run --project src/Game.Server --configuration Release -- --scenario recon-raid --port 7777
```

In a second terminal, connect the client:

```sh
./tools/run-client.sh -- --scenario recon-raid --connect 127.0.0.1:7777
```

#### Mission structure and rules

- **Objective**: "Locate and destroy the enemy command vehicle. Preserve your reconnaissance vehicle."
- **Forces**:
  - Player (Side 1): 1 reconnaissance vehicle (`recon_vehicle`, 70 m sensor range) and 2 combat tanks (`prototype_tank`, 100 mm gun, 10 rounds).
  - Enemy (Side 2, AI bot): 1 unarmed command vehicle (`prototype_vehicle`, 80 HP) inside a fortified compound, 1 stationary guard tank behind low cover, 1 patrol tank, and 1 patrol recon vehicle.
- **Map (512 x 512 m)**:
  - Southwest deployment for player forces.
  - Northeast compound for enemy command post.
  - Direct open route with central barriers vs northwest covered approach through ruins and village structures.
- **Victory and defeat conditions**:
  - Victory: Enemy command vehicle destroyed and player recon vehicle is alive.
  - Defeat: Player recon vehicle destroyed, all player combat tanks destroyed, or 12:00 mission time limit expired.
  - Precedence: Defeat strictly takes precedence over victory if both conditions occur simultaneously.
  - Outcome immutability: The announced result is fixed once reached and cannot be altered.
  - Cessation: Once the mission finishes, units halt, combat orders are cleared, and subsequent gameplay orders are rejected.
- **Mission HUD**:
  - Top-left briefing card displays objective, target identification status, squad roster, and mission timer.
  - Approximate target search area is shown via a 3D ground ring until the target is spotted.
  - Result card displays Victory/Defeat, specific reason, and attempt duration.
  - In local play, clicking RETRY MISSION or pressing R starts a clean restart with fresh simulation state.

### Prototype vehicle models

Own tanks and reconnaissance vehicles use distinct original GLB models. Run the mission:

```sh
./tools/run-client.sh -- --scenario recon-raid
```

The launcher builds the client and imports the assets automatically. Use the mouse wheel to
zoom in on the starting squad. Hulls face their movement direction, and tank turrets face
currently observed attack targets. Selection, health bars and existing shot effects remain active.
Enemy contacts keep their knowledge-filtered markers. Wheels and tracks are not animated yet.
The client uses 4x MSAA to smooth diagonal model edges.

The client-only `src/Game.Godot/Assets/Vehicles/visuals.json` maps content IDs to scenes and scale.
The original mesh source is reproducible without external packages:

```sh
python3 tools/model-authoring/build_vehicles.py
```

See `src/Game.Godot/Assets/Vehicles/README.md` for geometry and presentation details.

### Combat scenario and combat feedback

To run the direct-fire combat scenario with tank and reconnaissance firefights:

```sh
./tools/run-client.sh -- --scenario combat
```

To run the automated combat feedback performance probe (measuring pool limits, steady 20 shots/sec load, 100-shot saturation burst, CPU work, and 0 warmed heap allocations):

```sh
./tools/run-client.sh -- --headless --combat-feedback-probe
```

To capture the high-resolution frame-by-frame combat feedback visual sequence (neutral aim, shot/recoil, shell flight, impact sparks, destruction fireball, ground residue):

```sh
./tools/run-client.sh -- --scenario combat --combat-feedback-capture <output_directory>
```

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
dotnet run --project src/Game.Server --configuration Release --no-build -- --smoke-test --scenario navigation
dotnet run --project src/Game.Server --configuration Release --no-build -- --smoke-test --scenario navigation-large
dotnet run --project src/Game.Server --configuration Release --no-build -- --smoke-test --scenario recon-raid
./tools/run-client.sh --headless -- --smoke-test
./tools/run-client.sh --headless -- --smoke-test --scenario recon-raid
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

To run the automated performance regression probe suite (measuring 1000-unit steady-state allocations, p99 tick times, visibility under occlusion, and navigation pathfinding):

```sh
dotnet run --project tests/Benchmarks --configuration Release -- --probe
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
   - Pan using WASD or arrow keys. Confirm diagonal movement speed matches axial speed and stops instantly on key release.
   - Zoom using the mouse scroll wheel. Confirm smooth exponential zooming.
   - Press Space to return the camera to the default overview position.
7. Display settings and frame pacing:
   - Press F10, O, or click SETTINGS in the HUD to open the settings panel.
   - Toggle VSync and select frame rate limits (Unlimited, 30, 60, 120, 144 FPS).
   - Press Escape to close the settings panel before clearing unit selections.
8. Unit and contact readability:
   - Floating role badges: confirm role badges appear above units (`TANK`, `RECON`, `CMD`) and contacts (`[ ? ]`, `TANK`, `RECON`, `CMD`). Left-clicking a badge selects the unit or contact; right-clicking an enemy badge issues an attack order. Badges smoothly fade when zooming in close and hide when occluded by HUD cards or off-screen.
   - Contact representation: unknown contacts appear as neutral red diamonds without health bars; identified contacts display generic vehicle models with hostile ground brackets; last-known contacts hide live models and display frozen ghost markers with observation age.
   - Tactical selection card: bottom-left card displays unit health, armament status (distinguishing unarmed recon from empty tanks), reload countdown, and server attack execution status. Inspecting contacts shows state, classification, and age in seconds without leaking health or ammo.
   - Order notifications: persistent notifications (3.0s) show queued or rejected order results without being overwritten by selection card updates.
9. Weapon and combat feedback:
   - Firing presentation: select an armed tank and right-click an identified enemy contact. Confirm directional muzzle flash aligned with the gun barrel, localized barrel recoil into the turret mantlet returning smoothly to bind pose without displacing health bars or selection rings, and an expanding smoke puff.
   - Shell streak: observe a transient moving shell streak (160 m/s, 3.2 m length, bounded flight duration [0.08s, 0.22s]) traveling from the muzzle to target, rather than a permanent laser beam.
   - Impact feedback: hits against targets produce a brief orange impact flash, sparks rising upward, and an expanding dust/smoke cloud accompanied by distinct metallic impact audio.
   - Destruction effect: destroyed vehicles produce a distinct large spherical explosion fireball and lingering ground scorch mark clamped to the terrain surface.

## Platforms

- Windows and macOS clients
- Linux dedicated server

## Stack

- Godot 4.7.2 (.NET) for rendering, UI, audio and input
- C# / .NET 10 for simulation, networking, AI and tools
