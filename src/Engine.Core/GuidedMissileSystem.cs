using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Authoritative deterministic simulation system for guided surface-to-air missiles
/// (3D proportional-navigation homing, proximity-fuze detonation, and physical IR flare spoofing)
/// and aircraft flare countermeasures (staggered M130 bursts, manual deployment, and missile-warning auto-release).
/// Zero steady-state allocations per tick.
/// </summary>
public sealed class GuidedMissileSystem : ISimulationSystem, IRandomStreamsAware
{
    public const float AutoFlareWarningRange = 1400f;
    public const float AutoFlareTriggerRange = 225f;
    public const float IrDecoyEvaluationRange = 190f;
    public const int MaxActiveFlares = 128;

    private struct ActiveFlareDecoy
    {
        public uint FlareId;
        public EntityId OwnerUnitId;
        public Vector3 Position;
        public Vector3 Velocity;
        public int DelayTicksRemaining;
        public int BurnTicksRemaining;
        public bool Active;
    }

    private readonly StaticGeometry _geometry;
    private readonly IUnitCountermeasureCatalog? _cmCatalog;
    private readonly IUnitCombatCatalog? _combatCatalog;
    private readonly ActiveFlareDecoy[] _activeFlares = new ActiveFlareDecoy[MaxActiveFlares];
    private uint _nextFlareId = 1;
    private DeterministicRandom? _rng;

    public int TotalFlaresDeployed { get; private set; }
    public int TotalMissilesSpoofed { get; private set; }
    public int TotalMissilesHit { get; private set; }
    public int TotalMissilesSelfDestructed { get; private set; }

