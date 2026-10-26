using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Represents a static terrain area (such as water or dense forest) that restricts movement
/// based on unit traversability capabilities.
/// </summary>
public sealed class TerrainObstacle : IEquatable<TerrainObstacle>
{
    public float MinX { get; }
    public float MinZ { get; }
    public float MaxX { get; }
    public float MaxZ { get; }

    /// <summary>
    /// Capability flag required to enter this terrain zone.
    /// If a unit lacks this capability, the area acts as an impassable obstacle.
    /// For example:
    /// - Water requires TraversabilityCapabilities.Water.
    /// - DenseForest requires TraversabilityCapabilities.DenseForest.
    /// </summary>
    public TraversabilityCapabilities RequiredCapability { get; }

    public TerrainObstacle(float minX, float minZ, float maxX, float maxZ, TraversabilityCapabilities requiredCapability)
    {
        if (!float.IsFinite(minX) || !float.IsFinite(minZ) || !float.IsFinite(maxX) || !float.IsFinite(maxZ))
            throw new ArgumentException("Terrain obstacle coordinates must be finite.");
        if (minX >= maxX || minZ >= maxZ)
            throw new ArgumentException("Terrain obstacle min must be strictly less than max.");

        MinX = minX;
        MinZ = minZ;
        MaxX = maxX;
        MaxZ = maxZ;
        RequiredCapability = requiredCapability;
    }

    public bool Contains(Vector2 point) =>
        point.X >= MinX && point.X <= MaxX &&
        point.Y >= MinZ && point.Y <= MaxZ;

    public bool Blocks(TraversabilityCapabilities capabilities) =>
        (capabilities & RequiredCapability) == 0;

    public float DistanceTo(Vector2 point)
    {
        float dx = MathF.Max(0f, MathF.Max(MinX - point.X, point.X - MaxX));
        float dz = MathF.Max(0f, MathF.Max(MinZ - point.Y, point.Y - MaxZ));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public bool Equals(TerrainObstacle? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return MinX.Equals(other.MinX) && MinZ.Equals(other.MinZ) &&
               MaxX.Equals(other.MaxX) && MaxZ.Equals(other.MaxZ) &&
               RequiredCapability == other.RequiredCapability;
    }

    public override bool Equals(object? obj) => Equals(obj as TerrainObstacle);

    public override int GetHashCode() => HashCode.Combine(MinX, MinZ, MaxX, MaxZ, (int)RequiredCapability);

    public override string ToString() => $"TerrainObstacle [{MinX},{MinZ} .. {MaxX},{MaxZ}] Requires={RequiredCapability}";
}
