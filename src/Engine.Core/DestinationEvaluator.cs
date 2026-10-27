using System.Numerics;

namespace DeadGround.Engine;

public enum DestinationBlockReason : byte
{
    None = 0,
    OutsideMapBounds = 1,
    Building = 2,
    Water = 3,
    DenseForest = 4,
    InsufficientClearance = 5
}

public enum DestinationValidity : byte
{
    Valid = 0,
    Adjusted = 1,
    Blocked = 2
}

public static class DestinationBlockReasons
{
    public const string Water = "Water blocks this unit.";
    public const string DenseForest = "Dense forest blocks this unit.";
    public const string Building = "Building blocks this position.";
    public const string InsufficientClearance = "Not enough clearance.";
    public const string OutsideMapBounds = "Outside map bounds.";

    public static string GetReasonText(DestinationBlockReason reason) => reason switch
    {
        DestinationBlockReason.Water => Water,
        DestinationBlockReason.DenseForest => DenseForest,
        DestinationBlockReason.Building => Building,
        DestinationBlockReason.InsufficientClearance => InsufficientClearance,
        DestinationBlockReason.OutsideMapBounds => OutsideMapBounds,
        _ => string.Empty
    };
}

public readonly record struct DestinationEvaluationResult(
    DestinationValidity Validity,
    DestinationBlockReason Reason,
    Vector2 Target,
    Vector2 AdjustedTarget,
    uint TargetSurface,
    float SurfaceHeight)
{
    public bool IsUsable => Validity is DestinationValidity.Valid or DestinationValidity.Adjusted;
    public string ReasonText => DestinationBlockReasons.GetReasonText(Reason);
}

/// <summary>
/// Lightweight, allocation-free evaluation of destination validity and snapping adjustments
/// for units across static terrain, grounded obstacles, terrain obstacles, and bridges.
/// Operates without HierarchicalNavGraph or Godot dependencies.
/// </summary>
public sealed class DestinationEvaluator
{
    public const float DefaultSpatialBucketSize = 16.0f;
    public const float GeometryTolerance = 0.05f;

    public float MapWidth { get; }
    public float MapDepth { get; }
    public float CellSize { get; }
    public int Columns { get; }
    public int Rows { get; }

    private readonly NavigationGrid.GroundedObstacle[] _obstacles;
    private readonly BridgeStructure[] _bridges;
    private readonly TerrainObstacle[] _terrainObstacles;
    private readonly HeightMap? _heightMap;

    private readonly int _bucketCols;
    private readonly int _bucketRows;
    private readonly int[] _bucketOffsets;
    private readonly int[] _bucketCounts;
    private readonly int[] _bucketObstacleIndices;

    private readonly int[] _obstacleQueryStamp;
    private int _currentQueryStamp;

    public int GroundedObstacleCount => _obstacles.Length;
    public ReadOnlySpan<NavigationGrid.GroundedObstacle> GroundedObstacles => _obstacles;
    public ReadOnlySpan<BridgeStructure> Bridges => _bridges;
    public ReadOnlySpan<TerrainObstacle> TerrainObstacles => _terrainObstacles;

    public DestinationEvaluator(StaticGeometry geometry, float cellSize = NavigationTuning.DefaultCellSize)
        : this(
            (geometry ?? throw new ArgumentNullException(nameof(geometry))).HeightMap.Width,
            geometry.HeightMap.Depth,
            FilterGroundedStructures(geometry.Structures),
            geometry.Bridges is { Count: > 0 } ? geometry.Bridges.ToArray() : [],
            geometry.TerrainObstacles is { Count: > 0 } ? geometry.TerrainObstacles.ToArray() : [],
            cellSize,
            geometry.HeightMap)
    {
    }

    public DestinationEvaluator(
        float mapWidth,
        float mapDepth,
        ReadOnlySpan<OpaqueStructure> structures,
        ReadOnlySpan<BridgeStructure> bridges,
        ReadOnlySpan<TerrainObstacle> terrainObstacles,
        float cellSize = NavigationTuning.DefaultCellSize,
        HeightMap? heightMap = null)
        : this(
            mapWidth,
            mapDepth,
            FilterGroundedStructures(structures),
            bridges.ToArray(),
            terrainObstacles.ToArray(),
            cellSize,
            heightMap)
    {
    }

