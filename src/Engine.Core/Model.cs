using System.Numerics;

namespace DeadGround.Engine;

public readonly record struct EntityId(uint Value);

public enum MoveMode : byte
{
    Forward = 0,
    Reverse = 1
}

public readonly record struct MoveCommand(
    uint Sequence,
    int ClientTick,
    EntityId Unit,
    Vector2 Target,
    float? FinalFacing = null,
    MoveMode Mode = MoveMode.Forward,
    uint TargetSurface = 0);

public readonly record struct GroupMoveCommand(
    uint Sequence,
    int ClientTick,
    ReadOnlyMemory<EntityId> Units,
    Vector2 Target,
    uint TargetSurface = 0,
    float? FinalFacing = null,
    MoveMode Mode = MoveMode.Forward);

public readonly record struct GroupMoveAssignment(
    EntityId Unit,
    Vector2 Target,
    uint TargetSurface = 0,
    float? FinalFacing = null);

public readonly record struct GroupMoveUnplaced(
    EntityId Unit,
    DestinationBlockReason Reason);

public readonly record struct GroupMoveResult(
    uint Sequence,
    int ServerTick,
    Vector2 Target,
    uint TargetSurface,
    MoveMode Mode,
    ReadOnlyMemory<GroupMoveAssignment> Placed,
    ReadOnlyMemory<GroupMoveUnplaced> Unplaced);

public readonly record struct AttackCommand(uint Sequence, int ClientTick, EntityId Unit, ContactId Target);

public readonly record struct StopCommand(uint Sequence, int ClientTick, EntityId Unit);

public readonly record struct BoardTransportCommand(
    uint Sequence,
    int ClientTick,
    EntityId Unit,
    EntityId Transport);

public readonly record struct DisembarkCommand(
    uint Sequence,
    int ClientTick,
    EntityId Transport,
    Vector2 Target,
    uint TargetSurface = 0);

public readonly record struct DisembarkResult(
    uint Sequence,
    int ServerTick,
    EntityId Transport,
    byte DisembarkedCount,
    byte RemainingCount,
    bool Success);

public enum FireStance : byte
{
    FireAtWill = 0,
    ReturnFire = 1,
    HoldFire = 2
}

public readonly record struct SetFireStanceCommand(uint Sequence, int ClientTick, EntityId Unit, FireStance Stance);

public enum AttackExecutionStatus : byte
{
    None = 0,
    Aiming = 1,
    OutOfRange = 2,
    Obstructed = 3,
    NoAmmo = 4,
    TargetLost = 5
}

public enum CombatEventKind : byte
{
    None = 0,
    ShotFired = 1,
    UnitDestroyed = 2,
    TookFire = 3,
    RocketLaunched = 4,
    ArtilleryImpact = 5
}

public enum HitResult : byte
{
    None = 0,
    Penetrated = 1,
    Deflected = 2,
    Miss = 3
}

public readonly record struct CombatEventObservation(
    uint EventId,
    int ServerTick,
    CombatEventKind Kind,
    ushort PresentationId,
    Vector3 Origin,
    Vector3 Target,
    EntityId ShooterUnitId = default,
    HitResult Result = HitResult.None)
{
    public WreckId WreckId => new(ShooterUnitId.Value);

    public CombatEventObservation(
        uint eventId,
        int serverTick,
        CombatEventKind kind,
        ushort presentationId,
        Vector3 origin,
        Vector3 target,
        WreckId wreckId)
        : this(eventId, serverTick, kind, presentationId, origin, target, new EntityId(wreckId.Value), HitResult.None)
    {
    }
}

public readonly record struct UnitState(
    EntityId Id,
    int Owner,
    ushort Definition,
    Vector2 Position,
    Vector2 Target,
    bool Moving,
    uint SupportSurface = 0);

// This DTO only represents a player's own unit. Enemy contacts will have separate IDs.
public readonly record struct OwnUnitObservation(
    EntityId Id,
    ushort Definition,
    Vector2 Position,
    uint SupportSurface,
    PathRequestStatus NavStatus = PathRequestStatus.None,
    ushort Health = 100,
    ushort Ammunition = 0,
    ushort ReloadTicksRemaining = 0,
    AttackExecutionStatus AttackStatus = AttackExecutionStatus.None,
    ContactId TargetContactId = default,
    FireStance Stance = FireStance.FireAtWill,
    float TurretYaw = 0f,
    float HullHeading = 0f,
    uint NavOrderSequence = 0,
    EntityId ContainerId = default,
    byte PassengerCount = 0,
    byte PassengerCapacity = 0,
    byte SmokeCharges = 0,
    ushort SmokeCooldownTicks = 0,
    bool HasSmokeCapability = false,
    bool SmokeActive = false,
    float Altitude = 0f,
    FlightPhase FlightPhase = FlightPhase.Ground,
    AltitudeBand AltitudeBand = AltitudeBand.High,
    float PitchRad = 0f,
    float RollRad = 0f,
    bool HasFlightCapability = false,
    EntityId SlingLoadId = default,
    EntityId SlungById = default,
    FlightStateFlags FlightFlags = FlightStateFlags.None,
    float GunPitchRad = 0f,
    ArtilleryFlags ArtilleryFlags = ArtilleryFlags.None);

public interface ISimulationRules
{
    void Update(WorldState world, int tick);
}
