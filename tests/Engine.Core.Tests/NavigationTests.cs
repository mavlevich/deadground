using System.Numerics;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class NavigationTests
{
    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public TestMovementCatalog Register(ushort definition, float speed, float radius)
        {
            _profiles[definition] = new UnitMovementProfile(speed, radius);
            return _profiles.Count > 0 ? this : this;
        }

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    [Fact]
    public void OpenFieldMovement_ReachesTargetDirectly()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 10f);

        bool enqueued = sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(30f, 30f)));
        Assert.True(enqueued);

        for (int i = 0; i < 80; i++)
        {
            sim.Step();
        }

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.Equal(1, nav.CompletedSearches);
        UnitState finalState = world.GetUnitAt(0);
        Assert.False(finalState.Moving);
        Assert.True(Vector2.Distance(finalState.Position, new Vector2(30f, 30f)) < 0.05f);
    }

    [Fact]
    public void WallBypass_NavigatesAroundObstacleToDestination()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);

        // Wall blocking straight line from (10, 20) to (35, 20)
        OpaqueStructure wall = new(
            new StructureId(1),
            minX: 20f, minY: 0f, minZ: 10f,
            maxX: 22f, maxY: 3f, maxZ: 30f,
            isGrounded: true);

        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 20f), 8f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(35f, 20f)));

        bool traversedAround = false;
        for (int i = 0; i < 150; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);

            // Verify unit NEVER enters interior of the wall
            bool insideWall = state.Position.X > wall.MinX && state.Position.X < wall.MaxX &&
                              state.Position.Y > wall.MinZ && state.Position.Y < wall.MaxZ;
            Assert.False(insideWall, "Unit penetrated obstacle volume.");

            // Check if unit bypassed around north or south
            if (state.Position.Y > 30.5f || state.Position.Y < 9.5f)
            {
                traversedAround = true;
            }
        }

        Assert.True(traversedAround, "Unit did not navigate around the wall bypass.");
        UnitState finalState = world.GetUnitAt(0);
        Assert.False(finalState.Moving);
        Assert.True(Vector2.Distance(finalState.Position, new Vector2(35f, 20f)) < 0.1f);
    }

    [Fact]
    public void NoCornerCutting_MaintainsAgentClearanceAroundVertex()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);

        // Standalone square obstacle at (20..30, 20..30)
        OpaqueStructure obstacle = new(
            new StructureId(1),
            minX: 20f, minY: 0f, minZ: 20f,
            maxX: 30f, maxY: 3f, maxZ: 30f,
            isGrounded: true);

        StaticGeometry geo = new(map, [obstacle]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        const float unitRadius = 1.2f;
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: unitRadius);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Move diagonally across the corner of the obstacle
        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 25f), 6f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(25f, 40f)));

        for (int i = 0; i < 150; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);

            // Compute Euclidean distance from unit center to obstacle box
            float dx = MathF.Max(0f, MathF.Max(obstacle.MinX - state.Position.X, state.Position.X - obstacle.MaxX));
            float dz = MathF.Max(0f, MathF.Max(obstacle.MinZ - state.Position.Y, state.Position.Y - obstacle.MaxZ));
            float distToObstacle = MathF.Sqrt(dx * dx + dz * dz);

            // The unit center must remain at least (unitRadius - tolerance) from the obstacle boundary
            Assert.True(distToObstacle >= unitRadius - 1e-3f,
                $"Unit cut corner! Distance to obstacle was {distToObstacle:F3} < radius {unitRadius:F3} at position {state.Position}.");
        }
    }

    [Fact]
    public void RadiusPassageFiltering_ReconPassesNarrowGap_TankBlocked()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);

        // Narrow passage: gap of 2.2m in X between X=30 and X=32.2 spanning Z in [20, 40]
        // West wall reaches map boundary at X=0
        OpaqueStructure westWall = new(
            new StructureId(1),
            minX: 0f, minY: 0f, minZ: 20f,
            maxX: 30f, maxY: 3f, maxZ: 40f,
            isGrounded: true);

        // East wall reaches map boundary at X=128
        OpaqueStructure eastWall = new(
            new StructureId(2),
            minX: 32.2f, minY: 0f, minZ: 20f,
            maxX: 128f, maxY: 3f, maxZ: 40f,
            isGrounded: true);

        StaticGeometry geo = new(map, [westWall, eastWall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(definition: 1, speed: 7f, radius: 0.8f); // Recon: radius 0.8m, needs clearance 1.6m < 2.2m
        catalog.Register(definition: 2, speed: 5f, radius: 1.4f); // Tank: radius 1.4m, needs clearance 2.8m > 2.2m

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId recon = world.CreateUnit(1, 1, new Vector2(31.1f, 10f), 7f);
        EntityId tank = world.CreateUnit(1, 2, new Vector2(31.1f, 8f), 5f);

        sim.TryEnqueue(1, new MoveCommand(1, 0, recon, new Vector2(31.1f, 50f)));
        sim.TryEnqueue(1, new MoveCommand(2, 0, tank, new Vector2(31.1f, 50f)));

        for (int i = 0; i < 200; i++)
        {
            sim.Step();
        }

        // Recon vehicle must successfully traverse the gap and reach the target
        Assert.True(world.TryGetUnit(recon, out UnitState reconState));
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(recon));
        Assert.True(reconState.Position.Y > 48f, "Recon vehicle failed to pass narrow gap.");

        // Tank cannot pass through the 2.2m gap and must be halted with NoPath
        Assert.True(world.TryGetUnit(tank, out UnitState tankState));
        Assert.Equal(PathRequestStatus.NoPath, nav.GetUnitStatus(tank));
        Assert.False(tankState.Moving);
        Assert.True(tankState.Position.Y < 20f, "Tank erroneously passed or breached narrow gap.");
    }

    [Fact]
    public void UnreachableGoal_DestinationInsideObstacle_ReportsInvalidDestination()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);

        OpaqueStructure building = new(
            new StructureId(1),
            minX: 30f, minY: 0f, minZ: 30f,
            maxX: 44f, maxY: 6f, maxZ: 44f,
            isGrounded: true);

        StaticGeometry geo = new(map, [building]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(15f, 20f), 6f);
        // Destination clicked inside building
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(35f, 35f)));

        sim.Step();

        Assert.Equal(PathRequestStatus.InvalidDestination, nav.GetUnitStatus(unit));
        Assert.Equal(1, nav.FailedSearches);
        UnitState state = world.GetUnitAt(0);
        Assert.False(state.Moving);
        Assert.Equal(new Vector2(15f, 20f), state.Position);
    }

    [Fact]
    public void UnreachableGoal_InsideEnclosedCourtyard_ReportsNoPath()
    {
        const int size = 17; // 32m x 32m map
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);

        // Enclosed courtyard inside [10..22, 10..22]
        OpaqueStructure south = new(new StructureId(1), 10f, 0f, 10f, 22f, 3f, 12f, isGrounded: true);
        OpaqueStructure north = new(new StructureId(2), 10f, 0f, 20f, 22f, 3f, 22f, isGrounded: true);
        OpaqueStructure west = new(new StructureId(3), 10f, 0f, 12f, 12f, 3f, 20f, isGrounded: true);
        OpaqueStructure east = new(new StructureId(4), 20f, 0f, 12f, 22f, 3f, 20f, isGrounded: true);

        StaticGeometry geo = new(map, [south, north, west, east]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(5f, 5f), 6f);
        // Destination inside courtyard interior (16, 16)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(16f, 16f)));

        for (int i = 0; i < 15; i++) sim.Step();

        Assert.Equal(PathRequestStatus.NoPath, nav.GetUnitStatus(unit));
        Assert.Equal(1, nav.FailedSearches);
        UnitState state = world.GetUnitAt(0);
        Assert.False(state.Moving);
        Assert.Equal(new Vector2(5f, 5f), state.Position);
    }

    [Fact]
    public void FastUnitVsThinWall_CannotTunnelOrJumpInSingleTick()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);

        // Thin wall: 0.2m thickness at X in [20, 20.2] spanning entire map depth
        OpaqueStructure thinWall = new(
            new StructureId(1),
            minX: 20f, minY: 0f, minZ: 0f,
            maxX: 20.2f, maxY: 3f, maxZ: 128f,
            isGrounded: true);

        StaticGeometry geo = new(map, [thinWall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        // Speed 100 m/s: travels 5.0m in a single 50ms tick
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 100f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts at X=18, destination at X=25
        EntityId unit = world.CreateUnit(1, 1, new Vector2(18f, 30f), 100f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(25f, 30f)));

        // Advance several ticks
        for (int i = 0; i < 5; i++) sim.Step();

        UnitState state = world.GetUnitAt(0);
        // The unit must NOT have jumped through the wall to X > 20
        Assert.True(state.Position.X < 20f,
            $"Fast unit tunneled through thin wall! Position was {state.Position.X} >= 20.0.");
        Assert.False(state.Moving);
    }

    [Fact]
    public void CommandReplacement_CancelsPendingAndInFlightSearch()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f);

        // Issue initial move order
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(10f, 50f)));
        sim.Step();

        // Immediately supersede with a new command to a different target
        sim.TryEnqueue(1, new MoveCommand(2, 0, unit, new Vector2(50f, 10f)));
        for (int i = 0; i < 150; i++) sim.Step();

        UnitState state = world.GetUnitAt(0);
        Assert.False(state.Moving);
        Assert.True(Vector2.Distance(state.Position, new Vector2(50f, 10f)) < 0.1f);
    }

    [Fact]
    public void UnitRemoval_FreesPendingRequestAndActiveRoute()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(50f, 50f)));

        // Step one tick so request is processed
        sim.Step();

        // Remove unit from world state
        bool removed = world.RemoveUnit(unit);
        Assert.True(removed);

        // Step further
        for (int i = 0; i < 10; i++) sim.Step();

        Assert.Equal(0, world.Count);
        Assert.Equal(0, nav.PendingRequests);
    }

    [Fact]
    public void MultiTickSearchContinuation_BudgetLimitsExpansionsPerTick()
    {
        const int size = 33; // 32m x 32m map, cell size 1.0m
        HeightMap map = new(size, size, 1f, 0.01f, new ushort[size * size]);

        // Wall from Z=0 to Z=20 at X in [15, 17]
        OpaqueStructure wall = new(new StructureId(1), 15f, 0f, 0f, 17f, 3f, 20f, isGrounded: true);

        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f);

        // Set tight budget: only 30 node expansions per tick
        NavigationTuning tuning = new(MaxNodeExpansionsPerTick: 30);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(22f, 10f)));

        sim.Step();

        // Search must have spent the full tick budget and continue in next ticks
        Assert.True(nav.NodesExpandedThisTick <= 30);
        Assert.Equal(1, nav.PendingRequests); // In-flight active search

        int totalTicks = 1;
        while (nav.GetUnitStatus(unit) == PathRequestStatus.Searching && totalTicks < 100)
        {
            sim.Step();
            totalTicks++;
        }

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.True(totalTicks > 1, "Search should have spanned multiple ticks due to node budget.");
        Assert.True(nav.TotalNodesExpanded > 30);
    }

    [Fact]
    public void BoundedBufferOverflow_CapacityExceededCounterIncrements()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f);

        NavigationTuning tuning = new(MaxPendingRequests: 4);
        NavigationSystem nav = new(grid, catalog, tuning);

        for (uint i = 1; i <= 4; i++)
        {
            bool ok = nav.EnqueueCommand(1, new MoveCommand(i, 0, new EntityId(i), new Vector2(30f, 30f)));
            Assert.True(ok);
        }

        Assert.Equal(0, nav.CapacityExceeded);

        // Request #5 exceeds queue capacity of 4
        bool overflow = nav.EnqueueCommand(1, new MoveCommand(5, 0, new EntityId(5), new Vector2(30f, 30f)));
        Assert.False(overflow);
        Assert.Equal(1, nav.CapacityExceeded);
        Assert.Equal(PathRequestStatus.CapacityExceeded, nav.GetUnitStatus(new EntityId(5)));
    }

    [Fact]
    public void RectangularMap_WithPartialEdgeBlocks_NavigatesCorrectly()
    {
        // Rectangular map: columns = 80, rows = 40, cellSize = 1.0m
        const int cols = 81;
        const int rows = 41;
        HeightMap map = new(cols, rows, 1f, 0.01f, new ushort[cols * rows]);

        // Wall flush against west edge at X in [0, 20], Z in [18, 22]
        OpaqueStructure wall = new(
            new StructureId(1),
            minX: 0f, minY: 0f, minZ: 18f,
            maxX: 20f, maxY: 3f, maxZ: 22f,
            isGrounded: true);

        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        Assert.Equal(80f, grid.MapWidth);
        Assert.Equal(40f, grid.MapDepth);
        Assert.Equal(160, grid.Columns);
        Assert.Equal(80, grid.Rows);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 10f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(10f, 30f)));

        for (int i = 0; i < 100; i++) sim.Step();

        UnitState state = world.GetUnitAt(0);
        Assert.False(state.Moving);
        Assert.True(Vector2.Distance(state.Position, new Vector2(10f, 30f)) < 0.1f);
    }

    [Fact]
    public void Determinism_CommandJournalProducesIdenticalStateAndCounters()
    {
        static (Vector2 Position, int TotalNodes, int Completed) RunSimulation()
        {
            HeightMap map = new(65, 65, 2f, 0.01f, new ushort[65 * 65]);
            OpaqueStructure wall = new(new StructureId(1), 30f, 0f, 10f, 32f, 3f, 40f, isGrounded: true);
            StaticGeometry geo = new(map, [wall]);
            NavigationGrid grid = new(geo, cellSize: 0.5f);

            TestMovementCatalog catalog = new();
            catalog.Register(1, speed: 7f, radius: 0.8f);
            catalog.Register(2, speed: 5f, radius: 1.4f);

            NavigationSystem nav = new(grid, catalog);
            WorldState world = new(map);
            RuleSet rules = new(nav);
            using Simulation sim = new(world, rules);

            EntityId u1 = world.CreateUnit(1, 1, new Vector2(15f, 20f), 7f);
            EntityId u2 = world.CreateUnit(1, 2, new Vector2(18f, 20f), 5f);

            sim.TryEnqueue(1, new MoveCommand(1, 0, u1, new Vector2(35f, 20f)));
            sim.TryEnqueue(1, new MoveCommand(2, 0, u2, new Vector2(50f, 20f)));

            for (int i = 0; i < 60; i++)
            {
                sim.Step();
                if (i == 20)
                {
                    sim.TryEnqueue(1, new MoveCommand(3, 0, u1, new Vector2(55f, 35f)));
                }
            }

            return (world.GetUnitAt(0).Position, nav.TotalNodesExpanded, nav.CompletedSearches);
        }

        var run1 = RunSimulation();
        var run2 = RunSimulation();

        Assert.Equal(run1.Position.X, run2.Position.X);
        Assert.Equal(run1.Position.Y, run2.Position.Y);
        Assert.Equal(run1.TotalNodes, run2.TotalNodes);
        Assert.Equal(run1.Completed, run2.Completed);
    }

    [Fact]
    public void ThinWallOffGrid_ContinuousSweptEdgeSafety_NavigatesViaBypass()
    {
        // Grounded thin wall occupying X in [15.49, 15.51], Z in [17.02, 27]
        HeightMap map = new(33, 33, 1f, 0.01f, new ushort[33 * 33]);
        OpaqueStructure thin = new(new StructureId(1), 15.49f, 0f, 17.02f, 15.51f, 3f, 27f, isGrounded: true);
        StaticGeometry geo = new(map, [thin]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 0.8f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(5.25f, 16.25f), 8f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(25.25f, 16.25f)));

        for (int i = 0; i < 400; i++) sim.Step();

        UnitState state = world.GetUnitAt(0);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.False(state.Moving);
        Assert.True(Vector2.Distance(state.Position, new Vector2(25.25f, 16.25f)) < 0.1f,
            $"Expected arrival at target (25.25, 16.25), but stopped at {state.Position}.");
    }

    [Fact]
    public void RouteCapacityExhaustion_SmallCapacity_ReportsCapacityExceededWithoutPartialRoute()
    {
        HeightMap map = new(33, 33, 1f, 0.01f, new ushort[33 * 33]);
        OpaqueStructure wall = new(new StructureId(1), 15f, 0f, 10f, 17f, 3f, 22f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.0f);

        // MaxWaypointsPerRoute = 1 cannot fit detour around wall
        NavigationTuning tuning = new(MaxWaypointsPerRoute: 1);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(5f, 16f), 8f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(26f, 16f)));

        for (int i = 0; i < 100; i++) sim.Step();

        UnitState state = world.GetUnitAt(0);
        Assert.Equal(PathRequestStatus.CapacityExceeded, nav.GetUnitStatus(unit));
        Assert.True(nav.CapacityExceeded >= 1);
        Assert.False(state.Moving);
        // Must not have installed a partial route advancing unit halfway
        Assert.Equal(new Vector2(5f, 16f), state.Position);
    }

    [Fact]
    public void RouteCapacityExhaustion_SnakeMaze_ReportsCapacityExceeded_AndCompletesWhenBufferSufficient()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        OpaqueStructure[] obstacles = new OpaqueStructure[34];
        for (int i = 0; i < obstacles.Length; i++)
        {
            float x = 4f + i * 3.5f;
            obstacles[i] = new OpaqueStructure(
                new StructureId(i + 1),
                x, 0f, (i % 2 == 0 ? 0f : 4f),
                x + 0.5f, 3f, (i % 2 == 0 ? 124f : 128f),
                isGrounded: true);
        }
        StaticGeometry geo = new(map, obstacles);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 500f, radius: 0.8f);

        // Case 1: Default capacity (64 waypoints) cannot fit 34-wall maze
        {
            NavigationSystem nav = new(grid, catalog, new NavigationTuning(MaxWaypointsPerRoute: 64));
            WorldState world = new(map);
            RuleSet rules = new(nav);
            using Simulation sim = new(world, rules);

            EntityId unit = world.CreateUnit(1, 1, new Vector2(2f, 2f), 500f);
            sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(125f, 2f)));

            for (int i = 0; i < 300; i++) sim.Step();

            UnitState state = world.GetUnitAt(0);
            Assert.Equal(PathRequestStatus.CapacityExceeded, nav.GetUnitStatus(unit));
            Assert.True(nav.CapacityExceeded >= 1);
            Assert.False(state.Moving);
        }

        // Case 2: Sufficient capacity (128 waypoints) completes full route
        {
            NavigationSystem nav = new(grid, catalog, new NavigationTuning(MaxWaypointsPerRoute: 128));
            WorldState world = new(map);
            RuleSet rules = new(nav);
            using Simulation sim = new(world, rules);

            EntityId unit = world.CreateUnit(1, 1, new Vector2(2f, 2f), 500f);
            sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(125f, 2f)));

            for (int i = 0; i < 400; i++)
            {
                sim.Step();
                if (nav.PendingRequests == 0 && world.TryGetUnit(unit, out var s) && !s.Moving) break;
            }

            UnitState state = world.GetUnitAt(0);
            Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
            Assert.False(state.Moving);
            Assert.True(Vector2.Distance(state.Position, new Vector2(125f, 2f)) < 1.0f);
        }
    }

    [Fact]
    public void MonotonicEntityIdGrowth_BeyondStorageLimit_ReusesSlotsForLiveUnits()
    {
        HeightMap map = new(33, 33, 1f, 0.01f, new ushort[33 * 33]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Monotonically allocate and remove 4095 temporary units
        for (int i = 0; i < 4095; i++)
        {
            EntityId temp = world.CreateUnit(1, 1, new Vector2(5f, 5f), 8f);
            world.RemoveUnit(temp);
        }

        // Create live unit with EntityId value 4096
        EntityId liveUnit = world.CreateUnit(1, 1, new Vector2(5f, 5f), 8f);
        Assert.True(liveUnit.Value >= 4096);
        Assert.Equal(1, world.Count);

        bool admitted = sim.TryEnqueue(1, new MoveCommand(1, 0, liveUnit, new Vector2(15f, 5f)));
        Assert.True(admitted);

        for (int i = 0; i < 40; i++) sim.Step();

        UnitState state = world.GetUnitAt(0);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(liveUnit));
        Assert.False(state.Moving);
        Assert.True(Vector2.Distance(state.Position, new Vector2(15f, 5f)) < 0.1f);
    }

    [Fact]
    public void CommandReplacement_OnMovingUnit_WithInvalidDestination_ClearsMovingFlag()
    {
        HeightMap map = new(33, 33, 1f, 0.01f, new ushort[33 * 33]);
        OpaqueStructure building = new(new StructureId(1), 12f, 0f, 10f, 22f, 3f, 22f, isGrounded: true);
        StaticGeometry geo = new(map, [building]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(5f, 16f), 8f);

        // Start moving toward (5, 26)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(5f, 26f)));
        sim.Step();

        UnitState before = world.GetUnitAt(0);
        Assert.True(before.Moving);
        Assert.True(before.Position.Y > 16.0f);

        // Supersede order with invalid target deep inside building at (17, 16)
        sim.TryEnqueue(1, new MoveCommand(2, 0, unit, new Vector2(17f, 16f)));
        sim.Step();

        UnitState after = world.GetUnitAt(0);
        Assert.False(after.Moving);
        Assert.Equal(PathRequestStatus.InvalidDestination, nav.GetUnitStatus(unit));
        Assert.Equal(before.Position, after.Position);
    }

    [Fact]
    public void CommandReplacement_OnMovingUnit_WithNoPath_ClearsMovingFlag()
    {
        HeightMap map = new(33, 33, 1f, 0.01f, new ushort[33 * 33]);
        // Enclosed courtyard from X in [20, 30], Z in [20, 30]
        OpaqueStructure s1 = new(new StructureId(1), 20f, 0f, 20f, 30f, 3f, 21f, isGrounded: true);
        OpaqueStructure s2 = new(new StructureId(2), 20f, 0f, 29f, 30f, 3f, 30f, isGrounded: true);
        OpaqueStructure s3 = new(new StructureId(3), 20f, 0f, 21f, 21f, 3f, 29f, isGrounded: true);
        OpaqueStructure s4 = new(new StructureId(4), 29f, 0f, 21f, 30f, 3f, 29f, isGrounded: true);
        StaticGeometry geo = new(map, [s1, s2, s3, s4]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.8f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(5f, 5f), 6f);

        // Start moving toward open field (5, 25)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(5f, 25f)));
        sim.Step();

        UnitState before = world.GetUnitAt(0);
        Assert.True(before.Moving);

        // Replace with target inside enclosed courtyard at (25, 25)
        sim.TryEnqueue(1, new MoveCommand(2, 0, unit, new Vector2(25f, 25f)));
        for (int i = 0; i < 20; i++) sim.Step();

        UnitState after = world.GetUnitAt(0);
        Assert.False(after.Moving);
        Assert.Equal(PathRequestStatus.NoPath, nav.GetUnitStatus(unit));
    }

    [Fact]
    public void CommandReplacement_CancelsInFlightMultiTickSearch()
    {
        HeightMap map = new(33, 33, 1f, 0.01f, new ushort[33 * 33]);
        OpaqueStructure wall = new(new StructureId(1), 15f, 0f, 0f, 17f, 3f, 25f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.8f);

        // Very small expansion budget: 5 nodes per tick forces multi-tick search
        NavigationTuning tuning = new(MaxNodeExpansionsPerTick: 5);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f);

        // Issue search requiring detour around wall
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(22f, 10f)));
        sim.Step();

        Assert.Equal(PathRequestStatus.Searching, nav.GetUnitStatus(unit));

        // Immediately supersede with direct open field move order
        sim.TryEnqueue(1, new MoveCommand(2, 0, unit, new Vector2(10f, 25f)));
        sim.Step();

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
    }

    [Fact]
    public void UnitRemoval_WhileSearchInFlight_SafelyAborts()
    {
        HeightMap map = new(33, 33, 1f, 0.01f, new ushort[33 * 33]);
        OpaqueStructure wall = new(new StructureId(1), 15f, 0f, 0f, 17f, 3f, 25f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.8f);

        NavigationTuning tuning = new(MaxNodeExpansionsPerTick: 5);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(22f, 10f)));
        sim.Step();

        Assert.Equal(PathRequestStatus.Searching, nav.GetUnitStatus(unit));

        // Remove unit while search is in-flight
        world.RemoveUnit(unit);
        sim.Step();

        Assert.Equal(0, nav.PendingRequests);
        Assert.Equal(PathRequestStatus.None, nav.GetUnitStatus(unit));
    }

    [Fact]
    public void ValidGoalNearObstacle_ContainingCellCenterBlocked_NavigatesSuccessfully()
    {
        // Reproduction scenario: flat 128 m map, building bounds X=30..44 and Z=30..44,
        // tank radius 1.4 m, start (50, 37), target (28.55, 37).
        const int size = 129;
        HeightMap map = new(size, size, 1f, 0.01f, new ushort[size * size]);
        OpaqueStructure building = new(
            new StructureId(1),
            minX: 30f, minY: 0f, minZ: 30f,
            maxX: 44f, maxY: 3f, maxZ: 44f,
            isGrounded: true);

        StaticGeometry geo = new(map, [building]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        const float tankRadius = 1.4f;
        const float tankSpeed = 6.0f;
        catalog.Register(1, speed: tankSpeed, radius: tankRadius);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        Vector2 start = new(50f, 37f);
        Vector2 target = new(28.55f, 37f);

        // Verify precondition: target has point clearance, but its containing cell center is blocked
        Assert.True(grid.IsPointClear(target, tankRadius), "Target point should have valid clearance.");
        (int goalCol, int goalRow) = grid.WorldToCell(target);
        Assert.False(grid.IsCellPassable(goalCol, goalRow, tankRadius), "Target containing cell center should be blocked.");

        EntityId tank = world.CreateUnit(1, 1, start, tankSpeed);
        bool enqueued = sim.TryEnqueue(1, new MoveCommand(1, 0, tank, target));
        Assert.True(enqueued);

        bool reached = false;
        for (int i = 0; i < 200; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);

            // Unit must never enter obstacle interior
            bool insideObstacle = state.Position.X > building.MinX && state.Position.X < building.MaxX &&
                                  state.Position.Y > building.MinZ && state.Position.Y < building.MaxZ;
            Assert.False(insideObstacle, "Tank penetrated building interior.");

            if (Vector2.Distance(state.Position, target) < 0.1f)
            {
                reached = true;
                break;
            }
        }

        Assert.True(reached, "Tank did not reach valid goal near obstacle.");
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(tank));
    }

    [Theory]
    [InlineData(28.55f, 37f, 50f, 37f)] // West face
    [InlineData(45.45f, 37f, 20f, 37f)] // East face
    [InlineData(37f, 28.55f, 37f, 50f)] // South face
    [InlineData(37f, 45.45f, 37f, 20f)] // North face
    public void ValidGoalNearObstacle_DifferentFaces_ContainingCellCenterBlocked_NavigatesSuccessfully(
        float targetX, float targetZ, float startX, float startZ)
    {
        const int size = 129;
        HeightMap map = new(size, size, 1f, 0.01f, new ushort[size * size]);
        OpaqueStructure building = new(
            new StructureId(1),
            minX: 30f, minY: 0f, minZ: 30f,
            maxX: 44f, maxY: 3f, maxZ: 44f,
            isGrounded: true);

        StaticGeometry geo = new(map, [building]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        const float tankRadius = 1.4f;
        const float tankSpeed = 6.0f;
        catalog.Register(1, speed: tankSpeed, radius: tankRadius);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        Vector2 start = new(startX, startZ);
        Vector2 target = new(targetX, targetZ);

        Assert.True(grid.IsPointClear(target, tankRadius));
        (int goalCol, int goalRow) = grid.WorldToCell(target);
        Assert.False(grid.IsCellPassable(goalCol, goalRow, tankRadius));

        EntityId tank = world.CreateUnit(1, 1, start, tankSpeed);
        sim.TryEnqueue(1, new MoveCommand(1, 0, tank, target));

        bool reached = false;
        for (int i = 0; i < 200; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            if (Vector2.Distance(state.Position, target) < 0.1f)
            {
                reached = true;
                break;
            }
        }

        Assert.True(reached, $"Tank did not reach destination ({targetX}, {targetZ}).");
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(tank));
    }

    [Fact]
    public void DirectOrder_DoesNotWaitBehindHeavyInFlightSearch()
    {
        HeightMap map = new(33, 33, 1f, 0.01f, new ushort[33 * 33]);
        OpaqueStructure wall = new(new StructureId(1), 15f, 0f, 0f, 17f, 3f, 25f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.8f);

        // Very small expansion budget: 5 nodes per tick forces multi-tick search for Unit 1
        NavigationTuning tuning = new(MaxNodeExpansionsPerTick: 5);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit1 = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f);
        EntityId unit2 = world.CreateUnit(1, 1, new Vector2(5f, 5f), 6f);

        // Unit 1 issues heavy search around the wall
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit1, new Vector2(22f, 10f)));
        sim.Step();

        Assert.Equal(PathRequestStatus.Searching, nav.GetUnitStatus(unit1));

        // Unit 2 issues direct open-field move order
        sim.TryEnqueue(1, new MoveCommand(2, 0, unit2, new Vector2(5f, 20f)));
        sim.Step();

        // Unit 2 must start moving immediately on this tick, without waiting for Unit 1's search
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit2));
        Assert.True(world.TryGetUnit(unit2, out UnitState u2State) && u2State.Moving);

        // Unit 1's search should still be in progress
        Assert.Equal(PathRequestStatus.Searching, nav.GetUnitStatus(unit1));
    }

    [Fact]
    public void SweepRemovedUnits_WithPredominantlyIdleWorld_RecyclesSlots()
    {
        const int size = 33;
        HeightMap map = new(size, size, 1f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.8f);

        // Setup world with capacity 1024 and nav with maxUnits 1024
        const int capacity = 1024;
        WorldState world = new(map, capacity: capacity);
        NavigationSystem nav = new(grid, catalog, maxUnits: capacity);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Create 1023 permanent idle units
        for (int i = 0; i < capacity - 1; i++)
        {
            world.CreateUnit(1, 1, new Vector2(5f, 5f), 6f);
        }
        Assert.Equal(capacity - 1, world.Count);

        // Repeatedly create one unit, issue move command, step, and remove it for 1100 cycles
        for (int cycle = 0; cycle < 1100; cycle++)
        {
            EntityId tempUnit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f);
            bool enqueued = sim.TryEnqueue(1, new MoveCommand((uint)(cycle + 1), 0, tempUnit, new Vector2(10f, 10f)));
            Assert.True(enqueued, $"Failed to enqueue move command at cycle {cycle}.");

            sim.Step();
            Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(tempUnit));

            world.RemoveUnit(tempUnit);
            sim.Step();
        }

        // Verify zero capacity exceeded
        Assert.Equal(0, nav.CapacityExceeded);

        // The 1101st unit must acquire a slot without error and navigate
        EntityId nextUnit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f);
        bool admitted = sim.TryEnqueue(1, new MoveCommand(2000, 0, nextUnit, new Vector2(20f, 20f)));
        Assert.True(admitted);

        sim.Step();
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(nextUnit));
        Assert.True(world.TryGetUnit(nextUnit, out UnitState nextState) && nextState.Moving);
        Assert.Equal(0, nav.CapacityExceeded);
    }

    [Fact]
    public void NavigationGrid_ExceedingMaxSupportedCells_Throws()
    {
        // 1025 x 1025 map with 0.5 m cells creates 2048 x 2048 = 4,194,304 cells > 262,144 limit
        const int size = 1025;
        HeightMap largeMap = new(size, size, 1f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(largeMap, []);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => new NavigationGrid(geo, cellSize: 0.5f));
        Assert.Contains("exceeds flat prototype limit", ex.Message);
    }

    [Fact]
    public void QueueCompaction_FullQueue_TailRemainsInBounds_AllowsSubsequentEnqueue()
    {
        // Reproduction of Finding [P1]:
        // Wall dividing map, 256 units on one side, 256 commands requiring A* detour.
        const int size = 65;
        HeightMap map = new(size, size, 1f, 0.01f, new ushort[size * size]);
        OpaqueStructure wall = new(new StructureId(1), 30f, 0f, 0f, 32f, 3f, 50f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.8f);

        // Tuning with MaxPendingRequests = 256, and MaxNodeExpansionsPerTick = 1 to keep search in flight
        NavigationTuning tuning = new(MaxPendingRequests: 256, MaxNodeExpansionsPerTick: 1);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map, capacity: 512);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId[] units = new EntityId[256];
        for (int i = 0; i < 256; i++)
        {
            units[i] = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f);
            bool enqueued = sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), 0, units[i], new Vector2(40f, 10f)));
            Assert.True(enqueued);
        }

        // Step once: ProcessImmediateOrders compacts all 256 requests.
        // ProcessPlanning dequeues 1 request to start in-flight search.
        // Queue now has 255 items, tail must be at a valid index (0), not 256!
        sim.Step();

        // Enqueue the 257th unit and command (direct open field move)
        EntityId extraUnit = world.CreateUnit(1, 1, new Vector2(5f, 5f), 6f);
        bool extraEnqueued = sim.TryEnqueue(1, new MoveCommand(500, 0, extraUnit, new Vector2(5f, 20f)));
        Assert.True(extraEnqueued);

        // Stepping simulation must NOT throw IndexOutOfRangeException
        sim.Step();

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(extraUnit));
        Assert.True(world.TryGetUnit(extraUnit, out UnitState extraState) && extraState.Moving);
    }

    [Fact]
    public void QueueCompaction_SupportedCapacity4_TailRemainsInBounds_AllowsSubsequentEnqueue()
    {
        const int size = 33;
        HeightMap map = new(size, size, 1f, 0.01f, new ushort[size * size]);
        OpaqueStructure wall = new(new StructureId(1), 15f, 0f, 0f, 17f, 3f, 25f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.8f);

        NavigationTuning tuning = new(MaxPendingRequests: 4, MaxNodeExpansionsPerTick: 1);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map, capacity: 64);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        for (int i = 0; i < 4; i++)
        {
            EntityId u = world.CreateUnit(1, 1, new Vector2(5f, 5f), 6f);
            sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), 0, u, new Vector2(25f, 5f)));
        }

        sim.Step();

        EntityId extra = world.CreateUnit(1, 1, new Vector2(5f, 5f), 6f);
        bool enqueued = sim.TryEnqueue(1, new MoveCommand(100, 0, extra, new Vector2(5f, 20f)));
        Assert.True(enqueued);

        sim.Step();
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(extra));
    }

    [Fact]
    public void QueueCompaction_FillDrainRefill_WraparoundHandledCorrectly()
    {
        const int size = 33;
        HeightMap map = new(size, size, 1f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.8f);

        NavigationTuning tuning = new(MaxPendingRequests: 8);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map, capacity: 128);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        uint seq = 1;
        for (int cycle = 0; cycle < 10; cycle++)
        {
            EntityId[] batch = new EntityId[6];
            for (int i = 0; i < 6; i++)
            {
                batch[i] = world.CreateUnit(1, 1, new Vector2(5f, 5f), 6f);
                Assert.True(sim.TryEnqueue(1, new MoveCommand(seq++, 0, batch[i], new Vector2(10f + i, 10f))));
            }

            sim.Step();

            for (int i = 0; i < 6; i++)
            {
                Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(batch[i]));
            }
        }
    }

    [Fact]
    public void OldQueuedOrder_CannotSupersedeNewSessionReplacementOrder_DirectSimulation()
    {
        // Reproduction of Finding [P2]:
        // 1. Another unit occupies single A* context with multi-tick search.
        // 2. Player 1's sequence 100 order for its unit remains pending behind that search.
        // 3. ResetPlayer(1) called when session disconnects / reconnects.
        // 4. Replacement order sequence 1 issued for the same unit to a direct target (5, 20).
        // 5. Verify the unit reaches (5, 20) and the old sequence 100 order never installs or redirects it.
        HeightMap map = new(33, 33, 1f, 0.01f, new ushort[33 * 33]);
        OpaqueStructure wall = new(new StructureId(1), 15f, 0f, 0f, 17f, 3f, 25f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.8f);

        NavigationTuning tuning = new(MaxNodeExpansionsPerTick: 5);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId blockerUnit = world.CreateUnit(2, 2, new Vector2(10f, 10f), 6f);
        EntityId player1Unit = world.CreateUnit(1, 1, new Vector2(5f, 5f), 6f);

        // Step 1: Blocker unit occupies the active search
        sim.TryEnqueue(2, new MoveCommand(1, 0, blockerUnit, new Vector2(22f, 10f)));
        sim.Step();
        Assert.Equal(PathRequestStatus.Searching, nav.GetUnitStatus(blockerUnit));

        // Step 2: Player 1 enqueues sequence 100 order requiring detour to (26, 10)
        sim.TryEnqueue(1, new MoveCommand(100, 0, player1Unit, new Vector2(26f, 10f)));
        sim.Step();
        Assert.Equal(PathRequestStatus.Pending, nav.GetUnitStatus(player1Unit));

        // Step 3: Session disconnects / resets Player 1
        sim.ResetPlayer(1);

        // Step 4: Replacement session sends sequence 1 for the same unit to direct target (5, 20)
        bool enqueued = sim.TryEnqueue(1, new MoveCommand(1, 0, player1Unit, new Vector2(5f, 20f)));
        Assert.True(enqueued);

        sim.Step();
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(player1Unit));
        Assert.True(world.TryGetUnit(player1Unit, out UnitState state) && state.Moving);

        // Step 5: Advance simulation until unit finishes moving
        for (int i = 0; i < 150; i++)
        {
            sim.Step();
        }

        UnitState finalState = world.GetUnitAt(1);
        Assert.False(finalState.Moving);
        Assert.True(Vector2.Distance(finalState.Position, new Vector2(5f, 20f)) < 0.1f,
            $"Unit did not reach replacement destination (5, 20), actual position: {finalState.Position}");
        Assert.False(Vector2.Distance(finalState.Position, new Vector2(26f, 10f)) < 5.0f,
            "Unit was diverted to obsolete route toward (26, 10).");
    }

    [Fact]
    public void AStar_IndexedHeap_MaintainsInvariantAndMatchesOptimalShortestPath()
    {
        // Construct a grid with obstacle configuration where multiple paths converge
        const int size = 33;
        HeightMap map = new(size, size, 1f, 0.01f, new ushort[size * size]);

        // Wall forming a bottleneck requiring detour
        OpaqueStructure wall1 = new(new StructureId(1), 10f, 0f, 5f, 12f, 3f, 20f, isGrounded: true);
        OpaqueStructure wall2 = new(new StructureId(2), 16f, 0f, 12f, 18f, 3f, 27f, isGrounded: true);

        StaticGeometry geo = new(map, [wall1, wall2]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        Vector2 start = new(5f, 10f);
        Vector2 target = new(25f, 15f);

        float dijkstraCost = RunIndependentDijkstra(grid, start, target, radius: 0.5f);
        Assert.True(float.IsFinite(dijkstraCost));

        EntityId unit = world.CreateUnit(1, 1, start, 6f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));

        for (int i = 0; i < 150; i++)
        {
            sim.Step();
        }

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.Equal(1, nav.CompletedSearches);
        UnitState state = world.GetUnitAt(0);
        Assert.False(state.Moving);
        Assert.True(Vector2.Distance(state.Position, target) < 0.1f);
    }

    [Fact]
    public void AStar_IndexedHeap_MatchesDijkstraDifferential_OnRandomObstacleMaps()
    {
        // Deterministic differential test across random obstacle configurations (including seed 900721)
        Random rng = new(900721);
        const int size = 33;
        const float radius = 0.2f;
        Vector2 start = new(2.5f, 2.5f);
        Vector2 target = new(29.5f, 29.5f);

        for (int mapIdx = 0; mapIdx < 15; mapIdx++)
        {
            HeightMap map = new(size, size, 1f, 0.01f, new ushort[size * size]);
            List<OpaqueStructure> structures = [];
            for (int s = 0; s < 18; s++)
            {
                float x1 = (float)(rng.NextDouble() * 24.0 + 3.0);
                float z1 = (float)(rng.NextDouble() * 24.0 + 3.0);
                float w = (float)(rng.NextDouble() * 4.0 + 1.0);
                float h = (float)(rng.NextDouble() * 4.0 + 1.0);
                structures.Add(new OpaqueStructure(
                    new StructureId(s + 1),
                    x1, 0f, z1, x1 + w, 3f, z1 + h,
                    isGrounded: true));
            }

            StaticGeometry geo = new(map, structures);
            NavigationGrid grid = new(geo, cellSize: 1.0f);

            float dijkstraCost = RunIndependentDijkstra(grid, start, target, radius);
            if (float.IsInfinity(dijkstraCost)) continue;

            TestMovementCatalog catalog = new();
            catalog.Register(1, speed: 6f, radius: radius);

            NavigationSystem nav = new(grid, catalog);
            WorldState world = new(map);
            RuleSet rules = new(nav);
            using Simulation sim = new(world, rules);

            EntityId unit = world.CreateUnit(1, 1, start, 6f);
            sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));

            for (int step = 0; step < 200; step++)
            {
                sim.Step();
                if (nav.GetUnitStatus(unit) == PathRequestStatus.PathFound) break;
            }

            Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        }
    }

    private static float RunIndependentDijkstra(NavigationGrid grid, Vector2 start, Vector2 target, float radius)
    {
        int totalCells = grid.TotalCells;
        int virtualGoal = totalCells;
        float[] dist = new float[totalCells + 1];
        Array.Fill(dist, float.PositiveInfinity);
        bool[] visited = new bool[totalCells + 1];

        // Start anchors
        (int startCol, int startRow) = grid.WorldToCell(start);
        int startCell = grid.CellIndex(startCol, startRow);
        if (grid.IsInBounds(startCol, startRow) &&
            grid.IsCellPassable(startCol, startRow, radius) &&
            grid.IsSegmentClear(start, grid.CellCenter(startCell), radius))
        {
            dist[startCell] = Vector2.Distance(start, grid.CellCenter(startCell));
        }
        else
        {
            int startWindow = Math.Clamp((int)MathF.Ceiling((radius + 1.5f * grid.CellSize) / grid.CellSize), 2, 5);
            for (int dr = -startWindow; dr <= startWindow; dr++)
            {
                int sr = startRow + dr;
                if (sr < 0 || sr >= grid.Rows) continue;
                for (int dc = -startWindow; dc <= startWindow; dc++)
                {
                    int sc = startCol + dc;
                    if (sc < 0 || sc >= grid.Columns) continue;
                    if (!grid.IsCellPassable(sc, sr, radius)) continue;
                    Vector2 c = grid.CellCenter(sc, sr);
                    if (!grid.IsSegmentClear(start, c, radius)) continue;
                    dist[grid.CellIndex(sc, sr)] = Vector2.Distance(start, c);
                }
            }
        }

        // Goal anchors
        (int goalCol, int goalRow) = grid.WorldToCell(target);
        int goalWindow = Math.Clamp((int)MathF.Ceiling((radius + 1.5f * grid.CellSize) / grid.CellSize), 2, 5);
        bool[] isGoalAnchor = new bool[totalCells];
        for (int dr = -goalWindow; dr <= goalWindow; dr++)
        {
            int gr = goalRow + dr;
            if (gr < 0 || gr >= grid.Rows) continue;
            for (int dc = -goalWindow; dc <= goalWindow; dc++)
            {
                int gc = goalCol + dc;
                if (gc < 0 || gc >= grid.Columns) continue;
                if (!grid.IsCellPassable(gc, gr, radius)) continue;
                Vector2 c = grid.CellCenter(gc, gr);
                if (!grid.IsSegmentClear(c, target, radius)) continue;
                isGoalAnchor[grid.CellIndex(gc, gr)] = true;
            }
        }

        (int dCol, int dRow, bool isDiag, int c1c, int c1r, int c2c, int c2r)[] neighbors =
        [
            ( 0,  1, false,  0, 0,  0, 0),
            ( 1,  0, false,  0, 0,  0, 0),
            ( 0, -1, false,  0, 0,  0, 0),
            (-1,  0, false,  0, 0,  0, 0),
            ( 1,  1, true,   1, 0,  0, 1),
            ( 1, -1, true,   1, 0,  0,-1),
            (-1, -1, true,  -1, 0,  0,-1),
            (-1,  1, true,  -1, 0,  0, 1),
        ];

        while (true)
        {
            int u = -1;
            float bestDist = float.PositiveInfinity;
            for (int i = 0; i <= totalCells; i++)
            {
                if (!visited[i] && dist[i] < bestDist)
                {
                    bestDist = dist[i];
                    u = i;
                }
            }

            if (u == -1 || float.IsPositiveInfinity(bestDist)) break;
            if (u == virtualGoal) return bestDist;

            visited[u] = true;

            (int col, int row) = grid.CellCoordinates(u);
            Vector2 uCenter = grid.CellCenter(col, row);
            float uClearance = grid.GetCellClearance(col, row);

            if (isGoalAnchor[u])
            {
                float costToGoal = bestDist + Vector2.Distance(uCenter, target);
                if (costToGoal < dist[virtualGoal])
                {
                    dist[virtualGoal] = costToGoal;
                }
            }

            for (int i = 0; i < neighbors.Length; i++)
            {
                var n = neighbors[i];
                int nc = col + n.dCol;
                int nr = row + n.dRow;
                if (!grid.IsInBounds(nc, nr)) continue;
                if (n.isDiag)
                {
                    int c1c = col + n.c1c;
                    int c1r = row + n.c1r;
                    int c2c = col + n.c2c;
                    int c2r = row + n.c2r;
                    if (!grid.IsCellPassable(c1c, c1r, radius) || !grid.IsCellPassable(c2c, c2r, radius)) continue;
                }
                if (!grid.IsCellPassable(nc, nr, radius)) continue;

                int v = grid.CellIndex(nc, nr);
                if (visited[v]) continue;

                float stepCost = n.isDiag ? 1.41421356f * grid.CellSize : grid.CellSize;
                float nextClearance = grid.GetCellClearance(nc, nr);
                float safeThresh = radius + stepCost * 0.5f;
                if (uClearance < safeThresh || nextClearance < safeThresh)
                {
                    Vector2 vCenter = grid.CellCenter(nc, nr);
                    if (!grid.IsSegmentClear(uCenter, vCenter, radius)) continue;
                }

                float alt = bestDist + stepCost;
                if (alt < dist[v])
                {
                    dist[v] = alt;
                }
            }
        }

        return dist[virtualGoal];
    }

    [Fact]
    public void ReorderedMovingUnit_MovesEveryTickDuringSearch_UntilNewRouteApplied()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);

        // Long wall blocking direct path from (10..25, 20) to (50, 20)
        OpaqueStructure wall = new(
            new StructureId(1),
            minX: 30f, minY: 0f, minZ: 0f,
            maxX: 32f, maxY: 6f, maxZ: 50f,
            isGrounded: true);

        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f);

        // Limit expansions per tick to 200 so A* search spans several ticks
        NavigationTuning tuning = new(MaxNodeExpansionsPerTick: 200);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 20f), 6f);

        // 1. Initial direct move toward (25, 20)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(25f, 20f)));
        sim.Step();

        UnitState s1 = world.GetUnitAt(0);
        Assert.True(s1.Moving);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));

        // Advance two more ticks so unit is actively traveling along route
        sim.Step();
        sim.Step();
        UnitState s2 = world.GetUnitAt(0);
        Assert.True(s2.Moving);
        Assert.True(s2.Position.X > s1.Position.X);

        // 2. Re-order unit to (50, 20), on the far side of the wall
        sim.TryEnqueue(1, new MoveCommand(2, 0, unit, new Vector2(50f, 20f)));

        int searchTicks = 0;
        Vector2 lastPos = s2.Position;

        // Step simulation tick-by-tick while search is pending or in progress
        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            UnitState current = world.GetUnitAt(0);
            PathRequestStatus status = nav.GetUnitStatus(unit);

            if (status == PathRequestStatus.Searching)
            {
                searchTicks++;
                float movedThisTick = Vector2.Distance(lastPos, current.Position);
                // Assert the unit moved every tick during search and did not stop dead
                Assert.True(movedThisTick > 0.05f,
                    $"Unit stopped dead on tick {i} during search: moved {movedThisTick:F4}m");
                Assert.True(current.Moving, $"Unit was marked not moving on tick {i} during search.");
            }

            lastPos = current.Position;
            if (status == PathRequestStatus.PathFound)
            {
                break;
            }
        }

        // Verify the search actually spanned multiple ticks
        Assert.True(searchTicks >= 2, $"Expected search to take multiple ticks, took {searchTicks}");
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.True(world.GetUnitAt(0).Moving);
    }

    [Fact]
    public void DestinationSnapping_TargetHalfMeterFromWall_SnapsToClearPointAndIsReached()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);

        // Wall from x=30 to x=31, z=15 to z=35
        OpaqueStructure wall = new(
            new StructureId(1),
            minX: 30f, minY: 0f, minZ: 15f,
            maxX: 31f, maxY: 6f, maxZ: 35f,
            isGrounded: true);

        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        // Tank with radius 1.5 m
        catalog.Register(1, speed: 6f, radius: 1.5f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId tank = world.CreateUnit(1, 1, new Vector2(10f, 25f), 6f);

        // Target at (31.5, 25) is 0.5m from wall face at x=31.
        // Tank radius is 1.5m, so clearance at target is 0.5m < 1.5m (target is not clear).
        Vector2 rawTarget = new(31.5f, 25f);
        Assert.False(grid.IsPointClear(rawTarget, 1.5f));

        sim.TryEnqueue(1, new MoveCommand(1, 0, tank, rawTarget));
        sim.Step();

        // Should snap destination and find path
        Assert.True(nav.GetUnitStatus(tank) is PathRequestStatus.PathFound or PathRequestStatus.Searching);

        // Run until tank arrives and stops
        for (int i = 0; i < 200; i++)
        {
            sim.Step();
            if (!world.GetUnitAt(0).Moving && nav.GetUnitStatus(tank) == PathRequestStatus.PathFound)
            {
                break;
            }
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.False(finalState.Moving);
        // Arrived at a clear point
        Assert.True(grid.IsPointClear(finalState.Position, 1.5f),
            $"Tank ended at non-clear position: {finalState.Position}");
        // Distance to requested target is within snap radius (3.0 m)
        float snapDist = Vector2.Distance(rawTarget, finalState.Position);
        Assert.True(snapDist <= 3.0f, $"Snapped destination too far from target: {snapDist:F2}m");
    }

    [Fact]
    public void DestinationDeepInsideBuilding_ReportsInvalidDestinationAndHalts()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);

        // Building from x=30 to x=50, z=30 to z=50 (20x20m)
        OpaqueStructure building = new(
            new StructureId(1),
            minX: 30f, minY: 0f, minZ: 30f,
            maxX: 50f, maxY: 6f, maxZ: 50f,
            isGrounded: true);

        StaticGeometry geo = new(map, [building]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 10f), 6f);

        // Target at (40, 40) is in center of building, 10m from all edges (> 3.0m snap distance)
        Vector2 deepTarget = new(40f, 40f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, deepTarget));
        sim.Step();

        Assert.Equal(PathRequestStatus.InvalidDestination, nav.GetUnitStatus(unit));
        Assert.False(world.GetUnitAt(0).Moving);
        Assert.Equal(new Vector2(10f, 10f), world.GetUnitAt(0).Position);
    }

    [Fact]
    public void DestinationSnapping_ClickingOnWall_SnapsToSameSideAsUnit()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);

        // Long wall 30m in length: x in [30..32], z in [5..35]
        OpaqueStructure wall = new(
            new StructureId(1),
            minX: 30f, minY: 0f, minZ: 5f,
            maxX: 32f, maxY: 6f, maxZ: 35f,
            isGrounded: true);

        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts on West side of wall
        EntityId unit = world.CreateUnit(1, 1, new Vector2(10f, 20f), 6f);

        // Player clicks directly on the wall at (31, 20)
        Vector2 clickOnWall = new(31f, 20f);
        Assert.False(grid.IsPointClear(clickOnWall, 1.0f));

        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, clickOnWall));
        sim.Step();

        // Destination must snap to the West side (x < 30), facing the unit
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));

        // Advance simulation until unit arrives
        for (int i = 0; i < 100; i++)
        {
            sim.Step();
            if (!world.GetUnitAt(0).Moving) break;
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.False(finalState.Moving);
        // Unit ended on West side of the wall (x < 30), without circumnavigating the 30m wall
        Assert.True(finalState.Position.X < 30.0f,
            $"Expected unit to stay on West side of wall, arrived at: {finalState.Position}");
        Assert.True(grid.IsPointClear(finalState.Position, 1.0f));
    }
}
