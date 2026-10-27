using System.Numerics;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class OrderPreviewTests
{
    private static HeightMap CreateFlatMap(int size = 65, float cellSize = 2.0f, float height = 0f)
    {
        ushort rawHeight = (ushort)(height / 0.01f);
        ushort[] heights = new ushort[size * size];
        if (rawHeight > 0)
        {
            Array.Fill(heights, rawHeight);
        }
        return new HeightMap(size, size, cellSize, 0.01f, heights);
    }

    [Fact]
    public void Case1_StaticEvaluator_IdentifiesAllBasicObstacleKinds()
    {
        // 1. Plain ground, water, dense forest, building, map boundary
        HeightMap map = CreateFlatMap(65, 2.0f); // 128m x 128m
        OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
        TerrainObstacle water = new(10f, 10f, 30f, 30f, TraversabilityCapabilities.Water);
        TerrainObstacle forest = new(70f, 70f, 90f, 90f, TraversabilityCapabilities.DenseForest);

        StaticGeometry geo = new(map, [building], ReadOnlySpan<BridgeStructure>.Empty, [water, forest]);
        DestinationEvaluator evaluator = new(geo, cellSize: 1.0f);

        const float unitRadius = 1.0f;
        const TraversabilityCapabilities vehicleCaps = TraversabilityCapabilities.DefaultVehicle;

        // Plain ground
        DestinationEvaluationResult groundResult = evaluator.Evaluate(new Vector2(20f, 50f), unitRadius, vehicleCaps);
        Assert.Equal(DestinationValidity.Valid, groundResult.Validity);
        Assert.Equal(DestinationBlockReason.None, groundResult.Reason);
        Assert.Equal(string.Empty, groundResult.ReasonText);

        // Water
        DestinationEvaluationResult waterResult = evaluator.Evaluate(new Vector2(20f, 20f), unitRadius, vehicleCaps);
        Assert.Equal(DestinationValidity.Blocked, waterResult.Validity);
        Assert.Equal(DestinationBlockReason.Water, waterResult.Reason);
        Assert.Equal("Water blocks this unit.", waterResult.ReasonText);

        // Dense forest
        DestinationEvaluationResult forestResult = evaluator.Evaluate(new Vector2(80f, 80f), unitRadius, vehicleCaps);
        Assert.Equal(DestinationValidity.Blocked, forestResult.Validity);
        Assert.Equal(DestinationBlockReason.DenseForest, forestResult.Reason);
        Assert.Equal("Dense forest blocks this unit.", forestResult.ReasonText);

        // Building center (cannot snap within 3m because center is 10m from all edges)
        DestinationEvaluationResult buildingResult = evaluator.Evaluate(new Vector2(50f, 50f), unitRadius, vehicleCaps, maxSnapDistance: 3.0f);
        Assert.Equal(DestinationValidity.Blocked, buildingResult.Validity);
        Assert.Equal(DestinationBlockReason.Building, buildingResult.Reason);
        Assert.Equal("Building blocks this position.", buildingResult.ReasonText);

        // Map boundary (strictly outside)
        DestinationEvaluationResult boundsResult = evaluator.Evaluate(new Vector2(-10f, 50f), unitRadius, vehicleCaps);
        Assert.Equal(DestinationValidity.Blocked, boundsResult.Validity);
        Assert.Equal(DestinationBlockReason.OutsideMapBounds, boundsResult.Reason);
        Assert.Equal("Outside map bounds.", boundsResult.ReasonText);
    }

    [Fact]
    public void Case2_SameRadiusUnitsWithDifferentCapabilities_DivergeCorrectly()
    {
        // 2. Two units with same radius (1.0m) and different capabilities (DefaultVehicle vs Amphibious / Foot)
        HeightMap map = CreateFlatMap(65, 2.0f);
        TerrainObstacle water = new(20f, 20f, 40f, 40f, TraversabilityCapabilities.Water);
        TerrainObstacle forest = new(60f, 60f, 80f, 80f, TraversabilityCapabilities.DenseForest);

        StaticGeometry geo = new(map, [], ReadOnlySpan<BridgeStructure>.Empty, [water, forest]);
        DestinationEvaluator evaluator = new(geo, cellSize: 1.0f);

        const float radius = 1.0f;
        const TraversabilityCapabilities tankCaps = TraversabilityCapabilities.DefaultVehicle;
        const TraversabilityCapabilities amphibiousCaps = TraversabilityCapabilities.DefaultVehicle | TraversabilityCapabilities.Water;
        const TraversabilityCapabilities footCaps = TraversabilityCapabilities.Foot | TraversabilityCapabilities.DenseForest;

        Vector2 waterPoint = new(30f, 30f);
        Vector2 forestPoint = new(70f, 70f);

        // Water test: Tank blocked, Amphibious valid
        DestinationEvaluationResult tankWater = evaluator.Evaluate(waterPoint, radius, tankCaps);
        Assert.Equal(DestinationValidity.Blocked, tankWater.Validity);
        Assert.Equal(DestinationBlockReason.Water, tankWater.Reason);

        DestinationEvaluationResult amphWater = evaluator.Evaluate(waterPoint, radius, amphibiousCaps);
        Assert.Equal(DestinationValidity.Valid, amphWater.Validity);
        Assert.Equal(DestinationBlockReason.None, amphWater.Reason);

        // Forest test: Tank blocked, Foot valid
        DestinationEvaluationResult tankForest = evaluator.Evaluate(forestPoint, radius, tankCaps);
        Assert.Equal(DestinationValidity.Blocked, tankForest.Validity);
        Assert.Equal(DestinationBlockReason.DenseForest, tankForest.Reason);

        DestinationEvaluationResult footForest = evaluator.Evaluate(forestPoint, radius, footCaps);
        Assert.Equal(DestinationValidity.Valid, footForest.Validity);
        Assert.Equal(DestinationBlockReason.None, footForest.Reason);
    }

    [Fact]
    public void Case4_NarrowGap_ReportsInsufficientClearanceForLargeRadius()
    {
        // 4. Narrow gap where clearance is insufficient
        HeightMap map = CreateFlatMap(65, 2.0f);
        // Gap of 1.5m between X=48.5 and X=50.0 along Z=50
        OpaqueStructure westWall = new(new StructureId(1), 20f, 0f, 20f, 48.5f, 10f, 80f);
        OpaqueStructure eastWall = new(new StructureId(2), 50.0f, 0f, 20f, 80.0f, 10f, 80f);

        StaticGeometry geo = new(map, [westWall, eastWall]);
        DestinationEvaluator evaluator = new(geo, cellSize: 0.5f);

        Vector2 gapCenter = new(49.25f, 50.0f);

        // Small unit with radius 0.6m: clearance is 0.75m > 0.6m - 0.05m -> Clear
        DestinationEvaluationResult smallUnit = evaluator.Evaluate(gapCenter, radius: 0.6f, maxSnapDistance: 0.0f);
        Assert.Equal(DestinationValidity.Valid, smallUnit.Validity);

        // Large unit with radius 1.2m: clearance is 0.75m < 1.15m -> Blocked with InsufficientClearance
        DestinationEvaluationResult largeUnit = evaluator.Evaluate(gapCenter, radius: 1.2f, maxSnapDistance: 0.0f);
        Assert.Equal(DestinationValidity.Blocked, largeUnit.Validity);
        Assert.Equal(DestinationBlockReason.InsufficientClearance, largeUnit.Reason);
        Assert.Equal("Not enough clearance.", largeUnit.ReasonText);
    }

    [Fact]
    public void Case5_ObstacleDestinationSnappedToValidAdjacentPositionWithinMaxSnapDistance()
    {
        // 5. Obstacle destination snapped to valid adjacent position within MaxSnapDistance
        HeightMap map = CreateFlatMap(65, 2.0f);
        // Building from 40 to 60 on X and Z
        OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
        StaticGeometry geo = new(map, [building]);
        DestinationEvaluator evaluator = new(geo, cellSize: 0.5f);

        const float radius = 0.8f;
        // Target is 1.0m inside the building west wall (X=41, Z=50)
        Vector2 insidePoint = new(41.0f, 50.0f);
        Vector2 startPoint = new(30.0f, 50.0f);

        DestinationEvaluationResult result = evaluator.Evaluate(
            insidePoint, radius, TraversabilityCapabilities.DefaultVehicle, targetSurface: 0, start: startPoint, maxSnapDistance: 3.0f);

        Assert.Equal(DestinationValidity.Adjusted, result.Validity);
        Assert.Equal(DestinationBlockReason.Building, result.Reason);
        // Snapped position must be outside the building and within 3m
        Assert.True(result.AdjustedTarget.X < 40.0f, $"Adjusted target {result.AdjustedTarget} should be west of wall (X < 40).");
        Assert.True(Vector2.Distance(insidePoint, result.AdjustedTarget) <= 3.0f);
        Assert.True(evaluator.IsPointClear(result.AdjustedTarget, radius));
    }

    [Fact]
    public void Case6_BridgeDeckVsGroundUnderBridgeSpan()
    {
        // 6. Bridge deck vs ground under bridge span
        HeightMap map = CreateFlatMap(100, 2.0f, height: 0f);
        // River under bridge spanning X=80..120, Z=0..198
        TerrainObstacle river = new(80f, 0f, 120f, 198f, TraversabilityCapabilities.Water);
        // Bridge across river along X=70..130 at Z=100, width=8m, deck at 4m
        BridgeStructure bridge = new(
            new StructureId(10),
            new Vector2(70f, 100f),
            new Vector2(130f, 100f),
            width: 8f,
            startHeight: 4f,
            endHeight: 4f,
            thickness: 0.5f);

        StaticGeometry geo = new(map, [], [bridge], [river]);
        DestinationEvaluator evaluator = new(geo, cellSize: 0.5f);

        const float radius = 1.0f;
        Vector2 midBridgePoint = new(100f, 100f);

        // 1. Raycast hit bridge deck: targetSurface = 10 (bridge ID)
        DestinationEvaluationResult onDeck = evaluator.Evaluate(
            midBridgePoint, radius, TraversabilityCapabilities.DefaultVehicle, targetSurface: 10);

        Assert.Equal(DestinationValidity.Valid, onDeck.Validity);
        Assert.Equal(10u, onDeck.TargetSurface);
        Assert.Equal(4.0f, onDeck.SurfaceHeight);

        // 2. Raycast hit ground under bridge (in the river): targetSurface = 0 (terrain)
        DestinationEvaluationResult underBridgeWater = evaluator.Evaluate(
            midBridgePoint, radius, TraversabilityCapabilities.DefaultVehicle, targetSurface: 0);

        Assert.Equal(DestinationValidity.Blocked, underBridgeWater.Validity);
        Assert.Equal(DestinationBlockReason.Water, underBridgeWater.Reason);
        Assert.Equal("Water blocks this unit.", underBridgeWater.ReasonText);
        Assert.Equal(0.0f, underBridgeWater.SurfaceHeight);

        // 3. Dry ground under a dry bridge span
        TerrainObstacle dryObstacle = new(0f, 0f, 1f, 1f, TraversabilityCapabilities.Water); // far away
        StaticGeometry dryGeo = new(map, [], [bridge], [dryObstacle]);
        DestinationEvaluator dryEvaluator = new(dryGeo, cellSize: 0.5f);

        DestinationEvaluationResult underBridgeDry = dryEvaluator.Evaluate(
            midBridgePoint, radius, TraversabilityCapabilities.DefaultVehicle, targetSurface: 0);

        Assert.Equal(DestinationValidity.Valid, underBridgeDry.Validity);
        Assert.Equal(0u, underBridgeDry.TargetSurface);
        Assert.Equal(0.0f, underBridgeDry.SurfaceHeight);
    }

    [Fact]
    public void Case7_StaticValidPoint_SimulationReturnsNoPath_WhenWalledIn()
    {
        // 7. Valid point that simulation subsequently fails with NoPath
        const int size = 65;
        HeightMap map = CreateFlatMap(size, 2.0f); // 128m x 128m
        // Walled enclosure around (80, 80)
        // Walls from 70 to 90 on X and Z
        OpaqueStructure north = new(new StructureId(1), 70f, 0f, 89f, 90f, 10f, 90f);
        OpaqueStructure south = new(new StructureId(2), 70f, 0f, 70f, 90f, 10f, 71f);
        OpaqueStructure west = new(new StructureId(3), 70f, 0f, 70f, 71f, 10f, 90f);
        OpaqueStructure east = new(new StructureId(4), 89f, 0f, 70f, 90f, 10f, 90f);

        StaticGeometry geo = new(map, [north, south, west, east]);
        DestinationEvaluator evaluator = new(geo, cellSize: 1.0f);
        NavigationGrid grid = new(geo, cellSize: 1.0f);

        Vector2 insideTarget = new(80.0f, 80.0f);
        const float radius = 0.8f;

        // Static evaluator pre-click check: destination itself is clear flat ground!
        DestinationEvaluationResult preview = evaluator.Evaluate(insideTarget, radius);
        Assert.Equal(DestinationValidity.Valid, preview.Validity);

        // Simulation attempt to reach it from outside:
        NavigationTests_MovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: radius);

        NavigationSystem nav = new(grid, catalog);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        using Simulation sim = new(world, rules);

        EntityId unit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 10f);
        bool enqueued = sim.TryEnqueue(1, new MoveCommand(1, 0, unit, insideTarget));
        Assert.True(enqueued);

        // Run simulation until pathfinder fails
        for (int i = 0; i < 50; i++)
        {
            sim.Step();
        }

        // The pathfinder discovers there is no route through the walls
        Assert.Equal(PathRequestStatus.NoPath, nav.GetUnitStatus(unit));
    }

    [Fact]
    public void BenchmarkAndDeduplication_LargeSelectionsShareProfileQueries()
    {
        // Performance test: 1, 20, 200 selected units with profile deduplication
        HeightMap map = CreateFlatMap(65, 2.0f);
        OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
        StaticGeometry geo = new(map, [building]);
        DestinationEvaluator evaluator = new(geo, cellSize: 1.0f);

        Vector2 target = new(35.0f, 50.0f); // Near building edge

        // Profile types in army: Tank (radius 1.2m), Recon (radius 0.8m), APC (radius 1.0m)
        (float Radius, TraversabilityCapabilities Caps, uint Surface)[] army200 =
            new (float, TraversabilityCapabilities, uint)[200];

        for (int i = 0; i < 200; i++)
        {
            if (i < 100)
                army200[i] = (1.2f, TraversabilityCapabilities.DefaultVehicle, 0);
            else if (i < 160)
                army200[i] = (0.8f, TraversabilityCapabilities.DefaultVehicle, 0);
            else
                army200[i] = (1.0f, TraversabilityCapabilities.DefaultVehicle, 0);
        }

        // Deduplication cache simulation
        int actualQueries = 0;
        Span<(float Radius, TraversabilityCapabilities Caps, uint Surface, DestinationEvaluationResult Result)> cache =
            stackalloc (float, TraversabilityCapabilities, uint, DestinationEvaluationResult)[8];
        int cacheCount = 0;

        for (int i = 0; i < 200; i++)
        {
            var unit = army200[i];
            bool found = false;
            for (int c = 0; c < cacheCount; c++)
            {
                if (cache[c].Radius == unit.Radius &&
                    cache[c].Caps == unit.Caps &&
                    cache[c].Surface == unit.Surface)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                actualQueries++;
                DestinationEvaluationResult res = evaluator.Evaluate(target, unit.Radius, unit.Caps, unit.Surface);
                cache[cacheCount++] = (unit.Radius, unit.Caps, unit.Surface, res);
            }
        }

        // 200 units of 3 distinct profiles requires exactly 3 queries
        Assert.Equal(3, actualQueries);
        Assert.Equal(3, cacheCount);
    }

    [Fact]
    public void SteadyStateEvaluation_ProducesZeroAllocations()
    {
        // 12. Steady-state allocation-free test
        HeightMap map = CreateFlatMap(65, 2.0f);
        OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
        TerrainObstacle water = new(10f, 10f, 30f, 30f, TraversabilityCapabilities.Water);
        StaticGeometry geo = new(map, [building], ReadOnlySpan<BridgeStructure>.Empty, [water]);
        DestinationEvaluator evaluator = new(geo, cellSize: 1.0f);

        Vector2 target = new(25.0f, 50.0f);

        // Warmup JIT
        for (int i = 0; i < 50; i++)
        {
            evaluator.Evaluate(target, 1.0f, TraversabilityCapabilities.DefaultVehicle, 0);
        }

        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 500; i++)
        {
            evaluator.Evaluate(target, 1.0f, TraversabilityCapabilities.DefaultVehicle, 0);
        }

        long afterAllocated = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, afterAllocated - beforeAllocated);
    }

    [Fact]
    public void Regression_ElevatedGroundedBuildings_AgreementWithNavigation()
    {
        // Issue 2: Grounded building on elevated ground (MinY = 4.0m) must be included in client evaluation
        HeightMap map = CreateFlatMap(65, 2.0f, height: 4.0f);
        OpaqueStructure elevatedBuilding = new(
            new StructureId(1),
            minX: 40f, minY: 4.0f, minZ: 40f,
            maxX: 60f, maxY: 10.0f, maxZ: 60f,
            isGrounded: true);

        StaticGeometry geo = new(map, [elevatedBuilding]);
        DestinationEvaluator evaluator = new(geo, cellSize: 1.0f);
        NavigationGrid grid = new(geo, cellSize: 1.0f);

        Assert.Equal(1, evaluator.GroundedObstacleCount);

        const float radius = 0.8f;

        // 1. Point deep inside elevated building
        Vector2 insidePoint = new(50.0f, 50.0f);
        DestinationEvaluationResult insideResult = evaluator.Evaluate(insidePoint, radius, maxSnapDistance: 2.0f);
        Assert.Equal(DestinationValidity.Blocked, insideResult.Validity);
        Assert.Equal(DestinationBlockReason.Building, insideResult.Reason);
        Assert.False(grid.IsPointClear(insidePoint, radius));

        // 2. Point clearly outside elevated building
        Vector2 outsidePoint = new(20.0f, 50.0f);
        DestinationEvaluationResult outsideResult = evaluator.Evaluate(outsidePoint, radius);
        Assert.Equal(DestinationValidity.Valid, outsideResult.Validity);
        Assert.Equal(DestinationBlockReason.None, outsideResult.Reason);
        Assert.True(grid.IsPointClear(outsidePoint, radius));

        // 3. Grid sampling: 100% agreement between client evaluator and simulation grid
        for (float x = 30f; x <= 70f; x += 4f)
        {
            for (float z = 30f; z <= 70f; z += 4f)
            {
                Vector2 pt = new(x, z);
                bool evalClear = evaluator.IsPointClear(pt, radius);
                bool gridClear = grid.IsPointClear(pt, radius);
                Assert.Equal(gridClear, evalClear);
            }
        }
    }

    [Fact]
    public void Regression_SnappingDependsOnStartPosition_DifferentStartPositionsYieldDifferentSnaps()
    {
        // Issue 1: Snapping depends on starting position; units at different starts must get distinct snaps
        HeightMap map = CreateFlatMap(65, 2.0f);
        OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
        StaticGeometry geo = new(map, [building]);
        DestinationEvaluator evaluator = new(geo, cellSize: 0.5f);

        const float radius = 0.8f;
        // Target is inside near the north-west corner (41, 59)
        Vector2 insideCorner = new(41.0f, 59.0f);
        Vector2 startWest = new(30.0f, 50.0f);
        Vector2 startNorth = new(50.0f, 70.0f);

        DestinationEvaluationResult snapWest = evaluator.Evaluate(
            insideCorner, radius, TraversabilityCapabilities.DefaultVehicle, 0, start: startWest, maxSnapDistance: 3.0f);
        DestinationEvaluationResult snapNorth = evaluator.Evaluate(
            insideCorner, radius, TraversabilityCapabilities.DefaultVehicle, 0, start: startNorth, maxSnapDistance: 3.0f);

        Assert.Equal(DestinationValidity.Adjusted, snapWest.Validity);
        Assert.Equal(DestinationValidity.Adjusted, snapNorth.Validity);

        // West start snaps to west side (X < 40)
        Assert.True(snapWest.AdjustedTarget.X < 40.0f, $"snapWest {snapWest.AdjustedTarget} should be on the west side.");
        // North start snaps to north side (Y > 60)
        Assert.True(snapNorth.AdjustedTarget.Y > 60.0f, $"snapNorth {snapNorth.AdjustedTarget} should be on the north side.");
        // The two snapped targets must be distinct
        Assert.NotEqual(snapWest.AdjustedTarget, snapNorth.AdjustedTarget);
    }

    private sealed class NavigationTests_MovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public void Register(ushort definition, float speed, float radius) =>
            _profiles[definition] = new UnitMovementProfile(speed, radius);

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }
}
