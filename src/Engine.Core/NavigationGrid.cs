using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Precomputed 2D passability grid and continuous clearance field built from static map geometry.
/// Only grounded structures (IsGrounded == true) block ground navigation.
/// Provides continuous swept-capsule safety checks preventing corner-cutting and wall tunneling.
/// </summary>
public sealed class NavigationGrid
{
    private const float GeometryTolerance = 1e-4f;
    private const float SpatialBucketSize = 32.0f;

    private readonly GroundedObstacle[] _obstacles;
    private readonly float[] _clearance;
    private readonly HierarchicalNavGraph[] _hierarchicalGraphs;

    // 2D spatial bucket partitioning for fast O(1) obstacle queries
    private readonly int _bucketCols;
    private readonly int _bucketRows;
    private readonly int[] _bucketOffsets;
    private readonly int[] _bucketCounts;
    private readonly int[] _bucketObstacleIndices;
    private readonly int[] _obstacleQueryStamp;
    private int _currentQueryStamp;
    private readonly int[] _windowObstacleBuffer;

    public const int MaxSupportedCells = 262_144;

    public float MapWidth { get; }
    public float MapDepth { get; }
    public float CellSize { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int TotalCells => Columns * Rows;
    public int GroundedObstacleCount => _obstacles.Length;
    public ReadOnlySpan<GroundedObstacle> GroundedObstacles => _obstacles;

    // Owned array payload, including all movement graphs, excluding object headers and build scratch.
    public long EstimatedMemoryBytes
    {
        get
        {
            long bytes = (long)_obstacles.Length * 16 + (long)_clearance.Length * 4 +
                (long)(_bucketOffsets.Length + _bucketCounts.Length + _bucketObstacleIndices.Length +
                       _obstacleQueryStamp.Length + _windowObstacleBuffer.Length) * 4 +
                (long)_hierarchicalGraphs.Length * IntPtr.Size;
            for (int i = 0; i < _hierarchicalGraphs.Length; i++) bytes += _hierarchicalGraphs[i].EstimatedMemoryBytes;
            return bytes;
        }
    }

    public readonly record struct GroundedObstacle(float MinX, float MinZ, float MaxX, float MaxZ);

    public NavigationGrid(
        StaticGeometry geometry,
        float cellSize = NavigationTuning.DefaultCellSize,
        bool enableHierarchical = false,
        NavigationBuildLimits? buildLimits = null,
        float sectorSize = NavigationTuning.DefaultSectorSize)
        : this(
            (geometry ?? throw new ArgumentNullException(nameof(geometry))).HeightMap,
            geometry.Structures is { Count: > 0 } ? GetStructuresArray(geometry) : [],
            cellSize,
            enableHierarchical,
            buildLimits,
            sectorSize)
    {
    }

    public NavigationGrid(
        HeightMap heightMap,
        ReadOnlySpan<OpaqueStructure> structures,
        float cellSize = NavigationTuning.DefaultCellSize,
        bool enableHierarchical = false,
        NavigationBuildLimits? buildLimits = null,
        float sectorSize = NavigationTuning.DefaultSectorSize)
    {
        ArgumentNullException.ThrowIfNull(heightMap);
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive and finite.");

        if (!float.IsFinite(sectorSize) || sectorSize < 10f)
            throw new ArgumentOutOfRangeException(nameof(sectorSize), "Sector size must be finite and at least 10 m.");
        buildLimits ??= new NavigationBuildLimits();
        buildLimits.Validate();
        buildLimits.CheckSectors(heightMap.Width, heightMap.Depth, sectorSize);
        double fineCols = Math.Ceiling((double)heightMap.Width / cellSize);
        double fineRows = Math.Ceiling((double)heightMap.Depth / cellSize);
        if (!double.IsFinite(fineCols * fineRows) || fineCols * fineRows > int.MaxValue)
            throw new InvalidOperationException("Navigation fine-grid dimensions exceed supported cell indexing.");

        MapWidth = heightMap.Width;
        MapDepth = heightMap.Depth;
        CellSize = cellSize;
        Columns = Math.Max(1, (int)MathF.Ceiling(MapWidth / cellSize));
        Rows = Math.Max(1, (int)MathF.Ceiling(MapDepth / cellSize));

        if (!enableHierarchical && (long)Columns * Rows > MaxSupportedCells)
        {
            throw new InvalidOperationException(
                $"NavigationGrid of {Columns}x{Rows} ({Columns * Rows} cells) exceeds flat prototype limit of {MaxSupportedCells} cells. Large maps require hierarchical navigation.");
        }

        bool isFlatSupported = (long)Columns * Rows <= MaxSupportedCells;


        // Filter only grounded structures
        int groundedCount = 0;
        for (int i = 0; i < structures.Length; i++)
        {
            if (structures[i].IsGrounded) groundedCount++;
        }

        _obstacles = new GroundedObstacle[groundedCount];
        int writeIdx = 0;
        for (int i = 0; i < structures.Length; i++)
        {
            OpaqueStructure s = structures[i];
            if (!s.IsGrounded) continue;
            _obstacles[writeIdx++] = new GroundedObstacle(s.MinX, s.MinZ, s.MaxX, s.MaxZ);
        }

        if (isFlatSupported)
        {
            // Build precomputed clearance field for grid cell centers on bounded prototype maps
            _clearance = new float[Columns * Rows];
            for (int r = 0; r < Rows; r++)
            {
                float cz = (r + 0.5f) * CellSize;
                for (int c = 0; c < Columns; c++)
                {
                    float cx = (c + 0.5f) * CellSize;

                    // Distance to map boundaries
                    float edgeDist = MathF.Min(MathF.Min(cx, MapWidth - cx), MathF.Min(cz, MapDepth - cz));
                    if (edgeDist <= 0f)
                    {
                        _clearance[r * Columns + c] = 0f;
                        continue;
                    }

                    float cellClearance = edgeDist;
                    for (int o = 0; o < _obstacles.Length; o++)
                    {
                        GroundedObstacle obs = _obstacles[o];
                        if (cx >= obs.MinX && cx <= obs.MaxX && cz >= obs.MinZ && cz <= obs.MaxZ)
                        {
                            cellClearance = 0f;
                            break;
                        }

                        float dx = MathF.Max(0f, MathF.Max(obs.MinX - cx, cx - obs.MaxX));
                        float dz = MathF.Max(0f, MathF.Max(obs.MinZ - cz, cz - obs.MaxZ));
                        float dist = MathF.Sqrt(dx * dx + dz * dz);
                        if (dist < cellClearance) cellClearance = dist;
                    }

                    _clearance[r * Columns + c] = MathF.Max(0f, cellClearance);
                }
            }
        }
        else
        {
            _clearance = [];
        }

        // Build 2D spatial bucket grid for fast O(1) obstacle queries
        _bucketCols = Math.Max(1, (int)MathF.Ceiling(MapWidth / SpatialBucketSize));
        _bucketRows = Math.Max(1, (int)MathF.Ceiling(MapDepth / SpatialBucketSize));
        int totalBuckets = _bucketCols * _bucketRows;

        int[] tempCounts = new int[totalBuckets];
        for (int i = 0; i < _obstacles.Length; i++)
        {
            GroundedObstacle obs = _obstacles[i];
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
            GroundedObstacle obs = _obstacles[i];
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
                    int pos = _bucketOffsets[b] + currentFill[b]++;
                    _bucketObstacleIndices[pos] = i;
                    _bucketCounts[b]++;
                }
            }
        }

