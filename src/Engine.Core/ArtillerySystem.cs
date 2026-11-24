using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Server-authoritative indirect-fire rocket artillery simulation system.
/// Handles area fire missions, launcher traverse and elevation, staggered multi-rocket
/// salvos with deterministic circular dispersion, parabolic flight timing, and impact triggers.
/// Strictly allocation-free during simulation ticks.
/// </summary>
public sealed class ArtillerySystem : ISimulationSystem, IRandomStreamsAware
{
    public const float ElevationRateDegPerSec = 25f;
    public const float PitchAlignmentToleranceRad = 0.035f;
    public const float DefaultTraverseRateDegPerSec = 25f;
    public const float DefaultElevationDeg = 32f;

    private readonly StaticGeometry _geometry;
    private readonly IUnitCombatCatalog _combatCatalog;
    private readonly NavigationSystem? _navigation;
    private DeterministicRandom? _artilleryRandom;

    public int TotalRocketsFired { get; private set; }
    public int TotalImpacts { get; private set; }

    public Action<int, ushort, EntityId, int, Vector3, Vector3>? OnRocketLaunched { get; set; }
    public Action<WorldState, int, RocketProjectileState>? OnRocketImpact { get; set; }

    private DeterministicRandom ArtilleryRandom =>
        _artilleryRandom ?? throw new InvalidOperationException("ArtillerySystem random stream is not initialized.");

    public ArtillerySystem(
        StaticGeometry geometry,
        IUnitCombatCatalog combatCatalog,
        NavigationSystem? navigation = null,
        DeterministicRandom? artilleryRandom = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(combatCatalog);

        _geometry = geometry;
        _combatCatalog = combatCatalog;
        _navigation = navigation;
        _artilleryRandom = artilleryRandom;
    }

    public void InitializeRandomStreams(IRandomStreamProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _artilleryRandom = provider.GetStream(RandomDomain.Artillery);
    }

    public bool CanExecuteFireMission(int player, FireMissionCommand command, WorldState world)
    {
        if (command.Sequence == 0)
            return false;

        if (!world.TryGetUnit(command.Unit, out UnitState unit) ||
            unit.Owner != player ||
            world.IsContained(command.Unit) ||
            world.IsCrashing(command.Unit) ||
            world.IsSlung(command.Unit))
        {
            return false;
        }

        if (!_combatCatalog.TryGetProfile(unit.Definition, out UnitCombatProfile profile) ||
            profile.Weapon is null ||
            !profile.Weapon.IsIndirectFire)
        {
            return false;
        }

        if (command.Cancel)
            return true;

        if (!world.Map.Contains(command.Target))
            return false;

        if (!world.TryGetCombatState(command.Unit, out WorldState.CombatState cs) || cs.Ammunition <= 0)
            return false;

        float dist = Vector2.Distance(unit.Position, command.Target);
        WeaponDefinition weapon = profile.Weapon;
        if (dist < weapon.MinRange - CombatSystem.RangeToleranceMargin ||
            dist > weapon.Range + CombatSystem.RangeToleranceMargin)
        {
            return false;
        }

        return true;
    }

    public bool TryOrderFireMission(int player, FireMissionCommand command, WorldState world)
    {
        if (!CanExecuteFireMission(player, command, world))
            return false;

        if (command.Cancel)
        {
            world.ClearFireMission(command.Unit);
            return true;
        }

        if (!world.TryGetUnit(command.Unit, out UnitState unit) ||
            !_combatCatalog.TryGetProfile(unit.Definition, out UnitCombatProfile profile) ||
            profile.Weapon is null ||
            !world.TryGetCombatState(command.Unit, out WorldState.CombatState cs) ||
            !world.TryGetArtilleryState(command.Unit, out WorldState.UnitArtilleryState art))
        {
            return false;
        }

        WeaponDefinition weapon = profile.Weapon;

        // Stop unit movement and clear any direct-fire attack order
        world.UpdateUnitNavigation(unit.Id, unit.Position, unit.Position, false);
        _navigation?.ClearUnit(unit.Id);
        world.ClearAttackOrder(unit.Id);

        int salvoSize = Math.Min(Math.Max(1, weapon.SalvoCount), cs.Ammunition);
        art.HasFireMission = true;
        art.Target = command.Target;
        art.SetupTicksRemaining = Math.Max(0, weapon.SetupTicks);
        art.SalvoRemaining = salvoSize;
        art.SalvoIntervalRemaining = 0;
        world.SetArtilleryState(unit.Id, in art);

        cs.Status = AttackExecutionStatus.Aiming;
        world.SetCombatState(unit.Id, in cs);
        return true;
    }

