using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

internal static class VehicleWreckRegressionTests
{
    internal static void Run(Node3D scene)
    {
        CheckWreckVisuals(scene);
        CheckWreckLodsAndTurret(scene);
        CheckWreckViewSetLifecycle(scene);
        CheckDroppedDestructionEventReconstruction(scene);
        CheckBridgeDeckElevationPlacement(scene);
        CheckSmokeAndFirePhases(scene);
        CheckDuplicateEventAndCameraReturn(scene);
        CheckActiveEmitterLimitAndHysteresis(scene);
        CheckUnobservedWrecksStayCold(scene);
        CheckBridgeAndGroundWrecksAtSamePosition(scene);
        CheckCombinedEmitterLimitCountingFireAndSmoke(scene);
        CheckDeduplicationAcrossSharedSets(scene);
    }

    private static void CheckWreckVisuals(Node3D scene)
    {
        WreckView soviet = new("prototype_tank", "soviet");
        WreckView us = new("prototype_tank", "us");
        scene.AddChild(soviet.Root);
        scene.AddChild(us.Root);

        try
        {
            Require(soviet.HasVehicleModel, "Soviet wreck must have a loaded vehicle visual model.");
            Require(us.HasVehicleModel, "US wreck must have a loaded vehicle visual model.");

            // Check that T-62 and M60 models have distinct meshes/nodes
            Node3D sovietModel = soviet.Model;
            Node3D usModel = us.Model;
            Require(sovietModel != usModel, "Soviet and US wreck models must be distinct instances.");

            // Verify charred dark matte material is applied to geometry instances
            bool foundCharredSoviet = false;
            CheckCharredRecursive(sovietModel, ref foundCharredSoviet);
            Require(foundCharredSoviet, "Soviet wreck must have charred dark matte material applied to geometry.");

            bool foundCharredUs = false;
            CheckCharredRecursive(usModel, ref foundCharredUs);
            Require(foundCharredUs, "US wreck must have charred dark matte material applied to geometry.");

            // Wrecks must not have health bars, selection rings, or badges attached
            Require(soviet.Root.FindChild("SelectionRing", recursive: true, owned: false) is null, "Wreck must not have a selection ring.");
            Require(soviet.Root.FindChild("HealthBar", recursive: true, owned: false) is null, "Wreck must not have a health bar.");
            Require(soviet.Root.FindChild("RoleBadge", recursive: true, owned: false) is null, "Wreck must not have a role badge.");
        }
        finally
        {
            scene.RemoveChild(soviet.Root);
            scene.RemoveChild(us.Root);
            soviet.Root.QueueFree();
            us.Root.QueueFree();
        }
    }

    private static void CheckWreckLodsAndTurret(Node3D scene)
    {
        WreckView view = new("prototype_tank", "soviet");
        scene.AddChild(view.Root);

        try
        {
            WreckObservation wreck = new(
                Id: new WreckId(42),
                Definition: 1,
                Position: new System.Numerics.Vector2(120f, 80f),
                SupportSurface: 0,
                HullHeading: 1.0f,
                TurretYaw: 2.5f);

            view.Update(wreck, groundY: 10f);

            Require(Mathf.Abs(view.Root.Position.X - 120f) < 0.01f, "Wreck root X position mismatch.");
            Require(Mathf.Abs(view.Root.Position.Y - 10f) < 0.01f, "Wreck root Y position mismatch.");
            Require(Mathf.Abs(view.Root.Position.Z - 80f) < 0.01f, "Wreck root Z position mismatch.");
            Require(Mathf.Abs(view.Model.Rotation.Y - 1.0f) < 0.01f, "Wreck hull heading mismatch.");

            // Verify LOD switching applies charred material
            view.UpdateLod(75f);
            Require(view.ActiveLod == 1, "RTS camera distance must switch wreck to LOD1.");

            view.UpdateLod(160f);
            Require(view.ActiveLod == 2, "Distant RTS camera must switch wreck to LOD2.");

            view.UpdateLod(30f);
            Require(view.ActiveLod == 0, "Close camera distance must return wreck to LOD0.");
        }
        finally
        {
            scene.RemoveChild(view.Root);
            view.Root.QueueFree();
        }
    }

