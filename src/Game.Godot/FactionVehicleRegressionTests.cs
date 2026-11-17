using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

// Faction vehicle presentation: model choice, LOD switching, track motion and imported materials.
internal static class FactionVehicleRegressionTests
{
    private const string WeatheringShader = "res://Assets/Vehicles/Shared/vehicle_weathering.gdshader";
    private const string MotionShader = "res://Assets/Vehicles/Shared/vehicle_weathering_motion.gdshader";

    internal static void Run(Node3D scene)
    {
        CheckFactionModels(scene);
        CheckLodSwitching(scene);
        CheckLodInitialDetailAndHysteresis(scene);
        CheckEnemyContactLodLifecycle(scene);
        CheckLastKnownContactDefersLodUntilResighted(scene);
        CheckWreckInitialLodDetail(scene);
        CheckViewSetsSpawnDirectLodOnFirstSync(scene);
        CheckLodInstantiationBudget100Views(scene);
        CheckAnimationHandoffOnLodSwitch(scene);
        CheckTrackTravel(scene);
        CheckImportedMaterials();
        CheckEnemyTankFaction();
        CheckEnemyContactFactions();
    }

    private static void CheckFactionModels(Node3D scene)
    {
        UnitView soviet = new("prototype_tank", "soviet");
        UnitView us = new("prototype_tank", "us");
        UnitView none = new("prototype_tank");
        UnitView unknown = new("prototype_tank", "unknown_faction");
        UnitView sovietApc = new("apc", "soviet");
        UnitView usApc = new("apc", "us");
        UnitView sovietRecon = new("recon_vehicle", "soviet");
        try
        {
            Require(soviet.LodCount == 3 && soviet.GetLod(0)!.SceneFilePath.EndsWith("T62Alt/t62alt.glb", StringComparison.Ordinal),
                "Soviet tank did not use the T-62 with three LODs.");
            Require(us.LodCount == 3 && us.GetLod(0)!.SceneFilePath.EndsWith("M60/m60.glb", StringComparison.Ordinal),
                "US tank did not use the M60 with three LODs.");
            Require(none.LodCount == 1 && none.GetLod(0)!.SceneFilePath.EndsWith("Vehicles/tank.glb", StringComparison.Ordinal),
                "A match without factions must keep the prototype tank.");
            Require(unknown.GetLod(0)!.SceneFilePath.EndsWith("Vehicles/tank.glb", StringComparison.Ordinal),
                "An unknown faction must fall back to the base scene.");
            Require(soviet.Model.FindChild("TurretYaw", true, false) is not null, "Faction tank lost its turret pivot.");

            Require(sovietApc.LodCount == 3 && sovietApc.GetLod(0)!.SceneFilePath.EndsWith("BTR70/btr70.glb", StringComparison.Ordinal),
                "Soviet APC did not use the BTR-70 with three LODs.");
            Require(usApc.LodCount == 1 && usApc.GetLod(0)!.SceneFilePath.EndsWith("Vehicles/recon.glb", StringComparison.Ordinal),
                "US APC must keep the placeholder until its own model exists.");
            Require(sovietRecon.GetLod(0)!.SceneFilePath.EndsWith("Vehicles/recon.glb", StringComparison.Ordinal),
                "The BTR-70 is an APC; Soviet recon must not use it.");
            Require(sovietApc.LeftMotionMeshCount > 0 && sovietApc.RightMotionMeshCount > 0,
                "BTR-70 wheel motion meshes were not found.");
            Require(sovietApc.Model.FindChild("TurretYaw", true, false) is not null, "BTR-70 lost its turret pivot.");
        }
        finally
        {
            soviet.Root.Free();
            us.Root.Free();
            none.Root.Free();
            unknown.Root.Free();
            sovietApc.Root.Free();
            usApc.Root.Free();
            sovietRecon.Root.Free();
        }
    }

