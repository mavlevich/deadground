using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Server-authoritative simulation system for rotorcraft and helicopters.
/// Manages takeoff, airborne flight, NOE/High altitude bands, smooth yaw/acceleration/braking,
/// airspace separation, landing zone validation, landing descent, and deterministic crash spin-down.
/// Strictly allocation-free during simulation ticks.
/// </summary>
public sealed class FlightSystem : ISimulationSystem
{
    public const float ArrivalDistance = 1.25f;
    public const float AltitudeTolerance = 0.08f;
    public const float HoverStopSpeed = 0.25f;
    public const float MinCrashAltitude = 0.5f;
    public const float SlingPickupDistance = 3.5f;
    public const int SlingHookupTicks = 20;
    public const int SlingDropTicks = 10;

    private readonly StaticGeometry _geometry;
    private readonly DestinationEvaluator _destinationEvaluator;
    private readonly IUnitFlightCatalog _catalog;
    private readonly int _maxUnits;

    private readonly EntityId[] _airIds;
    private readonly Vector2[] _airPositions;
    private readonly float[] _airRadii;
    private readonly Vector2[] _airSeparationOffsets;
    private readonly EntityId[] _impactIds;
    private readonly FlightProfile[] _impactProfiles;
    private readonly WorldState.UnitFlightState[] _impactStates;
    private readonly Vector2[] _impactPositions;
    private readonly float[] _impactHeadings;
    private readonly ushort[] _impactDefinitions;
    private readonly int[] _impactOwners;

    public delegate void CrashImpactHandler(
        WorldState world,
        int tick,
        EntityId unitId,
        ushort definition,
        int owner,
        Vector2 impactPosition,
        float heading,
        in FlightProfile profile,
        in WorldState.UnitFlightState flightState);

    public CrashImpactHandler? OnCrashImpact { get; set; }

    public int TotalTakeoffs { get; private set; }
    public int TotalLandings { get; private set; }
    public int TotalCrashImpacts { get; private set; }
    public int TotalFastRopeInsertions { get; private set; }
    public int TotalSlingPickups { get; private set; }
    public int TotalSlingDrops { get; private set; }

    public StaticGeometry Geometry => _geometry;
    public IUnitFlightCatalog Catalog => _catalog;

    public FlightSystem(StaticGeometry geometry, IUnitFlightCatalog catalog, int maxUnits = 1024)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _destinationEvaluator = new DestinationEvaluator(_geometry);
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _maxUnits = Math.Max(1, maxUnits);

