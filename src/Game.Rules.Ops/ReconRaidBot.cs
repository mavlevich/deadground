using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

public interface IBotCommandSink
{
    void UpdateKnowledge(PlayerKnowledge knowledge);
    bool EnqueueMove(MoveCommand command);
    bool EnqueueAttack(AttackCommand command);
    void AcknowledgeEvents(int count);
}

public sealed class SimulationBotCommandSink : IBotCommandSink
{
    private Simulation? _simulation;
    private readonly int _player;

    public SimulationBotCommandSink(Simulation? simulation, int player)
    {
        _simulation = simulation;
        _player = player;
    }

    public void SetSimulation(Simulation simulation)
    {
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
    }

    public void UpdateKnowledge(PlayerKnowledge knowledge) => _simulation?.UpdateKnowledge(knowledge);
    public bool EnqueueMove(MoveCommand command) => _simulation?.TryEnqueue(_player, command) ?? false;
    public bool EnqueueAttack(AttackCommand command) => _simulation?.TryEnqueue(_player, command) ?? false;
    public void AcknowledgeEvents(int count) => _simulation?.AcknowledgeCombatEvents(_player, count);
}

public sealed class ReconRaidBot
{
    public const int DecisionIntervalTicks = 10;
    public const float DefaultWeaponRange = 60.0f;
    public const float WaypointArrivalDistance = 8.0f;
    public const int MaxPatrolRetries = 3;

    public static bool IsUnitSearching(PathRequestStatus status) =>
        status is PathRequestStatus.Searching or PathRequestStatus.Pending;

    public static bool IsUnitMoving(PathRequestStatus status) =>
        status is PathRequestStatus.PathFound or PathRequestStatus.Searching or PathRequestStatus.Pending;

    private readonly Vector2[] _patrolWaypoints;
    private readonly Vector2 _guardAnchor;
    private readonly EntityId _guardTankId;
    private readonly EntityId _patrolTankId;
    private readonly EntityId _patrolReconId;

    private readonly float[] _weaponRanges;

    private int _tankPatrolIndex;
    private int _tankPatrolRetries;
    private int _reconPatrolIndex;
    private int _reconPatrolRetries;
    private uint _nextSequence = 1;

    public int TankPatrolIndex => _tankPatrolIndex;
    public int ReconPatrolIndex => _reconPatrolIndex;

    // Fixed capacity state buffers to ensure zero allocations during ticks
    private readonly EntityId[] _unitKeys = new EntityId[32];
    private readonly Vector2[] _lastMoveTarget = new Vector2[32];
    private readonly Vector2[] _lastObservedPosition = new Vector2[32];
    private readonly bool[] _isPhysicallyMoving = new bool[32];
    private readonly int[] _ticksStationary = new int[32];
    private readonly int[] _lastCommandTick = new int[32];
    private readonly ContactId[] _lastAttackTarget = new ContactId[32];
    private int _trackedUnitCount;

    public ReconRaidBot(
        Vector2[] patrolWaypoints,
        Vector2 guardAnchor,
        EntityId guardTankId,
        EntityId patrolTankId,
        EntityId patrolReconId,
        UnitCatalog? catalog = null)
    {
        _patrolWaypoints = patrolWaypoints ?? throw new ArgumentNullException(nameof(patrolWaypoints));
        if (_patrolWaypoints.Length == 0)
            throw new ArgumentException("Patrol waypoints cannot be empty.", nameof(patrolWaypoints));

        _guardAnchor = guardAnchor;
        _guardTankId = guardTankId;
        _patrolTankId = patrolTankId;
        _patrolReconId = patrolReconId;

        int unitCount = catalog?.Units.Length ?? 32;
        _weaponRanges = new float[Math.Max(32, unitCount)];
        for (int i = 0; i < _weaponRanges.Length; i++)
            _weaponRanges[i] = DefaultWeaponRange;

        if (catalog is not null && catalog.Weapons is not null)
        {
            Dictionary<string, float> weaponMap = new(StringComparer.Ordinal);
            foreach (WeaponData w in catalog.Weapons)
            {
                weaponMap[w.Id] = w.Range;
            }

            for (int i = 0; i < catalog.Units.Length; i++)
            {
                UnitDefinition u = catalog.Units[i];
                if (u.Weapon is not null && weaponMap.TryGetValue(u.Weapon, out float range))
                {
                    _weaponRanges[i] = range;
                }
            }
        }
    }

