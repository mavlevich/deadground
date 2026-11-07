using System.Numerics;

namespace DeadGround.Engine;

public readonly record struct WreckState(
    WreckId Id,
    ushort Definition,
    Vector2 Position,
    uint SupportSurface,
    float HullHeading,
    float TurretYaw,
    int DestroyedTick,
    int OriginalOwner)
{
    public WreckObservation ToObservation() => new(
        Id,
        Definition,
        Position,
        SupportSurface,
        HullHeading,
        TurretYaw);
}

public readonly record struct WreckObservation(
    WreckId Id,
    ushort Definition,
    Vector2 Position,
    uint SupportSurface,
    float HullHeading,
    float TurretYaw);
