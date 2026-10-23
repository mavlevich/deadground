using System.Diagnostics;

namespace DeadGround.Engine.Net;

/// <summary>
/// A reusable payload buffer acquired during packet reception and released upon consumption or rejection.
/// </summary>
public sealed class ReceiveBuffer
{
    public readonly byte[] Data;
    public readonly bool IsLarge;
    public int Length;
    public int Player;
    public long ConnectionId;
    public bool IsReliable;
    internal bool InUse;

    internal ReceiveBuffer(int size, bool isLarge)
    {
        Data = new byte[size];
        IsLarge = isLarge;
    }

    internal void Reset()
    {
        Length = 0;
        Player = 0;
        ConnectionId = 0;
        IsReliable = false;
    }
}

/// <summary>
/// Fixed-capacity pool of reusable receive buffers. Never expands on the heap after construction.
/// </summary>
public sealed class ReceiveBufferPool
{
    public const int DefaultStandardBufferSize = 1280;
    public const int DefaultLargeBufferSize = WireProtocol.MaxMessageSize;

    private readonly ReceiveBuffer[] _standardBuffers;
    private readonly ReceiveBuffer[] _largeBuffers;
    private readonly int _standardSize;
    private readonly int _largeSize;
    private int _standardAvailable;
    private int _largeAvailable;

    public int StandardCapacity => _standardBuffers.Length;
    public int LargeCapacity => _largeBuffers.Length;
    public int StandardAvailable => _standardAvailable;
    public int LargeAvailable => _largeAvailable;
    public int StandardSize => _standardSize;
    public int LargeSize => _largeSize;

    public ReceiveBufferPool(
        int standardCount = 64,
        int largeCount = 2,
        int standardSize = DefaultStandardBufferSize,
        int largeSize = DefaultLargeBufferSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(standardCount);
        ArgumentOutOfRangeException.ThrowIfNegative(largeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(standardSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(largeSize);

        _standardSize = standardSize;
        _largeSize = largeSize;

        _standardBuffers = new ReceiveBuffer[standardCount];
        for (int i = 0; i < standardCount; i++)
        {
            _standardBuffers[i] = new ReceiveBuffer(standardSize, isLarge: false);
        }
        _standardAvailable = standardCount;

        _largeBuffers = new ReceiveBuffer[largeCount];
        for (int i = 0; i < largeCount; i++)
        {
            _largeBuffers[i] = new ReceiveBuffer(largeSize, isLarge: true);
        }
        _largeAvailable = largeCount;
    }

    public bool TryAcquire(int length, out ReceiveBuffer? buffer)
    {
        buffer = null;
        if (length <= 0 || length > _largeSize) return false;

        if (length <= _standardSize)
        {
            if (_standardAvailable > 0)
            {
                buffer = _standardBuffers[--_standardAvailable];
                buffer.InUse = true;
                return true;
            }
            if (_largeAvailable > 0)
            {
                buffer = _largeBuffers[--_largeAvailable];
                buffer.InUse = true;
                return true;
            }
            return false;
        }

        if (_largeAvailable > 0)
        {
            buffer = _largeBuffers[--_largeAvailable];
            buffer.InUse = true;
            return true;
        }
        return false;
    }

    public void Return(ReceiveBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!buffer.InUse) return;
        buffer.InUse = false;
        buffer.Reset();

        if (buffer.IsLarge)
        {
            if (_largeAvailable < _largeBuffers.Length)
            {
                _largeBuffers[_largeAvailable++] = buffer;
            }
        }
        else
        {
            if (_standardAvailable < _standardBuffers.Length)
            {
                _standardBuffers[_standardAvailable++] = buffer;
            }
        }
    }
}

/// <summary>
/// Preallocated FIFO packet queue with bounded packet count, bounded payload bytes, and zero steady-state allocations.
/// </summary>
public sealed class ReceiveBufferQueue
{
    public enum EnqueueResult
    {
        Enqueued,
        DroppedUnreliable,
        ReliableOverflow,
        RejectedInvalidLength
    }