    private static void CheckWreckViewSetLifecycle(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);

        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            WreckViewSet set = new(parent, map, def => "prototype_tank", () => "soviet");

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            WreckObservation[] wrecks =
            [
                new(new WreckId(1), 1, new System.Numerics.Vector2(50f, 50f), 0, 0f, 0f),
                new(new WreckId(2), 1, new System.Numerics.Vector2(80f, 80f), 0, 1f, 1f)
            ];

            int size = WireProtocol.WriteSnapshot(bytes, 1, 10, [], [], [], default, wrecks);
            replica.TryApply(bytes.AsSpan(0, size));

            set.Sync(replica);
            Require(set.Views.Count == 2, "WreckViewSet must have 2 views after sync.");
            Require(set.Views.ContainsKey(new WreckId(1)), "Wreck 1 must exist.");
            Require(set.Views.ContainsKey(new WreckId(2)), "Wreck 2 must exist.");

            // Repeated sync must not duplicate views
            set.Sync(replica);
            Require(set.Views.Count == 2, "Repeated sync must not duplicate wreck views.");

            // Remove wreck 1 from replica snapshot
            WreckObservation[] reduced =
            [
                new(new WreckId(2), 1, new System.Numerics.Vector2(80f, 80f), 0, 1f, 1f)
            ];
            int size2 = WireProtocol.WriteSnapshot(bytes, 2, 20, [], [], [], default, reduced);
            replica.TryApply(bytes.AsSpan(0, size2));

            set.Sync(replica);
            Require(set.Views.Count == 1, "Wreck 1 must be removed after being omitted from snapshot.");
            Require(set.Views.ContainsKey(new WreckId(2)), "Wreck 2 must remain.");

