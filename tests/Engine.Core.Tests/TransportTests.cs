using System.Diagnostics;
using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using LiteNetLib;
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
    public void MoveCodec_RoundTripsOptionalFinalFacing_AndRejectsNonFiniteFacing()
    {
        Span<byte> bytes = stackalloc byte[WireProtocol.MoveSize];

        // 1. Null final facing (standard move)
        MoveCommand withoutFacing = new(10, 5, new EntityId(2), new Vector2(12f, 34f), FinalFacing: null);
        WireProtocol.WriteMove(bytes, withoutFacing);
        Assert.True(WireProtocol.TryReadMove(bytes, out MoveCommand readWithoutFacing));
        Assert.Equal(withoutFacing, readWithoutFacing);
        Assert.Null(readWithoutFacing.FinalFacing);

        // 2. Zero final facing (North)
        MoveCommand zeroFacing = new(11, 5, new EntityId(2), new Vector2(12f, 34f), FinalFacing: 0.0f);
        WireProtocol.WriteMove(bytes, zeroFacing);
        Assert.True(WireProtocol.TryReadMove(bytes, out MoveCommand readZeroFacing));
        Assert.Equal(zeroFacing, readZeroFacing);
        Assert.NotNull(readZeroFacing.FinalFacing);
        Assert.Equal(0.0f, readZeroFacing.FinalFacing.Value);

        // 3. Positive and negative finite facings
        MoveCommand positiveFacing = new(12, 5, new EntityId(2), new Vector2(12f, 34f), FinalFacing: 1.57079f);
        WireProtocol.WriteMove(bytes, positiveFacing);
        Assert.True(WireProtocol.TryReadMove(bytes, out MoveCommand readPositiveFacing));
        Assert.Equal(positiveFacing, readPositiveFacing);

        MoveCommand negativeFacing = new(13, 5, new EntityId(2), new Vector2(12f, 34f), FinalFacing: -2.35619f);
        WireProtocol.WriteMove(bytes, negativeFacing);
        Assert.True(WireProtocol.TryReadMove(bytes, out MoveCommand readNegativeFacing));
        Assert.Equal(negativeFacing, readNegativeFacing);

        // 4. Non-finite final facing rejected
        WireProtocol.WriteMove(bytes, withoutFacing with { FinalFacing = float.NaN });
        Assert.False(WireProtocol.TryReadMove(bytes, out _));

        WireProtocol.WriteMove(bytes, withoutFacing with { FinalFacing = float.PositiveInfinity });
        Assert.False(WireProtocol.TryReadMove(bytes, out _));

        WireProtocol.WriteMove(bytes, withoutFacing with { FinalFacing = float.NegativeInfinity });
        Assert.False(WireProtocol.TryReadMove(bytes, out _));
    }

    [Fact]
    public void MoveCodec_RoundTripsMoveMode_AndRejectsInvalidMode()
    {
        Span<byte> bytes = stackalloc byte[WireProtocol.MoveSize];

        // 1. MoveMode.Forward
        MoveCommand forwardCmd = new(14, 5, new EntityId(2), new Vector2(10f, 20f), FinalFacing: 1.0f, Mode: MoveMode.Forward);
        WireProtocol.WriteMove(bytes, forwardCmd);
        Assert.True(WireProtocol.TryReadMove(bytes, out MoveCommand readForward));
        Assert.Equal(forwardCmd, readForward);
        Assert.Equal(MoveMode.Forward, readForward.Mode);

        // 2. MoveMode.Reverse
        MoveCommand reverseCmd = new(15, 5, new EntityId(2), new Vector2(10f, 20f), FinalFacing: 1.0f, Mode: MoveMode.Reverse);
        WireProtocol.WriteMove(bytes, reverseCmd);
        Assert.True(WireProtocol.TryReadMove(bytes, out MoveCommand readReverse));
        Assert.Equal(reverseCmd, readReverse);
        Assert.Equal(MoveMode.Reverse, readReverse.Mode);

        // 3. Invalid Mode byte value (e.g. 2 or 255)
        bytes[27] = 2;
        Assert.False(WireProtocol.TryReadMove(bytes, out _));
        bytes[27] = 255;
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

    [Fact]
    public void HandshakeCodecRoundTripsAndValidates()
    {
        MatchSetup setup = new(1, 0x123456789ABCDEF0UL, 0xFEDCBA9876543210UL, "Ops");
        Span<byte> requestBytes = stackalloc byte[128];
        int reqLen = WireProtocol.WriteHandshakeRequest(requestBytes, setup);
        Assert.True(WireProtocol.TryReadHandshakeRequest(requestBytes[..reqLen], out MatchSetup decoded));
        Assert.Equal(setup.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(setup.ContentHash, decoded.ContentHash);
        Assert.Equal(setup.MapHash, decoded.MapHash);
        Assert.Equal(setup.RuleSetId, decoded.RuleSetId);
        Assert.False(WireProtocol.TryReadHandshakeRequest(requestBytes[..(reqLen - 1)], out _));

        Span<byte> respBytes = stackalloc byte[WireProtocol.HandshakeResponseSize];
        WireProtocol.WriteHandshakeResponse(respBytes, accepted: true, assignedPlayer: 2, HandshakeReason.Accepted);
        Assert.True(WireProtocol.TryReadHandshakeResponse(respBytes, out bool accepted, out int player, out HandshakeReason reason));
        Assert.True(accepted);
        Assert.Equal(2, player);
        Assert.Equal(HandshakeReason.Accepted, reason);
        Assert.False(WireProtocol.TryReadHandshakeResponse(respBytes[..^1], out _, out _, out _));
    }

    [Fact]
    public void SnapshotAssemblerReassemblesMultiPacketSnapshots()
    {
        byte[] snapshotBytes = new byte[WireProtocol.MaxMessageSize];
        OwnUnitObservation[] units = new OwnUnitObservation[100];
        for (int i = 0; i < units.Length; i++)
            units[i] = new OwnUnitObservation(new EntityId((uint)(i + 1)), 0, new Vector2(i, i), 0);

        int totalSize = WireProtocol.WriteSnapshot(snapshotBytes, sequence: 10, tick: 50, units);
        ushort count = (ushort)((totalSize + WireProtocol.MaxFragmentPayloadSize - 1) / WireProtocol.MaxFragmentPayloadSize);
        Assert.True(count > 1);

        byte[][] fragments = new byte[count][];
        for (ushort i = 0; i < count; i++)
        {
            int offset = i * WireProtocol.MaxFragmentPayloadSize;
            int sliceLen = Math.Min(WireProtocol.MaxFragmentPayloadSize, totalSize - offset);
            fragments[i] = new byte[WireProtocol.FragmentHeaderSize + sliceLen];
            WireProtocol.WriteSnapshotFragment(fragments[i], 10, 50, i, count, totalSize, offset, snapshotBytes.AsSpan(offset, sliceLen));
        }

        SnapshotAssembler assembler = new();

        // Feed fragments in reverse order
        for (int i = count - 1; i > 0; i--)
        {
            Assert.False(assembler.TryAddFragment(fragments[i], out _));
        }

        // Final fragment completes assembly
        Assert.True(assembler.TryAddFragment(fragments[0], out ReadOnlySpan<byte> assembled));
        Assert.Equal(totalSize, assembled.Length);
        Assert.Equal(10u, assembler.LatestCompletedSequence);

        // Stale or duplicate fragments are rejected
        Assert.False(assembler.TryAddFragment(fragments[0], out _));

        ClientReplica replica = new(100);
        Assert.True(replica.TryApply(assembled));
        Assert.Equal(100, replica.OwnUnits.Length);
    }

    [Fact]
    public void InMemoryServerTransportRoutesBetweenMultipleClientsAndServer()
    {
        using InMemoryServerTransport serverTransport = new(maxPlayers: 2);
        using ITransport client1 = serverTransport.CreateClientTransport(1);
        using ITransport client2 = serverTransport.CreateClientTransport(2);

        Assert.Equal(2, serverTransport.ConnectedPlayerCount);
        Assert.True(serverTransport.IsPlayerConnected(1));
        Assert.True(serverTransport.IsPlayerConnected(2));
        Assert.False(serverTransport.IsPlayerConnected(3));

        byte[] c1Msg = [1, 2, 3];
        byte[] c2Msg = [4, 5, 6];
        Assert.True(client1.TrySend(c1Msg));
        Assert.True(client2.TrySend(c2Msg));

        Span<byte> buffer = stackalloc byte[16];
        Assert.True(serverTransport.TryReceive(out int p1, buffer, out int len1));
        Assert.Equal(1, p1);
        Assert.Equal(3, len1);
        Assert.Equal(1, buffer[0]);

        Assert.True(serverTransport.TryReceive(out int p2, buffer, out int len2));
        Assert.Equal(2, p2);
        Assert.Equal(3, len2);
        Assert.Equal(4, buffer[0]);

        Assert.False(serverTransport.TryReceive(out _, buffer, out _));

        byte[] sMsg = [9, 8, 7];
        Assert.True(serverTransport.TrySend(2, sMsg));
        Assert.False(client1.TryReceive(buffer, out _));
        Assert.True(client2.TryReceive(buffer, out int lenClient2));
        Assert.Equal(3, lenClient2);
        Assert.Equal(9, buffer[0]);
    }

    [Fact]
    public void SnapshotAssemblerRejectsInvalidFragmentsAndGaps()
    {
        SnapshotAssembler assembler = new();
        byte[] bytes = new byte[WireProtocol.FragmentHeaderSize + 100];

        // Zero sequence is rejected
        WireProtocol.WriteSnapshotFragment(bytes, sequence: 0, serverTick: 1, fragmentIndex: 0, fragmentCount: 1, totalPayloadSize: 100, fragmentOffset: 0, new byte[100]);
        Assert.False(assembler.TryAddFragment(bytes, out _));

        // Non-canonical offset (e.g. index 0 with offset 50)
        WireProtocol.WriteSnapshotFragment(bytes, sequence: 1, serverTick: 1, fragmentIndex: 0, fragmentCount: 2, totalPayloadSize: 200, fragmentOffset: 50, new byte[100]);
        Assert.False(assembler.TryAddFragment(bytes, out _));

        // Total payload size smaller than snapshot header
        WireProtocol.WriteSnapshotFragment(bytes, sequence: 1, serverTick: 1, fragmentIndex: 0, fragmentCount: 1, totalPayloadSize: 5, fragmentOffset: 0, new byte[5]);
        Assert.False(assembler.TryAddFragment(bytes, out _));

        // Older sequence than current assembler sequence is rejected
        byte[] frag1 = new byte[WireProtocol.FragmentHeaderSize + WireProtocol.MaxFragmentPayloadSize];
        WireProtocol.WriteSnapshotFragment(frag1, sequence: 10, serverTick: 1, fragmentIndex: 0, fragmentCount: 2, totalPayloadSize: WireProtocol.MaxFragmentPayloadSize + 50, fragmentOffset: 0, new byte[WireProtocol.MaxFragmentPayloadSize]);
        Assert.False(assembler.TryAddFragment(frag1, out _));

        byte[] oldFrag = new byte[WireProtocol.FragmentHeaderSize + 50];
        WireProtocol.WriteSnapshotFragment(oldFrag, sequence: 9, serverTick: 1, fragmentIndex: 0, fragmentCount: 1, totalPayloadSize: 50, fragmentOffset: 0, new byte[50]);
        Assert.False(assembler.TryAddFragment(oldFrag, out _));
    }

    [Fact]
    public void LocalSessionSupportsPlayerTwoWithIndependentOwnership()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        _ = world.CreateUnit(1, 0, new Vector2(10, 10), 5);
        EntityId p2Unit = world.CreateUnit(2, 0, new Vector2(50, 50), 5);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using LocalSession session = new(sim, player: 2);

        session.Advance(1d / Simulation.TickRate);
        Assert.Equal(1, session.Replica.OwnUnits.Length);
        Assert.Equal(p2Unit, session.Replica.OwnUnits[0].Id);

        Assert.True(session.Move(p2Unit, new Vector2(60, 60)));
        session.Advance(1d / Simulation.TickRate);
        Assert.True(session.LastCommandResult is { Queued: true });
    }

    [Fact]
    public void ClientSessionConsumesRejectionFromTerminalDataWhenTransportAlreadyDisconnected()
    {
        byte[] rejection = new byte[WireProtocol.HandshakeResponseSize];
        WireProtocol.WriteHandshakeResponse(rejection, accepted: false, assignedPlayer: 1, HandshakeReason.ContentMismatch);

        MockTerminalTransport transport = new(rejection);
        MatchSetup setup = new(1, 1, 1, "Ops");
        using ClientSession session = new(transport, setup);

        Assert.Equal(SessionConnectionState.Failed, session.State);
        Assert.Equal("Connection rejected: ContentMismatch", session.StatusDescription);
    }

    [Fact]
    public void ClientSessionConsumesRejectionWhenTerminalDataArrivesAfterDisconnectFlag()
    {
        DeferredTerminalTransport transport = new();
        MatchSetup setup = new(1, 1, 1, "Ops");
        using ClientSession session = new(transport, setup);

        // Initially connected, transport drops connection before disconnect callback / reason arrives
        transport.Connected = false;
        session.Advance(0.05);

        // Must still be waiting for terminal event, not prematurely failed or disposed
        Assert.Equal(SessionConnectionState.HandshakePending, session.State);
        Assert.False(transport.Disposed);

        // Deliver delayed terminal rejection data
        transport.DeliverReason(HandshakeReason.ContentMismatch);
        session.Advance(0.05);

        Assert.Equal(SessionConnectionState.Failed, session.State);
        Assert.Equal("Connection rejected: ContentMismatch", session.StatusDescription);
        Assert.True(transport.Disposed);
    }

    [Fact]
    public void SimulationHostRejectsCommandsFromUnadmittedConnectionGeneration()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        _ = world.CreateUnit(1, 0, new Vector2(10, 10), 5);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 2);
        using ITransport client1 = transport.CreateClientTransport(1);
        using SimulationHost host = new(sim, transport);

        // Client 1 is connected, but has not completed handshake yet
        byte[] moveBytes = new byte[WireProtocol.MoveSize];
        WireProtocol.WriteMove(moveBytes, new MoveCommand(1, 0, new EntityId(1), new Vector2(20, 20)));
        Assert.True(client1.TrySend(moveBytes));

        // Advance host: command must be rejected because connection 1 is not admitted
        host.Advance(1d / Simulation.TickRate);
        Assert.False(client1.TryReceive(new byte[16], out _));
    }

    [Fact]
    public void SimulationHostRejectsCommandsWhenSlotReplacedDuringReceiveWindow()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        _ = world.CreateUnit(1, 0, new Vector2(10, 10), 5);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport inner = new(maxPlayers: 1);
        using ITransport client1 = inner.CreateClientTransport(1);

        InterceptingServerTransport transport = new(inner);
        using SimulationHost host = new(sim, transport);

        // Complete handshake for generation 1
        byte[] hsReq = new byte[WireProtocol.MaxMessageSize];
        int hsLen = WireProtocol.WriteHandshakeRequest(hsReq, new MatchSetup(1, 1, 1, "Movement"));
        Assert.True(client1.TrySend(hsReq.AsSpan(0, hsLen)));
        host.Advance(1d / Simulation.TickRate);
        Assert.True(client1.TryReceive(new byte[64], out _));

        // When TryReceive is called next, replace the slot connection ID with generation 999
        // and inject a move command from that unadmitted generation
        byte[] moveBytes = new byte[WireProtocol.MoveSize];
        WireProtocol.WriteMove(moveBytes, new MoveCommand(1, host.Tick, new EntityId(1), new Vector2(30, 30)));

        transport.OnNextReceive = () =>
        {
            transport.OverriddenConnectionId = 999;
            transport.InjectedPacket = moveBytes;
        };

        host.Advance(1d / Simulation.TickRate);

        // Command from unadmitted generation 999 must not affect unit position
        Assert.True(world.TryGetUnit(new EntityId(1), out UnitState unit));
        Assert.Equal(new Vector2(10, 10), unit.Position);
    }

    [Fact]
    public void UnauthenticatedConnection_TimesOutAndFreesSlot_AllowingSubsequentClientToConnect()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 1);
        using ITransport client1 = transport.CreateClientTransport(1);
        using SimulationHost host = new(sim, transport);

        // Advance past default handshake timeout (5.0s, say 6.0s in 120 ticks) without sending handshake
        for (int i = 0; i < 120; i++)
        {
            host.Advance(0.05);
        }

        // Slot 1 must be freed and client 1 disconnected
        Assert.False(transport.IsPlayerConnected(1));
        Assert.Equal(0, transport.ConnectedPlayerCount);

        // Subsequent client connects and completes handshake
        using ITransport client2 = transport.CreateClientTransport(1);
        MatchSetup setup = new(1, 1, 1, "Movement");
        byte[] hsReq = new byte[WireProtocol.MaxMessageSize];
        int hsLen = WireProtocol.WriteHandshakeRequest(hsReq, setup);
        Assert.True(client2.TrySend(hsReq.AsSpan(0, hsLen)));

        host.Advance(0.05);

        byte[] hsResp = new byte[64];
        Assert.True(client2.TryReceive(hsResp, out int respLen));
        Assert.True(WireProtocol.TryReadHandshakeResponse(hsResp.AsSpan(0, respLen), out bool accepted, out int assignedPlayer, out HandshakeReason reason));
        Assert.True(accepted);
        Assert.Equal(1, assignedPlayer);
        Assert.Equal(HandshakeReason.Accepted, reason);
    }

    [Fact]
    public void AuthenticatedConnection_WithinTimeout_SucceedsAndDoesNotTimeOut()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 1);
        using ITransport client = transport.CreateClientTransport(1);
        using SimulationHost host = new(sim, transport);

        MatchSetup setup = new(1, 1, 1, "Movement");
        byte[] hsReq = new byte[WireProtocol.MaxMessageSize];
        int hsLen = WireProtocol.WriteHandshakeRequest(hsReq, setup);
        Assert.True(client.TrySend(hsReq.AsSpan(0, hsLen)));

        host.Advance(0.05);

        byte[] hsResp = new byte[64];
        Assert.True(client.TryReceive(hsResp, out int respLen));
        Assert.True(WireProtocol.TryReadHandshakeResponse(hsResp.AsSpan(0, respLen), out bool accepted, out int assignedPlayer, out HandshakeReason reason));
        Assert.True(accepted);
        Assert.Equal(1, assignedPlayer);

        // Run past default timeout (6.0s in 120 ticks)
        for (int i = 0; i < 120; i++)
        {
            host.Advance(0.05);
        }

        // Client must remain connected
        Assert.True(transport.IsPlayerConnected(1));
        Assert.Equal(1, transport.ConnectedPlayerCount);
    }

    [Fact]
    public void CustomHandshakeTimeout_TimesOutAtConfiguredThreshold()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 1);
        using ITransport client = transport.CreateClientTransport(1);
        using SimulationHost host = new(sim, transport, handshakeTimeoutSeconds: 1.0);

        // Advance 0.8s (16 ticks at 0.05s) - still connected
        for (int i = 0; i < 16; i++)
        {
            host.Advance(0.05);
        }
        Assert.True(transport.IsPlayerConnected(1));

        // Advance another 0.3s (6 ticks, total 1.1s > 1.0s) - should time out
        for (int i = 0; i < 6; i++)
        {
            host.Advance(0.05);
        }
        Assert.False(transport.IsPlayerConnected(1));
        Assert.Equal(0, transport.ConnectedPlayerCount);
    }

    [Fact]
    public void ReservedBotSlot_RejectsClientHandshakeWithServerFull_AndPreservesBotAdmitted()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 2);
        MatchSetup setup = new(1, 1, 1, "Movement");
        using SimulationHost host = new(sim, transport, setup, reservedBotPlayer: 2);

        // Verify bot is admitted in simulation initially
        Assert.True(sim.IsPlayerAdmitted(2));

        // Client connects to player 2 slot and sends handshake request
        using ITransport client2 = transport.CreateClientTransport(2);
        byte[] hsReq = new byte[WireProtocol.MaxMessageSize];
        int hsLen = WireProtocol.WriteHandshakeRequest(hsReq, setup);
        Assert.True(client2.TrySend(hsReq.AsSpan(0, hsLen)));

        host.Advance(0.05);

        // Slot 2 must be disconnected, bot must still be admitted in simulation
        Assert.False(transport.IsPlayerConnected(2));
        Assert.True(sim.IsPlayerAdmitted(2));
        Assert.NotNull(client2.TerminalData);
        Assert.True(WireProtocol.TryReadHandshakeResponse(client2.TerminalData, out bool accepted, out int assignedPlayer, out HandshakeReason reason));
        Assert.False(accepted);
        Assert.Equal(2, assignedPlayer);
        Assert.Equal(HandshakeReason.ServerFull, reason);
    }

    [Fact]
    public void LateHandshake_DeliveredAtOrAfterDeadline_IsRejectedWithTimeout()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 1);
        using ITransport client = transport.CreateClientTransport(1);
        using SimulationHost host = new(sim, transport, handshakeTimeoutSeconds: 1.0);

        // Register the connection at session time 0.0
        host.Advance(0);
        Assert.True(transport.IsPlayerConnected(1));

        // Advance 0.99s without handshake
        host.Advance(0.99);
        Assert.True(transport.IsPlayerConnected(1));
        Assert.False(sim.IsPlayerAdmitted(1));

        // Deliver a valid handshake during the next advance that crosses the 1.0s deadline
        MatchSetup setup = new(1, 1, 1, "Movement");
        byte[] hsReq = new byte[WireProtocol.MaxMessageSize];
        int hsLen = WireProtocol.WriteHandshakeRequest(hsReq, setup);
        Assert.True(client.TrySend(hsReq.AsSpan(0, hsLen)));

        // Advance 0.11s (session time 1.10s > 1.0s timeout)
        host.Advance(0.11);

        // Client must be rejected with Timeout, not admitted
        Assert.False(sim.IsPlayerAdmitted(1));
        Assert.False(transport.IsPlayerConnected(1));
        Assert.NotNull(client.TerminalData);
        Assert.True(WireProtocol.TryReadHandshakeResponse(client.TerminalData, out bool accepted, out int assignedPlayer, out HandshakeReason reason));
        Assert.False(accepted);
        Assert.Equal(HandshakeReason.Timeout, reason);
    }

    [Fact]
    public void Handshake_DeliveredJustBeforeDeadline_SucceedsAndRemainsAdmitted()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 1);
        using ITransport client = transport.CreateClientTransport(1);
        using SimulationHost host = new(sim, transport, handshakeTimeoutSeconds: 1.0);

        host.Advance(0);
        host.Advance(0.90);
        Assert.True(transport.IsPlayerConnected(1));
        Assert.False(sim.IsPlayerAdmitted(1));

        // Deliver handshake at session time 0.90s + 0.05s = 0.95s (< 1.0s)
        MatchSetup setup = new(1, 1, 1, "Movement");
        byte[] hsReq = new byte[WireProtocol.MaxMessageSize];
        int hsLen = WireProtocol.WriteHandshakeRequest(hsReq, setup);
        Assert.True(client.TrySend(hsReq.AsSpan(0, hsLen)));

        host.Advance(0.05);

        // Client must be admitted successfully
        Assert.True(sim.IsPlayerAdmitted(1));
        Assert.True(transport.IsPlayerConnected(1));

        // Further advances well past 1.0s must keep client admitted
        for (int i = 0; i < 40; i++)
        {
            host.Advance(0.05);
        }
        Assert.True(sim.IsPlayerAdmitted(1));
        Assert.True(transport.IsPlayerConnected(1));
    }

    [Fact]
    public void RepeatedHandshakes_DoNotExtendOriginalDeadline()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 1);
        using ITransport client = transport.CreateClientTransport(1);
        MatchSetup serverSetup = new(1, 1, 1, "Movement");
        using SimulationHost host = new(sim, transport, serverSetup, handshakeTimeoutSeconds: 1.0);

        host.Advance(0);

        // Send a mismatched handshake at 0.5s that fails validation
        host.Advance(0.50);
        MatchSetup badSetup = new((byte)255, 1, 1, "Movement");
        byte[] badReq = new byte[WireProtocol.MaxMessageSize];
        int badLen = WireProtocol.WriteHandshakeRequest(badReq, badSetup);
        Assert.True(client.TrySend(badReq.AsSpan(0, badLen)));

        // Advance 0.05s: rejection occurs for protocol mismatch
        host.Advance(0.05);
        Assert.False(transport.IsPlayerConnected(1));
        Assert.False(sim.IsPlayerAdmitted(1));
    }

    [Fact]
    public void ConnectionIdReplacement_ReceivesFreshFullTimeoutWindow()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 1);
        using SimulationHost host = new(sim, transport, handshakeTimeoutSeconds: 1.0);

        // Client 1 connects and times out after 1.1s
        using (ITransport client1 = transport.CreateClientTransport(1))
        {
            host.Advance(0);
            host.Advance(1.10);
            Assert.False(transport.IsPlayerConnected(1));
        }

        // Advance 0.5s with empty slot (session time is now 1.6s)
        host.Advance(0.50);

        // Client 2 connects to the freed slot (new connection id)
        using ITransport client2 = transport.CreateClientTransport(1);
        host.Advance(0.05); // session time 1.65s, client 2 connected at 1.65s
        Assert.True(transport.IsPlayerConnected(1));

        // Advance 0.8s without handshake (session time 2.45s, 0.8s into client 2 window)
        host.Advance(0.80);
        Assert.True(transport.IsPlayerConnected(1));
        Assert.False(sim.IsPlayerAdmitted(1));

        // Client 2 completes handshake at 0.85s into its own window (< 1.0s window)
        MatchSetup setup = new(1, 1, 1, "Movement");
        byte[] hsReq = new byte[WireProtocol.MaxMessageSize];
        int hsLen = WireProtocol.WriteHandshakeRequest(hsReq, setup);
        Assert.True(client2.TrySend(hsReq.AsSpan(0, hsLen)));

        host.Advance(0.05);

        Assert.True(sim.IsPlayerAdmitted(1));
        Assert.True(transport.IsPlayerConnected(1));
    }

    [Fact]
    public void SilentPeerOnReservedBotSlot_IsImmediatelyDisconnectedWithServerFull_AndPreservesBot()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 2);
        MatchSetup setup = new(1, 1, 1, "Movement");
        using SimulationHost host = new(sim, transport, setup, reservedBotPlayer: 2, handshakeTimeoutSeconds: 1.0);

        Assert.True(sim.IsPlayerAdmitted(2));

        // Silent peer connects to slot 2 and sends nothing
        using ITransport client2 = transport.CreateClientTransport(2);

        // Advance 0: connection detected and immediately rejected with ServerFull
        host.Advance(0);

        Assert.False(transport.IsPlayerConnected(2));
        Assert.True(sim.IsPlayerAdmitted(2));
        Assert.NotNull(client2.TerminalData);
        Assert.True(WireProtocol.TryReadHandshakeResponse(client2.TerminalData, out bool accepted, out int assignedPlayer, out HandshakeReason reason));
        Assert.False(accepted);
        Assert.Equal(2, assignedPlayer);
        Assert.Equal(HandshakeReason.ServerFull, reason);

        // Advance 10 seconds: bot remains admitted and slot remains free
        for (int i = 0; i < 200; i++)
        {
            host.Advance(0.05);
        }
        Assert.False(transport.IsPlayerConnected(2));
        Assert.True(sim.IsPlayerAdmitted(2));
    }

    [Fact]
    public void PeerOnReservedBotSlot_SendingNonHandshakePackets_IsDisconnectedWithServerFull_AndPreservesBot()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using InMemoryServerTransport transport = new(maxPlayers: 2);
        MatchSetup setup = new(1, 1, 1, "Movement");
        using SimulationHost host = new(sim, transport, setup, reservedBotPlayer: 2);

        Assert.True(sim.IsPlayerAdmitted(2));

        // Peer connects to slot 2 and sends a Move command instead of handshake
        using ITransport client2 = transport.CreateClientTransport(2);
        byte[] movePacket = new byte[WireProtocol.MoveSize];
        WireProtocol.WriteMove(movePacket, new MoveCommand(1, 0, new EntityId(1), new Vector2(10, 10)));
        Assert.True(client2.TrySend(movePacket));

        host.Advance(0.05);

        Assert.False(transport.IsPlayerConnected(2));
        Assert.True(sim.IsPlayerAdmitted(2));
        Assert.NotNull(client2.TerminalData);
        Assert.True(WireProtocol.TryReadHandshakeResponse(client2.TerminalData, out bool accepted, out int assignedPlayer, out HandshakeReason reason));
        Assert.False(accepted);
        Assert.Equal(2, assignedPlayer);
        Assert.Equal(HandshakeReason.ServerFull, reason);
    }

    [Fact]
    public void RealUdp_HandshakeTimeout_FreesSlot_AndDeliversTimeoutTerminalReason()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 16);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        MatchSetup setup = new(1, 1, 1, "Movement");
        using SimulationHost host = new(sim, server, setup, handshakeTimeoutSeconds: 0.3);

        // Connect raw UDP client without completing handshake
        using LiteNetLibClientTransport client1 = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        // Advance until client is connected on transport level
        byte[] rx = new byte[64];
        for (int i = 0; i < 40 && !client1.IsConnected; i++)
        {
            server.Poll();
            client1.TryReceive(rx, out _);
            Thread.Sleep(5);
        }
        Assert.True(client1.IsConnected);

        // Host registers the unadmitted connection
        host.Advance(0.05);
        Assert.Equal(1, host.ConnectedPlayerCount);

        // Advance host past 0.3s timeout
        for (int i = 0; i < 20; i++)
        {
            host.Advance(0.05);
            client1.TryReceive(rx, out _);
            Thread.Sleep(5);
        }

        // Slot 1 must be freed and client1 disconnected
        Assert.Equal(0, host.ConnectedPlayerCount);
        Assert.False(client1.IsConnected);
        Assert.NotNull(client1.TerminalData);
        Assert.True(WireProtocol.TryReadHandshakeResponse(client1.TerminalData, out bool accepted, out int assignedPlayer, out HandshakeReason reason));
        Assert.False(accepted);
        Assert.Equal(HandshakeReason.Timeout, reason);

        // Replacement client connects and succeeds
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", server.Port, setup);
        for (int i = 0; i < 50 && !client2.IsConnected; i++)
        {
            host.Advance(0.05);
            client2.Advance(0.05);
            Thread.Sleep(5);
        }

        Assert.True(client2.IsConnected);
        Assert.Equal(1, client2.AssignedPlayerId);
    }

    [Fact]
    public void ClientSession_DisconnectWithTimeoutReason_TransitionsToFailed()
    {
        DeferredTerminalTransport transport = new();
        MatchSetup setup = new(1, 1, 1, "Movement");
        using ClientSession client = new(transport, setup);

        client.Advance(0.05);
        Assert.Equal(SessionConnectionState.HandshakePending, client.State);

        transport.DeliverReason(HandshakeReason.Timeout);
        transport.Connected = false;

        client.Advance(0.05);
        Assert.Equal(SessionConnectionState.Failed, client.State);
        Assert.Contains("Timeout", client.StatusDescription);
    }

    private sealed class DeferredTerminalTransport : ITransport
    {
        public bool Connected = true;
        public bool Disposed;
        public byte[]? TerminalData { get; private set; }
        public bool IsConnected => Connected;
        public bool HasTerminalEvent => TerminalData is not null;
        public bool TrySend(ReadOnlySpan<byte> message) => Connected && !Disposed;
        public bool TryReceive(Span<byte> destination, out int length) { length = 0; return false; }
        public void Dispose() => Disposed = true;
        public void DeliverReason(HandshakeReason reason)
        {
            TerminalData = new byte[WireProtocol.HandshakeResponseSize];
            WireProtocol.WriteHandshakeResponse(TerminalData, false, 1, reason);
        }
    }

    private sealed class InterceptingServerTransport(IServerTransport inner) : IServerTransport
    {
        public Action? OnNextReceive;
        public long OverriddenConnectionId;
        public byte[]? InjectedPacket;

        public int InitialAdmittedPlayer => inner.InitialAdmittedPlayer;
        public int ConnectedPlayerCount => inner.ConnectedPlayerCount;
        public void Poll() => inner.Poll();
        public bool TrySend(int player, ReadOnlySpan<byte> message, bool reliable = true) => inner.TrySend(player, message, reliable);
        public bool TryReceive(out int player, out long connectionId, Span<byte> destination, out int length)
        {
            if (OnNextReceive is not null)
            {
                Action act = OnNextReceive;
                OnNextReceive = null;
                act();
                if (InjectedPacket is not null)
                {
                    player = 1;
                    connectionId = OverriddenConnectionId;
                    InjectedPacket.CopyTo(destination);
                    length = InjectedPacket.Length;
                    InjectedPacket = null;
                    return true;
                }
            }
            return inner.TryReceive(out player, out connectionId, destination, out length);
        }
        public bool IsPlayerConnected(int player) => inner.IsPlayerConnected(player);
        public long GetPlayerConnectionId(int player) => inner.GetPlayerConnectionId(player);
        public void DisconnectPlayer(int player, ReadOnlySpan<byte> reason = default) => inner.DisconnectPlayer(player, reason);
        public void Dispose() => inner.Dispose();
    }

    private sealed class MockTerminalTransport : ITransport
    {
        private readonly byte[] _terminal;
        public bool IsConnected => false;
        public byte[]? TerminalData => _terminal;
        public MockTerminalTransport(byte[] terminal) => _terminal = terminal;
        public bool TrySend(ReadOnlySpan<byte> message) => false;
        public bool TryReceive(Span<byte> destination, out int length) { length = 0; return false; }
        public void Dispose() { }
    }

    [Fact]
    public void ReceiveBufferPool_AcquiresReleasesAndRejectsOversized()
    {
        ReceiveBufferPool pool = new(standardCount: 2, largeCount: 1, standardSize: 100, largeSize: 500);

        Assert.Equal(2, pool.StandardCapacity);
        Assert.Equal(1, pool.LargeCapacity);
        Assert.Equal(2, pool.StandardAvailable);
        Assert.Equal(1, pool.LargeAvailable);

        Assert.True(pool.TryAcquire(50, out ReceiveBuffer? b1));
        Assert.NotNull(b1);
        Assert.False(b1.IsLarge);
        Assert.Equal(1, pool.StandardAvailable);

        Assert.True(pool.TryAcquire(100, out ReceiveBuffer? b2));
        Assert.NotNull(b2);
        Assert.False(b2.IsLarge);
        Assert.Equal(0, pool.StandardAvailable);

        // Standard request borrows large buffer when standard is exhausted
        Assert.True(pool.TryAcquire(80, out ReceiveBuffer? b3));
        Assert.NotNull(b3);
        Assert.True(b3.IsLarge);
        Assert.Equal(0, pool.LargeAvailable);

        // Pool is now exhausted
        Assert.False(pool.TryAcquire(50, out ReceiveBuffer? b4));
        Assert.Null(b4);

        // Invalid lengths rejected
        Assert.False(pool.TryAcquire(0, out _));
        Assert.False(pool.TryAcquire(-1, out _));
        Assert.False(pool.TryAcquire(501, out _));

        // Return buffers to pool
        pool.Return(b1);
        Assert.Equal(1, pool.StandardAvailable);

        // Double return guard prevents extra count
        pool.Return(b1);
        Assert.Equal(1, pool.StandardAvailable);

        pool.Return(b2);
        Assert.Equal(2, pool.StandardAvailable);

        pool.Return(b3);
        Assert.Equal(1, pool.LargeAvailable);
    }

    [Fact]
    public void ReceiveBufferQueue_FifoOrderingAndPayloadIntegrity()
    {
        ReceiveBufferQueue queue = new(maxPackets: 10, maxBytes: 10000);

        byte[] payload1 = [0x01, 0x02, 0x03, 0x04];
        byte[] payload2 = [0x10, 0x20, 0x30, 0x40, 0x50];
        byte[] payload3 = [0xAA, 0xBB, 0xCC];

        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(payload1, 1, 101, isReliable: true));
        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(payload2, 2, 102, isReliable: false));
        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(payload3, 1, 101, isReliable: true));

        Assert.Equal(3, queue.QueuedPacketCount);
        Assert.Equal(12, queue.QueuedPayloadBytes);
        Assert.Equal(3, queue.PeakQueuedPackets);
        Assert.Equal(12, queue.PeakQueuedPayloadBytes);

        Assert.True(queue.TryDequeue(out ReceiveBuffer? d1));
        Assert.NotNull(d1);
        Assert.Equal(1, d1.Player);
        Assert.Equal(101, d1.ConnectionId);
        Assert.True(d1.IsReliable);
        Assert.Equal(4, d1.Length);
        Assert.True(payload1.AsSpan().SequenceEqual(d1.Data.AsSpan(0, d1.Length)));
        queue.Release(d1);

        Assert.Equal(2, queue.QueuedPacketCount);
        Assert.Equal(8, queue.QueuedPayloadBytes);
        Assert.Equal(3, queue.PeakQueuedPackets);

        Assert.True(queue.TryDequeue(out ReceiveBuffer? d2));
        Assert.NotNull(d2);
        Assert.Equal(2, d2.Player);
        Assert.Equal(102, d2.ConnectionId);
        Assert.False(d2.IsReliable);
        Assert.Equal(5, d2.Length);
        Assert.True(payload2.AsSpan().SequenceEqual(d2.Data.AsSpan(0, d2.Length)));
        queue.Release(d2);

        Assert.True(queue.TryDequeue(out ReceiveBuffer? d3));
        Assert.NotNull(d3);
        Assert.Equal(1, d3.Player);
        Assert.Equal(101, d3.ConnectionId);
        Assert.True(d3.IsReliable);
        Assert.Equal(3, d3.Length);
        Assert.True(payload3.AsSpan().SequenceEqual(d3.Data.AsSpan(0, d3.Length)));
        queue.Release(d3);

        Assert.Equal(0, queue.QueuedPacketCount);
        Assert.Equal(0, queue.QueuedPayloadBytes);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void ReceiveBufferQueue_OverloadPolicy_PacketCountLimit()
    {
        ReceiveBufferQueue queue = new(maxPackets: 3, maxBytes: 10000);
        byte[] payload = [0x01, 0x02, 0x03, 0x04];

        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(payload, 1, 1, false));
        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(payload, 1, 1, false));
        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(payload, 1, 1, false));
        Assert.Equal(3, queue.QueuedPacketCount);

        Assert.Equal(ReceiveBufferQueue.EnqueueResult.DroppedUnreliable, queue.TryEnqueue(payload, 1, 1, false));
        Assert.Equal(1, queue.DroppedUnreliablePackets);
        Assert.Equal(4, queue.DroppedUnreliableBytes);
        Assert.Equal(0, queue.ReliableOverflowFailures);

        Assert.Equal(ReceiveBufferQueue.EnqueueResult.ReliableOverflow, queue.TryEnqueue(payload, 1, 1, true));
        Assert.Equal(1, queue.DroppedUnreliablePackets);
        Assert.Equal(1, queue.ReliableOverflowFailures);

        Assert.True(queue.TryDequeue(out ReceiveBuffer? b));
        Assert.NotNull(b);
        queue.Release(b);
        Assert.Equal(2, queue.QueuedPacketCount);

        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(payload, 1, 1, true));
        Assert.Equal(3, queue.QueuedPacketCount);
    }

    [Fact]
    public void ReceiveBufferQueue_OverloadPolicy_ByteLimit()
    {
        ReceiveBufferQueue queue = new(maxPackets: 100, maxBytes: 20);
        byte[] eightBytes = [1, 2, 3, 4, 5, 6, 7, 8];

        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(eightBytes, 1, 1, false));
        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(eightBytes, 1, 1, false));
        Assert.Equal(16, queue.QueuedPayloadBytes);

        Assert.Equal(ReceiveBufferQueue.EnqueueResult.DroppedUnreliable, queue.TryEnqueue(eightBytes, 1, 1, false));
        Assert.Equal(1, queue.DroppedUnreliablePackets);
        Assert.Equal(8, queue.DroppedUnreliableBytes);

        Assert.Equal(ReceiveBufferQueue.EnqueueResult.ReliableOverflow, queue.TryEnqueue(eightBytes, 1, 1, true));
        Assert.Equal(1, queue.ReliableOverflowFailures);

        Assert.True(queue.TryDequeue(out ReceiveBuffer? b));
        Assert.NotNull(b);
        queue.Release(b);
        Assert.Equal(8, queue.QueuedPayloadBytes);

        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(eightBytes, 1, 1, true));
        Assert.Equal(16, queue.QueuedPayloadBytes);
    }

    [Fact]
    public void ReceiveBufferQueue_Clear_ReleasesAllBuffersToPool()
    {
        ReceiveBufferPool pool = new(standardCount: 4, largeCount: 1, standardSize: 100, largeSize: 500);
        ReceiveBufferQueue queue = new(maxPackets: 4, maxBytes: 1000, pool: pool);

        byte[] payload = [1, 2, 3, 4];
        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(payload, 1, 1, false));
        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(payload, 1, 1, false));
        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue.TryEnqueue(payload, 1, 1, false));

        Assert.Equal(3, queue.QueuedPacketCount);
        Assert.Equal(1, pool.StandardAvailable);

        queue.Clear();

        Assert.Equal(0, queue.QueuedPacketCount);
        Assert.Equal(0, queue.QueuedPayloadBytes);
        Assert.Equal(4, pool.StandardAvailable);
    }

    [Fact]
    public void SimulationHost_Advance_BoundsMessagesProcessedPerCall()
    {
        HeightMap map = HeightMap.CreatePrototype();
        FloodServerTransport flood = new();
        using SimulationHost host = new(new Simulation(new WorldState(map), new RuleSet()), flood);

        host.Advance(0);
        Assert.Equal(SimulationHost.MaxMessagesPerAdvance, flood.ReceivedCount);

        host.Advance(0);
        Assert.Equal(SimulationHost.MaxMessagesPerAdvance * 2, flood.ReceivedCount);
    }

    [Fact]
    public void ClientSession_Poll_BoundsMessagesProcessedPerCall()
    {
        FloodClientTransport flood = new();
        using ClientSession client = new(flood);

        Assert.Equal(ClientSession.MaxMessagesPerPoll, flood.ReceivedCount);

        client.Advance(0);
        Assert.Equal(ClientSession.MaxMessagesPerPoll * 2, flood.ReceivedCount);
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs = 3000, string message = "Timed out", Action? pump = null)
    {
        long deadline = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(timeoutMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        while (!condition())
        {
            pump?.Invoke();
            if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException(message);
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void ServerTransport_StaleConnectionMessage_DiscardedOnSlotReuse()
    {
        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        ReceiveBufferQueue queue1 = server.GetPlayerQueue(1);

        byte[] payloadStale = [WireProtocol.Version, WireProtocol.MoveKind, 1, 2, 3, 4];
        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue1.TryEnqueue(payloadStale, 1, connectionId: 999, isReliable: true));
        Assert.Equal(1, queue1.QueuedPacketCount);

        // TryReceive must detect stale connectionId (slot 1 currently has connectionId 0) and discard it safely
        Span<byte> rx = stackalloc byte[WireProtocol.MaxMessageSize];
        Assert.False(server.TryReceive(out _, out _, rx, out _));
        Assert.Equal(0, queue1.QueuedPacketCount);

        // Connect a real client to slot 1
        using LiteNetLibClientTransport client = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);
        WaitUntil(() => client.IsConnected && server.ConnectedPlayerCount > 0, 3000, "Client did not connect", () =>
        {
            server.Poll();
            client.Poll();
        });
        Assert.True(client.IsConnected);
        Assert.Equal(1, server.ConnectedPlayerCount);
        long activeConnId = server.GetPlayerConnectionId(1);
        Assert.True(activeConnId > 0);

        // Enqueue another stale message with old connectionId (activeConnId - 1)
        Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, queue1.TryEnqueue(payloadStale, 1, connectionId: activeConnId - 1, isReliable: true));
        Assert.Equal(1, queue1.QueuedPacketCount);

        // TryReceive must discard the stale message because connectionId does not match activeConnId
        Assert.False(server.TryReceive(out _, out _, rx, out _));
        Assert.Equal(0, queue1.QueuedPacketCount);

        // Now send a valid message from the connected client
        byte[] validMove = new byte[WireProtocol.MoveSize];
        WireProtocol.WriteMove(validMove, new MoveCommand(1, 1, new EntityId(1), new Vector2(5, 5)));
        client.TrySend(validMove);

        WaitUntil(() => server.GetPlayerQueuedPacketCount(1) > 0, 3000, "Server did not receive message", () =>
        {
            server.Poll();
            client.Poll();
        });

        Assert.True(server.TryReceive(out int p, out long connId, rx, out int len));
        Assert.Equal(1, p);
        Assert.Equal(activeConnId, connId);
        Assert.Equal(WireProtocol.MoveSize, len);
    }

    [Fact]
    public void ServerTransport_FairRoundRobin_ServicingAcrossPlayers()
    {
        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        using LiteNetLibClientTransport client1 = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);
        using LiteNetLibClientTransport client2 = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        WaitUntil(() => client1.IsConnected && client2.IsConnected && server.ConnectedPlayerCount == 2, 3000, "Both clients did not connect", () =>
        {
            server.Poll();
            client1.Poll();
            client2.Poll();
        });
        Assert.True(client1.IsConnected);
        Assert.True(client2.IsConnected);
        Assert.Equal(2, server.ConnectedPlayerCount);

        ReceiveBufferQueue q1 = server.GetPlayerQueue(1);
        ReceiveBufferQueue q2 = server.GetPlayerQueue(2);
        long conn1 = server.GetPlayerConnectionId(1);
        long conn2 = server.GetPlayerConnectionId(2);

        byte[] cmd1 = [WireProtocol.Version, WireProtocol.MoveKind, 1, 0, 0, 0];
        byte[] cmd2 = [WireProtocol.Version, WireProtocol.MoveKind, 2, 0, 0, 0];

        // Populate both player queues directly to test fair round-robin scheduling deterministically
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, q1.TryEnqueue(cmd1, 1, conn1, isReliable: true));
            Assert.Equal(ReceiveBufferQueue.EnqueueResult.Enqueued, q2.TryEnqueue(cmd2, 2, conn2, isReliable: true));
        }

        List<int> servicedOrder = [];
        Span<byte> rx = stackalloc byte[WireProtocol.MaxMessageSize];
        while (server.TryReceive(out int player, out _, rx, out _))
        {
            servicedOrder.Add(player);
        }

        Assert.Equal(10, servicedOrder.Count);
        int[] expectedOrder = [1, 2, 1, 2, 1, 2, 1, 2, 1, 2];
        Assert.Equal(expectedOrder, servicedOrder);
    }

    [Fact]
    public void ServerTransport_ReliableOverflow_DisconnectsPeer()
    {
        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 2, maxPacketsPerPeer: 3);
        using LiteNetLibClientTransport client = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        WaitUntil(() => client.IsConnected && server.ConnectedPlayerCount == 1, 3000, "Client did not connect", () =>
        {
            server.Poll();
            client.Poll();
        });
        Assert.True(client.IsConnected);
        Assert.Equal(1, server.ConnectedPlayerCount);

        byte[] commandBytes = new byte[WireProtocol.MoveSize];
        WireProtocol.WriteMove(commandBytes, new MoveCommand(1, 1, new EntityId(1), new Vector2(1, 1)));

        // Send 10 reliable packets exceeding capacity of 3
        for (int i = 0; i < 10; i++)
        {
            client.TrySend(commandBytes);
        }

        WaitUntil(() => server.ReliableOverflowDisconnects >= 1, 3000, "Reliable overflow disconnect not triggered", () =>
        {
            server.Poll();
            client.Poll();
        });

        Assert.True(server.ReliableOverflowFailures >= 1);
        Assert.Equal(0, server.ConnectedPlayerCount);
    }

    [Fact]
    public void ServerTransport_PendingNativeEvents_BoundedByPollBudget()
    {
        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        using LiteNetLibClientTransport client = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        WaitUntil(() => client.IsConnected && server.ConnectedPlayerCount > 0, 3000, "Client did not connect", () =>
        {
            server.Poll();
            client.Poll();
        });

        byte[] smallPayload = [WireProtocol.Version, WireProtocol.MoveKind, 1, 2, 3, 4];

        // Burst 1: client sends 200 unreliable packets while server does not poll
        for (int i = 0; i < 200; i++)
        {
            Assert.True(client.TrySend(smallPayload, reliable: false));
        }
        Thread.Sleep(50);

        // Server Poll with explicit budget 100
        server.Poll(100);
        int handled1 = server.QueuedPacketCount + (int)server.DroppedUnreliablePackets;

        // Native callback dispatch is strictly bounded by the poll budget (100)
        Assert.Equal(100, handled1);
        Assert.Equal(64, server.QueuedPacketCount);
        Assert.Equal(36L, server.DroppedUnreliablePackets);

        // Burst 2: send another 100 unreliable packets to verify remaining work on a subsequent poll
        for (int i = 0; i < 100; i++)
        {
            Assert.True(client.TrySend(smallPayload, reliable: false));
        }
        Thread.Sleep(50);

        server.Poll(50);
        int handled2 = server.QueuedPacketCount + (int)server.DroppedUnreliablePackets;

        // Second poll with budget 50 dispatches 50 additional native callbacks
        Assert.Equal(150, handled2);
        Assert.Equal(64, server.QueuedPacketCount);
        Assert.Equal(86L, server.DroppedUnreliablePackets);
    }

    [Fact]
    public void ClientTransport_PendingNativeEvents_BoundedByPollBudget()
    {
        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        using LiteNetLibClientTransport client = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port, maxPackets: 128);

        WaitUntil(() => client.IsConnected && server.ConnectedPlayerCount > 0, 3000, "Client did not connect", () =>
        {
            server.Poll();
            client.Poll();
        });

        byte[] smallPayload = [WireProtocol.Version, WireProtocol.MoveKind, 1, 2, 3, 4];

        // Burst 1: server sends 100 unreliable packets to client while client does not poll
        for (int i = 0; i < 100; i++)
        {
            Assert.True(server.TrySend(1, smallPayload, reliable: false));
        }
        Thread.Sleep(50);

        // First client poll with budget 50
        client.Poll(50);

        Assert.True(client.IsConnected);
        Assert.Equal(50, client.QueuedPacketCount);
        Assert.Equal(0L, client.DroppedUnreliablePackets);

        // Burst 2: server sends another 50 unreliable packets to client
        for (int i = 0; i < 50; i++)
        {
            Assert.True(server.TrySend(1, smallPayload, reliable: false));
        }
        Thread.Sleep(50);

        // Second client poll with budget 50 verifies subsequent progress
        client.Poll(50);
        Assert.Equal(100, client.QueuedPacketCount);
        Assert.Equal(0L, client.DroppedUnreliablePackets);

        // Burst 3: server sends 50 more unreliable packets, filling client queue (capacity 128) and dropping excess
        for (int i = 0; i < 50; i++)
        {
            Assert.True(server.TrySend(1, smallPayload, reliable: false));
        }
        Thread.Sleep(50);

        client.Poll(50);
        Assert.Equal(128, client.QueuedPacketCount);
        Assert.Equal(22L, client.DroppedUnreliablePackets);
    }

    [Fact]
    public void ClientTransport_DisconnectAndReliableOverflow_ClearsQueueAndStorage()
    {
        // 1. Reliable overflow on client immediately clears queue and storage
        using LiteNetLibServerTransport server1 = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        using LiteNetLibClientTransport client1 = LiteNetLibTransport.CreateClient("127.0.0.1", server1.Port, maxPackets: 2);

        WaitUntil(() => client1.IsConnected && server1.ConnectedPlayerCount > 0, 3000, "Client 1 did not connect", () =>
        {
            server1.Poll();
            client1.Poll();
        });

        byte[] payload = [WireProtocol.Version, WireProtocol.MoveKind, 1, 2, 3, 4];
        for (int i = 0; i < 5; i++)
        {
            server1.TrySend(1, payload, reliable: true);
        }

        WaitUntil(() => !client1.IsConnected, 3000, "Client 1 did not disconnect on overflow", () =>
        {
            server1.Poll();
            client1.Poll(10);
        });

        Assert.False(client1.IsConnected);
        Assert.True(client1.HasTerminalEvent);
        Assert.Equal(0, client1.QueuedPacketCount);
        Assert.Equal(0, client1.QueuedPayloadBytes);
        Assert.True(client1.ReliableOverflowFailures >= 1);
        Span<byte> rx = stackalloc byte[WireProtocol.MaxMessageSize];
        Assert.False(client1.TryReceive(rx, out _));

        // 2. Normal remote disconnect clears queue and delivers terminal response
        using LiteNetLibServerTransport server2 = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        using LiteNetLibClientTransport client2 = LiteNetLibTransport.CreateClient("127.0.0.1", server2.Port, maxPackets: 10);

        WaitUntil(() => client2.IsConnected && server2.ConnectedPlayerCount > 0, 3000, "Client 2 did not connect", () =>
        {
            server2.Poll();
            client2.Poll();
        });

        for (int i = 0; i < 3; i++)
        {
            server2.TrySend(1, payload, reliable: true);
        }

        WaitUntil(() => client2.QueuedPacketCount == 3, 3000, "Client 2 did not receive 3 packets", () =>
        {
            server2.Poll();
            client2.Poll(10);
        });

        byte[] reasonBuffer = new byte[WireProtocol.HandshakeResponseSize];
        WireProtocol.WriteHandshakeResponse(reasonBuffer, false, 1, HandshakeReason.Timeout);
        server2.DisconnectPlayer(1, reasonBuffer);

        WaitUntil(() => !client2.IsConnected && client2.TerminalData is not null, 3000, "Client 2 did not receive terminal reason", () =>
        {
            server2.Poll();
            client2.Poll(10);
        });

        Assert.False(client2.IsConnected);
        Assert.NotNull(client2.TerminalData);
        Assert.True(WireProtocol.TryReadHandshakeResponse(client2.TerminalData, out bool accepted, out _, out HandshakeReason reason));
        Assert.False(accepted);
        Assert.Equal(HandshakeReason.Timeout, reason);

        // Terminal response is readable once, old pre-disconnect packets are cleared
        Assert.True(client2.TryReceive(rx, out int termLen));
        Assert.Equal(WireProtocol.HandshakeResponseSize, termLen);
        Assert.False(client2.TryReceive(rx, out _));
        Assert.Equal(0, client2.QueuedPacketCount);
    }

    [Fact]
    public void ClientSession_DisconnectedHandshakeBacklog_BoundedPerPoll()
    {
        BacklogTransport transport = new(300);
        MatchSetup setup = new(1, 1, 1, "Movement");
        using ClientSession client = new(transport, setup);

        // Constructor Poll must be bounded by MaxMessagesPerPoll (128)
        Assert.Equal(ClientSession.MaxMessagesPerPoll, transport.ReceivedCount);

        // Next Advance processes the next 128
        client.Advance(0);
        Assert.Equal(ClientSession.MaxMessagesPerPoll * 2, transport.ReceivedCount);
    }

    [Fact]
    public void RealUdp_BurstAndSnapshotDelivery_WithZeroUnreliableDrops()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = new(1, 1, 1, "Movement");
        WorldState world = new(map);
        EntityId unitId = world.CreateUnit(1, 0, new Vector2(100, 100), 10f);
        Simulation sim = new(world, new RuleSet(new MovementSystem()));
        sim.SetPlayerAdmitted(1, true);

        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        using SimulationHost host = new(sim, server, setup);
        using ClientSession client = ClientSession.Connect("127.0.0.1", server.Port, setup);

        WaitUntil(() => client.IsConnected && client.Replica.OwnUnits.Length > 0, 3000, "Client did not connect or receive initial unit", () =>
        {
            host.Advance(0.05);
            client.Advance(0.05);
        });

        Assert.True(client.IsConnected);
        Assert.Equal(1, client.Replica.OwnUnits.Length);
        Vector2 startPos = client.Replica.OwnUnits[0].Position;

        for (uint seq = 1; seq <= 10; seq++)
        {
            client.Move(unitId, new Vector2(100 + seq * 10, 100));
        }

        int initialTick = client.Replica.LatestTick;
        WaitUntil(() => client.Replica.LatestTick > initialTick + 2 && client.Replica.OwnUnits[0].Position.X > startPos.X, 3000, "Client replica did not advance under move burst", () =>
        {
            host.Advance(0.05);
            client.Advance(0.05);
        });

        Assert.NotNull(client.LastCommandResult);
        Assert.True(client.BytesReceived > 0);
        Assert.Equal(0L, server.DroppedUnreliablePackets);
        Assert.Equal(0L, server.ReliableOverflowFailures);
    }

    [Fact]
    public void NetManager_BoundedPoll_DeterministicRegression_PreservesUnreadEventsAndOrdering()
    {
        EventBasedNetListener listener = new();
        List<int> processedLatencies = [];
        listener.NetworkLatencyUpdateEvent += (peer, latency) => processedLatencies.Add(latency);

        NetManager manager = new(listener);
        Assert.True(manager.Start());

        // Deterministically queue 5 events at the event queue boundary
        for (int i = 1; i <= 5; i++)
        {
            manager.CreateEvent(NetEvent.EType.ConnectionLatencyUpdated, latency: i);
        }
        Assert.Equal(5, manager.PendingEventsCount);

        // First poll: budget of 2
        manager.PollEvents(2);
        Assert.Equal([1, 2], processedLatencies);
        Assert.Equal(3, manager.PendingEventsCount);

        // A new event arrives while unread backlog exists
        manager.CreateEvent(NetEvent.EType.ConnectionLatencyUpdated, latency: 6);
        Assert.Equal(4, manager.PendingEventsCount);

        // Second poll: budget of 2. Must process preserved unread tail (3, 4) ahead of newly arrived event (6)
        manager.PollEvents(2);
        Assert.Equal([1, 2, 3, 4], processedLatencies);
        Assert.Equal(2, manager.PendingEventsCount);

        // Third poll: unlimited (0). Drains remaining 5 and 6
        manager.PollEvents(0);
        Assert.Equal([1, 2, 3, 4, 5, 6], processedLatencies);
        Assert.Equal(0, manager.PendingEventsCount);

        // Verify Stop clears pending events
        for (int i = 10; i <= 15; i++)
        {
            manager.CreateEvent(NetEvent.EType.ConnectionLatencyUpdated, latency: i);
        }
        Assert.Equal(6, manager.PendingEventsCount);
        manager.Stop();
        Assert.Equal(0, manager.PendingEventsCount);
        manager.PollEvents();
        Assert.Equal([1, 2, 3, 4, 5, 6], processedLatencies);
    }

    [Fact]
    public void Deterministic_BoundedPoll_LargeBacklog_BookkeepingIsBounded()
    {
        List<int> processedLatencies = [];
        EventBasedNetListener listener = new();
        listener.NetworkLatencyUpdateEvent += (peer, latency) =>
        {
            processedLatencies.Add(latency);
        };

        NetManager manager = new(listener);
        Assert.True(manager.Start());

        const int largeBacklogSize = 100_000;
        for (int i = 1; i <= largeBacklogSize; i++)
        {
            manager.CreateEvent(NetEvent.EType.ConnectionLatencyUpdated, latency: i);
        }
        Assert.Equal(largeBacklogSize, manager.PendingEventsCount);

        // Perform 10 bounded polls of budget 1
        const int pollCount = 10;
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < pollCount; i++)
        {
            manager.PollEvents(1);
        }
        sw.Stop();

        Assert.Equal(pollCount, processedLatencies.Count);
        for (int i = 0; i < pollCount; i++)
        {
            Assert.Equal(i + 1, processedLatencies[i]);
        }
        Assert.Equal(largeBacklogSize - pollCount, manager.PendingEventsCount);

        // Bookkeeping must not depend on unread backlog size: 10 PollEvents(1) calls on 100k queued events
        // must take negligible time (< 20ms total, far below the old O(N^2) scan of ~46ms).
        Assert.True(sw.ElapsedMilliseconds < 50, $"Bookkeeping took too long: {sw.ElapsedMilliseconds}ms for {pollCount} polls on backlog of {largeBacklogSize}");

        manager.Stop();
    }

    [Fact]
    public void Deterministic_StopInsideCallback_AbortsPollAndDiscardsPendingEvents()
    {
        int callbackCount = 0;
        NetManager? managerRef = null;
        EventBasedNetListener listener = new();
        listener.NetworkLatencyUpdateEvent += (peer, latency) =>
        {
            callbackCount++;
            if (latency == 1)
            {
                managerRef?.Stop();
            }
        };

        NetManager manager = new(listener);
        managerRef = manager;
        Assert.True(manager.Start());

        // Queue 3 latency events
        manager.CreateEvent(NetEvent.EType.ConnectionLatencyUpdated, latency: 1);
        manager.CreateEvent(NetEvent.EType.ConnectionLatencyUpdated, latency: 2);
        manager.CreateEvent(NetEvent.EType.ConnectionLatencyUpdated, latency: 3);
        Assert.Equal(3, manager.PendingEventsCount);

        // Poll 1 event. Callback fires, calls manager.Stop().
        manager.PollEvents(1);

        // NetManager must be stopped, unread events must not be resurrected
        Assert.False(manager.IsRunning);
        Assert.Equal(0, manager.PendingEventsCount);

        // Calling PollEvents again after Stop must NOT invoke further callbacks
        manager.PollEvents(1);
        Assert.Equal(1, callbackCount);

        // Restarting the manager must NOT resurrect old events
        Assert.True(manager.Start());
        Assert.Equal(0, manager.PendingEventsCount);
        manager.PollEvents(1);
        Assert.Equal(1, callbackCount);

        manager.Stop();
    }

    [Fact]
    public void Deterministic_StopInsideCallback_ReleasesAbandonedPacketReaders()
    {
        int receiveCallbacks = 0;
        NetManager? managerRef = null;
        EventBasedNetListener listener = new();
        listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            receiveCallbacks++;
            managerRef?.Stop();
        };

        NetManager manager = new(listener) { AutoRecycle = true };
        managerRef = manager;
        Assert.True(manager.Start());

        // Queue 3 receive events with packet buffers allocated from pool
        for (int i = 1; i <= 3; i++)
        {
            NetPacket packet = manager.PoolGetPacket(64);
            packet.Size = 10;
            manager.CreateEvent(NetEvent.EType.Receive, readerSource: packet);
        }
        Assert.Equal(3, manager.PendingEventsCount);
        Assert.Equal(3, manager.PendingReceiveCount);

        manager.PollEvents(1);

        Assert.False(manager.IsRunning);
        Assert.Equal(0, manager.PendingEventsCount);
        Assert.Equal(0, manager.PendingReceiveCount);
        Assert.Equal(1, receiveCallbacks);

        // Abandoned packets must have been recycled to the packet pool
        Assert.True(manager.PoolCount > 0);

        manager.Stop();
    }

    [Fact]
    public void RealUdp_BoundedPoll_ClientToServer_BurstDrainedOverMultipleLimitedPolls()
    {
        const int messageCount = 20;
        const int pollBudget = 3;

        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 1, maxPacketsPerPeer: 64);
        using LiteNetLibClientTransport client = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        WaitUntil(() => client.IsConnected && server.ConnectedPlayerCount > 0, 3000, "Client did not connect", () =>
        {
            server.Poll();
            client.Poll();
        });

        // Queue a burst larger than one poll budget
        for (int i = 0; i < messageCount; i++)
        {
            byte[] msg = [WireProtocol.Version, WireProtocol.MoveKind, (byte)i, (byte)(i >> 8)];
            Assert.True(client.TrySend(msg, reliable: true));
        }

        // Wait until all messages are received by server NetManager into pending events using receive-specific readiness
        WaitUntil(() => server.PendingReceiveCount >= messageCount, 3000, "Server did not receive burst into pending events", () =>
        {
            client.Poll(); // pump client so it flushes sends and processes ACKs
        });

        Assert.True(server.PendingReceiveCount >= messageCount);

        List<int> receivedIdentities = [];
        Span<byte> rx = stackalloc byte[WireProtocol.MaxMessageSize];

        // Drain over multiple limited polls without additional sends
        int pollCount = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while (receivedIdentities.Count < messageCount)
        {
            Assert.True(sw.ElapsedMilliseconds < 5000, "Drain loop timed out");
            server.Poll(pollBudget);

            int polledInThisBatch = 0;
            while (server.TryReceive(out _, out _, rx, out int len))
            {
                Assert.Equal(WireProtocol.Version, rx[0]);
                Assert.Equal(WireProtocol.MoveKind, rx[1]);
                int id = rx[2] | (rx[3] << 8);
                receivedIdentities.Add(id);
                polledInThisBatch++;
            }

            Assert.True(polledInThisBatch <= pollBudget, $"Poll budget exceeded: received {polledInThisBatch} > {pollBudget}");
            pollCount++;
            Assert.True(pollCount <= 20, "Too many polls to drain burst");
        }

        Assert.Equal(messageCount, receivedIdentities.Count);
        for (int i = 0; i < messageCount; i++)
        {
            Assert.Equal(i, receivedIdentities[i]);
        }
        Assert.Equal(0, server.PendingReceiveCount);
        Assert.Equal(0L, server.ReliableOverflowFailures);
    }

    [Fact]
    public void RealUdp_BoundedPoll_ServerToClient_BurstDrainedOverMultipleLimitedPolls()
    {
        const int messageCount = 20;
        const int pollBudget = 3;

        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 1, maxPacketsPerPeer: 64);
        using LiteNetLibClientTransport client = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        WaitUntil(() => client.IsConnected && server.ConnectedPlayerCount > 0, 3000, "Client did not connect", () =>
        {
            server.Poll();
            client.Poll();
        });

        // Server queues a burst to client
        for (int i = 0; i < messageCount; i++)
        {
            byte[] msg = [WireProtocol.Version, WireProtocol.MoveKind, (byte)i, (byte)(i >> 8)];
            Assert.True(server.TrySend(1, msg, reliable: true));
        }

        // Wait until all messages are received by client NetManager into pending events using receive-specific readiness
        WaitUntil(() => client.PendingReceiveCount >= messageCount, 3000, "Client did not receive burst into pending events", () =>
        {
            server.Poll(); // pump server so it flushes sends and processes ACKs
        });

        Assert.True(client.PendingReceiveCount >= messageCount);

        List<int> receivedIdentities = [];
        Span<byte> rx = stackalloc byte[WireProtocol.MaxMessageSize];

        int pollCount = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while (receivedIdentities.Count < messageCount)
        {
            Assert.True(sw.ElapsedMilliseconds < 5000, "Drain loop timed out");
            client.Poll(pollBudget);

            int polledInThisBatch = 0;
            while (client.TryReceive(rx, out int len))
            {
                Assert.Equal(WireProtocol.Version, rx[0]);
                Assert.Equal(WireProtocol.MoveKind, rx[1]);
                int id = rx[2] | (rx[3] << 8);
                receivedIdentities.Add(id);
                polledInThisBatch++;
            }

            Assert.True(polledInThisBatch <= pollBudget, $"Poll budget exceeded: received {polledInThisBatch} > {pollBudget}");
            pollCount++;
            Assert.True(pollCount <= 20, "Too many polls to drain burst");
        }

        Assert.Equal(messageCount, receivedIdentities.Count);
        for (int i = 0; i < messageCount; i++)
        {
            Assert.Equal(i, receivedIdentities[i]);
        }
        Assert.Equal(0, client.PendingReceiveCount);
    }

    [Fact]
    public void RealUdp_BoundedPoll_WithNoiseEvents_PreservesPayloadDeliveryAndOrdering()
    {
        const int messageCount = 20;
        const int pollBudget = 3;

        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 1, maxPacketsPerPeer: 64);
        using LiteNetLibClientTransport client = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        WaitUntil(() => client.IsConnected && server.ConnectedPlayerCount > 0, 3000, "Client did not connect", () =>
        {
            server.Poll();
            client.Poll();
        });

        // Inject non-receive noise event before payload burst
        server.NetManager.CreateEvent(NetEvent.EType.ConnectionLatencyUpdated, latency: 15);

        // Queue burst of payload packets
        for (int i = 0; i < messageCount; i++)
        {
            byte[] msg = [WireProtocol.Version, WireProtocol.MoveKind, (byte)i, (byte)(i >> 8)];
            Assert.True(client.TrySend(msg, reliable: true));
        }

        // Inject another non-receive noise event after payload burst
        server.NetManager.CreateEvent(NetEvent.EType.ConnectionLatencyUpdated, latency: 25);

        // Wait until all payload packets have arrived into server NetManager using receive-specific readiness
        WaitUntil(() => server.PendingReceiveCount >= messageCount, 3000, "Server did not receive burst with noise", () =>
        {
            client.Poll();
        });

        Assert.True(server.PendingReceiveCount >= messageCount);
        // Total pending events include the 2 injected noise events
        Assert.True(server.PendingEventCount >= messageCount + 2);

        List<int> receivedIdentities = [];
        Span<byte> rx = stackalloc byte[WireProtocol.MaxMessageSize];

        int pollCount = 0;
        Stopwatch sw = Stopwatch.StartNew();
        while (receivedIdentities.Count < messageCount)
        {
            Assert.True(sw.ElapsedMilliseconds < 5000, "Drain loop with noise timed out");
            server.Poll(pollBudget);

            int polledInThisBatch = 0;
            while (server.TryReceive(out _, out _, rx, out int len))
            {
                Assert.Equal(WireProtocol.Version, rx[0]);
                Assert.Equal(WireProtocol.MoveKind, rx[1]);
                int id = rx[2] | (rx[3] << 8);
                receivedIdentities.Add(id);
                polledInThisBatch++;
            }

            Assert.True(polledInThisBatch <= pollBudget, $"Poll budget exceeded: received {polledInThisBatch} > {pollBudget}");
            pollCount++;
            Assert.True(pollCount <= 20, "Too many polls to drain burst with noise");
        }

        Assert.Equal(messageCount, receivedIdentities.Count);
        for (int i = 0; i < messageCount; i++)
        {
            Assert.Equal(i, receivedIdentities[i]);
        }
        Assert.Equal(0, server.PendingReceiveCount);
        Assert.Equal(0L, server.ReliableOverflowFailures);
    }

    [Fact]
    public void RealUdp_BoundedPoll_TwoPeers_FairSchedulingUnderHeavyTraffic()
    {
        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 2, maxPacketsPerPeer: 64);
        using LiteNetLibClientTransport client1 = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);
        using LiteNetLibClientTransport client2 = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        WaitUntil(() => client1.IsConnected && client2.IsConnected && server.ConnectedPlayerCount == 2, 3000, "Clients did not connect", () =>
        {
            server.Poll();
            client1.Poll();
            client2.Poll();
        });

        const int c1Count = 20;
        const int c2Count = 10;

        // Client 1 floods heavy prefix burst
        for (int i = 0; i < c1Count; i++)
        {
            byte[] msg1 = [WireProtocol.Version, WireProtocol.MoveKind, 1, (byte)i];
            Assert.True(client1.TrySend(msg1, reliable: true));
        }

        // Client 2 sends traffic
        for (int i = 0; i < c2Count; i++)
        {
            byte[] msg2 = [WireProtocol.Version, WireProtocol.MoveKind, 2, (byte)i];
            Assert.True(client2.TrySend(msg2, reliable: true));
        }

        // Wait until all messages from both peers arrive in server NetManager
        WaitUntil(() => server.PendingReceiveCount >= c1Count + c2Count, 3000, "Server did not receive all peer traffic", () =>
        {
            client1.Poll();
            client2.Poll();
        });

        Assert.True(server.PendingReceiveCount >= c1Count + c2Count);

        List<int> c1Received = [];
        List<int> c2Received = [];
        List<int> serviceOrder = [];
        Span<byte> rx = stackalloc byte[WireProtocol.MaxMessageSize];

        Stopwatch sw = Stopwatch.StartNew();
        while (c1Received.Count < c1Count || c2Received.Count < c2Count)
        {
            Assert.True(sw.ElapsedMilliseconds < 5000, "Fairness drain loop timed out");
            server.Poll(6);

            while (server.TryReceive(out int player, out _, rx, out int len))
            {
                serviceOrder.Add(player);
                int seq = rx[3];
                if (player == 1) c1Received.Add(seq);
                else if (player == 2) c2Received.Add(seq);
            }
        }

        Assert.Equal(c1Count, c1Received.Count);
        Assert.Equal(c2Count, c2Received.Count);

        for (int i = 0; i < c1Count; i++) Assert.Equal(i, c1Received[i]);
        for (int i = 0; i < c2Count; i++) Assert.Equal(i, c2Received[i]);

        // Player 2 must not be starved by Player 1's heavy backlog:
        // Player 2 must be serviced once its packets enter server transport queues (at index <= 20)
        int firstP2 = serviceOrder.IndexOf(2);
        Assert.True(firstP2 >= 0 && firstP2 <= 20, $"Player 2 was starved: first serviced at index {firstP2}");

        // While both queues have pending packets, server round-robins across peers
        int lastP2 = serviceOrder.LastIndexOf(2);
        Assert.True(lastP2 >= firstP2);
    }

    [Fact]
    public void RealUdp_BoundedPoll_DisconnectAndSlotReuse_StaleEventsNotDeliveredToReplacement()
    {
        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 1, maxPacketsPerPeer: 64);
        using LiteNetLibClientTransport client1 = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        WaitUntil(() => client1.IsConnected && server.ConnectedPlayerCount > 0, 3000, "Client 1 did not connect", () =>
        {
            server.Poll();
            client1.Poll();
        });

        // Client 1 sends burst of 10 messages
        for (int i = 0; i < 10; i++)
        {
            byte[] msg = [WireProtocol.Version, WireProtocol.MoveKind, (byte)i];
            Assert.True(client1.TrySend(msg, reliable: true));
        }

        WaitUntil(() => server.PendingReceiveCount >= 10, 3000, "Server did not receive burst", () => client1.Poll());

        // Poll only 2 messages
        Span<byte> rx = stackalloc byte[WireProtocol.MaxMessageSize];
        server.Poll(2);
        Assert.True(server.TryReceive(out int p, out long c1ConnId, rx, out _));
        Assert.Equal(1, p);
        Assert.Equal(0, rx[2]);
        Assert.True(server.TryReceive(out p, out _, rx, out _));
        Assert.Equal(1, p);
        Assert.Equal(1, rx[2]);
        Assert.False(server.TryReceive(out _, out _, rx, out _));

        // Disconnect Client 1 while 8 events remain unread in NetManager
        server.DisconnectPlayer(1);
        Assert.False(server.IsPlayerConnected(1));

        // Connect Client 2 to replace Client 1 in slot 1
        using LiteNetLibClientTransport client2 = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);
        WaitUntil(() => client2.IsConnected && server.IsPlayerConnected(1), 3000, "Client 2 did not connect to slot 1", () =>
        {
            server.Poll();
            client2.Poll();
        });

        long c2ConnId = server.GetPlayerConnectionId(1);
        Assert.NotEqual(c1ConnId, c2ConnId);

        // Client 2 sends a new distinct message
        byte[] c2Msg = [WireProtocol.Version, WireProtocol.MoveKind, 99];
        Assert.True(client2.TrySend(c2Msg, reliable: true));

        WaitUntil(() => server.PendingReceiveCount > 0, 3000, "Server did not receive client 2 packet", () => client2.Poll());

        // Server polls all remaining events
        server.Poll(0);

        // TryReceive must receive Client 2's message, and NONE of Client 1's stale messages!
        Assert.True(server.TryReceive(out int recPlayer, out long recConnId, rx, out int len));
        Assert.Equal(1, recPlayer);
        Assert.Equal(c2ConnId, recConnId);
        Assert.Equal(99, rx[2]);

        // No more messages should be received
        Assert.False(server.TryReceive(out _, out _, rx, out _));
    }

    [Fact]
    public void RealUdp_BoundedPoll_OverloadPolicy_PreservesReliableDisconnectAndUnreliableDrop()
    {
        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 1, maxPacketsPerPeer: 3);
        using LiteNetLibClientTransport client = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        WaitUntil(() => client.IsConnected && server.ConnectedPlayerCount > 0, 3000, "Client did not connect", () =>
        {
            server.Poll();
            client.Poll();
        });

        // 1. Unreliable burst exceeding queue capacity (3)
        for (int i = 0; i < 8; i++)
        {
            byte[] msg = [WireProtocol.Version, WireProtocol.MoveKind, (byte)i];
            Assert.True(client.TrySend(msg, reliable: false));
        }

        WaitUntil(() => server.PendingReceiveCount >= 8, 3000, "Server did not receive unreliable burst", () => client.Poll());

        server.Poll(10);
        Span<byte> rx = stackalloc byte[WireProtocol.MaxMessageSize];
        int unreliableReceived = 0;
        while (server.TryReceive(out _, out _, rx, out _))
        {
            unreliableReceived++;
        }

        Assert.Equal(3, unreliableReceived);
        Assert.Equal(5L, server.DroppedUnreliablePackets);
        Assert.True(server.IsPlayerConnected(1)); // Unreliable drops do NOT disconnect

        // 2. Reliable burst exceeding queue capacity (3)
        for (int i = 0; i < 6; i++)
        {
            byte[] msg = [WireProtocol.Version, WireProtocol.MoveKind, (byte)i];
            Assert.True(client.TrySend(msg, reliable: true));
        }

        WaitUntil(() => server.PendingReceiveCount >= 6, 3000, "Server did not receive reliable burst", () => client.Poll());

        server.Poll(10);
        Assert.Equal(1L, server.ReliableOverflowDisconnects);
        Assert.False(server.IsPlayerConnected(1)); // Reliable overflow disconnects the peer
    }

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    public void RealUdp_SteadyStateReceive_ZeroManagedAllocations(int maxPacketsPerPeer)
    {
        const int packetCount = 100;
        const int payloadSize = 100;
        byte[] payload = new byte[payloadSize];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
        payload[0] = WireProtocol.Version;
        payload[1] = WireProtocol.MoveKind;

        using LiteNetLibServerTransport server = LiteNetLibTransport.CreateServer(0, maxPlayers: 2,
            maxPacketsPerPeer: maxPacketsPerPeer);
        using LiteNetLibClientTransport client = LiteNetLibTransport.CreateClient("127.0.0.1", server.Port);

        WaitUntil(() => client.IsConnected && server.ConnectedPlayerCount > 0, 3000, "Client did not connect", () =>
        {
            server.Poll();
            client.Poll();
        });

        Span<byte> rxBuffer = stackalloc byte[WireProtocol.MaxMessageSize];

        // Wait for and drain every warmup packet so no pending delivery enters the measurement.
        Action pumpWarmup = () =>
        {
            server.Poll();
            client.Poll();
        };
        for (int i = 0; i < 20; i++)
        {
            Assert.True(client.TrySend(payload));
            WaitUntil(() => server.QueuedPacketCount > 0, message: "Server warmup packet did not arrive", pump: pumpWarmup);
            Assert.True(server.TryReceive(out _, out _, rxBuffer, out int serverWarmupLength));
            Assert.True(rxBuffer[..serverWarmupLength].SequenceEqual(payload));
            Assert.False(server.TryReceive(out _, out _, rxBuffer, out _));

            Assert.True(server.TrySend(1, payload, reliable: false));
            WaitUntil(() => client.QueuedPacketCount > 0, message: "Client warmup packet did not arrive", pump: pumpWarmup);
            Assert.True(client.TryReceive(rxBuffer, out int clientWarmupLength));
            Assert.True(rxBuffer[..clientWarmupLength].SequenceEqual(payload));
            Assert.False(client.TryReceive(rxBuffer, out _));
        }

        // Drain each batch before sending another, including when the queue holds only one packet.
        // Only Poll/TryReceive belong to the receive allocation measurement, not the sends.
        int serverSent = 0;
        int serverReceived = 0;
        long serverBytes = 0;
        long serverAllocated = 0;
        long serverDeadline = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(3000 * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        while (serverReceived < packetCount && System.Diagnostics.Stopwatch.GetTimestamp() < serverDeadline)
        {
            if (serverSent == serverReceived)
            {
                int batchSize = Math.Min(server.MaxPacketsPerPeer, packetCount - serverSent);
                for (int i = 0; i < batchSize; i++) Assert.True(client.TrySend(payload));
                serverSent += batchSize;
            }

            long serverBefore = GC.GetAllocatedBytesForCurrentThread();
            server.Poll();
            while (server.TryReceive(out _, out _, rxBuffer, out int len))
            {
                serverReceived++;
                serverBytes += len;
            }
            serverAllocated += GC.GetAllocatedBytesForCurrentThread() - serverBefore;
            if (serverReceived < packetCount) Thread.Sleep(1);
        }

        Assert.True(server.IsPlayerConnected(1));
        Assert.Equal(0L, server.ReliableOverflowFailures);
        Assert.Equal(0L, server.ReliableOverflowDisconnects);
        Assert.Equal(packetCount, serverReceived);
        Assert.Equal(packetCount * payloadSize, serverBytes);
        Assert.Equal(0L, serverAllocated);

        // Measure Client steady-state receive path over UDP for 100 packets of 100 bytes
        for (int i = 0; i < packetCount; i++)
        {
            Assert.True(server.TrySend(1, payload, reliable: false));
        }

        long clientBefore = GC.GetAllocatedBytesForCurrentThread();
        int clientReceived = 0;
        long clientBytes = 0;
        long clientDeadline = System.Diagnostics.Stopwatch.GetTimestamp() + (long)(3000 * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        while (clientReceived < packetCount && System.Diagnostics.Stopwatch.GetTimestamp() < clientDeadline)
        {
            client.Poll();
            while (client.TryReceive(rxBuffer, out int len))
            {
                clientReceived++;
                clientBytes += len;
            }
            if (clientReceived < packetCount) Thread.Sleep(1);
        }
        long clientAllocated = GC.GetAllocatedBytesForCurrentThread() - clientBefore;

        Assert.True(client.IsConnected);
        Assert.Equal(0L, client.DroppedUnreliablePackets);
        Assert.Equal(packetCount, clientReceived);
        Assert.Equal(packetCount * payloadSize, clientBytes);
        Assert.Equal(0L, clientAllocated);
    }

    private sealed class BacklogTransport(int initialCount) : ITransport
    {
        private int _remaining = initialCount;
        public int ReceivedCount { get; private set; }
        public bool IsConnected => false;
        public byte[]? TerminalData => null;
        public bool TrySend(ReadOnlySpan<byte> message) => false;
        public bool TryReceive(Span<byte> destination, out int length)
        {
            if (_remaining <= 0)
            {
                length = 0;
                return false;
            }
            _remaining--;
            ReceivedCount++;
            destination[0] = WireProtocol.Version;
            destination[1] = WireProtocol.MoveKind;
            length = WireProtocol.MoveSize;
            return true;
        }
        public void Dispose() { }
    }

    private sealed class FloodServerTransport : IServerTransport
    {
        public int ReceivedCount { get; private set; }
        public int InitialAdmittedPlayer => 1;
        public int ConnectedPlayerCount => 1;
        public void Poll() { }
        public bool TrySend(int player, ReadOnlySpan<byte> message, bool reliable = true) => true;
        public bool TryReceive(out int player, out long connectionId, Span<byte> destination, out int length)
        {
            ReceivedCount++;
            player = 1;
            connectionId = 1;
            destination[0] = WireProtocol.Version;
            destination[1] = WireProtocol.MoveKind;
            MoveCommand cmd = new(1, 1, new EntityId(1), new Vector2(10, 10));
            WireProtocol.WriteMove(destination, cmd);
            length = WireProtocol.MoveSize;
            return true;
        }
        public bool IsPlayerConnected(int player) => true;
        public long GetPlayerConnectionId(int player) => 1;
        public void DisconnectPlayer(int player, ReadOnlySpan<byte> reason = default) { }
        public void Dispose() { }
    }

    private sealed class FloodClientTransport : ITransport
    {
        public int ReceivedCount { get; private set; }
        public bool IsConnected => true;
        public byte[]? TerminalData => null;
        public bool TrySend(ReadOnlySpan<byte> message) => true;
        public bool TryReceive(Span<byte> destination, out int length)
        {
            ReceivedCount++;
            destination[0] = WireProtocol.Version;
            destination[1] = WireProtocol.CommandResultKind;
            WireProtocol.WriteCommandResult(destination, new CommandResult(1, 1, true));
            length = WireProtocol.CommandResultSize;
            return true;
        }
        public void Dispose() { }
    }
}
