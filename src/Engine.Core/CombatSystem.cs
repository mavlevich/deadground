using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Server-authoritative direct-fire combat system.
/// Handles target validation, stationary firing constraints, line-of-sight obstruction,
/// tick-based reload, ammo consumption, single-application damage, deterministic unit destruction,
/// and knowledge-filtered presentation events.
/// Strictly allocation-free during simulation ticks.
/// </summary>
public readonly record struct CombatKillRecord(
    int Tick,
    EntityId KillerId,
    ushort KillerDefinition,
    int KillerOwner,
    EntityId VictimId,
    ushort VictimDefinition,
    int VictimOwner,
    float ShotDistance);

public sealed class CombatSystem : ISimulationSystem, IRandomStreamsAware
{
    public const int DefaultMaxEventsPerPlayer = 64;
    public const float RangeToleranceMargin = 0.5f;
    public const float TurretAlignmentTolerance = 0.05f;
    public const int TargetRecentlyMovedWindowTicks = 20;
    public const float TargetMovedRangingResetDistance = 10.0f;
    public const float MinHitProbability = 0.02f;
    public const float MaxHitProbability = 0.98f;
    public const float AutoFireSwitchHysteresis = 140f;

    private readonly StaticGeometry _geometry;
    private readonly DestinationEvaluator _destinationEvaluator;
    private readonly IUnitCombatCatalog _combatCatalog;
    private readonly IUnitPerceptionCatalog _perceptionCatalog;
    private readonly DetectionSystem _detection;
    private readonly NavigationSystem? _navigation;
    private FlightSystem? _flight;
    private BuildingDestructionSystem? _buildingDestruction;
    private GarrisonSystem? _garrison;
    private readonly LineOfSightContext _losContext;
    private readonly StructureSplashResult[] _splashStructureResults = new StructureSplashResult[WorldState.MaxDestructibleStructures];
    private readonly GarrisonCasualty[] _garrisonCasualtyBuffer = new GarrisonCasualty[BuildingGarrisonProfile.MaxOccupantsPerStructure];
    private DeterministicRandom? _combatRandom;
    private int _currentTick;

    private DeterministicRandom CombatRandom =>
        _combatRandom ?? throw new InvalidOperationException("CombatSystem random stream is not initialized.");

    public DeterministicRandom RandomStream => CombatRandom;

    public void InitializeRandomStreams(IRandomStreamProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _combatRandom = provider.GetStream(RandomDomain.Combat);
    }

    private readonly int _maxUnits;
    private readonly int _maxSides;
    private readonly int _maxEventsPerPlayer;

    private readonly EntityId[] _destroyedBuffer;
    private int _destroyedCount;
    private readonly EntityId[] _crashSplashKilledBuffer;

    private readonly CombatKillRecord[] _kills = new CombatKillRecord[256];
    private int _killCount;

    public int KillCount => _killCount;
    public ReadOnlySpan<CombatKillRecord> Kills => _kills.AsSpan(0, _killCount);