    private static void CheckLodSwitching(Node3D scene)
    {
        UnitView view = new("prototype_tank", "soviet");
        scene.AddChild(view.Root);
        try
        {
            Require(view.ActiveLod == 0 && view.GetLod(0)!.Visible, "LOD0 must start visible.");
            Require(!view.IsLodLoaded(1) && !view.IsLodLoaded(2), "Unused LODs must not be instantiated up front.");
            view.UpdatePosition(new Vector3(0, 0, 0));
            view.UpdatePosition(new Vector3(0, 0, -5));
            view.AimAt(new Vector3(10, 0, -5));
            view.ApplyRecoil(Vector3.Forward);
            view.UpdateLod(75f);
            Require(view.ActiveLod == 1 && view.GetLod(1)!.Visible && !view.GetLod(0)!.Visible, "RTS zoom must show LOD1.");
            Require(!view.IsLodLoaded(2), "LOD2 must wait until the distant zoom.");
            view.UpdateLod(160f);
            Require(view.ActiveLod == 2 && view.GetLod(2)!.Visible && !view.GetLod(1)!.Visible, "Distant zoom must show LOD2.");
            foreach (int lod in new[] { 0, 1, 2 })
            {
                Node3D turret = (Node3D)view.GetLod(lod)!.FindChild("TurretYaw", true, false)!;
                Require(Mathf.IsEqualApprox(turret.Rotation.Y, -Mathf.Pi / 2), $"LOD{lod} turret did not take the current aim.");
                Node3D barrel = (Node3D)view.GetLod(lod)!.FindChild("BarrelRecoil", true, false)!;
                Require(barrel.Position.Z > 0.3f, $"LOD{lod} barrel did not take the current recoil.");
            }
            view.UpdateLod(38f);
            Require(view.ActiveLod == 0 && view.GetLod(0)!.Visible && !view.GetLod(2)!.Visible, "Close zoom must return to LOD0.");
            Require(view.TryGetMuzzlePosition(out Vector3 muzzle) && muzzle.Z < -5, "Muzzle must come from the visible LOD.");
        }
        finally { view.Root.Free(); }
    }

    private static void CheckTrackTravel(Node3D scene)
    {
        UnitView view = new("prototype_tank", "us");
        scene.AddChild(view.Root);
        try
        {
            Require(view.LeftMotionMeshCount > 0 && view.RightMotionMeshCount > 0, "Track motion meshes were not found.");
            view.SetHullHeading(0f); // the spawn heading is not a turn
            Require(view.LeftTrackTravel == 0f && view.RightTrackTravel == 0f, "The spawn heading must not move the tracks.");
            view.UpdatePosition(new Vector3(0, 0, 0));
            view.UpdatePosition(new Vector3(0, 0, -3));
            // 3 world meters at visual scale 0.75 is 4 model meters on both sides.
            Require(Mathf.IsEqualApprox(view.LeftTrackTravel, 4f) && Mathf.IsEqualApprox(view.RightTrackTravel, 4f),
                "Straight driving must move both tracks by the same model distance.");
            Require(Mathf.IsEqualApprox(view.GetShaderTrackTravel(left: true), 4f), "Track travel did not reach the shader.");
            // A gentle left turn: the right track runs farther than the left one.
            view.SetHullHeading(0.1f);
            view.UpdatePosition(new Vector3(-0.3f, 0, -6));
            Require(view.RightTrackTravel - view.LeftTrackTravel > 0.05f, "A left turn must drive the right track farther.");
            float before = view.LeftTrackTravel + view.RightTrackTravel;
            float differentialBefore = view.RightTrackTravel - view.LeftTrackTravel;
            // A 180 degree heading snap is clamped to 0.5 m per side instead of spinning the tracks.
            view.SetHullHeading(0.1f - Mathf.Pi);
            view.UpdatePosition(new Vector3(-0.3f, 0, -5.9f));
            float differentialChange = Mathf.Abs(view.RightTrackTravel - view.LeftTrackTravel - differentialBefore);
            Require(differentialChange <= 1.0001f, "Heading snaps must not spin the tracks.");
            Require(view.LeftTrackTravel + view.RightTrackTravel > before, "Tracks must keep moving forward along the path.");
            view.UpdateLod(75f);
            Require(Mathf.IsEqualApprox(view.GetShaderTrackTravel(left: false), view.RightTrackTravel),
                "Switching LOD must carry the track travel over.");
            // Long drives wrap at the exported period (993.18 m for the M60) instead of growing without bound.
            view.UpdatePosition(new Vector3(-0.3f, 0, -6.0f - 800f));
            Require(view.LeftTrackTravel >= 0f && view.LeftTrackTravel < 993.2f && view.RightTrackTravel < 993.2f,
                "Track travel must wrap at the exported period.");
        }
        finally { view.Root.Free(); }
    }

