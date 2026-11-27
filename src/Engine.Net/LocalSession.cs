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
    public GroupMoveResult? LastGroupMoveResult => _client.LastGroupMoveResult;
    public DisembarkResult? LastDisembarkResult => _client.LastDisembarkResult;
    public uint LastSubmittedCommandSequence => _client.LastSubmittedCommandSequence;
    public long RejectedCommands => _client.RejectedCommands;
    public bool IsConnected => true;
    public int AssignedPlayerId { get; }
    public string StatusDescription => "Local authoritative simulation";

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

    public bool Move(EntityId unit, Vector2 target, float? finalFacing = null, MoveMode mode = MoveMode.Forward, uint? targetSurface = null) => _client.Move(unit, target, finalFacing, mode, targetSurface);
    public bool GroupMove(ReadOnlyMemory<EntityId> units, Vector2 target, uint targetSurface = 0, float? finalFacing = null, MoveMode mode = MoveMode.Forward) => _client.GroupMove(units, target, targetSurface, finalFacing, mode);
    public bool Attack(EntityId unit, ContactId target) => _client.Attack(unit, target);
    public bool SetFireStance(EntityId unit, FireStance stance) => _client.SetFireStance(unit, stance);
    public bool BoardTransport(EntityId unit, EntityId transport) => _client.BoardTransport(unit, transport);
    public bool Disembark(EntityId transport, Vector2 target, uint targetSurface = 0) => _client.Disembark(transport, target, targetSurface);
    public bool DeploySmoke(EntityId unit, bool hasTarget = false, Vector2 target = default, uint targetSurface = 0) => _client.DeploySmoke(unit, hasTarget, target, targetSurface);
    public bool OrderHelicopter(EntityId unit, HelicopterOrderKind kind, AltitudeBand altitudeBand = AltitudeBand.High, bool hasTarget = false, Vector2 target = default, EntityId targetUnit = default) => _client.OrderHelicopter(unit, kind, altitudeBand, hasTarget, target, targetUnit);
    public bool FireMission(EntityId unit, Vector2 target, bool cancel = false) => _client.FireMission(unit, target, cancel);
    public bool Stop(EntityId unit) => _client.Stop(unit);
    public bool DeployFlares(EntityId unit, bool toggleAuto = false) => _client.DeployFlares(unit, toggleAuto);
    public bool AttackGround(EntityId unit, Vector3 targetPoint, StructureId targetStructure = default) => _client.AttackGround(unit, targetPoint, targetStructure);
    public bool EnterBuilding(EntityId unit, StructureId targetStructure, GarrisonSlotKind slotKind = GarrisonSlotKind.Interior) => _client.EnterBuilding(unit, targetStructure, slotKind);
    public bool ExitBuilding(EntityId unit) => _client.ExitBuilding(unit);

    public bool TrySpawnSandboxUnit(
        int owner,
        ushort definition,
        Vector2 position,
        float speed,
        int maxHealth,
        int ammo,
        float initialYaw,
        FireStance stance,
        int passengerCapacity,
        out EntityId spawnedId) =>
        _host.TrySpawnSandboxUnit(owner, definition, position, speed, maxHealth, ammo, initialYaw, stance, passengerCapacity, out spawnedId);

    public int SetAllUnitsFireStanceForOwner(int owner, FireStance stance) =>
        _host.SetAllUnitsFireStanceForOwner(owner, stance);

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }
}
