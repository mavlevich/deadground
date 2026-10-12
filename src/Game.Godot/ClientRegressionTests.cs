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
            CheckCameraRig(scene);
            CheckDisplaySettings(scene);
            CheckCombatEffectsFramePacing(scene);
            GD.Print("M0_CLIENT_REGRESSIONS_OK view_lifecycle contact_lifecycle hud_assignment rectangular_grids combat_effects configured_health camera_rig display_settings combat_frame_pacing");
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

            // Finding 6: Tracer longitudinal Z scale preserved during animation
            float shotDist = new Vector3(20, 1.9f, 20).DistanceTo(new Vector3(40, 1.5f, 20));
            Require(MathF.Abs(effects.Tracers[0].Mesh.Scale.Z - shotDist) < 0.05f,
                $"Tracer initial scale Z was {effects.Tracers[0].Mesh.Scale.Z}, expected {shotDist}");

            effects.Update(0.05);
            unitViews.UpdateRecoil(0.05);

            Require(MathF.Abs(effects.Tracers[0].Mesh.Scale.Z - shotDist) < 0.05f,
                $"Tracer longitudinal scale Z collapsed to {effects.Tracers[0].Mesh.Scale.Z} after update, expected {shotDist}");
            Require(effects.Tracers[0].Mesh.Scale.X < 1.0f,
                "Tracer transverse scale was not scaled down during animation.");

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

            float initialDistance = new Vector3(20, 1.9f, 20).DistanceTo(new Vector3(40, 1.5f, 20));

            // Run updates at high frame rate (144 FPS -> dt = 1/144s)
            for (int i = 0; i < 5; i++)
            {
                effects.Update(1.0 / 144.0);
                unitViews.UpdateRecoil(1.0 / 144.0);
            }

            Require(MathF.Abs(effects.Tracers[0].Mesh.Scale.Z - initialDistance) < 0.05f,
                "Tracer longitudinal scale Z deviated during high-fps updates.");

            // Run update at low frame rate (30 FPS -> dt = 1/30s)
            effects.Update(1.0 / 30.0);
            unitViews.UpdateRecoil(1.0 / 30.0);

            Require(MathF.Abs(effects.Tracers[0].Mesh.Scale.Z - initialDistance) < 0.05f,
                "Tracer longitudinal scale Z deviated after low-fps update.");
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
