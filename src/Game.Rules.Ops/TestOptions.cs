using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>Setup switches for local test sessions; never used by matches.</summary>
public static class TestOptions
{
    /// <summary>
    /// Every unit not owned by the given player holds fire, so its own units can be tried out
    /// without losing them (for example to watch turret traverse).
    /// </summary>
    public static int HoldFireForOthers(Simulation simulation, int player)
    {
        WorldState world = simulation.World;
        int changed = 0;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            if (unit.Owner != player && world.SetFireStance(unit.Id, FireStance.HoldFire)) changed++;
        }
        return changed;
    }
}
