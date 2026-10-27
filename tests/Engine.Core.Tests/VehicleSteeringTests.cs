using System.Numerics;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class VehicleSteeringTests
{
    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public TestMovementCatalog Register(ushort definition, float speed, float radius, float turnRate = 45.0f)
        {
            _profiles[definition] = new UnitMovementProfile(speed, radius, TraversabilityCapabilities.DefaultVehicle, turnRate);
            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    [Fact]
    public void TurnRateCap_NeverExceedsAngularVelocityPerTick()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        // 30 deg/s turn rate = MathF.PI / 6 rad/s => ~0.02618 rad/tick at 20 Hz
        catalog.Register(1, speed: 8f, radius: 1.0f, turnRate: 30f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (20, 20) facing North (0.0 rad)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 8f, initialTurretYaw: 0f);

        // Commanded to move East (50, 20) => requires -pi/2 rad heading (-90 degrees)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(50f, 20f)));

        float maxTurnPerTick = 30f * (MathF.PI / 180f) * Simulation.TickSeconds;
        float prevHeading = 0f;

        for (int i = 0; i < 100; i++)
        {
            sim.Step();
            world.TryGetCombatState(unit, out WorldState.CombatState cs);
            float currentHeading = cs.HullHeading;
            float delta = MathF.Abs(CombatSystem.ShorterArcDelta(prevHeading, currentHeading));

            Assert.True(delta <= maxTurnPerTick + 0.0002f,
                $"Tick {i}: Turn delta {delta} exceeded max angular step {maxTurnPerTick}");

            prevHeading = currentHeading;
        }
    }

    [Fact]
    public void LargeTurnDiscrepancy_PivotsOnSpotBeforeAdvancing()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f, turnRate: 30f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (20, 20) facing North (0 rad)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 6f, initialTurretYaw: 0f);

        // Commanded to move South (20, 40) => 180 degrees discrepancy (> 60 deg threshold)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(20f, 40f)));

        // During first 20 ticks (error from 180 deg down to 150 deg), vehicle must stay exactly at (20, 20)
        for (int i = 0; i < 20; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            world.TryGetCombatState(unit, out WorldState.CombatState cs);

            Assert.Equal(new Vector2(20f, 20f), state.Position);
            Assert.True(state.Moving, "Unit must report Moving status while pivoting in place.");
            Assert.True(MathF.Abs(cs.HullHeading) > 0.01f, "Hull heading should be rotating.");
        }
    }

    [Fact]
    public void SmallTurnDiscrepancy_MovesAndTurnsSimultaneously()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.0f, turnRate: 60f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (20, 20) facing North (0 rad)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 8f, initialTurretYaw: 0f);

        // Commanded to move at ~2 degrees off North: target (20.35, 10)
        // Delta = (0.35, -10), angle = Atan2(-0.35, 10) = -0.035 rad (~2 deg, below 0.05 rad threshold)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(20.35f, 10f)));

        sim.Step();
        sim.Step();

        UnitState state = world.GetUnitAt(0);
        world.TryGetCombatState(unit, out WorldState.CombatState cs);

        Assert.True(state.Moving);
        Assert.True(Vector2.Distance(state.Position, new Vector2(20f, 20f)) > 0.1f,
            "Unit must advance forward while simultaneously turning under threshold.");
        Assert.True(cs.HullHeading < 0f, "Unit must simultaneously turn heading towards target.");
    }

    [Fact]
    public void HeadingWrap_ChoosesShorterArcAcrossPiBoundary()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f, turnRate: 60f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (20, 20) facing +3.0 rad (~+172 deg)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 6f, initialTurretYaw: 3.0f);

        // Command move to target that is at -3.0 rad (~-172 deg)
        // Shorter arc across pi is only 0.28 rad (16 degrees) positive turn, NOT the long 6.0 rad negative turn
        Vector2 targetDir = new(-MathF.Sin(-3.0f), -MathF.Cos(-3.0f));
        Vector2 target = new Vector2(20f, 20f) + targetDir * 20f;

        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));

        // In 5 ticks (0.25 s at 60 deg/s = 15 deg), it should cross the pi boundary to negative radians
        for (int i = 0; i < 8; i++)
        {
            sim.Step();
        }

        world.TryGetCombatState(unit, out WorldState.CombatState cs);
        Assert.True(cs.HullHeading < 0f, "Heading should wrap across +pi to negative radians via shorter arc.");
        Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(cs.HullHeading, -3.0f)) < 0.1f);
    }

    [Fact]
    public void ArrivalWithFinalFacing_StopsAtDestinationAndRotatesToFacing()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f, turnRate: 45f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (10, 10) facing East (-pi/2)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 10f, initialTurretYaw: -MathF.PI / 2f);

        // Move to (20, 10) with FinalFacing = 0.0f (North)
        Vector2 dest = new(20f, 10f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, dest, FinalFacing: 0.0f));

        // Advance until unit reaches dest position
        for (int i = 0; i < 25; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            if (Vector2.Distance(state.Position, dest) <= 0.05f) break;
        }

        UnitState atDest = world.GetUnitAt(0);
        world.TryGetCombatState(unit, out WorldState.CombatState csAtDest);
        Assert.True(Vector2.Distance(atDest.Position, dest) <= 0.05f);

        // Still turning to final facing (North = 0f), so Moving flag should be active
        Assert.True(atDest.Moving, "Unit must stay active/moving while turning to final facing at destination.");
        Assert.True(nav.HasActiveRoute(unit));

        // Advance until in-place turn completes
        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            if (!state.Moving) break;
        }

        UnitState completed = world.GetUnitAt(0);
        world.TryGetCombatState(unit, out WorldState.CombatState csCompleted);

        Assert.False(completed.Moving, "Order must complete once final facing is reached.");
        Assert.False(nav.HasActiveRoute(unit));
        Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(csCompleted.HullHeading, 0.0f)) <= NavigationTuning.DefaultFinalFacingTolerance,
            $"Final facing {csCompleted.HullHeading} must be within tolerance of commanded 0.0f.");
    }

    [Fact]
    public void ArrivalWithoutFinalFacing_CompletesImmediatelyOnArrival()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f, turnRate: 45f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 10f, initialTurretYaw: -MathF.PI / 2f);

        // Move with FinalFacing = null
        Vector2 dest = new(20f, 10f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, dest, FinalFacing: null));

        for (int i = 0; i < 40; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            if (!state.Moving) break;
        }

        UnitState finished = world.GetUnitAt(0);
        Assert.False(finished.Moving);
        Assert.False(nav.HasActiveRoute(unit));
        Assert.True(Vector2.Distance(finished.Position, dest) <= 0.05f);
    }

    [Fact]
    public void TurnInPlaceCommand_AlreadyAtTarget_PivotsToFacing()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f, turnRate: 45f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Parked at (25, 25) facing 0.0f (North)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(25f, 25f), 10f, initialTurretYaw: 0f);

        // Command to the same spot (25, 25) with FinalFacing = MathF.PI / 2f (West)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(25f, 25f), FinalFacing: MathF.PI / 2f));
        sim.Step();

        Assert.True(nav.HasActiveRoute(unit), "Turn in place must register as an active route/maneuver.");

        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            if (!state.Moving) break;
        }

        UnitState finalState = world.GetUnitAt(0);
        world.TryGetCombatState(unit, out WorldState.CombatState cs);

        Assert.False(finalState.Moving);
        Assert.Equal(new Vector2(25f, 25f), finalState.Position);
        Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(cs.HullHeading, MathF.PI / 2f)) <= NavigationTuning.DefaultFinalFacingTolerance);
    }

    [Fact]
    public void MovingFireConstraint_MaintainedWhilePivotingToFinalFacing()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f, turnRate: 30f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 10f, initialTurretYaw: 0f);

        // Move to (10, 10) with FinalFacing = MathF.PI
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(10f, 10f), FinalFacing: MathF.PI));

        // While turning, Moving is true on every tick
        for (int i = 0; i < 30; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            Assert.True(state.Moving, $"Tick {i}: Moving status must remain true during turn to final facing.");
        }
    }

    [Fact]
    public void StraightMove_WithInitialMisalignment_AlignsDisplacementWithHullHeading()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        // 22.5 deg/s = 2.25 deg per tick
        catalog.Register(1, speed: 7f, radius: 1.0f, turnRate: 22.5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (20, 20) facing North (0 rad)
        Vector2 startPos = new(20f, 20f);
        EntityId unit = world.CreateUnit(1, 1, startPos, 7f, initialTurretYaw: 0f);

        // Target at 45 deg North-East: (34.14, 5.86)
        Vector2 target = new(34.14f, 5.86f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));

        // Step 1: unit turns by 2.25 deg towards target (misalignment 45 deg > threshold)
        // Unit must pivot in place and NOT move sideways at 45 deg
        sim.Step();

        UnitState state1 = world.GetUnitAt(0);
        world.TryGetCombatState(unit, out WorldState.CombatState cs1);

        Assert.Equal(startPos, state1.Position);
        Assert.True(cs1.HullHeading < 0f, "Hull must turn towards target during pivot");

        // Advance until unit completes pivot and moves to target
        Vector2 prevPos = startPos;
        for (int i = 0; i < 100; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            world.TryGetCombatState(unit, out WorldState.CombatState cs);

            Vector2 step = state.Position - prevPos;
            if (step.Length() > 0.05f)
            {
                // Displacement direction must align with hull heading (no lateral sliding!)
                float stepAngle = MathF.Atan2(-step.X, -step.Y);
                float slip = MathF.Abs(CombatSystem.ShorterArcDelta(stepAngle, cs.HullHeading));
                Assert.True(slip <= 0.05f,
                    $"Displacement angle {stepAngle} must align with hull heading {cs.HullHeading}, slip was {slip}");
            }

            prevPos = state.Position;
            if (Vector2.Distance(state.Position, target) <= 0.05f) break;
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.True(Vector2.Distance(finalState.Position, target) <= 0.1f, "Unit must reach target position");
    }

    [Fact]
    public void ArrivalTick_AngularVelocityNeverExceedsPerTickLimit()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        // 22.5 deg/s = 2.25 deg per tick = 0.03927 rad/tick
        catalog.Register(1, speed: 7f, radius: 1.0f, turnRate: 22.5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        float maxTurnPerTick = 22.5f * (MathF.PI / 180f) * Simulation.TickSeconds;

        // Unit starts at (20, 20) facing East (-pi/2)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 7f, initialTurretYaw: -MathF.PI / 2f);

        // Short move to (20.3, 20) with final facing North (0.0 rad)
        // Unit reaches destination in 1 tick and initiates final facing turn
        Vector2 dest = new(20.3f, 20f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, dest, FinalFacing: 0.0f));

        float prevHeading = -MathF.PI / 2f;
        for (int i = 0; i < 40; i++)
        {
            sim.Step();
            world.TryGetCombatState(unit, out WorldState.CombatState cs);
            float currentHeading = cs.HullHeading;
            float deltaThisTick = MathF.Abs(CombatSystem.ShorterArcDelta(prevHeading, currentHeading));

            Assert.True(deltaThisTick <= maxTurnPerTick + 0.0002f,
                $"Tick {i}: Angular delta {deltaThisTick * 180f / MathF.PI:F2} deg exceeded per-tick limit {maxTurnPerTick * 180f / MathF.PI:F2} deg");

            prevHeading = currentHeading;
        }
    }

    [Fact]
    public void ObstacleCorner_NavigatesCleanlyWithoutClipping()
    {
        // Standalone building at [30..44, 30..44], height 6.0m
        OpaqueStructure building = new(
            new StructureId(1),
            minX: 30f, minY: 0f, minZ: 30f,
            maxX: 44f, maxY: 6f, maxZ: 44f,
            isGrounded: true);

        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, [building]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 7f, radius: 1.0f, turnRate: 45f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Recon vehicle starts at (15, 20)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(15f, 20f), 7f, initialTurretYaw: 0f);

        // Commanded to navigate around building to (50, 37)
        Vector2 target = new(50f, 37f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));

        // Advance simulation and verify unit never intersects building interior or bounds
        // and displacement strictly aligns with hull heading on every moving tick (no slip on corners)
        Vector2 prevPos = new(15f, 20f);
        for (int i = 0; i < 200; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            world.TryGetCombatState(unit, out WorldState.CombatState cs);

            Vector2 step = state.Position - prevPos;
            if (step.Length() > 0.01f)
            {
                float stepAngle = MathF.Atan2(-step.X, -step.Y);
                float slip = MathF.Abs(CombatSystem.ShorterArcDelta(stepAngle, cs.HullHeading));
                Assert.True(slip <= 0.05f,
                    $"Tick {i}: slip {slip * 180f / MathF.PI:F2} deg exceeded threshold at {state.Position}");
            }
            prevPos = state.Position;

            // Unit must not intersect building [30..44, 30..44]
            bool insideBuilding = state.Position.X >= 30f && state.Position.X <= 44f &&
                                  state.Position.Y >= 30f && state.Position.Y <= 44f;
            Assert.False(insideBuilding, $"Tick {i}: Unit penetrated building at {state.Position}");

            if (Vector2.Distance(state.Position, target) <= 0.1f)
            {
                break;
            }
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.True(Vector2.Distance(finalState.Position, target) <= 0.5f,
            $"Unit should reach destination around obstacle: ended at {finalState.Position}");
    }
}
