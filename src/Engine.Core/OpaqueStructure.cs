using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Represents an axis-aligned opaque bounding box structure on the map.
/// Dimensions and coordinates are in meters using System.Numerics.
///
/// Current geometric limitation:
/// Only axis-aligned bounding boxes (AABBs) with explicit bottom and top heights (Min.Y and Max.Y)
/// are supported in this milestone. Rotations, arbitrary convex polygonal prisms,
/// and windows/openings are not supported. Complex structures can be assembled from multiple
/// adjacent or stacked axis-aligned volumes.
/// </summary>
public sealed class OpaqueStructure : IEquatable<OpaqueStructure>
{
    public StructureId Id { get; }
    public Vector3 Min { get; }
    public Vector3 Max { get; }
    public bool IsGrounded { get; }

    public float MinX => Min.X;
    public float MaxX => Max.X;
    public float MinY => Min.Y;
    public float MaxY => Max.Y;
    public float MinZ => Min.Z;
    public float MaxZ => Max.Z;

    public Vector3 Size => Max - Min;
    public Vector3 Center => (Min + Max) * 0.5f;

    public OpaqueStructure(StructureId id, Vector3 min, Vector3 max, bool isGrounded = true)
    {
        if (id.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Structure ID must be a positive integer.");

        if (!float.IsFinite(min.X) || !float.IsFinite(min.Y) || !float.IsFinite(min.Z) ||
            !float.IsFinite(max.X) || !float.IsFinite(max.Y) || !float.IsFinite(max.Z))
            throw new ArgumentException("Structure coordinates must be finite values.");

        if (min.X >= max.X)
            throw new ArgumentException($"Structure X dimension must be positive: MinX={min.X}, MaxX={max.X}.");

        if (min.Y >= max.Y)
            throw new ArgumentException($"Structure Y height must be positive: MinY={min.Y}, MaxY={max.Y}.");

        if (min.Z >= max.Z)
            throw new ArgumentException($"Structure Z dimension must be positive: MinZ={min.Z}, MaxZ={max.Z}.");

        Id = id;
        Min = min;
        Max = max;
        IsGrounded = isGrounded;
    }

    public OpaqueStructure(StructureId id, float minX, float minY, float minZ, float maxX, float maxY, float maxZ, bool isGrounded = true)
        : this(id, new Vector3(minX, minY, minZ), new Vector3(maxX, maxY, maxZ), isGrounded)
    {
    }

    public bool Contains(Vector3 point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    public bool ContainsInterior(Vector3 point, float epsilon = 1e-4f) =>
        point.X > Min.X + epsilon && point.X < Max.X - epsilon &&
        point.Y > Min.Y + epsilon && point.Y < Max.Y - epsilon &&
        point.Z > Min.Z + epsilon && point.Z < Max.Z - epsilon;

    public bool Equals(OpaqueStructure? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id && Min.Equals(other.Min) && Max.Equals(other.Max) && IsGrounded == other.IsGrounded;
    }

    public override bool Equals(object? obj) => Equals(obj as OpaqueStructure);

    public override int GetHashCode() => HashCode.Combine(Id, Min, Max, IsGrounded);

    public override string ToString() => $"{Id} [{Min} .. {Max}] Grounded={IsGrounded}";
}
