using System.Numerics;

namespace DeadGround.Engine;

public sealed class HeightMap
{
    private readonly ushort[] _samples;
    private readonly float[] _cellMaxHeights;
    private readonly float[] _blockMaxHeights;
    public int Columns { get; }
    public int Rows { get; }
    public float CellSize { get; }
    public float HeightScale { get; }
    public float MaxHeight { get; }
    public float MinHeight { get; }
    public float Width => (Columns - 1) * CellSize;
    public float Depth => (Rows - 1) * CellSize;
    public int BlockCells { get; }
    public float BlockSize { get; }
    public int BlockColumns { get; }
    public int BlockRows { get; }

    public HeightMap(int columns, int rows, float cellSize, float heightScale, ReadOnlySpan<ushort> samples)
    {
        if (columns < 2 || rows < 2 || !float.IsFinite(cellSize) || cellSize <= 0 ||
            !float.IsFinite(heightScale) || heightScale <= 0 || samples.Length != checked(columns * rows))
            throw new ArgumentException("Invalid heightmap dimensions or samples.");
        Columns = columns;
        Rows = rows;
        CellSize = cellSize;
        HeightScale = heightScale;
        _samples = samples.ToArray();

        ushort minSample = ushort.MaxValue;
        ushort maxSample = ushort.MinValue;
        for (int i = 0; i < samples.Length; i++)
        {
            ushort s = samples[i];
            if (s < minSample) minSample = s;
            if (s > maxSample) maxSample = s;
        }
        MinHeight = minSample * heightScale;
        MaxHeight = maxSample * heightScale;

        _cellMaxHeights = new float[(columns - 1) * (rows - 1)];
        for (int r = 0; r < rows - 1; r++)
        {
            int rowOffset = r * (columns - 1);
            int sampleRowOffset = r * columns;
            int nextSampleRowOffset = (r + 1) * columns;
            for (int c = 0; c < columns - 1; c++)
            {
                ushort s00 = samples[sampleRowOffset + c];
                ushort s10 = samples[sampleRowOffset + c + 1];
                ushort s01 = samples[nextSampleRowOffset + c];
                ushort s11 = samples[nextSampleRowOffset + c + 1];
                ushort maxS = Math.Max(Math.Max(s00, s10), Math.Max(s01, s11));
                _cellMaxHeights[rowOffset + c] = maxS * heightScale;
            }
        }

        BlockCells = Math.Max(1, Math.Min(8, (columns - 1) / 4));
        BlockSize = BlockCells * CellSize;
        BlockColumns = (columns - 2) / BlockCells + 1;
        BlockRows = (rows - 2) / BlockCells + 1;
        _blockMaxHeights = new float[BlockColumns * BlockRows];

        for (int br = 0; br < BlockRows; br++)
        {
            int rStart = br * BlockCells;
            int rEnd = Math.Min(rows - 1, rStart + BlockCells);
            for (int bc = 0; bc < BlockColumns; bc++)
            {
                int cStart = bc * BlockCells;
                int cEnd = Math.Min(columns - 1, cStart + BlockCells);
                float maxH = float.MinValue;
                for (int r = rStart; r < rEnd; r++)
                {
                    int rowOff = r * (columns - 1);
                    for (int c = cStart; c < cEnd; c++)
                    {
                        float ch = _cellMaxHeights[rowOff + c];
                        if (ch > maxH) maxH = ch;
                    }
                }
                _blockMaxHeights[br * BlockColumns + bc] = maxH;
            }
        }
    }

