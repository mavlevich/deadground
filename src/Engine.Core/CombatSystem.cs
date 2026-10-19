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
    public const float TurretAlignmentTolerance = 0.05f;

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
    private void OrderUnitToMove(int player, EntityId unitId, Vector2 destination, WorldState world)
    {
        if (_navigation is not null)
        {
            _navigation.EnqueueCommand(player, new MoveCommand(0, 0, unitId, destination), world);
        }
        else
        {
            world.SetMoveTarget(player, unitId, destination);
        }
    }

    private bool HasLineOfSightToTarget(UnitState attacker, WeaponDefinition weapon, UnitState targetUnit, out Vector3 hitPoint, out Vector3 muzzlePos)
    {
        float attackerGroundY = _geometry.HeightMap.GetHeight(attacker.Position.X, attacker.Position.Y);
        muzzlePos = new Vector3(attacker.Position.X, attackerGroundY + weapon.MuzzleHeight, attacker.Position.Y);

        float targetGroundY = _geometry.HeightMap.GetHeight(targetUnit.Position.X, targetUnit.Position.Y);
        hitPoint = Vector3.Zero;

        if (_perceptionCatalog.TryGetProfile(targetUnit.Definition, out UnitPerceptionProfile targetPerception) &&
            targetPerception.TargetPoints is { RelativeHeights.Length: > 0 } points)
        {
            for (int p = points.RelativeHeights.Length - 1; p >= 0; p--)
            {
                Vector3 testPoint = new(targetUnit.Position.X, targetGroundY + points.RelativeHeights[p], targetUnit.Position.Y);
                if (_geometry.IsLineOfSightClear(muzzlePos, testPoint, _losContext, out _))
                {
                    hitPoint = testPoint;
                    return true;
                }
            }
            return false;
        }

        Vector3 fallbackTarget = new(targetUnit.Position.X, targetGroundY + 1.5f, targetUnit.Position.Y);
        if (_geometry.IsLineOfSightClear(muzzlePos, fallbackTarget, _losContext, out _))
        {
            hitPoint = fallbackTarget;
            return true;
        }

        return false;
    }

    private static int GetTargetClassPriority(ContactClassification classification) => classification switch
    {
        ContactClassification.Tank => 0,
        ContactClassification.Apc => 1,
        ContactClassification.Recon => 2,
        _ => 3
    };

    public static float NormalizeYaw(float yaw)
    {
        yaw = MathF.IEEERemainder(yaw, MathF.Tau);
        if (yaw <= -MathF.PI) yaw += MathF.Tau;
        if (yaw > MathF.PI) yaw -= MathF.Tau;
        return yaw;
    }

    public static float ShorterArcDelta(float fromYaw, float toYaw)
    {
        return MathF.IEEERemainder(toYaw - fromYaw, MathF.Tau);
    }

    public static bool IsTurretAligned(float currentYaw, Vector2 attackerPos, Vector2 targetPos)
    {
        Vector2 dir = targetPos - attackerPos;
        if (dir.LengthSquared() < 0.0001f) return true;
        float desiredYaw = MathF.Atan2(-dir.X, -dir.Y);
        float delta = ShorterArcDelta(currentYaw, desiredYaw);
        return MathF.Abs(delta) <= TurretAlignmentTolerance;
    }

    private static void TurnTurret(ref WorldState.CombatState cs, Vector2 attackerPos, Vector2 targetPos, float traverseRateDegPerSec)
    {
        Vector2 dir = targetPos - attackerPos;
        if (dir.LengthSquared() < 0.0001f) return;
        float desiredYaw = MathF.Atan2(-dir.X, -dir.Y);
        if (traverseRateDegPerSec <= 0f)
        {
            cs.TurretYaw = NormalizeYaw(desiredYaw);
            return;
        }

        float maxTurnPerTick = (traverseRateDegPerSec * (MathF.PI / 180f)) * Simulation.TickSeconds;
        float delta = ShorterArcDelta(cs.TurretYaw, desiredYaw);
        if (MathF.Abs(delta) <= maxTurnPerTick)
        {
            cs.TurretYaw = NormalizeYaw(desiredYaw);
        }
        else
        {
            cs.TurretYaw = NormalizeYaw(cs.TurretYaw + MathF.Sign(delta) * maxTurnPerTick);
        }
    }

    public void OnUnitStanceChanged(EntityId unitId, FireStance stance, WorldState world)
    {
        if (!world.TryGetCombatState(unitId, out WorldState.CombatState cs))
            return;

        if (!cs.HasAttackOrder && stance == FireStance.HoldFire)
        {
            cs.TargetContactId = default;
            cs.TargetEntityId = default;
            cs.Status = AttackExecutionStatus.None;
            world.SetCombatState(unitId, cs);
        }
        else if (!cs.HasAttackOrder && stance == FireStance.ReturnFire && cs.TargetEntityId.Value != 0)
        {
            if (cs.LastTookFireAttacker.Value == 0 || cs.TargetEntityId.Value != cs.LastTookFireAttacker.Value)
            {
                cs.TargetContactId = default;
                cs.TargetEntityId = default;
                cs.Status = AttackExecutionStatus.None;
                world.SetCombatState(unitId, cs);
            }
        }
    }

    private void TryPursueTarget(int tick, UnitState attacker, Vector2 targetPos, ref WorldState.CombatState cs, WorldState world)
    {
        float distToTarget = Vector2.Distance(attacker.Position, targetPos);
        if (distToTarget <= 1.0f)
        {
            return;
        }

        bool hasPreviousDestination = cs.PursuitDestination != Vector2.Zero;
        float destinationMoved = hasPreviousDestination ? Vector2.Distance(cs.PursuitDestination, targetPos) : float.MaxValue;
        bool targetMovedSignificantly = destinationMoved > 3.0f;

        PathRequestStatus navStatus = _navigation?.GetUnitStatus(attacker.Id) ?? PathRequestStatus.None;
        bool isUnreachable = navStatus is PathRequestStatus.InvalidDestination or PathRequestStatus.NoPath;

        if (isUnreachable && !targetMovedSignificantly)
        {
            // Stop retrying after an InvalidDestination or NoPath result until the target moves.
            return;
        }

        if (navStatus is PathRequestStatus.Searching or PathRequestStatus.Pending && !targetMovedSignificantly)
        {
            return;
        }

        if (attacker.Moving && !targetMovedSignificantly)
        {
            return;
        }

        if (!targetMovedSignificantly && cs.LastPursuitRequestTick >= 0 && (tick - cs.LastPursuitRequestTick) < Simulation.TickRate)
        {
            return;
        }

        OrderUnitToMove(attacker.Owner, attacker.Id, targetPos, world);
        cs.PursuitDestination = targetPos;
        cs.LastPursuitRequestTick = tick;
    }

    /// <summary>
    /// Validates and executes an attack order from a client targeting a ContactId.
    /// Orders out of range are accepted; the unit drives into range and fires.
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

        // Resolve internal truth link on the server only
        if (!tracker.TryGetBoundEntity(command.Target, out EntityId targetEntityId))
            return false;

        if (!world.TryGetUnit(targetEntityId, out UnitState targetUnit) || !world.AreHostile(player, targetUnit.Owner))
            return false;

        // Attack orders out of range are accepted.
        // If already within weapon range and has clear line of sight: stop moving immediately.
        // Otherwise: drive toward target position.
        float dist = Vector2.Distance(attacker.Position, targetUnit.Position);
        bool inRange = dist <= attackerProfile.Weapon.Range + RangeToleranceMargin;
        bool hasClearShot = inRange && HasLineOfSightToTarget(attacker, attackerProfile.Weapon, targetUnit, out _, out _);

        if (inRange && hasClearShot)
        {
            world.UpdateUnitNavigation(command.Unit, attacker.Position, attacker.Position, false);
            _navigation?.ClearUnit(command.Unit);
        }
        else
        {
            OrderUnitToMove(player, command.Unit, contact.Position, world);
        }

        // Apply validated attack order
        if (world.TryGetCombatState(command.Unit, out WorldState.CombatState cs))
        {
            cs.HasAttackOrder = true;
            cs.TargetContactId = command.Target;
            cs.TargetEntityId = targetEntityId;
            cs.Status = hasClearShot ? AttackExecutionStatus.Aiming : (inRange ? AttackExecutionStatus.Obstructed : AttackExecutionStatus.OutOfRange);
            cs.PursuitDestination = (inRange && hasClearShot) ? Vector2.Zero : contact.Position;
            cs.LastPursuitRequestTick = -1;
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

        // Step 1: Advance reload timers and resolve weapons fire / pursuit / auto-fire
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

            if (cs.HasAttackOrder)
            {
                ActiveAttackersCount++;

                SideContactTracker tracker = _detection.GetTracker(attacker.Owner);

                // Check 1: Has contact expired/removed?
                if (!tracker.TryGetContact(cs.TargetContactId, out ContactObservation contact))
                {
                    cs.HasAttackOrder = false;
                    cs.TargetContactId = default;
                    cs.TargetEntityId = default;
                    cs.Status = AttackExecutionStatus.None;
                    cs.PursuitDestination = Vector2.Zero;
                    cs.LastPursuitRequestTick = -1;
                    world.SetCombatState(attacker.Id, cs);
                    continue;
                }

                // Check 2: Is contact LastKnown?
                if (contact.State != ContactState.Current)
                {
                    TurnTurret(ref cs, attacker.Position, contact.Position, profile.TurretTraverseRate);
                    cs.Status = AttackExecutionStatus.TargetLost;
                    TryPursueTarget(tick, attacker, contact.Position, ref cs, world);
                    world.SetCombatState(attacker.Id, cs);
                    continue;
                }

                // Contact is Current: re-resolve bound entity
                if (!tracker.TryGetBoundEntity(cs.TargetContactId, out EntityId targetEntityId) ||
                    !world.TryGetUnit(targetEntityId, out UnitState targetUnit) ||
                    !world.AreHostile(attacker.Owner, targetUnit.Owner))
                {
                    cs.HasAttackOrder = false;
                    cs.TargetContactId = default;
                    cs.TargetEntityId = default;
                    cs.Status = AttackExecutionStatus.None;
                    cs.PursuitDestination = Vector2.Zero;
                    cs.LastPursuitRequestTick = -1;
                    world.SetCombatState(attacker.Id, cs);
                    continue;
                }

                cs.TargetEntityId = targetEntityId;
                TurnTurret(ref cs, attacker.Position, targetUnit.Position, profile.TurretTraverseRate);

                float dist = Vector2.Distance(attacker.Position, targetUnit.Position);

                // Check if within weapon range
                if (dist <= weapon.Range + RangeToleranceMargin)
                {
                    if (HasLineOfSightToTarget(attacker, weapon, targetUnit, out Vector3 hitPoint, out Vector3 muzzlePos))
                    {
                        // Within range and clear line of sight: stop moving and engage
                        if (attacker.Moving)
                        {
                            world.UpdateUnitNavigation(attacker.Id, attacker.Position, attacker.Position, false);
                            _navigation?.ClearUnit(attacker.Id);
                            attacker = world.GetUnitAt(i);
                        }

                        cs.PursuitDestination = Vector2.Zero;
                        cs.LastPursuitRequestTick = -1;

                        if (cs.Ammunition <= 0)
                        {
                            cs.Status = AttackExecutionStatus.NoAmmo;
                            world.SetCombatState(attacker.Id, cs);
                            continue;
                        }

                        if (cs.ReloadTicksRemaining > 0)
                        {
                            cs.Status = AttackExecutionStatus.Aiming;
                            world.SetCombatState(attacker.Id, cs);
                            continue;
                        }

                        if (!IsTurretAligned(cs.TurretYaw, attacker.Position, targetUnit.Position))
                        {
                            cs.Status = AttackExecutionStatus.Aiming;
                            world.SetCombatState(attacker.Id, cs);
                            continue;
                        }

                        // Fire shot!
                        ExecuteShot(tick, attacker, weapon, targetUnit, muzzlePos, hitPoint, ref cs, world);
                        continue;
                    }
                    else
                    {
                        // Obstructed line of sight: drive toward contact position
                        cs.Status = AttackExecutionStatus.Obstructed;
                        TryPursueTarget(tick, attacker, contact.Position, ref cs, world);
                        world.SetCombatState(attacker.Id, cs);
                        continue;
                    }
                }
                else
                {
                    // Out of range: drive toward contact position
                    cs.Status = AttackExecutionStatus.OutOfRange;
                    TryPursueTarget(tick, attacker, contact.Position, ref cs, world);
                    world.SetCombatState(attacker.Id, cs);
                    continue;
                }
            }
            else
            {
                // Unit has NO attack order: evaluate Auto-Fire
                if (attacker.Moving || cs.Stance == FireStance.HoldFire)
                {
                    if (cs.TargetContactId.Value != 0)
                    {
                        cs.TargetContactId = default;
                        cs.TargetEntityId = default;
                        cs.Status = AttackExecutionStatus.None;
                    }
                    world.SetCombatState(attacker.Id, cs);
                    continue;
                }

                SideContactTracker tracker = _detection.GetTracker(attacker.Owner);

                // Target retention
                bool targetRetained = false;
                Vector3 currentHitPoint = Vector3.Zero;
                Vector3 currentMuzzlePos = Vector3.Zero;

                if (cs.TargetContactId.Value != 0 && cs.TargetEntityId.Value != 0)
                {
                    if (tracker.TryGetContact(cs.TargetContactId, out ContactObservation currentContact) &&
                        currentContact.State == ContactState.Current &&
                        tracker.TryGetBoundEntity(cs.TargetContactId, out EntityId boundId) &&
                        boundId.Value == cs.TargetEntityId.Value &&
                        world.TryGetUnit(cs.TargetEntityId, out UnitState currentTargetUnit) &&
                        world.AreHostile(attacker.Owner, currentTargetUnit.Owner) &&
                        Vector2.Distance(attacker.Position, currentTargetUnit.Position) <= weapon.Range + RangeToleranceMargin &&
                        (cs.Stance != FireStance.ReturnFire || (cs.LastTookFireAttacker.Value != 0 && cs.TargetEntityId.Value == cs.LastTookFireAttacker.Value)) &&
                        HasLineOfSightToTarget(attacker, weapon, currentTargetUnit, out currentHitPoint, out currentMuzzlePos))
                    {
                        targetRetained = true;
                    }
                    else
                    {
                        cs.TargetContactId = default;
                        cs.TargetEntityId = default;
                        cs.Status = AttackExecutionStatus.None;
                    }
                }

                // Pick new target if not retained: scan only when weapon is ready to fire
                if (!targetRetained)
                {
                    if (cs.ReloadTicksRemaining > 0 || cs.Ammunition <= 0)
                    {
                        world.SetCombatState(attacker.Id, cs);
                        continue;
                    }

                    ReadOnlySpan<ContactObservation> contacts = tracker.Contacts;
                    ContactId bestContactId = default;
                    EntityId bestEntityId = default;
                    int bestPriority = int.MaxValue;
                    float bestDistSq = float.MaxValue;
                    Vector3 bestHit = Vector3.Zero;
                    Vector3 bestMuzzle = Vector3.Zero;

                    for (int c = 0; c < contacts.Length; c++)
                    {
                        ContactObservation candidate = contacts[c];
                        if (candidate.State != ContactState.Current)
                            continue;

                        if (!tracker.TryGetBoundEntity(candidate.Id, out EntityId candidateEntityId) ||
                            !world.TryGetUnit(candidateEntityId, out UnitState candidateUnit) ||
                            !world.AreHostile(attacker.Owner, candidateUnit.Owner))
                        {
                            continue;
                        }

                        float distToCandidate = Vector2.Distance(attacker.Position, candidateUnit.Position);
                        if (distToCandidate > weapon.Range + RangeToleranceMargin)
                            continue;

                        if (cs.Stance == FireStance.ReturnFire && (cs.LastTookFireAttacker.Value == 0 || candidateEntityId.Value != cs.LastTookFireAttacker.Value))
                            continue;

                        if (!HasLineOfSightToTarget(attacker, weapon, candidateUnit, out Vector3 candHit, out Vector3 candMuzzle))
                            continue;

                        int priority = GetTargetClassPriority(candidate.Classification);
                        float distSq = distToCandidate * distToCandidate;

                        bool isBetter = priority < bestPriority ||
                            (priority == bestPriority && distSq < bestDistSq) ||
                            (priority == bestPriority && MathF.Abs(distSq - bestDistSq) < 0.001f && candidate.Id.Value < bestContactId.Value);

                        if (isBetter)
                        {
                            bestContactId = candidate.Id;
                            bestEntityId = candidateEntityId;
                            bestPriority = priority;
                            bestDistSq = distSq;
                            bestHit = candHit;
                            bestMuzzle = candMuzzle;
                        }
                    }

                    if (bestContactId.Value != 0)
                    {
                        cs.TargetContactId = bestContactId;
                        cs.TargetEntityId = bestEntityId;
                        cs.Status = AttackExecutionStatus.Aiming;
                        targetRetained = true;
                        currentHitPoint = bestHit;
                        currentMuzzlePos = bestMuzzle;
                    }
                }

                if (targetRetained)
                {
                    ActiveAttackersCount++;
                    world.TryGetUnit(cs.TargetEntityId, out UnitState targetUnit);
                    TurnTurret(ref cs, attacker.Position, targetUnit.Position, profile.TurretTraverseRate);

                    if (cs.Ammunition <= 0)
                    {
                        cs.Status = AttackExecutionStatus.NoAmmo;
                        world.SetCombatState(attacker.Id, cs);
                        continue;
                    }

                    if (cs.ReloadTicksRemaining > 0)
                    {
                        cs.Status = AttackExecutionStatus.Aiming;
                        world.SetCombatState(attacker.Id, cs);
                        continue;
                    }

                    if (!IsTurretAligned(cs.TurretYaw, attacker.Position, targetUnit.Position))
                    {
                        cs.Status = AttackExecutionStatus.Aiming;
                        world.SetCombatState(attacker.Id, cs);
                        continue;
                    }

                    ExecuteShot(tick, attacker, weapon, targetUnit, currentMuzzlePos, currentHitPoint, ref cs, world);
                    continue;
                }
                else
                {
                    world.SetCombatState(attacker.Id, cs);
                }
            }
        }

        // Step 2: Deterministic destruction pass
        if (_destroyedCount > 0)
        {
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
                            survivorCs.PursuitDestination = Vector2.Zero;
                            survivorCs.LastPursuitRequestTick = -1;
                            world.UpdateUnitNavigation(survivor.Id, survivor.Position, survivor.Position, false);
                            _navigation?.ClearUnit(survivor.Id);
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

    private void ExecuteShot(
        int tick,
        UnitState attacker,
        WeaponDefinition weapon,
        UnitState targetUnit,
        Vector3 muzzlePos,
        Vector3 hitPoint,
        ref WorldState.CombatState cs,
        WorldState world)
    {
        cs.Ammunition--;
        cs.ReloadTicksRemaining = weapon.ReloadDurationTicks;
        cs.Status = AttackExecutionStatus.Aiming;
        world.SetCombatState(attacker.Id, cs);

        TotalShotsFired++;

        // Apply fixed damage
        world.ApplyDamage(targetUnit.Id, weapon.Damage, out bool isDestroyed);
        TotalDamageApplied += weapon.Damage;

        // Record TookFire attribution in server memory for ReturnFire stance
        if (world.TryGetCombatState(targetUnit.Id, out WorldState.CombatState victimCs))
        {
            victimCs.LastTookFireAttacker = attacker.Id;
            victimCs.LastTookFireTick = tick;
            world.SetCombatState(targetUnit.Id, victimCs);
        }

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
