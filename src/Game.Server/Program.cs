using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;

int port = 7777;
int maxClients = 2;
bool smokeTest = false;

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
    else if (args[i] == "--smoke-test")
    {
        smokeTest = true;
    }
}

HeightMap map = HeightMap.CreatePrototype();

if (smokeTest)
{
    using LocalSession session = new(PrototypeScenario.Create(map));
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

MatchSetup setup = PrototypeScenario.CreateMatchSetup(map);
using LiteNetLibServerTransport transport = LiteNetLibTransport.CreateServer(port, maxClients);
using SimulationHost host = new(PrototypeScenario.Create(map), transport, setup);

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
