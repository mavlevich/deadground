using System.Diagnostics;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Rules.Sandbox;

if (args.Length > 0 && args[0] == "--navigation-5km")
{
    Environment.ExitCode = DeadGround.Benchmarks.NavigationScaleProbe.Run(args.Length == 3 && args[1] == "--json" ? args[2] : null) ? 0 : 1;
    return;
}

if (args.Length > 0 && args[0] == "--order-preview")
{
    RunOrderPreviewBenchmark();
    return;
}

if (args.Length > 0 && (args[0] == "--vehicle-separation" || args[0] == "--vehicle-traffic"))
{
    RunVehicleSeparationBenchmark();
    return;
}

if (args.Length > 0 && args[0] == "--probe")
{
    RunProbe();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(MovementBenchmark).Assembly).Run(args);

static void RunProbe()
{
    const int size = 65;
    HeightMap flatMap = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
    StaticGeometry openGeo = new(flatMap, []);

    // Solid wall: 6m high wall at X in [63, 65] spanning whole depth Z in [0, 128]
    // Side 1 units are at X in [10, 58], Side 2 units are at X in [70, 118]
    OpaqueStructure wall = new(new StructureId(1), 63f, 0f, 0f, 65f, 6f, 128f, isGrounded: true);
    StaticGeometry wallGeo = new(flatMap, [wall]);

    // Hill: center ridge rising to 10m height at X in [58, 70]
    ushort[] hillSamples = new ushort[size * size];
    for (int r = 0; r < size; r++)
    {
        for (int c = 0; c < size; c++)
        {
            float x = c * 2.0f;
            if (x >= 58f && x <= 70f)
            {
                float normalized = (x - 58f) / 12f;
                float h = 10f * MathF.Sin(normalized * MathF.PI);
                hillSamples[r * size + c] = (ushort)(h / 0.01f);
            }
        }
    }
    HeightMap hillMap = new(size, size, 2.0f, 0.01f, hillSamples);
    StaticGeometry hillGeo = new(hillMap, []);

    var scenarios = new (string Name, StaticGeometry Geo)[]
    {
        ("Open Map (flat)", openGeo),
        ("Solid Wall (occluded)", wallGeo),
        ("Hill (terrain occluded)", hillGeo)
    };

    bool allPassed = true;
    foreach (var (name, geo) in scenarios)
    {
        if (!RunSingleScenarioProbe(name, geo))
        {
            allPassed = false;
        }
    }

    if (!RunNavigationProbe())
    {
        allPassed = false;
    }

    if (!RunLargeNavigationProbe())
    {
        allPassed = false;
    }

    if (!RunCombatProbe())
    {
        allPassed = false;
    }

    if (!allPassed)
    {
        Console.WriteLine("Performance gate FAILED: one or more scenarios exceeded the 15 ms target budget or had allocations.");
        Environment.ExitCode = 1;
    }
    else
    {
        Console.WriteLine("Performance gate PASSED: all scenarios meet the < 15 ms target budget and 0 B allocations.");
    }
}

static bool RunSingleScenarioProbe(string scenarioName, StaticGeometry geo)
{
    using Simulation sim = DeadGround.Benchmarks.DetectionBenchmark.CreateDense1000Simulation(
        evaluationIntervalTicks: 2,
        customGeometry: geo);

    PlayerKnowledge k1 = new(1);
    PlayerKnowledge k2 = new(2);
    byte[] b1 = new byte[WireProtocol.MaxMessageSize];
    byte[] b2 = new byte[WireProtocol.MaxMessageSize];

    int movingCount = 0;
    for (int i = 0; i < sim.World.Count; i++)
    {
        if (sim.World.GetUnitAt(i).Moving) movingCount++;
    }

    for (int i = 0; i < 20; i++)
    {
        sim.Step();
        if (sim.Tick % 2 == 0)
        {
            sim.UpdateKnowledge(k1);
            WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts);
            sim.UpdateKnowledge(k2);
            WireProtocol.WriteSnapshot(b2, (uint)sim.Tick, sim.Tick, k2.OwnUnits, k2.Contacts);
        }
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    long totalLosQueries = 0;
    double[] times = new double[100];
    long totalAllocated = 0;

    for (int i = 0; i < 100; i++)
    {
        long startAlloc = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();

        sim.Step();
        if (sim.Tick % 2 == 0)
        {
            sim.UpdateKnowledge(k1);
            WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts);
            sim.UpdateKnowledge(k2);
            WireProtocol.WriteSnapshot(b2, (uint)sim.Tick, sim.Tick, k2.OwnUnits, k2.Contacts);
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        long endAlloc = GC.GetAllocatedBytesForCurrentThread();

        totalAllocated += (endAlloc - startAlloc);
        totalLosQueries += sim.Detection?.LosQueriesCount ?? 0;
        times[i] = (double)elapsed / Stopwatch.Frequency * 1000.0;
    }

    Array.Sort(times);
    bool meetsBudget = movingCount == 1000 && totalAllocated == 0 && times[50] < 15.0 && times[98] < 15.0;

    Console.WriteLine($"Scenario: {scenarioName} (1000 moving units, 100 measured ticks)");
    Console.WriteLine($"  Moving units:      {movingCount}/1000");
    Console.WriteLine($"  Median (p50):      {times[50]:F2} ms");
    Console.WriteLine($"  p95:               {times[95]:F2} ms");
    Console.WriteLine($"  p99:               {times[98]:F2} ms");
    Console.WriteLine($"  Max:               {times[99]:F2} ms");
    Console.WriteLine($"  Allocations:       {totalAllocated} B");
    Console.WriteLine($"  LOS queries:       {totalLosQueries}");
    Console.WriteLine($"  Contacts (P1/P2):  {k1.ContactCount}/{k2.ContactCount}");
    Console.WriteLine($"  Performance gate:  {(meetsBudget ? "PASS (p99 < 15 ms, median < 15 ms, 0 B)" : "FAIL")}");
    Console.WriteLine();

    return meetsBudget;
}

