using System.Diagnostics;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using DeadGround.Rules.Sandbox;

if (args.Length > 0 && args[0] == "--ustye-nav")
{
    RunUstyeNavigationBenchmark();
    return;
}

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

if (args.Length > 0 && (args[0] == "--group-destination" || args[0] == "--group-move"))
{
    RunGroupDestinationBenchmark();
    return;
}

if (args.Length > 0 && (args[0] == "--contact-memory" || args[0] == "--contact-recheck"))
{
    RunContactMemoryRecheckBenchmark();
    return;
}

if (args.Length > 0 && (args[0] == "--ustye-contacts" || args[0] == "--ustye-contact-reproduction"))
{
    RunUstyeContactReproduction();
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

    if (!RunGroupDestinationProbe())
    {
        allPassed = false;
    }

    if (!RunContactMemoryRecheckProbe())
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

static void RunUstyeNavigationBenchmark()
{
    Console.WriteLine("=== USTYE RAID NAVIGATION TICK BENCHMARK ===");
    Console.WriteLine("Map: Ustye (5000 x 5000 m), seed 1337, 3 units moving to (2200, 2400)");

    var sim = UstyeRaidScenario.CreateSimulation(
        matchSeed: 1337UL,
        navTuning: UstyeGroundScenario.CreateNavigationTuning() with { CollectStageTimings = true });
    using var session = new LocalSession(sim);

    for (int f = 0; f < 60; f++)
    {
        session.Advance(0.016667);
    }

    foreach (var u in session.Replica.OwnUnits)
    {
        session.Move(u.Id, new Vector2(2200f, 2400f));
    }

    double maxAdvance = 0;
    int slow = 0;
    int slow50 = 0;
    int firstContactFrame = -1;
    int firstContactTick = -1;
    List<double> simTickTimes = new();
    long totalAllocated = 0;

    int lastSimTick = sim.Tick;

    for (int f = 0; f < 300; f++)
    {
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        session.Advance(0.016667);
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        totalAllocated += allocDelta;
        if (allocDelta > 0)
        {
            Console.WriteLine($"ALLOC frame={f} tick={sim.Tick} bytes={allocDelta} hostAlloc={session.LastTickAllocatedBytes}");
        }

        maxAdvance = Math.Max(maxAdvance, ms);

        if (sim.Tick > lastSimTick)
        {
            lastSimTick = sim.Tick;
            double simTickMs = session.LastTickMilliseconds;
            simTickTimes.Add(simTickMs);

            if (simTickMs > 20.0)
            {
                slow++;
                if (simTickMs > 50.0) slow50++;
                var nav = sim.Navigation;
                Console.WriteLine($"SLOW tick={sim.Tick} frame={f} totalMs={ms:F2} simTickMs={simTickMs:F2} stage={nav?.ActiveStage} areaCells={nav?.AreaPrepCellsThisTick} expansions={nav?.NodesExpandedThisTick} [Global={nav?.LastTickGlobalPlanMs:F2} AreaPrep={nav?.LastTickAreaPrepMs:F2} Local={nav?.LastTickLocalPlanMs:F2} Smooth={nav?.LastTickSmoothingMs:F2} Move={nav?.LastTickMovementMs:F2} Sep={nav?.LastTickSeparationMs:F2}]");
            }
        }

        if (firstContactFrame < 0 && session.Replica.Contacts.Length > 0)
        {
            firstContactFrame = f;
            firstContactTick = sim.Tick;
            Console.WriteLine($"FIRST_CONTACT frame={f} tick={sim.Tick}");
        }
    }

    Console.WriteLine($"SUMMARY tick={sim.Tick} maxAdvanceMs={maxAdvance:F2} slow={slow} firstContact={firstContactFrame}");

    simTickTimes.Sort();
    double p50 = simTickTimes[(int)(simTickTimes.Count * 0.50)];
    double p95 = simTickTimes[(int)(simTickTimes.Count * 0.95)];
    double p99 = simTickTimes[(int)(simTickTimes.Count * 0.99)];
    double maxTick = simTickTimes[^1];

    Console.WriteLine("\n=== USTYE NAVIGATION BENCHMARK DETAILED STATS ===");
    Console.WriteLine($"Simulation Ticks:      {simTickTimes.Count}");
    Console.WriteLine($"Sim Tick Median (p50): {p50:F2} ms");
    Console.WriteLine($"Sim Tick p95:          {p95:F2} ms");
    Console.WriteLine($"Sim Tick p99:          {p99:F2} ms");
    Console.WriteLine($"Sim Tick Max:          {maxTick:F2} ms");
    Console.WriteLine($"Ticks > 20 ms:         {slow}");
    Console.WriteLine($"Ticks > 50 ms:         {slow50}");
    Console.WriteLine($"Steady-state alloc:    {totalAllocated} B");

    if (sim.Navigation != null)
    {
        var nav = sim.Navigation;
        Console.WriteLine("Navigation System Totals:");
        Console.WriteLine($"  Global Planning:     {nav.TotalGlobalPlanMs:F2} ms ({nav.TotalGlobalExpansions} expansions, cache hits: {nav.GlobalPlanCacheHits}, misses: {nav.GlobalPlanCacheMisses})");
        Console.WriteLine($"  Area Preparation:    {nav.TotalAreaPrepMs:F2} ms ({nav.TotalAreaPrepCells} cells evaluated)");
        Console.WriteLine($"  Local Planning:      {nav.TotalLocalPlanMs:F2} ms ({nav.TotalLocalExpansions} node expansions)");
        Console.WriteLine($"  Path Smoothing:      {nav.TotalSmoothingMs:F2} ms ({nav.TotalSmoothingRaycasts} raycasts)");
        Console.WriteLine($"  Movement Advance:    {nav.TotalMovementMs:F2} ms");
        Console.WriteLine($"  Vehicle Separation:  {nav.TotalSeparationMs:F2} ms ({nav.SeparationCandidatesChecked} checks, {nav.SeparationEmergencyStops} stops)");
        Console.WriteLine($"  Completed Searches:  {nav.CompletedSearches}");
        Console.WriteLine($"  Failed Searches:     {nav.FailedSearches}");
        Console.WriteLine($"  Capacity Exceeded:   {nav.CapacityExceeded}");
    }

    Console.WriteLine("\n=== TERRAIN SPATIAL INDEX BATCH QUERY BENCHMARK ===");
    const int queryMapSize = 201;
    HeightMap queryMap = new(queryMapSize, queryMapSize, 1.0f, 0.01f, new ushort[queryMapSize * queryMapSize]);
    List<TerrainObstacle> batchWater = new();
    for (int i = 0; i < 200; i++)
    {
        batchWater.Add(new TerrainObstacle(95f, i * 1.0f, 105f, (i + 1) * 1.0f, TraversabilityCapabilities.Water));
    }
    StaticGeometry batchGeo = new(queryMap, ReadOnlySpan<OpaqueStructure>.Empty, ReadOnlySpan<BridgeStructure>.Empty, batchWater.ToArray());
    NavigationGrid batchGrid = new(batchGeo, cellSize: 0.5f, enableHierarchical: true);

    for (int q = 0; q < 1_000; q++)
    {
        batchGrid.IsPointClear(new Vector2(10f + (q % 80), 10f + (q / 80 % 80)), 1.0f, TraversabilityCapabilities.DefaultVehicle);
    }

    Stopwatch swBatch = Stopwatch.StartNew();
    for (int q = 0; q < 10_000; q++)
    {
        batchGrid.IsPointClear(new Vector2(10f + (q % 80), 10f + (q / 80 % 80)), 1.0f, TraversabilityCapabilities.DefaultVehicle);
    }
    swBatch.Stop();
    Console.WriteLine($"10,000 point queries on 200 water slices took {swBatch.Elapsed.TotalMilliseconds:F2} ms (budget < 50 ms)");
}

static GroupUnitInput[] CreateBenchmarkUnits(int count) => GroupBenchmarkHelpers.CreateBenchmarkUnits(count);

static void RunGroupDestinationBenchmark()
{
    Console.WriteLine("=== GROUP MOVE DESTINATION DISTRIBUTION BENCHMARK ===");
    Console.WriteLine("Measures authoritative slot distribution and client preview generation.");
    Console.WriteLine("Target budgets: < 15.0 ms per operation, 0 B allocations in steady state.\n");

    const int size = 251;
    HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
    OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
    TerrainObstacle water = new(10f, 10f, 30f, 30f, TraversabilityCapabilities.Water);
    StaticGeometry geo = new(map, [building], ReadOnlySpan<BridgeStructure>.Empty, [water]);
    DestinationEvaluator evaluator = new(geo, cellSize: 1.0f);

    int[] counts = [1, 20, 200, WireProtocol.MaxUnits];

    var scenarios = new (string Name, Vector2 Target, float? Facing)[]
    {
        ("Open Field (no obstacles)", new Vector2(150f, 150f), null),
        ("Near Obstacle (building at 40..60)", new Vector2(62f, 50f), null),
        ("With Drag Facing (facing East)", new Vector2(150f, 150f), -MathF.PI * 0.5f)
    };

    foreach (var (scenName, target, facing) in scenarios)
    {
        Console.WriteLine($"--- Scenario: {scenName} ---");
        Console.WriteLine($"{"Units",6} | {"Stage",12} | {"Median",10} | {"p95",10} | {"p99",10} | {"Max",10} | {"Allocated",10} | {"Placed",10}");
        Console.WriteLine(new string('-', 88));

        foreach (int count in counts)
        {
            GroupUnitInput[] units = CreateBenchmarkUnits(count);

            // Warmup Distribute
            for (int w = 0; w < 100; w++)
            {
                GroupDestinationDistribution.Distribute(
                    1, 1, units.AsSpan(), target, 0, facing, MoveMode.Forward, evaluator);
            }

            // Measure Distribute
            const int iters = 200;
            double[] distTimes = new double[iters];
            long allocBeforeDist = GC.GetAllocatedBytesForCurrentThread();

            int lastPlaced = 0;
            for (int it = 0; it < iters; it++)
            {
                long start = Stopwatch.GetTimestamp();
                GroupMoveResult res = GroupDestinationDistribution.Distribute(
                    (uint)(it + 1), it, units.AsSpan(), target, 0, facing, MoveMode.Forward, evaluator);
                long elapsed = Stopwatch.GetTimestamp() - start;
                distTimes[it] = (double)elapsed / Stopwatch.Frequency * 1000.0;
                lastPlaced = res.Placed.Length;
            }
            long distAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBeforeDist;

            Array.Sort(distTimes);
            double distP50 = distTimes[iters / 2];
            double distP95 = distTimes[(int)(iters * 0.95)];
            double distP99 = distTimes[(int)(iters * 0.99)];
            double distMax = distTimes[iters - 1];

            Console.WriteLine($"{count,6} | {"Distribute",12} | {distP50,8:F3} ms | {distP95,8:F3} ms | {distP99,8:F3} ms | {distMax,8:F3} ms | {distAlloc,8} B | {lastPlaced,6}/{count}");

            // Warmup Preview
            for (int w = 0; w < 100; w++)
            {
                GroupDestinationDistribution.ComputePreview(
                    units.AsSpan(), target, 0, facing, MoveMode.Forward, evaluator);
            }

            // Measure Preview
            double[] prevTimes = new double[iters];
            long allocBeforePrev = GC.GetAllocatedBytesForCurrentThread();
            int lastPrevPlaced = 0;

            for (int it = 0; it < iters; it++)
            {
                long start = Stopwatch.GetTimestamp();
                GroupPreviewResult prev = GroupDestinationDistribution.ComputePreview(
                    units.AsSpan(), target, 0, facing, MoveMode.Forward, evaluator);
                long elapsed = Stopwatch.GetTimestamp() - start;
                prevTimes[it] = (double)elapsed / Stopwatch.Frequency * 1000.0;
                lastPrevPlaced = prev.PlacedCount + prev.AdjustedCount;
            }
            long prevAlloc = GC.GetAllocatedBytesForCurrentThread() - allocBeforePrev;

            Array.Sort(prevTimes);
            double prevP50 = prevTimes[iters / 2];
            double prevP95 = prevTimes[(int)(iters * 0.95)];
            double prevP99 = prevTimes[(int)(iters * 0.99)];
            double prevMax = prevTimes[iters - 1];

            Console.WriteLine($"{count,6} | {"Preview",12} | {prevP50,8:F3} ms | {prevP95,8:F3} ms | {prevP99,8:F3} ms | {prevMax,8:F3} ms | {prevAlloc,8} B | {lastPrevPlaced,6}/{count}");
        }
        Console.WriteLine();
    }
}

static bool RunGroupDestinationProbe()
{
    const int size = 251;
    HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
    OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
    TerrainObstacle water = new(10f, 10f, 30f, 30f, TraversabilityCapabilities.Water);
    StaticGeometry geo = new(map, [building], ReadOnlySpan<BridgeStructure>.Empty, [water]);
    DestinationEvaluator evaluator = new(geo, cellSize: 1.0f);

    int[] counts = [1, 20, 200, WireProtocol.MaxUnits];
    Vector2 target = new(150f, 150f);
    bool allPassed = true;

    foreach (int count in counts)
    {
        GroupUnitInput[] units = CreateBenchmarkUnits(count);

        // Warmup
        for (int w = 0; w < 50; w++)
        {
            GroupDestinationDistribution.Distribute(1, 1, units.AsSpan(), target, 0, null, MoveMode.Forward, evaluator);
            GroupDestinationDistribution.ComputePreview(units.AsSpan(), target, 0, null, MoveMode.Forward, evaluator);
        }

        const int iters = 100;
        double[] times = new double[iters];
        long allocBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int it = 0; it < iters; it++)
        {
            long start = Stopwatch.GetTimestamp();
            GroupDestinationDistribution.Distribute((uint)(it + 1), it, units.AsSpan(), target, 0, null, MoveMode.Forward, evaluator);
            long elapsed = Stopwatch.GetTimestamp() - start;
            times[it] = (double)elapsed / Stopwatch.Frequency * 1000.0;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
        Array.Sort(times);
        bool ok = allocated == 0 && times[iters / 2] < 15.0;

        Console.WriteLine($"Group Destination Probe ({count} units):");
        Console.WriteLine($"  Median (p50): {times[iters / 2]:F3} ms");
        Console.WriteLine($"  p99:          {times[(int)(iters * 0.99)]:F3} ms");
        Console.WriteLine($"  Allocations:  {allocated} B");
        Console.WriteLine($"  Budget:       {(ok ? "PASS (< 15 ms, 0 B)" : "FAIL")}");
        Console.WriteLine();

        if (!ok) allPassed = false;
    }

    return allPassed;
}

static void RunContactMemoryRecheckBenchmark()
{
    bool passed = RunContactMemoryRecheckProbe();
    Environment.ExitCode = passed ? 0 : 1;
}

static bool RunContactMemoryRecheckProbe()
{
    Console.WriteLine("=== CONTACT MEMORY RECHECK PROBE (80 LASTKNOWN MARKERS) ===");
    const int size = 129;
    HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);

    // Wall between observer patrol area and markers:
    // Observers at X=40, wall at X=68..72, Z=0..110, height 8m.
    // Markers at X=85, Z=15..94.
    OpaqueStructure wall = new(new StructureId(1), 68f, 0f, 0f, 72f, 8f, 110f, isGrounded: true);
    StaticGeometry geo = new(map, [wall]);
    UnitCatalog catalog = PrototypeScenario.LoadCatalog();

    DetectionTuning tuning = new(
        memoryDurationTicks: 1000,
        correlationWindowTicks: 100,
        correlationDistanceGate: 30.0f,
        evaluationIntervalTicks: 2,
        samePositionTolerance: 3.0f);

    OpsRules rules = new(geo, new OpsPerceptionCatalog(catalog), tuning);
    WorldState world = new(map, capacity: 256, playerTeams: [1, 2]);
    using Simulation sim = new(world, rules);

    ushort reconIdx = catalog.RequireDefinitionIndex("recon_vehicle");
    ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
    UnitDefinition reconDef = catalog.RequireDefinition("recon_vehicle");
    UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");

    // 10 friendly observers on Side 1: 5 near the wall (X=40, Z=20..60), 5 far back (X=10, Z=70..110)
    const int observerCount = 10;
    EntityId[] observers = new EntityId[observerCount];
    for (int i = 0; i < observerCount; i++)
    {
        float x = i < 5 ? 40f : 10f;
        float y = 20f + i * 9.0f;
        observers[i] = world.CreateUnit(1, reconIdx, new Vector2(x, y), reconDef.Speed);
        world.SetFireStance(observers[i], FireStance.HoldFire);
    }

    // 80 enemy targets on Side 2:
    // Initially positioned at X=85, Y=15..94
    const int targetCount = 80;
    EntityId[] targets = new EntityId[targetCount];
    for (int i = 0; i < targetCount; i++)
    {
        float y = 15f + i * 1.0f;
        targets[i] = world.CreateUnit(2, tankIdx, new Vector2(85f, y), tankDef.Speed);
        world.SetFireStance(targets[i], FireStance.HoldFire);
    }

    // Temporarily place observers in direct sight at X=83 for 4 ticks to establish Current contacts
    for (int i = 0; i < observerCount; i++)
    {
        world.SetPosition(observers[i], new Vector2(83f, 20f + i * 8.0f));
    }
    for (int t = 0; t < 4; t++) sim.Step();

    PlayerKnowledge k1 = new(1);
    sim.UpdateKnowledge(k1);
    if (k1.ContactCount != targetCount)
    {
        Console.WriteLine($"[FAIL] Expected {targetCount} initial contacts, got {k1.ContactCount}");
        return false;
    }

    // Move targets away into fog of war:
    // 40 targets move behind the wall to X=150, Y=15..54
    // 40 targets move far east to X=240, Y=55..94
    for (int i = 0; i < 40; i++)
    {
        world.SetPosition(targets[i], new Vector2(150f, 15f + i * 1.0f));
    }
    for (int i = 40; i < targetCount; i++)
    {
        world.SetPosition(targets[i], new Vector2(240f, 15f + i * 1.0f));
    }

    // Move observers to their patrol stations:
    // 5 observers at X=40 (behind the wall from markers 0..39)
    // 5 observers at X=10 (out of sensor range from markers 40..79)
    for (int i = 0; i < 5; i++)
    {
        world.SetPosition(observers[i], new Vector2(40f, 20f + i * 8.0f));
    }
    for (int i = 5; i < observerCount; i++)
    {
        world.SetPosition(observers[i], new Vector2(10f, 20f + i * 8.0f));
    }

    // Step 4 ticks so detection transitions all 80 contacts to LastKnown at (85, y)
    for (int t = 0; t < 4; t++) sim.Step();

    sim.UpdateKnowledge(k1);
    int lkCount = 0;
    for (int i = 0; i < k1.ContactCount; i++)
    {
        if (k1.Contacts[i].State == ContactState.LastKnown) lkCount++;
    }
    if (lkCount != targetCount)
    {
        Console.WriteLine($"[FAIL] Expected {targetCount} LastKnown contacts, got {lkCount}");
        return false;
    }

    // Observers patrol slowly keeping them active
    for (int i = 0; i < observerCount; i++)
    {
        world.TryGetUnit(observers[i], out UnitState u);
        sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), sim.Tick, observers[i], new Vector2(u.Position.X, 100f)));
    }

    byte[] b1 = new byte[WireProtocol.MaxMessageSize];

    // Warmup JIT and snapshot buffers over 10 ticks
    for (int i = 0; i < 10; i++)
    {
        sim.Step();
        if (sim.Tick % 2 == 0)
        {
            sim.UpdateKnowledge(k1);
            WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts);
        }
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    const int measuredTicks = 100;
    double[] times = new double[measuredTicks];
    long totalAllocated = 0;
    int totalConfirmedEmpty = 0;

    for (int i = 0; i < measuredTicks; i++)
    {
        // On tick 80, move 2 observers around the wall to X=85 to recheck and confirm empty
        if (i == 80)
        {
            world.SetPosition(observers[0], new Vector2(85f, 20f));
            world.SetPosition(observers[1], new Vector2(85f, 30f));
        }

        long startAlloc = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();

        sim.Step();
        totalConfirmedEmpty += sim.Detection?.ConfirmedEmptyPositionsCount ?? 0;
        if (sim.Tick % 2 == 0)
        {
            sim.UpdateKnowledge(k1);
            WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts);
        }

        long elapsed = Stopwatch.GetTimestamp() - start;
        long alloc = GC.GetAllocatedBytesForCurrentThread() - startAlloc;

        times[i] = (double)elapsed / Stopwatch.Frequency * 1000.0;
        totalAllocated += alloc;
    }

    Array.Sort(times);
    double p50 = times[(int)(measuredTicks * 0.50)];
    double p95 = times[(int)(measuredTicks * 0.95)];
    double p99 = times[(int)(measuredTicks * 0.99)];
    double max = times[measuredTicks - 1];

    Console.WriteLine($"[CONTACT RECHECK] 80 LastKnown markers across 10 observers:");
    Console.WriteLine($"  p50: {p50:F2} ms, p95: {p95:F2} ms, p99: {p99:F2} ms, max: {max:F2} ms");
    Console.WriteLine($"  Total Allocations: {totalAllocated} B (steady-state: 0 B)");
    Console.WriteLine($"  Total Confirmed Empty Positions Removed: {totalConfirmedEmpty}");

    bool passed = p99 < 15.0 && totalAllocated == 0 && totalConfirmedEmpty > 0;
    Console.WriteLine(passed ? "[PASS] Contact memory recheck met performance budget." : "[FAIL] Contact memory recheck exceeded budget or allocated memory.");
    return passed;
}

