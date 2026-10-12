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

    private readonly GroundedObstacle[] _obstacles;
    private readonly float[] _clearance;

    public const int MaxSupportedCells = 262_144;

    public float MapWidth { get; }
    public float MapDepth { get; }
    public float CellSize { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int TotalCells => Columns * Rows;
    public int GroundedObstacleCount => _obstacles.Length;

    public readonly record struct GroundedObstacle(float MinX, float MinZ, float MaxX, float MaxZ);

    public NavigationGrid(StaticGeometry geometry, float cellSize = NavigationTuning.DefaultCellSize)
        : this(
            (geometry ?? throw new ArgumentNullException(nameof(geometry))).HeightMap,
            geometry.Structures is { Count: > 0 } ? GetStructuresArray(geometry) : [],
            cellSize)
    {
    }

    public NavigationGrid(HeightMap heightMap, ReadOnlySpan<OpaqueStructure> structures, float cellSize = NavigationTuning.DefaultCellSize)
    {
        ArgumentNullException.ThrowIfNull(heightMap);
        if (!float.IsFinite(cellSize) || cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive and finite.");

        MapWidth = heightMap.Width;
        MapDepth = heightMap.Depth;
        CellSize = cellSize;
        Columns = Math.Max(1, (int)MathF.Ceiling(MapWidth / cellSize));
        Rows = Math.Max(1, (int)MathF.Ceiling(MapDepth / cellSize));

        if ((long)Columns * Rows > MaxSupportedCells)
        {
            throw new InvalidOperationException(
                $"NavigationGrid of {Columns}x{Rows} ({Columns * Rows} cells) exceeds flat prototype limit of {MaxSupportedCells} cells. Large maps require hierarchical navigation.");
        }

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

        // Build precomputed clearance field for grid cell centers
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
        return _clearance[row * Columns + col];
    }

    public bool IsCellPassable(int col, int row, float radius)
    {
        if (!IsInBounds(col, row)) return false;
        return _clearance[row * Columns + col] >= radius;
    }

    public bool IsPointClear(Vector2 point, float radius)
    {
        float effectiveRadius = radius - GeometryTolerance;
        if (point.X < effectiveRadius || point.X > MapWidth - effectiveRadius ||
            point.Y < effectiveRadius || point.Y > MapDepth - effectiveRadius)
        {
            return false;
        }

        for (int i = 0; i < _obstacles.Length; i++)
        {
            GroundedObstacle obs = _obstacles[i];
            if (point.X >= obs.MinX && point.X <= obs.MaxX && point.Y >= obs.MinZ && point.Y <= obs.MaxZ)
                return false;

            float dx = MathF.Max(0f, MathF.Max(obs.MinX - point.X, point.X - obs.MaxX));
            float dz = MathF.Max(0f, MathF.Max(obs.MinZ - point.Y, point.Y - obs.MaxZ));
            float distSq = dx * dx + dz * dz;
            if (distSq < effectiveRadius * effectiveRadius)
                return false;
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

        float segMinX = MathF.Min(start.X, end.X) - effectiveRadius;
        float segMaxX = MathF.Max(start.X, end.X) + effectiveRadius;
        float segMinZ = MathF.Min(start.Y, end.Y) - effectiveRadius;
        float segMaxZ = MathF.Max(start.Y, end.Y) + effectiveRadius;

        for (int i = 0; i < _obstacles.Length; i++)
        {
            GroundedObstacle obs = _obstacles[i];

            // Broad-phase AABB rejection
            if (segMaxX < obs.MinX || segMinX > obs.MaxX || segMaxZ < obs.MinZ || segMinZ > obs.MaxZ)
                continue;

            // Detailed continuous Minkowski sum check:
            // The region of points within distance < effectiveRadius of an AABB is the union of:
            // 1. Box expanded in X
            // 2. Box expanded in Z
            // 3. Four corner circles
            if (SegmentIntersectsExpandedObstacle(start, end, obs, effectiveRadius))
                return false;
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
