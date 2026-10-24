namespace DeadGround.Engine;

/// <summary>
/// Load-time limits per movement-class graph. These bound map complexity before large allocations.
/// Runtime route and per-tick work limits are configured separately by NavigationTuning.
/// </summary>
public sealed record NavigationBuildLimits(
    int MaxSectors = 16_384,
    int MaxDetailedCells = 4_000_000,
    int MaxPortals = 131_072,
    int MaxIntraEdges = 1_000_000)
{
    internal void Validate()
    {
        if (MaxSectors <= 0 || MaxDetailedCells <= 0 || MaxPortals <= 0 || MaxIntraEdges <= 0 ||
            MaxPortals > int.MaxValue - 2)
            throw new ArgumentOutOfRangeException(nameof(NavigationBuildLimits), "Navigation build limits must be positive and fit array indices.");
    }

    internal void CheckSectors(float width, float depth, float sectorSize)
    {
        double cols = Math.Ceiling(width / sectorSize);
        double rows = Math.Ceiling(depth / sectorSize);
        if (!double.IsFinite(cols * rows) || cols * rows > MaxSectors)
            throw new InvalidOperationException($"Navigation sector budget exceeded: {cols * rows} sectors, limit {MaxSectors}.");
    }
}
