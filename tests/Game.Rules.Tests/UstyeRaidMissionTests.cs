using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class UstyeRaidMissionTests
{
    private static readonly StaticGeometry SharedGeometry = UstyeRaidScenario.CreateStaticGeometry();
    private static readonly UnitCatalog SharedCatalog = ScenarioContent.LoadCatalog("ustye-raid");
    private static readonly NavigationGrid SharedNavGrid = new(
        SharedGeometry,
        UstyeGroundScenario.CreateNavigationTuning().CellSize,
        enableHierarchical: true,
        buildLimits: UstyeGroundScenario.CreateNavigationBuildLimits(),
        sectorSize: UstyeGroundScenario.CreateNavigationTuning().SectorSize);

    private static Simulation CreateSimulation(int timeLimitTicks = UstyeRaidScenario.TimeLimitTicks, ulong matchSeed = 1337UL) =>
        UstyeRaidScenario.CreateSimulation(SharedGeometry, SharedCatalog, timeLimitTicks: timeLimitTicks, matchSeed: matchSeed, navGrid: SharedNavGrid);

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
    public void ScenarioSetup_HasValidDimensions_Catalog_AndTeams()
    {
        StaticGeometry geometry = SharedGeometry;
        MatchSetup setup = UstyeRaidScenario.CreateMatchSetup(geometry);

        Assert.Equal("UstyeRaid", setup.RuleSetId);
        Assert.NotNull(setup.PlayerTeams);
        Assert.Equal(1, setup.PlayerTeams[0]);
        Assert.Equal(2, setup.PlayerTeams[1]);
        Assert.NotNull(setup.PlayerFactions);
        Assert.Equal("soviet", setup.PlayerFactions[0]);
        Assert.Equal("us", setup.PlayerFactions[1]);

        Assert.Equal(5000f, geometry.HeightMap.Width);
        Assert.Equal(5000f, geometry.HeightMap.Depth);
    }

    [Fact]
    public void InitialForces_MatchHistoricalOrderOfBattle_AndAmmunition()
    {
        UnitCatalog catalog = SharedCatalog;
        Simulation sim = CreateSimulation();

        // Player (Soviet): 1x BRDM-2, 2x T-62
        Assert.True(sim.World.TryGetUnit(new EntityId(1), out UnitState brdm));
        Assert.True(sim.World.TryGetCombatState(new EntityId(1), out WorldState.CombatState brdmCombat));
        Assert.Equal(1, brdm.Owner);
        Assert.Equal(catalog.RequireDefinitionIndex("brdm2"), brdm.Definition);
        Assert.Equal(catalog.RequireDefinition("brdm2").MaxHealth, brdmCombat.MaxHealth);
        Assert.Equal(catalog.GetAmmoCapacity(catalog.RequireDefinition("brdm2")), brdmCombat.Ammunition);

        Assert.True(sim.World.TryGetUnit(new EntityId(2), out UnitState t62A));
        Assert.True(sim.World.TryGetCombatState(new EntityId(2), out WorldState.CombatState t62ACombat));
        Assert.Equal(1, t62A.Owner);
        Assert.Equal(catalog.RequireDefinitionIndex("t62"), t62A.Definition);
        Assert.Equal(catalog.GetAmmoCapacity(catalog.RequireDefinition("t62")), t62ACombat.Ammunition);

        Assert.True(sim.World.TryGetUnit(new EntityId(3), out UnitState t62B));
        Assert.True(sim.World.TryGetCombatState(new EntityId(3), out WorldState.CombatState t62BCombat));
        Assert.Equal(1, t62B.Owner);
        Assert.Equal(catalog.RequireDefinitionIndex("t62"), t62B.Definition);
        Assert.Equal(catalog.GetAmmoCapacity(catalog.RequireDefinition("t62")), t62BCombat.Ammunition);

        // Enemy (US): 1x M577 (cmd), 1x M60A1 (guard), 1x M60A1 (patrol), 1x M151A2 (recon)
        Assert.True(sim.World.TryGetUnit(new EntityId(4), out UnitState m577));
        Assert.True(sim.World.TryGetCombatState(new EntityId(4), out WorldState.CombatState m577Combat));
        Assert.Equal(2, m577.Owner);
        Assert.Equal(catalog.RequireDefinitionIndex("m577"), m577.Definition);
        Assert.Equal(0, m577Combat.Ammunition); // Unarmed command post vehicle

        Assert.True(sim.World.TryGetUnit(new EntityId(5), out UnitState guardM60));
        Assert.True(sim.World.TryGetCombatState(new EntityId(5), out WorldState.CombatState guardM60Combat));
        Assert.Equal(2, guardM60.Owner);
        Assert.Equal(catalog.RequireDefinitionIndex("m60a1"), guardM60.Definition);
        Assert.True(guardM60Combat.Ammunition > 0);

        Assert.True(sim.World.TryGetUnit(new EntityId(6), out UnitState patrolM60));
        Assert.True(sim.World.TryGetCombatState(new EntityId(6), out WorldState.CombatState patrolM60Combat));
        Assert.Equal(2, patrolM60.Owner);
        Assert.Equal(catalog.RequireDefinitionIndex("m60a1"), patrolM60.Definition);
        Assert.True(patrolM60Combat.Ammunition > 0);

        Assert.True(sim.World.TryGetUnit(new EntityId(7), out UnitState patrolRecon));
        Assert.True(sim.World.TryGetCombatState(new EntityId(7), out WorldState.CombatState patrolReconCombat));
        Assert.Equal(2, patrolRecon.Owner);
        Assert.Equal(catalog.RequireDefinitionIndex("m151a2"), patrolRecon.Definition);
        Assert.True(patrolReconCombat.Ammunition > 0);
    }

    [Fact]
    public void SpawnPositions_AreOnDryLand_AndClearOfStructures()
    {
        StaticGeometry geometry = SharedGeometry;
        Simulation sim = CreateSimulation();

        for (int i = 0; i < sim.World.Count; i++)
        {
            UnitState u = sim.World.GetUnitAt(i);
            float terrainH = geometry.HeightMap.GetHeight(u.Position.X, u.Position.Y);
            Assert.True(terrainH >= 2.0f, $"Unit {u.Id.Value} is in water (elevation {terrainH:F2} < 2.0m)");

            // Structure clearance
            foreach (OpaqueStructure s in geometry.Structures)
            {
                bool inside = u.Position.X >= s.MinX - 2.0f && u.Position.X <= s.MaxX + 2.0f &&
                              u.Position.Y >= s.MinZ - 2.0f && u.Position.Y <= s.MaxZ + 2.0f;
                Assert.False(inside, $"Unit {u.Id.Value} at ({u.Position.X}, {u.Position.Y}) collides with structure {s.Id.Value}");
            }
        }
    }

    [Fact]
    public void SpawnPositions_HaveZeroDirectLineOfSight_ToEnemyUnits()
    {
        Simulation sim = CreateSimulation();

        sim.Step();

        // Player has zero initial contacts
        Assert.NotNull(sim.Detection);
        ReadOnlySpan<ContactObservation> playerContacts = sim.Detection.GetContacts(1);
        Assert.True(playerContacts.IsEmpty, "Player should have no contacts at spawn due to panel block occlusion.");

        // Enemy has zero initial contacts
        ReadOnlySpan<ContactObservation> enemyContacts = sim.Detection.GetContacts(2);
        Assert.True(enemyContacts.IsEmpty, "Enemy should have no contacts at spawn due to panel block occlusion.");
    }

    [Fact]
    public void Victory_WhenCommandVehicleDestroyed_AndReconAlive()
    {
        Simulation sim = CreateSimulation();

        // Command vehicle is unit 4 (M577)
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
        Simulation sim = CreateSimulation();

        // Player recon is unit 1 (BRDM-2)
        sim.World.RemoveUnit(new EntityId(1));
        sim.Step();

        Assert.True(sim.IsMissionFinished);
        Assert.NotNull(sim.Mission);
        Assert.Equal(MissionStatus.Defeat, sim.Mission.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.ReconVehicleDestroyed, sim.Mission.GetObservation(1).EndReason);
    }

    [Fact]
    public void Defeat_WhenAllArmedUnitsDestroyed()
    {
        Simulation sim = CreateSimulation();

        // Remove tank 1 (unit 2) -> mission continues
        sim.World.RemoveUnit(new EntityId(2));
        sim.Step();
        Assert.False(sim.IsMissionFinished);

        // Remove tank 2 (unit 3) -> both tanks dead, defeat triggers
        sim.World.RemoveUnit(new EntityId(3));
        sim.Step();

        Assert.True(sim.IsMissionFinished);
        Assert.NotNull(sim.Mission);
        Assert.Equal(MissionStatus.Defeat, sim.Mission.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, sim.Mission.GetObservation(1).EndReason);
    }

    [Fact]
    public void Defeat_WhenTimeLimitExpires()
    {
        Simulation sim = CreateSimulation(timeLimitTicks: 15);

        for (int i = 0; i < 14; i++)
        {
            sim.Step();
            Assert.False(sim.IsMissionFinished);
        }

        sim.Step(); // Tick 15: time limit reached
        Assert.True(sim.IsMissionFinished);
        Assert.NotNull(sim.Mission);
        Assert.Equal(MissionStatus.Defeat, sim.Mission.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.TimeLimitExpired, sim.Mission.GetObservation(1).EndReason);
    }

    [Fact]
    public void SimultaneousConditions_DefeatTakesPrecedenceOverVictory()
    {
        // 1. Target destroyed + Recon destroyed in same tick -> Defeat
        {
            Simulation sim = CreateSimulation();

            sim.World.RemoveUnit(new EntityId(4)); // Command vehicle
            sim.World.RemoveUnit(new EntityId(1)); // Player recon
            sim.Step();

            Assert.True(sim.IsMissionFinished);
            Assert.Equal(MissionStatus.Defeat, sim.Mission!.GetObservation(1).Status);
            Assert.Equal(MissionEndReason.ReconVehicleDestroyed, sim.Mission.GetObservation(1).EndReason);
        }

        // 2. Target destroyed + All combat tanks destroyed in same tick -> Defeat
        {
            Simulation sim = CreateSimulation();

            sim.World.RemoveUnit(new EntityId(4)); // Command vehicle
            sim.World.RemoveUnit(new EntityId(2)); // Tank 1
            sim.World.RemoveUnit(new EntityId(3)); // Tank 2
            sim.Step();

            Assert.True(sim.IsMissionFinished);
            Assert.Equal(MissionStatus.Defeat, sim.Mission!.GetObservation(1).Status);
            Assert.Equal(MissionEndReason.AllArmedUnitsDestroyed, sim.Mission.GetObservation(1).EndReason);
        }

        // 3. Target destroyed + Time limit expired in same tick -> Defeat
        {
            Simulation sim = CreateSimulation(timeLimitTicks: 5);

            for (int i = 0; i < 4; i++) sim.Step();

            sim.World.RemoveUnit(new EntityId(4)); // Command vehicle
            sim.Step(); // Tick 5: time expired and target destroyed

            Assert.True(sim.IsMissionFinished);
            Assert.Equal(MissionStatus.Defeat, sim.Mission!.GetObservation(1).Status);
            Assert.Equal(MissionEndReason.TimeLimitExpired, sim.Mission.GetObservation(1).EndReason);
        }
    }

    [Fact]
    public void FinishedMission_RejectsCommands_AndHaltsSystems()
    {
        Simulation sim = CreateSimulation();

        // Start player recon moving
        sim.TryEnqueue(1, new MoveCommand(1, 0, new EntityId(1), new Vector2(2200f, 2320f)));
        sim.Step();
        sim.Step();

        // Finish mission
        sim.World.RemoveUnit(new EntityId(4));
        sim.Step();
        Assert.True(sim.IsMissionFinished);

        // Subsequent commands must be rejected
        bool queued = sim.TryEnqueue(1, new MoveCommand(2, 0, new EntityId(1), new Vector2(2250f, 2320f)));
        Assert.False(queued);

        // Step again: moving units must be halted
        sim.Step();
        sim.World.TryGetUnit(new EntityId(1), out UnitState reconUnit);
        Assert.False(reconUnit.Moving);
    }

    [Fact]
    public void MissionResult_IsImmutable_OnceFinished()
    {
        Simulation sim = CreateSimulation();

        // Trigger victory by destroying command vehicle
        sim.World.RemoveUnit(new EntityId(4));
        sim.Step();

        Assert.True(sim.IsMissionFinished);
        Assert.Equal(MissionStatus.Victory, sim.Mission!.GetObservation(1).Status);
        int finishedTick = sim.Mission.GetObservation(1).FinishedTick;

        // Subsequent unit deaths after finish should NOT alter victory status
        sim.World.RemoveUnit(new EntityId(1));
        sim.World.RemoveUnit(new EntityId(2));
        sim.World.RemoveUnit(new EntityId(3));
        sim.Step();

        Assert.Equal(MissionStatus.Victory, sim.Mission.GetObservation(1).Status);
        Assert.Equal(MissionEndReason.TargetDestroyed, sim.Mission.GetObservation(1).EndReason);
        Assert.Equal(finishedTick, sim.Mission.GetObservation(1).FinishedTick);
    }

    [Fact]
    public void DedicatedServer_ReservesBotPlayerSlot_AndAllowsSingleHuman()
    {
        StaticGeometry geometry = SharedGeometry;
        MatchSetup setup = UstyeRaidScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;

        using SimulationHost host = new(
            CreateSimulation(),
            serverTransport,
            setup,
            reservedBotPlayer: 2);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);

        for (int i = 0; i < 50 && !client1.IsConnected; i++)
        {
            host.Advance(0.05);
            client1.Advance(0.05);
            Thread.Sleep(5);
        }

        Assert.True(client1.IsConnected);
        Assert.Equal(1, client1.AssignedPlayerId);

        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);
        for (int i = 0; i < 20; i++)
        {
            host.Advance(0.05);
            client2.Advance(0.05);
            Thread.Sleep(5);
        }

        Assert.False(client2.IsConnected);
    }

    [Fact]
    public void UnitInitialFacing_OrientsTowardLandmarks()
    {
        Simulation sim = CreateSimulation();

        static Vector2 GetForwardVector(float yaw) => new(-MathF.Sin(yaw), -MathF.Cos(yaw));

        // Player units (BRDM-2, T-62 #1, T-62 #2) must face East towards central avenue (1, 0)
        for (uint id = 1; id <= 3; id++)
        {
            Assert.True(sim.World.TryGetCombatState(new EntityId(id), out WorldState.CombatState combat));
            Vector2 forward = GetForwardVector(combat.HullHeading);
            Assert.True(forward.X > 0.95f, $"Unit {id} forward X ({forward.X:F2}) should face East (> 0.95)");
            Assert.True(MathF.Abs(forward.Y) < 0.2f, $"Unit {id} forward Y ({forward.Y:F2}) should be near 0");
        }

        // Enemy command vehicle (M577, id 4) faces North towards courtyard entrance (0, -1)
        {
            Assert.True(sim.World.TryGetCombatState(new EntityId(4), out WorldState.CombatState cmdCombat));
            Vector2 forward = GetForwardVector(cmdCombat.HullHeading);
            Assert.True(forward.Y < -0.95f, $"Command vehicle forward Y ({forward.Y:F2}) should face North (< -0.95)");
            Assert.True(MathF.Abs(forward.X) < 0.2f, $"Command vehicle forward X ({forward.X:F2}) should be near 0");
        }

        // Enemy guard tank (M60A1, id 5) faces West towards approach road (-1, 0)
        {
            Assert.True(sim.World.TryGetCombatState(new EntityId(5), out WorldState.CombatState guardCombat));
            Vector2 forward = GetForwardVector(guardCombat.HullHeading);
            Assert.True(forward.X < -0.95f, $"Guard tank forward X ({forward.X:F2}) should face West (< -0.95)");
            Assert.True(MathF.Abs(forward.Y) < 0.2f, $"Guard tank forward Y ({forward.Y:F2}) should be near 0");
        }

        // Enemy patrol tank (M60A1, id 6) faces East towards first waypoint (1, 0)
        {
            Assert.True(sim.World.TryGetCombatState(new EntityId(6), out WorldState.CombatState patrolTankCombat));
            Vector2 forward = GetForwardVector(patrolTankCombat.HullHeading);
            Assert.True(forward.X > 0.95f, $"Patrol tank forward X ({forward.X:F2}) should face East (> 0.95)");
            Assert.True(MathF.Abs(forward.Y) < 0.2f, $"Patrol tank forward Y ({forward.Y:F2}) should be near 0");
        }

        // Enemy patrol recon (M151A2, id 7) faces East-Southeast towards first waypoint
        {
            Assert.True(sim.World.TryGetCombatState(new EntityId(7), out WorldState.CombatState patrolReconCombat));
            Vector2 forward = GetForwardVector(patrolReconCombat.HullHeading);
            Assert.True(forward.X > 0.85f, $"Patrol recon forward X ({forward.X:F2}) should face East-Southeast (> 0.85)");
        }
    }

    [Fact]
    public void IdleSimulation_1200Ticks_AdvancesWithoutCrash()
    {
        using LocalSession session = new(CreateSimulation());

        // Advance 1200 ticks (60.0s), which covers tick 712 where bot previously threw IndexOutOfRangeException
        for (int i = 0; i < 1200; i++)
        {
            session.Advance(0.05);
        }

        Assert.Equal(1200, session.Tick);
        Assert.True(session.Mission.Status == MissionStatus.InProgress || session.Mission.Status == MissionStatus.Defeat);
    }

    [Fact]
    public void NorthernBridge_SequentialColumnCrossing_BothTanksTraverseDeckAndReachEastBank()
    {
        Simulation sim = CreateSimulation();
        // Remove ammunition from enemy combat units to isolate bridge navigation behavior
        foreach (uint enemyId in new uint[] { 5, 6, 7 })
        {
            if (sim.World.TryGetCombatState(new EntityId(enemyId), out var cs))
            {
                sim.World.SetCombatState(new EntityId(enemyId), cs with { Ammunition = 0 });
            }
        }

        using LocalSession session = new(sim);

        EntityId brdmId = new(1);
        EntityId tank1 = new(2);
        EntityId tank2 = new(3);

        for (int i = 0; i < 5; i++) session.Advance(0.05);
        Assert.True(session.Replica.OwnUnits.Length >= 3);

        session.SetFireStance(brdmId, FireStance.HoldFire);

        EntityId id1 = tank1;
        EntityId id2 = tank2;

        // Step 1: Move both tanks sequentially in column to line up along northern approach road (Z = 1000)
        Assert.True(session.Move(tank1, new Vector2(2150f, 1000f)));
        Assert.True(session.Move(tank2, new Vector2(2080f, 1000f)));

        Vector2 p1 = Vector2.Zero;
        uint s1 = 0;
        Vector2 p2 = Vector2.Zero;
        uint s2 = 0;

        bool tank1SawDeck = false;
        bool tank2SawDeck = false;
        bool tank1ReachedEast = false;
        bool tank2ReachedEast = false;
        bool tank1OrderedAcross = false;
        bool tank2OrderedAcross = false;

        for (int i = 0; i < 9000; i++)
        {
            session.Advance(0.05);

            if (session.Mission.Status != MissionStatus.InProgress)
            {
                Assert.Fail($"Mission ended at tick {session.Tick} with status {session.Mission.Status}, reason {session.Mission.EndReason}, p1=({p1.X:F1},{p1.Y:F1}) p2=({p2.X:F1},{p2.Y:F1})");
            }

            p1 = session.Replica.SamplePosition(id1, session.Replica.LatestTick);
            s1 = session.Replica.SampleSupportSurface(id1);
            p2 = session.Replica.SamplePosition(id2, session.Replica.LatestTick);
            s2 = session.Replica.SampleSupportSurface(id2);

            if (s1 == 1u) tank1SawDeck = true;
            if (s2 == 1u) tank2SawDeck = true;

            if (p1.X >= 2520f) tank1ReachedEast = true;
            if (p2.X >= 2520f) tank2ReachedEast = true;

            // Step 2: Once lead tank is lined up on approach road at Z = 1000, order lead tank across CROSS-N
            if (!tank1OrderedAcross && p1.X >= 2140f && MathF.Abs(p1.Y - 1000f) < 2f)
            {
                session.Move(tank1, new Vector2(2550f, 1000f));
                tank1OrderedAcross = true;
            }

            // Step 3: Once lead tank clears bridge onto east bank, order trail tank across CROSS-N
            if (tank1ReachedEast && !tank2OrderedAcross)
            {
                session.Move(tank2, new Vector2(2550f, 1000f));
                tank2OrderedAcross = true;
            }

            if (tank1ReachedEast && tank2ReachedEast)
            {
                break;
            }
        }

        Assert.True(tank1SawDeck, $"Tank 1 must traverse northern bridge deck (SupportSurface == 1). LastPos=({p1.X}, {p1.Y}) surf={s1} p2=({p2.X}, {p2.Y}) surf2={s2}");
        Assert.True(tank2SawDeck, "Tank 2 must traverse northern bridge deck (SupportSurface == 1).");
        Assert.True(tank1ReachedEast, "Tank 1 must reach eastern bank (X >= 2520).");
        Assert.True(tank2ReachedEast, "Tank 2 must reach eastern bank (X >= 2520).");
    }

    [Fact]
    public void EndToEndPlaythrough_VictoryWithActiveBot()
    {
        using LocalSession session = new(CreateSimulation());

        EntityId brdmId = new(1);
        EntityId tank1Id = new(2);
        EntityId tank2Id = new(3);

        for (int i = 0; i < 5; i++) session.Advance(0.05);
        Assert.True(session.Replica.OwnUnits.Length >= 3);

        // Recon vehicle holds fire in western cover to ensure preservation
        session.SetFireStance(brdmId, FireStance.ReturnFire);

        // Tanks engage at will
        session.SetFireStance(tank1Id, FireStance.FireAtWill);
        session.SetFireStance(tank2Id, FireStance.FireAtWill);

        // Phase 1: Sequential column move along northern approach corridor (Z = 1000)
        Assert.True(session.Move(tank1Id, new Vector2(2150f, 1000f)));
        Assert.True(session.Move(tank2Id, new Vector2(2080f, 1000f)));
        Assert.True(session.Move(brdmId, new Vector2(2010f, 1000f)));

        bool sawDeck1 = false;
        bool sawDeck2 = false;
        bool crossedNorthLead = false;
        bool crossedNorthTrail = false;
        bool leadOrderedAcross = false;
        bool trailOrderedAcross = false;
        bool advanceSouthOrdered = false;
        bool approachedDepotNorth = false;
        bool depotPushOrdered = false;
        bool enteredDepot = false;
        ContactId lastAttackTarget = default;
        bool wasInCombat = false;

        Vector2 p1 = Vector2.Zero;
        uint s1 = 0;
        Vector2 p2 = Vector2.Zero;
        uint s2 = 0;

        for (int tick = 0; tick < 14000; tick++)
        {
            session.Advance(0.05);

            p1 = session.Replica.SamplePosition(tank1Id, session.Replica.LatestTick);
            s1 = session.Replica.SampleSupportSurface(tank1Id);
            p2 = session.Replica.SamplePosition(tank2Id, session.Replica.LatestTick);
            s2 = session.Replica.SampleSupportSurface(tank2Id);

            if (session.Mission.Status == MissionStatus.Victory)
            {
                break;
            }

            if (session.Mission.Status == MissionStatus.Defeat)
            {
                Assert.Fail($"Mission ended in Defeat at tick {session.Tick}: {session.Mission.EndReason}, p1=({p1.X:F1},{p1.Y:F1}) p2=({p2.X:F1},{p2.Y:F1})");
            }

            if (s1 == 1u) sawDeck1 = true;
            if (s2 == 1u) sawDeck2 = true;

            if (p1.X >= 2520f) crossedNorthLead = true;
            if (p2.X >= 2520f) crossedNorthTrail = true;

            // Tactical engagement: Once both tanks reach eastern bank, engage spotted threats
            ContactId threatContact = default;
            if (crossedNorthLead && crossedNorthTrail)
            {
                if (session.Mission.IsTargetSpotted)
                {
                    threatContact = session.Mission.TargetContactId;
                }
                else
                {
                    float minThreatDistSq = float.MaxValue;
                    for (int c = 0; c < session.Replica.Contacts.Length; c++)
                    {
                        var contact = session.Replica.Contacts[c];
                        if (contact.State == ContactState.Current)
                        {
                            float d1 = Vector2.DistanceSquared(p1, contact.Position);
                            float d2 = Vector2.DistanceSquared(p2, contact.Position);
                            float d = MathF.Min(d1, d2);
                            if (d <= 1600f * 1600f && d < minThreatDistSq)
                            {
                                minThreatDistSq = d;
                                threatContact = contact.Id;
                            }
                        }
                    }
                }
            }

            if (threatContact.Value != 0)
            {
                if (lastAttackTarget.Value != threatContact.Value)
                {
                    session.Attack(tank1Id, threatContact);
                    session.Attack(tank2Id, threatContact);
                    lastAttackTarget = threatContact;
                    wasInCombat = true;
                }
                continue;
            }
            else if (wasInCombat)
            {
                // Threat neutralized; resume movement appropriate for current mission phase
                wasInCombat = false;
                lastAttackTarget = default;
                if (enteredDepot || approachedDepotNorth)
                {
                    session.Move(tank1Id, new Vector2(3490f, 2720f));
                    session.Move(tank2Id, new Vector2(3490f, 2680f));
                }
                else if (crossedNorthLead && crossedNorthTrail)
                {
                    session.Move(tank1Id, new Vector2(3450f, 2050f));
                    session.Move(tank2Id, new Vector2(3300f, 1200f));
                }
            }

            // Phase 2: Order lead tank across CROSS-N once lined up on approach road at Z = 1000
            if (!leadOrderedAcross && p1.X >= 2140f && MathF.Abs(p1.Y - 1000f) < 2f)
            {
                session.Move(tank1Id, new Vector2(2550f, 1000f));
                leadOrderedAcross = true;
            }

            // Phase 3: Order trail tank across CROSS-N once lead tank reaches eastern bank
            if (crossedNorthLead && !trailOrderedAcross)
            {
                session.Move(tank2Id, new Vector2(2550f, 1000f));
                session.Move(tank1Id, new Vector2(2540f, 1000f));
                trailOrderedAcross = true;
            }

            // Phase 4: Advance down east bank towards Depot north approach once both tanks cross
            if (crossedNorthLead && crossedNorthTrail && !advanceSouthOrdered)
            {
                session.Move(tank1Id, new Vector2(3450f, 2050f));
                session.Move(tank2Id, new Vector2(3300f, 1200f));
                advanceSouthOrdered = true;
            }

            if (crossedNorthLead && crossedNorthTrail && !approachedDepotNorth)
            {
                if (p1.Y >= 1800f || p2.Y >= 1800f)
                {
                    approachedDepotNorth = true;
                }
            }

            // Phase 5: Push into Depot courtyard from north
            if (approachedDepotNorth && !depotPushOrdered)
            {
                session.Move(tank1Id, new Vector2(3490f, 2720f));
                session.Move(tank2Id, new Vector2(3490f, 2680f));
                depotPushOrdered = true;
            }

            if (approachedDepotNorth && !enteredDepot)
            {
                if (p1.Y >= 2500f || p2.Y >= 2500f)
                {
                    enteredDepot = true;
                }
            }
        }

        Assert.True(sawDeck1, $"Tank 1 must traverse northern bridge deck (SupportSurface == 1). LastPos=({p1.X:F1},{p1.Y:F1})");
        Assert.True(sawDeck2, $"Tank 2 must traverse northern bridge deck (SupportSurface == 1). LastPos=({p2.X:F1},{p2.Y:F1})");
        Assert.True(crossedNorthLead, "Tank 1 must reach eastern bank (X >= 2520).");
        Assert.True(crossedNorthTrail, "Tank 2 must reach eastern bank (X >= 2520).");
        Assert.True(approachedDepotNorth, "Tanks must approach Depot from north flank.");
        Assert.True(enteredDepot, "Tanks must enter Depot courtyard.");
        Assert.Equal(MissionStatus.Victory, session.Mission.Status);
        Assert.Equal(MissionEndReason.TargetDestroyed, session.Mission.EndReason);
        Assert.True(session.Mission.IsTargetDestroyed);
    }
}
