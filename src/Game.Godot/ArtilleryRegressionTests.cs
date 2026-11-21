using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

internal static class ArtilleryRegressionTests
{
    private static readonly ushort RocketPresentationId =
        WeaponPresentation.MakeId(WeaponPresentationKind.RocketArtillery, 1);

    internal static void Run(Node3D scene)
    {
        CheckBm21ModelRigAndLaunchTubes(scene);
        CheckRocketAndArtilleryImpactEffectsZeroAlloc(scene);
        CheckArtilleryAudioStreams();
        CheckArtilleryContextActionPanelZeroAlloc(scene);
        CheckArtilleryContactPresentation(scene);
    }

    private static void CheckBm21ModelRigAndLaunchTubes(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            UnitView grad = new("bm21", faction: "soviet", classification: "artillery", initialCameraSize: 40f);
            parent.AddChild(grad.Root);

            Require(grad.HasVehicleModel, "BM-21 Grad must load its authored 3D model.");
            Require(grad.LodCount == 3, $"BM-21 Grad should have 3 LODs, got {grad.LodCount}.");
            Require(grad.GunPitchNode is not null, "BM-21 Grad LOD0 must expose GunPitch node.");
            Require(grad.LaunchTubeCount == 40, $"BM-21 Grad LOD0 must expose 40 launch tubes, got {grad.LaunchTubeCount}.");

            float pitchRad = Mathf.DegToRad(32f);
            grad.SetGunPitch(pitchRad);
            Require(MathF.Abs(grad.GunPitchNode!.Rotation.X - pitchRad) < 0.001f,
                "SetGunPitch must elevate BM-21 GunPitch node around local +X.");

            // Verify balanced ripple sequence yields distinct tube transforms
            Require(grad.TryGetNextLaunchTubeTransform(out Transform3D t1), "First launch tube transform missing.");
            Require(grad.TryGetNextLaunchTubeTransform(out Transform3D t2), "Second launch tube transform missing.");
            Require(t1.Origin.DistanceTo(t2.Origin) > 0.3f,
                "Balanced left-right ripple sequence must alternate across opposite sides of the rocket rack.");

            // Verify LOD switching preserves GunPitch elevation
            grad.UpdateLod(UnitView.Lod1CameraSize + 5f);
            Require(grad.ActiveLod == 1, $"Expected LOD1 at medium camera size, got {grad.ActiveLod}.");
            Require(grad.GunPitchNode is not null && MathF.Abs(grad.GunPitchNode.Rotation.X - pitchRad) < 0.001f,
                "BM-21 LOD1 must inherit current GunPitch elevation.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckRocketAndArtilleryImpactEffectsZeroAlloc(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitCatalog catalog = ColdWarContent.LoadCatalog();
            UnitViewSet unitViews = new(parent, map, catalog);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            ushort bm21Def = catalog.RequireDefinitionIndex("bm21");
            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation gradObs = new(
                new EntityId(101),
                bm21Def,
                new NumericsVector2(20f, 20f),
                0,
                Health: 40,
                Ammunition: 40,
                GunPitchRad: Mathf.DegToRad(32f),
                ArtilleryFlags: ArtilleryFlags.Firing);
            int len = WireProtocol.WriteSnapshot(snapBuf, 1, 10, [gradObs]);
            Require(replica.TryApply(snapBuf.AsSpan(0, len)), "Failed to apply BM-21 snapshot.");
            unitViews.Sync(replica, 10);

            CombatEventObservation launchEvent = new(
                EventId: 8001,
                ServerTick: 10,
                Kind: CombatEventKind.RocketLaunched,
                PresentationId: RocketPresentationId,
                Origin: new NumericsVector3(20f, 3.5f, 20f),
                Target: new NumericsVector3(20f, 1.0f, 1220f),
                ShooterUnitId: new EntityId(101));

            effects.ProcessEvents([launchEvent], unitViews, currentTick: 10);
            Require(effects.ActiveRocketsCount == 1, $"Expected 1 active rocket after launch event, got {effects.ActiveRocketsCount}.");
            Require(effects.ActiveSmokePuffsCount > 0, "Rocket launch must emit launch/backblast smoke puffs.");

            // Warm up steady-state update loop
            effects.Update(0.05);
            effects.Update(0.05);

            // Verify zero steady-state allocations during rocket flight & trail updates
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int f = 0; f < 30; f++)
            {
                effects.Update(0.016);
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(alloc == 0L, $"Rocket flight steady-state Update allocated {alloc} bytes.");

            // Process artillery impact event
            CombatEventObservation impactEvent = new(
                EventId: 8002,
                ServerTick: 160,
                Kind: CombatEventKind.ArtilleryImpact,
                PresentationId: RocketPresentationId,
                Origin: new NumericsVector3(20f, 3.5f, 20f),
                Target: new NumericsVector3(20f, 1.0f, 1220f));

            effects.ProcessEvents([impactEvent], unitViews, currentTick: 160);
            Require(effects.ActiveExplosionsCount > 0, "Artillery impact must spawn HE fireball.");
            Require(effects.ActiveShockwavesCount > 0, "Artillery impact must spawn ground shockwave ring.");
            Require(effects.ActiveArtilleryPlumesCount > 0, "Artillery impact must spawn rising dirt/smoke plumes.");
            Require(effects.ActiveArtilleryCratersCount > 0, "Artillery impact must spawn persistent scorched crater decal.");

            effects.Clear();
            Require(effects.ActiveEffectsCount == 0, "CombatEffects.Clear must reset all artillery effects.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckArtilleryAudioStreams()
    {
        float[] launchSamples = CannonSound.RenderRocketLaunch(4000);
        Require(launchSamples.Length > 40000, "Rocket launch sample buffer too short.");
        float launchPeak = 0f;
        for (int i = 0; i < launchSamples.Length; i++)
        {
            Require(float.IsFinite(launchSamples[i]), "Rocket launch sample contains non-finite value.");
            launchPeak = MathF.Max(launchPeak, MathF.Abs(launchSamples[i]));
        }
        Require(launchPeak > 0.5f, $"Rocket launch audio peak too quiet: {launchPeak:F3}.");

        float[] impactSamples = CannonSound.RenderArtilleryImpact(5000);
        Require(impactSamples.Length > 80000, "Artillery impact sample buffer too short.");
        float impactPeak = 0f;
        for (int i = 0; i < impactSamples.Length; i++)
        {
            Require(float.IsFinite(impactSamples[i]), "Artillery impact sample contains non-finite value.");
            impactPeak = MathF.Max(impactPeak, MathF.Abs(impactSamples[i]));
        }
        Require(impactPeak > 0.5f, $"Artillery impact audio peak too quiet: {impactPeak:F3}.");

        float[] incomingSamples = CannonSound.RenderRocketIncoming(6000);
        Require(incomingSamples.Length > 35000, "Rocket incoming whoosh buffer too short.");
        float incomingPeak = 0f;
        for (int i = 0; i < incomingSamples.Length; i++)
        {
            Require(float.IsFinite(incomingSamples[i]), "Rocket incoming sample contains non-finite value.");
            incomingPeak = MathF.Max(incomingPeak, MathF.Abs(incomingSamples[i]));
        }
        Require(incomingPeak > 0.3f, $"Rocket incoming audio peak too quiet: {incomingPeak:F3}.");
    }

    private static void CheckArtilleryContextActionPanelZeroAlloc(Node3D scene)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        ushort bm21Def = catalog.RequireDefinitionIndex("bm21");
        ContextActionPanel panel = new(catalog);
        scene.AddChild(panel);
        try
        {
            ClientReplica replica = new();
            byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
            EntityId gradId = new(501);
            OwnUnitObservation readyObs = new(gradId, bm21Def, new NumericsVector2(100f, 100f), 0, Health: 40, Ammunition: 40);
            int len = WireProtocol.WriteSnapshot(snapBuf, 1, 10, [readyObs]);
            Require(replica.TryApply(snapBuf.AsSpan(0, len)), "Failed to apply ready BM-21 snapshot.");

            HashSet<EntityId> selected = [gradId];
            panel.UpdateContext(selected, replica);
            Require(panel.Visible, "ContextActionPanel must be visible for BM-21 Grad.");
            Require(panel.FireMissionButton.Visible, "FIRE BARRAGE button must be visible for BM-21 Grad.");
            Require(!panel.FireMissionButton.Disabled, "FIRE BARRAGE button must be enabled when rockets are available.");
            Require(panel.FireMissionButton.Text.Contains("FIRE BARRAGE [B]", StringComparison.Ordinal),
                $"Unexpected barrage button text: '{panel.FireMissionButton.Text}'.");

            // Steady-state zero allocation check
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 60; i++)
            {
                panel.UpdateContext(selected, replica);
            }
            long alloc = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(alloc == 0L, $"ContextActionPanel steady-state update for artillery allocated {alloc} bytes.");

            // Active firing state switches button to STOP BARRAGE [B]
            OwnUnitObservation firingObs = readyObs with { ArtilleryFlags = ArtilleryFlags.Firing };
            len = WireProtocol.WriteSnapshot(snapBuf, 2, 12, [firingObs]);
            Require(replica.TryApply(snapBuf.AsSpan(0, len)), "Failed to apply firing BM-21 snapshot.");
            panel.UpdateContext(selected, replica);
            Require(panel.FireMissionButton.Text.Contains("STOP BARRAGE", StringComparison.Ordinal),
                $"Expected STOP BARRAGE button text while firing, got '{panel.FireMissionButton.Text}'.");
        }
        finally
        {
            panel.QueueFree();
        }
    }

    private static void CheckArtilleryContactPresentation(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            UnitCatalog catalog = ColdWarContent.LoadCatalog();
            ushort bm21Def = catalog.RequireDefinitionIndex("bm21");
            ContactView contactView = new(
                hostileFaction: () => "soviet",
                unitIdProvider: def => def < catalog.Units.Length ? catalog.Units[def].Id : null);
            parent.AddChild(contactView.Root);

            ContactObservation obs = new(
                new ContactId(11),
                ContactState.Current,
                ContactClassification.Artillery,
                bm21Def,
                new NumericsVector2(100f, 100f),
                10,
                GunPitchRad: Mathf.DegToRad(32f));
            contactView.Update(obs, groundY: 2f, isSelected: false);

            Require(contactView.HasEnemyModel, "Artillery contact must render a live 3D BM-21 model.");
            Require(contactView.CurrentModelClassification == ContactClassification.Artillery,
                "Artillery contact view must preserve Artillery classification.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"ARTILLERY_REGRESSION_FAILED: {message}");
    }
}
