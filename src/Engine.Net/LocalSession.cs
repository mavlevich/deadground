using System.Numerics;

namespace DeadGround.Engine.Net;

// The client has no access to WorldState. This composition root owns both endpoints for local play.
public sealed class LocalSession : IGameSession
{
    private readonly SimulationHost _host;
    private readonly ClientSession _client;

    public ClientReplica Replica => _client.Replica;
    public double RenderTick => _client.RenderTick;
    public int Tick => _host.Tick;
    public double LastTickMilliseconds => _host.LastTickMilliseconds;
    public long LastTickAllocatedBytes => _host.LastTickAllocatedBytes;
    public long BytesReceived => _client.BytesReceived;
    public double PendingSeconds => _host.PendingSeconds;
    public CommandResult? LastCommandResult => _client.LastCommandResult;
    public long RejectedCommands => _client.RejectedCommands;
    public bool IsConnected => true;
    public int AssignedPlayerId { get; }
    public string StatusDescription => "Local authoritative simulation / prototype terrain";

    public LocalSession(Simulation simulation, int player = 1)
    {
        SimulationHost.ValidateConfiguration(simulation, player);
        AssignedPlayerId = player;
        (ITransport client, ITransport server) = InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);
        _host = new SimulationHost(simulation, server, player);
        _client = new ClientSession(client, replicaCapacity: simulation.Capacity);
    }

    public void Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        _host.Advance(elapsedSeconds);
        _client.Advance(elapsedSeconds);
    }

    public bool Move(EntityId unit, Vector2 target) => _client.Move(unit, target);

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }
}