    public DestinationEvaluator(
        float mapWidth,
        float mapDepth,
        NavigationGrid.GroundedObstacle[] obstacles,
        BridgeStructure[] bridges,
        TerrainObstacle[] terrainObstacles,
        float cellSize = NavigationTuning.DefaultCellSize,
        HeightMap? heightMap = null)
    {
        if (!float.IsFinite(mapWidth) || mapWidth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(mapWidth), "Map width must be positive and finite.");
        if (!float.IsFinite(mapDepth) || mapDepth <= 0f)
            throw new ArgumentOutOfRangeException(nameof(mapDepth), "Map depth must be positive and finite.");
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive and finite.");

        MapWidth = mapWidth;
        MapDepth = mapDepth;
        CellSize = cellSize;
        Columns = Math.Max(1, (int)MathF.Ceiling(MapWidth / cellSize));
        Rows = Math.Max(1, (int)MathF.Ceiling(MapDepth / cellSize));

        _obstacles = obstacles ?? [];
        _bridges = bridges ?? [];
        _terrainObstacles = terrainObstacles ?? [];
        _heightMap = heightMap;

        _bucketCols = Math.Max(1, (int)MathF.Ceiling(MapWidth / DefaultSpatialBucketSize));
        _bucketRows = Math.Max(1, (int)MathF.Ceiling(MapDepth / DefaultSpatialBucketSize));
        int totalBuckets = _bucketCols * _bucketRows;

        int[] tempCounts = new int[totalBuckets];
        for (int i = 0; i < _obstacles.Length; i++)
        {
            NavigationGrid.GroundedObstacle obs = _obstacles[i];
            int minBx = Math.Clamp((int)(obs.MinX / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int maxBx = Math.Clamp((int)(obs.MaxX / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int minBz = Math.Clamp((int)(obs.MinZ / DefaultSpatialBucketSize), 0, _bucketRows - 1);
            int maxBz = Math.Clamp((int)(obs.MaxZ / DefaultSpatialBucketSize), 0, _bucketRows - 1);

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
        int totalEntries = 0;
        for (int b = 0; b < totalBuckets; b++)
        {
            _bucketOffsets[b] = totalEntries;
            totalEntries += tempCounts[b];
        }

        _bucketObstacleIndices = new int[totalEntries];
        int[] currentFill = new int[totalBuckets];

        for (int i = 0; i < _obstacles.Length; i++)
        {
            NavigationGrid.GroundedObstacle obs = _obstacles[i];
            int minBx = Math.Clamp((int)(obs.MinX / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int maxBx = Math.Clamp((int)(obs.MaxX / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int minBz = Math.Clamp((int)(obs.MinZ / DefaultSpatialBucketSize), 0, _bucketRows - 1);
            int maxBz = Math.Clamp((int)(obs.MaxZ / DefaultSpatialBucketSize), 0, _bucketRows - 1);

            for (int bz = minBz; bz <= maxBz; bz++)
            {
                int row = bz * _bucketCols;
                for (int bx = minBx; bx <= maxBx; bx++)
                {
                    int b = row + bx;
                    int pos = _bucketOffsets[b] + currentFill[b]++;
                    _bucketObstacleIndices[pos] = i;
                    _bucketCounts[b]++;
                }
            }
        }

        _obstacleQueryStamp = new int[_obstacles.Length];
        _currentQueryStamp = 0;
    }

    private static NavigationGrid.GroundedObstacle[] FilterGroundedStructures(IReadOnlyList<OpaqueStructure> structures)
    {
        if (structures is null || structures.Count == 0) return [];
        int count = 0;
        for (int i = 0; i < structures.Count; i++)
        {
            if (structures[i].IsGrounded) count++;
        }
        NavigationGrid.GroundedObstacle[] result = new NavigationGrid.GroundedObstacle[count];
        int idx = 0;
        for (int i = 0; i < structures.Count; i++)
        {
            OpaqueStructure st = structures[i];
            if (st.IsGrounded)
            {
                result[idx++] = new NavigationGrid.GroundedObstacle(st.MinX, st.MinZ, st.MaxX, st.MaxZ);
            }
        }
        return result;
    }

    private static NavigationGrid.GroundedObstacle[] FilterGroundedStructures(ReadOnlySpan<OpaqueStructure> structures)
    {
        int count = 0;
        for (int i = 0; i < structures.Length; i++)
        {
            if (structures[i].IsGrounded) count++;
        }
        NavigationGrid.GroundedObstacle[] result = new NavigationGrid.GroundedObstacle[count];
        int idx = 0;
        for (int i = 0; i < structures.Length; i++)
        {
            ref readonly OpaqueStructure st = ref structures[i];
            if (st.IsGrounded)
            {
                result[idx++] = new NavigationGrid.GroundedObstacle(st.MinX, st.MinZ, st.MaxX, st.MaxZ);
            }
        }
        return result;
    }

    public float GetSurfaceHeight(Vector2 position, uint supportSurface = 0)
    {
        if (supportSurface > 0 && TryGetBridge(supportSurface, out BridgeStructure bridge))
        {
            if (bridge.Contains(position))
            {
                return bridge.GetHeight(position);
            }
        }
        return _heightMap?.GetHeight(position.X, position.Y) ?? 0f;
    }

    public bool TryGetBridge(uint id, out BridgeStructure bridge)
    {
        for (int i = 0; i < _bridges.Length; i++)
        {
            if (_bridges[i].Id.Value == id)
            {
                bridge = _bridges[i];
                return true;
            }
        }
        bridge = null!;
        return false;
    }

    public bool TryGetBridge(StructureId id, out BridgeStructure bridge) =>
        TryGetBridge((uint)id.Value, out bridge);

    public DestinationEvaluationResult Evaluate(
        Vector2 target,
        float radius,
        TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle,
        uint targetSurface = 0,
        Vector2? start = null,
        float maxSnapDistance = 3.0f)
    {
        float effectiveRadius = MathF.Max(0f, radius - GeometryTolerance);
        bool inBounds = target.X >= effectiveRadius && target.X <= MapWidth - effectiveRadius &&
                        target.Y >= effectiveRadius && target.Y <= MapDepth - effectiveRadius;

        if (!inBounds)
        {
            bool strictlyOutside = target.X < 0f || target.X > MapWidth || target.Y < 0f || target.Y > MapDepth;
            if (strictlyOutside)
            {
                return new DestinationEvaluationResult(
                    DestinationValidity.Blocked,
                    DestinationBlockReason.OutsideMapBounds,
                    target,
                    target,
                    targetSurface,
                    GetSurfaceHeight(target, targetSurface));
            }

            // Target is near map boundary; attempt snapping inward
            if (TrySnapDestination(start ?? target, target, radius, capabilities, targetSurface, maxSnapDistance, out Vector2 snapped))
            {
                return new DestinationEvaluationResult(
                    DestinationValidity.Adjusted,
                    DestinationBlockReason.OutsideMapBounds,
                    target,
                    snapped,
                    targetSurface,
                    GetSurfaceHeight(snapped, targetSurface));
            }

            return new DestinationEvaluationResult(
                DestinationValidity.Blocked,
                DestinationBlockReason.OutsideMapBounds,
                target,
                target,
                targetSurface,
                GetSurfaceHeight(target, targetSurface));
        }

        if (IsPointClear(target, radius, capabilities, targetSurface))
        {
            return new DestinationEvaluationResult(
                DestinationValidity.Valid,
                DestinationBlockReason.None,
                target,
                target,
                targetSurface,
                GetSurfaceHeight(target, targetSurface));
        }

        DestinationBlockReason reason = DiagnoseBlockReason(target, radius, capabilities, targetSurface);

        if (TrySnapDestination(start ?? target, target, radius, capabilities, targetSurface, maxSnapDistance, out Vector2 snappedTarget))
        {
            return new DestinationEvaluationResult(
                DestinationValidity.Adjusted,
                reason,
                target,
                snappedTarget,
                targetSurface,
                GetSurfaceHeight(snappedTarget, targetSurface));
        }

        return new DestinationEvaluationResult(
            DestinationValidity.Blocked,
            reason,
            target,
            target,
            targetSurface,
            GetSurfaceHeight(target, targetSurface));
    }

    public DestinationBlockReason DiagnoseBlockReason(
        Vector2 point,
        float radius,
        TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle,
        uint supportSurface = 0)
    {
        float effectiveRadius = MathF.Max(0f, radius - GeometryTolerance);

        if (point.X < 0f || point.X > MapWidth || point.Y < 0f || point.Y > MapDepth)
        {
            return DestinationBlockReason.OutsideMapBounds;
        }

        if (point.X < effectiveRadius || point.X > MapWidth - effectiveRadius ||
            point.Y < effectiveRadius || point.Y > MapDepth - effectiveRadius)
        {
            return DestinationBlockReason.OutsideMapBounds;
        }

        // Check if inside grounded building
        if (_obstacles.Length > 0)
        {
            int minBx = Math.Clamp((int)(point.X / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int maxBx = Math.Clamp((int)(point.X / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int minBz = Math.Clamp((int)(point.Y / DefaultSpatialBucketSize), 0, _bucketRows - 1);
            int maxBz = Math.Clamp((int)(point.Y / DefaultSpatialBucketSize), 0, _bucketRows - 1);

            int b = minBz * _bucketCols + minBx;
            int startIdx = _bucketOffsets[b];
            int count = _bucketCounts[b];
            for (int i = 0; i < count; i++)
            {
                int obsIdx = _bucketObstacleIndices[startIdx + i];
                ref readonly NavigationGrid.GroundedObstacle obs = ref _obstacles[obsIdx];
                if (point.X >= obs.MinX && point.X <= obs.MaxX && point.Y >= obs.MinZ && point.Y <= obs.MaxZ)
                {
                    return DestinationBlockReason.Building;
                }
            }
        }

        // Check bridge coverage
        bool bridgeCovered = false;
        if (supportSurface > 0 && TryGetBridge(supportSurface, out BridgeStructure bridgeOnDeck))
        {
            if ((capabilities & TraversabilityCapabilities.Bridge) == 0)
            {
                return DestinationBlockReason.InsufficientClearance;
            }

            if (bridgeOnDeck.Contains(point))
            {
                if (bridgeOnDeck.GetSideClearance(point) < effectiveRadius)
                {
                    return DestinationBlockReason.InsufficientClearance;
                }
                bridgeCovered = true;
            }
            else
            {
                return DestinationBlockReason.InsufficientClearance;
            }
        }

        // Check terrain obstacles (water, forest)
        if (!bridgeCovered)
        {
            for (int t = 0; t < _terrainObstacles.Length; t++)
            {
                TerrainObstacle obs = _terrainObstacles[t];
                if (obs.Blocks(capabilities))
                {
                    if (obs.Contains(point))
                    {
                        if ((obs.RequiredCapability & TraversabilityCapabilities.Water) != 0)
                            return DestinationBlockReason.Water;
                        if ((obs.RequiredCapability & TraversabilityCapabilities.DenseForest) != 0)
                            return DestinationBlockReason.DenseForest;
                        return DestinationBlockReason.InsufficientClearance;
                    }
                }
            }

            // Check if within effective radius of terrain obstacles
            for (int t = 0; t < _terrainObstacles.Length; t++)
            {
                TerrainObstacle obs = _terrainObstacles[t];
                if (obs.Blocks(capabilities))
                {
                    if (obs.DistanceTo(point) < effectiveRadius)
                    {
                        if ((obs.RequiredCapability & TraversabilityCapabilities.Water) != 0)
                            return DestinationBlockReason.Water;
                        if ((obs.RequiredCapability & TraversabilityCapabilities.DenseForest) != 0)
                            return DestinationBlockReason.DenseForest;
                        return DestinationBlockReason.InsufficientClearance;
                    }
                }
            }
        }

        // Check clearance to buildings
        if (_obstacles.Length > 0)
        {
            float searchRadius = MathF.Min(effectiveRadius, 50.0f);
            int minBx = Math.Clamp((int)((point.X - searchRadius) / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int maxBx = Math.Clamp((int)((point.X + searchRadius) / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int minBz = Math.Clamp((int)((point.Y - searchRadius) / DefaultSpatialBucketSize), 0, _bucketRows - 1);
            int maxBz = Math.Clamp((int)((point.Y + searchRadius) / DefaultSpatialBucketSize), 0, _bucketRows - 1);

            float radiusSq = effectiveRadius * effectiveRadius;
            for (int bz = minBz; bz <= maxBz; bz++)
            {
                int row = bz * _bucketCols;
                for (int bx = minBx; bx <= maxBx; bx++)
                {
                    int b = row + bx;
                    int startIdx = _bucketOffsets[b];
                    int bCount = _bucketCounts[b];
                    for (int i = 0; i < bCount; i++)
                    {
                        int obsIdx = _bucketObstacleIndices[startIdx + i];
                        ref readonly NavigationGrid.GroundedObstacle obs = ref _obstacles[obsIdx];
                        float dx = MathF.Max(0f, MathF.Max(obs.MinX - point.X, point.X - obs.MaxX));
                        float dz = MathF.Max(0f, MathF.Max(obs.MinZ - point.Y, point.Y - obs.MaxZ));
                        if (dx * dx + dz * dz < radiusSq)
                        {
                            return DestinationBlockReason.InsufficientClearance;
                        }
                    }
                }
            }
        }

        return DestinationBlockReason.InsufficientClearance;
    }

    public bool IsPointClear(
        Vector2 point,
        float radius,
        TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle,
        uint supportSurface = 0)
    {
        float effectiveRadius = MathF.Max(0f, radius - GeometryTolerance);
        if (point.X < effectiveRadius || point.X > MapWidth - effectiveRadius ||
            point.Y < effectiveRadius || point.Y > MapDepth - effectiveRadius)
        {
            return false;
        }

        if (_obstacles.Length > 0)
        {
            int minBx = Math.Clamp((int)((point.X - effectiveRadius) / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int maxBx = Math.Clamp((int)((point.X + effectiveRadius) / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int minBz = Math.Clamp((int)((point.Y - effectiveRadius) / DefaultSpatialBucketSize), 0, _bucketRows - 1);
            int maxBz = Math.Clamp((int)((point.Y + effectiveRadius) / DefaultSpatialBucketSize), 0, _bucketRows - 1);

            float radiusSq = effectiveRadius * effectiveRadius;
            for (int bz = minBz; bz <= maxBz; bz++)
            {
                int row = bz * _bucketCols;
                for (int bx = minBx; bx <= maxBx; bx++)
                {
                    int b = row + bx;
                    int startIdx = _bucketOffsets[b];
                    int bCount = _bucketCounts[b];
                    for (int i = 0; i < bCount; i++)
                    {
                        int obsIdx = _bucketObstacleIndices[startIdx + i];
                        ref readonly NavigationGrid.GroundedObstacle obs = ref _obstacles[obsIdx];
                        if (point.X >= obs.MinX && point.X <= obs.MaxX && point.Y >= obs.MinZ && point.Y <= obs.MaxZ)
                            return false;

                        float dx = MathF.Max(0f, MathF.Max(obs.MinX - point.X, point.X - obs.MaxX));
                        float dz = MathF.Max(0f, MathF.Max(obs.MinZ - point.Y, point.Y - obs.MaxZ));
                        if (dx * dx + dz * dz < radiusSq)
                            return false;
                    }
                }
            }
        }

        // Bridge check
        bool bridgeCovered = false;
        if (supportSurface > 0 && TryGetBridge(supportSurface, out BridgeStructure bridgeOnDeck))
        {
            if ((capabilities & TraversabilityCapabilities.Bridge) == 0)
                return false;

            if (bridgeOnDeck.Contains(point))
            {
                if (bridgeOnDeck.GetSideClearance(point) < effectiveRadius)
                    return false;
                bridgeCovered = true;
            }
        }

        // Terrain obstacles
        if (!bridgeCovered)
        {
            for (int t = 0; t < _terrainObstacles.Length; t++)
            {
                TerrainObstacle obs = _terrainObstacles[t];
                if (obs.Blocks(capabilities))
                {
                    if (obs.DistanceTo(point) < effectiveRadius)
                        return false;
                }
            }
        }

        return true;
    }

    public bool TrySnapDestination(
        Vector2 start,
        Vector2 target,
        float radius,
        TraversabilityCapabilities capabilities,
        uint targetSurface,
        float maxSnapDistance,
        out Vector2 snapped)
    {
        float maxSnapDistanceSq = maxSnapDistance * maxSnapDistance;
        int centerCol = Math.Clamp((int)(target.X / CellSize), 0, Columns - 1);
        int centerRow = Math.Clamp((int)(target.Y / CellSize), 0, Rows - 1);
        int cellWindow = (int)MathF.Ceiling(maxSnapDistance / CellSize);

        float bestScore = float.MaxValue;
        Vector2 bestPoint = target;
        bool found = false;

        for (int dr = -cellWindow; dr <= cellWindow; dr++)
        {
            int r = centerRow + dr;
            if (r < 0 || r >= Rows) continue;

            for (int dc = -cellWindow; dc <= cellWindow; dc++)
            {
                int c = centerCol + dc;
                if (c < 0 || c >= Columns) continue;

                Vector2 candidate = new((c + 0.5f) * CellSize, (r + 0.5f) * CellSize);
                float distSq = Vector2.DistanceSquared(target, candidate);
                if (distSq > maxSnapDistanceSq) continue;

                if (!IsPointClear(candidate, radius, capabilities, targetSurface)) continue;

                float score = distSq;
                if (!IsSegmentClear(start, candidate, radius, capabilities, targetSurface)) score += 4.0f;
                score += 0.001f * Vector2.DistanceSquared(start, candidate);

                if (score < bestScore - 1e-4f)
                {
                    bestScore = score;
                    bestPoint = candidate;
                    found = true;
                }
            }
        }

        snapped = bestPoint;
        return found;
    }

    public bool IsSegmentClear(
        Vector2 start,
        Vector2 end,
        float radius,
        TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle,
        uint supportSurface = 0)
    {
        float effectiveRadius = MathF.Max(0f, radius - GeometryTolerance);

        if (MathF.Min(start.X, end.X) < effectiveRadius ||
            MathF.Max(start.X, end.X) > MapWidth - effectiveRadius ||
            MathF.Min(start.Y, end.Y) < effectiveRadius ||
            MathF.Max(start.Y, end.Y) > MapDepth - effectiveRadius)
        {
            return false;
        }

        if (_obstacles.Length > 0)
        {
            float segMinX = MathF.Min(start.X, end.X) - effectiveRadius;
            float segMaxX = MathF.Max(start.X, end.X) + effectiveRadius;
            float segMinZ = MathF.Min(start.Y, end.Y) - effectiveRadius;
            float segMaxZ = MathF.Max(start.Y, end.Y) + effectiveRadius;

            int minBx = Math.Clamp((int)(segMinX / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int maxBx = Math.Clamp((int)(segMaxX / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int minBz = Math.Clamp((int)(segMinZ / DefaultSpatialBucketSize), 0, _bucketRows - 1);
            int maxBz = Math.Clamp((int)(segMaxZ / DefaultSpatialBucketSize), 0, _bucketRows - 1);

            if (minBx == maxBx && minBz == maxBz)
            {
                int b = minBz * _bucketCols + minBx;
                int startIdx = _bucketOffsets[b];
                int count = _bucketCounts[b];
                for (int i = 0; i < count; i++)
                {
                    int obsIdx = _bucketObstacleIndices[startIdx + i];
                    ref readonly NavigationGrid.GroundedObstacle obs = ref _obstacles[obsIdx];
                    if (segMaxX < obs.MinX || segMinX > obs.MaxX || segMaxZ < obs.MinZ || segMinZ > obs.MaxZ)
                        continue;

                    if (SegmentIntersectsExpandedObstacle(start, end, obs, effectiveRadius))
                        return false;
                }
            }
            else
            {
                _currentQueryStamp++;
                if (_currentQueryStamp == int.MaxValue)
                {
                    Array.Clear(_obstacleQueryStamp);
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
                            int obsIdx = _bucketObstacleIndices[startIdx + i];
                            if (_obstacleQueryStamp[obsIdx] == _currentQueryStamp) continue;
                            _obstacleQueryStamp[obsIdx] = _currentQueryStamp;

                            ref readonly NavigationGrid.GroundedObstacle obs = ref _obstacles[obsIdx];
                            if (segMaxX < obs.MinX || segMinX > obs.MaxX || segMaxZ < obs.MinZ || segMinZ > obs.MaxZ)
                                continue;

                            if (SegmentIntersectsExpandedObstacle(start, end, obs, effectiveRadius))
                                return false;
                        }
                    }
                }
            }
        }

        // Bridge check
        bool bridgeCovered = false;
        if ((capabilities & TraversabilityCapabilities.Bridge) != 0 && _bridges.Length > 0)
        {
            if (supportSurface > 0)
            {
                if (TryGetBridge(supportSurface, out BridgeStructure bridgeOnDeck))
                {
                    bool startOnBridge = bridgeOnDeck.Contains(start);
                    bool endOnBridge = bridgeOnDeck.Contains(end);

                    if (startOnBridge && endOnBridge)
                    {
                        if (bridgeOnDeck.GetSideClearance(start) < effectiveRadius ||
                            bridgeOnDeck.GetSideClearance(end) < effectiveRadius)
                        {
                            return false;
                        }
                        bridgeCovered = true;
                    }
                    else if (startOnBridge != endOnBridge)
                    {
                        float threshold = MathF.Max(6.0f, bridgeOnDeck.Width * 0.5f + 1.0f);
                        Vector2 bridgePoint = startOnBridge ? start : end;
                        Vector2 landPoint = startOnBridge ? end : start;
                        if (!bridgeOnDeck.IsNearEntry(bridgePoint, threshold) || !bridgeOnDeck.IsNearEntry(landPoint, threshold))
                        {
                            return false;
                        }
                        if (endOnBridge && bridgeOnDeck.GetSideClearance(end) < effectiveRadius)
                        {
                            return false;
                        }
                        bridgeCovered = true;
                    }
                }
            }
            else
            {
                for (int b = 0; b < _bridges.Length; b++)
                {
                    BridgeStructure bridge = _bridges[b];
                    bool startOnBridge = bridge.Contains(start);
                    bool endOnBridge = bridge.Contains(end);

                    if (startOnBridge != endOnBridge)
                    {
                        float threshold = MathF.Max(6.0f, bridge.Width * 0.5f + 1.0f);
                        Vector2 bridgePoint = startOnBridge ? start : end;
                        Vector2 landPoint = startOnBridge ? end : start;

                        if (bridge.IsNearEntry(bridgePoint, threshold) && bridge.IsNearEntry(landPoint, threshold))
                        {
                            if (endOnBridge && bridge.GetSideClearance(end) < effectiveRadius)
                            {
                                return false;
                            }
                            bridgeCovered = true;
                            break;
                        }
                    }
                }
            }
        }

        // Terrain obstacles
        if (!bridgeCovered && _terrainObstacles.Length > 0)
        {
            for (int t = 0; t < _terrainObstacles.Length; t++)
            {
                TerrainObstacle obs = _terrainObstacles[t];
                if (obs.Blocks(capabilities))
                {
                    if (SegmentIntersectsExpandedBox(start, end, obs.MinX, obs.MinZ, obs.MaxX, obs.MaxZ, effectiveRadius))
                        return false;
                }
            }
        }

        return true;
    }

    public float ComputePointClearance(
        Vector2 point,
        TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle,
        uint supportSurface = 0)
    {
        float edgeDist = MathF.Min(MathF.Min(point.X, MapWidth - point.X), MathF.Min(point.Y, MapDepth - point.Y));
        if (edgeDist <= 0f) return 0f;

        float clearance = edgeDist;

        if (_obstacles.Length > 0)
        {
            float searchRadius = MathF.Min(edgeDist, 50.0f);
            int minBx = Math.Clamp((int)((point.X - searchRadius) / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int maxBx = Math.Clamp((int)((point.X + searchRadius) / DefaultSpatialBucketSize), 0, _bucketCols - 1);
            int minBz = Math.Clamp((int)((point.Y - searchRadius) / DefaultSpatialBucketSize), 0, _bucketRows - 1);
            int maxBz = Math.Clamp((int)((point.Y + searchRadius) / DefaultSpatialBucketSize), 0, _bucketRows - 1);

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
                        int obsIdx = _bucketObstacleIndices[startIdx + i];
                        ref readonly NavigationGrid.GroundedObstacle obs = ref _obstacles[obsIdx];
                        if (point.X >= obs.MinX && point.X <= obs.MaxX && point.Y >= obs.MinZ && point.Y <= obs.MaxZ)
                            return 0f;

                        float dx = MathF.Max(0f, MathF.Max(obs.MinX - point.X, point.X - obs.MaxX));
                        float dz = MathF.Max(0f, MathF.Max(obs.MinZ - point.Y, point.Y - obs.MaxZ));
                        float dist = MathF.Sqrt(dx * dx + dz * dz);
                        if (dist < clearance)
                        {
                            clearance = dist;
                        }
                    }
                }
            }
        }

        // Bridge check
        bool bridgeCovered = false;
        if (supportSurface > 0 && TryGetBridge(supportSurface, out BridgeStructure bridgeOnDeck))
        {
            if (bridgeOnDeck.Contains(point))
            {
                float sc = bridgeOnDeck.GetSideClearance(point);
                clearance = MathF.Min(clearance, sc);
                bridgeCovered = true;
            }
        }

        // Terrain obstacles
        if (!bridgeCovered)
        {
            for (int t = 0; t < _terrainObstacles.Length; t++)
            {
                TerrainObstacle obs = _terrainObstacles[t];
                if (obs.Blocks(capabilities))
                {
                    if (obs.Contains(point)) return 0f;
                    float dist = obs.DistanceTo(point);
                    if (dist < clearance) clearance = dist;
                }
            }
        }

        return MathF.Max(0f, clearance);
    }

    public bool HasObstaclesInBox(float minX, float minZ, float maxX, float maxZ)
    {
        if (_obstacles.Length == 0) return false;
        int minBx = Math.Clamp((int)(minX / DefaultSpatialBucketSize), 0, _bucketCols - 1);
        int maxBx = Math.Clamp((int)(maxX / DefaultSpatialBucketSize), 0, _bucketCols - 1);
        int minBz = Math.Clamp((int)(minZ / DefaultSpatialBucketSize), 0, _bucketRows - 1);
        int maxBz = Math.Clamp((int)(maxZ / DefaultSpatialBucketSize), 0, _bucketRows - 1);

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
                    int obsIdx = _bucketObstacleIndices[start + i];
                    ref readonly NavigationGrid.GroundedObstacle obs = ref _obstacles[obsIdx];
                    if (obs.MaxX >= minX && obs.MinX <= maxX && obs.MaxZ >= minZ && obs.MinZ <= maxZ)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public bool HasTerrainObstaclesInBox(float minX, float minZ, float maxX, float maxZ, TraversabilityCapabilities capabilities)
    {
        for (int i = 0; i < _terrainObstacles.Length; i++)
        {
            TerrainObstacle obs = _terrainObstacles[i];
            if (obs.Blocks(capabilities) &&
                obs.MaxX >= minX && obs.MinX <= maxX &&
                obs.MaxZ >= minZ && obs.MinZ <= maxZ)
            {
                return true;
            }
        }
        return false;
    }

    public bool HasBridgesInBox(float minX, float minZ, float maxX, float maxZ)
    {
        for (int i = 0; i < _bridges.Length; i++)
        {
            BridgeStructure b = _bridges[i];
            if (b.MaxX >= minX && b.MinX <= maxX &&
                b.MaxZ >= minZ && b.MinZ <= maxZ)
            {
                return true;
            }
        }
        return false;
    }

    public int QueryObstacles(float minX, float minZ, float maxX, float maxZ, Span<int> results)
    {
        if (_obstacles.Length == 0 || results.Length == 0) return 0;
        int minBx = Math.Clamp((int)(minX / DefaultSpatialBucketSize), 0, _bucketCols - 1);
        int maxBx = Math.Clamp((int)(maxX / DefaultSpatialBucketSize), 0, _bucketCols - 1);
        int minBz = Math.Clamp((int)(minZ / DefaultSpatialBucketSize), 0, _bucketRows - 1);
        int maxBz = Math.Clamp((int)(maxZ / DefaultSpatialBucketSize), 0, _bucketRows - 1);

        _currentQueryStamp++;
        if (_currentQueryStamp == int.MaxValue)
        {
            Array.Clear(_obstacleQueryStamp);
            _currentQueryStamp = 1;
        }

        int count = 0;
        for (int bz = minBz; bz <= maxBz; bz++)
        {
            int row = bz * _bucketCols;
            for (int bx = minBx; bx <= maxBx; bx++)
            {
                int b = row + bx;
                int start = _bucketOffsets[b];
                int bCount = _bucketCounts[b];
                for (int i = 0; i < bCount; i++)
                {
                    int obsIdx = _bucketObstacleIndices[start + i];
                    if (_obstacleQueryStamp[obsIdx] == _currentQueryStamp) continue;
                    _obstacleQueryStamp[obsIdx] = _currentQueryStamp;

                    ref readonly NavigationGrid.GroundedObstacle obs = ref _obstacles[obsIdx];
                    if (obs.MaxX >= minX && obs.MinX <= maxX && obs.MaxZ >= minZ && obs.MinZ <= maxZ)
                    {
                        results[count++] = obsIdx;
                        if (count == results.Length) return count;
                    }
                }
            }
        }
        return count;
    }

    private static bool SegmentIntersectsExpandedObstacle(Vector2 start, Vector2 end, in NavigationGrid.GroundedObstacle obs, float radius) =>
        SegmentIntersectsExpandedBox(start, end, obs.MinX, obs.MinZ, obs.MaxX, obs.MaxZ, radius);

    private static bool SegmentIntersectsExpandedBox(Vector2 start, Vector2 end, float minX, float minZ, float maxX, float maxZ, float radius)
    {
        if (SegmentIntersectsAabb(start, end, minX - radius, maxX + radius, minZ, maxZ))
            return true;

        if (SegmentIntersectsAabb(start, end, minX, maxX, minZ - radius, maxZ + radius))
            return true;

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

        if (MathF.Abs(dx) < 1e-9f)
        {
            if (start.X < minX || start.X > maxX) return false;
        }
        else
        {
            float invDx = 1f / dx;
            float t1 = (minX - start.X) * invDx;
            float t2 = (maxX - start.X) * invDx;
            if (t1 > t2) (t1, t2) = (t2, t1);
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax) return false;
        }

        if (MathF.Abs(dz) < 1e-9f)
        {
            if (start.Y < minZ || start.Y > maxZ) return false;
        }
        else
        {
            float invDz = 1f / dz;
            float t1 = (minZ - start.Y) * invDz;
            float t2 = (maxZ - start.Y) * invDz;
            if (t1 > t2) (t1, t2) = (t2, t1);
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
        if (a < 1e-9f)
        {
            return Vector2.DistanceSquared(start, center) <= radius * radius;
        }

        float b = 2f * Vector2.Dot(f, d);
        float c = Vector2.Dot(f, f) - radius * radius;

        float discriminant = b * b - 4f * a * c;
        if (discriminant < 0f) return false;

        float sqrtD = MathF.Sqrt(discriminant);
        float t1 = (-b - sqrtD) / (2f * a);
        float t2 = (-b + sqrtD) / (2f * a);

        return (t1 >= 0f && t1 <= 1f) || (t2 >= 0f && t2 <= 1f) || (t1 < 0f && t2 > 1f);
    }
}
