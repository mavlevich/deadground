using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;

namespace DeadGround.Client;

// Local composition root, never used by a network player. Two ordinary clients share one host.
internal sealed class TankBattleDemoSession : IDisposable
{
    private readonly SimulationHost _host;
    private readonly Simulation _simulation;
    public ClientSession Soviet { get; }
    public ClientSession Us { get; }
    public TankBattleDirector Director { get; }
    public int Tick => _host.Tick;
    public double TickMilliseconds => _host.LastTickMilliseconds;
    public long Shots => _simulation.Combat?.TotalShotsFired ?? 0;

    public TankBattleDemoSession(StaticGeometry geometry, UnitCatalog catalog, ulong seed)
    {
        _simulation = TankBattleScenario.CreateSimulation(geometry, catalog, seed);
        Director = ((OpsRules)_simulation.Rules).FindSystem<TankBattleDirector>()!;
        InMemoryServerTransport transport = new(2);
        MatchSetup setup = TankBattleScenario.CreateMatchSetup(geometry, catalog);
        _host = new SimulationHost(_simulation, transport, setup);
        Soviet = new ClientSession(transport.CreateClientTransport(1), setup);
        Us = new ClientSession(transport.CreateClientTransport(2), setup);
        Advance(0);
    }

    public void Advance(double delta)
    {
        _host.Advance(delta);
        Soviet.Advance(delta);
        Us.Advance(delta);
    }

    public void Dispose()
    {
        Soviet.Dispose();
        Us.Dispose();
        _host.Dispose();
    }
}
