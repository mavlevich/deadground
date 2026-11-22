using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Guidance seeker kind for guided missiles (SAMs and future ATGMs).
/// </summary>
public enum MissileGuidanceKind : byte
{
    None = 0,
    Infrared = 1,
    Radar = 2,
    Optical = 3
}

/// <summary>
/// Runtime bitflags replicated to clients for aircraft flare countermeasures and missile warnings.
/// </summary>
[Flags]
public enum CountermeasureFlags : byte
{
    None = 0,
    HasFlares = 1 << 0,
    FlaresActive = 1 << 1,
    AutoFlares = 1 << 2,
    MissileWarning = 1 << 3
}

/// <summary>
/// Replicated flags for an in-flight guided missile.
/// </summary>
[Flags]
public enum GuidedMissileFlags : byte
{
    None = 0,
    MotorBurning = 1 << 0,
    Decoyed = 1 << 1
}

/// <summary>
/// Player command to manually deploy flare countermeasures or toggle automatic flare release.
/// </summary>
public readonly record struct DeployFlaresCommand(
    uint Sequence,
    int ClientTick,
    EntityId Unit,
    bool ToggleAuto = false);

/// <summary>
/// Data-driven infrared flare dispenser profile for an aircraft unit definition.
/// </summary>
public readonly record struct FlareProfile(
    byte Charges,
    ushort CooldownTicks,
    ushort ActiveDurationTicks,
    float DecoyRadius = 65f,
    float IrSpoofProbability = 0.62f,
    bool DefaultAutoFlares = true)
{
    public bool IsValid =>
        Charges > 0 &&
        ActiveDurationTicks > 0 &&
        float.IsFinite(DecoyRadius) && DecoyRadius > 0f &&
        float.IsFinite(IrSpoofProbability) && IrSpoofProbability >= 0f && IrSpoofProbability <= 1f;
}

/// <summary>
/// Catalog interface providing countermeasure profiles to simulation systems.
/// </summary>
public interface IUnitCountermeasureCatalog
{
    bool TryGetFlareProfile(ushort definition, out FlareProfile profile);
}

/// <summary>
/// Authoritative in-flight guided missile state tracked inside WorldState.
/// </summary>
public struct GuidedMissileState
{
    public uint MissileId;
    public int Owner;
    public EntityId ShooterId;
    public ushort ShooterDefinition;
    public EntityId TargetId;
    public ushort PresentationId;
    public MissileGuidanceKind Guidance;
    public Vector3 Position;
    public Vector3 Velocity;
    public float Speed;
    public float MaxTurnRateRadPerSec;
    public int Damage;
    public float SplashRadius;
    public float ProximityFuzeRadius;
    public float IrCountermeasureResistance;
    public int LaunchedTick;
    public int MaxFlightTicks;
    public int MotorBurnTicks;
    public bool Decoyed;
    public uint DecoyFlareId;
    public Vector3 DecoyTargetPos;
    public int FlareEvaluatedBurstTick;
}

/// <summary>
/// Compact replicated observation of an active guided missile for smooth client interpolation.
/// Contains only public observable flight state (never shooter/target truth IDs).
/// </summary>
public readonly record struct GuidedMissileObservation(
    uint MissileId,
    ushort PresentationId,
    Vector3 Position,
    Vector3 Velocity,
    GuidedMissileFlags Flags);
