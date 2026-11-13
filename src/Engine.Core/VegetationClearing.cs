using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Identifies the shape of a vegetation clearing.
/// </summary>
public enum VegetationClearingKind : byte
{
    Polygon = 1,
    Corridor = 2,
    Box = 3
}

/// <summary>
/// Immutable region within which vegetation optical density is zero.
/// Used for roads, yards, clearings, and cutouts where trees do not grow.
/// Allocation-free containment checks.
/// </summary>
public sealed class VegetationClearing
{
    public string Id { get; }
    public VegetationClearingKind Kind { get; }
    public float MinX { get; }
    public float MaxX { get; }
    public float MinZ { get; }
    public float MaxZ { get; }

    public Vector2[]? Polygon { get; }
    public Vector2 SegmentA { get; }
    public Vector2 SegmentB { get; }
    public float Radius { get; }

    private readonly float _radiusSq;

    private VegetationClearing(
        string id,
        VegetationClearingKind kind,
        float minX,
        float maxX,
        float minZ,
        float maxZ,
        Vector2[]? polygon,
        Vector2 segmentA,
        Vector2 segmentB,
        float radius)
    {
        Id = id;
        Kind = kind;
        MinX = minX;
        MaxX = maxX;
        MinZ = minZ;
        MaxZ = maxZ;
        Polygon = polygon;
        SegmentA = segmentA;
        SegmentB = segmentB;
        Radius = radius;
        _radiusSq = radius * radius;
    }

    public static VegetationClearing CreatePolygon(string id, ReadOnlySpan<Vector2> polygon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (polygon.Length < 3)
            throw new ArgumentException("Clearing polygon must contain at least 3 vertices.", nameof(polygon));

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        Vector2[] polyCopy = new Vector2[polygon.Length];

        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 p = polygon[i];
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
                throw new ArgumentException($"Clearing polygon vertex {i} has invalid coordinates ({p.X}, {p.Y}).");

            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minZ) minZ = p.Y;
            if (p.Y > maxZ) maxZ = p.Y;
            polyCopy[i] = p;
        }

        return new VegetationClearing(
            id,
            VegetationClearingKind.Polygon,
            minX, maxX, minZ, maxZ,
            polyCopy,
            default, default, 0f);
    }

    public static VegetationClearing CreateBox(string id, float minX, float minZ, float maxX, float maxZ)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!float.IsFinite(minX) || !float.IsFinite(maxX) || !float.IsFinite(minZ) || !float.IsFinite(maxZ))
            throw new ArgumentException("Clearing box coordinates must be finite values.");

        if (minX > maxX) (minX, maxX) = (maxX, minX);
        if (minZ > maxZ) (minZ, maxZ) = (maxZ, minZ);

        return new VegetationClearing(
            id,
            VegetationClearingKind.Box,
            minX, maxX, minZ, maxZ,
            null,
            default, default, 0f);
    }

    public static VegetationClearing CreateCorridor(string id, Vector2 a, Vector2 b, float radius)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!float.IsFinite(a.X) || !float.IsFinite(a.Y) || !float.IsFinite(b.X) || !float.IsFinite(b.Y))
            throw new ArgumentException("Corridor endpoints must be finite values.");
        if (!float.IsFinite(radius) || radius <= 0f)
            throw new ArgumentOutOfRangeException(nameof(radius), "Corridor radius must be positive and finite.");

        float minX = MathF.Min(a.X, b.X) - radius;
        float maxX = MathF.Max(a.X, b.X) + radius;
        float minZ = MathF.Min(a.Y, b.Y) - radius;
        float maxZ = MathF.Max(a.Y, b.Y) + radius;

        return new VegetationClearing(
            id,
            VegetationClearingKind.Corridor,
            minX, maxX, minZ, maxZ,
            null,
            a, b, radius);
    }

    public bool ContainsPoint(float x, float z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ)
            return false;

        return Kind switch
        {
            VegetationClearingKind.Box => true,
            VegetationClearingKind.Polygon => VegetationZone.IsPointInPolygon(new Vector2(x, z), Polygon!),
            VegetationClearingKind.Corridor => DistanceToSegmentSquared(new Vector2(x, z), SegmentA, SegmentB) <= _radiusSq,
            _ => false
        };
    }

    private static float DistanceToSegmentSquared(Vector2 pt, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.X * ab.X + ab.Y * ab.Y;
        if (lenSq < 1e-6f)
        {
            Vector2 d = pt - a;
            return d.X * d.X + d.Y * d.Y;
        }

        float t = Math.Clamp(((pt.X - a.X) * ab.X + (pt.Y - a.Y) * ab.Y) / lenSq, 0f, 1f);
        Vector2 proj = a + t * ab;
        Vector2 diff = pt - proj;
        return diff.X * diff.X + diff.Y * diff.Y;
    }
}
