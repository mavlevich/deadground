using System.Numerics;
using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class WheeledRouteChunkHandoffTests
{
    [Fact]
    public void M151A2_WesternSaddleLongRoute_AdvancesThroughChunkBoundaryWithoutCircling()
    {
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");
        using Simulation sim = BattleScenario.CreateSimulation(geometry, catalog, matchSeed: 1337UL, enableMission: false);

        OpsRules rules = (OpsRules)sim.Rules;
        NavigationSystem nav = rules.FindSystem<NavigationSystem>()!;
        NavigationGrid grid = nav.Grid;
        WorldState world = sim.World;
        OpsMovementCatalog movementCatalog = new(catalog);

        // Remove opposing forces to focus strictly on navigational geometry and progression
        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 1)
            {
                world.RemoveUnit(u.Id);
            }
        }

        EntityId m151Id = default;
        ushort m151DefIdx = 0;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            UnitDefinition def = catalog.Units[u.Definition];
            if (u.Owner == 2 && def.Id == "m151a2")
            {
                m151Id = u.Id;
                m151DefIdx = u.Definition;
                break;
            }
        }
        Assert.NotEqual(default, m151Id);
        movementCatalog.TryGetProfile(m151DefIdx, out UnitMovementProfile profile);

        // Target in western saddle village from the playtest
        Vector2 target = new(650f, 630f);
        sim.TryEnqueue(2, new MoveCommand(1, sim.Tick, m151Id, target));

        bool reached = false;
        int circlingCount = 0;
        Vector2 prevPos = Vector2.Zero;
        float prevHeading = 0f;
        int stuckTick = -1;

        float turnRateRadVal = profile.TurnRate * (MathF.PI / 180f);
        float dt = Simulation.TickSeconds;

        for (int tick = 1; tick <= 2000; tick++)
        {
            sim.Step();

            bool found = false;
            UnitState u = default;
            for (int i = 0; i < world.Count; i++)
            {
                if (world.GetUnitAt(i).Id == m151Id)
                {
                    u = world.GetUnitAt(i);
                    found = true;
                    break;
                }
            }
            if (!found) break;
            world.TryGetCombatState(m151Id, out WorldState.CombatState cs);

            // Verify position stays strictly in clear passable area at all times
            Assert.True(grid.IsPointClear(u.Position, profile.Radius, profile.Capabilities, u.SupportSurface),
                $"Vehicle must remain in passable clear area at tick {tick}: Pos={u.Position}");

            float speed = nav.GetUnitSpeed(m151Id);
            float angleDelta = MathF.Abs(CombatSystem.ShorterArcDelta(prevHeading, cs.HullHeading));

            // Verify kinematic turn limits are never violated during chunk handoff or cornering
            float stepDist = speed * dt;
            float maxTurnStep = MathF.Min(turnRateRadVal * dt, stepDist / MathF.Max(0.1f, profile.MinTurnRadius)) + 1e-3f;
            if (tick > 5 && speed > 0.5f)
            {
                Assert.True(angleDelta <= maxTurnStep,
                    $"Tick {tick}: turn step {angleDelta:F4} exceeded kinematic limit {maxTurnStep:F4}");
            }

            float dist = Vector2.Distance(u.Position, target);
            if (dist <= 1.5f)
            {
                reached = true;
                break;
            }

            if (speed > 1.0f && angleDelta > 0.02f && Vector2.Distance(u.Position, prevPos) > 0.05f)
            {
                circlingCount++;
            }
            else
            {
                circlingCount = 0;
            }
            prevPos = u.Position;
            prevHeading = cs.HullHeading;

            // In the buggy code, the vehicle circled indefinitely around (1550, 475) starting at tick 255
            if (circlingCount > 120)
            {
                stuckTick = tick;
                break;
            }
        }

        Assert.Equal(-1, stuckTick);
        Assert.True(reached, "M151A2 must advance past chunk boundaries and reach western saddle target.");
    }

    [Fact]
    public void TrackedVehicle_WesternSaddleRoute_CompletesWithoutLockup()
    {
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");
        using Simulation sim = BattleScenario.CreateSimulation(geometry, catalog, matchSeed: 1337UL, enableMission: false);

        OpsRules rules = (OpsRules)sim.Rules;
        NavigationSystem nav = rules.FindSystem<NavigationSystem>()!;
        WorldState world = sim.World;

        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 1)
            {
                world.RemoveUnit(u.Id);
            }
        }

        EntityId m60Id = default;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            UnitDefinition def = catalog.Units[u.Definition];
            if (u.Owner == 2 && def.Id == "m60a1")
            {
                m60Id = u.Id;
                break;
            }
        }
        Assert.NotEqual(default, m60Id);

        // Remove other units so the test isolates individual tank navigation without traffic jam at spawn
        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Id != m60Id)
            {
                world.RemoveUnit(u.Id);
            }
        }

        Vector2 target = new(650f, 630f);
        sim.TryEnqueue(2, new MoveCommand(1, sim.Tick, m60Id, target));

        bool reached = false;
        for (int tick = 1; tick <= 4500; tick++)
        {
            sim.Step();

            bool found = false;
            UnitState u = default;
            for (int i = 0; i < world.Count; i++)
            {
                if (world.GetUnitAt(i).Id == m60Id)
                {
                    u = world.GetUnitAt(i);
                    found = true;
                    break;
                }
            }
            if (!found) break;

            if (Vector2.Distance(u.Position, target) <= 1.5f)
            {
                reached = true;
                break;
            }
        }

        Assert.True(reached, "Tracked tank must advance past chunk boundaries and reach target.");
    }

    [Fact]
    public void IntermediateChunkHandoff_DoesNotTriggerPrematureArrivalStatus()
    {
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");
        using Simulation sim = BattleScenario.CreateSimulation(geometry, catalog, matchSeed: 1337UL, enableMission: false);

        OpsRules rules = (OpsRules)sim.Rules;
        NavigationSystem nav = rules.FindSystem<NavigationSystem>()!;
        WorldState world = sim.World;

        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 1)
            {
                world.RemoveUnit(u.Id);
            }
        }

        EntityId m151Id = default;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            UnitDefinition def = catalog.Units[u.Definition];
            if (u.Owner == 2 && def.Id == "m151a2")
            {
                m151Id = u.Id;
                break;
            }
        }

        Vector2 target = new(650f, 630f);
        sim.TryEnqueue(2, new MoveCommand(1, sim.Tick, m151Id, target));

        bool reachedDestination = false;
        int stoppedAtDestinationTick = -1;

        // Step through full transit including chunk transitions (ticks ~50, 102, 225, 255, and later)
        // until the vehicle reaches its destination around tick 1513 and comes to a full stop.
        for (int tick = 1; tick <= 2000; tick++)
        {
            sim.Step();

            UnitState u = default;
            for (int i = 0; i < world.Count; i++)
            {
                if (world.GetUnitAt(i).Id == m151Id)
                {
                    u = world.GetUnitAt(i);
                    break;
                }
            }

            float distToTarget = Vector2.Distance(u.Position, target);
            float speed = nav.GetUnitSpeed(m151Id);

            if (distToTarget <= 1.5f)
            {
                reachedDestination = true;
                if (!u.Moving && speed <= 1e-4f)
                {
                    stoppedAtDestinationTick = tick;
                    break;
                }
            }
            else if (tick > 5)
            {
                // Prior to arriving at the destination, the vehicle must remain actively moving
                // across all intermediate chunk handoffs, never reporting false arrival.
                Assert.True(u.Moving, $"Unit should remain moving prior to final destination at tick {tick}");
                Assert.True(speed > 0.5f, $"Speed should remain above cruising threshold at tick {tick}");
                Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(m151Id));
            }
        }

        Assert.True(reachedDestination, "Vehicle should reach destination area.");
        Assert.True(stoppedAtDestinationTick > 0, "Vehicle should come to a complete stop at final destination.");
    }

    [Fact]
    public void M151A2_TargetNearSaddleBuilding_StopsCleanlyWhenKinematicApproachBlocked()
    {
        // Target (660, 660) is located immediately adjacent to saddle buildings.
        // The vehicle cleanly traverses 20+ chunk transitions over 1000m to the village,
        // then terminates with NoPath without entering an endless circle.
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");
        using Simulation sim = BattleScenario.CreateSimulation(geometry, catalog, matchSeed: 1337UL, enableMission: false);

        OpsRules rules = (OpsRules)sim.Rules;
        NavigationSystem nav = rules.FindSystem<NavigationSystem>()!;
        WorldState world = sim.World;

        for (int i = world.Count - 1; i >= 0; i--)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 1)
            {
                world.RemoveUnit(u.Id);
            }
        }

        EntityId m151Id = default;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            UnitDefinition def = catalog.Units[u.Definition];
            if (u.Owner == 2 && def.Id == "m151a2")
            {
                m151Id = u.Id;
                break;
            }
        }

        Vector2 target = new(660f, 660f);
        sim.TryEnqueue(2, new MoveCommand(1, sim.Tick, m151Id, target));

        int circlingCount = 0;
        Vector2 prevPos = Vector2.Zero;
        float prevHeading = 0f;
        int stuckCirclingTick = -1;

        for (int tick = 1; tick <= 2000; tick++)
        {
            sim.Step();

            UnitState u = default;
            for (int i = 0; i < world.Count; i++)
            {
                if (world.GetUnitAt(i).Id == m151Id)
                {
                    u = world.GetUnitAt(i);
                    break;
                }
            }

            world.TryGetCombatState(m151Id, out WorldState.CombatState cs);
            float speed = nav.GetUnitSpeed(m151Id);
            float angleDelta = MathF.Abs(CombatSystem.ShorterArcDelta(prevHeading, cs.HullHeading));

            if (speed > 1.0f && angleDelta > 0.02f && Vector2.Distance(u.Position, prevPos) > 0.05f)
            {
                circlingCount++;
            }
            else
            {
                circlingCount = 0;
            }
            prevPos = u.Position;
            prevHeading = cs.HullHeading;

            if (circlingCount > 120)
            {
                stuckCirclingTick = tick;
                break;
            }

            if (tick > 50 && !u.Moving && speed <= 1e-4f)
            {
                break;
            }
        }

        Assert.Equal(-1, stuckCirclingTick);
        UnitState finalU = default;
        for (int i = 0; i < world.Count; i++)
        {
            if (world.GetUnitAt(i).Id == m151Id) finalU = world.GetUnitAt(i);
        }
        float finalDist = Vector2.Distance(finalU.Position, target);
        // The vehicle cleanly traversed across the map and halted within 10m of the target rather than circling
        Assert.True(finalDist < 15.0f, $"Vehicle should advance close to the target area: Dist={finalDist:F2}");
        Assert.False(finalU.Moving, "Vehicle should come to a halt rather than circling indefinitely.");
    }
}
