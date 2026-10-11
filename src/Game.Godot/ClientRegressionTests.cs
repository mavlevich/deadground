using DeadGround.Engine;
using DeadGround.Engine.Net;
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
            GD.Print("M0_CLIENT_REGRESSIONS_OK view_lifecycle contact_lifecycle hud_assignment rectangular_grids");
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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
