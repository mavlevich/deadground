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
    MachineGun = 2,
    Rifle = 3,
    RocketArtillery = 4,
    SurfaceToAirMissile = 5,
    RifleAk = 6,
    AntiTankRocket = 7
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
            (byte)WeaponPresentationKind.Rifle => WeaponPresentationKind.Rifle,
            (byte)WeaponPresentationKind.RocketArtillery => WeaponPresentationKind.RocketArtillery,
            (byte)WeaponPresentationKind.SurfaceToAirMissile => WeaponPresentationKind.SurfaceToAirMissile,
            (byte)WeaponPresentationKind.RifleAk => WeaponPresentationKind.RifleAk,
            (byte)WeaponPresentationKind.AntiTankRocket => WeaponPresentationKind.AntiTankRocket,
            _ => WeaponPresentationKind.None
        };
    }

    public static bool IsMachineGun(ushort presentationId) =>
        GetKind(presentationId) == WeaponPresentationKind.MachineGun;

    public static bool IsCannon(ushort presentationId) =>
        GetKind(presentationId) == WeaponPresentationKind.Cannon;

    public static bool IsRifle(ushort presentationId) =>
        GetKind(presentationId) is WeaponPresentationKind.Rifle or WeaponPresentationKind.RifleAk;

    public static bool IsSovietRifle(ushort presentationId) =>
        GetKind(presentationId) == WeaponPresentationKind.RifleAk;

    public static bool IsAntiTankRocket(ushort presentationId) =>
        GetKind(presentationId) == WeaponPresentationKind.AntiTankRocket;

    public static bool IsRocketArtillery(ushort presentationId) =>
        GetKind(presentationId) == WeaponPresentationKind.RocketArtillery;

    public static bool IsSurfaceToAirMissile(ushort presentationId) =>
        GetKind(presentationId) == WeaponPresentationKind.SurfaceToAirMissile;
}

public readonly record struct AccuracyPoint(float Range, float HitProbability);

/// <summary>
/// Data-driven weapon specification for direct-fire and indirect-fire combat.
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
    float MaxRangingBonus = 0.24f,
    bool CanTargetAir = false,
    float MaxAirTargetAltitude = 0f,
    bool IsIndirectFire = false,
    float MinRange = 0f,
    int SalvoCount = 1,
    int SalvoIntervalTicks = 1,
    float DispersionRadius = 0f,
    float SplashRadius = 0f,
    float ProjectileSpeed = ArtilleryBallistics.DefaultRocketSpeed,
    int SetupTicks = 0,
    float ElevationDegrees = 0f,
    MissileGuidanceKind GuidanceKind = MissileGuidanceKind.None,
    bool AirTargetsOnly = false,
    float MissileMaxTurnRateDegPerSec = 0f,
    float MissileProximityFuzeRadius = 6.0f,
    float IrCountermeasureResistance = 0f,
    int MissileMaxFlightTicks = 160,
    int MissileMotorBurnTicks = 50,
    int StructuralDamage = 0,
    float MovingAccuracyMultiplier = 0f)
{
    public bool IsGuidedMissile => GuidanceKind != MissileGuidanceKind.None;
    public bool IsAntiTankRocket => WeaponPresentation.IsAntiTankRocket(PresentationId);
    public bool CanFireOnMove => MovingAccuracyMultiplier > 0f && !IsIndirectFire && !IsGuidedMissile;
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
    float Radius = 1.0f,
    bool LeavesWreck = true,
    int ContainerDestructionDamage = 50);

/// <summary>
/// Catalog interface providing combat profiles to combat systems.
/// </summary>
public interface IUnitCombatCatalog
{
    bool TryGetProfile(ushort definition, out UnitCombatProfile profile);
}
