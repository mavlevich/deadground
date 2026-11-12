using System.Net;
using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using LiteNetLib;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class NetworkSessionUiTests
{
    private static void Pump(SimulationHost host, ClientLifecycleController controller, int iterations = 1, int delayMs = 5)
    {
        for (int i = 0; i < iterations; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            controller.Advance(1d / Simulation.TickRate);
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
    }

    private static void WaitFor(Func<bool> condition, SimulationHost host, ClientLifecycleController controller, int maxAttempts = 150)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
        {
            Pump(host, controller, iterations: 1, delayMs: 5);
        }
        Assert.True(condition());
    }

    [Fact]
    public void TryConnect_ToRunningServer_AdvancesThroughStatesToActive_AndReceivesFirstSnapshot()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        using ClientLifecycleController controller = new();
        bool readyFired = false;
        controller.OnSessionReady += session =>
        {
            readyFired = true;
            Assert.True(session.Replica.HasSnapshots);
        };

        bool ok = controller.TryConnect($"127.0.0.1:{port}", setup, out string? error);
        Assert.True(ok);
        Assert.Null(error);

        WaitFor(() => controller.State == ClientLifecycleState.Active, host, controller);

        Assert.True(readyFired);
        Assert.NotNull(controller.NetworkSession);
        Assert.True(controller.NetworkSession.IsConnected);
        Assert.True(controller.NetworkSession.Replica.HasSnapshots);
        Assert.Equal(1, controller.NetworkSession.AssignedPlayerId);
    }

    [Fact]
    public void TryConnect_ServerFull_FailsWithCleanServerFullMessage()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        // Reserving slot 1 for bot causes incoming connection to be rejected as ServerFull
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup, reservedBotPlayer: 1);

        using ClientLifecycleController controller = new();
        string? failureReason = null;
        controller.OnConnectionFailed += reason => failureReason = reason;

        bool ok = controller.TryConnect($"127.0.0.1:{port}", setup, out _);
        Assert.True(ok);

        for (int i = 0; i < 150 && controller.State != ClientLifecycleState.Failed; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            controller.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        Assert.Equal(ClientLifecycleState.Failed, controller.State);
        Assert.NotNull(failureReason);
        Assert.Contains("Server is full", failureReason);
        Assert.Contains("Server is full", controller.StatusDescription);
    }

    [Fact]
    public void TryConnect_ProtocolMismatch_FailsWithCleanVersionMismatchMessage()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup serverSetup = PrototypeScenario.CreateMatchSetup(map);
        MatchSetup clientOldSetup = serverSetup with { ProtocolVersion = 22 };

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, serverSetup);

        using ClientLifecycleController controller = new();
        string? failureReason = null;
        controller.OnConnectionFailed += reason => failureReason = reason;

        bool ok = controller.TryConnect($"127.0.0.1:{port}", clientOldSetup, out _);
        Assert.True(ok);

        for (int i = 0; i < 150 && controller.State != ClientLifecycleState.Failed; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            controller.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        Assert.Equal(ClientLifecycleState.Failed, controller.State);
        Assert.NotNull(failureReason);
        Assert.Contains("Protocol version mismatch", failureReason);
    }

    [Fact]
    public void TryConnect_CancelWhileConnecting_AbortsAndPreventsActivation()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        using ClientLifecycleController controller = new();
        bool readyFired = false;
        controller.OnSessionReady += _ => readyFired = true;

        bool ok = controller.TryConnect($"127.0.0.1:{port}", setup, out _);
        Assert.True(ok);

        controller.Cancel();
        Assert.Equal(ClientLifecycleState.Inactive, controller.State);
        Assert.Null(controller.NetworkSession);

        Pump(host, controller, iterations: 30, delayMs: 5);

        Assert.False(readyFired);
        Assert.Equal(ClientLifecycleState.Inactive, controller.State);
    }

    [Fact]
    public void DisconnectedServer_FiresOnConnectionLost_AndRejectsOrders()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        using ClientLifecycleController controller = new();
        string? lostReason = null;
        controller.OnConnectionLost += reason => lostReason = reason;

        controller.TryConnect($"127.0.0.1:{port}", setup, out _);
        WaitFor(() => controller.State == ClientLifecycleState.Active, host, controller);

        ClientSession session = controller.NetworkSession!;
        Assert.True(session.IsConnected);

        // Abruptly shut down the host
        host.Dispose();
        serverTransport.Dispose();

        for (int i = 0; i < 150 && controller.State != ClientLifecycleState.Disconnected; i++)
        {
            controller.Advance(1d / Simulation.TickRate);
            Thread.Sleep(10);
        }

        Assert.Equal(ClientLifecycleState.Disconnected, controller.State);
        Assert.NotNull(lostReason);

        // Orders must be rejected when disconnected
        bool moveRejected = session.Move(new EntityId(1), new Vector2(50, 50));
        Assert.False(moveRejected);

        bool stanceRejected = session.SetFireStance(new EntityId(1), FireStance.HoldFire);
        Assert.False(stanceRejected);
    }

    [Fact]
    public void TwoClients_AssignedDistinctPlayerSides_BothReachActive()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, setup);

        using ClientLifecycleController c1 = new();
        using ClientLifecycleController c2 = new();

        c1.TryConnect($"127.0.0.1:{port}", setup, out _);
        c2.TryConnect($"127.0.0.1:{port}", setup, out _);

        for (int i = 0; i < 150 && (c1.State != ClientLifecycleState.Active || c2.State != ClientLifecycleState.Active); i++)
        {
            host.Advance(1d / Simulation.TickRate);
            c1.Advance(1d / Simulation.TickRate);
            c2.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        Assert.Equal(ClientLifecycleState.Active, c1.State);
        Assert.Equal(ClientLifecycleState.Active, c2.State);
        Assert.Equal(1, c1.NetworkSession!.AssignedPlayerId);
        Assert.Equal(2, c2.NetworkSession!.AssignedPlayerId);
    }

    [Fact]
    public void TwoSequentialMatches_WithServerRestart_ReusesControllersWithoutRestartingClients()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);

        using ClientLifecycleController c1 = new();
        using ClientLifecycleController c2 = new();

        // Match 1
        using (LiteNetLibServerTransport server1 = LiteNetLibTransport.CreateServer(0, maxPlayers: 2))
        {
            int port1 = server1.Port;
            using SimulationHost host1 = new(PrototypeScenario.Create(map), server1, setup);

            c1.TryConnect($"127.0.0.1:{port1}", setup, out _);
            c2.TryConnect($"127.0.0.1:{port1}", setup, out _);

            for (int i = 0; i < 150 && (c1.State != ClientLifecycleState.Active || c2.State != ClientLifecycleState.Active); i++)
            {
                host1.Advance(1d / Simulation.TickRate);
                c1.Advance(1d / Simulation.TickRate);
                c2.Advance(1d / Simulation.TickRate);
                Thread.Sleep(5);
            }

            Assert.Equal(ClientLifecycleState.Active, c1.State);
            Assert.Equal(ClientLifecycleState.Active, c2.State);
            Assert.Equal(1, c1.NetworkSession!.AssignedPlayerId);
            Assert.Equal(2, c2.NetworkSession!.AssignedPlayerId);

            // Clients leave match back to menu
            c1.Disconnect();
            c2.Disconnect();
        }

        Assert.Equal(ClientLifecycleState.Inactive, c1.State);
        Assert.Equal(ClientLifecycleState.Inactive, c2.State);

        // Server restarts for Match 2
        using (LiteNetLibServerTransport server2 = LiteNetLibTransport.CreateServer(0, maxPlayers: 2))
        {
            int port2 = server2.Port;
            using SimulationHost host2 = new(PrototypeScenario.Create(map), server2, setup);

            // Reconnect using the same controllers (simulating clients staying open)
            c1.TryConnect($"127.0.0.1:{port2}", setup, out _);
            c2.TryConnect($"127.0.0.1:{port2}", setup, out _);

            for (int i = 0; i < 150 && (c1.State != ClientLifecycleState.Active || c2.State != ClientLifecycleState.Active); i++)
            {
                host2.Advance(1d / Simulation.TickRate);
                c1.Advance(1d / Simulation.TickRate);
                c2.Advance(1d / Simulation.TickRate);
                Thread.Sleep(5);
            }

            Assert.Equal(ClientLifecycleState.Active, c1.State);
            Assert.Equal(ClientLifecycleState.Active, c2.State);
            Assert.Equal(1, c1.NetworkSession!.AssignedPlayerId);
            Assert.Equal(2, c2.NetworkSession!.AssignedPlayerId);
        }
    }
}