    public float GetWeaponRange(ushort definition)
    {
        if (definition < _weaponRanges.Length)
            return _weaponRanges[definition];
        return DefaultWeaponRange;
    }

    public void Update(PlayerKnowledge knowledge, int tick, IBotCommandSink sink)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(sink);

        if (tick % DecisionIntervalTicks != 0) return;

        ReadOnlySpan<OwnUnitObservation> ownUnits = knowledge.OwnUnits;
        ReadOnlySpan<ContactObservation> contacts = knowledge.Contacts;

        // 1. Update physical movement tracking for each unit
        for (int i = 0; i < ownUnits.Length; i++)
        {
            OwnUnitObservation unit = ownUnits[i];
            if (unit.Health == 0) continue;

            int slot = GetOrCreateUnitSlot(unit.Id);
            float distMoved = Vector2.Distance(unit.Position, _lastObservedPosition[slot]);
            if (_lastCommandTick[slot] == 0)
            {
                _lastObservedPosition[slot] = unit.Position;
                _isPhysicallyMoving[slot] = false;
                _ticksStationary[slot] = 0;
            }
            else if (distMoved > 0.05f)
            {
                _lastObservedPosition[slot] = unit.Position;
                _isPhysicallyMoving[slot] = true;
                _ticksStationary[slot] = 0;
            }
            else
            {
                _isPhysicallyMoving[slot] = false;
                _ticksStationary[slot] += DecisionIntervalTicks;
            }
        }

