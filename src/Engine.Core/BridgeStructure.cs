using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Represents an elevated bridge structure spanning across an impassable water feature.
/// Ground units on the bridge receive SupportSurface == Id, computing height from the deck profile.
/// Units cannot enter the deck from the side or water; they must use designated bank approaches.
/// </summary>
public sealed class BridgeStructure : IEquatable<BridgeStructure>
{
    public StructureId Id { get; }
    public Vector2 Start { get; }
    public Vector2 End { get; }
    public float Width { get; }
    public float StartHeight { get; }
    public float EndHeight { get; }
    public float Thickness { get; }

    public float Length { get; }
    public Vector2 Direction { get; }
    public Vector2 Perpendicular { get; }

    public float MinX { get; }
    public float MaxX { get; }
    public float MinZ { get; }
    public float MaxZ { get; }

    public BridgeStructure(
        StructureId id,
        Vector2 start,
        Vector2 end,
        float width,
        float startHeight,
        float endHeight,
        float thickness = 0.8f)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Bridge structure ID must be positive.");
        if (!float.IsFinite(start.X) || !float.IsFinite(start.Y) || !float.IsFinite(end.X) || !float.IsFinite(end.Y))
            throw new ArgumentException("Bridge endpoints must be finite.");
        if (!float.IsFinite(width) || width <= 0f)
            throw new ArgumentOutOfRangeException(nameof(width), "Bridge width must be positive and finite.");
        if (!float.IsFinite(startHeight) || !float.IsFinite(endHeight))
            throw new ArgumentException("Bridge deck heights must be finite.");
        if (!float.IsFinite(thickness) || thickness <= 0f)
            throw new ArgumentOutOfRangeException(nameof(thickness), "Bridge thickness must be positive and finite.");

        Vector2 delta = end - start;
        float len = delta.Length();
        if (len < 0.1f)
            throw new ArgumentException("Bridge length must be at least 0.1 m.");

        Id = id;
        Start = start;
        End = end;
        Width = width;
        StartHeight = startHeight;
        EndHeight = endHeight;
        Thickness = thickness;

        Length = len;
        Direction = delta / len;
        Perpendicular = new Vector2(-Direction.Y, Direction.X);

        float halfW = width * 0.5f;
        Vector2 corner1 = start + Perpendicular * halfW;
        Vector2 corner2 = start - Perpendicular * halfW;
        Vector2 corner3 = end + Perpendicular * halfW;
        Vector2 corner4 = end - Perpendicular * halfW;

