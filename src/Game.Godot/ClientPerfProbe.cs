using System.Diagnostics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

/// <summary>
/// Automated performance diagnostic probe for client loading times,
/// resource prewarming, frame pacing across gameplay phases and Fog of War costs.
/// </summary>
internal static class ClientPerfProbe
{
    private readonly record struct ModelTimings(double LoadMs, double InstantiateMs, double AddMs)
    {
        public double TotalMs => LoadMs + InstantiateMs + AddMs;
    }

    public static Task<bool> RunMicrobenchmark(Node3D scene)
    {
        GD.Print("================================================================");
        GD.Print("        DEAD GROUND VEHICLE MODEL LIFECYCLE BENCHMARK           ");
        GD.Print("================================================================");

        try
        {
            ReportEnvironment();
            VehicleVisualCatalog.ClearCache();
            RunModelLifecycleMicrobenchmark(scene);

            GD.Print("================================================================");
            GD.Print("              MICROBENCHMARK COMPLETED OK                       ");
            GD.Print("================================================================");

            scene.GetTree().Quit(0);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            GD.PushError("CLIENT_PERF_MICROBENCH_ERROR: " + ex);
            scene.GetTree().Quit(1);
            return Task.FromResult(false);
        }
    }

    public static async Task<bool> Run(Main main, bool noPrewarm)
    {
        GD.Print("================================================================");
        GD.Print("         DEAD GROUND CLIENT PERFORMANCE & STUTTER PROBE         ");
        GD.Print("================================================================");

        try
        {
            // 1. Hardware and environment report
            ReportEnvironment();

            if (noPrewarm)
            {
                VehicleVisualCatalog.ClearCache();
                GD.Print(">>> MODE: Cold Baseline (--no-prewarm). Visual cache is empty.");
            }
            else
            {
                GD.Print(">>> MODE: Prewarmed. Visual catalog prewarmed during startup.");
            }

            // 2. In-engine live gameplay frame pacing phases on Ustye map
            await RunLiveScenarioProbe(main, noPrewarm);

            // 3. Fog of war direct CPU calculation cost comparison
            await RunFogOfWarCpuCostProbe(main);

            // 4. Scenario startup phases breakdown (executed after probe to avoid polluting cold cache)
            RunStartupBreakdown();

            // 5. Navigation grid recreation cost on retry
            RunNavigationRebuildBenchmark();

            GD.Print("================================================================");
            GD.Print("                  PERF PROBE COMPLETED OK                      ");
            GD.Print("================================================================");

            main.GetTree().Quit(0);
            return true;
        }
        catch (Exception ex)
        {
            GD.PushError("CLIENT_PERF_PROBE_ERROR: " + ex);
            main.GetTree().Quit(1);
            return false;
        }
    }

    private static void ReportEnvironment()
    {
        GD.Print("--- ENVIRONMENT CONFIGURATION ---");
        GD.Print($"OS: {OS.GetName()} {OS.GetVersion()} ({OS.GetProcessorName()})");
        GD.Print($"Godot Engine: {Godot.Engine.GetVersionInfo()["string"]}");
        GD.Print($"Display Server: {DisplayServer.GetName()}");
        GD.Print($"Rendering Method: {ProjectSettings.GetSetting("rendering/renderer/rendering_method")}");
        GD.Print($"Vulkan / Driver: {RenderingServer.GetVideoAdapterName()} (API {RenderingServer.GetVideoAdapterApiVersion()})");
        GD.Print($"Viewport Resolution: {ProjectSettings.GetSetting("display/window/size/viewport_width")}x{ProjectSettings.GetSetting("display/window/size/viewport_height")}");
        GD.Print($"Override Resolution: {ProjectSettings.GetSetting("display/window/size/window_width_override")}x{ProjectSettings.GetSetting("display/window/size/window_height_override")}");
        GD.Print($"VSync Mode: {DisplayServer.WindowGetVsyncMode()}");
        GD.Print($"Max FPS Limit: {Godot.Engine.MaxFps}");
        GD.Print($".NET SDK Runtime: {System.Environment.Version}");
        GD.Print("---------------------------------");
    }