    private static void CheckImportedMaterials()
    {
        foreach ((string path, bool camo) in new[]
        {
            ("res://Assets/Vehicles/T62Alt/t62alt.glb", false),
            ("res://Assets/Vehicles/M60/m60.glb", true),
            ("res://Assets/Vehicles/BTR70/btr70.glb", false)
        })
        {
            Node3D model = GD.Load<PackedScene>(path).Instantiate<Node3D>();
            try
            {
                bool foundPaint = false;
                bool foundMarkings = false;
                bool foundMotion = false;
                foreach (Node node in model.FindChildren("*", "MeshInstance3D", recursive: true, owned: false))
                {
                    Mesh mesh = ((MeshInstance3D)node).Mesh;
                    bool motion = node.Name.ToString().Contains("_Motion_", StringComparison.Ordinal);
                    for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                    {
                        // Only running gear reserves the per-instance travel parameter.
                        if (mesh.SurfaceGetMaterial(surface) is ShaderMaterial weathered)
                        {
                            Require(weathered.Shader.ResourcePath == (motion ? MotionShader : WeatheringShader),
                                $"{path} {node.Name} uses the wrong weathering shader variant.");
                            foundMotion |= motion;
                        }
                    }
                    for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                    {
                        Material material = mesh.SurfaceGetMaterial(surface);
                        if (material.ResourceName.EndsWith("_Paint", StringComparison.Ordinal))
                        {
                            ShaderMaterial? shader = material as ShaderMaterial;
                            Require(shader is not null, $"{path} paint is not on the weathering shader.");
                            float camoStrength = shader!.GetShaderParameter("camo_strength").AsSingle();
                            Require(camo ? camoStrength == 1f : camoStrength == 0f, $"{path} has the wrong camouflage setting.");
                            foundPaint = true;
                        }
                        if (material.ResourceName.EndsWith("_Markings", StringComparison.Ordinal))
                        {
                            Require(material is StandardMaterial3D { AlbedoTexture: not null }, $"{path} markings lost their texture.");
                            foundMarkings = true;
                        }
                    }
                }
                Require(foundPaint && foundMarkings && foundMotion, $"{path} is missing paint, markings or running gear motion.");
            }
            finally { model.Free(); }
        }
    }

