using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using Xunit;

namespace DeadGround.Tests;

public sealed class TransportTests
{
    [Fact]
    public void TransportCopiesMessagesAndRespectsCapacity()
    {
        (ITransport client, ITransport server) = InMemoryTransport.CreatePair(1, 16);
        using (client)
        using (server)
        {
            byte[] source = [1, 2, 3];
            Assert.True(client.TrySend(source));
            source[0] = 99;
            Assert.False(client.TrySend(source));
            Span<byte> destination = stackalloc byte[16];
            Assert.True(server.TryReceive(destination, out int length));
            Assert.Equal(3, length);
            Assert.Equal((byte)1, destination[0]);
            Assert.False(server.TryReceive(destination, out _));
            Assert.True(client.TrySend(source));
        }
    }

    [Fact]
    public void MoveCodecRejectsTruncationAndNonFiniteCoordinates()
    {
        Span<byte> bytes = stackalloc byte[WireProtocol.MoveSize];
        MoveCommand original = new(7, 20, new EntityId(5), new Vector2(1, 3));
        WireProtocol.WriteMove(bytes, original);
        Assert.True(WireProtocol.TryReadMove(bytes, out MoveCommand decoded));
        Assert.Equal(original, decoded);
        Assert.False(WireProtocol.TryReadMove(bytes[..^1], out _));
        WireProtocol.WriteMove(bytes, original with { Target = new Vector2(float.PositiveInfinity, 0) });
        Assert.False(WireProtocol.TryReadMove(bytes, out _));
    }

    [Fact]
    public void ReplicaRejectsIncompleteAndOldSnapshotsWithoutChangingState()
    {
        ClientReplica replica = new(4);
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];
        OwnUnitObservation[] units = [new(new EntityId(1), 0, new Vector2(10, 20), 0)];
        int size = WireProtocol.WriteSnapshot(bytes, 1, 10, units);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size)));
        units[0] = units[0] with { Position = new Vector2(30, 40) };
        size = WireProtocol.WriteSnapshot(bytes, 2, 12, units);
        Assert.False(replica.TryApply(bytes.AsSpan(0, size - 1)));
        Assert.Equal(new Vector2(10, 20), replica.OwnUnits[0].Position);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size)));
        size = WireProtocol.WriteSnapshot(bytes, 1, 10, units);
        Assert.False(replica.TryApply(bytes.AsSpan(0, size)));
        Assert.Equal(12, replica.LatestTick);
    }

    [Fact]
    public void CorruptSnapshotCannotOverwriteHistoryWhenTheRingIsFull()
    {
        ClientReplica replica = new(4);
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];
        OwnUnitObservation[] units = [new(new EntityId(1), 0, Vector2.Zero, 0)];
        for (uint i = 1; i <= 8; i++)
        {
            units[0] = units[0] with { Position = new Vector2(i, 0) };
            int size = WireProtocol.WriteSnapshot(bytes, i, (int)i * 2, units);
            Assert.True(replica.TryApply(bytes.AsSpan(0, size)));
        }
        OwnUnitObservation[] duplicateIds = [units[0] with { Position = new Vector2(999, 0) }, units[0]];
        int corruptSize = WireProtocol.WriteSnapshot(bytes, 9, 18, duplicateIds);
        Assert.False(replica.TryApply(bytes.AsSpan(0, corruptSize)));
        Assert.Equal(new Vector2(1, 0), replica.SamplePosition(new EntityId(1), 2));
        Assert.Equal(16, replica.LatestTick);
    }

    [Fact]
    public void InterpolationUsesHistoryAndRemovedUnitsDisappear()
    {
        ClientReplica replica = new(4);
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];
        OwnUnitObservation[] units = [new(new EntityId(1), 0, Vector2.Zero, 0)];
        int size = WireProtocol.WriteSnapshot(bytes, 1, 10, units);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size)));
        units[0] = units[0] with { Position = new Vector2(10, 0) };
        size = WireProtocol.WriteSnapshot(bytes, 2, 12, units);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size)));
        Assert.Equal(new Vector2(5, 0), replica.SamplePosition(new EntityId(1), 11));
        size = WireProtocol.WriteSnapshot(bytes, 3, 14, []);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size)));
        Assert.Empty(replica.OwnUnits.ToArray());
    }
}
