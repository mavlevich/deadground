using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace DeadGround.Engine.Net;

public static class LiteNetLibTransport
{
    public const string DefaultConnectionKey = "DeadGround";

    public static LiteNetLibClientTransport CreateClient(string host, int port, string connectionKey = DefaultConnectionKey) =>
        new(host, port, connectionKey);

    public static LiteNetLibServerTransport CreateServer(int port, int maxPlayers = 2, string connectionKey = DefaultConnectionKey) =>
        new(port, maxPlayers, connectionKey);
}

public sealed class LiteNetLibClientTransport : ITransport, INetEventListener
{
    private readonly NetManager _netManager;
    private readonly Queue<byte[]> _incoming = new();
    private readonly object _lock = new();
    private NetPeer? _serverPeer;
    private bool _connected;
    private bool _hasTerminalEvent;
    private bool _disposed;

    public byte[]? TerminalData { get; private set; }

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

    public LiteNetLibClientTransport(string host, int port, string connectionKey = LiteNetLibTransport.DefaultConnectionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            IPv6Enabled = false,
            DisconnectTimeout = 10000
        };
        _netManager.Start();
        _netManager.Connect(host, port, connectionKey);
    }

    public bool TrySend(ReadOnlySpan<byte> message)
    {
        if (_disposed || message.Length > WireProtocol.MaxMessageSize) return false;
        NetPeer? peer;
        lock (_lock)
        {
            peer = _serverPeer;
        }
        if (peer is null || peer.ConnectionState != ConnectionState.Connected) return false;
        peer.Send(message, DeliveryMethod.ReliableOrdered);
        return true;
    }

    public bool TryReceive(Span<byte> destination, out int length)
    {
        length = 0;
        if (_disposed) return false;
        _netManager.PollEvents();
        lock (_lock)
        {
            while (_incoming.Count > 0)
            {
                byte[] packet = _incoming.Dequeue();
                if (destination.Length < packet.Length)
                {
                    // Drop oversized packet safely rather than crashing
                    continue;
                }
                packet.AsSpan().CopyTo(destination);
                length = packet.Length;
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
            if (_serverPeer == peer) _serverPeer = null;
            _connected = false;
            _hasTerminalEvent = true;
            if (disconnectInfo.AdditionalData is { AvailableBytes: > 0 } reader &&
                reader.AvailableBytes <= WireProtocol.MaxMessageSize)
            {
                byte[] bytes = reader.GetRemainingBytes();
                TerminalData = bytes;
                _incoming.Enqueue(bytes);
            }
        }
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        if (reader.AvailableBytes > WireProtocol.MaxMessageSize) return;
        byte[] bytes = reader.GetRemainingBytes();
        lock (_lock) { _incoming.Enqueue(bytes); }
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
    public void OnNetworkReceiveUnconnected(IPEndPoint endPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    public void OnConnectionRequest(ConnectionRequest request) => request.Reject();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _netManager.Stop(true);
    }
}

public sealed class LiteNetLibServerTransport : IServerTransport, INetEventListener
{
    private readonly NetManager _netManager;
    private readonly int _maxPlayers;
    private readonly string _connectionKey;
    private readonly Dictionary<int, int> _peerToSlot = new();
    private readonly Dictionary<int, NetPeer> _slotToPeer = new();
    private readonly Queue<(int Player, long ConnectionId, byte[] Data)> _incoming = new();
    private readonly long[] _slotConnectionIds;
    private readonly object _lock = new();
    private long _nextConnectionId = 1;
    private bool _disposed;

    public int Port => _netManager.LocalPort;
    public int InitialAdmittedPlayer => 0;

    public int ConnectedPlayerCount
    {
        get
        {
            lock (_lock) { return _slotToPeer.Count; }
        }
    }

    public LiteNetLibServerTransport(int port, int maxPlayers = 2, string connectionKey = LiteNetLibTransport.DefaultConnectionKey)
    {
        if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPlayers);
        _maxPlayers = maxPlayers;
        _connectionKey = connectionKey;
        _slotConnectionIds = new long[maxPlayers + 1];

        _netManager = new NetManager(this)
        {
            AutoRecycle = true,
            IPv6Enabled = false,
            DisconnectTimeout = 10000
        };
        if (!_netManager.Start(port))
            throw new InvalidOperationException($"Failed to bind LiteNetLib server on port {port}.");
    }

    public void Poll()
    {
        if (!_disposed) _netManager.PollEvents();
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
            while (_incoming.Count > 0)
            {
                (int p, long connId, byte[] data) = _incoming.Dequeue();
                if (p < 1 || p > _maxPlayers || _slotConnectionIds[p] != connId || connId == 0)
                    continue;
                if (destination.Length < data.Length)
                    continue;
                data.AsSpan().CopyTo(destination);
                player = p;
                connectionId = connId;
                length = data.Length;
                return true;
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
        lock (_lock)
        {
            if (_slotToPeer.Remove(player, out NetPeer? peer))
            {
                _peerToSlot.Remove(peer.Id);
                _slotConnectionIds[player] = 0;
                if (!reason.IsEmpty)
                {
                    byte[] data = reason.ToArray();
                    peer.Disconnect(data);
                }
                else
                {
                    peer.Disconnect();
                }
            }
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
            }
        }
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        if (reader.AvailableBytes > WireProtocol.MaxMessageSize) return;
        byte[] bytes = reader.GetRemainingBytes();
        lock (_lock)
        {
            if (_peerToSlot.TryGetValue(peer.Id, out int slot))
            {
                long connId = _slotConnectionIds[slot];
                _incoming.Enqueue((slot, connId, bytes));
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
        _netManager.Stop(true);
    }
}