    public void Update(WorldState world, int tick)
    {
        // Phase 1: Resolve active in-flight rockets reaching impact tick
        int projIndex = 0;
        while (projIndex < world.ArtilleryProjectileCount)
        {
            RocketProjectileState proj = world.ArtilleryProjectiles[projIndex];
            if (tick >= proj.ImpactTick)
            {
                world.RemoveArtilleryProjectileAt(projIndex);
                TotalImpacts++;
                OnRocketImpact?.Invoke(world, tick, proj);
            }
            else
            {
                projIndex++;
            }
        }

        // Phase 2: Step indirect-fire artillery launchers
        float maxPitchStep = (ElevationRateDegPerSec * (MathF.PI / 180f)) * Simulation.TickSeconds;
        int unitCount = world.Count;

        for (int i = 0; i < unitCount; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            if (!_combatCatalog.TryGetProfile(unit.Definition, out UnitCombatProfile profile) ||
                profile.Weapon is null ||
                !profile.Weapon.IsIndirectFire)
            {
                continue;
            }

            if (!world.TryGetArtilleryState(unit.Id, out WorldState.UnitArtilleryState art) ||
                !world.TryGetCombatState(unit.Id, out WorldState.CombatState cs))
            {
                continue;
            }

            bool unavailable = unit.Moving ||
                world.IsContained(unit.Id) ||
                world.IsCrashing(unit.Id) ||
                world.IsSlung(unit.Id);

            if (unavailable || !art.HasFireMission)
            {
                bool artChanged = false;
                bool csChanged = false;
                if (art.HasFireMission)
                {
                    art.HasFireMission = false;
                    art.SetupTicksRemaining = 0;
                    art.SalvoRemaining = 0;
                    art.SalvoIntervalRemaining = 0;
                    artChanged = true;
                    if (cs.Status == AttackExecutionStatus.Aiming)
                    {
                        cs.Status = AttackExecutionStatus.None;
                        csChanged = true;
                    }
                }

                if (art.GunPitchRad > 0f)
                {
                    art.GunPitchRad = MathF.Max(0f, art.GunPitchRad - maxPitchStep);
                    artChanged = true;
                }

                if (artChanged)
                    world.SetArtilleryState(unit.Id, in art);
                if (csChanged)
                    world.SetCombatState(unit.Id, in cs);
                continue;
            }

            WeaponDefinition weapon = profile.Weapon;

            if (cs.Ammunition <= 0 || art.SalvoRemaining <= 0)
            {
                art.HasFireMission = false;
                art.SetupTicksRemaining = 0;
                art.SalvoRemaining = 0;
                art.SalvoIntervalRemaining = 0;
                cs.Status = cs.Ammunition <= 0 ? AttackExecutionStatus.NoAmmo : AttackExecutionStatus.None;
                world.SetArtilleryState(unit.Id, in art);
                world.SetCombatState(unit.Id, in cs);
                continue;
            }

            // Traverse turntable toward fire mission bearing
            float traverseRate = profile.TurretTraverseRate > 0f ? profile.TurretTraverseRate : DefaultTraverseRateDegPerSec;
            Vector2 toTarget = art.Target - unit.Position;
            if (toTarget.LengthSquared() > 0.0001f)
            {
                float desiredYaw = MathF.Atan2(-toTarget.X, -toTarget.Y);
                float maxYawStep = (traverseRate * (MathF.PI / 180f)) * Simulation.TickSeconds;
                float yawDelta = CombatSystem.ShorterArcDelta(cs.TurretYaw, desiredYaw);
                if (MathF.Abs(yawDelta) <= maxYawStep)
                {
                    cs.TurretYaw = CombatSystem.NormalizeYaw(desiredYaw);
                }
                else
                {
                    cs.TurretYaw = CombatSystem.NormalizeYaw(cs.TurretYaw + MathF.Sign(yawDelta) * maxYawStep);
                }
            }

            // Elevate launcher rack to firing pitch
            float elevationDeg = weapon.ElevationDegrees > 0f ? Math.Clamp(weapon.ElevationDegrees, 11f, 55f) : DefaultElevationDeg;
            float desiredPitchRad = elevationDeg * (MathF.PI / 180f);
            float pitchDelta = desiredPitchRad - art.GunPitchRad;
            if (MathF.Abs(pitchDelta) <= maxPitchStep)
            {
                art.GunPitchRad = desiredPitchRad;
            }
            else
            {
                art.GunPitchRad += MathF.Sign(pitchDelta) * maxPitchStep;
            }

            if (art.SetupTicksRemaining > 0)
            {
                art.SetupTicksRemaining--;
            }

            bool readyToFire =
                art.SetupTicksRemaining == 0 &&
                cs.ReloadTicksRemaining == 0 &&
                CombatSystem.IsTurretAligned(cs.TurretYaw, unit.Position, art.Target) &&
                MathF.Abs(art.GunPitchRad - desiredPitchRad) <= PitchAlignmentToleranceRad;

            if (readyToFire)
            {
                if (art.SalvoIntervalRemaining > 0)
                {
                    art.SalvoIntervalRemaining--;
                }
                else
                {
                    FireSingleRocket(world, tick, unit, weapon, ref cs, ref art);
                }
            }
            else
            {
                cs.Status = AttackExecutionStatus.Aiming;
            }

            world.SetArtilleryState(unit.Id, in art);
            world.SetCombatState(unit.Id, in cs);
        }
    }

