using System.Numerics;
using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class TankBattleScenarioTests
{
    [Fact]
    public void Scenario_SpawnsOneHundredOfEachHistoricalTankWithFiniteAmmo()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using Simulation sim = TankBattleScenario.CreateSimulation(catalog: catalog);
        Assert.Equal(200, sim.World.Count);
        int soviet = 0, us = 0;
        HashSet<Vector2> positions = [];
        HashSet<int> reloads = [];
        for (int i = 0; i < sim.World.Count; i++)
        {
            UnitState unit = sim.World.GetUnitAt(i);
            UnitDefinition definition = catalog.Units[unit.Definition];
            Assert.Equal(unit.Owner == 1 ? "t62" : "m60a1", definition.Id);
            Assert.True(positions.Add(unit.Position));
            Assert.True(sim.World.Map.Contains(unit.Position));
            Assert.True(sim.World.TryGetCombatState(unit.Id, out WorldState.CombatState combat));
            Assert.Equal(catalog.GetAmmoCapacity(definition), combat.Ammunition);
            reloads.Add(combat.ReloadTicksRemaining);
            if (unit.Owner == 1) soviet++; else us++;
        }
        Assert.Equal(100, soviet);
        Assert.Equal(100, us);
        Assert.True(reloads.Count > 50);
    }

    [Fact]
    public void Autobattle_ProducesMovementShotsAndCasualtiesWithoutRejectedOrders()
    {
        using Simulation sim = TankBattleScenario.CreateSimulation();
        Vector2 initial = sim.World.GetUnitAt(99).Position;
        EntityId lastTank = sim.World.GetUnitAt(99).Id;
        bool moved = false;
        for (int tick = 0; tick < 3600; tick++)
        {
            sim.Step();
            if (sim.World.TryGetUnit(lastTank, out UnitState unit) && unit.Position != initial) moved = true;
            // In production the host acknowledges published events.
            sim.AcknowledgeCombatEvents(1, sim.Combat!.GetEvents(1).Length);
            sim.AcknowledgeCombatEvents(2, sim.Combat.GetEvents(2).Length);
        }
        TankBattleDirector director = ((OpsRules)sim.Rules).FindSystem<TankBattleDirector>()!;
        Assert.True(moved);
        Assert.True(sim.Combat!.TotalShotsFired > 20);
        Assert.True(sim.World.Count < 200);
        Assert.Equal(0, director.RejectedOrders);
    }

    [Fact]
    public void FixedSeed_RestartReproducesCombatAndPositions()
    {
        StaticGeometry geometry = TankBattleScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using Simulation a = TankBattleScenario.CreateSimulation(geometry, catalog, 42);
        using Simulation b = TankBattleScenario.CreateSimulation(geometry, catalog, 42);
        for (int i = 0; i < 1800; i++)
        {
            a.Step(); b.Step();
            a.AcknowledgeCombatEvents(1, a.Combat!.GetEvents(1).Length);
            a.AcknowledgeCombatEvents(2, a.Combat.GetEvents(2).Length);
            b.AcknowledgeCombatEvents(1, b.Combat!.GetEvents(1).Length);
            b.AcknowledgeCombatEvents(2, b.Combat.GetEvents(2).Length);
        }
        Assert.True(a.Combat!.TotalShotsFired > 0);
        Assert.Equal(a.Combat!.TotalShotsFired, b.Combat!.TotalShotsFired);
        Assert.Equal(a.World.Count, b.World.Count);
        for (int i = 0; i < a.World.Count; i++)
        {
            UnitState unit = a.World.GetUnitAt(i);
            Assert.True(b.World.TryGetUnit(unit.Id, out UnitState replay));
            Assert.Equal(unit, replay);
            Assert.True(a.World.TryGetCombatState(unit.Id, out WorldState.CombatState ac));
            Assert.True(b.World.TryGetCombatState(unit.Id, out WorldState.CombatState bc));
            Assert.Equal(ac, bc);
        }
    }

    [Fact]
    public void Bot_AdvancesWithoutObservedContactsAndDoesNotRepeatAcceptedOrder()
    {
        HeightMap map = HeightMap.CreatePrototype();
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(50, 50), 10);
        world.CreateUnit(2, 0, new Vector2(60, 50), 10);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        PlayerKnowledge knowledge = new(1);
        sim.UpdateKnowledge(knowledge);
        TankBattleBot bot = new(1, new StaticGeometry(map, []), ColdWarContent.LoadCatalog());
        RecordingSink sink = new();
        bot.Update(knowledge, 10, sink);
        bot.Update(knowledge, 20, sink);
        Assert.Equal(0, knowledge.ContactCount);
        Assert.Equal(1, sink.MoveCount);
        Assert.Equal(new Vector2(950, 50), sink.LastMove.Target);
        Assert.Throws<ArgumentException>(() => new TankBattleBot(2, new StaticGeometry(map, []), ColdWarContent.LoadCatalog()).Update(knowledge, 20, sink));
    }

    [Fact]
    public void Bot_HaltsForUsableObservedTargetAndHoldsDuringReloadWithoutRepeatedOrders()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        StaticGeometry geometry = TankBattleScenario.CreateStaticGeometry();
        using WorldState world = new(geometry.HeightMap);
        EntityId id = CreateProbeTank(world, catalog);
        PlayerKnowledge knowledge = new(1);
        knowledge.Refresh(world);
        TankBattleBot bot = new(1, geometry, catalog);
        RecordingSink sink = new();
        bot.Update(knowledge, 10, sink);
        Assert.Equal(new Vector2(1400, 1600), sink.LastMove.Target);

        // No enemy entity exists in this world. The bot must decide from the earned contact alone.
        ContactObservation contact = ProbeContact(catalog, 1200f);
        knowledge.Refresh(world, [contact]);
        bot.Update(knowledge, 20, sink);
        Assert.Equal(2, sink.MoveCount);
        Assert.Equal(new Vector2(500, 1600), sink.LastMove.Target);
        Assert.True(world.TryGetCombatState(id, out WorldState.CombatState combat));
        world.SetCombatState(id, combat with { ReloadTicksRemaining = 100 });
        knowledge.Refresh(world, [contact]);
        bot.Update(knowledge, 30, sink);
        bot.Update(knowledge, 40, sink);
        Assert.Equal(2, sink.MoveCount);
    }

    [Theory]
    [InlineData(1700f, ContactState.Current, 40, FireStance.FireAtWill)]
    [InlineData(1500f, ContactState.Current, 40, FireStance.FireAtWill)]
    [InlineData(1200f, ContactState.LastKnown, 40, FireStance.FireAtWill)]
    [InlineData(1200f, ContactState.Current, 0, FireStance.FireAtWill)]
    [InlineData(1200f, ContactState.Current, 40, FireStance.HoldFire)]
    [InlineData(1200f, ContactState.Current, 40, FireStance.ReturnFire)]
    public void Bot_KeepsAdvancingWhenContactCannotSupportAutoFire(float distance, ContactState state,
        int ammunition, FireStance stance)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        StaticGeometry geometry = TankBattleScenario.CreateStaticGeometry();
        using WorldState world = new(geometry.HeightMap);
        EntityId id = CreateProbeTank(world, catalog, ammunition);
        world.SetFireStance(id, stance);
        PlayerKnowledge knowledge = new(1);
        knowledge.Refresh(world, [ProbeContact(catalog, distance) with { State = state }]);
        TankBattleBot bot = new(1, geometry, catalog);
        RecordingSink sink = new();
        bot.Update(knowledge, 10, sink);
        Assert.Equal(new Vector2(1400, 1600), sink.LastMove.Target);
    }

    [Fact]
    public void Bot_ContactSeenByAnotherObserverBehindWallDoesNotHaltThisTank()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = TankBattleScenario.CreateStaticGeometry().HeightMap;
        StaticGeometry geometry = new(map,
            [new OpaqueStructure(new StructureId(1), 1000, 4, 1500, 1010, 15, 1700)]);
        using WorldState world = new(map);
        CreateProbeTank(world, catalog);
        PlayerKnowledge knowledge = new(1);
        knowledge.Refresh(world, [ProbeContact(catalog, 1200f)]);
        TankBattleBot bot = new(1, geometry, catalog);
        RecordingSink sink = new();
        bot.Update(knowledge, 10, sink);
        Assert.Equal(new Vector2(1400, 1600), sink.LastMove.Target);
    }

    [Fact]
    public void Bot_BriefLossDoesNotOscillateAndSustainedLossResumesOriginalFormationSlot()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        StaticGeometry geometry = TankBattleScenario.CreateStaticGeometry();
        using WorldState world = new(geometry.HeightMap);
        EntityId id = CreateProbeTank(world, catalog);
        PlayerKnowledge knowledge = new(1);
        knowledge.Refresh(world);
        TankBattleBot bot = new(1, geometry, catalog);
        RecordingSink sink = new();
        bot.Update(knowledge, 10, sink);
        ContactObservation contact = ProbeContact(catalog, 1200f);
        world.UpdateUnitNavigation(id, new Vector2(550, 1600), new Vector2(1400, 1600), true);
        knowledge.Refresh(world, [contact]);
        bot.Update(knowledge, 20, sink);
        Assert.Equal(new Vector2(550, 1600), sink.LastMove.Target);
        knowledge.Refresh(world, [contact with { State = ContactState.LastKnown }]);
        bot.Update(knowledge, 30, sink);
        Assert.Equal(2, sink.MoveCount);
        bot.Update(knowledge, 20 + TankBattleBot.ContactLossHoldTicks, sink);
        Assert.Equal(3, sink.MoveCount);
        Assert.Equal(new Vector2(1400, 1600), sink.LastMove.Target);
        bot.Update(knowledge, 50, sink);
        Assert.Equal(3, sink.MoveCount);
    }

    [Fact]
    public void Autobattle_BothSidesFireBeforeReachingScriptedEndOfAdvance()
    {
        using Simulation sim = TankBattleScenario.CreateSimulation();
        bool[] fired = new bool[3];
        for (int tick = 0; tick < 2400 && !(fired[1] && fired[2]); tick++)
        {
            sim.Step();
            for (int player = 1; player <= 2; player++)
            {
                foreach (CombatEventObservation ev in sim.Combat!.GetEvents(player))
                {
                    if (ev.Kind != CombatEventKind.ShotFired || ev.ShooterUnitId.Value == 0 || fired[player]) continue;
                    Assert.True(sim.World.TryGetUnit(ev.ShooterUnitId, out UnitState shooter));
                    Assert.Equal(player, shooter.Owner);
                    Assert.False(shooter.Moving);
                    float offset = MathF.Abs(shooter.Position.X - TankBattleScenario.Center.X);
                    Assert.True(offset > TankBattleScenario.FiringOffset + TankBattleScenario.RowSpacing + 10f,
                        $"Side {player} waited until the scripted end of its advance before firing at tick {sim.Tick}.");
                    fired[player] = true;
                }
                sim.AcknowledgeCombatEvents(player, sim.Combat!.GetEvents(player).Length);
            }
        }
        Assert.True(fired[1], "Soviet tanks did not halt and fire during the approach.");
        Assert.True(fired[2], "US tanks did not halt and fire during the approach.");
        TankBattleDirector director = ((OpsRules)sim.Rules).FindSystem<TankBattleDirector>()!;
        Assert.Equal(0, director.RejectedOrders);
    }

    private static EntityId CreateProbeTank(WorldState world, UnitCatalog catalog, int ammunition = 40) =>
        world.CreateUnit(1, catalog.RequireDefinitionIndex("t62"), new Vector2(500, 1600), 8.3f, 100, ammunition);

    private static ContactObservation ProbeContact(UnitCatalog catalog, float distance) =>
        new(new ContactId(1), ContactState.Current, ContactClassification.Tank,
            catalog.RequireDefinitionIndex("m60a1"), new Vector2(500 + distance, 1600), 10, HullHeading: MathF.PI / 2f);

    [Fact]
    public void Formation_TwoStaggeredRowsBeginOutsideEnemySensorRange()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using Simulation sim = TankBattleScenario.CreateSimulation(catalog: catalog);
        for (int player = 1; player <= 2; player++)
        {
            Dictionary<float, int> rows = [];
            for (int i = 0; i < TankBattleScenario.UnitsPerSide; i++)
            {
                Vector2 position = TankBattleScenario.GetFormationPosition(player, i, TankBattleScenario.StartOffset);
                rows.TryGetValue(position.X, out int count);
                rows[position.X] = count + 1;
                Vector2 firing = TankBattleScenario.GetFormationPosition(player, i, TankBattleScenario.FiringOffset);
                Assert.Equal(position.Y, firing.Y);
                Assert.Equal(900f, MathF.Abs(position.X - firing.X));
            }
            Assert.Equal(2, rows.Count);
            Assert.All(rows.Values, count => Assert.Equal(50, count));
        }
        foreach (UnitDefinition definition in catalog.Units)
            if (definition.Id is "t62" or "m60a1") Assert.True(2500f > definition.SensorRange);
        for (int i = 0; i < 200; i++) sim.Step();
        PlayerKnowledge knowledge = new(1);
        sim.UpdateKnowledge(knowledge);
        Assert.Equal(0, knowledge.ContactCount);
        Assert.Equal(0, sim.Combat!.TotalShotsFired);
    }

    [Fact]
    public void Advance_PreservesTwoRowsWhileClosingThroughSpottingRange()
    {
        using Simulation sim = TankBattleScenario.CreateSimulation();
        PlayerKnowledge knowledge = new(1);
        bool sawContactWhileMoving = false;
        for (int tick = 0; tick < 3000; tick++)
        {
            sim.Step();
            if (tick % 10 != 0) continue;
            sim.UpdateKnowledge(knowledge);
            foreach (OwnUnitObservation unit in knowledge.OwnUnits)
            {
                if (knowledge.ContactCount > 0 && sim.World.TryGetUnit(unit.Id, out UnitState state) && state.Moving)
                    sawContactWhileMoving = true;
            }
            sim.AcknowledgeCombatEvents(1, sim.Combat!.GetEvents(1).Length);
            sim.AcknowledgeCombatEvents(2, sim.Combat.GetEvents(2).Length);
        }
        Assert.True(sawContactWhileMoving);
        Assert.True(sim.Combat!.TotalShotsFired > 0);
        TankBattleDirector director = ((OpsRules)sim.Rules).FindSystem<TankBattleDirector>()!;
        Assert.Equal(0, director.RejectedOrders);
    }

    [Fact]
    public void Elimination_EndsMissionAndStopsFurtherCombat()
    {
        using Simulation sim = TankBattleScenario.CreateSimulation();
        for (int i = sim.World.Count - 1; i >= 0; i--)
        {
            UnitState unit = sim.World.GetUnitAt(i);
            if (unit.Owner == 2) Assert.True(sim.World.RemoveUnit(unit.Id));
        }
        for (int i = 0; i < 12; i++) sim.Step();
        Assert.True(sim.IsMissionFinished);
        Assert.Equal(MissionStatus.Victory, sim.Mission!.GetObservation(1).Status);
        Assert.Equal(MissionStatus.Defeat, sim.Mission.GetObservation(2).Status);
        Assert.Equal(0, sim.Combat!.TotalShotsFired);
        for (int i = 0; i < sim.World.Count; i++) Assert.False(sim.World.GetUnitAt(i).Moving);
    }

    private sealed class RecordingSink : IBotCommandSink
    {
        public int MoveCount { get; private set; }
        public MoveCommand LastMove { get; private set; }
        public List<MoveCommand> Moves { get; } = [];
        public bool EnqueueMove(MoveCommand command) { MoveCount++; LastMove = command; Moves.Add(command); return true; }
        public bool EnqueueAttack(AttackCommand command) => throw new InvalidOperationException();
        public void UpdateKnowledge(PlayerKnowledge knowledge) => throw new InvalidOperationException();
        public void AcknowledgeEvents(int count) => throw new InvalidOperationException();
    }
}