static bool RunNavigationProbe()
{
    const int size = 65;
    HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);

    // Central building obstacle at X in [58..70], Z in [40..88]
    OpaqueStructure building = new(
        new StructureId(1),
        minX: 58f, minY: 0f, minZ: 40f,
        maxX: 70f, maxY: 6f, maxZ: 88f,
        isGrounded: true);

    // Enclosed courtyard obstacle: 4 walls enclosing interior [92..108, 77..95]
    OpaqueStructure courtS = new(new StructureId(2), 90f, 0f, 75f, 110f, 3f, 77f, isGrounded: true);
    OpaqueStructure courtN = new(new StructureId(3), 90f, 0f, 95f, 110f, 3f, 97f, isGrounded: true);
    OpaqueStructure courtW = new(new StructureId(4), 90f, 0f, 77f, 92f, 3f, 95f, isGrounded: true);
    OpaqueStructure courtE = new(new StructureId(5), 108f, 0f, 77f, 110f, 3f, 95f, isGrounded: true);

    StaticGeometry geo = new(map, [building, courtS, courtN, courtW, courtE]);
    UnitCatalog catalog = PrototypeScenario.LoadCatalog();

    NavigationTuning navTuning = new(MaxNodeExpansionsPerTick: 1000, MaxPendingRequests: 1024);
    OpsRules rules = new(geo, new OpsPerceptionCatalog(catalog), new SlowMovementCatalog(), navTuning);
    WorldState world = new(geo.HeightMap);
    using Simulation sim = new(world, rules);

    // 500 units on Side 1: moving North in West corridor X in [10..46]
    for (int i = 0; i < 500; i++)
    {
        float x = 10f + (i % 25) * 1.5f;
        float y = 10f + (i / 25) * 2.5f;
        EntityId id = world.CreateUnit(1, 1, new Vector2(x, y), 0.001f);
        bool enqueued = sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), 0, id, new Vector2(x, y + 60f)));
        if (!enqueued) throw new InvalidOperationException($"Failed to enqueue unit {id.Value}");
        if (i % 200 == 199) sim.Step();
    }
    sim.Step();

    // 500 units on Side 2: moving South in West corridor X in [10..46]
    for (int i = 0; i < 500; i++)
    {
        float x = 10f + (i % 25) * 1.5f;
        float y = 118f - (i / 25) * 2.5f;
        EntityId id = world.CreateUnit(2, 2, new Vector2(x, y), 0.001f);
        bool enqueued = sim.TryEnqueue(2, new MoveCommand((uint)(500 + i + 1), 0, id, new Vector2(x, y - 60f)));
        if (!enqueued) throw new InvalidOperationException($"Failed to enqueue unit {id.Value}");
        if (i % 200 == 199) sim.Step();
    }
    sim.Step();

    // Step simulation until all 1000 units have routes established and are moving
    int prepTicks = 0;
    while (prepTicks < 150)
    {
        sim.Step();
        prepTicks++;
        int moving = 0;
        for (int u = 0; u < world.Count; u++) if (world.GetUnitAt(u).Moving) moving++;
        if (moving == 1000) break;
    }

    int initialMoving = 0;
    for (int u = 0; u < world.Count; u++) if (world.GetUnitAt(u).Moving) initialMoving++;
    if (initialMoving != 1000)
        throw new InvalidOperationException($"Failed to establish routes for all 1000 units. Moving={initialMoving}/1000");

    PlayerKnowledge k1 = new(1);
    PlayerKnowledge k2 = new(2);
    byte[] b1 = new byte[WireProtocol.MaxMessageSize];
    byte[] b2 = new byte[WireProtocol.MaxMessageSize];

    uint commandSeq = 2001;

    // Warm up for 20 ticks with representative workload to JIT compile all code paths
    for (int m = 0; m < 5; m++)
    {
        EntityId uId = world.GetUnitAt(m).Id;
        sim.TryEnqueue(1, new MoveCommand(commandSeq++, 0, uId, new Vector2(15f + (m * 4f), 80f)));
    }
    EntityId warmupUnreachableId = world.GetUnitAt(500).Id;
    sim.TryEnqueue(2, new MoveCommand(commandSeq++, 0, warmupUnreachableId, new Vector2(100f, 86f)));

    for (int i = 0; i < 20; i++)
    {
        sim.Step();
        if (sim.Tick % 2 == 0)
        {
            sim.UpdateKnowledge(k1);
            WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts);
            sim.UpdateKnowledge(k2);
            WireProtocol.WriteSnapshot(b2, (uint)sim.Tick, sim.Tick, k2.OwnUnits, k2.Contacts);
        }
    }

    double[] times = new double[100];
    long totalAllocated = 0;
    int minMoving = 1000;
    int maxMoving = 0;

    for (int i = 0; i < 100; i++)
    {
        // At tick 0, enqueue a bounded burst of new move orders and genuine unreachable searches
        if (i == 0)
        {
            for (int m = 0; m < 10; m++)
            {
                EntityId uId = world.GetUnitAt(10 + m).Id;
                sim.TryEnqueue(1, new MoveCommand(commandSeq++, 0, uId, new Vector2(15f + (m * 2f), 80f)));
            }
            for (int u = 0; u < 2; u++)
            {
                EntityId uId = world.GetUnitAt(501 + u).Id;
                sim.TryEnqueue(2, new MoveCommand(commandSeq++, 0, uId, new Vector2(100f, 86f)));
            }
        }

        long startAlloc = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();

        sim.Step();
        if (sim.Tick % 2 == 0)
        {
            sim.UpdateKnowledge(k1);
            WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts);
            sim.UpdateKnowledge(k2);
            WireProtocol.WriteSnapshot(b2, (uint)sim.Tick, sim.Tick, k2.OwnUnits, k2.Contacts);
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        long endAlloc = GC.GetAllocatedBytesForCurrentThread();

        totalAllocated += (endAlloc - startAlloc);
        times[i] = (double)elapsed / Stopwatch.Frequency * 1000.0;

        int currentMoving = 0;
        for (int n = 0; n < world.Count; n++) if (world.GetUnitAt(n).Moving) currentMoving++;
        minMoving = Math.Min(minMoving, currentMoving);
        maxMoving = Math.Max(maxMoving, currentMoving);
    }

    Array.Sort(times);
    double median = times[50];
    double p95 = times[94];
    double p99 = times[98];
    double max = times[99];

    bool meetsBudget = totalAllocated == 0 && median < 15.0 && p99 < 15.0 && minMoving >= 980;

    Console.WriteLine("Scenario: Navigation 1000 Units (moving + batch planning + unreachable searches) (100 measured ticks)");
    Console.WriteLine($"  Moving units:      {minMoving}..{maxMoving}/1000");
    Console.WriteLine($"  Pending queue:     {sim.Navigation?.PendingRequests ?? 0}");
    Console.WriteLine($"  Median (p50):      {median:F2} ms");
    Console.WriteLine($"  p95:               {p95:F2} ms");
    Console.WriteLine($"  p99:               {p99:F2} ms");
    Console.WriteLine($"  Max:               {max:F2} ms");
    Console.WriteLine($"  Allocations:       {totalAllocated} B");
    Console.WriteLine($"  Nodes expanded:    {sim.Navigation?.TotalNodesExpanded ?? 0}");
    Console.WriteLine($"  Searches done:     {sim.Navigation?.CompletedSearches ?? 0}");
    Console.WriteLine($"  NoPath searches:   {sim.Navigation?.FailedSearches ?? 0}");
    Console.WriteLine($"  Capacity exceeded: {sim.Navigation?.CapacityExceeded ?? 0}");
    Console.WriteLine($"  Performance gate:  {(meetsBudget ? "PASS (p99 < 15 ms, median < 15 ms, 0 B)" : "FAIL")}");
    Console.WriteLine();

    return meetsBudget;
}

