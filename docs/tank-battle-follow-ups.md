# Tank battle follow-ups

Recorded on 2026-10-22 from the maintainer's interactive play session.
This note tracks pending tasks and explicitly marked resolutions. Keep confirmed causes separate from hypotheses.

## Context

- Branch: `feat/demo-tank-battle`.
- Worktree: `/Users/mavlevich/Documents/Codex/2026-10-02/lw/work/deadground-tank-battle`.
- The demo and its earlier changes are uncommitted. Preserve them when preparing follow-up branches.
- Local observer, two normal client replicas, one authoritative simulation, 100 T-62 vs 100 M60A1.
- Map: 3.2 x 3.2 km. Two staggered rows of 50 tanks per side. Initial front-row separation: 2500 m.
- Both sides start with a 900 m advance to formation slots, ending 700 m apart at the front if uninterrupted.
  The 2026-10-23 fix now interrupts that advance for usable observed targets.
- Continue to keep simulation and bot decisions in rules, presentation in Godot, and enemy truth out of player replicas.

## 1. Shader instance buffer exhaustion

Status on 2026-10-23: fixed for the validated 200-tank scene. The configured buffer is now
262144 slots, or 4 MiB, supporting up to 16384 instances before other consumers. The real Metal
Forward+ stress probe measured 4400 retained instances and an 8800-instance restart peak across
three restarts. The wrapper rejects headless mode, missing completion and renderer errors.
The source setting counts 16-byte slots, not bytes; each instance reserves 16 slots. The default
65536 slots therefore give 1 MiB and about 4096 instances, not 64 KiB and 1024 instances.
Further motion mesh consolidation and budgets for larger armies remain future optimization.
The original report and investigation checklist below are retained for context.

User evidence, repeated during a real rendered play session:

```text
ERROR: Too many instances using shader instance variables. Increase buffer size in Project Settings.
at: global_shader_parameters_instance_allocate (servers/rendering/renderer_rd/storage_rd/material_storage.cpp:2031)
```

The full supplied log is in:
`/Users/mavlevich/.codex/attachments/4e763bb0-85bf-4981-8b07-8dc87e7262d3/Вставленный текст.txt`.

Confirmed: the renderer cannot allocate more instance-uniform storage. The exact object count,
triggering camera distance, and whether repeated restarts make it worse are not yet measured.

Relevant code:

- `Assets/Vehicles/Shared/vehicle_weathering.gdshaderinc`: motion shaders declare `instance uniform float track_travel`.
- `UnitView.CreateRig`, `EnsureRig`, `UpdateLod`, `PushTrackTravel`: motion mesh instances receive per-instance track travel.
- LODs are loaded on demand and cached; switching hides the previous LOD. Check whether their resources
  continue to reserve uniform slots. Do not assume visibility releases them.
- Static shader variants already guard the motion uniform with `VEHICLE_MOTION`; verify the actual materials
  and generated meshes instead of assuming every static mesh declares it.

Investigation and acceptance:

1. Reproduce in the actual Godot renderer with 200 tanks. Record motion mesh counts by vehicle and LOD,
   loaded LOD counts, and active instance-uniform consumers.
2. Exercise overview, army inspection, closest zoom, repeated LOD changes, casualties and repeated restart.
3. Evaluate reducing motion mesh consumers, LOD animation cost and unused cached resources. Size any
   required buffer increase from a measured scene budget, with headroom, rather than an arbitrary large value.
4. Confirm that animation remains independent per vehicle and side, and that resource use does not grow
   without bound after restart or camera cycling.
5. Add regression coverage and an actual rendered stress check that treats these log errors as failures.
   Headless smoke alone does not establish that GPU instance allocation is healthy.

Official setting reference:
https://docs.godotengine.org/en/stable/classes/class_projectsettings.html#class-projectsettings-property-rendering-limits-global-shader-variables-buffer-size

## 2. Unnatural approach and first-fire advantage

Status on 2026-10-23: the fixed-destination halt bug is fixed in the demo. Tanks now halt when a
Current contact passes range, observed-armor penetration and muzzle LOS checks, hold through
reloads, and resume their original formation slot after one second without a usable target.
The bot receives no enemy entities or simulation reference. Normal auto-fire remains unchanged.
Regression coverage includes both sides firing before their planned end positions, unusable
contacts, blocked LOS, reloads and contact-loss hysteresis. Faction balance and coordinated fire
allocation remain separate open work. The original diagnosis below is retained for context.

User observations:

- Armies detect each other well before they fire, and keep driving for a long time.
- Soviet tanks stop and fire while American tanks are still advancing.

Confirmed causes:

- Both sensors have a 2000 m range. T-62 cannon range is 1600 m; M60A1 cannon range is 2000 m.
  Detection and weapon range are distinct, and neither alone guarantees a clear shot.
- `TankBattleBot.Update` submits a fixed 900 m advance and does not react to observed contacts.
- `CombatSystem` currently prevents auto-fire while `attacker.Moving` is true.
- Catalog off-road speeds are 8.3 m/s for T-62 and 6.9 m/s for M60A1. Ignoring navigation delays,
  900 m takes about 108 s and 130 s respectively. The scripted stopping rule creates about 22 s
  of arrival-time difference. This contributes a scenario-induced first-fire advantage.
- The latest default-seed probe ended with 85 Soviet tanks and no US tanks after 478 shots.
  That result does not isolate faction-stat balance from scenario behavior.

Desired direction, to specify before implementation:

- Keep a visible approach and the two staggered rows, but let units react to genuinely observed threats.
- Consider short halts or bounded platoon/row halts for an observed target within usable weapon range
  and LOS, followed by deliberate movement when no usable target remains. Add hysteresis to avoid
  stop/start oscillation. Do not stop merely because a distant contact exists.
