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
}
