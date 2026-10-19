namespace DeadGround.Engine.Net;

public interface IGameSession : IDisposable
{
    ClientReplica Replica { get; }
    MissionObservation Mission { get; }
    double RenderTick { get; }
    int Tick { get; }
    double LastTickMilliseconds { get; }
    long LastTickAllocatedBytes { get; }
    long BytesReceived { get; }
    double PendingSeconds { get; }
    CommandResult? LastCommandResult { get; }
    long RejectedCommands { get; }
    bool IsConnected { get; }
    int AssignedPlayerId { get; }
    string StatusDescription { get; }
    bool Move(EntityId unit, System.Numerics.Vector2 target);
    bool Attack(EntityId unit, ContactId target);
    bool SetFireStance(EntityId unit, FireStance stance);
    void Advance(double elapsedSeconds);
}
