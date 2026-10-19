using System.Numerics;

namespace DeadGround.Engine.Net;

public enum SessionConnectionState
{
    Disconnected,
    Connecting,
    HandshakePending,
    Connected,
    Failed
}

public sealed class ClientSession : IGameSession
{
    private readonly ITransport _transport;
    private readonly MatchSetup? _setup;
    private readonly byte[] _receive = new byte[WireProtocol.MaxMessageSize];
    private readonly byte[] _command = new byte[WireProtocol.MoveSize];
    private readonly byte[] _handshakeBuffer = new byte[128];
    private readonly SnapshotAssembler _assembler = new();
    private uint _nextCommand = 1;
    private double _time, _receivedAt;
    private double _lastHandshakeSendTime = -100;
    private double _disconnectedAt = -1.0;
    private bool _everConnected;

    public const double InterpolationBufferTicks = 3.0;
    public const double MaxClockLagTicks = 6.0;
    private double _renderTick;
    private bool _clockInitialized;

    public ClientReplica Replica { get; }
    public MissionObservation Mission => Replica.LatestMission;
    public double RenderTick => _renderTick;
    public int Tick => Replica.LatestTick;
    public double LastTickMilliseconds => 0;
    public long LastTickAllocatedBytes => 0;
    public double PendingSeconds => 0;
    public long BytesReceived { get; private set; }
    public CommandResult? LastCommandResult { get; private set; }
    public long RejectedCommands { get; private set; }
    public int AssignedPlayerId { get; private set; }
    public SessionConnectionState State { get; private set; }
    public string StatusDescription { get; private set; }
    public bool IsConnected => State == SessionConnectionState.Connected;

    public ClientSession(ITransport transport, MatchSetup? setup = null, int replicaCapacity = WireProtocol.MaxUnits)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _setup = setup;
        Replica = new ClientReplica(replicaCapacity);

        if (_setup is not null)
        {
            State = SessionConnectionState.HandshakePending;
            StatusDescription = "Authenticating with server...";
            if (_transport.IsConnected)
            {
                _everConnected = true;
                _lastHandshakeSendTime = 0;
                SendHandshake();
            }
        }
        else
        {
            State = SessionConnectionState.Connected;
            StatusDescription = "Connected";
            _everConnected = true;
        }