    public bool Contains(Vector2 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) &&
        point.X >= 0 && point.Y >= 0 && point.X <= Width && point.Y <= Depth;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal float SampleHeight(int column, int row) => _samples[row * Columns + column] * HeightScale;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal void GetCellCornerHeights(int column, int row, out float h00, out float h10, out float h01, out float h11)
    {
        int rowStride = Columns;
        int idx = row * rowStride + column;
        float scale = HeightScale;
        ReadOnlySpan<ushort> samples = _samples;
        h00 = samples[idx] * scale;
        h10 = samples[idx + 1] * scale;
        h01 = samples[idx + rowStride] * scale;
        h11 = samples[idx + rowStride + 1] * scale;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal float GetCellMaxHeight(int column, int row) => _cellMaxHeights[row * (Columns - 1) + column];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal float GetCellMaxHeightByIndex(int index) => _cellMaxHeights[index];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal float GetBlockMaxHeight(int blockCol, int blockRow) => _blockMaxHeights[blockRow * BlockColumns + blockCol];

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public float GetHeight(float x, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z))
            throw new ArgumentOutOfRangeException(nameof(x));
        float gx = Math.Clamp(x / CellSize, 0, Columns - 1);
        float gz = Math.Clamp(z / CellSize, 0, Rows - 1);
        int ix = Math.Min((int)gx, Columns - 2);
        int iz = Math.Min((int)gz, Rows - 2);
        float tx = gx - ix;
        float tz = gz - iz;
        float a = _samples[iz * Columns + ix];
        float b = _samples[iz * Columns + ix + 1];
        float c = _samples[(iz + 1) * Columns + ix];
        float d = _samples[(iz + 1) * Columns + ix + 1];
        return ((a + (b - a) * tx) * (1 - tz) + (c + (d - c) * tx) * tz) * HeightScale;
    }

    // Client picking against the same immutable heightmap used by the simulation.
    public bool TryRaycast(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 hit)
    {
        hit = default;
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z) ||
            !float.IsFinite(maxDistance) || maxDistance <= 0 ||
            !float.IsFinite(direction.LengthSquared()) || direction.LengthSquared() < 0.0001f)
            return false;
        direction = Vector3.Normalize(direction);
        double entry = 0, exit = maxDistance;
        if (!ClipAxis(origin.X, direction.X, Width, ref entry, ref exit) ||
            !ClipAxis(origin.Z, direction.Z, Depth, ref entry, ref exit)) return false;
        double t = entry;
        Vector3 previous = origin + direction * (float)t;
        float previousHeight = previous.Y - GetHeight(previous.X, previous.Z);
        if (previousHeight == 0) { hit = previous; return true; }
        while (t < exit)
        {
            double next = Math.Min(t + CellSize * 0.5d, exit);
            Vector3 p = origin + direction * (float)next;
            float height = p.Y - GetHeight(p.X, p.Z);
            if (height <= 0 && previousHeight > 0)
            {
                double lo = t, hi = next;
                for (int i = 0; i < 16; i++)
                {
                    double mid = (lo + hi) * 0.5d;
                    Vector3 q = origin + direction * (float)mid;
                    if (q.Y > GetHeight(q.X, q.Z)) lo = mid;
                    else hi = mid;
                }
                hit = origin + direction * (float)((lo + hi) * 0.5d);
                return true;
            }
            previousHeight = height;
            t = next;
        }
        return false;
    }

    private static bool ClipAxis(float origin, float direction, float size, ref double entry, ref double exit)
    {
        if (direction == 0) return origin >= 0 && origin <= size;
        double a = -origin / (double)direction, b = (size - origin) / (double)direction;
        entry = Math.Max(entry, Math.Min(a, b));
        exit = Math.Min(exit, Math.Max(a, b));
        return entry <= exit;
    }

    public static HeightMap CreatePrototype()
    {
        const int size = 65;
        ushort[] heights = new ushort[size * size];
        for (int z = 0; z < size; z++)
            for (int x = 0; x < size; x++)
            {
                float dx = x * 2 - 86, dz = z * 2 - 44;
                float h = MathF.Max(0, 1 - (dx * dx + dz * dz) / 1200) * 9;
                dx = x * 2 - 34; dz = z * 2 - 100;
                h += MathF.Max(0, 1 - (dx * dx + dz * dz) / 650) * 5;
                heights[z * size + x] = (ushort)MathF.Round(h * 100);
            }
        return new HeightMap(size, size, 2, 0.01f, heights);
    }
}
