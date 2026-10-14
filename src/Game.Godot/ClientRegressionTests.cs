using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Sandbox;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

// Runs inside the real Godot runtime with --smoke-test, including --headless.
internal static class ClientRegressionTests
{
    internal static bool Run(Node3D scene)
    {
        try
        {
            CheckViewLifecycle(scene);
            CheckContactViewLifecycle(scene);
            CheckRemoteHudSideAssignment(scene);
            CheckRectangularGrid(5, 3);
            CheckRectangularGrid(3, 5);
            CheckCombatEffectsAndLifecycle(scene);
            CheckConfiguredUnitHealthBars(scene);
            CheckVehicleModels(scene);
            CheckCameraRig(scene);
            CheckDisplaySettings(scene);
            CheckCombatEffectsFramePacing(scene);
            CheckMissionPanelLifecycle(scene);
            CheckCombatReadability(scene);
            CombatFeedbackRegressionTests.Run(scene);
            GD.Print("M0_CLIENT_REGRESSIONS_OK view_lifecycle contact_lifecycle hud_assignment rectangular_grids combat_effects configured_health vehicle_models camera_rig display_settings combat_frame_pacing mission_panel combat_readability combat_feedback");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError(exception.ToString());
            return false;
        }
    }

    private static void CheckViewLifecycle(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            UnitViewSet views = new(parent, HeightMap.CreatePrototype());
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation a = new(new EntityId(1), 0, new NumericsVector2(10, 10), 0);
            OwnUnitObservation b = new(new EntityId(2), 0, new NumericsVector2(20, 20), 0);
            Apply(replica, bytes, 1, 0, [a]);
            views.Sync(replica, 0);
            Require(views.Views.Count == 1 && parent.GetChildCount() == 1, "Initial view was not created.");
            Node3D original = views.Views[a.Id].Root;
            views.Selected.Add(a.Id);
            Apply(replica, bytes, 2, 2, [a, b]);
            views.Sync(replica, 2);
            Require(views.Views.Count == 2 && parent.GetChildCount() == 2, "Spawn was not rendered.");
            Require(ReferenceEquals(original, views.Views[a.Id].Root), "An unchanged view was replaced.");
            Apply(replica, bytes, 3, 4, [b]);
            views.Sync(replica, 4);
            Require(views.Views.Count == 1 && !views.Views.ContainsKey(a.Id) && original.GetParent() is null,
                "Removed unit still has an attached view.");
            Require(views.Selected.Count == 0, "Removed unit stayed selected.");
            Require(views.Views[b.Id].Root.Position.X == 20, "Spawned unit did not receive its position.");
            Apply(replica, bytes, 4, 6, []);
            views.Sync(replica, 6);
            Require(views.Views.Count == 0 && parent.GetChildCount() == 0, "Empty snapshot did not clear views.");
        }
        finally { parent.QueueFree(); }
    }

    private static void CheckVehicleModels(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        UnitViewSet? views = null;
        try
        {
            // Catalog order must not select the wrong vehicle scene.
            DeadGround.Rules.Ops.UnitCatalog source = DeadGround.Rules.Ops.PrototypeScenario.LoadCatalog();
            DeadGround.Rules.Ops.UnitCatalog reordered = source with
            {
                Units = [source.RequireDefinition("prototype_tank"), source.RequireDefinition("recon_vehicle"),
                    source.RequireDefinition("prototype_vehicle")]
            };
            views = new UnitViewSet(parent, HeightMap.CreatePrototype(), reordered);
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            OwnUnitObservation tank = new(new EntityId(301), 0, new NumericsVector2(10, 10), 0, Health: 120);
            OwnUnitObservation recon = new(new EntityId(302), 1, new NumericsVector2(20, 10), 0, Health: 60);
            OwnUnitObservation otherTank = new(new EntityId(303), 0, new NumericsVector2(20, 20), 0, Health: 120);
            Apply(replica, bytes, 1, 0, [tank, recon, otherTank]);
            views.Sync(replica, 0);
            UnitView tankView = views.Views[tank.Id];
            UnitView reconView = views.Views[recon.Id];
            Require(tankView.HasVehicleModel && reconView.HasVehicleModel, "Vehicle assets did not load.");
            Node3D? turret = tankView.Model.FindChild("TurretYaw", true, false) as Node3D;
            Require(turret is not null, "Reordered tank definition did not instantiate the tank scene.");
            Require(reconView.Model.FindChild("SensorYaw", true, false) is not null,
                "Reordered recon definition did not instantiate the recon scene.");
            Require(!ReferenceEquals(tankView.Model, views.Views[otherTank.Id].Model), "Vehicle instances share a node.");

            ContactObservation target = new(new ContactId(801), ContactState.Current, ContactClassification.Unknown,
                new NumericsVector2(12, 0), 2);
            tank = tank with { Position = new NumericsVector2(12, 10), TargetContactId = target.Id };
            ApplyWithContacts(replica, bytes, 2, 2, [tank, recon, otherTank], [target]);
            views.Sync(replica, 2);
            Require((-tankView.Model.GlobalBasis.Z.Normalized()).Dot(Vector3.Right) > 0.99f,
                "Tank did not face its observed movement direction.");
            Require((-turret!.GlobalBasis.Z.Normalized()).Dot(Vector3.Forward) > 0.99f,
                "Turret did not face the permitted target independently of the hull.");
            Require(views.Views[otherTank.Id].Model.Rotation == Vector3.Zero, "Turning one tank rotated another instance.");
            Vector3 aim = turret.Rotation;
            target = target with { State = ContactState.LastKnown, Position = new NumericsVector2(30, 30) };
            ApplyWithContacts(replica, bytes, 3, 4, [tank, recon, otherTank], [target]);
            views.Sync(replica, 4);
            Require(turret.Rotation == aim, "Turret tracked a last-known contact as a live target.");
        }
        finally
        {
            views?.Clear();
            parent.QueueFree();
        }
    }

    private static void CheckContactViewLifecycle(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            ContactViewSet contactViews = new(parent, map);
            UnitViewSet unitViews = new(parent, map);
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation own = new(new EntityId(1), 0, new NumericsVector2(10, 10), 0);
            ContactObservation c1 = new(
                new ContactId(101),
                ContactState.Current,
                ContactClassification.Unknown,
                new NumericsVector2(25, 30),
                10);

            // 1. Initial snapshot with 1 contact
            ApplyWithContacts(replica, bytes, 1, 10, [own], [c1]);
            unitViews.Sync(replica, 10);
            contactViews.Sync(replica);

            Require(contactViews.Views.Count == 1 && contactViews.Views.ContainsKey(c1.Id),
                "Contact view was not created.");
            ContactView v1 = contactViews.Views[c1.Id];
            Require(v1.Observation.State == ContactState.Current, "Initial contact state should be Current.");
            Require(v1.Root.Position.X == 25 && v1.Root.Position.Z == 30, "Contact position mismatch.");

            // 2. Selection isolation: select contact and own unit separately
            contactViews.Selected.Add(c1.Id);
            unitViews.Selected.Add(own.Id);
            Require(contactViews.Selected.Contains(c1.Id) && unitViews.Selected.Contains(own.Id),
                "Selections should be trackable.");
            Require((object)c1.Id is not EntityId, "ContactId must not be EntityId.");

            // 3. Transition to LastKnown with frozen position
            ContactObservation c1Lost = new(
                new ContactId(101),
                ContactState.LastKnown,
                ContactClassification.Unknown,
                new NumericsVector2(25, 30),
                10);

            ApplyWithContacts(replica, bytes, 2, 20, [own], [c1Lost]);
            contactViews.Sync(replica);

            Require(contactViews.Views.Count == 1, "LastKnown contact should persist.");
            Require(ReferenceEquals(v1, contactViews.Views[c1.Id]), "Existing contact view should be retained.");
            Require(v1.Observation.State == ContactState.LastKnown, "Contact state should transition to LastKnown.");
            Require(v1.Root.Position.X == 25 && v1.Root.Position.Z == 30, "LastKnown position must remain frozen.");

            // 4. Removal after expiry (absent from subsequent snapshot)
            ApplyWithContacts(replica, bytes, 3, 30, [own], []);
            contactViews.Sync(replica);

            Require(contactViews.Views.Count == 0, "Expired contact view was not removed.");
            Require(!contactViews.Views.ContainsKey(c1.Id), "Expired contact view dictionary entry retained.");
            Require(!contactViews.Selected.Contains(c1.Id), "Expired contact remained selected.");
            Require(v1.Root.GetParent() is null, "Expired contact node was not detached from scene.");
        }
        finally { parent.QueueFree(); }
    }

    private static void Apply(ClientReplica replica, byte[] bytes, uint sequence, int tick, OwnUnitObservation[] units)
    {
        int size = WireProtocol.WriteSnapshot(bytes, sequence, tick, units);
        Require(replica.TryApply(bytes.AsSpan(0, size)), "Regression snapshot was rejected.");
    }

    private static void ApplyWithContacts(ClientReplica replica, byte[] bytes, uint sequence, int tick,
        OwnUnitObservation[] units, ContactObservation[] contacts)
    {
        int size = WireProtocol.WriteSnapshot(bytes, sequence, tick, units, contacts);
        Require(replica.TryApply(bytes.AsSpan(0, size)), "Regression snapshot with contacts was rejected.");
    }

    private static void CheckRemoteHudSideAssignment(Node3D scene)
    {
        string pending = Main.FormatTitle(0, "visibility");
        Require(pending.Contains("SIDE 0"), "Pending handshake title did not show SIDE 0.");

        string side1 = Main.FormatTitle(1, "visibility");
        Require(side1.Contains("SIDE 1") && side1.Contains("RECON SCENARIO"), "Assigned side 1 did not reflect in title.");

        string side2 = Main.FormatTitle(2, "visibility");
        Require(side2.Contains("SIDE 2") && side2.Contains("RECON SCENARIO"), "Assigned side 2 did not reflect in title.");

        string proto = Main.FormatTitle(1, "prototype");
        Require(proto.Contains("SIDE 1") && proto.Contains("M0 NETWORK TEST"), "Prototype title did not format correctly.");

        string combat = Main.FormatTitle(1, "combat");
        Require(combat.Contains("SIDE 1") && combat.Contains("COMBAT SCENARIO"), "Combat title did not format correctly.");
    }

    private static void CheckCombatEffectsAndLifecycle(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            UnitViewSet unitViews = new(parent, map);
            CombatEffects effects = new(map);
            parent.AddChild(effects);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation tank = new(
                new EntityId(10),
                2,
                new NumericsVector2(20, 20),
                0,
                PathRequestStatus.None,
                Health: 120,
                Ammunition: 8,
                ReloadTicksRemaining: 15,
                AttackStatus: AttackExecutionStatus.Aiming,
                TargetContactId: new ContactId(202));

            OwnUnitObservation recon = new(
                new EntityId(11),
                1,
                new NumericsVector2(18, 20),
                0,
                PathRequestStatus.None,
                Health: 60,
                Ammunition: 0,
                ReloadTicksRemaining: 0,
                AttackStatus: AttackExecutionStatus.None,
                TargetContactId: default);

            CombatEventObservation shotEvent = new(
                EventId: 1,
                ServerTick: 5,
                Kind: CombatEventKind.ShotFired,
                PresentationId: 1,
                Origin: new System.Numerics.Vector3(20, 1.9f, 20),
                Target: new System.Numerics.Vector3(40, 1.5f, 20),
                ShooterUnitId: tank.Id);

            int size = WireProtocol.WriteSnapshot(bytes, 1, 5, [tank, recon], [], [shotEvent]);
            Require(replica.TryApply(bytes.AsSpan(0, size)), "Combat snapshot was rejected by replica.");

            unitViews.Sync(replica, 5);
            Require(unitViews.Views.ContainsKey(tank.Id), "Tank unit view was not created.");
            Require(unitViews.Views.ContainsKey(recon.Id), "Recon unit view was not created.");

            // Finding 9: Health bar normalization using actual definition maximum health
            // Full health recon (60/60) must display 1.0 (100%), not 0.60
            Require(MathF.Abs(unitViews.Views[recon.Id].HealthBarScaleX - 1.0f) < 0.01f,
                $"Full recon health bar scale was {unitViews.Views[recon.Id].HealthBarScaleX}, expected 1.0");
            // Full health tank (120/120) must display 1.0 (100%)
            Require(MathF.Abs(unitViews.Views[tank.Id].HealthBarScaleX - 1.0f) < 0.01f,
                $"Full tank health bar scale was {unitViews.Views[tank.Id].HealthBarScaleX}, expected 1.0");

            // Process shot event
            effects.ProcessEvents(replica.Events, unitViews);

            // Finding 8: Vehicle recoil applies only to shooter, not adjacent recon vehicle (2 meters away)
            Require(unitViews.Views[tank.Id].RecoilOffset != Vector3.Zero, "Shooter tank did not receive recoil.");
            Require(unitViews.Views[recon.Id].RecoilOffset == Vector3.Zero, "Adjacent recon vehicle wrongly received recoil.");

            // Tracer shell streak preserves intended streak length, shot direction, and compact transverse scale
            float streakLen = effects.Tracers[0].StreakLength;
            Vector3 expectedDir = (new Vector3(40, 1.5f, 20) - effects.Tracers[0].Origin).Normalized();
            Require(effects.Tracers[0].Direction.Dot(expectedDir) > 0.99f, "Tracer did not follow shot direction.");
            Require(effects.Tracers[0].Mesh.Scale.Z > 0f && effects.Tracers[0].Mesh.Scale.Z <= streakLen,
                $"Tracer initial scale Z was {effects.Tracers[0].Mesh.Scale.Z}, expected <= {streakLen}");

            effects.Update(0.05);
            unitViews.UpdateRecoil(0.05);

            Require(MathF.Abs(effects.Tracers[0].Mesh.Scale.Z - streakLen) < 0.05f,
                $"Tracer longitudinal scale Z collapsed to {effects.Tracers[0].Mesh.Scale.Z} after update, expected {streakLen}");
            Require(MathF.Abs(effects.Tracers[0].Mesh.Scale.X - CombatEffects.TracerThickness) < 0.01f,
                $"Tracer transverse scale was {effects.Tracers[0].Mesh.Scale.X}, expected {CombatEffects.TracerThickness}");

            // Re-processing same event should deduplicate (no duplicate recoil)
            Vector3 tankRecoilBefore = unitViews.Views[tank.Id].RecoilOffset;
            effects.ProcessEvents(replica.Events, unitViews);
            Require(unitViews.Views[tank.Id].RecoilOffset == tankRecoilBefore,
                "Duplicate event repeated recoil on shooter.");

            effects.Update(0.2);
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckRectangularGrid(int columns, int rows)
    {
        HeightMap map = new(columns, rows, 4, 1, new ushort[columns * rows]);
        MeshInstance3D grid = TerrainView.CreateGrid(map);
        try
        {
            using Godot.Collections.Array arrays = grid.Mesh.SurfaceGetArrays(0);
            Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Require(vertices.Length == 28, "Rectangular grid has missing or extra segments.");
            foreach (Vector3 vertex in vertices)
                Require(map.Contains(new NumericsVector2(vertex.X, vertex.Z)), "Grid extends outside the map.");
        }
        finally { grid.Free(); }
    }

    private static void CheckConfiguredUnitHealthBars(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            // Create a custom catalog with non-default health values and reordered definitions
            // Definition 0: recon with 40 max health
            // Definition 1: tank with 200 max health
            string customCatalogJson = """
            {
              "schema_version": 1,
              "units": [
                {
                  "id": "custom_recon",
                  "speed": 6.0,
                  "sensor_range": 60.0,
                  "eye_height": 1.8,
                  "radius": 1.0,
                  "max_health": 40,
                  "target_points": [1.0]
                },
                {
                  "id": "custom_tank",
                  "speed": 4.0,
                  "sensor_range": 60.0,
                  "eye_height": 2.0,
                  "radius": 1.5,
                  "max_health": 200,
                  "target_points": [1.0]
                }
              ]
            }
            """;
            DeadGround.Rules.Ops.UnitCatalog customCatalog = DeadGround.Rules.Ops.PrototypeScenario.LoadCatalog(customCatalogJson);

            // UnitViewSet constructed with custom catalog
            UnitViewSet views = new(parent, map, customCatalog);
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            // Unit 1 is custom_recon (def 0) with full health (40/40)
            OwnUnitObservation recon = new(
                new EntityId(1),
                Definition: 0,
                Position: new NumericsVector2(10, 10),
                SupportSurface: 0,
                NavStatus: PathRequestStatus.None,
                Health: 40);

            // Unit 2 is custom_tank (def 1) with full health (200/200)
            OwnUnitObservation tank = new(
                new EntityId(2),
                Definition: 1,
                Position: new NumericsVector2(20, 20),
                SupportSurface: 0,
                NavStatus: PathRequestStatus.None,
                Health: 200);

            int size = WireProtocol.WriteSnapshot(bytes, 1, 1, [recon, tank]);
            Require(replica.TryApply(bytes.AsSpan(0, size)), "Snapshot apply failed.");

            views.Sync(replica, 1);

            Require(views.Views.ContainsKey(recon.Id), "Recon view missing.");
            Require(views.Views.ContainsKey(tank.Id), "Tank view missing.");

            // Full health (40/40) must scale to 1.0 (100%), not 40/60 = 0.6667
            float reconScale = views.Views[recon.Id].HealthBarScaleX;
            Require(MathF.Abs(reconScale - 1.0f) < 0.01f,
                $"Configured recon health bar scale was {reconScale}, expected 1.0");

            // Full health (200/200) must scale to 1.0 (100%), not 200/120 = 1.6667
            float tankScale = views.Views[tank.Id].HealthBarScaleX;
            Require(MathF.Abs(tankScale - 1.0f) < 0.01f,
                $"Configured tank health bar scale was {tankScale}, expected 1.0");

            // Also test default UnitViewSet fallback pulls from loaded catalog
            UnitViewSet defaultViews = new(parent, map);
            Require(UnitViewSet.GetDefaultMaxHealth(0) == DeadGround.Rules.Ops.PrototypeScenario.LoadCatalog().Units[0].MaxHealth,
                "Default max health provider should match loaded catalog.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckCameraRig(Node3D scene)
    {
        Camera3D camera = new();
        scene.AddChild(camera);
        try
        {
            CameraRig rig = new(camera, initialFocus: new Vector3(64, 0, 64), initialSize: 112, mapWidth: 128, mapDepth: 128);
            Require(rig.Focus == new Vector3(64, 0, 64), "Initial focus mismatch.");
            Require(rig.Size == 112f, "Initial size mismatch.");
            Require(camera.Size == 112f, "Camera projection size mismatch.");
            Require(camera.Position.Y > 64f, "Camera Y offset missing.");

            // Test zoom in
            rig.ZoomIn();
            Require(rig.Model.TargetSize < 112f, "ZoomIn did not reduce target size.");
            rig.Update(0.1, allowInput: false);
            Require(rig.Size < 112f, "Camera size did not decrease after zoom in update.");

            // Test reset to default view
            rig.ResetToDefault();
            Require(rig.Focus == new Vector3(64, 0, 64), "Reset did not restore focus.");
            Require(rig.Size == 112f, "Reset did not restore size.");

            // Test bounds clamping
            rig.Model.SetInput(100f, 100f);
            rig.Update(1.0, allowInput: true);
            Require(rig.Focus.X <= 128f && rig.Focus.Z <= 128f, "Focus exceeded map bounds.");
        }
        finally
        {
            camera.QueueFree();
        }
    }

    private static void CheckDisplaySettings(Node3D scene)
    {
        Require(DisplaySettings.SanitizeMaxFps(144) == 144, "SanitizeMaxFps failed on valid value.");
        Require(DisplaySettings.SanitizeMaxFps(75) == 60, "SanitizeMaxFps failed to fall back.");
        Require(DisplaySettings.SanitizeMaxFps(0) == 0, "SanitizeMaxFps failed on unlimited (0).");

        SettingsPanel panel = new();
        scene.AddChild(panel);
        try
        {
            DisplaySettings saved = new(VSyncEnabled: true, MaxFps: 60, BenchmarkFrames: 0);
            DisplaySettings effective = new(VSyncEnabled: true, MaxFps: 144, BenchmarkFrames: 0);
            panel.Initialize(saved, effective, cliOverridesVsync: false, cliOverridesMaxFps: true);

            Require(!panel.IsOpen, "Panel should be closed initially.");
            panel.Toggle();
            Require(panel.IsOpen, "Panel should be open after toggle.");
            panel.Close();
            Require(!panel.IsOpen, "Panel should be closed after Close().");

            Require(panel.CurrentSettings.VSyncEnabled == true, "Panel VSync setting mismatch.");
            Require(panel.CurrentSettings.MaxFps == 144, "Panel effective MaxFps should reflect CLI override.");
            Require(panel.SavedConfig.MaxFps == 60, "Panel saved config must retain original 60 FPS, not CLI override.");
        }
        finally
        {
            panel.QueueFree();
        }
    }

    private static void CheckCombatEffectsFramePacing(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            CombatEffects effects = new(map);
            parent.AddChild(effects);
            UnitViewSet unitViews = new(parent, map);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            OwnUnitObservation tank = new(
                new EntityId(10),
                2,
                new NumericsVector2(20, 20),
                0,
                PathRequestStatus.None,
                Health: 120,
                Ammunition: 8,
                ReloadTicksRemaining: 15,
                AttackStatus: AttackExecutionStatus.Aiming,
                TargetContactId: new ContactId(202));

            CombatEventObservation shotEvent = new(
                EventId: 101,
                ServerTick: 5,
                Kind: CombatEventKind.ShotFired,
                PresentationId: 1,
                Origin: new System.Numerics.Vector3(20, 1.9f, 20),
                Target: new System.Numerics.Vector3(40, 1.5f, 20),
                ShooterUnitId: tank.Id);

            int size = WireProtocol.WriteSnapshot(bytes, 1, 5, [tank], [], [shotEvent]);
            Require(replica.TryApply(bytes.AsSpan(0, size)), "Snapshot apply failed.");
            unitViews.Sync(replica, 5);

            effects.ProcessEvents(replica.Events, unitViews);
            Require(effects.Tracers[0].Active, "Tracer was not activated.");

            float expectedStreakLength = effects.Tracers[0].StreakLength;

            // Run updates at high frame rate (144 FPS -> dt = 1/144s)
            for (int i = 0; i < 5; i++)
            {
                effects.Update(1.0 / 144.0);
                unitViews.UpdateRecoil(1.0 / 144.0);
            }

            Require(MathF.Abs(effects.Tracers[0].Mesh.Scale.Z - expectedStreakLength) < 0.05f,
                "Tracer longitudinal scale Z deviated during high-fps updates.");

            // Run update at low frame rate (30 FPS -> dt = 1/30s)
            effects.Update(1.0 / 30.0);
            unitViews.UpdateRecoil(1.0 / 30.0);

            Require(MathF.Abs(effects.Tracers[0].Mesh.Scale.Z - expectedStreakLength) < 0.05f,
                "Tracer longitudinal scale Z deviated after low-fps update.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckMissionPanelLifecycle(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            MissionPanel panel = new(parent, map);
            parent.AddChild(panel);
            panel.SetLocalMode(true);

            // 1. Initial observation: InProgress
            MissionObservation inProgress = new(
                MissionId: 1,
                Status: MissionStatus.InProgress,
                EndReason: MissionEndReason.None,
                TimeLimitTicks: 14400,
                FinishedTick: 0,
                TargetContactId: default,
                SearchAreaCenter: new System.Numerics.Vector2(420f, 420f),
                SearchAreaRadius: 70f,
                Flags: 0);

            panel.UpdateMission(inProgress, 100);
            Require(!panel.IsMissionFinished, "Mission should not be marked finished while in progress.");

            // 2. Target identified
            MissionObservation identified = inProgress with
            {
                TargetContactId = new ContactId(501),
                Flags = MissionObservation.FlagTargetIdentified
            };
            panel.UpdateMission(identified, 200);
            Require(!panel.IsMissionFinished, "Mission should not be finished when target is only identified.");

            // 3. Victory
            MissionObservation victory = identified with
            {
                Status = MissionStatus.Victory,
                EndReason = MissionEndReason.TargetDestroyed,
                Flags = MissionObservation.FlagTargetIdentified | MissionObservation.FlagTargetDestroyed
            };
            panel.UpdateMission(victory, 300);
            Require(panel.IsMissionFinished, "Mission should be marked finished on victory.");

            // 4. Reset UI
            panel.ResetUi();
            Require(!panel.IsMissionFinished, "ResetUi should clear finished state.");

            // 5. Cleanup
            panel.Cleanup();
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckCombatReadability(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            DeadGround.Rules.Ops.UnitCatalog catalog = DeadGround.Rules.Ops.PrototypeScenario.LoadCatalog();
            ContactViewSet contactViews = new(parent, map);
            UnitViewSet unitViews = new(parent, map, catalog);
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            // 1. Unknown contact representation
            ContactObservation unknownContact = new(
                new ContactId(101),
                ContactState.Current,
                DetectionLevel.Detected,
                ContactClassification.Unknown,
                new NumericsVector2(30, 40),
                5);

            ApplyWithContacts(replica, bytes, 1, 5, [], [unknownContact]);
            contactViews.Sync(replica);

            Require(contactViews.Views.TryGetValue(unknownContact.Id, out ContactView? cv), "Unknown contact view not created.");
            Require(!cv!.HasEnemyModel, "Unknown contact must not have a 3D vehicle model.");
            Require(cv.Marker.Visible, "Unknown contact must display the neutral diamond marker.");
            Require(!cv.GhostBase.Visible, "Current unknown contact must not display ghost base marker.");
            Require(cv.CurrentModelClassification == ContactClassification.Unknown, "Current classification should be Unknown.");

            // 2. Identified contact models (Tank, Recon, CommandVehicle)
            ContactObservation tankContact = unknownContact with
            {
                Classification = ContactClassification.Tank,
                Level = DetectionLevel.Identified
            };
            ApplyWithContacts(replica, bytes, 2, 6, [], [tankContact]);
            contactViews.Sync(replica);

            Require(cv.HasEnemyModel, "Identified tank must instantiate a vehicle model.");
            Require(cv.CurrentModelClassification == ContactClassification.Tank, "Classification should be Tank.");
            Require(!cv.Marker.Visible, "Diamond marker should be hidden when identified.");
            Require(cv.EnemyModel!.FindChild("HostileAffiliationMarker", true, false) is not null,
                "Identified enemy model must attach HostileAffiliationMarker.");

            // Switch to Recon
            ContactObservation reconContact = tankContact with
            {
                Classification = ContactClassification.Recon
            };
            ApplyWithContacts(replica, bytes, 3, 7, [], [reconContact]);
            contactViews.Sync(replica);

            Require(cv.HasEnemyModel, "Identified recon must have enemy model.");
            Require(cv.CurrentModelClassification == ContactClassification.Recon, "Classification should be Recon.");
            Require(cv.EnemyModel!.Name == "EnemyReconModel", "Model should be enemy recon.");

            // Switch to CommandVehicle
            ContactObservation cmdContact = tankContact with
            {
                Classification = ContactClassification.CommandVehicle
            };
            ApplyWithContacts(replica, bytes, 4, 8, [], [cmdContact]);
            contactViews.Sync(replica);

            Require(cv.HasEnemyModel, "Identified command vehicle must have model.");
            Require(cv.CurrentModelClassification == ContactClassification.CommandVehicle, "Classification should be CommandVehicle.");
            Require(cv.EnemyModel!.Name == "EnemyCommandVehicleModel", "Model should be enemy command vehicle.");

            // 3. Transition to LastKnown hides live model and displays ghost marker
            ContactObservation lastKnownContact = cmdContact with
            {
                State = ContactState.LastKnown,
                Position = new NumericsVector2(32, 42)
            };
            ApplyWithContacts(replica, bytes, 5, 9, [], [lastKnownContact]);
            contactViews.Sync(replica);

            Require(!cv.HasEnemyModel, "LastKnown contact must hide live model.");
            Require(cv.Marker.Visible, "LastKnown contact must display marker.");
            Require(cv.GhostBase.Visible, "LastKnown contact must display ghost base marker.");
            Require(cv.Root.Position.X == 32 && cv.Root.Position.Z == 42, "Position must match last known coordinates.");

            // 4. Re-appearance as Detected Unknown under same ContactId clears model and identity
            ContactObservation reacquiredUnknown = new(
                cmdContact.Id,
                ContactState.Current,
                DetectionLevel.Detected,
                ContactClassification.Unknown,
                new NumericsVector2(35, 45),
                10);
            ApplyWithContacts(replica, bytes, 6, 10, [], [reacquiredUnknown]);
            contactViews.Sync(replica);

            Require(!cv.HasEnemyModel, "Reacquired unknown contact must not have model.");
            Require(cv.EnemyModel is null, "Previous enemy model must be destroyed and freed.");
            Require(cv.CurrentModelClassification == ContactClassification.Unknown, "Classification must reset to Unknown.");
            Require(cv.Marker.Visible, "Marker diamond must be visible.");
            Require(!cv.GhostBase.Visible, "Ghost base must be hidden.");

            // Subsequent re-identification as Tank creates tank model, not old command vehicle
            ContactObservation reIdentifiedTank = reacquiredUnknown with
            {
                Classification = ContactClassification.Tank,
                Level = DetectionLevel.Identified
            };
            ApplyWithContacts(replica, bytes, 7, 11, [], [reIdentifiedTank]);
            contactViews.Sync(replica);
            Require(cv.HasEnemyModel, "Re-identified contact must create model.");
            Require(cv.CurrentModelClassification == ContactClassification.Tank, "Classification should be Tank.");
            Require(cv.EnemyModel!.Name == "EnemyTankModel", "Model should be enemy tank.");

            // Generic Vehicle classification
            ContactObservation vehicleContact = reIdentifiedTank with
            {
                Classification = ContactClassification.Vehicle
            };
            ApplyWithContacts(replica, bytes, 8, 12, [], [vehicleContact]);
            contactViews.Sync(replica);
            Require(cv.HasEnemyModel, "Identified generic vehicle must instantiate a model.");
            Require(cv.CurrentModelClassification == ContactClassification.Vehicle, "Classification should be Vehicle.");
            Require(cv.EnemyModel!.Name == "EnemyGenericVehicleModel", "Model should be enemy generic vehicle.");
            Require(cv.EnemyModel.FindChild("HostileAffiliationMarker", true, false) is not null,
                "Enemy generic vehicle model must attach HostileAffiliationMarker.");
            Require(!cv.Marker.Visible, "Diamond marker should be hidden when generic vehicle model is active.");

            // Unsupported classification fallback retains world representation
            ContactObservation unsupportedContact = vehicleContact with
            {
                Classification = (ContactClassification)99
            };
            cv.Update(unsupportedContact, groundY: 0f, isSelected: false);
            Require(!cv.HasEnemyModel, "Unsupported classification must not have an enemy model.");
            Require(cv.EnemyModel is null, "Previous vehicle model must be cleared.");
            Require(cv.Marker.Visible, "Unsupported classification must retain generic marker diamond.");
            Require(!cv.GhostBase.Visible, "Live unsupported contact must not show ghost base.");

            // Transition from unsupported back to modeled class (Tank)
            cv.Update(reIdentifiedTank, groundY: 0f, isSelected: false);
            Require(cv.HasEnemyModel, "Re-identifying as tank after unsupported class must create tank model.");
            Require(!cv.Marker.Visible, "Diamond marker should be hidden when tank model is restored.");

            // 5. SelectionCard unit, multiple units, contact, empty
            SelectionCard card = new(catalog);
            parent.AddChild(card);

            card.UpdateEmpty();
            Require(card.TitleText.Contains("No Selection"), "Empty card should say No Selection.");

            // Unarmed recon
            OwnUnitObservation reconUnit = new(
                new EntityId(101),
                catalog.RequireDefinitionIndex("recon_vehicle"),
                new NumericsVector2(10, 10),
                0,
                Health: 60);
            card.UpdateSingleOwnUnit(reconUnit);
            Require(card.Line2Text.Contains("Armament: Unarmed"), "Recon must be formatted as Unarmed.");
            Require(card.Line1Text.Contains("60 / 60 HP"), "Health should be accurately formatted.");

            // Tank with 0 ammo
            OwnUnitObservation tankZeroAmmo = new(
                new EntityId(102),
                catalog.RequireDefinitionIndex("prototype_tank"),
                new NumericsVector2(12, 12),
                0,
                Health: 120,
                Ammunition: 0,
                ReloadTicksRemaining: 16,
                AttackStatus: AttackExecutionStatus.NoAmmo);
            card.UpdateSingleOwnUnit(tankZeroAmmo);
            Require(card.Line2Text.Contains("Depleted"), "Tank with 0 ammo must be formatted as Depleted.");
            Require(card.Line2Text.Contains("0.8s"), "Reload timer in ticks must convert to seconds.");
            Require(card.Line3Text.Contains("Out of Ammo"), "Status should show out of ammunition.");

            // Moving unit with No attack order (AttackExecutionStatus.None must not say Idle)
            OwnUnitObservation movingUnitNoAttack = new(
                new EntityId(103),
                catalog.RequireDefinitionIndex("prototype_tank"),
                new NumericsVector2(14, 14),
                0,
                NavStatus: PathRequestStatus.PathFound,
                Health: 120,
                Ammunition: 10,
                ReloadTicksRemaining: 0,
                AttackStatus: AttackExecutionStatus.None);
            card.UpdateSingleOwnUnit(movingUnitNoAttack);
            Require(card.Line3Text.Contains("No attack order"), "Moving unit with None attack status must display No attack order.");
            Require(!card.Line3Text.Contains("Idle"), "Attack status must not say Idle.");

            // Multiple units
            card.UpdateMultipleOwnUnits(3, 2, 1, 0);
            Require(card.TitleText.Contains("3 Units Selected"), "Multi-unit title mismatch.");
            Require(card.Line1Text.Contains("2 Tanks, 1 Recon"), "Multi-unit breakdown mismatch.");

            // Enemy contact inspection
            card.UpdateContact(cmdContact with { LastSeenTick = 8 }, serverTick: 28);
            Require(card.TitleText.Contains("Enemy Contact #101"), "Contact card should show contact ID.");
            Require(card.Line2Text.Contains("Command Vehicle"), "Contact card should show classification.");
            Require(card.Line3Text.Contains("Seen 1.0s ago"), "Contact card should show observation age.");
            Require(!card.Line1Text.Contains("HP") && !card.Line2Text.Contains("HP"), "Contact card must not show HP.");

            // 6. OrderNotification persistence, timeout, and submission reporting
            OrderNotification notification = new();
            parent.AddChild(notification);

            notification.ShowNotification("Server queued order #42");
            Require(notification.Visible, "Notification should be visible after ShowNotification.");
            Require(notification.CurrentText == "Server queued order #42", "Notification text mismatch.");

            // Local submission reporting verification (zero, partial, full)
            int totalSelected = 2;
            string failAttack = $"Failed to submit attack order / 0 of {totalSelected} units -> Contact #101";
            notification.ShowNotification(failAttack);
            Require(notification.CurrentText == failAttack, "Zero-submission attack message mismatch.");

            string partialAttack = $"Attack order submitted for 1 of {totalSelected} units -> Contact #101";
            notification.ShowNotification(partialAttack);
            Require(notification.CurrentText == partialAttack, "Partial-submission attack message mismatch.");

            string fullAttack = $"Attack order submitted / 2 units -> Contact #101";
            notification.ShowNotification(fullAttack);
            Require(notification.CurrentText == fullAttack, "Full-submission attack message mismatch.");

            string failMove = $"Failed to submit move order / 0 of {totalSelected} units / 72, 92 m";
            notification.ShowNotification(failMove);
            Require(notification.CurrentText == failMove, "Zero-submission move message mismatch.");

            string partialMove = $"Move order submitted for 1 of {totalSelected} units / 72, 92 m";
            notification.ShowNotification(partialMove);
            Require(notification.CurrentText == partialMove, "Partial-submission move message mismatch.");

            string fullMove = $"Move order submitted / 2 units / 72, 92 m";
            notification.ShowNotification(fullMove);
            Require(notification.CurrentText == fullMove, "Full-submission move message mismatch.");

            // Authoritative server results
            string serverQueued = "Server queued order #42";
            notification.ShowNotification(serverQueued);
            Require(notification.CurrentText == serverQueued, "Server queued message mismatch.");

            string serverRejected = "Order #42 rejected by server";
            notification.ShowNotification(serverRejected);
            Require(notification.CurrentText == serverRejected, "Server rejected message mismatch.");

            // Selection card updates do NOT wipe out order notification
            card.UpdateEmpty();
            Require(notification.Visible && notification.CurrentText == serverRejected,
                "SelectionCard update must not affect persistent OrderNotification.");

            // Advance notification time
            notification.Update(2.0);
            Require(notification.Visible, "Notification should still be visible after 2.0s.");
            notification.Update(1.2);
            Require(!notification.Visible, "Notification should expire after 3.2s total.");

            // 7. RoleBadge presentation state caching
            RoleBadge cacheTestBadge = new();
            parent.AddChild(cacheTestBadge);
            cacheTestBadge.BindUnit(new EntityId(9001), "TANK");
            cacheTestBadge.BindUnit(new EntityId(9001), "TANK");

            cacheTestBadge.BindContact(new ContactId(801), ContactState.Current, DetectionLevel.Identified, ContactClassification.Tank, 0.2f);
            cacheTestBadge.BindContact(new ContactId(801), ContactState.LastKnown, DetectionLevel.Identified, ContactClassification.Tank, 1.2f);
            cacheTestBadge.BindContact(new ContactId(801), ContactState.LastKnown, DetectionLevel.Identified, ContactClassification.Tank, 1.4f);
            cacheTestBadge.BindContact(new ContactId(801), ContactState.LastKnown, DetectionLevel.Identified, ContactClassification.Tank, 2.1f);
            parent.RemoveChild(cacheTestBadge);
            cacheTestBadge.QueueFree();

            // 8. RoleBadgeOverlay and hit testing
            Camera3D camera = new()
            {
                Projection = Camera3D.ProjectionType.Orthogonal,
                Size = 100,
                Far = 1000,
                Position = new Vector3(0, 50, 50)
            };
            parent.AddChild(camera);
            camera.LookAt(Vector3.Zero, Vector3.Up);

            RoleBadgeOverlay overlay = new(catalog);
            parent.AddChild(overlay);

            ApplyWithContacts(replica, bytes, 11, 15, [reconUnit, tankZeroAmmo], [tankContact]);
            unitViews.Sync(replica, 15);
            contactViews.Sync(replica);

            Rect2[] occludingPanels = [];
            overlay.UpdateBadges(camera, camera.Size, unitViews, contactViews, replica, 15, occludingPanels);

            Require(overlay.GetChildCount() >= 3, "Badges should be created for units and contacts.");

            // 9. Overlapping badges hit testing in reverse actual draw order
            overlay.Clear();
            RoleBadge badgeTank = new();
            badgeTank.BindUnit(new EntityId(9001), "TANK");
            badgeTank.UpdateScreenPosition(new Vector2(200, 200));
            badgeTank.Visible = true;
            overlay.AddChild(badgeTank);

            RoleBadge badgeRecon = new();
            badgeRecon.BindUnit(new EntityId(9002), "RECON");
            badgeRecon.UpdateScreenPosition(new Vector2(200, 200));
            badgeRecon.Visible = true;
            overlay.AddChild(badgeRecon);

            // In Godot, badgeRecon is child 1 and draws over badgeTank (child 0).
            // Clicking (200, 200) must return the topmost child (Recon 9002), not Tank 9001.
            Require(overlay.TryHitBadge(new Vector2(200, 200), out EntityId? hitUnit, out ContactId? hitContact),
                "Overlapping badge click should hit.");
            Require(hitUnit == new EntityId(9002),
                $"Overlapping badge should hit topmost child (Recon 9002), but got {hitUnit?.Value}.");
            Require(hitContact is null, "Hit contact should be null for unit badge.");

            // Mixed unit and contact: add contact badge on top of both
            RoleBadge badgeEnemy = new();
            badgeEnemy.BindContact(new ContactId(7001), ContactState.Current, DetectionLevel.Identified, ContactClassification.Tank, 0f);
            badgeEnemy.UpdateScreenPosition(new Vector2(200, 200));
            badgeEnemy.Visible = true;
            overlay.AddChild(badgeEnemy);

            // badgeEnemy is child 2 (drawn on top). Clicking (200, 200) must return Contact 7001.
            Require(overlay.TryHitBadge(new Vector2(200, 200), out hitUnit, out hitContact),
                "Overlapping mixed badge click should hit.");
            Require(hitContact == new ContactId(7001),
                $"Overlapping mixed badge should hit topmost child (Contact 7001), but got contact {hitContact?.Value}.");
            Require(hitUnit is null, "Hit unit should be null for contact badge.");

            // 10. Badge partially occluded by HUD panel and click rejection inside panels
            Rect2 badgeRect = new(700f, 423.1809f, 40f, 18f);
            Rect2 panel = new(316f, 382.1809f, 400f, 100f);
            Vector2 viewportSize = new(1280f, 720f);
            Require(RoleBadgeOverlay.IsOffscreenOrOccluded(badgeRect, viewportSize, [panel]),
                "Partially occluded badge rectangle must be detected as occluded.");
            Require(!RoleBadgeOverlay.IsOffscreenOrOccluded(new Rect2(750f, 423.1809f, 40f, 18f), viewportSize, [panel]),
                "Unoccluded badge must not be detected as occluded.");

            overlay.Clear();
            overlay.UpdateBadges(camera, camera.Size, unitViews, contactViews, replica, 15, [panel]);

            RoleBadge partiallyOccludedBadge = new();
            partiallyOccludedBadge.BindUnit(new EntityId(9005), "TANK");
            partiallyOccludedBadge.UpdateScreenPosition(new Vector2(720f, 432.18f));
            partiallyOccludedBadge.Visible = true;
            overlay.AddChild(partiallyOccludedBadge);

            // Click at (712, 432.18f) is inside panel rectangle (712 <= 716). Must be rejected!
            Require(!overlay.TryHitBadge(new Vector2(712f, 432.18f), out _, out _),
                "Click inside occluding panel rectangle must be rejected.");

            // Unoccluded badge outside panel is selectable
            RoleBadge unoccludedBadge = new();
            unoccludedBadge.BindUnit(new EntityId(9006), "RECON");
            unoccludedBadge.UpdateScreenPosition(new Vector2(800f, 432.18f));
            unoccludedBadge.Visible = true;
            overlay.AddChild(unoccludedBadge);

            Require(overlay.TryHitBadge(new Vector2(800f, 432.18f), out EntityId? hitUnoccluded, out _),
                "Click on unoccluded badge must succeed.");
            Require(hitUnoccluded == new EntityId(9006), "Unoccluded badge hit mismatch.");

            // Occluded badge (Visible = false) must not be selectable even on exposed portion
            partiallyOccludedBadge.Visible = false;
            Require(!overlay.TryHitBadge(new Vector2(725f, 432.18f), out _, out _),
                "Occluded badge must not be selectable.");

            // Clear overlay
            overlay.Clear();
            Require(overlay.GetChildCount() == 0, "Overlay Clear() should remove all badge nodes.");
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
