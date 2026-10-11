using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Allocation-free 2D uniform spatial grid for indexing units during perception queries.
/// Rebuilt deterministically during detection ticks to guarantee movement and removal
/// are immediately reflected without stale index drift.
/// </summary>
public sealed class UnitSpatialGrid
{
    private readonly float _cellSize;
    private readonly float _invCellSize;
    private readonly int _columns;
    private readonly int _rows;
    private readonly int[] _heads;
    private readonly int[] _next;

    public float CellSize => _cellSize;
    public int Columns => _columns;
    public int Rows => _rows;

    public UnitSpatialGrid(float mapWidth, float mapDepth, int maxUnits = 1024, float cellSize = 32.0f)
    {
        if (!float.IsFinite(mapWidth) || mapWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(mapWidth));
        if (!float.IsFinite(mapDepth) || mapDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(mapDepth));
        if (!float.IsFinite(cellSize) || cellSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (maxUnits <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxUnits));

        _cellSize = cellSize;
        _invCellSize = 1.0f / cellSize;
        _columns = Math.Max(1, (int)MathF.Ceiling(mapWidth * _invCellSize));
        _rows = Math.Max(1, (int)MathF.Ceiling(mapDepth * _invCellSize));

        int totalCells = checked(_columns * _rows);
        _heads = new int[totalCells];
        _next = new int[maxUnits];
        Array.Fill(_heads, -1);
    }

    /// <summary>
    /// Re-indexes all unit positions from contiguous memory without heap allocations.
    /// </summary>
    public void Build(ReadOnlySpan<Vector2> positions)
    {
        Array.Fill(_heads, -1);
        for (int i = 0; i < positions.Length; i++)
        {
            Vector2 pos = positions[i];
            int cx = Math.Clamp((int)(pos.X * _invCellSize), 0, _columns - 1);
            int cz = Math.Clamp((int)(pos.Y * _invCellSize), 0, _rows - 1);
            int cellIndex = cz * _columns + cx;

            _next[i] = _heads[cellIndex];
            _heads[cellIndex] = i;
        }
    }

    /// <summary>
    /// Re-indexes all current units in the world without heap allocations.
    /// </summary>
    public void Build(WorldState world)
    {
        Array.Fill(_heads, -1);
        int count = world.Count;

        for (int i = 0; i < count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            int cx = Math.Clamp((int)(unit.Position.X * _invCellSize), 0, _columns - 1);
            int cz = Math.Clamp((int)(unit.Position.Y * _invCellSize), 0, _rows - 1);
            int cellIndex = cz * _columns + cx;

            _next[i] = _heads[cellIndex];
            _heads[cellIndex] = i;
        }
    }

    /// <summary>
    /// Queries hostile unit indices from flat contiguous arrays within a horizontal radius of an observer position.
    /// </summary>
    public int QueryHostileCandidates(
        Vector2 position,
        float radius,
        int observerTeam,
        ReadOnlySpan<Vector2> positions,
        ReadOnlySpan<int> teams,
        Span<int> candidateIndices)
    {
        if (radius <= 0) return 0;

        float radiusSq = radius * radius;
        int minCx = Math.Clamp((int)((position.X - radius) * _invCellSize), 0, _columns - 1);
        int maxCx = Math.Clamp((int)((position.X + radius) * _invCellSize), 0, _columns - 1);
        int minCz = Math.Clamp((int)((position.Y - radius) * _invCellSize), 0, _rows - 1);
        int maxCz = Math.Clamp((int)((position.Y + radius) * _invCellSize), 0, _rows - 1);

        int written = 0;

        for (int cz = minCz; cz <= maxCz; cz++)
        {
            int rowOffset = cz * _columns;
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                int cursor = _heads[rowOffset + cx];
                while (cursor != -1)
                {
                    // Filter: only hostile units (different team)
                    if (teams[cursor] != observerTeam)
                    {
                        float distSq = Vector2.DistanceSquared(position, positions[cursor]);
                        if (distSq <= radiusSq)
                        {
                            if (written < candidateIndices.Length)
                            {
                                candidateIndices[written++] = cursor;
                            }
                        }
                    }
                    cursor = _next[cursor];
                }
            }
        }

        return written;
    }

    /// <summary>
    /// Queries hostile unit indices within a horizontal radius of an observer position.
    /// Writes unit indices into the caller-provided buffer without allocating memory.
    /// </summary>
    public int QueryHostileCandidates(
        Vector2 position,
        float radius,
        int observerOwner,
        WorldState world,
        Span<int> candidateIndices)
    {
        if (radius <= 0) return 0;

        float radiusSq = radius * radius;
        int minCx = Math.Clamp((int)((position.X - radius) * _invCellSize), 0, _columns - 1);
        int maxCx = Math.Clamp((int)((position.X + radius) * _invCellSize), 0, _columns - 1);
        int minCz = Math.Clamp((int)((position.Y - radius) * _invCellSize), 0, _rows - 1);
        int maxCz = Math.Clamp((int)((position.Y + radius) * _invCellSize), 0, _rows - 1);

        int written = 0;

        for (int cz = minCz; cz <= maxCz; cz++)
        {
            int rowOffset = cz * _columns;
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                int cursor = _heads[rowOffset + cx];
                while (cursor != -1)
                {
                    UnitState unit = world.GetUnitAt(cursor);
                    // Filter: only hostile units (different team)
                    if (world.AreHostile(observerOwner, unit.Owner))
                    {
                        float distSq = Vector2.DistanceSquared(position, unit.Position);
                        if (distSq <= radiusSq)
                        {
                            if (written < candidateIndices.Length)
                            {
                                candidateIndices[written++] = cursor;
                            }
                        }
                    }
                    cursor = _next[cursor];
                }
            }
        }

        return written;
    }
}