    private static void RunStartupBreakdown()
    {
        GD.Print("\n--- STARTUP PHASES BREAKDOWN (ustye-raid, 5000x5000m) ---");

        Stopwatch sw = new();

        // Phase A: Static geometry
        sw.Restart();
        UstyePlanData plan = UstyePlanData.Load();
        StaticGeometry geometry = UstyeRaidScenario.CreateStaticGeometry(plan);
        sw.Stop();
        double geomMs = sw.Elapsed.TotalMilliseconds;
        GD.Print($"1. Static Geometry Creation: {geomMs:F2} ms (Structures: {geometry.Structures.Count})");

        // Phase B: Navigation Grid
        sw.Restart();
        NavigationTuning navTuning = UstyeGroundScenario.CreateNavigationTuning();
        NavigationBuildLimits buildLimits = UstyeGroundScenario.CreateNavigationBuildLimits();
        NavigationGrid navGrid = new(
            geometry,
            navTuning.CellSize,
            enableHierarchical: true,
            buildLimits: buildLimits,
            sectorSize: navTuning.SectorSize);
        sw.Stop();
        double navMs = sw.Elapsed.TotalMilliseconds;
        GD.Print($"2. Navigation Grid Rasterization: {navMs:F2} ms (Cells: {navGrid.Columns}x{navGrid.Rows} = {navGrid.Columns * navGrid.Rows})");

        // Phase C: Simulation Creation
        sw.Restart();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        MatchSetup setup = UstyeRaidScenario.CreateMatchSetup(geometry, catalog: catalog);
        Simulation sim = UstyeRaidScenario.CreateSimulation(geometry, catalog: catalog, navGrid: navGrid);
        sw.Stop();
        double simMs = sw.Elapsed.TotalMilliseconds;
        GD.Print($"3. Simulation & Roster Init: {simMs:F2} ms (Entities: {sim.World.Count})");

        // Phase D: Vehicle Visual Catalog Prewarm
        VehicleVisualCatalog.ClearCache();
        long memBefore = GC.GetTotalMemory(true);
        sw.Restart();
        VehicleVisualCatalog.PrewarmResult prewarmRes = VehicleVisualCatalog.PrewarmScenario(setup, catalog);
        sw.Stop();
        long memAfter = GC.GetTotalMemory(false);
        long memDeltaBytes = Math.Max(0, memAfter - memBefore);
        GD.Print($"4. Visual Resource Prewarm: {prewarmRes.ElapsedMs:F2} ms (Scenes: {prewarmRes.TotalPaths}, Newly Loaded: {prewarmRes.NewlyLoadedCount}, Managed Mem Delta: {memDeltaBytes / 1024.0:F1} KB)");

        // Phase E: Repeat Prewarm (idempotency check)
        sw.Restart();
        VehicleVisualCatalog.PrewarmResult repeatRes = VehicleVisualCatalog.PrewarmScenario(setup, catalog);
        sw.Stop();
        GD.Print($"5. Visual Resource Repeat Prewarm: {repeatRes.ElapsedMs:F2} ms (Newly Loaded: {repeatRes.NewlyLoadedCount} - verified 0 duplicate loads)");

        double totalMs = geomMs + navMs + simMs + prewarmRes.ElapsedMs;
        GD.Print($"Total Engine Setup Time: {totalMs:F2} ms");
        GD.Print("---------------------------------------------------------");
    }