    private readonly ReceiveBufferPool _pool;
    private readonly ReceiveBuffer[] _ring;
    private readonly int _maxPackets;
    private readonly int _maxBytes;
    private int _head;
    private int _tail;
    private int _count;
    private int _queuedBytes;
    private int _peakPackets;
    private int _peakBytes;
    private long _droppedUnreliablePackets;
    private long _droppedUnreliableBytes;
    private long _reliableOverflowFailures;

    public int QueuedPacketCount => _count;
    public int QueuedPayloadBytes => _queuedBytes;
    public int PeakQueuedPackets => _peakPackets;
    public int PeakQueuedPayloadBytes => _peakBytes;
    public long DroppedUnreliablePackets => _droppedUnreliablePackets;
    public long DroppedUnreliableBytes => _droppedUnreliableBytes;
    public long ReliableOverflowFailures => _reliableOverflowFailures;

    public int MaxPackets => _maxPackets;
    public int MaxBytes => _maxBytes;

    public ReceiveBufferQueue(int maxPackets = 64, int maxBytes = 65536, ReceiveBufferPool? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPackets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        _maxPackets = maxPackets;
        _maxBytes = maxBytes;
        _ring = new ReceiveBuffer[maxPackets];
        _pool = pool ?? new ReceiveBufferPool(standardCount: maxPackets, largeCount: 2);
    }

    public EnqueueResult TryEnqueue(ReadOnlySpan<byte> payload, int player, long connectionId, bool isReliable)
    {
        int length = payload.Length;
        if (length < 2 || length > WireProtocol.MaxMessageSize)
        {
            return EnqueueResult.RejectedInvalidLength;
        }

        if (_count >= _maxPackets || _queuedBytes + length > _maxBytes)
        {
            if (isReliable)
            {
                _reliableOverflowFailures++;
                return EnqueueResult.ReliableOverflow;
            }
            _droppedUnreliablePackets++;
            _droppedUnreliableBytes += length;
            return EnqueueResult.DroppedUnreliable;
        }

        if (!_pool.TryAcquire(length, out ReceiveBuffer? buffer) || buffer is null)
        {
            if (isReliable)
            {
                _reliableOverflowFailures++;
                return EnqueueResult.ReliableOverflow;
            }
            _droppedUnreliablePackets++;
            _droppedUnreliableBytes += length;
            return EnqueueResult.DroppedUnreliable;
        }

        payload.CopyTo(buffer.Data.AsSpan(0, length));
        buffer.Length = length;
        buffer.Player = player;
        buffer.ConnectionId = connectionId;
        buffer.IsReliable = isReliable;

        _ring[_tail] = buffer;
        _tail = (_tail + 1) % _ring.Length;
        _count++;
        _queuedBytes += length;

        if (_count > _peakPackets) _peakPackets = _count;
        if (_queuedBytes > _peakBytes) _peakBytes = _queuedBytes;

        return EnqueueResult.Enqueued;
    }

    public bool TryDequeue(out ReceiveBuffer? buffer)
    {
        if (_count == 0)
        {
            buffer = null;
            return false;
        }

        buffer = _ring[_head];
        _ring[_head] = null!;
        _head = (_head + 1) % _ring.Length;
        _count--;
        _queuedBytes -= buffer.Length;
        return true;
    }

    public void Release(ReceiveBuffer buffer)
    {
        _pool.Return(buffer);
    }

    public void Clear()
    {
        while (_count > 0)
        {
            ReceiveBuffer buffer = _ring[_head];
            _ring[_head] = null!;
            _head = (_head + 1) % _ring.Length;
            _count--;
            _pool.Return(buffer);
        }
        _head = 0;
        _tail = 0;
        _count = 0;
        _queuedBytes = 0;
    }
}
