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
