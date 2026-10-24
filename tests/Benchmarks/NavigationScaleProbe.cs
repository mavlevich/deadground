using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using DeadGround.Engine;

namespace DeadGround.Benchmarks;

/// <summary>Headless navigation-only load and request probe. No sensing, networking or rendering.</summary>
public static class NavigationScaleProbe
{
    private const ulong ScatteredSeed = 0x50002024;
    private readonly record struct Order(Vector2 Start, Vector2 Target);
    private sealed record Result(string Scene, string Layout, int Units, int CacheEntries, ulong Seed, ulong OrderHash,
        int BlockedOrders, double LoadMs, long ArrayPayload, long RetainedHeapDelta, long LoadAllocated,
        int Portals, int DetailedCells, int Ticks, int InitialPaths, int InitialNoPaths, int Unresolved,
        int FailedSearches, int CapacityExceeded, int GlobalSearches, int GlobalExpansions,
        int LatencyP50, int LatencyP99, int LatencyMax, double TickP50, double TickP95, double TickP99,
        double TickMax, long TickAllocated, int CacheHits, int CacheMisses,
        int MaxGlobalWork, int MaxLocalWork, int MaxPrepWork, int MaxSmoothingWork, bool WorkBudgetsPassed, bool Passed);

    private sealed class Catalog : IUnitMovementCatalog
    {
        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
        {
            profile = new UnitMovementProfile(10f, 1.4f);
            return true;
        }
    }

    public static bool Run(string? jsonPath = null)
    {
        WarmUp();
        List<Result> results = new();
        foreach (string scene in new[] { "open", "town", "barrier" })
            foreach (string layout in new[] { "clustered", "scattered" })
                foreach (int units in new[] { 100, 200 })
                    foreach (int entries in new[] { 16, 0 }) results.Add(RunScene(scene, layout, units, entries));
        foreach (int units in new[] { 100, 200 })
            foreach (int entries in new[] { 16, 0 }) results.Add(RunScene("disconnected", "scattered", units, entries));
        bool passed = true;
        foreach (Result result in results) passed &= result.Passed;
        if (jsonPath is not null)
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return passed;
    }

    private static void WarmUp()
    {
        HeightMap map = new(2, 2, 400, .01f, new ushort[4]);
        WorldState world = new(map);
        OpaqueStructure[] obstacles = [new(new StructureId(1), 190, 0, 0, 210, 10, 300)];
        NavigationSystem nav = new(new NavigationGrid(map, obstacles, enableHierarchical: true), new Catalog(), maxUnits: 1000);
        using Simulation sim = new(world, new RuleSet(nav));
        EntityId id = world.CreateUnit(1, 1, new Vector2(50, 50), 10f);
        if (!sim.TryEnqueue(1, new MoveCommand(1, 0, id, new Vector2(350, 50))))
            throw new InvalidOperationException("Warm-up order rejected.");
        for (int tick = 0; tick < 1000; tick++) sim.Step();
    }

    private static OpaqueStructure[] CreateStructures(string scene)
    {
        List<OpaqueStructure> structures = new();
        if (scene == "town")
            for (int row = 0; row < 30; row++)
                for (int col = 0; col < 30; col++)
                {
                    float x = 1800 + col * 20, z = 1800 + row * 20;
                    structures.Add(new(new StructureId(structures.Count + 1), x, 0, z, x + 12, 15, z + 12));
                }
        if (scene == "barrier")
        {
            structures.Add(new(new StructureId(1), 2490, 0, 0, 2510, 10, 2200));
            structures.Add(new(new StructureId(2), 2490, 0, 2800, 2510, 10, 5000));
        }
        if (scene == "disconnected") structures.Add(new(new StructureId(1), 2490, 0, 0, 2510, 10, 5000));
        return structures.ToArray();
    }

