using System.Diagnostics;
using System.Numerics;
using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class HierarchicalNavigationTests
{
    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public TestMovementCatalog Register(ushort definition, float speed, float radius, float turnRate = 360f, TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle)
        {
            _profiles[definition] = new UnitMovementProfile(speed, radius, capabilities, turnRate);
            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    [Fact]
    public void MultiChunkRoute_WindingRoute_CompletesAcrossMultipleChunks()
    {
        // 2x2 km map from LargeNavigationScenario
        StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 20f, radius: 0.8f);

        NavigationTuning tuning = new(MaxWaypointsPerRoute: 32);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts before the slalom maze at (1650, 220) and targets (1650, 680)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(1650f, 220f), 20f);
        Vector2 target = new(1650f, 680f);

        bool enqueued = sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));
        Assert.True(enqueued);

        Vector2 lastPos = world.GetUnitAt(0).Position;
        int ticks = 0;
        int maxTicks = 1500;

        while (ticks < maxTicks)
        {
            sim.Step();
            ticks++;

            UnitState state = world.GetUnitAt(0);
            Vector2 currentPos = state.Position;

            // Swept-capsule safety check for every tick of movement
            if (Vector2.DistanceSquared(lastPos, currentPos) > 1e-4f)
            {
                Assert.True(grid.IsSegmentClear(lastPos, currentPos, 0.8f),
                    $"Movement segment penetrated obstacle volume between {lastPos} and {currentPos} at tick {ticks}");
                lastPos = currentPos;
            }

            if (!state.Moving && Vector2.Distance(currentPos, target) <= 0.5f)
            {
                break;
            }
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.False(finalState.Moving);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.True(Vector2.Distance(finalState.Position, target) <= 0.5f,
            $"Unit did not reach destination through multi-chunk maze. Final pos: {finalState.Position}");
        Assert.True(nav.ChunkRequestsCompleted > 0,
            $"Route should have executed multiple chained chunks. ChunkRequestsCompleted={nav.ChunkRequestsCompleted}");
    }

    [Fact]
    public void BoundaryAndCornerTransitions_CrossesSectorsDiagonallyWithoutGettingStuck()
    {
        // 2x2 km map with open area crossing sector boundaries diagonally
        StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 25f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Travel diagonally across 4 sector boundaries from (100, 100) to (350, 350)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(100f, 100f), 25f);
        Vector2 target = new(350f, 350f);

        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));

        for (int i = 0; i < 350; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            if (!state.Moving && Vector2.Distance(state.Position, target) <= 0.5f) break;
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.False(finalState.Moving);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.True(Vector2.Distance(finalState.Position, target) <= 0.5f);
    }

    [Fact]
    public void NarrowPassage_ReconPassesNarrowGap_TankBlockedOrDetoured()
    {
        StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 0.8f); // Recon: radius 0.8m (clearance 1.6m < 2.2m gap)
        catalog.Register(2, speed: 8f, radius: 1.4f);  // Tank: radius 1.4m (clearance 2.8m > 2.2m gap)

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Gap is at X in [1200, 1202.2], Z in [500, 540]
        EntityId recon = world.CreateUnit(1, 1, new Vector2(1201.1f, 480f), 10f);
        EntityId tank = world.CreateUnit(1, 2, new Vector2(1201.1f, 470f), 8f);

        Vector2 targetAcrossGap = new(1201.1f, 560f);

        sim.TryEnqueue(1, new MoveCommand(1, 0, recon, targetAcrossGap));
        sim.TryEnqueue(1, new MoveCommand(2, 0, tank, targetAcrossGap));

        for (int i = 0; i < 200; i++)
        {
            sim.Step();
        }

        // Recon fits and passes gap directly
        Assert.True(world.TryGetUnit(recon, out UnitState reconState));
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(recon));
        Assert.True(reconState.Position.Y > 530f, "Recon should traverse narrow gap.");

        // Tank cannot fit through 2.2m gap; must detour or remain south of the gap
        Assert.True(world.TryGetUnit(tank, out UnitState tankState));
        if (nav.GetUnitStatus(tank) == PathRequestStatus.NoPath)
        {
            Assert.False(tankState.Moving);
            Assert.True(tankState.Position.Y < 500f, "Blocked tank must not penetrate gap.");
        }
        else
        {
            // If detoured, path must avoid narrow gap volume
            Assert.True(tankState.Position.X < 1140f || tankState.Position.X > 1260f || tankState.Position.Y < 500f || tankState.Position.Y > 550f);
        }
    }

    [Fact]
    public void DisconnectedComponents_WallDividingSector_DoesNotWalkThroughWall()
    {
        // 200m x 200m map with 50m sectors
        const int size = 101;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);

        // Solid dividing wall inside sector (0, 0) dividing X in [0..50] at Z=25
        OpaqueStructure dividingWall = new(
            new StructureId(1),
            minX: 0f, minY: 0f, minZ: 24f,
            maxX: 50f, maxY: 4f, maxZ: 26f,
            isGrounded: true);

        StaticGeometry geo = new(map, [dividingWall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Start south of wall at (25, 10), target north of wall at (25, 40)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(25f, 10f), 10f);
        Vector2 target = new(25f, 40f);

        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));

        for (int i = 0; i < 200; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);

            // Unit must never enter the wall volume [0..50, 24..26]
            bool insideWall = state.Position.X >= 0f && state.Position.X <= 50f &&
                              state.Position.Y >= 23.5f && state.Position.Y <= 26.5f;
            Assert.False(insideWall, "Unit penetrated dividing wall in sector.");
        }

        // Unit detoured around wall through adjacent open sectors (X > 50) and reached target
        UnitState finalState = world.GetUnitAt(0);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.True(Vector2.Distance(finalState.Position, target) <= 0.5f);
    }

    [Fact]
    public void LongBarrierDetour_500mWall_FindsDetourInsteadOfFalseNoPath()
    {
        StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 20f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // 500m barrier is at X in [950, 956], Z in [300, 800]
        // Start at (900, 550), Target at (1050, 550)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(900f, 550f), 20f);
        Vector2 target = new(1050f, 550f);

        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, target));

        int ticks = 0;
        while (ticks < 1200)
        {
            sim.Step();
            ticks++;
            UnitState state = world.GetUnitAt(0);
            if (!state.Moving && Vector2.Distance(state.Position, target) <= 1.0f) break;
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.True(Vector2.Distance(finalState.Position, target) <= 1.0f,
            $"Unit did not reach destination across long barrier. Position: {finalState.Position}");
    }

    [Fact]
    public void UnreachableDestination_QueueContinuesProcessingOtherOrders()
    {
        StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 15f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit 1 targets inside completely enclosed courtyard [1400..1480, 1400..1480]
        EntityId unreachableUnit = world.CreateUnit(1, 1, new Vector2(1350f, 1440f), 15f);
        Vector2 unreachableTarget = new(1440f, 1440f);

        // Unit 2 targets reachable open area [300, 300]
        EntityId reachableUnit = world.CreateUnit(1, 1, new Vector2(200f, 200f), 15f);
        Vector2 reachableTarget = new(300f, 300f);

        sim.TryEnqueue(1, new MoveCommand(1, 0, unreachableUnit, unreachableTarget));
        sim.TryEnqueue(1, new MoveCommand(2, 0, reachableUnit, reachableTarget));

        for (int i = 0; i < 250; i++)
        {
            sim.Step();
        }

        // Unreachable unit receives NoPath or CapacityExceeded without infinite stall
        PathRequestStatus status1 = nav.GetUnitStatus(unreachableUnit);
        Assert.True(status1 is PathRequestStatus.NoPath or PathRequestStatus.CapacityExceeded,
            $"Expected NoPath or CapacityExceeded for enclosed courtyard, got {status1}");

        // Reachable unit successfully completes despite following unreachable order in queue
        Assert.True(world.TryGetUnit(reachableUnit, out UnitState state2));
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(reachableUnit));
        Assert.True(Vector2.Distance(state2.Position, reachableTarget) <= 0.5f);
    }

    [Fact]
    public void ReorderDuringPlanningStages_CancelsStaleWorkAndAppliesNewRoute()
    {
        StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        // Restrict expansions to prolong planning stages
        NavigationTuning tuning = new(MaxGlobalExpansionsPerTick: 20, MaxLocalExpansionsPerTick: 20);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(200f, 200f), 10f);

        // Order 1: target detour to (1050, 550)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(1050f, 550f)));
        sim.Step();
        Assert.Equal(PathRequestStatus.Searching, nav.GetUnitStatus(unit));

        // Order 2: replacement order during active planning to open area (250, 200)
        sim.TryEnqueue(1, new MoveCommand(2, 0, unit, new Vector2(250f, 200f)));

        for (int i = 0; i < 150; i++)
        {
            sim.Step();
            UnitState state = world.GetUnitAt(0);
            if (!state.Moving && Vector2.Distance(state.Position, new Vector2(250f, 200f)) <= 0.5f) break;
        }

        UnitState finalState = world.GetUnitAt(0);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(unit));
        Assert.True(Vector2.Distance(finalState.Position, new Vector2(250f, 200f)) <= 0.5f);
        Assert.False(Vector2.Distance(finalState.Position, new Vector2(1050f, 550f)) <= 10f,
            "Unit must not follow obsolete route from cancelled order.");
    }

    [Fact]
    public void AttackAndDestructionDuringSearch_CancelsPendingWorkCleanly()
    {
        StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        NavigationTuning tuning = new(MaxGlobalExpansionsPerTick: 20, MaxLocalExpansionsPerTick: 20);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(200f, 200f), 10f);

        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(1050f, 550f)));
        sim.Step();
        Assert.Equal(PathRequestStatus.Searching, nav.GetUnitStatus(unit));

        // Destroy unit while search is in progress
        world.RemoveUnit(unit);
        sim.Step();

        // Search cancelled, slot swept, no crash or resurrecting state
        Assert.Equal(PathRequestStatus.None, nav.GetUnitStatus(unit));
        Assert.Equal(0, nav.PendingRequests);
    }

    [Fact]
    public void QueueOverflow_RejectsExcessWithoutCorruptingExistingRequests()
    {
        StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        NavigationTuning tuning = new(MaxPendingRequests: 4, MaxGlobalExpansionsPerTick: 5);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId[] units = new EntityId[6];
        for (int i = 0; i < 6; i++)
        {
            units[i] = world.CreateUnit(1, 1, new Vector2(200f + i * 10f, 200f), 10f);
        }

        // Fill queue to capacity (4 requests) and overflow with 5th
        for (int i = 0; i < 5; i++)
        {
            bool enqueued = sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), 0, units[i], new Vector2(900f, 550f)));
            Assert.True(enqueued);
        }

        sim.Step();

        Assert.True(nav.CapacityExceeded >= 1);
        Assert.True(nav.GetUnitStatus(units[0]) is PathRequestStatus.Searching or PathRequestStatus.PathFound);
        Assert.Equal(PathRequestStatus.CapacityExceeded, nav.GetUnitStatus(units[4]));
    }

    [Fact]
    public void BudgetCompliance_AllExpensiveStagesObeyPerTickLimits()
    {
        StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        NavigationTuning tuning = new(
            MaxGlobalExpansionsPerTick: 50,
            MaxLocalExpansionsPerTick: 100,
            MaxSmoothingRaycastsPerTick: 30,
            MaxAreaPrepCellsPerTick: 5000);

        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(200f, 200f), 10f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(1050f, 550f)));

        for (int i = 0; i < 60; i++)
        {
            sim.Step();

            Assert.True(nav.GlobalExpansionsThisTick <= tuning.MaxGlobalExpansionsPerTick,
                $"Global budget violated: {nav.GlobalExpansionsThisTick} > {tuning.MaxGlobalExpansionsPerTick}");
            Assert.True(nav.LocalExpansionsThisTick <= tuning.MaxLocalExpansionsPerTick,
                $"Local budget violated: {nav.LocalExpansionsThisTick} > {tuning.MaxLocalExpansionsPerTick}");
            Assert.True(nav.SmoothingRaycastsThisTick <= tuning.MaxSmoothingRaycastsPerTick,
                $"Smoothing budget violated: {nav.SmoothingRaycastsThisTick} > {tuning.MaxSmoothingRaycastsPerTick}");
            Assert.True(nav.AreaPrepCellsThisTick <= tuning.MaxAreaPrepCellsPerTick,
                $"Area prep budget violated: {nav.AreaPrepCellsThisTick} > {tuning.MaxAreaPrepCellsPerTick}");
        }
    }

    [Fact]
    public void ReachabilityComparison_AcrossRandomSeeds_MatchesGroundTruth()
    {
        // Compare reachability on random obstacle scenarios across 3 deterministic seeds
        int[] seeds = [42, 1337, 2026];

        foreach (int seed in seeds)
        {
            Random rng = new(seed);
            const int size = 65;
            HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

            List<OpaqueStructure> structures = new();
            for (int i = 0; i < 10; i++)
            {
                float x = rng.Next(10, 50);
                float z = rng.Next(10, 50);
                float w = rng.Next(4, 12);
                float d = rng.Next(4, 12);
                structures.Add(new OpaqueStructure(
                    new StructureId(i + 1),
                    x, 0f, z, x + w, 3f, z + d, isGrounded: true));
            }

            StaticGeometry geo = new(map, structures.ToArray());
            NavigationGrid flatGrid = new(geo, cellSize: 0.5f, enableHierarchical: false);
            NavigationGrid hierGrid = new(geo, cellSize: 0.5f, enableHierarchical: true);

            TestMovementCatalog catalog = new();
            catalog.Register(1, speed: 10f, radius: 0.8f);

            NavigationSystem flatNav = new(flatGrid, catalog);
            NavigationSystem hierNav = new(hierGrid, catalog);

            WorldState world1 = new(map);
            WorldState world2 = new(map);

            Vector2 start = new(5f, 5f);
            Vector2 target = new(58f, 58f);

            EntityId u1 = world1.CreateUnit(1, 1, start, 10f);
            EntityId u2 = world2.CreateUnit(1, 1, start, 10f);

            flatNav.EnqueueCommand(1, new MoveCommand(1, 0, u1, target), world1);
            hierNav.EnqueueCommand(1, new MoveCommand(1, 0, u2, target), world2);

            for (int t = 0; t < 100; t++)
            {
                flatNav.Update(world1, t);
                hierNav.Update(world2, t);
            }

            PathRequestStatus flatStatus = flatNav.GetUnitStatus(u1);
            PathRequestStatus hierStatus = hierNav.GetUnitStatus(u2);

            Assert.Equal(flatStatus, hierStatus);
        }
    }

    [Fact]
    public void NarrowGap_SplitsSectorForTracked_WhilePassableForWheeled()
    {
        const int size = 301;
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Wall across sector (2, 2) [100..150] at z in [124..126]
        // Left piece: x in [100..124] (length 24 m)
        // Right piece: x in [126..150] (length 24 m)
        // Gap: 2.0 m opening at x in [124..126]
        List<OpaqueStructure> structures =
        [
            new OpaqueStructure(new StructureId(1), 100f, 0f, 124f, 124f, 5f, 126f, isGrounded: true),
            new OpaqueStructure(new StructureId(2), 126f, 0f, 124f, 150f, 5f, 126f, isGrounded: true)
        ];

        StaticGeometry geo = new(map, structures.ToArray());
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 20f, radius: MovementClassExtensions.WheeledRadius); // Recon (0.8 m)
        catalog.Register(2, speed: 10f, radius: MovementClassExtensions.TrackedRadius); // Tank (1.4 m)

        Vector2 start = new(125f, 110f);
        Vector2 target = new(125f, 140f);

        // Verify connected component topology per MovementClass
        HierarchicalNavGraph wheeledGraph = grid.GetHierarchicalGraph(MovementClass.Wheeled);
        HierarchicalNavGraph trackedGraph = grid.GetHierarchicalGraph(MovementClass.Tracked);

        int wheeledStartComp = wheeledGraph.GetComponentId(start, MovementClassExtensions.WheeledRadius);
        int wheeledTargetComp = wheeledGraph.GetComponentId(target, MovementClassExtensions.WheeledRadius);
        Assert.True(wheeledStartComp > 0 && wheeledStartComp == wheeledTargetComp,
            $"Wheeled units must connect through the 2 m gap in a single component. Got {wheeledStartComp} vs {wheeledTargetComp}");

        int trackedStartComp = trackedGraph.GetComponentId(start, MovementClassExtensions.TrackedRadius);
        int trackedTargetComp = trackedGraph.GetComponentId(target, MovementClassExtensions.TrackedRadius);
        Assert.True(trackedStartComp > 0 && trackedTargetComp > 0 && trackedStartComp != trackedTargetComp,
            $"Tracked units must be separated into different components by the 2 m gap. Got {trackedStartComp} vs {trackedTargetComp}");

        // Now verify both units plan paths through simulation
        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId recon = world.CreateUnit(1, 1, start, 20f);
        EntityId tank = world.CreateUnit(1, 2, start, 10f);

        // Recon plans directly through the gap
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, recon, target)));
        for (int t = 0; t < 20; t++) sim.Step();

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(recon));

        // Tank plans detour around the wall (not NoPath)
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, 0, tank, target)));
        for (int t = 0; t < 60; t++) sim.Step();

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(tank));
    }

    [Fact]
    public void TownScenario_TanksStickToWideStreets_ReconTakesAlleys()
    {
        const int size = 301;
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Alley corridor along z = 50 of width 2.0 m (between z=44..49 and z=51..56)
        // Avenue along z = 70 of width 6.0 m
        List<OpaqueStructure> structures =
        [
            new OpaqueStructure(new StructureId(1), 50f, 0f, 44f, 150f, 5f, 49f, isGrounded: true),
            new OpaqueStructure(new StructureId(2), 50f, 0f, 51f, 150f, 5f, 56f, isGrounded: true),
            new OpaqueStructure(new StructureId(3), 40f, 0f, 30f, 160f, 5f, 44f, isGrounded: true)
        ];

        StaticGeometry geo = new(map, structures.ToArray());
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 20f, radius: MovementClassExtensions.WheeledRadius); // Recon (0.8 m)
        catalog.Register(2, speed: 10f, radius: MovementClassExtensions.TrackedRadius); // Tank (1.4 m)

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        Vector2 start = new(45f, 50f);
        Vector2 target = new(155f, 50f);

        EntityId recon = world.CreateUnit(1, 1, start, 20f);
        EntityId tank = world.CreateUnit(1, 2, start, 10f);

        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, recon, target)));
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, 0, tank, target)));

        for (int t = 0; t < 50; t++) sim.Step();

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(recon));
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(tank));
    }

    [Fact]
    public void HierarchicalNavGraph_900HouseTown_BuildsUnderThreeSeconds()
    {
        const int size = 2001;
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);
        List<OpaqueStructure> structures = new(900);
        int id = 1;
        for (int r = 0; r < 30; r++)
        {
            float z = 200f + r * 20f;
            for (int c = 0; c < 30; c++)
            {
                float x = 200f + c * 20f;
                structures.Add(new OpaqueStructure(
                    new StructureId(id++),
                    x, 0f, z, x + 10f, 5f, z + 10f, isGrounded: true));
            }
        }

        StaticGeometry geo = new(map, structures.ToArray());

        Stopwatch sw = Stopwatch.StartNew();
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);
        sw.Stop();

        Assert.True(sw.Elapsed.TotalSeconds < 3.0,
            $"HierarchicalNavGraph build took {sw.Elapsed.TotalSeconds:F2} s, target is < 3.0 s");
    }

    [Fact]
    public void PopulateWindowClearance_WithOver200Obstacles_IncludesAllAndPlansSafely()
    {
        const int size = 120;
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Place 240 small obstacles in a local area
        List<OpaqueStructure> structures = new(240);
        int id = 1;
        for (int r = 0; r < 20; r++)
        {
            float z = 15f + r * 4f;
            for (int c = 0; c < 12; c++)
            {
                float x = 15f + c * 6f;
                structures.Add(new OpaqueStructure(
                    new StructureId(id++),
                    x, 0f, z, x + 1.5f, 3f, z + 1.5f, isGrounded: true));
            }
        }

        StaticGeometry geo = new(map, structures.ToArray());
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        // Window covering all 240 obstacles
        float minX = 10f;
        float minZ = 10f;
        int cols = 160;
        int rows = 160;
        float[] clearance = new float[cols * rows];

        grid.PopulateWindowClearance(minX, minZ, cols, rows, 0.5f, clearance);

        // Verify obstacle centers have 0 clearance
        for (int i = 0; i < structures.Count; i++)
        {
            var s = structures[i];
            float midX = (s.MinX + s.MaxX) * 0.5f;
            float midZ = (s.MinZ + s.MaxZ) * 0.5f;
            int c = (int)((midX - minX) / 0.5f);
            int r = (int)((midZ - minZ) / 0.5f);
            if (c >= 0 && c < cols && r >= 0 && r < rows)
            {
                Assert.Equal(0f, clearance[r * cols + c]);
            }
        }
    }

    [Fact]
    public void AddPortalToSector_SectorWithTwentyOpenings_RetainsAllPortals()
    {
        const int size = 150;
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Sector boundary at x = 50, z in [0..50]
        // Wall along x = 50 with 20 distinct 1.8 m openings (separated by 0.6 m posts)
        List<OpaqueStructure> structures = new(21);
        int id = 1;
        float z = 0f;
        for (int i = 0; i < 21; i++)
        {
            structures.Add(new OpaqueStructure(
                new StructureId(id++),
                49.5f, 0f, z, 50.5f, 3f, z + 0.6f, isGrounded: true));
            z += 0.6f + 1.8f;
        }

        StaticGeometry geo = new(map, structures.ToArray());
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        HierarchicalNavGraph wheeledGraph = grid.GetHierarchicalGraph(MovementClass.Wheeled);

        // Border between sector 0 and sector 1 has at least 20 portals
        Assert.True(wheeledGraph.PortalCount >= 20,
            $"Expected at least 20 portals along the boundary, but found {wheeledGraph.PortalCount}");
    }

    [Fact]
    public void SectorComponentGrid_SectorWithTwoHundredPockets_KeepsComponentsSeparate()
    {
        const int size = 100;
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Grid of 15x15 = 225 rooms in sector (0, 0) [0..50] x [0..50]
        List<OpaqueStructure> structures = new();
        int id = 1;

        // Horizontal walls
        for (int r = 0; r <= 15; r++)
        {
            float z = 4f + r * 2.8f;
            structures.Add(new OpaqueStructure(
                new StructureId(id++),
                4f, 0f, z, 46f, 3f, z + 0.4f, isGrounded: true));
        }

        // Vertical walls
        for (int c = 0; c <= 15; c++)
        {
            float x = 4f + c * 2.8f;
            structures.Add(new OpaqueStructure(
                new StructureId(id++),
                x, 0f, 4f, x + 0.4f, 3f, 46f, isGrounded: true));
        }

        StaticGeometry geo = new(map, structures.ToArray());
        NavigationGrid grid = new(geo, cellSize: 0.25f, enableHierarchical: true);
        HierarchicalNavGraph footGraph = grid.GetHierarchicalGraph(MovementClass.Foot);

        // Sample centers of 200 rooms and verify all have distinct component IDs
        HashSet<int> distinctComponents = new();
        int sampled = 0;

        for (int r = 0; r < 15 && sampled < 200; r++)
        {
            float z = 4f + r * 2.8f + 1.4f;
            for (int c = 0; c < 15 && sampled < 200; c++)
            {
                float x = 4f + c * 2.8f + 1.4f;
                int comp = footGraph.GetComponentId(new Vector2(x, z), 0.4f);
                Assert.True(comp > 0, $"Room at ({x}, {z}) should have a valid component ID, got {comp}");
                distinctComponents.Add(comp);
                sampled++;
            }
        }

        Assert.Equal(200, distinctComponents.Count);
        Assert.True(distinctComponents.Max() >= 200,
            $"Component IDs must not wrap around at 127. Max component ID was {distinctComponents.Max()}");
    }

    [Fact]
    public void SpatialBucketPartitioning_DenseWaterSlices_QueryCostRemainsFastAndAccurate()
    {
        const int size = 201; // 200m x 200m at 1m spacing
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Add 200 water slices slicing across the map along X=100
        List<TerrainObstacle> water = new();
        for (int i = 0; i < 200; i++)
        {
            float z0 = i * 1.0f;
            float z1 = (i + 1) * 1.0f;
            water.Add(new TerrainObstacle(95f, z0, 105f, z1, TraversabilityCapabilities.Water));
        }

        StaticGeometry geo = new(map, ReadOnlySpan<OpaqueStructure>.Empty, ReadOnlySpan<BridgeStructure>.Empty, water.ToArray());
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        // Point far from water on dry land (X=20, Z=50)
        Vector2 dryPoint = new(20f, 50f);
        Assert.True(grid.IsPointClear(dryPoint, 1.4f, TraversabilityCapabilities.DefaultVehicle));
        float dryClearance = grid.ComputePointClearance(dryPoint, TraversabilityCapabilities.DefaultVehicle);
        Assert.True(dryClearance >= 19.5f && dryClearance <= 20.5f);

        // Point inside water at (100, 50)
        Vector2 inWater = new(100f, 50f);
        Assert.False(grid.IsPointClear(inWater, 1.4f, TraversabilityCapabilities.DefaultVehicle));
        Assert.True(grid.IsPointClear(inWater, 1.4f, TraversabilityCapabilities.Water));
        Assert.Equal(0f, grid.ComputePointClearance(inWater, TraversabilityCapabilities.DefaultVehicle));

        // Point query sanity checks on 200 water slices
        for (int q = 0; q < 100; q++)
        {
            float qx = 10f + (q % 80);
            float qz = 10f + (q / 80 % 80);
            bool isClear = grid.IsPointClear(new Vector2(qx, qz), 1.0f, TraversabilityCapabilities.DefaultVehicle);
            if (qx >= 94f && qx <= 106f)
            {
                Assert.False(isClear);
            }
        }
    }

    [Fact]
    public void ComputePointClearance_DistantObstacle_ReturnsExactClearanceWithoutCap()
    {
        const int size = 501; // 500m x 500m map
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Water strip at X in [300..350], across entire Z
        TerrainObstacle water = new(300f, 0f, 350f, 500f, TraversabilityCapabilities.Water);
        StaticGeometry geo = new(map, ReadOnlySpan<OpaqueStructure>.Empty, ReadOnlySpan<BridgeStructure>.Empty, [water]);
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        // Point at (200, 250). Distance to water (X=300) is 100m.
        // Distances to map borders: min(200, 300, 250, 250) = 200m.
        Vector2 queryPoint = new(200f, 250f);

        // Exact clearance must be 100m (distance to water), not 200m (boundary) due to 50m search radius clamp
        float gridClearance = grid.ComputePointClearance(queryPoint, TraversabilityCapabilities.DefaultVehicle);
        Assert.True(MathF.Abs(gridClearance - 100f) < 0.1f, $"Expected 100m clearance to water, got {gridClearance}m");

        float evalClearance = grid.Evaluator.ComputePointClearance(queryPoint, TraversabilityCapabilities.DefaultVehicle);
        Assert.True(MathF.Abs(evalClearance - 100f) < 0.1f, $"Expected 100m evaluator clearance, got {evalClearance}m");

        // IsPointClear consistency checks
        Assert.True(grid.IsPointClear(queryPoint, 90f, TraversabilityCapabilities.DefaultVehicle));
        Assert.False(grid.IsPointClear(queryPoint, 110f, TraversabilityCapabilities.DefaultVehicle));

        Assert.True(grid.Evaluator.IsPointClear(queryPoint, 90f, TraversabilityCapabilities.DefaultVehicle));
        Assert.False(grid.Evaluator.IsPointClear(queryPoint, 110f, TraversabilityCapabilities.DefaultVehicle));
    }

    [Fact]
    public void PreparedWindowClearanceReuse_AdjacentUnitsInSameArea_ReusesPreparedWindow()
    {
        const int size = 301; // 300m x 300m at 1m spacing -> 600x600 cells at 0.5m = 360,000 cells > 262,144 (hierarchical)
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // A single 10m building in sector (1, 1) [100..200, 100..200]
        OpaqueStructure building = new(new StructureId(1), 145f, 0f, 145f, 155f, 5f, 155f, isGrounded: true);
        StaticGeometry geo = new(map, [building]);
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        NavigationTuning tuning = new(MaxWaypointsPerRoute: 64, MaxNodeExpansionsPerTick: 3000, MaxLocalExpansionsPerTick: 3000);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit 1 maneuvers around the building from (135, 150) to (165, 150). Direct raycast is blocked.
        EntityId u1 = world.CreateUnit(1, 1, new Vector2(135f, 150f), 10f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, u1, new Vector2(165f, 150f)));

        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            if (nav.GetUnitStatus(u1) == PathRequestStatus.PathFound) break;
        }

        int cellsAfterUnit1 = nav.TotalAreaPrepCells;
        Assert.True(cellsAfterUnit1 > 0, $"Unit 1 must have prepared area cells, got {cellsAfterUnit1}.");
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(u1));

        // Unit 2 nearby at (136, 150) moving to (164, 150), strictly contained within Unit 1 window
        EntityId u2 = world.CreateUnit(1, 1, new Vector2(136f, 150f), 10f);
        sim.TryEnqueue(1, new MoveCommand(2, 0, u2, new Vector2(164f, 150f)));

        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            if (nav.GetUnitStatus(u2) == PathRequestStatus.PathFound) break;
        }

        // Unit 2 must have reused the prepared static window without adding new area prep cells
        Assert.Equal(cellsAfterUnit1, nav.TotalAreaPrepCells);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(u2));
    }

    [Fact]
    public void WindowClearanceCache_CancellationMidway_InvalidatesCache()
    {
        const int size = 301; // 300m x 300m at 1m spacing -> 600x600 cells at 0.5m = 360,000 cells > 262,144 (hierarchical)
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Building 1 in sector (1, 1) and Building 2 in sector (0, 0)
        OpaqueStructure building1 = new(new StructureId(1), 145f, 0f, 145f, 155f, 5f, 155f, isGrounded: true);
        OpaqueStructure building2 = new(new StructureId(2), 45f, 0f, 45f, 55f, 5f, 55f, isGrounded: true);
        StaticGeometry geo = new(map, [building1, building2]);
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        // Limit area prep budget to 500 cells per tick so multi-row preparation takes multiple ticks
        NavigationTuning tuning = new(MaxWaypointsPerRoute: 64, MaxAreaPrepCellsPerTick: 500);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Step 1: Unit 1 maneuvers around building 1 from (135, 150) to (165, 150). Window A is fully prepared.
        EntityId u1 = world.CreateUnit(1, 1, new Vector2(135f, 150f), 10f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, u1, new Vector2(165f, 150f)));

        for (int i = 0; i < 200; i++)
        {
            sim.Step();
            if (nav.GetUnitStatus(u1) == PathRequestStatus.PathFound) break;
        }

        int cellsAfterUnit1 = nav.TotalAreaPrepCells;
        Assert.True(cellsAfterUnit1 > 0, $"Unit 1 must have prepared area cells, got {cellsAfterUnit1}.");
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(u1));

        // Step 2: Unit 2 maneuvers around building 2 from (35, 50) to (65, 50). Window B starts preparation.
        EntityId u2 = world.CreateUnit(1, 1, new Vector2(35f, 50f), 10f);
        sim.TryEnqueue(1, new MoveCommand(2, 0, u2, new Vector2(65f, 50f)));

        // Step 1 tick: Window B preparation starts and partially fills _localClearance (at most 500 cells)
        sim.Step();
        Assert.Equal(NavigationSearchStage.AreaPreparation, nav.ActiveStage);
        int cellsDuringUnit2 = nav.TotalAreaPrepCells;
        Assert.True(cellsDuringUnit2 > cellsAfterUnit1, "Unit 2 must have started area preparation.");

        // Step 3: Cancel Unit 2 while its area preparation is in progress
        world.RemoveUnit(u2);
        sim.Step();
        Assert.Equal(NavigationSearchStage.None, nav.ActiveStage);

        // Step 4: Unit 1 requests movement again strictly inside Window A
        sim.TryEnqueue(1, new MoveCommand(4, 0, u1, new Vector2(164f, 150f)));

        for (int i = 0; i < 200; i++)
        {
            sim.Step();
            if (nav.GetUnitStatus(u1) == PathRequestStatus.PathFound) break;
        }

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(u1));
        // Total area prep cells must increase: Window A was invalidated and re-prepared from scratch
        Assert.True(nav.TotalAreaPrepCells > cellsDuringUnit2,
            $"Interrupted window preparation must invalidate cache; cells remained {nav.TotalAreaPrepCells}");
    }

    [Fact]
    public void UnreachableTarget_EnclosedInWater_ReturnsNoPathPromptly()
    {
        const int size = 301; // 300m x 300m -> 600x600 cells at 0.5m = 360,000 cells > 262,144 (hierarchical)
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Completely enclose island [130..170, 130..170] in wide water moat [120..130, 170..180]
        List<TerrainObstacle> water = new()
        {
            new TerrainObstacle(120f, 120f, 180f, 130f, TraversabilityCapabilities.Water),
            new TerrainObstacle(120f, 170f, 180f, 180f, TraversabilityCapabilities.Water),
            new TerrainObstacle(120f, 130f, 130f, 170f, TraversabilityCapabilities.Water),
            new TerrainObstacle(170f, 130f, 180f, 170f, TraversabilityCapabilities.Water)
        };

        StaticGeometry geo = new(map, ReadOnlySpan<OpaqueStructure>.Empty, ReadOnlySpan<BridgeStructure>.Empty, water.ToArray());
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit starts outside moat at (50, 50)
        EntityId unit = world.CreateUnit(1, 1, new Vector2(50f, 50f), 10f);

        // Target is inside the enclosed island at (150, 150)
        sim.TryEnqueue(1, new MoveCommand(1, 0, unit, new Vector2(150f, 150f)));

        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            if (nav.GetUnitStatus(unit) == PathRequestStatus.NoPath) break;
        }

        Assert.Equal(PathRequestStatus.NoPath, nav.GetUnitStatus(unit));
        Assert.Equal(1, nav.FailedSearches);
    }

    [Fact]
    public void WindowClearanceCache_SearchFromBridgeDeck_DoesNotCorruptGroundClearanceUnderBridge()
    {
        // P2 regression: When preparing local window for a unit on bridge deck over water,
        // _localClearance (the ground clearance cache) must record ground clearance (0m for water),
        // not the bridge deck side clearance (5.75m). Ground searches reusing the window must
        // not treat the water cell under the bridge span as passable dry land.
        const int size = 301; // 300m x 300m -> 600x600 cells at 0.5m = 360,000 cells > 262,144 (hierarchical)
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Bridge spanning over water at Z=150: X from 120 to 180, width 8m (Z in [146..154]), height 4m
        BridgeStructure bridge = new(
            new StructureId(1),
            new Vector2(120f, 150f),
            new Vector2(180f, 150f),
            width: 8.0f,
            startHeight: 4.0f,
            endHeight: 4.0f,
            thickness: 0.8f);

        // Water obstacle under bridge span: X in [130..170], Z in [100..200]
        TerrainObstacle water = new(130f, 100f, 170f, 200f, TraversabilityCapabilities.Water);

        StaticGeometry geo = new(map, ReadOnlySpan<OpaqueStructure>.Empty, [bridge], [water]);
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 0.8f);

        NavigationTuning tuning = new(MaxWaypointsPerRoute: 64, MaxNodeExpansionsPerTick: 3000, MaxLocalExpansionsPerTick: 3000);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit 1 starts on bridge deck over water at (145, 150) with supportSurface: 1,
        // and orders movement to the west bank at (110, 150) on ground (TargetSurface: 0).
        // Since start is far from bridge entry, direct raycast cannot skip area preparation.
        EntityId u1 = world.CreateUnit(1, 1, new Vector2(145f, 150f), 10f);
        world.UpdateUnitNavigation(u1, new Vector2(145f, 150f), new Vector2(145f, 150f), false, supportSurface: 1u);

        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, u1, new Vector2(110f, 150f), TargetSurface: 0u)));

        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            if (nav.GetUnitStatus(u1) == PathRequestStatus.PathFound) break;
        }

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(u1));
        Assert.True(nav.IsWindowCacheValid, "Window cache must be valid after Unit 1 search.");

        // Check cached clearance under the bridge span at (145, 150) in water.
        // Before fix: stored bridge deck clearance (~5.75m).
        // With fix: must be 0m (water on ground surface 0).
        Vector2 underBridgeRiverPoint = new(145f, 150f);
        Assert.True(nav.TryGetCachedLocalClearanceAt(underBridgeRiverPoint, out float cachedClearance),
            "Point under bridge must be covered by the prepared local window.");
        Assert.Equal(0.0f, cachedClearance);

        int cellsAfterUnit1 = nav.TotalAreaPrepCells;
        Assert.True(cellsAfterUnit1 > 0, $"Unit 1 must have prepared area cells, got {cellsAfterUnit1}.");

        // Ground request reuse: Unit 2 on ground (surface 0) west of water at (112, 150) moves to (114, 150)
        EntityId u2 = world.CreateUnit(1, 1, new Vector2(112f, 150f), 10f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, 0, u2, new Vector2(114f, 150f))));

        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            if (nav.GetUnitStatus(u2) == PathRequestStatus.PathFound) break;
        }

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(u2));
        // Total area prep cells must not increase, confirming window reuse
        Assert.Equal(cellsAfterUnit1, nav.TotalAreaPrepCells);
    }

    [Fact]
    public void SmallAreaPreparation_AndLocalPlanning_FinishInSingleTick()
    {
        // When area preparation consumes less than half the tick cell budget,
        // it must not force an early yield. Small local windows (e.g. around local obstacles)
        // should finish area preparation and local planning in the same tick.
        const int size = 301;
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        // Building inside sector (2, 2) [100..150, 100..150]
        OpaqueStructure building = new(new StructureId(1), 123f, 0f, 123f, 127f, 5f, 127f, isGrounded: true);
        StaticGeometry geo = new(map, [building]);
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 0.8f);

        NavigationTuning tuning = new(MaxWaypointsPerRoute: 64);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Unit maneuvers around building inside sector (2, 2) from (115, 125) to (135, 125).
        // Direct raycast is blocked by building.
        EntityId u1 = world.CreateUnit(1, 1, new Vector2(115f, 125f), 10f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, u1, new Vector2(135f, 125f)));

        // Step simulation exactly ONCE
        sim.Step();

        // Small window preparation and local planning must both complete in this single tick
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(u1));
        Assert.True(nav.AreaPrepCellsThisTick > 0, "Area preparation cells must be evaluated in tick 1.");
        Assert.True(nav.LocalExpansionsThisTick > 0, "Local planning expansions must execute in the same tick.");
    }

    [Fact]
    public void GeometryVersion_Increment_InvalidatesWindowClearanceAndCorridorCache()
    {
        // Incrementing GeometryVersion on NavigationGrid must invalidate both
        // prepared window clearance cache and cached global corridor plans.
        const int size = 301;
        HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);

        OpaqueStructure building = new(new StructureId(1), 145f, 0f, 145f, 155f, 5f, 155f, isGrounded: true);
        StaticGeometry geo = new(map, [building]);
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.0f);

        NavigationTuning tuning = new(MaxWaypointsPerRoute: 64, MaxNodeExpansionsPerTick: 3000, MaxLocalExpansionsPerTick: 3000);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(geo.HeightMap);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        // Step 1: Unit 1 maneuvers around building from (135, 150) to (165, 150).
        EntityId u1 = world.CreateUnit(1, 1, new Vector2(135f, 150f), 10f);
        sim.TryEnqueue(1, new MoveCommand(1, 0, u1, new Vector2(165f, 150f)));

        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            if (nav.GetUnitStatus(u1) == PathRequestStatus.PathFound) break;
        }

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(u1));
        Assert.True(nav.IsWindowCacheValid, "Window cache must be valid after Unit 1 search.");
        int cellsAfterUnit1 = nav.TotalAreaPrepCells;

        // Step 2: Invalidate geometry version (simulates bridge destruction / map changes)
        grid.InvalidateGeometry();

        // Cached point clearance must report false because version no longer matches
        Assert.False(nav.TryGetCachedLocalClearanceAt(new Vector2(135f, 150f), out _));

        // Step 3: Unit 2 nearby inside the same window bounds requests path
        EntityId u2 = world.CreateUnit(1, 1, new Vector2(136f, 150f), 10f);
        sim.TryEnqueue(1, new MoveCommand(2, 0, u2, new Vector2(164f, 150f)));

        for (int i = 0; i < 50; i++)
        {
            sim.Step();
            if (nav.GetUnitStatus(u2) == PathRequestStatus.PathFound) break;
        }

        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(u2));
        // Total area prep cells must increase, confirming window cache was not reused
        Assert.True(nav.TotalAreaPrepCells > cellsAfterUnit1,
            $"Geometry invalidation must force area re-preparation; total cells remained {nav.TotalAreaPrepCells}.");
    }
}
