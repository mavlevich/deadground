using System.Numerics;
using BenchmarkDotNet.Attributes;
using DeadGround.Engine;
using DeadGround.Sandbox;

namespace DeadGround.Benchmarks;

[MemoryDiagnoser]
public class LineOfSightBenchmark
{
    private const int BatchSize = 1000;
    private StaticGeometry _prototypeGeo = null!;
    private StaticGeometry _denseGeo = null!;
    private Vector3[] _queryStarts = null!;
    private Vector3[] _queryEnds = null!;

    [GlobalSetup]
    public void Setup()
    {
        _prototypeGeo = LosSandboxScenario.CreateGeometry();

        HeightMap map = HeightMap.CreatePrototype();
        List<OpaqueStructure> structures = new(50);
        int id = 1;
        for (int z = 10; z <= 110; z += 20)
        {
            for (int x = 10; x <= 110; x += 15)
            {
                structures.Add(new OpaqueStructure(
                    new StructureId(id++),
                    new Vector3(x, 0, z),
                    new Vector3(x + 8, 5, z + 6)));
            }
        }
        _denseGeo = new StaticGeometry(map, structures, structureCellSize: 16f);

        _queryStarts = new Vector3[BatchSize];
        _queryEnds = new Vector3[BatchSize];
        Random rng = new(42);
        for (int i = 0; i < BatchSize; i++)
        {
            _queryStarts[i] = new Vector3(
                (float)(rng.NextDouble() * 120 + 4),
                (float)(rng.NextDouble() * 3 + 1),
                (float)(rng.NextDouble() * 120 + 4));
            _queryEnds[i] = new Vector3(
                (float)(rng.NextDouble() * 120 + 4),
                (float)(rng.NextDouble() * 3 + 1),
                (float)(rng.NextDouble() * 120 + 4));
        }
    }

    [Benchmark]
    public LineOfSightResult SingleOpenFieldQuery()
    {
        return _prototypeGeo.CheckLineOfSight(new Vector3(15f, 1f, 10f), new Vector3(15f, 0.5f, 18f));
    }

    [Benchmark]
    public LineOfSightResult SingleHillOcclusionQuery()
    {
        return _prototypeGeo.CheckLineOfSight(new Vector3(50f, 1.5f, 44f), new Vector3(122f, 1.5f, 44f));
    }

    [Benchmark]
    public LineOfSightResult SingleBuildingOcclusionQuery()
    {
        return _prototypeGeo.CheckLineOfSight(new Vector3(86f, 1.5f, 88f), new Vector3(86f, 1f, 112f));
    }

    [Benchmark]
    public LineOfSightResult LongDiagonalTraversedQuery()
    {
        return _prototypeGeo.CheckLineOfSight(new Vector3(4f, 15f, 4f), new Vector3(124f, 15f, 124f));
    }

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public int Batch1000RandomQueries()
    {
        int clearCount = 0;
        for (int i = 0; i < BatchSize; i++)
        {
            LineOfSightResult result = _denseGeo.CheckLineOfSight(_queryStarts[i], _queryEnds[i]);
            if (result.IsClear) clearCount++;
        }
        return clearCount;
    }
}
