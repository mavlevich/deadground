using System.Numerics;

namespace DeadGround.Engine;

public readonly record struct EntityId(uint Value);

public readonly record struct MoveCommand(uint Sequence, int ClientTick, EntityId Unit, Vector2 Target);

public readonly record struct AttackCommand(uint Sequence, int ClientTick, EntityId Unit, ContactId Target);

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
    TookFire = 3
}

public enum HitResult : byte
{
    None = 0,
    Penetrated = 1,
    Deflected = 2
}

public readonly record struct CombatEventObservation(
    uint EventId,
    int ServerTick,
    CombatEventKind Kind,
    ushort PresentationId,
    Vector3 Origin,
    Vector3 Target,
    EntityId ShooterUnitId = default,
    HitResult Result = HitResult.None);

public readonly record struct UnitState(EntityId Id, int Owner, ushort Definition,
    Vector2 Position, Vector2 Target, bool Moving);

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
    float HullHeading = 0f);

public interface ISimulationRules
{
    void Update(WorldState world, int tick);
}
