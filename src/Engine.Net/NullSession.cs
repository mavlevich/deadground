using System.Numerics;

namespace DeadGround.Engine.Net;

public sealed class NullSession : IGameSession
{
    public ClientReplica Replica { get; } = new();
    public MissionObservation Mission { get; } = default;
    public double RenderTick => 0;
    public int Tick => 0;
    public double LastTickMilliseconds => 0;
    public long LastTickAllocatedBytes => 0;
    public long BytesReceived => 0;
    public double PendingSeconds => 0;
    public CommandResult? LastCommandResult => null;
    public GroupMoveResult? LastGroupMoveResult => null;
    public DisembarkResult? LastDisembarkResult => null;
    public uint LastSubmittedCommandSequence => 0;
    public long RejectedCommands => 0;
    public bool IsConnected => false;
    public int AssignedPlayerId => 0;
    public string StatusDescription => "Idle";

    public bool Move(EntityId unit, Vector2 target, float? finalFacing = null, MoveMode mode = MoveMode.Forward, uint? targetSurface = null) => false;
    public bool GroupMove(ReadOnlyMemory<EntityId> units, Vector2 target, uint targetSurface = 0, float? finalFacing = null, MoveMode mode = MoveMode.Forward) => false;
    public bool Attack(EntityId unit, ContactId target) => false;
    public bool SetFireStance(EntityId unit, FireStance stance) => false;
    public bool BoardTransport(EntityId unit, EntityId transport) => false;
    public bool Disembark(EntityId transport, Vector2 target, uint targetSurface = 0) => false;
    public bool DeploySmoke(EntityId unit, bool hasTarget = false, Vector2 target = default, uint targetSurface = 0) => false;
    public bool OrderHelicopter(EntityId unit, HelicopterOrderKind kind, AltitudeBand altitudeBand = AltitudeBand.High, bool hasTarget = false, Vector2 target = default, EntityId targetUnit = default) => false;
    public bool FireMission(EntityId unit, Vector2 target, bool cancel = false) => false;
    public bool Stop(EntityId unit) => false;
    public bool DeployFlares(EntityId unit, bool toggleAuto = false) => false;
    public bool AttackGround(EntityId unit, Vector3 targetPoint, StructureId targetStructure = default) => false;
    public void Advance(double elapsedSeconds) { }
    public void Dispose() { }
}
