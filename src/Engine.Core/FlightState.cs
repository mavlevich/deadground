using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Authoritative flight phase of an aircraft unit.
/// </summary>
public enum FlightPhase : byte
{
    Ground = 0,
    TakingOff = 1,
    Airborne = 2,
    Landing = 3,
    Crashing = 4
}

/// <summary>
/// Tactical flight altitude band above terrain (AGL).
/// </summary>
public enum AltitudeBand : byte
{
    Low = 0,
    High = 1
}

/// <summary>
/// Player order kind targeting a helicopter or rotorcraft.
/// </summary>
public enum HelicopterOrderKind : byte
{
    TakeOff = 0,
    Land = 1,
    SetAltitudeBand = 2
}

/// <summary>
/// Explicit helicopter command submitted by a player.
/// </summary>
public readonly record struct HelicopterCommand(
    uint Sequence,
    int ClientTick,
    EntityId Unit,
    HelicopterOrderKind Kind,
    AltitudeBand AltitudeBand = AltitudeBand.High,
    bool HasTarget = false,
    Vector2 Target = default);

/// <summary>
/// Data-driven flight performance, landing zone, and crash profile for an aircraft unit definition.
/// Map-agnostic and model-agnostic: any unit declaring a valid flight profile flies through FlightSystem.
/// </summary>
public readonly record struct FlightProfile(
    float CruiseSpeed,
    float LowAltitudeSpeed,
    float Acceleration,
    float Deceleration,
    float YawRateDegPerSec,
    float ClimbRate,
    float DescentRate,
    float LowAltitude,
    float HighAltitude,
    float LandingClearanceRadius = 10.0f,
    float MaxLandingSlopeDegrees = 12.0f,
    float CrashDescentRate = 14.0f,
    float CrashSpinRateDegPerSec = 180.0f,
    float CrashSplashRadius = 12.0f,
    int CrashSplashDamage = 60,
    bool StartAirborne = false)
{
    public bool IsValid =>
        float.IsFinite(CruiseSpeed) && CruiseSpeed > 0f &&
        float.IsFinite(LowAltitudeSpeed) && LowAltitudeSpeed > 0f &&
        float.IsFinite(Acceleration) && Acceleration > 0f &&
        float.IsFinite(Deceleration) && Deceleration > 0f &&
        float.IsFinite(YawRateDegPerSec) && YawRateDegPerSec > 0f &&
        float.IsFinite(ClimbRate) && ClimbRate > 0f &&
        float.IsFinite(DescentRate) && DescentRate > 0f &&
        float.IsFinite(LowAltitude) && LowAltitude > 0f &&
        float.IsFinite(HighAltitude) && HighAltitude >= LowAltitude &&
        float.IsFinite(LandingClearanceRadius) && LandingClearanceRadius > 0f &&
        float.IsFinite(MaxLandingSlopeDegrees) && MaxLandingSlopeDegrees > 0f &&
        float.IsFinite(CrashDescentRate) && CrashDescentRate > 0f;

    public float GetTargetAltitude(AltitudeBand band) =>
        band == AltitudeBand.Low ? LowAltitude : HighAltitude;

    public float GetMaxSpeed(AltitudeBand band) =>
        band == AltitudeBand.Low ? LowAltitudeSpeed : CruiseSpeed;
}

/// <summary>
/// Catalog interface providing flight profiles to simulation systems.
/// </summary>
public interface IUnitFlightCatalog
{
    bool TryGetFlightProfile(ushort definition, out FlightProfile profile);
}

/// <summary>
/// Reason why a candidate helicopter landing zone is blocked.
/// </summary>
public enum LandingZoneStatus : byte
{
    Valid = 0,
    OutOfBounds = 1,
    BlockedByWater = 2,
    BlockedByStructure = 3,
    BlockedByForest = 4,
    SlopeTooSteep = 5,
    BlockedByUnit = 6,
    BlockedByWreck = 7
}