        _airIds = new EntityId[_maxUnits];
        _airPositions = new Vector2[_maxUnits];
        _airRadii = new float[_maxUnits];
        _airSeparationOffsets = new Vector2[_maxUnits];
        _impactIds = new EntityId[_maxUnits];
        _impactProfiles = new FlightProfile[_maxUnits];
        _impactStates = new WorldState.UnitFlightState[_maxUnits];
        _impactPositions = new Vector2[_maxUnits];
        _impactHeadings = new float[_maxUnits];
        _impactDefinitions = new ushort[_maxUnits];
        _impactOwners = new int[_maxUnits];
    }

    public void EnsureUnitsInitialized(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        for (int i = 0; i < world.Count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            EnsureUnitInitialized(world, unit.Id, unit.Definition);
        }
    }

    public bool EnsureUnitInitialized(WorldState world, EntityId unitId, ushort definition)
    {
        if (!world.TryGetFlightState(unitId, out WorldState.UnitFlightState state))
            return false;
        if (state.Initialized)
            return state.HasCapability;

        if (_catalog.TryGetFlightProfile(definition, out FlightProfile profile) && profile.IsValid)
        {
            FlightPhase initialPhase = profile.StartAirborne ? FlightPhase.Airborne : FlightPhase.Ground;
            float initialAlt = profile.StartAirborne ? profile.HighAltitude : 0f;
            state = new WorldState.UnitFlightState(
                Initialized: true,
                HasCapability: true,
                Phase: initialPhase,
                DesiredBand: AltitudeBand.High,
                Altitude: initialAlt,
                Velocity: Vector2.Zero,
                TargetPosition: Vector2.Zero,
                HasMoveTarget: false,
                LandOnArrival: false,
                HasFinalFacing: false,
                FinalFacing: 0f,
                PitchRad: 0f,
                RollRad: 0f,
                CrashSpinVelocity: 0f,
                CrashTicks: 0,
                CrashKillerId: default,
                CrashKillerDefinition: 0,
                CrashKillerOwner: 0,
                CrashShotDistance: 0f);
            world.SetFlightState(unitId, in state);
            return true;
        }

        state = state with { Initialized = true, HasCapability = false };
        world.SetFlightState(unitId, in state);
        return false;
    }

    public bool IsAircraftDefinition(ushort definition) =>
        _catalog.TryGetFlightProfile(definition, out FlightProfile profile) && profile.IsValid;

    public bool TryGetProfile(ushort definition, out FlightProfile profile)
    {
        if (_catalog.TryGetFlightProfile(definition, out profile) && profile.IsValid)
            return true;
        profile = default;
        return false;
    }

    public bool TryGetCargoProfile(ushort definition, out CargoProfile cargo) =>
        _catalog.TryGetCargoProfile(definition, out cargo);

    public bool IsInfantryDefinition(ushort definition) =>
        _catalog.TryGetCargoProfile(definition, out CargoProfile cargo) ? cargo.IsInfantry : true;

    public bool HasInfantryPassenger(WorldState world, EntityId heliId)
    {
        ReadOnlySpan<EntityId> passengers = world.GetPassengers(heliId);
        for (int i = 0; i < passengers.Length; i++)
        {
            if (world.TryGetUnit(passengers[i], out UnitState p) && IsInfantryDefinition(p.Definition))
                return true;
        }
        return false;
    }

    public bool CanSlingTarget(
        WorldState world,
        EntityId heliId,
        int heliOwner,
        in FlightProfile profile,
        EntityId targetId,
        out float weightKg)
    {
        weightKg = 0f;
        if (profile.MaxSlingWeightKg <= 0f || targetId == default || targetId == heliId)
            return false;
        if (!world.TryGetUnit(targetId, out UnitState target) || target.Owner != heliOwner)
            return false;
        if (world.IsContained(targetId) || world.IsAircraft(targetId) || world.IsCrashing(targetId) || world.IsSlung(targetId))
            return false;
        if (!_catalog.TryGetCargoProfile(target.Definition, out CargoProfile cargo) || !cargo.CanSlingLoad)
            return false;
        if (!float.IsFinite(cargo.WeightKg) || cargo.WeightKg <= 0f || cargo.WeightKg > profile.MaxSlingWeightKg)
            return false;
        weightKg = cargo.WeightKg;
        return true;
    }

    public bool OrderMove(
        WorldState world,
        EntityId unitId,
        Vector2 target,
        float? finalFacing,
        bool landOnArrival,
        int tick)
    {
        if (!world.TryGetUnit(unitId, out UnitState unit) || !world.Map.Contains(target))
            return false;
        if (!EnsureUnitInitialized(world, unitId, unit.Definition))
            return false;
        if (!world.TryGetFlightState(unitId, out WorldState.UnitFlightState state) || state.Phase == FlightPhase.Crashing)
            return false;

        // Clear any boarding orders targeting this helicopter if it starts moving or taking off
        ClearIncomingBoardingOrders(world, unitId);

        // Moving cancels pending/active fast-rope and sling pickup/drop sequences (while keeping attached sling load)
        state.FastRopePending = false;
        state.FastRopeActive = false;
        state.FastRopeCooldownTicks = 0;
        state.PendingSlingTargetId = default;
        state.SlingDropPending = false;
        state.SlingActionTicks = 0;

        state.TargetPosition = target;
        state.HasMoveTarget = true;
        state.LandOnArrival = landOnArrival;
        state.HasFinalFacing = finalFacing.HasValue && float.IsFinite(finalFacing.Value);
        state.FinalFacing = state.HasFinalFacing ? CombatSystem.NormalizeYaw(finalFacing!.Value) : 0f;

        if (state.Phase is FlightPhase.Ground or FlightPhase.Landing)
        {
            float distSq = Vector2.DistanceSquared(unit.Position, target);
            if (distSq > ArrivalDistance * ArrivalDistance || !landOnArrival)
            {
                state.Phase = FlightPhase.TakingOff;
                TotalTakeoffs++;
            }
        }

        world.SetFlightState(unitId, in state);
        world.UpdateUnitNavigation(unitId, unit.Position, target, true, tick, supportSurface: 0);
        return true;
    }

    public void HoldPosition(WorldState world, EntityId unitId)
    {
        if (!world.TryGetFlightState(unitId, out WorldState.UnitFlightState state) ||
            !state.HasCapability ||
            state.Phase == FlightPhase.Crashing)
        {
            return;
        }

        state.HasMoveTarget = false;
        state.LandOnArrival = false;
        state.FastRopePending = false;
        state.FastRopeActive = false;
        state.PendingSlingTargetId = default;
        state.SlingDropPending = false;
        state.SlingActionTicks = 0;
        world.SetFlightState(unitId, in state);
    }

    public bool OrderHelicopter(WorldState world, HelicopterCommand command, int tick)
    {
        if (!world.TryGetUnit(command.Unit, out UnitState unit))
            return false;
        if (!EnsureUnitInitialized(world, command.Unit, unit.Definition))
            return false;
        if (!TryGetProfile(unit.Definition, out FlightProfile profile))
            return false;
        if (!world.TryGetFlightState(command.Unit, out WorldState.UnitFlightState state) || state.Phase == FlightPhase.Crashing)
            return false;

        switch (command.Kind)
        {
            case HelicopterOrderKind.TakeOff:
                state.FastRopePending = false;
                state.FastRopeActive = false;
                state.FastRopeCooldownTicks = 0;
                state.PendingSlingTargetId = default;
                state.SlingDropPending = false;
                state.SlingActionTicks = 0;
                if (state.Phase is FlightPhase.Ground or FlightPhase.Landing)
                {
                    ClearIncomingBoardingOrders(world, command.Unit);
                    state.Phase = FlightPhase.TakingOff;
                    state.HasMoveTarget = false;
                    state.LandOnArrival = false;
                    TotalTakeoffs++;
                    world.SetFlightState(command.Unit, in state);
                    world.UpdateUnitNavigation(command.Unit, unit.Position, unit.Position, true, tick, supportSurface: 0);
                    return true;
                }
                world.SetFlightState(command.Unit, in state);
                return true;

            case HelicopterOrderKind.Land:
                state.FastRopePending = false;
                state.FastRopeActive = false;
                state.FastRopeCooldownTicks = 0;
                state.PendingSlingTargetId = default;
                state.SlingDropPending = false;
                state.SlingActionTicks = 0;
                if (command.HasTarget)
                {
                    if (!world.Map.Contains(command.Target))
                        return false;
                    return OrderMove(world, command.Unit, command.Target, finalFacing: null, landOnArrival: true, tick);
                }

                if (state.Phase == FlightPhase.Ground)
                    return true;

                if (EvaluateLandingZone(_geometry, world, unit.Position, profile.LandingClearanceRadius, profile.MaxLandingSlopeDegrees, command.Unit) != LandingZoneStatus.Valid)
                    return false;

                state.HasMoveTarget = false;
                state.LandOnArrival = true;
                if (state.Phase is FlightPhase.Airborne or FlightPhase.TakingOff)
                {
                    state.Phase = FlightPhase.Landing;
                }
                world.SetFlightState(command.Unit, in state);
                return true;

            case HelicopterOrderKind.SetAltitudeBand:
                if (command.AltitudeBand > AltitudeBand.High)
                    return false;
                state.DesiredBand = command.AltitudeBand;
                world.SetFlightState(command.Unit, in state);
                return true;

            case HelicopterOrderKind.FastRope:
                if (!profile.CanFastRope || !HasInfantryPassenger(world, command.Unit))
                    return false;

                Vector2 ropeTarget = command.HasTarget ? command.Target : unit.Position;
                if (EvaluateFastRopeZone(_geometry, ropeTarget) != FastRopeZoneStatus.Valid)
                    return false;

                ClearIncomingBoardingOrders(world, command.Unit);
                state.LandOnArrival = false;
                state.PendingSlingTargetId = default;
                state.SlingDropPending = false;
                state.SlingActionTicks = 0;
                state.FastRopePending = true;
                state.FastRopeActive = false;
                state.FastRopeCooldownTicks = 0;
                state.TargetPosition = ropeTarget;
                state.HasMoveTarget = Vector2.DistanceSquared(unit.Position, ropeTarget) > ArrivalDistance * ArrivalDistance;
                if (state.Phase is FlightPhase.Ground or FlightPhase.Landing)
                {
                    state.Phase = FlightPhase.TakingOff;
                    TotalTakeoffs++;
                }
                world.SetFlightState(command.Unit, in state);
                world.UpdateUnitNavigation(command.Unit, unit.Position, ropeTarget, true, tick, supportSurface: 0);
                return true;

            case HelicopterOrderKind.SlingPickup:
                if (state.SlingLoadId != default)
                    return false;
                if (!CanSlingTarget(world, command.Unit, unit.Owner, in profile, command.TargetUnit, out _))
                    return false;
                if (!world.TryGetUnit(command.TargetUnit, out UnitState targetCargo))
                    return false;

                ClearIncomingBoardingOrders(world, command.Unit);
                state.LandOnArrival = false;
                state.FastRopePending = false;
                state.FastRopeActive = false;
                state.FastRopeCooldownTicks = 0;
                state.SlingDropPending = false;
                state.PendingSlingTargetId = command.TargetUnit;
                state.SlingActionTicks = 0;
                state.TargetPosition = targetCargo.Position;
                state.HasMoveTarget = Vector2.DistanceSquared(unit.Position, targetCargo.Position) > SlingPickupDistance * SlingPickupDistance;
                if (state.Phase is FlightPhase.Ground or FlightPhase.Landing)
                {
                    state.Phase = FlightPhase.TakingOff;
                    TotalTakeoffs++;
                }
                world.SetFlightState(command.Unit, in state);
                world.UpdateUnitNavigation(command.Unit, unit.Position, targetCargo.Position, true, tick, supportSurface: 0);
                return true;

            case HelicopterOrderKind.SlingDrop:
                if (state.SlingLoadId == default)
                    return false;

                Vector2 dropTarget = command.HasTarget ? command.Target : unit.Position;
                if (!world.Map.Contains(dropTarget))
                    return false;

                state.LandOnArrival = false;
                state.FastRopePending = false;
                state.FastRopeActive = false;
                state.FastRopeCooldownTicks = 0;
                state.PendingSlingTargetId = default;
                state.SlingDropPending = true;
                state.SlingActionTicks = 0;
                state.TargetPosition = dropTarget;
                state.HasMoveTarget = Vector2.DistanceSquared(unit.Position, dropTarget) > ArrivalDistance * ArrivalDistance;
                if (state.Phase is FlightPhase.Ground or FlightPhase.Landing)
                {
                    state.Phase = FlightPhase.TakingOff;
                    TotalTakeoffs++;
                }
                world.SetFlightState(command.Unit, in state);
                world.UpdateUnitNavigation(command.Unit, unit.Position, dropTarget, true, tick, supportSurface: 0);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Transitions a shot-down airborne helicopter into a deterministic crash spin-down sequence.
    /// Returns false if the unit is not airborne (meaning caller should destroy it immediately on the ground).
    /// </summary>
    public bool TryBeginCrash(
        WorldState world,
        EntityId unitId,
        EntityId killerId,
        ushort killerDefinition,
        int killerOwner,
        float shotDistance)
    {
        if (!world.TryGetUnit(unitId, out UnitState unit))
            return false;
        if (!EnsureUnitInitialized(world, unitId, unit.Definition))
            return false;
        if (!TryGetProfile(unit.Definition, out FlightProfile profile))
            return false;
        if (!world.TryGetFlightState(unitId, out WorldState.UnitFlightState state))
            return false;
        if (state.Phase == FlightPhase.Crashing)
            return true;
        if (state.Altitude <= MinCrashAltitude || state.Phase == FlightPhase.Ground)
            return false;

        world.ClearAttackOrder(unitId);
        world.ClearPendingDisembark(unitId);
        ClearIncomingBoardingOrders(world, unitId);

        // Give hovering aircraft deterministic forward drift so crashes spiral visibly across terrain
        Vector2 crashVel = state.Velocity;
        if (crashVel.LengthSquared() < 64f)
        {
            float heading = world.TryGetCombatState(unitId, out WorldState.CombatState cs) ? cs.HullHeading : 0f;
            Vector2 fwd = new(-MathF.Sin(heading), -MathF.Cos(heading));
            crashVel = fwd * 10.0f;
        }

        float spinDir = (unitId.Value & 1u) == 0u ? 1f : -1f;
        state.Phase = FlightPhase.Crashing;
        state.Velocity = crashVel;
        state.HasMoveTarget = false;
        state.LandOnArrival = false;
        state.FastRopePending = false;
        state.FastRopeActive = false;
        state.FastRopeCooldownTicks = 0;
        state.PendingSlingTargetId = default;
        state.SlingDropPending = false;
        state.SlingActionTicks = 0;
        state.CrashSpinVelocity = spinDir * (profile.CrashSpinRateDegPerSec * (MathF.PI / 180f));
        state.CrashTicks = 0;
        state.CrashKillerId = killerId;
        state.CrashKillerDefinition = killerDefinition;
        state.CrashKillerOwner = killerOwner;
        state.CrashShotDistance = shotDistance;
        world.SetFlightState(unitId, in state);
        return true;
    }

    public void Update(WorldState world, int tick)
    {
        ArgumentNullException.ThrowIfNull(world);
        EnsureUnitsInitialized(world);

        const float dt = Simulation.TickSeconds;

        // 1. Collect all airborne helicopters for zero-allocation airspace separation
        int airCount = 0;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            if (!world.TryGetFlightState(u.Id, out WorldState.UnitFlightState fs) || !fs.HasCapability)
                continue;
            if (fs.Phase is FlightPhase.TakingOff or FlightPhase.Airborne or FlightPhase.Landing)
            {
                if (airCount < _maxUnits)
                {
                    float r = TryGetProfile(u.Definition, out FlightProfile p) ? p.LandingClearanceRadius : 10f;
                    _airIds[airCount] = u.Id;
                    _airPositions[airCount] = u.Position;
                    _airRadii[airCount] = r;
                    _airSeparationOffsets[airCount] = Vector2.Zero;
                    airCount++;
                }
            }
        }

        for (int a = 0; a < airCount; a++)
        {
            for (int b = a + 1; b < airCount; b++)
            {
                Vector2 diff = _airPositions[a] - _airPositions[b];
                float minSep = (_airRadii[a] + _airRadii[b]) * 1.15f;
                float distSq = diff.LengthSquared();
                if (distSq < minSep * minSep)
                {
                    Vector2 pushDir;
                    float dist = MathF.Sqrt(distSq);
                    if (dist > 0.01f)
                    {
                        pushDir = diff / dist;
                    }
                    else
                    {
                        // Deterministic tie-breaker when overlapping exactly
                        float ang = ((_airIds[a].Value * 2654435761u) % 360u) * (MathF.PI / 180f);
                        pushDir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                    }
                    float strength = MathF.Min(12.0f, (minSep - dist) * 1.1f);
                    _airSeparationOffsets[a] += pushDir * strength;
                    _airSeparationOffsets[b] -= pushDir * strength;
                }
            }
        }

        // 2. Step each aircraft unit
        int impactCount = 0;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            if (!world.TryGetFlightState(unit.Id, out WorldState.UnitFlightState state) || !state.HasCapability)
                continue;
            if (!TryGetProfile(unit.Definition, out FlightProfile profile))
                continue;

            bool hasCombat = world.TryGetCombatState(unit.Id, out WorldState.CombatState combat);
            float heading = hasCombat ? combat.HullHeading : 0f;
            Vector2 pos = unit.Position;

            // Validate attached sling load still exists
            float slingLoadWeightKg = 0f;
            if (state.SlingLoadId != default)
            {
                if (!world.TryGetUnit(state.SlingLoadId, out UnitState slungUnit) || world.IsContained(state.SlingLoadId))
                {
                    state.SlingLoadId = default;
                    state.SlingDropPending = false;
                    state.SlingActionTicks = 0;
                }
                else if (_catalog.TryGetCargoProfile(slungUnit.Definition, out CargoProfile loadCargo))
                {
                    slingLoadWeightKg = loadCargo.WeightKg;
                }
            }

            float slingPerfMult = profile.GetSlingPerformanceMultiplier(slingLoadWeightKg);

            switch (state.Phase)
            {
                case FlightPhase.Ground:
                    state.Altitude = 0f;
                    state.Velocity = Vector2.Zero;
                    state.PitchRad = Lerp(state.PitchRad, 0f, dt * 6f);
                    state.RollRad = Lerp(state.RollRad, 0f, dt * 6f);
                    state.FastRopeActive = false;
                    world.SetFlightState(unit.Id, in state);
                    world.UpdateUnitNavigation(unit.Id, pos, pos, false, supportSurface: 0);
                    break;

                case FlightPhase.TakingOff:
                    {
                        float targetAlt = ResolveTargetAltitude(in state, in profile, pos);
                        float climbRate = profile.ClimbRate * slingPerfMult;
                        state.Altitude = MathF.Min(targetAlt, state.Altitude + climbRate * dt);
                        state.Velocity = MoveToward(state.Velocity, Vector2.Zero, profile.Deceleration * dt);
                        state.PitchRad = Lerp(state.PitchRad, 0f, dt * 4f);
                        state.RollRad = Lerp(state.RollRad, 0f, dt * 4f);

                        if (state.Altitude >= targetAlt - AltitudeTolerance)
                        {
                            state.Altitude = targetAlt;
                            state.Phase = FlightPhase.Airborne;
                        }
                        world.SetFlightState(unit.Id, in state);
                        world.UpdateUnitNavigation(unit.Id, pos, state.HasMoveTarget ? state.TargetPosition : pos, true, tick, supportSurface: 0);
                        SyncAttachedSlingLoad(world, unit.Id, state.SlingLoadId, pos, heading, state.Altitude, in profile, tick);
                        break;
                    }

                case FlightPhase.Airborne:
                    {
                        // Track live position of a pending sling pickup target
                        if (state.PendingSlingTargetId != default)
                        {
                            if (state.SlingLoadId != default ||
                                !CanSlingTarget(world, unit.Id, unit.Owner, in profile, state.PendingSlingTargetId, out _) ||
                                !world.TryGetUnit(state.PendingSlingTargetId, out UnitState pendingCargo))
                            {
                                state.PendingSlingTargetId = default;
                                state.SlingActionTicks = 0;
                                state.HasMoveTarget = false;
                            }
                            else
                            {
                                state.TargetPosition = pendingCargo.Position;
                                float pickupDistSq = Vector2.DistanceSquared(pos, pendingCargo.Position);
                                state.HasMoveTarget = pickupDistSq > SlingPickupDistance * SlingPickupDistance;
                            }
                        }

                        // Adjust altitude smoothly toward desired band or active cargo/insertion maneuver altitude
                        float desiredAlt = ResolveTargetAltitude(in state, in profile, pos);
                        float climbRate = profile.ClimbRate * slingPerfMult;
                        if (state.Altitude < desiredAlt - AltitudeTolerance)
                            state.Altitude = MathF.Min(desiredAlt, state.Altitude + climbRate * dt);
                        else if (state.Altitude > desiredAlt + AltitudeTolerance)
                            state.Altitude = MathF.Max(desiredAlt, state.Altitude - profile.DescentRate * dt);
                        else
                            state.Altitude = desiredAlt;

                        Vector2 prevVel = state.Velocity;
                        float maxSpeed = profile.GetMaxSpeed(state.DesiredBand) * slingPerfMult;
                        float accelRate = profile.Acceleration * slingPerfMult;
                        float yawRateRad = profile.YawRateDegPerSec * (MathF.PI / 180f);

                        // Airspace separation vector for this unit
                        Vector2 sepVel = Vector2.Zero;
                        float selfRadius = profile.LandingClearanceRadius;
                        for (int k = 0; k < airCount; k++)
                        {
                            if (_airIds[k] == unit.Id)
                            {
                                sepVel = _airSeparationOffsets[k];
                                selfRadius = _airRadii[k];
                                break;
                            }
                        }

                        if (state.HasMoveTarget)
                        {
                            Vector2 toTarget = state.TargetPosition - pos;
                            float dist = toTarget.Length();

                            // If another helicopter already occupies the target point or holds deterministic priority
                            // on the same target airspace, settle into a clean standoff hover instead of fighting separation.
                            bool targetOccupiedByOther = false;
                            for (int k = 0; k < airCount; k++)
                            {
                                EntityId otherId = _airIds[k];
                                if (otherId == unit.Id) continue;
                                float pairMinSep = (selfRadius + _airRadii[k]) * 1.15f;
                                if (dist > pairMinSep * 1.25f) continue;

                                float otherDistToTarget = Vector2.Distance(_airPositions[k], state.TargetPosition);
                                bool otherIsLanding = world.TryGetFlightState(otherId, out WorldState.UnitFlightState otherFs) &&
                                    otherFs.Phase == FlightPhase.Landing;
                                bool otherAtTarget = otherDistToTarget <= ArrivalDistance * 2.5f || otherIsLanding;
                                bool otherHasPriority = otherDistToTarget <= pairMinSep * 0.75f &&
                                    otherId.Value < unit.Id.Value &&
                                    otherDistToTarget <= dist + 2.0f;

                                if (otherAtTarget || otherHasPriority)
                                {
                                    targetOccupiedByOther = true;
                                    break;
                                }
                            }

                            float arrivalRadius = state.PendingSlingTargetId != default ? SlingPickupDistance : ArrivalDistance;
                            if (targetOccupiedByOther)
                            {
                                state.HasMoveTarget = false;
                                state.LandOnArrival = false;
                                state.FastRopePending = false;
                                state.PendingSlingTargetId = default;
                                state.SlingDropPending = false;
                                state.SlingActionTicks = 0;
                                state.Velocity = MoveToward(state.Velocity, sepVel, profile.Deceleration * dt);
                            }
                            else if (dist <= arrivalRadius)
                            {
                                state.HasMoveTarget = false;
                                if (state.LandOnArrival)
                                {
                                    if (EvaluateLandingZone(_geometry, world, pos, profile.LandingClearanceRadius, profile.MaxLandingSlopeDegrees, unit.Id) == LandingZoneStatus.Valid)
                                    {
                                        state.Phase = FlightPhase.Landing;
                                    }
                                    else
                                    {
                                        // Landing site blocked: hold safe hover rather than clipping obstacles
                                        state.LandOnArrival = false;
                                    }
                                }
                            }
                            else
                            {
                                Vector2 dir = toTarget / dist;
                                float desiredHeading = MathF.Atan2(-dir.X, -dir.Y);
                                heading = StepHeading(heading, desiredHeading, yawRateRad * dt);

                                // Smooth braking envelope so helicopter flares naturally into hover over target
                                float brakeSpeed = MathF.Sqrt(MathF.Max(0f, 2f * profile.Deceleration * MathF.Max(0f, dist - 0.25f)));
                                float targetSpeed = MathF.Min(maxSpeed, brakeSpeed);

                                // Reduce thrust while making a sharp turn in place
                                float headingError = MathF.Abs(CombatSystem.ShorterArcDelta(heading, desiredHeading));
                                float alignmentScale = headingError > 1.2f ? 0.25f : MathF.Max(0.45f, MathF.Cos(headingError * 0.6f));
                                Vector2 desiredVel = dir * (targetSpeed * alignmentScale) + sepVel;
                                float accelLimit = desiredVel.LengthSquared() >= state.Velocity.LengthSquared()
                                    ? accelRate
                                    : profile.Deceleration;
                                state.Velocity = MoveToward(state.Velocity, desiredVel, accelLimit * dt);
                            }
                        }
                        else
                        {
                            // Hovering in place (optionally aligning to requested final facing)
                            Vector2 desiredVel = sepVel;
                            state.Velocity = MoveToward(state.Velocity, desiredVel, profile.Deceleration * dt);
                            if (state.HasFinalFacing)
                            {
                                heading = StepHeading(heading, state.FinalFacing, yawRateRad * dt);
                                if (MathF.Abs(CombatSystem.ShorterArcDelta(heading, state.FinalFacing)) < 0.01f)
                                {
                                    state.HasFinalFacing = false;
                                }
                            }
                        }

                        Vector2 nextPos = ClampToMap(world.Map, pos + state.Velocity * dt);

                        // Process hover cargo / insertion actions once horizontally over target
                        if (!state.HasMoveTarget)
                        {
                            float horizSpeedSq = state.Velocity.LengthSquared();

                            // A. Sling pickup hookup countdown
                            if (state.PendingSlingTargetId != default)
                            {
                                if (MathF.Abs(state.Altitude - profile.LowAltitude) <= 1.5f && horizSpeedSq <= 4.0f)
                                {
                                    state.SlingActionTicks++;
                                    if (state.SlingActionTicks >= SlingHookupTicks)
                                    {
                                        EntityId loadId = state.PendingSlingTargetId;
                                        state.PendingSlingTargetId = default;
                                        state.SlingActionTicks = 0;
                                        if (CanSlingTarget(world, unit.Id, unit.Owner, in profile, loadId, out _))
                                        {
                                            AttachSlingLoad(world, unit.Id, loadId, nextPos, heading, state.Altitude, in profile, tick, ref state);
                                        }
                                    }
                                }
                            }
                            // B. Sling drop touchdown & release
                            else if (state.SlingDropPending && state.SlingLoadId != default)
                            {
                                float dropHoverAlt = profile.SlingCableLength + 1.0f;
                                if (EvaluateSlingDropZone(_geometry, world, nextPos, 3.0f, unit.Id, state.SlingLoadId) != SlingDropZoneStatus.Valid)
                                {
                                    state.SlingDropPending = false;
                                    state.SlingActionTicks = 0;
                                }
                                else if (MathF.Abs(state.Altitude - dropHoverAlt) <= 1.5f && horizSpeedSq <= 4.0f)
                                {
                                    state.SlingActionTicks++;
                                    if (state.SlingActionTicks >= SlingDropTicks)
                                    {
                                        DetachSlingLoad(world, nextPos, heading, tick, ref state);
                                    }
                                }
                            }
                            // C. Fast-rope hover insertion of infantry passengers
                            else if (state.FastRopePending || state.FastRopeActive)
                            {
                                if (!HasInfantryPassenger(world, unit.Id) ||
                                    EvaluateFastRopeZone(_geometry, nextPos) != FastRopeZoneStatus.Valid)
                                {
                                    state.FastRopePending = false;
                                    state.FastRopeActive = false;
                                    state.FastRopeCooldownTicks = 0;
                                }
                                else if (!state.FastRopeActive)
                                {
                                    if (MathF.Abs(state.Altitude - profile.FastRopeAltitude) <= 1.5f && horizSpeedSq <= 4.0f)
                                    {
                                        state.FastRopeActive = true;
                                        state.FastRopeCooldownTicks = 0;
                                    }
                                }
                                else
                                {
                                    if (state.FastRopeCooldownTicks > 0)
                                    {
                                        state.FastRopeCooldownTicks--;
                                    }
                                    else if (TryDeployNextFastRopeSoldier(world, unit.Id, nextPos))
                                    {
                                        TotalFastRopeInsertions++;
                                        if (HasInfantryPassenger(world, unit.Id))
                                        {
                                            state.FastRopeCooldownTicks = Math.Max(1, profile.FastRopeIntervalTicks);
                                        }
                                        else
                                        {
                                            state.FastRopePending = false;
                                            state.FastRopeActive = false;
                                            state.FastRopeCooldownTicks = 0;
                                        }
                                    }
                                    else
                                    {
                                        state.FastRopePending = false;
                                        state.FastRopeActive = false;
                                        state.FastRopeCooldownTicks = 0;
                                    }
                                }
                            }
                        }

                        Vector2 accel = (state.Velocity - prevVel) / dt;
                        Vector2 fwdVec = new(-MathF.Sin(heading), -MathF.Cos(heading));
                        Vector2 rightVec = new(MathF.Cos(heading), -MathF.Sin(heading));

                        float fwdSpeed = Vector2.Dot(state.Velocity, fwdVec);
                        float fwdAccel = Vector2.Dot(accel, fwdVec);
                        float latAccel = Vector2.Dot(accel, rightVec);

                        // Nose-down during forward acceleration & cruise, nose-up flare during braking
                        float targetPitch = Math.Clamp(-(fwdAccel * 0.016f) - (fwdSpeed / MathF.Max(1f, maxSpeed)) * 0.12f, -0.26f, 0.24f);
                        float targetRoll = Math.Clamp(-latAccel * 0.022f, -0.34f, 0.34f);
                        state.PitchRad = Lerp(state.PitchRad, targetPitch, dt * 4.5f);
                        state.RollRad = Lerp(state.RollRad, targetRoll, dt * 4.5f);

                        bool isMoving = state.HasMoveTarget || state.Velocity.LengthSquared() > HoverStopSpeed * HoverStopSpeed;
                        world.SetFlightState(unit.Id, in state);
                        world.UpdateUnitNavigation(
                            unit.Id,
                            nextPos,
                            state.HasMoveTarget ? state.TargetPosition : nextPos,
                            isMoving,
                            tick,
                            supportSurface: 0,
                            hullHeading: heading);
                        SyncAttachedSlingLoad(world, unit.Id, state.SlingLoadId, nextPos, heading, state.Altitude, in profile, tick);
                        break;
                    }

                case FlightPhase.Landing:
                    {
                        state.Velocity = MoveToward(state.Velocity, Vector2.Zero, profile.Deceleration * dt);
                        Vector2 nextPos = ClampToMap(world.Map, pos + state.Velocity * dt);

                        // Abort landing back to safe hover if LZ became obstructed before touchdown
                        if (state.Altitude > 1.5f &&
                            EvaluateLandingZone(_geometry, world, nextPos, profile.LandingClearanceRadius, profile.MaxLandingSlopeDegrees, unit.Id) != LandingZoneStatus.Valid)
                        {
                            state.Phase = FlightPhase.Airborne;
                            state.LandOnArrival = false;
                            world.SetFlightState(unit.Id, in state);
                            world.UpdateUnitNavigation(unit.Id, nextPos, nextPos, false, tick, supportSurface: 0, hullHeading: heading);
                            SyncAttachedSlingLoad(world, unit.Id, state.SlingLoadId, nextPos, heading, state.Altitude, in profile, tick);
                            break;
                        }

                        // Automatically release an attached sling load safely before helicopter wheels touch down
                        if (state.SlingLoadId != default && state.Altitude <= profile.SlingCableLength + 1.0f)
                        {
                            if (EvaluateSlingDropZone(_geometry, world, nextPos, 3.0f, unit.Id, state.SlingLoadId) == SlingDropZoneStatus.Valid)
                            {
                                DetachSlingLoad(world, nextPos, heading, tick, ref state);
                                // Abort touchdown so helicopter does not land on top of its just-released cargo
                                state.Phase = FlightPhase.Airborne;
                                state.LandOnArrival = false;
                                world.SetFlightState(unit.Id, in state);
                                world.UpdateUnitNavigation(unit.Id, nextPos, nextPos, false, tick, supportSurface: 0, hullHeading: heading);
                                break;
                            }
                            else
                            {
                                state.Phase = FlightPhase.Airborne;
                                state.LandOnArrival = false;
                                world.SetFlightState(unit.Id, in state);
                                world.UpdateUnitNavigation(unit.Id, nextPos, nextPos, false, tick, supportSurface: 0, hullHeading: heading);
                                SyncAttachedSlingLoad(world, unit.Id, state.SlingLoadId, nextPos, heading, state.Altitude, in profile, tick);
                                break;
                            }
                        }

                        state.Altitude = MathF.Max(0f, state.Altitude - profile.DescentRate * dt);
                        state.PitchRad = Lerp(state.PitchRad, 0f, dt * 5f);
                        state.RollRad = Lerp(state.RollRad, 0f, dt * 5f);

                        if (state.Altitude <= AltitudeTolerance)
                        {
                            state.Altitude = 0f;
                            state.Velocity = Vector2.Zero;
                            state.PitchRad = 0f;
                            state.RollRad = 0f;
                            state.Phase = FlightPhase.Ground;
                            state.LandOnArrival = false;
                            TotalLandings++;
                        }
                        world.SetFlightState(unit.Id, in state);
                        world.UpdateUnitNavigation(unit.Id, nextPos, nextPos, state.Phase == FlightPhase.Landing, tick, supportSurface: 0, hullHeading: heading);
                        SyncAttachedSlingLoad(world, unit.Id, state.SlingLoadId, nextPos, heading, state.Altitude, in profile, tick);
                        break;
                    }

                case FlightPhase.Crashing:
                    {
                        state.CrashTicks++;
                        state.Velocity *= 0.985f;
                        Vector2 nextPos = ClampToMap(world.Map, pos + state.Velocity * dt);
                        heading = CombatSystem.NormalizeYaw(heading + state.CrashSpinVelocity * dt);

                        // Accelerating spin-down descent
                        float descentSpeed = profile.CrashDescentRate * (1f + MathF.Min(1.2f, state.CrashTicks * 0.03f));
                        state.Altitude = MathF.Max(0f, state.Altitude - descentSpeed * dt);
                        state.PitchRad = Lerp(state.PitchRad, -0.45f, dt * 3.5f);
                        float spinSign = state.CrashSpinVelocity >= 0f ? 1f : -1f;
                        state.RollRad = Lerp(state.RollRad, spinSign * 0.62f, dt * 3.5f);

                        world.SetFlightState(unit.Id, in state);
                        world.UpdateUnitNavigation(unit.Id, nextPos, nextPos, true, tick, supportSurface: 0, hullHeading: heading);
                        SyncAttachedSlingLoad(world, unit.Id, state.SlingLoadId, nextPos, heading, state.Altitude, in profile, tick);

                        if (state.Altitude <= 0f && impactCount < _maxUnits)
                        {
                            _impactIds[impactCount] = unit.Id;
                            _impactProfiles[impactCount] = profile;
                            _impactStates[impactCount] = state;
                            _impactPositions[impactCount] = nextPos;
                            _impactHeadings[impactCount] = heading;
                            _impactDefinitions[impactCount] = unit.Definition;
                            _impactOwners[impactCount] = unit.Owner;
                            impactCount++;
                        }
                        break;
                    }
            }
        }

        // 3. Resolve ground/water crash impacts after loop so entity removal never invalidates iteration
        for (int i = 0; i < impactCount; i++)
        {
            TotalCrashImpacts++;
            EntityId unitId = _impactIds[i];
            if (OnCrashImpact is not null)
            {
                OnCrashImpact(
                    world,
                    tick,
                    unitId,
                    _impactDefinitions[i],
                    _impactOwners[i],
                    _impactPositions[i],
                    _impactHeadings[i],
                    in _impactProfiles[i],
                    in _impactStates[i]);
            }
            else
            {
                world.AddWreck(_impactDefinitions[i], _impactPositions[i], 0, _impactHeadings[i], _impactHeadings[i], tick, _impactOwners[i]);
                world.RemoveUnit(unitId);
            }
        }
    }

    /// <summary>
    /// Deterministic, zero-allocation evaluation of whether a position can safely accommodate a helicopter landing.
    /// Shared by FlightSystem (authoritative) and Godot UI (LZ preview cursor).
    /// </summary>
    public static LandingZoneStatus EvaluateLandingZone(
        StaticGeometry geometry,
        WorldState? world,
        Vector2 position,
        float clearanceRadius,
        float maxSlopeDegrees,
        EntityId ignoreUnitId = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        HeightMap map = geometry.HeightMap;
        float margin = MathF.Max(1.0f, clearanceRadius);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            position.X < margin || position.Y < margin ||
            position.X > map.Width - margin || position.Y > map.Depth - margin)
        {
            return LandingZoneStatus.OutOfBounds;
        }

        // 1. Check water and terrain obstacles
        IReadOnlyList<TerrainObstacle> obstacles = geometry.TerrainObstacles;
        for (int i = 0; i < obstacles.Count; i++)
        {
            TerrainObstacle obs = obstacles[i];
            if (CircleIntersectsAabb(position, clearanceRadius, obs.MinX, obs.MinZ, obs.MaxX, obs.MaxZ))
            {
                if ((obs.RequiredCapability & TraversabilityCapabilities.Water) != 0)
                    return LandingZoneStatus.BlockedByWater;
                if ((obs.RequiredCapability & TraversabilityCapabilities.DenseForest) != 0)
                    return LandingZoneStatus.BlockedByForest;
            }
        }

        // 2. Check opaque buildings/structures
        IReadOnlyList<OpaqueStructure> structures = geometry.Structures;
        for (int i = 0; i < structures.Count; i++)
        {
            OpaqueStructure s = structures[i];
            if (CircleIntersectsAabb(position, clearanceRadius, s.MinX, s.MinZ, s.MaxX, s.MaxZ))
                return LandingZoneStatus.BlockedByStructure;
        }

        // 3. Check dense vegetation zones (respecting clearings)
        IReadOnlyList<VegetationZone> vegZones = geometry.VegetationZones;
        IReadOnlyList<VegetationClearing> clearings = geometry.VegetationClearings;
        for (int i = 0; i < vegZones.Count; i++)
        {
            VegetationZone vz = vegZones[i];
            if (!CircleIntersectsAabb(position, clearanceRadius, vz.MinX, vz.MinZ, vz.MaxX, vz.MaxZ))
                continue;
            if (!IsPointInsideClearing(position, clearings))
                return LandingZoneStatus.BlockedByForest;
        }

        // 4. Check terrain slope across 8 perimeter samples around the landing footprint
        float centerH = map.GetHeight(position.X, position.Y);
        float sampleR = MathF.Max(1.0f, clearanceRadius * 0.85f);
        float maxTan = MathF.Tan(maxSlopeDegrees * (MathF.PI / 180f));
        const float Diag = 0.70710678f;
        ReadOnlySpan<Vector2> offsets =
        [
            new(sampleR, 0f), new(-sampleR, 0f), new(0f, sampleR), new(0f, -sampleR),
            new(sampleR * Diag, sampleR * Diag), new(-sampleR * Diag, sampleR * Diag),
            new(sampleR * Diag, -sampleR * Diag), new(-sampleR * Diag, -sampleR * Diag)
        ];
        for (int i = 0; i < offsets.Length; i++)
        {
            Vector2 sp = position + offsets[i];
            float h = map.GetHeight(sp.X, sp.Y);
            if (MathF.Abs(h - centerH) > sampleR * maxTan)
                return LandingZoneStatus.SlopeTooSteep;
        }

        if (world is not null)
        {
            // 5. Check wrecks
            float wreckMinDist = clearanceRadius + 2.5f;
            float wreckMinDistSq = wreckMinDist * wreckMinDist;
            ReadOnlySpan<WreckState> wrecks = world.Wrecks;
            for (int i = 0; i < wrecks.Length; i++)
            {
                if (Vector2.DistanceSquared(position, wrecks[i].Position) < wreckMinDistSq)
                    return LandingZoneStatus.BlockedByWreck;
            }

            // 6. Check ground/landed units and other helicopters actively descending to land
            float unitMinDist = clearanceRadius + 2.0f;
            float unitMinDistSq = unitMinDist * unitMinDist;
            EntityId ownSlingLoad = ignoreUnitId != default ? world.GetSlingLoad(ignoreUnitId) : default;
            for (int i = 0; i < world.Count; i++)
            {
                UnitState other = world.GetUnitAt(i);
                if (other.Id == ignoreUnitId || other.Id == ownSlingLoad || world.IsContained(other.Id) || world.IsSlung(other.Id))
                    continue;
                if (world.TryGetFlightState(other.Id, out WorldState.UnitFlightState otherFs) &&
                    otherFs.HasCapability &&
                    otherFs.Phase is FlightPhase.TakingOff or FlightPhase.Airborne)
                {
                    continue;
                }
                if (Vector2.DistanceSquared(position, other.Position) < unitMinDistSq)
                    return LandingZoneStatus.BlockedByUnit;
            }
        }

        return LandingZoneStatus.Valid;
    }

    /// <summary>
    /// Deterministic, zero-allocation evaluation of whether infantry can fast-rope at a position.
    /// Dense forests and steep hills are allowed; water and solid structures are blocked.
    /// Shared by FlightSystem (authoritative) and Godot UI (fast-rope preview cursor).
    /// </summary>
    public static FastRopeZoneStatus EvaluateFastRopeZone(StaticGeometry geometry, Vector2 position)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        HeightMap map = geometry.HeightMap;
        const float margin = 2.0f;
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            position.X < margin || position.Y < margin ||
            position.X > map.Width - margin || position.Y > map.Depth - margin)
        {
            return FastRopeZoneStatus.OutOfBounds;
        }

        const float checkRadius = 1.2f;
        IReadOnlyList<TerrainObstacle> obstacles = geometry.TerrainObstacles;
        for (int i = 0; i < obstacles.Count; i++)
        {
            TerrainObstacle obs = obstacles[i];
            if ((obs.RequiredCapability & TraversabilityCapabilities.Water) != 0 &&
                CircleIntersectsAabb(position, checkRadius, obs.MinX, obs.MinZ, obs.MaxX, obs.MaxZ))
            {
                return FastRopeZoneStatus.BlockedByWater;
            }
        }

        IReadOnlyList<OpaqueStructure> structures = geometry.Structures;
        for (int i = 0; i < structures.Count; i++)
        {
            OpaqueStructure s = structures[i];
            if (CircleIntersectsAabb(position, checkRadius, s.MinX, s.MinZ, s.MaxX, s.MaxZ))
                return FastRopeZoneStatus.BlockedByStructure;
        }

        return FastRopeZoneStatus.Valid;
    }

    /// <summary>
    /// Deterministic, zero-allocation evaluation of whether a slung vehicle can be released at a position.
    /// Requires clearance for the vehicle footprint (much tighter than full rotor LZ clearance).
    /// Shared by FlightSystem (authoritative) and Godot UI (sling-drop preview cursor).
    /// </summary>
    public static SlingDropZoneStatus EvaluateSlingDropZone(
        StaticGeometry geometry,
        WorldState? world,
        Vector2 position,
        float vehicleRadius = 3.0f,
        EntityId ignoreHeliId = default,
        EntityId ignoreLoadId = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        HeightMap map = geometry.HeightMap;
        float clearance = MathF.Max(1.5f, vehicleRadius);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
            position.X < clearance || position.Y < clearance ||
            position.X > map.Width - clearance || position.Y > map.Depth - clearance)
        {
            return SlingDropZoneStatus.OutOfBounds;
        }

        IReadOnlyList<TerrainObstacle> obstacles = geometry.TerrainObstacles;
        for (int i = 0; i < obstacles.Count; i++)
        {
            TerrainObstacle obs = obstacles[i];
            if (CircleIntersectsAabb(position, clearance, obs.MinX, obs.MinZ, obs.MaxX, obs.MaxZ))
            {
                if ((obs.RequiredCapability & TraversabilityCapabilities.Water) != 0)
                    return SlingDropZoneStatus.BlockedByWater;
                if ((obs.RequiredCapability & TraversabilityCapabilities.DenseForest) != 0)
                    return SlingDropZoneStatus.BlockedByForest;
            }
        }

        IReadOnlyList<OpaqueStructure> structures = geometry.Structures;
        for (int i = 0; i < structures.Count; i++)
        {
            OpaqueStructure s = structures[i];
            if (CircleIntersectsAabb(position, clearance, s.MinX, s.MinZ, s.MaxX, s.MaxZ))
                return SlingDropZoneStatus.BlockedByStructure;
        }

        IReadOnlyList<VegetationZone> vegZones = geometry.VegetationZones;
        IReadOnlyList<VegetationClearing> clearings = geometry.VegetationClearings;
        for (int i = 0; i < vegZones.Count; i++)
        {
            VegetationZone vz = vegZones[i];
            if (!CircleIntersectsAabb(position, clearance, vz.MinX, vz.MinZ, vz.MaxX, vz.MaxZ))
                continue;
            if (!IsPointInsideClearing(position, clearings))
                return SlingDropZoneStatus.BlockedByForest;
        }

        if (world is not null)
        {
            float wreckMinDist = clearance + 2.0f;
            float wreckMinDistSq = wreckMinDist * wreckMinDist;
            ReadOnlySpan<WreckState> wrecks = world.Wrecks;
            for (int i = 0; i < wrecks.Length; i++)
            {
                if (Vector2.DistanceSquared(position, wrecks[i].Position) < wreckMinDistSq)
                    return SlingDropZoneStatus.BlockedByWreck;
            }

            float unitMinDist = clearance + 1.5f;
            float unitMinDistSq = unitMinDist * unitMinDist;
            for (int i = 0; i < world.Count; i++)
            {
                UnitState other = world.GetUnitAt(i);
                if (other.Id == ignoreHeliId || other.Id == ignoreLoadId ||
                    world.IsContained(other.Id) || world.IsAirborne(other.Id) || world.IsSlung(other.Id))
                {
                    continue;
                }
                if (Vector2.DistanceSquared(position, other.Position) < unitMinDistSq)
                    return SlingDropZoneStatus.BlockedByUnit;
            }
        }

        return SlingDropZoneStatus.Valid;
    }

    private static float ResolveTargetAltitude(in WorldState.UnitFlightState state, in FlightProfile profile, Vector2 currentPos)
    {
        const float approachDescentDistSq = 180f * 180f;
        if (state.FastRopeActive ||
            (state.FastRopePending && (!state.HasMoveTarget || Vector2.DistanceSquared(currentPos, state.TargetPosition) <= approachDescentDistSq)))
        {
            return profile.FastRopeAltitude;
        }
        if (state.PendingSlingTargetId != default &&
            (!state.HasMoveTarget || Vector2.DistanceSquared(currentPos, state.TargetPosition) <= approachDescentDistSq))
        {
            return profile.LowAltitude;
        }
        if (state.SlingDropPending &&
            (!state.HasMoveTarget || Vector2.DistanceSquared(currentPos, state.TargetPosition) <= approachDescentDistSq))
        {
            return MathF.Min(profile.LowAltitude, profile.SlingCableLength + 1.0f);
        }
        return profile.GetTargetAltitude(state.DesiredBand);
    }

    private void AttachSlingLoad(
        WorldState world,
        EntityId heliId,
        EntityId loadId,
        Vector2 heliPos,
        float heliHeading,
        float heliAltitude,
        in FlightProfile profile,
        int tick,
        ref WorldState.UnitFlightState heliState)
    {
        heliState.SlingLoadId = loadId;
        heliState.PendingSlingTargetId = default;
        heliState.SlingActionTicks = 0;

        world.ClearAttackOrder(loadId);
        world.ClearBoardingOrder(loadId);
        world.ClearPendingDisembark(loadId);

        if (world.TryGetFlightState(loadId, out WorldState.UnitFlightState loadFs))
        {
            loadFs.SlungById = heliId;
            loadFs.SlungAltitude = MathF.Max(0f, heliAltitude - profile.SlingCableLength);
            world.SetFlightState(loadId, in loadFs);
        }
        world.UpdateUnitNavigation(loadId, heliPos, heliPos, false, tick, supportSurface: 0, hullHeading: heliHeading);
        TotalSlingPickups++;
    }

    private void DetachSlingLoad(
        WorldState world,
        Vector2 dropPos,
        float heading,
        int tick,
        ref WorldState.UnitFlightState heliState)
    {
        EntityId loadId = heliState.SlingLoadId;
        heliState.SlingLoadId = default;
        heliState.SlingDropPending = false;
        heliState.SlingActionTicks = 0;

        if (loadId != default && world.TryGetFlightState(loadId, out WorldState.UnitFlightState loadFs))
        {
            loadFs.SlungById = default;
            loadFs.SlungAltitude = 0f;
            world.SetFlightState(loadId, in loadFs);
            world.UpdateUnitNavigation(loadId, dropPos, dropPos, false, tick, supportSurface: 0, hullHeading: heading);
            TotalSlingDrops++;
        }
    }

    private static void SyncAttachedSlingLoad(
        WorldState world,
        EntityId heliId,
        EntityId loadId,
        Vector2 heliPos,
        float heliHeading,
        float heliAltitude,
        in FlightProfile profile,
        int tick)
    {
        if (loadId == default || !world.TryGetFlightState(loadId, out WorldState.UnitFlightState loadFs))
            return;
        if (loadFs.SlungById != heliId)
            return;

        loadFs.SlungAltitude = MathF.Max(0f, heliAltitude - profile.SlingCableLength);
        world.SetFlightState(loadId, in loadFs);
        world.UpdateUnitNavigation(loadId, heliPos, heliPos, false, tick, supportSurface: 0, hullHeading: heliHeading);
    }

    private bool TryDeployNextFastRopeSoldier(WorldState world, EntityId heliId, Vector2 hoverPos)
    {
        ReadOnlySpan<EntityId> passengers = world.GetPassengers(heliId);
        EntityId infantryId = default;
        for (int i = 0; i < passengers.Length; i++)
        {
            if (world.TryGetUnit(passengers[i], out UnitState p) && IsInfantryDefinition(p.Definition))
            {
                infantryId = passengers[i];
                break;
            }
        }
        if (infantryId == default)
            return false;

        if (!PassengerDistribution.TryFindFastRopeSpot(world, _destinationEvaluator, hoverPos, 0, infantryId, out Vector2 spot))
            return false;

        return world.Disembark(heliId, infantryId, spot, supportSurface: 0);
    }

    private static void ClearIncomingBoardingOrders(WorldState world, EntityId transportId)
    {
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            if (world.TryGetBoardingTarget(u.Id, out EntityId target) && target == transportId)
            {
                world.ClearBoardingOrder(u.Id);
            }
        }
    }

    private static bool CircleIntersectsAabb(Vector2 center, float radius, float minX, float minZ, float maxX, float maxZ)
    {
        float cx = Math.Clamp(center.X, minX, maxX);
        float cz = Math.Clamp(center.Y, minZ, maxZ);
        float dx = center.X - cx;
        float dz = center.Y - cz;
        return dx * dx + dz * dz < radius * radius;
    }

    private static bool IsPointInsideClearing(Vector2 point, IReadOnlyList<VegetationClearing> clearings)
    {
        for (int i = 0; i < clearings.Count; i++)
        {
            VegetationClearing c = clearings[i];
            if (point.X >= c.MinX && point.X <= c.MaxX && point.Y >= c.MinZ && point.Y <= c.MaxZ)
                return true;
        }
        return false;
    }

    private static Vector2 ClampToMap(HeightMap map, Vector2 pos)
    {
        const float edgeMargin = 1.0f;
        float x = Math.Clamp(pos.X, edgeMargin, MathF.Max(edgeMargin, map.Width - edgeMargin));
        float y = Math.Clamp(pos.Y, edgeMargin, MathF.Max(edgeMargin, map.Depth - edgeMargin));
        return new Vector2(x, y);
    }

    private static Vector2 MoveToward(Vector2 current, Vector2 target, float maxDelta)
    {
        Vector2 delta = target - current;
        float lenSq = delta.LengthSquared();
        if (lenSq <= maxDelta * maxDelta || lenSq < 1e-8f)
            return target;
        return current + delta * (maxDelta / MathF.Sqrt(lenSq));
    }

    private static float StepHeading(float current, float target, float maxStepRad)
    {
        float delta = CombatSystem.ShorterArcDelta(current, target);
        if (MathF.Abs(delta) <= maxStepRad)
            return CombatSystem.NormalizeYaw(target);
        return CombatSystem.NormalizeYaw(current + MathF.Sign(delta) * maxStepRad);
    }

    private static float Lerp(float a, float b, float t) =>
        a + (b - a) * Math.Clamp(t, 0f, 1f);
}
