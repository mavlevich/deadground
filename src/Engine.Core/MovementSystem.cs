using System.Numerics;
using Arch.Core;

namespace DeadGround.Engine;

internal record struct Position(Vector2 Value);
internal record struct Motion(Vector2 Target, float Speed, bool Active, uint SupportSurface = 0);

public sealed class MovementSystem : ISimulationSystem
{
    private readonly QueryDescription _query = new QueryDescription().WithAll<Position, Motion, WorldState.CombatState>();

    public void Update(WorldState world, int tick)
    {
        MoveUnits mover = new(tick);
        world.Storage.InlineQuery<MoveUnits, Position, Motion, WorldState.CombatState>(in _query, ref mover);
    }

    private struct MoveUnits : IForEach<Position, Motion, WorldState.CombatState>
    {
        private readonly int _tick;

        public MoveUnits(int tick)
        {
            _tick = tick;
        }

        public void Update(ref Position position, ref Motion motion, ref WorldState.CombatState combat)
        {
            if (!motion.Active) return;
            Vector2 offset = motion.Target - position.Value;
            float distance = offset.Length();
            float step = motion.Speed * Simulation.TickSeconds;
            Vector2 next = distance <= step ? motion.Target : position.Value + offset * (step / distance);
            Vector2 delta = next - position.Value;
            if (delta.LengthSquared() > 0.0001f)
            {
                WorldState.TurnHullAlong(ref combat, delta);
                combat.LastMovedTick = _tick;
                combat.ConsecutiveShotsOnTarget = 0;
            }
            position.Value = next;
            if (distance <= step) motion.Active = false;
        }
    }
}
