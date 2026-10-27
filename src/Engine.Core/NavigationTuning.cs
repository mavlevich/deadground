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
/// Execution stage of the multi-stage path planning pipeline.
/// </summary>
public enum NavigationSearchStage : byte
{
    None = 0,
    GlobalPlanning = 1,
    AreaPreparation = 2,
    LocalPlanning = 3,
    PathSmoothing = 4,
    Completed = 5,
    Failed = 6
}

/// <summary>
/// Configuration tuning parameters for server-side static obstacle navigation and hierarchical planning.
/// </summary>
public sealed record NavigationTuning(
    int MaxNodeExpansionsPerTick = NavigationTuning.DefaultMaxNodeExpansionsPerTick,
    int MaxPendingRequests = NavigationTuning.DefaultMaxPendingRequests,
    int MaxWaypointsPerRoute = NavigationTuning.DefaultMaxWaypointsPerRoute,
    float CellSize = NavigationTuning.DefaultCellSize,
    float MaxSnapDistance = NavigationTuning.DefaultMaxSnapDistance,
    int MaxGlobalExpansionsPerTick = NavigationTuning.DefaultMaxGlobalExpansionsPerTick,
    int MaxLocalExpansionsPerTick = NavigationTuning.DefaultMaxLocalExpansionsPerTick,
    int MaxSmoothingRaycastsPerTick = NavigationTuning.DefaultMaxSmoothingRaycastsPerTick,
    int MaxAreaPrepCellsPerTick = NavigationTuning.DefaultMaxAreaPrepCellsPerTick,
    int MaxTotalExpansionsPerRequest = NavigationTuning.DefaultMaxTotalExpansionsPerRequest,
    float SectorSize = NavigationTuning.DefaultSectorSize,
    float MaxLocalWindowSize = NavigationTuning.DefaultMaxLocalWindowSize,
    int ChunkLookaheadWaypoints = NavigationTuning.DefaultChunkLookaheadWaypoints,
    int GlobalPlanCacheEntries = NavigationTuning.DefaultGlobalPlanCacheEntries,
    float PivotSteerAngleThreshold = NavigationTuning.DefaultPivotSteerAngleThreshold,
    float FinalFacingTolerance = NavigationTuning.DefaultFinalFacingTolerance,
    float LookaheadTime = NavigationTuning.DefaultLookaheadTime,
    float MinLookaheadDistance = NavigationTuning.DefaultMinLookaheadDistance,
    float MaxLookaheadDistance = NavigationTuning.DefaultMaxLookaheadDistance,
    int MaxOrderExecutionTicks = NavigationTuning.DefaultMaxOrderExecutionTicks)
{
    public const int DefaultMaxNodeExpansionsPerTick = 1000;
    public const int DefaultMaxPendingRequests = 256;
    public const int DefaultMaxWaypointsPerRoute = 64;
    public const float DefaultCellSize = 0.5f;
    public const float DefaultMaxSnapDistance = 3.0f;
    public const int DefaultMaxGlobalExpansionsPerTick = 500;
    public const int DefaultMaxLocalExpansionsPerTick = 1000;
    public const int DefaultMaxSmoothingRaycastsPerTick = 200;
    public const int DefaultMaxAreaPrepCellsPerTick = 40000;
    public const int DefaultMaxTotalExpansionsPerRequest = 20000;
    public const float DefaultSectorSize = 50.0f;
    public const float DefaultMaxLocalWindowSize = 160.0f;
    public const int DefaultChunkLookaheadWaypoints = 2;
    public const int DefaultGlobalPlanCacheEntries = 16;
    public const float DefaultPivotSteerAngleThreshold = 0.05f; // ~2.8 degrees in radians
    public const float DefaultFinalFacingTolerance = 0.02f; // ~1.15 degrees in radians
    public const float DefaultLookaheadTime = 0.5f;
    public const float DefaultMinLookaheadDistance = 1.5f;
    public const float DefaultMaxLookaheadDistance = 8.0f;
    public const int DefaultMaxOrderExecutionTicks = 1000; // 50 seconds at 20 Hz
}

