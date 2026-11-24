using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Server-authoritative destructible building simulation system.
/// Tracks per-section durability, per-floor breach/integrity masks, deterministic progressive
/// upper-floor collapse when supporting floors fail, O(1) height overrides for StaticGeometry
/// line-of-sight queries, and collapse dust clouds backed by SmokeSystem/SmokeVolume.
/// Strictly allocation-free during simulation ticks.
/// </summary>
public sealed class BuildingDestructionSystem : ISimulationSystem
{
    public const byte BreachIntegrityThreshold = 55;
    // Zero bloom ticks so collapse dust occludes LOS immediately on the collapse tick (no 1-tick flash)
    public const int CollapseDustBloomTicks = 0;

    private readonly StaticGeometry _geometry;
    private readonly SmokeSystem? _smokeSystem;
    private readonly float[] _structureMaxYOverrides;

    public StaticGeometry Geometry => _geometry;
    public int TotalDirectHits { get; private set; }
    public int TotalSplashHits { get; private set; }
    public int TotalFloorsCollapsed { get; private set; }
    public int TotalSectionsCollapsed { get; private set; }

    public BuildingDestructionSystem(StaticGeometry geometry, SmokeSystem? smokeSystem = null)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        _geometry = geometry;
        _smokeSystem = smokeSystem;
        _structureMaxYOverrides = new float[geometry.Structures.Count];
        Array.Fill(_structureMaxYOverrides, float.NaN);
    }

    public bool RegisterStructure(WorldState world, StructureId structureId, in StructureDestructionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!profile.IsValid)
            return false;
        if (!_geometry.TryGetStructureIndex(structureId, out int geomIndex))
            return false;

        OpaqueStructure structure = _geometry.Structures[geomIndex];
        if (!world.TryRegisterDestructibleStructure(structure, geomIndex, in profile))
            return false;

        _structureMaxYOverrides[geomIndex] = structure.MaxY;
        return true;
    }

    public void CopyHeightOverridesTo(LineOfSightContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.SetStructureMaxYOverrides(_structureMaxYOverrides, _structureMaxYOverrides.Length);
    }

    public void SyncOverridesFromWorld(WorldState world)
    {
        ArgumentNullException.ThrowIfNull(world);
        int count = world.DestructibleStructureCount;
        for (int i = 0; i < count; i++)
        {
            ref readonly DestructibleStructureState s = ref world.GetDestructibleStructureRef(i);
            if ((uint)s.GeometryIndex < (uint)_structureMaxYOverrides.Length)
            {
                _structureMaxYOverrides[s.GeometryIndex] = s.CurrentMaxY;
            }
        }
    }

    public void Update(WorldState world, int tick)
    {
        SyncOverridesFromWorld(world);
    }

    /// <summary>
    /// Applies a direct weapon impact to a registered destructible structure at <paramref name="impactPoint"/>.
    /// Evaluates per-floor breach and deterministic progressive collapse when a supporting floor fails.
    /// </summary>
    public bool ApplyDirectHit(
        WorldState world,
        int tick,
        StructureId structureId,
        Vector3 impactPoint,
        int structuralDamage,
        out byte struckFloor,
        out byte floorsLost,
        out bool sectionFullyCollapsed)
    {
        struckFloor = 0;
        floorsLost = 0;
        sectionFullyCollapsed = false;

        if (structuralDamage <= 0)
            return false;

        if (!world.TryGetDestructibleStructureIndex(structureId, out int index))
            return false;

        ref DestructibleStructureState state = ref world.GetDestructibleStructureRef(index);
        if (state.FloorsStanding == 0)
            return false;

        int effectiveDamage = structuralDamage - state.Profile.StructuralArmor;
        if (effectiveDamage <= 0)
            return false;

        TotalDirectHits++;
        struckFloor = StructureDestructionMath.ResolveFloorIndex(
            impactPoint.Y,
            state.Min.Y,
            in state.Profile,
            state.FloorsStanding);

        ApplyFloorDamage(world, tick, ref state, struckFloor, effectiveDamage, out floorsLost, out sectionFullyCollapsed);
        return true;
    }

    /// <summary>
    /// Applies radial explosive splash damage (e.g. artillery rockets) to all destructible sections
    /// in deterministic registration order. Returns the number of sections damaged and invokes
    /// optional callback data through caller-supplied buffers with zero allocations.
    /// </summary>
    public int ApplySplashDamage(
        WorldState world,
        int tick,
        Vector3 center,
        float splashRadius,
        int structuralDamage,
        Span<StructureSplashResult> results)
    {
        if (structuralDamage <= 0 || splashRadius <= 0f)
            return 0;

        float radiusSq = splashRadius * splashRadius;
        int count = world.DestructibleStructureCount;
        int written = 0;

        // Deterministic fixed-order traversal over registered destructible structures
        for (int i = 0; i < count; i++)
        {
            ref DestructibleStructureState state = ref world.GetDestructibleStructureRef(i);
            if (state.FloorsStanding == 0)
                continue;

            float currentMaxY = state.CurrentMaxY;
            Vector3 closest = new(
                Math.Clamp(center.X, state.Min.X, state.Max.X),
                Math.Clamp(center.Y, state.Min.Y, currentMaxY),
                Math.Clamp(center.Z, state.Min.Z, state.Max.Z));

            float distSq = Vector3.DistanceSquared(center, closest);
            if (distSq > radiusSq)
                continue;

            float dist = MathF.Sqrt(distSq);
            float falloff = Math.Clamp(1f - (dist / splashRadius) * 0.72f, 0.28f, 1f);
            int rawSplashDamage = (int)MathF.Round(structuralDamage * falloff);
            int effectiveDamage = rawSplashDamage - state.Profile.StructuralArmor;
            if (effectiveDamage <= 0)
                continue;

            TotalSplashHits++;
            byte struckFloor = StructureDestructionMath.ResolveFloorIndex(
                closest.Y,
                state.Min.Y,
                in state.Profile,
                state.FloorsStanding);

            ApplyFloorDamage(world, tick, ref state, struckFloor, effectiveDamage, out byte floorsLost, out bool sectionCollapsed);

            if (written < results.Length)
            {
                results[written++] = new StructureSplashResult(
                    state.Id,
                    closest,
                    struckFloor,
                    floorsLost,
                    sectionCollapsed);
            }
        }

        return written;
    }

    private void ApplyFloorDamage(
        WorldState world,
        int tick,
        ref DestructibleStructureState state,
        byte struckFloor,
        int effectiveDamage,
        out byte floorsLost,
        out bool sectionFullyCollapsed)
    {
        floorsLost = 0;
        sectionFullyCollapsed = false;

        byte prevFloorsStanding = state.FloorsStanding;
        int newHealth = Math.Max(0, state.Health - effectiveDamage);
        state.Health = (ushort)newHealth;
        state.LastDamagedTick = tick;

        // Compute per-floor integrity drop relative to floor durability budget.
        // A floor supports its share of the section health (~MaxHealth / FloorsTotal).
        float floorBudget = MathF.Max(1f, (float)state.Profile.MaxHealth / state.Profile.FloorsTotal);
        int integrityLoss = Math.Max(1, (int)MathF.Round((effectiveDamage / floorBudget) * 100f));
        byte currentIntegrity = StructureDestructionMath.GetFloorIntegrity(state.FloorIntegrityPacked, struckFloor);
        byte nextIntegrity = (byte)Math.Max(0, currentIntegrity - integrityLoss);
        state.FloorIntegrityPacked = StructureDestructionMath.SetFloorIntegrity(state.FloorIntegrityPacked, struckFloor, nextIntegrity);

        if (nextIntegrity <= BreachIntegrityThreshold)
        {
            state.FloorBreachMask |= (byte)(1 << struckFloor);
        }

        byte nextFloorsStanding = prevFloorsStanding;
        if (state.Health == 0)
        {
            // Total section structural failure: entire section collapses to rubble
            nextFloorsStanding = 0;
        }
        else if (nextIntegrity == 0 && struckFloor < prevFloorsStanding)
        {
            // Progressive collapse: supporting floor failed, so it and every floor above it collapse immediately
            nextFloorsStanding = struckFloor;
        }

        if (nextFloorsStanding < prevFloorsStanding)
        {
            floorsLost = (byte)(prevFloorsStanding - nextFloorsStanding);
            state.FloorsStanding = nextFloorsStanding;

            // Clear breach flags and floor integrity for collapsed upper floors
            byte standingMask = nextFloorsStanding == 0 ? (byte)0 : (byte)((1 << nextFloorsStanding) - 1);
            state.FloorBreachMask &= standingMask;
            for (int f = nextFloorsStanding; f < prevFloorsStanding; f++)
            {
                state.FloorIntegrityPacked = StructureDestructionMath.SetFloorIntegrity(state.FloorIntegrityPacked, f, 0);
            }

            if (nextFloorsStanding == 0)
            {
                state.Health = 0;
                state.CollapsedTick = tick;
                sectionFullyCollapsed = true;
                TotalSectionsCollapsed++;
            }

            TotalFloorsCollapsed += floorsLost;

            // Update O(1) height override immediately so subsequent LOS/fire checks in this tick use the new height
            if ((uint)state.GeometryIndex < (uint)_structureMaxYOverrides.Length)
            {
                _structureMaxYOverrides[state.GeometryIndex] = state.CurrentMaxY;
            }

            // Spawn a real tactical dust SmokeVolume so LOS does not snap open ahead of collapse visuals
            SpawnCollapseDustCloud(world, tick, in state, floorsLost);
        }
    }

    private void SpawnCollapseDustCloud(
        WorldState world,
        int tick,
        in DestructibleStructureState state,
        byte floorsLost)
    {
        if (state.Profile.DustCloudDurationTicks <= 0 || state.Profile.DustCloudRadius <= 0f)
            return;

        Vector2 center2D = new(
            (state.Min.X + state.Max.X) * 0.5f,
            (state.Min.Z + state.Max.Z) * 0.5f);
        float radiusScale = state.FloorsStanding == 0
            ? 1.0f
            : Math.Clamp(0.65f + floorsLost * 0.12f, 0.65f, 1.0f);
        float heightScale = state.FloorsStanding == 0
            ? 1.0f
            : Math.Clamp(0.6f + floorsLost * 0.12f, 0.6f, 1.0f);

        int duration = state.Profile.DustCloudDurationTicks;
        ushort fadeTicks = (ushort)Math.Clamp(duration / 3, 20, ushort.MaxValue);

        world.AddSmokeCloud(
            owner: 0,
            position: center2D,
            supportSurface: 0,
            radius: state.Profile.DustCloudRadius * radiusScale,
            height: state.Profile.DustCloudHeight * heightScale,
            createdTick: tick,
            expiresTick: tick + duration,
            bloomTicks: CollapseDustBloomTicks,
            fadeTicks: fadeTicks,
            opticalDensity: 1.0f);

        _smokeSystem?.RebuildActiveVolumes(world, tick);
    }
}

public readonly record struct StructureSplashResult(
    StructureId Id,
    Vector3 ImpactPoint,
    byte StruckFloor,
    byte FloorsLost,
    bool SectionCollapsed);