    public int FirstShotTick { get; private set; } = -1;
    public float FirstShotDistance { get; private set; } = -1f;

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
        int maxEventsPerPlayer = DefaultMaxEventsPerPlayer,
        DeterministicRandom? combatRandom = null,
        FlightSystem? flight = null,
        BuildingDestructionSystem? buildingDestruction = null)
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
        _flight = flight;
        _buildingDestruction = buildingDestruction;
        _maxUnits = maxUnits;
        _maxSides = maxSides;
        _maxEventsPerPlayer = maxEventsPerPlayer;
        _combatRandom = combatRandom;

        _destinationEvaluator = new DestinationEvaluator(geometry);
        _losContext = new LineOfSightContext(geometry.Structures.Count);
        _destroyedBuffer = new EntityId[maxUnits];
        _crashSplashKilledBuffer = new EntityId[maxUnits];
        _playerEvents = new CombatEventObservation[maxSides * maxEventsPerPlayer];
        _playerEventCount = new int[maxSides];
        _playerEventOverflow = new long[maxSides];
        _admittedPlayers = new bool[maxSides];
        Array.Fill(_admittedPlayers, true);
    }

    internal void SetFlightSystem(FlightSystem? flight) => _flight = flight;
    internal void SetBuildingDestructionSystem(BuildingDestructionSystem? buildingDestruction) =>
        _buildingDestruction = buildingDestruction;

    public void SetGarrisonSystem(GarrisonSystem? garrison) =>
        _garrison = garrison;

    public static bool CanEngageTargetAltitude(WeaponDefinition weapon, WorldState world, EntityId targetId)
    {
        if (world.IsCrashing(targetId))
            return false;

        if (world.IsAirborne(targetId))
        {
            float altitude = world.GetUnitAltitude(targetId);
            if (altitude > FlightSystem.MinCrashAltitude)
            {
                return weapon.CanTargetAir && altitude <= weapon.MaxAirTargetAltitude;
            }
        }

        return !weapon.AirTargetsOnly;
    }

    /// <summary>
    /// Validates and executes an explicit attack order from a client targeting a ContactId.
    /// Never accepts enemy EntityIds from a client.
    /// </summary>
    private void OrderUnitToMove(int player, EntityId unitId, Vector2 destination, WorldState world)
    {
        if (world.IsGarrisoned(unitId))
            return;

        if (_flight is not null && world.IsAircraft(unitId))
        {
            _flight.OrderMove(world, unitId, destination, finalFacing: null, landOnArrival: false, _currentTick);
            return;
        }

        if (_navigation is not null)
        {
            _navigation.EnqueueCommand(player, new MoveCommand(0, 0, unitId, destination), world);
        }
        else
        {
            world.SetMoveTarget(player, unitId, destination);
        }
    }

    private void StopUnitMovement(UnitState unit, WorldState world)
    {
        if (_flight is not null && world.IsAircraft(unit.Id))
        {
            _flight.HoldPosition(world, unit.Id);
            return;
        }

        world.UpdateUnitNavigation(unit.Id, unit.Position, unit.Position, false);
        _navigation?.ClearUnit(unit.Id);
    }

    public bool HasLineOfSightToTarget(UnitState attacker, WeaponDefinition weapon, UnitState targetUnit, out Vector3 hitPoint, out Vector3 muzzlePos) =>
        HasLineOfSightToTarget(attacker, weapon, targetUnit, attackerAltitude: 0f, targetAltitude: 0f, out hitPoint, out muzzlePos);

    public bool HasLineOfSightToTarget(UnitState attacker, WeaponDefinition weapon, UnitState targetUnit, WorldState world, out Vector3 hitPoint, out Vector3 muzzlePos)
    {
        if (_garrison is not null && (world.IsGarrisoned(attacker.Id) || world.IsGarrisoned(targetUnit.Id)))
        {
            if (_garrison.AreInSameGarrison(world, attacker.Id, targetUnit.Id, out StructureId sharedStructure) &&
                _garrison.TryGetCqbShotPoints(world, sharedStructure, attacker.Id, targetUnit.Id, _currentTick, out muzzlePos, out hitPoint))
            {
                return true;
            }

            if (!_garrison.TryGetElevatedPoint(world, attacker.Id, targetUnit.Position, out muzzlePos))
            {
                float attackerGroundY = _geometry.GetSurfaceHeight(attacker.Position, attacker.SupportSurface) + world.GetUnitAltitude(attacker.Id);
                muzzlePos = new Vector3(attacker.Position.X, attackerGroundY + weapon.MuzzleHeight, attacker.Position.Y);
            }

            if (_garrison.TryGetElevatedPoint(world, targetUnit.Id, attacker.Position, out Vector3 garrisonTargetPt))
            {
                if (_geometry.IsLineOfSightClear(muzzlePos, garrisonTargetPt, _losContext, out _))
                {
                    hitPoint = garrisonTargetPt;
                    return true;
                }
                hitPoint = Vector3.Zero;
                return false;
            }

            float targetGroundY = _geometry.GetSurfaceHeight(targetUnit.Position, targetUnit.SupportSurface) + world.GetUnitAltitude(targetUnit.Id);
            if (_perceptionCatalog.TryGetProfile(targetUnit.Definition, out UnitPerceptionProfile tp) &&
                tp.TargetPoints is { RelativeHeights.Length: > 0 } pts)
            {
                for (int p = pts.RelativeHeights.Length - 1; p >= 0; p--)
                {
                    Vector3 testPoint = new(targetUnit.Position.X, targetGroundY + pts.RelativeHeights[p], targetUnit.Position.Y);
                    if (_geometry.IsLineOfSightClear(muzzlePos, testPoint, _losContext, out _))
                    {
                        hitPoint = testPoint;
                        return true;
                    }
                }
                hitPoint = Vector3.Zero;
                return false;
            }

            Vector3 fallbackPt = new(targetUnit.Position.X, targetGroundY + 1.5f, targetUnit.Position.Y);
            if (_geometry.IsLineOfSightClear(muzzlePos, fallbackPt, _losContext, out _))
            {
                hitPoint = fallbackPt;
                return true;
            }
            hitPoint = Vector3.Zero;
            return false;
        }

        return HasLineOfSightToTarget(
            attacker,
            weapon,
            targetUnit,
            world.GetUnitAltitude(attacker.Id),
            world.GetUnitAltitude(targetUnit.Id),
            out hitPoint,
            out muzzlePos);
    }

    public bool HasLineOfSightToTarget(
        UnitState attacker,
        WeaponDefinition weapon,
        UnitState targetUnit,
        float attackerAltitude,
        float targetAltitude,
        out Vector3 hitPoint,
        out Vector3 muzzlePos)
    {
        float attackerGroundY = _geometry.GetSurfaceHeight(attacker.Position, attacker.SupportSurface) + attackerAltitude;
        muzzlePos = new Vector3(attacker.Position.X, attackerGroundY + weapon.MuzzleHeight, attacker.Position.Y);

        float targetGroundY = _geometry.GetSurfaceHeight(targetUnit.Position, targetUnit.SupportSurface) + targetAltitude;
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

    public float ComputeVisibleShare(UnitState attacker, UnitState targetUnit) =>
        ComputeVisibleShare(attacker, targetUnit, attackerAltitude: 0f, targetAltitude: 0f);

    public float ComputeVisibleShare(UnitState attacker, UnitState targetUnit, WorldState world)
    {
        if (_garrison is not null && (world.IsGarrisoned(attacker.Id) || world.IsGarrisoned(targetUnit.Id)))
        {
            if (_garrison.AreInSameGarrison(world, attacker.Id, targetUnit.Id, out _))
            {
                return 1.0f;
            }

            if (!_garrison.TryGetElevatedPoint(world, attacker.Id, targetUnit.Position, out Vector3 muzzlePos))
            {
                float attackerGroundY = _geometry.GetSurfaceHeight(attacker.Position, attacker.SupportSurface) + world.GetUnitAltitude(attacker.Id);
                float muzzleHeight = 1.8f;
                if (_combatCatalog.TryGetProfile(attacker.Definition, out UnitCombatProfile ap) && ap.Weapon is not null)
                    muzzleHeight = ap.Weapon.MuzzleHeight;
                muzzlePos = new Vector3(attacker.Position.X, attackerGroundY + muzzleHeight, attacker.Position.Y);
            }

            if (_garrison.TryGetElevatedPoint(world, targetUnit.Id, attacker.Position, out Vector3 garrisonTargetPt))
            {
                // Cover protection for garrisoned troops is applied once via CoverDamageMultiplier
                // (no double penalty on hit probability).
                return _geometry.IsLineOfSightClear(muzzlePos, garrisonTargetPt, _losContext, out _) ? 1.0f : 0f;
            }

            float targetGroundY = _geometry.GetSurfaceHeight(targetUnit.Position, targetUnit.SupportSurface) + world.GetUnitAltitude(targetUnit.Id);
            if (_perceptionCatalog.TryGetProfile(targetUnit.Definition, out UnitPerceptionProfile tp) &&
                tp.TargetPoints is { RelativeHeights.Length: > 0 } pts)
            {
                int visibleCount = 0;
                for (int p = 0; p < pts.RelativeHeights.Length; p++)
                {
                    Vector3 testPoint = new(targetUnit.Position.X, targetGroundY + pts.RelativeHeights[p], targetUnit.Position.Y);
                    if (_geometry.IsLineOfSightClear(muzzlePos, testPoint, _losContext, out _))
                        visibleCount++;
                }
                return (float)visibleCount / pts.RelativeHeights.Length;
            }

            Vector3 fallbackPt = new(targetUnit.Position.X, targetGroundY + 1.5f, targetUnit.Position.Y);
            return _geometry.IsLineOfSightClear(muzzlePos, fallbackPt, _losContext, out _) ? 1.0f : 0.0f;
        }

        return ComputeVisibleShare(attacker, targetUnit, world.GetUnitAltitude(attacker.Id), world.GetUnitAltitude(targetUnit.Id));
    }

    public float ComputeVisibleShare(UnitState attacker, UnitState targetUnit, float attackerAltitude, float targetAltitude)
    {
        float attackerGroundY = _geometry.GetSurfaceHeight(attacker.Position, attacker.SupportSurface) + attackerAltitude;
        float muzzleHeight = 1.8f;
        if (_combatCatalog.TryGetProfile(attacker.Definition, out UnitCombatProfile attackerProfile) &&
            attackerProfile.Weapon is not null)
        {
            muzzleHeight = attackerProfile.Weapon.MuzzleHeight;
        }
        Vector3 muzzlePos = new(attacker.Position.X, attackerGroundY + muzzleHeight, attacker.Position.Y);

        float targetGroundY = _geometry.GetSurfaceHeight(targetUnit.Position, targetUnit.SupportSurface) + targetAltitude;
        if (_perceptionCatalog.TryGetProfile(targetUnit.Definition, out UnitPerceptionProfile targetPerception) &&
            targetPerception.TargetPoints is { RelativeHeights.Length: > 0 } points)
        {
            int visibleCount = 0;
            for (int p = 0; p < points.RelativeHeights.Length; p++)
            {
                Vector3 testPoint = new(targetUnit.Position.X, targetGroundY + points.RelativeHeights[p], targetUnit.Position.Y);
                if (_geometry.IsLineOfSightClear(muzzlePos, testPoint, _losContext, out _))
                {
                    visibleCount++;
                }
            }
            return (float)visibleCount / points.RelativeHeights.Length;
        }

        Vector3 fallbackTarget = new(targetUnit.Position.X, targetGroundY + 1.5f, targetUnit.Position.Y);
        return _geometry.IsLineOfSightClear(muzzlePos, fallbackTarget, _losContext, out _) ? 1.0f : 0.0f;
    }

    public static float CalculateHitProbability(
        WeaponDefinition weapon,
        float distance,
        float visibleShare,
        bool isTargetMoving,
        int consecutiveShots,
        bool isAttackerMoving = false)
    {
        if (visibleShare <= 0f) return 0f;
        if (isAttackerMoving && !weapon.CanFireOnMove) return 0f;

        float baseAccuracy = weapon.GetAccuracyAt(distance);
        float prob = baseAccuracy * visibleShare;
        if (isTargetMoving)
        {
            prob -= weapon.TargetMovementPenalty;
        }
        if (isAttackerMoving)
        {
            prob *= weapon.MovingAccuracyMultiplier;
        }
        else
        {
            float bonus = MathF.Min(weapon.MaxRangingBonus, consecutiveShots * weapon.RangingBonus);
            prob += bonus;
        }
        return Math.Clamp(prob, MinHitProbability, MaxHitProbability);
    }

    public Vector3 CalculateMissImpactPoint(
        Vector3 muzzlePos,
        Vector2 attackerPos,
        Vector2 targetPos,
        float targetRadius,
        WeaponDefinition weapon,
        uint targetSupportSurface = 0,
        float targetAltitude = 0f,
        Vector3? aimedHitPoint = null,
        bool isAttackerMoving = false)
    {
        Vector2 lineOfFire = targetPos - attackerPos;
        float lineLen = lineOfFire.Length();
        Vector2 fireDir = lineLen > 0.0001f ? lineOfFire / lineLen : Vector2.UnitX;
        Vector2 lateralDir = new(-fireDir.Y, fireDir.X);

        float mils = weapon.DispersionMils > 0f ? weapon.DispersionMils : 3.0f;
        float effectiveMils = isAttackerMoving ? mils * 2.25f : mils;
        // 1 mil is 1 meter lateral/vertical displacement per 1,000 meters range
        float dispersionMeters = MathF.Max(0.75f, lineLen * (effectiveMils / 1000f));
        float forwardSign = (CombatRandom.Next(10) < 6) ? 1f : -1f;

        // Ensure impact point is strictly outside target radius
        float minForward = targetRadius + 0.4f;
        float maxForward = MathF.Max(minForward + 1.0f, dispersionMeters * (isAttackerMoving ? 1.85f : 1.45f));
        float forwardDist = minForward + (float)CombatRandom.NextDouble() * (maxForward - minForward);

        float lateralMax = MathF.Max(0.75f, dispersionMeters * (isAttackerMoving ? 0.65f : 0.45f));
        float lateralDist = ((float)CombatRandom.NextDouble() * 2f - 1f) * lateralMax;

        Vector2 miss2D = targetPos + fireDir * (forwardSign * forwardDist) + lateralDir * lateralDist;
        uint surfaceAtMiss = 0;
        if (targetSupportSurface > 0 && _geometry.TryGetBridge(targetSupportSurface, out BridgeStructure? targetBridge))
        {
            if (targetBridge is not null && targetBridge.Contains(miss2D))
            {
                surfaceAtMiss = targetSupportSurface;
            }
        }
        float groundY = _geometry.GetSurfaceHeight(miss2D, surfaceAtMiss);
        float missY = targetAltitude > FlightSystem.MinCrashAltitude
            ? MathF.Max(groundY, groundY + targetAltitude + ((float)CombatRandom.NextDouble() * 2f - 1f) * MathF.Min(4.0f, lateralMax))
            : groundY;
        Vector3 miss3D = new(miss2D.X, missY, miss2D.Y);

        // For ground overshoots (long/wide shots), trace along the aimed elevation past low intervening
        // cover (such as collapsed rubble) before descending onto the ground behind the target.
        if (forwardSign > 0f && aimedHitPoint.HasValue && targetAltitude <= FlightSystem.MinCrashAltitude)
        {
            Vector3 passPoint = new(
                targetPos.X + lateralDir.X * lateralDist,
                aimedHitPoint.Value.Y,
                targetPos.Y + lateralDir.Y * lateralDist);
            LineOfSightResult approachLos = _geometry.CheckLineOfFire(muzzlePos, passPoint, _losContext);
            if (!approachLos.IsClear)
            {
                return approachLos.HitPoint;
            }

            LineOfSightResult descendLos = _geometry.CheckLineOfFire(passPoint, miss3D, _losContext);
            return !descendLos.IsClear ? descendLos.HitPoint : miss3D;
        }

        LineOfSightResult los = _geometry.CheckLineOfFire(muzzlePos, miss3D, _losContext);
        return !los.IsClear ? los.HitPoint : miss3D;
    }

    public static int GetTargetClassPriority(ContactClassification classification) => classification switch
    {
        ContactClassification.Tank => 0,
        ContactClassification.Artillery => 1,
        ContactClassification.AntiAir => 1,
        ContactClassification.Apc => 2,
        ContactClassification.Helicopter => 3,
        ContactClassification.Recon => 4,
        ContactClassification.Infantry => 5,
        _ => 6
    };

    private static float GetBaseTargetThreatScore(WeaponDefinition weapon, ContactClassification classification)
    {
        if (weapon.AirTargetsOnly)
        {
            return 1000f;
        }

        return classification switch
        {
            ContactClassification.Tank => 1000f,
            ContactClassification.Artillery => 820f,
            ContactClassification.AntiAir => 820f,
            ContactClassification.Apc => 640f,
            ContactClassification.Helicopter => 540f,
            ContactClassification.Recon => 420f,
            ContactClassification.Infantry => 300f,
            _ => 200f
        };
    }

    private static void CountAlliedCommitmentsOnTarget(
        WorldState world,
        int owner,
        EntityId excludeAttackerId,
        EntityId targetEntityId,
        out int alliedAttackers,
        out int alliedMissilesInFlight)
    {
        alliedAttackers = 0;
        alliedMissilesInFlight = 0;

        int unitCount = world.Count;
        for (int i = 0; i < unitCount; i++)
        {
            UnitState other = world.GetUnitAt(i);
            if (other.Owner != owner || other.Id == excludeAttackerId)
                continue;
            if (world.IsContained(other.Id) || world.IsCrashing(other.Id) || world.IsSlung(other.Id))
                continue;
            if (world.TryGetCombatState(other.Id, out WorldState.CombatState otherCs) &&
                otherCs.TargetEntityId == targetEntityId &&
                otherCs.Status != AttackExecutionStatus.None &&
                otherCs.Status != AttackExecutionStatus.NoAmmo)
            {
                alliedAttackers++;
            }
        }

        ReadOnlySpan<GuidedMissileState> missiles = world.GuidedMissiles;
        for (int m = 0; m < missiles.Length; m++)
        {
            ref readonly GuidedMissileState missile = ref missiles[m];
            if (missile.Owner == owner &&
                !missile.Decoyed &&
                missile.TargetId == targetEntityId)
            {
                alliedMissilesInFlight++;
            }
        }
    }

    private float ComputeAutoFireTargetScore(
        UnitState attacker,
        WeaponDefinition weapon,
        in WorldState.CombatState cs,
        ContactObservation candidate,
        EntityId candidateEntityId,
        float distance,
        WorldState world)
    {
        float score = GetBaseTargetThreatScore(weapon, candidate.Classification);

        // Emergency close-quarters priority: enemies inside the same contested building section
        // always take immediate precedence over distant outside targets
        if (_garrison is not null && _garrison.AreInSameGarrison(world, attacker.Id, candidateEntityId, out _))
        {
            score += 1200f;
        }

        // Quadratic close-range urgency so close threats outrank distant targets
        float normDist = Math.Clamp(distance / MathF.Max(1f, weapon.Range), 0f, 1f);
        float proximity = 1f - normDist;
        score += proximity * proximity * 320f;

        // Mild turret alignment preference to avoid unnecessary 180-degree swings
        Vector2 dir = candidate.Position - attacker.Position;
        if (dir.LengthSquared() > 0.0001f)
        {
            float desiredYaw = MathF.Atan2(-dir.X, -dir.Y);
            float yawDelta = MathF.Abs(ShorterArcDelta(cs.TurretYaw, desiredYaw));
            score += (1f - yawDelta / MathF.PI) * 45f;
        }

        // Anti-overkill distribution across allied units and in-flight guided missiles (own-side data only)
        CountAlliedCommitmentsOnTarget(
            world,
            attacker.Owner,
            attacker.Id,
            candidateEntityId,
            out int alliedAttackers,
            out int alliedMissilesInFlight);

        if (weapon.IsGuidedMissile)
        {
            score -= 280f * alliedAttackers;
            score -= 420f * alliedMissilesInFlight;
        }
        else
        {
            score -= 45f * Math.Min(3, alliedAttackers);
        }

        return score;
    }

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
        TurnTurretTo(ref cs, MathF.Atan2(-dir.X, -dir.Y), traverseRateDegPerSec);
    }

    // With nothing to aim at, the gunner lays the gun back over the front of the hull.
    private static void ReturnTurretToFront(ref WorldState.CombatState cs, float traverseRateDegPerSec) =>
        TurnTurretTo(ref cs, cs.HullHeading, traverseRateDegPerSec);

    private static void TurnTurretTo(ref WorldState.CombatState cs, float desiredYaw, float traverseRateDegPerSec)
    {
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

    public static ArmorSide DetermineHitSide(Vector2 attackerPos, Vector2 targetPos, float targetHullHeading, in UnitArmor armor)
    {
        Vector2 toShooter = attackerPos - targetPos;
        if (toShooter.LengthSquared() < 0.0001f)
        {
            return ArmorSide.Front;
        }
        float incomingBearing = MathF.Atan2(-toShooter.X, -toShooter.Y);
        float relAngle = MathF.Abs(ShorterArcDelta(targetHullHeading, incomingBearing));

        if (relAngle <= armor.FrontSectorHalfAngle)
        {
            return ArmorSide.Front;
        }
        if (relAngle >= MathF.PI - armor.RearSectorHalfAngle)
        {
            return ArmorSide.Rear;
        }
        return ArmorSide.Side;
    }

    public static float GetArmorThickness(in UnitArmor armor, ArmorSide side) => side switch
    {
        ArmorSide.Front => armor.Front,
        ArmorSide.Side => armor.Side,
        ArmorSide.Rear => armor.Rear,
        _ => armor.Side
    };

    public static bool CanPenetrateObservedContact(
        Vector2 attackerPos,
        WeaponDefinition weapon,
        in ContactObservation contact,
        IUnitCombatCatalog catalog)
    {
        if (weapon.IsGuidedMissile)
            return true;
        if (!catalog.TryGetProfile(contact.Type, out UnitCombatProfile targetProfile))
            return true;

        Vector2 shotDir = contact.Position - attackerPos;
        float distSq = shotDir.LengthSquared();
        if (distSq < 0.0001f)
            return weapon.GetPenetrationAt(0f) > targetProfile.Armor.Front;

        float dist = MathF.Sqrt(distSq);
        ArmorSide hitSide = DetermineHitSide(attackerPos, contact.Position, contact.HullHeading, targetProfile.Armor);
        float armorThickness = GetArmorThickness(targetProfile.Armor, hitSide);
        float penetration = weapon.GetPenetrationAt(dist);
        return penetration > armorThickness;
    }

    /// <summary>
    /// Checks whether an observed contact is eligible for auto-fire engagement by the attacker.
    /// Evaluates attacker weapon existence, ammunition, stance, distance, aspect penetration against
    /// observed catalog armor, line of sight, and visible share.
    /// </summary>
    public bool IsContactEligibleForAutoFire(
        UnitState attacker,
        WeaponDefinition weapon,
        in WorldState.CombatState cs,
        in ContactObservation contact,
        SideContactTracker tracker,
        WorldState world)
    {
        if (cs.Stance == FireStance.HoldFire || cs.Ammunition <= 0 || world.IsCrashing(attacker.Id))
        {
            return false;
        }

        if (contact.State != ContactState.Current)
        {
            return false;
        }

        if (!tracker.TryGetBoundEntity(contact.Id, out EntityId candidateEntityId) ||
            !world.TryGetUnit(candidateEntityId, out UnitState candidateUnit) ||
            !world.AreHostile(attacker.Owner, candidateUnit.Owner))
        {
            return false;
        }

        if (!CanEngageTargetAltitude(weapon, world, candidateEntityId))
        {
            return false;
        }

        if (cs.Stance == FireStance.ReturnFire &&
            (cs.LastTookFireAttacker.Value == 0 || candidateEntityId.Value != cs.LastTookFireAttacker.Value))
        {
            return false;
        }

        float distToCandidate = Vector2.Distance(attacker.Position, candidateUnit.Position);
        if (distToCandidate > weapon.Range + RangeToleranceMargin)
        {
            return false;
        }

        if (!CanPenetrateObservedContact(attacker.Position, weapon, contact, _combatCatalog))
        {
            return false;
        }

        if (!HasLineOfSightToTarget(attacker, weapon, candidateUnit, world, out _, out _))
        {
            return false;
        }

        if (ComputeVisibleShare(attacker, candidateUnit, world) <= 0f)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks whether an attacker observing contacts has at least one target eligible for auto-fire engagement.
    /// </summary>
    public bool HasEligibleAutoFireTarget(
        UnitState attacker,
        WeaponDefinition weapon,
        in WorldState.CombatState cs,
        SideContactTracker tracker,
        WorldState world)
    {
        ReadOnlySpan<ContactObservation> contacts = tracker.Contacts;
        for (int c = 0; c < contacts.Length; c++)
        {
            if (IsContactEligibleForAutoFire(attacker, weapon, cs, contacts[c], tracker, world))
            {
                return true;
            }
        }
        return false;
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
        if (world.IsGarrisoned(attacker.Id))
        {
            return;
        }

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
        if (!world.TryGetUnit(command.Unit, out UnitState attacker) || attacker.Owner != player ||
            world.IsContained(command.Unit) || world.IsCrashing(command.Unit) || world.IsSlung(command.Unit))
            return false;

        // Verify attacker has an operational direct-fire weapon
        if (!_combatCatalog.TryGetProfile(attacker.Definition, out UnitCombatProfile attackerProfile) ||
            attackerProfile.Weapon is null ||
            attackerProfile.Weapon.IsIndirectFire)
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

        if (!world.TryGetUnit(targetEntityId, out UnitState targetUnit) || !world.AreHostile(player, targetUnit.Owner) ||
            world.IsContained(targetEntityId) || !CanEngageTargetAltitude(attackerProfile.Weapon, world, targetEntityId))
            return false;

        SyncSmokeContext();

        // Attack orders out of range are accepted.
        // If already within weapon range and has clear line of sight: stop moving immediately.
        // Otherwise: drive toward target position.
        float dist = Vector2.Distance(attacker.Position, targetUnit.Position);
        bool inRange = dist <= attackerProfile.Weapon.Range + RangeToleranceMargin;
        bool hasClearShot = inRange && HasLineOfSightToTarget(attacker, attackerProfile.Weapon, targetUnit, world, out _, out _);

        if (inRange && hasClearShot)
        {
            StopUnitMovement(attacker, world);
        }
        else
        {
            OrderUnitToMove(player, command.Unit, contact.Position, world);
        }

        // Apply validated attack order
        if (world.TryGetCombatState(command.Unit, out WorldState.CombatState cs))
        {
            cs.HasAttackOrder = true;
            cs.HasAttackGroundOrder = false;
            cs.AttackGroundTarget = default;
            cs.AttackGroundStructureId = default;
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

    /// <summary>
    /// Validates own-unit and own-weapon preconditions for an AttackGroundCommand at enqueue time
    /// without inspecting hidden enemy or unobserved structure state.
    /// </summary>
    public bool CanEnqueueAttackGround(int player, AttackGroundCommand command, WorldState world)
    {
        if (player < 1 || command.Sequence == 0)
            return false;

        if (!float.IsFinite(command.TargetPoint.X) ||
            !float.IsFinite(command.TargetPoint.Y) ||
            !float.IsFinite(command.TargetPoint.Z))
            return false;

        Vector2 target2D = new(command.TargetPoint.X, command.TargetPoint.Z);
        if (!world.Map.Contains(target2D))
            return false;

        if (!world.TryGetUnit(command.Unit, out UnitState attacker) || attacker.Owner != player ||
            world.IsContained(command.Unit) || world.IsCrashing(command.Unit) || world.IsSlung(command.Unit))
            return false;

        if (!_combatCatalog.TryGetProfile(attacker.Definition, out UnitCombatProfile profile) ||
            profile.Weapon is null ||
            profile.Weapon.IsIndirectFire ||
            profile.Weapon.AirTargetsOnly)
            return false;

        if (command.TargetStructure != default && profile.Weapon.StructuralDamage <= 0)
            return false;

        if (!world.TryGetCombatState(command.Unit, out WorldState.CombatState cs) || cs.Ammunition <= 0)
            return false;

        return true;
    }

    public bool TryOrderAttackGround(int player, AttackGroundCommand command, WorldState world)
    {
        if (player < 1 || command.Sequence == 0)
            return false;

        if (!float.IsFinite(command.TargetPoint.X) ||
            !float.IsFinite(command.TargetPoint.Y) ||
            !float.IsFinite(command.TargetPoint.Z))
            return false;

        Vector2 target2D = new(command.TargetPoint.X, command.TargetPoint.Z);
        if (!world.Map.Contains(target2D))
            return false;

        if (!world.TryGetUnit(command.Unit, out UnitState attacker) || attacker.Owner != player ||
            world.IsContained(command.Unit) || world.IsCrashing(command.Unit) || world.IsSlung(command.Unit))
            return false;

        if (!_combatCatalog.TryGetProfile(attacker.Definition, out UnitCombatProfile profile) ||
            profile.Weapon is null ||
            profile.Weapon.IsIndirectFire ||
            profile.Weapon.AirTargetsOnly)
            return false;

        WeaponDefinition weapon = profile.Weapon;
        Vector3 targetPoint = command.TargetPoint;
        StructureId targetStructure = command.TargetStructure;
        if (targetStructure != default)
        {
            // Weapons without structural damage cannot be ordered to demolish buildings
            if (weapon.StructuralDamage <= 0)
                return false;

            if (world.TryGetDestructibleStructure(targetStructure, out DestructibleStructureState st))
            {
                // Respect fog of war: only reject or clamp by what this player actually knows
                float knownMaxY = st.Max.Y;
                if (_detection.TryGetKnownStructure(player, targetStructure, out StructureObservation known))
                {
                    if (known.FloorsStanding == 0)
                        return false;
                    knownMaxY = StructureDestructionMath.ComputeCurrentHeight(
                        st.Min.Y,
                        st.Max.Y,
                        in st.Profile,
                        known.FloorsStanding);
                }

                targetPoint = new Vector3(
                    Math.Clamp(targetPoint.X, st.Min.X, st.Max.X),
                    Math.Clamp(targetPoint.Y, st.Min.Y + 0.25f, MathF.Max(st.Min.Y + 0.25f, knownMaxY - 0.1f)),
                    Math.Clamp(targetPoint.Z, st.Min.Z, st.Max.Z));
                target2D = new Vector2(targetPoint.X, targetPoint.Z);
            }
            else if (!_geometry.TryGetStructureIndex(targetStructure, out _))
            {
                targetStructure = default;
            }
        }

        SyncSmokeContext();
        float dist = Vector2.Distance(attacker.Position, target2D);
        bool inRange = dist <= weapon.Range + RangeToleranceMargin;
        bool hasClearShot = inRange && HasLineOfFireToPoint(attacker, weapon, targetPoint, targetStructure, world, out _, out _);

        if (inRange && hasClearShot)
        {
            if (attacker.Moving)
            {
                StopUnitMovement(attacker, world);
            }
        }
        else
        {
            OrderUnitToMove(player, command.Unit, target2D, world);
        }

        if (!world.SetAttackGroundOrder(command.Unit, targetPoint, targetStructure))
            return false;

        if (world.TryGetCombatState(command.Unit, out WorldState.CombatState cs))
        {
            cs.Status = hasClearShot
                ? AttackExecutionStatus.Aiming
                : (inRange ? AttackExecutionStatus.Obstructed : AttackExecutionStatus.OutOfRange);
            cs.PursuitDestination = (inRange && hasClearShot) ? Vector2.Zero : target2D;
            cs.LastPursuitRequestTick = -1;
            world.SetCombatState(command.Unit, cs);
            return true;
        }

        return false;
    }

    private void SyncSmokeContext()
    {
        if (_detection.SmokeSystem is not null)
        {
            _detection.SmokeSystem.CopyActiveVolumesTo(_losContext);
        }
        else
        {
            _losContext.SetSmokeVolumes(null, 0);
        }

        if (_buildingDestruction is not null)
        {
            _buildingDestruction.CopyHeightOverridesTo(_losContext);
        }
        else
        {
            _losContext.SetStructureMaxYOverrides(null, 0);
        }
    }

    public void Update(WorldState world, int tick)
    {
        _currentTick = tick;
        ActiveAttackersCount = 0;
        _destroyedCount = 0;
        SyncSmokeContext();

        int unitCount = world.Count;

        // Step 1: Advance reload timers and resolve weapons fire / pursuit / auto-fire
        for (int i = 0; i < unitCount; i++)
        {
            UnitState attacker = world.GetUnitAt(i);
            if (world.IsContained(attacker.Id) || world.IsCrashing(attacker.Id) || world.IsSlung(attacker.Id)) continue;
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
                if (weapon.IsIndirectFire)
                {
                    world.SetCombatState(attacker.Id, cs);
                }
            }

            if (weapon.IsIndirectFire)
            {
                continue;
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
                    !world.AreHostile(attacker.Owner, targetUnit.Owner) ||
                    !CanEngageTargetAltitude(weapon, world, targetEntityId))
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
                if (dist <= weapon.Range + RangeToleranceMargin &&
                    (!weapon.IsGuidedMissile || dist >= weapon.MinRange))
                {
                    if (HasLineOfSightToTarget(attacker, weapon, targetUnit, world, out Vector3 hitPoint, out Vector3 muzzlePos))
                    {
                        if (weapon.IsGuidedMissile)
                        {
                            UpdateGuidedLauncherPitch(world, attacker.Id, muzzlePos, hitPoint);
                        }
                        // Within range and clear line of sight: stop moving and engage
                        if (attacker.Moving)
                        {
                            StopUnitMovement(attacker, world);
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

                        float visibleShare = ComputeVisibleShare(attacker, targetUnit, world);
                        if (visibleShare <= 0f)
                        {
                            cs.Status = AttackExecutionStatus.Obstructed;
                            TryPursueTarget(tick, attacker, contact.Position, ref cs, world);
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
            else if (cs.HasAttackGroundOrder)
            {
                ActiveAttackersCount++;
                ProcessAttackGroundOrder(tick, i, attacker, profile, weapon, ref cs, world);
                continue;
            }
            else
            {
                // Unit has NO attack order: evaluate Auto-Fire
                SideContactTracker tracker = _detection.GetTracker(attacker.Owner);

                // Target retention
                bool targetRetained = false;
                Vector3 currentHitPoint = Vector3.Zero;
                Vector3 currentMuzzlePos = Vector3.Zero;
                ContactObservation retainedContact = default;

                if (cs.Stance != FireStance.HoldFire && cs.TargetContactId.Value != 0 && cs.TargetEntityId.Value != 0)
                {
                    if (tracker.TryGetContact(cs.TargetContactId, out ContactObservation currentContact) &&
                        currentContact.State == ContactState.Current &&
                        tracker.TryGetBoundEntity(cs.TargetContactId, out EntityId boundId) &&
                        boundId.Value == cs.TargetEntityId.Value &&
                        world.TryGetUnit(cs.TargetEntityId, out UnitState currentTargetUnit) &&
                        world.AreHostile(attacker.Owner, currentTargetUnit.Owner) &&
                        CanEngageTargetAltitude(weapon, world, cs.TargetEntityId) &&
                        Vector2.Distance(attacker.Position, currentTargetUnit.Position) <= weapon.Range + RangeToleranceMargin &&
                        (!weapon.IsGuidedMissile || Vector2.Distance(attacker.Position, currentTargetUnit.Position) >= weapon.MinRange) &&
                        (cs.Stance != FireStance.ReturnFire || (cs.LastTookFireAttacker.Value != 0 && cs.TargetEntityId.Value == cs.LastTookFireAttacker.Value)) &&
                        CanPenetrateObservedContact(attacker.Position, weapon, currentContact, _combatCatalog) &&
                        HasLineOfSightToTarget(attacker, weapon, currentTargetUnit, world, out currentHitPoint, out currentMuzzlePos))
                    {
                        targetRetained = true;
                        retainedContact = currentContact;
                    }
                }
                if (!targetRetained && cs.TargetContactId.Value != 0)
                {
                    cs.TargetContactId = default;
                    cs.TargetEntityId = default;
                    cs.Status = AttackExecutionStatus.None;
                    cs.HasSwitchedTargetThisReload = false;
                }

                if (cs.Stance == FireStance.HoldFire || (attacker.Moving && !weapon.CanFireOnMove))
                {
                    // Units on HoldFire or with unstabilized weapons cannot fire while moving.
                    // A unit driving off keeps its gun on the target it was engaging while valid;
                    // otherwise the gun returns over the front.
                    if (targetRetained && world.TryGetUnit(cs.TargetEntityId, out UnitState watched))
                        TurnTurret(ref cs, attacker.Position, watched.Position, profile.TurretTraverseRate);
                    else
                        ReturnTurretToFront(ref cs, profile.TurretTraverseRate);
                    world.SetCombatState(attacker.Id, cs);
                    continue;
                }

                // Auto-fire target selection and threat/overkill-aware re-evaluation
                bool shouldScan = !targetRetained
                    ? (cs.ReloadTicksRemaining == 0 && cs.Ammunition > 0)
                    : (cs.ReloadTicksRemaining == 0 && cs.Ammunition > 0 && !cs.HasSwitchedTargetThisReload);

                if (!targetRetained && !shouldScan)
                {
                    ReturnTurretToFront(ref cs, profile.TurretTraverseRate);
                    world.SetCombatState(attacker.Id, cs);
                    continue;
                }

                if (shouldScan)
                {
                    float currentScore = float.NegativeInfinity;
                    if (targetRetained)
                    {
                        float retainedDist = Vector2.Distance(attacker.Position, retainedContact.Position);
                        currentScore = ComputeAutoFireTargetScore(
                            attacker, weapon, in cs, retainedContact, cs.TargetEntityId, retainedDist, world);
                    }

                    float bestScore = targetRetained ? currentScore + AutoFireSwitchHysteresis : float.NegativeInfinity;
                    ContactId bestContactId = default;
                    EntityId bestEntityId = default;
                    Vector3 bestHit = Vector3.Zero;
                    Vector3 bestMuzzle = Vector3.Zero;
                    Vector2 bestCandidatePos = Vector2.Zero;

                    ReadOnlySpan<ContactObservation> contacts = tracker.Contacts;
                    for (int c = 0; c < contacts.Length; c++)
                    {
                        ContactObservation candidate = contacts[c];
                        if (candidate.State != ContactState.Current)
                            continue;

                        if (targetRetained && candidate.Id == cs.TargetContactId)
                            continue;

                        if (!tracker.TryGetBoundEntity(candidate.Id, out EntityId candidateEntityId) ||
                            !world.TryGetUnit(candidateEntityId, out UnitState candidateUnit) ||
                            !world.AreHostile(attacker.Owner, candidateUnit.Owner))
                        {
                            continue;
                        }

                        if (!CanEngageTargetAltitude(weapon, world, candidateEntityId))
                            continue;

                        float distToCandidate = Vector2.Distance(attacker.Position, candidateUnit.Position);
                        if (distToCandidate > weapon.Range + RangeToleranceMargin)
                            continue;
                        if (weapon.IsGuidedMissile && distToCandidate < weapon.MinRange)
                            continue;

                        if (cs.Stance == FireStance.ReturnFire && (cs.LastTookFireAttacker.Value == 0 || candidateEntityId.Value != cs.LastTookFireAttacker.Value))
                            continue;

                        if (!CanPenetrateObservedContact(attacker.Position, weapon, candidate, _combatCatalog))
                            continue;

                        if (!HasLineOfSightToTarget(attacker, weapon, candidateUnit, world, out Vector3 candHit, out Vector3 candMuzzle))
                            continue;

                        if (ComputeVisibleShare(attacker, candidateUnit, world) <= 0f)
                            continue;

                        float score = ComputeAutoFireTargetScore(
                            attacker, weapon, in cs, candidate, candidateEntityId, distToCandidate, world);

                        bool isBetter = score > bestScore + 0.001f ||
                            (bestContactId.Value != 0 && MathF.Abs(score - bestScore) <= 0.001f && candidate.Id.Value < bestContactId.Value);

                        if (isBetter)
                        {
                            bestContactId = candidate.Id;
                            bestEntityId = candidateEntityId;
                            bestScore = score;
                            bestHit = candHit;
                            bestMuzzle = candMuzzle;
                            bestCandidatePos = candidateUnit.Position;
                        }
                    }

                    if (bestContactId.Value != 0)
                    {
                        if (targetRetained)
                        {
                            cs.HasSwitchedTargetThisReload = true;
                        }
                        cs.TargetContactId = bestContactId;
                        cs.TargetEntityId = bestEntityId;
                        cs.Status = AttackExecutionStatus.Aiming;
                        targetRetained = true;
                        currentHitPoint = bestHit;
                        currentMuzzlePos = bestMuzzle;
                        if (bestEntityId != cs.LastTargetEntityId)
                        {
                            cs.ConsecutiveShotsOnTarget = 0;
                            cs.LastTargetEntityId = bestEntityId;
                            cs.LastTargetPosition = bestCandidatePos;
                        }
                    }
                }

                if (targetRetained)
                {
                    ActiveAttackersCount++;
                    world.TryGetUnit(cs.TargetEntityId, out UnitState targetUnit);
                    TurnTurret(ref cs, attacker.Position, targetUnit.Position, profile.TurretTraverseRate);
                    if (weapon.IsGuidedMissile)
                    {
                        UpdateGuidedLauncherPitch(world, attacker.Id, currentMuzzlePos, currentHitPoint);
                    }

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

                    float visibleShare = ComputeVisibleShare(attacker, targetUnit, world);
                    if (visibleShare <= 0f)
                    {
                        cs.Status = AttackExecutionStatus.Obstructed;
                        world.SetCombatState(attacker.Id, cs);
                        continue;
                    }

                    // Fire auto-fire shot!
                    ExecuteShot(tick, attacker, weapon, targetUnit, currentMuzzlePos, currentHitPoint, ref cs, world);
                    continue;
                }
                else
                {
                    ReturnTurretToFront(ref cs, profile.TurretTraverseRate);
                    if (weapon.IsGuidedMissile)
                    {
                        UpdateGuidedLauncherPitch(world, attacker.Id, Vector3.Zero, null);
                    }
                    world.SetCombatState(attacker.Id, cs);
                    continue;
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
                if (world.IsContainer(destroyedId) && world.GetPassengerCount(destroyedId) > 0)
                {
                    HandleContainerDestruction(destroyedId, world, tick);
                }
                world.RemoveUnit(destroyedId);
                _navigation?.ClearUnit(destroyedId);
                _detection.NotifyEntityDestroyed(destroyedId);

                // Cancel attack orders on any surviving units whose observable contact was retired
                for (int u = 0; u < world.Count; u++)
                {
                    UnitState survivor = world.GetUnitAt(u);
                    if (world.TryGetCombatState(survivor.Id, out WorldState.CombatState survivorCs) &&
                        survivorCs.TargetEntityId.Value != 0 &&
                        survivorCs.TargetEntityId.Value == destroyedId.Value)
                    {
                        SideContactTracker survivorTracker = _detection.GetTracker(survivor.Owner);
                        if (!survivorTracker.TryGetContact(survivorCs.TargetContactId, out _))
                        {
                            // Contact was observed and retired: clear order immediately
                            if (survivorCs.HasAttackOrder)
                            {
                                survivorCs.HasAttackOrder = false;
                                world.UpdateUnitNavigation(survivor.Id, survivor.Position, survivor.Position, false);
                                _navigation?.ClearUnit(survivor.Id);
                            }
                            survivorCs.TargetContactId = default;
                            survivorCs.TargetEntityId = default;
                            survivorCs.Status = AttackExecutionStatus.None;
                            survivorCs.PursuitDestination = Vector2.Zero;
                            survivorCs.LastPursuitRequestTick = -1;
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
        if (cs.Ammunition <= 0)
        {
            cs.Status = AttackExecutionStatus.NoAmmo;
            world.SetCombatState(attacker.Id, cs);
            return;
        }

        int burstRounds = Math.Max(1, weapon.RoundsPerBurst);
        int roundsFired = Math.Min(cs.Ammunition, burstRounds);
        if (roundsFired <= 0)
        {
            cs.Status = AttackExecutionStatus.NoAmmo;
            world.SetCombatState(attacker.Id, cs);
            return;
        }

        // 0. Visible share calculation from muzzle height
        float visibleShare = ComputeVisibleShare(attacker, targetUnit, world);
        if (visibleShare <= 0f)
        {
            return;
        }

        if (weapon.IsGuidedMissile)
        {
            ExecuteGuidedMissileLaunch(tick, attacker, weapon, targetUnit, muzzlePos, hitPoint, ref cs, world);
            return;
        }

        cs.Ammunition -= roundsFired;
        cs.ReloadTicksRemaining = weapon.ReloadDurationTicks;
        cs.HasSwitchedTargetThisReload = false;
        cs.Status = AttackExecutionStatus.Aiming;
        world.RecordUnitFired(attacker.Id, tick);

        TotalShotsFired++;

        // 1. Ranging bonus calculation (no static ranging while firing on the move)
        bool isAttackerMoving = attacker.Moving;
        if (isAttackerMoving)
        {
            cs.ConsecutiveShotsOnTarget = 0;
        }
        else if (targetUnit.Id.Value != 0 && targetUnit.Id == cs.LastTargetEntityId)
        {
            float targetMovedDistance = Vector2.Distance(targetUnit.Position, cs.LastTargetPosition);
            if (targetMovedDistance > TargetMovedRangingResetDistance)
            {
                cs.ConsecutiveShotsOnTarget = 0;
            }
        }
        else
        {
            cs.ConsecutiveShotsOnTarget = 0;
        }

        int currentRangingCount = cs.ConsecutiveShotsOnTarget;

        // Advance ranging for next shot at this target only when firing stationary
        if (!isAttackerMoving)
        {
            cs.ConsecutiveShotsOnTarget++;
        }
        cs.LastTargetEntityId = targetUnit.Id;
        cs.LastTargetPosition = targetUnit.Position;
        world.SetCombatState(attacker.Id, cs);

        // 2. Target movement check
        bool isTargetMoving = targetUnit.Moving;
        if (world.TryGetCombatState(targetUnit.Id, out WorldState.CombatState victimCs))
        {
            if (victimCs.LastMovedTick >= 0 && (tick - victimCs.LastMovedTick) <= TargetRecentlyMovedWindowTicks)
            {
                isTargetMoving = true;
            }
        }

        // 4. Hit probability calculation
        float shotDistance = Vector2.Distance(attacker.Position, targetUnit.Position);
        if (FirstShotTick < 0)
        {
            FirstShotTick = tick;
            FirstShotDistance = shotDistance;
        }

        float hitProb = CalculateHitProbability(
            weapon,
            shotDistance,
            visibleShare,
            isTargetMoving,
            currentRangingCount,
            isAttackerMoving);

        // 5. Roll from combat random stream
        float roll = (float)CombatRandom.NextDouble();
        bool hits = roll < hitProb;

        UnitArmor targetArmor = default;
        float targetRadius = 1.0f;
        if (_combatCatalog.TryGetProfile(targetUnit.Definition, out UnitCombatProfile targetProfile))
        {
            targetArmor = targetProfile.Armor;
            if (targetProfile.Radius > 0f)
            {
                targetRadius = targetProfile.Radius;
            }
        }

        Vector3 impactPoint;
        HitResult hitResult;
        bool isDestroyed = false;

        if (hits)
        {
            impactPoint = hitPoint;
            float targetHullHeading = 0f;
            if (world.TryGetCombatState(targetUnit.Id, out victimCs))
            {
                targetHullHeading = victimCs.HullHeading;
            }

            ArmorSide hitSide = DetermineHitSide(attacker.Position, targetUnit.Position, targetHullHeading, targetArmor);
            float armorThickness = GetArmorThickness(targetArmor, hitSide);
            float penetration = weapon.GetPenetrationAt(shotDistance);
            bool penetrates = penetration > armorThickness;

            if (penetrates)
            {
                int rawDamage = Math.Max(1, (weapon.Damage * roundsFired) / burstRounds);
                float coverMult = _garrison is not null
                    ? (_garrison.AreInSameGarrison(world, attacker.Id, targetUnit.Id, out _)
                        ? _garrison.GetCqbDamageMultiplier(world, attacker.Id, targetUnit.Id)
                        : _garrison.GetIncomingCoverDamageMultiplier(world, targetUnit.Id))
                    : 1.0f;
                int damageToApply = Math.Max(1, (int)MathF.Round(rawDamage * coverMult));
                world.ApplyDamage(targetUnit.Id, damageToApply, out isDestroyed);
                TotalDamageApplied += damageToApply;
                hitResult = HitResult.Penetrated;
            }
            else
            {
                hitResult = HitResult.Deflected;
            }
        }
        else
        {
            // Miss: for room-to-room CQB keep impact on the contested floor; otherwise compute dispersed outdoor point
            if (_garrison is not null && _garrison.AreInSameGarrison(world, attacker.Id, targetUnit.Id, out _))
            {
                impactPoint = hitPoint;
            }
            else
            {
                impactPoint = CalculateMissImpactPoint(
                    muzzlePos,
                    attacker.Position,
                    targetUnit.Position,
                    targetRadius,
                    weapon,
                    targetUnit.SupportSurface,
                    world.GetUnitAltitude(targetUnit.Id),
                    hitPoint,
                    isAttackerMoving);
            }
            hitResult = HitResult.Miss;
        }

        // Record TookFire attribution in server memory for ReturnFire stance
        if (world.TryGetCombatState(targetUnit.Id, out victimCs))
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
            impactPoint,
            hitResult);

        if (isDestroyed)
        {
            if (_flight is not null &&
                _flight.TryBeginCrash(world, targetUnit.Id, attacker.Id, attacker.Definition, attacker.Owner, shotDistance))
            {
                return;
            }

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
                bool leavesWreck = true;
                if (_combatCatalog is not null && _combatCatalog.TryGetProfile(targetUnit.Definition, out UnitCombatProfile victimProfile))
                {
                    leavesWreck = victimProfile.LeavesWreck;
                }

                WreckState wreck = default;
                if (leavesWreck)
                {
                    wreck = world.AddWreck(
                        targetUnit.Definition,
                        targetUnit.Position,
                        targetUnit.SupportSurface,
                        victimCs.HullHeading,
                        victimCs.TurretYaw,
                        tick,
                        targetUnit.Owner);
                }

                RecordDestroyedEvent(
                    tick,
                    targetUnit.Definition,
                    targetUnit.Id,
                    targetUnit.Owner,
                    targetUnit.Position,
                    targetUnit.SupportSurface,
                    wreck.Id);

                if (leavesWreck)
                {
                    _detection.NotifyWreckCreated(wreck, targetUnit.Owner, targetUnit.Id);
                }

                if (_destroyedCount < _destroyedBuffer.Length)
                {
                    _destroyedBuffer[_destroyedCount++] = targetUnit.Id;
                }

                if (_killCount < _kills.Length)
                {
                    _kills[_killCount++] = new CombatKillRecord(
                        tick,
                        attacker.Id,
                        attacker.Definition,
                        attacker.Owner,
                        targetUnit.Id,
                        targetUnit.Definition,
                        targetUnit.Owner,
                        shotDistance);
                }
            }
        }
    }

    public const int DefaultContainerDestructionDamage = 50;

    public void DestroyUnit(EntityId unitId, WorldState world, int tick)
    {
        if (!world.TryGetUnit(unitId, out UnitState u)) return;
        bool leavesWreck = true;
        if (_combatCatalog is not null && _combatCatalog.TryGetProfile(u.Definition, out UnitCombatProfile victimProfile))
        {
            leavesWreck = victimProfile.LeavesWreck;
        }

        world.TryGetCombatState(u.Id, out WorldState.CombatState cs);
        WreckState wreck = default;
        if (leavesWreck)
        {
            wreck = world.AddWreck(
                u.Definition,
                u.Position,
                u.SupportSurface,
                cs.HullHeading,
                cs.TurretYaw,
                tick,
                u.Owner);
            _detection.NotifyWreckCreated(wreck, u.Owner, u.Id);
        }

        RecordDestroyedEvent(
            tick,
            u.Definition,
            u.Id,
            u.Owner,
            u.Position,
            u.SupportSurface,
            wreck.Id);

        if (world.IsContainer(unitId) && world.GetPassengerCount(unitId) > 0)
        {
            HandleContainerDestruction(unitId, world, tick);
        }
        world.RemoveUnit(unitId);
        _navigation?.ClearUnit(unitId);
        _detection.NotifyEntityDestroyed(unitId);

        for (int i = 0; i < world.Count; i++)
        {
            UnitState survivor = world.GetUnitAt(i);
            if (world.TryGetCombatState(survivor.Id, out WorldState.CombatState survivorCs) &&
                survivorCs.TargetEntityId.Value != 0 &&
                survivorCs.TargetEntityId.Value == unitId.Value)
            {
                if (survivorCs.HasAttackOrder)
                {
                    survivorCs = survivorCs with
                    {
                        HasAttackOrder = false,
                        TargetEntityId = default,
                        TargetContactId = default
                    };
                    world.UpdateUnitNavigation(survivor.Id, survivor.Position, survivor.Position, false);
                    _navigation?.ClearUnit(survivor.Id);
                }
                else
                {
                    survivorCs = survivorCs with
                    {
                        TargetEntityId = default,
                        TargetContactId = default
                    };
                }
                world.SetCombatState(survivor.Id, survivorCs);
            }
        }
    }

    public void ResolveHelicopterCrashImpact(
        WorldState world,
        int tick,
        EntityId unitId,
        ushort definition,
        int owner,
        Vector2 impactPosition,
        float heading,
        in FlightProfile profile,
        in WorldState.UnitFlightState flightState)
    {
        if (!world.TryGetUnit(unitId, out UnitState u))
            return;

        TotalUnitsDestroyed++;
        bool leavesWreck = true;
        if (_combatCatalog is not null && _combatCatalog.TryGetProfile(definition, out UnitCombatProfile victimProfile))
        {
            leavesWreck = victimProfile.LeavesWreck;
        }

        WreckState wreck = default;
        if (leavesWreck)
        {
            wreck = world.AddWreck(
                definition,
                impactPosition,
                supportSurface: 0,
                heading,
                turretYaw: 0f,
                tick,
                owner);
            _detection.NotifyWreckCreated(wreck, owner, unitId);
        }

        RecordDestroyedEvent(
            tick,
            definition,
            unitId,
            owner,
            impactPosition,
            targetSupportSurface: 0,
            wreck.Id);

        if (flightState.CrashKillerId.Value != 0 && _killCount < _kills.Length)
        {
            _kills[_killCount++] = new CombatKillRecord(
                tick,
                flightState.CrashKillerId,
                flightState.CrashKillerDefinition,
                flightState.CrashKillerOwner,
                unitId,
                definition,
                owner,
                flightState.CrashShotDistance);
        }

        if (world.IsContainer(unitId) && world.GetPassengerCount(unitId) > 0)
        {
            int crashPassengerDamage = Math.Max(200, profile.CrashSplashDamage * 3);
            HandleContainerDestruction(unitId, world, tick, damageOverride: crashPassengerDamage);
        }

        // Destroy any externally slung cargo vehicle on crash impact
        if (flightState.SlingLoadId != default && world.TryGetUnit(flightState.SlingLoadId, out UnitState slungUnit))
        {
            float slungHeading = world.TryGetCombatState(slungUnit.Id, out WorldState.CombatState slungCs) ? slungCs.HullHeading : heading;
            float slungTurret = slungCs.TurretYaw;
            world.AddWreck(slungUnit.Definition, impactPosition, 0, slungHeading, slungTurret, tick, slungUnit.Owner);
            if (world.IsContainer(slungUnit.Id) && world.GetPassengerCount(slungUnit.Id) > 0)
            {
                HandleContainerDestruction(slungUnit.Id, world, tick, damageOverride: 200);
            }
            _navigation?.ClearUnit(slungUnit.Id);
            world.RemoveUnit(slungUnit.Id);
            TotalUnitsDestroyed++;
        }

        // Apply crash explosion splash damage to nearby ground units
        int splashKilledCount = 0;
        if (profile.CrashSplashRadius > 0f && profile.CrashSplashDamage > 0)
        {
            float radiusSq = profile.CrashSplashRadius * profile.CrashSplashRadius;
            for (int i = 0; i < world.Count; i++)
            {
                UnitState candidate = world.GetUnitAt(i);
                if (candidate.Id == unitId || world.IsContained(candidate.Id) || world.IsAirborne(candidate.Id))
                    continue;

                float distSq = Vector2.DistanceSquared(impactPosition, candidate.Position);
                if (distSq > radiusSq)
                    continue;

                float dist = MathF.Sqrt(distSq);
                float falloff = Math.Clamp(1f - (dist / profile.CrashSplashRadius) * 0.65f, 0.35f, 1f);
                int splashDamage = Math.Max(1, (int)MathF.Round(profile.CrashSplashDamage * falloff));
                world.ApplyDamage(candidate.Id, splashDamage, out bool killed);
                TotalDamageApplied += splashDamage;
                if (killed && splashKilledCount < _crashSplashKilledBuffer.Length)
                {
                    _crashSplashKilledBuffer[splashKilledCount++] = candidate.Id;
                }
            }
        }

        world.RemoveUnit(unitId);
        _navigation?.ClearUnit(unitId);
        _detection.NotifyEntityDestroyed(unitId);

        for (int i = 0; i < world.Count; i++)
        {
            UnitState survivor = world.GetUnitAt(i);
            if (world.TryGetCombatState(survivor.Id, out WorldState.CombatState survivorCs) &&
                survivorCs.TargetEntityId.Value != 0 &&
                survivorCs.TargetEntityId.Value == unitId.Value)
            {
                if (survivorCs.HasAttackOrder)
                {
                    survivorCs = survivorCs with
                    {
                        HasAttackOrder = false,
                        TargetEntityId = default,
                        TargetContactId = default
                    };
                    StopUnitMovement(survivor, world);
                }
                else
                {
                    survivorCs = survivorCs with
                    {
                        TargetEntityId = default,
                        TargetContactId = default
                    };
                }
                world.SetCombatState(survivor.Id, survivorCs);
            }
        }

        for (int k = 0; k < splashKilledCount; k++)
        {
            if (world.TryGetUnit(_crashSplashKilledBuffer[k], out _))
            {
                TotalUnitsDestroyed++;
                DestroyUnit(_crashSplashKilledBuffer[k], world, tick);
            }
        }
    }

    private static void UpdateGuidedLauncherPitch(
        WorldState world,
        EntityId unitId,
        Vector3 muzzlePos,
        Vector3? targetPos)
    {
        if (!world.TryGetArtilleryState(unitId, out WorldState.UnitArtilleryState art))
            return;

        // Idle alert search pitch (+12 deg) when no air target is tracked; otherwise aim at target
        const float minPitchRad = -5f * MathF.PI / 180f;
        const float maxPitchRad = 80f * MathF.PI / 180f;
        const float idlePitchRad = 12f * MathF.PI / 180f;
        const float maxStepRad = (55f * MathF.PI / 180f) * Simulation.TickSeconds;

        float desiredPitch = idlePitchRad;
        if (targetPos.HasValue)
        {
            Vector3 delta = targetPos.Value - muzzlePos;
            float horizDist = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
            if (horizDist > 0.5f)
            {
                desiredPitch = Math.Clamp(MathF.Atan2(delta.Y, horizDist), minPitchRad, maxPitchRad);
            }
        }

        float diff = desiredPitch - art.GunPitchRad;
        float nextPitch = MathF.Abs(diff) <= maxStepRad
            ? desiredPitch
            : art.GunPitchRad + MathF.Sign(diff) * maxStepRad;

        if (MathF.Abs(nextPitch - art.GunPitchRad) > 1e-5f)
        {
            art.GunPitchRad = nextPitch;
            world.SetArtilleryState(unitId, art);
        }
    }

    private void ExecuteGuidedMissileLaunch(
        int tick,
        UnitState attacker,
        WeaponDefinition weapon,
        UnitState targetUnit,
        Vector3 muzzlePos,
        Vector3 hitPoint,
        ref WorldState.CombatState cs,
        WorldState world)
    {
        cs.Ammunition = Math.Max(0, cs.Ammunition - 1);
        cs.ReloadTicksRemaining = weapon.ReloadDurationTicks;
        cs.HasSwitchedTargetThisReload = false;
        cs.Status = AttackExecutionStatus.Aiming;
        cs.LastTargetEntityId = targetUnit.Id;
        cs.LastTargetPosition = targetUnit.Position;
        world.SetCombatState(attacker.Id, cs);
        world.RecordUnitFired(attacker.Id, tick);

        TotalShotsFired++;
        float shotDistance = Vector2.Distance(attacker.Position, targetUnit.Position);
        if (FirstShotTick < 0)
        {
            FirstShotTick = tick;
            FirstShotDistance = shotDistance;
        }

        // Launch cleanly above the Strela-10 canister rack toward the target
        Vector3 launchOrigin = muzzlePos + new Vector3(0f, 1.35f, 0f);
        Vector3 toTarget = hitPoint - launchOrigin;
        float dist3D = toTarget.Length();
        Vector3 launchDir = dist3D > 0.1f ? toTarget / dist3D : new Vector3(MathF.Cos(cs.TurretYaw), 0.25f, MathF.Sin(cs.TurretYaw));
        launchDir = Vector3.Normalize(launchDir);

        float speed = weapon.ProjectileSpeed > 1f ? weapon.ProjectileSpeed : 135f;
        float maxTurnRad = (weapon.MissileMaxTurnRateDegPerSec > 0f ? weapon.MissileMaxTurnRateDegPerSec : 52f) * MathF.PI / 180f;
        float fuzeRadius = weapon.MissileProximityFuzeRadius > 0.5f ? weapon.MissileProximityFuzeRadius : 11f;
        int maxFlightTicks = weapon.MissileMaxFlightTicks > 0 ? weapon.MissileMaxFlightTicks : 320;
        int motorBurnTicks = weapon.MissileMotorBurnTicks > 0 ? weapon.MissileMotorBurnTicks : 150;

        GuidedMissileState missile = new()
        {
            Owner = attacker.Owner,
            ShooterId = attacker.Id,
            ShooterDefinition = attacker.Definition,
            TargetId = targetUnit.Id,
            PresentationId = weapon.PresentationId,
            Guidance = weapon.GuidanceKind != MissileGuidanceKind.None ? weapon.GuidanceKind : MissileGuidanceKind.Infrared,
            Position = launchOrigin,
            Velocity = launchDir * speed,
            Speed = speed,
            MaxTurnRateRadPerSec = maxTurnRad,
            Damage = weapon.Damage,
            SplashRadius = weapon.SplashRadius,
            ProximityFuzeRadius = fuzeRadius,
            IrCountermeasureResistance = Math.Clamp(weapon.IrCountermeasureResistance, 0f, 1f),
            LaunchedTick = tick,
            MaxFlightTicks = maxFlightTicks,
            MotorBurnTicks = motorBurnTicks,
            Decoyed = false,
            DecoyTargetPos = Vector3.Zero,
            FlareEvaluatedBurstTick = -10000
        };

        if (world.TryAddGuidedMissile(missile, out _))
        {
            RecordMissileLaunchedEvent(
                tick,
                weapon.PresentationId,
                attacker.Id,
                attacker.Owner,
                targetUnit.Id,
                targetUnit.Owner,
                launchOrigin,
                hitPoint);
        }
    }

    private void RecordMissileLaunchedEvent(
        int tick,
        ushort presentationId,
        EntityId shooterId,
        int shooterOwner,
        EntityId targetId,
        int targetOwner,
        Vector3 origin,
        Vector3 target)
    {
        uint eventId = _nextEventId++;
        if (_nextEventId == 0) _nextEventId = 1;

        for (int p = 1; p <= _maxSides; p++)
        {
            if (p == shooterOwner)
            {
                CombatEventObservation ev = new(
                    eventId,
                    tick,
                    CombatEventKind.MissileLaunched,
                    presentationId,
                    origin,
                    target,
                    shooterId,
                    HitResult.None);
                AppendEvent(p, ev);
            }
            else if (_detection.IsEntityObservedBy(p, shooterId))
            {
                CombatEventObservation ev = new(
                    eventId,
                    tick,
                    CombatEventKind.MissileLaunched,
                    presentationId,
                    origin,
                    target,
                    default,
                    HitResult.None);
                AppendEvent(p, ev);
            }
        }
    }

    public void RecordFlaresDeployedEvent(
        WorldState world,
        int tick,
        EntityId unitId,
        ushort definition,
        Vector3 origin,
        Vector3 encodedVelocityAndHeading)
    {
        if (!world.TryGetUnit(unitId, out UnitState unit))
            return;

        uint eventId = _nextEventId++;
        if (_nextEventId == 0) _nextEventId = 1;

        for (int p = 1; p <= _maxSides; p++)
        {
            if (p == unit.Owner)
            {
                CombatEventObservation ev = new(
                    eventId,
                    tick,
                    CombatEventKind.FlaresDeployed,
                    definition,
                    origin,
                    encodedVelocityAndHeading,
                    unitId,
                    HitResult.None);
                AppendEvent(p, ev);
            }
            else if (_detection.IsEntityObservedBy(p, unitId))
            {
                CombatEventObservation ev = new(
                    eventId,
                    tick,
                    CombatEventKind.FlaresDeployed,
                    definition,
                    origin,
                    encodedVelocityAndHeading,
                    default,
                    HitResult.None);
                AppendEvent(p, ev);
            }
        }
    }

    public void ResolveMissileDetonation(WorldState world, int tick, GuidedMissileState missile, bool hitTarget)
    {
        uint eventId = _nextEventId++;
        if (_nextEventId == 0) _nextEventId = 1;

        HitResult result = hitTarget ? HitResult.Penetrated : HitResult.Miss;
        Vector3 detOrigin = missile.Position - (missile.Velocity.LengthSquared() > 1f
            ? Vector3.Normalize(missile.Velocity) * 6f
            : Vector3.Zero);

        for (int p = 1; p <= _maxSides; p++)
        {
            bool visible = (p == missile.Owner);
            if (!visible && missile.TargetId.Value != 0 && world.TryGetUnit(missile.TargetId, out UnitState targetCheck))
            {
                visible = (p == targetCheck.Owner) || _detection.IsEntityObservedBy(p, missile.TargetId);
            }
            if (!visible)
            {
                // Airborne missile detonations are bright and audible across nearby units
                Vector2 det2D = new(missile.Position.X, missile.Position.Z);
                const float HearRadiusSq = 650f * 650f;
                for (int i = 0; i < world.Count; i++)
                {
                    UnitState u = world.GetUnitAt(i);
                    if (u.Owner == p && !world.IsContained(u.Id) &&
                        Vector2.DistanceSquared(u.Position, det2D) <= HearRadiusSq)
                    {
                        visible = true;
                        break;
                    }
                }
            }

            if (visible)
            {
                CombatEventObservation ev = new(
                    eventId,
                    tick,
                    CombatEventKind.MissileDetonated,
                    missile.PresentationId,
                    detOrigin,
                    missile.Position,
                    default,
                    result);
                AppendEvent(p, ev);
            }
        }

        if (!hitTarget || missile.TargetId.Value == 0)
            return;

        if (!world.TryGetUnit(missile.TargetId, out UnitState victim) ||
            world.IsContained(victim.Id) ||
            world.IsCrashing(victim.Id))
        {
            return;
        }

        int damage = Math.Max(1, missile.Damage);
        world.ApplyDamage(victim.Id, damage, out bool killed);
        TotalDamageApplied += damage;

        if (world.TryGetCombatState(victim.Id, out WorldState.CombatState victimCs))
        {
            victimCs.LastTookFireTick = tick;
            victimCs.LastTookFireAttacker = missile.ShooterId;
            world.SetCombatState(victim.Id, victimCs);
        }

        if (!killed)
            return;

        float shotDistance = Vector2.Distance(
            new Vector2(detOrigin.X, detOrigin.Z),
            victim.Position);

        if (_flight is not null && world.IsAirborne(victim.Id) &&
            _flight.TryBeginCrash(
                world,
                victim.Id,
                missile.ShooterId,
                missile.ShooterDefinition,
                missile.Owner,
                shotDistance))
        {
            return;
        }

        TotalUnitsDestroyed++;
        if (missile.ShooterId.Value != 0 && _killCount < _kills.Length)
        {
            _kills[_killCount++] = new CombatKillRecord(
                tick,
                missile.ShooterId,
                missile.ShooterDefinition,
                missile.Owner,
                victim.Id,
                victim.Definition,
                victim.Owner,
                shotDistance);
        }
        DestroyUnit(victim.Id, world, tick);
    }

    public void RecordRocketLaunchedEvent(
        int tick,
        ushort presentationId,
        EntityId shooterId,
        int shooterOwner,
        Vector3 origin,
        Vector3 target)
    {
        uint eventId = _nextEventId++;
        if (_nextEventId == 0) _nextEventId = 1;

        TotalShotsFired++;
        if (FirstShotTick < 0)
        {
            FirstShotTick = tick;
            FirstShotDistance = Vector2.Distance(new Vector2(origin.X, origin.Z), new Vector2(target.X, target.Z));
        }

        for (int p = 1; p <= _maxSides; p++)
        {
            if (p == shooterOwner)
            {
                CombatEventObservation ev = new(
                    eventId,
                    tick,
                    CombatEventKind.RocketLaunched,
                    presentationId,
                    origin,
                    target,
                    shooterId,
                    HitResult.None);
                AppendEvent(p, ev);
            }
            else if (_detection.IsEntityObservedBy(p, shooterId))
            {
                CombatEventObservation ev = new(
                    eventId,
                    tick,
                    CombatEventKind.RocketLaunched,
                    presentationId,
                    origin,
                    target,
                    default,
                    HitResult.None);
                AppendEvent(p, ev);
            }
        }
    }

    public void ResolveArtilleryImpact(WorldState world, int tick, RocketProjectileState projectile)
    {
        Vector2 impact2D = new(projectile.Target.X, projectile.Target.Z);
        float splashRadius = MathF.Max(1f, projectile.SplashRadius);
        float splashRadiusSq = splashRadius * splashRadius;
        int splashKilledCount = 0;

        for (int i = 0; i < world.Count; i++)
        {
            UnitState candidate = world.GetUnitAt(i);
            if (world.IsContained(candidate.Id) || world.IsCrashing(candidate.Id) || world.IsGarrisoned(candidate.Id))
                continue;

            float altitude = world.GetUnitAltitude(candidate.Id);
            if (altitude > splashRadius * 0.8f)
                continue;

            float distSq = Vector2.DistanceSquared(impact2D, candidate.Position);
            if (distSq > splashRadiusSq)
                continue;

            float dist = MathF.Sqrt(distSq);
            float radialFalloff = Math.Clamp(1f - (dist / splashRadius) * 0.78f, 0.22f, 1f);

            float armorMultiplier = 1f;
            if (_combatCatalog.TryGetProfile(candidate.Definition, out UnitCombatProfile targetProfile))
            {
                float topOrSideArmor = MathF.Min(targetProfile.Armor.Side, targetProfile.Armor.Rear);
                if (topOrSideArmor > projectile.Penetration)
                {
                    // Heavy armor resists outer fragments, while near top-deck hits and heavy concussive blasts inflict real damage
                    if (dist <= 6.5f)
                        armorMultiplier = 0.75f;
                    else if (dist <= splashRadius * 0.70f)
                        armorMultiplier = 0.36f;
                    else
                        armorMultiplier = 0.15f;
                }
                else if (topOrSideArmor > 0f)
                {
                    // Light armor (APC/recon): lethal at close range, moderate fragmentation damage farther out
                    armorMultiplier = dist <= splashRadius * 0.45f ? 0.90f : 0.55f;
                }
            }

            if (armorMultiplier <= 0f)
                continue;

            int damage = Math.Max(1, (int)MathF.Round(projectile.Damage * radialFalloff * armorMultiplier));
            world.ApplyDamage(candidate.Id, damage, out bool killed);
            TotalDamageApplied += damage;

            if (killed && splashKilledCount < _crashSplashKilledBuffer.Length)
            {
                _crashSplashKilledBuffer[splashKilledCount++] = candidate.Id;
            }
        }

        RecordArtilleryImpactEvent(tick, projectile.PresentationId, projectile.ShooterOwner, projectile.Origin, projectile.Target, world);

        if (_buildingDestruction is not null && projectile.StructuralDamage > 0)
        {
            int affected = _buildingDestruction.ApplySplashDamage(
                world,
                tick,
                projectile.Target,
                splashRadius,
                projectile.StructuralDamage,
                _splashStructureResults);

            for (int s = 0; s < affected; s++)
            {
                StructureSplashResult sr = _splashStructureResults[s];
                RecordStructureImpactEvent(
                    tick,
                    projectile.PresentationId,
                    default,
                    projectile.ShooterOwner,
                    sr.Id,
                    projectile.Origin,
                    sr.ImpactPoint,
                    sr.FloorsLost > 0 && !sr.SectionCollapsed,
                    sr.SectionCollapsed,
                    world);

                ApplyGarrisonStructuralCasualties(
                    world,
                    tick,
                    sr.Id,
                    projectile.StructuralDamage,
                    sr.FloorsLost,
                    sr.SectionCollapsed,
                    immediateRemoval: true);
            }
        }

        float shotDistance = Vector2.Distance(
            new Vector2(projectile.Origin.X, projectile.Origin.Z),
            impact2D);

        for (int k = 0; k < splashKilledCount; k++)
        {
            EntityId victimId = _crashSplashKilledBuffer[k];
            if (!world.TryGetUnit(victimId, out UnitState victim))
                continue;

            if (_flight is not null && world.IsAirborne(victimId) &&
                _flight.TryBeginCrash(
                    world,
                    victimId,
                    projectile.ShooterId,
                    projectile.ShooterDefinition,
                    projectile.ShooterOwner,
                    shotDistance))
            {
                continue;
            }

            TotalUnitsDestroyed++;
            if (projectile.ShooterId.Value != 0 && _killCount < _kills.Length)
            {
                _kills[_killCount++] = new CombatKillRecord(
                    tick,
                    projectile.ShooterId,
                    projectile.ShooterDefinition,
                    projectile.ShooterOwner,
                    victim.Id,
                    victim.Definition,
                    victim.Owner,
                    shotDistance);
            }
            DestroyUnit(victimId, world, tick);
        }
    }

    private void RecordArtilleryImpactEvent(
        int tick,
        ushort presentationId,
        int shooterOwner,
        Vector3 origin,
        Vector3 impactPoint,
        WorldState world)
    {
        uint eventId = _nextEventId++;
        if (_nextEventId == 0) _nextEventId = 1;

        Vector2 impact2D = new(impactPoint.X, impactPoint.Z);
        const float HearBlastRadiusSq = 350f * 350f;

        for (int p = 1; p <= _maxSides; p++)
        {
            bool visible = (p == shooterOwner);
            if (!visible)
            {
                for (int i = 0; i < world.Count; i++)
                {
                    UnitState u = world.GetUnitAt(i);
                    if (u.Owner == p && !world.IsContained(u.Id) &&
                        Vector2.DistanceSquared(u.Position, impact2D) <= HearBlastRadiusSq)
                    {
                        visible = true;
                        break;
                    }
                }
            }

            if (visible)
            {
                // Never disclose hidden launcher origin to non-owner sides
                Vector3 eventOrigin = p == shooterOwner ? origin : impactPoint;
                CombatEventObservation ev = new(
                    eventId,
                    tick,
                    CombatEventKind.ArtilleryImpact,
                    presentationId,
                    eventOrigin,
                    impactPoint,
                    default,
                    HitResult.None);
                AppendEvent(p, ev);
            }
        }
    }

    private void HandleContainerDestruction(EntityId transportId, WorldState world, int tick, int? damageOverride = null)
    {
        if (!world.TryGetUnit(transportId, out UnitState transport)) return;
        ReadOnlySpan<EntityId> passengers = world.GetPassengers(transportId);
        if (passengers.Length == 0) return;

        Span<EntityId> buffer = stackalloc EntityId[passengers.Length];
        passengers.CopyTo(buffer);

        Span<EntityId> survivors = stackalloc EntityId[buffer.Length];
        int survivorCount = 0;

        int destructionDamage = damageOverride ?? DefaultContainerDestructionDamage;
        if (!damageOverride.HasValue && _combatCatalog is not null && _combatCatalog.TryGetProfile(transport.Definition, out UnitCombatProfile transProfile))
        {
            destructionDamage = transProfile.ContainerDestructionDamage;
        }

        for (int p = 0; p < buffer.Length; p++)
        {
            EntityId pId = buffer[p];
            world.ApplyDamage(pId, destructionDamage, out bool killed);
            if (killed)
            {
                if (world.TryGetUnit(pId, out UnitState deadPassenger))
                {
                    TotalUnitsDestroyed++;
                    RecordDestroyedEvent(
                        tick,
                        deadPassenger.Definition,
                        deadPassenger.Id,
                        deadPassenger.Owner,
                        deadPassenger.Position,
                        deadPassenger.SupportSurface,
                        default);
                    world.RemoveUnit(pId);
                    _navigation?.ClearUnit(pId);
                    _detection.NotifyEntityDestroyed(pId);
                }
            }
            else
            {
                survivors[survivorCount++] = pId;
            }
        }

        if (survivorCount > 0)
        {
            Span<Vector2> placedPositions = stackalloc Vector2[survivorCount];
            Span<bool> placedSuccess = stackalloc bool[survivorCount];

            PassengerDistribution.Distribute(
                world,
                _destinationEvaluator,
                transportId,
                transport.Position,
                transport.SupportSurface,
                survivors.Slice(0, survivorCount),
                placedPositions,
                placedSuccess,
                _navigation?.Catalog);

            for (int s = 0; s < survivorCount; s++)
            {
                EntityId sId = survivors[s];
                if (placedSuccess[s])
                {
                    world.Disembark(transportId, sId, placedPositions[s], transport.SupportSurface);
                }
                else
                {
                    world.ApplyDamage(sId, 9999, out _);
                    if (world.TryGetUnit(sId, out UnitState killedUnit))
                    {
                        TotalUnitsDestroyed++;
                        RecordDestroyedEvent(
                            tick,
                            killedUnit.Definition,
                            killedUnit.Id,
                            killedUnit.Owner,
                            killedUnit.Position,
                            killedUnit.SupportSurface,
                            default);
                        world.RemoveUnit(sId);
                        _navigation?.ClearUnit(sId);
                        _detection.NotifyEntityDestroyed(sId);
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
        Vector3 hitPoint,
        HitResult result = HitResult.None)
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
                    attackerId,
                    result);
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
                        default,
                        result);
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
                        default,
                        result);
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
                        default,
                        result);
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
        Vector2 targetPos,
        uint targetSupportSurface = 0,
        WreckId wreckId = default)
    {
        uint eventId = _nextEventId++;
        if (_nextEventId == 0) _nextEventId = 1;

        float groundY = _geometry.GetSurfaceHeight(targetPos, targetSupportSurface);
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
                    wreckId);
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
                        wreckId);
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

    private void ProcessAttackGroundOrder(
        int tick,
        int unitIndex,
        UnitState attacker,
        UnitCombatProfile profile,
        WeaponDefinition weapon,
        ref WorldState.CombatState cs,
        WorldState world)
    {
        StructureId targetStructure = cs.AttackGroundStructureId;
        Vector3 targetPoint = cs.AttackGroundTarget;

        // Respect fog of war: only auto-clear or adjust aim height based on what the attacker's side knows
        if (targetStructure != default &&
            world.TryGetDestructibleStructure(targetStructure, out DestructibleStructureState st) &&
            _detection.TryGetKnownStructure(attacker.Owner, targetStructure, out StructureObservation known))
        {
            if (known.FloorsStanding == 0)
            {
                cs.HasAttackGroundOrder = false;
                cs.AttackGroundTarget = default;
                cs.AttackGroundStructureId = default;
                cs.Status = AttackExecutionStatus.None;
                cs.PursuitDestination = Vector2.Zero;
                cs.LastPursuitRequestTick = -1;
                world.SetCombatState(attacker.Id, cs);
                return;
            }

            // Clamp aim height to known standing floors if upper floors collapsed
            float knownMaxY = StructureDestructionMath.ComputeCurrentHeight(
                st.Min.Y,
                st.Max.Y,
                in st.Profile,
                known.FloorsStanding);
            if (targetPoint.Y > knownMaxY - 0.1f)
            {
                targetPoint = new Vector3(
                    targetPoint.X,
                    MathF.Max(st.Min.Y + 0.25f, knownMaxY - 0.35f),
                    targetPoint.Z);
                cs.AttackGroundTarget = targetPoint;
            }
        }

        Vector2 target2D = new(targetPoint.X, targetPoint.Z);
        TurnTurret(ref cs, attacker.Position, target2D, profile.TurretTraverseRate);

        float dist = Vector2.Distance(attacker.Position, target2D);
        if (dist > weapon.Range + RangeToleranceMargin)
        {
            cs.Status = AttackExecutionStatus.OutOfRange;
            TryPursueTarget(tick, attacker, target2D, ref cs, world);
            world.SetCombatState(attacker.Id, cs);
            return;
        }

        if (!HasLineOfFireToPoint(attacker, weapon, targetPoint, targetStructure, world, out Vector3 impactPoint, out Vector3 muzzlePos))
        {
            cs.Status = AttackExecutionStatus.Obstructed;
            TryPursueTarget(tick, attacker, target2D, ref cs, world);
            world.SetCombatState(attacker.Id, cs);
            return;
        }

        if (attacker.Moving)
        {
            StopUnitMovement(attacker, world);
            attacker = world.GetUnitAt(unitIndex);
        }

        cs.PursuitDestination = Vector2.Zero;
        cs.LastPursuitRequestTick = -1;

        if (cs.Ammunition <= 0)
        {
            cs.Status = AttackExecutionStatus.NoAmmo;
            world.SetCombatState(attacker.Id, cs);
            return;
        }

        if (cs.ReloadTicksRemaining > 0 || !IsTurretAligned(cs.TurretYaw, attacker.Position, target2D))
        {
            cs.Status = AttackExecutionStatus.Aiming;
            world.SetCombatState(attacker.Id, cs);
            return;
        }

        ExecuteAttackGroundShot(tick, attacker, weapon, muzzlePos, impactPoint, targetStructure, ref cs, world);
    }

    private bool HasLineOfFireToPoint(
        UnitState attacker,
        WeaponDefinition weapon,
        Vector3 targetPoint,
        StructureId targetStructure,
        WorldState world,
        out Vector3 impactPoint,
        out Vector3 muzzlePos)
    {
        if (_garrison is null || !_garrison.TryGetElevatedPoint(world, attacker.Id, new Vector2(targetPoint.X, targetPoint.Z), out muzzlePos))
        {
            float attackerGround = _geometry.GetSurfaceHeight(attacker.Position, attacker.SupportSurface);
            float attackerAlt = world.GetUnitAltitude(attacker.Id);
            muzzlePos = new Vector3(attacker.Position.X, attackerGround + attackerAlt + weapon.MuzzleHeight, attacker.Position.Y);
        }
        impactPoint = targetPoint;

        LineOfSightResult lof = _geometry.CheckLineOfFire(muzzlePos, targetPoint, _losContext, out _);
        if (lof.IsClear)
        {
            return true;
        }

        if (targetStructure != default &&
            lof.ObstructionKind == ObstructionKind.Structure &&
            lof.ObstructingStructureId == targetStructure)
        {
            impactPoint = lof.HitPoint;
            return true;
        }

        return false;
    }

    private void ExecuteAttackGroundShot(
        int tick,
        UnitState attacker,
        WeaponDefinition weapon,
        Vector3 muzzlePos,
        Vector3 impactPoint,
        StructureId targetStructure,
        ref WorldState.CombatState cs,
        WorldState world)
    {
        cs.Ammunition = Math.Max(0, cs.Ammunition - 1);
        cs.ReloadTicksRemaining = weapon.ReloadDurationTicks;
        cs.Status = cs.Ammunition <= 0 ? AttackExecutionStatus.NoAmmo : AttackExecutionStatus.Aiming;
        world.RecordUnitFired(attacker.Id, tick);

        TotalShotsFired++;
        if (FirstShotTick < 0)
        {
            FirstShotTick = tick;
            FirstShotDistance = Vector3.Distance(muzzlePos, impactPoint);
        }

        // Record knowledge-filtered ShotFired event for client tracer/muzzle presentation
        uint shotEventId = _nextEventId++;
        if (_nextEventId == 0) _nextEventId = 1;
        for (int p = 1; p <= _maxSides; p++)
        {
            if (p == attacker.Owner)
            {
                AppendEvent(p, new CombatEventObservation(
                    shotEventId,
                    tick,
                    CombatEventKind.ShotFired,
                    weapon.PresentationId,
                    muzzlePos,
                    impactPoint,
                    attacker.Id,
                    HitResult.Penetrated));
            }
            else if (_detection.IsEntityObservedBy(p, attacker.Id))
            {
                AppendEvent(p, new CombatEventObservation(
                    shotEventId,
                    tick,
                    CombatEventKind.ShotFired,
                    weapon.PresentationId,
                    muzzlePos,
                    impactPoint,
                    default,
                    HitResult.Penetrated));
            }
        }

        if (_buildingDestruction is not null && targetStructure != default && weapon.StructuralDamage > 0)
        {
            if (_buildingDestruction.ApplyDirectHit(
                    world,
                    tick,
                    targetStructure,
                    impactPoint,
                    weapon.StructuralDamage,
                    out _,
                    out byte floorsLost,
                    out bool sectionFullyCollapsed))
            {
                RecordStructureImpactEvent(
                    tick,
                    weapon.PresentationId,
                    attacker.Id,
                    attacker.Owner,
                    targetStructure,
                    muzzlePos,
                    impactPoint,
                    floorsLost > 0 && !sectionFullyCollapsed,
                    sectionFullyCollapsed,
                    world);

                ApplyGarrisonStructuralCasualties(
                    world,
                    tick,
                    targetStructure,
                    weapon.StructuralDamage,
                    floorsLost,
                    sectionFullyCollapsed,
                    immediateRemoval: false);

                if (sectionFullyCollapsed &&
                    world.TryGetDestructibleStructure(targetStructure, out DestructibleStructureState collapsedSt) &&
                    _detection.IsStructureObservedBy(attacker.Owner, in collapsedSt, world))
                {
                    cs.HasAttackGroundOrder = false;
                    cs.AttackGroundTarget = default;
                    cs.AttackGroundStructureId = default;
                    cs.Status = cs.Ammunition <= 0 ? AttackExecutionStatus.NoAmmo : AttackExecutionStatus.None;
                }
            }
        }
        else if (targetStructure == default)
        {
            // Pure ground-point AttackGround fires a single shot and returns the unit to normal auto-fire
            cs.HasAttackGroundOrder = false;
            cs.AttackGroundTarget = default;
            cs.AttackGroundStructureId = default;
            cs.Status = cs.Ammunition <= 0 ? AttackExecutionStatus.NoAmmo : AttackExecutionStatus.None;
        }

        world.SetCombatState(attacker.Id, cs);
    }

    private void RecordStructureImpactEvent(
        int tick,
        ushort presentationId,
        EntityId shooterId,
        int shooterOwner,
        StructureId structureId,
        Vector3 origin,
        Vector3 impactPoint,
        bool isFloorCollapse,
        bool isFullCollapse,
        WorldState world)
    {
        if (!world.TryGetDestructibleStructure(structureId, out DestructibleStructureState st))
            return;

        uint eventId = _nextEventId++;
        if (_nextEventId == 0) _nextEventId = 1;
        CombatEventKind kind = isFullCollapse
            ? CombatEventKind.StructureCollapsed
            : (isFloorCollapse ? CombatEventKind.StructureFloorCollapsed : CombatEventKind.StructureDamaged);

        for (int p = 1; p <= _maxSides; p++)
        {
            // Strict knowledge filter: only notify sides that actually observe the struck structure.
            if (!_detection.IsStructureObservedBy(p, in st, world))
                continue;

            // Never leak a hidden artillery or direct-fire shooter's origin to sides that do not observe the shooter.
            bool shooterVisible = p == shooterOwner ||
                (shooterId.Value != 0 && _detection.IsEntityObservedBy(p, shooterId));
            Vector3 safeOrigin = shooterVisible ? origin : impactPoint;

            AppendEvent(p, CombatEventObservation.ForStructure(
                eventId,
                tick,
                kind,
                presentationId,
                safeOrigin,
                impactPoint,
                structureId,
                (isFloorCollapse || isFullCollapse) ? HitResult.Penetrated : HitResult.None));
        }
    }

    private void ApplyGarrisonStructuralCasualties(
        WorldState world,
        int tick,
        StructureId structureId,
        int effectiveStructuralDamage,
        byte floorsLost,
        bool sectionCollapsed,
        bool immediateRemoval)
    {
        if (_garrison is null)
            return;

        int casualtyCount = _garrison.ComputeStructuralHitCasualties(
            world,
            structureId,
            effectiveStructuralDamage,
            floorsLost,
            sectionCollapsed,
            _garrisonCasualtyBuffer);

        for (int i = 0; i < casualtyCount; i++)
        {
            GarrisonCasualty c = _garrisonCasualtyBuffer[i];
            if (!world.TryGetUnit(c.UnitId, out UnitState victim))
                continue;

            world.ApplyDamage(c.UnitId, c.Damage, out bool killed);
            TotalDamageApplied += c.Damage;
            if (!killed)
                continue;

            _garrison.RecordCollapseCasualty();
            if (immediateRemoval)
            {
                TotalUnitsDestroyed++;
                RecordDestroyedEvent(tick, victim.Definition, victim.Id, victim.Owner, victim.Position, victim.SupportSurface);
                world.RemoveUnit(victim.Id);
                _navigation?.ClearUnit(victim.Id);
                _detection.NotifyEntityDestroyed(victim.Id);
            }
            else
            {
                bool alreadyQueued = false;
                for (int d = 0; d < _destroyedCount; d++)
                {
                    if (_destroyedBuffer[d].Value == victim.Id.Value)
                    {
                        alreadyQueued = true;
                        break;
                    }
                }
                if (!alreadyQueued && _destroyedCount < _destroyedBuffer.Length)
                {
                    TotalUnitsDestroyed++;
                    RecordDestroyedEvent(tick, victim.Definition, victim.Id, victim.Owner, victim.Position, victim.SupportSurface);
                    _destroyedBuffer[_destroyedCount++] = victim.Id;
                }
            }
        }
    }
}
