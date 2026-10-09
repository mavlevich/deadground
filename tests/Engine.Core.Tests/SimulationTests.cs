using System.Numerics;
using System.Security.Cryptography;
using DeadGround.Engine;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace DeadGround.Tests;

public sealed class SimulationTests
{
    private sealed class MovementRules : RuleSet
    {
        public MovementRules() : base(new MovementSystem()) { }
    }

    private sealed class AlternateMovement : ISimulationSystem
    {
        public int Calls { get; private set; }
        public void Update(WorldState world, int tick) => Calls++;
    }

    [Fact]
    public void RuleSetsCanOmitOrReplaceMovementWithoutChangingStorageOrCommands()
    {
        foreach (bool replace in new[] { false, true })
        {
            WorldState world = new(HeightMap.CreatePrototype());
            EntityId id = world.CreateUnit(1, 0, new Vector2(10, 10), 6);
            AlternateMovement alternative = new();
            using Simulation simulation = new(world, replace ? new RuleSet(alternative) : new RuleSet());
            Assert.True(simulation.TryEnqueue(1, new MoveCommand(1, 0, id, new Vector2(50, 50))));
            simulation.Step();
            Assert.Equal(new Vector2(50, 50), world.GetUnitAt(0).Target);
            Assert.Equal(new Vector2(10, 10), world.GetUnitAt(0).Position);
            Assert.Equal(replace ? 1 : 0, alternative.Calls);
        }
    }

    [Fact]
    public void CommandsApplyAtTheNextTickAndMovementUsesSimulationTime()
    {
        WorldState world = new(HeightMap.CreatePrototype());
        EntityId id = world.CreateUnit(1, 0, new Vector2(10, 10), 6);
        using Simulation simulation = new(world, new MovementRules());
        Assert.True(simulation.TryEnqueue(1, new MoveCommand(1, 0, id, new Vector2(100, 10))));
        Assert.Equal(new Vector2(10, 10), world.GetUnitAt(0).Position);
        for (int i = 0; i < 20; i++) simulation.Step();
        Assert.Equal(20, simulation.Tick);
        Assert.InRange(world.GetUnitAt(0).Position.X, 15.999f, 16.001f);
    }

    [Fact]
    public void ArrivalDoesNotOvershootOrKeepMoving()
    {
        WorldState world = new(HeightMap.CreatePrototype());
        EntityId id = world.CreateUnit(1, 0, new Vector2(10, 10), 6);
        using Simulation simulation = new(world, new MovementRules());
        Vector2 destination = new(10.1f, 10);
        simulation.TryEnqueue(1, new MoveCommand(1, 0, id, destination));
        for (int i = 0; i < 10; i++) simulation.Step();
        Assert.Equal(destination, world.GetUnitAt(0).Position);
        Assert.False(world.GetUnitAt(0).Moving);
    }

    [Fact]
    public void ForeignUnitsInvalidTargetsAndDuplicateCommandsAreRejected()
    {
        WorldState world = new(HeightMap.CreatePrototype());
        EntityId own = world.CreateUnit(1, 0, new Vector2(10, 10), 6);
        EntityId enemy = world.CreateUnit(2, 0, new Vector2(20, 20), 6);
        using Simulation simulation = new(world, new MovementRules());
        Assert.False(simulation.TryEnqueue(1, new MoveCommand(1, 0, enemy, new Vector2(30, 30))));
        Assert.False(simulation.TryEnqueue(1, new MoveCommand(1, 0, own, new Vector2(float.NaN, 30))));
        Assert.False(simulation.TryEnqueue(1, new MoveCommand(1, 0, own, new Vector2(500, 30))));
        Assert.True(simulation.TryEnqueue(1, new MoveCommand(1, 0, own, new Vector2(30, 30))));
        Assert.False(simulation.TryEnqueue(1, new MoveCommand(1, 0, own, new Vector2(40, 40))));
        simulation.Step();
        Assert.Equal(new Vector2(20, 20), world.GetUnitAt(1).Position);
        Assert.Equal(new Vector2(30, 30), world.GetUnitAt(0).Target);
    }

    [Fact]
    public void RemovingAndReplacingStorageNeverReusesPublicIds()
    {
        using WorldState world = new(HeightMap.CreatePrototype(), 2);
        EntityId removed = world.CreateUnit(1, 0, new Vector2(1, 1), 1);
        EntityId retained = world.CreateUnit(1, 0, new Vector2(2, 2), 1);
        Assert.True(world.RemoveUnit(removed));
        EntityId replacement = world.CreateUnit(1, 0, new Vector2(3, 3), 1);
        Assert.NotEqual(removed, replacement);
        Assert.False(world.TryGetUnit(removed, out _));
        Assert.True(world.TryGetUnit(retained, out _));
        Assert.True(world.TryGetUnit(replacement, out _));
    }

    [Fact]
    public void KnowledgeDoesNotExposeEnemyIdsOrPositions()
    {
        using WorldState world = new(HeightMap.CreatePrototype());
        EntityId own = world.CreateUnit(1, 0, new Vector2(10, 10), 1);
        world.CreateUnit(2, 0, new Vector2(20, 20), 1);
        PlayerKnowledge knowledge = new(1);
        using Simulation simulation = new(world, new MovementRules());
        simulation.UpdateKnowledge(knowledge);
        Assert.Equal(1, knowledge.Count);
        Assert.Equal(own, knowledge.OwnUnits[0].Id);
    }

    [Fact]
    public void RepeatedCommandLogsGiveTheSameState()
    {
        static string Run()
        {
            WorldState world = new(HeightMap.CreatePrototype());
            EntityId id = world.CreateUnit(1, 0, new Vector2(10, 10), 6);
            using Simulation simulation = new(world, new MovementRules());
            simulation.TryEnqueue(1, new MoveCommand(1, 0, id, new Vector2(20, 30)));
            for (int i = 0; i < 24; i++) simulation.Step();
            simulation.TryEnqueue(1, new MoveCommand(2, 24, id, new Vector2(90, 70)));
            for (int i = 0; i < 76; i++) simulation.Step();
            UnitState unit = world.GetUnitAt(0);
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(simulation.Tick);
            writer.Write(unit.Id.Value);
            writer.Write(unit.Owner);
            writer.Write(unit.Definition);
            writer.Write(unit.Position.X); writer.Write(unit.Position.Y);
            writer.Write(unit.Target.X); writer.Write(unit.Target.Y);
            writer.Write(unit.Moving);
            return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
        }
        Assert.Equal(Run(), Run());
        // M0 has no random systems. This pins the complete final state of its fixed command log.
        Assert.Equal("428FA6A36F9D7F94F4E3A0F34AC69EAFCF9AE75668298E8482475B900F7C05C4", Run());
    }
}