    private static Order[] CreateOrders(NavigationGrid grid, string scene, string layout, int units)
    {
        DeterministicRandom random = new(ScatteredSeed);
        Order[] orders = new Order[units];
        for (int i = 0; i < units; i++)
        {
            if (layout == "clustered")
            {
                float z = (scene == "town" ? 2000 : 1000) + i / 10 * 5;
                orders[i] = new(new(500 + i % 10 * 5, z), new(4500, z));
                continue;
            }
            bool accepted = false;
            for (int attempt = 0; attempt < 10_000; attempt++)
            {
                Vector2 start = new(5 + random.NextFloat() * 4990, 5 + random.NextFloat() * 4990);
                Vector2 target = new(5 + random.NextFloat() * 4990, 5 + random.NextFloat() * 4990);
                if (scene == "disconnected" && (start.X >= 2400 || target.X <= 2600)) continue;
                if (!grid.IsPointClear(start, 1.4f) || !grid.IsPointClear(target, 1.4f) || Vector2.Distance(start, target) < 250) continue;
                orders[i] = new(start, target);
                accepted = true;
                break;
            }
            if (!accepted) throw new InvalidOperationException("Could not generate a valid seeded order.");
        }
        return orders;
    }

    private static ulong HashOrders(Order[] orders)
    {
        ulong hash = 14695981039346656037UL;
        foreach (Order order in orders)
            foreach (float value in new[] { order.Start.X, order.Start.Y, order.Target.X, order.Target.Y })
                hash = unchecked((hash ^ BitConverter.SingleToUInt32Bits(value)) * 1099511628211UL);
        return hash;
    }

