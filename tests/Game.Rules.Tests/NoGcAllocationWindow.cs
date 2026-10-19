using System.Runtime;

namespace DeadGround.Rules.Tests;

/// <summary>
/// A window for zero allocation checks that no garbage collection can interrupt.
/// Zero allocation tests failed at random on x64 CI runners (712 and 800 bytes) and never on arm64,
/// with the same deterministic simulation. The likely cause is a collection started by another thread
/// in the test process (test host, network tests) landing inside the measured span. The window
/// collects first, then holds a no GC region for its lifetime, so that cannot happen. Real
/// allocations on this thread still count, and a failure message reports any collections.
/// </summary>
internal sealed class NoGcAllocationWindow : IDisposable
{
    private const long RegionBytes = 64L * 1024 * 1024;
    private readonly int _collectionsAtStart;

    public NoGcAllocationWindow()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        try
        {
            HoldsNoGcRegion = GC.TryStartNoGCRegion(RegionBytes);
        }
        catch (ArgumentOutOfRangeException)
        {
            HoldsNoGcRegion = GC.TryStartNoGCRegion(RegionBytes / 4);
        }
        _collectionsAtStart = GC.CollectionCount(0);
    }

    public bool HoldsNoGcRegion { get; }

    /// <summary>Collections since the window opened; 0 unless the region budget ran out.</summary>
    public int Collections => GC.CollectionCount(0) - _collectionsAtStart;

    public string Describe(long allocated) =>
        $"{allocated} bytes allocated, no GC region {(HoldsNoGcRegion ? "held" : "not held")}, {Collections} collections in the window";

    public void Dispose()
    {
        if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) GC.EndNoGCRegion();
    }
}
