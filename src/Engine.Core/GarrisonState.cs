using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Which elevation band a garrisoned infantry unit occupies inside a building section.
/// </summary>
public enum GarrisonSlotKind : byte
{
    None = 0,
    Interior = 1,
    Roof = 2
}

/// <summary>
/// Data-driven local window/embrasure firing port relative to a section's base center
/// (meters, +Y up from floor base, +Z front courtyard facade, +X chaining axis).
/// </summary>
public readonly record struct BuildingFiringPort(
    float LocalX,
    float LocalY,
    float LocalZ,
    float NormalX,
    float NormalZ);

/// <summary>
/// Data-driven local rooftop firing/observation position relative to the roof deck.
/// </summary>
public readonly record struct BuildingRoofPort(
    float LocalX,
    float LocalY,
    float LocalZ);

/// <summary>
/// Data-driven garrison parameters for a building section authored in content JSON.
/// Shared between server LOS/combat/cover simulation and client presentation.
/// </summary>
public sealed class BuildingGarrisonProfile
{
    public const int MaxOccupantsPerStructure = 16;

    public int Capacity { get; }
    public Vector3 EntranceLocalOffset { get; }
    public float CoverDamageMultiplier { get; }
    public float RubbleCoverDamageMultiplier { get; }
    public float RoofCoverDamageMultiplier { get; }
    public float ConcealmentSpotRange { get; }
    public int FiringRevealTicks { get; }
    public BuildingFiringPort[] FiringPorts { get; }
    public BuildingRoofPort[] RoofPorts { get; }

    public BuildingGarrisonProfile(
        int capacity,
        Vector3 entranceLocalOffset,
        float coverDamageMultiplier = 0.35f,
        float rubbleCoverDamageMultiplier = 0.55f,
        float roofCoverDamageMultiplier = 0.70f,
        float concealmentSpotRange = 40.0f,
        int firingRevealTicks = 60,
        BuildingFiringPort[]? firingPorts = null,
        BuildingRoofPort[]? roofPorts = null)
    {
        Capacity = capacity;
        EntranceLocalOffset = entranceLocalOffset;
        CoverDamageMultiplier = coverDamageMultiplier;
        RubbleCoverDamageMultiplier = rubbleCoverDamageMultiplier;
        RoofCoverDamageMultiplier = roofCoverDamageMultiplier;
        ConcealmentSpotRange = concealmentSpotRange;
        FiringRevealTicks = firingRevealTicks;
        FiringPorts = firingPorts ?? [];
        RoofPorts = roofPorts ?? [];
    }

    public bool IsValid =>
        Capacity >= 1 &&
        Capacity <= MaxOccupantsPerStructure &&
        float.IsFinite(EntranceLocalOffset.X) &&
        float.IsFinite(EntranceLocalOffset.Y) &&
        float.IsFinite(EntranceLocalOffset.Z) &&
        float.IsFinite(CoverDamageMultiplier) && CoverDamageMultiplier > 0f && CoverDamageMultiplier <= 1f &&
        float.IsFinite(RubbleCoverDamageMultiplier) && RubbleCoverDamageMultiplier > 0f && RubbleCoverDamageMultiplier <= 1f &&
        float.IsFinite(RoofCoverDamageMultiplier) && RoofCoverDamageMultiplier > 0f && RoofCoverDamageMultiplier <= 1f &&
        float.IsFinite(ConcealmentSpotRange) && ConcealmentSpotRange >= 0f &&
        FiringRevealTicks >= 0;

