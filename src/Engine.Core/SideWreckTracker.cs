namespace DeadGround.Engine;

public sealed class SideWreckTracker
{
    public const int DefaultCapacity = 128;
    private readonly WreckObservation[] _knownWrecks;
    private readonly HashSet<WreckId> _knownWreckIds;
    private int _count;

    public int Count => _count;
    public ReadOnlySpan<WreckObservation> KnownWrecks => _knownWrecks.AsSpan(0, _count);

    public SideWreckTracker(int capacity = DefaultCapacity)
    {
        _knownWrecks = new WreckObservation[capacity];
        _knownWreckIds = new HashSet<WreckId>(capacity);
    }

    public bool IsKnown(WreckId id) => _knownWreckIds.Contains(id);
    public bool Contains(WreckId id) => _knownWreckIds.Contains(id);
    public ReadOnlySpan<WreckObservation> ExportWrecks() => _knownWrecks.AsSpan(0, _count);

    public bool TryAdd(WreckObservation wreck)
    {
        if (_knownWreckIds.Contains(wreck.Id)) return false;
        if (_count < _knownWrecks.Length)
        {
            _knownWrecks[_count++] = wreck;
            _knownWreckIds.Add(wreck.Id);
            return true;
        }

        // Evict oldest wreck to stay bounded without dropping new observations or overflowing
        _knownWreckIds.Remove(_knownWrecks[0].Id);
        Array.Copy(_knownWrecks, 1, _knownWrecks, 0, _knownWrecks.Length - 1);
        _knownWrecks[^1] = wreck;
        _knownWreckIds.Add(wreck.Id);
        return true;
    }

    public void Clear()
    {
        _count = 0;
        _knownWreckIds.Clear();
    }
}
