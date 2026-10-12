using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Server-authoritative direct-fire combat system.
/// Handles target validation, stationary firing constraints, line-of-sight obstruction,
/// tick-based reload, ammo consumption, single-application damage, deterministic unit destruction,
/// and knowledge-filtered presentation events.
/// Strictly allocation-free during simulation ticks.
/// </summary>
public sealed class CombatSystem : ISimulationSystem
{
    public const int DefaultMaxEventsPerPlayer = 64;
    public const float RangeToleranceMargin = 0.5f;

    private readonly StaticGeometry _geometry;
    private readonly IUnitCombatCatalog _combatCatalog;
    private readonly IUnitPerceptionCatalog _perceptionCatalog;
    private readonly DetectionSystem _detection;
    private readonly NavigationSystem? _navigation;
    private readonly LineOfSightContext _losContext;

    private readonly int _maxUnits;
    private readonly int _maxSides;
    private readonly int _maxEventsPerPlayer;

    private readonly EntityId[] _destroyedBuffer;
    private int _destroyedCount;

    private readonly CombatEventObservation[] _playerEvents;
    private readonly int[] _playerEventCount;
    private readonly long[] _playerEventOverflow;
    private readonly bool[] _admittedPlayers;

    private uint _nextEventId = 1;

    public int TotalShotsFired { get; private set; }
    public int TotalDamageApplied { get; private set; }
    public int TotalUnitsDestroyed { get; private set; }
    public int ActiveAttackersCount { get; private set; }

    public CombatSystem(
        StaticGeometry geometry,
        IUnitCombatCatalog combatCatalog,
        IUnitPerceptionCatalog perceptionCatalog,
        DetectionSystem detection,
        NavigationSystem? navigation = null,
        int maxUnits = 1024,
        int maxSides = Simulation.MaxPlayers,
        int maxEventsPerPlayer = DefaultMaxEventsPerPlayer)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(combatCatalog);
        ArgumentNullException.ThrowIfNull(perceptionCatalog);
        ArgumentNullException.ThrowIfNull(detection);

        _geometry = geometry;
        _combatCatalog = combatCatalog;
        _perceptionCatalog = perceptionCatalog;
        _detection = detection;
        _navigation = navigation;
        _maxUnits = maxUnits;
        _maxSides = maxSides;
        _maxEventsPerPlayer = maxEventsPerPlayer;