    private void FireSingleRocket(
        WorldState world,
        int tick,
        UnitState unit,
        WeaponDefinition weapon,
        ref WorldState.CombatState cs,
        ref WorldState.UnitArtilleryState art)
    {
        float u = ArtilleryRandom.NextFloat();
        float radialFactor = 0.5f * (MathF.Sqrt(u) + u);
        float radius = weapon.DispersionRadius * radialFactor;
        float angle = ArtilleryRandom.NextFloat() * MathF.Tau;

        float margin = MathF.Min(2f, MathF.Min(world.Map.Width, world.Map.Depth) * 0.1f);
        Vector2 impact2D = new(
            Math.Clamp(art.Target.X + MathF.Cos(angle) * radius, margin, MathF.Max(margin, world.Map.Width - margin)),
            Math.Clamp(art.Target.Y + MathF.Sin(angle) * radius, margin, MathF.Max(margin, world.Map.Depth - margin)));

        float launchGroundY = _geometry.GetSurfaceHeight(unit.Position, unit.SupportSurface);
        Vector3 origin3D = new(unit.Position.X, launchGroundY + weapon.MuzzleHeight, unit.Position.Y);

        float impactGroundY = _geometry.GetSurfaceHeight(impact2D, supportSurface: 0);
        Vector3 target3D = new(impact2D.X, impactGroundY, impact2D.Y);

        ArtilleryBallistics.ComputeTrajectory(origin3D, target3D, weapon.ProjectileSpeed, out int flightTicks, out _);
        int impactTick = tick + flightTicks;

        RocketProjectileState projectile = new(
            unit.Id,
            unit.Definition,
            unit.Owner,
            weapon.PresentationId,
            origin3D,
            target3D,
            tick,
            impactTick,
            weapon.Damage,
            weapon.SplashRadius,
            weapon.PenetrationNear,
            weapon.StructuralDamage);

        world.TryAddArtilleryProjectile(in projectile);

        cs.Ammunition = Math.Max(0, cs.Ammunition - 1);
        art.SalvoRemaining = Math.Max(0, art.SalvoRemaining - 1);
        art.SalvoIntervalRemaining = Math.Max(1, weapon.SalvoIntervalTicks);
        art.NextTubeIndex++;
        TotalRocketsFired++;

        OnRocketLaunched?.Invoke(tick, weapon.PresentationId, unit.Id, unit.Owner, origin3D, target3D);

        if (art.SalvoRemaining == 0 || cs.Ammunition == 0)
        {
            art.HasFireMission = false;
            art.SetupTicksRemaining = 0;
            art.SalvoRemaining = 0;
            art.SalvoIntervalRemaining = 0;
            cs.ReloadTicksRemaining = cs.Ammunition > 0 ? weapon.ReloadDurationTicks : 0;
            cs.Status = cs.Ammunition > 0 ? AttackExecutionStatus.None : AttackExecutionStatus.NoAmmo;
        }
    }
}
