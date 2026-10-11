using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Result of a finite 3D line-of-sight query.
/// Allocation-free value type.
/// </summary>
public readonly struct LineOfSightResult : IEquatable<LineOfSightResult>
{
    public bool IsClear { get; }
    public ObstructionKind ObstructionKind { get; }
    public StructureId ObstructingStructureId { get; }
    public Vector3 HitPoint { get; }
    public float Distance { get; }
    public float RayFraction { get; }

    public LineOfSightResult(
        bool isClear,
        ObstructionKind obstructionKind,
        StructureId obstructingStructureId,
        Vector3 hitPoint,
        float distance,
        float rayFraction)
    {
        IsClear = isClear;
        ObstructionKind = obstructionKind;
        ObstructingStructureId = obstructingStructureId;
        HitPoint = hitPoint;
        Distance = distance;
        RayFraction = rayFraction;
    }

    public static LineOfSightResult Clear(Vector3 target, float distance) =>
        new(true, ObstructionKind.None, default, target, distance, 1.0f);

    public static LineOfSightResult BlockedByTerrain(Vector3 hitPoint, float distance, float rayFraction) =>
        new(false, ObstructionKind.Terrain, default, hitPoint, distance, rayFraction);

    public static LineOfSightResult BlockedByStructure(StructureId structureId, Vector3 hitPoint, float distance, float rayFraction) =>
        new(false, ObstructionKind.Structure, structureId, hitPoint, distance, rayFraction);

    public bool Equals(LineOfSightResult other) =>
        IsClear == other.IsClear &&
        ObstructionKind == other.ObstructionKind &&
        ObstructingStructureId == other.ObstructingStructureId &&
        HitPoint == other.HitPoint &&
        Distance == other.Distance &&
        RayFraction == other.RayFraction;

    public bool ApproximatelyEquals(LineOfSightResult other, float tolerance = 1e-4f) =>
        IsClear == other.IsClear &&
        ObstructionKind == other.ObstructionKind &&
        ObstructingStructureId == other.ObstructingStructureId &&
        Vector3.Distance(HitPoint, other.HitPoint) <= tolerance &&
        Math.Abs(Distance - other.Distance) <= tolerance &&
        Math.Abs(RayFraction - other.RayFraction) <= tolerance;

    public override bool Equals(object? obj) => obj is LineOfSightResult other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(IsClear, ObstructionKind, ObstructingStructureId, HitPoint, Distance, RayFraction);

    public static bool operator ==(LineOfSightResult left, LineOfSightResult right) => left.Equals(right);
    public static bool operator !=(LineOfSightResult left, LineOfSightResult right) => !left.Equals(right);

    public override string ToString() => IsClear
        ? $"Clear (Distance: {Distance:F2}m)"
        : ObstructionKind == ObstructionKind.Structure
            ? $"Blocked by {ObstructingStructureId} at {Distance:F2}m (Hit: {HitPoint})"
            : $"Blocked by Terrain at {Distance:F2}m (Hit: {HitPoint})";
}
