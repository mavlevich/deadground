using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

/// <summary>
/// Headless Godot regression suite for Stage 1 PR 3/3:
/// - Modular Soviet Series 1-464 panelka GLB loading and BuildingDestructionView lifecycle
/// - Variant A pure-C# MultiMesh debris physics pool (256 cap, bounce, auto-sleep, 0 allocs)
/// - FogOfWarView.UpdateStructureHeight parity with LineOfSightContext overrides
/// - Procedural concrete/collapse audio bank and AttackGround HUD ContextActionPanel (0 allocs)
/// </summary>
internal static class BuildingDestructionRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckPanelkaViewAndDebrisPhysics(scene);
        CheckFogOfWarCollapseParity();
        CheckSoundBankAndAttackGroundHud(scene);
        GD.Print("BUILDING_DESTRUCTION_REGRESSIONS_OK 3/3 passed: panelka_view_debris_zero_alloc fow_collapse_parity audio_and_attack_ground_hud");
    }

    private static void CheckPanelkaViewAndDebrisPhysics(Node3D scene)
    {
        StaticGeometry geometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        DestructibleBuildingSpec[] specs = UrbanDestructionPlaygroundScenario.GetBuildingSpecs();
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

            Require(view.SectionCount == specs.Length, $"Expected {specs.Length} panelka sections, got {view.SectionCount}.");
            Require(view.ContainsStructure(targetId), "Expected section 102 registered in BuildingDestructionView.");

            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];

            // 1. Floor 1 breached (5 floors still standing) + StructureDamaged event
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

            // 2. Partial collapse down to 2 floors + StructureFloorCollapsed event
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
            Require(heightCallbackCount == 1, $"Expected 1 height callback on partial collapse, got {heightCallbackCount}.");
            float expectedPartialMaxY = UrbanDestructionPlaygroundScenario.BaseHeight + 2f * UrbanDestructionPlaygroundScenario.FloorHeight;
            Require(MathF.Abs(lastReportedMaxY - expectedPartialMaxY) < 0.05f, $"Expected partial MaxY {expectedPartialMaxY}, got {lastReportedMaxY}.");

            // 3. Full collapse to rubble (0 floors) + StructureCollapsed event
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
            Require(heightCallbackCount == 2, $"Expected 2 height callbacks after full collapse, got {heightCallbackCount}.");
            float expectedRubbleMaxY = UrbanDestructionPlaygroundScenario.BaseHeight + UrbanDestructionPlaygroundScenario.RubbleHeight;
            Require(MathF.Abs(lastReportedMaxY - expectedRubbleMaxY) < 0.05f, $"Expected rubble MaxY {expectedRubbleMaxY}, got {lastReportedMaxY}.");

            // Overflow debris pool beyond 256 to test cap & recycling, then simulate until all debris auto-sleeps
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

    private static void CheckFogOfWarCollapseParity()
    {
        StaticGeometry geometry = UrbanDestructionPlaygroundScenario.CreateStaticGeometry();
        using FogOfWarView fow = new(geometry.HeightMap, geometry, customCellSize: 4.0f);

        NumericsVector2 observer = new(482f, 445f);
        NumericsVector2 targetBehindPanelka = UrbanDestructionPlaygroundScenario.UsM60WestPos;
        // Far ground point behind section 101 (X=476..488, clear of rear panelka 111 at X=488..512):
        // infinitely shadowed by intact 15.5m building, but clears the short ground shadow of 1.5m rubble.
        NumericsVector2 farGroundBehindPanelka = new(482f, 750f);

        fow.UpdateUnitNow(observer, eyeHeight: 2.3f, sensorRange: 450f);
        bool visibleBefore = fow.IsWorldPointSeen(farGroundBehindPanelka.X, farGroundBehindPanelka.Y);
        Require(!visibleBefore, "Ground behind intact 5-story panelka must be occluded in FogOfWarView.");

        float rubbleMaxY = UrbanDestructionPlaygroundScenario.BaseHeight + UrbanDestructionPlaygroundScenario.RubbleHeight;
        for (int id = 101; id <= 104; id++)
        {
            Require(fow.UpdateStructureHeight(new StructureId(id), rubbleMaxY), $"UpdateStructureHeight must succeed for section {id}.");
        }

        // Verify zero allocations on repeat UpdateStructureHeight call
        long before = GC.GetAllocatedBytesForCurrentThread();
        fow.UpdateStructureHeight(new StructureId(102), rubbleMaxY + 0.5f);
        fow.UpdateStructureHeight(new StructureId(102), rubbleMaxY);
        long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(alloc == 0L, $"FogOfWarView.UpdateStructureHeight must allocate 0 bytes, got {alloc}.");

        fow.UpdateUnitNow(observer, eyeHeight: 2.3f, sensorRange: 450f);
        bool visibleAfter = fow.IsWorldPointSeen(farGroundBehindPanelka.X, farGroundBehindPanelka.Y);
        Require(visibleAfter, "Collapsing central panelka to 1.5m rubble must open FogOfWarView sight behind it.");

        // Confirm 1-to-1 agreement with Engine.Core LineOfSightContext height overrides
        LineOfSightContext losCtx = new();
        float[] overrides = new float[geometry.Structures.Count];
        Array.Fill(overrides, float.NaN);
        for (int id = 101; id <= 104; id++)
        {
            if (geometry.TryGetStructureIndex(new StructureId(id), out int sIdx))
                overrides[sIdx] = rubbleMaxY;
        }
        losCtx.SetStructureMaxYOverrides(overrides, overrides.Length);

        float eyeY = geometry.HeightMap.GetHeight(observer.X, observer.Y) + 2.3f;
        float tgtY = geometry.HeightMap.GetHeight(targetBehindPanelka.X, targetBehindPanelka.Y) + 2.3f;
        bool engineLosClear = geometry.IsLineOfSightClear(
            new NumericsVector3(observer.X, eyeY, observer.Y),
            new NumericsVector3(targetBehindPanelka.X, tgtY, targetBehindPanelka.Y),
            losCtx);
        Require(engineLosClear, "Engine.Core LineOfSightContext with rubble height overrides must also see over collapsed panelka.");
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

            // Warmup playback, then assert 0 steady-state bytes allocated on playback
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

            // Steady-state zero allocation check
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

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"BuildingDestructionRegressionTests failed: {message}");
    }
}
