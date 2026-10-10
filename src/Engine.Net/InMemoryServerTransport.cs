namespace DeadGround.Engine.Net;

public sealed class InMemoryServerTransport : IServerTransport
{
    private readonly int _maxPlayers;
    private readonly int _maxMessageSize;
    private readonly ITransport?[] _clients;
    private readonly ITransport?[] _servers;
    private readonly bool[] _connected;
    private readonly long[] _connectionIds;
    private long _nextConnectionId = 1;

    public int InitialAdmittedPlayer => 0;

    public int ConnectedPlayerCount
    {
        get
        {
            int count = 0;
            for (int i = 0; i < _connected.Length; i++)
                if (_connected[i]) count++;
            return count;
        }
    }

    public InMemoryServerTransport(int maxPlayers = 4, int maxMessageSize = WireProtocol.MaxMessageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPlayers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMessageSize);
        _maxPlayers = maxPlayers;
        _maxMessageSize = maxMessageSize;
        _clients = new ITransport?[maxPlayers];
        _servers = new ITransport?[maxPlayers];
        _connected = new bool[maxPlayers];
        _connectionIds = new long[maxPlayers];
    }

    public ITransport CreateClientTransport(int player)
    {
        if (player < 1 || player > _maxPlayers)
            throw new ArgumentOutOfRangeException(nameof(player));
        int index = player - 1;
        if (_clients[index] is not null && _connected[index])
            throw new InvalidOperationException($"Client transport for player {player} already connected.");

        (ITransport client, ITransport server) = InMemoryTransport.CreatePair(maxMessageSize: _maxMessageSize);
        _clients[index] = client;
        _servers[index] = server;
        _connected[index] = true;
        _connectionIds[index] = _nextConnectionId++;
        return client;
    }

    public void Poll() { }

    public bool TrySend(int player, ReadOnlySpan<byte> message, bool reliable = true)
    {
        if (player < 1 || player > _maxPlayers) return false;
        int index = player - 1;
        ITransport? server = _servers[index];
        return server is not null && _connected[index] && server.TrySend(message);
    }

    public bool TryReceive(out int player, out long connectionId, Span<byte> destination, out int length)
    {
        for (int i = 0; i < _maxPlayers; i++)
        {
            ITransport? server = _servers[i];
            if (server is not null && _connected[i] && server.TryReceive(destination, out length))
            {
                player = i + 1;
                connectionId = _connectionIds[i];
                return true;
            }
        }
        player = 0;
        connectionId = 0;
        length = 0;
        return false;
    }

    public bool TryReceive(out int player, Span<byte> destination, out int length) =>
        TryReceive(out player, out _, destination, out length);

    public bool IsPlayerConnected(int player)
    {
        if (player < 1 || player > _maxPlayers) return false;
        int index = player - 1;
        return _connected[index] && _servers[index] is { IsConnected: true };
    }

    public long GetPlayerConnectionId(int player)
    {
        if (player < 1 || player > _maxPlayers) return 0;
        return _connected[player - 1] ? _connectionIds[player - 1] : 0;
    }

    public void DisconnectPlayer(int player, ReadOnlySpan<byte> reason = default)
    {
        if (player < 1 || player > _maxPlayers) return;
        int index = player - 1;
        _connected[index] = false;
        _servers[index]?.Dispose();
        _clients[index]?.Dispose();
        _servers[index] = null;
        _clients[index] = null;
    }

    public void Dispose()
    {
        for (int i = 0; i < _maxPlayers; i++)
        {
            DisconnectPlayer(i + 1);
        }
    }
}

internal sealed class SinglePlayerServerTransport : IServerTransport
{
    private readonly ITransport _transport;
    private readonly int _player;
    private long _connectionId = 1;
    private bool _connected = true;

    public int InitialAdmittedPlayer => _player;

    public int Player => _player;

    public SinglePlayerServerTransport(ITransport transport, int player)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (player < 1) throw new ArgumentOutOfRangeException(nameof(player));
        _player = player;
    }

    public void Poll() { }

    public bool TrySend(int player, ReadOnlySpan<byte> message, bool reliable = true)
    {
        if (!_connected || player != _player) return false;
        return _transport.TrySend(message);
    }

    public bool TryReceive(out int player, out long connectionId, Span<byte> destination, out int length)
    {
        if (_connected && _transport.TryReceive(destination, out length))
        {
            player = _player;
            connectionId = _connectionId;
            return true;
        }
        player = 0;
        connectionId = 0;
        length = 0;
        return false;
    }

    public bool TryReceive(out int player, Span<byte> destination, out int length) =>
        TryReceive(out player, out _, destination, out length);

    public bool IsPlayerConnected(int player) => _connected && player == _player && _transport.IsConnected;

    public long GetPlayerConnectionId(int player) => (_connected && player == _player) ? _connectionId : 0;

    public void DisconnectPlayer(int player, ReadOnlySpan<byte> reason = default)
    {
        if (player == _player)
        {
            _connected = false;
            _transport.Dispose();
        }
    }

    public int ConnectedPlayerCount => _connected && _transport.IsConnected ? 1 : 0;

    public void Dispose()
    {
        _connected = false;
        _transport.Dispose();
    }
}
