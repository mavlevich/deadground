using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Sandbox;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class MotionSmoothingTests
{
    private static byte[] WriteSnapshot(int sequence, int tick, OwnUnitObservation[] units)
    {
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        int size = WireProtocol.WriteSnapshot(buffer, (uint)sequence, tick, units);
        byte[] payload = new byte[size];
        buffer.AsSpan(0, size).CopyTo(payload);
        return payload;
    }

    [Fact]
    public void ConstantSpeedMotion_SampledAt60And120Fps_SmoothDisplacementWithinTolerance()
    {
        // 10 m/s along X, snapshots every 2 ticks (0.1s, dx = 1.0m)
        (ITransport clientTransport, ITransport serverTransport) =
            InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);
        using ClientSession session = new(clientTransport);
        EntityId unitId = new(101);

        const float Speed = 10f;
        const double Fps60Delta = 1.0 / 60.0;
        const double Fps120Delta = 1.0 / 120.0;

        // 1. Validate 60 FPS sampling
        int sequence = 1;
        double serverTime = 0.0;
        int nextSnapshotTick = 0;
        MotionDiagnostics diag60 = new(300);
        Vector2 prevPos = Vector2.Zero;

        for (int frame = 0; frame < 180; frame++)
        {
            serverTime += Fps60Delta;
            while (serverTime >= nextSnapshotTick * Simulation.TickSeconds)
            {
                float posX = nextSnapshotTick * Simulation.TickSeconds * Speed;
                OwnUnitObservation u = new(unitId, 0, new Vector2(posX, 0f), 0);
                byte[] bytes = WriteSnapshot(sequence++, nextSnapshotTick, [u]);
                serverTransport.TrySend(bytes);
                nextSnapshotTick += 2;
            }

            session.Advance(Fps60Delta);
            Vector2 pos = session.Replica.SamplePosition(unitId, session.RenderTick,
                out int leftTick, out int rightTick, out float alpha);
            float disp = frame > 0 ? (pos - prevPos).Length() : 0f;
            prevPos = pos;

            diag60.Record(
                Fps60Delta,
                session.Tick,
                session.RenderTick,
                leftTick,
                rightTick,
                alpha,
                pos,
                disp);
        }

        MotionDiagnostics.Analysis analysis60 = diag60.Analyze(warmupFrames: 30, expectedSpeed: Speed);
        Assert.Equal(0, analysis60.StallFrames);
        Assert.Equal(0, analysis60.JumpFrames);
        Assert.Equal(0, analysis60.StutterFrames);

        float expectedDisp60 = Speed * (float)Fps60Delta;
        Assert.True(
            MathF.Abs(analysis60.MeanDisplacement - expectedDisp60) < 0.005f,
            $"Expected mean 60 FPS displacement ~{expectedDisp60:F4}, got {analysis60.MeanDisplacement:F4}");
        Assert.True(
            MathF.Abs(analysis60.MaxDisplacement - expectedDisp60) < 0.015f,
            $"Max 60 FPS displacement {analysis60.MaxDisplacement:F4} deviated from {expectedDisp60:F4}");

        // 2. Validate 120 FPS sampling with a fresh session
        (ITransport cTransport120, ITransport sTransport120) =
            InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);
        using ClientSession session120 = new(cTransport120);

        sequence = 1;
        serverTime = 0.0;
        nextSnapshotTick = 0;
        MotionDiagnostics diag120 = new(400);
        prevPos = Vector2.Zero;

        for (int frame = 0; frame < 300; frame++)
        {
            serverTime += Fps120Delta;
            while (serverTime >= nextSnapshotTick * Simulation.TickSeconds)
            {
                float posX = nextSnapshotTick * Simulation.TickSeconds * Speed;
                OwnUnitObservation u = new(unitId, 0, new Vector2(posX, 0f), 0);
                byte[] bytes = WriteSnapshot(sequence++, nextSnapshotTick, [u]);
                sTransport120.TrySend(bytes);
                nextSnapshotTick += 2;
            }

            session120.Advance(Fps120Delta);
            Vector2 pos = session120.Replica.SamplePosition(unitId, session120.RenderTick,
                out int leftTick, out int rightTick, out float alpha);
            float disp = frame > 0 ? (pos - prevPos).Length() : 0f;
            prevPos = pos;

            diag120.Record(
                Fps120Delta,
                session120.Tick,
                session120.RenderTick,
                leftTick,
                rightTick,
                alpha,
                pos,
                disp);
        }

        MotionDiagnostics.Analysis analysis120 = diag120.Analyze(warmupFrames: 50, expectedSpeed: Speed);
        Assert.Equal(0, analysis120.StallFrames);
        Assert.Equal(0, analysis120.JumpFrames);
        Assert.Equal(0, analysis120.StutterFrames);

        float expectedDisp120 = Speed * (float)Fps120Delta;
        Assert.True(
            MathF.Abs(analysis120.MeanDisplacement - expectedDisp120) < 0.003f,
            $"Expected mean 120 FPS displacement ~{expectedDisp120:F4}, got {analysis120.MeanDisplacement:F4}");
    }

    [Theory]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void EffectiveDelayAndBufferHeadroom_ConsistentAcrossMultipleFps(int fps)
    {
        (ITransport clientTransport, ITransport serverTransport) =
            InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);
        using ClientSession session = new(clientTransport);
        EntityId unitId = new(1);

        const float Speed = 10f;
        double dt = 1.0 / fps;
        double serverTime = 0.0;
        int nextSnapshotTick = 0;
        int sequence = 1;

        // Run for 6 seconds (steady state reached after buffer fills)
        for (int frame = 0; serverTime < 6.0 - 1e-9; frame++)
        {
            serverTime += dt;
            while (serverTime + 1e-9 >= nextSnapshotTick * Simulation.TickSeconds)
            {
                float posX = nextSnapshotTick * Simulation.TickSeconds * Speed;
                OwnUnitObservation u = new(unitId, 0, new Vector2(posX, 0f), 0);
                byte[] bytes = WriteSnapshot(sequence++, nextSnapshotTick, [u]);
                serverTransport.TrySend(bytes);
                nextSnapshotTick += 2;
            }

            session.Advance(dt);
        }

        // At steady state, render clock must run at ~150 ms (3.0 ticks) effective delay
        double currentServerTick = serverTime * Simulation.TickRate;
        double effectiveDelayTicks = currentServerTick - session.RenderTick;
        double effectiveDelayMs = effectiveDelayTicks * Simulation.TickSeconds * 1000.0;

        // Discretization tolerance accounts for deadband plus half a frame step (dt * 20 / 2)
        double frameDiscretizationTicks = dt * Simulation.TickRate * 0.5;
        double allowableTolerance = ClientSession.ClockDeadbandTicks + frameDiscretizationTicks + 0.05;

        Assert.True(
            Math.Abs(effectiveDelayTicks - ClientSession.InterpolationBufferTicks) <= allowableTolerance,
            $"FPS {fps}: effective delay was {effectiveDelayTicks:F3} ticks ({effectiveDelayMs:F1} ms), expected within {allowableTolerance:F3} ticks of {ClientSession.InterpolationBufferTicks:F1} ticks.");
    }

    [Fact]
    public void SnapshotDelivery_IrregularIntervals_MaintainsSmoothPresentationWithoutStalls()
    {
        (ITransport clientTransport, ITransport serverTransport) =
            InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);
        using ClientSession session = new(clientTransport);
        EntityId unitId = new(201);

        const float Speed = 8f;
        const double Dt = 1.0 / 60.0;
        MotionDiagnostics diag = new(300);
        Vector2 prevPos = Vector2.Zero;

        // Feed snapshots at irregular intervals: 60ms, 140ms, 80ms, 120ms
        int[] snapshotIntervalsFrames = [4, 8, 5, 7, 4, 8, 6, 6, 5, 7, 4, 8, 6];
        int intervalIdx = 0;
        int framesUntilNext = snapshotIntervalsFrames[0];
        int sequence = 1;
        int currentTick = 0;

        for (int frame = 0; frame < 200; frame++)
        {
            if (--framesUntilNext <= 0)
            {
                currentTick += 2;
                float posX = currentTick * Simulation.TickSeconds * Speed;
                OwnUnitObservation u = new(unitId, 0, new Vector2(posX, 0f), 0);
                byte[] bytes = WriteSnapshot(sequence++, currentTick, [u]);
                serverTransport.TrySend(bytes);

                intervalIdx = (intervalIdx + 1) % snapshotIntervalsFrames.Length;
                framesUntilNext = snapshotIntervalsFrames[intervalIdx];
            }

            session.Advance(Dt);
            Vector2 pos = session.Replica.SamplePosition(unitId, session.RenderTick,
                out int leftTick, out int rightTick, out float alpha);
            float disp = frame > 0 ? (pos - prevPos).Length() : 0f;
            prevPos = pos;

            diag.Record(
                Dt,
                session.Tick,
                session.RenderTick,
                leftTick,
                rightTick,
                alpha,
                pos,
                disp);
        }

        MotionDiagnostics.Analysis analysis = diag.Analyze(warmupFrames: 30);
        // Under irregular network arrivals within buffer bounds, the clock must not stall or jump
        Assert.Equal(0, analysis.StallFrames);
        Assert.Equal(0, analysis.JumpFrames);
        Assert.True(analysis.MinDisplacement > 0f, "Position must strictly advance every frame.");
    }

    [Fact]
    public void ReplicaSpan_SampleAcrossMissingSnapshotTick_InterpolatesLinearly()
    {
        ClientReplica replica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        EntityId unitId = new(1);

        // Snapshot 1: Tick 10 at (10, 0)
        OwnUnitObservation u10 = new(unitId, 0, new Vector2(10f, 0f), 0);
        int sz10 = WireProtocol.WriteSnapshot(buffer, 1, 10, [u10]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, sz10)));

        // Snapshot 2: Tick 12 at (12, 0)
        OwnUnitObservation u12 = new(unitId, 0, new Vector2(12f, 0f), 0);
        int sz12 = WireProtocol.WriteSnapshot(buffer, 2, 12, [u12]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, sz12)));

        // Tick 14 is dropped (never arrives)

        // Snapshot 3: Tick 16 at (16, 0)
        OwnUnitObservation u16 = new(unitId, 0, new Vector2(16f, 0f), 0);
        int sz16 = WireProtocol.WriteSnapshot(buffer, 3, 16, [u16]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, sz16)));

        // Sample across the missing tick 14 gap
        Vector2 pos12 = replica.SamplePosition(unitId, 12.0, out int l12, out int r12, out float a12);
        Assert.Equal(new Vector2(12f, 0f), pos12);
        Assert.Equal(12, l12);
        Assert.Equal(12, r12);

        Vector2 pos13 = replica.SamplePosition(unitId, 13.0, out int l13, out int r13, out float a13);
        Assert.Equal(12, l13);
        Assert.Equal(16, r13);
        Assert.Equal(0.25f, a13, 3);
        Assert.True(MathF.Abs(pos13.X - 13f) < 0.01f);

        Vector2 pos14 = replica.SamplePosition(unitId, 14.0, out int l14, out int r14, out float a14);
        Assert.Equal(12, l14);
        Assert.Equal(16, r14);
        Assert.Equal(0.50f, a14, 3);
        Assert.True(MathF.Abs(pos14.X - 14f) < 0.01f);

        Vector2 pos15 = replica.SamplePosition(unitId, 15.0, out int l15, out int r15, out float a15);
        Assert.Equal(12, l15);
        Assert.Equal(16, r15);
        Assert.Equal(0.75f, a15, 3);
        Assert.True(MathF.Abs(pos15.X - 15f) < 0.01f);

        Vector2 pos16 = replica.SamplePosition(unitId, 16.0, out int l16, out int r16, out float a16);
        Assert.Equal(new Vector2(16f, 0f), pos16);
    }

    [Fact]
    public void MissingSnapshot_LiveDelivery_BoundedUnderflowAndGentleRecovery()
    {
        (ITransport clientTransport, ITransport serverTransport) =
            InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);
        using ClientSession session = new(clientTransport);
        EntityId unitId = new(1);

        const float Speed = 10f;
        const double Dt = 1.0 / 60.0;
        double serverTime = 0.0;
        int nextSnapshotTick = 0;
        int sequence = 1;

        // Warm up for ~4.9 seconds (ticks 0 through 98)
        while (serverTime < 4.90)
        {
            serverTime += Dt;
            while (serverTime >= nextSnapshotTick * Simulation.TickSeconds)
            {
                float posX = nextSnapshotTick * Simulation.TickSeconds * Speed;
                OwnUnitObservation u = new(unitId, 0, new Vector2(posX, 0f), 0);
                byte[] bytes = WriteSnapshot(sequence++, nextSnapshotTick, [u]);
                serverTransport.TrySend(bytes);
                nextSnapshotTick += 2;
            }
            session.Advance(Dt);
        }

        // Tick 98 is the last snapshot before the drop.
        // Tick 100 at t = 5.00s is dropped.
        // Tick 102 arrives at t = 5.10s.
        int droppedTick = 100;
        double prevRenderTick = session.RenderTick;
        int holdFrames = 0;
        int recoveredFrames = 0;
        double maxCatchupSpeed = 0.0;

        while (serverTime < 5.60)
        {
            serverTime += Dt;
            while (serverTime >= nextSnapshotTick * Simulation.TickSeconds)
            {
                int tickToSend = nextSnapshotTick;
                nextSnapshotTick += 2;
                if (tickToSend == droppedTick)
                {
                    // Drop this packet entirely
                    continue;
                }
                float posX = tickToSend * Simulation.TickSeconds * Speed;
                OwnUnitObservation u = new(unitId, 0, new Vector2(posX, 0f), 0);
                byte[] bytes = WriteSnapshot(sequence++, tickToSend, [u]);
                serverTransport.TrySend(bytes);
            }

            session.Advance(Dt);

            double renderTick = session.RenderTick;

            // Monotonic invariant: render clock must never rewind
            Assert.True(renderTick >= prevRenderTick, $"Render tick rewound from {prevRenderTick} to {renderTick}");

            double dTick = renderTick - prevRenderTick;
            double effectiveSpeed = dTick / (Dt * Simulation.TickRate);

            if (serverTime >= 5.05 && serverTime <= 5.09)
            {
                // Buffer exhausted: clock holds at latest available tick (98.0)
                if (dTick < 1e-6)
                {
                    holdFrames++;
                    Assert.True(Math.Abs(renderTick - 98.0) < 0.01, $"Expected hold at tick 98, got {renderTick}");
                }
            }
            else if (serverTime >= 5.10 && serverTime <= 5.35)
            {
                // Recovery: gentle catch-up bounded by 1.25x speed
                if (effectiveSpeed > 1.01)
                {
                    recoveredFrames++;
                    maxCatchupSpeed = Math.Max(maxCatchupSpeed, effectiveSpeed);
                    Assert.True(effectiveSpeed <= 1.255, $"Catchup speed exceeded bound: {effectiveSpeed:F3}");
                }
            }

            prevRenderTick = renderTick;
        }

        // Assert that the buffer cleanly held during underflow
        Assert.True(holdFrames >= 1, $"Expected at least 1 zero-advance hold frame during underflow, got {holdFrames}");
        // Assert that gentle catch-up occurred without exceeding the 1.25x limit
        Assert.True(recoveredFrames >= 2, $"Expected catch-up frames, got {recoveredFrames}");
        Assert.True(maxCatchupSpeed <= 1.255, $"Max catchup speed {maxCatchupSpeed:F3} exceeded 1.25x limit");
    }

    [Fact]
    public void MotionDiagnostics_DetectsSpatialStallsAndJumps_DistinguishesFromClock()
    {
        // 1. Frozen position test (advancing clock, zero displacement)
        MotionDiagnostics frozen = new(60);
        for (int i = 0; i < 60; i++)
        {
            frozen.Record(1.0 / 60.0, i / 3 * 2, i / 3.0, 0, 2, 0.5f, new Vector2(100f, 0f), 0f);
        }

        // When expected speed is 10 m/s, frozen position must flag position stalls
        MotionDiagnostics.Analysis frozenAnalysis = frozen.Analyze(warmupFrames: 30, expectedSpeed: 10f);
        Assert.Equal(0, frozenAnalysis.ClockStallFrames);
        Assert.Equal(30, frozenAnalysis.PositionStallFrames);
        Assert.Equal(30, frozenAnalysis.StallFrames);

        // When expected speed is 0 m/s (stationary), it should NOT flag stalls or jumps
        MotionDiagnostics.Analysis stoppedAnalysis = frozen.Analyze(warmupFrames: 30, expectedSpeed: 0f);
        Assert.Equal(0, stoppedAnalysis.ClockStallFrames);
        Assert.Equal(0, stoppedAnalysis.PositionStallFrames);
        Assert.Equal(0, stoppedAnalysis.StallFrames);

        // 2. Teleport jump test
        MotionDiagnostics teleport = new(60);
        for (int i = 0; i < 60; i++)
        {
            float disp = i == 40 ? 10000f : 1f;
            Vector2 pos = i == 40 ? new Vector2(10000f, 0f) : new Vector2(i, 0f);
            teleport.Record(1.0 / 60.0, i / 3 * 2, i / 3.0, 0, 2, 0.5f, pos, disp);
        }

        // Nominal speed is 1 m per (1/60 s) = 60 m/s
        MotionDiagnostics.Analysis teleportAnalysis = teleport.Analyze(warmupFrames: 30, expectedSpeed: 60f);
        Assert.Equal(0, teleportAnalysis.ClockJumpFrames);
        Assert.Equal(1, teleportAnalysis.PositionJumpFrames);
        Assert.Equal(1, teleportAnalysis.JumpFrames);

        // 3. Zero warmup frames must not throw IndexOutOfRangeException
        MotionDiagnostics.Analysis zeroWarmupAnalysis = frozen.Analyze(warmupFrames: 0);
        Assert.Equal(59, zeroWarmupAnalysis.SampleCount);
    }

    [Fact]
    public void MotionDiagnostics_MovingToStoppedAndStoppedToMoving_NoFalsePositivesInDefaultMode()
    {
        const double Dt = 1.0 / 60.0;
        const float Speed = 10f;
        float frameDisp = Speed * (float)Dt; // ~0.1667 m

        // 1. Moving then stopped (120 frames cruise + 120 frames halt)
        MotionDiagnostics movingThenStopped = new(250);
        Vector2 pos = Vector2.Zero;
        double renderTick = 0.0;

        for (int frame = 0; frame < 240; frame++)
        {
            renderTick += Dt * 20.0;
            float disp = frame < 120 ? frameDisp : 0f;
            pos += new Vector2(disp, 0f);
            movingThenStopped.Record(Dt, (int)renderTick, renderTick, 0, 2, 0.5f, pos, disp);
        }

        MotionDiagnostics.Analysis msAnalysis = movingThenStopped.Analyze(warmupFrames: 30);
        Assert.Equal(0, msAnalysis.ClockStallFrames);
        Assert.Equal(0, msAnalysis.ClockJumpFrames);
        Assert.Equal(0, msAnalysis.ClockStutterFrames);
        Assert.Equal(0, msAnalysis.PositionStallFrames);
        Assert.Equal(0, msAnalysis.PositionJumpFrames);
        Assert.Equal(0, msAnalysis.PositionStutterFrames);
        Assert.Equal(0, msAnalysis.StallFrames);
        Assert.Equal(0, msAnalysis.JumpFrames);
        Assert.Equal(0, msAnalysis.StutterFrames);

        // 2. Stopped then moving (120 frames halt + 120 frames cruise)
        MotionDiagnostics stoppedThenMoving = new(250);
        pos = Vector2.Zero;
        renderTick = 0.0;

        for (int frame = 0; frame < 240; frame++)
        {
            renderTick += Dt * 20.0;
            float disp = frame >= 120 ? frameDisp : 0f;
            pos += new Vector2(disp, 0f);
            stoppedThenMoving.Record(Dt, (int)renderTick, renderTick, 0, 2, 0.5f, pos, disp);
        }

        MotionDiagnostics.Analysis smAnalysis = stoppedThenMoving.Analyze(warmupFrames: 30);
        Assert.Equal(0, smAnalysis.ClockStallFrames);
        Assert.Equal(0, smAnalysis.ClockJumpFrames);
        Assert.Equal(0, smAnalysis.ClockStutterFrames);
        Assert.Equal(0, smAnalysis.PositionStallFrames);
        Assert.Equal(0, smAnalysis.PositionJumpFrames);
        Assert.Equal(0, smAnalysis.PositionStutterFrames);
        Assert.Equal(0, smAnalysis.StallFrames);
        Assert.Equal(0, smAnalysis.JumpFrames);
        Assert.Equal(0, smAnalysis.StutterFrames);
    }

    [Fact]
    public void MotionDiagnostics_AggregateUnion_NeverExceedsSampleCountOrDoubleCounts()
    {
        const double Dt = 1.0 / 60.0;
        MotionDiagnostics diag = new(60);

        // 30 warmup frames advancing normally at 10 m/s
        double renderTick = 0.0;
        Vector2 pos = Vector2.Zero;
        for (int frame = 0; frame < 30; frame++)
        {
            renderTick += Dt * 20.0;
            pos += new Vector2(10f * (float)Dt, 0f);
            diag.Record(Dt, (int)renderTick, renderTick, 0, 2, 0.5f, pos, 10f * (float)Dt);
        }

        // 30 frames where BOTH clock AND position freeze simultaneously
        for (int frame = 30; frame < 60; frame++)
        {
            // renderTick does NOT advance (clock freeze)
            // position does NOT advance (position freeze)
            diag.Record(Dt, (int)renderTick, renderTick, 0, 2, 0.5f, pos, 0f);
        }

        MotionDiagnostics.Analysis analysis = diag.Analyze(warmupFrames: 30, expectedSpeed: 10f);

        Assert.Equal(30, analysis.SampleCount);
        Assert.Equal(30, analysis.ClockStallFrames);
        Assert.Equal(30, analysis.PositionStallFrames);

        // Union count must be 30, strictly bounded by SampleCount (NEVER 60)
        Assert.Equal(30, analysis.StallFrames);
        Assert.True(analysis.StallFrames <= analysis.SampleCount);
        Assert.True(analysis.JumpFrames <= analysis.SampleCount);
        Assert.True(analysis.StutterFrames <= analysis.SampleCount);
    }

    [Fact]
    public void MotionDiagnostics_Reset_ClearsSamplesAndIsolatesEpochs()
    {
        MotionDiagnostics diag = new(100);
        for (int i = 0; i < 20; i++)
        {
            diag.Record(1.0 / 60.0, i, i * 0.5, 0, 1, 0.5f, new Vector2(i, 0f), 0.5f);
        }
        Assert.Equal(20, diag.Count);

        diag.Reset();
        Assert.Equal(0, diag.Count);
        Assert.Equal(0, diag.Samples.Length);

        // New epoch begins with empty buffer
        diag.Record(1.0 / 60.0, 0, 0.0, 0, 1, 0.0f, Vector2.Zero, 0f);
        Assert.Equal(1, diag.Count);
    }

    [Fact]
    public void PlaybackSpeed_1xAnd2x_ScaleDisplacementLinearlyWithoutStalls()
    {
        StaticGeometry geometry = TankBattleScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();

        // 1. Run at 1x speed
        using (Simulation sim1 = TankBattleScenario.CreateSimulation(geometry, catalog, 12345))
        using (InMemoryServerTransport transport1 = new(2))
        {
            MatchSetup setup = TankBattleScenario.CreateMatchSetup(geometry, catalog);
            using SimulationHost host1 = new(sim1, transport1, setup);
            using ClientSession client1 = new(transport1.CreateClientTransport(1), setup);

            host1.Advance(0);
            client1.Advance(0);

            // Warm up until tanks reach steady cruising speed (past initial bot order ticks)
            for (int i = 0; i < 240; i++)
            {
                host1.Advance(1.0 / 60.0);
                client1.Advance(1.0 / 60.0);
            }

            EntityId tankId = new(1);
            MotionDiagnostics diag1x = new(200);
            Vector2 prevPos = client1.Replica.SamplePosition(tankId, client1.RenderTick);
            const double Dt = 1.0 / 60.0;

            for (int f = 0; f < 120; f++)
            {
                host1.Advance(Dt);
                client1.Advance(Dt);
                Vector2 pos = client1.Replica.SamplePosition(tankId, client1.RenderTick,
                    out int left, out int right, out float alpha);
                float disp = (pos - prevPos).Length();
                prevPos = pos;
                diag1x.Record(Dt, client1.Tick, client1.RenderTick, left, right, alpha, pos, disp);
            }

            MotionDiagnostics.Analysis analysis1x = diag1x.Analyze(warmupFrames: 10);
            Assert.Equal(0, analysis1x.StallFrames);
            Assert.Equal(0, analysis1x.JumpFrames);

            // 2. Run at 2x speed with same initial conditions
            using Simulation sim2 = TankBattleScenario.CreateSimulation(geometry, catalog, 12345);
            using InMemoryServerTransport transport2 = new(2);
            using SimulationHost host2 = new(sim2, transport2, setup);
            using ClientSession client2 = new(transport2.CreateClientTransport(1), setup);

            host2.Advance(0);
            client2.Advance(0);

            // Warm up identically to cruise speed
            for (int i = 0; i < 240; i++)
            {
                host2.Advance(1.0 / 60.0);
                client2.Advance(1.0 / 60.0);
            }

            MotionDiagnostics diag2x = new(200);
            prevPos = client2.Replica.SamplePosition(tankId, client2.RenderTick);
            const double Dt2x = (1.0 / 60.0) * 2.0;

            for (int f = 0; f < 120; f++)
            {
                host2.Advance(Dt2x);
                client2.Advance(Dt2x);
                Vector2 pos = client2.Replica.SamplePosition(tankId, client2.RenderTick,
                    out int left, out int right, out float alpha);
                float disp = (pos - prevPos).Length();
                prevPos = pos;
                diag2x.Record(Dt2x, client2.Tick, client2.RenderTick, left, right, alpha, pos, disp);
            }

            MotionDiagnostics.Analysis analysis2x = diag2x.Analyze(warmupFrames: 10);
            Assert.Equal(0, analysis2x.StallFrames);
            Assert.Equal(0, analysis2x.JumpFrames);

            // Assert 2x mean displacement is approximately double 1x mean displacement
            float ratio = analysis2x.MeanDisplacement / analysis1x.MeanDisplacement;
            Assert.True(MathF.Abs(ratio - 2.0f) < 0.05f, $"Expected 2x ratio ~2.0, got {ratio:F3}");
        }
    }

    [Fact]
    public void PauseAndResume_FreezesClockDuringPause_ResumesWithoutJumping()
    {
        StaticGeometry geometry = TankBattleScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using Simulation sim = TankBattleScenario.CreateSimulation(geometry, catalog, 54321);
        using InMemoryServerTransport transport = new(2);
        MatchSetup setup = TankBattleScenario.CreateMatchSetup(geometry, catalog);
        using SimulationHost host = new(sim, transport, setup);
        using ClientSession client = new(transport.CreateClientTransport(1), setup);

        host.Advance(0);
        client.Advance(0);

        EntityId tankId = new(1);
        const double Dt = 1.0 / 60.0;

        // Advance 40 frames normally
        for (int i = 0; i < 40; i++)
        {
            host.Advance(Dt);
            client.Advance(Dt);
        }

        double renderTickBeforePause = client.RenderTick;
        Vector2 posBeforePause = client.Replica.SamplePosition(tankId, renderTickBeforePause);

        // Pause for 30 frames (elapsed = 0)
        for (int i = 0; i < 30; i++)
        {
            // When paused, demo advances 0 seconds
            client.Advance(0);
            Assert.Equal(renderTickBeforePause, client.RenderTick);
            Assert.Equal(posBeforePause, client.Replica.SamplePosition(tankId, client.RenderTick));
        }

        // Resume: advance 1 frame
        host.Advance(Dt);
        client.Advance(Dt);

        double renderTickAfterResume = client.RenderTick;
        Vector2 posAfterResume = client.Replica.SamplePosition(tankId, renderTickAfterResume);

        double deltaTick = renderTickAfterResume - renderTickBeforePause;
        float displacement = (posAfterResume - posBeforePause).Length();

        Assert.True(deltaTick > 0, "Render tick must resume advancing.");
        Assert.True(deltaTick <= Dt * Simulation.TickRate * 1.5, $"Delta tick {deltaTick:F4} jumped unexpectedly.");
        Assert.True(displacement > 0, "Tank must resume moving.");
        Assert.True(displacement < 0.35f, $"Displacement {displacement:F4} leaped after resume.");
    }

    [Fact]
    public void UnitLifecycle_RemovalAndSpawn_DoesNotInterpolateUnrelatedEntities()
    {
        ClientReplica replica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        EntityId unit1 = new(10);
        EntityId unit2 = new(20);

        // Snapshot 1: Unit 1 active at (100, 100). Unit 2 does not exist.
        OwnUnitObservation u1Tick10 = new(unit1, 0, new Vector2(100f, 100f), 0);
        int sz10 = WireProtocol.WriteSnapshot(buffer, 1, 10, [u1Tick10]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, sz10)));

        // Snapshot 2: Unit 1 active at (102, 100).
        OwnUnitObservation u1Tick12 = new(unit1, 0, new Vector2(102f, 100f), 0);
        int sz12 = WireProtocol.WriteSnapshot(buffer, 2, 12, [u1Tick12]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, sz12)));

        // Snapshot 3: Unit 1 destroyed (removed). Unit 2 spawned at (500, 500).
        OwnUnitObservation u2Tick14 = new(unit2, 0, new Vector2(500f, 500f), 0);
        int sz14 = WireProtocol.WriteSnapshot(buffer, 3, 14, [u2Tick14]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, sz14)));

        // Unit 1 was removed in frame 14. Sampling at tick 13 (between 12 and 14) must not throw or lerp to 0
        Vector2 posUnit1At13 = replica.SamplePosition(unit1, 13.0);
        Assert.Equal(default, posUnit1At13); // Not in latest frame, returns default without error

        // Newly spawned Unit 2 at tick 13 (before its existence) must return its latest initial spawn position,
        // NEVER interpolating from (0,0) or unrelated unit positions!
        Vector2 posUnit2At13 = replica.SamplePosition(unit2, 13.0);
        Assert.Equal(new Vector2(500f, 500f), posUnit2At13);
    }

    [Fact]
    public void SteadyStateHotPath_AllocatesZeroBytesOnHeap()
    {
        (ITransport clientTransport, ITransport serverTransport) =
            InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);
        using ClientSession session = new(clientTransport);
        EntityId unitId = new(1);

        // Warm up and pre-fill replica history
        for (int tick = 0; tick < 20; tick += 2)
        {
            OwnUnitObservation u = new(unitId, 0, new Vector2(tick * 0.5f, 0f), 0);
            byte[] bytes = WriteSnapshot(tick + 1, tick, [u]);
            serverTransport.TrySend(bytes);
            session.Advance(0.016667);
            session.Replica.SamplePosition(unitId, session.RenderTick, out _, out _, out _);
        }

        // Measure allocations in steady-state loop
        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        const int Iterations = 60;
        for (int i = 0; i < Iterations; i++)
        {
            session.Advance(0.016667);
            session.Replica.SamplePosition(unitId, session.RenderTick, out int left, out int right, out float alpha);
        }
        long afterAllocated = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, afterAllocated - beforeAllocated);
    }

    [Fact]
    public void TankBattle_CruiseFormationMotion_ScenarioMaintainsSmoothness()
    {
        StaticGeometry geometry = TankBattleScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using Simulation simulation = TankBattleScenario.CreateSimulation(geometry, catalog, TankBattleScenario.DefaultSeed);
        using InMemoryServerTransport transport = new(2);
        MatchSetup setup = TankBattleScenario.CreateMatchSetup(geometry, catalog);
        using SimulationHost host = new(simulation, transport, setup);
        using ClientSession soviet = new(transport.CreateClientTransport(1), setup);
        using ClientSession us = new(transport.CreateClientTransport(2), setup);

        host.Advance(0);
        soviet.Advance(0);
        us.Advance(0);

        EntityId sovietTank = new(1);
        EntityId usTank = new(101);
        MotionDiagnostics sovietDiag = new(350);
        MotionDiagnostics usDiag = new(350);
        Vector2 prevSovietPos = Vector2.Zero;
        Vector2 prevUsPos = Vector2.Zero;

        // Realistic frame timing with display refresh jitter (~60 FPS +/- 1.5ms)
        Random rng = new(20261023);
        const int TotalFrames = 300;

        for (int frame = 0; frame < TotalFrames; frame++)
        {
            double dt = (1.0 / 60.0) + (rng.NextDouble() - 0.5) * 0.003;
            host.Advance(dt);
            soviet.Advance(dt);
            us.Advance(dt);

            Vector2 sPos = soviet.Replica.SamplePosition(sovietTank, soviet.RenderTick,
                out int sLeft, out int sRight, out float sAlpha);
            float sDisp = frame > 0 ? (sPos - prevSovietPos).Length() : 0f;
            prevSovietPos = sPos;
            sovietDiag.Record(dt, soviet.Tick, soviet.RenderTick, sLeft, sRight, sAlpha, sPos, sDisp);

            Vector2 uPos = us.Replica.SamplePosition(usTank, us.RenderTick,
                out int uLeft, out int uRight, out float uAlpha);
            float uDisp = frame > 0 ? (uPos - prevUsPos).Length() : 0f;
            prevUsPos = uPos;
            usDiag.Record(dt, us.Tick, us.RenderTick, uLeft, uRight, uAlpha, uPos, uDisp);
        }

        MotionDiagnostics.Analysis sAnalysis = sovietDiag.Analyze(warmupFrames: 70);
        MotionDiagnostics.Analysis uAnalysis = usDiag.Analyze(warmupFrames: 70);

        Assert.Equal(0, sAnalysis.ClockStallFrames);
        Assert.Equal(0, sAnalysis.ClockJumpFrames);
        Assert.Equal(0, sAnalysis.ClockStutterFrames);
        Assert.Equal(0, sAnalysis.PositionStallFrames);
        Assert.Equal(0, sAnalysis.PositionJumpFrames);

        Assert.Equal(0, uAnalysis.ClockStallFrames);
        Assert.Equal(0, uAnalysis.ClockJumpFrames);
        Assert.Equal(0, uAnalysis.ClockStutterFrames);
        Assert.Equal(0, uAnalysis.PositionStallFrames);
        Assert.Equal(0, uAnalysis.PositionJumpFrames);
    }
}
