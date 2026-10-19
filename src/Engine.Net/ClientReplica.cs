using System.Numerics;

namespace DeadGround.Engine.Net;

public sealed class ClientReplica
{
    public const int EventCapacity = 128;

    private readonly SnapshotFrame[] _frames = new SnapshotFrame[8];
    private readonly CombatEventObservation[] _eventBuffer = new CombatEventObservation[EventCapacity * 2];
    private int _eventHead;
    private int _eventCount;
    private SnapshotFrame _incoming;
    private int _next, _count;
    private SnapshotFrame Latest => _frames[(_next + _frames.Length - 1) % _frames.Length];
    public int LatestTick => _count == 0 ? 0 : Latest.Tick;
    public uint LatestSequence => _count == 0 ? 0 : Latest.Sequence;
    public ReadOnlySpan<OwnUnitObservation> OwnUnits => _count == 0 ? [] : Latest.Units.AsSpan(0, Latest.Count);
    public ReadOnlySpan<ContactObservation> Contacts => _count == 0 ? [] : Latest.Contacts.AsSpan(0, Latest.ContactCount);
    public ReadOnlySpan<CombatEventObservation> Events
    {
        get
        {
            if (_eventCount == 0) return [];
            int start = _eventCount == EventCapacity ? _eventHead : 0;
            return _eventBuffer.AsSpan(start, _eventCount);
        }
    }
    public int ContactCount => _count == 0 ? 0 : Latest.ContactCount;
    public int EventCount => _eventCount;
    public ReadOnlySpan<CombatEventObservation> LatestSnapshotEvents => _count == 0 ? [] : Latest.Events.AsSpan(0, Latest.EventCount);
    public int LatestSnapshotEventCount => _count == 0 ? 0 : Latest.EventCount;
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