        _obstacleQueryStamp = new int[_obstacles.Length];
        _currentQueryStamp = 0;
        _windowObstacleBuffer = new int[_obstacles.Length];

        _hierarchicalGraphs = new HierarchicalNavGraph[4];
        _hierarchicalGraphs[(int)MovementClass.Foot] = new HierarchicalNavGraph(this, MovementClass.Foot, sectorSize: sectorSize, cellSize: CellSize, buildLimits: buildLimits);
        _hierarchicalGraphs[(int)MovementClass.Wheeled] = new HierarchicalNavGraph(this, MovementClass.Wheeled, sectorSize: sectorSize, cellSize: CellSize, buildLimits: buildLimits);
        _hierarchicalGraphs[(int)MovementClass.Medium] = new HierarchicalNavGraph(this, MovementClass.Medium, sectorSize: sectorSize, cellSize: CellSize, buildLimits: buildLimits);
        _hierarchicalGraphs[(int)MovementClass.Tracked] = new HierarchicalNavGraph(this, MovementClass.Tracked, sectorSize: sectorSize, cellSize: CellSize, buildLimits: buildLimits);
    }

    public bool IsFlatGrid => _clearance.Length > 0;
    public HierarchicalNavGraph HierarchicalGraph => GetHierarchicalGraph(MovementClass.Tracked);

    public HierarchicalNavGraph GetHierarchicalGraph(MovementClass movementClass) =>
        _hierarchicalGraphs[(int)movementClass];

    public HierarchicalNavGraph GetHierarchicalGraph(float radius) =>
        GetHierarchicalGraph(MovementClassExtensions.FromRadius(radius));


    private static OpaqueStructure[] GetStructuresArray(StaticGeometry geometry)
    {
        OpaqueStructure[] arr = new OpaqueStructure[geometry.Structures.Count];
        for (int i = 0; i < arr.Length; i++) arr[i] = geometry.Structures[i];
        return arr;
    }

    public bool IsInBounds(int col, int row) =>
        (uint)col < (uint)Columns && (uint)row < (uint)Rows;

    public int CellIndex(int col, int row) => row * Columns + col;

    public (int Col, int Row) CellCoordinates(int cellIndex) => (cellIndex % Columns, cellIndex / Columns);

    public Vector2 CellCenter(int col, int row) => new((col + 0.5f) * CellSize, (row + 0.5f) * CellSize);

    public Vector2 CellCenter(int cellIndex) => CellCenter(cellIndex % Columns, cellIndex / Columns);

    public (int Col, int Row) WorldToCell(Vector2 position)
    {
        int col = Math.Clamp((int)(position.X / CellSize), 0, Columns - 1);
        int row = Math.Clamp((int)(position.Y / CellSize), 0, Rows - 1);
        return (col, row);
    }

    public float GetCellClearance(int col, int row)
    {
        if (!IsInBounds(col, row)) return 0f;
        if (IsFlatGrid) return _clearance[row * Columns + col];
        return ComputePointClearance(CellCenter(col, row));
    }

    public bool IsCellPassable(int col, int row, float radius)
    {
        if (!IsInBounds(col, row)) return false;
        if (IsFlatGrid) return _clearance[row * Columns + col] >= radius;
        return IsPointClear(CellCenter(col, row), radius);
    }

    public bool HasObstaclesInBox(float minX, float minZ, float maxX, float maxZ)
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
                    int obsIdx = _bucketObstacleIndices[start + i];
                    ref readonly GroundedObstacle obs = ref _obstacles[obsIdx];
                    if (obs.MaxX >= minX && obs.MinX <= maxX && obs.MaxZ >= minZ && obs.MinZ <= maxZ)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    public int QueryObstacles(float minX, float minZ, float maxX, float maxZ, Span<int> results)
    {
        if (_obstacles.Length == 0 || results.Length == 0) return 0;
        int minBx = Math.Clamp((int)(minX / SpatialBucketSize), 0, _bucketCols - 1);
        int maxBx = Math.Clamp((int)(maxX / SpatialBucketSize), 0, _bucketCols - 1);
        int minBz = Math.Clamp((int)(minZ / SpatialBucketSize), 0, _bucketRows - 1);
        int maxBz = Math.Clamp((int)(maxZ / SpatialBucketSize), 0, _bucketRows - 1);

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

                    ref readonly GroundedObstacle obs = ref _obstacles[obsIdx];
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

    public float ComputePointClearance(Vector2 point)
    {
        float edgeDist = MathF.Min(MathF.Min(point.X, MapWidth - point.X), MathF.Min(point.Y, MapDepth - point.Y));
        if (edgeDist <= 0f) return 0f;
        if (_obstacles.Length == 0) return edgeDist;

        float searchRadius = MathF.Min(edgeDist, 50.0f);
        int minBx = Math.Clamp((int)((point.X - searchRadius) / SpatialBucketSize), 0, _bucketCols - 1);
        int maxBx = Math.Clamp((int)((point.X + searchRadius) / SpatialBucketSize), 0, _bucketCols - 1);
        int minBz = Math.Clamp((int)((point.Y - searchRadius) / SpatialBucketSize), 0, _bucketRows - 1);
        int maxBz = Math.Clamp((int)((point.Y + searchRadius) / SpatialBucketSize), 0, _bucketRows - 1);

        float clearance = searchRadius;
        bool foundAny = false;

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
                    ref readonly GroundedObstacle obs = ref _obstacles[obsIdx];
                    if (point.X >= obs.MinX && point.X <= obs.MaxX && point.Y >= obs.MinZ && point.Y <= obs.MaxZ)
                        return 0f;

                    float dx = MathF.Max(0f, MathF.Max(obs.MinX - point.X, point.X - obs.MaxX));
                    float dz = MathF.Max(0f, MathF.Max(obs.MinZ - point.Y, point.Y - obs.MaxZ));
                    float dist = MathF.Sqrt(dx * dx + dz * dz);
                    if (dist < clearance)
                    {
                        clearance = dist;
                        foundAny = true;
                    }
                }
            }
        }

        if (foundAny) return MathF.Max(0f, clearance);
        return MathF.Min(edgeDist, searchRadius);
    }

    public void PopulateWindowClearance(
        float minX, float minZ,
        int cols, int rows,
        float cellSize,
        Span<float> clearance)
    {
        float effectiveRadiusMargin = 10.0f;
        float maxX = minX + cols * cellSize;
        float maxZ = minZ + rows * cellSize;

        int overlapCount = QueryObstacles(
            minX - effectiveRadiusMargin,
            minZ - effectiveRadiusMargin,
            maxX + effectiveRadiusMargin,
            maxZ + effectiveRadiusMargin,
            _windowObstacleBuffer);

        for (int r = 0; r < rows; r++)
        {
            float cz = minZ + (r + 0.5f) * cellSize;
            for (int c = 0; c < cols; c++)
            {
                float cx = minX + (c + 0.5f) * cellSize;
                float edgeDist = MathF.Min(MathF.Min(cx, MapWidth - cx), MathF.Min(cz, MapDepth - cz));
                if (edgeDist <= 0f)
                {
                    clearance[r * cols + c] = 0f;
                    continue;
                }

                float cellClearance = edgeDist;
                for (int oi = 0; oi < overlapCount; oi++)
                {
                    ref readonly GroundedObstacle obs = ref _obstacles[_windowObstacleBuffer[oi]];
                    if (cx >= obs.MinX && cx <= obs.MaxX && cz >= obs.MinZ && cz <= obs.MaxZ)
                    {
                        cellClearance = 0f;
                        break;
                    }

                    float dx = MathF.Max(0f, MathF.Max(obs.MinX - cx, cx - obs.MaxX));
                    float dz = MathF.Max(0f, MathF.Max(obs.MinZ - cz, cz - obs.MaxZ));
                    float dist = MathF.Sqrt(dx * dx + dz * dz);
                    if (dist < cellClearance) cellClearance = dist;
                }

                clearance[r * cols + c] = MathF.Max(0f, cellClearance);
            }
        }
    }

    public bool IsPointClear(Vector2 point, float radius)
    {
        float effectiveRadius = radius - GeometryTolerance;
        if (point.X < effectiveRadius || point.X > MapWidth - effectiveRadius ||
            point.Y < effectiveRadius || point.Y > MapDepth - effectiveRadius)
        {
            return false;
        }

        if (_obstacles.Length == 0) return true;

        int minBx = Math.Clamp((int)((point.X - effectiveRadius) / SpatialBucketSize), 0, _bucketCols - 1);
        int maxBx = Math.Clamp((int)((point.X + effectiveRadius) / SpatialBucketSize), 0, _bucketCols - 1);
        int minBz = Math.Clamp((int)((point.Y - effectiveRadius) / SpatialBucketSize), 0, _bucketRows - 1);
        int maxBz = Math.Clamp((int)((point.Y + effectiveRadius) / SpatialBucketSize), 0, _bucketRows - 1);

        float radiusSq = effectiveRadius * effectiveRadius;
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
                    ref readonly GroundedObstacle obs = ref _obstacles[obsIdx];
                    if (point.X >= obs.MinX && point.X <= obs.MaxX && point.Y >= obs.MinZ && point.Y <= obs.MaxZ)
                        return false;

                    float dx = MathF.Max(0f, MathF.Max(obs.MinX - point.X, point.X - obs.MaxX));
                    float dz = MathF.Max(0f, MathF.Max(obs.MinZ - point.Y, point.Y - obs.MaxZ));
                    if (dx * dx + dz * dz < radiusSq)
                        return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Continuous geometric swept-capsule safety check between start and end with unit radius.
    /// Returns true if and only if the moving agent never penetrates map boundaries or grounded obstacles.
    /// </summary>
    public bool IsSegmentClear(Vector2 start, Vector2 end, float radius)
    {
        float effectiveRadius = MathF.Max(0f, radius - GeometryTolerance);

        // Check map boundary clearance along the straight line segment
        if (MathF.Min(start.X, end.X) < effectiveRadius ||
            MathF.Max(start.X, end.X) > MapWidth - effectiveRadius ||
            MathF.Min(start.Y, end.Y) < effectiveRadius ||
            MathF.Max(start.Y, end.Y) > MapDepth - effectiveRadius)
        {
            return false;
        }

        if (_obstacles.Length == 0) return true;

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
                int obsIdx = _bucketObstacleIndices[startIdx + i];
                ref readonly GroundedObstacle obs = ref _obstacles[obsIdx];
                if (segMaxX < obs.MinX || segMinX > obs.MaxX || segMaxZ < obs.MinZ || segMinZ > obs.MaxZ)
                    continue;

                if (SegmentIntersectsExpandedObstacle(start, end, obs, effectiveRadius))
                    return false;
            }
            return true;
        }

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

                    ref readonly GroundedObstacle obs = ref _obstacles[obsIdx];
                    if (segMaxX < obs.MinX || segMinX > obs.MaxX || segMaxZ < obs.MinZ || segMinZ > obs.MaxZ)
                        continue;

                    if (SegmentIntersectsExpandedObstacle(start, end, obs, effectiveRadius))
                        return false;
                }
            }
        }

        return true;
    }

    private static bool SegmentIntersectsExpandedObstacle(Vector2 start, Vector2 end, in GroundedObstacle obs, float radius)
    {
        // 1. Expanded rectangle in X
        if (SegmentIntersectsAabb(start, end, obs.MinX - radius, obs.MaxX + radius, obs.MinZ, obs.MaxZ))
            return true;

        // 2. Expanded rectangle in Z
        if (SegmentIntersectsAabb(start, end, obs.MinX, obs.MaxX, obs.MinZ - radius, obs.MaxZ + radius))
            return true;

        // 3. Four rounded corner discs
        if (SegmentIntersectsCircle(start, end, new Vector2(obs.MinX, obs.MinZ), radius)) return true;
        if (SegmentIntersectsCircle(start, end, new Vector2(obs.MaxX, obs.MinZ), radius)) return true;
        if (SegmentIntersectsCircle(start, end, new Vector2(obs.MinX, obs.MaxZ), radius)) return true;
        if (SegmentIntersectsCircle(start, end, new Vector2(obs.MaxX, obs.MaxZ), radius)) return true;

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

        return tMin <= tMax;
    }

    private static bool SegmentIntersectsCircle(Vector2 start, Vector2 end, Vector2 center, float radius)
    {
        Vector2 seg = end - start;
        float segLenSq = seg.LengthSquared();
        float t;
        if (segLenSq < 1e-9f)
        {
            t = 0f;
        }
        else
        {
            t = Vector2.Dot(center - start, seg) / segLenSq;
            t = Math.Clamp(t, 0f, 1f);
        }
        Vector2 closest = start + seg * t;
        return Vector2.DistanceSquared(closest, center) < radius * radius;
    }
}
