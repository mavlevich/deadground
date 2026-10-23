using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace DeadGround.Engine.Net;

public static class LiteNetLibTransport
{
    public const string DefaultConnectionKey = "DeadGround";

    public static LiteNetLibClientTransport CreateClient(
        string host,
        int port,
        string connectionKey = DefaultConnectionKey,
        int maxPackets = 128,
        int maxBytes = 262144) =>
        new(host, port, connectionKey, maxPackets, maxBytes);

    public static LiteNetLibServerTransport CreateServer(
        int port,
        int maxPlayers = 2,
        string connectionKey = DefaultConnectionKey,
        int maxPacketsPerPeer = 64,
        int maxBytesPerPeer = 65536) =>
        new(port, maxPlayers, connectionKey, maxPacketsPerPeer, maxBytesPerPeer);
}

public sealed class LiteNetLibClientTransport : ITransport, INetEventListener
{
    private readonly NetManager _netManager;
    private readonly ReceiveBufferQueue _queue;
    private readonly object _lock = new();
    private NetPeer? _serverPeer;
    private bool _connected;
    private bool _hasTerminalEvent;
    private bool _disposed;

    public byte[]? TerminalData { get; private set; }

    public int QueuedPacketCount { get { lock (_lock) { return _queue.QueuedPacketCount; } } }
    public int QueuedPayloadBytes { get { lock (_lock) { return _queue.QueuedPayloadBytes; } } }
    public int PeakQueuedPackets { get { lock (_lock) { return _queue.PeakQueuedPackets; } } }
    public int PeakQueuedPayloadBytes { get { lock (_lock) { return _queue.PeakQueuedPayloadBytes; } } }
    public long DroppedUnreliablePackets { get { lock (_lock) { return _queue.DroppedUnreliablePackets; } } }
    public long DroppedUnreliableBytes { get { lock (_lock) { return _queue.DroppedUnreliableBytes; } } }
    public long ReliableOverflowFailures { get { lock (_lock) { return _queue.ReliableOverflowFailures; } } }
    public int PendingEventCount => _netManager.PendingEventsCount;
    public int PendingReceiveCount => _netManager.PendingReceiveCount;
    internal NetManager NetManager => _netManager;
    public int MaxPackets => _queue.MaxPackets;
    public int MaxBytes => _queue.MaxBytes;

