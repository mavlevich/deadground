using DeadGround.Engine;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace DeadGround.Client;

// Renders movement dust, muzzle blast dust, barrel smoke and hull rock in the combat scenario
// to PNG files, for visual review. Run with: --scenario combat --vehicle-effects-capture <dir>
internal static class VehicleEffectsProbe
{
    public static async System.Threading.Tasks.Task RunCapture(Main main, string outputDir)
    {
        System.IO.Directory.CreateDirectory(outputDir);
        main.CameraRig.SetDefaultView(new Vector3(26f, 0f, 46f), 30f);
        main.CameraRig.ResetToDefault();

        // Tank 3 drives east across the view, stops and then fires further east.
        main.Session.Move(new EntityId(3), new NumericsVector2(30f, 50f));
        await WaitSeconds(main, 1.6);
        CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, "fx_0_driving.png"));
        await WaitSeconds(main, 2.4);

        UnitView shooter = main.UnitViews.Views[new EntityId(3)];
        Vector3 target = new(shooter.Root.Position.X + 40f, 1.5f, shooter.Root.Position.Z - 6f);
        shooter.AimAt(new Vector3(target.X, 0f, target.Z));
        await WaitSeconds(main, 0.1);
        Vector3 origin = shooter.TryGetMuzzlePosition(out Vector3 muzzle) ? muzzle : shooter.Root.Position + Vector3.Up * 1.8f;
        CombatEventObservation shot = new(
            EventId: 9901,
            ServerTick: 0,
            Kind: CombatEventKind.ShotFired,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            Origin: new NumericsVector3(origin.X, origin.Y, origin.Z),
            Target: new NumericsVector3(target.X, target.Y, target.Z),
            ShooterUnitId: new EntityId(3));
        main.CombatEffects.ProcessEvents([shot], main.UnitViews);

        double elapsed = 0;
        foreach ((double at, string name) in new[] { (0.05, "fx_1_shot"), (0.25, "fx_2_blast"), (0.6, "fx_3_dust"), (1.2, "fx_4_smoke") })
        {
            await WaitSeconds(main, at - elapsed);
            elapsed = at;
            CombatFeedbackProbe.SaveViewport(main, System.IO.Path.Combine(outputDir, name + ".png"));
            GD.Print($"VEHICLE_EFFECTS_FRAME {name} rock={shooter.RockAngle:F4}");
        }

        GD.Print($"VEHICLE_EFFECTS_CAPTURE_OK: {outputDir}");
        main.GetTree().Quit(0);
    }

    private static async System.Threading.Tasks.Task WaitSeconds(Main main, double seconds)
    {
        double waited = 0;
        while (waited < seconds)
        {
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            waited += main.GetProcessDeltaTime();
        }
    }
}