static bool RunCombatProbe()
{
    const int size = 128;
    HeightMap map = new(size, size, 1.0f, 0.01f, new ushort[size * size]);
    StaticGeometry geo = new(map, []);
    UnitCatalog catalog = PrototypeScenario.LoadCatalog();

    OpsRules rules = new(geo, catalog);
    WorldState world = new(map, capacity: 1024, playerTeams: [1, 2]);
    using Simulation sim = new(world, rules);

    ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
    UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");

    // 100 tanks per side facing each other across a 30m firing lane
    // Side 1: X=30, Y=14..113 (100 tanks)
    // Side 2: X=60, Y=14..113 (100 tanks, distance 30m within 60m gun range)
    const int tanksPerSide = 100;
    EntityId[] side1Tanks = new EntityId[tanksPerSide];
    EntityId[] side2Tanks = new EntityId[tanksPerSide];

    for (int i = 0; i < tanksPerSide; i++)
    {
        float y = 14f + i;
        side1Tanks[i] = world.CreateUnit(1, tankIdx, new Vector2(30f, y), tankDef.Speed, maxHealth: 2000, ammo: 100);
        side2Tanks[i] = world.CreateUnit(2, tankIdx, new Vector2(60f, y), tankDef.Speed, maxHealth: 2000, ammo: 100);
    }

    // Advance 4 ticks so detection completes observer phases for both sides
    for (int t = 0; t < 4; t++) sim.Step();

    // Enqueue attack orders for all 100 Side 1 tanks targeting opposing Side 2 tanks
    uint cmdSeq1 = 1;
    for (int i = 0; i < tanksPerSide; i++)
    {
        if (sim.Detection!.TryGetContactByEntity(1, side2Tanks[i], out ContactObservation targetContact))
        {
            sim.TryEnqueue(1, new AttackCommand(cmdSeq1++, sim.Tick, side1Tanks[i], targetContact.Id));
        }
    }

    // Enqueue attack orders for all 100 Side 2 tanks targeting opposing Side 1 tanks
    uint cmdSeq2 = 1;
    for (int i = 0; i < tanksPerSide; i++)
    {
        if (sim.Detection!.TryGetContactByEntity(2, side1Tanks[i], out ContactObservation targetContact))
        {
            sim.TryEnqueue(2, new AttackCommand(cmdSeq2++, sim.Tick, side2Tanks[i], targetContact.Id));
        }
    }

    PlayerKnowledge k1 = new(1);
    PlayerKnowledge k2 = new(2);
    byte[] b1 = new byte[WireProtocol.MaxMessageSize];
    byte[] b2 = new byte[WireProtocol.MaxMessageSize];

    // Warm up JIT and internal buffers over 10 ticks
    for (int i = 0; i < 10; i++)
    {
        sim.Step();
        if (sim.Tick % 2 == 0)
        {
            sim.UpdateKnowledge(k1);
            WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts, k1.Events);
            sim.UpdateKnowledge(k2);
            WireProtocol.WriteSnapshot(b2, (uint)sim.Tick, sim.Tick, k2.OwnUnits, k2.Contacts, k2.Events);
        }
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    const int measuredTicks = 100;
    double[] times = new double[measuredTicks];
    long totalAllocated = 0;
    long totalEventBytes = 0;
    int minActiveAttackers = int.MaxValue;
    int maxActiveAttackers = 0;
    int initialShots = sim.Combat?.TotalShotsFired ?? 0;
    int initialDamage = sim.Combat?.TotalDamageApplied ?? 0;

    for (int i = 0; i < measuredTicks; i++)
    {
        long startAlloc = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();

        sim.Step();
        if (sim.Tick % 2 == 0)
        {
            sim.UpdateKnowledge(k1);
            int bytes1 = WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts, k1.Events);
            sim.UpdateKnowledge(k2);
            int bytes2 = WireProtocol.WriteSnapshot(b2, (uint)sim.Tick, sim.Tick, k2.OwnUnits, k2.Contacts, k2.Events);
            totalEventBytes += (bytes1 + bytes2);
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        long endAlloc = GC.GetAllocatedBytesForCurrentThread();

        totalAllocated += (endAlloc - startAlloc);
        times[i] = (double)elapsed / Stopwatch.Frequency * 1000.0;

        int attackers = sim.Combat?.ActiveAttackersCount ?? 0;
        minActiveAttackers = Math.Min(minActiveAttackers, attackers);
        maxActiveAttackers = Math.Max(maxActiveAttackers, attackers);
    }

    int measuredShots = (sim.Combat?.TotalShotsFired ?? 0) - initialShots;
    int measuredDamage = (sim.Combat?.TotalDamageApplied ?? 0) - initialDamage;

    Array.Sort(times);
    double median = times[50];
    double p95 = times[94];
    double p99 = times[98];
    double max = times[99];

    bool meetsBudget = totalAllocated == 0 && median < 15.0 && p99 < 15.0 && minActiveAttackers >= 180 && measuredShots > 0;

    Console.WriteLine("Scenario: Direct-Fire Combat 200 Tanks (100 vs 100 active engagement) (100 measured ticks)");
    Console.WriteLine($"  Active attackers:  {minActiveAttackers}..{maxActiveAttackers}/200");
    Console.WriteLine($"  Shots resolved:    {measuredShots}");
    Console.WriteLine($"  Damage applied:    {measuredDamage}");
    Console.WriteLine($"  Event traffic:     {totalEventBytes / 1024.0:F1} KiB");
    Console.WriteLine($"  Median (p50):      {median:F2} ms");
    Console.WriteLine($"  p95:               {p95:F2} ms");
    Console.WriteLine($"  p99:               {p99:F2} ms");
    Console.WriteLine($"  Max:               {max:F2} ms");
    Console.WriteLine($"  Allocations:       {totalAllocated} B");
    Console.WriteLine($"  Performance gate:  {(meetsBudget ? "PASS (p99 < 15 ms, median < 15 ms, 0 B, continuous combat)" : "FAIL")}");
    Console.WriteLine();

    return meetsBudget;
}

