using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Sandbox;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class FramePacingTests
{
    [Fact]
    public void DisplaySettings_CommandLineOverrides_AreParsedCorrectly()
    {
        DisplaySettings initial = new(VSyncEnabled: false, MaxFps: 60, BenchmarkFrames: 0);

        string[] args = ["--vsync", "on", "--max-fps", "144", "--benchmark-frames", "250"];
        DisplaySettings parsed = DisplaySettings.ParseCommandLine(args, initial);

        Assert.True(parsed.VSyncEnabled);
        Assert.Equal(144, parsed.MaxFps);
        Assert.Equal(250, parsed.BenchmarkFrames);

        string[] args165 = ["--max-fps", "165"];
        DisplaySettings parsed165 = DisplaySettings.ParseCommandLine(args165, initial);
        Assert.Equal(165, parsed165.MaxFps);

        string[] args240 = ["--max-fps", "240"];
        DisplaySettings parsed240 = DisplaySettings.ParseCommandLine(args240, initial);
        Assert.Equal(240, parsed240.MaxFps);
    }

    [Fact]
    public void DisplaySettings_InvalidMaxFps_FallsBackToDefault()
    {
        DisplaySettings initial = new(VSyncEnabled: true, MaxFps: 60, BenchmarkFrames: 0);

        string[] args = ["--max-fps", "75"]; // 75 is not in [0, 30, 60, 120, 144]
        DisplaySettings parsed = DisplaySettings.ParseCommandLine(args, initial);

        Assert.Equal(60, parsed.MaxFps);
    }

    [Fact]
    public void DisplaySettings_UnlimitedFps_IsSupported()
    {
        DisplaySettings initial = new(VSyncEnabled: true, MaxFps: 60, BenchmarkFrames: 0);

        string[] args = ["--max-fps", "0"];
        DisplaySettings parsed = DisplaySettings.ParseCommandLine(args, initial);

        Assert.Equal(0, parsed.MaxFps);
    }

    [Fact]
    public void DisplaySettings_WithoutOverrides_RetainsInitialValues()
    {
        DisplaySettings initial = new(VSyncEnabled: true, MaxFps: 120, BenchmarkFrames: 0);

        string[] args = ["--scenario", "combat", "--headless"];
        DisplaySettings parsed = DisplaySettings.ParseCommandLine(args, initial);

        Assert.Equal(initial.VSyncEnabled, parsed.VSyncEnabled);
        Assert.Equal(initial.MaxFps, parsed.MaxFps);
        Assert.Equal(0, parsed.BenchmarkFrames);
    }

    [Fact]
    public void FramePacingMetrics_CalculatesPercentilesAndBucketsAccurately()
    {
        FramePacingMetrics metrics = new(100);

        // Record 100 frames with controlled frame times:
        // 90 frames of 10.0 ms
        // 5 frames of 17.0 ms -> crosses 16.67 ms bucket
        // 4 frames of 25.0 ms -> crosses 16.67 ms bucket
        // 1 frame of 35.0 ms -> crosses 33.33 ms bucket
        for (int i = 0; i < 90; i++) metrics.Record(10.0f, 9.5f, 0.5f);
        for (int i = 0; i < 5; i++) metrics.Record(17.0f, 16.5f, 0.5f);
        for (int i = 0; i < 4; i++) metrics.Record(25.0f, 24.5f, 0.5f);
        metrics.Record(35.0f, 34.0f, 1.0f);

        Assert.Equal(100, metrics.Count);
        FramePacingMetrics.PercentileResults res = metrics.CalculateResults();
        Assert.True(MathF.Abs(res.P50Ms - 10.0f) < 0.2f, $"P50 was {res.P50Ms}");
        Assert.True(MathF.Abs(res.P95Ms - 17.4f) < 0.2f, $"P95 was {res.P95Ms}");
        Assert.True(MathF.Abs(res.P99Ms - 25.0f) < 0.2f, $"P99 was {res.P99Ms}");
        Assert.True(MathF.Abs(res.MaxMs - 35.0f) < 0.2f, $"Max was {res.MaxMs}");

        // Frames over 8.33 ms: all 100
        Assert.Equal(100, res.LongFramesOver8Ms);
        // Frames over 16.67 ms: 5 + 4 + 1 = 10
        Assert.Equal(10, res.LongFramesOver16Ms);
        // Frames over 33.33 ms: 1
        Assert.Equal(1, res.LongFramesOver33Ms);

        string report = metrics.FormatReport("test_scenario", "Hardware", "1920x1080", vsync: false, maxFps: 144, warmupFrames: 5);
        Assert.Contains("BENCHMARK REPORT", report);
        Assert.Contains("P50:", report);
        Assert.Contains("P95:", report);
        Assert.Contains("P99:", report);
        Assert.Contains("Max:", report);
        // Verify no en-dash or em-dash characters
        Assert.DoesNotContain("\u2013", report);
        Assert.DoesNotContain("\u2014", report);
    }

    [Fact]
    public void FramePacingMetrics_ControlledLongFrame_IsAccuratelyCaptured()
    {
        FramePacingMetrics metrics = new(128);

        for (int i = 0; i < 98; i++)
        {
            metrics.Record(16.6f, 5.0f);
        }

        // Simulate 2 stalls of 305 ms (top 2% of 100 samples)
        metrics.Record(305.0f, 304.5f);
        metrics.Record(305.0f, 304.5f);

        FramePacingMetrics.PercentileResults results = metrics.CalculateResults();
        Assert.True(results.MaxMs >= 305.0f, $"MaxMs was {results.MaxMs}");
        Assert.True(results.P99Ms >= 300.0f, $"P99Ms was {results.P99Ms}");
        Assert.True(results.LongFramesOver33Ms >= 2, $"LongFramesOver33Ms was {results.LongFramesOver33Ms}");
    }

    [Fact]
    public void FramePacingMetrics_RecordsAndSummarizesPhaseAndRenderCounters()
    {
        FramePacingMetrics metrics = new(16);
        // Unphased frame must be included in frame/GPU stats, not dilute phase averages.
        metrics.Record(20.0f, 3.0f, 12.0f);
        metrics.Record(16.0f, 4.0f, 6.0f, new FramePacingMetrics.FramePhaseSample(
            SessionMs: 1.0f,
            FogOfWarMs: 0.5f,
            ViewsMs: 1.2f,
            OrderPreviewMs: 0.3f,
            MinimapMs: 0.2f,
            EffectsAndHudMs: 0.8f,
            RenderDrawCalls: 420,
            RenderPrimitives: 180000,
            ActiveSceneNodes: 350,
            LodSwitches: 2,
            VisibleDrawCalls: 250,
            VisiblePrimitives: 110000,
            ShadowDrawCalls: 170,
            ShadowPrimitives: 70000));
        metrics.Record(18.0f, 5.0f, 9.0f, new FramePacingMetrics.FramePhaseSample(
            SessionMs: 3.0f,
            FogOfWarMs: 0.9f,
            ViewsMs: 1.8f,
            OrderPreviewMs: 0.5f,
            MinimapMs: 0.4f,
            EffectsAndHudMs: 1.0f,
            RenderDrawCalls: 460,
            RenderPrimitives: 220000,
            ActiveSceneNodes: 355,
            LodSwitches: 1,
            VisibleDrawCalls: 270,
            VisiblePrimitives: 130000,
            ShadowDrawCalls: 190,
            ShadowPrimitives: 90000));

        FramePacingMetrics.PercentileResults res = metrics.CalculateResults();
        Assert.True(res.HasGpuTiming);
        Assert.True(MathF.Abs(res.GpuP50Ms - 9.0f) < 0.01f);
        Assert.True(MathF.Abs(res.GpuP95Ms - 11.7f) < 0.1f);

        FramePacingMetrics.PhaseSummary summary = metrics.CalculatePhaseSummary();
        Assert.True(summary.HasPhaseData);
        Assert.True(MathF.Abs(summary.MeanSessionMs - 2.0f) < 0.01f);
        Assert.True(MathF.Abs(summary.MaxSessionMs - 3.0f) < 0.01f);
        Assert.True(MathF.Abs(summary.MeanFogOfWarMs - 0.7f) < 0.01f);
        Assert.True(MathF.Abs(summary.MeanViewsMs - 1.5f) < 0.01f);
        Assert.True(summary.HasRenderCounters);
        Assert.True(MathF.Abs(summary.MeanDrawCalls - 440f) < 0.5f);
        Assert.Equal(460, summary.MaxDrawCalls);
        Assert.Equal(355, summary.LastActiveSceneNodes);
        Assert.Equal(3, summary.TotalLodSwitches);
        Assert.True(summary.HasPassCounters);
        Assert.True(MathF.Abs(summary.MeanVisiblePrimitives - 120000f) < 1f);
        Assert.True(MathF.Abs(summary.MeanShadowPrimitives - 80000f) < 1f);

        string report = metrics.FormatReport("phase_test", "Hardware", "1920x1080", vsync: true, maxFps: 60);
        Assert.Contains("p50=9.00 ms | p95=11.70 ms | mean=9.00 ms", report);
        Assert.Contains("Bottleneck:         GPU bound", report);
        Assert.Contains("CPU PHASE BREAKDOWN", report);
        Assert.Contains("RENDER COUNTERS", report);
        Assert.Contains("Visible Pass:", report);
        Assert.Contains("Shadow Pass:", report);
        Assert.Contains("LOD Switches:     3", report);
        Assert.DoesNotContain("\u2013", report);
        Assert.DoesNotContain("\u2014", report);

        FramePacingMetrics cpuBoundMetrics = new(16);
        cpuBoundMetrics.Record(16.0f, 8.0f, 3.0f);
        string cpuReport = cpuBoundMetrics.FormatReport("cpu_test", "Hardware", "1920x1080", vsync: false, maxFps: 0);
        Assert.Contains("Bottleneck:         CPU bound", cpuReport);
    }

    [Fact]
    public void DisplaySettings_UserEdits_DoNotPersistCliOverrides()
    {
        // Saved preferences: VSync on, MaxFps 60
        DisplaySettings savedConfig = new(VSyncEnabled: true, MaxFps: 60, BenchmarkFrames: 0);

        // Launch with CLI override: --max-fps 144
        string[] args = ["--max-fps", "144"];
        DisplaySettings effective = DisplaySettings.ParseCommandLine(args, savedConfig);
        Assert.Equal(144, effective.MaxFps);
        Assert.True(effective.VSyncEnabled);

        // User edits only VSync in UI (sets to false)
        DisplaySettings updatedSavedConfig1 = DisplaySettings.ApplyUserEdit(savedConfig, newVsync: false, newMaxFps: null);
        // Saved config must retain original MaxFps 60, NOT the CLI override 144!
        Assert.False(updatedSavedConfig1.VSyncEnabled);
        Assert.Equal(60, updatedSavedConfig1.MaxFps);

        // In reverse: launch with CLI override --vsync off, saved MaxFps 60
        string[] args2 = ["--vsync", "off"];
        DisplaySettings effective2 = DisplaySettings.ParseCommandLine(args2, savedConfig);
        Assert.False(effective2.VSyncEnabled);
        Assert.Equal(60, effective2.MaxFps);

        // User edits only MaxFps in UI (sets to 120)
        DisplaySettings updatedSavedConfig2 = DisplaySettings.ApplyUserEdit(savedConfig, newVsync: null, newMaxFps: 120);
        // Saved config must retain original VSync true, NOT the CLI override false!
        Assert.True(updatedSavedConfig2.VSyncEnabled);
        Assert.Equal(120, updatedSavedConfig2.MaxFps);
    }

    [Fact]
    public void DisplaySettings_RenderScale_And_AntiAliasing_WorkCorrectly()
    {
        DisplaySettings defaults = DisplaySettings.CreateDefault();
        Assert.Equal(RenderScalePreset.Native100, defaults.RenderScale);
        Assert.Equal(AntiAliasingPreset.Auto, defaults.AntiAliasing);

        Assert.Equal(RenderScalePreset.Auto, DisplaySettings.ParseRenderScale("auto"));
        Assert.Equal(RenderScalePreset.Native100, DisplaySettings.ParseRenderScale("100%"));
        Assert.Equal(RenderScalePreset.Native100, DisplaySettings.ParseRenderScale("1.0"));
        Assert.Equal(RenderScalePreset.Scale85, DisplaySettings.ParseRenderScale("85"));
        Assert.Equal(RenderScalePreset.Scale75, DisplaySettings.ParseRenderScale("0.75"));
        Assert.Equal(RenderScalePreset.Scale50, DisplaySettings.ParseRenderScale("50%"));
        Assert.Equal(RenderScalePreset.Native100, DisplaySettings.ParseRenderScale("bogus", RenderScalePreset.Native100));

        // Pixel-budget Auto scaling: clamp(sqrt(TargetPixels / windowPixels), 0.35, 1.0)
        float scale6880 = DisplaySettings.ResolveRenderScaleFactor(RenderScalePreset.Auto, 6880, 2880);
        Assert.True(MathF.Abs(scale6880 - 0.4313f) < 0.002f, $"Expected ~0.431 on 6880x2880, got {scale6880}");
        float scale2560 = DisplaySettings.ResolveRenderScaleFactor(RenderScalePreset.Auto, 2560, 1600);
        Assert.True(MathF.Abs(scale2560 - 0.9487f) < 0.01f, $"Expected ~0.95 on 2560x1600, got {scale2560}");
        float scale1440 = DisplaySettings.ResolveRenderScaleFactor(RenderScalePreset.Auto, 1440, 900);
        Assert.Equal(1.0f, scale1440);

        Assert.Equal(1.0f, DisplaySettings.ResolveRenderScaleFactor(RenderScalePreset.Native100, 6880, 2880));
        Assert.Equal(0.85f, DisplaySettings.ResolveRenderScaleFactor(RenderScalePreset.Scale85, 1440, 900));
        Assert.Equal(0.75f, DisplaySettings.ResolveRenderScaleFactor(RenderScalePreset.Scale75, 1440, 900));
        Assert.Equal(0.50f, DisplaySettings.ResolveRenderScaleFactor(RenderScalePreset.Scale50, 1440, 900));

        // Anti-aliasing parsing & Auto cutoff (~3.7M internal pixels)
        Assert.Equal(AntiAliasingPreset.Auto, DisplaySettings.ParseAntiAliasing("auto"));
        Assert.Equal(AntiAliasingPreset.Off, DisplaySettings.ParseAntiAliasing("off"));
        Assert.Equal(AntiAliasingPreset.Msaa2x, DisplaySettings.ParseAntiAliasing("2x"));
        Assert.Equal(AntiAliasingPreset.Msaa4x, DisplaySettings.ParseAntiAliasing("4x"));
        Assert.Equal(2, DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Auto, 1440, 900, scale1440));
        Assert.Equal(2, DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Auto, 2560, 1600, scale2560));
        Assert.Equal(2, DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Auto, 6880, 2880, scale6880));
        Assert.Equal(0, DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Auto, 6880, 2880, 0.50f));
        Assert.Equal(0, DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Off, 1440, 900, 1.0f));
        Assert.Equal(2, DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Msaa2x, 6880, 2880, 1.0f));
        Assert.Equal(4, DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Msaa4x, 6880, 2880, 1.0f));

        // Ambient occlusion (SSAO) Auto policy: ON below 4.5M output pixels (except Low), OFF on >= 4.5M UWQHD/HiDPI outputs
        Assert.True(DisplaySettings.ResolveSsaoEnabled(AmbientOcclusionPreset.Auto, EnvironmentQuality.High, true, 1920, 1080));
        Assert.True(DisplaySettings.ResolveSsaoEnabled(AmbientOcclusionPreset.Auto, EnvironmentQuality.Medium, true, 2560, 1600));
        Assert.False(DisplaySettings.ResolveSsaoEnabled(AmbientOcclusionPreset.Auto, EnvironmentQuality.Medium, true, 3440, 1440));
        Assert.False(DisplaySettings.ResolveSsaoEnabled(AmbientOcclusionPreset.Auto, EnvironmentQuality.High, true, 3360, 2100));
        Assert.False(DisplaySettings.ResolveSsaoEnabled(AmbientOcclusionPreset.Auto, EnvironmentQuality.High, true, 6880, 2880));
        Assert.True(DisplaySettings.ResolveSsaoEnabled(AmbientOcclusionPreset.On, EnvironmentQuality.High, true, 6880, 2880));
        Assert.False(DisplaySettings.ResolveSsaoEnabled(AmbientOcclusionPreset.Off, EnvironmentQuality.High, true, 1920, 1080));
        Assert.False(DisplaySettings.ResolveSsaoEnabled(AmbientOcclusionPreset.Auto, EnvironmentQuality.Low, true, 1920, 1080));
        Assert.Equal(AmbientOcclusionPreset.Off, DisplaySettings.ParseAmbientOcclusion("off"));
        Assert.Equal(AmbientOcclusionPreset.On, DisplaySettings.ParseAmbientOcclusion("half"));
        Assert.True(DisplaySettings.HasSsaoOverride(["--ssao", "off"]));
        Assert.Null(DisplaySettings.ParseBenchmarkOverrides(["--ssao", "half", "--ssao", "auto"]).SsaoMode);

        Assert.True(DisplaySettings.HasRenderScaleOverride(["--render-scale", "75"]));
        Assert.False(DisplaySettings.HasRenderScaleOverride(["--max-fps", "60"]));
        Assert.True(DisplaySettings.HasAntiAliasingOverride(["--msaa", "off"]));
        Assert.False(DisplaySettings.HasAntiAliasingOverride(["--max-fps", "60"]));

        DisplaySettings parsed = DisplaySettings.ParseCommandLine(["--render-scale=85%", "--msaa=4x"], defaults);
        Assert.Equal(RenderScalePreset.Scale85, parsed.RenderScale);
        Assert.Equal(AntiAliasingPreset.Msaa4x, parsed.AntiAliasing);

        DisplaySettings edited = DisplaySettings.ApplyUserEdit(defaults, newVsync: false);
        Assert.Equal(RenderScalePreset.Native100, edited.RenderScale);
        Assert.Equal(AntiAliasingPreset.Auto, edited.AntiAliasing);
    }

    [Fact]
    public void ClientReplica_SamplePosition_InterpolatesSmoothlyBetweenSnapshots()
    {
        ClientReplica replica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        EntityId id = new(1);
        OwnUnitObservation u0 = new(id, 0, new Vector2(0f, 0f), 0);
        OwnUnitObservation u10 = new(id, 0, new Vector2(100f, 0f), 0);

        int sz0 = WireProtocol.WriteSnapshot(buffer, 1, 0, [u0]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, sz0)));

        int sz1 = WireProtocol.WriteSnapshot(buffer, 2, 10, [u10]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, sz1)));

        // Sample at midpoint (renderTick = 5)
        Vector2 posMid = replica.SamplePosition(id, 5.0);
        Assert.True(MathF.Abs(posMid.X - 50f) < 0.01f, $"Midpoint X was {posMid.X}");

        // Sample at 25% (renderTick = 2.5)
        Vector2 posQuarter = replica.SamplePosition(id, 2.5);
        Assert.True(MathF.Abs(posQuarter.X - 25f) < 0.01f, $"Quarter X was {posQuarter.X}");

        // Sample at 75% (renderTick = 7.5)
        Vector2 posThreeQuarter = replica.SamplePosition(id, 7.5);
        Assert.True(MathF.Abs(posThreeQuarter.X - 75f) < 0.01f, $"ThreeQuarter X was {posThreeQuarter.X}");
    }

    [Fact]
    public void ClientReplica_SampleContactPosition_LastKnownRemainsStrictlyFrozen()
    {
        ClientReplica replica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        OwnUnitObservation own = new(new EntityId(1), 0, new Vector2(0f, 0f), 0);
        ContactId contactId = new(99);

        // Snapshot 1: Current contact at (50, 50) at tick 0
        ContactObservation cCurrent = new(
            contactId,
            ContactState.Current,
            ContactClassification.Unknown,
            0,
            new Vector2(50f, 50f),
            0);

        int sz0 = WireProtocol.WriteSnapshot(buffer, 1, 0, [own], [cCurrent]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, sz0)));

        // Snapshot 2: Contact becomes LastKnown at tick 10 with position (50, 50)
        ContactObservation cLost = new(
            contactId,
            ContactState.LastKnown,
            ContactClassification.Unknown,
            0,
            new Vector2(50f, 50f),
            0);

        int sz1 = WireProtocol.WriteSnapshot(buffer, 2, 10, [own], [cLost]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, sz1)));

        // Regardless of renderTick (even if intermediate or advanced), LastKnown must NOT interpolate
        Vector2 posAt0 = replica.SampleContactPosition(contactId, 0.0);
        Vector2 posAt5 = replica.SampleContactPosition(contactId, 5.0);
        Vector2 posAt10 = replica.SampleContactPosition(contactId, 10.0);
        Vector2 posAt15 = replica.SampleContactPosition(contactId, 15.0);

        Assert.Equal(new Vector2(50f, 50f), posAt0);
        Assert.Equal(new Vector2(50f, 50f), posAt5);
        Assert.Equal(new Vector2(50f, 50f), posAt10);
        Assert.Equal(new Vector2(50f, 50f), posAt15);
    }

    [Fact]
    public void ClientSession_DelayedSnapshotArrival_PresentationClockDoesNotRewindOrMoveUnitsBackward()
    {
        (ITransport clientTransport, ITransport serverTransport) =
            InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);

        using ClientSession session = new(clientTransport);
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        EntityId unitId = new(1);

        // 1. Snapshot tick 10: unit at X = 10
        OwnUnitObservation u10 = new(unitId, 0, new Vector2(10f, 0f), 0);
        int sz10 = WireProtocol.WriteSnapshot(buffer, 1, 10, [u10]);
        serverTransport.TrySend(buffer.AsSpan(0, sz10));
        session.Advance(0.05);

        // 2. Snapshot tick 12: unit at X = 12
        OwnUnitObservation u12 = new(unitId, 0, new Vector2(12f, 0f), 0);
        int sz12 = WireProtocol.WriteSnapshot(buffer, 2, 12, [u12]);
        serverTransport.TrySend(buffer.AsSpan(0, sz12));
        session.Advance(0.05);

        // 3. Jitter / delay: advance 0.20 seconds with no snapshots from server
        for (int i = 0; i < 4; i++)
        {
            session.Advance(0.05);
        }

        double renderTickBeforeDelayedSnapshot = session.RenderTick;
        Vector2 positionBeforeDelayedSnapshot = session.Replica.SamplePosition(unitId, renderTickBeforeDelayedSnapshot);

        // Unit should have advanced to at least position at tick 12
        Assert.True(positionBeforeDelayedSnapshot.X >= 12.0f, $"Position before delayed snapshot was {positionBeforeDelayedSnapshot.X}");

        // 4. Delayed snapshot arrives for tick 14: unit at X = 14
        OwnUnitObservation u14 = new(unitId, 0, new Vector2(14f, 0f), 0);
        int sz14 = WireProtocol.WriteSnapshot(buffer, 3, 14, [u14]);
        serverTransport.TrySend(buffer.AsSpan(0, sz14));
        session.Advance(0.016);

        double renderTickAfterDelayedSnapshot = session.RenderTick;
        Vector2 positionAfterDelayedSnapshot = session.Replica.SamplePosition(unitId, renderTickAfterDelayedSnapshot);

        // Assert presentation clock did NOT jump backwards
        Assert.True(
            renderTickAfterDelayedSnapshot >= renderTickBeforeDelayedSnapshot,
            $"RenderTick jumped backwards from {renderTickBeforeDelayedSnapshot} to {renderTickAfterDelayedSnapshot}");

        // Assert unit position did NOT jump backwards
        Assert.True(
            positionAfterDelayedSnapshot.X >= positionBeforeDelayedSnapshot.X,
            $"Unit position moved backwards from {positionBeforeDelayedSnapshot.X} to {positionAfterDelayedSnapshot.X}");
    }

    [Fact]
    public void ClientSession_SnapshotPauseAndBurst_RecoversImmediatelyWithoutLingeringLag()
    {
        (ITransport clientTransport, ITransport serverTransport) =
            InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);

        using ClientSession session = new(clientTransport);
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        EntityId unitId = new(1);

        // 1. Initial steady snapshots up to tick 20
        OwnUnitObservation u16 = new(unitId, 0, new Vector2(16f, 0f), 0);
        int sz16 = WireProtocol.WriteSnapshot(buffer, 1, 16, [u16]);
        serverTransport.TrySend(buffer.AsSpan(0, sz16));
        session.Advance(0.10);

        OwnUnitObservation u20 = new(unitId, 0, new Vector2(20f, 0f), 0);
        int sz20 = WireProtocol.WriteSnapshot(buffer, 2, 20, [u20]);
        serverTransport.TrySend(buffer.AsSpan(0, sz20));
        session.Advance(0.20);

        Assert.True(session.RenderTick >= 17.0, $"Initial render tick was {session.RenderTick}");

        // 2. 1.0 second pause without snapshots (simulating network stall)
        for (int i = 0; i < 20; i++)
        {
            session.Advance(0.05);
        }

        // Clock should have stopped at Replica.LatestTick (20)
        Assert.Equal(20.0, session.RenderTick);

        // 3. Burst / server resumes sending at tick 40: unit at X = 40
        OwnUnitObservation u40 = new(unitId, 0, new Vector2(40f, 0f), 0);
        int sz40 = WireProtocol.WriteSnapshot(buffer, 3, 40, [u40]);
        serverTransport.TrySend(buffer.AsSpan(0, sz40));

        // Advance a single client frame (16 ms)
        session.Advance(0.016);

        // Clamped target is 40 - 3 = 37.
        // Clock should have snapped immediately to >= 37 without taking 1.7 seconds to catch up
        double renderTickAfterBurst = session.RenderTick;
        Assert.True(
            renderTickAfterBurst >= 37.0,
            $"Render tick after burst should have recovered immediately to >= 37.0, but was {renderTickAfterBurst}");

        // Sampled position should reflect caught-up time, not lagging at tick 20
        Vector2 posAfterBurst = session.Replica.SamplePosition(unitId, renderTickAfterBurst);
        Assert.True(
            posAfterBurst.X >= 37.0f,
            $"Position after burst should be >= 37.0, but was {posAfterBurst.X}");
    }

    [Fact]
    public void DisplaySettings_Stage2BilinearFxaaAndCliOverrides_BehaveConsistently()
    {
        // Last CLI argument wins: numeric override then "auto" clears numeric override
        BenchmarkRenderOverrides cleared = DisplaySettings.ParseBenchmarkOverrides(
            ["--render-scale", "0.43", "--render-scale", "auto", "--aa", "fxaa", "--scaling-mode", "bilinear"]);
        Assert.Null(cleared.RenderScaleOverride);
        Assert.Equal("bilinear", cleared.ScalingMode);

        // Reverse order: "auto" then numeric override preserves numeric override
        BenchmarkRenderOverrides active = DisplaySettings.ParseBenchmarkOverrides(
            ["--render-scale", "auto", "--render-scale", "0.43"]);
        Assert.NotNull(active.RenderScaleOverride);
        Assert.True(MathF.Abs(active.RenderScaleOverride!.Value - 0.43f) < 0.001f);

        // Auto AA computes against actual effectiveRenderScale (< 0.50 not clamped up to 0.50)
        // 6880*0.43 * 2880*0.43 ~= 3.665M <= 3.7M -> MSAA 2x enabled
        Assert.Equal(2, DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Auto, 6880, 2880, 0.43f));
        Assert.Equal(0, DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Auto, 6880, 2880, 0.50f));
        Assert.Equal(0, DisplaySettings.ResolveMsaaSamples(AntiAliasingPreset.Fxaa, 1440, 900, 1.0f));

        // Screen-space FXAA activates for explicit FXAA preset and Auto fallback when MSAA == 0
        Assert.True(DisplaySettings.ResolveScreenSpaceFxaa(AntiAliasingPreset.Fxaa, 0));
        Assert.True(DisplaySettings.ResolveScreenSpaceFxaa(AntiAliasingPreset.Auto, 0));
        Assert.False(DisplaySettings.ResolveScreenSpaceFxaa(AntiAliasingPreset.Auto, 2));
        Assert.False(DisplaySettings.ResolveScreenSpaceFxaa(AntiAliasingPreset.Off, 0));
        Assert.False(DisplaySettings.ResolveScreenSpaceFxaa(AntiAliasingPreset.Msaa2x, 2));
        Assert.False(DisplaySettings.ResolveScreenSpaceFxaa(AntiAliasingPreset.Msaa4x, 4));
        Assert.Equal(AntiAliasingPreset.Fxaa, DisplaySettings.ParseAntiAliasing("fxaa"));
        Assert.Equal(AntiAliasingPreset.Fxaa, DisplaySettings.ParseAntiAliasing("FXAA"));

        // Bilinear scaling replaces FSR on large/HiDPI outputs (>= 4.5M pixels) and native 1.0
        Assert.True(DisplaySettings.ResolveUseBilinearScaling(6880, 2880, 0.50f));
        Assert.True(DisplaySettings.ResolveUseBilinearScaling(3440, 1440, 0.86f));
        Assert.True(DisplaySettings.ResolveUseBilinearScaling(3360, 2100, 0.72f));
        Assert.True(DisplaySettings.ResolveUseBilinearScaling(1920, 1080, 1.0f));
        Assert.False(DisplaySettings.ResolveUseBilinearScaling(2560, 1600, 0.95f));
        Assert.False(DisplaySettings.ResolveUseBilinearScaling(1920, 1080, 0.75f));
        Assert.False(DisplaySettings.ResolveUseBilinearScaling(1440, 900, 0.75f));
    }

    [Fact]
    public void FramePacingMetrics_Record_IsZeroAllocation_AndTracksOverflowExplicitly()
    {
        FramePacingMetrics metrics = new(capacity: 64);
        FramePacingMetrics.FramePhaseSample phase = new(SessionMs: 1.0f, RenderDrawCalls: 100, VisibleDrawCalls: 60, ShadowDrawCalls: 40);

        // Warm up JIT paths before measuring allocations
        metrics.Record(12.0f, 1.0f, 5.0f);
        metrics.Record(12.0f, 1.0f, 5.0f, in phase);
        metrics.Reset();

        using (NoGcAllocationWindow window = new())
        {
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                float ms = i == 999 ? 45.0f : (i % 2 == 0 ? 12.0f : 24.0f);
                metrics.Record(ms, 2.0f, 6.0f, in phase);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            Assert.True(allocated == 0, window.Describe(allocated));
        }

        // Buffer stores exact capacity for percentiles while streaming stats never drop samples silently
        Assert.Equal(64, metrics.Capacity);
        Assert.Equal(64, metrics.Count);
        Assert.Equal(1000, metrics.TotalCount);
        Assert.True(metrics.IsBufferSaturated);
        Assert.Equal(936, metrics.DroppedPercentileSamples);

        FramePacingMetrics.PercentileResults res = metrics.CalculateResults();
        Assert.Equal(1000, res.SampleCount);
        Assert.Equal(64, res.StoredSampleCount);
        Assert.True(res.ArePercentilesTruncated);
        Assert.True(MathF.Abs(res.MaxMs - 45.0f) < 0.01f, $"MaxMs should preserve post-capacity spike 45.0, got {res.MaxMs}");
        Assert.Equal(500, res.LongFramesOver16Ms);
        Assert.Equal(1, res.LongFramesOver33Ms);
        Assert.True(MathF.Abs(res.MeanCpuMs - 2.0f) < 0.001f);
        Assert.True(MathF.Abs(res.MeanGpuMs - 6.0f) < 0.001f);
        Assert.Equal("<=8.3ms:0 | 8.3-16.7:500 | 16.7-25:499 | 25-33.3:0 | 33.3-41.7:0 | >41.7:1", res.Histogram);

        string report = metrics.FormatReport("battle", "Metal", "3440x1440", vsync: true, maxFps: 0);
        Assert.Contains("percentile buffer full: 64/1000 kept", report);
        Assert.Contains("Frame Histogram:", report);

        // Large preallocated sustained buffer also records with zero heap allocations
        FramePacingMetrics sustained = new(capacity: 36_000);
        sustained.Record(16.0f, 1.0f, 5.0f, in phase);
        sustained.Reset();
        using (NoGcAllocationWindow window = new())
        {
            long sustainedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 36_000; i++)
            {
                sustained.Record(16.0f, 1.0f, 5.0f, in phase);
            }
            long sustainedAllocated = GC.GetAllocatedBytesForCurrentThread() - sustainedBefore;
            Assert.True(sustainedAllocated == 0, window.Describe(sustainedAllocated));
        }
        Assert.False(sustained.IsBufferSaturated);
        Assert.Equal(36_000, sustained.TotalCount);
    }

    [Fact]
    public void DisplaySettings_EnvironmentProfiles_SanitizeAndCycleCleanly()
    {
        Assert.Equal("overcast", DisplaySettings.SanitizeProfile(null));
        Assert.Equal("overcast", DisplaySettings.SanitizeProfile("unknown_profile"));
        Assert.Equal("cool_clear_day", DisplaySettings.SanitizeProfile("cool_clear_day"));
        Assert.Equal("cool_clear_day", DisplaySettings.SanitizeProfile("day"));
        Assert.Equal("clear_afternoon", DisplaySettings.SanitizeProfile("clear"));
        Assert.Equal("amber_sunset", DisplaySettings.SanitizeProfile("sunset"));
        Assert.Equal("amber_sunset", DisplaySettings.SanitizeProfile("amber_sunset"));
        Assert.Equal("night_ops", DisplaySettings.SanitizeProfile("night"));
        Assert.Equal("night_ops", DisplaySettings.SanitizeProfile("night_ops"));
        Assert.Equal("storm_rain", DisplaySettings.SanitizeProfile("storm"));
        Assert.Equal("storm_rain", DisplaySettings.SanitizeProfile("rain"));
        Assert.Equal("storm_rain", DisplaySettings.SanitizeProfile("storm_rain"));
        Assert.Equal("overcast", DisplaySettings.SanitizeProfile("fog"));

        string current = DisplaySettings.ValidEnvironmentProfiles[0];
        HashSet<string> visited = [current];
        for (int i = 0; i < DisplaySettings.ValidEnvironmentProfiles.Length; i++)
        {
            current = DisplaySettings.GetNextEnvironmentProfile(current);
            visited.Add(current);
        }
        Assert.Equal(DisplaySettings.ValidEnvironmentProfiles.Length, visited.Count);
        Assert.Equal(DisplaySettings.ValidEnvironmentProfiles[0], current);

        DisplaySettings parsed = DisplaySettings.ParseCommandLine(["--env", "amber_sunset"]);
        Assert.Equal("amber_sunset", parsed.EnvironmentProfile);
        DisplaySettings parsedRain = DisplaySettings.ParseCommandLine(["--environment=storm_rain"]);
        Assert.Equal("storm_rain", parsedRain.EnvironmentProfile);
    }

    [Fact]
    public void EnvironmentProfileJsonFiles_AllCatalogProfilesExistAndPassSchemaValidation()
    {
        string? dir = AppContext.BaseDirectory;
        string? envDir = null;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "content", "environments");
            if (Directory.Exists(candidate))
            {
                envDir = candidate;
                break;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        Assert.NotNull(envDir);

        foreach ((string id, string label) in DisplaySettings.EnvironmentProfileCatalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(label));
            string path = Path.Combine(envDir!, $"{id}.json");
            Assert.True(File.Exists(path), $"Missing environment profile JSON: {path}");

            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            System.Text.Json.JsonElement root = doc.RootElement;
            Assert.Equal(id, root.GetProperty("id").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("name").GetString()));

            System.Text.Json.JsonElement sun = root.GetProperty("sun");
            float pitch = sun.GetProperty("pitch").GetSingle();
            float sunEnergy = sun.GetProperty("energy").GetSingle();
            Assert.InRange(pitch, -90f, 0f);
            Assert.InRange(sunEnergy, 0.05f, 16f);

            System.Text.Json.JsonElement haze = root.GetProperty("haze");
            float closeDensity = haze.GetProperty("close_zoom_density").GetSingle();
            float wideDensity = haze.GetProperty("wide_zoom_density").GetSingle();
            Assert.True(closeDensity > wideDensity, $"Profile '{id}' must have close_zoom_density > wide_zoom_density.");

            if (root.TryGetProperty("glow", out System.Text.Json.JsonElement glow) &&
                glow.GetProperty("enabled").GetBoolean())
            {
                float threshold = glow.TryGetProperty("hdr_threshold", out System.Text.Json.JsonElement hdrEl)
                    ? hdrEl.GetSingle()
                    : 1.05f;
                Assert.InRange(threshold, 1.0f, 4.0f);
            }

            if (root.TryGetProperty("weather", out System.Text.Json.JsonElement weather))
            {
                Assert.InRange(weather.GetProperty("rain_intensity").GetSingle(), 0f, 1f);
                Assert.InRange(weather.GetProperty("wetness").GetSingle(), 0f, 1f);
            }

            System.Text.Json.JsonElement tints = root.GetProperty("material_tints");
            if (tints.TryGetProperty("water_horizon_reflect", out System.Text.Json.JsonElement reflectEl))
            {
                Assert.InRange(reflectEl.GetSingle(), 0f, 1f);
            }
        }
    }
}
