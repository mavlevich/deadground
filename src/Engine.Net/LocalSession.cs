using System.Numerics;

namespace DeadGround.Engine.Net;

// The client has no access to WorldState. This composition root owns both endpoints for local play.
public sealed class LocalSession : IGameSession
{
    private readonly SimulationHost _host;
    private readonly ClientSession _client;

    public ClientReplica Replica => _client.Replica;
    public MissionObservation Mission => _client.Mission;
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

    public LocalSession(Simulation simulation, int player = 1, int reservedBotPlayer = 0)
    {
        SimulationHost.ValidateConfiguration(simulation, player);
        AssignedPlayerId = player;
        (ITransport client, ITransport server) = InMemoryTransport.CreatePair(maxMessageSize: WireProtocol.MaxMessageSize);
        int resolvedBot = reservedBotPlayer != 0 ? reservedBotPlayer : (simulation.IsPlayerAdmitted(2) ? 2 : 0);
        _host = new SimulationHost(simulation, server, player, resolvedBot);
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
    public bool Attack(EntityId unit, ContactId target) => _client.Attack(unit, target);

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }
}
