using System.Numerics;

namespace DeadGround.Engine;

public readonly record struct EntityId(uint Value);

public readonly record struct MoveCommand(uint Sequence, int ClientTick, EntityId Unit, Vector2 Target);

public readonly record struct UnitState(EntityId Id, int Owner, ushort Definition,
    Vector2 Position, Vector2 Target, bool Moving);

// This DTO only represents a player's own unit. Enemy contacts will have separate IDs.
public readonly record struct OwnUnitObservation(EntityId Id, ushort Definition,
    Vector2 Position, uint SupportSurface, PathRequestStatus NavStatus = PathRequestStatus.None);

public interface ISimulationRules
{
    void Update(WorldState world, int tick);
}
