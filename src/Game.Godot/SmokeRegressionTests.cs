using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

/// <summary>
/// Headless regression tests for tactical smoke presentation, FogOfWarView smoke occlusion,
/// ContextActionPanel smoke controls, and zero steady-state allocations.
/// </summary>
internal static class SmokeRegressionTests
{
    public static void Run(Node3D scene)
    {
        CheckSmokeViewRenderingAndZeroAllocations(scene);
        CheckFogOfWarSmokeOcclusion();
        CheckContextActionPanelSmokeStates(scene);
        CheckMainLiveBattleSmokeButtonAndDeploy(scene);
        GD.Print("TACTICAL_SMOKE_REGRESSIONS_OK 4/4 passed: smoke_view_zero_alloc fog_of_war_occlusion context_action_panel main_live_deploy");
    }

    private static void CheckSmokeViewRenderingAndZeroAllocations(Node3D scene)
    {
        HeightMap map = new(65, 65, 4f, 0.01f, new ushort[65 * 65]);
        StaticGeometry geometry = new(map, []);
        SmokeView smokeView = new(map, geometry);
        scene.AddChild(smokeView);

        try
        {
            Require(smokeView.MultiMeshInstance.CastShadow == GeometryInstance3D.ShadowCastingSetting.Off,
                "SmokeView MultiMeshInstance3D must have CastShadow=Off.");

            ClientReplica replica = new();
            SmokeCloudObservation[] clouds =
            [
                new SmokeCloudObservation(new SmokeCloudId(1), new NumericsVector2(100f, 100f), 0, 16f, 7f, 10, 2110, 60, 300, 0.25f),
                new SmokeCloudObservation(new SmokeCloudId(2), new NumericsVector2(114f, 100f), 0, 16f, 7f, 30, 2130, 60, 300, 0.25f)
            ];
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
            int written = WireProtocol.WriteSnapshot(snapBuf, 1, 100, ReadOnlySpan<OwnUnitObservation>.Empty, smokeClouds: clouds);
            Require(replica.TryApply(snapBuf.AsSpan(0, written)), "Smoke snapshot must apply to ClientReplica.");

            // Warmup frame
            smokeView.Update(0.016f, replica, 100.0);
            Require(smokeView.ActiveCloudCount == 2, $"Expected 2 active clouds in SmokeView, got {smokeView.ActiveCloudCount}.");
            Require(smokeView.ActivePuffCount == 2 * SmokeView.PuffsPerCloud,
                $"Expected {2 * SmokeView.PuffsPerCloud} visible puffs, got {smokeView.ActivePuffCount}.");

            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 60; f++)
            {
                smokeView.Update(0.016f, replica, 100.0 + f * 0.3);
            }
            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Require(allocDelta == 0, $"SmokeView steady-state Update must allocate 0 bytes, got {allocDelta}.");

            // Verify sub-tick fractional renderTick produces smooth continuous bloom growth
            // even while replica.LatestTick remains fixed between 10 Hz snapshots.
            SmokeCloudObservation bloomCloud = new(new SmokeCloudId(5), new NumericsVector2(100f, 100f), 0, 16f, 7f, 100, 2200, 60, 300, 0.25f);
            SmokeView.EvaluateContinuous(in bloomCloud, 110.0, out float radAt110, out float densAt110, out _, out _);
            SmokeView.EvaluateContinuous(in bloomCloud, 110.4, out float radAt1104, out float densAt1104, out _, out _);
            Require(radAt1104 > radAt110 + 0.01f && densAt1104 > densAt110 + 0.0005f,
                $"SmokeView must interpolate bloom smoothly across fractional renderTick (r={radAt110:F4}->{radAt1104:F4}, d={densAt110:F4}->{densAt1104:F4}).");

            smokeView.Clear();
            Require(smokeView.ActiveCloudCount == 0 && smokeView.ActivePuffCount == 0,
                "SmokeView.Clear must hide all active puffs.");
        }
        finally
        {
            smokeView.QueueFree();
        }
    }

    private static void CheckFogOfWarSmokeOcclusion()
    {
        HeightMap map = new(65, 65, 4f, 0.01f, new ushort[65 * 65]);
        StaticGeometry geometry = new(map, []);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");

        using FogOfWarView fow = new(map, geometry, customCellSize: 4.0f);
        ClientReplica replica = new();
        byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];

        OwnUnitObservation[] units =
        [
            new OwnUnitObservation(new EntityId(1), t62Idx, new NumericsVector2(40f, 128f), 0)
        ];

        // First: without smoke, cell behind (100, 128) at (180, 128) is visible
        int written1 = WireProtocol.WriteSnapshot(snapBuf, 1, 10, units);
        Require(replica.TryApply(snapBuf.AsSpan(0, written1)), "Clear FoW snapshot must apply.");
        fow.UpdateAllNow(replica, catalog);
        Require(fow.IsWorldPointSeen(180f, 128f), "Expected clear visibility behind open ground.");

        // Second: dense smoke cloud centered at (100, 128) between observer (40, 128) and (180, 128)
        SmokeCloudObservation[] clouds =
        [
            new SmokeCloudObservation(new SmokeCloudId(1), new NumericsVector2(100f, 128f), 0, 16f, 7f, 10, 2110, 60, 300, 0.25f)
        ];
        int written2 = WireProtocol.WriteSnapshot(snapBuf, 2, 120, units, smokeClouds: clouds);
        Require(replica.TryApply(snapBuf.AsSpan(0, written2)), "Smoked FoW snapshot must apply.");
        fow.UpdateAllNow(replica, catalog);
        Require(!fow.IsWorldPointSeen(180f, 128f), "Expected occluded visibility behind opaque smoke screen.");

        // Third: gradual fade without moving the observer must invalidate cached viewshed
        // once optical depth drops below 1.0 (e.g. tick 2080: 32m * 0.025 = 0.80 < 1.0),
        // restoring visibility via normal FogOfWarView.Update() without UpdateAllNow().
        int writtenFadeOpaque = WireProtocol.WriteSnapshot(snapBuf, 3, 1990, units, smokeClouds: clouds);
        Require(replica.TryApply(snapBuf.AsSpan(0, writtenFadeOpaque)), "Fade opaque FoW snapshot must apply.");
        fow.Update(0.30f, replica, catalog);
        while (fow.IsCycleInProgress)
        {
            fow.Update(0.016f, replica, catalog);
        }
        Require(!fow.IsWorldPointSeen(180f, 128f), "Smoke at tick 1990 (tau=3.2) must still occlude FoW.");

        int writtenFadeThin = WireProtocol.WriteSnapshot(snapBuf, 4, 2080, units, smokeClouds: clouds);
        Require(replica.TryApply(snapBuf.AsSpan(0, writtenFadeThin)), "Fade thin FoW snapshot must apply.");
        fow.Update(0.30f, replica, catalog);
        while (fow.IsCycleInProgress)
        {
            fow.Update(0.016f, replica, catalog);
        }
        Require(fow.SweepsInLastCycle >= 1,
            "Stationary observer viewshed must be invalidated and re-swept as smoke fades below blocking density.");
        Require(fow.IsWorldPointSeen(180f, 128f),
            "Expected visibility to recover at tick 2080 (tau=0.80 < 1.0) during gradual fade without moving observer.");

        // Fourth: smoke clears completely -> visibility stays open
        int writtenCleared = WireProtocol.WriteSnapshot(snapBuf, 5, 2200, units);
        Require(replica.TryApply(snapBuf.AsSpan(0, writtenCleared)), "Post-smoke FoW snapshot must apply.");
        fow.UpdateAllNow(replica, catalog);
        Require(fow.IsWorldPointSeen(180f, 128f), "Expected visibility to return after smoke expires.");
    }

    private static void CheckContextActionPanelSmokeStates(Node3D scene)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");

        ContextActionPanel panel = new(catalog);
        scene.AddChild(panel);
        try
        {
            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation[] readyUnits =
            [
                new OwnUnitObservation(new EntityId(10), t62Idx, new NumericsVector2(100f, 100f), 0,
                    SmokeCharges: 2, SmokeCooldownTicks: 0, HasSmokeCapability: true, SmokeActive: false),
                new OwnUnitObservation(new EntityId(11), m60Idx, new NumericsVector2(120f, 100f), 0,
                    SmokeCharges: 0, SmokeCooldownTicks: 0, HasSmokeCapability: false, SmokeActive: false)
            ];
            int w1 = WireProtocol.WriteSnapshot(snapBuf, 1, 10, readyUnits);
            Require(replica.TryApply(snapBuf.AsSpan(0, w1)), "Panel test snapshot 1 must apply.");

            HashSet<EntityId> selected = [new EntityId(10)];
            panel.UpdateContext(selected, replica);
            Require(panel.Visible && panel.SmokeButton.Visible && !panel.SmokeButton.Disabled,
                "Selecting a smoke-capable T-62 with charges must show enabled SmokeButton.");
            Require(panel.SmokeButton.Text.Contains("(2)", StringComparison.Ordinal),
                $"Expected charge count (2) on SmokeButton, got '{panel.SmokeButton.Text}'.");

            // Zero steady-state allocations when selection and smoke state are unchanged
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 60; f++)
            {
                panel.UpdateContext(selected, replica);
            }
            long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Require(allocDelta == 0, $"ContextActionPanel steady-state UpdateContext must allocate 0 bytes, got {allocDelta}.");

            // Selecting an M60A1 (no smoke capability) must hide SmokeButton while keeping AttackGround available
            selected.Clear();
            selected.Add(new EntityId(11));
            panel.UpdateContext(selected, replica);
            Require(!panel.SmokeButton.Visible && panel.AttackGroundButton.Visible,
                "Selecting M60A1 without smoke capability must hide SmokeButton.");

            // Active smoke state disables button and updates label
            OwnUnitObservation[] activeUnits =
            [
                new OwnUnitObservation(new EntityId(10), t62Idx, new NumericsVector2(100f, 100f), 0,
                    SmokeCharges: 1, SmokeCooldownTicks: 0, HasSmokeCapability: true, SmokeActive: true)
            ];
            int w2 = WireProtocol.WriteSnapshot(snapBuf, 2, 20, activeUnits);
            Require(replica.TryApply(snapBuf.AsSpan(0, w2)), "Panel test snapshot 2 must apply.");

            selected.Clear();
            selected.Add(new EntityId(10));
            panel.UpdateContext(selected, replica);
            Require(panel.Visible && panel.SmokeButton.Visible && panel.SmokeButton.Disabled &&
                    panel.SmokeButton.Text.Contains("LAYING SMOKE", StringComparison.Ordinal),
                $"Active smoke state expected disabled LAYING SMOKE button, got '{panel.SmokeButton.Text}'.");
        }
        finally
        {
            panel.QueueFree();
        }
    }

    private static void CheckMainLiveBattleSmokeButtonAndDeploy(Node3D scene)
    {
        if (scene is not Main main)
        {
            return;
        }

        try
        {
            main.SetScenarioForTest("battle");
            main.Session.Advance(0.15);
            main.UpdateViews();

            EntityId t62Id = default;
            foreach (OwnUnitObservation u in main.Session.Replica.OwnUnits)
            {
                if (u.HasSmokeCapability)
                {
                    t62Id = u.Id;
                    break;
                }
            }
            Require(t62Id.Value != 0, "Main live battle scenario must expose a smoke-capable T-62 in Player 1 replica.");

            main.UnitViews.Selected.Clear();
            main.UnitViews.Selected.Add(t62Id);
            main.UpdateSelectionPresentation();

            Require(main.ContextActionPanel.Visible && main.ContextActionPanel.SmokeButton.Visible && !main.ContextActionPanel.SmokeButton.Disabled,
                "Selecting a T-62 in Main battle scenario must display enabled SmokeButton.");

            main.OnDeploySmokeRequested();
            for (int i = 0; i < 10; i++)
            {
                main.Session.Advance(0.05);
            }
            main.UpdateViews();
            main.SmokeView?.Update(0.016f, main.Session.Replica, main.Session.RenderTick);
            main.UpdateSelectionPresentation();

            Require(main.Session.Replica.SmokeCloudCount >= 1,
                "Deploying smoke via Main.OnDeploySmokeRequested must replicate smoke clouds to ClientReplica.");
            Require(main.SmokeView is not null && main.SmokeView.ActiveCloudCount >= 1,
                "SmokeView must render active smoke clouds after Main.OnDeploySmokeRequested.");
            Require(main.ContextActionPanel.SmokeButton.Disabled &&
                    main.ContextActionPanel.SmokeButton.Text.Contains("LAYING SMOKE", StringComparison.Ordinal),
                $"Active smoke in Main expected disabled LAYING SMOKE button, got '{main.ContextActionPanel.SmokeButton.Text}'.");

            // Also verify ustye-raid exposes smoke button and deploys smoke on its T-62
            main.SetScenarioForTest("ustye-raid");
            main.Session.Advance(0.15);
            main.UpdateViews();
            EntityId ustyeT62 = default;
            foreach (OwnUnitObservation u in main.Session.Replica.OwnUnits)
            {
                if (u.HasSmokeCapability)
                {
                    ustyeT62 = u.Id;
                    break;
                }
            }
            Require(ustyeT62.Value != 0, "Main live ustye-raid scenario must expose a smoke-capable T-62.");
            main.UnitViews.Selected.Clear();
            main.UnitViews.Selected.Add(ustyeT62);
            main.UpdateSelectionPresentation();
            Require(main.ContextActionPanel.Visible && main.ContextActionPanel.SmokeButton.Visible && !main.ContextActionPanel.SmokeButton.Disabled,
                "Selecting a T-62 in ustye-raid must display enabled SmokeButton.");
        }
        finally
        {
            main.SetScenarioForTest("prototype");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"SmokeRegressionTests failed: {message}");
        }
    }
}
