namespace DeadGround.Engine;

public interface ISimulationSystem
{
    void Update(WorldState world, int tick);
}

public class RuleSet : ISimulationRules, IRandomStreamsAware
{
    private readonly ISimulationSystem[] _systems;

    public RuleSet(params ISimulationSystem[] systems)
    {
        ArgumentNullException.ThrowIfNull(systems);
        foreach (ISimulationSystem system in systems) ArgumentNullException.ThrowIfNull(system);
        _systems = (ISimulationSystem[])systems.Clone();
    }

    public void InitializeRandomStreams(IRandomStreamProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        for (int i = 0; i < _systems.Length; i++)
        {
            if (_systems[i] is IRandomStreamsAware aware)
            {
                aware.InitializeRandomStreams(provider);
            }
        }
    }

    public void Update(WorldState world, int tick)
    {
        foreach (ISimulationSystem system in _systems) system.Update(world, tick);
    }

    public T? FindSystem<T>() where T : class
    {
        for (int i = 0; i < _systems.Length; i++)
        {
            if (_systems[i] is T match) return match;
        }
        return null;
    }
}
