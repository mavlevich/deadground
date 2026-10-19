using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

// Hulls and turrets both take their angles from the snapshot. A turret that rides on the hull keeps
// its angle to the hull through a turn, and contact turret nodes are found once per model.
internal static class TurretTraverseRegressionTests
{
    internal static void Run(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            CheckContactTurrets(parent);
            CheckOwnHullTurn(parent);
        }
        finally
        {
            parent.QueueFree();
        }
    }

    private static void CheckContactTurrets(Node3D parent)
    {
        DeadGround.Rules.Ops.UnitCatalog catalog = DeadGround.Rules.Ops.ScenarioContent.LoadCatalog("battle");
        ContactView view = new(() => "soviet", def => def < catalog.Units.Length ? catalog.Units[def].Id : null);
        parent.AddChild(view.Root);

        ContactObservation t62 = new(new ContactId(301), ContactState.Current, ContactClassification.Tank,
            catalog.RequireDefinitionIndex("t62"), new NumericsVector2(25, 25), 10, TurretYaw: 1.0f, HullHeading: 1.0f);
        view.Update(t62, 0f, false);
        Node3D? firstTurret = view.Turret;
        Require(firstTurret is not null && view.EnemyModel!.IsAncestorOf(firstTurret),
            "A contact tank must find its turret node when the model is built.");
        RequireYaw(view.EnemyModel!.GlobalRotation.Y, 1.0f, "A contact hull must take the snapshot heading");
        RequireYaw(firstTurret!.GlobalRotation.Y, 1.0f, "Turret yaw must follow the snapshot");
        Require(view.TryGetMuzzleTransform(out _), "A contact tank must expose its muzzle for shot effects.");

        // The hull turns east, the gun keeps its world bearing.
        view.Update(t62 with { TurretYaw = -0.5f, HullHeading = -Mathf.Pi / 2f }, 0f, false);
        RequireYaw(view.EnemyModel!.GlobalRotation.Y, -Mathf.Pi / 2f, "A turned contact hull must take the snapshot heading");
        RequireYaw(firstTurret.GlobalRotation.Y, -0.5f, "Turret yaw must stay a world bearing on a turned hull");

        ContactObservation m60 = t62 with { Type = catalog.RequireDefinitionIndex("m60a1"), TurretYaw = -2.0f };
        view.Update(m60, 0f, false);
        Require(view.Turret is not null && view.Turret != firstTurret && view.EnemyModel!.IsAncestorOf(view.Turret),
            "A new model must bring its own turret node, not keep the freed one.");
        RequireYaw(view.Turret!.GlobalRotation.Y, -2.0f, "The new turret must take the snapshot yaw");
    }

    // Snapshots of a tank turning on the march with no target: the sim carries the gun with the hull.
    // Sampled between snapshots, the gun must keep its angle to the hull, not lag behind or run ahead.
    private static void CheckOwnHullTurn(Node3D parent)
    {
        UnitViewSet views = new(parent, HeightMap.CreatePrototype());
        try
        {
            ClientReplica replica = new();
            byte[] bytes = new byte[WireProtocol.MaxMessageSize];
            ushort tankType = UnitViewSet.GetDefaultCatalog().RequireDefinitionIndex("prototype_tank");
            OwnUnitObservation tank = new(new EntityId(401), tankType, new NumericsVector2(10, 10), 0,
                TurretYaw: 0.3f, HullHeading: 0f);
            Apply(replica, bytes, 1, 0, tank);
            Apply(replica, bytes, 2, 2, tank with { TurretYaw = 0.3f + 2.5f, HullHeading = 2.5f });
            views.Sync(replica, 0);
            UnitView view = views.Views[tank.Id];
            Require(view.Model.FindChild("TurretYaw", true, false) is not null, "The test tank has no turret.");
            RequireYaw(view.Model.Rotation.Y, 0f, "The hull must take the snapshot heading");
            RequireYaw(view.TurretRotation.Y, 0.3f, "The gun must sit at its snapshot angle to the hull");

            for (double tick = 0.25; tick <= 2.0; tick += 0.25)
            {
                views.Sync(replica, tick);
                float expectedHull = 2.5f * (float)(tick / 2.0);
                RequireYaw(view.Heading, expectedHull, $"Render tick {tick}: hull heading");
                RequireYaw(view.TurretRotation.Y, 0.3f, $"Render tick {tick}: a gun riding on the hull must not move on it");
            }
        }
        finally
        {
            views.Clear();
        }
    }

    private static void Apply(ClientReplica replica, byte[] bytes, uint sequence, int tick, OwnUnitObservation unit)
    {
        int length = WireProtocol.WriteSnapshot(bytes, sequence, tick, [unit]);
        Require(replica.TryApply(bytes.AsSpan(0, length)), $"Snapshot {sequence} was rejected.");
    }

    private static void RequireYaw(float yaw, float expected, string what)
    {
        float error = Mathf.Abs(Mathf.Wrap(yaw - expected, -Mathf.Pi, Mathf.Pi));
        Require(error < 0.001f, $"{what}: yaw {yaw}, want {expected}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
