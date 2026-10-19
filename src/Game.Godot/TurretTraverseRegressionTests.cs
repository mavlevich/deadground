using DeadGround.Engine;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

// Contact turrets follow the snapshot yaw through the turret node found when the model is built,
// and the cached nodes move to the new model when the contact's type changes.
internal static class TurretTraverseRegressionTests
{
    internal static void Run(Node3D scene)
    {
        Node3D parent = new();
        scene.AddChild(parent);
        try
        {
            DeadGround.Rules.Ops.UnitCatalog catalog = DeadGround.Rules.Ops.ScenarioContent.LoadCatalog("battle");
            ContactView view = new(() => "soviet", def => def < catalog.Units.Length ? catalog.Units[def].Id : null);
            parent.AddChild(view.Root);

            ContactObservation t62 = new(new ContactId(301), ContactState.Current, ContactClassification.Tank,
                catalog.RequireDefinitionIndex("t62"), new NumericsVector2(25, 25), 10, 1.0f);
            view.Update(t62, 0f, false, 1.0f);
            Node3D? firstTurret = view.Turret;
            Require(firstTurret is not null && view.EnemyModel!.IsAncestorOf(firstTurret),
                "A contact tank must find its turret node when the model is built.");
            Require(Mathf.IsEqualApprox(firstTurret!.Rotation.Y, 1.0f), $"Turret yaw must follow the snapshot, got {firstTurret.Rotation.Y}.");
            view.Update(t62, 0f, false, -0.5f);
            Require(Mathf.IsEqualApprox(firstTurret.Rotation.Y, -0.5f), "Turret yaw must follow later snapshots.");
            Require(view.TryGetMuzzleTransform(out _), "A contact tank must expose its muzzle for shot effects.");

            ContactObservation m60 = t62 with { Type = catalog.RequireDefinitionIndex("m60a1"), TurretYaw = -2.0f };
            view.Update(m60, 0f, false, -2.0f);
            Require(view.Turret is not null && view.Turret != firstTurret && view.EnemyModel!.IsAncestorOf(view.Turret),
                "A new model must bring its own turret node, not keep the freed one.");
            Require(Mathf.IsEqualApprox(view.Turret!.Rotation.Y, -2.0f), "The new turret must take the snapshot yaw.");
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
