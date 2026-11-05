using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

internal static class VisualPrewarmRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckPrewarmLoadsFactionsAndLods();
        CheckDuplicatePrewarmLoadsZeroScenes();
        CheckCacheBeforeContactPresentation();
        CheckFogCalculationSuspensionToggle();
        CheckContactChurnNoLingeringNodes(scene);
        CheckScenarioResetClearsVisuals(scene);
        GD.Print("VISUAL_PREWARM_REGRESSIONS_OK 6/6 passed: prewarm_factions repeat_prewarm cache_before_contact fog_suspension contact_churn scenario_reset");
    }

    private static void CheckPrewarmLoadsFactionsAndLods()
    {
        MatchSetup setup = UstyeRaidScenario.CreateMatchSetup();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();

        VehicleVisualCatalog.PrewarmResult result = VehicleVisualCatalog.PrewarmScenario(setup, catalog);
        Require(result.TotalPaths >= 14, $"Prewarm must register at least 14 scenes, got {result.TotalPaths}.");

        // Verify Soviet vehicle models and LODs
        Require(VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/T62Alt/t62alt.glb"),
            "Soviet T-62 LOD0 scene must be loaded in cache.");
        Require(VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/T62Alt/t62alt_lod1.glb"),
            "Soviet T-62 LOD1 scene must be loaded in cache.");
        Require(VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/T62Alt/t62alt_lod2.glb"),
            "Soviet T-62 LOD2 scene must be loaded in cache.");
        Require(VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/BRDM2/brdm2.glb"),
            "Soviet BRDM-2 scene must be loaded in cache.");

        // Verify US vehicle models and LODs
        Require(VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/M60/m60.glb"),
            "US M60 LOD0 scene must be loaded in cache.");
        Require(VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/M60/m60_lod1.glb"),
            "US M60 LOD1 scene must be loaded in cache.");
        Require(VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/M60/m60_lod2.glb"),
            "US M60 LOD2 scene must be loaded in cache.");

        // Verify Fallback scenes
        Require(VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/tank.glb"),
            "Tank fallback scene must be loaded in cache.");
        Require(VehicleVisualCatalog.IsSceneLoaded("res://Assets/Vehicles/recon.glb"),
            "Recon fallback scene must be loaded in cache.");

        // Verify faction vehicle lookup helpers
        Require(VehicleVisualCatalog.AreVisualsLoaded("m60a1", "us"),
            "AreVisualsLoaded for US m60a1 must be true.");
        Require(VehicleVisualCatalog.AreVisualsLoaded("t62", "soviet"),
            "AreVisualsLoaded for Soviet t62 must be true.");
    }

    private static void CheckDuplicatePrewarmLoadsZeroScenes()
    {
        MatchSetup setup = UstyeRaidScenario.CreateMatchSetup();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();

        VehicleVisualCatalog.PrewarmResult result = VehicleVisualCatalog.PrewarmScenario(setup, catalog);
        Require(result.NewlyLoadedCount == 0,
            $"Repeat call to PrewarmScenario must load 0 new scenes, got {result.NewlyLoadedCount}.");
        Require(result.TotalPaths >= 14,
            $"Repeat call to PrewarmScenario must maintain total scenes >= 14, got {result.TotalPaths}.");
    }

    private static void CheckCacheBeforeContactPresentation()
    {
        // Visual assets must already reside in cache before any contact view is instantiated.
        Require(VehicleVisualCatalog.AreVisualsLoaded("m60a1", "us"),
            "M60 visual assets must reside in cache prior to contact presentation.");
        Require(VehicleVisualCatalog.AreVisualsLoaded("m151a2", "us"),
            "M151 visual assets must reside in cache prior to contact presentation.");
        Require(VehicleVisualCatalog.AreVisualsLoaded("brdm2", "soviet"),
            "BRDM-2 visual assets must reside in cache prior to contact presentation.");
    }

    private static void CheckFogCalculationSuspensionToggle()
    {
        int size = 65;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
        using FogOfWarView fog = new(map, null);

        Require(!fog.IsCalculationSuspended,
            "Fog calculation must not be suspended by default.");

        fog.IsCalculationSuspended = true;
        Require(fog.IsCalculationSuspended,
            "IsCalculationSuspended must reflect assigned state true.");

        ClientReplica replica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];
        OwnUnitObservation unit = new(new EntityId(1), 0, new NumericsVector2(10, 10), 0);
        int writeSize = WireProtocol.WriteSnapshot(bytes, 1, 10, [unit], []);
        replica.TryApply(bytes.AsSpan(0, writeSize));
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        // While suspended, Update must skip calculation completely
        fog.Update(0.1f, replica, catalog);
        Require(!fog.IsCycleInProgress,
            "Suspended fog must not start or execute calculation cycle.");
        Require(fog.SweepsInLastCycle == 0,
            "Suspended fog must perform zero sweeps.");

        // Resuming calculation
        fog.IsCalculationSuspended = false;
        Require(!fog.IsCalculationSuspended,
            "IsCalculationSuspended must reflect assigned state false.");
        fog.Update(FogOfWarView.CycleInterval, replica, catalog);
    }

    private static void CheckContactChurnNoLingeringNodes(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            ContactViewSet contactViews = new(parent, map);
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            for (int i = 0; i < 5; i++)
            {
                int tickAppear = (i + 1) * 20;
                ContactObservation contact = new(
                    new ContactId(101),
                    ContactState.Current,
                    ContactClassification.Tank,
                    0,
                    new NumericsVector2(20, 20),
                    tickAppear);

                // Contact appears
                int sizeAppear = WireProtocol.WriteSnapshot(bytes, (uint)(i * 2 + 1), tickAppear, [], [contact]);
                bool appliedAppear = replica.TryApply(bytes.AsSpan(0, sizeAppear));
                Require(appliedAppear, $"Snapshot appear was rejected on iteration {i}.");
                contactViews.Sync(replica);

                Require(contactViews.Views.Count == 1,
                    $"Contact view must exist on churn iteration {i}.");
                Require(parent.GetChildCount() == 1,
                    $"Parent must have exactly 1 child node on churn iteration {i}.");

                // Contact disappears
                int tickDisappear = tickAppear + 10;
                int sizeDisappear = WireProtocol.WriteSnapshot(bytes, (uint)(i * 2 + 2), tickDisappear, [], []);
                bool appliedDisappear = replica.TryApply(bytes.AsSpan(0, sizeDisappear));
                Require(appliedDisappear, $"Snapshot disappear was rejected on iteration {i}.");
                contactViews.Sync(replica);

                Require(contactViews.Views.Count == 0,
                    $"Contact view must be removed on churn iteration {i}.");
                Require(parent.GetChildCount() == 0,
                    $"Parent child count must return to 0 with no lingering nodes on churn iteration {i}.");
            }
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckScenarioResetClearsVisuals(Node3D scene)
    {
        // Standalone verification: UnitViewSet and ContactViewSet Clear() cleans up all nodes
        Node3D standaloneParent = new();
        scene.AddChild(standaloneParent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(standaloneParent, map);
            ContactViewSet contactViews = new(standaloneParent, map);
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation u1 = new(new EntityId(1), 0, new NumericsVector2(10, 10), 0);
            OwnUnitObservation u2 = new(new EntityId(2), 0, new NumericsVector2(20, 20), 0);
            ContactObservation c1 = new(new ContactId(99), ContactState.Current, ContactClassification.Tank, 0, new NumericsVector2(30, 30), 10);

            int size = WireProtocol.WriteSnapshot(bytes, 1, 10, [u1, u2], [c1]);
            replica.TryApply(bytes.AsSpan(0, size));
            unitViews.Sync(replica, 10);
            contactViews.Sync(replica);

            Require(unitViews.Views.Count == 2, "Unit views should have 2 entries.");
            Require(contactViews.Views.Count == 1, "Contact views should have 1 entry.");
            Require(standaloneParent.GetChildCount() == 3, "Parent should have 3 visual child nodes.");

            unitViews.Clear();
            contactViews.Clear();

            Require(unitViews.Views.Count == 0, "Unit views dictionary must be empty after Clear().");
            Require(contactViews.Views.Count == 0, "Contact views dictionary must be empty after Clear().");
            Require(standaloneParent.GetChildCount() == 0,
                "Parent child count must be 0 after Clear() with no hanging nodes.");
        }
        finally
        {
            standaloneParent.QueueFree();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
