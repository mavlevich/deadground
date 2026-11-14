using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class BattleMatchResultTests
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

    private static void WaitFor(Func<bool> condition, SimulationHost host, ClientSession c1, ClientSession c2, int maxAttempts = 150)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
        {
            Pump(host, c1, c2, iterations: 1, delayMs: 5);
        }
        Assert.True(condition());
    }

    [Fact]
    public void PeacefulMatchWithoutLosses_RemainsInProgress()
    {
        using Simulation sim = BattleScenario.CreateSimulation();
        Assert.NotNull(sim.Mission);

        for (int tick = 0; tick < 50; tick++)
        {
            sim.Step();
            Assert.False(sim.IsMissionFinished);
            Assert.Equal(MissionStatus.InProgress, sim.Mission.GetObservation(1).Status);
            Assert.Equal(MissionStatus.InProgress, sim.Mission.GetObservation(2).Status);
        }
    }

    [Fact]
    public void VictoryForPlayer1_WhenPlayer2UnitsDestroyed()
    {
        using Simulation sim = BattleScenario.CreateSimulation();
        WorldState world = sim.World;

        // Remove all player 2 units
        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 2)
            {
                world.RemoveUnit(u.Id);
            }
        }

        sim.Step();

        Assert.True(sim.IsMissionFinished);
        Assert.NotNull(sim.Mission);

        MissionObservation p1Obs = sim.Mission.GetObservation(1);
        Assert.Equal(BattleMissionSystem.MissionIdentifier, p1Obs.MissionId);
        Assert.Equal(MissionStatus.Victory, p1Obs.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, p1Obs.EndReason);
        Assert.Equal(1, p1Obs.FinishedTick);

        MissionObservation p2Obs = sim.Mission.GetObservation(2);
        Assert.Equal(BattleMissionSystem.MissionIdentifier, p2Obs.MissionId);
        Assert.Equal(MissionStatus.Defeat, p2Obs.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, p2Obs.EndReason);
        Assert.Equal(1, p2Obs.FinishedTick);
    }

    [Fact]
    public void VictoryForPlayer2_WhenPlayer1UnitsDestroyed()
    {
        using Simulation sim = BattleScenario.CreateSimulation();
        WorldState world = sim.World;

        // Remove all player 1 units
        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 1)
            {
                world.RemoveUnit(u.Id);
            }
        }

        sim.Step();

        Assert.True(sim.IsMissionFinished);
        Assert.NotNull(sim.Mission);

        MissionObservation p1Obs = sim.Mission.GetObservation(1);
        Assert.Equal(BattleMissionSystem.MissionIdentifier, p1Obs.MissionId);
        Assert.Equal(MissionStatus.Defeat, p1Obs.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, p1Obs.EndReason);
        Assert.Equal(1, p1Obs.FinishedTick);

        MissionObservation p2Obs = sim.Mission.GetObservation(2);
        Assert.Equal(BattleMissionSystem.MissionIdentifier, p2Obs.MissionId);
        Assert.Equal(MissionStatus.Victory, p2Obs.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, p2Obs.EndReason);
        Assert.Equal(1, p2Obs.FinishedTick);
    }

    [Fact]
    public void Draw_WhenBothPlayersLoseLastUnitsOnSameTick()
    {
        using Simulation sim = BattleScenario.CreateSimulation();
        WorldState world = sim.World;

        // Remove all units of both sides on the same tick
        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            world.RemoveUnit(u.Id);
        }

        sim.Step();

        Assert.True(sim.IsMissionFinished);
        Assert.NotNull(sim.Mission);

        MissionObservation p1Obs = sim.Mission.GetObservation(1);
        Assert.Equal(BattleMissionSystem.MissionIdentifier, p1Obs.MissionId);
        Assert.Equal(MissionStatus.Draw, p1Obs.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, p1Obs.EndReason);
        Assert.Equal(1, p1Obs.FinishedTick);

        MissionObservation p2Obs = sim.Mission.GetObservation(2);
        Assert.Equal(BattleMissionSystem.MissionIdentifier, p2Obs.MissionId);
        Assert.Equal(MissionStatus.Draw, p2Obs.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, p2Obs.EndReason);
        Assert.Equal(1, p2Obs.FinishedTick);
    }

    [Fact]
    public void ResultIsImmutable_OnceFinished()
    {
        using Simulation sim = BattleScenario.CreateSimulation();
        WorldState world = sim.World;

        // Wipe out player 2
        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 2)
            {
                world.RemoveUnit(u.Id);
            }
        }

        sim.Step();
        Assert.True(sim.IsMissionFinished);
        int finishedTick = sim.Mission!.GetObservation(1).FinishedTick;
        Assert.Equal(1, finishedTick);

        // Subsequently wipe out remaining player 1 units after match already ended
        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 1)
            {
                world.RemoveUnit(u.Id);
            }
        }

        for (int t = 0; t < 20; t++)
        {
            sim.Step();
        }

        MissionObservation p1Obs = sim.Mission.GetObservation(1);
        Assert.Equal(MissionStatus.Victory, p1Obs.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, p1Obs.EndReason);
        Assert.Equal(finishedTick, p1Obs.FinishedTick);

        MissionObservation p2Obs = sim.Mission.GetObservation(2);
        Assert.Equal(MissionStatus.Defeat, p2Obs.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, p2Obs.EndReason);
        Assert.Equal(finishedTick, p2Obs.FinishedTick);
    }

    [Fact]
    public void FinishedMission_RejectsOrders_AndHaltsUnits()
    {
        using Simulation sim = BattleScenario.CreateSimulation();
        WorldState world = sim.World;

        // Find a player 1 unit
        EntityId p1Unit = default;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 1)
            {
                p1Unit = u.Id;
                break;
            }
        }
        Assert.NotEqual(default, p1Unit);

        // Start player 1 unit moving
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, p1Unit, new Vector2(400f, 1600f))));
        sim.Step();
        sim.Step();

        // Wipe out player 2 to trigger finish
        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 2)
            {
                world.RemoveUnit(u.Id);
            }
        }

        sim.Step();
        Assert.True(sim.IsMissionFinished);

        // New move order must be rejected
        bool moveAccepted = sim.TryEnqueue(1, new MoveCommand(2, 0, p1Unit, new Vector2(450f, 1600f)));
        Assert.False(moveAccepted);

        // New attack order must be rejected
        bool attackAccepted = sim.TryEnqueue(1, new AttackCommand(3, 0, p1Unit, new ContactId(101)));
        Assert.False(attackAccepted);

        // New fire stance order must be rejected
        bool stanceAccepted = sim.TryEnqueue(1, new SetFireStanceCommand(4, 0, p1Unit, FireStance.HoldFire));
        Assert.False(stanceAccepted);

        // Step again: cleanup must halt moving units
        sim.Step();
        Assert.True(world.TryGetUnit(p1Unit, out UnitState haltedUnit));
        Assert.False(haltedUnit.Moving);
    }

    [Fact]
    public void WireProtocol_DrawStatus_RoundTripsCorrectly()
    {
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        SnapshotFrame frame = new(32);

        MissionObservation mission = new(
            MissionId: BattleMissionSystem.MissionIdentifier,
            Status: MissionStatus.Draw,
            EndReason: MissionEndReason.AllArmedUnitsDestroyed,
            TimeLimitTicks: 0,
            FinishedTick: 250,
            TargetContactId: default,
            SearchAreaCenter: Vector2.Zero,
            SearchAreaRadius: 0f,
            Flags: 0);

        int written = WireProtocol.WriteSnapshot(
            buffer,
            sequence: 1,
            tick: 250,
            units: default,
            contacts: default,
            events: default,
            mission: mission,
            wrecks: default);

        Assert.True(written > 0);
        bool success = WireProtocol.TryReadSnapshot(buffer.AsSpan(0, written), frame);
        Assert.True(success);
        Assert.Equal(MissionStatus.Draw, frame.Mission.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, frame.Mission.EndReason);
        Assert.Equal(250, frame.Mission.FinishedTick);
        Assert.Equal(BattleMissionSystem.MissionIdentifier, frame.Mission.MissionId);
    }

    [Fact]
    public void NetworkSnapshots_DeliverDistinctResultsToWinnerAndLoser()
    {
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        MatchSetup setup = BattleScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;

        Simulation simulation = BattleScenario.CreateSimulation(geometry);
        using SimulationHost host = new(simulation, serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == BattleScenario.UnitsPerSide &&
            client2.Replica.OwnUnits.Length == BattleScenario.UnitsPerSide, host, client1, client2);

        Assert.Equal(1, client1.AssignedPlayerId);
        Assert.Equal(2, client2.AssignedPlayerId);
        Assert.Equal(MissionStatus.InProgress, client1.Mission.Status);
        Assert.Equal(MissionStatus.InProgress, client2.Mission.Status);

        // Wipe out player 2 on server
        WorldState world = simulation.World;
        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 2)
            {
                world.RemoveUnit(u.Id);
            }
        }

        // Advance host and clients until result is delivered
        WaitFor(() => client1.Mission.Status == MissionStatus.Victory && client2.Mission.Status == MissionStatus.Defeat, host, client1, client2);

        Assert.Equal(MissionStatus.Victory, client1.Mission.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, client1.Mission.EndReason);
        Assert.True(client1.Mission.FinishedTick > 0);

        Assert.Equal(MissionStatus.Defeat, client2.Mission.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, client2.Mission.EndReason);
        Assert.Equal(client1.Mission.FinishedTick, client2.Mission.FinishedTick);
    }

    [Fact]
    public void FreshClient_ReceivesFinishedResultViaSnapshot_WithoutPriorCombatEvents()
    {
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        MatchSetup setup = BattleScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;

        Simulation simulation = BattleScenario.CreateSimulation(geometry);
        using SimulationHost host = new(simulation, serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2Initial = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2Initial.IsConnected, host, client1, client2Initial);

        // Wipe out player 1 on server
        WorldState world = simulation.World;
        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 1)
            {
                world.RemoveUnit(u.Id);
            }
        }

        WaitFor(() => client2Initial.Mission.Status == MissionStatus.Victory, host, client1, client2Initial);
        int finishedTick = client2Initial.Mission.FinishedTick;
        Assert.True(finishedTick > 0);

        // Disconnect initial client 2
        client2Initial.Dispose();
        for (int i = 0; i < 20; i++) host.Advance(1d / Simulation.TickRate);

        // Connect a fresh client 2 that never witnessed prior combat
        using ClientSession client2Fresh = ClientSession.Connect("127.0.0.1", port, setup);
        for (int i = 0; i < 150 && !client2Fresh.IsConnected; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client1.Advance(1d / Simulation.TickRate);
            client2Fresh.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        for (int i = 0; i < 150 && client2Fresh.Mission.Status == MissionStatus.InProgress; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client1.Advance(1d / Simulation.TickRate);
            client2Fresh.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        Assert.Equal(MissionStatus.Victory, client2Fresh.Mission.Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, client2Fresh.Mission.EndReason);
        Assert.Equal(finishedTick, client2Fresh.Mission.FinishedTick);
    }

    [Fact]
    public void LegacyVersion22Client_ConnectingToCurrentServer_IsRejectedWithProtocolMismatch()
    {
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        MatchSetup serverSetup = BattleScenario.CreateMatchSetup(geometry);
        Assert.Equal(24, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(24, serverSetup.ProtocolVersion);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;

        Simulation simulation = BattleScenario.CreateSimulation(geometry);
        using SimulationHost host = new(simulation, serverTransport, serverSetup);

        // Client configured with legacy protocol version 22 (before Draw was added)
        MatchSetup v22ClientSetup = serverSetup with { ProtocolVersion = 22 };
        using ClientSession client = ClientSession.Connect("127.0.0.1", port, v22ClientSetup);

        for (int i = 0; i < 150 && client.State != SessionConnectionState.Failed; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        Assert.Equal(SessionConnectionState.Failed, client.State);
        Assert.Contains("ProtocolVersionMismatch", client.StatusDescription);
    }

    [Fact]
    public void LegacyVersion23Client_ConnectingToCurrentServer_IsRejectedWithProtocolMismatch()
    {
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        MatchSetup serverSetup = BattleScenario.CreateMatchSetup(geometry);
        Assert.Equal(24, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(24, serverSetup.ProtocolVersion);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;

        Simulation simulation = BattleScenario.CreateSimulation(geometry);
        using SimulationHost host = new(simulation, serverTransport, serverSetup);

        // Client configured with legacy protocol version 23 (before Infantry wire values were added)
        MatchSetup v23ClientSetup = serverSetup with { ProtocolVersion = 23 };
        using ClientSession client = ClientSession.Connect("127.0.0.1", port, v23ClientSetup);

        for (int i = 0; i < 150 && client.State != SessionConnectionState.Failed; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        Assert.Equal(SessionConnectionState.Failed, client.State);
        Assert.Contains("ProtocolVersionMismatch", client.StatusDescription);
    }

    [Fact]
    public void WireProtocol_DrawStatus_RejectedByLegacyVersion22SnapshotBounds()
    {
        byte[] buffer = new byte[1024];
        MissionObservation drawObs = new(
            MissionId: BattleMissionSystem.MissionIdentifier,
            Status: MissionStatus.Draw,
            EndReason: MissionEndReason.AllArmedUnitsDestroyed,
            TimeLimitTicks: 0,
            FinishedTick: 200,
            TargetContactId: default,
            SearchAreaCenter: Vector2.Zero,
            SearchAreaRadius: 0f,
            Flags: 0);

        int written = WireProtocol.WriteSnapshot(
            buffer,
            sequence: 1,
            tick: 200,
            units: default,
            contacts: default,
            events: default,
            mission: drawObs,
            wrecks: default);

        Assert.True(written > 0);

        // Current validator (v23) accepts Draw = 4
        SnapshotFrame frame = new(32);
        Assert.True(WireProtocol.TryReadSnapshot(buffer.AsSpan(0, written), frame));
        Assert.Equal(MissionStatus.Draw, frame.Mission.Status);

        // Legacy v22 client snapshot validator checked statusByte <= (byte)MissionStatus.Defeat (3).
        // Since statusByte for Draw is 4, legacy client rejected the entire snapshot frame.
        ReadOnlySpan<byte> missionRecord = buffer.AsSpan(18, WireProtocol.MissionSize);
        byte statusByte = missionRecord[2];
        Assert.Equal((byte)MissionStatus.Draw, statusByte);
        Assert.True(statusByte > (byte)MissionStatus.Defeat, "Draw status byte must exceed legacy Defeat upper bound (3).");
    }
}