    public int ActiveBurningFlareCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < MaxActiveFlares; i++)
            {
                if (_activeFlares[i].Active && _activeFlares[i].DelayTicksRemaining <= 0 && _activeFlares[i].BurnTicksRemaining > 0)
                    count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Callback invoked when an aircraft deploys a flare salvo burst:
    /// (world, tick, unitId, definition, origin3D, encodedVelXHeadingVelZ).
    /// </summary>
    public Action<WorldState, int, EntityId, ushort, Vector3, Vector3>? OnFlaresDeployed { get; set; }

    /// <summary>
    /// Callback invoked when a guided missile detonates (either proximity hit or harmless airburst/self-destruct):
    /// (world, tick, missile, hitTarget).
    /// </summary>
    public Action<WorldState, int, GuidedMissileState, bool>? OnMissileDetonated { get; set; }

    public GuidedMissileSystem(
        StaticGeometry geometry,
        IUnitCountermeasureCatalog? cmCatalog = null,
        IUnitCombatCatalog? combatCatalog = null)
    {
        _geometry = geometry ?? throw new ArgumentNullException(nameof(geometry));
        _cmCatalog = cmCatalog;
        _combatCatalog = combatCatalog;
    }

    public void InitializeRandomStreams(IRandomStreamProvider provider)
    {
        _rng = provider.GetStream(RandomDomain.Artillery);
    }

    private DeterministicRandom Rng => _rng ??= new DeterministicRandom(9035UL);

    /// <summary>
    /// Computes effective IR flare decoy probability taking into account flare base effectiveness,
    /// missile seeker countermeasure resistance, helicopter airspeed, and cross-track beaming angle.
    /// Hovering helicopters are significantly harder to decoy than fast maneuvering helicopters.
    /// </summary>
    public static float ComputeEffectiveSpoofProbability(
        float baseSpoofProbability,
        float missileIrResistance,
        Vector2 aircraftVelocity2D,
        Vector2 missileVelocity2D)
    {
        if (baseSpoofProbability <= 0f)
            return 0f;
        if (baseSpoofProbability >= 1f && missileIrResistance <= 0f)
            return 1f;

        float baseProb = Math.Clamp(baseSpoofProbability * (1f - Math.Clamp(missileIrResistance, 0f, 0.9f)), 0f, 1f);
        float speed = aircraftVelocity2D.Length();

        // Hovering (< 4 m/s): helicopter stays close to its own flare cloud -> 0.72x multiplier (~42% for CH-47D vs 9M37M)
        // Cruising/evading (4..55 m/s): aircraft physically separates from hot flares -> up to 1.14x
        float speedT = Math.Clamp((speed - 4f) / 46f, 0f, 1f);
        float motionMult = 0.72f + 0.42f * speedT;

        // Cross-track (beaming) maneuver bonus: moving perpendicular to incoming SAM maximizes angular separation
        if (speed >= 6f && missileVelocity2D.LengthSquared() > 1f)
        {
            Vector2 heliDir = aircraftVelocity2D / speed;
            Vector2 missileDir = Vector2.Normalize(missileVelocity2D);
            float cross = MathF.Abs(heliDir.X * missileDir.Y - heliDir.Y * missileDir.X);
            motionMult += 0.14f * cross * speedT;
        }

        return Math.Clamp(baseProb * motionMult, 0.08f, 0.88f);
    }

    public void EnsureUnitsInitialized(WorldState world)
    {
        if (_cmCatalog is null)
            return;

        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            if (!world.TryGetCountermeasureState(u.Id, out WorldState.UnitCountermeasureState cm) || cm.Initialized)
                continue;

            if (_cmCatalog.TryGetFlareProfile(u.Definition, out FlareProfile profile) && profile.IsValid)
            {
                cm = new WorldState.UnitCountermeasureState(
                    Initialized: true,
                    HasFlares: true,
                    ChargesRemaining: profile.Charges,
                    CooldownTicksRemaining: 0,
                    ActiveTicksRemaining: 0,
                    AutoFlaresEnabled: profile.DefaultAutoFlares,
                    MissileWarningActive: false,
                    LastFlareBurstTick: -10000);
            }
            else
            {
                cm = new WorldState.UnitCountermeasureState(
                    Initialized: true,
                    HasFlares: false,
                    ChargesRemaining: 0,
                    CooldownTicksRemaining: 0,
                    ActiveTicksRemaining: 0,
                    AutoFlaresEnabled: false,
                    MissileWarningActive: false,
                    LastFlareBurstTick: -10000);
            }
            world.SetCountermeasureState(u.Id, cm);
        }
    }

    public bool CanDeployFlares(int player, DeployFlaresCommand cmd, WorldState world)
    {
        EnsureUnitsInitialized(world);
        if (!world.TryGetUnit(cmd.Unit, out UnitState unit) || unit.Owner != player ||
            world.IsContained(cmd.Unit) || world.IsCrashing(cmd.Unit) || world.IsSlung(cmd.Unit))
            return false;

        if (!world.TryGetCountermeasureState(cmd.Unit, out WorldState.UnitCountermeasureState cm) || !cm.HasFlares)
            return false;

        if (cmd.ToggleAuto)
            return true;

        return world.IsAirborne(cmd.Unit) && cm.ChargesRemaining > 0 && cm.CooldownTicksRemaining <= 0;
    }

    public bool TryExecuteDeployFlares(WorldState world, int tick, DeployFlaresCommand cmd)
    {
        EnsureUnitsInitialized(world);
        if (!world.TryGetUnit(cmd.Unit, out UnitState unit) || world.IsCrashing(cmd.Unit))
            return false;
        if (!world.TryGetCountermeasureState(cmd.Unit, out WorldState.UnitCountermeasureState cm) || !cm.HasFlares)
            return false;

        if (cmd.ToggleAuto)
        {
            cm.AutoFlaresEnabled = !cm.AutoFlaresEnabled;
            world.SetCountermeasureState(cmd.Unit, cm);
            return true;
        }

        if (!world.IsAirborne(cmd.Unit))
            return false;
        if (cm.ChargesRemaining <= 0 || cm.CooldownTicksRemaining > 0)
            return false;

        if (_cmCatalog is null || !_cmCatalog.TryGetFlareProfile(unit.Definition, out FlareProfile profile) || !profile.IsValid)
            return false;

        TriggerFlareBurst(world, tick, unit, ref cm, profile);
        world.SetCountermeasureState(cmd.Unit, cm);
        return true;
    }

    public void Update(WorldState world, int tick)
    {
        EnsureUnitsInitialized(world);
        const float dt = Simulation.TickSeconds;

        // 0. Step physical falling flare decoys (shared FlareBallistics math, 0 allocations)
        UpdateActiveFlares(dt);

        // 1. Decay flare cooldowns/active windows and clear previous tick's missile warnings
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            if (!world.TryGetCountermeasureState(u.Id, out WorldState.UnitCountermeasureState cm) || !cm.HasFlares)
                continue;

            bool changed = false;
            if (cm.CooldownTicksRemaining > 0)
            {
                cm.CooldownTicksRemaining--;
                changed = true;
            }
            if (cm.ActiveTicksRemaining > 0)
            {
                cm.ActiveTicksRemaining--;
                changed = true;
            }
            if (cm.MissileWarningActive)
            {
                cm.MissileWarningActive = false;
                changed = true;
            }
            if (changed)
            {
                world.SetCountermeasureState(u.Id, cm);
            }
        }

        // 2. Scan active guided missiles for missile warning flags and close-in reactive auto-flares
        for (int mIdx = 0; mIdx < world.GuidedMissileCount; mIdx++)
        {
            ref readonly GuidedMissileState missile = ref world.GetGuidedMissileRef(mIdx);
            if (missile.Decoyed || missile.TargetId.Value == 0)
                continue;
            if (!world.TryGetUnit(missile.TargetId, out UnitState targetUnit) || world.IsCrashing(targetUnit.Id))
                continue;
            if (!world.TryGetCountermeasureState(targetUnit.Id, out WorldState.UnitCountermeasureState cm) || !cm.HasFlares)
                continue;

            Vector3 targetPos = GetUnitCenter3D(world, targetUnit);
            float distSq = Vector3.DistanceSquared(missile.Position, targetPos);
            if (distSq > AutoFlareWarningRange * AutoFlareWarningRange)
                continue;

            cm.MissileWarningActive = true;

            if (distSq <= AutoFlareTriggerRange * AutoFlareTriggerRange &&
                missile.Guidance == MissileGuidanceKind.Infrared &&
                cm.AutoFlaresEnabled &&
                cm.ChargesRemaining > 0 &&
                cm.CooldownTicksRemaining == 0 &&
                cm.ActiveTicksRemaining == 0 &&
                world.IsAirborne(targetUnit.Id) &&
                _cmCatalog is not null &&
                _cmCatalog.TryGetFlareProfile(targetUnit.Definition, out FlareProfile profile) &&
                profile.IsValid)
            {
                TriggerFlareBurst(world, tick, targetUnit, ref cm, profile);
            }

            world.SetCountermeasureState(targetUnit.Id, cm);
        }

        // 3. Advance guided missiles: physical IR flare lock-on, 3D proportional guidance, and proximity detonation
        for (int mIdx = world.GuidedMissileCount - 1; mIdx >= 0; mIdx--)
        {
            ref GuidedMissileState missile = ref world.GetGuidedMissileRef(mIdx);
            Vector3 prevPos = missile.Position;
            int ageTicks = tick - missile.LaunchedTick;

            if (ageTicks >= missile.MaxFlightTicks)
            {
                GuidedMissileState expired = missile;
                world.RemoveGuidedMissileAt(mIdx);
                TotalMissilesSelfDestructed++;
                OnMissileDetonated?.Invoke(world, tick, expired, false);
                continue;
            }

            UnitState targetUnit = default;
            bool hasLiveTarget = !missile.Decoyed &&
                                 missile.TargetId.Value != 0 &&
                                 world.TryGetUnit(missile.TargetId, out targetUnit) &&
                                 !world.IsCrashing(targetUnit.Id);

            if (hasLiveTarget)
            {
                Vector3 targetPos = GetUnitCenter3D(world, targetUnit);
                float distToTarget = Vector3.Distance(missile.Position, targetPos);

                // Deterministic IR flare decoy roll once the missile enters seeker discrimination range
                // and at least one real flare cartridge from the target aircraft is burning outside the fuselage.
                if (missile.Guidance == MissileGuidanceKind.Infrared &&
                    distToTarget <= IrDecoyEvaluationRange &&
                    world.TryGetCountermeasureState(targetUnit.Id, out WorldState.UnitCountermeasureState targetCm) &&
                    targetCm.HasFlares &&
                    targetCm.ActiveTicksRemaining > 0 &&
                    missile.FlareEvaluatedBurstTick != targetCm.LastFlareBurstTick &&
                    _cmCatalog is not null &&
                    _cmCatalog.TryGetFlareProfile(targetUnit.Definition, out FlareProfile flareProfile) &&
                    TrySelectBestDecoyFlare(targetUnit.Id, targetPos, missile.Position, out int selectedFlareSlot))
                {
                    missile.FlareEvaluatedBurstTick = targetCm.LastFlareBurstTick;

                    Vector2 heliVel2D = Vector2.Zero;
                    if (world.TryGetFlightState(targetUnit.Id, out WorldState.UnitFlightState heliFs))
                    {
                        heliVel2D = heliFs.Velocity;
                    }
                    Vector2 missileVel2D = new(missile.Velocity.X, missile.Velocity.Z);
                    float effectiveSpoof = ComputeEffectiveSpoofProbability(
                        flareProfile.IrSpoofProbability,
                        missile.IrCountermeasureResistance,
                        heliVel2D,
                        missileVel2D);

                    double roll = Rng.NextDouble();
                    if (roll < effectiveSpoof)
                    {
                        missile.Decoyed = true;
                        TotalMissilesSpoofed++;
                        ref readonly ActiveFlareDecoy chosenFlare = ref _activeFlares[selectedFlareSlot];
                        missile.DecoyFlareId = chosenFlare.FlareId;
                        missile.DecoyTargetPos = chosenFlare.Position;
                        // Spoofed SAM chases the falling flare, overshoots past the tiny pellet (no proximity trigger
                        // on a 3cm flare), and flies away unguided until self-destruct or terrain impact.
                        int maxAfterDecoy = ageTicks + 46;
                        if (missile.MaxFlightTicks > maxAfterDecoy)
                            missile.MaxFlightTicks = maxAfterDecoy;
                    }
                }

                if (!missile.Decoyed)
                {
                    Vector3 targetVel3D = Vector3.Zero;
                    if (world.TryGetFlightState(targetUnit.Id, out WorldState.UnitFlightState fs))
                    {
                        targetVel3D = new Vector3(fs.Velocity.X, 0f, fs.Velocity.Y);
                    }
                    float timeToIntercept = Math.Clamp(distToTarget / MathF.Max(10f, missile.Speed), 0f, 1.8f);
                    Vector3 aimPoint = targetPos + targetVel3D * timeToIntercept;
                    SteerToward(ref missile, aimPoint, dt, turnRateMultiplier: 1.0f);
                }
                else if (TryFindActiveFlareById(missile.DecoyFlareId, out int lockedSlot))
                {
                    // Immediately break hard toward the real falling magnesium flare!
                    missile.DecoyTargetPos = _activeFlares[lockedSlot].Position;
                    SteerToward(ref missile, missile.DecoyTargetPos, dt, turnRateMultiplier: 2.8f);
                }
            }
            else if (missile.Decoyed)
            {
                if (missile.DecoyFlareId != 0 && TryFindActiveFlareById(missile.DecoyFlareId, out int lockedSlot))
                {
                    // Track the actual falling flare while it remains ahead of the missile
                    ref readonly ActiveFlareDecoy flare = ref _activeFlares[lockedSlot];
                    missile.DecoyTargetPos = flare.Position;
                    Vector3 toFlare = flare.Position - missile.Position;
                    if (Vector3.Dot(toFlare, missile.Velocity) > 0f && toFlare.LengthSquared() > 14f * 14f)
                    {
                        SteerToward(ref missile, flare.Position, dt, turnRateMultiplier: 2.8f);
                    }
                    else
                    {
                        // Missile has streaked past the tiny flare: proximity fuze does not trigger on a flare pellet;
                        // seeker loses lock and missile flies off unguided until self-destruct or ground impact.
                        missile.DecoyFlareId = 0;
                        int coastLimit = ageTicks + 28;
                        if (missile.MaxFlightTicks > coastLimit)
                            missile.MaxFlightTicks = coastLimit;
                    }
                }
                else
                {
                    // Flare burned out or passed: missile coasts unguided away from the helicopter until self-destruct
                    missile.DecoyFlareId = 0;
                    int coastLimit = ageTicks + 28;
                    if (missile.MaxFlightTicks > coastLimit)
                        missile.MaxFlightTicks = coastLimit;
                }
            }
            else if (missile.TargetId.Value != 0)
            {
                // Target died or crashed mid-flight: self-destruct cleanly after a short coast
                int shortLimit = ageTicks + 10;
                if (missile.MaxFlightTicks > shortLimit)
                    missile.MaxFlightTicks = shortLimit;
            }

            Vector3 nextPos = prevPos + missile.Velocity * dt;
            missile.Position = nextPos;

            // Check proximity-fuze hit against live target (only if NOT decoyed)
            if (hasLiveTarget && !missile.Decoyed && world.TryGetUnit(missile.TargetId, out UnitState liveTarget))
            {
                Vector3 targetCenter = GetUnitCenter3D(world, liveTarget);
                float targetRadius = 4.0f;
                if (_combatCatalog is not null && _combatCatalog.TryGetProfile(liveTarget.Definition, out UnitCombatProfile tp))
                {
                    targetRadius = MathF.Max(1.0f, tp.Radius);
                }
                float triggerRadius = missile.ProximityFuzeRadius + targetRadius;
                float segDistSq = DistancePointToSegmentSquared(targetCenter, prevPos, nextPos);
                if (segDistSq <= triggerRadius * triggerRadius)
                {
                    missile.Position = targetCenter;
                    GuidedMissileState impact = missile;
                    world.RemoveGuidedMissileAt(mIdx);
                    TotalMissilesHit++;
                    OnMissileDetonated?.Invoke(world, tick, impact, true);
                    continue;
                }
            }

            // Terrain / out-of-bounds impact check
            Vector2 next2D = new(nextPos.X, nextPos.Z);
            if (!_geometry.HeightMap.Contains(next2D) || nextPos.Y <= _geometry.HeightMap.GetHeight(next2D.X, next2D.Y))
            {
                if (_geometry.HeightMap.Contains(next2D))
                {
                    missile.Position = new Vector3(nextPos.X, _geometry.HeightMap.GetHeight(next2D.X, next2D.Y), nextPos.Z);
                }
                GuidedMissileState terrainHit = missile;
                world.RemoveGuidedMissileAt(mIdx);
                TotalMissilesSelfDestructed++;
                OnMissileDetonated?.Invoke(world, tick, terrainHit, false);
            }
        }
    }

    private void UpdateActiveFlares(float dt)
    {
        for (int i = 0; i < MaxActiveFlares; i++)
        {
            ref ActiveFlareDecoy flare = ref _activeFlares[i];
            if (!flare.Active) continue;

            if (flare.DelayTicksRemaining > 0)
            {
                flare.DelayTicksRemaining--;
                continue;
            }

            flare.BurnTicksRemaining--;
            if (flare.BurnTicksRemaining <= 0)
            {
                flare.Active = false;
                continue;
            }

            Vector2 pos2D = new(flare.Position.X, flare.Position.Z);
            float groundY = _geometry.HeightMap.Contains(pos2D)
                ? _geometry.HeightMap.GetHeight(pos2D.X, pos2D.Y)
                : 0f;
            FlareBallistics.StepFlare(ref flare.Position, ref flare.Velocity, dt, groundY);
        }
    }

    private bool TrySelectBestDecoyFlare(EntityId ownerUnitId, Vector3 aircraftPos, Vector3 missilePos, out int bestSlot)
    {
        bestSlot = -1;
        float bestScore = -1f;
        int fallbackSlot = -1;
        float fallbackDistSq = -1f;

        for (int i = 0; i < MaxActiveFlares; i++)
        {
            ref readonly ActiveFlareDecoy f = ref _activeFlares[i];
            if (!f.Active || f.OwnerUnitId != ownerUnitId || f.DelayTicksRemaining > 0 || f.BurnTicksRemaining < 10)
                continue;

            float sepSq = Vector3.DistanceSquared(f.Position, aircraftPos);
            if (sepSq > fallbackDistSq)
            {
                fallbackDistSq = sepSq;
                fallbackSlot = i;
            }

            // Prefer flares that have already separated clearly from the fuselage (>= 6m) and are in front of the missile
            Vector3 toFlare = f.Position - missilePos;
            if (sepSq >= 36f && toFlare.LengthSquared() > 1f)
            {
                float score = MathF.Sqrt(sepSq) + f.BurnTicksRemaining * 0.4f;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSlot = i;
                }
            }
        }

        if (bestSlot < 0)
            bestSlot = fallbackSlot;
        return bestSlot >= 0;
    }

    private bool TryFindActiveFlareById(uint flareId, out int slot)
    {
        if (flareId == 0)
        {
            slot = -1;
            return false;
        }
        for (int i = 0; i < MaxActiveFlares; i++)
        {
            if (_activeFlares[i].Active &&
                _activeFlares[i].FlareId == flareId &&
                _activeFlares[i].DelayTicksRemaining <= 0 &&
                _activeFlares[i].BurnTicksRemaining > 0)
            {
                slot = i;
                return true;
            }
        }
        slot = -1;
        return false;
    }

    private void TriggerFlareBurst(
        WorldState world,
        int tick,
        UnitState unit,
        ref WorldState.UnitCountermeasureState cm,
        in FlareProfile profile)
    {
        cm.ChargesRemaining = Math.Max(0, cm.ChargesRemaining - 1);
        cm.CooldownTicksRemaining = profile.CooldownTicks;
        cm.ActiveTicksRemaining = profile.ActiveDurationTicks;
        cm.LastFlareBurstTick = tick;
        TotalFlaresDeployed++;

        Vector3 origin = GetUnitCenter3D(world, unit);
        float heading = 0f;
        if (world.TryGetCombatState(unit.Id, out WorldState.CombatState cs))
        {
            heading = cs.HullHeading;
        }
        Vector2 aircraftVel2D = Vector2.Zero;
        if (world.TryGetFlightState(unit.Id, out WorldState.UnitFlightState fs))
        {
            aircraftVel2D = fs.Velocity;
        }

        // Spawn all 8 staggered M130 flare cartridges into the authoritative pool using shared FlareBallistics
        for (int f = 0; f < FlareBallistics.FlaresPerBurst; f++)
        {
            int slot = AllocateFlareSlot();
            FlareBallistics.ComputeFlareLaunch(
                origin,
                heading,
                aircraftVel2D,
                f,
                out float delaySec,
                out Vector3 spawnPos,
                out Vector3 initVel,
                out float burnSec);

            uint flareId = _nextFlareId++;
            if (_nextFlareId == 0) _nextFlareId = 1;

            _activeFlares[slot] = new ActiveFlareDecoy
            {
                FlareId = flareId,
                OwnerUnitId = unit.Id,
                Position = spawnPos,
                Velocity = initVel,
                DelayTicksRemaining = (int)MathF.Round(delaySec * Simulation.TickRate),
                BurnTicksRemaining = Math.Max(10, (int)MathF.Round(burnSec * Simulation.TickRate)),
                Active = true
            };
        }

        // Encode (velX, headingRad, velZ) so client reproduces the exact staggered FlareBallistics trajectories
        Vector3 encodedVelAndHeading = new(aircraftVel2D.X, heading, aircraftVel2D.Y);
        OnFlaresDeployed?.Invoke(world, tick, unit.Id, unit.Definition, origin, encodedVelAndHeading);
    }

    private int AllocateFlareSlot()
    {
        int oldestSlot = 0;
        int lowestRemaining = int.MaxValue;
        for (int i = 0; i < MaxActiveFlares; i++)
        {
            if (!_activeFlares[i].Active)
                return i;
            if (_activeFlares[i].BurnTicksRemaining < lowestRemaining)
            {
                lowestRemaining = _activeFlares[i].BurnTicksRemaining;
                oldestSlot = i;
            }
        }
        return oldestSlot;
    }

    private static void SteerToward(ref GuidedMissileState missile, Vector3 targetPoint, float dt, float turnRateMultiplier = 1.0f)
    {
        Vector3 toTarget = targetPoint - missile.Position;
        float distSq = toTarget.LengthSquared();
        if (distSq < 1e-6f)
            return;

        Vector3 desiredDir = toTarget / MathF.Sqrt(distSq);
        float curSpeed = missile.Velocity.Length();
        Vector3 currentDir = curSpeed > 1e-4f ? missile.Velocity / curSpeed : desiredDir;

        float dot = Math.Clamp(Vector3.Dot(currentDir, desiredDir), -1f, 1f);
        float angle = MathF.Acos(dot);
        float maxTurn = MathF.Max(0.2f, missile.MaxTurnRateRadPerSec * turnRateMultiplier) * dt;

        Vector3 newDir;
        if (angle <= maxTurn || angle < 1e-5f)
        {
            newDir = desiredDir;
        }
        else
        {
            float t = maxTurn / angle;
            Vector3 blended = Vector3.Lerp(currentDir, desiredDir, t);
            float bLen = blended.Length();
            newDir = bLen > 1e-5f ? blended / bLen : desiredDir;
        }

        missile.Velocity = newDir * missile.Speed;
    }

    private Vector3 GetUnitCenter3D(WorldState world, UnitState unit)
    {
        float groundY = _geometry.GetSurfaceHeight(unit.Position, unit.SupportSurface);
        float alt = world.GetUnitAltitude(unit.Id);
        return new Vector3(unit.Position.X, groundY + alt + 2.2f, unit.Position.Y);
    }

    private static float DistancePointToSegmentSquared(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float abLenSq = ab.LengthSquared();
        if (abLenSq < 1e-6f)
            return Vector3.DistanceSquared(point, a);
        float t = Math.Clamp(Vector3.Dot(point - a, ab) / abLenSq, 0f, 1f);
        Vector3 closest = a + ab * t;
        return Vector3.DistanceSquared(point, closest);
    }
}
