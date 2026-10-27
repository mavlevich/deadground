using System.Numerics;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class VehicleSteeringTests
{
    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public TestMovementCatalog Register(
            ushort definition,
            float speed,
            float radius,
            float turnRate = 45.0f,
            float reverseSpeed = 2.5f,
            float acceleration = 0.0f,
            float deceleration = 0.0f,
            bool canPivot = true,
            float minTurnRadius = 0.0f)
        {
            _profiles[definition] = new UnitMovementProfile(
                speed,
                radius,
                TraversabilityCapabilities.DefaultVehicle,
                turnRate,
                reverseSpeed,
                acceleration,
                deceleration,
                canPivot,
                minTurnRadius);
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
            if (Vector2.Distance(state.Position, target) <= 0.05f && !state.Moving) break;
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

    [Fact]
    public void AccelerationAndDeceleration_LinearRamp_SmoothlyScalesSpeed()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        // Speed 8 m/s, accel 2 m/s^2, decel 4 m/s^2
        catalog.Register(1, speed: 8f, radius: 1.0f, turnRate: 45f, acceleration: 2f, deceleration: 4f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 8f, initialTurretYaw: -MathF.PI / 2f);
        Vector2 target = new(60f, 20f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));

        Vector2 prevPos = new(20f, 20f);
        float maxSpeedObserved = 0f;

        for (int i = 0; i < 200; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            float stepDist = Vector2.Distance(state.Position, prevPos);
            float speed = stepDist / Simulation.TickSeconds;
            if (speed > maxSpeedObserved) maxSpeedObserved = speed;

            // Speed must not exceed max speed of 8 m/s
            Assert.True(speed <= 8.01f, $"Tick {i}: Speed {speed:F2} m/s exceeded max speed 8 m/s");

            prevPos = state.Position;
            if (Vector2.Distance(state.Position, target) <= 0.05f && !state.Moving) break;
        }

        // Vehicle must accelerate up to high cruise speed
        Assert.True(maxSpeedObserved >= 7.0f, $"Vehicle should accelerate to near max speed: observed {maxSpeedObserved:F2} m/s");

        UnitState finalState = world.GetUnitAt(0);
        Assert.True(Vector2.Distance(finalState.Position, target) <= 0.15f, "Vehicle should reach target.");
        Assert.False(finalState.Moving, "Vehicle should come to halt at target.");
    }

    [Fact]
    public void WheeledVehicle_StationaryTurn_IsZero()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        // Wheeled vehicle: canPivot = false, minTurnRadius = 5.0m, accel = 2 m/s^2, turnRate = 45 deg/s
        catalog.Register(1, speed: 6f, radius: 1.0f, turnRate: 45f, acceleration: 2f, deceleration: 4f, canPivot: false, minTurnRadius: 5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (20, 20) facing North (0.0 rad)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 6f, initialTurretYaw: 0f);

        // Commanded to move East (40, 20)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(40f, 20f)));

        // On first tick, unit speed starts from 0; turn rate at v=0 must be 0 (cannot pivot on spot)
        float prevHeading = 0f;
        Vector2 prevPos = new(20f, 20f);

        for (int i = 0; i < 60; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            world.TryGetCombatState(unit, out WorldState.CombatState cs);

            float stepDist = Vector2.Distance(state.Position, prevPos);
            float speed = stepDist / Simulation.TickSeconds;
            float angularDelta = MathF.Abs(CombatSystem.ShorterArcDelta(prevHeading, cs.HullHeading));
            float omega = angularDelta / Simulation.TickSeconds;

            if (speed <= 0.01f)
            {
                // When stationary, angular velocity must be 0
                Assert.True(omega <= 0.001f, $"Tick {i}: Wheeled vehicle turned {omega:F3} rad/s while stationary!");
            }
            else
            {
                // When moving, angular velocity must respect omega <= v / R_min
                float maxOmegaAllowed = (speed / 5.0f) + 0.1f;
                Assert.True(omega <= maxOmegaAllowed,
                    $"Tick {i}: Omega {omega:F3} exceeded kinematic limit {maxOmegaAllowed:F3} for speed {speed:F2}");
            }

            prevHeading = cs.HullHeading;
            prevPos = state.Position;
        }
    }

    [Fact]
    public void ReverseMove_ExplicitCommand_MaintainsForwardHullFacing()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        // Forward speed 8 m/s, reverse speed 3 m/s, accel 3 m/s^2, decel 6 m/s^2
        catalog.Register(1, speed: 8f, radius: 1.0f, turnRate: 45f, reverseSpeed: 3f, acceleration: 3f, deceleration: 6f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (20, 20) facing North (0.0 rad, vector (0, -1))
        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 8f, initialTurretYaw: 0f);

        // Commanded to move South (20, 35) in REVERSE gear
        // Heading is North (0 rad), so moving South is moving backwards
        Vector2 target = new(20f, 35f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target, Mode: MoveMode.Reverse));

        Vector2 prevPos = new(20f, 20f);
        for (int i = 0; i < 150; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            world.TryGetCombatState(unit, out WorldState.CombatState cs);

            Vector2 step = state.Position - prevPos;
            if (state.Moving && step.Length() > 0.01f)
            {
                float speed = step.Length() / Simulation.TickSeconds;
                Assert.True(speed <= 3.05f, $"Tick {i}: Reverse speed {speed:F2} exceeded reverse limit 3 m/s");

                // Displacement should be in South (+Z) direction, while hull remains facing North (0 rad)
                Assert.True(step.Y > 0f, $"Tick {i}: Reverse displacement should move in +Z direction");
                float headingError = MathF.Abs(CombatSystem.ShorterArcDelta(cs.HullHeading, 0f));
                Assert.True(headingError <= 0.05f, $"Tick {i}: Hull heading should remain facing North, error={headingError:F3}");
            }

            prevPos = state.Position;
            if (Vector2.Distance(state.Position, target) <= 0.05f && !state.Moving) break;
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.True(Vector2.Distance(finalState.Position, target) <= 0.15f, "Unit reached reverse target");
        world.TryGetCombatState(unit, out WorldState.CombatState finalCs);
        Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(finalCs.HullHeading, 0f)) <= 0.05f, "Hull kept forward facing");
    }

    [Fact]
    public void DirectionTransition_ForwardToReverse_BrakesThroughZeroSpeed()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.0f, turnRate: 45f, reverseSpeed: 3f, acceleration: 4f, deceleration: 4f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (20, 20) facing East (-pi/2)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 8f, initialTurretYaw: -MathF.PI / 2f);

        // Drive forward East to (50, 20)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(50f, 20f), Mode: MoveMode.Forward));

        // Advance until cruising at high speed (> 6 m/s)
        float maxForwardSpeed = 0f;
        Vector2 prevPos = new(20f, 20f);
        for (int i = 0; i < 30; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            float stepDist = Vector2.Distance(state.Position, prevPos);
            float spd = stepDist / Simulation.TickSeconds;
            if (spd > maxForwardSpeed) maxForwardSpeed = spd;
            prevPos = state.Position;
        }
        Assert.True(maxForwardSpeed >= 5f, $"Unit should reach cruise speed: {maxForwardSpeed:F2}");

        // Now issue reverse order to go West (10, 20) in reverse gear
        sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, unit, new Vector2(10f, 20f), Mode: MoveMode.Reverse));

        // Unit must brake down smoothly through zero speed before moving West
        bool reachedNearZero = false;
        bool beganReverseMove = false;

        for (int i = 0; i < 100; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            Vector2 step = state.Position - prevPos;
            float spd = step.Length() / Simulation.TickSeconds;

            if (spd <= 0.15f)
            {
                reachedNearZero = true;
            }

            if (reachedNearZero && step.X < -0.01f)
            {
                beganReverseMove = true;
                break;
            }

            prevPos = state.Position;
        }

        Assert.True(reachedNearZero, "Vehicle must decelerate to near zero speed during direction change.");
        Assert.True(beganReverseMove, "Vehicle must begin reverse travel after braking to halt.");
    }

    [Fact]
    public void OnTheFlyOrderRedirection_PreservesSpeedAndMomemtum()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.0f, turnRate: 45f, acceleration: 3f, deceleration: 4f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (20, 20) facing East (-pi/2)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 8f, initialTurretYaw: -MathF.PI / 2f);

        // Move to (50, 20)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(50f, 20f)));

        // Run until cruising at speed > 5 m/s
        Vector2 prevPos = new(20f, 20f);
        for (int i = 0; i < 25; i++)
        {
            sim.Step();
            prevPos = world.GetUnitAt(0).Position;
        }

        // On the fly redirect to (55, 25) - gentle 15 deg redirection ahead
        sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, unit, new Vector2(55f, 25f)));

        // Verify that speed does NOT drop to zero on redirect
        float minSpeedAfterRedirect = 999f;
        for (int i = 0; i < 15; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            float stepDist = Vector2.Distance(state.Position, prevPos);
            float spd = stepDist / Simulation.TickSeconds;
            if (spd < minSpeedAfterRedirect) minSpeedAfterRedirect = spd;
            prevPos = state.Position;
        }

        Assert.True(minSpeedAfterRedirect >= 3.0f,
            $"Speed dropped too low on redirection: {minSpeedAfterRedirect:F2} m/s; momentum should be preserved.");
    }

    [Fact]
    public void WheeledVehicle_UnachievableFinalFacing_HonestlyRejectsOrder()
    {
        // Wall directly behind the target at (30, 10..30)
        OpaqueStructure wall = new(
            new StructureId(1),
            minX: 28f, minY: 0f, minZ: 10f,
            maxX: 32f, maxY: 4f, maxZ: 30f,
            isGrounded: true);

        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        // Wheeled vehicle cannot pivot: canPivot = false, minTurnRadius = 5.0m
        catalog.Register(1, speed: 6f, radius: 1.0f, turnRate: 45f, canPivot: false, minTurnRadius: 5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (15, 20) facing East (-pi/2)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(15f, 20f), 6f, initialTurretYaw: -MathF.PI / 2f);

        // Target is at (25, 20) right in front of the wall at X=28..32.
        // Desired final facing is West (+pi/2). To face West on arrival, approach corridor would have
        // to come from the East (X=35, through the wall!). Because the wall blocks the approach corridor,
        // kinematic approach corridor is impossible and order must be honestly rejected.
        Vector2 target = new(25f, 20f);
        float desiredFacing = MathF.PI / 2f; // West
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target, FinalFacing: desiredFacing));

        sim.Step();

        // The order must be honestly rejected as InvalidDestination, unit does not move
        Assert.Equal(PathRequestStatus.InvalidDestination, nav.GetUnitStatus(unit));
        UnitState state = world.GetUnitAt(0);
        Assert.False(state.Moving, "Unit should not move when order is rejected.");
        Assert.Equal(new Vector2(15f, 20f), state.Position);
    }

    [Fact]
    public void WheeledVehicle_InPlaceTurn_RejectedAsInvalidDestination_PreservesSequence()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f, turnRate: 45f, canPivot: false, minTurnRadius: 5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(15f, 20f), 6f, initialTurretYaw: 0f);

        // Turn in place by 5.73 degrees (0.10 rad)
        sim.TryEnqueue(1, new MoveCommand(42, 0, unit, new Vector2(15f, 20f), FinalFacing: 0.10f));
        sim.Step();

        Assert.Equal(PathRequestStatus.InvalidDestination, nav.GetUnitStatus(unit));
        Assert.Equal(42u, nav.GetUnitOrderSequence(unit));

        world.TryGetCombatState(unit, out WorldState.CombatState cs);
        Assert.Equal(0f, cs.HullHeading);
        UnitState state = world.GetUnitAt(0);
        Assert.False(state.Moving);
        Assert.Equal(new Vector2(15f, 20f), state.Position);

        // Turn in place by 90 degrees
        sim.TryEnqueue(1, new MoveCommand(43, sim.Tick, unit, new Vector2(15f, 20f), FinalFacing: MathF.PI / 2f));
        sim.Step();

        Assert.Equal(PathRequestStatus.InvalidDestination, nav.GetUnitStatus(unit));
        Assert.Equal(43u, nav.GetUnitOrderSequence(unit));

        world.TryGetCombatState(unit, out cs);
        Assert.Equal(0f, cs.HullHeading);
        state = world.GetUnitAt(0);
        Assert.False(state.Moving);
    }

    [Fact]
    public void WheeledVehicle_DetourSearch_RetainsTrueUserDestination()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        OpaqueStructure wall = new(new StructureId(1), 30f, 0f, 15f, 34f, 4f, 25f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f, turnRate: 45f, canPivot: false, minTurnRadius: 5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 6f, initialTurretYaw: -MathF.PI / 2f);
        Vector2 userTarget = new(45f, 20f);
        float userFacing = -MathF.PI / 2f; // East

        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, userTarget, FinalFacing: userFacing));

        for (int i = 0; i < 20; i++)
        {
            sim.Step();
            if (nav.GetUnitStatus(unit) == PathRequestStatus.PathFound) break;
        }

        Assert.Equal(userTarget, nav.GetUnitFinalTarget(unit));
    }

    [Fact]
    public void VehicleDeceleration_RespectsDecelLimit_OnEveryTickIncludingArrival()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        float decel = 4.0f;
        float dt = Simulation.TickSeconds;
        float maxAllowedDecel = decel * dt + 1e-3f;
        catalog.Register(1, speed: 7f, radius: 1.0f, turnRate: 45f, acceleration: 3f, deceleration: decel);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(15f, 20f), 7f, initialTurretYaw: -MathF.PI / 2f);
        Vector2 target = new(45f, 20f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));

        Vector2 prevPos = new(15f, 20f);
        float prevInternalSpeed = 0f;
        float prevActualSpeed = 0f;
        bool checkedArrival = false;

        for (int t = 0; t < 150; t++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            float currentInternalSpeed = nav.GetUnitSpeed(unit);
            float currentActualSpeed = Vector2.Distance(prevPos, state.Position) / dt;

            // Actual physical displacement speed must match internal speed closely
            float mismatch = MathF.Abs(currentActualSpeed - currentInternalSpeed);
            Assert.True(mismatch <= 0.05f,
                $"Speed mismatch {mismatch:F4} at tick {t}: actual={currentActualSpeed:F4}, internal={currentInternalSpeed:F4}");

            if (currentActualSpeed < prevActualSpeed)
            {
                float decelStep = prevActualSpeed - currentActualSpeed;
                Assert.True(decelStep <= maxAllowedDecel,
                    $"Actual deceleration step {decelStep:F3} m/s at tick {t} exceeded max allowed {maxAllowedDecel:F3} m/s (prev={prevActualSpeed:F3}, curr={currentActualSpeed:F3})");
            }

            if (prevActualSpeed > 0f && currentActualSpeed <= 1e-4f)
            {
                checkedArrival = true;
            }

            prevPos = state.Position;
            prevInternalSpeed = currentInternalSpeed;
            prevActualSpeed = currentActualSpeed;
            if (!state.Moving && currentInternalSpeed <= 1e-4f) break;
        }

        Assert.True(checkedArrival, "Must observe and verify the arrival tick deceleration.");

        // Part 2: Re-assigning close target while cruising at high speed must not drop speed to 0 in one tick
        EntityId fastUnit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 7f, initialTurretYaw: -MathF.PI / 2f);
        sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, fastUnit, new Vector2(50f, 10f)));

        for (int t = 0; t < 50; t++)
        {
            sim.Step();
            if (nav.GetUnitSpeed(fastUnit) >= 5.0f) break;
        }

        float cruiseSpeed = nav.GetUnitSpeed(fastUnit);
        Assert.True(cruiseSpeed >= 5.0f, $"Unit must reach high speed (actual={cruiseSpeed}).");

        world.TryGetUnit(fastUnit, out UnitState fastState);
        Vector2 posBeforeReassign = fastState.Position;
        Vector2 closeTarget = fastState.Position + new Vector2(0.10f, 0f);

        sim.TryEnqueue(1, new MoveCommand(3, sim.Tick, fastUnit, closeTarget));
        sim.Step();

        world.TryGetUnit(fastUnit, out UnitState fastStateAfter);
        float actualSpeedAfter = Vector2.Distance(posBeforeReassign, fastStateAfter.Position) / dt;
        float internalSpeedAfter = nav.GetUnitSpeed(fastUnit);

        float actualDrop = cruiseSpeed - actualSpeedAfter;
        Assert.True(actualDrop <= maxAllowedDecel,
            $"Actual coordinate speed drop {actualDrop:F3} m/s after close re-assignment exceeded max deceleration {maxAllowedDecel:F3} m/s (cruise={cruiseSpeed:F3}, actualAfter={actualSpeedAfter:F3})");

        float internalDrop = cruiseSpeed - internalSpeedAfter;
        Assert.True(internalDrop <= maxAllowedDecel,
            $"Internal speed drop {internalDrop:F3} m/s after close re-assignment exceeded max deceleration {maxAllowedDecel:F3} m/s (cruise={cruiseSpeed:F3}, internalAfter={internalSpeedAfter:F3})");
    }

    [Fact]
    public void WheeledVehicle_AchievableFinalFacing_ArrivesAndAlignsSuccessfully()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f, turnRate: 45f, reverseSpeed: 3f, acceleration: 2f, deceleration: 4f, canPivot: false, minTurnRadius: 5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (50, 70) facing North (0)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(50f, 70f), 6f, initialTurretYaw: 0f);
        Vector2 goal = new(50f, 50f);
        float desiredFacing = 0f; // North

        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, goal, FinalFacing: desiredFacing));

        for (int t = 0; t < 500; t++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            if (t > 0 && !state.Moving) break;
        }

        UnitState finalState = world.GetUnitAt(0);
        world.TryGetCombatState(unit, out WorldState.CombatState cs);

        Assert.False(finalState.Moving, "Unit should come to a complete stop upon arrival.");
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.True(Vector2.Distance(finalState.Position, goal) <= 0.15f,
            $"Final position {finalState.Position} should be within 0.15m of goal {goal}.");
        float headingDiff = MathF.Abs(MathF.IEEERemainder(cs.HullHeading - desiredFacing, MathF.PI * 2f));
        Assert.True(headingDiff <= 0.05f,
            $"Final heading {cs.HullHeading} should align with desired facing {desiredFacing} within 0.05 rad (diff={headingDiff}).");
    }

    [Fact]
    public void WheeledVehicle_UnachievableFinalFacing_AbortsWithInvalidDestinationWithinTimeout()
    {
        const int size = 129;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f, turnRate: 45f, reverseSpeed: 3f, acceleration: 2f, deceleration: 4f, canPivot: false, minTurnRadius: 5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at (50, 50) facing North (0), target (80, 50) with facing North (0)
        // With minTurnRadius = 5m, arriving at (80, 50) while facing North cannot be achieved directly on straight path
        EntityId unit = world.CreateUnit(1, 1, new Vector2(50f, 50f), 6f, initialTurretYaw: 0f);
        Vector2 goal = new(80f, 50f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, goal, FinalFacing: 0f));

        for (int t = 0; t < 500; t++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            if (t > 0 && !state.Moving) break;
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.False(finalState.Moving, "Unit should stop rather than circling infinitely.");
        Assert.Equal(PathRequestStatus.InvalidDestination, nav.GetUnitStatus(unit));
        Assert.True(sim.Tick < 400, $"Simulation should terminate within 400 ticks (actual: {sim.Tick}).");
    }

    [Fact]
    public void NewOrderRejection_PreservedWhenOldRouteFinishesConcurrently()
    {
        const int size = 129;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        float decel = 4f;
        float dt = Simulation.TickSeconds;
        catalog.Register(1, speed: 6f, radius: 1.0f, turnRate: 45f, reverseSpeed: 3f, acceleration: 2f, deceleration: decel, canPivot: false, minTurnRadius: 5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(50f, 80f), 6f, initialTurretYaw: 0f);
        Vector2 goal = new(50f, 50f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, goal));

        bool approachedArrival = false;
        for (int t = 0; t < 1000; t++)
        {
            UnitState u = world.GetUnitAt(0);
            if (nav.HasActiveRoute(unit) &&
                Vector2.Distance(u.Position, goal) <= 0.15f &&
                nav.GetUnitSpeed(unit) <= decel * dt + 1e-4f)
            {
                approachedArrival = true;
                break;
            }
            sim.Step();
        }

        Assert.True(approachedArrival, "Unit must approach arrival point.");

        // Enqueue invalid in-place turn command with Sequence=2
        bool queued = sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, unit, goal, FinalFacing: MathF.PI / 2f));
        Assert.True(queued);

        sim.Step();

        world.TryGetCombatState(unit, out WorldState.CombatState cs);
        Assert.Equal(2u, nav.GetUnitOrderSequence(unit));
        Assert.Equal(PathRequestStatus.InvalidDestination, nav.GetUnitStatus(unit));
        Assert.False(nav.HasActiveRoute(unit));
        Assert.True(MathF.Abs(cs.HullHeading - 0f) < 0.1f, "Hull heading must not snap to invalid requested heading.");
    }

    [Fact]
    public void ReverseOrder_ProfileWithoutReverseSpeed_RejectedByServer()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        // reverseSpeed is 0
        catalog.Register(1, speed: 6f, radius: 1.0f, turnRate: 45f, reverseSpeed: 0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 6f);

        sim.TryEnqueue(1, new MoveCommand(55, 0, unit, new Vector2(20f, 35f), Mode: MoveMode.Reverse));
        sim.Step();

        Assert.Equal(PathRequestStatus.InvalidDestination, nav.GetUnitStatus(unit));
        Assert.Equal(55u, nav.GetUnitOrderSequence(unit));

        UnitState state = world.GetUnitAt(0);
        Assert.False(state.Moving);
        Assert.Equal(new Vector2(20f, 20f), state.Position);
    }

    [Fact]
    public void TightCorridor_TrackedVehicle_CompliesWithTurnRateLimit()
    {
        // Test both standard tracked tank (turnRate=45) and T-62 parameters (turnRate=30, radius=1.65)
        var profiles = new[]
        {
            (Speed: 7f, Radius: 1.0f, TurnRate: 45f, Accel: 3f, Decel: 4f),
            (Speed: 8.3f, Radius: 1.65f, TurnRate: 30f, Accel: 2f, Decel: 4f)
        };

        foreach (var p in profiles)
        {
            const int size = 129;
            HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
            OpaqueStructure[] walls =
            [
                new(new StructureId(1), 0f, 0f, 22f, 38f, 6f, 48f, true),
                new(new StructureId(2), 42f, 0f, 0f, 50f, 6f, 48f, true)
            ];
            StaticGeometry geo = new(map, walls);
            NavigationGrid grid = new(geo, cellSize: 0.5f);

            TestMovementCatalog catalog = new();
            catalog.Register(1, speed: p.Speed, radius: p.Radius, turnRate: p.TurnRate,
                acceleration: p.Accel, deceleration: p.Decel, canPivot: true);

            NavigationSystem nav = new(grid, catalog);
            WorldState world = new(map);
            RuleSet rules = new(nav);
            using Simulation sim = new(world, rules);

            EntityId unit = world.CreateUnit(1, 1, new Vector2(15f, 20f), p.Speed, initialTurretYaw: -MathF.PI / 2f);
            Vector2 goal = new(40f, 60f);
            sim.TryEnqueue(1, new MoveCommand(1, 0, unit, goal));

            float prevYaw = -MathF.PI / 2f;
            float maxAllowedDeg = p.TurnRate * Simulation.TickSeconds + 1e-3f;
            float dt = Simulation.TickSeconds;
            float maxAllowedAccelStep = p.Accel * dt + 1e-3f;
            float maxAllowedDecelStep = p.Decel * dt + 1e-3f;

            Vector2 prevPos = new(15f, 20f);
            float prevActualSpeed = 0f;

            for (int t = 0; t < 1500; t++)
            {
                sim.Step();
                world.TryGetCombatState(unit, out WorldState.CombatState cs);
                float deltaDeg = MathF.Abs(CombatSystem.ShorterArcDelta(prevYaw, cs.HullHeading)) * 180f / MathF.PI;

                Assert.True(deltaDeg <= maxAllowedDeg,
                    $"Turn delta {deltaDeg:F4} deg at tick {t} exceeded limit {maxAllowedDeg:F4} deg for radius={p.Radius}");

                prevYaw = cs.HullHeading;
                UnitState state = world.GetUnitAt(0);

                Vector2 displacement = state.Position - prevPos;
                float actualSpeed = displacement.Length() / dt;

                if (actualSpeed > prevActualSpeed)
                {
                    float accelStep = actualSpeed - prevActualSpeed;
                    Assert.True(accelStep <= maxAllowedAccelStep,
                        $"Actual acceleration step {accelStep:F4} at tick {t} exceeded limit {maxAllowedAccelStep:F4} for radius={p.Radius}");
                }

                if (actualSpeed < prevActualSpeed)
                {
                    float decelStep = prevActualSpeed - actualSpeed;
                    Assert.True(decelStep <= maxAllowedDecelStep,
                        $"Actual deceleration step {decelStep:F4} at tick {t} exceeded limit {maxAllowedDecelStep:F4} for radius={p.Radius}");
                }

                float mismatch = MathF.Abs(actualSpeed - nav.GetUnitSpeed(unit));
                Assert.True(mismatch <= 0.05f,
                    $"Speed mismatch {mismatch:F4} at tick {t} exceeded tolerance 0.05 (actual={actualSpeed:F4}, internal={nav.GetUnitSpeed(unit):F4}) for radius={p.Radius}");

                if (actualSpeed > 0.05f)
                {
                    float motionHeading = MathF.Atan2(-displacement.X, -displacement.Y);
                    float slipDeg = MathF.Abs(CombatSystem.ShorterArcDelta(cs.HullHeading, motionHeading)) * 180f / MathF.PI;
                    Assert.True(slipDeg <= 5.0f,
                        $"Slip angle {slipDeg:F4} deg at tick {t} exceeded limit 5.0 deg for radius={p.Radius}");
                }

                prevPos = state.Position;
                prevActualSpeed = actualSpeed;

                if (t > 0 && !state.Moving) break;
            }

            Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
            UnitState finalState = world.GetUnitAt(0);
            Assert.False(finalState.Moving);
            Assert.True(Vector2.Distance(finalState.Position, goal) <= 0.15f,
                $"Final position {finalState.Position} should be within 0.15m of goal {goal} for radius={p.Radius}.");
        }
    }

    [Fact]
    public void ZeroAccelerationProfile_CompliesWithReverseModeAndSpeedCap()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.0f, turnRate: 45f, reverseSpeed: 3f,
            acceleration: 0f, deceleration: 0f, canPivot: true);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(50f, 50f), 8f, initialTurretYaw: 0f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(50f, 70f), Mode: MoveMode.Reverse));

        float peakSpeed = 0f;
        for (int t = 0; t < 400; t++)
        {
            Vector2 before = world.GetUnitAt(0).Position;
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            float speed = Vector2.Distance(before, state.Position) / Simulation.TickSeconds;
            peakSpeed = MathF.Max(peakSpeed, speed);
            if (t > 0 && !state.Moving) break;
        }

        world.TryGetCombatState(unit, out WorldState.CombatState cs);
        Assert.True(MathF.Abs(cs.HullHeading - 0f) < 0.05f,
            $"Hull heading {cs.HullHeading:F4} should remain North (0), not flip 180 deg.");
        Assert.True(peakSpeed <= 3.01f,
            $"Peak speed {peakSpeed:F4} exceeded reverse cap 3.0 m/s.");
        Assert.Equal(MoveMode.Reverse, nav.GetUnitMoveMode(unit));
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
    }

    [Fact]
    public void WheeledVehicle_SlowSpeedLongApproach_ReachesGoalWithoutPrematureTimeout()
    {
        const int size = 129;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 1f, radius: 1.0f, turnRate: 45f, reverseSpeed: 0.5f,
            acceleration: 0.5f, deceleration: 1f, canPivot: false, minTurnRadius: 5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(50f, 70f), 1f, initialTurretYaw: 0f);
        Vector2 goal = new(50f, 50f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, goal, FinalFacing: 0f));

        for (int t = 0; t < 1500; t++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            if (t > 0 && !state.Moving) break;
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.False(finalState.Moving);
        Assert.True(Vector2.Distance(finalState.Position, goal) <= 0.15f,
            $"Final position {finalState.Position} should be within 0.15m of goal {goal}.");
    }
}
