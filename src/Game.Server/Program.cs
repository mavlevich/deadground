using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;

int port = 7777;
int maxClients = 2;
bool smokeTest = false;
bool battleReport = false;
int battleRuns = 50;
string scenario = "prototype";
ulong? customSeed = null;
bool excludeRecon = false;

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
    else if (args[i] == "--battle-report")
    {
        battleReport = true;
    }
    else if (args[i] == "--runs" && i + 1 < args.Length && int.TryParse(args[i + 1], out int r))
    {
        battleRuns = r;
        i++;
    }
    else if (args[i] == "--seed" && i + 1 < args.Length && ulong.TryParse(args[i + 1], out ulong s))
    {
        customSeed = s;
        i++;
    }
    else if (args[i] == "--exclude-recon")
    {
        excludeRecon = true;
    }
}

if (battleReport)
{
    ExecuteBattleReport(battleRuns, excludeRecon);
    return;
}

if (scenario == "recon-raid")
{
    maxClients = 1;
}

StaticGeometry? scenarioGeometry = scenario switch
{
    "visibility" => VisibilityScenario.CreateStaticGeometry(),
    "navigation" => NavigationScenario.CreateStaticGeometry(),
    "navigation-large" => LargeNavigationScenario.CreateStaticGeometry(),
    "combat" => CombatScenario.CreateStaticGeometry(),
    "recon-raid" => ReconRaidScenario.CreateStaticGeometry(),
    "battle" => BattleScenario.CreateStaticGeometry(),
    _ => null
};

// Scenarios with geometry carry their own map; build it only once.
HeightMap map = scenarioGeometry?.HeightMap ?? HeightMap.CreatePrototype();

MatchSetup setup = scenario switch
{
    "visibility" => VisibilityScenario.CreateMatchSetup(scenarioGeometry!),
    "navigation" => NavigationScenario.CreateMatchSetup(scenarioGeometry!),
    "navigation-large" => LargeNavigationScenario.CreateMatchSetup(scenarioGeometry!),
    "combat" => CombatScenario.CreateMatchSetup(scenarioGeometry!),
    "recon-raid" => ReconRaidScenario.CreateMatchSetup(scenarioGeometry!),
    "battle" => BattleScenario.CreateMatchSetup(scenarioGeometry!),
    _ => PrototypeScenario.CreateMatchSetup(map)
};

ulong matchSeed;
if (customSeed.HasValue)
{
    matchSeed = customSeed.Value;
}
else
{
    byte[] seedBytes = new byte[8];
    System.Security.Cryptography.RandomNumberGenerator.Fill(seedBytes);
    matchSeed = BitConverter.ToUInt64(seedBytes);
}

Simulation CreateSimulation(ulong seed = 0)
{
    ulong effectiveSeed = seed != 0 ? seed : matchSeed;
    return scenario switch
    {
        "visibility" => VisibilityScenario.CreateSimulation(scenarioGeometry!),
        "navigation" => NavigationScenario.CreateSimulation(scenarioGeometry!),
        "navigation-large" => LargeNavigationScenario.CreateSimulation(scenarioGeometry!),
        "combat" => CombatScenario.CreateSimulation(scenarioGeometry!, matchSeed: effectiveSeed),
        "recon-raid" => ReconRaidScenario.CreateSimulation(scenarioGeometry!, matchSeed: effectiveSeed),
        "battle" => BattleScenario.CreateSimulation(scenarioGeometry!, matchSeed: effectiveSeed),
        _ => PrototypeScenario.Create(map)
    };
}

