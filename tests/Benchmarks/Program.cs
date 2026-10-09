using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Sandbox;
using System.Numerics;

BenchmarkSwitcher.FromAssembly(typeof(MovementBenchmark).Assembly).Run(args);

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
