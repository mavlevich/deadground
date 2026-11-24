namespace DeadGround.Engine;

/// <summary>
/// Per-side fog-of-war knowledge filter for destructible structures, mirroring SideWreckTracker.
/// Tracks the last observed damaged/collapsed state of each building section per player side so
/// destruction occurring outside line of sight never leaks through snapshots.
/// Strictly allocation-free after initialization.
/// </summary>
public sealed class SideStructureTracker
{
    public const int DefaultCapacity = WorldState.MaxDestructibleStructures;

    private readonly StructureObservation[] _knownStructures;
    private readonly Dictionary<StructureId, int> _indexById;
    private int _count;

    public int Count => _count;
    public ReadOnlySpan<StructureObservation> KnownStructures => _knownStructures.AsSpan(0, _count);

    public SideStructureTracker(int capacity = DefaultCapacity)
    {
        _knownStructures = new StructureObservation[capacity];
        _indexById = new Dictionary<StructureId, int>(capacity);
    }

    public bool TryGet(StructureId id, out StructureObservation observation)
    {
        if (_indexById.TryGetValue(id, out int index))
        {
            observation = _knownStructures[index];
            return true;
        }
        observation = default;
        return false;
    }

    public ReadOnlySpan<StructureObservation> ExportStructures() => _knownStructures.AsSpan(0, _count);

    /// <summary>
    /// Records or updates the side's last observed damaged state for a destructible structure.
    /// Returns true if the stored state changed.
    /// </summary>
    public bool TryUpsert(in StructureObservation observation)
    {
        if (_indexById.TryGetValue(observation.Id, out int existingIndex))
        {
            if (_knownStructures[existingIndex] == observation)
                return false;
            _knownStructures[existingIndex] = observation;
            return true;
        }

        if (_count >= _knownStructures.Length)
            return false;

        int index = _count++;
        _knownStructures[index] = observation;
        _indexById[observation.Id] = index;
        return true;
    }

    public void Clear()
    {
        _count = 0;
        _indexById.Clear();
    }
}
