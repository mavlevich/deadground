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
}
