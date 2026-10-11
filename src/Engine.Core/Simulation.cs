namespace DeadGround.Engine;

public sealed class Simulation : IDisposable
{
    public const int TickRate = 20;
    public const int MaxPlayers = 4;
    public const float TickSeconds = 1f / TickRate;
    private readonly WorldState _world;
    private readonly ISimulationRules _rules;
    private readonly DetectionSystem? _detection;
    private readonly PendingMove[] _commands = new PendingMove[256];
    private readonly uint[] _lastAccepted = new uint[MaxPlayers];
    private int _commandCount;
    public int Tick { get; private set; }
    public int Capacity => _world.Capacity;
    public WorldState World => _world;
    public ISimulationRules Rules => _rules;
    public DetectionSystem? Detection => _detection;
    private readonly record struct PendingMove(int Player, MoveCommand Command);

    public Simulation(WorldState world, ISimulationRules rules)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _detection = (_rules as RuleSet)?.FindSystem<DetectionSystem>();
    }

    // Player identity is supplied by the authenticated connection, never by packet data.
    public bool TryEnqueue(int player, MoveCommand command)
    {
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length ||
            !_world.Map.Contains(command.Target) || !_world.TryGetUnit(command.Unit, out UnitState unit) ||
            unit.Owner != player) return false;
        _commands[_commandCount++] = new PendingMove(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    public void Step()
    {
        Tick = checked(Tick + 1);
        for (int i = 0; i < _commandCount; i++)
        {
            PendingMove pending = _commands[i];
            _world.SetMoveTarget(pending.Player, pending.Command.Unit, pending.Command.Target);
        }
        _commandCount = 0;
        _rules.Update(_world, Tick);
    }

    public void ResetPlayer(int player)
    {
        if (player >= 1 && player <= _lastAccepted.Length)
            _lastAccepted[player - 1] = 0;
    }

    public void SetPlayerTeam(int player, int team) => _world.SetPlayerTeam(player, team);
    public int GetPlayerTeam(int player) => _world.GetPlayerTeam(player);
    public bool AreHostile(int playerA, int playerB) => _world.AreHostile(playerA, playerB);

    public void UpdateKnowledge(PlayerKnowledge knowledge)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        ReadOnlySpan<ContactObservation> contacts = _detection is not null
            ? _detection.GetContacts(knowledge.Player)
            : [];
        knowledge.Refresh(_world, contacts);
    }
    public void Dispose() => _world.Dispose();
}
