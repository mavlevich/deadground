using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Allocation-free uniform 2D spatial grid for indexing vehicle positions and querying nearby candidates for separation.
/// </summary>
public sealed class VehicleSpatialGrid
{
    private readonly float _cellSize;
    private readonly float _invCellSize;
    private readonly int _columns;
    private readonly int _rows;
    private readonly int[] _heads;
    private int[] _next;
    private Vector2[] _positions;

    public float CellSize => _cellSize;
    public int Columns => _columns;
    public int Rows => _rows;

    public Vector2 GetPosition(int unitIndex) => _positions[unitIndex];

    public int QueryCount { get; private set; }
    public int CandidatesChecked { get; private set; }
    public int BufferOverflows { get; private set; }

    public VehicleSpatialGrid(float mapWidth, float mapDepth, int maxUnits = 1024, float cellSize = 16.0f)
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
        _positions = new Vector2[maxUnits];
        Array.Fill(_heads, -1);
    }

    private void EnsureCapacity(int requiredUnits)
    {
        if (requiredUnits > _next.Length)
        {
            int newCap = Math.Max(requiredUnits, _next.Length * 2);
            Array.Resize(ref _next, newCap);
            Array.Resize(ref _positions, newCap);
        }
    }

    /// <summary>
    /// Re-indexes all unit positions from WorldState without heap allocations.
    /// Only units with EffectiveCollisionRadius > 0 participate in the spatial collision index.
    /// </summary>
    public void Build(WorldState world, IUnitMovementCatalog catalog)
    {
        Array.Fill(_heads, -1);
        int count = world.Count;
        EnsureCapacity(count);

        for (int i = 0; i < count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            _positions[i] = unit.Position;

            if (!catalog.TryGetProfile(unit.Definition, out UnitMovementProfile profile) || profile.EffectiveCollisionRadius <= 0f)
            {
                _next[i] = -1;
                continue;
            }

            int cx = Math.Clamp((int)(unit.Position.X * _invCellSize), 0, _columns - 1);
            int cz = Math.Clamp((int)(unit.Position.Y * _invCellSize), 0, _rows - 1);
            int cellIndex = cz * _columns + cx;

            _next[i] = _heads[cellIndex];
            _heads[cellIndex] = i;
        }
    }

    /// <summary>
    /// Queries neighbor unit indices within a given horizontal radius of a query position.
    /// Resizes the buffer if necessary so no candidates are dropped.
    /// </summary>
    public int QueryNeighbors(Vector2 position, float queryRadius, ref int[] resultIndices)
    {
        QueryCount++;
        if (queryRadius <= 0) return 0;

        float radiusSq = queryRadius * queryRadius;
        int minCx = Math.Clamp((int)((position.X - queryRadius) * _invCellSize), 0, _columns - 1);
        int maxCx = Math.Clamp((int)((position.X + queryRadius) * _invCellSize), 0, _columns - 1);
        int minCz = Math.Clamp((int)((position.Y - queryRadius) * _invCellSize), 0, _rows - 1);
        int maxCz = Math.Clamp((int)((position.Y + queryRadius) * _invCellSize), 0, _rows - 1);

        int written = 0;

        for (int cz = minCz; cz <= maxCz; cz++)
        {
            int rowOffset = cz * _columns;
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                int unitIndex = _heads[rowOffset + cx];
                while (unitIndex != -1)
                {
                    CandidatesChecked++;
                    Vector2 unitPos = _positions[unitIndex];
                    if (Vector2.DistanceSquared(position, unitPos) <= radiusSq)
                    {
                        if (written < resultIndices.Length)
                        {
                            resultIndices[written++] = unitIndex;
                        }
                        else
                        {
                            BufferOverflows++;
                        }
                    }
                    unitIndex = _next[unitIndex];
                }
            }
        }

        return written;
    }

    /// <summary>
    /// Queries neighbor unit indices within a given horizontal radius of a query position.
    /// Returns the number of found neighbors written to resultIndices.
    /// </summary>
    public int QueryNeighbors(Vector2 position, float queryRadius, Span<int> resultIndices)
    {
        QueryCount++;
        if (queryRadius <= 0) return 0;

        float radiusSq = queryRadius * queryRadius;
        int minCx = Math.Clamp((int)((position.X - queryRadius) * _invCellSize), 0, _columns - 1);
        int maxCx = Math.Clamp((int)((position.X + queryRadius) * _invCellSize), 0, _columns - 1);
        int minCz = Math.Clamp((int)((position.Y - queryRadius) * _invCellSize), 0, _rows - 1);
        int maxCz = Math.Clamp((int)((position.Y + queryRadius) * _invCellSize), 0, _rows - 1);

        int written = 0;
        int bufferLimit = resultIndices.Length;

        for (int cz = minCz; cz <= maxCz; cz++)
        {
            int rowOffset = cz * _columns;
            for (int cx = minCx; cx <= maxCx; cx++)
            {
                int unitIndex = _heads[rowOffset + cx];
                while (unitIndex != -1)
                {
                    CandidatesChecked++;
                    Vector2 unitPos = _positions[unitIndex];
                    if (Vector2.DistanceSquared(position, unitPos) <= radiusSq)
                    {
                        if (written < bufferLimit)
                        {
                            resultIndices[written++] = unitIndex;
                        }
                        else
                        {
                            BufferOverflows++;
                            return written;
                        }
                    }
                    unitIndex = _next[unitIndex];
                }
            }
        }

        return written;
    }

    public void ResetMetrics()
    {
        QueryCount = 0;
        CandidatesChecked = 0;
        BufferOverflows = 0;
    }
}