    private static void RunModelLifecycleMicrobenchmark(Node3D scene)
    {
        GD.Print("\n--- VEHICLE MODEL LIFECYCLE MICROBENCHMARK (Cold vs Warm) ---");

        string[] testModels =
        [
            "res://Assets/Vehicles/M60/m60.glb",
            "res://Assets/Vehicles/M60/m60_lod1.glb",
            "res://Assets/Vehicles/M60/m60_lod2.glb",
            "res://Assets/Vehicles/T62Alt/t62alt.glb",
            "res://Assets/Vehicles/T62Alt/t62alt_lod1.glb",
            "res://Assets/Vehicles/T62Alt/t62alt_lod2.glb",
            "res://Assets/Vehicles/BRDM2/brdm2.glb",
            "res://Assets/Vehicles/BRDM2/brdm2_lod1.glb",
            "res://Assets/Vehicles/BRDM2/brdm2_lod2.glb",
            "res://Assets/Vehicles/BTR70/btr70.glb",
            "res://Assets/Vehicles/recon.glb",
            "res://Assets/Vehicles/tank.glb"
        ];

        Node3D container = new();
        scene.AddChild(container);

        try
        {
            Stopwatch sw = new();
            Span<ModelTimings> coldTimings = stackalloc ModelTimings[testModels.Length];
            Span<ModelTimings> warmTimings = stackalloc ModelTimings[testModels.Length];

            // Pass 1: Cold load (first time disk read, GLB parse and scene instantiation in new process)
            for (int i = 0; i < testModels.Length; i++)
            {
                string path = testModels[i];

                sw.Restart();
                PackedScene packed = GD.Load<PackedScene>(path);
                sw.Stop();
                double loadMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                Node3D instance = packed.Instantiate<Node3D>();
                sw.Stop();
                double instantiateMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                container.AddChild(instance);
                sw.Stop();
                double addMs = sw.Elapsed.TotalMilliseconds;

                container.RemoveChild(instance);
                instance.Free();

                coldTimings[i] = new ModelTimings(loadMs, instantiateMs, addMs);
            }

            // Pass 2: Warm load (repeat fetch from engine resource cache)
            for (int i = 0; i < testModels.Length; i++)
            {
                string path = testModels[i];

                sw.Restart();
                PackedScene packed = GD.Load<PackedScene>(path);
                sw.Stop();
                double loadMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                Node3D instance = packed.Instantiate<Node3D>();
                sw.Stop();
                double instantiateMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                container.AddChild(instance);
                sw.Stop();
                double addMs = sw.Elapsed.TotalMilliseconds;

                container.RemoveChild(instance);
                instance.Free();

                warmTimings[i] = new ModelTimings(loadMs, instantiateMs, addMs);
            }

            GD.Print($"{"Model",-18} | {"Cold Load",9} | {"Cold Inst",9} | {"Cold Total",10} | {"Warm Load",9} | {"Warm Inst",9} | {"Warm Total",10}");
            GD.Print(new string('-', 86));

            for (int i = 0; i < testModels.Length; i++)
            {
                string shortName = testModels[i].Substring(testModels[i].LastIndexOf('/') + 1);
                ModelTimings c = coldTimings[i];
                ModelTimings w = warmTimings[i];
                GD.Print($"{shortName,-18} | {c.LoadMs,7:F2} ms | {c.InstantiateMs,7:F2} ms | {c.TotalMs,8:F2} ms | {w.LoadMs,7:F2} ms | {w.InstantiateMs,7:F2} ms | {w.TotalMs,8:F2} ms");
            }
            GD.Print(new string('-', 86));
        }
        finally
        {
            container.Free();
        }
    }

