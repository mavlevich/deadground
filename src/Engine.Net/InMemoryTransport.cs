namespace DeadGround.Engine.Net;

public interface ITransport : IDisposable
{
    bool IsConnected { get; }
    byte[]? TerminalData => null;
    bool HasTerminalEvent => false;
    bool TrySend(ReadOnlySpan<byte> message);
    bool TryReceive(Span<byte> destination, out int length);
}

// M0 endpoints are confined to one thread. A future threaded transport must own its synchronization.
public sealed class InMemoryTransport : ITransport
{
    private readonly MessageRing _incoming;
    private readonly MessageRing _outgoing;

    public bool IsConnected => !_incoming.IsClosed && !_outgoing.IsClosed;
    public byte[]? TerminalData { get; internal set; }
    public bool HasTerminalEvent => TerminalData is not null || !IsConnected;

    private InMemoryTransport(MessageRing incoming, MessageRing outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public static (ITransport Client, ITransport Server) CreatePair(int capacity = 64, int maxMessageSize = 32768)
    {
        MessageRing toClient = new(capacity, maxMessageSize);
        MessageRing toServer = new(capacity, maxMessageSize);
        return (new InMemoryTransport(toClient, toServer), new InMemoryTransport(toServer, toClient));
    }

    public bool TrySend(ReadOnlySpan<byte> message) => _outgoing.TryWrite(message);
    public bool TryReceive(Span<byte> destination, out int length) => _incoming.TryRead(destination, out length);
    public void Dispose() { _incoming.Close(); _outgoing.Close(); }

    private sealed class MessageRing
    {
        private readonly byte[] _bytes;
        private readonly int[] _lengths;
        private readonly int _maxSize;
        private int _head, _tail, _count;
        private bool _closed;

        public MessageRing(int capacity, int maxSize)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSize);
            _maxSize = maxSize;
            _bytes = new byte[checked(capacity * maxSize)];
            _lengths = new int[capacity];
        }

        public bool TryWrite(ReadOnlySpan<byte> message)
        {
            if (_closed || _count == _lengths.Length || message.Length > _maxSize || message.IsEmpty) return false;
            message.CopyTo(_bytes.AsSpan(_tail * _maxSize));
            _lengths[_tail] = message.Length;
            _tail = (_tail + 1) % _lengths.Length;
            _count++;
            return true;
        }

        public bool TryRead(Span<byte> destination, out int length)
        {
            length = 0;
            if (_closed || _count == 0) return false;
            int size = _lengths[_head];
            if (destination.Length < size) throw new ArgumentException("Receive buffer is too small.");
            _bytes.AsSpan(_head * _maxSize, size).CopyTo(destination);
            length = size;
            _head = (_head + 1) % _lengths.Length;
            _count--;
            return true;
        }

        public bool IsClosed => _closed;
        public void Close() { _closed = true; _count = 0; }
    }
}
