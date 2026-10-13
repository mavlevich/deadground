using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Authoritative server-side contact tracker for a single player/side.
/// Manages contact lifecycle, correlation upon rediscovery, memory aging,
/// expiry, and bounded capacity with deterministic eviction.
///
/// Guaranteed allocation-free during simulation ticks.
/// </summary>
public sealed class SideContactTracker
{
    public const int DefaultCapacity = 256;

    public readonly record struct ObservedTarget(
        EntityId Id,
        Vector2 Position,
        bool InIdentifyRange = false,
        int IdentifyTicks = 0,
        ContactClassification TrueClassification = ContactClassification.Unknown);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int CompareTargets(in ObservedTarget a, in ObservedTarget b)
    {
        int cmpX = a.Position.X.CompareTo(b.Position.X);
        if (cmpX != 0) return cmpX;
        int cmpY = a.Position.Y.CompareTo(b.Position.Y);
        if (cmpY != 0) return cmpY;
        return a.Id.Value.CompareTo(b.Id.Value);
    }

    private static void SortUnassigned(ObservedTarget[] buffer, int left, int right)
    {
        while (left < right)
        {
            if (right - left < 16)
            {
                for (int insI = left + 1; insI <= right; insI++)
                {
                    ObservedTarget key = buffer[insI];
                    int insJ = insI - 1;
                    while (insJ >= left && CompareTargets(buffer[insJ], key) > 0)
                    {
                        buffer[insJ + 1] = buffer[insJ];
                        insJ--;
                    }
                    buffer[insJ + 1] = key;
                }
                break;
            }

            int mid = left + ((right - left) >> 1);
            if (CompareTargets(buffer[left], buffer[mid]) > 0) (buffer[left], buffer[mid]) = (buffer[mid], buffer[left]);
            if (CompareTargets(buffer[left], buffer[right]) > 0) (buffer[left], buffer[right]) = (buffer[right], buffer[left]);
            if (CompareTargets(buffer[mid], buffer[right]) > 0) (buffer[mid], buffer[right]) = (buffer[right], buffer[mid]);

            ObservedTarget pivot = buffer[mid];
            (buffer[mid], buffer[right - 1]) = (buffer[right - 1], buffer[mid]);
            int i = left;
            int j = right - 1;

            while (true)
            {
                while (CompareTargets(buffer[++i], pivot) < 0) { }
                while (CompareTargets(pivot, buffer[--j]) < 0) { }
                if (i >= j) break;
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }

            (buffer[i], buffer[right - 1]) = (buffer[right - 1], buffer[i]);

            if (i - left < right - i)
            {
                SortUnassigned(buffer, left, i - 1);
                left = i + 1;
            }
            else
            {
                SortUnassigned(buffer, i + 1, right);
                right = i - 1;
            }
        }
    }

    private static void SortExportBuffer(ContactObservation[] buffer, int left, int right)
    {
        while (left < right)
        {
            if (right - left < 16)
            {
                for (int insI = left + 1; insI <= right; insI++)
                {
                    ContactObservation key = buffer[insI];
                    int insJ = insI - 1;
                    while (insJ >= left && buffer[insJ].Id.Value > key.Id.Value)
                    {
                        buffer[insJ + 1] = buffer[insJ];
                        insJ--;
                    }
                    buffer[insJ + 1] = key;
                }
                break;
            }

            int mid = left + ((right - left) >> 1);
            if (buffer[left].Id.Value > buffer[mid].Id.Value) (buffer[left], buffer[mid]) = (buffer[mid], buffer[left]);
            if (buffer[left].Id.Value > buffer[right].Id.Value) (buffer[left], buffer[right]) = (buffer[right], buffer[left]);
            if (buffer[mid].Id.Value > buffer[right].Id.Value) (buffer[mid], buffer[right]) = (buffer[right], buffer[mid]);

            ContactObservation pivot = buffer[mid];
            (buffer[mid], buffer[right - 1]) = (buffer[right - 1], buffer[mid]);
            int i = left;
            int j = right - 1;

            while (true)
            {
                while (buffer[++i].Id.Value < pivot.Id.Value) { }
                while (pivot.Id.Value < buffer[--j].Id.Value) { }
                if (i >= j) break;
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
            }

            (buffer[i], buffer[right - 1]) = (buffer[right - 1], buffer[i]);

            if (i - left < right - i)
            {
                SortExportBuffer(buffer, left, i - 1);
                left = i + 1;
            }
            else
            {
                SortExportBuffer(buffer, i + 1, right);
                right = i - 1;
            }
        }
    }

