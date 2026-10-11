using System.Diagnostics;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Sandbox;

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