    public bool HasTerminalEvent
    {
        get
        {
            lock (_lock)
            {
                return _hasTerminalEvent;
            }
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _connected;
            }
        }
    }

    public LiteNetLibClientTransport(
        string host,
        int port,
        string connectionKey = LiteNetLibTransport.DefaultConnectionKey,
        int maxPackets = 128,
        int maxBytes = 262144)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        _queue = new ReceiveBufferQueue(maxPackets, maxBytes, new ReceiveBufferPool(standardCount: maxPackets, largeCount: 4));
        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            IPv6Enabled = false,
            DisconnectTimeout = 10000
        };
        _netManager.Start();
        _netManager.Connect(host, port, connectionKey);
    }

    public bool TrySend(ReadOnlySpan<byte> message) => TrySend(message, reliable: true);

    public bool TrySend(ReadOnlySpan<byte> message, bool reliable)
    {
        if (_disposed || message.Length > WireProtocol.MaxMessageSize) return false;
        NetPeer? peer;
        lock (_lock)
        {
            peer = _serverPeer;
        }
        if (peer is null || peer.ConnectionState != ConnectionState.Connected) return false;
        DeliveryMethod method = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
        peer.Send(message, method);
        _netManager.TriggerUpdate();
        return true;
    }

    public const int DefaultMaxMessagesPerPoll = 128;
    private int _maxMessagesPerPoll = DefaultMaxMessagesPerPoll;
    private bool _hasPolledInCycle;

    public int MaxMessagesPerPoll
    {
        get => _maxMessagesPerPoll;
        set => _maxMessagesPerPoll = value > 0 ? value : DefaultMaxMessagesPerPoll;
    }

    public void Poll() => Poll(_maxMessagesPerPoll);

    public void Poll(int maxProcessedEvents)
    {
        if (_disposed) return;
        _hasPolledInCycle = true;
        _netManager.PollEvents(maxProcessedEvents);
    }

    public bool TryReceive(Span<byte> destination, out int length)
    {
        length = 0;
        if (_disposed) return false;

        if (!_hasPolledInCycle && _queue.QueuedPacketCount == 0)
        {
            _netManager.PollEvents(1);
        }

        lock (_lock)
        {
            while (_queue.TryDequeue(out ReceiveBuffer? buffer))
            {
                if (buffer is null) continue;
                if (destination.Length < buffer.Length)
                {
                    _queue.Release(buffer);
                    continue;
                }
                buffer.Data.AsSpan(0, buffer.Length).CopyTo(destination);
                length = buffer.Length;
                _queue.Release(buffer);
                return true;
            }
            return false;
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        lock (_lock)
        {
            _serverPeer = peer;
            _connected = true;
        }
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        lock (_lock)
        {
            if (_serverPeer != null && peer != _serverPeer) return;
            _serverPeer = null;
            _connected = false;
            _hasTerminalEvent = true;

            // Clear all pre-disconnect queued messages so stale payloads are not returned after disconnect.
            _queue.Clear();

            if (disconnectInfo.AdditionalData is { AvailableBytes: > 0 } reader &&
                reader.AvailableBytes <= WireProtocol.MaxMessageSize)
            {
                byte[] bytes = reader.GetRemainingBytes();
                TerminalData = bytes;
                // Retain terminal response in cleared queue; queue has full capacity so it cannot be starved.
                _queue.TryEnqueue(bytes, 0, 0, isReliable: true);
            }
        }
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        if (reader.AvailableBytes < 2 || reader.AvailableBytes > WireProtocol.MaxMessageSize) return;

        bool isReliable = deliveryMethod != DeliveryMethod.Unreliable;
        lock (_lock)
        {
            // Reject subsequent callbacks for inactive, disposed, or stale peers.
            if (!_connected || _disposed || peer != _serverPeer) return;

            ReceiveBufferQueue.EnqueueResult result = _queue.TryEnqueue(reader.GetRemainingBytesSpan(), 0, 0, isReliable);
            if (result == ReceiveBufferQueue.EnqueueResult.ReliableOverflow)
            {
                _connected = false;
                _hasTerminalEvent = true;
                _queue.Clear();
                _serverPeer?.Disconnect();
                _serverPeer = null;
            }
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
    public void OnNetworkReceiveUnconnected(IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    public void OnConnectionRequest(ConnectionRequest request) => request.Reject();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            _queue.Clear();
        }
        _netManager.Stop(true);
    }
}
public sealed class LiteNetLibServerTransport : IServerTransport, INetEventListener
{
    private readonly NetManager _netManager;
    private readonly int _maxPlayers;
    private readonly int _maxPacketsPerPeer;
    private readonly int _maxBytesPerPeer;
    private readonly string _connectionKey;
    private readonly Dictionary<int, int> _peerToSlot = new();
    private readonly Dictionary<int, NetPeer> _slotToPeer = new();
    private readonly ReceiveBufferQueue[] _playerQueues;
    private readonly long[] _slotConnectionIds;
    private readonly object _lock = new();
    private long _nextConnectionId = 1;
    private int _lastServicedSlot;
    private long _reliableOverflowDisconnects;
    private int _peakAggregatePackets;
    private int _peakAggregatePayloadBytes;
    private bool _disposed;

    public int Port => _netManager.LocalPort;
    public int InitialAdmittedPlayer => 0;
    public int MaxPacketsPerPeer => _maxPacketsPerPeer;
    public int MaxBytesPerPeer => _maxBytesPerPeer;
    public int PendingEventCount => _netManager.PendingEventsCount;
    public int PendingReceiveCount => _netManager.PendingReceiveCount;
    internal NetManager NetManager => _netManager;
    public long ReliableOverflowDisconnects { get { lock (_lock) { return _reliableOverflowDisconnects; } } }

    public int QueuedPacketCount
    {
        get
        {
            lock (_lock)
            {
                int count = 0;
                for (int i = 1; i <= _maxPlayers; i++) count += _playerQueues[i].QueuedPacketCount;
                return count;
            }
        }
    }

    public int QueuedPayloadBytes
    {
        get
        {
            lock (_lock)
            {
                int bytes = 0;
                for (int i = 1; i <= _maxPlayers; i++) bytes += _playerQueues[i].QueuedPayloadBytes;
                return bytes;
            }
        }
    }

    public int PeakQueuedPackets
    {
        get
        {
            lock (_lock) { return _peakAggregatePackets; }
        }
    }

    public int PeakQueuedPayloadBytes
    {
        get
        {
            lock (_lock) { return _peakAggregatePayloadBytes; }
        }
    }

    public int MaxPeerPeakQueuedPackets
    {
        get
        {
            lock (_lock)
            {
                int peak = 0;
                for (int i = 1; i <= _maxPlayers; i++) peak = Math.Max(peak, _playerQueues[i].PeakQueuedPackets);
                return peak;
            }
        }
    }

    public int MaxPeerPeakQueuedPayloadBytes
    {
        get
        {
            lock (_lock)
            {
                int peak = 0;
                for (int i = 1; i <= _maxPlayers; i++) peak = Math.Max(peak, _playerQueues[i].PeakQueuedPayloadBytes);
                return peak;
            }
        }
    }

    public long DroppedUnreliablePackets
    {
        get
        {
            lock (_lock)
            {
                long dropped = 0;
                for (int i = 1; i <= _maxPlayers; i++) dropped += _playerQueues[i].DroppedUnreliablePackets;
                return dropped;
            }
        }
    }

    public long DroppedUnreliableBytes
    {
        get
        {
            lock (_lock)
            {
                long dropped = 0;
                for (int i = 1; i <= _maxPlayers; i++) dropped += _playerQueues[i].DroppedUnreliableBytes;
                return dropped;
            }
        }
    }

    public long ReliableOverflowFailures
    {
        get
        {
            lock (_lock)
            {
                long total = 0;
                for (int i = 1; i <= _maxPlayers; i++) total += _playerQueues[i].ReliableOverflowFailures;
                return total;
            }
        }
    }

    public int GetPlayerQueuedPacketCount(int player)
    {
        lock (_lock)
        {
            if (player >= 1 && player <= _maxPlayers) return _playerQueues[player].QueuedPacketCount;
            return 0;
        }
    }

    public int GetPlayerQueuedPayloadBytes(int player)
    {
        lock (_lock)
        {
            if (player >= 1 && player <= _maxPlayers) return _playerQueues[player].QueuedPayloadBytes;
            return 0;
        }
    }

    public int GetPlayerPeakQueuedPackets(int player)
    {
        lock (_lock)
        {
            if (player >= 1 && player <= _maxPlayers) return _playerQueues[player].PeakQueuedPackets;
            return 0;
        }
    }

    public int GetPlayerPeakQueuedPayloadBytes(int player)
    {
        lock (_lock)
        {
            if (player >= 1 && player <= _maxPlayers) return _playerQueues[player].PeakQueuedPayloadBytes;
            return 0;
        }
    }

    public long GetPlayerDroppedUnreliablePackets(int player)
    {
        lock (_lock)
        {
            if (player >= 1 && player <= _maxPlayers) return _playerQueues[player].DroppedUnreliablePackets;
            return 0;
        }
    }

    public long GetPlayerDroppedUnreliableBytes(int player)
    {
        lock (_lock)
        {
            if (player >= 1 && player <= _maxPlayers) return _playerQueues[player].DroppedUnreliableBytes;
            return 0;
        }
    }

    public long GetPlayerReliableOverflowFailures(int player)
    {
        lock (_lock)
        {
            if (player >= 1 && player <= _maxPlayers) return _playerQueues[player].ReliableOverflowFailures;
            return 0;
        }
    }

    internal ReceiveBufferQueue GetPlayerQueue(int player)
    {
        lock (_lock)
        {
            return _playerQueues[player];
        }
    }

    public int ConnectedPlayerCount
    {
        get
        {
            lock (_lock) { return _slotToPeer.Count; }
        }
    }

    public LiteNetLibServerTransport(
        int port,
        int maxPlayers = 2,
        string connectionKey = LiteNetLibTransport.DefaultConnectionKey,
        int maxPacketsPerPeer = 64,
        int maxBytesPerPeer = 65536)
    {
        if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPlayers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPacketsPerPeer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytesPerPeer);

        _maxPlayers = maxPlayers;
        _maxPacketsPerPeer = maxPacketsPerPeer;
        _maxBytesPerPeer = maxBytesPerPeer;
        _connectionKey = connectionKey;
        _slotConnectionIds = new long[maxPlayers + 1];

        _playerQueues = new ReceiveBufferQueue[maxPlayers + 1];
        for (int i = 1; i <= maxPlayers; i++)
        {
            _playerQueues[i] = new ReceiveBufferQueue(maxPacketsPerPeer, maxBytesPerPeer);
        }

        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            IPv6Enabled = false,
            DisconnectTimeout = 10000
        };
        if (!_netManager.Start(port))
            throw new InvalidOperationException($"Failed to bind LiteNetLib server on port {port}.");
    }

    public const int DefaultMaxMessagesPerPoll = 128;
    private int _maxMessagesPerPoll = DefaultMaxMessagesPerPoll;

    public int MaxMessagesPerPoll
    {
        get => _maxMessagesPerPoll;
        set => _maxMessagesPerPoll = value > 0 ? value : DefaultMaxMessagesPerPoll;
    }

    public void Poll() => Poll(_maxMessagesPerPoll);

    public void Poll(int maxProcessedEvents)
    {
        if (!_disposed) _netManager.PollEvents(maxProcessedEvents);
    }

    public bool TrySend(int player, ReadOnlySpan<byte> message, bool reliable = true)
    {
        if (_disposed || message.Length > WireProtocol.MaxMessageSize) return false;
        NetPeer? peer;
        lock (_lock)
        {
            _slotToPeer.TryGetValue(player, out peer);
        }
        if (peer is null || peer.ConnectionState != ConnectionState.Connected) return false;
        DeliveryMethod method = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
        peer.Send(message, method);
        _netManager.TriggerUpdate();
        return true;
    }

    public bool TryReceive(out int player, out long connectionId, Span<byte> destination, out int length)
    {
        player = 0;
        connectionId = 0;
        length = 0;
        if (_disposed) return false;

        lock (_lock)
        {
            for (int i = 0; i < _maxPlayers; i++)
            {
                int p = ((_lastServicedSlot + i) % _maxPlayers) + 1;
                ReceiveBufferQueue queue = _playerQueues[p];

                while (queue.TryDequeue(out ReceiveBuffer? buffer))
                {
                    if (buffer is null) continue;

                    if (buffer.ConnectionId != _slotConnectionIds[p] || _slotConnectionIds[p] == 0)
                    {
                        queue.Release(buffer);
                        continue;
                    }

                    if (destination.Length < buffer.Length)
                    {
                        queue.Release(buffer);
                        continue;
                    }

                    buffer.Data.AsSpan(0, buffer.Length).CopyTo(destination);
                    player = p;
                    connectionId = buffer.ConnectionId;
                    length = buffer.Length;
                    queue.Release(buffer);

                    _lastServicedSlot = p;
                    return true;
                }
            }
            return false;
        }
    }

    public bool TryReceive(out int player, Span<byte> destination, out int length) =>
        TryReceive(out player, out _, destination, out length);

    public bool IsPlayerConnected(int player)
    {
        lock (_lock)
        {
            return _slotToPeer.TryGetValue(player, out NetPeer? peer) &&
                peer.ConnectionState == ConnectionState.Connected;
        }
    }

    public long GetPlayerConnectionId(int player)
    {
        lock (_lock)
        {
            if (player >= 1 && player <= _maxPlayers)
                return _slotConnectionIds[player];
            return 0;
        }
    }

    public void DisconnectPlayer(int player, ReadOnlySpan<byte> reason = default)
    {
        NetPeer? peerToDisconnect = null;
        byte[]? data = null;
        lock (_lock)
        {
            if (_slotToPeer.Remove(player, out NetPeer? peer))
            {
                _peerToSlot.Remove(peer.Id);
                _slotConnectionIds[player] = 0;
                peerToDisconnect = peer;
                if (!reason.IsEmpty)
                {
                    data = reason.ToArray();
                }
            }
            if (player >= 1 && player <= _maxPlayers)
            {
                _playerQueues[player].Clear();
            }
        }

        if (peerToDisconnect is not null)
        {
            if (data is not null)
                peerToDisconnect.Disconnect(data);
            else
                peerToDisconnect.Disconnect();
            _netManager.TriggerUpdate();
        }
    }

    public void OnConnectionRequest(ConnectionRequest request)
    {
        lock (_lock)
        {
            if (_slotToPeer.Count < _maxPlayers)
                request.AcceptIfKey(_connectionKey);
            else
                request.Reject();
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        lock (_lock)
        {
            for (int slot = 1; slot <= _maxPlayers; slot++)
            {
                if (!_slotToPeer.ContainsKey(slot))
                {
                    _slotToPeer[slot] = peer;
                    _peerToSlot[peer.Id] = slot;
                    _slotConnectionIds[slot] = ++_nextConnectionId;
                    _playerQueues[slot].Clear();
                    break;
                }
            }
        }
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
    {
        lock (_lock)
        {
            if (_peerToSlot.Remove(peer.Id, out int slot))
            {
                _slotToPeer.Remove(slot);
                _slotConnectionIds[slot] = 0;
                _playerQueues[slot].Clear();
            }
        }
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        if (reader.AvailableBytes < 2 || reader.AvailableBytes > WireProtocol.MaxMessageSize) return;

        lock (_lock)
        {
            if (!_peerToSlot.TryGetValue(peer.Id, out int slot)) return;
            long connId = _slotConnectionIds[slot];
            if (connId == 0) return;

            bool isReliable = deliveryMethod != DeliveryMethod.Unreliable;
            ReceiveBufferQueue queue = _playerQueues[slot];
            ReceiveBufferQueue.EnqueueResult result = queue.TryEnqueue(reader.GetRemainingBytesSpan(), slot, connId, isReliable);
            if (result == ReceiveBufferQueue.EnqueueResult.Enqueued)
            {
                int currentPackets = 0;
                int currentBytes = 0;
                for (int i = 1; i <= _maxPlayers; i++)
                {
                    currentPackets += _playerQueues[i].QueuedPacketCount;
                    currentBytes += _playerQueues[i].QueuedPayloadBytes;
                }
                if (currentPackets > _peakAggregatePackets) _peakAggregatePackets = currentPackets;
                if (currentBytes > _peakAggregatePayloadBytes) _peakAggregatePayloadBytes = currentBytes;
            }
            else if (result == ReceiveBufferQueue.EnqueueResult.ReliableOverflow)
            {
                _reliableOverflowDisconnects++;
                DisconnectPlayer(slot);
            }
        }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
    public void OnNetworkReceiveUnconnected(IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            for (int i = 1; i <= _maxPlayers; i++)
            {
                _playerQueues[i].Clear();
            }
        }
        _netManager.Stop(true);
    }
}