        SnapshotFrame applied = Latest;
        for (int i = 0; i < applied.EventCount; i++)
        {
            AddEvent(applied.Events[i]);
        }
        return true;
    }

    public void ClearEvents()
    {
        _eventCount = 0;
        _eventHead = 0;
    }

    private void AddEvent(in CombatEventObservation ev)
    {
        if (ev.EventId != 0 && ContainsEvent(ev.EventId))
        {
            return;
        }

        int slot = _eventHead;
        _eventBuffer[slot] = ev;
        _eventBuffer[slot + EventCapacity] = ev;

        _eventHead = (_eventHead + 1) % EventCapacity;
        if (_eventCount < EventCapacity)
        {
            _eventCount++;
        }
    }

    private bool ContainsEvent(uint id)
    {
        ReadOnlySpan<CombatEventObservation> span = Events;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i].EventId == id) return true;
        }
        return false;
    }

    public Vector2 SamplePosition(EntityId id, double renderTick)
    {
        if (_count == 0 || !Latest.Indices.TryGetValue(id, out int index)) return default;
        FindBracket(renderTick, out SnapshotFrame left, out SnapshotFrame right, out float alpha);
        if (!left.Indices.TryGetValue(id, out int li) || !right.Indices.TryGetValue(id, out int ri))
            return Latest.Units[index].Position;
        return Vector2.Lerp(left.Units[li].Position, right.Units[ri].Position, alpha);
    }

    public Vector2 SampleContactPosition(ContactId id, double renderTick)
    {
        if (_count == 0 || !Latest.ContactIndices.TryGetValue(id, out int index)) return default;
        ref readonly ContactObservation latestContact = ref Latest.Contacts[index];
        if (latestContact.State != ContactState.Current)
            return latestContact.Position;

        FindBracket(renderTick, out SnapshotFrame left, out SnapshotFrame right, out float alpha);
        if (!left.ContactIndices.TryGetValue(id, out int li) || !right.ContactIndices.TryGetValue(id, out int ri))
            return latestContact.Position;

        ref readonly ContactObservation leftContact = ref left.Contacts[li];
        ref readonly ContactObservation rightContact = ref right.Contacts[ri];

        // Only interpolate when both frames observe the contact as Current
        if (leftContact.State != ContactState.Current || rightContact.State != ContactState.Current)
            return latestContact.Position;

        return Vector2.Lerp(leftContact.Position, rightContact.Position, alpha);
    }

    public static float InterpolateYaw(float fromYaw, float toYaw, float alpha)
    {
        float delta = MathF.IEEERemainder(toYaw - fromYaw, MathF.Tau);
        float yaw = fromYaw + delta * alpha;
        yaw = MathF.IEEERemainder(yaw, MathF.Tau);
        if (yaw <= -MathF.PI) yaw += MathF.Tau;
        if (yaw > MathF.PI) yaw -= MathF.Tau;
        return yaw;
    }

    /// <summary>
    /// Turret yaw and hull heading of an own unit, both sampled from the same snapshot pair so a
    /// turret that rides on the hull stays fixed to it on screen.
    /// </summary>
    public void SampleYaws(EntityId id, double renderTick, out float turretYaw, out float hullHeading)
    {
        turretYaw = 0f;
        hullHeading = 0f;
        if (_count == 0 || !Latest.Indices.TryGetValue(id, out int index)) return;
        ref readonly OwnUnitObservation latest = ref Latest.Units[index];
        FindBracket(renderTick, out SnapshotFrame left, out SnapshotFrame right, out float alpha);
        if (!left.Indices.TryGetValue(id, out int li) || !right.Indices.TryGetValue(id, out int ri))
        {
            turretYaw = latest.TurretYaw;
            hullHeading = latest.HullHeading;
            return;
        }
        turretYaw = InterpolateYaw(left.Units[li].TurretYaw, right.Units[ri].TurretYaw, alpha);
        hullHeading = InterpolateYaw(left.Units[li].HullHeading, right.Units[ri].HullHeading, alpha);
    }

    /// <summary>
    /// Turret yaw and hull heading of a contact. A contact that is not current keeps its last seen pose.
    /// </summary>
    public void SampleContactYaws(ContactId id, double renderTick, out float turretYaw, out float hullHeading)
    {
        turretYaw = 0f;
        hullHeading = 0f;
        if (_count == 0 || !Latest.ContactIndices.TryGetValue(id, out int index)) return;
        ref readonly ContactObservation latest = ref Latest.Contacts[index];
        turretYaw = latest.TurretYaw;
        hullHeading = latest.HullHeading;
        if (latest.State != ContactState.Current) return;

        FindBracket(renderTick, out SnapshotFrame left, out SnapshotFrame right, out float alpha);
        if (!left.ContactIndices.TryGetValue(id, out int li) || !right.ContactIndices.TryGetValue(id, out int ri))
            return;

        ref readonly ContactObservation leftContact = ref left.Contacts[li];
        ref readonly ContactObservation rightContact = ref right.Contacts[ri];
        if (leftContact.State != ContactState.Current || rightContact.State != ContactState.Current)
            return;

        turretYaw = InterpolateYaw(leftContact.TurretYaw, rightContact.TurretYaw, alpha);
        hullHeading = InterpolateYaw(leftContact.HullHeading, rightContact.HullHeading, alpha);
    }

    private void FindBracket(double renderTick, out SnapshotFrame left, out SnapshotFrame right, out float alpha)
    {
        int oldest = (_next - _count + _frames.Length) % _frames.Length;
        left = _frames[oldest];
        right = Latest;
        for (int i = 0; i < _count; i++)
        {
            SnapshotFrame frame = _frames[(oldest + i) % _frames.Length];
            if (frame.Tick <= renderTick) left = frame;
            if (frame.Tick >= renderTick) { right = frame; break; }
        }
        alpha = right.Tick == left.Tick ? 1 :
            (float)Math.Clamp((renderTick - left.Tick) / (right.Tick - left.Tick), 0, 1);
    }
}
