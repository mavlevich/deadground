using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using Xunit;

namespace DeadGround.Tests;

public sealed class HostTests
{
    private sealed class ManualTime : TimeProvider
    {
        public long Milliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }

    [Fact]
    public void DelayedWakeupMeasuresActualElapsedTimeAndCatchUpPreservesItsDebt()
    {
        ManualTime time = new();
        ElapsedTimeClock clock = new(time);
        using LocalSession session = new(new Simulation(new WorldState(HeightMap.CreatePrototype()), new RuleSet()));
        time.Milliseconds = 600;
        session.Advance(clock.ReadSeconds());
        Assert.Equal(SimulationHost.MaxTicksPerAdvance, session.Tick);
        Assert.InRange(session.PendingSeconds, 0.34999, 0.35001);
        session.Advance(clock.ReadSeconds());
        session.Advance(clock.ReadSeconds());
        Assert.Equal(12, session.Tick);
        Assert.InRange(session.PendingSeconds, 0, 1e-9);
        time.Milliseconds = 650;
        session.Advance(clock.ReadSeconds());
        Assert.Equal(13, session.Tick);
    }

    [Fact]
    public void OversizedWorldIsRejectedBeforeAHostOrSessionCanStart()
    {
        using Simulation simulation = new(new WorldState(HeightMap.CreatePrototype(), WireProtocol.MaxUnits + 1), new RuleSet());
        (ITransport client, ITransport server) = InMemoryTransport.CreatePair();
        using (client)
        using (server)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SimulationHost(simulation, server, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LocalSession(simulation));
        }
    }

    [Fact]
    public void FullSupportedWorldFitsOneSnapshotAndReachesTheReplica()
    {
        WorldState world = new(HeightMap.CreatePrototype(), WireProtocol.MaxUnits);
        for (int i = 0; i < WireProtocol.MaxUnits; i++) world.CreateUnit(1, 0, new Vector2(1, 1), 1);
        using LocalSession session = new(new Simulation(world, new RuleSet()));
        Assert.Equal(WireProtocol.MaxUnits, session.Replica.OwnUnits.Length);
        Assert.Equal(WireProtocol.SnapshotHeaderSize + WireProtocol.MaxUnits * WireProtocol.UnitSize, session.BytesReceived);
    }

    [Fact]
    public void ServerReportsQueuedAndRejectedCommandsThroughTheTransport()
    {
        WorldState world = new(HeightMap.CreatePrototype());
        EntityId id = world.CreateUnit(1, 0, new Vector2(1, 1), 1);
        using LocalSession session = new(new Simulation(world, new RuleSet(new MovementSystem())));
        Assert.True(session.Move(id, new Vector2(5, 5)));
        session.Advance(0.05);
        Assert.Equal(new CommandResult(1, 0, true), session.LastCommandResult);
        Assert.True(session.Move(id, new Vector2(500, 500)));
        session.Advance(0.05);
        Assert.Equal(new CommandResult(2, 1, false), session.LastCommandResult);
        Assert.Equal(1L, session.RejectedCommands);
        Assert.Equal(new Vector2(5, 5), world.GetUnitAt(0).Target);
    }

    [Fact]
    public void CommandResultCodecRejectsMalformedMessages()
    {
        Span<byte> bytes = stackalloc byte[WireProtocol.CommandResultSize];
        CommandResult result = new(7, 10, false);
        WireProtocol.WriteCommandResult(bytes, result);
        Assert.True(WireProtocol.TryReadCommandResult(bytes, out CommandResult decoded));
        Assert.Equal(result, decoded);
        Assert.False(WireProtocol.TryReadCommandResult(bytes[..^1], out _));
        bytes[10] = 2;
        Assert.False(WireProtocol.TryReadCommandResult(bytes, out _));
    }

    [Fact]
    public void ClientReplica_SampleContactPosition_InterpolatesCurrentContacts()
    {
        ClientReplica replica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        ContactId contactId = new(1);
        ContactObservation c1 = new(contactId, ContactState.Current, ContactClassification.Unknown, 0, new Vector2(10, 10), 10);
        int size1 = WireProtocol.WriteSnapshot(buffer, sequence: 1, tick: 10, units: [], contacts: [c1]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, size1)));

        ContactObservation c2 = new(contactId, ContactState.Current, ContactClassification.Unknown, 0, new Vector2(30, 10), 20);
        int size2 = WireProtocol.WriteSnapshot(buffer, sequence: 2, tick: 20, units: [], contacts: [c2]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, size2)));

        // Midpoint at renderTick = 15
        Vector2 sampled = replica.SampleContactPosition(contactId, renderTick: 15.0);
        Assert.Equal(20f, sampled.X, 0.001f);
        Assert.Equal(10f, sampled.Y, 0.001f);
    }

    [Fact]
    public void ClientReplica_SampleContactPosition_DoesNotInterpolateLastKnownContacts()
    {
        ClientReplica replica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        ContactId contactId = new(1);
        ContactObservation c1 = new(contactId, ContactState.Current, ContactClassification.Unknown, 0, new Vector2(10, 10), 10);
        int size1 = WireProtocol.WriteSnapshot(buffer, sequence: 1, tick: 10, units: [], contacts: [c1]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, size1)));

        // In second frame, contact becomes LastKnown at (10, 10)
        ContactObservation c2 = new(contactId, ContactState.LastKnown, ContactClassification.Unknown, 0, new Vector2(10, 10), 10);
        int size2 = WireProtocol.WriteSnapshot(buffer, sequence: 2, tick: 20, units: [], contacts: [c2]);
        Assert.True(replica.TryApply(buffer.AsSpan(0, size2)));

        Vector2 sampled = replica.SampleContactPosition(contactId, renderTick: 15.0);
        Assert.Equal(10f, sampled.X);
        Assert.Equal(10f, sampled.Y);
    }
}
