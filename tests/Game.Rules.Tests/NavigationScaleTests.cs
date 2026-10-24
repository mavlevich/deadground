using System.Numerics;
using DeadGround.Engine;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class NavigationScaleTests
{
    private sealed class MovementCatalog : IUnitMovementCatalog
    {
        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
        {
            profile = new UnitMovementProfile(100f, definition == 2 ? .8f : 1.4f);
            return true;
        }
    }

    private static HeightMap Map(float size = 5000f) => new(2, 2, size, 0.01f, new ushort[4]);

    [Fact]
    public void FiveKmOpenMap_AllClassesExceedOldPortalCapWithoutFullMapDetail()
    {
        NavigationGrid grid = new(Map(), [], enableHierarchical: true);
        for (int c = 0; c < 4; c++)
        {
            HierarchicalNavGraph graph = grid.GetHierarchicalGraph((MovementClass)c);
            Assert.Equal(10_000, graph.TotalSectors);
            Assert.Equal(19_800, graph.PortalCount);
            Assert.Equal(0, graph.DetailedCellCount);
            Assert.Equal(0, graph.DetailedSectorCount);
            Assert.True(graph.GetComponentId(new Vector2(25, 25), graph.ClassRadius) > 0);
            Assert.False(grid.IsPointClear(Vector2.Zero, graph.ClassRadius));
        }
        NavigationSystem nav = new(grid, new MovementCatalog(), maxUnits: 1000);
        Assert.InRange(nav.EstimatedMemoryBytes, grid.EstimatedMemoryBytes, 64L * 1024 * 1024);
    }

    [Fact]
    public void SparseGeometry_DetailedStorageDependsOnObstacleSectorsNotMapArea()
    {
        OpaqueStructure[] obstacles = [new(new StructureId(1), 2455, 0, 2455, 2470, 10, 2470, true)];
        NavigationGrid grid = new(Map(), obstacles, enableHierarchical: true);
        for (int c = 0; c < 4; c++)
        {
            HierarchicalNavGraph graph = grid.GetHierarchicalGraph((MovementClass)c);
            Assert.Equal(1, graph.DetailedSectorCount);
            Assert.Equal(10_000, graph.DetailedCellCount);
            Assert.Equal(-1, graph.GetComponentId(new Vector2(2460, 2460), graph.ClassRadius));
        }
    }

    [Theory]
    [InlineData("sectors")]
    [InlineData("detailed-cell")]
    [InlineData("portal")]
    [InlineData("edge")]
    public void LoadBudgets_RejectComplexityExplicitly(string budget)
    {
        NavigationBuildLimits limits = budget switch
        {
            "sectors" => new(MaxSectors: 10),
            "detailed-cell" => new(MaxDetailedCells: 10),
            "portal" => new(MaxPortals: 10),
            _ => new(MaxIntraEdges: 10)
        };
        OpaqueStructure[] obstacles = budget == "detailed-cell"
            ? [new(new StructureId(1), 55, 0, 55, 65, 10, 65, true)] : [];
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new NavigationGrid(Map(200), obstacles, enableHierarchical: true, buildLimits: limits));
        Assert.Contains(budget, error.Message);
        Assert.Contains("limit", error.Message);
    }

    [Fact]
    public void LongGlobalSearch_IsDeterministicBudgetedAndDoesNotTruncateSmallOutput()
    {
        NavigationGrid grid = new(Map(), [], enableHierarchical: true);
        HierarchicalNavGraph graph = grid.HierarchicalGraph;
        Vector2 start = new(25, 25), goal = new(4975, 4975);
        Vector2[] first = new Vector2[1024], second = new Vector2[1024];
        int firstCount = Search(graph, start, goal, first, PathRequestStatus.PathFound);
        int secondCount = Search(graph, start, goal, second, PathRequestStatus.PathFound);
        Assert.True(firstCount > 100);
        Assert.Equal(first.AsSpan(0, firstCount).ToArray(), second.AsSpan(0, secondCount).ToArray());
        Assert.Equal(0, Search(graph, start, goal, new Vector2[4], PathRequestStatus.CapacityExceeded));
    }

    private static int Search(HierarchicalNavGraph graph, Vector2 start, Vector2 goal,
        Vector2[] output, PathRequestStatus expected)
    {
        graph.StartGlobalSearch(start, goal, graph.ClassRadius);
        for (int tick = 0; tick < 1000; tick++)
        {
            PathRequestStatus status = graph.ResumeGlobalSearch(50, 20_000, out int used, output, out int count);
            Assert.InRange(used, 0, 50);
            if (status == PathRequestStatus.Searching) continue;
            Assert.Equal(expected, status);
            return count;
        }
        Assert.Fail("Global search did not finish within its work budget.");
        return 0;
    }

    [Fact]
    public void FiveKmDetour_FollowsChunksToExactGoalWithSafeMovementAndBoundedWork()
    {
        HeightMap map = Map();
        OpaqueStructure[] obstacles = [new(new StructureId(1), 2490, 0, 0, 2510, 10, 2600)];
        NavigationGrid grid = new(map, obstacles, enableHierarchical: true);
        NavigationSystem nav = new(grid, new MovementCatalog(), maxUnits: 1000);
        WorldState world = new(map);
        using Simulation sim = new(world, new RuleSet(nav));
        Vector2 start = new(500, 1000), target = new(4500, 1000);
        EntityId id = world.CreateUnit(1, 1, start, 100f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, id, target)));
        Vector2 previous = start;
        for (int tick = 0; tick < 6000; tick++)
        {
            sim.Step();
            Vector2 current = world.GetUnitAt(0).Position;
            Assert.True(grid.IsSegmentClear(previous, current, 1.4f));
            Assert.InRange(nav.GlobalExpansionsThisTick, 0, nav.Tuning.MaxGlobalExpansionsPerTick);
            Assert.InRange(nav.LocalExpansionsThisTick, 0, nav.Tuning.MaxLocalExpansionsPerTick);
            Assert.InRange(nav.AreaPrepCellsThisTick, 0, nav.Tuning.MaxAreaPrepCellsPerTick);
            Assert.InRange(nav.SmoothingRaycastsThisTick, 0, nav.Tuning.MaxSmoothingRaycastsPerTick);
            previous = current;
            if (Vector2.Distance(current, target) < .1f) break;
        }
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(id));
        Assert.InRange(Vector2.Distance(previous, target), 0, .1f);
        Assert.Equal(0, nav.FailedSearches);
        Assert.True(nav.ChunkRequestsCompleted > 10);
    }

    [Fact]
    public void FiveKmDisconnectedMap_GlobalSearchReportsNoPathAndCanRestart()
    {
        OpaqueStructure[] obstacles = [new(new StructureId(1), 2490, 0, 0, 2510, 10, 5000)];
        NavigationGrid grid = new(Map(), obstacles, enableHierarchical: true);
        HierarchicalNavGraph graph = grid.HierarchicalGraph;
        Assert.Equal(0, Search(graph, new(500, 1000), new(4500, 1000), new Vector2[1024], PathRequestStatus.NoPath));
        graph.StartGlobalSearch(new(500, 1000), new(4500, 1000), 1.4f);
        graph.CancelGlobalSearch();
        Assert.False(graph.HasActiveGlobalSearch);
        Assert.True(Search(graph, new(500, 1000), new(500, 4500), new Vector2[1024], PathRequestStatus.PathFound) > 0);
    }

    [Fact]
    public void DetailedSectorQuery_IncludesObstaclesAfterTheOld1024EntryBuffer()
    {
        OpaqueStructure[] obstacles = new OpaqueStructure[1025];
        for (int i = 0; i < 1024; i++)
            obstacles[i] = new(new StructureId(i + 1), 5, 0, 5, 6, 5, 6, true);
        obstacles[1024] = new(new StructureId(1025), 30, 0, 30, 35, 5, 35, true);
        NavigationGrid grid = new(Map(200), obstacles, enableHierarchical: true);
        Assert.Equal(-1, grid.HierarchicalGraph.GetComponentId(new Vector2(32, 32), 1.4f));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CorridorReuse_PreservesPerUnitExactDestinationsAndSafeMovement(int cacheEntries)
    {
        HeightMap map = Map(400);
        OpaqueStructure[] obstacles = [new(new StructureId(1), 190, 0, 0, 210, 10, 300)];
        NavigationGrid grid = new(map, obstacles, enableHierarchical: true);
        NavigationSystem nav = new(grid, new MovementCatalog(), new NavigationTuning(GlobalPlanCacheEntries: cacheEntries), 1000);
        WorldState world = new(map);
        using Simulation sim = new(world, new RuleSet(nav));
        Vector2[] targets = [new(355, 55), new(360, 60)];
        EntityId[] ids = [world.CreateUnit(1, 1, new(55, 55), 100), world.CreateUnit(1, 1, new(60, 60), 100)];
        long secondUnitAllocated = 0;
        for (int unit = 0; unit < ids.Length; unit++)
        {
            Assert.True(sim.TryEnqueue(1, new MoveCommand((uint)(unit + 1), sim.Tick, ids[unit], targets[unit])));
            for (int tick = 0; tick < 2000; tick++)
            {
                Vector2 previous = world.GetUnitAt(unit).Position;
                long before = GC.GetAllocatedBytesForCurrentThread();
                sim.Step();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                if (unit == 1) secondUnitAllocated += allocated;
                Vector2 current = world.GetUnitAt(unit).Position;
                Assert.True(grid.IsSegmentClear(previous, current, 1.4f));
                if (Vector2.Distance(current, targets[unit]) < .1f) break;
            }
            Assert.InRange(Vector2.Distance(world.GetUnitAt(unit).Position, targets[unit]), 0, .1f);
        }
        Assert.Equal(cacheEntries == 0 ? 0 : 1, nav.GlobalPlanCacheHits);
        Assert.Equal(cacheEntries == 0 ? 2 : 1, nav.GlobalPlanCacheMisses);
        Assert.Equal(0, secondUnitAllocated);
    }

    [Fact]
    public void CorridorReuse_DoesNotReuseLightVehiclePassageForHeavyVehicle()
    {
        HeightMap map = Map(400);
        OpaqueStructure[] obstacles = [
            new(new StructureId(1), 190, 0, 0, 210, 10, 200),
            new(new StructureId(2), 190, 0, 202, 210, 10, 400)];
        NavigationSystem nav = new(new NavigationGrid(map, obstacles, enableHierarchical: true), new MovementCatalog(), maxUnits: 1000);
        WorldState world = new(map);
        using Simulation sim = new(world, new RuleSet(nav));
        EntityId light = world.CreateUnit(1, 2, new(55, 155), 100);
        EntityId heavy = world.CreateUnit(1, 1, new(60, 160), 100);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, light, new(355, 155))));
        for (int tick = 0; tick < 2000 && nav.GetUnitStatus(light) != PathRequestStatus.PathFound; tick++) sim.Step();
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(light));
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, heavy, new(360, 160))));
        for (int tick = 0; tick < 2000 && nav.GetUnitStatus(heavy) != PathRequestStatus.NoPath; tick++) sim.Step();
        Assert.Equal(PathRequestStatus.NoPath, nav.GetUnitStatus(heavy));
        Assert.Equal(0, nav.GlobalPlanCacheHits);
        Assert.Equal(1, nav.GlobalPlanCacheMisses);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void DisconnectedBurst_TwoHundredOrdersResolveWithoutSearchAndReachableOrderStillPlans(int cacheEntries)
    {
        HeightMap map = Map();
        OpaqueStructure[] obstacles = [
            new(new StructureId(1), 2490, 0, 0, 2510, 10, 5000),
            new(new StructureId(2), 590, 0, 2800, 610, 10, 3200)];
        NavigationGrid grid = new(map, obstacles, enableHierarchical: true);
        NavigationSystem nav = new(grid, new MovementCatalog(), new NavigationTuning(GlobalPlanCacheEntries: cacheEntries), 1000);
        WorldState world = new(map);
        using Simulation sim = new(world, new RuleSet(nav));
        EntityId[] ids = new EntityId[200];
        for (int i = 0; i < ids.Length; i++) ids[i] = world.CreateUnit(1, 1, new(500 + i % 10 * 5, 1000 + i / 10 * 5), 100);
        sim.Step();
        for (int i = 0; i < ids.Length; i++)
            Assert.True(sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), sim.Tick, ids[i], new(4500, 1000 + i / 10 * 5))));
        long before = GC.GetAllocatedBytesForCurrentThread();
        sim.Step();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        foreach (EntityId id in ids) Assert.Equal(PathRequestStatus.NoPath, nav.GetUnitStatus(id));
        Assert.Equal(0, allocated);
        Assert.Equal(0, nav.PendingRequests);
        Assert.Equal(0, nav.TotalGlobalExpansions);
        Assert.Equal(0, nav.TotalLocalExpansions);
        for (int c = 0; c < 4; c++) Assert.Equal(0, grid.GetHierarchicalGraph((MovementClass)c).GlobalSearchesStarted);

        EntityId reachable = world.CreateUnit(1, 1, new(500, 3000), 100);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(201, sim.Tick, reachable, new(700, 3000))));
        for (int tick = 0; tick < 2000; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(reachable, out UnitState unit) && Vector2.Distance(unit.Position, new(700, 3000)) < .1f) break;
        }
        Assert.True(world.TryGetUnit(reachable, out UnitState final));
        Assert.InRange(Vector2.Distance(final.Position, new(700, 3000)), 0, .1f);
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(reachable));
        Assert.True(grid.HierarchicalGraph.GlobalSearchesStarted > 0);
    }

    [Fact]
    public void GlobalConnectivity_DistinguishesComponentsInsideOneSectorForEveryClass()
    {
        OpaqueStructure[] obstacles = [new(new StructureId(1), 2470, 0, 0, 2480, 10, 5000)];
        NavigationGrid grid = new(Map(), obstacles, enableHierarchical: true);
        Vector2 start = new(2455, 1025), target = new(2490, 1025);
        for (int c = 0; c < 4; c++)
        {
            HierarchicalNavGraph graph = grid.GetHierarchicalGraph((MovementClass)c);
            int sector = graph.GetSectorIndex(start);
            Assert.Equal(sector, graph.GetSectorIndex(target));
            int startComponent = graph.GetComponentId(start, graph.ClassRadius);
            int targetComponent = graph.GetComponentId(target, graph.ClassRadius);
            Assert.NotEqual(startComponent, targetComponent);
            Assert.False(graph.AreComponentsConnected(sector, startComponent, sector, targetComponent));
            graph.StartGlobalSearch(start, target, graph.ClassRadius);
            Assert.False(graph.HasActiveGlobalSearch);
            Assert.Equal(0, graph.GlobalSearchesStarted);
        }
    }

    [Fact]
    public void LoadSectorBudget_UsesRequestedSectorSizeInGridAndGraphs()
    {
        NavigationBuildLimits limits = new(MaxSectors: 3000);
        NavigationGrid grid = new(Map(), [], enableHierarchical: true, buildLimits: limits, sectorSize: 100);
        for (int c = 0; c < 4; c++)
        {
            HierarchicalNavGraph graph = grid.GetHierarchicalGraph((MovementClass)c);
            Assert.Equal(100, graph.SectorSize);
            Assert.Equal(2500, graph.TotalSectors);
        }
        Assert.Throws<InvalidOperationException>(() =>
            new NavigationGrid(Map(), [], enableHierarchical: true, buildLimits: limits, sectorSize: 50));
    }

    [Fact]
    public void SmoothingBudget_ResumesInitialSpliceAndInnerScanWithoutOvershoot()
    {
        HeightMap map = Map(400);
        OpaqueStructure[] obstacles = [new(new StructureId(1), 190, 0, 0, 210, 10, 300)];
        NavigationGrid grid = new(map, obstacles, enableHierarchical: true);
        NavigationSystem nav = new(grid, new MovementCatalog(), new NavigationTuning(MaxSmoothingRaycastsPerTick: 1), 1000);
        WorldState world = new(map);
        using Simulation sim = new(world, new RuleSet(nav));
        Vector2 target = new(355, 55), previous = new(55, 55);
        EntityId id = world.CreateUnit(1, 1, previous, 100);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, id, target)));
        for (int tick = 0; tick < 4000; tick++)
        {
            sim.Step();
            Assert.InRange(nav.SmoothingRaycastsThisTick, 0, 1);
            Vector2 current = world.GetUnitAt(0).Position;
            Assert.True(grid.IsSegmentClear(previous, current, 1.4f));
            previous = current;
            if (Vector2.Distance(current, target) < .1f) break;
        }
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(id));
        Assert.InRange(Vector2.Distance(previous, target), 0, .1f);
        Assert.Equal(0, nav.CapacityExceeded);
        Assert.Equal(0, nav.FailedSearches);
    }

    [Fact]
    public void GlobalConnectivity_DoesNotRejectSmallerRadiusAssignedToTrackedClass()
    {
        OpaqueStructure[] obstacles = [
            new(new StructureId(1), 190, 0, 0, 210, 10, 198.8f),
            new(new StructureId(2), 190, 0, 201.7f, 210, 10, 400)];
        NavigationGrid grid = new(Map(400), obstacles, enableHierarchical: true);
        HierarchicalNavGraph graph = grid.GetHierarchicalGraph(1.1f);
        Assert.Equal(MovementClass.Tracked, graph.MovementClass);
        Vector2 start = new(55, 155), target = new(355, 155);
        graph.StartGlobalSearch(start, target, 1.1f);
        Vector2[] portals = new Vector2[1024];
        PathRequestStatus status = PathRequestStatus.Searching;
        for (int tick = 0; tick < 100 && status == PathRequestStatus.Searching; tick++)
            status = graph.ResumeGlobalSearch(50, 20_000, out _, portals, out _);
        Assert.Equal(PathRequestStatus.PathFound, status);
    }
}
