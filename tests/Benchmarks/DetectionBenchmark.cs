using System.Numerics;
using BenchmarkDotNet.Attributes;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;

namespace DeadGround.Benchmarks;

[MemoryDiagnoser]
public class DetectionBenchmark
{
    private const int TicksPerBatch = 100;

    private Simulation _simVisibility = null!;
    private Simulation _simDense1000 = null!;
    private PlayerKnowledge _knowledge1 = null!;
    private PlayerKnowledge _knowledge2 = null!;
    private byte[] _snapshotBuffer1 = null!;
    private byte[] _snapshotBuffer2 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _knowledge1 = new PlayerKnowledge(1);
        _knowledge2 = new PlayerKnowledge(2);
        _snapshotBuffer1 = new byte[WireProtocol.MaxMessageSize];
        _snapshotBuffer2 = new byte[WireProtocol.MaxMessageSize];

        // 1. Playable visibility scenario
        _simVisibility = VisibilityScenario.CreateSimulation();

        // 2. Dense scenario: 500 recon observers vs 500 tanks on flat 128m map
        _simDense1000 = CreateDense1000Simulation();

        // Warm up allocations
        for (int i = 0; i < 20; i++)
        {
            _simVisibility.Step();
            _simVisibility.UpdateKnowledge(_knowledge1);
            _simDense1000.Step();
            if (_simDense1000.Tick % 2 == 0)
            {
                _simDense1000.UpdateKnowledge(_knowledge1);
                WireProtocol.WriteSnapshot(_snapshotBuffer1, (uint)_simDense1000.Tick, _simDense1000.Tick, _knowledge1.OwnUnits, _knowledge1.Contacts);
                _simDense1000.UpdateKnowledge(_knowledge2);
                WireProtocol.WriteSnapshot(_snapshotBuffer2, (uint)_simDense1000.Tick, _simDense1000.Tick, _knowledge2.OwnUnits, _knowledge2.Contacts);
            }
        }
    }

    public static Simulation CreateDense1000Simulation(
        int evaluationIntervalTicks = 2,
        StaticGeometry? customGeometry = null)
    {
        // Flat 128m x 128m map
        const int size = 65;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
        StaticGeometry geometry = customGeometry ?? new(map, []);
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        DetectionTuning tuning = new(
            memoryDurationTicks: 100,
            correlationWindowTicks: 60,
            correlationDistanceGate: 25.0f,
            evaluationIntervalTicks: evaluationIntervalTicks);

        OpsRules rules = new(geometry, new OpsPerceptionCatalog(catalog), tuning);
        WorldState world = new(geometry.HeightMap);
        Simulation sim = new(world, rules);

        // 500 recon observers on Side 1 spread across X: 10..58, Y: 10..118
        // Slow movement keeps every unit active for measured batches
        for (int i = 0; i < 500; i++)
        {
            float x = 10f + (i % 25) * 2.0f;
            float y = 10f + (i / 25) * 5.4f;
            EntityId id = world.CreateUnit(1, 1, new Vector2(x, y), 0.001f);
            if (!sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), 0, id, new Vector2(x + 50f, y))))
                throw new InvalidOperationException("Failed to enqueue move command for Side 1 unit.");
            if (i % 200 == 199) sim.Step();
        }
        sim.Step();

        // 500 tanks on Side 2 spread across X: 70..118, Y: 10..118
        for (int i = 0; i < 500; i++)
        {
            float x = 70f + (i % 25) * 2.0f;
            float y = 10f + (i / 25) * 5.4f;
            EntityId id = world.CreateUnit(2, 2, new Vector2(x, y), 0.001f);
            if (!sim.TryEnqueue(2, new MoveCommand((uint)(500 + i + 1), 0, id, new Vector2(x - 50f, y))))
                throw new InvalidOperationException("Failed to enqueue move command for Side 2 unit.");
            if (i % 200 == 199) sim.Step();
        }
        sim.Step();

        int movingCount = 0;
        for (int i = 0; i < world.Count; i++)
        {
            if (world.GetUnitAt(i).Moving) movingCount++;
        }
        if (movingCount != 1000)
            throw new InvalidOperationException($"Expected 1000 moving units before benchmark, but found {movingCount}.");

        return sim;
    }

    [Benchmark(OperationsPerInvoke = TicksPerBatch)]
    public long Dense1000Entities_StepKnowledgeAndSnapshot()
    {
        long totalBytes = 0;
        for (int i = 0; i < TicksPerBatch; i++)
        {
            _simDense1000.Step();
            if (_simDense1000.Tick % 2 == 0)
            {
                _simDense1000.UpdateKnowledge(_knowledge1);
                totalBytes += WireProtocol.WriteSnapshot(_snapshotBuffer1, (uint)_simDense1000.Tick, _simDense1000.Tick, _knowledge1.OwnUnits, _knowledge1.Contacts);
                _simDense1000.UpdateKnowledge(_knowledge2);
                totalBytes += WireProtocol.WriteSnapshot(_snapshotBuffer2, (uint)_simDense1000.Tick, _simDense1000.Tick, _knowledge2.OwnUnits, _knowledge2.Contacts);
            }
        }
        return totalBytes;
    }

    [Benchmark(OperationsPerInvoke = TicksPerBatch)]
    public int VisibilityScenario_StepAndUpdateKnowledge()
    {
        int totalContacts = 0;
        for (int i = 0; i < TicksPerBatch; i++)
        {
            _simVisibility.Step();
            _simVisibility.UpdateKnowledge(_knowledge1);
            totalContacts += _knowledge1.ContactCount;
        }
        return totalContacts;
    }
}
