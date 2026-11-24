using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Data-driven structural durability and collapse parameters for a destructible building section.
/// Values are supplied from map/content data rather than hardcoded constants.
/// </summary>
public readonly record struct StructureDestructionProfile(
    byte FloorsTotal,
    float FloorHeight,
    float RubbleHeight,
    ushort MaxHealth,
    ushort StructuralArmor = 0,
    float DustCloudRadius = 18f,
    float DustCloudHeight = 14f,
    int DustCloudDurationTicks = 200)
{
    public const byte MaxSupportedFloors = 8;

    public bool IsValid =>
        FloorsTotal >= 1 &&
        FloorsTotal <= MaxSupportedFloors &&
        float.IsFinite(FloorHeight) && FloorHeight > 0.1f &&
        float.IsFinite(RubbleHeight) && RubbleHeight >= 0f &&
        MaxHealth > 0 &&
        float.IsFinite(DustCloudRadius) && DustCloudRadius >= 0f &&
        float.IsFinite(DustCloudHeight) && DustCloudHeight >= 0f &&
        DustCloudDurationTicks >= 0;
}

/// <summary>
/// Shared deterministic math for deriving building section height and struck floor index
/// from authoritative discrete state (FloorsStanding) and data-driven profile parameters.
/// Used identically on server and client so CurrentHeight never needs wire replication.
/// </summary>
public static class StructureDestructionMath
{
    public static float ComputeCurrentHeight(
        float baseMinY,
        float originalMaxY,
        in StructureDestructionProfile profile,
        byte floorsStanding)
    {
        if (floorsStanding >= profile.FloorsTotal)
            return originalMaxY;

        float rubbleTop = MathF.Min(originalMaxY, baseMinY + MathF.Max(0f, profile.RubbleHeight));
        if (floorsStanding == 0)
            return rubbleTop;

        float standingTop = baseMinY + floorsStanding * profile.FloorHeight;
        return Math.Clamp(standingTop, rubbleTop, originalMaxY);
    }

    public static byte ResolveFloorIndex(
        float hitY,
        float baseMinY,
        in StructureDestructionProfile profile,
        byte floorsStanding)
    {
        if (floorsStanding <= 1)
            return 0;

        float relY = hitY - baseMinY;
        if (!float.IsFinite(relY) || relY <= 0f)
            return 0;

        int floor = (int)MathF.Floor(relY / profile.FloorHeight);
        return (byte)Math.Clamp(floor, 0, floorsStanding - 1);
    }

    public static byte GetFloorIntegrity(ulong packed, int floorIndex)
    {
        if ((uint)floorIndex >= StructureDestructionProfile.MaxSupportedFloors)
            return 0;
        return (byte)((packed >> (floorIndex * 8)) & 0xFFUL);
    }

    public static ulong SetFloorIntegrity(ulong packed, int floorIndex, byte integrity)
    {
        if ((uint)floorIndex >= StructureDestructionProfile.MaxSupportedFloors)
            return packed;
        int shift = floorIndex * 8;
        ulong mask = ~(0xFFUL << shift);
        return (packed & mask) | ((ulong)integrity << shift);
    }

    public static ulong CreateIntactFloorIntegrity(byte floorsTotal)
    {
        ulong packed = 0;
        int count = Math.Min((int)floorsTotal, (int)StructureDestructionProfile.MaxSupportedFloors);
        for (int f = 0; f < count; f++)
        {
            packed = SetFloorIntegrity(packed, f, 100);
        }
        return packed;
    }
}

/// <summary>
/// Authoritative runtime state for a single destructible building section in WorldState.
/// </summary>
public struct DestructibleStructureState
{
    public StructureId Id;
    public int GeometryIndex;
    public Vector3 Min;
    public Vector3 Max;
    public StructureDestructionProfile Profile;
    public ushort Health;
    public byte FloorsStanding;
    public byte FloorBreachMask;
    public ulong FloorIntegrityPacked;
    public SmokeCloudId DustCloudId;
    public int LastDamagedTick;
    public int CollapsedTick;

    public readonly float CurrentMaxY =>
        StructureDestructionMath.ComputeCurrentHeight(Min.Y, Max.Y, in Profile, FloorsStanding);

    public readonly bool IsDamaged =>
        Health < Profile.MaxHealth || FloorsStanding < Profile.FloorsTotal || FloorBreachMask != 0;

    public readonly bool IsCollapsed => FloorsStanding == 0;

    public readonly StructureObservation ToObservation() =>
        new(Id, FloorsStanding, FloorBreachMask, Health);
}

/// <summary>
/// Compact per-side observable snapshot record for a damaged or collapsed building section.
/// Omits CurrentHeight (derived deterministically from FloorsStanding and structure profile).
/// </summary>
public readonly record struct StructureObservation(
    StructureId Id,
    byte FloorsStanding,
    byte FloorBreachMask,
    ushort Health);

/// <summary>
/// Authoritative direct-fire order against a 3D world point or destructible building section.
/// </summary>
public readonly record struct AttackGroundCommand(
    uint Sequence,
    int ClientTick,
    EntityId Unit,
    Vector3 TargetPoint,
    StructureId TargetStructure = default);
