using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Shared deterministic ballistic model for aircraft M130 infrared decoy flare salvos.
/// Used identically by both the authoritative server (GuidedMissileSystem) and the
/// presentation client (CombatEffects) so guided SAMs track and intercept the exact
/// burning flares rendered in 3D space without streaming per-flare network state.
/// </summary>
public static class FlareBallistics
{
    public const int PairsPerBurst = 4;
    public const int FlaresPerBurst = PairsPerBurst * 2;
    public const int PairIntervalTicks = 4; // 0.20s between twin cartridge pairs at 20 Hz
    public const float PairIntervalSeconds = PairIntervalTicks * Simulation.TickSeconds;
    public const float DragRate = 0.42f;
    public const float Gravity = 11.2f;
    public const float MaxFallSpeed = 18.0f;

    /// <summary>
    /// Computes deterministic ejection delay, spawn position, initial velocity, and burn duration
    /// for one flare cartridge (index 0..7) in a staggered 4-pair M130 salvo.
    /// </summary>
    public static void ComputeFlareLaunch(
        Vector3 burstOrigin,
        float headingRad,
        Vector2 aircraftVelocity2D,
        int flareIndex,
        out float delaySeconds,
        out Vector3 spawnPos,
        out Vector3 initialVelocity,
        out float burnDurationSeconds)
    {
        int pairIdx = flareIndex / 2;
        float side = (flareIndex % 2 == 0) ? 1f : -1f;
        delaySeconds = pairIdx * PairIntervalSeconds;

        // Forward vector matching Godot / FlightSystem yaw convention (-sin(yaw), 0, -cos(yaw))
        Vector3 forward = new(-MathF.Sin(headingRad), 0f, -MathF.Cos(headingRad));
        Vector3 right = new(MathF.Cos(headingRad), 0f, -MathF.Sin(headingRad));
        Vector3 aircraftVel3D = new(aircraftVelocity2D.X, 0f, aircraftVelocity2D.Y);

        // Aircraft continues along its velocity vector during the 0.60s staggered ejection sequence
        Vector3 aircraftPosAtRelease = burstOrigin + aircraftVel3D * delaySeconds;

        // Fixed angled M130 aft-sponson dispenser ejection vectors creating a wide curtain away from the fuselage
        float lateralSpeed = (24.0f + pairIdx * 3.5f) * side;
        float aftSpeed = -(8.5f + pairIdx * 2.0f);
        float downSpeed = -(4.5f + pairIdx * 1.4f);

        spawnPos = aircraftPosAtRelease
            + right * (side * 1.75f)
            + forward * (-2.2f - pairIdx * 0.35f)
            + new Vector3(0f, -1.15f, 0f);

        initialVelocity = aircraftVel3D * 0.55f
            + right * lateralSpeed
            + forward * aftSpeed
            + new Vector3(0f, downSpeed, 0f);

        burnDurationSeconds = 2.45f + (pairIdx * 0.14f) + (side > 0f ? 0.08f : 0f);
    }

    /// <summary>
    /// Advances a single falling decoy flare by dt seconds using identical drag and gravity.
    /// </summary>
    public static void StepFlare(ref Vector3 position, ref Vector3 velocity, float dt, float groundY)
    {
        float drag = MathF.Exp(-DragRate * dt);
        velocity.X *= drag;
        velocity.Z *= drag;
        velocity.Y = MathF.Max(-MaxFallSpeed, velocity.Y - Gravity * dt);

        position += velocity * dt;
        float minY = groundY + 0.35f;
        if (position.Y < minY)
        {
            position.Y = minY;
            velocity *= 0.2f;
        }
    }
}