    private static async Task RunLiveScenarioProbe(Main main, bool noPrewarm)
    {
        GD.Print("\n--- LIVE GAMEPLAY FRAME PACING PHASES (ustye-raid) ---");

        IGameSession session = main.Session;
        UnitCatalog catalog = main.Catalog;

        // Ensure Soviet squad is selected
        main.UnitViews.Selected.Clear();
        foreach (OwnUnitObservation u in session.Replica.OwnUnits)
        {
            main.UnitViews.Selected.Add(u.Id);
        }

        const float dt = 0.016667f; // ~60 Hz simulation pacing
        double peakSimTickMs = 0.0;

        // Helper to advance simulation and track simulation tick duration
        void AdvanceSimulation()
        {
            session.Advance(dt);
            double tickMs = session.LastTickMilliseconds;
            if (tickMs > peakSimTickMs)
            {
                peakSimTickMs = tickMs;
            }
        }

        // ----------------------------------------------------
        // Phase 1: Stationary Squad after loading (60 frames)
        // ----------------------------------------------------
        FramePacingMetrics stationaryMetrics = new(100);
        for (int f = 0; f < 60; f++)
        {
            long startNs = Stopwatch.GetTimestamp();
            AdvanceSimulation();
            main.FogOfWar?.Update(dt, session.Replica, catalog);
            main.UpdateViews();
            main.UnitViews.UpdateLod(main.CameraRig.Size);
            main.CombatEffects.Update(dt);

            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            long endNs = Stopwatch.GetTimestamp();
            float frameMs = (float)((endNs - startNs) * 1000.0 / Stopwatch.Frequency);
            stationaryMetrics.Record(frameMs);
        }
        FramePacingMetrics.PercentileResults resStationary = stationaryMetrics.CalculateResults();
        GD.Print($"Phase 1 (Stationary Squad 60f):   p50={resStationary.P50Ms:F2} ms | p95={resStationary.P95Ms:F2} ms | p99={resStationary.P99Ms:F2} ms | max={resStationary.MaxMs:F2} ms");

        // ----------------------------------------------------
        // Phase 2: Start of Movement (50 frames)
        // ----------------------------------------------------
        // Order player units to move towards riverbank opening (X=2200, Z=2400)
        NumericsVector2 moveTarget = new(2200f, 2400f);
        foreach (OwnUnitObservation u in session.Replica.OwnUnits)
        {
            session.Move(u.Id, moveTarget);
        }

        FramePacingMetrics moveMetrics = new(100);
        for (int f = 0; f < 50; f++)
        {
            long startNs = Stopwatch.GetTimestamp();
            AdvanceSimulation();
            main.FogOfWar?.Update(dt, session.Replica, catalog);
            main.UpdateViews();
            main.UnitViews.UpdateLod(main.CameraRig.Size);
            main.CombatEffects.Update(dt);

            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            long endNs = Stopwatch.GetTimestamp();
            float frameMs = (float)((endNs - startNs) * 1000.0 / Stopwatch.Frequency);
            moveMetrics.Record(frameMs);
        }
        FramePacingMetrics.PercentileResults resMove = moveMetrics.CalculateResults();
        GD.Print($"Phase 2 (Start of Movement 50f): p50={resMove.P50Ms:F2} ms | p95={resMove.P95Ms:F2} ms | p99={resMove.P99Ms:F2} ms | max={resMove.MaxMs:F2} ms");

        // ----------------------------------------------------
        // Phase 3: First Detection of Enemy Vehicle (M60A1)
        // ----------------------------------------------------
        // BRDM-2 reaches open riverbank and sights Guard Tank M60A1 across the river.
        bool m60LoadedBefore = VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/M60/m60.glb");

        bool contactSpotted = false;
        float firstContactFrameMs = 0f;
        int searchFrames = 0;
        const int maxSearchFrames = 300;

        while (!contactSpotted && searchFrames++ < maxSearchFrames)
        {
            long startNs = Stopwatch.GetTimestamp();
            AdvanceSimulation();
            main.FogOfWar?.Update(dt, session.Replica, catalog);
            main.UpdateViews();
            main.UnitViews.UpdateLod(main.CameraRig.Size);
            main.CombatEffects.Update(dt);

            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            long endNs = Stopwatch.GetTimestamp();
            float frameMs = (float)((endNs - startNs) * 1000.0 / Stopwatch.Frequency);

            if (session.Replica.Contacts.Length > 0 && !contactSpotted)
            {
                contactSpotted = true;
                firstContactFrameMs = frameMs;
                break;
            }
        }

        if (!contactSpotted)
        {
            throw new InvalidOperationException(
                $"Phase 3 failed: enemy contact was not sighted within {maxSearchFrames} frames (contacts: {session.Replica.Contacts.Length}).");
        }

        bool m60LoadedAfter = VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/M60/m60.glb");
        GD.Print($"Phase 3 Pre-check: M60 in cache before sighting = {m60LoadedBefore}");
        GD.Print($"Phase 3 Post-check: M60 in cache after sighting = {m60LoadedAfter} (sighted at frame {searchFrames})");
        GD.Print($"Phase 3 Initial Event: First Contact Sighting Frame = {firstContactFrameMs:F2} ms");

        // Subsequent 30 frames with the contact active in view
        FramePacingMetrics contactSubsequentMetrics = new(50);
        for (int f = 0; f < 30; f++)
        {
            long startNs = Stopwatch.GetTimestamp();
            AdvanceSimulation();
            main.FogOfWar?.Update(dt, session.Replica, catalog);
            main.UpdateViews();
            main.UnitViews.UpdateLod(main.CameraRig.Size);
            main.CombatEffects.Update(dt);

            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            long endNs = Stopwatch.GetTimestamp();
            float frameMs = (float)((endNs - startNs) * 1000.0 / Stopwatch.Frequency);
            contactSubsequentMetrics.Record(frameMs);
        }
        FramePacingMetrics.PercentileResults resContact = contactSubsequentMetrics.CalculateResults();
        GD.Print($"Phase 3 Subsequent (30f sustained): p50={resContact.P50Ms:F2} ms | p95={resContact.P95Ms:F2} ms | p99={resContact.P99Ms:F2} ms | max={resContact.MaxMs:F2} ms");

        // ----------------------------------------------------
        // Phase 4: Repeat Appearance / Sustained Contact (30 frames)
        // ----------------------------------------------------
        main.ContactViews.Clear();
        await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);

