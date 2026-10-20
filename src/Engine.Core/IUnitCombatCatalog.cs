namespace DeadGround.Engine;

public enum ArmorSide : byte
{
    Front = 0,
    Side = 1,
    Rear = 2
}

public readonly record struct UnitArmor(
    float Front,
    float Side,
    float Rear,
    float FrontSectorAngle = 90f,
    float RearSectorAngle = 90f)
{
    public float FrontSectorHalfAngle => MathF.Max(0f, FrontSectorAngle * 0.5f) * (MathF.PI / 180f);
    public float RearSectorHalfAngle => MathF.Max(0f, RearSectorAngle * 0.5f) * (MathF.PI / 180f);
}

public enum WeaponPresentationKind : byte
{
    None = 0,
    Cannon = 1,
    MachineGun = 2
}

public static class WeaponPresentation
{
    public static ushort MakeId(WeaponPresentationKind kind, ushort uniqueIndex = 0) =>
        (ushort)(((ushort)kind << 8) | (uniqueIndex & 0xFF));

    public static WeaponPresentationKind GetKind(ushort presentationId)
    {
        byte kindByte = (byte)(presentationId >> 8);
        return kindByte switch
        {
            (byte)WeaponPresentationKind.Cannon => WeaponPresentationKind.Cannon,
            (byte)WeaponPresentationKind.MachineGun => WeaponPresentationKind.MachineGun,
            _ => WeaponPresentationKind.None
        };
    }

    public static bool IsMachineGun(ushort presentationId) =>
        GetKind(presentationId) == WeaponPresentationKind.MachineGun;

    public static bool IsCannon(ushort presentationId) =>
        GetKind(presentationId) == WeaponPresentationKind.Cannon;
}

public readonly record struct AccuracyPoint(float Range, float HitProbability);

/// <summary>
/// Data-driven weapon specification for direct-fire combat.
/// </summary>
public sealed record WeaponDefinition(
    string Id,
    float Range,
    int ReloadDurationTicks,
    int Damage,
    int AmmoCapacity,
    float MuzzleHeight,
    ushort PresentationId,
    float PenetrationNear = float.MaxValue,
    float PenetrationFar = float.MaxValue,
    float PenetrationNearRange = 0f,
    float PenetrationFarRange = 0f,
    int RoundsPerBurst = 1,
    AccuracyPoint[]? AccuracyCurve = null,
    float DispersionMils = 3.0f,
    float TargetMovementPenalty = 0.20f,
    float RangingBonus = 0.08f,
    float MaxRangingBonus = 0.24f)
{
    public float Dispersion => DispersionMils;

    public float GetAccuracyAt(float distance)
    {
        if (AccuracyCurve is null || AccuracyCurve.Length == 0)
            return 1.0f;

        if (distance <= AccuracyCurve[0].Range)
            return AccuracyCurve[0].HitProbability;

        if (distance >= AccuracyCurve[^1].Range)
            return AccuracyCurve[^1].HitProbability;

        for (int i = 0; i < AccuracyCurve.Length - 1; i++)
        {
            AccuracyPoint p0 = AccuracyCurve[i];
            AccuracyPoint p1 = AccuracyCurve[i + 1];
            if (distance >= p0.Range && distance <= p1.Range)
            {
                if (p1.Range <= p0.Range)
                    return p0.HitProbability;

                float t = (distance - p0.Range) / (p1.Range - p0.Range);
                return p0.HitProbability + t * (p1.HitProbability - p0.HitProbability);
            }
        }

        return AccuracyCurve[^1].HitProbability;
    }

    public float GetPenetrationAt(float distance)
    {
        if (PenetrationNear == float.MaxValue && PenetrationFar == float.MaxValue)
            return float.MaxValue;

        float farRange = PenetrationFarRange > PenetrationNearRange ? PenetrationFarRange : Range;
        if (distance <= PenetrationNearRange || farRange <= PenetrationNearRange)
            return PenetrationNear;
        if (distance >= farRange)
            return PenetrationFar;

        float t = (distance - PenetrationNearRange) / (farRange - PenetrationNearRange);
        return PenetrationNear + t * (PenetrationFar - PenetrationNear);
    }
}

/// <summary>
/// Combat attributes of a unit definition including health and weapon reference.
/// </summary>
public readonly record struct UnitCombatProfile(
    int MaxHealth,
    WeaponDefinition? Weapon,
    float TurretTraverseRate = 0f,
    UnitArmor Armor = default,
    float Radius = 1.0f);

/// <summary>
/// Catalog interface providing combat profiles to combat systems.
/// </summary>
public interface IUnitCombatCatalog
{
    bool TryGetProfile(ushort definition, out UnitCombatProfile profile);
}
