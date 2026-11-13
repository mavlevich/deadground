using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Identifies the category of a vegetation region.
/// </summary>
public enum VegetationKind : byte
{
    DenseForest = 1,
    SparseForest = 2,
    Custom = 3
}

/// <summary>
/// Immutable data-driven vegetation region definition.
/// Defines a horizontal boundary polygon, vertical canopy height above terrain,
/// and an optical attenuation density per meter.
///
/// Designed to execute 3D ray attenuation queries independent of physics engines
/// or per-tree simulation objects, with zero allocations in query hot paths.
/// </summary>
public sealed class VegetationZone
{
    public const float DefaultDenseCanopyHeight = 12.0f;
    public const float DefaultDensePenetration = 25.0f;
    public const float DefaultDenseDensity = 1.0f / DefaultDensePenetration; // 0.04 m^-1

    public const float DefaultSparseCanopyHeight = 8.0f;
    public const float DefaultSparsePenetration = 75.0f;
    public const float DefaultSparseDensity = 1.0f / DefaultSparsePenetration; // ~0.013333 m^-1

    public string Id { get; }
    public VegetationKind Kind { get; }
    public float CanopyHeight { get; }
    public float Density { get; }
    public Vector2[] Polygon { get; }
    public float MinX { get; }
    public float MaxX { get; }
    public float MinZ { get; }
    public float MaxZ { get; }

    public VegetationZone(
        string id,
        VegetationKind kind,
        ReadOnlySpan<Vector2> polygon,
        float canopyHeight,
        float density)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (polygon.Length < 3)
            throw new ArgumentException("A vegetation region polygon must contain at least 3 vertices.", nameof(polygon));
        if (!float.IsFinite(canopyHeight) || canopyHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(canopyHeight), "Canopy height must be positive and finite.");
        if (!float.IsFinite(density) || density <= 0f)
            throw new ArgumentOutOfRangeException(nameof(density), "Vegetation density must be positive and finite.");

        Id = id;
        Kind = kind;
        CanopyHeight = canopyHeight;
        Density = density;
        Polygon = polygon.ToArray();

        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;

