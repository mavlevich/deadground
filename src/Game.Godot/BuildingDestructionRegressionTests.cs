using System.Diagnostics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

/// <summary>
/// Headless Godot regression suite for Stage 1 PR 3/3:
/// - Modular Soviet Series 1-464 panelka GLB loading (asserts LoadedGlbAsset == true), MultiMesh
///   sleeping intact LOD0/LOD1/LOD2 + settled rubble batching, and pooled damaged floor rigs
/// - Re-observed ruins from fog of war snapping cleanly without replaying live collapse animations
/// - Full chain verification: StructureEvent -> BuildingDestructionView -> FogOfWarView (with sweep
///   coalescing) -> LineOfSightContext height overrides
/// - Variant A pure-C# MultiMesh debris physics pool (256 cap, outward scatter, auto-sleep, 0 allocs)
/// - Procedural concrete/collapse audio bank and AttackGround HUD ContextActionPanel (0 allocs)
/// - 30-building (120-section) client benchmark (build time, draw calls, P50/P95/P99/max frame time, 0 allocs)
/// </summary>
internal static class BuildingDestructionRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckPanelkaViewAndDebrisPhysics(scene);
        CheckReobservedRuinsSnapWithoutLiveCollapse(scene);
        CheckFullChainEventViewFogLosAndCoalescing(scene);
        CheckSoundBankAndAttackGroundHud(scene);
        CheckThirtyBuildingClientBenchmark(scene);
        GD.Print("BUILDING_DESTRUCTION_REGRESSIONS_OK 5/5 passed: panelka_view_debris_zero_alloc reobserved_ruins_snap full_chain_event_view_fow_los audio_and_attack_ground_hud benchmark_120_sections");
    }

    private static void CheckPanelkaViewAndDebrisPhysics(Node3D scene)
    {
        StaticGeometry geometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        IReadOnlyList<DestructibleBuildingSpec> specs = geometry.DestructibleBuildings;
        Require(specs.Count == 12, $"Expected 12 destructible building specs on StaticGeometry, got {specs.Count}.");
        StructureId targetId = new(102);

        Node3D host = new() { Name = "BuildingDestructionTestHost" };
        scene.AddChild(host);

        int heightCallbackCount = 0;
        float lastReportedMaxY = -1f;
        try
        {
            using BuildingDestructionView view = new(
                host,
                geometry,
                specs,
                (id, newMaxY) =>
                {
                    if (id == targetId)
                    {
                        heightCallbackCount++;
                        lastReportedMaxY = newMaxY;
                    }
                });

            Require(view.LoadedGlbAsset, "Panelka GLB asset must load from res://Assets/Buildings/Panelka1464/panelka_1_464.glb.");
            Require(view.SectionCount == specs.Count, $"Expected {specs.Count} panelka sections, got {view.SectionCount}.");
            Require(view.ContainsStructure(targetId), "Expected section 102 registered in BuildingDestructionView.");
            Require(view.IntactBatchedCount == specs.Count, $"All {specs.Count} undamaged sections must be batched in intact MultiMesh.");
            Require(view.ActiveDamagedRigCount == 0, "Zero damaged floor rigs should be active while all sections are intact.");
            Require(view.ActiveDrawCallEstimate == 1, $"All intact sections must render in 1 MultiMesh draw call, got {view.ActiveDrawCallEstimate}.");

            // Verify LOD switching across zoom tiers
            view.UpdateLod(120f);
            Require(view.ActiveLodLevel == 0, "Expected LOD0 at close camera size 120m.");
            view.UpdateLod(240f);
            Require(view.ActiveLodLevel == 1, "Expected LOD1 at RTS camera size 240m.");
            view.UpdateLod(520f);
            Require(view.ActiveLodLevel == 2, "Expected LOD2 at distant camera size 520m.");
            view.UpdateLod(120f);

            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];

            // 1. Floor 1 breached (5 floors still standing) + live StructureDamaged event
            StructureObservation breachedObs = new(targetId, FloorsStanding: 5, FloorBreachMask: 0b00010, Health: 220);
            CombatEventObservation dmgEvent = CombatEventObservation.ForStructure(
                eventId: 1,
                serverTick: 10,
                kind: CombatEventKind.StructureDamaged,
                presentationId: 1,
                origin: new NumericsVector3(494f, 4f, 415f),
                target: new NumericsVector3(494f, 5.5f, 512f),
                structureId: targetId);
            int len1 = WireProtocol.WriteSnapshot(snapBuf, 1, 10, default, default, [dmgEvent], default, default, default, default, [breachedObs]);
            Require(replica.TryApply(snapBuf.AsSpan(0, len1)), "Breached snapshot must apply.");
            view.Update(replica, 0.05f);
            Require(view.DebrisPool.ActiveCount > 0, "StructureDamaged event must spawn cosmetic debris particles.");
            Require(view.ActiveDamagedRigCount == 1, $"Expected 1 active damaged rig after breach, got {view.ActiveDamagedRigCount}.");
            Require(view.IntactBatchedCount == specs.Count - 1, "Breached section must leave the intact MultiMesh batch.");

            // 2. Partial collapse down to 2 floors + live StructureFloorCollapsed event
            StructureObservation partialObs = new(targetId, FloorsStanding: 2, FloorBreachMask: 0b00010, Health: 120);
            CombatEventObservation floorEvent = CombatEventObservation.ForStructure(
                eventId: 2,
                serverTick: 20,
                kind: CombatEventKind.StructureFloorCollapsed,
                presentationId: 1,
                origin: new NumericsVector3(494f, 4f, 415f),
                target: new NumericsVector3(494f, 8.2f, 518f),
                structureId: targetId);
            int len2 = WireProtocol.WriteSnapshot(snapBuf, 2, 20, default, default, [floorEvent], default, default, default, default, [partialObs]);
            Require(replica.TryApply(snapBuf.AsSpan(0, len2)), "Partial collapse snapshot must apply.");
            view.Update(replica, 0.05f);
            Require(view.IsSectionAnimatingCollapse(targetId), "Live StructureFloorCollapsed event must start collapse animation.");
            Require(heightCallbackCount == 1, $"Expected 1 height callback on partial collapse, got {heightCallbackCount}.");
            float expectedPartialMaxY = UrbanDestructionPlaygroundScenario.BaseHeight + 2f * UrbanDestructionPlaygroundScenario.FloorHeight;
            Require(MathF.Abs(lastReportedMaxY - expectedPartialMaxY) < 0.05f, $"Expected partial MaxY {expectedPartialMaxY}, got {lastReportedMaxY}.");

            // 3. Full collapse to rubble (0 floors) + live StructureCollapsed event
            StructureObservation collapsedObs = new(targetId, FloorsStanding: 0, FloorBreachMask: 0, Health: 0);
            CombatEventObservation fullEvent = CombatEventObservation.ForStructure(
                eventId: 3,
                serverTick: 30,
                kind: CombatEventKind.StructureCollapsed,
                presentationId: 1,
                origin: new NumericsVector3(494f, 4f, 415f),
                target: new NumericsVector3(494f, 3.5f, 518f),
                structureId: targetId);
            int len3 = WireProtocol.WriteSnapshot(snapBuf, 3, 30, default, default, [fullEvent], default, default, default, default, [collapsedObs]);
            Require(replica.TryApply(snapBuf.AsSpan(0, len3)), "Full collapse snapshot must apply.");
            view.Update(replica, 0.05f);
            Require(view.IsSectionAnimatingCollapse(targetId), "Live StructureCollapsed event must start full collapse animation.");
            Require(heightCallbackCount == 2, $"Expected 2 height callbacks after full collapse, got {heightCallbackCount}.");
            float expectedRubbleMaxY = UrbanDestructionPlaygroundScenario.BaseHeight + UrbanDestructionPlaygroundScenario.RubbleHeight;
            Require(MathF.Abs(lastReportedMaxY - expectedRubbleMaxY) < 0.05f, $"Expected rubble MaxY {expectedRubbleMaxY}, got {lastReportedMaxY}.");

            // Overflow debris pool beyond 256 to test cap & recycling, then simulate until animation completes and all debris auto-sleeps
            for (int b = 0; b < 10; b++)
            {
                view.DebrisPool.SpawnBurst(new Vector3(494f, 9f, 518f), Vector3.Up, 4f, 32, 3f, 8f, expectedRubbleMaxY);
            }
            Require(view.DebrisPool.ActiveCount <= DebrisPhysicsPool.TotalCapacity, $"Debris pool must stay <= {DebrisPhysicsPool.TotalCapacity}.");

            for (int step = 0; step < 180; step++)
            {
                view.Update(replica, 0.05f);
            }
            Require(view.DebrisPool.AwakeCount == 0, $"All debris particles must settle and auto-sleep, awake={view.DebrisPool.AwakeCount}.");
            Require(!view.IsSectionAnimatingCollapse(targetId), "Collapse animation must complete after 9 seconds.");
            Require(view.ActiveDamagedRigCount == 0, "Collapsed section must release its damaged rig back to the pool.");
            Require(view.RubbleBatchedCount == 1, "Collapsed section must move into the settled Rubble MultiMesh batch.");

            // Zero steady-state allocation verification across 30 frames
            for (int i = 0; i < 5; i++) view.Update(replica, 0.016f);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 30; i++) view.Update(replica, 0.016f);
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(alloc == 0L, $"BuildingDestructionView steady-state Update must allocate 0 bytes, got {alloc}.");
        }
        finally
        {
            host.QueueFree();
        }
    }

    private static void CheckReobservedRuinsSnapWithoutLiveCollapse(Node3D scene)
    {
        StaticGeometry geometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        StructureId ruinsId = new(103);

        Node3D host = new() { Name = "BuildingReobservedRuinsHost" };
        scene.AddChild(host);

        int heightUpdates = 0;
        try
        {
            using BuildingDestructionView view = new(
                host,
                geometry,
                geometry.DestructibleBuildings,
                (id, _) =>
                {
                    if (id == ruinsId) heightUpdates++;
                });

            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];

            // Scout newly discovers already-collapsed ruins with NO live StructureCollapsed event in replica.Events
            StructureObservation staleRuins = new(ruinsId, FloorsStanding: 0, FloorBreachMask: 0, Health: 0);
            int len = WireProtocol.WriteSnapshot(snapBuf, 1, 400, default, default, default, default, default, default, default, [staleRuins]);
            Require(replica.TryApply(snapBuf.AsSpan(0, len)), "Re-observed ruins snapshot must apply.");
            view.Update(replica, 0.05f);

            Require(!view.IsSectionAnimatingCollapse(ruinsId), "Re-observed ruins without a live collapse event must NOT replay collapse animation.");
            Require(view.DebrisPool.ActiveCount == 0, $"Re-observed ruins must NOT spawn live collapse debris, got {view.DebrisPool.ActiveCount}.");
            Require(view.RubbleBatchedCount == 1, $"Re-observed ruins must snap directly to Rubble MultiMesh batch, got {view.RubbleBatchedCount}.");
            Require(view.ActiveDamagedRigCount == 0, "Re-observed ruins must not hold a damaged floor rig.");
            Require(heightUpdates == 1, "Re-observed ruins must still notify FoW/LOS of the lowered height.");
        }
        finally
        {
            host.QueueFree();
        }
    }

    private static void CheckFullChainEventViewFogLosAndCoalescing(Node3D scene)
    {
        StaticGeometry geometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using FogOfWarView fow = new(geometry.HeightMap, geometry, customCellSize: 4.0f);
        LineOfSightContext losCtx = new();

        Node3D host = new() { Name = "BuildingFullChainTestHost" };
        scene.AddChild(host);

        try
        {
            BuildingDestructionView? view = null;
            view = new BuildingDestructionView(
                host,
                geometry,
                geometry.DestructibleBuildings,
                (id, newMaxY) =>
                {
                    fow.UpdateStructureHeight(id, newMaxY);
                    if (view is not null)
                    {
                        float[] overrides = view.ObservedMaxYOverrides;
                        losCtx.SetStructureMaxYOverrides(overrides, overrides.Length);
                    }
                });
            using (view)
            {
                losCtx.SetStructureMaxYOverrides(view.ObservedMaxYOverrides, view.ObservedMaxYOverrides.Length);

                NumericsVector2 observer = new(482f, 445f);
                NumericsVector2 targetBehindPanelka = UrbanDestructionPlaygroundScenario.UsM60WestPos;
                NumericsVector2 farGroundBehindPanelka = new(482f, 750f);

                ushort t62Idx = catalog.RequireDefinitionIndex("t62");
                OwnUnitObservation scout = new(new EntityId(1), t62Idx, observer, 0, Health: 100, Ammunition: 40);
                ClientReplica replica = new();
                byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];

                int initLen = WireProtocol.WriteSnapshot(snapBuf, 1, 1, [scout]);
                Require(replica.TryApply(snapBuf.AsSpan(0, initLen)), "Initial scout snapshot must apply.");
                fow.UpdateAllNow(replica, catalog);

                bool visibleBefore = fow.IsWorldPointSeen(farGroundBehindPanelka.X, farGroundBehindPanelka.Y);
                Require(!visibleBefore, "Ground behind intact 5-story panelka must be occluded in FogOfWarView.");

                float eyeY = geometry.HeightMap.GetHeight(observer.X, observer.Y) + 2.3f;
                float tgtY = geometry.HeightMap.GetHeight(targetBehindPanelka.X, targetBehindPanelka.Y) + 2.3f;
                bool engineLosBefore = geometry.IsLineOfSightClear(
                    new NumericsVector3(observer.X, eyeY, observer.Y),
                    new NumericsVector3(targetBehindPanelka.X, tgtY, targetBehindPanelka.Y),
                    losCtx);
                Require(!engineLosBefore, "LineOfSightContext must be blocked by intact panelka before collapse.");

                // Feed live collapse events + observations across consecutive frames (verifying FoW sweep coalescing!)
                StructureObservation[] collapsedAll = new StructureObservation[4];
                uint seq = 2;
                uint eventId = 10;
                for (int i = 0; i < 4; i++)
                {
                    StructureId sid = new(101 + i);
                    collapsedAll[i] = new StructureObservation(sid, FloorsStanding: 0, FloorBreachMask: 0, Health: 0);
                    CombatEventObservation ev = CombatEventObservation.ForStructure(
                        eventId: eventId++,
                        serverTick: 20 + i * 2,
                        kind: CombatEventKind.StructureCollapsed,
                        presentationId: 1,
                        origin: new NumericsVector3(observer.X, eyeY, observer.Y),
                        target: new NumericsVector3(482f + i * 12f, 4f, 518f),
                        structureId: sid);
                    int len = WireProtocol.WriteSnapshot(snapBuf, seq++, 20 + i * 2, [scout], default, [ev], default, default, default, default, collapsedAll.AsSpan(0, i + 1));
                    Require(replica.TryApply(snapBuf.AsSpan(0, len)), $"Collapse snapshot {i} must apply.");
                    view.Update(replica, 0.016f);
                    fow.Update(0.016f, replica, catalog);
                }

                // Advance budgeted FogOfWarView.Update frames and confirm coalesced sweep finishes & publishes
                for (int frame = 0; frame < 30; frame++)
                {
                    fow.Update(0.05f, replica, catalog);
                }

                bool visibleAfter = fow.IsWorldPointSeen(farGroundBehindPanelka.X, farGroundBehindPanelka.Y);
                Require(visibleAfter, "Full chain event -> view -> coalesced FogOfWarView must open sight behind collapsed panelka.");

                bool engineLosAfter = geometry.IsLineOfSightClear(
                    new NumericsVector3(observer.X, eyeY, observer.Y),
                    new NumericsVector3(targetBehindPanelka.X, tgtY, targetBehindPanelka.Y),
                    losCtx);
                Require(engineLosAfter, "Full chain event -> view -> LineOfSightContext must see over collapsed panelka.");

                // Verify zero allocations on repeat UpdateStructureHeight call
                float rubbleMaxY = UrbanDestructionPlaygroundScenario.BaseHeight + UrbanDestructionPlaygroundScenario.RubbleHeight;
                long before = GC.GetAllocatedBytesForCurrentThread();
                fow.UpdateStructureHeight(new StructureId(102), rubbleMaxY + 0.5f);
                fow.UpdateStructureHeight(new StructureId(102), rubbleMaxY);
                long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(alloc == 0L, $"FogOfWarView.UpdateStructureHeight must allocate 0 bytes, got {alloc}.");
            }
        }
        finally
        {
            host.QueueFree();
        }
    }

    private static void CheckSoundBankAndAttackGroundHud(Node3D scene)
    {
        Node3D host = new() { Name = "BuildingDestructionHudAudioTest" };
        scene.AddChild(host);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        ContextActionPanel panel = new(catalog);
        host.AddChild(panel);

        try
        {
            using BuildingDestructionSoundBank bank = new(host);
            Require(bank.PrebakedClipCount == 6, $"Expected 6 pre-baked destruction PCM clips, got {bank.PrebakedClipCount}.");

            bank.PlayConcreteHit(new Vector3(500f, 5f, 512f));
            long beforeAudio = GC.GetAllocatedBytesForCurrentThread();
            bank.PlayConcreteHit(new Vector3(500f, 5f, 512f));
            bank.PlayFloorCollapse(new Vector3(500f, 8f, 512f));
            bank.PlayFullCollapse(new Vector3(500f, 3f, 512f));
            long allocAudio = GC.GetAllocatedBytesForCurrentThread() - beforeAudio;
            Require(allocAudio == 0L, $"BuildingDestructionSoundBank playback must allocate 0 bytes, got {allocAudio}.");

            ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
            OwnUnitObservation m60 = new(new EntityId(77), m60Idx, new NumericsVector2(490f, 415f), 0, Health: 200, Ammunition: 40);
            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
            int len = WireProtocol.WriteSnapshot(snapBuf, 1, 1, [m60]);
            Require(replica.TryApply(snapBuf.AsSpan(0, len)), "M60 snapshot must apply.");

            HashSet<EntityId> sel = [m60.Id];
            panel.UpdateContext(sel, replica);
            Require(panel.Visible && panel.AttackGroundButton.Visible && !panel.AttackGroundButton.Disabled,
                "ContextActionPanel must display enabled ATTACK GROUND [T] button for direct-fire tank.");

            long beforeHud = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++) panel.UpdateContext(sel, replica);
            long allocHud = GC.GetAllocatedBytesForCurrentThread() - beforeHud;
            Require(allocHud == 0L, $"ContextActionPanel AttackGround steady-state update must allocate 0 bytes, got {allocHud}.");
        }
        finally
        {
            host.QueueFree();
        }
    }

    private static void CheckThirtyBuildingClientBenchmark(Node3D scene)
    {
        const int buildings = 30;
        const int sectionsPerBuilding = 4;
        const int totalSections = buildings * sectionsPerBuilding;

        HeightMap map = UrbanDestructionPlaygroundScenario.CreateHeightMap();
        StructureDestructionProfile profile = UrbanDestructionPlaygroundScenario.PanelkaProfile;
        OpaqueStructure[] structures = new OpaqueStructure[totalSections];
        DestructibleBuildingSpec[] specs = new DestructibleBuildingSpec[totalSections];

        int idx = 0;
        for (int b = 0; b < buildings; b++)
        {
            int col = b % 6;
            int row = b / 6;
            float baseX = 120f + col * 130f;
            float baseZ = 220f + row * 120f;
            for (int s = 0; s < sectionsPerBuilding; s++)
            {
                float minX = baseX + s * 12f;
                float gy = map.GetHeight(minX + 6f, baseZ + 6f);
                StructureId sid = new(1000 + idx);
                structures[idx] = new OpaqueStructure(sid, minX, gy, baseZ, minX + 12f, gy + 15.5f, baseZ + 12f, isGrounded: true);
                specs[idx] = new DestructibleBuildingSpec(sid, profile, MathF.PI);
                idx++;
            }
        }

        StaticGeometry geometry = new(map, structures, [], [], [], [], destructibleBuildings: specs);
        Node3D host = new() { Name = "BuildingBenchmark120Host" };
        scene.AddChild(host);

        try
        {
            long buildStart = Stopwatch.GetTimestamp();
            using BuildingDestructionView view = new(host, geometry, geometry.DestructibleBuildings);
            double buildMs = Stopwatch.GetElapsedTime(buildStart).TotalMilliseconds;

            Require(view.LoadedGlbAsset, "120-section client benchmark must load real GLB asset.");
            Require(view.SectionCount == totalSections, $"Expected {totalSections} sections.");
            int intactDrawCalls = view.ActiveDrawCallEstimate;
            Require(intactDrawCalls == 1, $"120 sleeping intact sections must submit exactly 1 opaque draw call, got {intactDrawCalls}.");

            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];

            // Collapse 12 sections live (simulating heavy barrage), then measure 60 steady-state frames
            StructureObservation[] obsList = new StructureObservation[totalSections];
            CombatEventObservation[] events = new CombatEventObservation[12];
            for (int i = 0; i < totalSections; i++)
            {
                obsList[i] = new StructureObservation(specs[i].Id, FloorsStanding: i < 12 ? (byte)0 : (byte)5, FloorBreachMask: 0, Health: i < 12 ? (ushort)0 : profile.MaxHealth);
                if (i < 12)
                {
                    OpaqueStructure st = structures[i];
                    events[i] = CombatEventObservation.ForStructure(
                        eventId: (uint)(i + 1),
                        serverTick: 10,
                        kind: CombatEventKind.StructureCollapsed,
                        presentationId: 1,
                        origin: new NumericsVector3(st.MinX + 6f, st.MinY + 4f, st.MinZ - 80f),
                        target: new NumericsVector3(st.MinX + 6f, st.MinY + 5f, st.MinZ + 6f),
                        structureId: specs[i].Id);
                }
            }
            int snapLen = WireProtocol.WriteSnapshot(snapBuf, 1, 10, default, default, events, default, default, default, default, obsList);
            Require(replica.TryApply(snapBuf.AsSpan(0, snapLen)), "120-section collapse snapshot must apply.");
            view.Update(replica, 0.016f);

            const int measuredFrames = 60;
            double[] frameMs = new double[measuredFrames];
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < measuredFrames; f++)
            {
                long t0 = Stopwatch.GetTimestamp();
                view.Update(replica, 0.016f);
                frameMs[f] = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            }
            long steadyAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Require(steadyAlloc == 0L, $"120-section active collapse update must allocate 0 bytes, got {steadyAlloc}.");

            // Let all 12 collapses finish and collapse all remaining 108 sections to settled rubble
            for (int i = 0; i < totalSections; i++)
            {
                obsList[i] = new StructureObservation(specs[i].Id, FloorsStanding: 0, FloorBreachMask: 0, Health: 0);
            }
            int rubbleLen = WireProtocol.WriteSnapshot(snapBuf, 2, 120, default, default, default, default, default, default, default, obsList);
            Require(replica.TryApply(snapBuf.AsSpan(0, rubbleLen)), "120-section rubble snapshot must apply.");
            for (int step = 0; step < 120; step++) view.Update(replica, 0.05f);

            int rubbleDrawCalls = view.ActiveDrawCallEstimate;
            Require(view.RubbleBatchedCount == totalSections, $"All {totalSections} collapsed sections must batch in Rubble MultiMesh.");
            Require(rubbleDrawCalls == 1, $"120 settled rubble sections must submit exactly 1 opaque draw call, got {rubbleDrawCalls}.");

            Array.Sort(frameMs);
            double p50 = frameMs[measuredFrames / 2];
            double p95 = frameMs[(int)(measuredFrames * 0.95)];
            double p99 = frameMs[(int)(measuredFrames * 0.99)];
            double max = frameMs[measuredFrames - 1];
            GD.Print($"BUILDING_DESTRUCTION_BENCHMARK_120 sections={totalSections} build_ms={buildMs:F2} intact_draws={intactDrawCalls} rubble_draws={rubbleDrawCalls} p50_ms={p50:F3} p95_ms={p95:F3} p99_ms={p99:F3} max_ms={max:F3} alloc_bytes={steadyAlloc}");
        }
        finally
        {
            host.QueueFree();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"BuildingDestructionRegressionTests failed: {message}");
    }
}