        Poll();
        UpdateRenderClock(0);
    }

    public static ClientSession Connect(string host, int port, MatchSetup setup)
    {
        LiteNetLibClientTransport transport = LiteNetLibTransport.CreateClient(host, port);
        return new ClientSession(transport, setup);
    }

    private void SendHandshake()
    {
        if (_setup is null) return;
        int len = WireProtocol.WriteHandshakeRequest(_handshakeBuffer, _setup);
        _transport.TrySend(_handshakeBuffer.AsSpan(0, len));
    }

    public void Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));

        _time += elapsedSeconds;
        Poll();
        UpdateRenderClock(elapsedSeconds);
    }

    private void UpdateRenderClock(double elapsedSeconds)
    {
        if (Replica.LatestTick == 0)
        {
            _renderTick = 0;
            return;
        }

        if (!_clockInitialized)
        {
            _renderTick = Math.Max(0.0, Replica.LatestTick - InterpolationBufferTicks);
            _clockInitialized = true;
            return;
        }

        double prevRenderTick = _renderTick;
        double targetTick = Replica.LatestTick + (_time - _receivedAt) * Simulation.TickRate - InterpolationBufferTicks;
        double clampedTarget = Math.Min(targetTick, (double)Replica.LatestTick);
        double deltaTicks = elapsedSeconds * Simulation.TickRate;

        if (prevRenderTick < clampedTarget)
        {
            double lag = clampedTarget - prevRenderTick;
            if (lag > MaxClockLagTicks)
            {
                // After a network stall or pause, snap directly to target time to avoid
                // lagging in the past and exceeding the replica buffer history.
                _renderTick = clampedTarget;
            }
            else
            {
                double speed = Math.Clamp(1.0 + lag * 0.5, 1.0, 1.5);
                _renderTick = Math.Min(prevRenderTick + deltaTicks * speed, clampedTarget);
            }
        }
        else
        {
            // Clock is ahead of ideal target (e.g. delayed snapshot just arrived).
            // Retain monotonic progress and slow down or hold until target catches up.
            double error = clampedTarget - prevRenderTick;
            double speed = Math.Max(0.0, 1.0 + error * 0.5);
            _renderTick = Math.Min(prevRenderTick + deltaTicks * speed, (double)Replica.LatestTick);
        }

        // Monotonic invariant: presentation clock must never rewind
        if (_renderTick < prevRenderTick)
        {
            _renderTick = prevRenderTick;
        }
    }

    public bool Move(EntityId unit, Vector2 target)
    {
        if (State != SessionConnectionState.Connected) return false;
        MoveCommand command = new(_nextCommand, Replica.LatestTick, unit, target);
        WireProtocol.WriteMove(_command, command);
        if (!_transport.TrySend(_command.AsSpan(0, WireProtocol.MoveSize))) return false;
        _nextCommand = checked(_nextCommand + 1);
        return true;
    }

    public bool Attack(EntityId unit, ContactId target)
    {
        if (State != SessionConnectionState.Connected) return false;
        AttackCommand command = new(_nextCommand, Replica.LatestTick, unit, target);
        WireProtocol.WriteAttack(_command, command);
        if (!_transport.TrySend(_command.AsSpan(0, WireProtocol.AttackSize))) return false;
        _nextCommand = checked(_nextCommand + 1);
        return true;
    }

    public bool SetFireStance(EntityId unit, FireStance stance)
    {
        if (State != SessionConnectionState.Connected) return false;
        SetFireStanceCommand command = new(_nextCommand, Replica.LatestTick, unit, stance);
        WireProtocol.WriteSetFireStance(_command, command);
        if (!_transport.TrySend(_command.AsSpan(0, WireProtocol.SetFireStanceSize))) return false;
        _nextCommand = checked(_nextCommand + 1);
        return true;
    }

    private void Poll()
    {
        while (_transport.TryReceive(_receive, out int length))
        {
            if (length < 2) continue;
            BytesReceived += length;
            byte kind = _receive[1];

            if (kind == WireProtocol.HandshakeResponseKind)
            {
                if (WireProtocol.TryReadHandshakeResponse(_receive.AsSpan(0, length), out bool accepted, out int assignedPlayer, out HandshakeReason reason))
                {
                    if (accepted)
                    {
                        AssignedPlayerId = assignedPlayer;
                        State = SessionConnectionState.Connected;
                        StatusDescription = $"Connected (Player {assignedPlayer})";
                    }
                    else
                    {
                        State = SessionConnectionState.Failed;
                        StatusDescription = $"Connection rejected: {reason}";
                        _transport.Dispose();
                    }
                }
                continue;
            }

            if (kind == WireProtocol.CommandResultKind)
            {
                if (WireProtocol.TryReadCommandResult(_receive.AsSpan(0, length), out CommandResult result))
                {
                    if (result.Sequence >= _nextCommand || result.Sequence <= (LastCommandResult?.Sequence ?? 0)) continue;
                    LastCommandResult = result;
                    if (!result.Queued) RejectedCommands++;
                }
                continue;
            }

            if (kind == WireProtocol.SnapshotKind)
            {
                if (Replica.TryApply(_receive.AsSpan(0, length)))
                    _receivedAt = _time;
                continue;
            }

            if (kind == WireProtocol.SnapshotFragmentKind)
            {
                if (_assembler.TryAddFragment(_receive.AsSpan(0, length), out ReadOnlySpan<byte> completeSnapshot))
                {
                    if (Replica.TryApply(completeSnapshot))
                        _receivedAt = _time;
                }
                continue;
            }
        }

        if (_transport.IsConnected)
        {
            _everConnected = true;
            _disconnectedAt = -1.0;
            if (State == SessionConnectionState.HandshakePending && _setup is not null)
            {
                if (_time - _lastHandshakeSendTime >= 1.0)
                {
                    _lastHandshakeSendTime = _time;
                    SendHandshake();
                }
            }
        }
        else
        {
            if (State == SessionConnectionState.Connected)
            {
                State = SessionConnectionState.Disconnected;
                StatusDescription = "Disconnected from server";
            }
            else if (State == SessionConnectionState.HandshakePending)
            {
                if (_transport.TerminalData is { Length: > 0 } terminalBytes &&
                    WireProtocol.TryReadHandshakeResponse(terminalBytes, out bool accepted, out _, out HandshakeReason reason) &&
                    !accepted)
                {
                    State = SessionConnectionState.Failed;
                    StatusDescription = $"Connection rejected: {reason}";
                    _transport.Dispose();
                    return;
                }

                while (_transport.TryReceive(_receive, out int length))
                {
                    if (length >= 2 && _receive[1] == WireProtocol.HandshakeResponseKind &&
                        WireProtocol.TryReadHandshakeResponse(_receive.AsSpan(0, length), out bool acc, out _, out HandshakeReason r) &&
                        !acc)
                    {
                        State = SessionConnectionState.Failed;
                        StatusDescription = $"Connection rejected: {r}";
                        _transport.Dispose();
                        return;
                    }
                }

                if (_transport.TerminalData is { Length: > 0 } termBytes &&
                    WireProtocol.TryReadHandshakeResponse(termBytes, out bool acc2, out _, out HandshakeReason r2) &&
                    !acc2)
                {
                    State = SessionConnectionState.Failed;
                    StatusDescription = $"Connection rejected: {r2}";
                    _transport.Dispose();
                    return;
                }

                if (_disconnectedAt < 0)
                {
                    _disconnectedAt = _time;
                }

                bool graceExpired = _disconnectedAt >= 0 && (_time - _disconnectedAt >= 1.0);
                if (_transport.HasTerminalEvent || graceExpired || _time >= 10.0)
                {
                    State = SessionConnectionState.Failed;
                    StatusDescription = _everConnected ? "Connection closed by server" : "Connection timed out";
                    _transport.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        _transport.Dispose();
        if (State is SessionConnectionState.Connected or SessionConnectionState.HandshakePending)
        {
            State = SessionConnectionState.Disconnected;
            StatusDescription = "Disconnected";
        }
    }
}
