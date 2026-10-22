using System.Diagnostics;

namespace DeadGround.Engine.Net;

public sealed class SimulationHost : IDisposable
{
    public const int MaxTicksPerAdvance = 5;
    public const double DefaultHandshakeTimeoutSeconds = 5.0;
    private readonly Simulation _simulation;
    private readonly IServerTransport _transport;
    private readonly MatchSetup? _matchSetup;
    private readonly int _reservedBotPlayer;
    private readonly double _handshakeTimeoutSeconds;
    private readonly PlayerKnowledge[] _knowledge;
    private readonly long[] _admittedConnectionIds = new long[Simulation.MaxPlayers];
    private readonly long[] _peerConnectionIds = new long[Simulation.MaxPlayers];
    private readonly double[] _peerConnectionTimes = new double[Simulation.MaxPlayers];
    private readonly byte[] _receive = new byte[WireProtocol.MaxMessageSize];
    private readonly byte[] _send = new byte[WireProtocol.MaxMessageSize];
    private readonly byte[] _reply = new byte[WireProtocol.CommandResultSize];
    private readonly byte[] _fragmentBuffer = new byte[WireProtocol.FragmentHeaderSize + WireProtocol.MaxFragmentPayloadSize];
    private uint _snapshotSequence;
    private double _accumulator;
    private double _sessionElapsedTime;

    public int Tick => _simulation.Tick;
    public long BytesSent { get; private set; }
    public double LastTickMilliseconds { get; private set; }
    public long LastTickAllocatedBytes { get; private set; }
    public double PendingSeconds => Math.Max(0, _accumulator);
    public long DroppedCommandResults { get; private set; }
    public int ConnectedPlayerCount => _transport.ConnectedPlayerCount;
    public double HandshakeTimeoutSeconds => _handshakeTimeoutSeconds;