        // 2. React to combat events (e.g. TookFire from unseen shooter)
        ReadOnlySpan<CombatEventObservation> events = knowledge.Events;
        for (int e = 0; e < events.Length; e++)
        {
            ref readonly CombatEventObservation ev = ref events[e];
            if (ev.Kind == CombatEventKind.TookFire)
            {
                Vector2 targetPos = new(ev.Target.X, ev.Target.Z);
                Vector2 shooterPos = new(ev.Origin.X, ev.Origin.Z);
                for (int u = 0; u < ownUnits.Length; u++)
                {
                    OwnUnitObservation unit = ownUnits[u];
                    if (unit.Health == 0) continue;
                    if (Vector2.Distance(unit.Position, targetPos) < 4.0f)
                    {
                        // This unit took fire. If it is currently not attacking or moving, react towards shooter
                        if (unit.AttackStatus != AttackExecutionStatus.Aiming && !IsUnitMoving(unit.Id, unit.NavStatus))
                        {
                            Vector2 diff = shooterPos - unit.Position;
                            float len = diff.Length();
                            if (len > 0.1f)
                            {
                                Vector2 moveToward = unit.Position + (diff / len) * MathF.Min(12.0f, len * 0.5f);
                                sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, moveToward));
                                RecordLastMoveTarget(unit.Id, moveToward, tick);
                            }
                        }
                        break;
                    }
                }
            }
        }

        // 3. Make operational decisions for each active unit
        for (int i = 0; i < ownUnits.Length; i++)
        {
            OwnUnitObservation unit = ownUnits[i];
            if (unit.Health == 0) continue;

            if (unit.Ammunition > 0)
            {
                ProcessArmedUnit(unit, contacts, tick, sink);
            }
            else if (unit.Id.Value == _patrolReconId.Value)
            {
                ProcessReconUnit(unit, contacts, tick, sink);
            }
        }

        // 4. Acknowledge consumed combat events
        if (knowledge.EventCount > 0)
        {
            sink.AcknowledgeEvents(knowledge.EventCount);
        }
    }

    private void ProcessArmedUnit(
        in OwnUnitObservation unit,
        ReadOnlySpan<ContactObservation> contacts,
        int tick,
        IBotCommandSink sink)
    {
        // 1. Target selection: find nearest Current contact
        ContactObservation bestContact = default;
        float bestDistanceSq = float.MaxValue;
        bool hasCurrentContact = false;

        ContactObservation bestLastKnown = default;
        float bestLastKnownDistSq = float.MaxValue;
        bool hasLastKnownContact = false;

        for (int c = 0; c < contacts.Length; c++)
        {
            ContactObservation contact = contacts[c];
            float distSq = Vector2.DistanceSquared(unit.Position, contact.Position);

            if (contact.State == ContactState.Current)
            {
                if (distSq < bestDistanceSq || (Math.Abs(distSq - bestDistanceSq) < 0.01f && contact.Id.Value < bestContact.Id.Value))
                {
                    bestDistanceSq = distSq;
                    bestContact = contact;
                    hasCurrentContact = true;
                }
            }
            else if (contact.State == ContactState.LastKnown)
            {
                if (distSq < bestLastKnownDistSq)
                {
                    bestLastKnownDistSq = distSq;
                    bestLastKnown = contact;
                    hasLastKnownContact = true;
                }
            }
        }

        // 2. Engagement logic derived from weapon catalog data
        float weaponRange = GetWeaponRange(unit.Definition);
        float approachThreshold = MathF.Max(5.0f, weaponRange - 2.0f);

        if (hasCurrentContact)
        {
            float distance = MathF.Sqrt(bestDistanceSq);

            if (distance <= approachThreshold)
            {
                int slot = GetOrCreateUnitSlot(unit.Id);
                // Within weapon range: stop and fire
                if (IsUnitMoving(unit.Id, unit.NavStatus))
                {
                    sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, unit.Position));
                    RecordLastMoveTarget(unit.Id, unit.Position, tick);
                }

                if (_lastAttackTarget[slot].Value != bestContact.Id.Value ||
                    unit.AttackStatus != AttackExecutionStatus.Aiming ||
                    unit.TargetContactId.Value != bestContact.Id.Value)
                {
                    sink.EnqueueAttack(new AttackCommand(_nextSequence++, tick, unit.Id, bestContact.Id));
                    RecordLastAttackTarget(unit.Id, bestContact.Id);
                }
            }
            else
            {
                // Out of weapon range: approach contact position
                if (!IsUnitActivelyMovingTowards(unit.Id, bestContact.Position, 8.0f))
                {
                    sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, bestContact.Position));
                    RecordLastMoveTarget(unit.Id, bestContact.Position, tick);
                    int slot = GetOrCreateUnitSlot(unit.Id);
                    _lastAttackTarget[slot] = default;
                }
            }
            return;
        }

        // 3. Pursuit of last-known position
        if (hasLastKnownContact && bestLastKnownDistSq < 100f * 100f)
        {
            float lastKnownDist = MathF.Sqrt(bestLastKnownDistSq);
            if (lastKnownDist > 10.0f)
            {
                if (!IsUnitActivelyMovingTowards(unit.Id, bestLastKnown.Position, 8.0f))
                {
                    sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, bestLastKnown.Position));
                    RecordLastMoveTarget(unit.Id, bestLastKnown.Position, tick);
                }
                return;
            }
        }

        // 4. Default role behavior (Guard vs Patrol)
        if (unit.Id.Value == _guardTankId.Value)
        {
            float guardDist = Vector2.Distance(unit.Position, _guardAnchor);
            if (guardDist > 16.0f && !IsUnitActivelyMovingTowards(unit.Id, _guardAnchor, 8.0f))
            {
                sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, _guardAnchor));
                RecordLastMoveTarget(unit.Id, _guardAnchor, tick);
            }
        }
        else if (unit.Id.Value == _patrolTankId.Value)
        {
            Vector2 targetWp = _patrolWaypoints[_tankPatrolIndex];
            float distToWp = Vector2.Distance(unit.Position, targetWp);

            if (distToWp <= WaypointArrivalDistance)
            {
                _tankPatrolIndex = (_tankPatrolIndex + 1) % _patrolWaypoints.Length;
                _tankPatrolRetries = 0;
                targetWp = _patrolWaypoints[_tankPatrolIndex];
                sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, targetWp));
                RecordLastMoveTarget(unit.Id, targetWp, tick);
            }
            else if (unit.NavStatus is PathRequestStatus.NoPath or PathRequestStatus.InvalidDestination)
            {
                _tankPatrolRetries++;
                if (_tankPatrolRetries >= MaxPatrolRetries)
                {
                    _tankPatrolIndex = (_tankPatrolIndex + 1) % _patrolWaypoints.Length;
                    _tankPatrolRetries = 0;
                    targetWp = _patrolWaypoints[_tankPatrolIndex];
                }
                sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, targetWp));
                RecordLastMoveTarget(unit.Id, targetWp, tick);
            }
            else if (IsUnitSearching(unit.NavStatus))
            {
                // Route search in progress, wait
            }
            else if (!IsUnitActivelyMovingTowards(unit.Id, targetWp, WaypointArrivalDistance))
            {
                sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, targetWp));
                RecordLastMoveTarget(unit.Id, targetWp, tick);
            }
        }
    }

    private void ProcessReconUnit(
        in OwnUnitObservation unit,
        ReadOnlySpan<ContactObservation> contacts,
        int tick,
        IBotCommandSink sink)
    {
        // Unarmed scout: hold patrol route; if enemy comes within 40m, stop advancing closer
        bool enemyTooClose = false;
        for (int c = 0; c < contacts.Length; c++)
        {
            if (contacts[c].State == ContactState.Current &&
                Vector2.DistanceSquared(unit.Position, contacts[c].Position) < 40f * 40f)
            {
                enemyTooClose = true;
                break;
            }
        }

        if (enemyTooClose)
        {
            if (IsUnitMoving(unit.Id, unit.NavStatus))
            {
                sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, unit.Position));
                RecordLastMoveTarget(unit.Id, unit.Position, tick);
            }
            return;
        }

        Vector2 targetWp = _patrolWaypoints[_reconPatrolIndex];
        float distToWp = Vector2.Distance(unit.Position, targetWp);

        if (distToWp <= WaypointArrivalDistance)
        {
            _reconPatrolIndex = (_reconPatrolIndex + 1) % _patrolWaypoints.Length;
            _reconPatrolRetries = 0;
            targetWp = _patrolWaypoints[_reconPatrolIndex];
            sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, targetWp));
            RecordLastMoveTarget(unit.Id, targetWp, tick);
        }
        else if (unit.NavStatus is PathRequestStatus.NoPath or PathRequestStatus.InvalidDestination)
        {
            _reconPatrolRetries++;
            if (_reconPatrolRetries >= MaxPatrolRetries)
            {
                _reconPatrolIndex = (_reconPatrolIndex + 1) % _patrolWaypoints.Length;
                _reconPatrolRetries = 0;
                targetWp = _patrolWaypoints[_reconPatrolIndex];
            }
            sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, targetWp));
            RecordLastMoveTarget(unit.Id, targetWp, tick);
        }
        else if (IsUnitSearching(unit.NavStatus))
        {
            // Route search in progress, wait
        }
        else if (!IsUnitActivelyMovingTowards(unit.Id, targetWp, WaypointArrivalDistance))
        {
            sink.EnqueueMove(new MoveCommand(_nextSequence++, tick, unit.Id, targetWp));
            RecordLastMoveTarget(unit.Id, targetWp, tick);
        }
    }

    public bool IsUnitActivelyMovingTowards(EntityId id, Vector2 target, float tolerance)
    {
        int slot = FindUnitSlot(id);
        if (slot < 0) return false;

        if (Vector2.Distance(_lastMoveTarget[slot], target) > tolerance)
            return false;

        // If the command was sent on the current or immediate previous decision tick, allow time to start
        if (_ticksStationary[slot] == 0)
            return true;

        return _isPhysicallyMoving[slot];
    }

    public bool IsUnitMoving(EntityId id, PathRequestStatus navStatus)
    {
        if (IsUnitSearching(navStatus))
            return true;

        if (navStatus != PathRequestStatus.PathFound)
            return false;

        int slot = FindUnitSlot(id);
        if (slot < 0) return false;

        if (_ticksStationary[slot] > 0 && !_isPhysicallyMoving[slot])
            return false;

        return true;
    }

    private int FindUnitSlot(EntityId id)
    {
        for (int i = 0; i < _trackedUnitCount; i++)
        {
            if (_unitKeys[i].Value == id.Value)
                return i;
        }
        return -1;
    }

    private int GetOrCreateUnitSlot(EntityId id)
    {
        int slot = FindUnitSlot(id);
        if (slot >= 0) return slot;

        if (_trackedUnitCount < _unitKeys.Length)
        {
            slot = _trackedUnitCount++;
            _unitKeys[slot] = id;
            return slot;
        }
        return 0; // Fallback to avoid out of bounds
    }

    private void RecordLastMoveTarget(EntityId id, Vector2 target, int tick)
    {
        int slot = GetOrCreateUnitSlot(id);
        _lastMoveTarget[slot] = target;
        _lastCommandTick[slot] = tick;
        _ticksStationary[slot] = 0;
    }

    private void RecordLastAttackTarget(EntityId id, ContactId contactId)
    {
        int slot = GetOrCreateUnitSlot(id);
        _lastAttackTarget[slot] = contactId;
    }
}
