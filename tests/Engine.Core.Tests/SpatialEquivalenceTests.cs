using System.Numerics;
using DeadGround.Engine;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class SpatialEquivalenceTests
{
    [Fact]
    public void TerrainSpatialIndex_MatchesBruteForce_RandomPointsAndObstacles()
    {
        // Deterministic random seed for repeatable test runs
        Random rnd = new(42);
        const float mapWidth = 500f;
        const float mapDepth = 500f;

        // 400 random terrain obstacles, some extending beyond map boundaries
        const int obstacleCount = 400;
        TerrainObstacle[] obstacles = new TerrainObstacle[obstacleCount];
        for (int i = 0; i < obstacleCount; i++)
        {
            float minX = (float)(rnd.NextDouble() * 700.0 - 100.0);
            float minZ = (float)(rnd.NextDouble() * 700.0 - 100.0);
            float w = (float)(rnd.NextDouble() * 45.0 + 5.0);
            float d = (float)(rnd.NextDouble() * 45.0 + 5.0);
            TraversabilityCapabilities cap = (rnd.Next(2) == 0)
                ? TraversabilityCapabilities.Water
                : TraversabilityCapabilities.DenseForest;
            obstacles[i] = new TerrainObstacle(minX, minZ, minX + w, minZ + d, cap);
        }

        TerrainSpatialIndex index = new(mapWidth, mapDepth, obstacles);

        TraversabilityCapabilities[] testCapabilities =
        [
            TraversabilityCapabilities.DefaultVehicle,
            TraversabilityCapabilities.Foot,
            TraversabilityCapabilities.Amphibious
        ];

        // 20,000 random query points across and beyond map bounds
        const int queryCount = 20000;
        Vector2[] testPoints = new Vector2[queryCount];
        for (int i = 0; i < queryCount; i++)
        {
            testPoints[i] = new Vector2(
                (float)(rnd.NextDouble() * 600.0 - 50.0),
                (float)(rnd.NextDouble() * 600.0 - 50.0));
        }

        const float radius = 1.0f;
        const float maxClearance = 50.0f;
        const float tolerance = 1e-4f;

        foreach (TraversabilityCapabilities caps in testCapabilities)
        {
            for (int i = 0; i < queryCount; i++)
            {
                Vector2 pt = testPoints[i];

                // 1. Equivalence check for ComputePointClearance
                float bruteClearance = BruteForceClearance(pt, caps, maxClearance, obstacles);
                float indexClearance = index.ComputePointClearance(pt, caps, maxClearance);

                Assert.True(MathF.Abs(bruteClearance - indexClearance) <= tolerance,
                    $"Clearance mismatch at {pt} for {caps}: brute={bruteClearance}, index={indexClearance}");

                // 2. Equivalence check for IsPointClear
                bool bruteClear = BruteForceIsClear(pt, radius, caps, obstacles);
                bool indexClear = index.IsPointClear(pt, radius, caps);

                Assert.Equal(bruteClear, indexClear);

                // 3. Equivalence and containment priority check for TryFindBlockingObstacle
                bool hasBlocking = index.TryFindBlockingObstacle(pt, radius, caps, out TerrainObstacle? blocking);
                Assert.Equal(!bruteClear, hasBlocking);

                if (hasBlocking)
                {
                    Assert.NotNull(blocking);
                    Assert.True(blocking.Blocks(caps));

                    // Containment priority: if point is inside any blocking obstacle,
                    // returned obstacle must contain the point (inside > nearby).
                    bool pointInsideAny = false;
                    for (int o = 0; o < obstacles.Length; o++)
                    {
                        if (obstacles[o].Blocks(caps) && obstacles[o].Contains(pt))
                        {
                            pointInsideAny = true;
                            break;
                        }
                    }

                    if (pointInsideAny)
                    {
                        Assert.True(blocking.Contains(pt),
                            $"Point {pt} is inside an obstacle, but returned blocking obstacle does not contain it.");
                    }
                }
            }
        }
    }

    [Fact]
    public void NavigationGrid_ComputePointClearance_MatchesBruteForceWithStructures()
    {
        Random rnd = new(1337);
        const int mapSize = 501;
        HeightMap map = new(mapSize, mapSize, 1.0f, 0.01f, new ushort[mapSize * mapSize]);

        // 200 random grounded structures
        const int structureCount = 200;
        OpaqueStructure[] structures = new OpaqueStructure[structureCount];
        for (int i = 0; i < structureCount; i++)
        {
            float minX = (float)(rnd.NextDouble() * 450.0 + 10.0);
            float minZ = (float)(rnd.NextDouble() * 450.0 + 10.0);
            float w = (float)(rnd.NextDouble() * 20.0 + 5.0);
            float d = (float)(rnd.NextDouble() * 20.0 + 5.0);
            structures[i] = new OpaqueStructure(
                new StructureId(i + 1),
                minX, 0f, minZ,
                minX + w, 10f, minZ + d,
                isGrounded: true);
        }

        StaticGeometry geo = new(map, structures);
        NavigationGrid grid = new(geo, cellSize: 0.5f, enableHierarchical: true);

        const int queryCount = 5000;
        const float maxClearance = 50.0f;
        const float tolerance = 1e-4f;

        for (int i = 0; i < queryCount; i++)
        {
            Vector2 pt = new(
                (float)(rnd.NextDouble() * 500.0),
                (float)(rnd.NextDouble() * 500.0));

            float bruteClearance = BruteForceStructureClearance(pt, maxClearance, 500f, 500f, structures);
            float gridClearance = grid.ComputePointClearance(pt, TraversabilityCapabilities.DefaultVehicle, supportSurface: 0, maxClearance: maxClearance);

            Assert.True(MathF.Abs(bruteClearance - gridClearance) <= tolerance,
                $"Structure clearance mismatch at {pt}: brute={bruteClearance}, grid={gridClearance}");
        }
    }

    private static float BruteForceClearance(
        Vector2 pt,
        TraversabilityCapabilities caps,
        float maxClearance,
        TerrainObstacle[] obstacles)
    {
        float clearance = maxClearance;
        for (int i = 0; i < obstacles.Length; i++)
        {
            TerrainObstacle obs = obstacles[i];
            if (!obs.Blocks(caps)) continue;

            if (obs.Contains(pt)) return 0f;

            float dist = obs.DistanceTo(pt);
            if (dist < clearance) clearance = dist;
        }
        return clearance;
    }

    private static bool BruteForceIsClear(
        Vector2 pt,
        float radius,
        TraversabilityCapabilities caps,
        TerrainObstacle[] obstacles)
    {
        float effectiveRadius = radius - 1e-4f;
        for (int i = 0; i < obstacles.Length; i++)
        {
            TerrainObstacle obs = obstacles[i];
            if (!obs.Blocks(caps)) continue;

            if (obs.Contains(pt) || obs.DistanceTo(pt) < effectiveRadius)
                return false;
        }
        return true;
    }

    private static float BruteForceStructureClearance(
        Vector2 pt,
        float maxClearance,
        float mapWidth,
        float mapDepth,
        OpaqueStructure[] structures)
    {
        float edgeDist = MathF.Min(MathF.Min(pt.X, mapWidth - pt.X), MathF.Min(pt.Y, mapDepth - pt.Y));
        if (edgeDist <= 0f) return 0f;

        float clearance = MathF.Min(edgeDist, maxClearance);
        for (int i = 0; i < structures.Length; i++)
        {
            OpaqueStructure s = structures[i];
            if (!s.IsGrounded) continue;

            if (pt.X >= s.MinX && pt.X <= s.MaxX && pt.Y >= s.MinZ && pt.Y <= s.MaxZ)
                return 0f;

            float dx = MathF.Max(0f, MathF.Max(s.MinX - pt.X, pt.X - s.MaxX));
            float dz = MathF.Max(0f, MathF.Max(s.MinZ - pt.Y, pt.Y - s.MaxZ));
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < clearance) clearance = dist;
        }

        return MathF.Max(0f, clearance);
    }
}