        // Frame 1: Measure the re-sync frame inside timer
        long repeatStartNs = Stopwatch.GetTimestamp();
        main.ContactViews.Sync(session.Replica, session.RenderTick);
        AdvanceSimulation();
        main.FogOfWar?.Update(dt, session.Replica, catalog);
        main.UpdateViews();
        main.UnitViews.UpdateLod(main.CameraRig.Size);
        main.CombatEffects.Update(dt);
        await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
        long repeatEndNs = Stopwatch.GetTimestamp();
        float firstRepeatFrameMs = (float)((repeatEndNs - repeatStartNs) * 1000.0 / Stopwatch.Frequency);
        GD.Print($"Phase 4 Initial Event: First Re-sync Frame = {firstRepeatFrameMs:F2} ms");

        // Subsequent 30 frames of sustained repeat contact
        FramePacingMetrics repeatSubsequentMetrics = new(50);
        for (int f = 0; f < 30; f++)
        {
            long startNs = Stopwatch.GetTimestamp();
            AdvanceSimulation();
            main.FogOfWar?.Update(dt, session.Replica, catalog);
            main.UpdateViews();
            main.UnitViews.UpdateLod(main.CameraRig.Size);
            main.CombatEffects.Update(dt);

            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            long endNs = Stopwatch.GetTimestamp();
            float frameMs = (float)((endNs - startNs) * 1000.0 / Stopwatch.Frequency);
            repeatSubsequentMetrics.Record(frameMs);
        }
        FramePacingMetrics.PercentileResults resRepeat = repeatSubsequentMetrics.CalculateResults();
        GD.Print($"Phase 4 Subsequent (30f sustained): p50={resRepeat.P50Ms:F2} ms | p95={resRepeat.P95Ms:F2} ms | p99={resRepeat.P99Ms:F2} ms | max={resRepeat.MaxMs:F2} ms");

        // ----------------------------------------------------
        // Phase 5: First LOD Transition (Camera zoom) (30 frames)
        // ----------------------------------------------------
        // Frame 1: Measure camera zoom and initial LOD switch (LOD0 -> LOD1) inside timer
        long lodStartNs = Stopwatch.GetTimestamp();
        main.CameraRig.SetDefaultView(main.CameraRig.Focus, 60f);
        main.CameraRig.ResetToDefault();
        main.UnitViews.UpdateLod(main.CameraRig.Size);
        AdvanceSimulation();
        main.FogOfWar?.Update(dt, session.Replica, catalog);
        main.UpdateViews();
        main.UnitViews.UpdateLod(main.CameraRig.Size);
        main.CombatEffects.Update(dt);
        await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
        long lodEndNs = Stopwatch.GetTimestamp();
        float firstLodSwitchFrameMs = (float)((lodEndNs - lodStartNs) * 1000.0 / Stopwatch.Frequency);
        GD.Print($"Phase 5 Initial Event: First LOD Switch Frame = {firstLodSwitchFrameMs:F2} ms");