    private static Result RunScene(string scene, string layout, int units, int entries)
    {
        HeightMap map = new(2, 2, 5000, .01f, new ushort[4]);
        OpaqueStructure[] structures = CreateStructures(scene);
        GC.Collect();
        long beforeMemory = GC.GetTotalMemory(true);
        long beforeAllocated = GC.GetTotalAllocatedBytes(true);
        long loadStart = Stopwatch.GetTimestamp();
        NavigationGrid grid = new(map, structures, enableHierarchical: true);
        NavigationSystem nav = new(grid, new Catalog(), new NavigationTuning(GlobalPlanCacheEntries: entries), maxUnits: 1000);
        double loadMs = Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
        long loadAllocated = GC.GetTotalAllocatedBytes(true) - beforeAllocated;
        long retained = GC.GetTotalMemory(true) - beforeMemory;
        Order[] orders = CreateOrders(grid, scene, layout, units);
        int blockedOrders = 0;
        foreach (Order order in orders) if (!grid.IsSegmentClear(order.Start, order.Target, 1.4f)) blockedOrders++;
        WorldState world = new(map);
        using Simulation sim = new(world, new RuleSet(nav));
        int[] latencies = new int[units];
        EntityId[] ids = new EntityId[units];
        for (int i = 0; i < units; i++)
        {
            ids[i] = world.CreateUnit(1, 1, orders[i].Start, 10f);
            if (!sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), 0, ids[i], orders[i].Target)))
                throw new InvalidOperationException("Probe command was not accepted.");
        }
        double[] times = new double[8000];
        int paths = 0, noPaths = 0, ticks = 0;
        long allocated = 0;
        int maxGlobal = 0, maxLocal = 0, maxPrep = 0, maxSmoothing = 0;
        for (; ticks < times.Length; ticks++)
        {
            long startAllocated = GC.GetAllocatedBytesForCurrentThread();
            long startTime = Stopwatch.GetTimestamp();
            sim.Step();
            times[ticks] = Stopwatch.GetElapsedTime(startTime).TotalMilliseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - startAllocated;
            maxGlobal = Math.Max(maxGlobal, nav.GlobalExpansionsThisTick);
            maxLocal = Math.Max(maxLocal, nav.LocalExpansionsThisTick);
            maxPrep = Math.Max(maxPrep, nav.AreaPrepCellsThisTick);
            maxSmoothing = Math.Max(maxSmoothing, nav.SmoothingRaycastsThisTick);
            for (int i = 0; i < units; i++)
            {
                if (latencies[i] != 0) continue;
                PathRequestStatus status = nav.GetUnitStatus(ids[i]);
                if (status is PathRequestStatus.Pending or PathRequestStatus.Searching or PathRequestStatus.None) continue;
                latencies[i] = sim.Tick;
                if (status == PathRequestStatus.PathFound) paths++;
                else if (status == PathRequestStatus.NoPath) noPaths++;
            }
            // Keep measuring movement and continuation work after initial route resolution.
            if (AllResolved(latencies) && ticks >= 999) { ticks++; break; }
        }
        int unresolved = 0;
        foreach (int latency in latencies) if (latency == 0) unresolved++;
        Array.Sort(times, 0, ticks);
        Array.Sort(latencies);
        int globalSearches = 0;
        for (int c = 0; c < 4; c++) globalSearches += grid.GetHierarchicalGraph((MovementClass)c).GlobalSearchesStarted;
        double p99 = times[(int)Math.Ceiling(ticks * .99) - 1];
        bool budgets = maxGlobal <= nav.Tuning.MaxGlobalExpansionsPerTick &&
            maxLocal <= nav.Tuning.MaxLocalExpansionsPerTick && maxPrep <= nav.Tuning.MaxAreaPrepCellsPerTick &&
            maxSmoothing <= nav.Tuning.MaxSmoothingRaycastsPerTick;
        bool outcomes = scene == "disconnected"
            ? noPaths == units && paths == 0 && nav.FailedSearches == units && globalSearches == 0 && latencies[^1] <= 3
            : paths == units && noPaths == 0 && nav.FailedSearches == 0;
        bool passed = outcomes && unresolved == 0 && nav.CapacityExceeded == 0 && allocated == 0 && budgets &&
            p99 < 15 && loadMs < 3000 && nav.EstimatedMemoryBytes < 128L * 1024 * 1024;
        Result result = new(scene, layout, units, entries, layout == "scattered" ? ScatteredSeed : 0, HashOrders(orders),
            blockedOrders, loadMs, nav.EstimatedMemoryBytes, retained, loadAllocated, grid.HierarchicalGraph.PortalCount,
            grid.HierarchicalGraph.DetailedCellCount, ticks, paths, noPaths, unresolved, nav.FailedSearches, nav.CapacityExceeded,
            globalSearches, nav.TotalGlobalExpansions, latencies[units / 2], latencies[(int)Math.Ceiling(units * .99) - 1],
            latencies[^1], times[ticks / 2], times[(int)Math.Ceiling(ticks * .95) - 1], p99, times[ticks - 1],
            allocated, nav.GlobalPlanCacheHits, nav.GlobalPlanCacheMisses, maxGlobal, maxLocal, maxPrep, maxSmoothing, budgets, passed);
        Print(result);
        GC.KeepAlive(nav);
        return result;
    }

    private static bool AllResolved(int[] latencies)
    {
        foreach (int value in latencies) if (value == 0) return false;
        return true;
    }

    private static void Print(Result r)
    {
        Console.WriteLine(FormattableString.Invariant($"NAV_5KM {r.Scene}/{r.Layout} units={r.Units} cache={r.CacheEntries} {(r.Passed ? "PASS" : "FAIL")} seed={r.Seed} orders={r.OrderHash:X16}"));
        Console.WriteLine(FormattableString.Invariant($"  Load {r.LoadMs:F1} ms, array payload {r.ArrayPayload} B, retained managed delta {r.RetainedHeapDelta} B, load allocated {r.LoadAllocated} B"));
        Console.WriteLine($"  Paths {r.InitialPaths}, NoPath {r.InitialNoPaths}, unresolved {r.Unresolved}, failures {r.FailedSearches}, capacity {r.CapacityExceeded}, ticks {r.Ticks}, blocked orders {r.BlockedOrders}");
        Console.WriteLine($"  Initial latency p50/p99/max {r.LatencyP50}/{r.LatencyP99}/{r.LatencyMax} ticks, global searches/expansions {r.GlobalSearches}/{r.GlobalExpansions}");
        Console.WriteLine(FormattableString.Invariant($"  Tick p50/p95/p99/max {r.TickP50:F3}/{r.TickP95:F3}/{r.TickP99:F3}/{r.TickMax:F3} ms, allocated {r.TickAllocated} B, work budget={r.WorkBudgetsPassed}"));
        Console.WriteLine($"  Corridor cache hits/misses {r.CacheHits}/{r.CacheMisses}, work maxima {r.MaxGlobalWork}/{r.MaxLocalWork}/{r.MaxPrepWork}/{r.MaxSmoothingWork}");
    }
}