        _losContext = new LineOfSightContext(geometry.Structures.Count);
        _destroyedBuffer = new EntityId[maxUnits];
        _playerEvents = new CombatEventObservation[maxSides * maxEventsPerPlayer];
        _playerEventCount = new int[maxSides];
        _playerEventOverflow = new long[maxSides];
        _admittedPlayers = new bool[maxSides];
        Array.Fill(_admittedPlayers, true);
    }

    /// <summary>
    /// Validates and executes an explicit attack order from a client targeting a ContactId.
    /// Never accepts enemy EntityIds from a client.
    /// </summary>
    public bool TryExecuteAttackOrder(int player, AttackCommand command, WorldState world)
    {
        if (player < 1 || player > _maxSides || command.Sequence == 0 || command.Target.Value == 0)
            return false;

        // Attacker ownership validation
        if (!world.TryGetUnit(command.Unit, out UnitState attacker) || attacker.Owner != player)
            return false;

        // Verify attacker has an operational weapon
        if (!_combatCatalog.TryGetProfile(attacker.Definition, out UnitCombatProfile attackerProfile) ||
            attackerProfile.Weapon is null)
        {
            return false;
        }

        // Contact validation: must be a currently observed ContactId belonging to this player's knowledge
        SideContactTracker tracker = _detection.GetTracker(player);
        if (!tracker.TryGetContact(command.Target, out ContactObservation contact) ||
            contact.State != ContactState.Current)
        {
            return false;
        }

        // Range boundary check on order issue: reject orders on targets beyond weapon range plus margin.
        // Rejection preserves existing valid orders (including active attack or ongoing movement).
        float distToContact = Vector2.Distance(attacker.Position, contact.Position);
        if (distToContact > attackerProfile.Weapon.Range + RangeToleranceMargin)
        {
            return false;
        }

        // Resolve internal truth link on the server only
        if (!tracker.TryGetBoundEntity(command.Target, out EntityId targetEntityId))
            return false;

        if (!world.TryGetUnit(targetEntityId, out UnitState targetUnit) || !world.AreHostile(player, targetUnit.Owner))
            return false;

        // Cancel existing movement order on attacker and stop immediately
        world.UpdateUnitNavigation(command.Unit, attacker.Position, attacker.Position, false);
        _navigation?.ClearUnit(command.Unit);

        // Apply validated attack order
        if (world.TryGetCombatState(command.Unit, out WorldState.CombatState cs))
        {
            cs.HasAttackOrder = true;
            cs.TargetContactId = command.Target;
            cs.TargetEntityId = targetEntityId;
            cs.Status = AttackExecutionStatus.Aiming;
            world.SetCombatState(command.Unit, cs);
            return true;
        }

        return false;
    }

    public void Update(WorldState world, int tick)
    {
        ActiveAttackersCount = 0;
        _destroyedCount = 0;

        int unitCount = world.Count;

        // Step 1: Advance reload timers and resolve direct weapons fire
        for (int i = 0; i < unitCount; i++)
        {
            UnitState attacker = world.GetUnitAt(i);
            if (!_combatCatalog.TryGetProfile(attacker.Definition, out UnitCombatProfile profile) ||
                profile.Weapon is null)
            {
                continue;
            }

            if (!world.TryGetCombatState(attacker.Id, out WorldState.CombatState cs))
                continue;

            WeaponDefinition weapon = profile.Weapon;

            // Tick-based reload decrement
            if (cs.ReloadTicksRemaining > 0)
            {
                cs.ReloadTicksRemaining--;
            }

            if (!cs.HasAttackOrder)
            {
                world.SetCombatState(attacker.Id, cs);
                continue;
            }

            ActiveAttackersCount++;

            // Stationary firing only: a moving attacker cannot fire
            if (attacker.Moving)
            {
                cs.Status = AttackExecutionStatus.None;
                world.SetCombatState(attacker.Id, cs);
                continue;
            }

            // Contact check:
            // Drop order only when contact is expired/removed (e.g. MemoryDurationTicks elapsed or destroyed)
            SideContactTracker tracker = _detection.GetTracker(attacker.Owner);
            if (!tracker.TryGetContact(cs.TargetContactId, out ContactObservation contact))
            {
                cs.HasAttackOrder = false;
                cs.TargetContactId = default;
                cs.TargetEntityId = default;
                cs.Status = AttackExecutionStatus.None;
                world.SetCombatState(attacker.Id, cs);
                continue;
            }

            // On LastKnown, keep the order and hold fire with status TargetLost
            if (contact.State != ContactState.Current)
            {
                cs.Status = AttackExecutionStatus.TargetLost;
                world.SetCombatState(attacker.Id, cs);
                continue;
            }

            // When contact is Current again: re-resolve bound entity (it may have been re-correlated)
            if (!tracker.TryGetBoundEntity(cs.TargetContactId, out EntityId targetEntityId) ||
                !world.TryGetUnit(targetEntityId, out UnitState targetUnit) ||
                !world.AreHostile(attacker.Owner, targetUnit.Owner))
            {
                cs.HasAttackOrder = false;
                cs.TargetContactId = default;
                cs.TargetEntityId = default;
                cs.Status = AttackExecutionStatus.None;
                world.SetCombatState(attacker.Id, cs);
                continue;
            }

            cs.TargetEntityId = targetEntityId;

            // Ammunition check
            if (cs.Ammunition <= 0)
            {
                cs.Status = AttackExecutionStatus.NoAmmo;
                world.SetCombatState(attacker.Id, cs);
                continue;
            }

            // Range boundary check: out of range maintains order but holds fire
            float dist = Vector2.Distance(attacker.Position, targetUnit.Position);
            if (dist > weapon.Range)
            {
                cs.Status = AttackExecutionStatus.OutOfRange;
                world.SetCombatState(attacker.Id, cs);
                continue;
            }

            // Revalidate line of sight from muzzle to vertical target observation points
            float attackerGroundY = _geometry.HeightMap.GetHeight(attacker.Position.X, attacker.Position.Y);
            Vector3 muzzlePos = new(attacker.Position.X, attackerGroundY + weapon.MuzzleHeight, attacker.Position.Y);

            float targetGroundY = _geometry.HeightMap.GetHeight(targetUnit.Position.X, targetUnit.Position.Y);
            bool hasClearShot = false;
            Vector3 hitPoint = Vector3.Zero;

            if (_perceptionCatalog.TryGetProfile(targetUnit.Definition, out UnitPerceptionProfile targetPerception) &&
                targetPerception.TargetPoints is { RelativeHeights.Length: > 0 } points)
            {
                // Prioritize top target point (turret/roof), then lower points
                for (int p = points.RelativeHeights.Length - 1; p >= 0; p--)
                {
                    Vector3 testPoint = new(targetUnit.Position.X, targetGroundY + points.RelativeHeights[p], targetUnit.Position.Y);
                    if (_geometry.IsLineOfSightClear(muzzlePos, testPoint, _losContext, out _))
                    {
                        hasClearShot = true;
                        hitPoint = testPoint;
                        break;
                    }
                }
            }
            else
            {
                Vector3 fallbackTarget = new(targetUnit.Position.X, targetGroundY + 1.5f, targetUnit.Position.Y);
                if (_geometry.IsLineOfSightClear(muzzlePos, fallbackTarget, _losContext, out _))
                {
                    hasClearShot = true;
                    hitPoint = fallbackTarget;
                }
            }

            if (!hasClearShot)
            {
                cs.Status = AttackExecutionStatus.Obstructed;
                world.SetCombatState(attacker.Id, cs);
                continue;
            }

            // Firing line is clear; check reload readiness
            if (cs.ReloadTicksRemaining > 0)
            {
                cs.Status = AttackExecutionStatus.Aiming;
                world.SetCombatState(attacker.Id, cs);
                continue;
            }

            // Execute shot: consume round, start reload, apply damage exactly once
            cs.Ammunition--;
            cs.ReloadTicksRemaining = weapon.ReloadDurationTicks;
            cs.Status = AttackExecutionStatus.Aiming;
            world.SetCombatState(attacker.Id, cs);

            TotalShotsFired++;

            // Apply fixed damage
            world.ApplyDamage(targetUnit.Id, weapon.Damage, out bool isDestroyed);
            TotalDamageApplied += weapon.Damage;

            // Record knowledge-filtered ShotFired / TookFire presentation event
            RecordShotEvent(
                tick,
                weapon.PresentationId,
                attacker.Id,
                attacker.Owner,
                muzzlePos,
                targetUnit.Id,
                targetUnit.Owner,
                hitPoint);

            if (isDestroyed)
            {
                bool alreadyDestroyed = false;
                for (int d = 0; d < _destroyedCount; d++)
                {
                    if (_destroyedBuffer[d].Value == targetUnit.Id.Value)
                    {
                        alreadyDestroyed = true;
                        break;
                    }
                }

                if (!alreadyDestroyed)
                {
                    TotalUnitsDestroyed++;
                    RecordDestroyedEvent(
                        tick,
                        targetUnit.Definition,
                        targetUnit.Id,
                        targetUnit.Owner,
                        targetUnit.Position);

                    if (_destroyedCount < _destroyedBuffer.Length)
                    {
                        _destroyedBuffer[_destroyedCount++] = targetUnit.Id;
                    }
                }
            }
        }

        // Step 2: Deterministic destruction pass
        if (_destroyedCount > 0)
        {
            // Sort by EntityId ascending using in-place insertion sort to guarantee zero allocations
            if (_destroyedCount > 1)
            {
                for (int i = 1; i < _destroyedCount; i++)
                {
                    EntityId key = _destroyedBuffer[i];
                    int j = i - 1;
                    while (j >= 0 && _destroyedBuffer[j].Value > key.Value)
                    {
                        _destroyedBuffer[j + 1] = _destroyedBuffer[j];
                        j--;
                    }
                    _destroyedBuffer[j + 1] = key;
                }
            }

            for (int d = 0; d < _destroyedCount; d++)
            {
                EntityId destroyedId = _destroyedBuffer[d];
                world.RemoveUnit(destroyedId);
                _navigation?.ClearUnit(destroyedId);
                _detection.NotifyEntityDestroyed(destroyedId);

                // Cancel attack orders on any surviving units whose observable contact was retired
                for (int u = 0; u < world.Count; u++)
                {
                    UnitState survivor = world.GetUnitAt(u);
                    if (world.TryGetCombatState(survivor.Id, out WorldState.CombatState survivorCs) &&
                        survivorCs.HasAttackOrder &&
                        survivorCs.TargetEntityId.Value == destroyedId.Value)
                    {
                        SideContactTracker survivorTracker = _detection.GetTracker(survivor.Owner);
                        if (!survivorTracker.TryGetContact(survivorCs.TargetContactId, out _))
                        {
                            // Contact was observed and retired: clear order immediately
                            survivorCs.HasAttackOrder = false;
                            survivorCs.TargetContactId = default;
                            survivorCs.TargetEntityId = default;
                            survivorCs.Status = AttackExecutionStatus.None;
                            world.SetCombatState(survivor.Id, survivorCs);
                        }
                        else
                        {
                            // Contact remains observable (e.g. LastKnown): retain uncertainty and do not leak
                            // death through public order state. Invalidate cached truth link.
                            survivorCs.TargetEntityId = default;
                            survivorCs.Status = AttackExecutionStatus.TargetLost;
                            world.SetCombatState(survivor.Id, survivorCs);
                        }
                    }
                }
            }
        }
    }

    private void RecordShotEvent(
        int tick,
        ushort presentationId,
        EntityId attackerId,
        int attackerOwner,
        Vector3 muzzlePos,
        EntityId targetId,
        int targetOwner,
        Vector3 hitPoint)
    {
        uint eventId = _nextEventId++;
        if (_nextEventId == 0) _nextEventId = 1;

        for (int p = 1; p <= _maxSides; p++)
        {
            if (p == attackerOwner)
            {
                // Attacker owner always sees own shot with shooter unit ID for recoil
                CombatEventObservation ev = new(
                    eventId,
                    tick,
                    CombatEventKind.ShotFired,
                    presentationId,
                    muzzlePos,
                    hitPoint,
                    attackerId);
                AppendEvent(p, ev);
            }
            else if (p == targetOwner)
            {
                // Target owner sees shot if attacker is currently observed by target owner.
                // Do not expose enemy shooter EntityId to opponents.
                if (_detection.IsEntityObservedBy(p, attackerId))
                {
                    CombatEventObservation ev = new(
                        eventId,
                        tick,
                        CombatEventKind.ShotFired,
                        presentationId,
                        muzzlePos,
                        hitPoint,
                        default);
                    AppendEvent(p, ev);
                }
                else
                {
                    // Attacker is hidden from target owner: send TookFire event with victim hit point
                    // and coarse incoming bearing. Never disclose shooter EntityId or exact coordinates.
                    Vector3 delta = muzzlePos - hitPoint;
                    float angle = MathF.Atan2(delta.Z, delta.X);
                    // Quantize bearing into 16 discrete sectors (22.5 degrees each / PI/8)
                    float sectorSize = MathF.PI / 8f;
                    float coarseAngle = MathF.Round(angle / sectorSize) * sectorSize;
                    float coarseDistance = 50.0f;
                    Vector3 coarseOrigin = new(
                        hitPoint.X + MathF.Cos(coarseAngle) * coarseDistance,
                        hitPoint.Y,
                        hitPoint.Z + MathF.Sin(coarseAngle) * coarseDistance);

                    CombatEventObservation tookFire = new(
                        eventId,
                        tick,
                        CombatEventKind.TookFire,
                        presentationId,
                        coarseOrigin,
                        hitPoint,
                        default);
                    AppendEvent(p, tookFire);
                }
            }
            else
            {
                // Bystander player only sees shot if both attacker and target are currently observed
                if (_detection.IsEntityObservedBy(p, attackerId) && _detection.IsEntityObservedBy(p, targetId))
                {
                    CombatEventObservation ev = new(
                        eventId,
                        tick,
                        CombatEventKind.ShotFired,
                        presentationId,
                        muzzlePos,
                        hitPoint,
                        default);
                    AppendEvent(p, ev);
                }
            }
        }
    }

    private void RecordDestroyedEvent(
        int tick,
        ushort definition,
        EntityId targetId,
        int targetOwner,
        Vector2 targetPos)
    {
        uint eventId = _nextEventId++;
        if (_nextEventId == 0) _nextEventId = 1;

        float groundY = _geometry.HeightMap.GetHeight(targetPos.X, targetPos.Y);
        Vector3 targetPos3D = new(targetPos.X, groundY, targetPos.Y);

        for (int p = 1; p <= _maxSides; p++)
        {
            if (p == targetOwner)
            {
                // Victim owner always receives own destruction notification with exact definition
                CombatEventObservation ev = new(
                    eventId,
                    tick,
                    CombatEventKind.UnitDestroyed,
                    definition,
                    Vector3.Zero,
                    targetPos3D,
                    default);
                AppendEvent(p, ev);
            }
            else
            {
                // Opponent only receives destruction notification if target is currently observed
                if (_detection.IsEntityObservedBy(p, targetId))
                {
                    ushort oppPresId = 0;
                    if (_detection.TryGetContactByEntity(p, targetId, out ContactObservation contact) &&
                        contact.Classification != ContactClassification.Unknown)
                    {
                        oppPresId = definition;
                    }

                    CombatEventObservation ev = new(
                        eventId,
                        tick,
                        CombatEventKind.UnitDestroyed,
                        oppPresId,
                        Vector3.Zero,
                        targetPos3D,
                        default);
                    AppendEvent(p, ev);
                }
            }
        }
    }

    private void AppendEvent(int player, CombatEventObservation ev)
    {
        int sideIndex = player - 1;
        if (sideIndex < 0 || sideIndex >= _maxSides || !_admittedPlayers[sideIndex]) return;

        int count = _playerEventCount[sideIndex];
        if (count < _maxEventsPerPlayer)
        {
            _playerEvents[sideIndex * _maxEventsPerPlayer + count] = ev;
            _playerEventCount[sideIndex] = count + 1;
        }
        else
        {
            _playerEventOverflow[sideIndex]++;
        }
    }

    /// <summary>
    /// Returns the pending combat presentation events for a player without clearing them.
    /// Free of side effects: multiple calls return the same events.
    /// Strictly allocation-free.
    /// </summary>
    public ReadOnlySpan<CombatEventObservation> GetEvents(int player)
    {
        int sideIndex = player - 1;
        if (sideIndex < 0 || sideIndex >= _maxSides) return [];

        int count = _playerEventCount[sideIndex];
        return new ReadOnlySpan<CombatEventObservation>(_playerEvents, sideIndex * _maxEventsPerPlayer, count);
    }

    /// <summary>
    /// Acknowledges and drops up to count events after successful network transmission.
    /// </summary>
    public void AcknowledgeEvents(int player, int count)
    {
        int sideIndex = player - 1;
        if (sideIndex < 0 || sideIndex >= _maxSides || count <= 0) return;

        int currentCount = _playerEventCount[sideIndex];
        if (count >= currentCount)
        {
            _playerEventCount[sideIndex] = 0;
            return;
        }

        int remaining = currentCount - count;
        int baseOffset = sideIndex * _maxEventsPerPlayer;
        Array.Copy(_playerEvents, baseOffset + count, _playerEvents, baseOffset, remaining);
        _playerEventCount[sideIndex] = remaining;
    }

    /// <summary>
    /// Clears any pending events for a player (used on disconnect / reconnect).
    /// </summary>
    public void ResetPlayer(int player)
    {
        int sideIndex = player - 1;
        if (sideIndex >= 0 && sideIndex < _maxSides)
        {
            _playerEventCount[sideIndex] = 0;
        }
    }

    public void SetPlayerAdmitted(int player, bool admitted)
    {
        int sideIndex = player - 1;
        if (sideIndex >= 0 && sideIndex < _maxSides)
        {
            _admittedPlayers[sideIndex] = admitted;
            if (!admitted)
            {
                _playerEventCount[sideIndex] = 0;
            }
        }
    }

    public bool IsPlayerAdmitted(int player)
    {
        int sideIndex = player - 1;
        return sideIndex >= 0 && sideIndex < _maxSides && _admittedPlayers[sideIndex];
    }

    /// <summary>
    /// Reads and clears the pending combat presentation events for a player.
    /// </summary>
    public ReadOnlySpan<CombatEventObservation> DrainEvents(int player, Span<CombatEventObservation> destination)
    {
        int sideIndex = player - 1;
        if (sideIndex < 0 || sideIndex >= _maxSides) return [];

        int count = _playerEventCount[sideIndex];
        int copyCount = Math.Min(count, destination.Length);
        int baseOffset = sideIndex * _maxEventsPerPlayer;

        for (int i = 0; i < copyCount; i++)
        {
            destination[i] = _playerEvents[baseOffset + i];
        }

        AcknowledgeEvents(player, copyCount);
        return destination[..copyCount];
    }

    public int GetPendingEventCount(int player)
    {
        int sideIndex = player - 1;
        if (sideIndex < 0 || sideIndex >= _maxSides) return 0;
        return _playerEventCount[sideIndex];
    }
}
