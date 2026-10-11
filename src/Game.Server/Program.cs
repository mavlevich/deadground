using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;

int port = 7777;
int maxClients = 2;
bool smokeTest = false;
string scenario = "prototype";

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
    {
        port = p;
        i++;
    }
    else if (args[i] == "--max-clients" && i + 1 < args.Length && int.TryParse(args[i + 1], out int mc))
    {
        maxClients = mc;
        i++;
    }
    else if (args[i] == "--scenario" && i + 1 < args.Length)
    {
        scenario = args[i + 1].ToLowerInvariant();
        i++;
    }
    else if (args[i] == "--smoke-test")
    {
        smokeTest = true;
    }
}

HeightMap map = scenario == "visibility"
    ? VisibilityScenario.CreateMap()
    : HeightMap.CreatePrototype();

StaticGeometry? visibilityGeometry = scenario == "visibility"
    ? VisibilityScenario.CreateStaticGeometry()
    : null;

MatchSetup setup = scenario == "visibility"
    ? VisibilityScenario.CreateMatchSetup(visibilityGeometry!)
    : PrototypeScenario.CreateMatchSetup(map);

Simulation CreateSimulation() => scenario == "visibility"
    ? VisibilityScenario.CreateSimulation(visibilityGeometry!)
    : PrototypeScenario.Create(map);

if (smokeTest)
{
    using LocalSession session = new(CreateSimulation());
    if (scenario == "visibility")
    {
        for (int i = 0; i < 40; i++) session.Advance(1d / Simulation.TickRate);
        if (session.Tick != 40 || session.Replica.OwnUnits.Length != 2)
            throw new InvalidOperationException("Visibility smoke test failed.");
        Console.WriteLine($"VISIBILITY_SMOKE_OK tick={session.Tick} own_units={session.Replica.OwnUnits.Length} contacts={session.Replica.ContactCount}");
        return;
    }

    EntityId first = session.Replica.OwnUnits[0].Id;
    Vector2 start = session.Replica.OwnUnits[0].Position;
    for (int i = 0; i < session.Replica.OwnUnits.Length; i++)
    {
        OwnUnitObservation unit = session.Replica.OwnUnits[i];
        session.Move(unit.Id, unit.Position + new Vector2(0, 60));
    }
    for (int i = 0; i < 120; i++) session.Advance(1d / Simulation.TickRate);
    Vector2 end = session.Replica.SamplePosition(first, session.Replica.LatestTick);
    if (session.Tick != 120 || session.Replica.OwnUnits.Length != 12 || end.Y <= start.Y)
        throw new InvalidOperationException("M0 smoke test failed.");
    Console.WriteLine($"M0_SMOKE_OK tick={session.Tick} own_units={session.Replica.OwnUnits.Length} start={start} end={end}");
    return;
}

using LiteNetLibServerTransport transport = LiteNetLibTransport.CreateServer(port, maxClients);
using SimulationHost host = new(CreateSimulation(), transport, setup);

using CancellationTokenSource cancellation = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

Console.WriteLine($"Dead Ground dedicated server listening on UDP port {transport.Port}.");
Console.WriteLine($"Max players: {maxClients}. 20 Hz simulation, 10 Hz snapshots.");
Console.WriteLine($"Protocol: {setup.ProtocolVersion}, MapHash: {setup.MapHash:X16}, ContentHash: {setup.ContentHash:X16}, RuleSet: {setup.RuleSetId}.");
Console.WriteLine("Ctrl+C to stop.");

using PeriodicTimer timer = new(TimeSpan.FromSeconds(1d / Simulation.TickRate));
ElapsedTimeClock clock = new();
int nextReportTick = Simulation.TickRate;

try
{
    while (await timer.WaitForNextTickAsync(cancellation.Token))
    {
        host.Advance(clock.ReadSeconds());
        if (host.Tick >= nextReportTick)
        {
            Console.WriteLine($"tick={host.Tick} players={host.ConnectedPlayerCount} tick_ms={host.LastTickMilliseconds:F3} alloc={host.LastTickAllocatedBytes} backlog_s={host.PendingSeconds:F3} bytes_sent={host.BytesSent}");
            nextReportTick = host.Tick + Simulation.TickRate;
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Server shutting down.");
}
