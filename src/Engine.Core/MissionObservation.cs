using System.Numerics;

namespace DeadGround.Engine;

public enum MissionStatus : byte
{
    None = 0,
    InProgress = 1,
    Victory = 2,
    Defeat = 3
}

public enum MissionEndReason : byte
{
    None = 0,
    TargetDestroyed = 1,
    ReconVehicleDestroyed = 2,
    AllArmedUnitsDestroyed = 3,
    TimeLimitExpired = 4
}

public readonly record struct MissionObservation(
    ushort MissionId,
    MissionStatus Status,
    MissionEndReason EndReason,
    int TimeLimitTicks,
    int FinishedTick,
    ContactId TargetContactId,
    Vector2 SearchAreaCenter,
    float SearchAreaRadius,
    byte Flags)
{
    public const byte FlagTargetIdentified = 1;
    public const byte FlagTargetDestroyed = 2;

    public bool IsTargetIdentified => (Flags & FlagTargetIdentified) != 0;
    public bool IsTargetDestroyed => (Flags & FlagTargetDestroyed) != 0;
}

public interface IMissionSystem : ISimulationSystem
{
    bool IsFinished { get; }
    MissionObservation GetObservation(int player);
}
