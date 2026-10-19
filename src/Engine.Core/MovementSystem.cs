using System.Numerics;
using Arch.Core;

namespace DeadGround.Engine;

internal record struct Position(Vector2 Value);
internal record struct Motion(Vector2 Target, float Speed, bool Active);

public sealed class MovementSystem : ISimulationSystem
{
    private readonly QueryDescription _query = new QueryDescription().WithAll<Position, Motion, WorldState.CombatState>();

    public void Update(WorldState world, int tick) =>
        world.Storage.InlineQuery<MoveUnits, Position, Motion, WorldState.CombatState>(in _query);

    private struct MoveUnits : IForEach<Position, Motion, WorldState.CombatState>
    {
        public void Update(ref Position position, ref Motion motion, ref WorldState.CombatState combat)
        {
            if (!motion.Active) return;
            Vector2 offset = motion.Target - position.Value;
            float distance = offset.Length();
            float step = motion.Speed * Simulation.TickSeconds;
            Vector2 next = distance <= step ? motion.Target : position.Value + offset * (step / distance);
            WorldState.TurnHullAlong(ref combat, next - position.Value);
            position.Value = next;
            if (distance <= step) motion.Active = false;
        }
    }
}