    public static BuildingGarrisonProfile CreateDefault(float width = 14.4f, float depth = 11.8f)
    {
        float hw = width * 0.5f;
        float hd = depth * 0.5f;
        BuildingFiringPort[] ports =
        [
            new(-hw * 0.5f, 1.42f, hd, 0f, 1f),
            new(hw * 0.5f, 1.42f, hd, 0f, 1f),
            new(-hw * 0.5f, 1.42f, -hd, 0f, -1f),
            new(hw * 0.5f, 1.42f, -hd, 0f, -1f),
            new(-hw, 1.42f, 0f, -1f, 0f),
            new(hw, 1.42f, 0f, 1f, 0f),
        ];
        BuildingRoofPort[] roofPorts =
        [
            new(-hw * 0.55f, 1.15f, hd * 0.6f),
            new(hw * 0.55f, 1.15f, hd * 0.6f),
            new(-hw * 0.55f, 1.15f, -hd * 0.6f),
            new(hw * 0.55f, 1.15f, -hd * 0.6f),
        ];
        return new BuildingGarrisonProfile(
            capacity: 12,
            entranceLocalOffset: new Vector3(-1.8f, 0f, hd + 0.5f),
            coverDamageMultiplier: 0.35f,
            rubbleCoverDamageMultiplier: 0.55f,
            roofCoverDamageMultiplier: 0.70f,
            concealmentSpotRange: 40.0f,
            firingRevealTicks: 60,
            firingPorts: ports,
            roofPorts: roofPorts);
    }
}

/// <summary>
/// Command ordering an infantry unit to move to a building section's entrance and garrison it.
/// </summary>
public readonly record struct EnterBuildingCommand(
    uint Sequence,
    int ClientTick,
    EntityId Unit,
    StructureId TargetStructure,
    GarrisonSlotKind SlotKind = GarrisonSlotKind.Interior);

/// <summary>
/// Command ordering a garrisoned infantry unit to exit its building section at the entrance.
/// </summary>
public readonly record struct ExitBuildingCommand(
    uint Sequence,
    int ClientTick,
    EntityId Unit);

/// <summary>
/// Allocation-free structural blast/collapse casualty descriptor returned by GarrisonSystem
/// for CombatSystem to apply damage and destruction events.
/// </summary>
public readonly record struct GarrisonCasualty(
    EntityId UnitId,
    int Damage,
    Vector3 Position);

/// <summary>
/// Shared deterministic geometry helpers for rotating local entrance, window, and roof ports
/// by a building section's YawRad (matching Godot right-handed +Y rotation convention).
/// </summary>
public static class GarrisonMath
{
    public const float ExteriorClearanceMeters = 0.25f;

    public static Vector3 RotateLocalPoint(Vector3 baseCenter, float yawRad, Vector3 localOffset)
    {
        float cos = MathF.Cos(yawRad);
        float sin = MathF.Sin(yawRad);
        float wx = baseCenter.X + localOffset.X * cos + localOffset.Z * sin;
        float wz = baseCenter.Z - localOffset.X * sin + localOffset.Z * cos;
        return new Vector3(wx, baseCenter.Y + localOffset.Y, wz);
    }

    public static Vector2 RotateLocalDirection(float yawRad, float nx, float nz)
    {
        float cos = MathF.Cos(yawRad);
        float sin = MathF.Sin(yawRad);
        return new Vector2(nx * cos + nz * sin, -nx * sin + nz * cos);
    }

    public static Vector2 ComputeWorldEntrance(OpaqueStructure bounds, float yawRad, BuildingGarrisonProfile profile)
    {
        Vector3 baseCenter = new((bounds.MinX + bounds.MaxX) * 0.5f, bounds.MinY, (bounds.MinZ + bounds.MaxZ) * 0.5f);
        Vector3 world = RotateLocalPoint(baseCenter, yawRad, profile.EntranceLocalOffset);
        return new Vector2(world.X, world.Z);
    }

