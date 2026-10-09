using System.Numerics;
using Arch.Core;

namespace DeadGround.Engine;

internal record struct Position(Vector2 Value);
internal record struct Motion(Vector2 Target, float Speed, bool Active);

public sealed class MovementSystem : ISimulationSystem
{
    private readonly QueryDescription _query = new QueryDescription().WithAll<Position, Motion>();

    public void Update(WorldState world, int tick) =>
        world.Storage.InlineQuery<MoveUnits, Position, Motion>(in _query);

    private struct MoveUnits : IForEach<Position, Motion>
    {
        public void Update(ref Position position, ref Motion motion)
        {
            if (!motion.Active) return;
            Vector2 offset = motion.Target - position.Value;
            float distance = offset.Length();
            float step = motion.Speed * Simulation.TickSeconds;
            if (distance <= step)
            {
                position.Value = motion.Target;
                motion.Active = false;
            }
            else position.Value += offset * (step / distance);
        }
    }
}
