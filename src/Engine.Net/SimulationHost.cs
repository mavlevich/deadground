using System.Diagnostics;

namespace DeadGround.Engine.Net;

public sealed class SimulationHost : IDisposable
{
    public const int MaxTicksPerAdvance = 5;
    private readonly Simulation _simulation;
    private readonly ITransport _transport;
    private readonly PlayerKnowledge _knowledge;
    private readonly byte[] _receive = new byte[WireProtocol.MaxMessageSize];
    private readonly byte[] _send = new byte[WireProtocol.MaxMessageSize];
    private readonly byte[] _reply = new byte[WireProtocol.CommandResultSize];
    private uint _snapshotSequence;
    private double _accumulator;
    public int Tick => _simulation.Tick;
    public long BytesSent { get; private set; }
    public double LastTickMilliseconds { get; private set; }
    public long LastTickAllocatedBytes { get; private set; }
    public double PendingSeconds => Math.Max(0, _accumulator);
    public long DroppedCommandResults { get; private set; }

    public SimulationHost(Simulation simulation, ITransport transport, int player)
    {
        ValidateConfiguration(simulation, player);
        ArgumentNullException.ThrowIfNull(transport);
        _simulation = simulation;
        _transport = transport;
        _knowledge = new PlayerKnowledge(player, simulation.Capacity);
        Publish();
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
        while (_transport.TryReceive(_receive, out int length))
            if (WireProtocol.TryReadMove(_receive.AsSpan(0, length), out MoveCommand command))
            {
                bool queued = _simulation.TryEnqueue(_knowledge.Player, command);
                WireProtocol.WriteCommandResult(_reply, new CommandResult(command.Sequence, Tick, queued));
                if (_transport.TrySend(_reply)) BytesSent += _reply.Length;
                else DroppedCommandResults++;
            }
        // Bound work per call, not elapsed time. Later calls drain the remaining debt.
        _accumulator += elapsedSeconds;
        int steps = 0;
        while (_accumulator + 1e-9 >= 1d / Simulation.TickRate && steps++ < MaxTicksPerAdvance)
        {
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            _simulation.Step();
            if (Tick % 2 == 0) Publish();
            LastTickMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            LastTickAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            _accumulator -= 1d / Simulation.TickRate;
        }
    }

    private void Publish()
    {
        _simulation.UpdateKnowledge(_knowledge);
        int length = WireProtocol.WriteSnapshot(_send, checked(++_snapshotSequence), Tick, _knowledge.OwnUnits);
        if (_transport.TrySend(_send.AsSpan(0, length))) BytesSent += length;
    }

    public void Dispose() { _simulation.Dispose(); _transport.Dispose(); }
}