        for (int i = 0; i < Polygon.Length; i++)
        {
            Vector2 p = Polygon[i];
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minZ) minZ = p.Y;
            if (p.Y > maxZ) maxZ = p.Y;
        }

        MinX = minX;
        MaxX = maxX;
        MinZ = minZ;
        MaxZ = maxZ;
    }

    public static VegetationZone CreateDenseForest(
        string id,
        ReadOnlySpan<Vector2> polygon,
        float canopyHeight = DefaultDenseCanopyHeight,
        float maxPenetration = DefaultDensePenetration)
    {
        if (maxPenetration <= 0f || !float.IsFinite(maxPenetration))
            throw new ArgumentOutOfRangeException(nameof(maxPenetration), "Penetration depth must be positive.");
        return new VegetationZone(id, VegetationKind.DenseForest, polygon, canopyHeight, 1.0f / maxPenetration);
    }

    public static VegetationZone CreateSparseForest(
        string id,
        ReadOnlySpan<Vector2> polygon,
        float canopyHeight = DefaultSparseCanopyHeight,
        float maxPenetration = DefaultSparsePenetration)
    {
        if (maxPenetration <= 0f || !float.IsFinite(maxPenetration))
            throw new ArgumentOutOfRangeException(nameof(maxPenetration), "Penetration depth must be positive.");
        return new VegetationZone(id, VegetationKind.SparseForest, polygon, canopyHeight, 1.0f / maxPenetration);
    }

    /// <summary>
    /// Tests whether a 2D horizontal world point (x, z) lies inside this polygon.
    /// Allocation-free parity check.
    /// </summary>
    public bool ContainsPoint(Vector2 pt)
    {
        if (pt.X < MinX || pt.X > MaxX || pt.Y < MinZ || pt.Y > MaxZ)
            return false;

        return IsPointInPolygon(pt, Polygon);
    }

    /// <summary>
    /// 2D ray-casting point in polygon parity test.
    /// </summary>
    public static bool IsPointInPolygon(Vector2 pt, ReadOnlySpan<Vector2> poly)
    {
        bool inside = false;
        int count = poly.Length;
        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Vector2 vi = poly[i];
            Vector2 vj = poly[j];
            if (((vi.Y > pt.Y) != (vj.Y > pt.Y)) &&
                (pt.X < (vj.X - vi.X) * (pt.Y - vi.Y) / (vj.Y - vi.Y) + vi.X))
            {
                inside = !inside;
            }
        }
        return inside;
    }

    /// <summary>
    /// Computes the intersection intervals [tIn, tOut] along the 2D ray segment
    /// from start to end (normalized parameter t in [0, 1]).
    /// Returns the number of written intervals in the destination span.
    /// Allocation-free.
    /// </summary>
    [ThreadStatic]
    private static float[]? t_polyBuffer;

    public int ClipRaySegment(Vector2 start, Vector2 end, Span<Vector2> intervals)
    {
        if (intervals.Length == 0)
            return 0;

        float rayMinX = MathF.Min(start.X, end.X);
        float rayMaxX = MathF.Max(start.X, end.X);
        float rayMinZ = MathF.Min(start.Y, end.Y);
        float rayMaxZ = MathF.Max(start.Y, end.Y);

        if (rayMinX > MaxX || rayMaxX < MinX || rayMinZ > MaxZ || rayMaxZ < MinZ)
            return 0;

        Vector2 d = end - start;
        float lenSq = d.X * d.X + d.Y * d.Y;

        if (lenSq < 1e-12f)
        {
            if (ContainsPoint(start))
            {
                intervals[0] = new Vector2(0f, 1f);
                return 1;
            }
            return 0;
        }

        int polyCount = Polygon.Length;
        int maxPoints = polyCount + 2;
        Span<float> ts = stackalloc float[128];
        if (maxPoints > 128)
        {
            if (t_polyBuffer is null || t_polyBuffer.Length < maxPoints)
            {
                t_polyBuffer = new float[Math.Max(maxPoints, 256)];
            }
            ts = t_polyBuffer.AsSpan(0, maxPoints);
        }
        else
        {
            ts = ts[..maxPoints];
        }
        int count = 0;

        ts[count++] = 0.0f;
        ts[count++] = 1.0f;

        for (int i = 0; i < polyCount; i++)
        {
            Vector2 v1 = Polygon[i];
            Vector2 v2 = Polygon[(i + 1) % polyCount];
            Vector2 e = v2 - v1;

            float denom = d.X * e.Y - d.Y * e.X;
            if (MathF.Abs(denom) < 1e-7f)
                continue;

            Vector2 c = v1 - start;
            float t = (c.X * e.Y - c.Y * e.X) / denom;
            float u = (c.X * d.Y - c.Y * d.X) / denom;

            if (t >= -1e-5f && t <= 1.00001f && u >= -1e-5f && u <= 1.00001f)
            {
                float clampedT = Math.Clamp(t, 0f, 1f);
                if (count < ts.Length)
                {
                    ts[count++] = clampedT;
                }
            }
        }

        // Sort candidate partition parameters ascending (insertion sort)
        for (int i = 1; i < count; i++)
        {
            float key = ts[i];
            int j = i - 1;
            while (j >= 0 && ts[j] > key)
            {
                ts[j + 1] = ts[j];
                j--;
            }
            ts[j + 1] = key;
        }

        // Midpoint interval classification:
        // For each consecutive subsegment [t_k, t_{k+1}] with non-zero length,
        // test midpoint. If inside polygon, the subsegment is inside.
        // Merge adjacent inside intervals.
        int intervalCount = 0;
        float currentStart = -1.0f;
        float currentEnd = -1.0f;

        for (int i = 0; i < count - 1; i++)
        {
            float t0 = ts[i];
            float t1 = ts[i + 1];

            if (t1 - t0 <= 1e-5f)
                continue;

            float tMid = 0.5f * (t0 + t1);
            Vector2 pMid = start + tMid * d;

            if (ContainsPoint(pMid))
            {
                if (currentStart < 0.0f)
                {
                    currentStart = t0;
                    currentEnd = t1;
                }
                else
                {
                    currentEnd = t1;
                }
            }
            else
            {
                if (currentStart >= 0.0f)
                {
                    if (intervalCount < intervals.Length)
                    {
                        intervals[intervalCount++] = new Vector2(currentStart, currentEnd);
                    }
                    currentStart = -1.0f;
                    currentEnd = -1.0f;
                }
            }
        }

        if (currentStart >= 0.0f && intervalCount < intervals.Length)
        {
            intervals[intervalCount++] = new Vector2(currentStart, currentEnd);
        }

        return intervalCount;
    }
}
