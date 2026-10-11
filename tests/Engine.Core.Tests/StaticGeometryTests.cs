using System.Numerics;
using DeadGround.Engine;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class StaticGeometryTests
{
    private static HeightMap CreateFlatMap(int columns = 16, int rows = 16, float cellSize = 2.0f, float height = 0.0f)
    {
        ushort[] samples = new ushort[columns * rows];
        ushort sampleValue = (ushort)MathF.Round(height * 100f);
        Array.Fill(samples, sampleValue);
        return new HeightMap(columns, rows, cellSize, 0.01f, samples);
    }

    // -------------------------------------------------------------------------
    // 1. Terrain Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ClearSegmentOverFlatGround()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);

        Vector3 start = new(2f, 1.5f, 2f);
        Vector3 end = new(20f, 1.5f, 20f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.True(result.IsClear);
        Assert.Equal(ObstructionKind.None, result.ObstructionKind);
        Assert.Equal(1.0f, result.RayFraction);
        Assert.Equal(Vector3.Distance(start, end), result.Distance, 1e-3f);
        Assert.Equal(end, result.HitPoint);
    }

    [Fact]
    public void HillOcclusionBetweenObserverAndTarget()
    {
        HeightMap map = HeightMap.CreatePrototype();
        StaticGeometry geo = new(map, []);

        // Peak of 9m hill is at (86, 44)
        Vector3 start = new(50f, 1.5f, 44f);
        Vector3 end = new(122f, 1.5f, 44f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.False(result.IsClear);
        Assert.Equal(ObstructionKind.Terrain, result.ObstructionKind);
        Assert.True(result.RayFraction > 0f && result.RayFraction < 1.0f);
        Assert.True(result.HitPoint.X > 50f && result.HitPoint.X < 86f);
    }

    [Fact]
    public void ObserverElevationClearsHillOcclusion()
    {
        HeightMap map = HeightMap.CreatePrototype();
        StaticGeometry geo = new(map, []);

        // Both elevated to 15m (hill peak is 9m)
        Vector3 start = new(70f, 15f, 44f);
        Vector3 end = new(102f, 15f, 44f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.True(result.IsClear);
        Assert.Equal(ObstructionKind.None, result.ObstructionKind);
    }

    [Fact]
    public void NarrowRidgeOcclusion()
    {
        // 5 columns, 5 rows, cellSize = 2m.
        // Columns: x = 0, 2, 4, 6, 8. Column index 2 is x = 4m.
        // Ridge at column index 2 has height 5m, while index 1 and 3 are 0m.
        int cols = 5, rows = 5;
        ushort[] samples = new ushort[cols * rows];
        for (int z = 0; z < rows; z++)
        {
            samples[z * cols + 2] = (ushort)(5.0f * 100);
        }
        HeightMap map = new(cols, rows, 2.0f, 0.01f, samples);
        StaticGeometry geo = new(map, []);

        Vector3 start = new(1f, 1.0f, 4f);
        Vector3 end = new(7f, 1.0f, 4f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.False(result.IsClear);
        Assert.Equal(ObstructionKind.Terrain, result.ObstructionKind);
        Assert.True(result.HitPoint.X >= 2.0f && result.HitPoint.X <= 6.0f);
    }

    [Fact]
    public void RidgeWithinCellOccludesRayEvenWhenBothEndpointsAboveGround()
    {
        // Create a single 2x2 heightmap cell (cellSize = 10m).
        // Corner heights: (0,0)=0m, (10,0)=10m, (0,10)=10m, (10,10)=0m.
        // Bilinear saddle: H(u,v) = 10u + 10v - 20uv.
        // Along diagonal from (0,0) to (10,10), u = v = t:
        // H(t) = 20t - 20t^2. At t = 0.5 (center), H = 10 - 5 = 5m!
        // At t = 0: H = 0m. At t = 1: H = 0m.
        // Ray from (0, 1.0m, 0) to (10, 1.0m, 10).
        // At entry: ray Y = 1m > ground 0m.
        // At exit: ray Y = 1m > ground 0m.
        // In center (t = 0.5): ray Y = 1m, but ground H = 5m!
        // The ray penetrates the surface inside the cell!
        ushort[] samples = [(ushort)0, (ushort)1000, (ushort)1000, (ushort)0];
        HeightMap map = new(2, 2, 10.0f, 0.01f, samples);
        StaticGeometry geo = new(map, []);

        Vector3 start = new(0f, 1.0f, 0f);
        Vector3 end = new(10f, 1.0f, 10f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.False(result.IsClear);
        Assert.Equal(ObstructionKind.Terrain, result.ObstructionKind);
        Assert.True(result.RayFraction > 0f && result.RayFraction < 0.5f);
        Assert.True(result.HitPoint.Y <= 5.0f);
    }

    [Fact]
    public void VerticalQueries()
    {
        HeightMap map = CreateFlatMap(10, 10, 2f, height: 5.0f);
        StaticGeometry geo = new(map, []);

        // Downward ray crossing ground: start at Y=10, end at Y=0
        Vector3 downStart = new(5f, 10f, 5f);
        Vector3 downEnd = new(5f, 0f, 5f);
        LineOfSightResult downResult = geo.CheckLineOfSight(downStart, downEnd);

        Assert.False(downResult.IsClear);
        Assert.Equal(ObstructionKind.Terrain, downResult.ObstructionKind);
        Assert.Equal(5.0f, downResult.HitPoint.Y, 1e-3f);
        Assert.Equal(0.5f, downResult.RayFraction, 1e-3f);

        // Upward ray from above ground into sky: start at Y=6, end at Y=20
        Vector3 upStart = new(5f, 6f, 5f);
        Vector3 upEnd = new(5f, 20f, 5f);
        LineOfSightResult upResult = geo.CheckLineOfSight(upStart, upEnd);

        Assert.True(upResult.IsClear);
        Assert.Equal(ObstructionKind.None, upResult.ObstructionKind);
    }

    [Fact]
    public void RectangularNonSquareMap()
    {
        // 5 columns (X in [0..8]), 10 rows (Z in [0..18])
        HeightMap map = CreateFlatMap(columns: 5, rows: 10, cellSize: 2f, height: 0f);
        StaticGeometry geo = new(map, []);

        Vector3 start = new(2f, 1f, 2f);
        Vector3 end = new(6f, 1f, 16f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.True(result.IsClear);
        Assert.Equal(ObstructionKind.None, result.ObstructionKind);
    }

    // -------------------------------------------------------------------------
    // 2. Structure Tests
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildingBetweenObserverAndTargetBlocksLineOfSight()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure building = new(new StructureId(1), new Vector3(8f, 0f, 8f), new Vector3(12f, 5f, 12f));
        StaticGeometry geo = new(map, [building]);

        Vector3 start = new(10f, 1.5f, 4f);
        Vector3 end = new(10f, 1.5f, 16f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.False(result.IsClear);
        Assert.Equal(ObstructionKind.Structure, result.ObstructionKind);
        Assert.Equal(new StructureId(1), result.ObstructingStructureId);
        Assert.Equal(8.0f, result.HitPoint.Z, 1e-3f);
        Assert.Equal(4.0f, result.Distance, 1e-3f);
    }

    [Fact]
    public void BuildingBehindTargetDoesNotBlock()
    {
        HeightMap map = CreateFlatMap();
        // Building at Z in [20..25]
        OpaqueStructure building = new(new StructureId(1), new Vector3(8f, 0f, 20f), new Vector3(12f, 5f, 25f));
        StaticGeometry geo = new(map, [building]);

        // Target at Z = 15 (before building)
        Vector3 start = new(10f, 1.5f, 5f);
        Vector3 end = new(10f, 1.5f, 15f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.True(result.IsClear);
        Assert.Equal(ObstructionKind.None, result.ObstructionKind);
        Assert.Equal(end, result.HitPoint);
    }

    [Fact]
    public void RayOverRoofClears()
    {
        HeightMap map = CreateFlatMap();
        // Roof is at Y = 4.0
        OpaqueStructure building = new(new StructureId(1), new Vector3(8f, 0f, 8f), new Vector3(12f, 4.0f, 12f));
        StaticGeometry geo = new(map, [building]);

        // Ray passes at Y = 5.0
        Vector3 start = new(10f, 5.0f, 4f);
        Vector3 end = new(10f, 5.0f, 16f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.True(result.IsClear);
        Assert.Equal(ObstructionKind.None, result.ObstructionKind);
    }

    [Fact]
    public void LowWallBlocksHullWhileTurretIsVisible()
    {
        HeightMap map = CreateFlatMap();
        // Low wall: height 1.3m at Z in [10..11]
        OpaqueStructure wall = new(new StructureId(2), new Vector3(0f, 0f, 10f), new Vector3(20f, 1.3f, 11f));
        StaticGeometry geo = new(map, [wall]);

        Vector3 observer = new(10f, 1.0f, 5f);
        Vector3 targetHull = new(10f, 0.5f, 17f);
        Vector3 targetTurret = new(10f, 2.5f, 17f);

        LineOfSightResult hullResult = geo.CheckLineOfSight(observer, targetHull);
        LineOfSightResult turretResult = geo.CheckLineOfSight(observer, targetTurret);

        // Hull is blocked by the wall
        Assert.False(hullResult.IsClear);
        Assert.Equal(ObstructionKind.Structure, hullResult.ObstructionKind);
        Assert.Equal(new StructureId(2), hullResult.ObstructingStructureId);

        // Turret clears over the wall
        Assert.True(turretResult.IsClear);
        Assert.Equal(ObstructionKind.None, turretResult.ObstructionKind);
    }

    [Fact]
    public void ThinWallBlocksLineOfSight()
    {
        HeightMap map = CreateFlatMap();
        // Thin wall: 0.1m thickness at Z in [10.0..10.1]
        OpaqueStructure thinWall = new(new StructureId(5), new Vector3(0f, 0f, 10.0f), new Vector3(20f, 3.0f, 10.1f));
        StaticGeometry geo = new(map, [thinWall]);

        Vector3 start = new(10f, 1.5f, 5f);
        Vector3 end = new(10f, 1.5f, 15f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.False(result.IsClear);
        Assert.Equal(ObstructionKind.Structure, result.ObstructionKind);
        Assert.Equal(new StructureId(5), result.ObstructingStructureId);
        Assert.Equal(10.0f, result.HitPoint.Z, 1e-3f);
    }

    [Fact]
    public void ObserverInsideVolumeIsImmediatelyBlocked()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure building = new(new StructureId(1), new Vector3(5f, 0f, 5f), new Vector3(15f, 5f, 15f));
        StaticGeometry geo = new(map, [building]);

        // Start point is inside the building
        Vector3 start = new(10f, 2f, 10f);
        Vector3 end = new(20f, 2f, 20f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.False(result.IsClear);
        Assert.Equal(ObstructionKind.Structure, result.ObstructionKind);
        Assert.Equal(new StructureId(1), result.ObstructingStructureId);
        Assert.Equal(0f, result.Distance);
        Assert.Equal(0f, result.RayFraction);
        Assert.Equal(start, result.HitPoint);
    }

    [Fact]
    public void MultipleObstaclesPicksClosest()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure wall1 = new(new StructureId(1), new Vector3(5f, 0f, 8f), new Vector3(15f, 3f, 9f));
        OpaqueStructure wall2 = new(new StructureId(2), new Vector3(5f, 0f, 12f), new Vector3(15f, 3f, 13f));
        OpaqueStructure wall3 = new(new StructureId(3), new Vector3(5f, 0f, 16f), new Vector3(15f, 3f, 17f));

        StaticGeometry geo = new(map, [wall3, wall1, wall2]); // Shuffled insertion

        Vector3 start = new(10f, 1f, 2f);
        Vector3 end = new(10f, 1f, 20f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.False(result.IsClear);
        Assert.Equal(ObstructionKind.Structure, result.ObstructionKind);
        Assert.Equal(new StructureId(1), result.ObstructingStructureId);
        Assert.Equal(8.0f, result.HitPoint.Z, 1e-3f);
        Assert.Equal(6.0f, result.Distance, 1e-3f);
    }

    [Fact]
    public void StructureSpanningMultipleGridCellsIsHandled()
    {
        HeightMap map = CreateFlatMap(32, 32, 2f);
        // StructureCellSize is 8m. A 24m wide structure spans at least 3 grid cells.
        OpaqueStructure bigBuilding = new(new StructureId(10), new Vector3(4f, 0f, 4f), new Vector3(28f, 5f, 28f));
        StaticGeometry geo = new(map, [bigBuilding], structureCellSize: 8.0f);

        Vector3 start = new(2f, 1.5f, 16f);
        Vector3 end = new(30f, 1.5f, 16f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.False(result.IsClear);
        Assert.Equal(new StructureId(10), result.ObstructingStructureId);
        Assert.Equal(4.0f, result.HitPoint.X, 1e-3f);
    }

    [Fact]
    public void RayPassingExactlyAlongCellBoundaryOrCorner()
    {
        HeightMap map = CreateFlatMap(16, 16, 4f);
        // Grid lines are at multiples of 4m. Wall is placed right on a grid line.
        OpaqueStructure wall = new(new StructureId(7), new Vector3(7.9f, 0f, 4f), new Vector3(8.1f, 3f, 16f));
        StaticGeometry geo = new(map, [wall], structureCellSize: 8.0f);

        // Ray traveling perpendicular through X = 8.0m
        Vector3 start = new(4f, 1.5f, 8f);
        Vector3 end = new(12f, 1.5f, 8f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.False(result.IsClear);
        Assert.Equal(new StructureId(7), result.ObstructingStructureId);
        Assert.Equal(7.9f, result.HitPoint.X, 1e-3f);
    }

    [Fact]
    public void OrderIndependenceOfInputStructures()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure s1 = new(new StructureId(1), new Vector3(5f, 0f, 10f), new Vector3(15f, 3f, 12f));
        OpaqueStructure s2 = new(new StructureId(2), new Vector3(5f, 0f, 10f), new Vector3(15f, 3f, 12f)); // identical bounds, different ID

        StaticGeometry geoA = new(map, [s1, s2]);
        StaticGeometry geoB = new(map, [s2, s1]);

        Vector3 start = new(10f, 1.5f, 2f);
        Vector3 end = new(10f, 1.5f, 20f);

        LineOfSightResult resA = geoA.CheckLineOfSight(start, end);
        LineOfSightResult resB = geoB.CheckLineOfSight(start, end);

        Assert.Equal(resA.IsClear, resB.IsClear);
        Assert.Equal(resA.ObstructionKind, resB.ObstructionKind);
        Assert.Equal(resA.ObstructingStructureId, resB.ObstructingStructureId);
        Assert.Equal(resA.HitPoint, resB.HitPoint);
        Assert.Equal(resA.Distance, resB.Distance, 1e-3f);
    }

    // -------------------------------------------------------------------------
    // 3. Contract & Edge Cases
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptyStructuresCollectionOnlyChecksTerrain()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);

        Assert.Empty(geo.Structures);

        Vector3 start = new(2f, 1f, 2f);
        Vector3 end = new(10f, 1f, 10f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.True(result.IsClear);
    }

    [Fact]
    public void DefensiveCopyPreservesImmutability()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure s1 = new(new StructureId(1), new Vector3(5f, 0f, 5f), new Vector3(10f, 4f, 10f));
        OpaqueStructure[] array = [s1];

        StaticGeometry geo = new(map, array);

        // Mutate original array
        OpaqueStructure s2 = new(new StructureId(2), new Vector3(15f, 0f, 15f), new Vector3(20f, 4f, 20f));
        array[0] = s2;

        Assert.Single(geo.Structures);
        Assert.Equal(new StructureId(1), geo.Structures[0].Id);
    }

    [Fact]
    public void InvalidDimensionsAndCoordinatesThrow()
    {
        HeightMap map = CreateFlatMap();

        // Negative/inverted dimensions
        Assert.Throws<ArgumentException>(() =>
            new OpaqueStructure(new StructureId(1), new Vector3(10f, 0f, 10f), new Vector3(5f, 4f, 10f)));

        Assert.Throws<ArgumentException>(() =>
            new OpaqueStructure(new StructureId(1), new Vector3(5f, 5f, 5f), new Vector3(10f, 2f, 10f)));

        // Non-positive ID
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OpaqueStructure(new StructureId(0), new Vector3(0f, 0f, 0f), new Vector3(5f, 5f, 5f)));

        // Non-finite coordinates
        Assert.Throws<ArgumentException>(() =>
            new OpaqueStructure(new StructureId(1), new Vector3(float.NaN, 0f, 0f), new Vector3(5f, 5f, 5f)));

        // Duplicate IDs in StaticGeometry
        OpaqueStructure a = new(new StructureId(1), new Vector3(2f, 0f, 2f), new Vector3(4f, 4f, 4f));
        OpaqueStructure b = new(new StructureId(1), new Vector3(6f, 0f, 6f), new Vector3(8f, 4f, 8f));
        Assert.Throws<ArgumentException>(() => new StaticGeometry(map, [a, b]));

        // Structure outside map bounds (map width = 30m)
        OpaqueStructure outside = new(new StructureId(2), new Vector3(25f, 0f, 25f), new Vector3(35f, 4f, 35f));
        Assert.Throws<ArgumentOutOfRangeException>(() => new StaticGeometry(map, [outside]));
    }

    [Fact]
    public void NonFiniteQueryCoordinatesThrow()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geo = new(map, []);

        Assert.Throws<ArgumentException>(() =>
            geo.CheckLineOfSight(new Vector3(float.NaN, 1f, 1f), new Vector3(5f, 1f, 5f)));

        Assert.Throws<ArgumentException>(() =>
            geo.CheckLineOfSight(new Vector3(1f, 1f, 1f), new Vector3(5f, float.PositiveInfinity, 5f)));
    }

    [Fact]
    public void CoincidentStartAndEndPoints()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure s = new(new StructureId(1), new Vector3(5f, 0f, 5f), new Vector3(10f, 4f, 10f));
        StaticGeometry geo = new(map, [s]);

        // Point in open air
        Vector3 air = new(2f, 2f, 2f);
        LineOfSightResult airRes = geo.CheckLineOfSight(air, air);
        Assert.True(airRes.IsClear);

        // Point inside structure
        Vector3 inside = new(7f, 2f, 7f);
        LineOfSightResult structRes = geo.CheckLineOfSight(inside, inside);
        Assert.False(structRes.IsClear);
        Assert.Equal(ObstructionKind.Structure, structRes.ObstructionKind);
        Assert.Equal(new StructureId(1), structRes.ObstructingStructureId);

        // Point underground
        Vector3 under = new(2f, -1f, 2f);
        LineOfSightResult underRes = geo.CheckLineOfSight(under, under);
        Assert.False(underRes.IsClear);
        Assert.Equal(ObstructionKind.Terrain, underRes.ObstructionKind);
    }

    [Fact]
    public void PointsOnGroundSurfaceLookingAwayAreClear()
    {
        HeightMap map = CreateFlatMap(16, 16, 2f, height: 2.0f);
        StaticGeometry geo = new(map, []);

        // Start point is exactly on the ground (Y = 2.0m), looking upward to (10, 5, 10)
        Vector3 onGround = new(2f, 2.0f, 2f);
        Vector3 targetInAir = new(10f, 5.0f, 10f);

        LineOfSightResult result = geo.CheckLineOfSight(onGround, targetInAir);

        Assert.True(result.IsClear);
        Assert.Equal(ObstructionKind.None, result.ObstructionKind);
    }

    [Fact]
    public void GrazingRayAlongOuterFaceWithoutEnteringIsClear()
    {
        HeightMap map = CreateFlatMap();
        // Box at X in [5..10], Y in [0..5], Z in [5..10]
        OpaqueStructure box = new(new StructureId(1), new Vector3(5f, 0f, 5f), new Vector3(10f, 5f, 10f));
        StaticGeometry geo = new(map, [box]);

        // Ray runs along X = 10.0 (the outer boundary plane) at Y = 2.5, from Z = 2 to Z = 15
        Vector3 start = new(10.0f, 2.5f, 2f);
        Vector3 end = new(10.0f, 2.5f, 15f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end);

        Assert.True(result.IsClear);
        Assert.Equal(ObstructionKind.None, result.ObstructionKind);
    }

    [Fact]
    public void ZeroAllocationsAfterWarmup()
    {
        HeightMap heightMap = HeightMap.CreatePrototype();
        OpaqueStructure building = new(new StructureId(1), new Vector3(80f, 0f, 96f), new Vector3(92f, 6.0f, 104f));
        OpaqueStructure lowWall = new(new StructureId(2), new Vector3(14f, 0f, 20f), new Vector3(28f, 1.3f, 21f));
        StaticGeometry geo = new(heightMap, [building, lowWall]);

        Vector3 start = new(15f, 1.0f, 10f);
        Vector3 end1 = new(15f, 0.5f, 18f);
        Vector3 end2 = new(86f, 1.0f, 112f);
        Vector3 end3 = new(122f, 1.5f, 44f);

        // Warm up
        for (int i = 0; i < 50; i++)
        {
            geo.CheckLineOfSight(start, end1);
            geo.CheckLineOfSight(start, end2);
            geo.CheckLineOfSight(start, end3);
        }

        // Measure allocations over 1000 queries
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++)
        {
            geo.CheckLineOfSight(start, end1);
            geo.CheckLineOfSight(start, end2);
            geo.CheckLineOfSight(start, end3);
        }

        long allocatedAfter = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, allocatedAfter - allocatedBefore);
    }

    // -------------------------------------------------------------------------
    // 5. Oracle Comparison: Spatial Index vs Brute Force
    // -------------------------------------------------------------------------

    [Fact]
    public void SpatialIndexMatchesBruteForceOverManyQueries()
    {
        HeightMap map = HeightMap.CreatePrototype();
        List<OpaqueStructure> structures = new();

        // Procedurally distribute 25 structures
        int id = 1;
        for (int z = 10; z <= 110; z += 25)
        {
            for (int x = 10; x <= 110; x += 25)
            {
                structures.Add(new OpaqueStructure(
                    new StructureId(id++),
                    new Vector3(x, 0f, z),
                    new Vector3(x + 10f, 4f + (id % 4), z + 8f)));
            }
        }

        StaticGeometry geo = new(map, structures, structureCellSize: 16f);

        // Generate systematic grid of queries
        Random rng = new(42);
        for (int i = 0; i < 200; i++)
        {
            Vector3 start = new(
                (float)(rng.NextDouble() * 120 + 4),
                (float)(rng.NextDouble() * 8 + 1),
                (float)(rng.NextDouble() * 120 + 4));

            Vector3 end = new(
                (float)(rng.NextDouble() * 120 + 4),
                (float)(rng.NextDouble() * 8 + 1),
                (float)(rng.NextDouble() * 120 + 4));

            LineOfSightResult indexedResult = geo.CheckLineOfSight(start, end);
            LineOfSightResult bruteResult = geo.CheckLineOfSightBruteForce(start, end);

            Assert.Equal(bruteResult.IsClear, indexedResult.IsClear);
            Assert.Equal(bruteResult.ObstructionKind, indexedResult.ObstructionKind);
            if (!indexedResult.IsClear && indexedResult.ObstructionKind == ObstructionKind.Structure)
            {
                Assert.Equal(bruteResult.ObstructingStructureId, indexedResult.ObstructingStructureId);
            }
            Assert.Equal(bruteResult.Distance, indexedResult.Distance, 1e-2f);
        }
    }

    // -------------------------------------------------------------------------
    // 6. Regression Tests for Reported Edge Cases
    // -------------------------------------------------------------------------

    [Fact]
    public void TerrainTangentOriginAtCurvedSaddleSurfaceBlocksImmediatePlunge()
    {
        // 2x2 saddle cell with samples [0, 0, 0, 1000] and scale 0.01 has H(t) = 10 * t^2 along diagonal.
        HeightMap map = new(2, 2, 10f, 0.01f, [(ushort)0, (ushort)0, (ushort)0, (ushort)1000]);
        StaticGeometry geo = new(map, []);

        // Ray starts at (0, 0, 0) and aims at (10, 0, 10).
        // Midpoint terrain is 2.5m above ray, endpoint is underground.
        LineOfSightResult plunge = geo.CheckLineOfSight(new Vector3(0, 0, 0), new Vector3(10, 0, 10));
        Assert.False(plunge.IsClear);
        Assert.Equal(ObstructionKind.Terrain, plunge.ObstructionKind);
        Assert.True(plunge.RayFraction < 0.05f);
        Assert.True(plunge.Distance < 0.5f);
        Assert.True(Vector3.Distance(new Vector3(0, 0, 0), plunge.HitPoint) < 0.5f);

        // Counterpart departing into air: starts on surface at (0, 0, 0), ends at (10, 20, 10).
        // Terrain midpoint is 2.5m, ray is 10m. Clears the terrain.
        LineOfSightResult air = geo.CheckLineOfSight(new Vector3(0, 0, 0), new Vector3(10, 20, 10));
        Assert.True(air.IsClear);
        Assert.Equal(ObstructionKind.None, air.ObstructionKind);
        Assert.Equal(1.0f, air.RayFraction);
    }

    [Fact]
    public void ThinWallRemainsOpaqueOnLongAndShortRays()
    {
        // 10 km flat map with a 5 mm thin wall at X in [5000, 5000.005]
        HeightMap map = CreateFlatMap(2, 2, 10000f, 0f);
        OpaqueStructure thinWall = new(
            new StructureId(4),
            new Vector3(5000f, 0f, 0f),
            new Vector3(5000.005f, 3f, 10000f));
        StaticGeometry geo = new(map, [thinWall], structureCellSize: 10000f);

        // Short 2-meter ray through wall
        LineOfSightResult shortRay = geo.CheckLineOfSight(new Vector3(4999f, 1f, 5000f), new Vector3(5001f, 1f, 5000f));
        Assert.False(shortRay.IsClear);
        Assert.Equal(ObstructionKind.Structure, shortRay.ObstructionKind);
        Assert.Equal(new StructureId(4), shortRay.ObstructingStructureId);

        // Long 10-kilometer ray through wall
        LineOfSightResult longRay = geo.CheckLineOfSight(new Vector3(1f, 1f, 5000f), new Vector3(9999f, 1f, 5000f));
        Assert.False(longRay.IsClear);
        Assert.Equal(ObstructionKind.Structure, longRay.ObstructionKind);
        Assert.Equal(new StructureId(4), longRay.ObstructingStructureId);

        // Ray passing above the 3-meter wall is clear
        LineOfSightResult aboveRay = geo.CheckLineOfSight(new Vector3(4999f, 4f, 5000f), new Vector3(5001f, 4f, 5000f));
        Assert.True(aboveRay.IsClear);
        Assert.Equal(ObstructionKind.None, aboveRay.ObstructionKind);

        // Ray parallel to the wall outside its bounds is clear
        LineOfSightResult parallelRay = geo.CheckLineOfSight(new Vector3(5000.01f, 1f, 100f), new Vector3(5000.01f, 1f, 9900f));
        Assert.True(parallelRay.IsClear);
        Assert.Equal(ObstructionKind.None, parallelRay.ObstructionKind);
    }

    [Fact]
    public void StructuresCollectionBackingArrayCannotBeMutated()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure wall = new(new StructureId(1), new Vector3(5f, 0f, 5f), new Vector3(10f, 5f, 10f));
        StaticGeometry geo = new(map, [wall]);

        Assert.False(geo.Structures is OpaqueStructure[]);
        Assert.Throws<InvalidCastException>(() => (OpaqueStructure[])geo.Structures);
        Assert.Throws<NotSupportedException>(() =>
            ((System.Collections.Generic.IList<OpaqueStructure>)geo.Structures)[0] =
                new OpaqueStructure(new StructureId(3), new Vector3(20f, 0f, 20f), new Vector3(25f, 5f, 25f)));
    }

    [Fact]
    public void DeterministicTieBreakingAcrossStructurePermutations()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure box1 = new(new StructureId(1), new Vector3(5f, 0f, 5f), new Vector3(10f, 5f, 10f));
        OpaqueStructure box2 = new(new StructureId(2), new Vector3(5f, 0f, 5f), new Vector3(10f, 5f, 10f));

        StaticGeometry geoA = new(map, [box1, box2]);
        StaticGeometry geoB = new(map, [box2, box1]);

        // Vertical query: both boxes have roof at Y=5m
        Vector3 vertStart = new(7f, 10f, 7f);
        Vector3 vertEnd = new(7f, 1f, 7f);
        LineOfSightResult vertA = geoA.CheckLineOfSight(vertStart, vertEnd);
        LineOfSightResult vertB = geoB.CheckLineOfSight(vertStart, vertEnd);
        LineOfSightResult vertBruteA = geoA.CheckLineOfSightBruteForce(vertStart, vertEnd);
        LineOfSightResult vertBruteB = geoB.CheckLineOfSightBruteForce(vertStart, vertEnd);

        Assert.Equal(new StructureId(1), vertA.ObstructingStructureId);
        Assert.Equal(new StructureId(1), vertB.ObstructingStructureId);
        Assert.Equal(new StructureId(1), vertBruteA.ObstructingStructureId);
        Assert.Equal(new StructureId(1), vertBruteB.ObstructingStructureId);

        // Inside volume query: starting inside both boxes
        Vector3 insideStart = new(7f, 2f, 7f);
        Vector3 insideEnd = new(15f, 2f, 15f);
        LineOfSightResult insideA = geoA.CheckLineOfSight(insideStart, insideEnd);
        LineOfSightResult insideB = geoB.CheckLineOfSight(insideStart, insideEnd);
        LineOfSightResult insideBruteA = geoA.CheckLineOfSightBruteForce(insideStart, insideEnd);
        LineOfSightResult insideBruteB = geoB.CheckLineOfSightBruteForce(insideStart, insideEnd);

        Assert.Equal(new StructureId(1), insideA.ObstructingStructureId);
        Assert.Equal(new StructureId(1), insideB.ObstructingStructureId);
        Assert.Equal(new StructureId(1), insideBruteA.ObstructingStructureId);
        Assert.Equal(new StructureId(1), insideBruteB.ObstructingStructureId);
    }

    [Fact]
    public void LineOfSightResultEqualsAndGetHashCodeContract()
    {
        LineOfSightResult a = LineOfSightResult.Clear(new Vector3(1f, 1f, 1f), 1.0f);
        LineOfSightResult b = LineOfSightResult.Clear(new Vector3(1f, 1f, 1f), 1.00005f);
        LineOfSightResult a2 = LineOfSightResult.Clear(new Vector3(1f, 1f, 1f), 1.0f);

        // Exact equality
        Assert.False(a == b);
        Assert.False(a.Equals(b));
        Assert.True(a == a2);
        Assert.True(a.Equals(a2));

        // Hash contract: equal objects must have identical hash codes
        Assert.Equal(a.GetHashCode(), a2.GetHashCode());

        // Set lookup consistency
        HashSet<LineOfSightResult> set = [a];
        Assert.Contains(a2, set);
        Assert.DoesNotContain(b, set);

        // Approximate comparison helper
        Assert.True(a.ApproximatelyEquals(b, 1e-4f));
        Assert.False(a.ApproximatelyEquals(b, 1e-6f));
    }

    [Fact]
    public void MultiCellStructuresTestedAtMostOncePerQuery()
    {
        HeightMap map = CreateFlatMap(2, 2, 1024f, 0f);
        List<OpaqueStructure> structures = new(50);
        for (int i = 1; i <= 50; i++)
        {
            structures.Add(new OpaqueStructure(
                new StructureId(i),
                new Vector3(0f, 0f, 0f),
                new Vector3(1024f, 5f, 1024f)));
        }

        StaticGeometry geo = new(map, structures, structureCellSize: 16f);

        // Ray passes at Y=10m across whole map above the 5m structures
        Vector3 start = new(1f, 10f, 1f);
        Vector3 end = new(1023f, 10f, 1023f);

        LineOfSightResult result = geo.CheckLineOfSight(start, end, out LineOfSightDiagnostics diag);

        Assert.True(result.IsClear);
        Assert.True(diag.VisitedStructureCells >= 60);
        // Mailboxing ensures each of the 50 structures is tested at most once, not 64 * 50 = 3200 times
        Assert.True(diag.TestedStructures <= 50);
    }

    // -------------------------------------------------------------------------
    // 6. Review 2 Regression Tests: Surface Contact, Roof Observation, Context, Long Rays, Grounding
    // -------------------------------------------------------------------------

    [Fact]
    public void SurfaceContactPointsAreReportedClear()
    {
        // Flat map at 2.0m and structure(1) from (10,0,10) to (16,6,16)
        HeightMap map = CreateFlatMap(16, 16, 2.0f, 2.0f);
        OpaqueStructure box = new(new StructureId(1), new Vector3(10f, 0f, 10f), new Vector3(16f, 6f, 16f));
        StaticGeometry geo = new(map, [box]);

        // (2,4,2) -> (8,2,2): Target exactly on the ground is clear
        LineOfSightResult r1 = geo.CheckLineOfSight(new Vector3(2f, 4f, 2f), new Vector3(8f, 2f, 2f));
        Assert.True(r1.IsClear);
        Assert.Equal(ObstructionKind.None, r1.ObstructionKind);

        // (2,2,2) -> (8,2,2): Ground to ground on flat terrain is clear
        LineOfSightResult r2 = geo.CheckLineOfSight(new Vector3(2f, 2f, 2f), new Vector3(8f, 2f, 2f));
        Assert.True(r2.IsClear);
        Assert.Equal(ObstructionKind.None, r2.ObstructionKind);

        // (4,10,4) -> (4,2,4): Vertical ray down to a ground point is clear
        LineOfSightResult r3 = geo.CheckLineOfSight(new Vector3(4f, 10f, 4f), new Vector3(4f, 2f, 4f));
        Assert.True(r3.IsClear);
        Assert.Equal(ObstructionKind.None, r3.ObstructionKind);

        // Counter-test: A ray that dives 0.5m into ground stays blocked
        LineOfSightResult r4 = geo.CheckLineOfSight(new Vector3(2f, 4f, 2f), new Vector3(8f, 1.5f, 2f));
        Assert.False(r4.IsClear);
        Assert.Equal(ObstructionKind.Terrain, r4.ObstructionKind);
    }

    [Fact]
    public void ObserverOnRoofOrWallBoundaryIsNotBlind()
    {
        HeightMap map = CreateFlatMap(16, 16, 2.0f, 2.0f);
        OpaqueStructure box = new(new StructureId(1), new Vector3(10f, 0f, 10f), new Vector3(16f, 6f, 16f));
        StaticGeometry geo = new(map, [box]);

        // Observer flush against wall at X=16 looking away to (28,3,13) is clear
        LineOfSightResult rWall = geo.CheckLineOfSight(new Vector3(16f, 3f, 13f), new Vector3(28f, 3f, 13f));
        Assert.True(rWall.IsClear);
        Assert.Equal(ObstructionKind.None, rWall.ObstructionKind);

        // Observer on roof (13,6,13) looking level to (28,6,28) is clear
        LineOfSightResult rRoofLevel = geo.CheckLineOfSight(new Vector3(13f, 6f, 13f), new Vector3(28f, 6f, 28f));
        Assert.True(rRoofLevel.IsClear);
        Assert.Equal(ObstructionKind.None, rRoofLevel.ObstructionKind);

        // Observer on roof (13,6,13) looking up to (28,8,28) is clear
        LineOfSightResult rRoofUp = geo.CheckLineOfSight(new Vector3(13f, 6f, 13f), new Vector3(28f, 8f, 28f));
        Assert.True(rRoofUp.IsClear);
        Assert.Equal(ObstructionKind.None, rRoofUp.ObstructionKind);

        // Observer at roof edge (16,6,13) looking down to (28,3,28) is clear
        LineOfSightResult rRoofEdge = geo.CheckLineOfSight(new Vector3(16f, 6f, 13f), new Vector3(28f, 3f, 28f));
        Assert.True(rRoofEdge.IsClear);
        Assert.Equal(ObstructionKind.None, rRoofEdge.ObstructionKind);

        // Observer standing on roof (eye height Y=7.5m) looking down to (28,3,28) is clear
        LineOfSightResult rRoofStanding = geo.CheckLineOfSight(new Vector3(13f, 7.5f, 13f), new Vector3(28f, 3f, 28f));
        Assert.True(rRoofStanding.IsClear);
        Assert.Equal(ObstructionKind.None, rRoofStanding.ObstructionKind);

        // Counter-test 1: Observer on the roof looking down through the roof is blocked by the box
        LineOfSightResult rThroughRoof = geo.CheckLineOfSight(new Vector3(13f, 6f, 13f), new Vector3(13f, 2f, 13f));
        Assert.False(rThroughRoof.IsClear);
        Assert.Equal(ObstructionKind.Structure, rThroughRoof.ObstructionKind);
        Assert.Equal(new StructureId(1), rThroughRoof.ObstructingStructureId);
        Assert.Equal(0.0f, rThroughRoof.Distance, 1e-4f);

        // Counter-test 2: Observer strictly inside the box interior is blocked
        LineOfSightResult rInside = geo.CheckLineOfSight(new Vector3(13f, 3f, 13f), new Vector3(28f, 3f, 28f));
        Assert.False(rInside.IsClear);
        Assert.Equal(ObstructionKind.Structure, rInside.ObstructionKind);
        Assert.Equal(new StructureId(1), rInside.ObstructingStructureId);
        Assert.Equal(0.0f, rInside.Distance, 1e-4f);
    }

    [Fact]
    public void CallerProvidedSmallContextExpandsForManyStructures()
    {
        HeightMap map = CreateFlatMap(16, 16, 2.0f, 2.0f);
        List<OpaqueStructure> structures = new(100);
        for (int i = 1; i <= 100; i++)
        {
            structures.Add(new OpaqueStructure(
                new StructureId(i),
                new Vector3(10f + i * 0.1f, 0f, 10f),
                new Vector3(10.05f + i * 0.1f, 5f, 12f)));
        }

        StaticGeometry geo = new(map, structures);

        // Caller supplies small context with capacity 16
        LineOfSightContext smallCtx = new(16);

        Vector3 start = new(5f, 2f, 11f);
        Vector3 end = new(25f, 2f, 11f);

        LineOfSightResult queryResult = geo.CheckLineOfSight(start, end, smallCtx);
        LineOfSightResult oracleResult = geo.CheckLineOfSightBruteForce(start, end);

        Assert.Equal(oracleResult, queryResult);
    }

    private static LineOfSightResult SamplingOracle(
        StaticGeometry geo,
        Vector3 start,
        Vector3 end,
        float step = 0.5f)
    {
        Vector3 delta = end - start;
        float totalDistance = delta.Length();
        if (totalDistance < 1e-7f)
            return geo.CheckLineOfSight(start, end);

        int steps = Math.Max(1, (int)MathF.Ceiling(totalDistance / Math.Max(step, 0.01f)));
        float invSteps = 1.0f / steps;

        for (int i = 0; i <= steps; i++)
        {
            float t = i * invSteps;
            Vector3 p = start + delta * t;
            if (geo.HeightMap.Contains(new Vector2(p.X, p.Z)))
            {
                float h = geo.HeightMap.GetHeight(p.X, p.Z);
                if (p.Y < h - StaticGeometry.GroundTolerance)
                {
                    float remainingDist = (1.0f - t) * totalDistance;
                    if (remainingDist <= StaticGeometry.EndContactTolerance &&
                        end.Y >= geo.HeightMap.GetHeight(end.X, end.Z) - StaticGeometry.GroundTolerance)
                    {
                        continue;
                    }

                    return LineOfSightResult.BlockedByTerrain(p, t * totalDistance, t);
                }
            }
        }

        return LineOfSightResult.Clear(end, totalDistance);
    }

    [Fact]
    public void LongRayDoublePrecisionDdaMatchesSamplingOracleOnDiagonalHillyTerrain()
    {
        // 2001 x 2001 map at 5m = 10 km x 10 km with rolling hills
        int cols = 2001;
        int rows = 2001;
        float cell = 5.0f;
        ushort[] samples = new ushort[cols * rows];

        for (int r = 0; r < rows; r++)
        {
            float nz = r * 0.01f;
            for (int c = 0; c < cols; c++)
            {
                float nx = c * 0.01f;
                float h = (MathF.Sin(nx) * MathF.Cos(nz) * 0.5f + 0.5f) * 80f + 10f; // 10m to 90m hills
                samples[r * cols + c] = (ushort)(h * 100f);
            }
        }

        HeightMap map = new(cols, rows, cell, 0.01f, samples);
        StaticGeometry geo = new(map, []);

        // Ray 1: Long diagonal ray over hills (150m height) spanning ~11.3 km -> clear
        Vector3 startHigh = new(1000f, 150f, 1000f);
        Vector3 endHigh = new(9000f, 150f, 9000f);
        LineOfSightResult resHigh = geo.CheckLineOfSight(startHigh, endHigh);
        LineOfSightResult oracleHigh = SamplingOracle(geo, startHigh, endHigh, step: 2.0f);

        Assert.True(resHigh.IsClear);
        Assert.True(oracleHigh.IsClear);

        // Ray 2: Long diagonal ray cutting through hills (20m height) -> blocked by terrain
        Vector3 startLow = new(1000f, 20f, 1000f);
        Vector3 endLow = new(9000f, 20f, 9000f);
        LineOfSightResult resLow = geo.CheckLineOfSight(startLow, endLow);
        LineOfSightResult oracleLow = SamplingOracle(geo, startLow, endLow, step: 2.0f);

        Assert.False(resLow.IsClear);
        Assert.False(oracleLow.IsClear);
        Assert.Equal(ObstructionKind.Terrain, resLow.ObstructionKind);
        Assert.Equal(oracleLow.ObstructionKind, resLow.ObstructionKind);
        Assert.True(Vector3.Distance(resLow.HitPoint, oracleLow.HitPoint) < 5.0f);

        // Ray 3: Arbitrary diagonal ray across non-equal axis proportions
        Vector3 startDiag = new(1500f, 120f, 1200f);
        Vector3 endDiag = new(8500f, 120f, 6200f);
        LineOfSightResult resDiag = geo.CheckLineOfSight(startDiag, endDiag);
        LineOfSightResult oracleDiag = SamplingOracle(geo, startDiag, endDiag, step: 2.0f);
        Assert.Equal(oracleDiag.IsClear, resDiag.IsClear);
    }

    [Fact]
    public void GroundTargetsSampledViaGetHeightAreReportedClearFromAbove()
    {
        // 1. Prototype map test: 2000 random ground targets sampled via GetHeight with observer 300m above
        HeightMap protoMap = HeightMap.CreatePrototype();
        StaticGeometry protoGeo = new(protoMap, []);
        Random rng = new(12345);

        for (int i = 0; i < 2000; i++)
        {
            float tx = (float)(rng.NextDouble() * (protoMap.Width - 4f) + 2f);
            float tz = (float)(rng.NextDouble() * (protoMap.Depth - 4f) + 2f);
            float ty = protoMap.GetHeight(tx, tz);
            Vector3 target = new(tx, ty, tz);

            Vector3 observer = new(
                tx + (float)(rng.NextDouble() * 20 - 10),
                ty + 300f,
                tz + (float)(rng.NextDouble() * 20 - 10));

            LineOfSightResult res = protoGeo.CheckLineOfSight(observer, target);
            Assert.True(res.IsClear, $"Prototype ground target at ({tx:F2}, {ty:F2}, {tz:F2}) should be clear, got {res.ObstructionKind} at fraction {res.RayFraction:F5}");
        }

        // 2. High-relief 4000m mountain map test: 2000 random ground targets
        int cols = 65, rows = 65;
        ushort[] highSamples = new ushort[cols * rows];
        for (int z = 0; z < rows; z++)
        {
            for (int x = 0; x < cols; x++)
            {
                float nx = x / (float)cols;
                float nz = z / (float)rows;
                float h = (MathF.Sin(nx * 10f) * MathF.Cos(nz * 10f) * 0.5f + 0.5f) * 4000f;
                highSamples[z * cols + x] = (ushort)Math.Clamp((int)(h * 10f), 0, 65535); // scale 0.1
            }
        }
        HeightMap mountainMap = new(cols, rows, 30f, 0.1f, highSamples);
        StaticGeometry mountainGeo = new(mountainMap, []);

        for (int i = 0; i < 2000; i++)
        {
            float tx = (float)(rng.NextDouble() * (mountainMap.Width - 10f) + 5f);
            float tz = (float)(rng.NextDouble() * (mountainMap.Depth - 10f) + 5f);
            float ty = mountainMap.GetHeight(tx, tz);
            Vector3 target = new(tx, ty, tz);

            Vector3 observer = new(
                tx + (float)(rng.NextDouble() * 40 - 20),
                ty + 300f,
                tz + (float)(rng.NextDouble() * 40 - 20));

            LineOfSightResult res = mountainGeo.CheckLineOfSight(observer, target);
            Assert.True(res.IsClear, $"Mountain ground target at ({tx:F2}, {ty:F2}, {tz:F2}) should be clear, got {res.ObstructionKind} at fraction {res.RayFraction:F5}");
        }

        // 3. Counter-test: A target placed 0.5m below terrain surface is blocked
        Vector3 undergroundTarget = new(50f, protoMap.GetHeight(50f, 50f) - 0.5f, 50f);
        Vector3 aboveObserver = new(50f, protoMap.GetHeight(50f, 50f) + 100f, 50f);
        LineOfSightResult undergroundRes = protoGeo.CheckLineOfSight(aboveObserver, undergroundTarget);
        Assert.False(undergroundRes.IsClear);
        Assert.Equal(ObstructionKind.Terrain, undergroundRes.ObstructionKind);
    }

    [Fact]
    public void GroundedCheckRejectsStructureWhenTerrainDipsAlongFootprintEdgeIntersection()
    {
        // 4x4 grid with cellSize = 2.0m.
        // Row 0 (Z=0.0): all samples 2.0m
        // Row 1 (Z=2.0): sample at column 1 (X=2.0) dips to 1.0m, others are 2.0m
        // Row 2 (Z=4.0): all samples 2.0m
        int cols = 4, rows = 4;
        ushort[] samples = new ushort[cols * rows];
        Array.Fill(samples, (ushort)200); // 2.0m
        samples[1 * cols + 1] = 100; // (X=2.0, Z=2.0) dips to 1.0m
        HeightMap map = new(cols, rows, 2.0f, 0.01f, samples);

        // Footprint spans X in [1.0, 3.0], Z in [0.5, 1.5].
        // The 4 corners:
        // (1.0, 0.5), (3.0, 0.5), (1.0, 1.5), (3.0, 1.5) all have terrain height 2.0m!
        // No grid vertices fall strictly inside Z in [0.5, 1.5] and X in [1.0, 3.0] because Z=2.0 is outside.
        // BUT the grid line X=2.0 intersects the edge Z=1.5 at (2.0, 1.5).
        // At (2.0, 1.5), bilinear height is 2.0 + (1.0 - 2.0) * (1.5 / 2.0) = 1.25m!
        // So terrain under the footprint edge has a 0.75m gap (min terrain is 1.25m, not 2.0m).
        OpaqueStructure edgeFloating = new(
            new StructureId(10),
            new Vector3(1.0f, 2.0f, 0.5f),
            new Vector3(3.0f, 5.0f, 1.5f),
            isGrounded: true);

        // Grounded box with MinY = 2.0m must be rejected because edge minimum is 1.25m!
        Assert.Throws<ArgumentException>(() => new StaticGeometry(map, [edgeFloating]));

        // Box adjusted to edge minimum (MinY = 1.25m) is accepted!
        OpaqueStructure edgeGrounded = new(
            new StructureId(11),
            new Vector3(1.0f, 1.25f, 0.5f),
            new Vector3(3.0f, 5.0f, 1.5f),
            isGrounded: true);

        StaticGeometry geo = new(map, [edgeGrounded]);
        Assert.Single(geo.Structures);
    }

    [Fact]
    public void GroundedStructureRejectsFloatingBoxAndAcceptsDeck()
    {
        // Flat map at 2.0m height
        HeightMap flatMap = CreateFlatMap(16, 16, 2.0f, 2.0f);

        // A grounded box placed 1 m above terrain (MinY = 3.0m) floats -> must throw!
        OpaqueStructure floatingGrounded = new(
            new StructureId(1),
            new Vector3(2f, 3.0f, 2f),
            new Vector3(6f, 7.0f, 6f),
            isGrounded: true);

        Assert.Throws<ArgumentException>(() => new StaticGeometry(flatMap, [floatingGrounded]));

        // The same box with IsGrounded = false is accepted
        OpaqueStructure bridgeDeck = new(
            new StructureId(1),
            new Vector3(2f, 3.0f, 2f),
            new Vector3(6f, 7.0f, 6f),
            isGrounded: false);

        StaticGeometry geo = new(flatMap, [bridgeDeck]);
        Assert.Single(geo.Structures);

        // A ray passing under the deck at Y=2.5m clears (terrain is at 2.0m, deck starts at 3.0m)
        LineOfSightResult underDeck = geo.CheckLineOfSight(new Vector3(1f, 2.5f, 4f), new Vector3(7f, 2.5f, 4f));
        Assert.True(underDeck.IsClear);
        Assert.Equal(ObstructionKind.None, underDeck.ObstructionKind);

        // A grounded box whose bottom touches or penetrates terrain is accepted
        OpaqueStructure touchingGrounded = new(
            new StructureId(2),
            new Vector3(2f, 2.0f, 2f),
            new Vector3(6f, 7.0f, 6f),
            isGrounded: true);

        OpaqueStructure penetratingGrounded = new(
            new StructureId(3),
            new Vector3(2f, 1.0f, 2f),
            new Vector3(6f, 7.0f, 6f),
            isGrounded: true);

        StaticGeometry geoTouching = new(flatMap, [touchingGrounded]);
        StaticGeometry geoPenetrating = new(flatMap, [penetratingGrounded]);
        Assert.Single(geoTouching.Structures);
        Assert.Single(geoPenetrating.Structures);
    }

    // -------------------------------------------------------------------------
    // 9. Equivalence Tests (IsLineOfSightClear vs CheckLineOfSight.IsClear)
    // -------------------------------------------------------------------------

    [Fact]
    public void Equivalence_Wall_OccludedAndClearRaysMatch()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure wall = new(
            new StructureId(1),
            new Vector3(10f, 0f, 0f),
            new Vector3(12f, 3.0f, 20f),
            isGrounded: true);
        StaticGeometry geo = new(map, [wall]);

        // 1. Ray through wall (blocked)
        Vector3 startBlocked = new(5f, 1.5f, 10f);
        Vector3 endBlocked = new(15f, 1.5f, 10f);
        bool fastBlocked = geo.IsLineOfSightClear(startBlocked, endBlocked);
        bool detailedBlocked = geo.CheckLineOfSight(startBlocked, endBlocked).IsClear;
        Assert.False(fastBlocked);
        Assert.Equal(detailedBlocked, fastBlocked);

        // 2. Ray over wall (clear)
        Vector3 startClear = new(5f, 4.0f, 10f);
        Vector3 endClear = new(15f, 4.0f, 10f);
        bool fastClear = geo.IsLineOfSightClear(startClear, endClear);
        bool detailedClear = geo.CheckLineOfSight(startClear, endClear).IsClear;
        Assert.True(fastClear);
        Assert.Equal(detailedClear, fastClear);
    }

    [Fact]
    public void Equivalence_Hill_OccludedAndClearRaysMatch()
    {
        // 9m hill centered at (86, 44)
        HeightMap map = HeightMap.CreatePrototype();
        StaticGeometry geo = new(map, []);

        // 1. Ray through hill (blocked)
        Vector3 startBlocked = new(50f, 1.5f, 44f);
        Vector3 endBlocked = new(122f, 1.5f, 44f);
        bool fastBlocked = geo.IsLineOfSightClear(startBlocked, endBlocked);
        bool detailedBlocked = geo.CheckLineOfSight(startBlocked, endBlocked).IsClear;
        Assert.False(fastBlocked);
        Assert.Equal(detailedBlocked, fastBlocked);

        // 2. Ray elevated over hill crest (clear)
        Vector3 startClear = new(70f, 15f, 44f);
        Vector3 endClear = new(102f, 15f, 44f);
        bool fastClear = geo.IsLineOfSightClear(startClear, endClear);
        bool detailedClear = geo.CheckLineOfSight(startClear, endClear).IsClear;
        Assert.True(fastClear);
        Assert.Equal(detailedClear, fastClear);
    }

    [Fact]
    public void Equivalence_LowCover_StandingVisibleAndProneBlockedMatch()
    {
        HeightMap map = CreateFlatMap();
        // 1.0m low cover wall
        OpaqueStructure lowWall = new(
            new StructureId(1),
            new Vector3(10f, 0f, 0f),
            new Vector3(12f, 1.0f, 20f),
            isGrounded: true);
        StaticGeometry geo = new(map, [lowWall]);

        Vector3 observerEye = new(5f, 1.6f, 10f);

        // Standing target point at 1.8m clears over low cover
        Vector3 standingTarget = new(15f, 1.8f, 10f);
        bool fastStanding = geo.IsLineOfSightClear(observerEye, standingTarget);
        bool detailedStanding = geo.CheckLineOfSight(observerEye, standingTarget).IsClear;
        Assert.True(fastStanding);
        Assert.Equal(detailedStanding, fastStanding);

        // Prone target point at 0.4m is occluded behind 1.0m low cover
        Vector3 proneTarget = new(15f, 0.4f, 10f);
        bool fastProne = geo.IsLineOfSightClear(observerEye, proneTarget);
        bool detailedProne = geo.CheckLineOfSight(observerEye, proneTarget).IsClear;
        Assert.False(fastProne);
        Assert.Equal(detailedProne, fastProne);
    }

    [Fact]
    public void Equivalence_BoundaryContactsAndTolerancesMatch()
    {
        HeightMap map = CreateFlatMap(16, 16, 2.0f, 0.0f);
        OpaqueStructure wall = new(
            new StructureId(1),
            new Vector3(10f, 0f, 5f),
            new Vector3(12f, 3f, 15f),
            isGrounded: true);
        StaticGeometry geo = new(map, [wall]);

        // 1. Grazing ray alongside wall boundary without penetration
        Vector3 grazeStart = new(5f, 1.5f, 4.999f);
        Vector3 grazeEnd = new(15f, 1.5f, 4.999f);
        bool fastGraze = geo.IsLineOfSightClear(grazeStart, grazeEnd);
        bool detailedGraze = geo.CheckLineOfSight(grazeStart, grazeEnd).IsClear;
        Assert.True(fastGraze);
        Assert.Equal(detailedGraze, fastGraze);

        // 2. Surface target contact tolerance: end touches ground exactly at valid surface
        Vector3 eye = new(2f, 1.5f, 2f);
        Vector3 groundTarget = new(8f, 0.0f, 2f);
        bool fastGround = geo.IsLineOfSightClear(eye, groundTarget);
        bool detailedGround = geo.CheckLineOfSight(eye, groundTarget).IsClear;
        Assert.True(fastGround);
        Assert.Equal(detailedGround, fastGround);

        // 3. Underground start point below tolerance is blocked
        Vector3 undergroundStart = new(2f, -0.2f, 2f);
        Vector3 normalEnd = new(8f, 1.5f, 2f);
        bool fastUnderground = geo.IsLineOfSightClear(undergroundStart, normalEnd);
        bool detailedUnderground = geo.CheckLineOfSight(undergroundStart, normalEnd).IsClear;
        Assert.False(fastUnderground);
        Assert.Equal(detailedUnderground, fastUnderground);
    }

    [Fact]
    public void Equivalence_CornerPeek_TransitionMatches()
    {
        HeightMap map = CreateFlatMap();
        // Building corner at (10, 10) to (16, 16), height 4m
        OpaqueStructure building = new(
            new StructureId(1),
            new Vector3(10f, 0f, 10f),
            new Vector3(16f, 4.0f, 16f),
            isGrounded: true);
        StaticGeometry geo = new(map, [building]);

        Vector3 target = new(13f, 1.5f, 20f);

        // Observer moves along X at Z=5 stepping past the building edge at X=10
        // X = 8, 9 is behind the building (blocked)
        // X = 17, 18 has stepped around the corner (clear)
        float[] testXPositions = [6f, 8f, 9.5f, 10.5f, 12f, 16.5f, 18f, 20f];
        foreach (float obsX in testXPositions)
        {
            Vector3 observer = new(obsX, 1.5f, 5f);
            bool fastResult = geo.IsLineOfSightClear(observer, target);
            bool detailedResult = geo.CheckLineOfSight(observer, target).IsClear;
            Assert.Equal(detailedResult, fastResult);
        }
    }

    [Fact]
    public void Equivalence_TwoObservers_OneSeesTargetMatch()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure centralObstacle = new(
            new StructureId(1),
            new Vector3(8f, 0f, 8f),
            new Vector3(12f, 3.0f, 12f),
            isGrounded: true);
        StaticGeometry geo = new(map, [centralObstacle]);

        Vector3 target = new(16f, 1.5f, 10f);

        // Observer 1 is directly behind obstacle at Z=10
        Vector3 observer1Behind = new(4f, 1.5f, 10f);
        bool fastObs1 = geo.IsLineOfSightClear(observer1Behind, target);
        bool detailedObs1 = geo.CheckLineOfSight(observer1Behind, target).IsClear;
        Assert.False(fastObs1);
        Assert.Equal(detailedObs1, fastObs1);

        // Observer 2 has clear flanking sightline at Z=2
        Vector3 observer2Flank = new(4f, 1.5f, 2f);
        bool fastObs2 = geo.IsLineOfSightClear(observer2Flank, target);
        bool detailedObs2 = geo.CheckLineOfSight(observer2Flank, target).IsClear;
        Assert.True(fastObs2);
        Assert.Equal(detailedObs2, fastObs2);
    }

    [Fact]
    public void Equivalence_SparsePeak_DiagonalRayBlockedInBothDirectionsAndAllSigns()
    {
        const int size = 65;
        ushort[] spike = new ushort[size * size];
        spike[5 * size + 10] = 500; // 5m peak at x=20, z=10
        HeightMap spikeMap = new(size, size, 2.0f, 0.01f, spike);
        StaticGeometry spikeGeo = new(spikeMap, []);

        Vector3 a = new(1f, 1f, 1f);
        Vector3 b = new(101f, 1f, 51f);

        // Forward ray through peak
        bool fastForward = spikeGeo.IsLineOfSightClear(a, b);
        bool detailedForward = spikeGeo.CheckLineOfSight(a, b).IsClear;
        Assert.False(fastForward);
        Assert.Equal(detailedForward, fastForward);

        // Reverse ray through peak
        bool fastReverse = spikeGeo.IsLineOfSightClear(b, a);
        bool detailedReverse = spikeGeo.CheckLineOfSight(b, a).IsClear;
        Assert.False(fastReverse);
        Assert.Equal(detailedReverse, fastReverse);

        // All 4 direction quadrants crossing through the peak at (20, 10)
        // 1. +dx, +dz
        Vector3 q1 = new(10f, 1f, 5f);
        Vector3 q2 = new(30f, 1f, 15f);
        Assert.False(spikeGeo.IsLineOfSightClear(q1, q2));
        Assert.Equal(spikeGeo.CheckLineOfSight(q1, q2).IsClear, spikeGeo.IsLineOfSightClear(q1, q2));

        // 2. -dx, -dz
        Assert.False(spikeGeo.IsLineOfSightClear(q2, q1));
        Assert.Equal(spikeGeo.CheckLineOfSight(q2, q1).IsClear, spikeGeo.IsLineOfSightClear(q2, q1));

        // 3. +dx, -dz
        Vector3 q3 = new(10f, 1f, 15f);
        Vector3 q4 = new(30f, 1f, 5f);
        Assert.False(spikeGeo.IsLineOfSightClear(q3, q4));
        Assert.Equal(spikeGeo.CheckLineOfSight(q3, q4).IsClear, spikeGeo.IsLineOfSightClear(q3, q4));

        // 4. -dx, +dz
        Assert.False(spikeGeo.IsLineOfSightClear(q4, q3));
        Assert.Equal(spikeGeo.CheckLineOfSight(q4, q3).IsClear, spikeGeo.IsLineOfSightClear(q4, q3));
    }

    [Fact]
    public void Equivalence_RectangularMap_WithPartialBlocksAndCornerCrossings()
    {
        const int cols = 33;
        const int rows = 49;
        ushort[] samples = new ushort[cols * rows];
        samples[24 * cols + 16] = 800; // 8m peak at world (32, 48)
        HeightMap map = new(cols, rows, 2.0f, 0.01f, samples);
        StaticGeometry geo = new(map, []);

        Vector3 start = new(2f, 1.5f, 3f);
        Vector3 end = new(62f, 1.5f, 93f);

        bool fast = geo.IsLineOfSightClear(start, end);
        bool detailed = geo.CheckLineOfSight(start, end).IsClear;
        Assert.False(fast);
        Assert.Equal(detailed, fast);

        // Reverse direction
        Assert.False(geo.IsLineOfSightClear(end, start));
        Assert.Equal(geo.CheckLineOfSight(end, start).IsClear, geo.IsLineOfSightClear(end, start));

        // Diagonal corner-to-corner crossing above max terrain height
        Vector3 cornerStart = new(0.1f, 15.0f, 0.1f);
        Vector3 cornerEnd = new(map.Width - 0.1f, 15.0f, map.Depth - 0.1f);
        Assert.True(geo.IsLineOfSightClear(cornerStart, cornerEnd));
        Assert.Equal(geo.CheckLineOfSight(cornerStart, cornerEnd).IsClear, geo.IsLineOfSightClear(cornerStart, cornerEnd));
    }

    [Fact]
    public void Equivalence_RandomSparsePeaks_36000RaysDifferentialProbeHasZeroMismatches()
    {
        const int size = 65;
        Random random = new(821734);
        int mismatch = 0;

        for (int mapIndex = 0; mapIndex < 12; mapIndex++)
        {
            ushort[] samples = new ushort[size * size];
            for (int k = 0; k < 45; k++)
            {
                int cx = random.Next(3, 62);
                int cz = random.Next(3, 62);
                samples[cz * size + cx] = (ushort)random.Next(80, 1200);
            }
            HeightMap map = new(size, size, 2.0f, 0.01f, samples);
            StaticGeometry geo = new(map, []);

            for (int ray = 0; ray < 3000; ray++)
            {
                float ax = 1f + (float)random.NextDouble() * 126f;
                float az = 1f + (float)random.NextDouble() * 126f;
                float bx = 1f + (float)random.NextDouble() * 126f;
                float bz = 1f + (float)random.NextDouble() * 126f;
                Vector3 start = new(ax, map.GetHeight(ax, az) + 1.6f, az);
                Vector3 end = new(bx, map.GetHeight(bx, bz) + 1.8f, bz);

                bool fast = geo.IsLineOfSightClear(start, end);
                bool detailed = geo.CheckLineOfSight(start, end).IsClear;
                if (fast != detailed)
                {
                    mismatch++;
                }
            }
        }

        Assert.Equal(0, mismatch);
    }

    [Fact]
    public void Equivalence_UndergroundOriginsAndVerticalRays_BlockedByTerrain()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);

        var cases = new (Vector3 Start, Vector3 End, bool ExpectedClear, bool ExpectedBlockedByTerrain)[]
        {
            // Upward vertical ray starting underground
            (new Vector3(4f, -1f, 4f), new Vector3(4f, 1f, 4f), false, true),
            // Downward vertical ray entirely underground
            (new Vector3(4f, -1f, 4f), new Vector3(4f, -2f, 4f), false, true),
            // Short oblique ray starting slightly underground ending at surface
            (new Vector3(4f, -0.02f, 4f), new Vector3(4.01f, 0f, 4f), false, true),
            // Longer oblique ray starting slightly underground ending at surface
            (new Vector3(4f, -0.02f, 4f), new Vector3(4.1f, 0f, 4f), false, true),
            // Downward vertical ray starting above ground and entering ground
            (new Vector3(4f, 1f, 4f), new Vector3(4f, -1f, 4f), false, true),
            // Downward vertical ray starting above ground and touching surface
            (new Vector3(4f, 1f, 4f), new Vector3(4f, 0f, 4f), true, false),
            // Both endpoints underground oblique
            (new Vector3(5f, -0.5f, 5f), new Vector3(8f, -0.5f, 8f), false, true),
            // Upward oblique ray starting underground and ending above ground
            (new Vector3(5f, -0.5f, 5f), new Vector3(8f, 2f, 8f), false, true),
        };

        foreach (var (start, end, expectedClear, expectedBlockedByTerrain) in cases)
        {
            bool fast = geo.IsLineOfSightClear(start, end, null, out bool blockedByTerrain);
            LineOfSightResult detailed = geo.CheckLineOfSight(start, end);

            Assert.Equal(expectedClear, fast);
            Assert.Equal(expectedClear, detailed.IsClear);
            Assert.Equal(expectedBlockedByTerrain, blockedByTerrain);
            if (expectedBlockedByTerrain)
            {
                Assert.Equal(ObstructionKind.Terrain, detailed.ObstructionKind);
            }
        }
    }
}