    public SimulationHost(
        Simulation simulation,
        IServerTransport transport,
        MatchSetup? matchSetup = null,
        int reservedBotPlayer = 0,
        double handshakeTimeoutSeconds = DefaultHandshakeTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(transport);
        if (handshakeTimeoutSeconds <= 0 || !double.IsFinite(handshakeTimeoutSeconds))
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeoutSeconds),
                "Handshake timeout must be positive and finite.");
        if (simulation.Capacity > WireProtocol.MaxUnits)
            throw new ArgumentOutOfRangeException(nameof(simulation),
                $"M0 world capacity must not exceed the snapshot limit of {WireProtocol.MaxUnits} units.");

        _simulation = simulation;
        _transport = transport;
        _matchSetup = matchSetup;
        _reservedBotPlayer = reservedBotPlayer;
        _handshakeTimeoutSeconds = handshakeTimeoutSeconds;
        if (matchSetup?.PlayerTeams is { Length: > 0 } teams)
        {
            for (int p = 1; p <= teams.Length; p++)
                simulation.SetPlayerTeam(p, MatchSetup.ResolveTeam(teams, p));
        }
        _knowledge = new PlayerKnowledge[Simulation.MaxPlayers];

        for (int p = 1; p <= Simulation.MaxPlayers; p++)
            _simulation.SetPlayerAdmitted(p, false);

        for (int i = 0; i < Simulation.MaxPlayers; i++)
            _knowledge[i] = new PlayerKnowledge(i + 1, simulation.Capacity);

        if (transport.InitialAdmittedPlayer >= 1 && transport.InitialAdmittedPlayer <= Simulation.MaxPlayers)
        {
            int initial = transport.InitialAdmittedPlayer;
            long connId = transport.GetPlayerConnectionId(initial);
            _admittedConnectionIds[initial - 1] = connId;
            _peerConnectionIds[initial - 1] = connId;
            _simulation.SetPlayerAdmitted(initial, true);
        }

        if (_reservedBotPlayer >= 1 && _reservedBotPlayer <= Simulation.MaxPlayers)
        {
            _simulation.SetPlayerAdmitted(_reservedBotPlayer, true);
        }

        PublishAll();
    }

    public SimulationHost(
        Simulation simulation,
        ITransport transport,
        int player = 1,
        int reservedBotPlayer = 0,
        double handshakeTimeoutSeconds = DefaultHandshakeTimeoutSeconds)
        : this(simulation, CreateSingleTransport(simulation, transport, player), matchSetup: null, reservedBotPlayer: reservedBotPlayer, handshakeTimeoutSeconds: handshakeTimeoutSeconds)
    {
    }

    private static IServerTransport CreateSingleTransport(Simulation simulation, ITransport transport, int player)
    {
        ValidateConfiguration(simulation, player);
        ArgumentNullException.ThrowIfNull(transport);
        return new SinglePlayerServerTransport(transport, player);
    }

    internal static void ValidateConfiguration(Simulation simulation, int player)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        if (simulation.Capacity > WireProtocol.MaxUnits)
            throw new ArgumentOutOfRangeException(nameof(simulation),
                $"M0 world capacity must not exceed the snapshot limit of {WireProtocol.MaxUnits} units.");
        if (player < 1 || player > Simulation.MaxPlayers) throw new ArgumentOutOfRangeException(nameof(player));
    }

    public void Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));

        _sessionElapsedTime += elapsedSeconds;
        _transport.Poll();

        for (int p = 1; p <= Simulation.MaxPlayers; p++)
        {
            long currentConnId = _transport.GetPlayerConnectionId(p);
            if (currentConnId != _peerConnectionIds[p - 1])
            {
                _peerConnectionIds[p - 1] = currentConnId;
                _peerConnectionTimes[p - 1] = currentConnId != 0 ? _sessionElapsedTime : 0;
                if (_admittedConnectionIds[p - 1] != 0 && _admittedConnectionIds[p - 1] != currentConnId)
                {
                    _admittedConnectionIds[p - 1] = 0;
                    _simulation.SetPlayerAdmitted(p, false);
                    _simulation.ResetPlayer(p);
                }
            }

            if (_reservedBotPlayer != 0 && p == _reservedBotPlayer)
            {
                if (currentConnId != 0)
                {
                    RejectHandshake(p, HandshakeReason.ServerFull);
                }
                continue;
            }

            if (currentConnId != 0 && _admittedConnectionIds[p - 1] != currentConnId)
            {
                if (_sessionElapsedTime - _peerConnectionTimes[p - 1] + 1e-9 >= _handshakeTimeoutSeconds)
                {
                    RejectHandshake(p, HandshakeReason.Timeout);
                }
            }
        }

        while (_transport.TryReceive(out int player, out long connectionId, _receive, out int length))
        {
            if (length < 2 || player < 1 || player > Simulation.MaxPlayers) continue;
            if (_reservedBotPlayer != 0 && player == _reservedBotPlayer) continue;

            byte kind = _receive[1];
            if (kind == WireProtocol.HandshakeRequestKind)
            {
                HandleHandshake(player, connectionId, _receive.AsSpan(0, length));
                continue;
            }

            if (kind == WireProtocol.MoveKind)
            {
                if (connectionId == 0 || _admittedConnectionIds[player - 1] != connectionId)
                    continue;

                if (WireProtocol.TryReadMove(_receive.AsSpan(0, length), out MoveCommand command))
                {
                    // Player identity is bound strictly from the transport peer, never trusted from packet.
                    bool queued = _simulation.TryEnqueue(player, command);
                    WireProtocol.WriteCommandResult(_reply, new CommandResult(command.Sequence, Tick, queued));
                    if (_transport.TrySend(player, _reply.AsSpan(0, WireProtocol.CommandResultSize)))
                        BytesSent += WireProtocol.CommandResultSize;
                    else
                        DroppedCommandResults++;
                }
            }
            else if (kind == WireProtocol.AttackKind)
            {
                if (connectionId == 0 || _admittedConnectionIds[player - 1] != connectionId)
                    continue;

                if (WireProtocol.TryReadAttack(_receive.AsSpan(0, length), out AttackCommand command))
                {
                    bool queued = _simulation.TryEnqueue(player, command);
                    WireProtocol.WriteCommandResult(_reply, new CommandResult(command.Sequence, Tick, queued));
                    if (_transport.TrySend(player, _reply.AsSpan(0, WireProtocol.CommandResultSize)))
                        BytesSent += WireProtocol.CommandResultSize;
                    else
                        DroppedCommandResults++;
                }
            }
            else if (kind == WireProtocol.SetFireStanceKind)
            {
                if (connectionId == 0 || _admittedConnectionIds[player - 1] != connectionId)
                    continue;

                if (WireProtocol.TryReadSetFireStance(_receive.AsSpan(0, length), out SetFireStanceCommand command))
                {
                    bool queued = _simulation.TryEnqueue(player, command);
                    WireProtocol.WriteCommandResult(_reply, new CommandResult(command.Sequence, Tick, queued));
                    if (_transport.TrySend(player, _reply.AsSpan(0, WireProtocol.CommandResultSize)))
                        BytesSent += WireProtocol.CommandResultSize;
                    else
                        DroppedCommandResults++;
                }
            }
        }

        // Bound work per call, not elapsed time. Later calls drain the remaining debt.
        _accumulator += elapsedSeconds;
        int steps = 0;
        while (_accumulator + 1e-9 >= 1d / Simulation.TickRate && steps++ < MaxTicksPerAdvance)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            _simulation.Step();
            if (Tick % 2 == 0) PublishAll();
            LastTickMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            LastTickAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            _accumulator -= 1d / Simulation.TickRate;
        }
    }

    private void HandleHandshake(int player, long connectionId, ReadOnlySpan<byte> message)
    {
        if (_reservedBotPlayer != 0 && player == _reservedBotPlayer)
        {
            RejectHandshake(player, HandshakeReason.ServerFull);
            return;
        }

        if (connectionId == 0 || _peerConnectionIds[player - 1] != connectionId)
        {
            return;
        }

        if (_admittedConnectionIds[player - 1] != connectionId &&
            _sessionElapsedTime - _peerConnectionTimes[player - 1] + 1e-9 >= _handshakeTimeoutSeconds)
        {
            RejectHandshake(player, HandshakeReason.Timeout);
            return;
        }

        if (!WireProtocol.TryReadHandshakeRequest(message, out MatchSetup clientSetup))
        {
            RejectHandshake(player, HandshakeReason.ProtocolVersionMismatch);
            return;
        }

        if (_matchSetup is not null)
        {
            if (clientSetup.ProtocolVersion != _matchSetup.ProtocolVersion)
            {
                RejectHandshake(player, HandshakeReason.ProtocolVersionMismatch);
                return;
            }
            if (clientSetup.ContentHash != _matchSetup.ContentHash)
            {
                RejectHandshake(player, HandshakeReason.ContentMismatch);
                return;
            }
            if (clientSetup.MapHash != _matchSetup.MapHash)
            {
                RejectHandshake(player, HandshakeReason.MapMismatch);
                return;
            }
            if (!string.Equals(clientSetup.RuleSetId, _matchSetup.RuleSetId, StringComparison.Ordinal))
            {
                RejectHandshake(player, HandshakeReason.RuleSetMismatch);
                return;
            }
        }

        if (_admittedConnectionIds[player - 1] == connectionId && connectionId != 0)
        {
            SendHandshakeResponse(player, accepted: true, HandshakeReason.Accepted);
            return;
        }

        if (_admittedConnectionIds[player - 1] != 0 && _admittedConnectionIds[player - 1] != connectionId)
        {
            _simulation.SetPlayerAdmitted(player, false);
            _simulation.ResetPlayer(player);
        }

        _admittedConnectionIds[player - 1] = connectionId;
        _simulation.SetPlayerAdmitted(player, true);
        SendHandshakeResponse(player, accepted: true, HandshakeReason.Accepted);
        PublishPlayer(player);
    }

    private void RejectHandshake(int player, HandshakeReason reason)
    {
        _admittedConnectionIds[player - 1] = 0;
        _peerConnectionIds[player - 1] = 0;
        _peerConnectionTimes[player - 1] = 0;
        if (_reservedBotPlayer == 0 || player != _reservedBotPlayer)
        {
            _simulation.SetPlayerAdmitted(player, false);
            _simulation.ResetPlayer(player);
        }
        WireProtocol.WriteHandshakeResponse(_reply, accepted: false, player, reason);
        _transport.DisconnectPlayer(player, _reply.AsSpan(0, WireProtocol.HandshakeResponseSize));
    }

    private void SendHandshakeResponse(int player, bool accepted, HandshakeReason reason)
    {
        WireProtocol.WriteHandshakeResponse(_reply, accepted, player, reason);
        _transport.TrySend(player, _reply.AsSpan(0, WireProtocol.HandshakeResponseSize));
    }

    private void PublishAll()
    {
        for (int p = 1; p <= _knowledge.Length; p++)
        {
            long currentConnId = _transport.GetPlayerConnectionId(p);
            if (currentConnId != 0 && _admittedConnectionIds[p - 1] == currentConnId && _transport.IsPlayerConnected(p))
                PublishPlayer(p);
        }
    }

    private void PublishPlayer(int player)
    {
        PlayerKnowledge knowledge = _knowledge[player - 1];
        _simulation.UpdateKnowledge(knowledge);
        int length = WireProtocol.WriteSnapshot(
            _send,
            checked(++_snapshotSequence),
            Tick,
            knowledge.OwnUnits,
            knowledge.Contacts,
            knowledge.Events,
            knowledge.Mission);

        bool sentSuccessfully = false;
        if (_transport is SinglePlayerServerTransport || length <= WireProtocol.MaxFragmentPayloadSize)
        {
            if (_transport.TrySend(player, _send.AsSpan(0, length), reliable: _transport is SinglePlayerServerTransport))
            {
                BytesSent += length;
                sentSuccessfully = true;
            }
        }
        else
        {
            ushort count = (ushort)((length + WireProtocol.MaxFragmentPayloadSize - 1) / WireProtocol.MaxFragmentPayloadSize);
            bool allFragsSent = true;
            for (ushort i = 0; i < count; i++)
            {
                int offset = i * WireProtocol.MaxFragmentPayloadSize;
                int sliceLen = Math.Min(WireProtocol.MaxFragmentPayloadSize, length - offset);
                int fragLen = WireProtocol.WriteSnapshotFragment(_fragmentBuffer, _snapshotSequence, Tick, i, count, length, offset, _send.AsSpan(offset, sliceLen));
                if (_transport.TrySend(player, _fragmentBuffer.AsSpan(0, fragLen), reliable: false))
                {
                    BytesSent += fragLen;
                }
                else
                {
                    allFragsSent = false;
                }
            }
            sentSuccessfully = allFragsSent;
        }

        if (sentSuccessfully && knowledge.EventCount > 0)
        {
            _simulation.AcknowledgeCombatEvents(player, knowledge.EventCount);
        }
    }

    public void Dispose()
    {
        _simulation.Dispose();
        _transport.Dispose();
    }
}
