using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DeadGround.Engine;

/// <summary>
/// 2D spatial index for static terrain zones (water, dense forests, etc.) partitioned into regular spatial buckets.
/// Shared by NavigationGrid and DestinationEvaluator for consistent passability and clearance queries.
/// </summary>
public sealed class TerrainSpatialIndex
{
    private const float SpatialBucketSize = 32.0f;
    private const float GeometryTolerance = 1e-4f;

    private readonly TerrainObstacle[] _obstacles;
    private readonly int _bucketCols;
    private readonly int _bucketRows;
    private readonly int[] _bucketOffsets;
    private readonly int[] _bucketCounts;
    private readonly int[] _bucketIndices;
    private readonly int[] _queryStamp;
    private int _currentQueryStamp;

    public int Count => _obstacles.Length;
    public ReadOnlySpan<TerrainObstacle> Obstacles => _obstacles;
    public float BucketSize => SpatialBucketSize;
    public int BucketCols => _bucketCols;
    public int BucketRows => _bucketRows;

    public long EstimatedMemoryBytes =>
        (long)_obstacles.Length * 32L +
        (long)_bucketOffsets.Length * sizeof(int) +
        (long)_bucketCounts.Length * sizeof(int) +
        (long)_bucketIndices.Length * sizeof(int) +
        (long)_queryStamp.Length * sizeof(int);

    public TerrainSpatialIndex(float mapWidth, float mapDepth, ReadOnlySpan<TerrainObstacle> obstacles)
    {
        _obstacles = obstacles.ToArray();
        _bucketCols = Math.Max(1, (int)MathF.Ceiling(mapWidth / SpatialBucketSize));
        _bucketRows = Math.Max(1, (int)MathF.Ceiling(mapDepth / SpatialBucketSize));
        int totalBuckets = _bucketCols * _bucketRows;

        int[] tempCounts = new int[totalBuckets];
        for (int i = 0; i < _obstacles.Length; i++)
        {
            TerrainObstacle obs = _obstacles[i];
            int minBx = Math.Clamp((int)(obs.MinX / SpatialBucketSize), 0, _bucketCols - 1);
            int maxBx = Math.Clamp((int)(obs.MaxX / SpatialBucketSize), 0, _bucketCols - 1);
            int minBz = Math.Clamp((int)(obs.MinZ / SpatialBucketSize), 0, _bucketRows - 1);
            int maxBz = Math.Clamp((int)(obs.MaxZ / SpatialBucketSize), 0, _bucketRows - 1);

            for (int bz = minBz; bz <= maxBz; bz++)
            {
                int row = bz * _bucketCols;
                for (int bx = minBx; bx <= maxBx; bx++)
                {
                    tempCounts[row + bx]++;
                }
            }
        }

        _bucketOffsets = new int[totalBuckets];
        _bucketCounts = new int[totalBuckets];
        int totalRefs = 0;
        for (int b = 0; b < totalBuckets; b++)
        {
            _bucketOffsets[b] = totalRefs;
            totalRefs += tempCounts[b];
        }

        _bucketIndices = new int[totalRefs];
        Array.Clear(tempCounts);

        for (int i = 0; i < _obstacles.Length; i++)
        {
            TerrainObstacle obs = _obstacles[i];
            int minBx = Math.Clamp((int)(obs.MinX / SpatialBucketSize), 0, _bucketCols - 1);
            int maxBx = Math.Clamp((int)(obs.MaxX / SpatialBucketSize), 0, _bucketCols - 1);
            int minBz = Math.Clamp((int)(obs.MinZ / SpatialBucketSize), 0, _bucketRows - 1);
            int maxBz = Math.Clamp((int)(obs.MaxZ / SpatialBucketSize), 0, _bucketRows - 1);

            for (int bz = minBz; bz <= maxBz; bz++)
            {
                int row = bz * _bucketCols;
                for (int bx = minBx; bx <= maxBx; bx++)
                {
                    int b = row + bx;
                    int writeIdx = _bucketOffsets[b] + tempCounts[b]++;
                    _bucketIndices[writeIdx] = i;
                }
            }
        }

        for (int b = 0; b < totalBuckets; b++)
        {
            _bucketCounts[b] = tempCounts[b];
        }

        _queryStamp = new int[_obstacles.Length];
        _currentQueryStamp = 0;
    }

