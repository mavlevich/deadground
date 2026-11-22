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
    SetAltitudeBand = 2,
    FastRope = 3,
    SlingPickup = 4,
    SlingDrop = 5
}

/// <summary>
/// Runtime bitflags replicated to clients for helicopter cargo and insertion visuals.
/// </summary>
[Flags]
public enum FlightStateFlags : byte
{
    None = 0,
    FastRopeActive = 1 << 0,
    SlingHookupActive = 1 << 1,
    SlingDropActive = 1 << 2
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
    Vector2 Target = default,
    EntityId TargetUnit = default);

/// <summary>
/// Data-driven transport cargo metadata for any unit definition (infantry, light vehicles, APCs, tanks).
/// </summary>
public readonly record struct CargoProfile(
    float WeightKg,
    int InternalCargoSlots,
    bool CanInternalLoad,
    bool CanSlingLoad,
    bool IsInfantry)
{
    public static CargoProfile DefaultInfantry => new(100f, 1, true, false, true);
    public static CargoProfile NonTransportable => new(0f, 0, false, false, false);
}

/// <summary>
/// Data-driven flight performance, landing zone, cargo ramp, fast-rope, sling-load, and crash profile
/// for an aircraft unit definition. Map-agnostic and model-agnostic.
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
    bool StartAirborne = false,
    bool CanLoadInternalVehicles = false,
    bool CanFastRope = false,
    float FastRopeAltitude = 18.0f,
    int FastRopeIntervalTicks = 10,
    float MaxSlingWeightKg = 0.0f,
    float SlingCableLength = 10.0f,
    float SlingSpeedPenaltyAtMax = 0.45f)
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

    public float GetSlingPerformanceMultiplier(float slingWeightKg)
    {
        if (slingWeightKg <= 0f || MaxSlingWeightKg <= 0f)
            return 1f;
        float ratio = Math.Clamp(slingWeightKg / MaxSlingWeightKg, 0f, 1f);
        float penalty = Math.Clamp(SlingSpeedPenaltyAtMax, 0f, 0.85f);
        return MathF.Max(0.25f, 1f - ratio * penalty);
    }
}

/// <summary>
/// Catalog interface providing flight and cargo transport profiles to simulation systems.
/// </summary>
public interface IUnitFlightCatalog
{
    bool TryGetFlightProfile(ushort definition, out FlightProfile profile);

    bool TryGetCargoProfile(ushort definition, out CargoProfile cargo)
    {
        cargo = CargoProfile.DefaultInfantry;
        return false;
    }
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

/// <summary>
/// Validation status for hover fast-rope infantry insertion.
/// Dense forests and steep hills are allowed; water and solid structures are blocked.
/// </summary>
public enum FastRopeZoneStatus : byte
{
    Valid = 0,
    OutOfBounds = 1,
    BlockedByWater = 2,
    BlockedByStructure = 3,
    NoInfantryAboard = 4
}

/// <summary>
/// Validation status for external sling-load vehicle drop zone.
/// Requires clearance for the vehicle footprint rather than the full helicopter rotor disc.
/// </summary>
public enum SlingDropZoneStatus : byte
{
    Valid = 0,
    OutOfBounds = 1,
    BlockedByWater = 2,
    BlockedByStructure = 3,
    BlockedByForest = 4,
    BlockedByUnit = 5,
    BlockedByWreck = 6
}
