namespace DeadGround.Engine;

/// <summary>
/// Per-side bounded buffer of smoke clouds currently permitted in a player's snapshot.
/// Zero allocations during simulation ticks.
/// </summary>
public sealed class SideSmokeTracker
{
    public const int DefaultCapacity = WorldState.MaxSmokeClouds;
    private readonly SmokeCloudObservation[] _visibleClouds;
    private int _count;

    public int Count => _count;
    public ReadOnlySpan<SmokeCloudObservation> VisibleClouds => _visibleClouds.AsSpan(0, _count);

    public SideSmokeTracker(int capacity = DefaultCapacity)
    {
        _visibleClouds = new SmokeCloudObservation[Math.Max(1, capacity)];
    }

    public void Clear() => _count = 0;

    public bool TryAdd(SmokeCloudObservation cloud)
    {
        if (_count >= _visibleClouds.Length)
        {
            return false;
        }
        _visibleClouds[_count++] = cloud;
        return true;
    }

    public ReadOnlySpan<SmokeCloudObservation> ExportSmokeClouds() => _visibleClouds.AsSpan(0, _count);
}
