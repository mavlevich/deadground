namespace DeadGround.Engine;

/// <summary>
/// Execution status of an individual unit navigation path request.
/// </summary>
public enum PathRequestStatus : byte
{
    None = 0,
    Pending = 1,
    Searching = 2,
    PathFound = 3,
    NoPath = 4,
    InvalidDestination = 5,
    CapacityExceeded = 6
}

/// <summary>
/// Configuration tuning parameters for server-side static obstacle navigation and A* planning.
/// </summary>
public sealed record NavigationTuning(
    int MaxNodeExpansionsPerTick = NavigationTuning.DefaultMaxNodeExpansionsPerTick,
    int MaxPendingRequests = NavigationTuning.DefaultMaxPendingRequests,
    int MaxWaypointsPerRoute = NavigationTuning.DefaultMaxWaypointsPerRoute,
    float CellSize = NavigationTuning.DefaultCellSize,
    float MaxSnapDistance = NavigationTuning.DefaultMaxSnapDistance)
{
    public const int DefaultMaxNodeExpansionsPerTick = 1000;
    public const int DefaultMaxPendingRequests = 256;
    public const int DefaultMaxWaypointsPerRoute = 64;
    public const float DefaultCellSize = 0.5f;
    public const float DefaultMaxSnapDistance = 3.0f;
}