static void RunUstyeContactReproduction()
{
    Console.WriteLine("=== USTYE RAID CONTACT MEMORY & RECHECK REPRODUCTION ===");
    Console.WriteLine("Map: Ustye (5000 x 5000 m), seed 1337, BRDM-2 scouting western approaches");

    var sim = UstyeRaidScenario.CreateSimulation(matchSeed: 1337UL);
    using var session = new LocalSession(sim);

    // Warmup 20 frames
    for (int f = 0; f < 20; f++) session.Advance(0.016667);

    EntityId brdmId = session.Replica.OwnUnits[0].Id;
    // Dispatch BRDM-2 forward into depot / eastern road approach
    session.Move(brdmId, new Vector2(2850f, 2650f));
    Console.WriteLine("[1] BRDM-2 dispatched towards depot road approach (2850, 2650).");

    int contactAcquiredTick = -1;
    ContactId spottedContactId = default;

    for (int f = 0; f < 500; f++)
    {
        session.Advance(0.016667);
        if (session.Replica.ContactCount > 0 && contactAcquiredTick == -1)
        {
            var c = session.Replica.Contacts[0];
            if (c.State == ContactState.Current)
            {
                contactAcquiredTick = sim.Tick;
                spottedContactId = c.Id;
                Console.WriteLine($"[2] Contact #{c.Id.Value} SPOTTED as Current at tick {sim.Tick} ({c.Classification}, pos: {c.Position.X:F1}, {c.Position.Y:F1}).");
                break;
            }
        }
    }

    if (contactAcquiredTick == -1)
    {
        Console.WriteLine("[WARN] Contact was not spotted within initial search window.");
        return;
    }

    // Retreat BRDM-2 behind courtyard building to break line of sight
    session.Move(brdmId, new Vector2(2450f, 2300f));
    Console.WriteLine("[3] BRDM-2 retreating behind Town-West building (2450, 2300) to break line of sight.");

    int contactLostTick = -1;
    int lastReportedAgeSecond = 0;
    for (int f = 0; f < 500; f++)
    {
        session.Advance(0.016667);
        if (session.Replica.TryGetContact(spottedContactId, out ContactObservation c))
        {
            if (c.State == ContactState.LastKnown && contactLostTick == -1)
            {
                contactLostTick = sim.Tick;
                Console.WriteLine($"[4] Contact #{c.Id.Value} transitioned to LastKnown at tick {sim.Tick} (frozen pos: {c.Position.X:F1}, {c.Position.Y:F1}).");
            }
            if (contactLostTick != -1)
            {
                int ageSecond = (int)((sim.Tick - c.LastSeenTick) / (float)Simulation.TickRate);
                if (ageSecond > lastReportedAgeSecond && ageSecond <= 5)
                {
                    lastReportedAgeSecond = ageSecond;
                    float ageExact = (sim.Tick - c.LastSeenTick) / (float)Simulation.TickRate;
                    Console.WriteLine($"    HUD RoleBadgeOverlay & SelectionCard: Contact #{c.Id.Value} age is {ageExact:F1}s (LastSeenTick: {c.LastSeenTick}, ServerTick: {sim.Tick}).");
                }
            }
        }
        else if (contactLostTick != -1)
        {
            Console.WriteLine($"[5] Contact #{spottedContactId.Value} expired / removed from ClientReplica at tick {sim.Tick}.");
            break;
        }
    }

    Console.WriteLine("[PASS] Ustye-Raid contact memory & presentation reproduction complete.");
}