    internal struct Entry
    {
        public ContactId Id;
        public ContactState State;
        public DetectionLevel Level;
        public ContactClassification Classification;
        public Vector2 Position;
        public int LastSeenTick;
        public bool ObservedThisStep;
        public bool HasBoundEntity;
        public EntityId BoundEntityId; // Internal tracker link for continuous tracking only
        public int ConsecutiveIdentifyTicks;
        public int RequiredIdentifyTicks;
        public int LastIdentifiedTick;
        public ContactClassification TargetTrueClassification;
    }

    private readonly Entry[] _entries;
    private readonly ContactObservation[] _exportBuffer;
    private readonly ObservedTarget[] _unassignedBuffer;
    private readonly Dictionary<EntityId, int> _entityToContactIndex;
    private readonly DetectionTuning _tuning;
    private uint _nextContactId = 1;
    private int _count;

    public int Side { get; }
    public int Capacity => _entries.Length;
    public int Count => _count;
    public long OverflowEvictionCount { get; private set; }
    public long DroppedDueToCapacityCount { get; private set; }

    public SideContactTracker(int side, DetectionTuning tuning, int capacity = DefaultCapacity)
    {
        if (side < 1) throw new ArgumentOutOfRangeException(nameof(side));
        ArgumentNullException.ThrowIfNull(tuning);
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));

        Side = side;
        _tuning = tuning;
        _entries = new Entry[capacity];
        _exportBuffer = new ContactObservation[capacity];
        _unassignedBuffer = new ObservedTarget[Math.Max(1024, capacity * 4)];
        _entityToContactIndex = new Dictionary<EntityId, int>(Math.Max(512, capacity * 2));
    }

    /// <summary>
    /// Begins a detection step by resetting per-step observation flags.
    /// </summary>
    public void BeginDetectionStep()
    {
        for (int i = 0; i < _count; i++)
        {
            _entries[i].ObservedThisStep = false;
        }
    }

    /// <summary>
    /// Registers a single observation (convenience overload for tests).
    /// </summary>
    public void RegisterObservation(EntityId enemyId, Vector2 position, int currentTick)
    {
        Span<ObservedTarget> single = stackalloc ObservedTarget[1] { new(enemyId, position) };
        RegisterObservations(single, currentTick);
    }

    /// <summary>
    /// Registers all observations established for this side in the current tick.
    /// Fast-paths existing continuous observations in O(1) time without linear scans,
    /// and establishes a canonical deterministic order for unassigned targets
    /// to guarantee information isolation independent of mutable storage compaction.
    /// </summary>
    public void RegisterObservations(ReadOnlySpan<ObservedTarget> targets, int currentTick)
    {
        int unassignedCount = 0;

        // 1. Fast continuous track check: O(1) direct lookup for already-tracked entities
        for (int i = 0; i < targets.Length; i++)
        {
            ObservedTarget target = targets[i];
            if (_entityToContactIndex.TryGetValue(target.Id, out int contactIndex) &&
                contactIndex >= 0 && contactIndex < _count &&
                _entries[contactIndex].State == ContactState.Current &&
                _entries[contactIndex].HasBoundEntity &&
                _entries[contactIndex].BoundEntityId.Value == target.Id.Value)
            {
                ref Entry entry = ref _entries[contactIndex];
                entry.Position = target.Position;
                entry.LastSeenTick = currentTick;
                entry.ObservedThisStep = true;
                UpdateIdentification(ref entry, in target, currentTick);
            }
            else
            {
                if (unassignedCount < _unassignedBuffer.Length)
                {
                    _unassignedBuffer[unassignedCount++] = target;
                }
            }
        }

        if (unassignedCount == 0) return;

        // 2. Canonical deterministic ordering for unassigned targets
        // Guarantees contact ID assignment and correlation are invariant to internal storage reordering
        if (unassignedCount > 1)
        {
            SortUnassigned(_unassignedBuffer, 0, unassignedCount - 1);
        }

        // 3. Process unassigned targets in canonical order
        for (int u = 0; u < unassignedCount; u++)
        {
            ObservedTarget target = _unassignedBuffer[u];

            // Rediscovery / Correlation: Check against LastKnown contacts
            int bestMatchIndex = -1;
            float bestDistanceSq = float.PositiveInfinity;

            for (int i = 0; i < _count; i++)
            {
                ref Entry entry = ref _entries[i];
                if (entry.State != ContactState.LastKnown || entry.ObservedThisStep)
                    continue;

                int elapsedTicks = currentTick - entry.LastSeenTick;
                if (elapsedTicks < 0 || elapsedTicks > _tuning.CorrelationWindowTicks)
                    continue;

                float elapsedSeconds = elapsedTicks * Simulation.TickSeconds;
                float gate = MathF.Min(_tuning.MaxGate, _tuning.Slack + elapsedSeconds * _tuning.MaxCorrelationSpeed);
                float gateSq = gate * gate;

                float distSq = Vector2.DistanceSquared(entry.Position, target.Position);
                if (distSq <= gateSq)
                {
                    if (distSq < bestDistanceSq - 1e-4f ||
                        (MathF.Abs(distSq - bestDistanceSq) <= 1e-4f &&
                         (bestMatchIndex == -1 || entry.Id.Value < _entries[bestMatchIndex].Id.Value)))
                    {
                        bestDistanceSq = distSq;
                        bestMatchIndex = i;
                    }
                }
            }

            if (bestMatchIndex != -1)
            {
                // Correlated with existing last-known contact: reset to Detected and Unknown
                ref Entry correlated = ref _entries[bestMatchIndex];
                correlated.State = ContactState.Current;
                correlated.Level = DetectionLevel.Detected;
                correlated.Classification = ContactClassification.Unknown;
                correlated.ConsecutiveIdentifyTicks = 0;
                correlated.LastIdentifiedTick = -1;
                correlated.Position = target.Position;
                correlated.LastSeenTick = currentTick;
                correlated.ObservedThisStep = true;
                correlated.HasBoundEntity = true;
                correlated.BoundEntityId = target.Id;
                _entityToContactIndex[target.Id] = bestMatchIndex;
                UpdateIdentification(ref correlated, in target, currentTick);
            }
            else
            {
                // New contact creation
                AllocateNewContact(in target, currentTick);
            }
        }
    }

    private void UpdateIdentification(ref Entry entry, in ObservedTarget target, int currentTick)
    {
        if (target.TrueClassification != ContactClassification.Unknown)
        {
            entry.TargetTrueClassification = target.TrueClassification;
        }

        if (entry.Level == DetectionLevel.Identified)
        {
            return;
        }

        if (target.InIdentifyRange)
        {
            if (target.IdentifyTicks > 0)
            {
                entry.RequiredIdentifyTicks = target.IdentifyTicks;
            }

            if (entry.LastIdentifiedTick >= 0 && currentTick - entry.LastIdentifiedTick <= _tuning.EvaluationIntervalTicks)
            {
                int elapsed = currentTick - entry.LastIdentifiedTick;
                if (elapsed <= 0) elapsed = _tuning.EvaluationIntervalTicks;
                entry.ConsecutiveIdentifyTicks += elapsed;
            }
            else
            {
                entry.ConsecutiveIdentifyTicks = _tuning.EvaluationIntervalTicks;
            }
            entry.LastIdentifiedTick = currentTick;

            if (entry.RequiredIdentifyTicks > 0 && entry.ConsecutiveIdentifyTicks >= entry.RequiredIdentifyTicks)
            {
                entry.Level = DetectionLevel.Identified;
                entry.Classification = entry.TargetTrueClassification != ContactClassification.Unknown
                    ? entry.TargetTrueClassification
                    : ContactClassification.Vehicle;
            }
        }
        else
        {
            if (entry.LastIdentifiedTick >= 0 && currentTick - entry.LastIdentifiedTick > _tuning.EvaluationIntervalTicks)
            {
                entry.ConsecutiveIdentifyTicks = 0;
            }
        }
    }

    /// <summary>
    /// Finalizes the detection step: transitions unobserved current contacts to LastKnown,
    /// and expires old contacts past the memory duration.
    /// </summary>
    public void EndDetectionStep(int currentTick)
    {
        // 1. Transition lost tracks to LastKnown
        for (int i = 0; i < _count; i++)
        {
            ref Entry entry = ref _entries[i];
            if (entry.State == ContactState.Current && currentTick - entry.LastSeenTick >= _tuning.EvaluationIntervalTicks)
            {
                entry.State = ContactState.LastKnown;
                if (entry.HasBoundEntity)
                {
                    _entityToContactIndex.Remove(entry.BoundEntityId);
                    entry.HasBoundEntity = false;
                    entry.BoundEntityId = default; // Break internal truth link upon loss
                }
            }

            if (entry.Level != DetectionLevel.Identified)
            {
                if (entry.LastIdentifiedTick >= 0 && currentTick - entry.LastIdentifiedTick > _tuning.EvaluationIntervalTicks)
                {
                    entry.ConsecutiveIdentifyTicks = 0;
                }
            }
        }

        // 2. Expire old contacts
        ExpireOldContacts(currentTick);
    }

    /// <summary>
    /// Advances contact aging and expires contacts past memory duration even on ticks
    /// where detection evaluation does not execute.
    /// </summary>
    public void AgeContacts(int currentTick)
    {
        ExpireOldContacts(currentTick);
    }

    private void ExpireOldContacts(int currentTick)
    {
        int memoryTicks = _tuning.MemoryDurationTicks;
        for (int i = _count - 1; i >= 0; i--)
        {
            ref Entry entry = ref _entries[i];
            if (entry.State == ContactState.LastKnown)
            {
                if (currentTick - entry.LastSeenTick > memoryTicks)
                {
                    RemoveAt(i);
                }
            }
        }
    }

    private void AllocateNewContact(in ObservedTarget target, int currentTick)
    {
        if (_count >= _entries.Length)
        {
            // Evict oldest LastKnown contact to make room for new observation
            int oldestLastKnownIndex = -1;
            int oldestTick = int.MaxValue;

            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].State == ContactState.LastKnown && _entries[i].LastSeenTick < oldestTick)
                {
                    oldestTick = _entries[i].LastSeenTick;
                    oldestLastKnownIndex = i;
                }
            }

            if (oldestLastKnownIndex != -1)
            {
                RemoveAt(oldestLastKnownIndex);
                OverflowEvictionCount++;
            }
            else
            {
                // All contacts are currently active; drop new contact to preserve live tracks
                DroppedDueToCapacityCount++;
                return;
            }
        }

        ContactId newId = new(_nextContactId++);
        if (_nextContactId == 0) _nextContactId = 1; // Safeguard against uint rollover

        int assignedIndex = _count;
        ref Entry newEntry = ref _entries[assignedIndex];
        newEntry = new Entry
        {
            Id = newId,
            State = ContactState.Current,
            Level = DetectionLevel.Detected,
            Classification = ContactClassification.Unknown,
            Position = target.Position,
            LastSeenTick = currentTick,
            ObservedThisStep = true,
            HasBoundEntity = true,
            BoundEntityId = target.Id,
            ConsecutiveIdentifyTicks = 0,
            LastIdentifiedTick = -1,
            RequiredIdentifyTicks = target.IdentifyTicks,
            TargetTrueClassification = target.TrueClassification
        };
        _entityToContactIndex[target.Id] = assignedIndex;
        _count++;
        UpdateIdentification(ref newEntry, in target, currentTick);
    }

    private void RemoveAt(int index)
    {
        ref Entry removed = ref _entries[index];
        if (removed.HasBoundEntity)
        {
            _entityToContactIndex.Remove(removed.BoundEntityId);
        }

        int lastIndex = _count - 1;
        if (index != lastIndex)
        {
            _entries[index] = _entries[lastIndex];
            if (_entries[index].HasBoundEntity)
            {
                _entityToContactIndex[_entries[index].BoundEntityId] = index;
            }
        }
        _entries[lastIndex] = default;
        _count--;
    }

    /// <summary>
    /// Exports the current contacts to a contiguous read-only span without heap allocations.
    /// Sorted canonically by ContactId to guarantee deterministic packet serialization.
    /// </summary>
    public ReadOnlySpan<ContactObservation> ExportContacts()
    {
        for (int i = 0; i < _count; i++)
        {
            ref Entry entry = ref _entries[i];
            _exportBuffer[i] = new ContactObservation(
                entry.Id,
                entry.State,
                entry.Level,
                entry.Classification,
                entry.Position,
                entry.LastSeenTick);
        }

        // Canonical sort by ContactId to avoid publication order variations
        if (_count > 1)
        {
            SortExportBuffer(_exportBuffer, 0, _count - 1);
        }
        return _exportBuffer.AsSpan(0, _count);
    }

    public bool TryGetContact(ContactId id, out ContactObservation contact)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_entries[i].Id.Value == id.Value)
            {
                ref Entry e = ref _entries[i];
                contact = new ContactObservation(e.Id, e.State, e.Level, e.Classification, e.Position, e.LastSeenTick);
                return true;
            }
        }
        contact = default;
        return false;
    }

    public bool TryGetBoundEntity(ContactId id, out EntityId entityId)
    {
        for (int i = 0; i < _count; i++)
        {
            if (_entries[i].Id.Value == id.Value)
            {
                if (_entries[i].State == ContactState.Current && _entries[i].HasBoundEntity)
                {
                    entityId = _entries[i].BoundEntityId;
                    return true;
                }
                break;
            }
        }
        entityId = default;
        return false;
    }

    public bool IsEntityCurrentlyObserved(EntityId entityId)
    {
        if (_entityToContactIndex.TryGetValue(entityId, out int contactIndex) &&
            contactIndex >= 0 && contactIndex < _count)
        {
            return _entries[contactIndex].State == ContactState.Current;
        }
        return false;
    }

    public bool TryGetContactByEntity(EntityId entityId, out ContactObservation contact)
    {
        if (_entityToContactIndex.TryGetValue(entityId, out int contactIndex) &&
            contactIndex >= 0 && contactIndex < _count)
        {
            ref Entry e = ref _entries[contactIndex];
            contact = new ContactObservation(e.Id, e.State, e.Level, e.Classification, e.Position, e.LastSeenTick);
            return true;
        }
        contact = default;
        return false;
    }

    /// <summary>
    /// Retires contact knowledge if this side was actively observing the destroyed entity.
    /// Preserves uncertainty for sides that only had unobserved LastKnown markers or no contact.
    /// </summary>
    public void NotifyEntityDestroyed(EntityId victimId)
    {
        if (_entityToContactIndex.TryGetValue(victimId, out int contactIndex) &&
            contactIndex >= 0 && contactIndex < _count)
        {
            ref Entry entry = ref _entries[contactIndex];
            if (entry.State == ContactState.Current &&
                entry.HasBoundEntity &&
                entry.BoundEntityId.Value == victimId.Value)
            {
                RemoveAt(contactIndex);
            }
        }
    }
}
