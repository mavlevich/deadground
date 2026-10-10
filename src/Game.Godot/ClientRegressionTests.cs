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
            CheckRectangularGrid(5, 3);
            CheckRectangularGrid(3, 5);
            GD.Print("M0_CLIENT_REGRESSIONS_OK view_lifecycle rectangular_grids");
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

    private static void Apply(ClientReplica replica, byte[] bytes, uint sequence, int tick, OwnUnitObservation[] units)
    {
        int size = WireProtocol.WriteSnapshot(bytes, sequence, tick, units);
        Require(replica.TryApply(bytes.AsSpan(0, size)), "Regression snapshot was rejected.");
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
