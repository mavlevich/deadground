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

if (scenario == "recon-raid")
{
    maxClients = 1;
}

HeightMap map = scenario switch
{
    "visibility" => VisibilityScenario.CreateMap(),
    "navigation" => NavigationScenario.CreateMap(),
    "navigation-large" => LargeNavigationScenario.CreateMap(),
    "combat" => CombatScenario.CreateMap(),
    "recon-raid" => ReconRaidScenario.CreateMap(),
    _ => HeightMap.CreatePrototype()
};

StaticGeometry? scenarioGeometry = scenario switch
{
    "visibility" => VisibilityScenario.CreateStaticGeometry(),
    "navigation" => NavigationScenario.CreateStaticGeometry(),
    "navigation-large" => LargeNavigationScenario.CreateStaticGeometry(),
    "combat" => CombatScenario.CreateStaticGeometry(),
    "recon-raid" => ReconRaidScenario.CreateStaticGeometry(),
    _ => null
};

MatchSetup setup = scenario switch
{
    "visibility" => VisibilityScenario.CreateMatchSetup(scenarioGeometry!),
    "navigation" => NavigationScenario.CreateMatchSetup(scenarioGeometry!),
    "navigation-large" => LargeNavigationScenario.CreateMatchSetup(scenarioGeometry!),
    "combat" => CombatScenario.CreateMatchSetup(scenarioGeometry!),
    "recon-raid" => ReconRaidScenario.CreateMatchSetup(scenarioGeometry!),
    _ => PrototypeScenario.CreateMatchSetup(map)
};

Simulation CreateSimulation() => scenario switch
{
    "visibility" => VisibilityScenario.CreateSimulation(scenarioGeometry!),
    "navigation" => NavigationScenario.CreateSimulation(scenarioGeometry!),
    "navigation-large" => LargeNavigationScenario.CreateSimulation(scenarioGeometry!),
    "combat" => CombatScenario.CreateSimulation(scenarioGeometry!),
    "recon-raid" => ReconRaidScenario.CreateSimulation(scenarioGeometry!),
    _ => PrototypeScenario.Create(map)
};

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
    if (scenario == "navigation")
    {
        for (int i = 0; i < 40; i++) session.Advance(1d / Simulation.TickRate);
        if (session.Tick != 40 || session.Replica.OwnUnits.Length != 3)
            throw new InvalidOperationException("Navigation smoke test failed.");
        Console.WriteLine($"NAVIGATION_SMOKE_OK tick={session.Tick} own_units={session.Replica.OwnUnits.Length}");
        return;
    }
    if (scenario == "navigation-large")
    {
        for (int i = 0; i < 40; i++) session.Advance(1d / Simulation.TickRate);
        if (session.Tick != 40 || session.Replica.OwnUnits.Length != 3)
            throw new InvalidOperationException("Navigation-large smoke test failed.");

        Vector2 startPos = session.Replica.OwnUnits[0].Position;
        EntityId reconId = session.Replica.OwnUnits[0].Id;
        session.Move(reconId, new Vector2(300f, 300f));
        for (int i = 0; i < 30; i++) session.Advance(1d / Simulation.TickRate);

        Vector2 movedPos = session.Replica.OwnUnits[0].Position;
        bool hasMoved = Vector2.Distance(startPos, movedPos) > 1.0f;
        if (!hasMoved)
            throw new InvalidOperationException("Navigation-large unit movement failed.");

        Console.WriteLine($"NAVIGATION_LARGE_SMOKE_OK tick={session.Tick} own_units={session.Replica.OwnUnits.Length} moved={hasMoved} nav_status={session.Replica.OwnUnits[0].NavStatus}");
        return;
    }

    if (scenario == "combat")
    {
        for (int i = 0; i < 20; i++) session.Advance(1d / Simulation.TickRate);
        if (session.Replica.OwnUnits.Length != 4 || session.Replica.ContactCount == 0)
            throw new InvalidOperationException("Combat smoke test initial detection failed.");

        EntityId tankId = default;
        for (int i = 0; i < session.Replica.OwnUnits.Length; i++)
        {
            if (session.Replica.OwnUnits[i].Ammunition > 0)
            {
                tankId = session.Replica.OwnUnits[i].Id;
                break;
            }
        }
        ContactId targetContact = session.Replica.Contacts[0].Id;
        bool attacked = session.Attack(tankId, targetContact);
        if (!attacked) throw new InvalidOperationException("Failed to issue attack command.");

        for (int i = 0; i < 100; i++) session.Advance(1d / Simulation.TickRate);
        if (session.Tick != 120)
            throw new InvalidOperationException("Combat smoke test tick mismatch.");

        Console.WriteLine($"COMBAT_SMOKE_OK tick={session.Tick} own_units={session.Replica.OwnUnits.Length} contacts={session.Replica.ContactCount}");
        return;
    }

    if (scenario == "recon-raid")
    {
        for (int i = 0; i < 40; i++) session.Advance(1d / Simulation.TickRate);
        if (session.Tick != 40 || session.Replica.OwnUnits.Length != 3)
            throw new InvalidOperationException("Recon-raid smoke test initial units check failed.");
        if (session.Mission.Status != MissionStatus.InProgress)
            throw new InvalidOperationException("Recon-raid smoke test initial mission status mismatch.");

        Console.WriteLine($"RECON_RAID_SMOKE_OK tick={session.Tick} own_units={session.Replica.OwnUnits.Length} mission_status={session.Mission.Status}");
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
int reservedBotPlayer = scenario == "recon-raid" ? 2 : 0;
using SimulationHost host = new(CreateSimulation(), transport, setup, reservedBotPlayer);

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