    private static void CheckEnemyTankFaction()
    {
        MatchSetup setup = new(MatchSetup.CurrentProtocolVersion, 1, 1, "Ops", PlayerFactions: ["soviet", "us"]);
        Node3D us = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Tank, setup.GetHostileFaction(1))!;
        Node3D soviet = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Tank, setup.GetHostileFaction(2))!;
        Node3D generic = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Tank)!;
        try
        {
            Require(us.Name == "EnemyTankModel" && us.GetChild<Node3D>(0).SceneFilePath.EndsWith("M60/m60_lod1.glb", StringComparison.Ordinal),
                "Player 1 must see identified enemy tanks as M60s.");
            Require(soviet.GetChild<Node3D>(0).SceneFilePath.EndsWith("T62Alt/t62alt_lod1.glb", StringComparison.Ordinal),
                "Player 2 must see identified enemy tanks as T-62s.");
            Require(generic.GetChild<Node3D>(0).SceneFilePath.EndsWith("Vehicles/tank.glb", StringComparison.Ordinal),
                "Without factions enemy tanks keep the generic model.");
            Require(us.FindChild("HostileAffiliationMarker", true, false) is not null, "Faction enemy tank lost its hostile marker.");
        }
        finally
        {
            us.Free();
            soviet.Free();
            generic.Free();
        }
    }

    private static void CheckEnemyContactFactions()
    {
        MatchSetup setup = new(MatchSetup.CurrentProtocolVersion, 1, 1, "Ops", PlayerFactions: ["soviet", "us"]);
        Node3D usApc = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Apc, setup.GetHostileFaction(1))!;
        Node3D sovietApc = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Apc, setup.GetHostileFaction(2))!;
        Node3D sovietRecon = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Recon, setup.GetHostileFaction(2))!;
        Node3D generic = ContactVisualCatalog.InstantiateEnemyModel(null, ContactClassification.Apc)!;
        try
        {
            Require(sovietApc.Name == "EnemyApcModel" && sovietApc.GetChild<Node3D>(0).SceneFilePath.EndsWith("BTR70/btr70_lod1.glb", StringComparison.Ordinal),
                "Player 2 must see an identified enemy APC as BTR-70 LOD1.");
            Require(usApc.Name == "EnemyApcModel" && usApc.GetChild<Node3D>(0).SceneFilePath.EndsWith("Vehicles/recon.glb", StringComparison.Ordinal),
                "Player 1 must see a US APC with the placeholder model.");
            Require(sovietRecon.Name == "EnemyReconModel" && sovietRecon.GetChild<Node3D>(0).SceneFilePath.EndsWith("BRDM2/brdm2_lod1.glb", StringComparison.Ordinal),
                "Identified enemy Soviet recon must use the BRDM-2, not the BTR-70.");
            Require(generic.GetChild<Node3D>(0).SceneFilePath.EndsWith("Vehicles/recon.glb", StringComparison.Ordinal),
                "Without factions an enemy APC keeps the generic model.");
            Require(sovietApc.FindChild("HostileAffiliationMarker", true, false) is not null, "Enemy APC lost its hostile marker.");
            Require(sovietRecon.FindChild("HostileAffiliationMarker", true, false) is not null, "Enemy recon lost its hostile marker.");
        }
        finally
        {
            usApc.Free();
            sovietApc.Free();
            sovietRecon.Free();
            generic.Free();
        }
    }

    private static void CheckLodInitialDetailAndHysteresis(Node3D scene)
    {
        UnitView wideView = new("m60a1", "us", "tank", initialCameraSize: 160f);
        scene.AddChild(wideView.Root);
        try
        {
            Require(wideView.ActiveLod == 2 && wideView.GetLod(2)!.Visible,
                "Vehicle spawned at wide zoom must start directly on LOD2.");
            Require(!wideView.IsLodLoaded(0) && !wideView.IsLodLoaded(1),
                "Vehicle spawned at wide zoom must not instantiate LOD0 or LOD1 up front.");

            wideView.UpdateLod(94f);
            Require(wideView.ActiveLod == 2,
                "Hysteresis must keep LOD2 above ExitLod2Size (92.5).");

            wideView.UpdateLod(75f);
            Require(wideView.ActiveLod == 1 && wideView.GetLod(1)!.Visible && !wideView.IsLodLoaded(0),
                "Zooming into play distance must switch to LOD1 without loading LOD0.");

            wideView.UpdateLod(44f);
            Require(wideView.ActiveLod == 1,
                "Hysteresis must keep LOD1 above ExitLod1Size (42.5).");

            wideView.UpdateLod(46f);
            Require(wideView.ActiveLod == 1,
                "Hysteresis must keep LOD1 near boundary 46.");

            wideView.UpdateLod(30f);
            Require(wideView.ActiveLod == 0 && wideView.GetLod(0)!.Visible && !wideView.GetLod(1)!.Visible,
                "Close zoom below ExitLod1Size must activate LOD0.");

            wideView.UpdateLod(46f);
            Require(wideView.ActiveLod == 0,
                "Upward hysteresis must keep LOD0 below EnterLod1Size (47.5).");

            wideView.UpdateLod(47.5f);
            Require(wideView.ActiveLod == 1 && wideView.GetLod(1)!.Visible && !wideView.GetLod(0)!.Visible,
                "Crossing EnterLod1Size (47.5) from LOD0 must switch to LOD1.");
        }
        finally { wideView.Root.Free(); }
    }

    private static void CheckEnemyContactLodLifecycle(Node3D scene)
    {
        ContactView contactView = new(() => "us", _ => "m60a1");
        contactView.UpdateLod(160f);
        scene.AddChild(contactView.Root);
        try
        {
            ContactObservation obs = new(
                new ContactId(7),
                ContactState.Current,
                ContactClassification.Tank,
                Type: 0,
                Position: new System.Numerics.Vector2(100f, 100f),
                LastSeenTick: 10,
                TurretYaw: -Mathf.Pi / 2f,
                HullHeading: 0f);
            contactView.Update(obs, obs.Position, 0f, isSelected: false, turretYaw: -Mathf.Pi / 2f, hullHeading: 0f);

            Require(contactView.LodCount == 3 && contactView.ActiveLod == 2,
                "Identified enemy tank spawned at wide zoom must start on LOD2.");
            Require(!contactView.IsLodLoaded(0) && !contactView.IsLodLoaded(1),
                "Enemy tank spawned at wide zoom must not load LOD0 up front.");
            Require(contactView.GetLod(2)!.Visible, "Enemy contact LOD2 must be visible.");

            contactView.UpdateLod(70f);
            Require(contactView.ActiveLod == 1 && contactView.GetLod(1)!.Visible && !contactView.GetLod(2)!.Visible,
                "Enemy contact must switch to LOD1 at play zoom.");
            Node3D lod1Turret = (Node3D)contactView.GetLod(1)!.FindChild("TurretYaw", true, false)!;
            Require(Mathf.IsEqualApprox(lod1Turret.Rotation.Y, -Mathf.Pi / 2f),
                "Enemy contact LOD1 must preserve turret yaw across switch.");

            contactView.UpdateLod(30f);
            Require(contactView.ActiveLod == 0 && contactView.GetLod(0)!.Visible && !contactView.GetLod(1)!.Visible,
                "Enemy contact must switch to LOD0 at close zoom.");
            Node3D lod0Turret = (Node3D)contactView.GetLod(0)!.FindChild("TurretYaw", true, false)!;
            Require(Mathf.IsEqualApprox(lod0Turret.Rotation.Y, -Mathf.Pi / 2f),
                "Enemy contact LOD0 must preserve turret yaw across switch.");
            Require(contactView.TryGetMuzzleTransform(out _),
                "Enemy contact active LOD0 must provide a valid muzzle transform.");
        }
        finally { contactView.Root.Free(); }
    }

    private static void CheckWreckInitialLodDetail(Node3D scene)
    {
        WreckView wreck = new("t62", "soviet", initialCameraSize: 160f);
        scene.AddChild(wreck.Root);
        try
        {
            Require(wreck.ActiveLod == 2 && wreck.GetLod(2)!.Visible,
                "Wreck spawned at wide zoom must start directly on LOD2.");
            Require(!wreck.IsLodLoaded(0) && !wreck.IsLodLoaded(1),
                "Wreck spawned at wide zoom must not load LOD0 or LOD1 up front.");
        }
        finally { wreck.Root.Free(); }
    }

    private static void CheckLastKnownContactDefersLodUntilResighted(Node3D scene)
    {
        ContactView contactView = new(() => "us", _ => "m60a1");
        contactView.UpdateLod(160f);
        scene.AddChild(contactView.Root);
        try
        {
            ContactObservation currentWide = new(
                new ContactId(12),
                ContactState.Current,
                ContactClassification.Tank,
                Type: 0,
                Position: new System.Numerics.Vector2(120f, 120f),
                LastSeenTick: 10,
                TurretYaw: -Mathf.Pi / 2f,
                HullHeading: 0f);
            contactView.Update(currentWide, currentWide.Position, 0f, isSelected: false, turretYaw: -Mathf.Pi / 2f, hullHeading: 0f);

            Require(contactView.ActiveLod == 2 && !contactView.IsLodLoaded(0) && !contactView.IsLodLoaded(1),
                "Enemy contact discovered at wide zoom must start on LOD2 without loading LOD0 or LOD1.");

            ContactObservation lost = currentWide with { State = ContactState.LastKnown, LastSeenTick = 12 };
            contactView.Update(lost, lost.Position, 0f, isSelected: false, turretYaw: -Mathf.Pi / 2f, hullHeading: 0f);
            Require(!contactView.HasEnemyModel && contactView.GhostBase.Visible,
                "Lost contact must hide its 3D model and show the last-known marker.");

            // Zooming in while the contact is hidden must record the size without creating LOD0 yet.
            bool switchedWhileHidden = contactView.UpdateLod(30f);
            Require(!switchedWhileHidden && contactView.ActiveLod == 2 && !contactView.IsLodLoaded(0),
                "Hidden LastKnown contact must not instantiate LOD0 when camera zooms in.");

            // Returning to Current applies the recorded camera size and activates LOD0 cleanly.
            ContactObservation resighted = currentWide with { LastSeenTick = 20 };
            contactView.Update(resighted, resighted.Position, 0f, isSelected: false, turretYaw: -Mathf.Pi / 2f, hullHeading: 0f);
            Require(contactView.HasEnemyModel && contactView.ActiveLod == 0,
                "Resighted contact must switch immediately to LOD0 for the close camera.");
            Require(contactView.IsLodLoaded(0) && contactView.GetLod(0)!.Visible && !contactView.GetLod(2)!.Visible,
                "Resighted contact must show LOD0 and hide LOD2.");
            Node3D lod0Turret = (Node3D)contactView.GetLod(0)!.FindChild("TurretYaw", true, false)!;
            Require(Mathf.IsEqualApprox(lod0Turret.Rotation.Y, -Mathf.Pi / 2f),
                "Resighted contact LOD0 must preserve turret yaw.");
        }
        finally { contactView.Root.Free(); }
    }

    private static void CheckViewSetsSpawnDirectLodOnFirstSync(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation own = new(new EntityId(10), 0, new System.Numerics.Vector2(40f, 40f), 0);
            ContactObservation contact = new(
                new ContactId(20),
                ContactState.Current,
                ContactClassification.Tank,
                Type: 0,
                Position: new System.Numerics.Vector2(80f, 80f),
                LastSeenTick: 5);
            WreckObservation wreck = new(new WreckId(30), 0, new System.Numerics.Vector2(60f, 60f), SupportSurface: 0, HullHeading: 0f, TurretYaw: 0f);

            int size = WireProtocol.WriteSnapshot(bytes, sequence: 1, tick: 5, [own], [contact], wrecks: [wreck]);
            Require(replica.TryApply(bytes.AsSpan(0, size)), "ViewSet direct LOD snapshot was rejected.");

            // UpdateLod sets camera size on ViewSets before first Sync (single camera-size path).
            UnitViewSet units = new(parent, map, unitIdProvider: _ => "m60a1", factionProvider: () => "us");
            ContactViewSet contacts = new(parent, map, hostileFactionProvider: () => "soviet", unitIdProvider: _ => "t62");
            WreckViewSet wrecks = new(parent, map, unitIdProvider: _ => "m60a1", factionProvider: () => "us");

            units.UpdateLod(160f);
            contacts.UpdateLod(160f);
            wrecks.UpdateLod(160f);

            units.Sync(replica, 5.0);
            contacts.Sync(replica, 5.0);
            wrecks.Sync(replica);

            UnitView uView = units.Views[own.Id];
            ContactView cView = contacts.Views[contact.Id];
            WreckView wView = wrecks.Views[wreck.Id];
            Require(uView.ActiveLod == 2 && !uView.IsLodLoaded(0) && !uView.IsLodLoaded(1),
                "UnitViewSet after UpdateLod must spawn straight at LOD2 on first Sync.");
            Require(cView.ActiveLod == 2 && !cView.IsLodLoaded(0) && !cView.IsLodLoaded(1),
                "ContactViewSet after UpdateLod must spawn straight at LOD2 on first Sync.");
            Require(wView.ActiveLod == 2 && !wView.IsLodLoaded(0) && !wView.IsLodLoaded(1),
                "WreckViewSet after UpdateLod must spawn straight at LOD2 on first Sync.");
        }
        finally { parent.Free(); }
    }

    private static void CheckLodInstantiationBudget100Views(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            const int unitCount = 100;
            HeightMap map = HeightMap.CreatePrototype();
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation[] units = new OwnUnitObservation[unitCount];
            for (int i = 0; i < unitCount; i++)
            {
                units[i] = new OwnUnitObservation(
                    new EntityId((uint)(100 + i)),
                    0,
                    new System.Numerics.Vector2(10f + (i % 10) * 6f, 10f + (i / 10) * 6f),
                    0);
            }
            int size = WireProtocol.WriteSnapshot(bytes, sequence: 1, tick: 5, units);
            Require(replica.TryApply(bytes.AsSpan(0, size)), "100-unit LOD budget snapshot was rejected.");

            UnitViewSet viewSet = new(parent, map, unitIdProvider: _ => "m60a1", factionProvider: () => "us");
            viewSet.UpdateLod(160f);
            viewSet.Sync(replica, 5.0);

            foreach (UnitView v in viewSet.Views.Values)
            {
                Require(v.ActiveLod == 2 && !v.IsLodLoaded(0) && v.GetLod(2)!.Visible,
                    "All 100 wide-spawned units must start visible at LOD2 without LOD0 loaded.");
            }

            int budgetPerFrame = VehicleLod.DefaultMaxInstantiationsPerFrame;
            int expectedFrames = (unitCount + budgetPerFrame - 1) / budgetPerFrame;
            int prevLoaded = 0;
            int frames = 0;
            while (prevLoaded < unitCount && frames < expectedFrames + 2)
            {
                frames++;
                int remaining = budgetPerFrame;
                viewSet.UpdateLod(30f, ref remaining);

                int loadedNow = 0;
                foreach (UnitView v in viewSet.Views.Values)
                {
                    if (v.IsLodLoaded(0)) loadedNow++;
                    Require(v.GetLod(v.ActiveLod)!.Visible,
                        "Every view must keep its active LOD visible while waiting for budget.");
                }

                int createdThisFrame = loadedNow - prevLoaded;
                Require(createdThisFrame <= budgetPerFrame,
                    $"Frame {frames} instantiated {createdThisFrame} LOD0 nodes, exceeding budget {budgetPerFrame}.");
                prevLoaded = loadedNow;
            }

            Require(frames <= expectedFrames,
                $"100 units took {frames} frames to reach LOD0, expected at most {expectedFrames}.");
            foreach (UnitView v in viewSet.Views.Values)
            {
                Require(v.ActiveLod == 0 && v.GetLod(0)!.Visible && !v.GetLod(2)!.Visible,
                    "Every unit must finish at visible LOD0 once transition frames complete.");
            }
        }
        finally { parent.Free(); }
    }

    private static void CheckAnimationHandoffOnLodSwitch(Node3D scene)
    {
        static PackedScene BuildAnimatedLodScene(string animName)
        {
            Node3D root = new() { Name = "LodScene" };
            AnimationPlayer player = new() { Name = "AnimationPlayer" };
            Animation anim = new() { Length = 2.0f };
            AnimationLibrary lib = new();
            lib.AddAnimation(animName, anim);
            player.AddAnimationLibrary("", lib);
            root.AddChild(player);
            player.Owner = root;
            PackedScene packed = new();
            packed.Pack(root);
            root.Free();
            return packed;
        }

        PackedScene lod0Scene = BuildAnimatedLodScene("Walk");
        PackedScene lod1Scene = BuildAnimatedLodScene("Walk");

        // 1. UnitView AnimationPlayer handoff
        VehicleVisual unitVisual = new(new Node3D { Name = "UnitAnimTest" }, [lod0Scene, lod1Scene], 0f, 0f, initialLod: 0);
        UnitView unitView = new(unitVisual, "test_infantry", "unit_test");
        scene.AddChild(unitView.Root);
        try
        {
            AnimationPlayer p0 = unitView.AnimationPlayer!;
            unitView.PlayAnimation("Walk");
            p0.SpeedScale = 1.5f;
            p0.Seek(0.4, update: true);
            Require(p0.IsPlaying() && p0.CurrentAnimation == "Walk", "LOD0 AnimationPlayer must be playing Walk before switch.");

            unitView.UpdateLod(75f);
            AnimationPlayer p1 = unitView.AnimationPlayer!;
            Require(unitView.ActiveLod == 1 && !ReferenceEquals(p0, p1),
                "UnitView must switch AnimationPlayer reference to LOD1 on LOD change.");
            Require(p1.IsPlaying() && p1.CurrentAnimation == "Walk",
                "LOD1 AnimationPlayer must continue playing Walk after LOD switch.");
            Require(Mathf.Abs((float)p1.CurrentAnimationPosition - 0.4f) < 0.05f,
                $"LOD1 animation position was {p1.CurrentAnimationPosition:F3}, expected ~0.40.");
            Require(Mathf.IsEqualApprox(p1.SpeedScale, 1.5f),
                $"LOD1 animation speed scale was {p1.SpeedScale}, expected 1.5.");
        }
        finally { unitView.Root.Free(); }

        // 2. ContactView AnimationPlayer handoff
        VehicleVisual contactVisual = new(new Node3D { Name = "ContactAnimTest" }, [lod0Scene, lod1Scene], 0f, 0f, initialLod: 0);
        ContactView contactView = new(contactVisual, ContactClassification.Infantry);
        scene.AddChild(contactView.Root);
        try
        {
            AnimationPlayer p0 = contactView.AnimationPlayer!;
            contactView.PlayAnimation("Walk");
            p0.SpeedScale = 1.75f;
            p0.Seek(0.6, update: true);
            Require(p0.IsPlaying() && p0.CurrentAnimation == "Walk", "Contact LOD0 AnimationPlayer must be playing Walk before switch.");

            contactView.UpdateLod(75f);
            AnimationPlayer p1 = contactView.AnimationPlayer!;
            Require(contactView.ActiveLod == 1 && !ReferenceEquals(p0, p1),
                "ContactView must switch AnimationPlayer reference to LOD1 on LOD change.");
            Require(p1.IsPlaying() && p1.CurrentAnimation == "Walk",
                "Contact LOD1 AnimationPlayer must resume playing Walk even though _currentAnimName matches.");
            Require(Mathf.Abs((float)p1.CurrentAnimationPosition - 0.6f) < 0.05f,
                $"Contact LOD1 animation position was {p1.CurrentAnimationPosition:F3}, expected ~0.60.");
            Require(Mathf.IsEqualApprox(p1.SpeedScale, 1.75f),
                $"Contact LOD1 animation speed scale was {p1.SpeedScale}, expected 1.75.");
        }
        finally { contactView.Root.Free(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