            // Clear should remove all views
            set.Clear();
            Require(set.Views.Count == 0, "WreckViewSet.Clear must empty views dictionary.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckDroppedDestructionEventReconstruction(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);

        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            WreckViewSet set = new(parent, map, def => "prototype_tank", () => "soviet");

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            WreckObservation[] wrecks =
            [
                new(new WreckId(10), 1, new System.Numerics.Vector2(100f, 100f), 0, 0f, 0f)
            ];

            // Snapshot contains wreck, but events span is empty (event was lost/dropped)
            int size = WireProtocol.WriteSnapshot(bytes, 1, 5, [], [], [], default, wrecks);
            replica.TryApply(bytes.AsSpan(0, size));

            Require(replica.EventCount == 0, "Replica must not have received any combat events.");
            Require(replica.WreckCount == 1, "Replica must have received wreck from snapshot.");

            set.Sync(replica);
            Require(set.Views.Count == 1, "WreckViewSet must successfully reconstruct wreck without UnitDestroyed event.");
            Require(set.Views.ContainsKey(new WreckId(10)), "Reconstructed wreck ID must match snapshot.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckBridgeDeckElevationPlacement(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);

        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            BridgeStructure bridge = new(
                new StructureId(1),
                new System.Numerics.Vector2(50f, 20f),
                new System.Numerics.Vector2(50f, 80f),
                width: 10f,
                startHeight: 25.0f,
                endHeight: 25.0f,
                thickness: 1.5f);

            StaticGeometry geometry = new(map, structures: [], bridges: [bridge]);
            WreckViewSet set = new(parent, map, def => "prototype_tank", () => "soviet", geometry);

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            WreckObservation[] bridgeWreck =
            [
                new(new WreckId(99), 1, new System.Numerics.Vector2(50f, 50f), SupportSurface: 1, 0f, 0f)
            ];

            int size = WireProtocol.WriteSnapshot(bytes, 1, 10, [], [], [], default, bridgeWreck);
            replica.TryApply(bytes.AsSpan(0, size));

            set.Sync(replica);
            Require(set.Views.TryGetValue(new WreckId(99), out WreckView? view), "Bridge wreck view must exist.");
            Require(Mathf.Abs(view!.Root.Position.Y - 25.0f) < 0.05f, "Bridge wreck view Y elevation must match bridge deck (25.0m).");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckSmokeAndFirePhases(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            WreckViewSet set = new(parent, map, def => "prototype_tank", () => "soviet");

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            WreckObservation[] wrecks = [new(new WreckId(1), 1, new System.Numerics.Vector2(50f, 50f), 0, 0f, 0f)];
            int size = WireProtocol.WriteSnapshot(bytes, 1, 100, [], [], [], default, wrecks);
            replica.TryApply(bytes.AsSpan(0, size));
            set.Sync(replica);

            CombatEventObservation[] events =
            [
                new(EventId: 1, ServerTick: 100, CombatEventKind.UnitDestroyed, 1, Origin: System.Numerics.Vector3.Zero, Target: new System.Numerics.Vector3(50f, 0f, 50f))
            ];
            set.ProcessEvents(events, serverTick: 100);

            WreckView view = set.Views[new WreckId(1)];
            Require(view.DestructionTick == 100, "Wreck view must record destruction tick from observed event.");

            // Age 2 seconds (tick 100 + 2s): fresh kill phase -> fire and smoke active
            set.UpdateEffects(delta: 0.1, cameraFocus: new Vector3(50f, 0f, 50f), cameraSize: 50f, serverTick: 100 + 2 * Simulation.TickRate);
            Require(view.IsBurning, "Wreck must be burning during fresh kill phase (age < 8s).");
            Require(view.IsSmoking, "Wreck must be smoking during fresh kill phase.");
            Require(set.ActiveSmokeCount == 1, "Active smoke count must be 1.");

            // Age 20 seconds: medium smoldering phase -> fire stopped, smoke continues
            set.UpdateEffects(delta: 0.1, cameraFocus: new Vector3(50f, 0f, 50f), cameraSize: 50f, serverTick: 100 + 20 * Simulation.TickRate);
            Require(!view.IsBurning, "Wreck fire must stop after 8 seconds of age.");
            Require(view.IsSmoking, "Wreck must still be smoldering with smoke at age 20s.");

            // Age 50 seconds: cold phase -> fire and smoke stopped
            set.UpdateEffects(delta: 0.1, cameraFocus: new Vector3(50f, 0f, 50f), cameraSize: 50f, serverTick: 100 + 50 * Simulation.TickRate);
            Require(!view.IsBurning, "Cold wreck must not be burning.");
            Require(!view.IsSmoking, "Cold wreck must not be smoking after 45 seconds.");
            Require(set.ActiveSmokeCount == 0, "Active smoke count must be 0 for cold wreck.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckDuplicateEventAndCameraReturn(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            WreckViewSet set = new(parent, map, def => "prototype_tank", () => "soviet");

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            WreckObservation[] wrecks = [new(new WreckId(2), 1, new System.Numerics.Vector2(50f, 50f), 0, 0f, 0f)];
            int size = WireProtocol.WriteSnapshot(bytes, 1, 100, [], [], [], default, wrecks);
            replica.TryApply(bytes.AsSpan(0, size));
            set.Sync(replica);

            CombatEventObservation[] ev1 = [new(1, 100, CombatEventKind.UnitDestroyed, 1, System.Numerics.Vector3.Zero, new System.Numerics.Vector3(50f, 0f, 50f))];
            set.ProcessEvents(ev1, serverTick: 100);

            WreckView view = set.Views[new WreckId(2)];
            Require(view.DestructionTick == 100, "Destruction tick must be set.");

            // Advance to age 30s -> smoke active, fire stopped
            int tick30s = 100 + 30 * Simulation.TickRate;
            set.UpdateEffects(0.1, new Vector3(50f, 0f, 50f), 50f, serverTick: tick30s);
            Require(!view.IsBurning && view.IsSmoking, "Must be smoldering at age 30s.");

            // Duplicate event arriving at age 30s must NOT restart the fire or reset DestructionTick!
            CombatEventObservation[] evDup = [new(2, tick30s, CombatEventKind.UnitDestroyed, 1, System.Numerics.Vector3.Zero, new System.Numerics.Vector3(50f, 0f, 50f))];
            set.ProcessEvents(evDup, serverTick: tick30s);
            Require(view.DestructionTick == 100, "Duplicate destruction event must NOT overwrite existing destruction tick.");
            set.UpdateEffects(0.1, new Vector3(50f, 0f, 50f), 50f, serverTick: tick30s);
            Require(!view.IsBurning, "Duplicate event must NOT restart the fire.");

            // Zoom out to LOD2 (camera size 120f) -> smoke must be disabled
            set.UpdateEffects(0.1, new Vector3(50f, 0f, 50f), 120f, serverTick: tick30s);
            Require(!view.IsSmoking, "Strategic zoom (LOD2) must disable smoke emissions.");

            // Zoom back in (camera size 50f) -> smoke resumes from current age (not restarted)
            set.UpdateEffects(0.1, new Vector3(50f, 0f, 50f), 50f, serverTick: tick30s + 10);
            Require(view.IsSmoking, "Zooming back in must resume smoldering at current age.");
            Require(!view.IsBurning, "Resumed effect must not restart flame.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckActiveEmitterLimitAndHysteresis(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            WreckViewSet set = new(parent, map, def => "prototype_tank", () => "soviet");

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            const int totalWrecks = 24;
            WreckObservation[] wrecks = new WreckObservation[totalWrecks];
            for (int i = 0; i < totalWrecks; i++)
            {
                wrecks[i] = new WreckObservation(new WreckId((uint)(i + 1)), 1, new System.Numerics.Vector2(20f + i * 5f, 50f), 0, 0f, 0f);
            }

            int size = WireProtocol.WriteSnapshot(bytes, 1, 100, [], [], [], default, wrecks);
            replica.TryApply(bytes.AsSpan(0, size));
            set.Sync(replica);

            for (int i = 0; i < totalWrecks; i++)
            {
                set.Views[new WreckId((uint)(i + 1))].DestructionTick = 100;
            }

            // At age 20s (smoldering only), each wreck uses 1 emitter, so exactly 16 wrecks smoke
            int smolderTick = 100 + 20 * Simulation.TickRate;
            set.UpdateEffects(0.1, cameraFocus: new Vector3(20f, 0f, 50f), cameraSize: 50f, serverTick: smolderTick);

            Require(set.ActiveSmokeCount == WreckViewSet.MaxActiveEmitters,
                $"Active smoke emitters ({set.ActiveSmokeCount}) must strictly cap at {WreckViewSet.MaxActiveEmitters}.");
            Require(set.ActiveEmitterCount == WreckViewSet.MaxActiveEmitters,
                $"Active emitter count ({set.ActiveEmitterCount}) must strictly cap at {WreckViewSet.MaxActiveEmitters}.");

            Require(set.Views[new WreckId(1)].IsSmoking, "Nearest wreck must have active smoke emitter.");
            Require(!set.Views[new WreckId(24)].IsSmoking, "Distant wreck beyond active emitter limit must not be smoking.");

            // Hysteresis: moving camera slightly (3m) toward wreck 17 must not cause thrashing on wreck 1
            set.UpdateEffects(0.1, cameraFocus: new Vector3(23f, 0f, 50f), cameraSize: 50f, serverTick: smolderTick + 1);
            Require(set.ActiveSmokeCount == WreckViewSet.MaxActiveEmitters, "Active smoke count must remain capped.");
            Require(set.Views[new WreckId(1)].IsSmoking, "Wreck 1 must retain smoke due to distance hysteresis bonus.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckUnobservedWrecksStayCold(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            WreckViewSet set = new(parent, map, def => "prototype_tank", () => "soviet");

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            WreckObservation[] wrecks = [new(new WreckId(77), 1, new System.Numerics.Vector2(50f, 50f), 0, 0f, 0f)];
            int size = WireProtocol.WriteSnapshot(bytes, 1, 500, [], [], [], default, wrecks);
            replica.TryApply(bytes.AsSpan(0, size));
            set.Sync(replica);

            WreckView view = set.Views[new WreckId(77)];
            Require(view.DestructionTick == 0, "Unobserved wreck discovered from fog must have DestructionTick == 0.");

            set.UpdateEffects(0.1, new Vector3(50f, 0f, 50f), 50f, serverTick: 500);
            Require(!view.IsBurning, "Unobserved wreck must NOT burn.");
            Require(!view.IsSmoking, "Unobserved wreck must NOT smoke.");
            Require(set.ActiveSmokeCount == 0, "Active smoke count must remain 0 for unobserved wreck.");

            set.Clear();
            Require(set.Views.Count == 0, "Clear() must purge all wreck views.");
            Require(set.ActiveSmokeCount == 0, "Clear() must leave active smoke count at 0.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckBridgeAndGroundWrecksAtSamePosition(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            WreckViewSet set = new(parent, map, def => "prototype_tank", () => "soviet");

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            // Wreck 101 on bridge deck (SupportSurface: 1), Wreck 102 under bridge on ground (SupportSurface: 0)
            WreckObservation[] wrecks =
            [
                new(new WreckId(101), 1, new System.Numerics.Vector2(50f, 50f), SupportSurface: 1, 0f, 0f),
                new(new WreckId(102), 1, new System.Numerics.Vector2(50f, 50f), SupportSurface: 0, 0f, 0f)
            ];

            int size = WireProtocol.WriteSnapshot(bytes, 1, 100, [], [], [], default, wrecks);
            replica.TryApply(bytes.AsSpan(0, size));
            set.Sync(replica);

            // Destruction event specifically for Wreck 101 (bridge deck)
            CombatEventObservation ev = new(
                1,
                100,
                CombatEventKind.UnitDestroyed,
                1,
                System.Numerics.Vector3.Zero,
                new System.Numerics.Vector3(50f, 10f, 50f),
                new WreckId(101));

            set.ProcessEvents([ev], serverTick: 100);
            set.UpdateEffects(0.1, cameraFocus: new Vector3(50f, 10f, 50f), cameraSize: 50f, serverTick: 140);

            WreckView bridgeView = set.Views[new WreckId(101)];
            WreckView groundView = set.Views[new WreckId(102)];

            Require(bridgeView.DestructionTick == 100, "Bridge wreck 101 must match destruction event by WreckId.");
            Require(bridgeView.IsBurning, "Bridge wreck 101 must be burning.");
            Require(bridgeView.IsSmoking, "Bridge wreck 101 must be smoking.");

            Require(groundView.DestructionTick == 0, "Ground wreck 102 at same X/Z position must NOT match event for Wreck 101.");
            Require(!groundView.IsBurning, "Ground wreck 102 must NOT burn.");
            Require(!groundView.IsSmoking, "Ground wreck 102 must NOT smoke.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckCombinedEmitterLimitCountingFireAndSmoke(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            WreckViewSet set = new(parent, map, def => "prototype_tank", () => "soviet");

            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            const int totalWrecks = 10;
            WreckObservation[] wrecks = new WreckObservation[totalWrecks];
            for (int i = 0; i < totalWrecks; i++)
            {
                wrecks[i] = new WreckObservation(new WreckId((uint)(201 + i)), 1, new System.Numerics.Vector2(30f + i * 5f, 50f), 0, 0f, 0f);
            }

            int size = WireProtocol.WriteSnapshot(bytes, 1, 100, [], [], [], default, wrecks);
            replica.TryApply(bytes.AsSpan(0, size));
            set.Sync(replica);

            // All 10 wrecks destroyed at tick 100; at tick 140 (age 2.0s), each wants fire AND smoke (2 emitters)
            for (int i = 0; i < totalWrecks; i++)
            {
                set.Views[new WreckId((uint)(201 + i))].DestructionTick = 100;
            }

            set.UpdateEffects(0.1, cameraFocus: new Vector3(30f, 0f, 50f), cameraSize: 50f, serverTick: 140);

            // Limit 16 must count both fire and smoke: 8 burning wrecks * 2 emitters = 16 emitters
            Require(set.ActiveEmitterCount == WreckViewSet.MaxActiveEmitters,
                $"Active emitter count ({set.ActiveEmitterCount}) must strictly cap at {WreckViewSet.MaxActiveEmitters}.");
            Require(set.ActiveSmokeCount == 8,
                $"Active smoke count ({set.ActiveSmokeCount}) must equal 8 when 8 wrecks burn (each using 2 emitters).");

            int burningCount = 0;
            foreach (WreckView view in set.Views.Values)
            {
                if (view.IsBurning) burningCount++;
            }
            Require(burningCount == 8, $"Exactly 8 wrecks must be burning under 16-emitter budget, got {burningCount}.");

            // Wrecks beyond the budget (indexes 8 and 9) must have 0 emitters
            Require(!set.Views[new WreckId(209)].IsBurning, "Wreck 9 beyond combined emitter budget must not burn.");
            Require(!set.Views[new WreckId(209)].IsSmoking, "Wreck 9 beyond combined emitter budget must not smoke.");
            Require(!set.Views[new WreckId(210)].IsBurning, "Wreck 10 beyond combined emitter budget must not burn.");
            Require(!set.Views[new WreckId(210)].IsSmoking, "Wreck 10 beyond combined emitter budget must not smoke.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckDeduplicationAcrossSharedSets(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            HeightMap map = HeightMap.CreatePrototype();
            WreckViewSet sovietSet = new(parent, map, def => "prototype_tank", () => "soviet");
            WreckViewSet usSet = new(parent, map, def => "prototype_tank", () => "us");

            ClientReplica sovietReplica = new();
            ClientReplica usReplica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];

            // Same wreck 301 observed by both Soviet and US sides
            WreckObservation[] sharedWrecks = [new(new WreckId(301), 1, new System.Numerics.Vector2(60f, 60f), 0, 0f, 0f)];

            int size = WireProtocol.WriteSnapshot(bytes, 1, 100, [], [], [], default, sharedWrecks);
            sovietReplica.TryApply(bytes.AsSpan(0, size));
            usReplica.TryApply(bytes.AsSpan(0, size));

            sovietSet.Sync(sovietReplica);
            usSet.Sync(usReplica);

            CombatEventObservation ev = new(
                10,
                100,
                CombatEventKind.UnitDestroyed,
                1,
                System.Numerics.Vector3.Zero,
                new System.Numerics.Vector3(60f, 0f, 60f),
                new WreckId(301));

            sovietSet.ProcessEvents([ev], serverTick: 100);
            usSet.ProcessEvents([ev], serverTick: 100);

            // Update scene effects with both sets together
            WreckViewSet.UpdateSceneEffects([sovietSet, usSet], 0.1, new Vector3(60f, 0f, 60f), 50f, serverTick: 140, maxActiveEmitters: 16);

            WreckView sovietView = sovietSet.Views[new WreckId(301)];
            WreckView usView = usSet.Views[new WreckId(301)];

            Require(sovietView.IsBurning && sovietView.IsSmoking, "First set's wreck view must receive active effects.");
            Require(!usView.IsBurning && !usView.IsSmoking, "Duplicate wreck in second set must be deduplicated and have NO effects.");

            int totalSceneEmitters = sovietSet.ActiveEmitterCount + usSet.ActiveEmitterCount;
            Require(totalSceneEmitters == 2, $"Total scene emitters for shared wreck must be exactly 2 (single wreck), got {totalSceneEmitters}.");
        }
        finally
        {
            scene.RemoveChild(parent);
            parent.QueueFree();
        }
    }

    private static void CheckCharredRecursive(Node node, ref bool foundCharred)
    {
        if (node is GeometryInstance3D geom && geom.MaterialOverride is StandardMaterial3D mat)
        {
            if (mat.Roughness >= 0.85f && mat.AlbedoColor.R < 0.2f && mat.AlbedoColor.G < 0.2f && mat.AlbedoColor.B < 0.2f)
            {
                foundCharred = true;
                return;
            }
        }

        int count = node.GetChildCount();
        for (int i = 0; i < count; i++)
        {
            CheckCharredRecursive(node.GetChild(i), ref foundCharred);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
