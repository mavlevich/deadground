using System.Numerics;

namespace DeadGround.Engine.Net;

// The client has no access to WorldState. This composition root owns both endpoints for M0.
public sealed class LocalSession : IDisposable
{
    private readonly ITransport _client;
    private readonly SimulationHost _host;
    private readonly byte[] _receive = new byte[WireProtocol.MaxMessageSize];
    private readonly byte[] _command = new byte[WireProtocol.MoveSize];
    private uint _nextCommand = 1;
    private double _time, _receivedAt;
    public ClientReplica Replica { get; } = new();
    public double RenderTick => Replica.LatestTick + (_time - _receivedAt) * Simulation.TickRate - 3;
    public int Tick => _host.Tick;
    public double LastTickMilliseconds => _host.LastTickMilliseconds;
    public long LastTickAllocatedBytes => _host.LastTickAllocatedBytes;
    public long BytesReceived { get; private set; }
    public double PendingSeconds => _host.PendingSeconds;
    public CommandResult? LastCommandResult { get; private set; }
    public long RejectedCommands { get; private set; }

    public LocalSession(Simulation simulation, int player = 1)
    {
        SimulationHost.ValidateConfiguration(simulation, player);
        (ITransport client, ITransport server) = InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);
        _client = client;
        _host = new SimulationHost(simulation, server, player);
        Poll();
    }

    public void Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        _time += elapsedSeconds;
        _host.Advance(elapsedSeconds);
        Poll();
    }

    public bool Move(EntityId unit, Vector2 target)
    {
        MoveCommand command = new(_nextCommand, Replica.LatestTick, unit, target);
        WireProtocol.WriteMove(_command, command);
        if (!_client.TrySend(_command)) return false;
        _nextCommand = checked(_nextCommand + 1);
        return true;
    }

    private void Poll()
    {
        while (_client.TryReceive(_receive, out int length))
        {
            if (WireProtocol.TryReadCommandResult(_receive.AsSpan(0, length), out CommandResult result))
            {
                if (result.Sequence >= _nextCommand || result.Sequence <= (LastCommandResult?.Sequence ?? 0)) continue;
                LastCommandResult = result;
                if (!result.Queued) RejectedCommands++;
                BytesReceived += length;
                continue;
            }
            if (!Replica.TryApply(_receive.AsSpan(0, length))) continue;
            _receivedAt = _time;
            BytesReceived += length;
        }
    }

    public void Dispose() { _host.Dispose(); _client.Dispose(); }
}