        MinX = MathF.Min(MathF.Min(corner1.X, corner2.X), MathF.Min(corner3.X, corner4.X));
        MaxX = MathF.Max(MathF.Max(corner1.X, corner2.X), MathF.Max(corner3.X, corner4.X));
        MinZ = MathF.Min(MathF.Min(corner1.Y, corner2.Y), MathF.Min(corner3.Y, corner4.Y));
        MaxZ = MathF.Max(MathF.Max(corner1.Y, corner2.Y), MathF.Max(corner3.Y, corner4.Y));
    }

    public bool Contains(Vector2 point, float tolerance = 0f)
    {
        Vector2 p = point - Start;
        float t = Vector2.Dot(p, Direction);
        if (t < -tolerance || t > Length + tolerance) return false;
        float distPerp = MathF.Abs(Vector2.Dot(p, Perpendicular));
        return distPerp <= Width * 0.5f + tolerance;
    }

    public float GetHeight(Vector2 point)
    {
        Vector2 p = point - Start;
        float t = Vector2.Dot(p, Direction);
        float fraction = Math.Clamp(t / Length, 0f, 1f);
        return StartHeight + (EndHeight - StartHeight) * fraction;
    }

    public float GetSideClearance(Vector2 point)
    {
        Vector2 p = point - Start;
        float distPerp = MathF.Abs(Vector2.Dot(p, Perpendicular));
        return MathF.Max(0f, Width * 0.5f - distPerp);
    }

    public bool ContainsInterior(Vector3 point, float tolerance = 0f)
    {
        Vector2 p = new Vector2(point.X, point.Z) - Start;
        float u = Vector2.Dot(p, Direction);
        if (u < -tolerance || u > Length + tolerance) return false;
        float distPerp = MathF.Abs(Vector2.Dot(p, Perpendicular));
        if (distPerp > Width * 0.5f + tolerance) return false;
        float topY = StartHeight + ((EndHeight - StartHeight) / Length) * u;
        float bottomY = topY - Thickness;
        return point.Y >= bottomY - tolerance && point.Y <= topY + tolerance;
    }

    public bool TryIntersect(Vector3 start, Vector3 delta, float totalDistance, out float hitT)
    {
        hitT = 1.0f;
        float tNear = 0.0f;
        float tFar = 1.0f;
        float slope = (EndHeight - StartHeight) / Length;

        // U slab (longitudinal axis along bridge)
        float u0 = (start.X - Start.X) * Direction.X + (start.Z - Start.Y) * Direction.Y;
        float deltaU = delta.X * Direction.X + delta.Z * Direction.Y;
        if (MathF.Abs(deltaU) > 1e-9f)
        {
            float invDu = 1.0f / deltaU;
            float t1 = (0f - u0) * invDu;
            float t2 = (Length - u0) * invDu;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tNear) tNear = t1;
            if (t2 < tFar) tFar = t2;
            if (tNear > tFar) return false;
        }
        else
        {
            if (u0 < 0f || u0 > Length) return false;
        }

        // V slab (lateral axis perpendicular to bridge)
        float v0 = (start.X - Start.X) * Perpendicular.X + (start.Z - Start.Y) * Perpendicular.Y;
        float deltaV = delta.X * Perpendicular.X + delta.Z * Perpendicular.Y;
        float halfW = Width * 0.5f;
        if (MathF.Abs(deltaV) > 1e-9f)
        {
            float invDv = 1.0f / deltaV;
            float t1 = (-halfW - v0) * invDv;
            float t2 = (halfW - v0) * invDv;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tNear) tNear = t1;
            if (t2 < tFar) tFar = t2;
            if (tNear > tFar) return false;
        }
        else
        {
            if (MathF.Abs(v0) > halfW) return false;
        }

        // W slab (vertical deck thickness relative to deck top)
        float w0 = start.Y - StartHeight - slope * u0;
        float deltaW = delta.Y - slope * deltaU;
        if (MathF.Abs(deltaW) > 1e-9f)
        {
            float invDw = 1.0f / deltaW;
            float t1 = (-Thickness - w0) * invDw;
            float t2 = (0f - w0) * invDw;
            if (t1 > t2) (t1, t2) = (t2, t1);
            if (t1 > tNear) tNear = t1;
            if (t2 < tFar) tFar = t2;
            if (tNear > tFar) return false;
        }
        else
        {
            if (w0 < -Thickness || w0 > 0f) return false;
        }

        float penetrationDistance = (tFar - tNear) * totalDistance;
        if (penetrationDistance < 0.005f)
            return false;

        if (ContainsInterior(start, 0.001f))
            hitT = 0.0f;
        else
            hitT = MathF.Max(0.0f, tNear);

        return true;
    }

    public bool IsNearEntry(Vector2 point, float threshold = 3.0f)
    {
        Vector2 pStart = point - Start;
        float tStart = Vector2.Dot(pStart, Direction);
        float perpStart = MathF.Abs(Vector2.Dot(pStart, Perpendicular));
        bool nearStart = MathF.Abs(tStart) <= threshold && perpStart <= Width * 0.5f + 0.5f;

        Vector2 pEnd = point - End;
        float tEnd = Vector2.Dot(pEnd, Direction);
        float perpEnd = MathF.Abs(Vector2.Dot(pEnd, Perpendicular));
        bool nearEnd = MathF.Abs(tEnd) <= threshold && perpEnd <= Width * 0.5f + 0.5f;

        return nearStart || nearEnd;
    }

    public bool Equals(BridgeStructure? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id && Start.Equals(other.Start) && End.Equals(other.End) &&
               Width.Equals(other.Width) && StartHeight.Equals(other.StartHeight) &&
               EndHeight.Equals(other.EndHeight) && Thickness.Equals(other.Thickness);
    }

    public override bool Equals(object? obj) => Equals(obj as BridgeStructure);

    public override int GetHashCode() => HashCode.Combine(Id, Start, End, Width, StartHeight, EndHeight, Thickness);

    public override string ToString() => $"Bridge {Id} [{Start} -> {End}] W={Width:F1}m H=[{StartHeight:F1}..{EndHeight:F1}]m";
}