    public float ComputePointClearance(
        Vector2 point,
        TraversabilityCapabilities capabilities,
        float currentClearance = float.PositiveInfinity)
    {
        if (_obstacles.Length == 0 || currentClearance <= 0f) return currentClearance;

        float clearance = currentClearance;

        int centerBx = Math.Clamp((int)(point.X / SpatialBucketSize), 0, _bucketCols - 1);
        int centerBz = Math.Clamp((int)(point.Y / SpatialBucketSize), 0, _bucketRows - 1);

        _currentQueryStamp++;
        if (_currentQueryStamp == int.MaxValue)
        {
            Array.Clear(_queryStamp);
            _currentQueryStamp = 1;
        }

        // Center bucket (k = 0)
        int centerBucket = centerBz * _bucketCols + centerBx;
        int centerStart = _bucketOffsets[centerBucket];
        int centerCount = _bucketCounts[centerBucket];
        for (int i = 0; i < centerCount; i++)
        {
            int obsIdx = _bucketIndices[centerStart + i];
            _queryStamp[obsIdx] = _currentQueryStamp;
            TerrainObstacle obs = _obstacles[obsIdx];
            if (obs.Blocks(capabilities))
            {
                if (obs.Contains(point)) return 0f;
                float dist = obs.DistanceTo(point);
                if (dist < clearance) clearance = dist;
            }
        }

        int maxRing = Math.Max(Math.Max(centerBx, _bucketCols - 1 - centerBx), Math.Max(centerBz, _bucketRows - 1 - centerBz));

        for (int k = 1; k <= maxRing; k++)
        {
            // Minimum distance from point to any bucket in ring k is at least
            // the distance from point to the boundary of the inner box [centerBx - k + 1 .. centerBx + k - 1].
            float xMin = (centerBx - k + 1) * SpatialBucketSize;
            float xMax = (centerBx + k) * SpatialBucketSize;
            float zMin = (centerBz - k + 1) * SpatialBucketSize;
            float zMax = (centerBz + k) * SpatialBucketSize;

            float minDistToRing = MathF.Min(MathF.Min(point.X - xMin, xMax - point.X), MathF.Min(point.Y - zMin, zMax - point.Y));
            if (minDistToRing >= clearance)
            {
                break;
            }

            int minBx = centerBx - k;
            int maxBx = centerBx + k;
            int minBz = centerBz - k;
            int maxBz = centerBz + k;

            // Top row (bz = minBz) & bottom row (bz = maxBz)
            for (int bx = minBx; bx <= maxBx; bx++)
            {
                if (bx >= 0 && bx < _bucketCols)
                {
                    if (minBz >= 0 && minBz < _bucketRows)
                        CheckBucket(minBz * _bucketCols + bx, point, capabilities, ref clearance);
                    if (clearance <= 0f) return 0f;

                    if (maxBz >= 0 && maxBz < _bucketRows)
                        CheckBucket(maxBz * _bucketCols + bx, point, capabilities, ref clearance);
                    if (clearance <= 0f) return 0f;
                }
            }

            // Left col (bx = minBx) & right col (bx = maxBx)
            for (int bz = minBz + 1; bz <= maxBz - 1; bz++)
            {
                if (bz >= 0 && bz < _bucketRows)
                {
                    if (minBx >= 0 && minBx < _bucketCols)
                        CheckBucket(bz * _bucketCols + minBx, point, capabilities, ref clearance);
                    if (clearance <= 0f) return 0f;

                    if (maxBx >= 0 && maxBx < _bucketCols)
                        CheckBucket(bz * _bucketCols + maxBx, point, capabilities, ref clearance);
                    if (clearance <= 0f) return 0f;
                }
            }
        }

        return MathF.Max(0f, clearance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CheckBucket(int b, Vector2 point, TraversabilityCapabilities capabilities, ref float clearance)
    {
        int start = _bucketOffsets[b];
        int count = _bucketCounts[b];
        for (int i = 0; i < count; i++)
        {
            int obsIdx = _bucketIndices[start + i];
            if (_queryStamp[obsIdx] == _currentQueryStamp) continue;
            _queryStamp[obsIdx] = _currentQueryStamp;

            TerrainObstacle obs = _obstacles[obsIdx];
            if (obs.Blocks(capabilities))
            {
                if (obs.Contains(point))
                {
                    clearance = 0f;
                    return;
                }
                float dist = obs.DistanceTo(point);
                if (dist < clearance) clearance = dist;
            }
        }
    }

    public bool TryFindBlockingObstacle(Vector2 point, float radius, TraversabilityCapabilities capabilities, [NotNullWhen(true)] out TerrainObstacle? obstacle)
    {
        obstacle = null;
        if (_obstacles.Length == 0) return false;

        float effectiveRadius = radius - GeometryTolerance;

        // Pass 1: Prioritize containment. If point is strictly inside an obstacle, that obstacle
        // takes precedence over nearby obstacles within radius (e.g. standing in forest near water).
        int centerBx = Math.Clamp((int)(point.X / SpatialBucketSize), 0, _bucketCols - 1);
        int centerBz = Math.Clamp((int)(point.Y / SpatialBucketSize), 0, _bucketRows - 1);
        int centerBucket = centerBz * _bucketCols + centerBx;
        int cStart = _bucketOffsets[centerBucket];
        int cCount = _bucketCounts[centerBucket];
        for (int i = 0; i < cCount; i++)
        {
            int obsIdx = _bucketIndices[cStart + i];
            TerrainObstacle obs = _obstacles[obsIdx];
            if (obs.Blocks(capabilities) && obs.Contains(point))
            {
                obstacle = obs;
                return true;
            }
        }

        // Pass 2: Check proximity across buckets within the search radius
        int minBx = Math.Clamp((int)((point.X - effectiveRadius) / SpatialBucketSize), 0, _bucketCols - 1);
        int maxBx = Math.Clamp((int)((point.X + effectiveRadius) / SpatialBucketSize), 0, _bucketCols - 1);
        int minBz = Math.Clamp((int)((point.Y - effectiveRadius) / SpatialBucketSize), 0, _bucketRows - 1);
        int maxBz = Math.Clamp((int)((point.Y + effectiveRadius) / SpatialBucketSize), 0, _bucketRows - 1);

        for (int bz = minBz; bz <= maxBz; bz++)
        {
            int row = bz * _bucketCols;
            for (int bx = minBx; bx <= maxBx; bx++)
            {
                int b = row + bx;
                int start = _bucketOffsets[b];
                int count = _bucketCounts[b];
                for (int i = 0; i < count; i++)
                {
                    int obsIdx = _bucketIndices[start + i];
                    TerrainObstacle obs = _obstacles[obsIdx];
                    if (obs.Blocks(capabilities))
                    {
                        if (obs.DistanceTo(point) < effectiveRadius)
                        {
                            obstacle = obs;
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    public bool IsPointClear(Vector2 point, float radius, TraversabilityCapabilities capabilities) =>
        !TryFindBlockingObstacle(point, radius, capabilities, out _);

    public bool HasObstaclesInBox(float minX, float minZ, float maxX, float maxZ, TraversabilityCapabilities capabilities)
    {
        if (_obstacles.Length == 0) return false;

        int minBx = Math.Clamp((int)(minX / SpatialBucketSize), 0, _bucketCols - 1);
        int maxBx = Math.Clamp((int)(maxX / SpatialBucketSize), 0, _bucketCols - 1);
        int minBz = Math.Clamp((int)(minZ / SpatialBucketSize), 0, _bucketRows - 1);
        int maxBz = Math.Clamp((int)(maxZ / SpatialBucketSize), 0, _bucketRows - 1);

        for (int bz = minBz; bz <= maxBz; bz++)
        {
            int row = bz * _bucketCols;
            for (int bx = minBx; bx <= maxBx; bx++)
            {
                int b = row + bx;
                int start = _bucketOffsets[b];
                int count = _bucketCounts[b];
                for (int i = 0; i < count; i++)
                {
                    int obsIdx = _bucketIndices[start + i];
                    TerrainObstacle obs = _obstacles[obsIdx];
                    if (obs.Blocks(capabilities))
                    {
                        if (maxX >= obs.MinX && minX <= obs.MaxX && maxZ >= obs.MinZ && minZ <= obs.MaxZ)
                            return true;
                    }
                }
            }
        }

        return false;
    }

    public bool IsSegmentClear(Vector2 start, Vector2 end, float radius, TraversabilityCapabilities capabilities)
    {
        if (_obstacles.Length == 0) return true;

        float effectiveRadius = radius - GeometryTolerance;
        float segMinX = MathF.Min(start.X, end.X) - effectiveRadius;
        float segMaxX = MathF.Max(start.X, end.X) + effectiveRadius;
        float segMinZ = MathF.Min(start.Y, end.Y) - effectiveRadius;
        float segMaxZ = MathF.Max(start.Y, end.Y) + effectiveRadius;

        int minBx = Math.Clamp((int)(segMinX / SpatialBucketSize), 0, _bucketCols - 1);
        int maxBx = Math.Clamp((int)(segMaxX / SpatialBucketSize), 0, _bucketCols - 1);
        int minBz = Math.Clamp((int)(segMinZ / SpatialBucketSize), 0, _bucketRows - 1);
        int maxBz = Math.Clamp((int)(segMaxZ / SpatialBucketSize), 0, _bucketRows - 1);

        if (minBx == maxBx && minBz == maxBz)
        {
            int b = minBz * _bucketCols + minBx;
            int startIdx = _bucketOffsets[b];
            int count = _bucketCounts[b];
            for (int i = 0; i < count; i++)
            {
                int obsIdx = _bucketIndices[startIdx + i];
                TerrainObstacle obs = _obstacles[obsIdx];
                if (obs.Blocks(capabilities))
                {
                    if (segMaxX < obs.MinX || segMinX > obs.MaxX || segMaxZ < obs.MinZ || segMinZ > obs.MaxZ)
                        continue;

                    if (SegmentIntersectsExpandedBox(start, end, obs.MinX, obs.MinZ, obs.MaxX, obs.MaxZ, effectiveRadius))
                        return false;
                }
            }
            return true;
        }

        _currentQueryStamp++;
        if (_currentQueryStamp == int.MaxValue)
        {
            Array.Clear(_queryStamp);
            _currentQueryStamp = 1;
        }

        for (int bz = minBz; bz <= maxBz; bz++)
        {
            int row = bz * _bucketCols;
            for (int bx = minBx; bx <= maxBx; bx++)
            {
                int b = row + bx;
                int startIdx = _bucketOffsets[b];
                int count = _bucketCounts[b];
                for (int i = 0; i < count; i++)
                {
                    int obsIdx = _bucketIndices[startIdx + i];
                    if (_queryStamp[obsIdx] == _currentQueryStamp) continue;
                    _queryStamp[obsIdx] = _currentQueryStamp;

                    TerrainObstacle obs = _obstacles[obsIdx];
                    if (obs.Blocks(capabilities))
                    {
                        if (segMaxX < obs.MinX || segMinX > obs.MaxX || segMaxZ < obs.MinZ || segMinZ > obs.MaxZ)
                            continue;

                        if (SegmentIntersectsExpandedBox(start, end, obs.MinX, obs.MinZ, obs.MaxX, obs.MaxZ, effectiveRadius))
                            return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool SegmentIntersectsExpandedBox(Vector2 start, Vector2 end, float minX, float minZ, float maxX, float maxZ, float radius)
    {
        // 1. Expanded rectangle in X
        if (SegmentIntersectsAabb(start, end, minX - radius, maxX + radius, minZ, maxZ))
            return true;

        // 2. Expanded rectangle in Z
        if (SegmentIntersectsAabb(start, end, minX, maxX, minZ - radius, maxZ + radius))
            return true;

        // 3. Four rounded corner discs
        if (SegmentIntersectsCircle(start, end, new Vector2(minX, minZ), radius)) return true;
        if (SegmentIntersectsCircle(start, end, new Vector2(maxX, minZ), radius)) return true;
        if (SegmentIntersectsCircle(start, end, new Vector2(minX, maxZ), radius)) return true;
        if (SegmentIntersectsCircle(start, end, new Vector2(maxX, maxZ), radius)) return true;

        return false;
    }

    private static bool SegmentIntersectsAabb(Vector2 start, Vector2 end, float minX, float maxX, float minZ, float maxZ)
    {
        float dx = end.X - start.X;
        float dz = end.Y - start.Y;

        float tMin = 0f;
        float tMax = 1f;

        if (MathF.Abs(dx) < 1e-6f)
        {
            if (start.X < minX || start.X > maxX) return false;
        }
        else
        {
            float invDx = 1f / dx;
            float t1 = (minX - start.X) * invDx;
            float t2 = (maxX - start.X) * invDx;
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return false;
        }

        if (MathF.Abs(dz) < 1e-6f)
        {
            if (start.Y < minZ || start.Y > maxZ) return false;
        }
        else
        {
            float invDz = 1f / dz;
            float t1 = (minZ - start.Y) * invDz;
            float t2 = (maxZ - start.Y) * invDz;
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return false;
        }

        return true;
    }

    private static bool SegmentIntersectsCircle(Vector2 start, Vector2 end, Vector2 center, float radius)
    {
        Vector2 d = end - start;
        Vector2 f = start - center;

        float a = Vector2.Dot(d, d);
        float b = 2f * Vector2.Dot(f, d);
        float c = Vector2.Dot(f, f) - radius * radius;

        if (c <= 0f) return true;
        if (a < 1e-8f) return false;

        float discriminant = b * b - 4f * a * c;
        if (discriminant < 0f) return false;

        float sqrtD = MathF.Sqrt(discriminant);
        float t1 = (-b - sqrtD) / (2f * a);
        float t2 = (-b + sqrtD) / (2f * a);

        return (t1 >= 0f && t1 <= 1f) || (t2 >= 0f && t2 <= 1f);
    }
}