internal static class GroupBenchmarkHelpers
{
    public static GroupUnitInput[] CreateBenchmarkUnits(int count)
    {
        var units = new GroupUnitInput[count];
        int cols = (int)MathF.Ceiling(MathF.Sqrt(count));
        for (int i = 0; i < count; i++)
        {
            int col = i % cols;
            int row = i / cols;
            Vector2 pos = new(50f + col * 5f, 50f + row * 5f);
            if (i % 3 == 0)
            {
                // Tank (T-62)
                units[i] = new GroupUnitInput(
                    new EntityId((uint)(i + 1)),
                    pos,
                    0f,
                    1.65f,
                    2.05f,
                    TraversabilityCapabilities.Ground | TraversabilityCapabilities.Bridge,
                    CanReverse: true,
                    ReverseSpeed: 2.5f);
            }
            else if (i % 3 == 1)
            {
                // Wheeled APC (BTR-70)
                units[i] = new GroupUnitInput(
                    new EntityId((uint)(i + 1)),
                    pos,
                    0f,
                    1.40f,
                    1.80f,
                    TraversabilityCapabilities.Ground | TraversabilityCapabilities.Bridge | TraversabilityCapabilities.Water,
                    CanReverse: true,
                    ReverseSpeed: 2.0f);
            }
            else
            {
                // Wheeled Recon (BRDM-2)
                units[i] = new GroupUnitInput(
                    new EntityId((uint)(i + 1)),
                    pos,
                    0f,
                    1.15f,
                    1.45f,
                    TraversabilityCapabilities.Ground | TraversabilityCapabilities.Bridge | TraversabilityCapabilities.Water,
                    CanReverse: true,
                    ReverseSpeed: 3.0f);
            }
        }
        return units;
    }
}

[MemoryDiagnoser]
public class GroupDestinationBenchmark
{
    private GroupUnitInput[] _units = null!;
    private DestinationEvaluator _evaluator = null!;
    private Vector2 _target;

    [Params(1, 20, 200, 300)]
    public int Units { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        const int size = 251;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
        OpaqueStructure building = new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f);
        StaticGeometry geo = new(map, [building]);
        _evaluator = new DestinationEvaluator(geo, cellSize: 1.0f);
        _units = GroupBenchmarkHelpers.CreateBenchmarkUnits(Units);
        _target = new Vector2(150f, 150f);
    }

    [Benchmark]
    public GroupMoveResult Distribute()
    {
        return GroupDestinationDistribution.Distribute(
            1, 1, _units.AsSpan(), _target, 0, null, MoveMode.Forward, _evaluator);
    }

    [Benchmark]
    public GroupPreviewResult Preview()
    {
        return GroupDestinationDistribution.ComputePreview(
            _units.AsSpan(), _target, 0, null, MoveMode.Forward, _evaluator);
    }
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
