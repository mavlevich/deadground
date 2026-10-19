using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Sandbox;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

internal static class CombatFeedbackProbe
{
    public static async System.Threading.Tasks.Task<bool> Run(Main main)
    {
        GD.Print("COMBAT_FEEDBACK_PROBE_START");
        try
        {
            // 1. Hot-path CPU and allocation microbenchmark
            bool microOk = RunCpuMicrobenchmark(main);
            if (!microOk)
            {
                main.GetTree().Quit(1);
                return false;
            }

            // 2. Native frame-driven sustained load probe
            bool frameOk = await RunNativeFrameProbe(main);
            main.GetTree().Quit(frameOk ? 0 : 1);
            return frameOk;
        }
        catch (Exception ex)
        {
            GD.PushError("COMBAT_FEEDBACK_PROBE_ERROR: " + ex);
            main.GetTree().Quit(1);
            return false;
        }
    }

    public static bool RunCpuMicrobenchmark(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            UnitView tank = new("prototype_tank");
            unitViews.Views.Add(new EntityId(101), tank);
            parent.AddChild(tank.Root);
            tank.UpdatePosition(new Vector3(25, 0, 25));

            Random rng = new(1337);
            uint eventCounter = 1;

            // Warmup: 5 seconds of simulated time (100 ticks @ 20Hz)
            const float tickDt = 0.05f; // 20 Hz
            for (int tick = 0; tick < 100; tick++)
            {
                CombatEventObservation shot = new(
                    EventId: eventCounter++,
                    ServerTick: tick,
                    Kind: CombatEventKind.ShotFired,
                    PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
                    Origin: new NumericsVector3(25, 1.9f, 25),
                    Target: new NumericsVector3(40 + (float)rng.NextDouble() * 10f, 1.5f, 25 + (float)rng.NextDouble() * 10f),
                    ShooterUnitId: new EntityId(101));

                effects.ProcessEvents([shot], unitViews);
                unitViews.UpdateRecoil(tickDt);
                effects.Update(tickDt);
            }

            effects.Clear();
            effects.ResetCounters();

            int measuredTicks = 600;
            float[] frameWorkMs = new float[measuredTicks];
            CombatEventObservation[] shotBatch = new CombatEventObservation[1];

            // Allocation measurement on warmed hot path
            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();

            for (int tick = 0; tick < measuredTicks; tick++)
            {
                long startNs = System.Diagnostics.Stopwatch.GetTimestamp();

                shotBatch[0] = new CombatEventObservation(
                    EventId: eventCounter++,
                    ServerTick: tick,
                    Kind: CombatEventKind.ShotFired,
                    PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
                    Origin: new NumericsVector3(25, 1.9f, 25),
                    Target: new NumericsVector3(35 + (float)rng.NextDouble() * 20f, 1.5f, 20 + (float)rng.NextDouble() * 20f),
                    ShooterUnitId: new EntityId(101));

                effects.ProcessEvents(shotBatch, unitViews);
                unitViews.UpdateRecoil(tickDt);
                effects.Update(tickDt);

                long endNs = System.Diagnostics.Stopwatch.GetTimestamp();
                frameWorkMs[tick] = (float)((endNs - startNs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }

            long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
            long hotPathAllocatedBytes = afterAlloc - beforeAlloc;

            int steadyPeak = effects.PeakEffectsCount;
            int steadyMeshDropped = effects.DroppedMeshEffectsCount;
            int steadyAudioDropped = effects.DroppedAudioVoicesCount;

            // Saturation burst test: 100 unique shots + 5 destruction events in single frame
            CombatEventObservation[] burst = new CombatEventObservation[105];
            for (int i = 0; i < 100; i++)
            {
                burst[i] = new CombatEventObservation(
                    EventId: eventCounter++,
                    ServerTick: 700,
                    Kind: CombatEventKind.ShotFired,
                    PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
                    Origin: new NumericsVector3(25, 1.9f, 25),
                    Target: new NumericsVector3(30 + (float)rng.NextDouble() * 30f, 1.5f, 20 + (float)rng.NextDouble() * 30f),
                    ShooterUnitId: new EntityId(101));
            }
            for (int i = 0; i < 5; i++)
            {
                burst[100 + i] = new CombatEventObservation(
                    EventId: eventCounter++,
                    ServerTick: 700,
                    Kind: CombatEventKind.UnitDestroyed,
                    PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
                    Origin: default,
                    Target: new NumericsVector3(30 + (float)rng.NextDouble() * 20f, 0, 20 + (float)rng.NextDouble() * 20f),
                    ShooterUnitId: default);
            }

            long burstStartNs = System.Diagnostics.Stopwatch.GetTimestamp();
            effects.ProcessEvents(burst, unitViews);
            effects.Update(0.016);
            long burstEndNs = System.Diagnostics.Stopwatch.GetTimestamp();
            float burstCpuWorkMs = (float)((burstEndNs - burstStartNs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

            int burstPeak = effects.PeakEffectsCount;
            int burstMeshDropped = effects.DroppedMeshEffectsCount;
            int burstAudioDropped = effects.DroppedAudioVoicesCount;

            Array.Sort(frameWorkMs);
            float p50 = frameWorkMs[(int)(measuredTicks * 0.50f)];
            float p95 = frameWorkMs[(int)(measuredTicks * 0.95f)];
            float p99 = frameWorkMs[(int)(measuredTicks * 0.99f)];

            GD.Print("--- COMBAT FEEDBACK CPU & ALLOCATION MICROBENCHMARK ---");
            GD.Print("Platform: " + OS.GetName() + " / " + OS.GetProcessorName());
            GD.Print("Capacities: Tracers=" + CombatEffects.MaxTracers + " Flashes=" + CombatEffects.MaxMuzzleFlashes +
                     " Smoke=" + CombatEffects.MaxSmokePuffs + " Sparks=" + CombatEffects.MaxSparks +
                     " Impacts=" + CombatEffects.MaxImpacts + " Explosions=" + CombatEffects.MaxExplosions +
                     " Residues=" + CombatEffects.MaxResidues + " Audio=" + CombatEffects.MaxAudioPlayers);
            GD.Print($"Steady Load: 20 shots/sec over {measuredTicks * tickDt:F1}s ({measuredTicks} ticks)");
            GD.Print($"Steady Peak Effects: {steadyPeak}, Steady Drops: mesh={steadyMeshDropped} audio={steadyAudioDropped}");
            GD.Print($"CPU Work (Steady ms): p50={p50:F3} p95={p95:F3} p99={p99:F3}");
            GD.Print($"Burst Load: 100 shots + 5 destructions in single frame ({burstCpuWorkMs:F3} ms)");
            GD.Print($"Burst Peak Effects: {burstPeak}, Burst Drops: mesh={burstMeshDropped} audio={burstAudioDropped}");
            GD.Print($"Warmed Hot Path Managed Allocations: {hotPathAllocatedBytes} B");
            GD.Print("--- END MICROBENCHMARK ---");
            return true;
        }
        catch (Exception ex)
        {
            GD.PushError("CPU_MICROBENCHMARK_ERROR: " + ex);
            return false;
        }
        finally
        {
            parent.QueueFree();
        }
    }

    public static async System.Threading.Tasks.Task<bool> RunNativeFrameProbe(Main main)
    {
        Node3D probeRoot = new();
        main.AddChild(probeRoot);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(probeRoot, map);
            CombatEffects effects = new(map);
            probeRoot.AddChild(effects);

            UnitView tank = new("prototype_tank");
            unitViews.Views.Add(new EntityId(101), tank);
            probeRoot.AddChild(tank.Root);
            tank.UpdatePosition(new Vector3(25, 0, 25));

            AudioListener3D listener = new();
            probeRoot.AddChild(listener);
            listener.Position = new Vector3(25, 5, 25);
            try { listener.MakeCurrent(); } catch { }

            Random rng = new(4242);
            uint eventCounter = 10000;
            const int warmupFrames = 60;
            const int sustainedFrames = 300;

            FramePacingMetrics pacing = new(sustainedFrames + 16);
            float[] cpuTimesMs = new float[sustainedFrames];
            CombatEventObservation[] shotBatch = new CombatEventObservation[1];

            // 1. Warmup: 60 actual process frames with combat effects and audio
            float warmupShotTimer = 0f;
            for (int f = 0; f < warmupFrames; f++)
            {
                await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
                float dt = (float)main.GetProcessDeltaTime();
                warmupShotTimer += dt;
                if (warmupShotTimer >= 0.05f)
                {
                    warmupShotTimer -= 0.05f;
                    shotBatch[0] = new CombatEventObservation(
                        EventId: eventCounter++,
                        ServerTick: f,
                        Kind: CombatEventKind.ShotFired,
                        PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
                        Origin: new NumericsVector3(25, 1.9f, 25),
                        Target: new NumericsVector3(45 + (float)rng.NextDouble() * 10f, 1.5f, 25 + (float)rng.NextDouble() * 10f),
                        ShooterUnitId: new EntityId(101));
                    effects.ProcessEvents(shotBatch, unitViews, f);
                }
                unitViews.UpdateRecoil(dt);
                effects.Update(dt);
                effects.UpdateListener(listener.Position);
            }

            effects.Clear();
            effects.ResetCounters();
            pacing.Reset();

            // 2. Sustained measurement: 300 process frames with steady direct-fire load (~20 shots/sec)
            // and a saturation burst at frame 150 (20 shots + 2 destructions)
            float shotTimer = 0f;
            long prevFrameNs = System.Diagnostics.Stopwatch.GetTimestamp();

            for (int f = 0; f < sustainedFrames; f++)
            {
                await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
                long nowNs = System.Diagnostics.Stopwatch.GetTimestamp();
                float frameIntervalMs = (float)((nowNs - prevFrameNs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                prevFrameNs = nowNs;

                float dt = (float)main.GetProcessDeltaTime();
                long workStartNs = System.Diagnostics.Stopwatch.GetTimestamp();

                shotTimer += dt;
                while (shotTimer >= 0.05f)
                {
                    shotTimer -= 0.05f;
                    shotBatch[0] = new CombatEventObservation(
                        EventId: eventCounter++,
                        ServerTick: 100 + f,
                        Kind: CombatEventKind.ShotFired,
                        PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
                        Origin: new NumericsVector3(25, 1.9f, 25),
                        Target: new NumericsVector3(40 + (float)rng.NextDouble() * 15f, 1.5f, 20 + (float)rng.NextDouble() * 15f),
                        ShooterUnitId: new EntityId(101));
                    effects.ProcessEvents(shotBatch, unitViews, 100 + f);
                }

                // Saturation burst at frame 150
                if (f == 150)
                {
                    CombatEventObservation[] burst = new CombatEventObservation[22];
                    for (int i = 0; i < 20; i++)
                    {
                        burst[i] = new CombatEventObservation(
                            EventId: eventCounter++,
                            ServerTick: 100 + f,
                            Kind: CombatEventKind.ShotFired,
                            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
                            Origin: new NumericsVector3(25, 1.9f, 25),
                            Target: new NumericsVector3(30 + (float)rng.NextDouble() * 30f, 1.5f, 20 + (float)rng.NextDouble() * 30f),
                            ShooterUnitId: new EntityId(101));
                    }
                    for (int i = 0; i < 2; i++)
                    {
                        burst[20 + i] = new CombatEventObservation(
                            EventId: eventCounter++,
                            ServerTick: 100 + f,
                            Kind: CombatEventKind.UnitDestroyed,
                            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
                            Origin: default,
                            Target: new NumericsVector3(35 + (float)rng.NextDouble() * 20f, 0, 25 + (float)rng.NextDouble() * 20f),
                            ShooterUnitId: default);
                    }
                    effects.ProcessEvents(burst, unitViews, 100 + f);
                }

                unitViews.UpdateRecoil(dt);
                effects.Update(dt);
                effects.UpdateListener(listener.Position);

                long workEndNs = System.Diagnostics.Stopwatch.GetTimestamp();
                float cpuWorkMs = (float)((workEndNs - workStartNs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                cpuTimesMs[f] = cpuWorkMs;

                pacing.Record(frameIntervalMs, cpuWorkMs);
            }

            FramePacingMetrics.PercentileResults results = pacing.CalculateResults();
            Array.Sort(cpuTimesMs);
            float cpuP50 = cpuTimesMs[(int)(sustainedFrames * 0.50f)];
            float cpuP95 = cpuTimesMs[(int)(sustainedFrames * 0.95f)];
            float cpuP99 = cpuTimesMs[(int)(sustainedFrames * 0.99f)];
            float cpuMax = cpuTimesMs[sustainedFrames - 1];

            int meshDrops = effects.DroppedMeshEffectsCount;
            int audioDrops = effects.DroppedAudioVoicesCount;

            GD.Print("--- COMBAT FEEDBACK NATIVE FRAME PROBE REPORT ---");
            GD.Print($"Platform: {OS.GetName()} / {OS.GetProcessorName()}");
            GD.Print($"Frames: {sustainedFrames} sustained frames (after {warmupFrames} warmup frames)");
            GD.Print($"Frame Interval (ms): p50={results.P50Ms:F2} p95={results.P95Ms:F2} p99={results.P99Ms:F2} max={results.MaxMs:F2}");
            GD.Print($"CPU Work (ms): p50={cpuP50:F3} p95={cpuP95:F3} p99={cpuP99:F3} max={cpuMax:F3}");
            GD.Print($"Mesh Slot Evictions: {meshDrops}, Audio Voice Drops: {audioDrops}");
            GD.Print($"Peak Active Effects: {effects.PeakEffectsCount}");
            GD.Print("--- END NATIVE FRAME PROBE REPORT ---");
            GD.Print($"COMBAT_FEEDBACK_PROBE_OK frame_p50={results.P50Ms:F2} work_p50={cpuP50:F3} mesh_drops={meshDrops} audio_drops={audioDrops}");
            return true;
        }
        catch (Exception ex)
        {
            GD.PushError("NATIVE_FRAME_PROBE_ERROR: " + ex);
            return false;
        }
        finally
        {
            probeRoot.QueueFree();
            // Await frames to allow Godot to flush freed node instances cleanly before quit
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    public static async System.Threading.Tasks.Task<bool> RunVisualCapture(Main main, string outputDir)
    {
        try
        {
            System.IO.Directory.CreateDirectory(outputDir);

            // Frame the firefight cleanly between tank #2 and enemy contact
            main.CameraRig.SetDefaultView(new Vector3(23, 0, 25), 38f);
            main.CameraRig.ResetToDefault();

            // Move recon out of line of fire so tank firing presentation is completely unobstructed
            main.Session.Move(new EntityId(1), new NumericsVector2(14, 16));

            // Wait for recon to clear and scene to settle
            for (int i = 0; i < 30; i++)
            {
                await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            // Find shooter tank (#2) and aim turret directly at enemy target
            EntityId shooterId = new(2);
            if (!main.UnitViews.Views.TryGetValue(shooterId, out UnitView? shooter))
            {
                shooter = main.UnitViews.Views.Values.FirstOrDefault(v => v.TryGetMuzzlePosition(out _))
                          ?? main.UnitViews.Views.Values.First();
                shooterId = main.UnitViews.Views.FirstOrDefault(kv => kv.Value == shooter).Key;
            }

            Vector3 target = new(33, 1.5f, 25);
            shooter.AimAt(new Vector3(target.X, 0, target.Z));

            // Wait 2 frames for aim alignment to render
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            bool hasMuzzle = shooter.TryGetMuzzlePosition(out Vector3 probeMuzzlePos);
            GD.Print($"CAPTURE_PROBE shooter={shooterId.Value} has_muzzle={hasMuzzle} muzzle_pos={probeMuzzlePos} turret_rot={shooter.TurretRotation} model_pos={shooter.Root.Position}");
            SaveViewport(main, System.IO.Path.Combine(outputDir, "feedback_0_aimed_neutral.png"));

            // Fire shot: own tank fires toward enemy contact
            Vector3 origin = shooter.TryGetMuzzlePosition(out Vector3 muzzlePos)
                ? muzzlePos
                : shooter.Root.Position + new Vector3(0, 1.9f, 0);

            CombatEventObservation shot = new(
                EventId: 8801,
                ServerTick: 10,
                Kind: CombatEventKind.ShotFired,
                PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
                Origin: new NumericsVector3(origin.X, origin.Y, origin.Z),
                Target: new NumericsVector3(target.X, target.Y, target.Z),
                ShooterUnitId: shooterId);

            main.CombatEffects.ProcessEvents([shot], main.UnitViews);

            // Step frame-by-frame and capture visual stages
            for (int f = 1; f <= 5; f++)
            {
                await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
                if (f == 1)
                {
                    SaveViewport(main, System.IO.Path.Combine(outputDir, "feedback_1_muzzle_recoil.png"));
                }
                else if (f == 3)
                {
                    SaveViewport(main, System.IO.Path.Combine(outputDir, "feedback_2_streak_flight.png"));
                }
                else if (f == 4)
                {
                    SaveViewport(main, System.IO.Path.Combine(outputDir, "feedback_3_impact_sparks.png"));
                }
            }

            // Frame 4: destruction explosion burst
            CombatEventObservation destroyed = new(
                EventId: 8802,
                ServerTick: 12,
                Kind: CombatEventKind.UnitDestroyed,
                PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
                Origin: default,
                Target: new NumericsVector3(target.X, 0, target.Z),
                ShooterUnitId: default);

            main.CombatEffects.ProcessEvents([destroyed], main.UnitViews);
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            SaveViewport(main, System.IO.Path.Combine(outputDir, "feedback_4_destruction_burst.png"));

            // Frame 5: lingering ground scorch residue
            for (int step = 0; step < 20; step++)
            {
                await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            SaveViewport(main, System.IO.Path.Combine(outputDir, "feedback_5_ground_residue.png"));

            GD.Print("COMBAT_FEEDBACK_CAPTURE_OK: " + outputDir);
            main.GetTree().Quit(0);
            return true;
        }
        catch (Exception ex)
        {
            GD.PushError("COMBAT_FEEDBACK_CAPTURE_ERROR: " + ex);
            main.GetTree().Quit(1);
            return false;
        }
    }

    internal static void SaveViewport(Main main, string path)
    {
        Image? img = main.GetViewport().GetTexture().GetImage();
        if (img is null)
        {
            throw new InvalidOperationException($"Viewport texture image is null when saving to '{path}'.");
        }

        Error err = img.SavePng(path);
        if (err != Error.Ok)
        {
            throw new InvalidOperationException($"Failed to save viewport image to '{path}': {err}");
        }

        GD.Print("CAPTURED_FRAME: " + path);
    }
}
