using System.Numerics;
using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class NavigationCombatTrackingTests
{
    private static HeightMap CreateFlatMap(int size = 129, float cellSize = 10f)
    {
        return new HeightMap(size, size, cellSize, 0.01f, new ushort[size * size]);
    }

    [Fact]
    public void OpsRuleSet_TargetThatStopped10TicksAgo_StillReceivesTargetMovementPenalty()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsRules rules = new(geo, catalog);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");

        // Attacker M60 at (300, 300), target T-62 at (300, 400)
        EntityId attacker = world.CreateUnit(1, m60Idx, new Vector2(300f, 300f), m60Def.Speed, m60Def.MaxHealth, 40, initialTurretYaw: 0f);
        EntityId target = world.CreateUnit(2, t62Idx, new Vector2(300f, 400f), t62Def.Speed, t62Def.MaxHealth, 40, initialTurretYaw: MathF.PI);

        world.SetFireStance(attacker, FireStance.HoldFire);
        world.SetFireStance(target, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        // Move target 5 meters south using Ops NavigationSystem
        Vector2 targetDest = new(300f, 405f);
        Assert.True(sim.TryEnqueue(2, new MoveCommand(1, sim.Tick, target, targetDest)));

        // Advance simulation until target stops moving
        sim.Step();
        while (sim.World.TryGetUnit(target, out UnitState u) && u.Moving)
        {
            sim.Step();
        }

        // Target has now stopped moving
        Assert.True(sim.World.TryGetCombatState(target, out WorldState.CombatState csStopped));
        Assert.True(csStopped.LastMovedTick > 0, "LastMovedTick must be recorded when unit moves via navigation.");

        int stopTick = sim.Tick;
        Assert.Equal(stopTick, csStopped.LastMovedTick);

        // Advance exactly 10 ticks while target remains stationary
        for (int i = 0; i < 10; i++)
        {
            sim.Step();
        }

        Assert.True(sim.World.TryGetCombatState(target, out WorldState.CombatState cs10Later));
        int elapsedSinceMove = sim.Tick - cs10Later.LastMovedTick;
        Assert.Equal(10, elapsedSinceMove);

        // Target is stationary now, but stopped only 10 ticks ago (within 20 ticks window)
        Assert.False(sim.World.GetUnitAt(1).Moving);
        bool targetConsideredMovingAt10Ticks = cs10Later.LastMovedTick >= 0 &&
            (sim.Tick - cs10Later.LastMovedTick) <= CombatSystem.TargetRecentlyMovedWindowTicks;
        Assert.True(targetConsideredMovingAt10Ticks, "Target stopped 10 ticks ago must be considered recently moved.");

        // Advance past the 20 ticks window (11 more ticks -> 21 ticks elapsed)
        for (int i = 0; i < 11; i++)
        {
            sim.Step();
        }

        Assert.True(sim.World.TryGetCombatState(target, out WorldState.CombatState cs21Later));
        int elapsedPastWindow = sim.Tick - cs21Later.LastMovedTick;
        Assert.True(elapsedPastWindow > CombatSystem.TargetRecentlyMovedWindowTicks);
        bool targetConsideredMovingAt21Ticks = cs21Later.LastMovedTick >= 0 &&
            (sim.Tick - cs21Later.LastMovedTick) <= CombatSystem.TargetRecentlyMovedWindowTicks;
        Assert.False(targetConsideredMovingAt21Ticks, "Target stopped 21 ticks ago must no longer receive movement penalty.");
    }

    [Fact]
    public void OpsRuleSet_ShooterThatDrove50m_LosesRangingBonus()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsRules rules = new(geo, catalog);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");

        EntityId shooter = world.CreateUnit(1, m60Idx, new Vector2(300f, 300f), m60Def.Speed, m60Def.MaxHealth, 40, initialTurretYaw: 0f);
        EntityId target = world.CreateUnit(2, t62Idx, new Vector2(300f, 500f), t62Def.Speed, t62Def.MaxHealth, 40, initialTurretYaw: MathF.PI);

        world.SetFireStance(shooter, FireStance.HoldFire);
        world.SetFireStance(target, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem det = rules.FindSystem<DetectionSystem>()!;
        Assert.True(det.TryGetContactByEntity(1, target, out ContactObservation contact));

        // Fire first shot at target to establish ranging count
        world.SetFireStance(shooter, FireStance.FireAtWill);
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, shooter, contact.Id)));

        CombatSystem combat = rules.FindSystem<CombatSystem>()!;
        while (combat.TotalShotsFired < 1) sim.Step();

        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState csBeforeMove));
        Assert.Equal(1, csBeforeMove.ConsecutiveShotsOnTarget);
        Assert.Equal(target, csBeforeMove.LastTargetEntityId);

        // Stop firing and drive shooter 50 meters west via navigation
        world.SetFireStance(shooter, FireStance.HoldFire);
        Vector2 shooterDest = new(250f, 300f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, shooter, shooterDest)));

        // Step simulation while moving
        sim.Step();
        sim.Step();

        // Ranging bonus must reset as soon as shooter starts driving
        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState csWhileMoving));
        Assert.Equal(0, csWhileMoving.ConsecutiveShotsOnTarget);

        // Drive all the way to destination (50m)
        while (sim.World.TryGetUnit(shooter, out UnitState u) && u.Moving)
        {
            sim.Step();
        }

        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState csAfter50m));
        Assert.Equal(0, csAfter50m.ConsecutiveShotsOnTarget);
        Assert.True(Vector2.Distance(sim.World.GetUnitAt(0).Position, new Vector2(300f, 300f)) >= 49f);
    }
}