if (smokeTest)
{
    using LocalSession session = new(CreateSimulation(1337UL));
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

    if (scenario == "battle")
    {
        for (int i = 0; i < 20; i++) session.Advance(1d / Simulation.TickRate);
        if (session.Replica.OwnUnits.Length != BattleScenario.UnitsPerSide || session.Replica.ContactCount != 0)
            throw new InvalidOperationException("Battle smoke test: the sides must start out of sight.");

        EntityId mover = session.Replica.OwnUnits[0].Id;
        Vector2 from = session.Replica.OwnUnits[0].Position;
        session.Move(mover, from + new Vector2(0f, -200f));
        for (int i = 0; i < 100; i++) session.Advance(1d / Simulation.TickRate);
        Vector2 to = session.Replica.SamplePosition(mover, session.Replica.LatestTick);
        if (session.Tick != 120 || Vector2.Distance(from, to) < 10f)
            throw new InvalidOperationException("Battle smoke test: the unit did not move.");

        Console.WriteLine($"BATTLE_SMOKE_OK tick={session.Tick} own_units={session.Replica.OwnUnits.Length} moved={Vector2.Distance(from, to):F1}");
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
Console.WriteLine($"MatchSeed: {matchSeed}");
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

static void ExecuteBattleReport(int runs, bool excludeRecon)
{
    string reconNote = excludeRecon ? " (Recon Pair Excluded)" : " (Full Order with Recon Pair)";
    Console.WriteLine($"Running battle report{reconNote}: {runs} runs per placement ({runs * 2} matches total)...");

    BattleBatchStats standard = RunPlacementBatch(runs, swapSides: false, excludeRecon);
    BattleBatchStats swapped = RunPlacementBatch(runs, swapSides: true, excludeRecon);

    PrintReport($"Standard Placement (Soviet SW -> NE, US NE -> SW){reconNote}", standard);
    PrintReport($"Swapped Placement (Soviet NE -> SW, US SW -> NE){reconNote}", swapped);

    float sovOverallWinPct = (float)(standard.SovietWins + swapped.SovietWins) / (runs * 2) * 100f;
    float usOverallWinPct = (float)(standard.UsWins + swapped.UsWins) / (runs * 2) * 100f;
    float drawOverallPct = (float)(standard.Draws + swapped.Draws) / (runs * 2) * 100f;

    Console.WriteLine($"=== COMBINED SUMMARY{reconNote.ToUpperInvariant()} ===");
    Console.WriteLine($"Total Matches: {runs * 2}");
    Console.WriteLine($"Soviet Overall Win Rate: {sovOverallWinPct:F1}%");
    Console.WriteLine($"US Overall Win Rate:     {usOverallWinPct:F1}%");
    Console.WriteLine($"Draw Overall Rate:       {drawOverallPct:F1}%");
}

static BattleBatchStats RunPlacementBatch(int runs, bool swapSides, bool excludeRecon)
{
    BattleBatchStats stats = new() { Runs = runs, ExcludeRecon = excludeRecon };
    StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
    UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");

    for (int r = 0; r < runs; r++)
    {
        ulong seed = 1000UL + (ulong)r * 1013904223UL;
        using Simulation sim = BattleScenario.CreateSimulation(geometry, catalog, seed, swapSides, excludeRecon);

        OpsRules rules = (OpsRules)sim.Rules;
        CombatSystem combat = rules.FindSystem<CombatSystem>()!;
        DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
        WorldState world = sim.World;

        Vector2 p1Target = swapSides ? BattleScenario.SovietStart : BattleScenario.UsStart;
        Vector2 p2Target = swapSides ? BattleScenario.UsStart : BattleScenario.SovietStart;

        for (int i = 0; i < world.Count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            Vector2 dest = unit.Owner == 1 ? p1Target : p2Target;
            sim.TryEnqueue(unit.Owner, new MoveCommand((uint)(i + 1), 0, unit.Id, dest));
        }

        int maxTicks = 20 * 60 * 10; // 10 minutes at 20 Hz
        int firstContactTick = -1;
        int initialP1 = 0;
        int initialP2 = 0;
        for (int i = 0; i < world.Count; i++)
        {
            if (world.GetUnitAt(i).Owner == 1) initialP1++;
            else if (world.GetUnitAt(i).Owner == 2) initialP2++;
        }

        HashSet<uint> seenShotEvents = new();
        int pens = 0;
        int defs = 0;
        int misses = 0;

        int finalP1Remaining = initialP1;
        int finalP2Remaining = initialP2;
        int durationTicks = maxTicks;

        for (int tick = 1; tick <= maxTicks; tick++)
        {
            sim.Step();

            for (int p = 1; p <= 2; p++)
            {
                ReadOnlySpan<CombatEventObservation> events = combat.GetEvents(p);
                for (int e = 0; e < events.Length; e++)
                {
                    if (events[e].Kind == CombatEventKind.ShotFired && seenShotEvents.Add(events[e].EventId))
                    {
                        switch (events[e].Result)
                        {
                            case HitResult.Penetrated: pens++; break;
                            case HitResult.Deflected: defs++; break;
                            case HitResult.Miss: misses++; break;
                        }
                    }
                }
            }

            if (firstContactTick < 0)
            {
                if (detection.GetTracker(1).Contacts.Length > 0 || detection.GetTracker(2).Contacts.Length > 0)
                {
                    firstContactTick = tick;
                }
            }

            if (firstContactTick > 0 && tick >= firstContactTick && (tick % 20 == 0))
            {
                for (int u = 0; u < world.Count; u++)
                {
                    UnitState unit = world.GetUnitAt(u);
                    SideContactTracker tracker = detection.GetTracker(unit.Owner);
                    if (tracker.Contacts.Length > 0)
                    {
                        ContactId bestContact = tracker.Contacts[0].Id;
                        float bestDist = float.MaxValue;
                        for (int c = 0; c < tracker.Contacts.Length; c++)
                        {
                            float d = Vector2.Distance(unit.Position, tracker.Contacts[c].Position);
                            if (d < bestDist)
                            {
                                bestDist = d;
                                bestContact = tracker.Contacts[c].Id;
                            }
                        }
                        sim.TryEnqueue(unit.Owner, new AttackCommand((uint)(10000 + tick * 50 + u), tick, unit.Id, bestContact));
                    }
                }
            }

            int p1Remaining = 0;
            int p2Remaining = 0;
            for (int u = 0; u < world.Count; u++)
            {
                UnitState unit = world.GetUnitAt(u);
                if (unit.Owner == 1) p1Remaining++;
                else if (unit.Owner == 2) p2Remaining++;
            }

            if (p1Remaining == 0 || p2Remaining == 0)
            {
                finalP1Remaining = p1Remaining;
                finalP2Remaining = p2Remaining;
                durationTicks = tick;
                break;
            }
        }

        if (finalP1Remaining > finalP2Remaining) stats.SovietWins++;
        else if (finalP2Remaining > finalP1Remaining) stats.UsWins++;
        else stats.Draws++;

        stats.TotalDurationTicks += durationTicks;
        stats.TotalSovietLosses += (initialP1 - finalP1Remaining);
        stats.TotalUsLosses += (initialP2 - finalP2Remaining);
        stats.TotalShotsFired += combat.TotalShotsFired;
        stats.TotalPenetrations += pens;
        stats.TotalDeflections += defs;
        stats.TotalMisses += misses;

        if (combat.FirstShotTick >= 0)
        {
            stats.FirstShotRuns++;
            stats.TotalFirstShotDistance += combat.FirstShotDistance;
        }

        if (combat.KillCount > 0)
        {
            stats.FirstKillRuns++;
            stats.TotalTimeToFirstKillSeconds += combat.Kills[0].Tick * Simulation.TickSeconds;
        }

        for (int k = 0; k < combat.KillCount; k++)
        {
            CombatKillRecord kill = combat.Kills[k];
            string killerType = catalog.Units[kill.KillerDefinition].Id;
            string victimType = catalog.Units[kill.VictimDefinition].Id;
            var key = (killerType, victimType);
            stats.KillsByShooterAndVictim[key] = stats.KillsByShooterAndVictim.GetValueOrDefault(key) + 1;
        }

        for (int u = 0; u < world.Count; u++)
        {
            UnitState unit = world.GetUnitAt(u);
            string type = catalog.Units[unit.Definition].Id;
            stats.TotalSurvivorsByType[type] = stats.TotalSurvivorsByType.GetValueOrDefault(type) + 1;
        }
    }

    return stats;
}

static void PrintReport(string placementName, BattleBatchStats stats)
{
    float sovWinPct = (float)stats.SovietWins / stats.Runs * 100f;
    float usWinPct = (float)stats.UsWins / stats.Runs * 100f;
    float drawPct = (float)stats.Draws / stats.Runs * 100f;
    float avgDurationSec = (float)stats.TotalDurationTicks / stats.Runs * Simulation.TickSeconds;
    float avgShots = (float)stats.TotalShotsFired / stats.Runs;
    int unitsPerSide = stats.ExcludeRecon ? 5 : 6;
    float avgSovietLosses = (float)stats.TotalSovietLosses / stats.Runs;
    float avgUsLosses = (float)stats.TotalUsLosses / stats.Runs;
    long totalHits = stats.TotalPenetrations + stats.TotalDeflections + stats.TotalMisses;

    float penPct = totalHits > 0 ? (float)stats.TotalPenetrations / totalHits * 100f : 0f;
    float defPct = totalHits > 0 ? (float)stats.TotalDeflections / totalHits * 100f : 0f;
    float missPct = totalHits > 0 ? (float)stats.TotalMisses / totalHits * 100f : 0f;

    float avgFirstShotDist = stats.FirstShotRuns > 0 ? (float)(stats.TotalFirstShotDistance / stats.FirstShotRuns) : 0f;
    float avgTimeToFirstKill = stats.FirstKillRuns > 0 ? (float)(stats.TotalTimeToFirstKillSeconds / stats.FirstKillRuns) : 0f;

    Console.WriteLine($"--- {placementName} ---");
    Console.WriteLine($"Runs: {stats.Runs}");
    Console.WriteLine($"Soviet Wins: {stats.SovietWins} ({sovWinPct:F1}%)");
    Console.WriteLine($"US Wins:     {stats.UsWins} ({usWinPct:F1}%)");
    Console.WriteLine($"Draws:       {stats.Draws} ({drawPct:F1}%)");
    Console.WriteLine($"Avg Duration: {avgDurationSec:F1}s");
    Console.WriteLine($"First Shot Range: {avgFirstShotDist:F1}m, Time to First Kill: {avgTimeToFirstKill:F1}s");
    Console.WriteLine($"Avg Shots Fired: {avgShots:F1} (Total: {stats.TotalShotsFired})");
    Console.WriteLine($"Hits: Penetrations {stats.TotalPenetrations} ({penPct:F1}%), Deflections {stats.TotalDeflections} ({defPct:F1}%), Misses {stats.TotalMisses} ({missPct:F1}%)");
    Console.WriteLine($"Avg Losses: Soviet {avgSovietLosses:F2} / {unitsPerSide}, US {avgUsLosses:F2} / {unitsPerSide}");

    Console.WriteLine("Survivors by Type (avg per match):");
    string[] sovTypes = stats.ExcludeRecon ? ["t62", "btr70"] : ["t62", "btr70", "brdm2"];
    string[] usTypes = stats.ExcludeRecon ? ["m60a1", "m113a1"] : ["m60a1", "m113a1", "m151a2"];

    Console.Write("  Soviet: ");
    for (int i = 0; i < sovTypes.Length; i++)
    {
        string t = sovTypes[i];
        float avg = stats.TotalSurvivorsByType.TryGetValue(t, out int count) ? (float)count / stats.Runs : 0f;
        Console.Write($"{t}: {avg:F2}  ");
    }
    Console.WriteLine();

    Console.Write("  US:     ");
    for (int i = 0; i < usTypes.Length; i++)
    {
        string t = usTypes[i];
        float avg = stats.TotalSurvivorsByType.TryGetValue(t, out int count) ? (float)count / stats.Runs : 0f;
        Console.Write($"{t}: {avg:F2}  ");
    }
    Console.WriteLine();

    Console.WriteLine("Kills by Shooter -> Victim:");
    foreach (var kvp in stats.KillsByShooterAndVictim.OrderByDescending(k => k.Value))
    {
        float avg = (float)kvp.Value / stats.Runs;
        Console.WriteLine($"  {kvp.Key.Killer,-8} -> {kvp.Key.Victim,-8} : {kvp.Value,4} total ({avg:F2} per match)");
    }
    Console.WriteLine();
}

sealed class BattleBatchStats
{
    public int Runs;
    public bool ExcludeRecon;
    public int SovietWins;
    public int UsWins;
    public int Draws;
    public long TotalDurationTicks;
    public long TotalSovietLosses;
    public long TotalUsLosses;
    public long TotalShotsFired;
    public long TotalPenetrations;
    public long TotalDeflections;
    public long TotalMisses;

    public int FirstShotRuns;
    public double TotalFirstShotDistance;
    public int FirstKillRuns;
    public double TotalTimeToFirstKillSeconds;

    public readonly Dictionary<(string Killer, string Victim), int> KillsByShooterAndVictim = new();
    public readonly Dictionary<string, int> TotalSurvivorsByType = new();
}