        // Subsequent 30 frames at new LOD
        FramePacingMetrics lodSubsequentMetrics = new(50);
        for (int f = 0; f < 30; f++)
        {
            long startNs = Stopwatch.GetTimestamp();
            AdvanceSimulation();
            main.FogOfWar?.Update(dt, session.Replica, catalog);
            main.UpdateViews();
            main.UnitViews.UpdateLod(main.CameraRig.Size);
            main.CombatEffects.Update(dt);

            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            long endNs = Stopwatch.GetTimestamp();
            float frameMs = (float)((endNs - startNs) * 1000.0 / Stopwatch.Frequency);
            lodSubsequentMetrics.Record(frameMs);
        }
        FramePacingMetrics.PercentileResults resLod = lodSubsequentMetrics.CalculateResults();
        GD.Print($"Phase 5 Subsequent (30f at LOD1):   p50={resLod.P50Ms:F2} ms | p95={resLod.P95Ms:F2} ms | p99={resLod.P99Ms:F2} ms | max={resLod.MaxMs:F2} ms");

        // ----------------------------------------------------
        // Phase 6: First Shots and Combat Effects (30 frames)
        // ----------------------------------------------------
        EntityId tankShooter = session.Replica.OwnUnits[1].Id;
        NumericsVector3 shotOrigin = new(2200f, 2.0f, 2400f);
        NumericsVector3 shotTarget = new(3330f, 1.5f, 2690f);

        CombatEventObservation shotEvent = new(
            EventId: 9901,
            ServerTick: session.Tick,
            Kind: CombatEventKind.ShotFired,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            Origin: shotOrigin,
            Target: shotTarget,
            ShooterUnitId: tankShooter);

        CombatEventObservation impactEvent = new(
            EventId: 9902,
            ServerTick: session.Tick + 1,
            Kind: CombatEventKind.UnitDestroyed,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            Origin: default,
            Target: shotTarget,
            ShooterUnitId: default);

        // Frame 1: Measure combat event processing, particle instantiation and explosion spawning inside timer
        long combatStartNs = Stopwatch.GetTimestamp();
        main.CombatEffects.ProcessEvents([shotEvent, impactEvent], main.UnitViews, session.Tick, main.ContactViews);
        AdvanceSimulation();
        main.FogOfWar?.Update(dt, session.Replica, catalog);
        main.UpdateViews();
        main.UnitViews.UpdateLod(main.CameraRig.Size);
        main.CombatEffects.Update(dt);
        await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
        long combatEndNs = Stopwatch.GetTimestamp();
        float firstCombatFrameMs = (float)((combatEndNs - combatStartNs) * 1000.0 / Stopwatch.Frequency);
        GD.Print($"Phase 6 Initial Event: First Combat Effects Frame = {firstCombatFrameMs:F2} ms");

        // Subsequent 30 frames with particle animations and effects active
        FramePacingMetrics combatSubsequentMetrics = new(50);
        for (int f = 0; f < 30; f++)
        {
            long startNs = Stopwatch.GetTimestamp();
            AdvanceSimulation();
            main.FogOfWar?.Update(dt, session.Replica, catalog);
            main.UpdateViews();
            main.UnitViews.UpdateLod(main.CameraRig.Size);
            main.CombatEffects.Update(dt);

            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            long endNs = Stopwatch.GetTimestamp();
            float frameMs = (float)((endNs - startNs) * 1000.0 / Stopwatch.Frequency);
            combatSubsequentMetrics.Record(frameMs);
        }
        FramePacingMetrics.PercentileResults resCombat = combatSubsequentMetrics.CalculateResults();
        GD.Print($"Phase 6 Subsequent (30f effects):  p50={resCombat.P50Ms:F2} ms | p95={resCombat.P95Ms:F2} ms | p99={resCombat.P99Ms:F2} ms | max={resCombat.MaxMs:F2} ms");

        // Simulation Tick Analysis
        GD.Print($"Simulation Tick Analysis: Peak tick duration during movement/sighting = {peakSimTickMs:F2} ms (Pathfinding & local planning executed on local session thread)");

