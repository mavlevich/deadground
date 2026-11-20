using System.Numerics;

namespace DeadGround.Engine;

public readonly record struct SmokeCloudId(uint Value);

public enum SmokeDeliveryKind : byte
{
    None = 0,
    EngineScreen = 1,
    Discharger = 2,
    Grenade = 3
}

/// <summary>
/// Data-driven tactical smoke capability for a unit definition.
/// Map-agnostic and model-agnostic: any unit type declaring a valid profile can deploy smoke.
/// </summary>
public readonly record struct SmokeProfile(
    SmokeDeliveryKind Kind,
    int Charges,
    int CooldownTicks,
    int EmitDurationTicks,
    int EmitIntervalTicks,
    float MinCloudSpacing,
    float CloudRadius,
    float CloudHeight,
    int CloudDurationTicks,
    int BloomTicks,
    int FadeTicks,
    float OpticalDensity,
    float ExhaustOffset,
    float Range)
{
    public bool IsValid =>
        Kind != SmokeDeliveryKind.None &&
        Charges > 0 &&
        CooldownTicks >= 0 &&
        CloudRadius > 0f &&
        CloudHeight > 0f &&
        CloudDurationTicks > 0 &&
        BloomTicks >= 0 &&
        FadeTicks >= 0 &&
        BloomTicks + FadeTicks <= CloudDurationTicks &&
        OpticalDensity > 0f &&
        MinCloudSpacing >= 0f &&
        Range >= 0f &&
        (Kind != SmokeDeliveryKind.EngineScreen || (EmitDurationTicks > 0 && EmitIntervalTicks > 0));
}

public interface IUnitSmokeCatalog
{
    bool TryGetSmokeProfile(ushort definition, out SmokeProfile profile);
}

public readonly record struct DeploySmokeCommand(
    uint Sequence,
    int ClientTick,
    EntityId Unit,
    bool HasTarget = false,
    Vector2 Target = default,
    uint TargetSurface = 0);

/// <summary>
/// Authoritative simulation state for a single active smoke cloud volume.
/// </summary>
public readonly record struct SmokeCloudState(
    SmokeCloudId Id,
    int Owner,
    Vector2 Position,
    uint SupportSurface,
    float Radius,
    float Height,
    int CreatedTick,
    int ExpiresTick,
    ushort BloomTicks,
    ushort FadeTicks,
    float OpticalDensity)
{
    public bool IsExpired(int tick) => tick >= ExpiresTick;

    public void EvaluateAtTick(int tick, out float effectiveRadius, out float effectiveDensity)
    {
        SmokeCloudEvaluation.Evaluate(
            tick,
            CreatedTick,
            ExpiresTick,
            BloomTicks,
            FadeTicks,
            Radius,
            OpticalDensity,
            out effectiveRadius,
            out effectiveDensity);
    }

    public SmokeCloudObservation ToObservation() =>
        new(Id, Position, SupportSurface, Radius, Height, CreatedTick, ExpiresTick, BloomTicks, FadeTicks, OpticalDensity);
}

/// <summary>
/// Permitted observation of a smoke cloud sent to clients.
/// Contains no owner player ID and no source EntityId.
/// </summary>
public readonly record struct SmokeCloudObservation(
    SmokeCloudId Id,
    Vector2 Position,
    uint SupportSurface,
    float Radius,
    float Height,
    int CreatedTick,
    int ExpiresTick,
    ushort BloomTicks,
    ushort FadeTicks,
    float OpticalDensity)
{
    public void EvaluateAtTick(int tick, out float effectiveRadius, out float effectiveDensity)
    {
        SmokeCloudEvaluation.Evaluate(
            tick,
            CreatedTick,
            ExpiresTick,
            BloomTicks,
            FadeTicks,
            Radius,
            OpticalDensity,
            out effectiveRadius,
            out effectiveDensity);
    }
}

/// <summary>
/// Allocation-free precomputed 3D vertical cylinder volume used in line-of-sight queries.
/// </summary>
public readonly record struct SmokeVolume(
    float CenterX,
    float CenterZ,
    float BaseY,
    float TopY,
    float Radius,
    float OpticalDensity,
    SmokeCloudId Id);

public static class SmokeCloudEvaluation
{
    public const float MinBloomRadiusFraction = 0.25f;

    public static void Evaluate(
        int tick,
        int createdTick,
        int expiresTick,
        int bloomTicks,
        int fadeTicks,
        float maxRadius,
        float maxOpticalDensity,
        out float effectiveRadius,
        out float effectiveDensity)
    {
        if (tick < createdTick || tick >= expiresTick)
        {
            effectiveRadius = 0f;
            effectiveDensity = 0f;
            return;
        }

        int age = tick - createdTick;
        int remaining = expiresTick - tick;

        float radiusFactor = 1f;
        float densityFactor = 1f;

        if (bloomTicks > 0 && age < bloomTicks)
        {
            float bloomT = (float)age / bloomTicks;
            radiusFactor = MinBloomRadiusFraction + (1f - MinBloomRadiusFraction) * bloomT;
            densityFactor = bloomT;
        }

        if (fadeTicks > 0 && remaining < fadeTicks)
        {
            float fadeT = (float)remaining / fadeTicks;
            if (fadeT < densityFactor)
            {
                densityFactor = fadeT;
            }
        }

        effectiveRadius = maxRadius * radiusFactor;
        effectiveDensity = maxOpticalDensity * densityFactor;
    }
}
