using System.Numerics;

namespace DeadGround.Sandbox;

// Presentation geometry, independent of the simulation and renderer.
public sealed class OrbitCameraPose
{
    public static readonly float DefaultPitch = MathF.Atan2(115f, 80f);
    public const float MinPitch = 8f * MathF.PI / 180f;
    public const float MaxPitch = 85f * MathF.PI / 180f;
    public float Yaw { get; private set; }
    public float Pitch { get; private set; } = DefaultPitch;
    public Vector3 OffsetDirection => new(MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch),
        MathF.Cos(Yaw) * MathF.Cos(Pitch));

    public void Rotate(float yawDelta, float pitchDelta)
    {
        if (!float.IsFinite(yawDelta) || !float.IsFinite(pitchDelta)) return;
        Yaw = MathF.IEEERemainder(Yaw + yawDelta, MathF.Tau);
        Pitch = Math.Clamp(Pitch + pitchDelta, MinPitch, MaxPitch);
    }

    // Moving the mouse right/down orbits right and tilts down toward the ground.
    public void Drag(float right, float down) => Rotate(right * 0.005f, down * 0.004f);

    public void Reset() { Yaw = 0f; Pitch = DefaultPitch; }

    public Vector2 GroundInput(float right, float down) =>
        new(right * MathF.Cos(Yaw) + down * MathF.Sin(Yaw), -right * MathF.Sin(Yaw) + down * MathF.Cos(Yaw));

    // Keep every orthographic view row in front of the camera, including low viewing angles.
    public static float Distance(float size, float pitch) =>
        MathF.Max(140f, size * 0.5f / MathF.Tan(pitch) + 150f / MathF.Sin(pitch));
}