- Decide explicitly between short-halt behavior and adding fire on the move. The latter changes combat
  rules and is not implicitly authorized as a demo-only fix.
- Preserve formation spacing and independent target selection. Coordinated fire allocation is a separate
  design choice; neither forced one-to-one duels nor all tanks attacking one target should be imposed accidentally.
- Bots must use only their own PlayerKnowledge and normal commands. Never read hidden enemy positions,
  armor, target IDs or movement to decide when to stop.
- Make detection, range, moving, aiming and reload states understandable through bounded diagnostics or HUD.
- Do not tune damage, armor or accuracy to compensate for the scripted arrival-time advantage.

Tests and measurements:

- Record first detection, first usable shot, halt and first fire times for both sides.
- Units may keep closing after detecting a target outside usable range, but should not continue blindly
  toward a fixed slot when the chosen engagement policy calls for firing.
- Test contact loss, blocked LOS, equal-priority targets, ammunition exhaustion and stable restart behavior.
- Re-run several deterministic seeds with swapped deployment sides and mirrored/equal-unit controls before
  attributing any residual win-rate imbalance to faction stats.

## 3. Jerky movement presentation

In progress: client clock-boundary stall defect resolved. Diagnosed and fixed in `ClientSession.UpdateRenderClock` without modifying authoritative simulation timing or adding secondary smoothing filters. The client clock controller was running with zero deadband, aggressive proportional speedup (`lag * 0.5`), and hard clamping against `clampedTarget`, forcing collisions on snapshot arrivals. In addition, frame-phase mismatch was corrected by comparing ideal target time against predicted nominal progression (`prevRenderTick + deltaTicks`). The fix establishes a deadband (`ClockDeadbandTicks = 0.25`), gentle drift correction, clamping against `Replica.LatestTick`, and waits for the initial buffer to fill before starting playback, ensuring consistent 150 ms effective delay across all framerates. On lost packets, the clock holds cleanly on buffer underflow and recovers without rewinding. Unit and integration coverage verifies clock pacing, buffer headroom, spatial motion diagnostics, and packet loss recovery in `MotionSmoothingTests.cs`. Real-view transform, full combat pacing, and hardware presentation checks remain open. The original investigation notes below are retained for context.

User reports slight repeated movement jerks instead of smooth driving. Root cause is not yet reproduced
or isolated. Do not label this as missing interpolation: the client already interpolates positions and angles.

Relevant path:

- Simulation: 20 Hz. `SimulationHost` publishes snapshots every two ticks, nominally 10 Hz.
- `TankBattleDemoSession.Advance` advances the host and both ClientSessions.
- `ClientSession.UpdateRenderClock`: three-tick interpolation buffer, clock catch-up and latest-tick clamps.
- `ClientReplica.SamplePosition` and `SampleYaws` interpolate snapshot brackets.
- `UnitViewSet.Sync` samples the replica using each session's RenderTick.
- `UnitView.UpdatePosition` and `AdvanceTracks` drive hull presentation and running gear motion.

Investigation and acceptance:

1. Fix or suppress the underlying shader allocation failure through a real fix first, then measure again.
   Error logging itself can affect frame pacing, but it is not yet proven to explain all jerks.
2. Separate whole-frame stalls from a moving hull that pauses/jumps while the camera stays smooth,
   and from wheel/track animation that appears discontinuous while the hull moves smoothly.
3. Record frame intervals, host work, snapshot arrival ticks, RenderTick, bracket endpoints and alpha,
   per-frame displayed displacement, and the selected LOD. Use preallocated bounded diagnostics.
4. Check whether clock catch-up or latest-frame clamping repeatedly exhausts interpolation headroom.
   Check fog updates, navigation work and visible effects as possible sources of frame spikes.
5. Cover 60 and 120 Hz rendering, 1x and 2x simulation, irregular frame intervals, pause/resume and restart.
6. Add a deterministic constant-speed presentation regression. Quantify unexpected holds and displacement
   spikes after warm-up, while preserving correct authoritative movement and contact visibility rules.
7. Preserve prompt stopping and correct angles. Do not hide the symptom with an arbitrary second smoothing
   filter, unbounded extrapolation or a higher simulation tick rate without identifying the cause.

## 4. Navigation scaling to 10 x 10 km

The first scaling slice now covers 5x5 km headless static geometry. See
[navigation-5km.md](navigation-5km.md) for the measurements and remaining limits.

- Replaced the old 8192-element portal arrays with actual-size storage and explicit load budgets.
- Removed full-map component cell allocation from obstacle-free sectors. Four graphs retain separate
  detail only around obstacles, with bounded local searches.
- Corrected the memory estimate to include all graphs and added heap/allocation measurements.
- Added bounded global corridor reuse to reduce repeated searches in clustered move-order bursts.
- Kept request queue latency visible; a low per-tick CPU cost is not an instant-response guarantee.

10 km remains unverified and is outside the default sector budget. Before extending the map again,
measure dense/disconnected geometry, load-time memory, long route capacities and order latency.
Do not expand the demo to 10 km by raising constants alone.

## Suggested handoff order

1. Rendering resource exhaustion: completed for 200 tanks with a real renderer stress check.
2. Approach/engagement halt behavior: completed. Broader balance diagnostics remain open.
3. Movement presentation investigation, retaining existing interpolation and simulation separation.
4. Navigation scaling as its own measured engine change.

Items 2 and 3 may share diagnostic recordings, but each confirmed bug needs its own regression.
No commit, push or agent dispatch has been performed. Later 2026-10-23 requests authorized the
demo engagement fix and renderer budget fix. Movement presentation follow-ups and scaling beyond the measured 5 km fixtures remain separate work.