static bool RunLargeNavigationProbe()
{
    StaticGeometry geo = LargeNavigationScenario.CreateStaticGeometry();
    UnitCatalog catalog = PrototypeScenario.LoadCatalog();

    NavigationTuning navTuning = new(
        MaxNodeExpansionsPerTick: 1000,
        MaxPendingRequests: 1024,
        MaxGlobalExpansionsPerTick: 400,
        MaxLocalExpansionsPerTick: 800,
        MaxAreaPrepCellsPerTick: 2500);

    OpsRules rules = new(geo, new OpsPerceptionCatalog(catalog), new SlowMovementCatalog(), navTuning);
    WorldState world = new(geo.HeightMap);
    using Simulation sim = new(world, rules);

    // 1000 units on 2x2 km map
    // Side 1: 450 units in open area [100..400, 100..400] moving North
    for (int i = 0; i < 450; i++)
    {
        float x = 100f + (i % 25) * 10f;
        float y = 100f + (i / 25) * 15f;
        EntityId id = world.CreateUnit(1, 1, new Vector2(x, y), 0.001f);
        bool enqueued = sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), 0, id, new Vector2(x, y + 60f)));
        if (!enqueued) throw new InvalidOperationException($"Failed to enqueue unit {id.Value}");
        if (i % 200 == 199) sim.Step();
    }
    sim.Step();

    // Side 2: 450 units in open area [1600..1900, 1600..1900] moving South
    for (int i = 0; i < 450; i++)
    {
        float x = 1600f + (i % 25) * 10f;
        float y = 1900f - (i / 25) * 15f;
        EntityId id = world.CreateUnit(2, 2, new Vector2(x, y), 0.001f);
        bool enqueued = sim.TryEnqueue(2, new MoveCommand((uint)(450 + i + 1), 0, id, new Vector2(x, y - 60f)));
        if (!enqueued) throw new InvalidOperationException($"Failed to enqueue unit {id.Value}");
        if (i % 200 == 199) sim.Step();
    }
    sim.Step();

    // 50 units near barrier [900, 350..550] moving east across the 500m barrier
    for (int i = 0; i < 50; i++)
    {
        float x = 900f;
        float y = 350f + i * 4f;
        EntityId id = world.CreateUnit(1, 1, new Vector2(x, y), 0.001f);
        bool enqueued = sim.TryEnqueue(1, new MoveCommand((uint)(900 + i + 1), 0, id, new Vector2(1050f, y)));
        if (!enqueued) throw new InvalidOperationException($"Failed to enqueue barrier unit {id.Value}");
    }
    sim.Step();

    // 50 units in [1300..1350, 1400..1480] targeting unreachable courtyard interior [1440, 1440]
    for (int i = 0; i < 50; i++)
    {
        float x = 1300f + (i % 10) * 5f;
        float y = 1400f + (i / 10) * 15f;
        EntityId id = world.CreateUnit(2, 2, new Vector2(x, y), 0.001f);
        bool enqueued = sim.TryEnqueue(2, new MoveCommand((uint)(950 + i + 1), 0, id, new Vector2(1440f, 1440f)));
        if (!enqueued) throw new InvalidOperationException($"Failed to enqueue unreachable unit {id.Value}");
    }
    sim.Step();

    // Step simulation until all reachable units establish routes
    int prepTicks = 0;
    while (prepTicks < 150)
    {
        sim.Step();
        prepTicks++;
        int moving = 0;
        for (int u = 0; u < world.Count; u++) if (world.GetUnitAt(u).Moving) moving++;
        if (moving >= 900) break;
    }

    PlayerKnowledge k1 = new(1);
    PlayerKnowledge k2 = new(2);
    byte[] b1 = new byte[WireProtocol.MaxMessageSize];
    byte[] b2 = new byte[WireProtocol.MaxMessageSize];

    uint commandSeq = 3001;

    // Warm up for 20 ticks with representative workload
    for (int m = 0; m < 5; m++)
    {
        EntityId uId = world.GetUnitAt(m).Id;
        sim.TryEnqueue(1, new MoveCommand(commandSeq++, 0, uId, new Vector2(150f + (m * 4f), 200f)));
    }
    EntityId warmupUnreachableId = world.GetUnitAt(950).Id;
    sim.TryEnqueue(2, new MoveCommand(commandSeq++, 0, warmupUnreachableId, new Vector2(1440f, 1440f)));

    for (int i = 0; i < 20; i++)
    {
        sim.Step();
        if (sim.Tick % 2 == 0)
        {
            sim.UpdateKnowledge(k1);
            WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts);
            sim.UpdateKnowledge(k2);
            WireProtocol.WriteSnapshot(b2, (uint)sim.Tick, sim.Tick, k2.OwnUnits, k2.Contacts);
        }
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    double[] times = new double[100];
    long totalAllocated = 0;
    int minMoving = 1000;
    int maxMoving = 0;

    for (int i = 0; i < 100; i++)
    {
        // At tick 0, enqueue a burst of new orders across hierarchical sectors
        if (i == 0)
        {
            for (int m = 0; m < 10; m++)
            {
                EntityId uId = world.GetUnitAt(10 + m).Id;
                sim.TryEnqueue(1, new MoveCommand(commandSeq++, 0, uId, new Vector2(250f + (m * 5f), 300f)));
            }
            for (int u = 0; u < 3; u++)
            {
                EntityId uId = world.GetUnitAt(955 + u).Id;
                sim.TryEnqueue(2, new MoveCommand(commandSeq++, 0, uId, new Vector2(1440f, 1440f)));
            }
        }

        long startAlloc = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();

        sim.Step();
        if (sim.Tick % 2 == 0)
        {
            sim.UpdateKnowledge(k1);
            WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts);
            sim.UpdateKnowledge(k2);
            WireProtocol.WriteSnapshot(b2, (uint)sim.Tick, sim.Tick, k2.OwnUnits, k2.Contacts);
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        long endAlloc = GC.GetAllocatedBytesForCurrentThread();

        totalAllocated += (endAlloc - startAlloc);
        times[i] = (double)elapsed / Stopwatch.Frequency * 1000.0;

        int currentMoving = 0;
        for (int n = 0; n < world.Count; n++) if (world.GetUnitAt(n).Moving) currentMoving++;
        minMoving = Math.Min(minMoving, currentMoving);
        maxMoving = Math.Max(maxMoving, currentMoving);
    }

    Array.Sort(times);
    double median = times[50];
    double p95 = times[94];
    double p99 = times[98];
    double max = times[99];

    bool meetsBudget = totalAllocated == 0 && median < 15.0 && p99 < 15.0;

    Console.WriteLine("Scenario: Hierarchical Navigation 1000 Units (2x2 km map) (100 measured ticks)");
    Console.WriteLine($"  Moving units:      {minMoving}..{maxMoving}/1000");
    Console.WriteLine($"  Pending queue:     {sim.Navigation?.PendingRequests ?? 0}");
    Console.WriteLine($"  Median (p50):      {median:F2} ms");
    Console.WriteLine($"  p95:               {p95:F2} ms");
    Console.WriteLine($"  p99:               {p99:F2} ms");
    Console.WriteLine($"  Max:               {max:F2} ms");
    Console.WriteLine($"  Allocations:       {totalAllocated} B");
    Console.WriteLine($"  Global nodes exp:  {sim.Navigation?.TotalGlobalExpansions ?? 0}");
    Console.WriteLine($"  Local nodes exp:   {sim.Navigation?.TotalLocalExpansions ?? 0}");
    Console.WriteLine($"  Area prep cells:   {sim.Navigation?.TotalAreaPrepCells ?? 0}");
    Console.WriteLine($"  Smoothing casts:   {sim.Navigation?.TotalSmoothingRaycasts ?? 0}");
    Console.WriteLine($"  Searches done:     {sim.Navigation?.CompletedSearches ?? 0}");
    Console.WriteLine($"  NoPath searches:   {sim.Navigation?.FailedSearches ?? 0}");
    Console.WriteLine($"  Capacity exceeded: {sim.Navigation?.CapacityExceeded ?? 0}");
    Console.WriteLine($"  Chunk requests:    {sim.Navigation?.ChunkRequestsCompleted ?? 0}");
    Console.WriteLine($"  Est. memory:       {sim.Navigation?.EstimatedMemoryBytes ?? 0} B");
    Console.WriteLine($"  Performance gate:  {(meetsBudget ? "PASS (p99 < 15 ms, median < 15 ms, 0 B)" : "FAIL")}");
    Console.WriteLine();

    return meetsBudget;
}

