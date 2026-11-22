using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

internal static class AirDefenseRegressionTests
{
    private static readonly ushort SamPresentationId =
        WeaponPresentation.MakeId(WeaponPresentationKind.SurfaceToAirMissile, 1);

    internal static void Run(Node3D scene)
    {
        CheckStrela10VisualAndRig(scene);
        CheckGuidedMissileFlareAndAirburstEffects(scene);
        CheckFlareContextPanelAndSelectionCard(scene);
        CheckStrela10EnemyContactPresentation(scene);
    }

    private static void CheckStrela10VisualAndRig(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            VehicleVisual? visual = VehicleVisualCatalog.InstantiateVisual("strela10", "soviet", initialCameraSize: 30f);
            Require(visual is not null, "visuals.json failed to instantiate strela10.");
            parent.AddChild(visual!.Root);

            Require(visual.LodCount == 3, $"Expected 3 LODs for strela10, got {visual.LodCount}.");

            Node3D lod0 = visual.GetOrCreateLod(0);
            Require(lod0.FindChild("TurretYaw", recursive: true, owned: false) is Node3D, "Strela-10 LOD0 missing TurretYaw.");
            Require(lod0.FindChild("GunPitch", recursive: true, owned: false) is Node3D, "Strela-10 LOD0 missing GunPitch.");
            for (int t = 1; t <= 4; t++)
            {
                string name = $"Launch_0{t}";
                Require(lod0.FindChild(name, recursive: true, owned: false) is Node3D, $"Strela-10 LOD0 missing {name}.");
            }
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckGuidedMissileFlareAndAirburstEffects(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = new(65, 65, 16f, 0.01f, new ushort[65 * 65]);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            UnitCatalog catalog = ColdWarContent.LoadCatalog();
            UnitViewSet views = new(parent, map, catalog, () => "us");

            // 1. Verify procedural audio streams for SAM, flares, and airburst
            Require(effects.SamLaunchVariants.Length == CombatEffects.SamLaunchVariantCount, "Missing SAM launch audio variants.");
            Require(effects.FlarePopVariants.Length == CombatEffects.FlarePopVariantCount, "Missing flare pop audio variants.");
            Require(effects.AirburstVariants.Length == CombatEffects.AirburstVariantCount, "Missing airburst audio variants.");
            for (int i = 0; i < CombatEffects.SamLaunchVariantCount; i++)
                Require(effects.SamLaunchVariants[i].Data.Length > 0, "Empty SAM launch audio data.");
            for (int i = 0; i < CombatEffects.FlarePopVariantCount; i++)
                Require(effects.FlarePopVariants[i].Data.Length > 0, "Empty flare pop audio data.");
            for (int i = 0; i < CombatEffects.AirburstVariantCount; i++)
                Require(effects.AirburstVariants[i].Data.Length > 0, "Empty airburst audio data.");

            // 2. Sync active replicated guided missiles from ClientReplica
            ClientReplica replica = new();
            byte[] buf = new byte[WireProtocol.MaxMessageSize];
            GuidedMissileObservation m1 = new(11, SamPresentationId, new NumericsVector3(400f, 35f, 800f), new NumericsVector3(0f, 15f, -140f), GuidedMissileFlags.MotorBurning);
            int written = WireProtocol.WriteSnapshot(buf, 1, 10, default, default, default, default, default, default, [m1]);
            Require(replica.TryApply(buf.AsSpan(0, written)), "Failed to apply guided missile snapshot.");

            effects.SyncGuidedMissiles(replica, 10.0, 0.05f);
            Require(effects.ActiveGuidedMissilesCount == 1, $"Expected 1 active guided missile, got {effects.ActiveGuidedMissilesCount}.");

            // 3. Process launch, flare burst, and airburst events
            CombatEventObservation launchEv = new(1001, 10, CombatEventKind.MissileLaunched, SamPresentationId, new NumericsVector3(400f, 5f, 950f), new NumericsVector3(400f, 55f, 500f));
            CombatEventObservation flareEv = new(1002, 11, CombatEventKind.FlaresDeployed, catalog.RequireDefinitionIndex("ch47d"), new NumericsVector3(400f, 55f, 500f), new NumericsVector3(35f, 0f, 0f));
            CombatEventObservation detEv = new(1003, 12, CombatEventKind.MissileDetonated, SamPresentationId, new NumericsVector3(400f, 55f, 510f), new NumericsVector3(400f, 55f, 500f), default, HitResult.Miss);

            effects.ProcessEvents([launchEv, flareEv, detEv], views, currentTick: 12);
            Require(effects.ActiveFlaresCount == FlareBallistics.FlaresPerBurst, $"Expected {FlareBallistics.FlaresPerBurst} active/queued flare decoys after salvo, got {effects.ActiveFlaresCount}.");

            effects.Update(0.25);
            effects.Clear();
            Require(effects.ActiveGuidedMissilesCount == 0, "Clear() must reset guided missiles.");
            Require(effects.ActiveFlaresCount == 0, "Clear() must reset flares.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckFlareContextPanelAndSelectionCard(Node3D scene)
    {
        Control parent = new();
        scene.AddChild(parent);
        try
        {
            UnitCatalog catalog = ColdWarContent.LoadCatalog();
            ushort ch47Def = catalog.RequireDefinitionIndex("ch47d");

            ContextActionPanel panel = new(catalog);
            SelectionCard card = new(catalog);
            parent.AddChild(panel);
            parent.AddChild(card);

            ClientReplica replica = new();
            byte[] buf = new byte[WireProtocol.MaxMessageSize];
            EntityId heliId = new(1);
            OwnUnitObservation heliObs = new(
                heliId,
                ch47Def,
                new NumericsVector2(500f, 500f),
                0,
                Health: 160,
                Ammunition: 1200,
                Altitude: 55f,
                FlightPhase: FlightPhase.Airborne,
                AltitudeBand: AltitudeBand.High,
                HasFlightCapability: true,
                FlareCharges: 8,
                FlareCooldownTicks: 0,
                CountermeasureFlags: CountermeasureFlags.HasFlares | CountermeasureFlags.AutoFlares | CountermeasureFlags.MissileWarning);

            int written = WireProtocol.WriteSnapshot(buf, 1, 20, [heliObs]);
            Require(replica.TryApply(buf.AsSpan(0, written)), "Failed to apply helicopter flare snapshot.");

            HashSet<EntityId> sel = [heliId];
            panel.UpdateContext(sel, replica);
            Require(panel.FlareButton.Visible, "FLARES button must be visible for CH-47D.");
            Require(!panel.FlareButton.Disabled, "FLARES button must be enabled when airborne with charges.");
            Require(panel.FlareButton.Text.Contains("FLARES [G]", StringComparison.Ordinal),
                $"Unexpected flare button text: '{panel.FlareButton.Text}'.");
            Require(panel.AutoFlareButton.Visible && panel.AutoFlareButton.Text == "AUTO: ON",
                $"Unexpected auto-flare button state: visible={panel.AutoFlareButton.Visible}, text='{panel.AutoFlareButton.Text}'.");

            card.UpdateSingleOwnUnit(heliObs);
            Require(card.Line1Text.Contains("MISSILE WARNING", StringComparison.Ordinal),
                $"SelectionCard should show MISSILE WARNING when flagged, got '{card.Line1Text}'.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckStrela10EnemyContactPresentation(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            UnitCatalog catalog = ColdWarContent.LoadCatalog();
            ushort strelaDef = catalog.RequireDefinitionIndex("strela10");
            ContactView contactView = new(() => "soviet", type => type < catalog.Units.Length ? catalog.Units[type].Id : null);
            parent.AddChild(contactView.Root);

            ContactObservation obs = new(
                new ContactId(55),
                ContactState.Current,
                ContactClassification.AntiAir,
                strelaDef,
                new NumericsVector2(960f, 1410f),
                LastSeenTick: 20,
                TurretYaw: 0.4f,
                HullHeading: 0f,
                GunPitchRad: 0.65f);

            contactView.Update(obs, groundY: 4f, isSelected: false);
            Require(contactView.HasEnemyModel, "Strela-10 enemy contact must render a 3D model.");
            Require(contactView.CurrentModelClassification == ContactClassification.AntiAir,
                $"Expected AntiAir classification, got {contactView.CurrentModelClassification}.");
            Require(contactView.TryGetNextLaunchTubeTransform(out _), "Strela-10 enemy contact must expose canister launch tubes.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"AirDefenseRegressionTests failed: {message}");
    }
}
