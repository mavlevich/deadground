using System.Numerics;

namespace DeadGround.Engine.Net;

public sealed class ClientReplica
{
    private readonly SnapshotFrame[] _frames = new SnapshotFrame[8];
    private SnapshotFrame _incoming;
    private int _next, _count;
    private SnapshotFrame Latest => _frames[(_next + _frames.Length - 1) % _frames.Length];
    public int LatestTick => _count == 0 ? 0 : Latest.Tick;
    public uint LatestSequence => _count == 0 ? 0 : Latest.Sequence;
    public ReadOnlySpan<OwnUnitObservation> OwnUnits => _count == 0 ? [] : Latest.Units.AsSpan(0, Latest.Count);
    public ReadOnlySpan<ContactObservation> Contacts => _count == 0 ? [] : Latest.Contacts.AsSpan(0, Latest.ContactCount);
    public ReadOnlySpan<CombatEventObservation> Events => _count == 0 ? [] : Latest.Events.AsSpan(0, Latest.EventCount);
    public int ContactCount => _count == 0 ? 0 : Latest.ContactCount;
    public int EventCount => _count == 0 ? 0 : Latest.EventCount;
    public MissionObservation LatestMission => _count == 0 ? default : Latest.Mission;
    public MissionObservation Mission => LatestMission;

    public bool TryGetOwnUnit(EntityId id, out OwnUnitObservation unit)
    {
        if (_count == 0 || !Latest.Indices.TryGetValue(id, out int index))
        {
            unit = default;
            return false;
        }
        unit = Latest.Units[index];
        return true;
    }

    public bool TryGetContact(ContactId id, out ContactObservation contact)
    {
        if (_count == 0 || !Latest.ContactIndices.TryGetValue(id, out int index))
        {
            contact = default;
            return false;
        }
        contact = Latest.Contacts[index];
        return true;
    }

    public ClientReplica(int capacity = WireProtocol.MaxUnits)
    {
        if (capacity < 1 || capacity > WireProtocol.MaxUnits) throw new ArgumentOutOfRangeException(nameof(capacity));
        for (int i = 0; i < _frames.Length; i++) _frames[i] = new SnapshotFrame(capacity);
        _incoming = new SnapshotFrame(capacity);
    }

    public bool TryApply(ReadOnlySpan<byte> message)
    {
        if (!WireProtocol.TryReadSnapshot(message, _incoming)) return false;
        if (_count > 0 && (_incoming.Sequence <= Latest.Sequence || _incoming.Tick <= Latest.Tick)) return false;
        // Decode into a spare buffer so a partial or malformed message cannot corrupt visible history.
        SnapshotFrame spare = _frames[_next];
        _frames[_next] = _incoming;
        _incoming = spare;
        _next = (_next + 1) % _frames.Length;
        _count = Math.Min(_count + 1, _frames.Length);
        return true;
    }

    public Vector2 SamplePosition(EntityId id, double renderTick)
    {
        if (_count == 0 || !Latest.Indices.TryGetValue(id, out int index)) return default;
        int oldest = (_next - _count + _frames.Length) % _frames.Length;
        SnapshotFrame left = _frames[oldest];
        SnapshotFrame right = Latest;
        for (int i = 0; i < _count; i++)
        {
            SnapshotFrame frame = _frames[(oldest + i) % _frames.Length];
            if (frame.Tick <= renderTick) left = frame;
            if (frame.Tick >= renderTick) { right = frame; break; }
        }
        if (!left.Indices.TryGetValue(id, out int li) || !right.Indices.TryGetValue(id, out int ri))
            return Latest.Units[index].Position;
        float alpha = right.Tick == left.Tick ? 1 :
            (float)Math.Clamp((renderTick - left.Tick) / (right.Tick - left.Tick), 0, 1);
        return Vector2.Lerp(left.Units[li].Position, right.Units[ri].Position, alpha);
    }

    public Vector2 SampleContactPosition(ContactId id, double renderTick)
    {
        if (_count == 0 || !Latest.ContactIndices.TryGetValue(id, out int index)) return default;
        ref readonly ContactObservation latestContact = ref Latest.Contacts[index];
        if (latestContact.State != ContactState.Current)
            return latestContact.Position;

        int oldest = (_next - _count + _frames.Length) % _frames.Length;
        SnapshotFrame left = _frames[oldest];
        SnapshotFrame right = Latest;
        for (int i = 0; i < _count; i++)
        {
            SnapshotFrame frame = _frames[(oldest + i) % _frames.Length];
            if (frame.Tick <= renderTick) left = frame;
            if (frame.Tick >= renderTick) { right = frame; break; }
        }

        if (!left.ContactIndices.TryGetValue(id, out int li) || !right.ContactIndices.TryGetValue(id, out int ri))
            return latestContact.Position;

        ref readonly ContactObservation leftContact = ref left.Contacts[li];
        ref readonly ContactObservation rightContact = ref right.Contacts[ri];

        // Only interpolate when both frames observe the contact as Current
        if (leftContact.State != ContactState.Current || rightContact.State != ContactState.Current)
            return latestContact.Position;

        float alpha = right.Tick == left.Tick ? 1 :
            (float)Math.Clamp((renderTick - left.Tick) / (right.Tick - left.Tick), 0, 1);
        return Vector2.Lerp(leftContact.Position, rightContact.Position, alpha);
    }
}