        // Initial Event Frame Timings Table
        GD.Print("\n--- INITIAL EVENT FRAME TIMINGS ---");
        GD.Print($"{"Event",-46} | {"Frame Time (ms)",16}");
        GD.Print(new string('-', 65));
        GD.Print($"{"Phase 3 First Contact Sighting (M60A1)",-46} | {firstContactFrameMs,13:F2} ms");
        GD.Print($"{"Phase 4 First Contact Re-sync",-46} | {firstRepeatFrameMs,13:F2} ms");
        GD.Print($"{"Phase 5 First LOD Switch (LOD0 -> LOD1)",-46} | {firstLodSwitchFrameMs,13:F2} ms");
        GD.Print($"{"Phase 6 First Combat Effects (Shot + Impact)",-46} | {firstCombatFrameMs,13:F2} ms");
        GD.Print(new string('-', 65));

        // Subsequent Sustained Pacing Table
        GD.Print("\n--- SUBSEQUENT SUSTAINED PACING TABLE (30f) ---");
        GD.Print($"{"Phase",-38} | {"p50 (ms)",8} | {"p95 (ms)",8} | {"p99 (ms)",8} | {"max (ms)",8}");
        GD.Print(new string('-', 78));
        GD.Print($"{"1. Stationary Squad (60f)",-38} | {resStationary.P50Ms,8:F2} | {resStationary.P95Ms,8:F2} | {resStationary.P99Ms,8:F2} | {resStationary.MaxMs,8:F2}");
        GD.Print($"{"2. Start of Movement (50f)",-38} | {resMove.P50Ms,8:F2} | {resMove.P95Ms,8:F2} | {resMove.P99Ms,8:F2} | {resMove.MaxMs,8:F2}");
        GD.Print($"{"3. Contact Sighting Window (30f)",-38} | {resContact.P50Ms,8:F2} | {resContact.P95Ms,8:F2} | {resContact.P99Ms,8:F2} | {resContact.MaxMs,8:F2}");
        GD.Print($"{"4. Repeat Sighting / Sustained (30f)",-38} | {resRepeat.P50Ms,8:F2} | {resRepeat.P95Ms,8:F2} | {resRepeat.P99Ms,8:F2} | {resRepeat.MaxMs,8:F2}");
        GD.Print($"{"5. Zoomed View at LOD1 (30f)",-38} | {resLod.P50Ms,8:F2} | {resLod.P95Ms,8:F2} | {resLod.P99Ms,8:F2} | {resLod.MaxMs,8:F2}");
        GD.Print($"{"6. Active Combat Effects (30f)",-38} | {resCombat.P50Ms,8:F2} | {resCombat.P95Ms,8:F2} | {resCombat.P99Ms,8:F2} | {resCombat.MaxMs,8:F2}");
        GD.Print(new string('-', 78));
    }

    private static async Task RunFogOfWarCpuCostProbe(Main main)
    {
        GD.Print("\n--- FOG OF WAR DIRECT CPU CALCULATION COST ---");

        if (main.FogOfWar is null)
        {
            GD.Print("Fog of war not attached, skipping cost measurement.");
            return;
        }

        ClientReplica replica = main.Session.Replica;
        UnitCatalog catalog = main.Catalog;
        const float dt = 0.016667f;
        const int frames = 100;

        // Keep squad moving so viewsheds are actively calculated
        for (int i = 0; i < replica.OwnUnits.Length; i++)
        {
            main.Session.Move(replica.OwnUnits[i].Id, replica.OwnUnits[i].Position + new NumericsVector2(150f, 50f));
        }

        // Test A: Fog calculation active (standard)
        main.FogOfWar.IsCalculationSuspended = false;
        main.FogOfWar.SetEnabled(true);
        FramePacingMetrics fogCpuActive = new(frames + 10);
        FramePacingMetrics fogFrameActive = new(frames + 10);

        for (int f = 0; f < frames; f++)
        {
            main.Session.Advance(dt);

            long cpuStart = Stopwatch.GetTimestamp();
            main.FogOfWar.Update(dt, replica, catalog);
            long cpuEnd = Stopwatch.GetTimestamp();
            fogCpuActive.Record((float)((cpuEnd - cpuStart) * 1000.0 / Stopwatch.Frequency));

            long frameStart = Stopwatch.GetTimestamp();
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            long frameEnd = Stopwatch.GetTimestamp();
            fogFrameActive.Record((float)((frameEnd - frameStart) * 1000.0 / Stopwatch.Frequency));
        }
        FramePacingMetrics.PercentileResults resCpuActive = fogCpuActive.CalculateResults();
        FramePacingMetrics.PercentileResults resFrameActive = fogFrameActive.CalculateResults();

        // Test B: Fog calculation suspended (IsCalculationSuspended = true)
        main.FogOfWar.IsCalculationSuspended = true;
        FramePacingMetrics fogCpuSuspended = new(frames + 10);
        FramePacingMetrics fogFrameSuspended = new(frames + 10);

        for (int f = 0; f < frames; f++)
        {
            main.Session.Advance(dt);

            long cpuStart = Stopwatch.GetTimestamp();
            main.FogOfWar.Update(dt, replica, catalog);
            long cpuEnd = Stopwatch.GetTimestamp();
            fogCpuSuspended.Record((float)((cpuEnd - cpuStart) * 1000.0 / Stopwatch.Frequency));

            long frameStart = Stopwatch.GetTimestamp();
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            long frameEnd = Stopwatch.GetTimestamp();
            fogFrameSuspended.Record((float)((frameEnd - frameStart) * 1000.0 / Stopwatch.Frequency));
        }
        FramePacingMetrics.PercentileResults resCpuSuspended = fogCpuSuspended.CalculateResults();
        FramePacingMetrics.PercentileResults resFrameSuspended = fogFrameSuspended.CalculateResults();

        // Restore normal state
        main.FogOfWar.IsCalculationSuspended = false;

        GD.Print($"FogOfWarView.Update CPU Time (Active):     p50={resCpuActive.P50Ms:F3} ms | p95={resCpuActive.P95Ms:F3} ms | p99={resCpuActive.P99Ms:F3} ms | max={resCpuActive.MaxMs:F3} ms");
        GD.Print($"FogOfWarView.Update CPU Time (Suspended):  p50={resCpuSuspended.P50Ms:F3} ms | p95={resCpuSuspended.P95Ms:F3} ms | p99={resCpuSuspended.P99Ms:F3} ms | max={resCpuSuspended.MaxMs:F3} ms");
        GD.Print($"Direct CPU Computation Cost:              p50={resCpuActive.P50Ms - resCpuSuspended.P50Ms:F3} ms | p95={resCpuActive.P95Ms - resCpuSuspended.P95Ms:F3} ms | max={resCpuActive.MaxMs - resCpuSuspended.MaxMs:F3} ms");
        GD.Print($"Frame Delta (Overall):                    Active p50={resFrameActive.P50Ms:F2} ms vs Suspended p50={resFrameSuspended.P50Ms:F2} ms");
        GD.Print("--------------------------------------------------");
    }

    private static void RunNavigationRebuildBenchmark()
    {
        GD.Print("\n--- NAVIGATION GRID REBUILD COST ON RETRY ---");

        StaticGeometry geometry = UstyeRaidScenario.CreateStaticGeometry();
        NavigationTuning navTuning = UstyeGroundScenario.CreateNavigationTuning();
        NavigationBuildLimits buildLimits = UstyeGroundScenario.CreateNavigationBuildLimits();

        Stopwatch sw = Stopwatch.StartNew();
        _ = new NavigationGrid(
            geometry,
            navTuning.CellSize,
            enableHierarchical: true,
            buildLimits: buildLimits,
            sectorSize: navTuning.SectorSize);
        sw.Stop();
        double timeMs = sw.Elapsed.TotalMilliseconds;

        GD.Print($"NavigationGrid Rebuild: {timeMs:F2} ms");
        GD.Print("Note: Reusing cached NavigationGrid across mission retries saves this entire rebuild time on restart.");
        GD.Print("---------------------------------------------");
    }
}
