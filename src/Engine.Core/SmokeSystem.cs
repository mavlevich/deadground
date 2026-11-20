using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Authoritative tactical smoke simulation system.
/// Drives unit smoke deployment (engine screens such as T-62 TDA, and future dischargers/grenades)
/// strictly from data profiles without depending on map names or model assets.
/// Fully allocation-free during simulation ticks.
/// </summary>
public sealed class SmokeSystem : ISimulationSystem
{
    public const int StationaryRefreshIntervalTicks = 200; // 10 seconds at 20 Hz

    private readonly StaticGeometry _geometry;
    private readonly IUnitSmokeCatalog _catalog;
    private readonly SmokeVolume[] _activeVolumes = new SmokeVolume[WorldState.MaxSmokeClouds];
    private int _activeVolumeCount;

    public StaticGeometry Geometry => _geometry;
    public int ActiveVolumeCount => _activeVolumeCount;
    public ReadOnlySpan<SmokeVolume> ActiveVolumes => _activeVolumes.AsSpan(0, _activeVolumeCount);

    public SmokeSystem(StaticGeometry geometry, IUnitSmokeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(catalog);
        _geometry = geometry;
        _catalog = catalog;
    }

    public void CopyActiveVolumesTo(LineOfSightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SetSmokeVolumes(_activeVolumes, _activeVolumeCount);
    }

    public void EnsureUnitsInitialized(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        int count = world.Count;
        for (int i = 0; i < count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            EnsureUnitInitialized(world, unit.Id, unit.Definition);
        }
    }

    public bool CanDeploySmoke(int player, EntityId unitId, WorldState world)
    {
        if (!world.TryGetUnit(unitId, out UnitState unit) || unit.Owner != player || world.IsContained(unitId))
        {
            return false;
        }

        if (!_catalog.TryGetSmokeProfile(unit.Definition, out SmokeProfile profile) || !profile.IsValid)
        {
            return false;
        }

        WorldState.UnitSmokeState state = EnsureUnitInitialized(world, unitId, unit.Definition);
        return state.HasCapability &&
               state.ChargesRemaining > 0 &&
               state.ActiveEmitTicksRemaining == 0 &&
               state.CooldownTicksRemaining == 0;
    }

    public bool TryExecuteDeploySmoke(int player, DeploySmokeCommand command, WorldState world, int tick)
    {
        if (player < 1 || command.Sequence == 0)
        {
            return false;
        }

        if (!world.TryGetUnit(command.Unit, out UnitState unit) || unit.Owner != player || world.IsContained(command.Unit))
        {
            return false;
        }

        if (!_catalog.TryGetSmokeProfile(unit.Definition, out SmokeProfile profile) || !profile.IsValid)
        {
            return false;
        }

        WorldState.UnitSmokeState state = EnsureUnitInitialized(world, command.Unit, unit.Definition);
        if (!state.HasCapability ||
            state.ChargesRemaining <= 0 ||
            state.ActiveEmitTicksRemaining > 0 ||
            state.CooldownTicksRemaining > 0)
        {
            return false;
        }

        if (profile.Kind == SmokeDeliveryKind.EngineScreen)
        {
            state.ChargesRemaining--;
            state.ActiveEmitTicksRemaining = profile.EmitDurationTicks;
            state.EmitIntervalRemaining = profile.EmitIntervalTicks;

            Vector2 cloudPos = ComputeExhaustCloudPosition(world, unit, profile.ExhaustOffset);
            SpawnCloud(world, unit.Owner, cloudPos, unit.SupportSurface, profile, tick);
            state.HasLastCloud = true;
            state.LastCloudPosition = cloudPos;
            state.LastCloudTick = tick;

            world.SetSmokeState(command.Unit, state);
            RebuildActiveVolumes(world, tick);
            return true;
        }

        // Targeted delivery (grenade / discharger) for future unit definitions
        Vector2 targetPos = command.HasTarget ? command.Target : unit.Position;
        uint targetSurface = command.HasTarget ? command.TargetSurface : unit.SupportSurface;
        if (!world.Map.Contains(targetPos))
        {
            return false;
        }

        float distSq = Vector2.DistanceSquared(unit.Position, targetPos);
        float maxRange = profile.Range;
        if (distSq > maxRange * maxRange + 1e-3f)
        {
            return false;
        }

        state.ChargesRemaining--;
        state.CooldownTicksRemaining = profile.CooldownTicks;
        SpawnCloud(world, unit.Owner, targetPos, targetSurface, profile, tick);
        state.HasLastCloud = true;
        state.LastCloudPosition = targetPos;
        state.LastCloudTick = tick;

        world.SetSmokeState(command.Unit, state);
        RebuildActiveVolumes(world, tick);
        return true;
    }

