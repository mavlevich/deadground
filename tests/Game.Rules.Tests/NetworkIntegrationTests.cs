using System.Net;
using System.Net.Sockets;
using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using LiteNetLib;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class NetworkIntegrationTests
{
    private static void Pump(SimulationHost host, ClientSession c1, ClientSession c2, int iterations = 1, int delayMs = 5)
    {
        for (int i = 0; i < iterations; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            c1.Advance(1d / Simulation.TickRate);
            c2.Advance(1d / Simulation.TickRate);
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
    }

    private static void WaitFor(Func<bool> condition, SimulationHost host, ClientSession c1, ClientSession c2, int maxAttempts = 100)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
        {
            Pump(host, c1, c2, iterations: 1, delayMs: 5);
        }
        Assert.True(condition());
    }

    private static void WaitForSingle(Func<bool> condition, SimulationHost host, ClientSession client, int maxAttempts = 100)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }
        Assert.True(condition());
    }

    [Fact]
    public void TwoClientsConnectOverLoopbackAndControlTheirOwnUnits()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        // Wait for both clients to connect and receive initial snapshots
        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 12 && client2.Replica.OwnUnits.Length == 4, host, client1, client2);

        // Verify independent player slot assignment
        Assert.True(client1.IsConnected);
        Assert.True(client2.IsConnected);
        Assert.Equal(1, client1.AssignedPlayerId);
        Assert.Equal(2, client2.AssignedPlayerId);

        // Verify independent ownership
        Assert.Equal(12, client1.Replica.OwnUnits.Length);
        Assert.Equal(4, client2.Replica.OwnUnits.Length);

        EntityId player1Unit = client1.Replica.OwnUnits[0].Id;
        EntityId player2Unit = client2.Replica.OwnUnits[0].Id;

        // Verify hidden enemy state: client replicas never include enemy units
        HashSet<EntityId> c1Units = new(client1.Replica.OwnUnits.ToArray().Select(u => u.Id));
        HashSet<EntityId> c2Units = new(client2.Replica.OwnUnits.ToArray().Select(u => u.Id));
        Assert.DoesNotContain(player2Unit, c1Units);
        Assert.DoesNotContain(player1Unit, c2Units);

        // Client 1 tries to order Player 2's unit: must be rejected
        Assert.True(client1.Move(player2Unit, new Vector2(50, 50)));
        WaitFor(() => client1.LastCommandResult is { Sequence: 1 }, host, client1, client2);
        Assert.False(client1.LastCommandResult!.Value.Queued);
        Assert.Equal(1L, client1.RejectedCommands);

        // Client 2 tries to order Player 1's unit: must be rejected
        Assert.True(client2.Move(player1Unit, new Vector2(50, 50)));
        WaitFor(() => client2.LastCommandResult is { Sequence: 1 }, host, client1, client2);
        Assert.False(client2.LastCommandResult!.Value.Queued);
        Assert.Equal(1L, client2.RejectedCommands);

        // Client 1 orders its own unit: must be admitted
        Vector2 c1Target = new(30, 40);
        Assert.True(client1.Move(player1Unit, c1Target));
        WaitFor(() => client1.LastCommandResult is { Sequence: 2 }, host, client1, client2);
        Assert.True(client1.LastCommandResult!.Value.Queued);

        // Client 2 orders its own unit: must be admitted
        Vector2 c2Target = new(95, 95);
        Assert.True(client2.Move(player2Unit, c2Target));
        WaitFor(() => client2.LastCommandResult is { Sequence: 2 }, host, client1, client2);
        Assert.True(client2.LastCommandResult!.Value.Queued);

        // Direct enqueue duplicate test: Simulation deduplication rejects replayed command sequence
        Simulation freshSim = PrototypeScenario.Create(map);
        Assert.True(freshSim.TryEnqueue(1, new MoveCommand(1, 0, player1Unit, c1Target)));
        Assert.False(freshSim.TryEnqueue(1, new MoveCommand(1, 0, player1Unit, c1Target)));

        // Run simulation at 20 Hz for 20 ticks (1 second) with 2 clients connected
        int startTick = host.Tick;
        for (int i = 0; i < 20; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client1.Advance(1d / Simulation.TickRate);
            client2.Advance(1d / Simulation.TickRate);
            Thread.Sleep(2);
        }

        Assert.Equal(startTick + 20, host.Tick);
        // Both clients receive updated snapshots
        WaitFor(() => client1.Replica.LatestTick >= startTick + 18 && client2.Replica.LatestTick >= startTick + 18, host, client1, client2);
    }

    [Fact]
    public void HandshakeRejectsMismatchedClients()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        void AssertRejected(MatchSetup badSetup, string expectedReason)
        {
            using ClientSession client = ClientSession.Connect("127.0.0.1", port, badSetup);
            WaitForSingle(() => client.State == SessionConnectionState.Failed, host, client);
            Assert.Contains(expectedReason, client.StatusDescription);
            client.Dispose();
            for (int i = 0; i < 30 && host.ConnectedPlayerCount > 0; i++)
            {
                host.Advance(1d / Simulation.TickRate);
                Thread.Sleep(5);
            }
        }

        // Legacy version 2 and 3 clients must be rejected by current version server
        AssertRejected(setup with { ProtocolVersion = 2 }, "ProtocolVersionMismatch");
        AssertRejected(setup with { ProtocolVersion = 3 }, "ProtocolVersionMismatch");
        AssertRejected(setup with { ProtocolVersion = 99 }, "ProtocolVersionMismatch");
        AssertRejected(setup with { ContentHash = 0xDEADBEEF }, "ContentMismatch");
        AssertRejected(setup with { MapHash = 0xCAFEBABE }, "MapMismatch");
        AssertRejected(setup with { RuleSetId = "UnknownRules" }, "RuleSetMismatch");
    }

    [Fact]
    public void LegacyVersion3Client_ConnectingToCurrentServer_IsRejectedWithProtocolVersionMismatch()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup serverSetup = PrototypeScenario.CreateMatchSetup(map);
        Assert.Equal(MatchSetup.CurrentProtocolVersion, serverSetup.ProtocolVersion);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, serverSetup);

        // Client configured with legacy protocol version 3 (before direct-fire combat records)
        MatchSetup v3ClientSetup = serverSetup with { ProtocolVersion = 3 };
        using ClientSession client = ClientSession.Connect("127.0.0.1", port, v3ClientSetup);

        WaitForSingle(() => client.State == SessionConnectionState.Failed, host, client);
        Assert.Contains("ProtocolVersionMismatch", client.StatusDescription);
    }

    [Fact]
    public void MultiPacketSnapshotFragmentsAndReassemblesOverLoopback()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map, capacity: 200);
        // Create 150 units for player 1: 150 * 18 bytes = 2700 bytes > 1024 UDP MTU
        for (int i = 0; i < 150; i++)
            world.CreateUnit(1, 0, new Vector2(20 + (i % 10) * 3, 20 + (i / 10) * 3), 5);

        using Simulation simulation = new(world, new OpsRules());
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;
        using SimulationHost host = new(simulation, serverTransport, setup);

        using ClientSession client = ClientSession.Connect("127.0.0.1", port, setup);

        WaitForSingle(() => client.IsConnected && client.Replica.OwnUnits.Length == 150, host, client);

        Assert.True(client.IsConnected);
        Assert.Equal(150, client.Replica.OwnUnits.Length);
        Assert.True(client.BytesReceived > 2700);
    }

    [Fact]
    public void ReusedSlotRequiresNewHandshakeAndResetsCommandSequence()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 12 && client2.Replica.OwnUnits.Length == 4, host, client1, client2);

        EntityId p1Unit = client1.Replica.OwnUnits[0].Id;
        Assert.True(client1.Move(p1Unit, new Vector2(30, 30)));
        WaitFor(() => client1.LastCommandResult is { Sequence: 1 }, host, client1, client2);

        // Disconnect client 1
        client1.Dispose();
        for (int i = 0; i < 30 && host.ConnectedPlayerCount > 1; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client2.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        // Connect raw transport without handshake
        using LiteNetLibClientTransport raw = LiteNetLibTransport.CreateClient("127.0.0.1", port);
        for (int i = 0; i < 50 && !raw.IsConnected; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            raw.TryReceive(new byte[16], out _);
            Thread.Sleep(5);
        }
        Assert.True(raw.IsConnected);

        // Send a move command from raw connection before handshake
        byte[] moveBytes = new byte[WireProtocol.MoveSize];
        WireProtocol.WriteMove(moveBytes, new MoveCommand(1, host.Tick, p1Unit, new Vector2(40, 40)));
        Assert.True(raw.TrySend(moveBytes));

        // Pump host: command must be ignored, raw receives no snapshots
        byte[] recvBuffer = new byte[WireProtocol.MaxMessageSize];
        for (int i = 0; i < 10; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }
        Assert.False(raw.TryReceive(recvBuffer, out _));

        raw.Dispose();
        for (int i = 0; i < 30 && host.ConnectedPlayerCount > 1; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        // Replacement client with proper handshake occupies slot 1 and can submit seq 1
        using ClientSession replacement = ClientSession.Connect("127.0.0.1", port, setup);
        WaitFor(() => replacement.IsConnected && replacement.Replica.OwnUnits.Length == 12, host, replacement, client2);
        Assert.Equal(1, replacement.AssignedPlayerId);

        Assert.True(replacement.Move(p1Unit, new Vector2(50, 50)));
        WaitFor(() => replacement.LastCommandResult is { Sequence: 1 }, host, replacement, client2);
        Assert.True(replacement.LastCommandResult!.Value.Queued);
    }

    [Fact]
    public void OversizedPacketDoesNotCrashServer()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        using ClientSession client = ClientSession.Connect("127.0.0.1", port, setup);
        WaitForSingle(() => client.IsConnected && client.Replica.OwnUnits.Length == 12, host, client);

        RawNetListener rawListener = new();
        NetManager rawNet = new(rawListener) { AutoRecycle = true, IPv6Enabled = false };
        rawNet.Start();
        try
        {
            rawNet.Connect("127.0.0.1", port, LiteNetLibTransport.DefaultConnectionKey);
            for (int i = 0; i < 50 && rawListener.ConnectedPeer is null; i++)
            {
                host.Advance(1d / Simulation.TickRate);
                rawNet.PollEvents();
                Thread.Sleep(5);
            }
            Assert.NotNull(rawListener.ConnectedPeer);

            // Raw peer sends reliable message exceeding WireProtocol.MaxMessageSize
            byte[] hugePacket = new byte[WireProtocol.MaxMessageSize + 500];
            Array.Fill(hugePacket, (byte)1);
            rawListener.ConnectedPeer.Send(hugePacket, DeliveryMethod.ReliableOrdered);
            rawNet.PollEvents();

            for (int i = 0; i < 10; i++)
            {
                host.Advance(1d / Simulation.TickRate);
                client.Advance(1d / Simulation.TickRate);
                rawNet.PollEvents();
                Thread.Sleep(5);
            }

            EntityId unit = client.Replica.OwnUnits[0].Id;
            Assert.True(client.Move(unit, new Vector2(30, 30)));
            WaitForSingle(() => client.LastCommandResult is { Sequence: 1 }, host, client);
            Assert.True(client.LastCommandResult!.Value.Queued);
        }
        finally
        {
            rawNet.Stop();
        }
    }

    [Fact]
    public void WarmedNetworkSnapshotAllocationsAreZero()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        using ClientSession client = ClientSession.Connect("127.0.0.1", port, setup);
        WaitForSingle(() => client.IsConnected && client.Replica.OwnUnits.Length == 12, host, client);

        // Warm up for 40 ticks
        for (int i = 0; i < 40; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        // Measure allocations over next 20 ticks
        for (int i = 0; i < 20; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
            if (host.Tick % 2 == 0)
            {
                Assert.Equal(0L, host.LastTickAllocatedBytes);
            }
        }
    }

    [Fact]
    public void ConcurrentMismatchedClientsAllReceiveRejectionReasons()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        MatchSetup badSetup = setup with { ContentHash = 0xBADF00D };
        using ClientSession c1 = ClientSession.Connect("127.0.0.1", port, badSetup);
        using ClientSession c2 = ClientSession.Connect("127.0.0.1", port, badSetup);

        WaitFor(() => c1.State == SessionConnectionState.Failed && c2.State == SessionConnectionState.Failed, host, c1, c2);

        Assert.Equal("Connection rejected: ContentMismatch", c1.StatusDescription);
        Assert.Equal("Connection rejected: ContentMismatch", c2.StatusDescription);
    }

    [Fact]
    public void SlotReplacementRaceCannotBypassHandshake()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;
        PausingServerTransport wrapper = new(serverTransport);
        using SimulationHost host = new(PrototypeScenario.Create(map), wrapper, setup);

        ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        WaitForSingle(() => client1.IsConnected && client1.Replica.OwnUnits.Length == 12, host, client1);

        EntityId p1Unit = client1.Replica.OwnUnits[0].Id;
        Vector2 startPos = client1.Replica.OwnUnits[0].Position;

        static void WaitUntil(Func<bool> done, Action pump)
        {
            for (int i = 0; i < 100 && !done(); i++) { pump(); Thread.Sleep(5); }
            Assert.True(done());
        }

        RawNetListener rawListener = new();
        NetManager rawNet = new(rawListener) { AutoRecycle = true, IPv6Enabled = false };
        rawNet.Start();

        try
        {
            wrapper.BeforeNextReceive = () =>
            {
                client1.Dispose();
                WaitUntil(() => serverTransport.ConnectedPlayerCount == 0, () => serverTransport.Poll());

                rawNet.Connect("127.0.0.1", port, LiteNetLibTransport.DefaultConnectionKey);
                WaitUntil(() => rawListener.ConnectedPeer is not null && serverTransport.ConnectedPlayerCount == 1,
                    () => { serverTransport.Poll(); rawNet.PollEvents(); });

                byte[] moveBytes = new byte[WireProtocol.MoveSize];
                WireProtocol.WriteMove(moveBytes, new MoveCommand(1000, host.Tick, p1Unit, new Vector2(40, 40)));
                rawListener.ConnectedPeer!.Send(moveBytes, DeliveryMethod.ReliableOrdered);
                Thread.Sleep(30);
                serverTransport.Poll();
            };

            host.Advance(0.1);

            for (int i = 0; i < 20; i++)
            {
                host.Advance(1d / Simulation.TickRate);
                rawNet.PollEvents();
                Thread.Sleep(5);
            }

            Assert.False(rawListener.CommandResultQueued, "Unauthenticated replacement command must not be queued.");
            Assert.Equal(0, rawListener.SnapshotsReceived);
        }
        finally
        {
            rawNet.Stop();
        }

        // Legitimate replacement connects and handshakes
        using ClientSession replacement = ClientSession.Connect("127.0.0.1", port, setup);
        WaitForSingle(() => replacement.IsConnected && replacement.Replica.OwnUnits.Length == 12, host, replacement);
        Assert.Equal(startPos, replacement.Replica.OwnUnits[0].Position);
        Assert.True(replacement.Move(p1Unit, new Vector2(50, 50)));
        WaitForSingle(() => replacement.LastCommandResult is { Sequence: 1 }, host, replacement);
        Assert.True(replacement.LastCommandResult!.Value.Queued);
    }

    [Fact]
    public void RejectedHandshakesFreeSlotsForNewClients()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        MatchSetup badSetup = setup with { ProtocolVersion = 99 };
        using (ClientSession rejected1 = ClientSession.Connect("127.0.0.1", port, badSetup))
        using (ClientSession rejected2 = ClientSession.Connect("127.0.0.1", port, badSetup))
        {
            WaitFor(() => rejected1.State == SessionConnectionState.Failed &&
                          rejected2.State == SessionConnectionState.Failed, host, rejected1, rejected2);
        }

        for (int i = 0; i < 50 && host.ConnectedPlayerCount > 0; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }
        Assert.Equal(0, host.ConnectedPlayerCount);

        using ClientSession valid1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession valid2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => valid1.IsConnected && valid2.IsConnected, host, valid1, valid2);
        Assert.Equal(1, valid1.AssignedPlayerId);
        Assert.Equal(2, valid2.AssignedPlayerId);
    }

    [Fact]
    public void ClientObservesServerDisconnect()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;
        SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        using ClientSession client = ClientSession.Connect("127.0.0.1", port, setup);
        WaitForSingle(() => client.IsConnected && client.Replica.OwnUnits.Length == 12, host, client);

        host.Dispose();

        for (int i = 0; i < 50 && client.IsConnected; i++)
        {
            client.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        Assert.Equal(SessionConnectionState.Disconnected, client.State);
        Assert.Equal("Disconnected from server", client.StatusDescription);
    }

    [Fact]
    public void TwoClientsConnectOnLargeNavigationScenarioAndMoveIndependentlyWithOpponentRouteIsolation()
    {
        StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
        MatchSetup setup = LargeNavigationScenario.CreateMatchSetup(geo);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(LargeNavigationScenario.CreateSimulation(geo), serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        // Wait for both clients to connect and receive initial snapshots on 2x2 km map
        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 3 && client2.Replica.OwnUnits.Length == 3, host, client1, client2);

        Assert.Equal(1, client1.AssignedPlayerId);
        Assert.Equal(2, client2.AssignedPlayerId);

        EntityId player1Unit = client1.Replica.OwnUnits[0].Id;
        EntityId player2Unit = client2.Replica.OwnUnits[0].Id;

        // Route isolation: Client replicas must only contain own units; opponents are in fog of war
        HashSet<EntityId> c1Units = new(client1.Replica.OwnUnits.ToArray().Select(u => u.Id));
        HashSet<EntityId> c2Units = new(client2.Replica.OwnUnits.ToArray().Select(u => u.Id));
        Assert.DoesNotContain(player2Unit, c1Units);
        Assert.DoesNotContain(player1Unit, c2Units);
        Assert.Equal(0, client1.Replica.Contacts.Length);
        Assert.Equal(0, client2.Replica.Contacts.Length);

        // Cross-ownership order rejection
        Assert.True(client1.Move(player2Unit, new Vector2(300f, 300f)));
        WaitFor(() => client1.LastCommandResult is { Sequence: 1 }, host, client1, client2);
        Assert.False(client1.LastCommandResult!.Value.Queued);
        Assert.Equal(1L, client1.RejectedCommands);

        // Client 1 issues valid movement order across hierarchical sectors
        Vector2 c1Target = new(350f, 350f);
        Assert.True(client1.Move(player1Unit, c1Target));
        WaitFor(() => client1.LastCommandResult is { Sequence: 2 }, host, client1, client2);
        Assert.True(client1.LastCommandResult!.Value.Queued);

        // Client 2 issues valid movement order
        Vector2 c2Target = new(1700f, 1700f);
        Assert.True(client2.Move(player2Unit, c2Target));
        WaitFor(() => client2.LastCommandResult is { Sequence: 1 }, host, client1, client2);
        Assert.True(client2.LastCommandResult!.Value.Queued);

        // Advance simulation and verify movement replication over network
        WaitFor(() => client1.Replica.OwnUnits[0].Position.X > 205f &&
                      client2.Replica.OwnUnits[0].Position.X < 1795f, host, client1, client2, maxAttempts: 150);

        // Verify position replication and that route isolation is preserved
        Assert.Equal(PathRequestStatus.PathFound, client1.Replica.OwnUnits[0].NavStatus);
        Assert.Equal(PathRequestStatus.PathFound, client2.Replica.OwnUnits[0].NavStatus);
        Assert.Equal(0, client1.Replica.Contacts.Length);
        Assert.Equal(0, client2.Replica.Contacts.Length);
    }

    private sealed class PausingServerTransport(IServerTransport inner) : IServerTransport
    {
        public Action? BeforeNextReceive;
        public int InitialAdmittedPlayer => inner.InitialAdmittedPlayer;
        public int ConnectedPlayerCount => inner.ConnectedPlayerCount;
        public void Poll() => inner.Poll();
        public bool TrySend(int player, ReadOnlySpan<byte> message, bool reliable = true) => inner.TrySend(player, message, reliable);
        public bool TryReceive(out int player, out long connectionId, Span<byte> destination, out int length)
        {
            Action? pause = BeforeNextReceive;
            BeforeNextReceive = null;
            pause?.Invoke();
            return inner.TryReceive(out player, out connectionId, destination, out length);
        }
        public bool IsPlayerConnected(int player) => inner.IsPlayerConnected(player);
        public long GetPlayerConnectionId(int player) => inner.GetPlayerConnectionId(player);
        public void DisconnectPlayer(int player, ReadOnlySpan<byte> reason = default) => inner.DisconnectPlayer(player, reason);
        public void Dispose() => inner.Dispose();
    }

    private sealed class RawNetListener : INetEventListener
    {
        public NetPeer? ConnectedPeer;
        public int SnapshotsReceived;
        public bool CommandResultReceived;
        public bool CommandResultQueued;

        public void OnPeerConnected(NetPeer peer) => ConnectedPeer = peer;
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (ConnectedPeer == peer) ConnectedPeer = null;
        }
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            byte[] bytes = reader.GetRemainingBytes();
            if (bytes.Length > 1 && bytes[1] == WireProtocol.SnapshotKind)
            {
                SnapshotsReceived++;
            }
            else if (WireProtocol.TryReadCommandResult(bytes, out CommandResult result))
            {
                CommandResultReceived = true;
                CommandResultQueued = result.Queued;
            }
        }
        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
        public void OnNetworkReceiveUnconnected(IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
    }

    [Fact]
    public void VehicleTrafficSmokeFlowTest()
    {
        var geo = VehicleTrafficScenario.CreateStaticGeometry();
        var sim = VehicleTrafficScenario.CreateSimulation(geo);
        using LocalSession session = new(sim, player: 1);

        bool observedWaiting = false;
        bool leaderCleared = false;
        bool observedResumed = false;

        int waitingTick = -1;

        for (int tick = 0; tick < 200; tick++)
        {
            session.Advance(1d / Simulation.TickRate);
            var ownUnits = session.Replica.OwnUnits;
            for (int u = 0; u < ownUnits.Length; u++)
            {
                if (ownUnits[u].NavStatus == PathRequestStatus.WaitingForClearance)
                {
                    if (!observedWaiting) waitingTick = tick;
                    observedWaiting = true;
                }
            }

            if (observedWaiting && !leaderCleared && ownUnits.Length > 0)
            {
                bool moved = session.Move(ownUnits[0].Id, new Vector2(60f, 100f));
                leaderCleared = true;
            }

            if (leaderCleared && ownUnits.Length > 1)
            {
                if (ownUnits[1].NavStatus == PathRequestStatus.PathFound)
                {
                    observedResumed = true;
                }
            }

        }

        Assert.True(observedWaiting, "Should observe waiting for clearance");
        Assert.True(observedResumed, "Should observe resumed movement");
    }
}
