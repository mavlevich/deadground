using System.Numerics;
using DeadGround.Engine;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class VegetationVisibilityTests
{
    private static HeightMap CreateFlatMap(int columns = 64, int rows = 64, float cellSize = 4.0f, float height = 0.0f)
    {
        ushort[] samples = new ushort[columns * rows];
        ushort sampleValue = (ushort)MathF.Round(height * 100f);
        Array.Fill(samples, sampleValue);
        return new HeightMap(columns, rows, cellSize, 0.01f, samples);
    }

    [Fact]
    public void OpenTerrain_HasIdenticalParity()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geoWithoutVegetation = new(map, []);
        StaticGeometry geoWithVegetation = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            []);

        Vector3 start = new(20f, 1.5f, 20f);
        Vector3 end = new(80f, 1.5f, 80f);

        LineOfSightResult r1 = geoWithoutVegetation.CheckLineOfSight(start, end);
        LineOfSightResult r2 = geoWithVegetation.CheckLineOfSight(start, end);

        Assert.True(r1.IsClear);
        Assert.True(r2.IsClear);
        Assert.Equal(r1.Distance, r2.Distance, 1e-4f);
        Assert.Equal(r1.ObstructionKind, r2.ObstructionKind);
    }

    [Fact]
    public void DenseVsSparse_DistinguishesPenetrationThresholds()
    {
        HeightMap map = CreateFlatMap();

        // Forest rectangle from X: 40 to 120, Z: 0 to 100
        Vector2[] forestPolygon =
        [
            new Vector2(40f, 0f),
            new Vector2(120f, 0f),
            new Vector2(120f, 100f),
            new Vector2(40f, 100f)
        ];

        // Dense forest: D_max = 25m, mu = 0.04 m^-1
        VegetationZone denseZone = VegetationZone.CreateDenseForest("DENSE", forestPolygon, canopyHeight: 12.0f, maxPenetration: 25.0f);
        StaticGeometry denseGeo = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [denseZone]);

        // Sparse forest: D_max = 75m, mu = 1/75 m^-1
        VegetationZone sparseZone = VegetationZone.CreateSparseForest("SPARSE", forestPolygon, canopyHeight: 12.0f, maxPenetration: 75.0f);
        StaticGeometry sparseGeo = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [sparseZone]);

        // Observer outside forest at X=30, looking into forest at X=70 (traversing 30m of forest).
        // For dense forest: 30m * 0.04 = 1.2 optical depth > 1.0 (blocked).
        // For sparse forest: 30m * (1/75) = 0.4 optical depth <= 1.0 (clear).
        Vector3 start = new(30f, 1.5f, 50f);
        Vector3 target = new(70f, 1.5f, 50f);

        LineOfSightResult denseResult = denseGeo.CheckLineOfSight(start, target);
        Assert.False(denseResult.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, denseResult.ObstructionKind);
        // Blocked after ~25m inside vegetation (X around 40 + 25 = 65)
        Assert.True(denseResult.HitPoint.X >= 64.0f && denseResult.HitPoint.X <= 66.5f, $"HitPoint.X was {denseResult.HitPoint.X}");

        LineOfSightResult sparseResult = sparseGeo.CheckLineOfSight(start, target);
        Assert.True(sparseResult.IsClear);
        Assert.Equal(ObstructionKind.None, sparseResult.ObstructionKind);
    }

    [Fact]
    public void ObservationGeometry_OutsideIn_InsideOut_InsideInside()
    {
        HeightMap map = CreateFlatMap();

        // Dense forest block: X in [50, 150], Z in [50, 150]. Canopy height 12m. D_max = 25m.
        Vector2[] forestPolygon =
        [
            new Vector2(50f, 50f),
            new Vector2(150f, 50f),
            new Vector2(150f, 150f),
            new Vector2(50f, 150f)
        ];

        VegetationZone denseZone = VegetationZone.CreateDenseForest("DENSE", forestPolygon, canopyHeight: 12.0f, maxPenetration: 25.0f);
        StaticGeometry geo = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [denseZone]);

        Vector3 outsidePt = new(30f, 1.5f, 100f);
        Vector3 shallowInsidePt = new(60f, 1.5f, 100f); // 10m into forest
        Vector3 deepInsidePt = new(90f, 1.5f, 100f);    // 40m into forest

        // 1. Outside to Shallow Inside (10m penetration <= 25m): Clear
        LineOfSightResult outToShallow = geo.CheckLineOfSight(outsidePt, shallowInsidePt);
        Assert.True(outToShallow.IsClear);

        // 2. Outside to Deep Inside (40m penetration > 25m): Blocked
        LineOfSightResult outToDeep = geo.CheckLineOfSight(outsidePt, deepInsidePt);
        Assert.False(outToDeep.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, outToDeep.ObstructionKind);

        // 3. Shallow Inside to Outside (10m penetration <= 25m): Clear (symmetry)
        LineOfSightResult shallowToOut = geo.CheckLineOfSight(shallowInsidePt, outsidePt);
        Assert.True(shallowToOut.IsClear);

        // 4. Deep Inside to Outside (40m penetration > 25m): Blocked
        LineOfSightResult deepToOut = geo.CheckLineOfSight(deepInsidePt, outsidePt);
        Assert.False(deepToOut.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, deepToOut.ObstructionKind);

        // 5. Two units inside forest, 15m apart (15m <= 25m): Clear
        Vector3 insideA = new(70f, 1.5f, 100f);
        Vector3 insideB = new(85f, 1.5f, 100f);
        LineOfSightResult insideClose = geo.CheckLineOfSight(insideA, insideB);
        Assert.True(insideClose.IsClear);

        // 6. Two units inside forest, 35m apart (35m > 25m): Blocked
        Vector3 insideFar = new(105f, 1.5f, 100f);
        LineOfSightResult insideFarResult = geo.CheckLineOfSight(insideA, insideFar);
        Assert.False(insideFarResult.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, insideFarResult.ObstructionKind);
    }

    [Fact]
    public void MultiBelt_AccumulatesOpticalDepth()
    {
        HeightMap map = CreateFlatMap();

        // Belt 1: X in [50, 65] (15m width)
        Vector2[] belt1 =
        [
            new Vector2(50f, 20f),
            new Vector2(65f, 20f),
            new Vector2(65f, 120f),
            new Vector2(50f, 120f)
        ];

        // Open corridor: X in [65, 95] (30m width)

        // Belt 2: X in [95, 110] (15m width)
        Vector2[] belt2 =
        [
            new Vector2(95f, 20f),
            new Vector2(110f, 20f),
            new Vector2(110f, 120f),
            new Vector2(95f, 120f)
        ];

        // Each belt is dense forest with D_max = 25m (mu = 0.04 m^-1).
        // 15m in belt 1 = 0.6 optical depth.
        // 15m in belt 2 = 0.6 optical depth.
        // Sum = 1.2 optical depth > 1.0 (blocked).
        VegetationZone b1 = VegetationZone.CreateDenseForest("BELT1", belt1, canopyHeight: 12.0f, maxPenetration: 25.0f);
        VegetationZone b2 = VegetationZone.CreateDenseForest("BELT2", belt2, canopyHeight: 12.0f, maxPenetration: 25.0f);

        StaticGeometry geo = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [b1, b2]);

        Vector3 observer = new(30f, 1.5f, 70f);
        Vector3 targetInCorridor = new(80f, 1.5f, 70f);
        Vector3 targetBehindBoth = new(130f, 1.5f, 70f);

        // Through single belt: tau = 0.6 <= 1.0 -> Clear
        LineOfSightResult singleBeltResult = geo.CheckLineOfSight(observer, targetInCorridor);
        Assert.True(singleBeltResult.IsClear);

        // Through both belts: tau = 1.2 > 1.0 -> Blocked by vegetation
        LineOfSightResult dualBeltResult = geo.CheckLineOfSight(observer, targetBehindBoth);
        Assert.False(dualBeltResult.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, dualBeltResult.ObstructionKind);
        // Hit point should be inside the second belt (after optical depth exceeds 1.0)
        Assert.True(dualBeltResult.HitPoint.X >= 95.0f && dualBeltResult.HitPoint.X <= 110.0f, $"HitPoint.X was {dualBeltResult.HitPoint.X}");
    }

    [Fact]
    public void ElevatedObserver_SeesOverCanopy()
    {
        // Height map with flat ground at Y=0
        HeightMap map = CreateFlatMap();

        // Forest belt: X in [50, 70] (20m width), canopy height 10m
        Vector2[] forest =
        [
            new Vector2(50f, 20f),
            new Vector2(70f, 20f),
            new Vector2(70f, 120f),
            new Vector2(50f, 120f)
        ];

        // Dense forest: D_max = 15m so 20m inside is blocked if traversed
        VegetationZone zone = VegetationZone.CreateDenseForest("BELT", forest, canopyHeight: 10.0f, maxPenetration: 15.0f);

        StaticGeometry geo = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [zone]);

        // Observer at X=10, Target at X=100. Distance = 90m.
        // Forest is at X=50..70 (center at X=60, which is fraction (60-10)/90 = 5/9 of distance).
        // 1. Ground observer at Y=1.5m, target at Y=1.5m: ray height is 1.5m < 10m canopy -> Blocked
        Vector3 groundObs = new(10f, 1.5f, 70f);
        Vector3 groundTgt = new(100f, 1.5f, 70f);
        LineOfSightResult groundResult = geo.CheckLineOfSight(groundObs, groundTgt);
        Assert.False(groundResult.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, groundResult.ObstructionKind);

        // 2. Elevated observer on ridge at Y=25m, target at ground Y=1.5m:
        // Ray height at X=50 is 25 - (25 - 1.5) * (40/90) = 25 - 10.44 = 14.56m > 10m canopy.
        // Ray height at X=70 is 25 - (25 - 1.5) * (60/90) = 25 - 15.67 = 9.33m.
        // Portion below 10m canopy is only from X = 66.8 to 70 (~3.2m).
        // Optical depth is 3.2m * (1/15) = 0.21 <= 1.0 -> Clear!
        Vector3 elevatedObs = new(10f, 25.0f, 70f);
        LineOfSightResult elevatedResult = geo.CheckLineOfSight(elevatedObs, groundTgt);
        Assert.True(elevatedResult.IsClear);
    }

    [Fact]
    public void MapHash_ChangesWhenVegetationChanges()
    {
        HeightMap map = CreateFlatMap();

        Vector2[] polyA =
        [
            new Vector2(40f, 40f),
            new Vector2(80f, 40f),
            new Vector2(80f, 80f),
            new Vector2(40f, 80f)
        ];

        VegetationZone zone1 = VegetationZone.CreateDenseForest("Z1", polyA, canopyHeight: 12.0f, maxPenetration: 25.0f);
        VegetationZone zone2 = VegetationZone.CreateDenseForest("Z1", polyA, canopyHeight: 14.0f, maxPenetration: 25.0f); // Different canopy
        VegetationZone zone3 = VegetationZone.CreateSparseForest("Z1", polyA, canopyHeight: 12.0f, maxPenetration: 75.0f); // Different density

        StaticGeometry geo1 = new(map, ReadOnlySpan<OpaqueStructure>.Empty, ReadOnlySpan<BridgeStructure>.Empty, ReadOnlySpan<TerrainObstacle>.Empty, [zone1]);
        StaticGeometry geo2 = new(map, ReadOnlySpan<OpaqueStructure>.Empty, ReadOnlySpan<BridgeStructure>.Empty, ReadOnlySpan<TerrainObstacle>.Empty, [zone2]);
        StaticGeometry geo3 = new(map, ReadOnlySpan<OpaqueStructure>.Empty, ReadOnlySpan<BridgeStructure>.Empty, ReadOnlySpan<TerrainObstacle>.Empty, [zone3]);

        ulong hash1 = MatchSetup.ComputeMapHash(geo1);
        ulong hash2 = MatchSetup.ComputeMapHash(geo2);
        ulong hash3 = MatchSetup.ComputeMapHash(geo3);

        Assert.NotEqual(hash1, hash2);
        Assert.NotEqual(hash1, hash3);
        Assert.NotEqual(hash2, hash3);
    }

    [Fact]
    public void ZeroManagedAllocations_InHotPath()
    {
        HeightMap map = CreateFlatMap();

        Vector2[] poly =
        [
            new Vector2(40f, 40f),
            new Vector2(80f, 40f),
            new Vector2(80f, 80f),
            new Vector2(40f, 80f)
        ];

        VegetationZone zone = VegetationZone.CreateDenseForest("Z", poly, canopyHeight: 12.0f, maxPenetration: 25.0f);
        StaticGeometry geo = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [zone]);

        Vector3 start = new(20f, 1.5f, 60f);
        Vector3 target = new(100f, 1.5f, 60f);

        // Warm up JIT and any static buffers
        for (int i = 0; i < 50; i++)
        {
            geo.CheckLineOfSight(start, target);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1000; i++)
        {
            geo.CheckLineOfSight(start, target);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void LineOfFire_ExcludesVegetation_DoesNotCreatePhysicalHitSurface()
    {
        HeightMap map = CreateFlatMap();

        // Dense forest block: X in [50, 150], Z in [50, 150]. Canopy height 12m. D_max = 20m.
        Vector2[] forestPolygon =
        [
            new Vector2(50f, 50f),
            new Vector2(150f, 50f),
            new Vector2(150f, 150f),
            new Vector2(50f, 150f)
        ];

        VegetationZone zone = VegetationZone.CreateDenseForest("DENSE", forestPolygon, canopyHeight: 12.0f, maxPenetration: 20.0f);
        StaticGeometry geo = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [zone]);

        // Observer at X=30, shooting towards X=120 (passing through 70m of dense forest)
        Vector3 muzzle = new(30f, 1.5f, 100f);
        Vector3 targetGround = new(120f, 0f, 100f);

        // Visual observation: blocked by vegetation
        LineOfSightResult los = geo.CheckLineOfSight(muzzle, targetGround);
        Assert.False(los.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, los.ObstructionKind);
        Assert.True(los.HitPoint.X >= 68f && los.HitPoint.X <= 72f, $"HitPoint.X was {los.HitPoint.X}");

        // Physical line of fire: NOT blocked by vegetation
        LineOfSightResult lof = geo.CheckLineOfFire(muzzle, targetGround);
        Assert.True(lof.IsClear);
        Assert.Equal(ObstructionKind.None, lof.ObstructionKind);

        Assert.True(geo.IsLineOfFireClear(muzzle, targetGround));
        Assert.False(geo.IsLineOfSightClear(muzzle, targetGround));
    }

    [Fact]
    public void BoundarySymmetry_StartEndBoundaryAndVertexTouch()
    {
        // Forest [50..100] x [50..100]
        Vector2[] poly =
        [
            new Vector2(50f, 50f),
            new Vector2(100f, 50f),
            new Vector2(100f, 100f),
            new Vector2(50f, 100f)
        ];

        VegetationZone zone = VegetationZone.CreateDenseForest("TEST", poly, canopyHeight: 12.0f, maxPenetration: 20.0f);

        Span<Vector2> intervalsFwd = stackalloc Vector2[8];
        Span<Vector2> intervalsRev = stackalloc Vector2[8];

        Vector2 start = new(50f, 70f);
        Vector2 end = new(90f, 70f);

        int countFwd = zone.ClipRaySegment(start, end, intervalsFwd);
        int countRev = zone.ClipRaySegment(end, start, intervalsRev);

        Assert.Equal(1, countFwd);
        Assert.Equal(1, countRev);

        // Forward: [0, 1]
        Assert.Equal(0f, intervalsFwd[0].X, 1e-4f);
        Assert.Equal(1f, intervalsFwd[0].Y, 1e-4f);

        // Reverse: [0, 1]
        Assert.Equal(0f, intervalsRev[0].X, 1e-4f);
        Assert.Equal(1f, intervalsRev[0].Y, 1e-4f);

        // Ray grazing a vertex: (20, 50) -> (45, 50) touching vertex (50, 50) extended
        Vector2 grazeStart = new(20f, 40f);
        Vector2 grazeEnd = new(40f, 50f);
        Span<Vector2> grazeIntervals = stackalloc Vector2[8];
        int countGraze = zone.ClipRaySegment(grazeStart, grazeEnd, grazeIntervals);
        Assert.Equal(0, countGraze);

        // Ray passing outside corner touching vertex (50, 50)
        Vector2 touchStart = new(40f, 60f);
        Vector2 touchEnd = new(60f, 40f);
        // Midpoint of (40, 60) to (60, 40) is (50, 50), which is exactly the corner vertex
        int countTouch = zone.ClipRaySegment(touchStart, touchEnd, grazeIntervals);
        Assert.Equal(0, countTouch);
    }

    [Fact]
    public void VegetationZoneReuse_DoesNotMutateAcrossStaticGeometries()
    {
        HeightMap flatLowMap = CreateFlatMap(64, 64, 4.0f, height: 0.0f);
        HeightMap flatHighMap = CreateFlatMap(64, 64, 4.0f, height: 100.0f);

        Vector2[] poly =
        [
            new Vector2(40f, 40f),
            new Vector2(80f, 40f),
            new Vector2(80f, 80f),
            new Vector2(40f, 80f)
        ];

        VegetationZone sharedZone = VegetationZone.CreateDenseForest("SHARED", poly, canopyHeight: 12.0f, maxPenetration: 20.0f);

        StaticGeometry geoLow = new(
            flatLowMap,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [sharedZone]);

        Vector3 start = new(20f, 5.0f, 60f);
        Vector3 target = new(100f, 5.0f, 60f);

        // In geoLow, ground is 0m, canopy is 0..12m. Ray at Y=5m is inside canopy (blocked after 20m)
        LineOfSightResult resLow1 = geoLow.CheckLineOfSight(start, target);
        Assert.False(resLow1.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, resLow1.ObstructionKind);

        // Create second geometry with height 100m using the exact same sharedZone
        StaticGeometry geoHigh = new(
            flatHighMap,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [sharedZone]);

        // geoLow query must be completely unchanged
        LineOfSightResult resLow2 = geoLow.CheckLineOfSight(start, target);
        Assert.False(resLow2.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, resLow2.ObstructionKind);
        Assert.Equal(resLow1.Distance, resLow2.Distance, 1e-4f);

        // In geoHigh, canopy is at 100..112m. Ray at Y=105m is inside canopy
        Vector3 highStart = new(20f, 105.0f, 60f);
        Vector3 highTarget = new(100f, 105.0f, 60f);
        LineOfSightResult resHigh = geoHigh.CheckLineOfSight(highStart, highTarget);
        Assert.False(resHigh.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, resHigh.ObstructionKind);
    }

    [Fact]
    public void BufferCapacity_ZoneOrderIndependence()
    {
        HeightMap map = CreateFlatMap(128, 128, 4.0f, height: 0.0f);

        // Create 35 vegetation zones in a row along X from X=10 to X=150
        VegetationZone[] zones = new VegetationZone[35];
        for (int i = 0; i < 35; i++)
        {
            float x0 = 10f + i * 4.0f;
            float x1 = x0 + 3.0f;
            Vector2[] poly =
            [
                new Vector2(x0, 20f),
                new Vector2(x1, 20f),
                new Vector2(x1, 80f),
                new Vector2(x0, 80f)
            ];
            // Sparse penetration = 100m
            zones[i] = VegetationZone.CreateSparseForest($"ZONE_{i}", poly, canopyHeight: 12.0f, maxPenetration: 100.0f);
        }

        VegetationZone[] reversedZones = (VegetationZone[])zones.Clone();
        Array.Reverse(reversedZones);

        StaticGeometry geoNormal = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            zones);

        StaticGeometry geoReversed = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            reversedZones);

        Vector3 start = new(5f, 1.5f, 50f);
        Vector3 target = new(160f, 1.5f, 50f);

        LineOfSightContext ctx = new();
        LineOfSightResult resNormal = geoNormal.CheckLineOfSight(start, target, ctx);
        LineOfSightResult resReversed = geoReversed.CheckLineOfSight(start, target, ctx);

        Assert.Equal(resNormal.IsClear, resReversed.IsClear);
        Assert.Equal(resNormal.ObstructionKind, resReversed.ObstructionKind);
        Assert.Equal(resNormal.Distance, resReversed.Distance, 1e-4f);
        Assert.Equal(resNormal.HitPoint.X, resReversed.HitPoint.X, 1e-3f);
    }

    [Fact]
    public void MapHash_ClearingChangesHash_AndPermutationPreservesHash()
    {
        HeightMap map = CreateFlatMap();
        VegetationZone forest = VegetationZone.CreateDenseForest(
            "FOREST",
            [
                new Vector2(40f, 40f),
                new Vector2(100f, 40f),
                new Vector2(100f, 100f),
                new Vector2(40f, 100f)
            ],
            canopyHeight: 12.0f,
            maxPenetration: 25.0f);

        VegetationClearing c1 = VegetationClearing.CreateBox("CLEAR-A", 50f, 50f, 60f, 60f);
        VegetationClearing c2 = VegetationClearing.CreateCorridor("CLEAR-B", new Vector2(70f, 50f), new Vector2(70f, 80f), 5f);
        VegetationClearing c3 = VegetationClearing.CreatePolygon(
            "CLEAR-C",
            [
                new Vector2(80f, 50f),
                new Vector2(90f, 50f),
                new Vector2(90f, 70f)
            ]);

        StaticGeometry geoBase = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [forest],
            [c1, c2, c3]);

        // Permute clearing order in array
        StaticGeometry geoPermuted = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [forest],
            [c3, c1, c2]);

        ulong hashBase = MatchSetup.ComputeMapHash(geoBase);
        ulong hashPermuted = MatchSetup.ComputeMapHash(geoPermuted);

        // Canonical ordering by Id preserves hash
        Assert.Equal(hashBase, hashPermuted);

        // Modifying corridor radius changes hash
        VegetationClearing c2ModRadius = VegetationClearing.CreateCorridor("CLEAR-B", new Vector2(70f, 50f), new Vector2(70f, 80f), 6f);
        StaticGeometry geoModRadius = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [forest],
            [c1, c2ModRadius, c3]);
        Assert.NotEqual(hashBase, MatchSetup.ComputeMapHash(geoModRadius));

        // Modifying corridor endpoint changes hash
        VegetationClearing c2ModEndpoint = VegetationClearing.CreateCorridor("CLEAR-B", new Vector2(70f, 50f), new Vector2(71f, 80f), 5f);
        StaticGeometry geoModEndpoint = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [forest],
            [c1, c2ModEndpoint, c3]);
        Assert.NotEqual(hashBase, MatchSetup.ComputeMapHash(geoModEndpoint));

        // Modifying polygon vertex changes hash
        VegetationClearing c3ModPoly = VegetationClearing.CreatePolygon(
            "CLEAR-C",
            [
                new Vector2(80f, 50f),
                new Vector2(91f, 50f),
                new Vector2(90f, 70f)
            ]);
        StaticGeometry geoModPoly = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [forest],
            [c1, c2, c3ModPoly]);
        Assert.NotEqual(hashBase, MatchSetup.ComputeMapHash(geoModPoly));

        // Modifying box bounds changes hash
        VegetationClearing c1ModBox = VegetationClearing.CreateBox("CLEAR-A", 50f, 50f, 61f, 60f);
        StaticGeometry geoModBox = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [forest],
            [c1ModBox, c2, c3]);
        Assert.NotEqual(hashBase, MatchSetup.ComputeMapHash(geoModBox));

        // Removing clearings changes hash
        StaticGeometry geoNoClearings = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [forest],
            []);
        Assert.NotEqual(hashBase, MatchSetup.ComputeMapHash(geoNoClearings));
    }

    [Fact]
    public void Clearings_Over64_OrderIndependence()
    {
        HeightMap map = CreateFlatMap(128, 128, 4.0f, height: 0.0f);

        // Dense forest block: X in [40, 80], Z in [20, 80]. D_max = 20m.
        VegetationZone forest = VegetationZone.CreateDenseForest(
            "DENSE",
            [
                new Vector2(40f, 20f),
                new Vector2(80f, 20f),
                new Vector2(80f, 80f),
                new Vector2(40f, 80f)
            ],
            canopyHeight: 12.0f,
            maxPenetration: 20.0f);

        // Create 70 clearings:
        // Clearings 0..63 are placed far away along Z (Z in 150..300)
        // Clearing 64 (the 65th) is placed right on the ray path: X in [45, 75], Z in [48, 52]
        // Clearings 65..69 are also placed far away
        VegetationClearing[] clearings = new VegetationClearing[70];
        for (int i = 0; i < 70; i++)
        {
            if (i == 64)
            {
                // This clearing cuts through 30m of forest (from X=45 to X=75) along the ray at Z=50
                clearings[i] = VegetationClearing.CreateBox($"CLEAR_{i:D2}", 45f, 48f, 75f, 52f);
            }
            else
            {
                float z = 150f + i * 5f;
                clearings[i] = VegetationClearing.CreateBox($"CLEAR_{i:D2}", 45f, z, 75f, z + 3f);
            }
        }

        VegetationClearing[] reversedClearings = (VegetationClearing[])clearings.Clone();
        Array.Reverse(reversedClearings);

        StaticGeometry geoNormal = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [forest],
            clearings);

        StaticGeometry geoReversed = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [forest],
            reversedClearings);

        // Ray from (30, 1.5, 50) to (90, 1.5, 50).
        // Total forest span: X=40 to X=80 (40m total).
        // Without clearing 64: 40m forest > D_max (20m) -> Blocked.
        // With clearing 64: 30m is clearing, remaining forest is 10m < D_max (20m) -> Clear.
        Vector3 start = new(30f, 1.5f, 50f);
        Vector3 target = new(90f, 1.5f, 50f);

        LineOfSightContext ctx = new();
        LineOfSightResult resNormal = geoNormal.CheckLineOfSight(start, target, ctx);
        LineOfSightResult resReversed = geoReversed.CheckLineOfSight(start, target, ctx);

        // Clearing 64 MUST be processed regardless of array ordering and whether it appears before or after index 64
        Assert.True(resNormal.IsClear, "Clearing at index 64 must not be dropped.");
        Assert.True(resReversed.IsClear, "Reversed clearings array must preserve clearing at index 64.");
        Assert.Equal(resNormal.IsClear, resReversed.IsClear);
        Assert.Equal(resNormal.ObstructionKind, resReversed.ObstructionKind);
        Assert.Equal(resNormal.Distance, resReversed.Distance, 1e-4f);

        // Verify that without clearing 64, the ray is indeed blocked
        VegetationClearing[] withoutClearing64 = new VegetationClearing[69];
        int destIdx = 0;
        for (int i = 0; i < 70; i++)
        {
            if (i != 64) withoutClearing64[destIdx++] = clearings[i];
        }
        StaticGeometry geoBlocked = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            [forest],
            withoutClearing64);
        LineOfSightResult resBlocked = geoBlocked.CheckLineOfSight(start, target, ctx);
        Assert.False(resBlocked.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, resBlocked.ObstructionKind);
    }

    [Fact]
    public void ZeroManagedAllocations_LargeGeometry_NoContext()
    {
        HeightMap map = CreateFlatMap(128, 128, 4.0f, height: 0.0f);

        // 35 zones and 70 clearings
        VegetationZone[] zones = new VegetationZone[35];
        for (int i = 0; i < 35; i++)
        {
            float x0 = 10f + i * 3.5f;
            float x1 = x0 + 3.0f;
            Vector2[] poly =
            [
                new Vector2(x0, 20f),
                new Vector2(x1, 20f),
                new Vector2(x1, 80f),
                new Vector2(x0, 80f)
            ];
            zones[i] = VegetationZone.CreateSparseForest($"ZONE_{i}", poly, canopyHeight: 12.0f, maxPenetration: 100.0f);
        }

        VegetationClearing[] clearings = new VegetationClearing[70];
        for (int i = 0; i < 70; i++)
        {
            float z0 = 10f + i * 3.0f;
            clearings[i] = VegetationClearing.CreateBox($"CLEAR_{i}", 5f, z0, 150f, z0 + 1.0f);
        }

        StaticGeometry geo = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            zones,
            clearings);

        Vector3 start = new(5f, 1.5f, 50f);
        Vector3 target = new(160f, 1.5f, 50f);

        // Warm up thread-static buffers
        for (int i = 0; i < 20; i++)
        {
            geo.CheckLineOfSight(start, target, context: null);
            geo.IsVegetationClear(start, target, context: null);
        }

        // Measure allocations over 500 iterations
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 500; i++)
        {
            geo.CheckLineOfSight(start, target, context: null);
            geo.IsVegetationClear(start, target, context: null);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