    public void Update(WorldState world, int tick)
    {
        world.RemoveExpiredSmokeClouds(tick);

        int unitCount = world.Count;
        for (int i = 0; i < unitCount; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            WorldState.UnitSmokeState state = EnsureUnitInitialized(world, unit.Id, unit.Definition);
            if (!state.HasCapability)
            {
                continue;
            }

            bool dirty = false;

            if (world.IsContained(unit.Id))
            {
                if (state.ActiveEmitTicksRemaining > 0)
                {
                    state.ActiveEmitTicksRemaining = 0;
                    state.EmitIntervalRemaining = 0;
                    dirty = true;
                }
                if (dirty)
                {
                    world.SetSmokeState(unit.Id, state);
                }
                continue;
            }

            if (state.ActiveEmitTicksRemaining > 0)
            {
                if (!_catalog.TryGetSmokeProfile(unit.Definition, out SmokeProfile profile) || !profile.IsValid)
                {
                    state.ActiveEmitTicksRemaining = 0;
                    state.EmitIntervalRemaining = 0;
                    world.SetSmokeState(unit.Id, state);
                    continue;
                }

                state.ActiveEmitTicksRemaining--;
                state.EmitIntervalRemaining--;
                dirty = true;

                if (state.EmitIntervalRemaining <= 0)
                {
                    Vector2 cloudPos = ComputeExhaustCloudPosition(world, unit, profile.ExhaustOffset);
                    float minSpacingSq = profile.MinCloudSpacing * profile.MinCloudSpacing;
                    bool movedEnough = !state.HasLastCloud ||
                        Vector2.DistanceSquared(cloudPos, state.LastCloudPosition) >= minSpacingSq;
                    bool stationaryRefreshDue = !state.HasLastCloud ||
                        (tick - state.LastCloudTick) >= StationaryRefreshIntervalTicks;

                    if (movedEnough || stationaryRefreshDue)
                    {
                        SpawnCloud(world, unit.Owner, cloudPos, unit.SupportSurface, profile, tick);
                        state.HasLastCloud = true;
                        state.LastCloudPosition = cloudPos;
                        state.LastCloudTick = tick;
                    }

                    state.EmitIntervalRemaining = profile.EmitIntervalTicks;
                }

                if (state.ActiveEmitTicksRemaining == 0)
                {
                    state.CooldownTicksRemaining = profile.CooldownTicks;
                }
            }
            else if (state.CooldownTicksRemaining > 0)
            {
                state.CooldownTicksRemaining--;
                dirty = true;
            }

            if (dirty)
            {
                world.SetSmokeState(unit.Id, state);
            }
        }

        RebuildActiveVolumes(world, tick);
    }

    public void RebuildActiveVolumes(WorldState world, int tick)
    {
        ReadOnlySpan<SmokeCloudState> clouds = world.SmokeClouds;
        int outCount = 0;
        for (int i = 0; i < clouds.Length; i++)
        {
            ref readonly SmokeCloudState cloud = ref clouds[i];
            cloud.EvaluateAtTick(tick, out float effectiveRadius, out float effectiveDensity);
            if (effectiveRadius <= 0f || effectiveDensity <= 0f)
            {
                continue;
            }

            float baseY = _geometry.GetSurfaceHeight(cloud.Position, cloud.SupportSurface);
            _activeVolumes[outCount++] = new SmokeVolume(
                cloud.Position.X,
                cloud.Position.Y,
                baseY,
                baseY + cloud.Height,
                effectiveRadius,
                effectiveDensity,
                cloud.Id);
        }
        _activeVolumeCount = outCount;
    }

    private WorldState.UnitSmokeState EnsureUnitInitialized(WorldState world, EntityId unitId, ushort definition)
    {
        if (!world.TryGetSmokeState(unitId, out WorldState.UnitSmokeState state))
        {
            return default;
        }

        if (state.Initialized)
        {
            return state;
        }

        if (_catalog.TryGetSmokeProfile(definition, out SmokeProfile profile) && profile.IsValid)
        {
            state = new WorldState.UnitSmokeState(
                Initialized: true,
                HasCapability: true,
                ChargesRemaining: profile.Charges,
                CooldownTicksRemaining: 0,
                ActiveEmitTicksRemaining: 0,
                EmitIntervalRemaining: 0,
                HasLastCloud: false,
                LastCloudPosition: Vector2.Zero,
                LastCloudTick: -1);
        }
        else
        {
            state = new WorldState.UnitSmokeState(
                Initialized: true,
                HasCapability: false,
                ChargesRemaining: 0,
                CooldownTicksRemaining: 0,
                ActiveEmitTicksRemaining: 0,
                EmitIntervalRemaining: 0,
                HasLastCloud: false,
                LastCloudPosition: Vector2.Zero,
                LastCloudTick: -1);
        }

        world.SetSmokeState(unitId, state);
        return state;
    }

    private static Vector2 ComputeExhaustCloudPosition(WorldState world, in UnitState unit, float exhaustOffset)
    {
        if (exhaustOffset <= 0f)
        {
            return unit.Position;
        }

        float hullHeading = world.TryGetCombatState(unit.Id, out WorldState.CombatState cs) ? cs.HullHeading : 0f;
        // HullHeading = Atan2(-forward.X, -forward.Y), so forward = (-sin, -cos) and rear exhaust = (+sin, +cos)
        Vector2 rearDir = new(MathF.Sin(hullHeading), MathF.Cos(hullHeading));
        Vector2 raw = unit.Position + rearDir * exhaustOffset;
        float clampedX = Math.Clamp(raw.X, 0f, world.Map.Width);
        float clampedY = Math.Clamp(raw.Y, 0f, world.Map.Depth);
        return new Vector2(clampedX, clampedY);
    }

    private static void SpawnCloud(
        WorldState world,
        int owner,
        Vector2 position,
        uint supportSurface,
        in SmokeProfile profile,
        int tick)
    {
        world.AddSmokeCloud(
            owner,
            position,
            supportSurface,
            profile.CloudRadius,
            profile.CloudHeight,
            createdTick: tick,
            expiresTick: tick + profile.CloudDurationTicks,
            bloomTicks: (ushort)Math.Clamp(profile.BloomTicks, 0, ushort.MaxValue),
            fadeTicks: (ushort)Math.Clamp(profile.FadeTicks, 0, ushort.MaxValue),
            opticalDensity: profile.OpticalDensity);
    }
}
