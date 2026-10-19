using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class NoGcAllocationWindowTests
{
    [Fact]
    public void CollectionsFromAnotherThreadStayOutOfTheWindow()
    {
        int[] data = new int[1024];
        using ManualResetEventSlim started = new();
        using NoGcAllocationWindow window = new();
        Assert.True(window.HoldsNoGcRegion);
        // 16 MB from another thread is several gen0 budgets: without the window it starts collections.
        Thread other = new(() =>
        {
            started.Set();
            byte[]? keep = null;
            for (int i = 0; i < 4096; i++) keep = new byte[4096];
            GC.KeepAlive(keep);
        });
        // Thread.Start allocates on this thread, so the measured span begins once the other thread runs.
        other.Start();
        started.Wait();
        long before = GC.GetAllocatedBytesForCurrentThread();
        long sum = 0;
        while (other.IsAlive)
            for (int k = 0; k < 4096; k++) sum += data[k & 1023] + k;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        other.Join();

        GC.KeepAlive(sum);
        Assert.Equal(0, window.Collections);
        Assert.True(allocated == 0, window.Describe(allocated));
    }

    [Fact]
    public void RealAllocationsInsideTheWindowStillCount()
    {
        using NoGcAllocationWindow window = new();
        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] buffer = new byte[1000];
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(buffer);
        Assert.True(allocated >= 1000, window.Describe(allocated));
    }
}
