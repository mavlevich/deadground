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