    /// <summary>
    /// Resolves the exterior 3D eye/muzzle/target point for a garrisoned unit looking or firing
    /// toward <paramref name="towardsXZ"/>. Offset slightly outside the structure face (+0.25 m)
    /// so the occupied building never self-occludes its own garrison while still blocking shots
    /// through solid interior walls.
    /// </summary>
    public static Vector3 ResolveElevatedPoint(
        OpaqueStructure bounds,
        float yawRad,
        in StructureDestructionProfile destructionProfile,
        BuildingGarrisonProfile garrisonProfile,
        byte floorsStanding,
        GarrisonSlotKind slotKind,
        Vector2 towardsXZ)
    {
        float cx = (bounds.MinX + bounds.MaxX) * 0.5f;
        float cz = (bounds.MinZ + bounds.MaxZ) * 0.5f;
        Vector3 baseCenter = new(cx, bounds.MinY, cz);

        Vector2 dir = towardsXZ - new Vector2(cx, cz);
        float dirLenSq = dir.LengthSquared();
        Vector2 dirNorm = dirLenSq > 1e-4f ? dir / MathF.Sqrt(dirLenSq) : new Vector2(0f, 1f);

        // Rooftop garrison (only valid when full roof stands)
        if (slotKind == GarrisonSlotKind.Roof && floorsStanding >= destructionProfile.FloorsTotal)
        {
            float roofY = bounds.MaxY;
            BuildingRoofPort[] roofPorts = garrisonProfile.RoofPorts;
            if (roofPorts.Length > 0)
            {
                int bestIdx = 0;
                float bestDot = float.NegativeInfinity;
                for (int i = 0; i < roofPorts.Length; i++)
                {
                    Vector2 worldOffset = RotateLocalDirection(yawRad, roofPorts[i].LocalX, roofPorts[i].LocalZ);
                    float d = Vector2.Dot(worldOffset, dirNorm);
                    if (d > bestDot)
                    {
                        bestDot = d;
                        bestIdx = i;
                    }
                }
                BuildingRoofPort chosen = roofPorts[bestIdx];
                Vector3 rp = RotateLocalPoint(new Vector3(cx, roofY, cz), yawRad, new Vector3(chosen.LocalX, chosen.LocalY, chosen.LocalZ));
                return rp;
            }
            return new Vector3(cx, roofY + 1.15f, cz);
        }

        // Collapsed rubble garrison (0 floors standing)
        if (floorsStanding == 0)
        {
            float rubbleEyeY = bounds.MinY + destructionProfile.RubbleHeight + 0.55f;
            float halfX = (bounds.MaxX - bounds.MinX) * 0.5f;
            float halfZ = (bounds.MaxZ - bounds.MinZ) * 0.5f;
            float px = Math.Clamp(cx + dirNorm.X * (halfX + ExteriorClearanceMeters), bounds.MinX - ExteriorClearanceMeters, bounds.MaxX + ExteriorClearanceMeters);
            float pz = Math.Clamp(cz + dirNorm.Y * (halfZ + ExteriorClearanceMeters), bounds.MinZ - ExteriorClearanceMeters, bounds.MaxZ + ExteriorClearanceMeters);
            return new Vector3(px, rubbleEyeY, pz);
        }

        // Interior garrison: top standing floor window port facing towardsXZ
        float floorBaseY = bounds.MinY + (floorsStanding - 1) * destructionProfile.FloorHeight;
        BuildingFiringPort[] ports = garrisonProfile.FiringPorts;
        if (ports.Length > 0)
        {
            int bestIdx = 0;
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < ports.Length; i++)
            {
                Vector2 worldNormal = RotateLocalDirection(yawRad, ports[i].NormalX, ports[i].NormalZ);
                float dot = Vector2.Dot(worldNormal, dirNorm);
                if (dot > bestScore)
                {
                    bestScore = dot;
                    bestIdx = i;
                }
            }

            BuildingFiringPort port = ports[bestIdx];
            Vector3 portWorld = RotateLocalPoint(new Vector3(cx, floorBaseY, cz), yawRad, new Vector3(port.LocalX, port.LocalY, port.LocalZ));
            Vector2 outNormal = RotateLocalDirection(yawRad, port.NormalX, port.NormalZ);
            return new Vector3(
                portWorld.X + outNormal.X * ExteriorClearanceMeters,
                portWorld.Y,
                portWorld.Z + outNormal.Y * ExteriorClearanceMeters);
        }

        // Fallback exterior face point when profile specifies no explicit firing ports
        float halfW = (bounds.MaxX - bounds.MinX) * 0.5f;
        float halfD = (bounds.MaxZ - bounds.MinZ) * 0.5f;
        if (MathF.Abs(dirNorm.X) * halfD > MathF.Abs(dirNorm.Y) * halfW)
        {
            float sx = dirNorm.X >= 0f ? 1f : -1f;
            return new Vector3(cx + sx * (halfW + ExteriorClearanceMeters), floorBaseY + 1.4f, cz);
        }
        else
        {
            float sz = dirNorm.Y >= 0f ? 1f : -1f;
            return new Vector3(cx, floorBaseY + 1.4f, cz + sz * (halfD + ExteriorClearanceMeters));
        }
    }
}