static void RunOrderPreviewBenchmark()
{
    const int size = 65;
    HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
    OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
    TerrainObstacle water = new(10f, 10f, 30f, 30f, TraversabilityCapabilities.Water);
    StaticGeometry geo = new(map, [building], ReadOnlySpan<BridgeStructure>.Empty, [water]);
    DestinationEvaluator evaluator = new(geo, cellSize: 1.0f);

    Vector2 target = new(35.0f, 50.0f);

    int[] counts = [1, 20, 200];
    Console.WriteLine("=== ORDER PREVIEW DEDUPLICATION BENCHMARK ===");
    Console.WriteLine($"Map: {map.Width:F0}x{map.Depth:F0}m, Target: ({target.X:F1}, {target.Y:F1})");

    Span<(float Radius, TraversabilityCapabilities Caps, uint Surface, DestinationEvaluationResult Result)> cache =
        stackalloc (float, TraversabilityCapabilities, uint, DestinationEvaluationResult)[8];

    foreach (int count in counts)
    {
        (float Radius, TraversabilityCapabilities Caps, uint Surface)[] army =
            new (float, TraversabilityCapabilities, uint)[count];

        for (int i = 0; i < count; i++)
        {
            if (i % 3 == 0)
                army[i] = (1.2f, TraversabilityCapabilities.DefaultVehicle, 0); // Tank
            else if (i % 3 == 1)
                army[i] = (0.8f, TraversabilityCapabilities.DefaultVehicle, 0); // Recon
            else
                army[i] = (1.0f, TraversabilityCapabilities.DefaultVehicle, 0); // APC
        }

        // Warmup
        for (int iter = 0; iter < 1000; iter++)
        {
            int cCount = 0;
            for (int i = 0; i < count; i++)
            {
                var unit = army[i];
                bool found = false;
                for (int c = 0; c < cCount; c++)
                {
                    if (cache[c].Radius == unit.Radius && cache[c].Caps == unit.Caps && cache[c].Surface == unit.Surface)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    DestinationEvaluationResult res = evaluator.Evaluate(target, unit.Radius, unit.Caps, unit.Surface);
                    if (cCount < cache.Length)
                        cache[cCount++] = (unit.Radius, unit.Caps, unit.Surface, res);
                }
            }
        }

        int totalQueries = 0;
        int iterations = 10000;
        Stopwatch sw = Stopwatch.StartNew();
        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();

        for (int iter = 0; iter < iterations; iter++)
        {
            int cCount = 0;
            for (int i = 0; i < count; i++)
            {
                var unit = army[i];
                bool found = false;
                for (int c = 0; c < cCount; c++)
                {
                    if (cache[c].Radius == unit.Radius && cache[c].Caps == unit.Caps && cache[c].Surface == unit.Surface)
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    totalQueries++;
                    DestinationEvaluationResult res = evaluator.Evaluate(target, unit.Radius, unit.Caps, unit.Surface);
                    if (cCount < cache.Length)
                        cache[cCount++] = (unit.Radius, unit.Caps, unit.Surface, res);
                }
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;
        sw.Stop();
        double totalMs = sw.Elapsed.TotalMilliseconds;
        double avgUsPerFrame = (totalMs * 1000.0) / iterations;
        int queriesPerFrame = totalQueries / iterations;

        Console.WriteLine($"Selected Units: {count,3} | Actual Queries: {queriesPerFrame} | Avg Time: {avgUsPerFrame:F3} us/frame | Allocated: {allocated} B");
    }
}

static void RunVehicleSeparationBenchmark()
{
    Console.WriteLine("=== Dynamic Vehicle Separation and Distance Keeping Benchmark ===");
    Console.WriteLine("Budget target: p99 < 15.000 ms for 1000 vehicles on open map\n");

    int[] counts = [1, 20, 200, 1000];
    const int resolution = 501; // 501x501 at 2.0m spacing = 1000m x 1000m

    Console.WriteLine("Scenario A: Multi-Stream Intersecting Traffic (4 Converging Streams)");
    foreach (int count in counts)
    {
        HeightMap map = new(resolution, resolution, 2.0f, 0.01f, new ushort[resolution * resolution]);
        NavigationGrid grid = new(map, [], enableHierarchical: true);

        TestSeparationMovementCatalog catalog = new();
        NavigationSystem nav = new(grid, catalog, new NavigationTuning(MaxPendingRequests: 2048), maxUnits: Math.Max(2048, count + 128));
        WorldState world = new(map, capacity: Math.Max(2048, count + 128));
        using Simulation sim = new(world, new RuleSet(nav));

        EntityId[] unitIds = new EntityId[count];
        Vector2[] targets = new Vector2[count];
        Vector2[] startPositions = new Vector2[count];

        const int streamLanes = 10;
        for (int i = 0; i < count; i++)
        {
            int stream = i % 4;
            int streamIdx = i / 4;
            int lane = streamIdx % streamLanes;
            int row = streamIdx / streamLanes;

            Vector2 start;
            Vector2 target;
            float yaw;

            switch (stream)
            {
                case 0: // North to South (+Y), crossing Stream 3 and Stream 2
                    start = new Vector2(350f + lane * 14f, 300f - row * 10f);
                    target = new Vector2(start.X, start.Y + 650f);
                    yaw = MathF.PI;
                    break;
                case 1: // South to North (-Y), crossing Stream 2 and Stream 3
                    start = new Vector2(524f + lane * 14f, 700f + row * 10f);
                    target = new Vector2(start.X, start.Y - 650f);
                    yaw = 0f;
                    break;
                case 2: // West to East (+X), crossing Stream 0 and Stream 1
                    start = new Vector2(300f - row * 10f, 524f + lane * 14f);
                    target = new Vector2(start.X + 650f, start.Y);
                    yaw = -MathF.PI * 0.5f;
                    break;
                default: // East to West (-X), crossing Stream 1 and Stream 0
                    start = new Vector2(700f + row * 10f, 350f + lane * 14f);
                    target = new Vector2(start.X - 650f, start.Y);
                    yaw = MathF.PI * 0.5f;
                    break;
            }

            startPositions[i] = start;
            EntityId id = world.CreateUnit(1, 1, start, 8.0f, initialTurretYaw: yaw);
            unitIds[i] = id;
            targets[i] = target;
        }

        // Verify all initial placements are strictly non-overlapping (>= 7.0m separation)
        for (int a = 0; a < count; a++)
        {
            for (int b = a + 1; b < count; b++)
            {
                float d = Vector2.Distance(startPositions[a], startPositions[b]);
                if (d < 7.0f)
                {
                    throw new InvalidOperationException($"Initial overlap between units {a} and {b}: distance {d:F2}m < 7.0m");
                }
            }
        }

        // Enqueue in verified batches of <= 200 per tick so no command is dropped
        int enqueued = 0;
        uint cmdId = 1;
        while (enqueued < count)
        {
            int batch = Math.Min(200, count - enqueued);
            for (int b = 0; b < batch; b++)
            {
                int idx = enqueued + b;
                bool ok = sim.TryEnqueue(1, new MoveCommand(cmdId++, sim.Tick, unitIds[idx], targets[idx]));
                if (!ok) throw new InvalidOperationException($"Failed to enqueue command for unit {idx}!");
            }
            enqueued += batch;
            sim.Step();
        }

        // Warmup 120 ticks so stream fronts converge into the intersection conflict zones
        for (int i = 0; i < 120; i++)
        {
            sim.Step();
        }

        // Measure 200 ticks
        const int measuredTicks = 200;
        double[] tickTimesMs = new double[measuredTicks];
        Stopwatch sw = new();

        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < measuredTicks; t++)
        {
            sw.Restart();
            sim.Step();
            sw.Stop();
            tickTimesMs[t] = sw.Elapsed.TotalMilliseconds;
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;

        int moving = 0;
        int waiting = 0;
        for (int i = 0; i < count; i++)
        {
            if (nav.GetUnitSpeed(unitIds[i]) > 0.001f) moving++;
            if (nav.GetUnitStatus(unitIds[i]) == PathRequestStatus.WaitingForClearance) waiting++;
        }

        Array.Sort(tickTimesMs);
        double mean = tickTimesMs.Average();
        double min = tickTimesMs[0];
        double max = tickTimesMs[^1];
        double p50 = tickTimesMs[(int)(measuredTicks * 0.50)];
        double p95 = tickTimesMs[(int)(measuredTicks * 0.95)];
        double p99 = tickTimesMs[(int)(measuredTicks * 0.99)];

        string status = p99 < 15.0 ? "PASS" : "FAIL";
        Console.WriteLine($"Units: {count,4} (active: {moving + waiting,4}, moving: {moving,4}, waiting: {waiting,4}) | Mean: {mean,6:F3} ms | Min: {min,6:F3} ms | p50: {p50,6:F3} ms | p95: {p95,6:F3} ms | p99: {p99,6:F3} ms | Alloc/tick: {allocatedBytes / measuredTicks,5} B | [{status}]");
    }

    Console.WriteLine("\nScenario B: Free Parallel Motion (1000 Vehicles on Parallel Tracks)");
    {
        const int count = 1000;
        HeightMap map = new(resolution, resolution, 2.0f, 0.01f, new ushort[resolution * resolution]);
        NavigationGrid grid = new(map, [], enableHierarchical: true);

        TestSeparationMovementCatalog catalog = new();
        NavigationSystem nav = new(grid, catalog, new NavigationTuning(MaxPendingRequests: 2048), maxUnits: Math.Max(2048, count + 128));
        WorldState world = new(map, capacity: Math.Max(2048, count + 128));
        using Simulation sim = new(world, new RuleSet(nav));

        EntityId[] unitIds = new EntityId[count];
        Vector2[] targets = new Vector2[count];
        int cols = 40;

        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            Vector2 start = new(80f + col * 20f, 50f + row * 15f);
            Vector2 target = new(start.X, start.Y + 400f);
            EntityId id = world.CreateUnit(1, 1, start, 8.0f, initialTurretYaw: MathF.PI);
            unitIds[i] = id;
            targets[i] = target;
        }

        int enqueued = 0;
        uint cmdId = 1;
        while (enqueued < count)
        {
            int batch = Math.Min(200, count - enqueued);
            for (int b = 0; b < batch; b++)
            {
                int idx = enqueued + b;
                bool ok = sim.TryEnqueue(1, new MoveCommand(cmdId++, sim.Tick, unitIds[idx], targets[idx]));
                if (!ok) throw new InvalidOperationException($"Failed to enqueue command for unit {idx}!");
            }
            enqueued += batch;
            sim.Step();
        }

        for (int i = 0; i < 20; i++)
        {
            sim.Step();
        }

        const int measuredTicks = 200;
        double[] tickTimesMs = new double[measuredTicks];
        Stopwatch sw = new();

        long beforeAllocated = GC.GetAllocatedBytesForCurrentThread();
        for (int t = 0; t < measuredTicks; t++)
        {
            sw.Restart();
            sim.Step();
            sw.Stop();
            tickTimesMs[t] = sw.Elapsed.TotalMilliseconds;
        }
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeAllocated;

        int moving = 0;
        int waiting = 0;
        for (int i = 0; i < count; i++)
        {
            if (nav.GetUnitSpeed(unitIds[i]) > 0.001f) moving++;
            if (nav.GetUnitStatus(unitIds[i]) == PathRequestStatus.WaitingForClearance) waiting++;
        }

        Array.Sort(tickTimesMs);
        double mean = tickTimesMs.Average();
        double min = tickTimesMs[0];
        double max = tickTimesMs[^1];
        double p50 = tickTimesMs[(int)(measuredTicks * 0.50)];
        double p95 = tickTimesMs[(int)(measuredTicks * 0.95)];
        double p99 = tickTimesMs[(int)(measuredTicks * 0.99)];

        string status = p99 < 15.0 ? "PASS" : "FAIL";
        Console.WriteLine($"Units: {count,4} (active: {moving + waiting,4}, moving: {moving,4}, waiting: {waiting,4}) | Mean: {mean,6:F3} ms | Min: {min,6:F3} ms | p50: {p50,6:F3} ms | p95: {p95,6:F3} ms | p99: {p99,6:F3} ms | Alloc/tick: {allocatedBytes / measuredTicks,5} B | [{status}]");
    }
    Console.WriteLine();
}

[MemoryDiagnoser]
public class MovementBenchmark
{
    private const int TicksPerBatch = 131072;
    private WorldState _world = null!;
    private Simulation _simulation = null!;
    private PlayerKnowledge _knowledge = null!;
    private byte[] _snapshot = null!;
    [Params(100, 1000)] public int Units { get; set; }

    [IterationSetup]
    public void Setup()
    {
        _world = new WorldState(HeightMap.CreatePrototype());
        _simulation = new Simulation(_world, new SandboxRules());
        _knowledge = new PlayerKnowledge(1);
        _snapshot = new byte[WireProtocol.MaxMessageSize];
        for (int i = 0; i < Units; i++)
        {
            // Slow movement keeps every unit active for the entire measured batch.
            EntityId id = _world.CreateUnit(1, 0, new Vector2(i % 32 * 3, i / 32 * 3), 0.001f);
            if (!_simulation.TryEnqueue(1, new MoveCommand((uint)(i + 1), _simulation.Tick, id, new Vector2(120, 120))))
                throw new InvalidOperationException("Benchmark command queue is full.");
            if (i % 200 == 199) _simulation.Step();
        }
    }

    [Benchmark(OperationsPerInvoke = TicksPerBatch)]
    public long MovingWorldAndSnapshot()
    {
        // Batch ticks to avoid measuring only timer noise. Setup and scene reset stay outside timing.
        long bytes = 0;
        for (int i = 0; i < TicksPerBatch; i++)
        {
            _simulation.Step();
            _simulation.UpdateKnowledge(_knowledge);
            bytes += WireProtocol.WriteSnapshot(_snapshot, 1, _simulation.Tick, _knowledge.OwnUnits);
        }
        return bytes;
    }

    [IterationCleanup] public void Cleanup() => _simulation.Dispose();
}

internal sealed class SlowMovementCatalog : IUnitMovementCatalog
{
    public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
    {
        profile = new UnitMovementProfile(0.001f, 1.0f);
        return true;
    }
}

internal sealed class TestSeparationMovementCatalog : IUnitMovementCatalog
{
    public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
    {
        profile = new UnitMovementProfile(
            Speed: 8.0f,
            Radius: 1.5f,
            TurnRate: 30.0f,
            ReverseSpeed: 4.0f,
            Acceleration: 2.0f,
            Deceleration: 4.0f,
            CanPivot: true,
            MinTurnRadius: 0.0f,
            CollisionRadius: 3.5f,
            Height: 2.4f);
        return true;
    }
}

[MemoryDiagnoser]
public class VehicleSeparationBenchmark
{
    private WorldState _world = null!;
    private Simulation _simulation = null!;
    private const int TicksPerBatch = 50;

    [Params(1, 20, 200, 1000)]
    public int Units { get; set; }

    [IterationSetup]
    public void Setup()
    {
        const int resolution = 501;
        HeightMap map = new(resolution, resolution, 2.0f, 0.01f, new ushort[resolution * resolution]);
        NavigationGrid grid = new(map, [], enableHierarchical: true);
        TestSeparationMovementCatalog catalog = new();
        NavigationSystem nav = new(grid, catalog, new NavigationTuning(MaxPendingRequests: 2048), maxUnits: Math.Max(2048, Units + 128));
        _world = new WorldState(map, capacity: Math.Max(2048, Units + 128));
        _simulation = new Simulation(_world, new RuleSet(nav));

        int cols = (int)MathF.Ceiling(MathF.Sqrt(Units));
        for (int i = 0; i < Units; i++)
        {
            int col = i % cols;
            int row = i / cols;
            Vector2 start = new(100f + col * 20f, 100f + row * 15f);
            Vector2 target = new(start.X, start.Y + 400f);
            EntityId id = _world.CreateUnit(1, 1, start, 8.0f, initialTurretYaw: MathF.PI);
            bool ok = _simulation.TryEnqueue(1, new MoveCommand((uint)(i + 1), _simulation.Tick, id, target));
            if (!ok)
            {
                throw new InvalidOperationException($"Failed to enqueue move command for unit {i} (admission failure)!");
            }
        }

        for (int i = 0; i < 20; i++)
        {
            _simulation.Step();
        }
    }

    [Benchmark(OperationsPerInvoke = TicksPerBatch)]
    public void SimulationStep()
    {
        for (int i = 0; i < TicksPerBatch; i++)
        {
            _simulation.Step();
        }
    }

    [IterationCleanup]
    public void Cleanup() => _simulation.Dispose();
}
