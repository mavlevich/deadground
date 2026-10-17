using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class ReconRaidMissionTests
{
    private sealed class TestBotSink : IBotCommandSink
    {
        public readonly List<MoveCommand> Moves = new();
        public readonly List<AttackCommand> Attacks = new();
        public int KnowledgeUpdateCount;
        public int AcknowledgedEventsCount;

        public void UpdateKnowledge(PlayerKnowledge knowledge) => KnowledgeUpdateCount++;

        public bool EnqueueMove(MoveCommand command)
        {
            Moves.Add(command);
            return true;
        }

        public bool EnqueueAttack(AttackCommand command)
        {
            Attacks.Add(command);
            return true;
        }

        public void AcknowledgeEvents(int count)
        {
            AcknowledgedEventsCount += count;
        }
    }

    [Fact]
    public void Victory_WhenCommandVehicleDestroyed_AndReconAlive()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Command vehicle is unit 4
        Assert.True(sim.World.TryGetUnit(new EntityId(4), out _));
        sim.World.RemoveUnit(new EntityId(4));

        sim.Step();

        Assert.True(sim.IsMissionFinished);
        Assert.NotNull(sim.Mission);
        Assert.Equal(MissionStatus.Victory, sim.Mission.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.TargetDestroyed, sim.Mission.GetObservation(1).EndReason);
        Assert.True(sim.Mission.GetObservation(1).IsTargetDestroyed);
    }

    [Fact]
    public void Defeat_WhenReconVehicleDestroyed()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Player recon vehicle is unit 1
        Assert.True(sim.World.TryGetUnit(new EntityId(1), out _));
        sim.World.RemoveUnit(new EntityId(1));

        sim.Step();

        Assert.True(sim.IsMissionFinished);
        Assert.NotNull(sim.Mission);
        Assert.Equal(MissionStatus.Defeat, sim.Mission.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.ReconVehicleDestroyed, sim.Mission.GetObservation(1).EndReason);
    }

    [Fact]
    public void Defeat_WhenAllCombatTanksDestroyed()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Player tanks are unit 2 and unit 3
        sim.World.RemoveUnit(new EntityId(2));
        sim.World.RemoveUnit(new EntityId(3));

        sim.Step();

        Assert.True(sim.IsMissionFinished);
        Assert.NotNull(sim.Mission);
        Assert.Equal(MissionStatus.Defeat, sim.Mission.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, sim.Mission.GetObservation(1).EndReason);
    }

    [Fact]
    public void Defeat_WhenTimeLimitExpired()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry, timeLimitTicks: 10);

        for (int i = 0; i < 10; i++)
        {
            sim.Step();
        }

        Assert.True(sim.IsMissionFinished);
        Assert.NotNull(sim.Mission);
        Assert.Equal(MissionStatus.Defeat, sim.Mission.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.TimeLimitExpired, sim.Mission.GetObservation(1).EndReason);
    }

    [Fact]
    public void SimultaneousVictoryAndDefeat_DefeatTakesPrecedence()
    {
        // 1. Command vehicle and recon vehicle destroyed in same tick
        {
            StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
            Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

            sim.World.RemoveUnit(new EntityId(4)); // Command vehicle
            sim.World.RemoveUnit(new EntityId(1)); // Recon vehicle

            sim.Step();

            Assert.True(sim.IsMissionFinished);
            Assert.Equal(MissionStatus.Defeat, sim.Mission!.GetObservation(1).Status);
            Assert.Equal(MissionEndReason.ReconVehicleDestroyed, sim.Mission.GetObservation(1).EndReason);
        }

        // 2. Command vehicle and both tanks destroyed in same tick
        {
            StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
            Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

            sim.World.RemoveUnit(new EntityId(4)); // Command vehicle
            sim.World.RemoveUnit(new EntityId(2)); // Tank 1
            sim.World.RemoveUnit(new EntityId(3)); // Tank 2

            sim.Step();

            Assert.True(sim.IsMissionFinished);
            Assert.Equal(MissionStatus.Defeat, sim.Mission!.GetObservation(1).Status);
            Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, sim.Mission.GetObservation(1).EndReason);
        }

        // 3. Command vehicle destroyed at expiration tick
        {
            StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
            Simulation sim = ReconRaidScenario.CreateSimulation(geometry, timeLimitTicks: 5);

            for (int i = 0; i < 4; i++) sim.Step();
            sim.World.RemoveUnit(new EntityId(4)); // Command vehicle destroyed on final tick

            sim.Step(); // tick 5 == timeLimitTicks

            Assert.True(sim.IsMissionFinished);
            Assert.Equal(MissionStatus.Defeat, sim.Mission!.GetObservation(1).Status);
            Assert.Equal(MissionEndReason.TimeLimitExpired, sim.Mission.GetObservation(1).EndReason);
        }
    }

    [Fact]
    public void MissionResult_IsImmutable_OnceFinished()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Win mission
        sim.World.RemoveUnit(new EntityId(4));
        sim.Step();

        Assert.Equal(MissionStatus.Victory, sim.Mission!.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.TargetDestroyed, sim.Mission.GetObservation(1).EndReason);

        // Subsequent destruction of player recon vehicle does not overwrite victory
        sim.World.RemoveUnit(new EntityId(1));
        sim.Step();

        Assert.Equal(MissionStatus.Victory, sim.Mission.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.TargetDestroyed, sim.Mission.GetObservation(1).EndReason);
    }

    [Fact]
    public void FinishedMission_RejectsCommands_AndHaltsSystems()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Start player recon moving
        sim.TryEnqueue(1, new MoveCommand(1, 0, new EntityId(1), new Vector2(100f, 100f)));
        sim.Step();
        sim.Step();

        // Finish mission
        sim.World.RemoveUnit(new EntityId(4));
        sim.Step();
        Assert.True(sim.IsMissionFinished);

        // Subsequent commands must be rejected
        bool queued = sim.TryEnqueue(1, new MoveCommand(2, 0, new EntityId(1), new Vector2(120f, 120f)));
        Assert.False(queued);

        // Step again: moving units must be halted
        sim.Step();
        sim.World.TryGetUnit(new EntityId(1), out UnitState reconUnit);
        Assert.False(reconUnit.Moving);
    }

    [Fact]
    public void Bot_DecisionsRateLimitedTo10Ticks_AndDeterministic()
    {
        Vector2[] waypoints = [new Vector2(250f, 240f), new Vector2(340f, 230f)];
        Vector2 anchor = new(396f, 428f);
        ReconRaidBot bot = new(waypoints, anchor, new EntityId(5), new EntityId(6), new EntityId(7));

        TestBotSink sink = new();
        PlayerKnowledge knowledge = new(2, 16);

        // Ticks not divisible by 10 should produce zero commands
        for (int t = 1; t <= 9; t++)
        {
            bot.Update(knowledge, t, sink);
            Assert.Empty(sink.Moves);
            Assert.Empty(sink.Attacks);
        }

        // Ticks divisible by 10 are processed
        bot.Update(knowledge, 10, sink);
        // Even if empty knowledge, rate limiting allowed the check
        for (int t = 11; t <= 19; t++)
        {
            bot.Update(knowledge, t, sink);
            Assert.Empty(sink.Moves);
            Assert.Empty(sink.Attacks);
        }
    }

    [Fact]
    public void Bot_Privacy_HiddenStateChangeDoesNotAffectCommands()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation simA = ReconRaidScenario.CreateSimulation(geometry);
        Simulation simB = ReconRaidScenario.CreateSimulation(geometry);

        // In simA, player unit (id 1) is at default (70, 70)
        // In simB, move hidden player unit (id 1) to (90, 90) - still far in fog of war for the bot
        simB.World.SetPosition(new EntityId(1), new Vector2(90f, 90f));

        PlayerKnowledge botKnowledgeA = new(2, 16);
        PlayerKnowledge botKnowledgeB = new(2, 16);

        simA.UpdateKnowledge(botKnowledgeA);
        simB.UpdateKnowledge(botKnowledgeB);

        // Bot knows nothing about player (0 contacts) in both simulations
        Assert.True(botKnowledgeA.Contacts.IsEmpty);
        Assert.True(botKnowledgeB.Contacts.IsEmpty);

        Vector2[] waypoints = [new Vector2(250f, 240f), new Vector2(340f, 230f)];
        Vector2 anchor = new(396f, 428f);

        ReconRaidBot botA = new(waypoints, anchor, new EntityId(5), new EntityId(6), new EntityId(7));
        TestBotSink sinkA = new();
        botA.Update(botKnowledgeA, 10, sinkA);

        ReconRaidBot botB = new(waypoints, anchor, new EntityId(5), new EntityId(6), new EntityId(7));
        TestBotSink sinkB = new();
        botB.Update(botKnowledgeB, 10, sinkB);

        // Both bots should produce identical commands regardless of external world state
        Assert.Equal(sinkA.Moves.Count, sinkB.Moves.Count);
        Assert.Equal(sinkA.Attacks.Count, sinkB.Attacks.Count);
        for (int i = 0; i < sinkA.Moves.Count; i++)
        {
            Assert.Equal(sinkA.Moves[i].Target, sinkB.Moves[i].Target);
        }
    }

    [Fact]
    public void MissionObservation_Privacy_NoInternalEntityIdOrExactCoordinates()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        MissionObservation obs = sim.Mission!.GetObservation(1);

        // Initial observation before target detection
        Assert.False(obs.IsTargetIdentified);
        Assert.Equal(default, obs.TargetContactId);
        Assert.Equal(new Vector2(420f, 420f), obs.SearchAreaCenter);
        Assert.Equal(70f, obs.SearchAreaRadius);

        // Target actual coordinate is (435, 432), not 420, 420
        sim.World.TryGetUnit(new EntityId(4), out UnitState cmdUnit);
        Assert.NotEqual(obs.SearchAreaCenter, cmdUnit.Position);

        // Verify wire serialization and deserialization
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        int size = WireProtocol.WriteSnapshot(buffer, 1, sim.Tick, [], [], [], obs);

        ClientReplica replica = new();
        bool ok = replica.TryApply(buffer.AsSpan(0, size));
        Assert.True(ok);

        Assert.Equal(obs.MissionId, replica.Mission.MissionId);
        Assert.False(replica.Mission.IsTargetIdentified);
        Assert.Equal(default, replica.Mission.TargetContactId);
    }

    [Fact]
    public void DurableDelivery_MissedSnapshotFrameStillDeliversResult()
    {
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        MissionObservation victoryObs = new(
            MissionId: 1,
            Status: MissionStatus.Victory,
            EndReason: MissionEndReason.TargetDestroyed,
            TimeLimitTicks: 14400,
            FinishedTick: 100,
            TargetContactId: default,
            SearchAreaCenter: new Vector2(420f, 420f),
            SearchAreaRadius: 70f,
            Flags: MissionObservation.FlagTargetIdentified | MissionObservation.FlagTargetDestroyed);

        // Simulate frame skipped: sequence jumps from 1 to 5
        int size = WireProtocol.WriteSnapshot(buffer, 5, 100, [], [], [], victoryObs);

        ClientReplica replica = new();
        Assert.True(replica.TryApply(buffer.AsSpan(0, size)));

        Assert.Equal(MissionStatus.Victory, replica.Mission.Status);
        Assert.Equal(MissionEndReason.TargetDestroyed, replica.Mission.EndReason);
    }

    [Fact]
    public void ReservedBotSlot_RejectsClientHandshake()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        MatchSetup setup = ReconRaidScenario.CreateMatchSetup(geometry);

        // Dedicated server transport for recon-raid with maxPlayers: 1
        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;

        using SimulationHost host = new(
            ReconRaidScenario.CreateSimulation(geometry),
            serverTransport,
            setup,
            reservedBotPlayer: 2);

        // First client connects to player 1
        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);

        for (int i = 0; i < 50 && !client1.IsConnected; i++)
        {
            host.Advance(0.05);
            client1.Advance(0.05);
            Thread.Sleep(5);
        }

        Assert.True(client1.IsConnected);
        Assert.Equal(1, client1.AssignedPlayerId);

        // Second client attempts to connect
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);
        for (int i = 0; i < 20; i++)
        {
            host.Advance(0.05);
            client2.Advance(0.05);
            Thread.Sleep(5);
        }

        // Server is configured for 1 player and slot 2 is reserved for bot, so client 2 cannot connect
        Assert.False(client2.IsConnected);
    }

    [Fact]
    public void LocalRetry_ProvidesCleanState_AndReleasesResources()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();

        for (int restart = 0; restart < 5; restart++)
        {
            using (LocalSession session = new(ReconRaidScenario.CreateSimulation(geometry)))
            {
                Assert.Equal(0, session.Tick);
                Assert.Equal(MissionStatus.InProgress, session.Mission.Status);
                Assert.Equal(3, session.Replica.OwnUnits.Length);

                // Run a few ticks
                for (int i = 0; i < 10; i++)
                {
                    session.Advance(0.05);
                }

                Assert.Equal(10, session.Tick);
            }
        }
    }

    [Fact]
    public void ZeroSteadyStateAllocations_DuringTicks()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        PlayerKnowledge knowledge1 = new(1, 16);
        PlayerKnowledge knowledge2 = new(2, 16);

        // Warm up JIT and initial buffers
        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            sim.UpdateKnowledge(knowledge1);
            sim.UpdateKnowledge(knowledge2);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            sim.Step();
            sim.UpdateKnowledge(knowledge1);
            sim.UpdateKnowledge(knowledge2);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void EndToEndVictory_ThroughNormalCommandsAndCombat()
    {
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();

        Simulation sim = ReconRaidScenario.CreateSimulation(geometry, catalog);

        // Remove patrolling units and guard to isolate direct combat on the target
        sim.World.RemoveUnit(new EntityId(5));
        sim.World.RemoveUnit(new EntityId(6));
        sim.World.RemoveUnit(new EntityId(7));

        // Position player units within engagement range of command vehicle [435, 432]
        // Place player recon at 415, 432 and player tanks at 410, 432
        sim.World.SetPosition(new EntityId(1), new Vector2(415f, 432f));
        sim.World.SetPosition(new EntityId(2), new Vector2(410f, 432f));
        sim.World.SetPosition(new EntityId(3), new Vector2(410f, 430f));

        // Step simulation to trigger detection of command vehicle
        PlayerKnowledge playerKnowledge = new(1, 16);
        ContactId targetContact = default;

        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            sim.UpdateKnowledge(playerKnowledge);

            MissionObservation obs = sim.Mission!.GetObservation(1);
            if (obs.IsTargetIdentified)
            {
                targetContact = obs.TargetContactId;
                break;
            }
        }

        Assert.NotEqual(default, targetContact);

        // Issue normal attack commands to player tanks against target contact
        bool attack1Queued = sim.TryEnqueue(1, new AttackCommand(1, 0, new EntityId(2), targetContact));
        bool attack2Queued = sim.TryEnqueue(1, new AttackCommand(2, 0, new EntityId(3), targetContact));
        Assert.True(attack1Queued);
        Assert.True(attack2Queued);

        // Step simulation until command vehicle is destroyed by tank fire
        bool won = false;
        for (int i = 0; i < 150; i++)
        {
            sim.Step();
            sim.UpdateKnowledge(playerKnowledge);
            if (sim.Mission!.GetObservation(1).Status == MissionStatus.Victory)
            {
                won = true;
                break;
            }
        }

        Assert.True(won);
        Assert.Equal(MissionStatus.Victory, sim.Mission!.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.TargetDestroyed, sim.Mission.GetObservation(1).EndReason);
    }

    [Fact]
    public void DeterministicSimulation_UnderDifferentFramePacing()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();

        // Run simulation at 30 FPS client delta (accumulates 1 tick or 0 ticks per frame)
        Simulation sim30 = ReconRaidScenario.CreateSimulation(geometry);
        LocalSession session30 = new(sim30);

        // Run simulation at 120 FPS client delta
        Simulation sim120 = ReconRaidScenario.CreateSimulation(geometry);
        LocalSession session120 = new(sim120);

        // Advance both for exactly 2.0 seconds of elapsed wall time (40 simulation ticks)
        for (int i = 0; i < 60; i++)
        {
            session30.Advance(2.0 / 60.0);
        }

        for (int i = 0; i < 240; i++)
        {
            session120.Advance(2.0 / 240.0);
        }

        Assert.Equal(session30.Tick, session120.Tick);
        Assert.Equal(session30.Mission.Status, session120.Mission.Status);

        session30.Dispose();
        session120.Dispose();
    }

    [Fact]
    public void Review_HandshakeAndWireProtocol_AgreeOnCurrentVersion()
    {
        Assert.Equal(9, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(MatchSetup.CurrentProtocolVersion, WireProtocol.Version);
    }

    [Fact]
    public void Review_ClientAdvertisingCurrentWireVersion_IsAccepted()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup serverSetup = PrototypeScenario.CreateMatchSetup(map);
        Assert.Equal(9, serverSetup.ProtocolVersion);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;
        using SimulationHost host = new(PrototypeScenario.Create(map), serverTransport, serverSetup);

        MatchSetup currentClientSetup = serverSetup with { ProtocolVersion = WireProtocol.Version };
        using ClientSession client = ClientSession.Connect("127.0.0.1", port, currentClientSetup);

        for (int i = 0; i < 50 && !client.IsConnected; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        Assert.True(client.IsConnected);
        Assert.Equal(SessionConnectionState.Connected, client.State);
    }

    [Fact]
    public void Review_FinishedMission_DoesNotContinueReloading()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Destroy command vehicle to trigger victory
        sim.World.RemoveUnit(new EntityId(4));
        sim.Step();

        Assert.True(sim.IsMissionFinished);
        Assert.Equal(MissionStatus.Victory, sim.Mission!.GetObservation(1).Status);

        // Give player tank 2 an active reload timer of 20 ticks
        Assert.True(sim.World.TryGetCombatState(new EntityId(2), out WorldState.CombatState currentCombat));
        sim.World.SetCombatState(new EntityId(2), currentCombat with
        {
            ReloadTicksRemaining = 20,
            Status = AttackExecutionStatus.Aiming
        });

        // Step simulation for 30 ticks after finish
        for (int i = 0; i < 30; i++)
        {
            sim.Step();
        }

        // Gameplay rules are frozen, so reload timer must not have decreased
        sim.World.TryGetCombatState(new EntityId(2), out WorldState.CombatState combatAfter);
        Assert.Equal(20, combatAfter.ReloadTicksRemaining);
        Assert.Equal(MissionStatus.Victory, sim.Mission.GetObservation(1).Status);
    }

    [Fact]
    public void Review_Bot_UsesActualWeaponRangeFromCatalog()
    {
        using Stream stream = typeof(PrototypeScenario).Assembly.GetManifestResourceStream("DeadGround.Units.json")!;
        using StreamReader reader = new(stream);
        string defaultCatalogJson = reader.ReadToEnd();
        string customCatalogJson = defaultCatalogJson.Replace("\"range\": 60.0", "\"range\": 30.0");
        UnitCatalog customCatalog = PrototypeScenario.LoadCatalog(customCatalogJson);
        ushort tankDefIdx = customCatalog.RequireDefinitionIndex("prototype_tank");

        Vector2[] waypoints = [new Vector2(100f, 100f), new Vector2(200f, 200f)];
        ReconRaidBot bot = new(
            waypoints,
            guardAnchor: new Vector2(300f, 300f),
            guardTankId: new EntityId(5),
            patrolTankId: new EntityId(6),
            patrolReconId: new EntityId(7),
            customCatalog);

        Assert.Equal(30.0f, bot.GetWeaponRange(tankDefIdx));

        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry, customCatalog);

        // Position enemy tank (2) 35m away from patrol tank (6 at [250, 240])
        sim.World.SetPosition(new EntityId(6), new Vector2(250f, 240f));
        sim.World.SetPosition(new EntityId(2), new Vector2(285f, 240f)); // 35m away

        TestBotSink sink = new();
        PlayerKnowledge knowledge = new(2, 16);

        // Step simulation to establish contact
        for (int i = 0; i < 10; i++)
        {
            sim.Step();
        }
        sim.UpdateKnowledge(knowledge);

        bot.Update(knowledge, 10, sink);

        // At 35m (> 28m approach threshold for 30m gun), bot moves tank 6 towards target to approach
        MoveCommand tankMove = Assert.Single(sink.Moves, m => m.Unit.Value == 6);
        Assert.Equal(new Vector2(285f, 240f), tankMove.Target);

        // Now move enemy tank to 20m away (<= 28m approach threshold)
        sim.World.SetPosition(new EntityId(2), new Vector2(270f, 240f)); // 20m away
        for (int i = 0; i < 4; i++)
        {
            sim.Step();
        }
        sim.UpdateKnowledge(knowledge);

        sink.Moves.Clear();
        sink.Attacks.Clear();

        bot.Update(knowledge, 20, sink);

        // At 20m (<= 28m), bot attacks target!
        AttackCommand tankAttack = Assert.Single(sink.Attacks, a => a.Unit.Value == 6);
        Assert.NotEqual(default, tankAttack.Target);
    }

    [Fact]
    public void Review_TargetIdentity_SurvivesLossToLastKnown()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Place player recon right next to command vehicle [435, 432]
        sim.World.SetPosition(new EntityId(1), new Vector2(430f, 432f));

        PlayerKnowledge knowledge = new(1, 16);

        // Step simulation for 40 ticks so recon identifies command vehicle as Current contact
        for (int i = 0; i < 40; i++)
        {
            sim.Step();
        }
        sim.UpdateKnowledge(knowledge);

        MissionObservation obs = sim.Mission!.GetObservation(1);
        Assert.True(obs.IsTargetIdentified);
        Assert.NotEqual(default, obs.TargetContactId);
        ContactId identifiedContactId = obs.TargetContactId;

        // Teleport recon far away so line of sight is lost
        sim.World.SetPosition(new EntityId(1), new Vector2(100f, 100f));

        // Step simulation to trigger loss of contact -> transitions to LastKnown
        sim.Step();
        sim.Step();
        sim.UpdateKnowledge(knowledge);

        // Target identity and ContactId must survive transition to LastKnown
        MissionObservation obsAfterLoss = sim.Mission.GetObservation(1);
        Assert.True(obsAfterLoss.IsTargetIdentified);
        Assert.Equal(identifiedContactId, obsAfterLoss.TargetContactId);
    }

    [Fact]
    public void Review_PatrolUnits_DoNotParkAtCompletedWaypoint()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Unmodified mission with no human movement
        EntityId tankId = new(6);
        EntityId scoutId = new(7);

        Vector2 lastTankPos = default;
        Vector2 lastScoutPos = default;
        int tankStationaryStreak = 0;
        int scoutStationaryStreak = 0;
        int maxTankStationaryStreak = 0;
        int maxScoutStationaryStreak = 0;

        // Run for 1200 ticks (60 seconds) - multiple full patrol loops with unequal speeds
        for (int tick = 1; tick <= 1200; tick++)
        {
            sim.Step();

            if (sim.World.TryGetUnit(tankId, out UnitState tank))
            {
                if (Vector2.Distance(tank.Position, lastTankPos) < 0.05f)
                    tankStationaryStreak++;
                else
                    tankStationaryStreak = 0;

                maxTankStationaryStreak = Math.Max(maxTankStationaryStreak, tankStationaryStreak);
                lastTankPos = tank.Position;
            }

            if (sim.World.TryGetUnit(scoutId, out UnitState scout))
            {
                if (Vector2.Distance(scout.Position, lastScoutPos) < 0.05f)
                    scoutStationaryStreak++;
                else
                    scoutStationaryStreak = 0;

                maxScoutStationaryStreak = Math.Max(maxScoutStationaryStreak, scoutStationaryStreak);
                lastScoutPos = scout.Position;
            }
        }

        // Stationary streak must not exceed reasonable decision and turning ticks
        Assert.True(maxTankStationaryStreak < 60, $"Tank parked for {maxTankStationaryStreak} ticks");
        Assert.True(maxScoutStationaryStreak < 60, $"Scout parked for {maxScoutStationaryStreak} ticks");
    }

    [Fact]
    public void Review_MissedFirstTerminalSnapshot_DeliversSameResult()
    {
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        MissionObservation termObs = new(
            MissionId: 1,
            Status: MissionStatus.Victory,
            EndReason: MissionEndReason.TargetDestroyed,
            TimeLimitTicks: 14400,
            FinishedTick: 85,
            TargetContactId: new ContactId(42),
            SearchAreaCenter: new Vector2(420f, 420f),
            SearchAreaRadius: 70f,
            Flags: MissionObservation.FlagTargetIdentified | MissionObservation.FlagTargetDestroyed);

        // Snapshot received 65 ticks after finish
        int size = WireProtocol.WriteSnapshot(buffer, 10, 150, [], [], [], termObs);

        ClientReplica replica = new();
        Assert.True(replica.TryApply(buffer.AsSpan(0, size)));

        Assert.Equal(MissionStatus.Victory, replica.Mission.Status);
        Assert.Equal(MissionEndReason.TargetDestroyed, replica.Mission.EndReason);
        Assert.Equal(85, replica.Mission.FinishedTick);
        Assert.True(replica.Mission.IsTargetDestroyed);
    }

    [Fact]
    public void Review_ProbingTargetWithPlayerTank_DoesNotIdentifyTargetPrematurely()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Place one extra player-1 tank (not the recon) at (412, 445), at the open west side of the compound, with ammo = 0
        ushort tankDef = sim.World.GetUnitAt(1).Definition;
        sim.World.CreateUnit(1, tankDef, new Vector2(412f, 445f), 5.0f, 120, 0);

        PlayerKnowledge knowledge = new(1, 16);

        // After 6 ticks, side 1 has contacts (guard tank and command vehicle)
        for (int i = 0; i < 6; i++)
        {
            sim.Step();
        }
        sim.UpdateKnowledge(knowledge);

        // Contacts are detected (current), but classification remains Unknown
        Assert.True(knowledge.ContactCount >= 2);
        for (int i = 0; i < knowledge.ContactCount; i++)
        {
            Assert.Equal(DetectionLevel.Detected, knowledge.Contacts[i].Level);
            Assert.Equal(ContactClassification.Unknown, knowledge.Contacts[i].Classification);
        }

        // Mission observation must not report TargetContactId or FlagTargetIdentified
        MissionObservation obs = sim.Mission!.GetObservation(1);
        Assert.False(obs.IsTargetIdentified);
        Assert.Equal(default, obs.TargetContactId);
    }

    [Fact]
    public void ReconVehicle_EarnsIdentification_AfterContinuousLosForIdentifyTicks()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Position player recon vehicle (id 1) within identify range of command vehicle (435, 432)
        sim.World.SetPosition(new EntityId(1), new Vector2(430f, 432f)); // 5m away, inside 60m identify range

        PlayerKnowledge knowledge = new(1, 16);

        // At tick 30 (before 40 ticks), target is detected but not yet identified
        for (int i = 0; i < 30; i++)
        {
            sim.Step();
        }
        sim.UpdateKnowledge(knowledge);
        MissionObservation obsBefore = sim.Mission!.GetObservation(1);
        Assert.False(obsBefore.IsTargetIdentified);

        // At tick 40, continuous LOS inside identify range completes
        for (int i = 30; i < 40; i++)
        {
            sim.Step();
        }
        sim.UpdateKnowledge(knowledge);
        MissionObservation obsAfter = sim.Mission.GetObservation(1);
        Assert.True(obsAfter.IsTargetIdentified);
        Assert.NotEqual(default, obsAfter.TargetContactId);

        // The contact itself has Identified level and CommandVehicle classification
        ContactObservation[] contactsArr = knowledge.Contacts.ToArray();
        ContactObservation targetContact = Assert.Single(contactsArr, c => c.Id == obsAfter.TargetContactId);
        Assert.Equal(DetectionLevel.Identified, targetContact.Level);
        Assert.Equal(ContactClassification.CommandVehicle, targetContact.Classification);
    }

    [Fact]
    public void InterruptedLineOfSight_ResetsIdentificationProgress()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Position player recon near command vehicle
        sim.World.SetPosition(new EntityId(1), new Vector2(430f, 432f));

        // Advance for 20 ticks (halfway to 40 ticks)
        for (int i = 0; i < 20; i++)
        {
            sim.Step();
        }

        // Teleport far away to break line of sight for 10 ticks
        sim.World.SetPosition(new EntityId(1), new Vector2(100f, 100f));
        for (int i = 0; i < 10; i++)
        {
            sim.Step();
        }

        // Return to visual contact
        sim.World.SetPosition(new EntityId(1), new Vector2(430f, 432f));

        // Advance another 20 ticks (interrupted observation requires fresh 40 ticks)
        for (int i = 0; i < 20; i++)
        {
            sim.Step();
        }

        PlayerKnowledge knowledge = new(1, 16);
        sim.UpdateKnowledge(knowledge);
        MissionObservation obs = sim.Mission!.GetObservation(1);

        // Must not be identified yet because prior progress was reset
        Assert.False(obs.IsTargetIdentified);
    }

    [Fact]
    public void ReCorrelation_ResetsLevelToDetected_AndClassificationToUnknown()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        Simulation sim = ReconRaidScenario.CreateSimulation(geometry);

        // Position recon to earn identification of command vehicle
        sim.World.SetPosition(new EntityId(1), new Vector2(430f, 432f));
        for (int i = 0; i < 40; i++)
        {
            sim.Step();
        }

        PlayerKnowledge knowledge = new(1, 16);
        sim.UpdateKnowledge(knowledge);
        Assert.True(sim.Mission!.GetObservation(1).IsTargetIdentified);

        // Break contact so it transitions to LastKnown
        sim.World.SetPosition(new EntityId(1), new Vector2(100f, 100f));
        sim.Step();
        sim.Step();
        sim.UpdateKnowledge(knowledge);

        ContactObservation lastKnown = Assert.Single(knowledge.Contacts.ToArray(), c => c.State == ContactState.LastKnown && c.Classification == ContactClassification.CommandVehicle);
        Assert.Equal(DetectionLevel.Identified, lastKnown.Level);

        // Re-observe target within correlation gate
        sim.World.SetPosition(new EntityId(1), new Vector2(430f, 432f));
        sim.Step();
        sim.UpdateKnowledge(knowledge);

        // Contact correlated back to live track: level resets to Detected, classification resets to Unknown
        ContactObservation correlated = Assert.Single(knowledge.Contacts.ToArray(), c => c.Id == lastKnown.Id);
        Assert.Equal(ContactState.Current, correlated.State);
        Assert.Equal(DetectionLevel.Detected, correlated.Level);
        Assert.Equal(ContactClassification.Unknown, correlated.Classification);
    }

    [Fact]
    public void BotKnowledge_UpdatedOnlyOnDecisionIntervalTicks()
    {
        StaticGeometry geometry = ReconRaidScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        TestBotSink sink = new();
        Vector2[] patrolWaypoints = [new Vector2(250f, 240f), new Vector2(340f, 230f)];
        Vector2 guardAnchor = new(396f, 428f);
        ReconRaidBot bot = new(patrolWaypoints, guardAnchor, new EntityId(5), new EntityId(6), new EntityId(7), catalog);

        OpsMovementCatalog movCatalog = new(catalog);
        OpsPerceptionCatalog perCatalog = new(catalog);
        OpsCombatCatalog comCatalog = new(catalog);

        NavigationSystem nav = new(new NavigationGrid(geometry, NavigationTuning.DefaultCellSize, enableHierarchical: true), movCatalog);
        DetectionSystem det = new(geometry, perCatalog, PrototypeScenario.CreateDetectionTuning(catalog));
        CombatSystem combat = new(geometry, comCatalog, perCatalog, det, nav);

        ReconRaidMissionSystem mission = new(
            new EntityId(1),
            new EntityId(2),
            new EntityId(3),
            new EntityId(4),
            new Vector2(420f, 420f),
            70f,
            det,
            nav,
            combat,
            bot,
            sink,
            timeLimitTicks: 1000);

        WorldState world = new(geometry.HeightMap, 32);
        ushort reconIdx = catalog.RequireDefinitionIndex("recon_vehicle");
        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
        world.CreateUnit(1, reconIdx, new Vector2(70f, 70f), 7f, 60, 0);
        world.CreateUnit(1, tankIdx, new Vector2(60f, 80f), 5f, 120, 10);
        world.CreateUnit(1, tankIdx, new Vector2(80f, 60f), 5f, 120, 10);
        world.CreateUnit(2, reconIdx, new Vector2(435f, 432f), 7f, 80, 0);

        // Step mission for 100 ticks (tick 1 to 100)
        for (int t = 1; t <= 100; t++)
        {
            mission.Update(world, t);
        }

        // Ticks divisible by 10 are: 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 (exactly 10 calls)
        Assert.Equal(10, sink.KnowledgeUpdateCount);
    }

    [Fact]
    public void SimulationHost_DecoupledFromScenarioName_AndAdmitsReservedBot()
    {
        HeightMap map = HeightMap.CreatePrototype();
        MatchSetup customSetup = new(MatchSetup.CurrentProtocolVersion, 1234UL, 5678UL, "CustomRuleSet");

        using LiteNetLibServerTransport transport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = transport.Port;

        Simulation sim = PrototypeScenario.Create(map);
        using SimulationHost host = new(sim, transport, customSetup, reservedBotPlayer: 2);

        // Player 2 is admitted on simulation because reservedBotPlayer: 2 was passed explicitly
        Assert.True(sim.IsPlayerAdmitted(2));

        // Unauthorized client attempting to connect to player 2 is rejected
        using ClientSession client = ClientSession.Connect("127.0.0.1", port, customSetup);
        for (int i = 0; i < 50 && !client.IsConnected; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        Assert.True(client.IsConnected);
        Assert.Equal(1, client.AssignedPlayerId);
    }

    [Fact]
    public void Bot_ReactsToTookFireFromUnseenShooter_AndAcknowledgesEvents()
    {
        Vector2[] waypoints = [new Vector2(250f, 240f), new Vector2(340f, 230f)];
        Vector2 guardAnchor = new(396f, 428f);
        EntityId guardTankId = new(1);
        EntityId patrolTankId = new(2);
        EntityId patrolReconId = new(3);

        ReconRaidBot bot = new(waypoints, guardAnchor, guardTankId, patrolTankId, patrolReconId);
        TestBotSink sink = new();

        PlayerKnowledge knowledge = new(2, 16);
        HeightMap map = ReconRaidScenario.CreateMap();
        WorldState world = new(map, 16);
        world.CreateUnit(2, 0, guardAnchor, 5.0f, 120, 10); // Guard tank at anchor -> Id will be 1
        world.SetCombatState(guardTankId, new WorldState.CombatState(120, 120, 10, 0, false, default, default, AttackExecutionStatus.None));

        // Inject TookFire event from unseen shooter at [396, 380] targeting guard tank at [396, 428]
        CombatEventObservation tookFire = new(
            EventId: 1,
            ServerTick: 10,
            Kind: CombatEventKind.TookFire,
            PresentationId: 1,
            Origin: new Vector3(396f, 0f, 380f),
            Target: new Vector3(396f, 0f, 428f),
            ShooterUnitId: default);

        knowledge.Refresh(world, [], null, [tookFire]);

        Assert.Equal(1, knowledge.EventCount);

        bot.Update(knowledge, 10, sink);

        // Bot should enqueue a move order toward incoming fire origin (south, towards Z=380)
        MoveCommand reactionMove = Assert.Single(sink.Moves, m => m.Unit == guardTankId);
        Assert.True(reactionMove.Target.Y < guardAnchor.Y);

        // Bot should have acknowledged the consumed event
        Assert.Equal(1, sink.AcknowledgedEventsCount);
    }
}
