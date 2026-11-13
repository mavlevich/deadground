using System.Numerics;

namespace DeadGround.Engine;

/// <summary>
/// Reusable context for line-of-sight queries.
/// Holds allocation-free query deduplication buffers.
/// Safe to reuse across queries on a single thread.
/// </summary>
public sealed class LineOfSightContext
{
    internal int QueryId;
    internal int[] Mailbox;
    internal VegetationInterval[]? VegetationIntervalsBuffer;
    internal Vector2[]? ZoneIntervalsBuffer;
    internal int[]? ClearingsBuffer;

    public LineOfSightContext(int initialCapacity = 64)
    {
        Mailbox = new int[Math.Max(initialCapacity, 16)];
        QueryId = 1;
    }

    public void EnsureCapacity(int count)
    {
        if (Mailbox.Length < count)
        {
            Array.Resize(ref Mailbox, Math.Max(count, Mailbox.Length * 2));
        }
    }

    public void EnsureVegetationCapacity(int maxIntervals, int maxZoneIntervals, int maxClearings = 0)
    {
        if (VegetationIntervalsBuffer is null || VegetationIntervalsBuffer.Length < maxIntervals)
        {
            VegetationIntervalsBuffer = new VegetationInterval[Math.Max(maxIntervals, 64)];
        }

        if (ZoneIntervalsBuffer is null || ZoneIntervalsBuffer.Length < maxZoneIntervals)
        {
            ZoneIntervalsBuffer = new Vector2[Math.Max(maxZoneIntervals, 16)];
        }

        if (maxClearings > 0 && (ClearingsBuffer is null || ClearingsBuffer.Length < maxClearings))
        {
            ClearingsBuffer = new int[Math.Max(maxClearings, 64)];
        }
    }

    internal int NextQueryId()
    {
        QueryId++;
        if (QueryId == 0)
        {
            Array.Clear(Mailbox);
            QueryId = 1;
        }
        return QueryId;
    }
}
