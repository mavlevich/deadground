using System.IO;
using DeadGround.Engine;
using DeadGround.Sandbox;
using Godot;
using NumericsVector2 = System.Numerics.Vector2;

namespace DeadGround.Client;

// Captures real-time rendered screenshots of T-62 TDA tactical smoke screens across
// close bloom, play zoom, low-pitch angle, and wide tactical zoom for visual review.
// Run with: --scenario ustye-raid --smoke-capture <dir> [--env overcast|clear_afternoon]
internal static class SmokeVisualProbe
{
    public static async System.Threading.Tasks.Task RunCapture(Main main, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        await WaitFrames(main, 12);

        EntityId t62Id = default;
        NumericsVector2 startPos = default;
        foreach (OwnUnitObservation u in main.Session.Replica.OwnUnits)
        {
            if (u.HasSmokeCapability)
            {
                t62Id = u.Id;
                startPos = u.Position;
                break;
            }
        }

        if (t62Id.Value == 0)
        {
            throw new InvalidOperationException("SmokeVisualProbe requires a scenario with a smoke-capable unit.");
        }

        main.UnitViews.Selected.Clear();
        main.UnitViews.Selected.Add(t62Id);
        main.UpdateSelectionPresentation();

        // Deploy TDA smoke screen while advancing north-east across open ground so consecutive clouds form a moving wall.
        main.Session.DeploySmoke(t62Id);
        main.Session.Move(t62Id, startPos + new NumericsVector2(18f, -110f));

        // 1. Close zoom during early exhaust bloom behind rolling T-62
        await WaitSeconds(main, 3.2);
        NumericsVector2 pos1 = FindUnitPos(main, t62Id, startPos);
        Vector3 focus1 = new(pos1.X, main.Map.GetHeight(pos1.X, pos1.Y) + 1.5f, pos1.Y + 8f);
        SetCamera(main, focus1, size: 65f);
        await WaitFrames(main, 6);
        CombatFeedbackProbe.SaveViewport(main, Path.Combine(outputDir, "01_bloom_close.png"));
        GD.Print($"SMOKE_VISUAL_FRAME 01_bloom_close clouds={main.SmokeView?.ActiveCloudCount} puffs={main.SmokeView?.ActivePuffCount}");

        // 2. Play zoom after a multi-cloud smoke wall has formed + active-smoke FPS measurement
        await WaitSeconds(main, 6.5);
        NumericsVector2 pos2 = FindUnitPos(main, t62Id, startPos);
        NumericsVector2 midWall = (startPos + pos2) * 0.5f;
        Vector3 focus2 = new(midWall.X, main.Map.GetHeight(midWall.X, midWall.Y) + 1.5f, midWall.Y);
        SetCamera(main, focus2, size: 145f);
        await WaitFrames(main, 6);
        CombatFeedbackProbe.SaveViewport(main, Path.Combine(outputDir, "02_screen_play_zoom.png"));
        (double playP50, double playP95) = await MeasureFrameIntervalsMs(main, 90);
        GD.Print($"SMOKE_VISUAL_FRAME 02_screen_play_zoom clouds={main.SmokeView?.ActiveCloudCount} puffs={main.SmokeView?.ActivePuffCount} p50_ms={playP50:F2} ({1000.0 / playP50:F1} FPS) p95_ms={playP95:F2}");

        // 3. Low-pitch angled view to inspect vertical height, ground contact, and volumetric shading
        SetCamera(main, focus2, size: 120f);
        float targetPitch = OrbitCameraPose.MinPitch + 0.16f;
        main.CameraRig.Orbit(0.35f, targetPitch - main.CameraRig.ViewPitch);
        await WaitFrames(main, 6);
        CombatFeedbackProbe.SaveViewport(main, Path.Combine(outputDir, "03_screen_low_angle.png"));
        (double lowP50, double lowP95) = await MeasureFrameIntervalsMs(main, 90);
        GD.Print($"SMOKE_VISUAL_FRAME 03_screen_low_angle clouds={main.SmokeView?.ActiveCloudCount} puffs={main.SmokeView?.ActivePuffCount} p50_ms={lowP50:F2} ({1000.0 / lowP50:F1} FPS) p95_ms={lowP95:F2}");

        // 4. Wide operational zoom showing smoke screen against terrain, roads, and FoW
        SetCamera(main, focus2, size: 320f);
        await WaitFrames(main, 6);
        CombatFeedbackProbe.SaveViewport(main, Path.Combine(outputDir, "04_screen_wide_zoom.png"));
        GD.Print($"SMOKE_VISUAL_FRAME 04_screen_wide_zoom clouds={main.SmokeView?.ActiveCloudCount} puffs={main.SmokeView?.ActivePuffCount}");

        GD.Print($"SMOKE_VISUAL_CAPTURE_OK: {outputDir}");
        main.GetTree().Quit(0);
    }

    private static async System.Threading.Tasks.Task<(double P50Ms, double P95Ms)> MeasureFrameIntervalsMs(Main main, int frames)
    {
        double[] samples = new double[frames];
        ulong prev = Godot.Time.GetTicksUsec();
        for (int i = 0; i < frames; i++)
        {
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
            ulong now = Godot.Time.GetTicksUsec();
            samples[i] = (now - prev) / 1000.0;
            prev = now;
        }
        Array.Sort(samples);
        double p50 = samples[frames / 2];
        double p95 = samples[(int)(frames * 0.95)];
        return (p50, p95);
    }

    private static void SetCamera(Main main, Vector3 focus, float size)
    {
        main.CameraRig.ResetOrbit();
        main.CameraRig.CenterOn(focus);
        main.CameraRig.Model.TargetSize = size;
        main.CameraRig.Model.CurrentSize = size;
        main.CameraRig.Update(0.0, allowInput: false);
    }

    private static NumericsVector2 FindUnitPos(Main main, EntityId id, NumericsVector2 fallback)
    {
        foreach (OwnUnitObservation u in main.Session.Replica.OwnUnits)
        {
            if (u.Id == id)
            {
                return u.Position;
            }
        }
        return fallback;
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

    private static async System.Threading.Tasks.Task WaitFrames(Main main, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            await main.ToSignal(main.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }
}
