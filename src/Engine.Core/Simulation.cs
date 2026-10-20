namespace DeadGround.Engine;

public sealed class Simulation : IDisposable, IRandomStreamProvider
{
    public const int TickRate = 20;
    public const int MaxPlayers = 4;
    public const float TickSeconds = 1f / TickRate;
    private readonly WorldState _world;
    private readonly ISimulationRules _rules;
    private readonly DetectionSystem? _detection;
    private readonly NavigationSystem? _navigation;
    private readonly CombatSystem? _combat;
    private readonly IMissionSystem? _mission;
    private readonly DeterministicRandom[] _domainStreams = new DeterministicRandom[4];

    private readonly QueuedCommand[] _commands = new QueuedCommand[256];
    private readonly uint[] _lastAccepted = new uint[MaxPlayers];
    private int _commandCount;

    public int Tick { get; private set; }
    public int Capacity => _world.Capacity;
    public WorldState World => _world;
    public ISimulationRules Rules => _rules;
    public DetectionSystem? Detection => _detection;
    public NavigationSystem? Navigation => _navigation;
    public CombatSystem? Combat => _combat;
    public IMissionSystem? Mission => _mission;
    public bool IsMissionFinished => _mission?.IsFinished == true;

    private enum CommandKind : byte
    {
        Move = 0,
        Attack = 1,
        SetFireStance = 2
    }

    private readonly record struct QueuedCommand(
        int Player,
        CommandKind Kind,
        MoveCommand Move,
        AttackCommand Attack,
        SetFireStanceCommand SetFireStance)
    {
        public static QueuedCommand CreateMove(int player, MoveCommand move) =>
            new(player, CommandKind.Move, move, default, default);

        public static QueuedCommand CreateAttack(int player, AttackCommand attack) =>
            new(player, CommandKind.Attack, default, attack, default);

        public static QueuedCommand CreateSetFireStance(int player, SetFireStanceCommand stance) =>
            new(player, CommandKind.SetFireStance, default, default, stance);
    }

    public ulong MatchSeed { get; }

    public DeterministicRandom GetStream(RandomDomain domain)
    {
        int index = (int)domain;
        if (index < 0 || index >= _domainStreams.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(domain));
        }
        return _domainStreams[index] ??= DeterministicRandom.CreateDomainStream(MatchSeed, domain);
    }

    public Simulation(WorldState world, ISimulationRules rules, ulong matchSeed = 2024UL)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        MatchSeed = matchSeed;
        if (_rules is IRandomStreamsAware awareRules)
        {
            awareRules.InitializeRandomStreams(this);
        }
        _detection = (_rules as RuleSet)?.FindSystem<DetectionSystem>();
        _navigation = (_rules as RuleSet)?.FindSystem<NavigationSystem>();
        _combat = (_rules as RuleSet)?.FindSystem<CombatSystem>();
        _mission = (_rules as RuleSet)?.FindSystem<IMissionSystem>();
    }

    // Player identity is supplied by the authenticated connection, never by packet data.
    public bool TryEnqueue(int player, MoveCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length ||
            !_world.Map.Contains(command.Target) || !_world.TryGetUnit(command.Unit, out UnitState unit) ||
            unit.Owner != player) return false;

        _commands[_commandCount++] = QueuedCommand.CreateMove(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    public bool TryEnqueue(int player, AttackCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length ||
            command.Target.Value == 0 || !_world.TryGetUnit(command.Unit, out UnitState unit) ||
            unit.Owner != player) return false;

        // Contact validation: target ContactId must be in sender's current contact knowledge
        if (_detection is not null)
        {
            SideContactTracker tracker = _detection.GetTracker(player);
            if (!tracker.TryGetContact(command.Target, out ContactObservation contact) ||
                contact.State != ContactState.Current)
            {
                return false;
            }
        }

        _commands[_commandCount++] = QueuedCommand.CreateAttack(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    public bool TryEnqueue(int player, SetFireStanceCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length ||
            command.Stance > FireStance.HoldFire ||
            !_world.TryGetUnit(command.Unit, out UnitState unit) ||
            unit.Owner != player) return false;

        _commands[_commandCount++] = QueuedCommand.CreateSetFireStance(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    private bool _missionCleanupPerformed;

    public void Step()
    {
        Tick = checked(Tick + 1);

        if (IsMissionFinished)
        {
            if (!_missionCleanupPerformed)
            {
                _missionCleanupPerformed = true;
                _commandCount = 0;
                for (int i = 0; i < _world.Count; i++)
                {
                    UnitState u = _world.GetUnitAt(i);
                    _world.ClearAttackOrder(u.Id);
                    _navigation?.ClearUnit(u.Id);
                    _world.UpdateUnitNavigation(u.Id, u.Position, u.Position, false);
                }
            }
            return;
        }

        _navigation?.SweepRemovedUnits(_world);

        // Process queued orders: movement, attack, and stance commands
        for (int i = 0; i < _commandCount; i++)
        {
            QueuedCommand queued = _commands[i];
            if (queued.Kind == CommandKind.Attack)
            {
                _combat?.TryExecuteAttackOrder(queued.Player, queued.Attack, _world);
            }
            else if (queued.Kind == CommandKind.SetFireStance)
            {
                _world.SetFireStance(queued.SetFireStance.Unit, queued.SetFireStance.Stance);
                _combat?.OnUnitStanceChanged(queued.SetFireStance.Unit, queued.SetFireStance.Stance, _world);
            }
            else
            {
                // A valid accepted movement order cancels an active attack order on this unit
                if (_navigation is not null)
                {
                    if (_navigation.EnqueueCommand(queued.Player, queued.Move, _world))
                    {
                        _world.ClearAttackOrder(queued.Move.Unit);
                    }
                }
                else
                {
                    if (_world.SetMoveTarget(queued.Player, queued.Move.Unit, queued.Move.Target))
                    {
                        _world.ClearAttackOrder(queued.Move.Unit);
                    }
                }
            }
        }
        _commandCount = 0;

        _rules.Update(_world, Tick);
    }

    public void ResetPlayer(int player)
    {
        if (player >= 1 && player <= _lastAccepted.Length)
            _lastAccepted[player - 1] = 0;

        _navigation?.ResetPlayer(player);
        _combat?.ResetPlayer(player);

        // Remove unexecuted queued commands for this player slot
        int writeIdx = 0;
        for (int i = 0; i < _commandCount; i++)
        {
            if (_commands[i].Player != player)
                _commands[writeIdx++] = _commands[i];
        }
        _commandCount = writeIdx;
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
        ReadOnlySpan<CombatEventObservation> events = _combat is not null
            ? _combat.GetEvents(knowledge.Player)
            : [];
        MissionObservation mission = _mission is not null
            ? _mission.GetObservation(knowledge.Player)
            : default;
        knowledge.Refresh(_world, contacts, _navigation, events, mission);
    }

    private readonly bool[] _admittedPlayers = new bool[MaxPlayers];

    public void AcknowledgeCombatEvents(int player, int count) => _combat?.AcknowledgeEvents(player, count);
    public void SetPlayerAdmitted(int player, bool admitted)
    {
        if (player >= 1 && player <= _admittedPlayers.Length)
        {
            _admittedPlayers[player - 1] = admitted;
        }
        _combat?.SetPlayerAdmitted(player, admitted);
    }

    public bool IsPlayerAdmitted(int player)
    {
        if (player >= 1 && player <= _admittedPlayers.Length)
            return _admittedPlayers[player - 1];
        return false;
    }

    public void Dispose() => _world.Dispose();
}
