using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Authoritative area fire mission order for indirect-fire artillery units.
/// </summary>
public readonly record struct FireMissionCommand(
    uint Sequence,
    int ClientTick,
    EntityId Unit,
    Vector2 Target,
    bool Cancel = false);

[Flags]
public enum ArtilleryFlags : byte
{
    None = 0,
    SettingUp = 1 << 0,
    Firing = 1 << 1
}

/// <summary>
/// Active in-flight ballistic rocket tracked by the authoritative server simulation.
/// </summary>
public readonly record struct RocketProjectileState(
    EntityId ShooterId,
    ushort ShooterDefinition,
    int ShooterOwner,
    ushort PresentationId,
    Vector3 Origin,
    Vector3 Target,
    int LaunchTick,
    int ImpactTick,
    int Damage,
    float SplashRadius,
    float Penetration);

/// <summary>
/// Shared deterministic parabolic trajectory helper used by both server artillery
/// simulation and client 3D rocket presentation so visual impact matches server
/// impact tick-for-tick without streaming per-tick projectile positions.
/// </summary>
public static class ArtilleryBallistics
{
    public const float DefaultRocketSpeed = 160f;
    public const float MinApexHeight = 35f;
    public const float MaxApexHeight = 380f;
    public const float ArcHeightRatio = 0.22f;

    public static void ComputeTrajectory(
        Vector3 origin,
        Vector3 target,
        float speed,
        out int flightTicks,
        out float apexHeight)
    {
        float dx = target.X - origin.X;
        float dz = target.Z - origin.Z;
        float horizontalDist = MathF.Sqrt(dx * dx + dz * dz);

        apexHeight = Math.Clamp(horizontalDist * ArcHeightRatio, MinApexHeight, MaxApexHeight);
        float effectiveSpeed = speed > 1f ? speed : DefaultRocketSpeed;
        float approxArcLength = MathF.Sqrt(horizontalDist * horizontalDist + (2f * apexHeight) * (2f * apexHeight));
        float flightSeconds = MathF.Max(0.5f, approxArcLength / effectiveSpeed);
        flightTicks = Math.Max(10, (int)MathF.Round(flightSeconds * Simulation.TickRate));
    }

    public static void SamplePositionAndTangent(
        Vector3 origin,
        Vector3 target,
        float apexHeight,
        float normalizedTime,
        out Vector3 position,
        out Vector3 tangent)
    {
        float t = Math.Clamp(normalizedTime, 0f, 1f);
        float arcY = 4f * apexHeight * t * (1f - t);
        Vector3 linear = Vector3.Lerp(origin, target, t);
        position = new Vector3(linear.X, linear.Y + arcY, linear.Z);

        float dArcY = 4f * apexHeight * (1f - 2f * t);
        Vector3 dir = target - origin;
        Vector3 rawTangent = new(dir.X, dir.Y + dArcY, dir.Z);
        float lenSq = rawTangent.LengthSquared();
        tangent = lenSq > 1e-6f ? rawTangent / MathF.Sqrt(lenSq) : Vector3.UnitY;
    }
}
