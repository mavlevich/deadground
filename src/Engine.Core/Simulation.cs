using System.Numerics;

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
    private readonly SmokeSystem? _smoke;
    private readonly FlightSystem? _flight;
    private readonly ArtillerySystem? _artillery;
    private readonly GuidedMissileSystem? _guidedMissiles;
    private readonly IMissionSystem? _mission;
    private readonly DeterministicRandom[] _domainStreams = new DeterministicRandom[4];

    private readonly QueuedCommand[] _commands;
    private readonly uint[] _lastAccepted = new uint[MaxPlayers];
    private readonly Queue<GroupMoveResult>[] _pendingGroupMoveResults = new Queue<GroupMoveResult>[MaxPlayers];
    private readonly Queue<DisembarkResult>[] _pendingDisembarkResults = new Queue<DisembarkResult>[MaxPlayers];
    private readonly EntityId[] _disembarkPassBuf = new EntityId[32];
    private readonly Vector2[] _disembarkPosBuf = new Vector2[32];
    private readonly bool[] _disembarkSuccessBuf = new bool[32];
    private int _commandCount;

    public int Tick { get; private set; }
    public int Capacity => _world.Capacity;
    public WorldState World => _world;
    public ISimulationRules Rules => _rules;
    public DetectionSystem? Detection => _detection;
    public NavigationSystem? Navigation => _navigation;
    public CombatSystem? Combat => _combat;
    public SmokeSystem? Smoke => _smoke;
    public FlightSystem? Flight => _flight;
    public ArtillerySystem? Artillery => _artillery;
    public GuidedMissileSystem? GuidedMissiles => _guidedMissiles;
    public IMissionSystem? Mission => _mission;
    public bool IsMissionFinished => _mission?.IsFinished == true;
    public GroupMoveResult? LastGroupMoveResult { get; private set; }
    public DisembarkResult? LastDisembarkResult { get; private set; }

    private enum CommandKind : byte
    {
        Move = 0,
        Attack = 1,
        SetFireStance = 2,
        GroupMove = 3,
        BoardTransport = 4,
        Disembark = 5,
        DeploySmoke = 6,
        Helicopter = 7,
        FireMission = 8,
        Stop = 9,
        DeployFlares = 10
    }

    private readonly record struct QueuedCommand(
        int Player,
        CommandKind Kind,
        MoveCommand Move,
        AttackCommand Attack,
        SetFireStanceCommand SetFireStance,
        GroupMoveCommand GroupMove,
        BoardTransportCommand BoardTransport,
        DisembarkCommand Disembark,
        DeploySmokeCommand DeploySmoke,
        HelicopterCommand Helicopter,
        FireMissionCommand FireMission,
        StopCommand Stop,
        DeployFlaresCommand DeployFlares)
    {
        public static QueuedCommand CreateMove(int player, MoveCommand move) =>
            new(player, CommandKind.Move, move, default, default, default, default, default, default, default, default, default, default);

        public static QueuedCommand CreateAttack(int player, AttackCommand attack) =>
            new(player, CommandKind.Attack, default, attack, default, default, default, default, default, default, default, default, default);

        public static QueuedCommand CreateSetFireStance(int player, SetFireStanceCommand stance) =>
            new(player, CommandKind.SetFireStance, default, default, stance, default, default, default, default, default, default, default, default);

        public static QueuedCommand CreateGroupMove(int player, GroupMoveCommand groupMove) =>
            new(player, CommandKind.GroupMove, default, default, default, groupMove, default, default, default, default, default, default, default);

        public static QueuedCommand CreateBoardTransport(int player, BoardTransportCommand board) =>
            new(player, CommandKind.BoardTransport, default, default, default, default, board, default, default, default, default, default, default);

        public static QueuedCommand CreateDisembark(int player, DisembarkCommand disembark) =>
            new(player, CommandKind.Disembark, default, default, default, default, default, disembark, default, default, default, default, default);

        public static QueuedCommand CreateDeploySmoke(int player, DeploySmokeCommand deploySmoke) =>
            new(player, CommandKind.DeploySmoke, default, default, default, default, default, default, deploySmoke, default, default, default, default);

        public static QueuedCommand CreateHelicopter(int player, HelicopterCommand helicopter) =>
            new(player, CommandKind.Helicopter, default, default, default, default, default, default, default, helicopter, default, default, default);

        public static QueuedCommand CreateFireMission(int player, FireMissionCommand fireMission) =>
            new(player, CommandKind.FireMission, default, default, default, default, default, default, default, default, fireMission, default, default);

        public static QueuedCommand CreateStop(int player, StopCommand stop) =>
            new(player, CommandKind.Stop, default, default, default, default, default, default, default, default, default, stop, default);

        public static QueuedCommand CreateDeployFlares(int player, DeployFlaresCommand deployFlares) =>
            new(player, CommandKind.DeployFlares, default, default, default, default, default, default, default, default, default, default, deployFlares);
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

    public Simulation(WorldState world, ISimulationRules rules, ulong matchSeed = 2024UL, int? commandCapacity = null)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        MatchSeed = matchSeed;
        int cmdCap = commandCapacity ?? Math.Max(_world.Capacity, 1024);
        _commands = new QueuedCommand[cmdCap];
        if (_rules is IRandomStreamsAware awareRules)
        {
            awareRules.InitializeRandomStreams(this);
        }
        _detection = (_rules as RuleSet)?.FindSystem<DetectionSystem>();
        _navigation = (_rules as RuleSet)?.FindSystem<NavigationSystem>();
        _combat = (_rules as RuleSet)?.FindSystem<CombatSystem>();
        _smoke = (_rules as RuleSet)?.FindSystem<SmokeSystem>();
        _flight = (_rules as RuleSet)?.FindSystem<FlightSystem>();
        _artillery = (_rules as RuleSet)?.FindSystem<ArtillerySystem>();
        _guidedMissiles = (_rules as RuleSet)?.FindSystem<GuidedMissileSystem>();
        _mission = (_rules as RuleSet)?.FindSystem<IMissionSystem>();
        _smoke?.EnsureUnitsInitialized(_world);
        _flight?.EnsureUnitsInitialized(_world);
        _guidedMissiles?.EnsureUnitsInitialized(_world);
        if (_flight is not null && _combat is not null)
        {
            _combat.SetFlightSystem(_flight);
            _flight.OnCrashImpact = _combat.ResolveHelicopterCrashImpact;
        }
        if (_artillery is not null && _combat is not null)
        {
            _artillery.OnRocketLaunched = _combat.RecordRocketLaunchedEvent;
            _artillery.OnRocketImpact = _combat.ResolveArtilleryImpact;
        }
        if (_guidedMissiles is not null && _combat is not null)
        {
            _guidedMissiles.OnFlaresDeployed = _combat.RecordFlaresDeployedEvent;
            _guidedMissiles.OnMissileDetonated = _combat.ResolveMissileDetonation;
        }
        for (int i = 0; i < MaxPlayers; i++)
        {
            _pendingGroupMoveResults[i] = new Queue<GroupMoveResult>();
            _pendingDisembarkResults[i] = new Queue<DisembarkResult>();
        }
    }

    public bool TryPopGroupMoveResult(int player, out GroupMoveResult result)
    {
        result = default;
        if (player < 1 || player > MaxPlayers) return false;
        return _pendingGroupMoveResults[player - 1].TryDequeue(out result);
    }

    public bool TryPopDisembarkResult(int player, out DisembarkResult result)
    {
        result = default;
        if (player < 1 || player > MaxPlayers) return false;
        return _pendingDisembarkResults[player - 1].TryDequeue(out result);
    }

    // Player identity is supplied by the authenticated connection, never by packet data.
    public bool TryEnqueue(int player, MoveCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length ||
            !_world.Map.Contains(command.Target) || !_world.TryGetUnit(command.Unit, out UnitState unit) ||
            unit.Owner != player || _world.IsContained(command.Unit) || _world.IsCrashing(command.Unit) || _world.IsSlung(command.Unit)) return false;

        if (command.FinalFacing.HasValue && !float.IsFinite(command.FinalFacing.Value)) return false;

        _world.ClearBoardingOrder(command.Unit);
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
            unit.Owner != player || _world.IsContained(command.Unit) || _world.IsCrashing(command.Unit) || _world.IsSlung(command.Unit)) return false;

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

        _world.ClearBoardingOrder(command.Unit);
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
            unit.Owner != player || _world.IsContained(command.Unit) || _world.IsCrashing(command.Unit) || _world.IsSlung(command.Unit)) return false;

        _commands[_commandCount++] = QueuedCommand.CreateSetFireStance(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    public bool TryEnqueue(int player, BoardTransportCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length)
            return false;

        if (!_world.TryGetUnit(command.Unit, out UnitState unit) || unit.Owner != player ||
            _world.IsContained(command.Unit) || _world.IsAircraft(command.Unit) ||
            _world.IsCrashing(command.Unit) || _world.IsSlung(command.Unit))
            return false;
        if (!_world.TryGetUnit(command.Transport, out UnitState transport) || transport.Owner != player)
            return false;
        if (!_world.IsContainer(command.Transport) || transport.Moving ||
            _world.IsAirborne(command.Transport) || _world.IsCrashing(command.Transport) || _world.IsSlung(command.Transport))
            return false;

        int requiredSlots = ResolveInternalCargoSlots(unit.Definition, transport.Definition);
        if (requiredSlots <= 0)
            return false;

        int queuedSeats = 0;
        for (int i = 0; i < _commandCount; i++)
        {
            if (_commands[i].Kind == CommandKind.BoardTransport &&
                _commands[i].BoardTransport.Transport == command.Transport &&
                _commands[i].BoardTransport.Unit != command.Unit &&
                _world.TryGetUnit(_commands[i].BoardTransport.Unit, out UnitState qUnit))
            {
                queuedSeats += Math.Max(1, ResolveInternalCargoSlots(qUnit.Definition, transport.Definition));
            }
        }

        if (_world.GetAvailableSeats(command.Transport) - queuedSeats < requiredSlots)
            return false;

        _commands[_commandCount++] = QueuedCommand.CreateBoardTransport(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    private int ResolveInternalCargoSlots(ushort unitDef, ushort transportDef)
    {
        if (_flight is not null && _flight.TryGetCargoProfile(unitDef, out CargoProfile cargo))
        {
            if (!cargo.CanInternalLoad)
                return 0;
            if (!cargo.IsInfantry)
            {
                if (!_flight.TryGetProfile(transportDef, out FlightProfile fp) || !fp.CanLoadInternalVehicles)
                    return 0;
            }
            return Math.Max(1, cargo.InternalCargoSlots);
        }
        return 1;
    }

    public bool TryEnqueue(int player, DisembarkCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length)
            return false;

        if (!_world.TryGetUnit(command.Transport, out UnitState transport) || transport.Owner != player ||
            _world.IsCrashing(command.Transport) || _world.IsSlung(command.Transport))
            return false;
        if (!_world.IsContainer(command.Transport) || _world.GetPassengerCount(command.Transport) == 0)
            return false;
        if (!_world.Map.Contains(command.Target))
            return false;

        _commands[_commandCount++] = QueuedCommand.CreateDisembark(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    public bool TryEnqueue(int player, DeploySmokeCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length)
            return false;

        if (command.HasTarget && !_world.Map.Contains(command.Target))
            return false;

        if (_smoke is null || !_smoke.CanDeploySmoke(player, command.Unit, _world) ||
            _world.IsCrashing(command.Unit) || _world.IsSlung(command.Unit))
            return false;

        // Prevent duplicate smoke activation in the same tick for the same unit
        for (int i = 0; i < _commandCount; i++)
        {
            if (_commands[i].Kind == CommandKind.DeploySmoke && _commands[i].DeploySmoke.Unit == command.Unit)
            {
                return false;
            }
        }

        _commands[_commandCount++] = QueuedCommand.CreateDeploySmoke(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    public bool TryEnqueue(int player, HelicopterCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length)
            return false;

        if (_flight is null) return false;
        if (command.Kind > HelicopterOrderKind.SlingDrop) return false;
        if (command.Kind == HelicopterOrderKind.SetAltitudeBand && command.AltitudeBand > AltitudeBand.High) return false;
        if (command.HasTarget && !_world.Map.Contains(command.Target)) return false;

        if (!_world.TryGetUnit(command.Unit, out UnitState unit) ||
            unit.Owner != player ||
            _world.IsContained(command.Unit) ||
            _world.IsCrashing(command.Unit) ||
            !_flight.IsAircraftDefinition(unit.Definition))
        {
            return false;
        }

        if (command.Kind == HelicopterOrderKind.SlingPickup)
        {
            if (!_flight.TryGetProfile(unit.Definition, out FlightProfile fp) ||
                !_flight.CanSlingTarget(_world, command.Unit, player, in fp, command.TargetUnit, out _))
            {
                return false;
            }
        }
        else if (command.Kind == HelicopterOrderKind.SlingDrop)
        {
            if (_world.GetSlingLoad(command.Unit) == default)
                return false;
        }
        else if (command.Kind == HelicopterOrderKind.FastRope)
        {
            if (!_flight.TryGetProfile(unit.Definition, out FlightProfile fp) ||
                !fp.CanFastRope ||
                !_flight.HasInfantryPassenger(_world, command.Unit))
            {
                return false;
            }
        }

        _commands[_commandCount++] = QueuedCommand.CreateHelicopter(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    public bool TryEnqueue(int player, FireMissionCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length)
            return false;

        if (_artillery is null || !_artillery.CanExecuteFireMission(player, command, _world))
            return false;

        _world.ClearBoardingOrder(command.Unit);
        _world.ClearPendingDisembark(command.Unit);
        _commands[_commandCount++] = QueuedCommand.CreateFireMission(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    public bool TryEnqueue(int player, StopCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length)
            return false;

        if (!_world.TryGetUnit(command.Unit, out UnitState unit) || unit.Owner != player ||
            _world.IsContained(command.Unit) || _world.IsCrashing(command.Unit) || _world.IsSlung(command.Unit))
            return false;

        _commands[_commandCount++] = QueuedCommand.CreateStop(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    public bool TryEnqueue(int player, DeployFlaresCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length)
            return false;

        if (_guidedMissiles is null || !_guidedMissiles.CanDeployFlares(player, command, _world))
            return false;

        if (!command.ToggleAuto)
        {
            // Prevent duplicate manual flare bursts in the same tick for the same aircraft
            for (int i = 0; i < _commandCount; i++)
            {
                if (_commands[i].Kind == CommandKind.DeployFlares &&
                    !_commands[i].DeployFlares.ToggleAuto &&
                    _commands[i].DeployFlares.Unit == command.Unit)
                {
                    return false;
                }
            }
        }

        _commands[_commandCount++] = QueuedCommand.CreateDeployFlares(player, command);
        _lastAccepted[player - 1] = command.Sequence;
        return true;
    }

    public bool TryEnqueue(int player, GroupMoveCommand command)
    {
        if (IsMissionFinished) return false;
        if (player < 1 || player > _lastAccepted.Length || command.Sequence == 0 ||
            command.Sequence <= _lastAccepted[player - 1] || _commandCount == _commands.Length ||
            !_world.Map.Contains(command.Target)) return false;

        if (command.FinalFacing.HasValue && !float.IsFinite(command.FinalFacing.Value)) return false;

        int unitCount = command.Units.Length;
        if (unitCount <= 0 || unitCount > 1024) return false;

        ReadOnlySpan<EntityId> units = command.Units.Span;

        if (unitCount > 32)
        {
            var seen = new HashSet<uint>(unitCount);
            for (int i = 0; i < unitCount; i++)
            {
                EntityId u = units[i];
                if (u.Value == 0 || !seen.Add(u.Value)) return false;
                if (!_world.TryGetUnit(u, out UnitState unit) || unit.Owner != player || _world.IsContained(u) || _world.IsCrashing(u) || _world.IsSlung(u)) return false;
            }
        }
        else
        {
            for (int i = 0; i < unitCount; i++)
            {
                EntityId u = units[i];
                if (u.Value == 0) return false;
                for (int j = i + 1; j < unitCount; j++)
                {
                    if (units[j].Value == u.Value) return false;
                }
                if (!_world.TryGetUnit(u, out UnitState unit) || unit.Owner != player || _world.IsContained(u) || _world.IsCrashing(u) || _world.IsSlung(u)) return false;
            }
        }

        for (int i = 0; i < unitCount; i++)
        {
            _world.ClearBoardingOrder(units[i]);
        }

        // Copy participants into system-owned bounded storage
        EntityId[] systemOwnedUnits = units.ToArray();
        GroupMoveCommand boundedCommand = command with { Units = systemOwnedUnits };

        _commands[_commandCount++] = QueuedCommand.CreateGroupMove(player, boundedCommand);
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
                    _world.ClearFireMission(u.Id);
                    if (_flight is not null && _world.IsAircraft(u.Id))
                    {
                        _flight.HoldPosition(_world, u.Id);
                    }
                    else
                    {
                        _navigation?.ClearUnit(u.Id);
                        _world.UpdateUnitNavigation(u.Id, u.Position, u.Position, false);
                    }
                }
            }
            return;
        }

        _navigation?.SweepRemovedUnits(_world);

        // Process queued orders: movement, attack, stance, and helicopter commands
        for (int i = 0; i < _commandCount; i++)
        {
            QueuedCommand queued = _commands[i];
            if (queued.Kind == CommandKind.Attack)
            {
                _world.ClearBoardingOrder(queued.Attack.Unit);
                _world.ClearPendingDisembark(queued.Attack.Unit);
                _combat?.TryExecuteAttackOrder(queued.Player, queued.Attack, _world);
            }
            else if (queued.Kind == CommandKind.SetFireStance)
            {
                _world.SetFireStance(queued.SetFireStance.Unit, queued.SetFireStance.Stance);
                _combat?.OnUnitStanceChanged(queued.SetFireStance.Unit, queued.SetFireStance.Stance, _world);
            }
            else if (queued.Kind == CommandKind.GroupMove)
            {
                ReadOnlySpan<EntityId> gUnits = queued.GroupMove.Units.Span;
                int gCount = gUnits.Length;
                int aircraftCount = 0;
                for (int u = 0; u < gCount; u++)
                {
                    _world.ClearPendingDisembark(gUnits[u]);
                    _world.ClearFireMission(gUnits[u]);
                    if (_flight is not null && _world.IsAircraft(gUnits[u]))
                    {
                        aircraftCount++;
                    }
                }

                if (aircraftCount > 0 && _flight is not null)
                {
                    GroupMoveResult result = ExecuteMixedGroupMove(queued.Player, queued.GroupMove, aircraftCount);
                    LastGroupMoveResult = result;
                    _pendingGroupMoveResults[queued.Player - 1].Enqueue(result);
                }
                else if (_navigation is not null)
                {
                    GroupMoveResult result = _navigation.ExecuteGroupMove(queued.Player, queued.GroupMove, _world);
                    LastGroupMoveResult = result;
                    _pendingGroupMoveResults[queued.Player - 1].Enqueue(result);
                }
                else
                {
                    int count = queued.GroupMove.Units.Length;
                    var placed = new GroupMoveAssignment[count];
                    for (int u = 0; u < count; u++)
                    {
                        EntityId id = queued.GroupMove.Units.Span[u];
                        placed[u] = new GroupMoveAssignment(id, queued.GroupMove.Target, queued.GroupMove.TargetSurface, queued.GroupMove.FinalFacing);
                        _world.SetMoveTarget(queued.Player, id, queued.GroupMove.Target);
                        _world.ClearAttackOrder(id);
                    }
                    var result = new GroupMoveResult(queued.GroupMove.Sequence, Tick, queued.GroupMove.Target, queued.GroupMove.TargetSurface, queued.GroupMove.Mode, placed, ReadOnlyMemory<GroupMoveUnplaced>.Empty);
                    LastGroupMoveResult = result;
                    _pendingGroupMoveResults[queued.Player - 1].Enqueue(result);
                }
            }
            else if (queued.Kind == CommandKind.BoardTransport)
            {
                if (!_world.TryGetUnit(queued.BoardTransport.Transport, out UnitState targetTransport) ||
                    targetTransport.Owner != queued.Player || targetTransport.Moving ||
                    _world.IsAirborne(targetTransport.Id) || _world.IsCrashing(targetTransport.Id) || _world.IsSlung(targetTransport.Id) ||
                    !_world.TryGetUnit(queued.BoardTransport.Unit, out UnitState soldier) ||
                    soldier.Owner != queued.Player || _world.IsContained(soldier.Id) || _world.IsAircraft(soldier.Id) || _world.IsSlung(soldier.Id))
                {
                    _world.ClearBoardingOrder(queued.BoardTransport.Unit);
                    continue;
                }

                int requiredSlots = ResolveInternalCargoSlots(soldier.Definition, targetTransport.Definition);
                if (requiredSlots <= 0)
                {
                    _world.ClearBoardingOrder(queued.BoardTransport.Unit);
                    continue;
                }

                _world.ClearBoardingOrder(soldier.Id);
                if (!_world.TryReserveSeat(targetTransport.Id, requiredSlots))
                {
                    continue;
                }

                _world.SetBoardingOrder(soldier.Id, targetTransport.Id, requiredSlots);
                _world.ClearAttackOrder(soldier.Id);

                Vector2 diff = soldier.Position - targetTransport.Position;
                Vector2 dir = diff.LengthSquared() > 0.01f ? Vector2.Normalize(diff) : Vector2.UnitX;

                float transColRadius = (_navigation?.Catalog is not null && _navigation.Catalog.TryGetProfile(targetTransport.Definition, out UnitMovementProfile transProf) && transProf.EffectiveCollisionRadius > 0f)
                    ? transProf.EffectiveCollisionRadius
                    : 1.4f;
                float soldierColRadius = (_navigation?.Catalog is not null && _navigation.Catalog.TryGetProfile(soldier.Definition, out UnitMovementProfile soldProf) && soldProf.EffectiveCollisionRadius > 0f)
                    ? soldProf.EffectiveCollisionRadius
                    : 0.4f;
                float approachRadius = transColRadius + soldierColRadius + 0.4f;
                Vector2 approachPos = targetTransport.Position + dir * approachRadius;

                MoveCommand approachMove = new(
                    queued.BoardTransport.Sequence,
                    queued.BoardTransport.ClientTick,
                    soldier.Id,
                    approachPos,
                    TargetSurface: targetTransport.SupportSurface);

                if (_navigation is not null)
                {
                    if (!_navigation.EnqueueCommand(queued.Player, approachMove, _world))
                    {
                        _world.ClearBoardingOrder(soldier.Id);
                    }
                }
                else
                {
                    _world.SetMoveTarget(queued.Player, soldier.Id, approachPos);
                }
            }
            else if (queued.Kind == CommandKind.Disembark)
            {
                if (_world.TryGetUnit(queued.Disembark.Transport, out UnitState trans) && trans.Owner == queued.Player)
                {
                    float dist = Vector2.Distance(trans.Position, queued.Disembark.Target);
                    if (_flight is not null && _world.IsAircraft(trans.Id))
                    {
                        if (_world.IsAirborne(trans.Id) || dist > 3.0f)
                        {
                            _world.SetPendingDisembark(trans.Id, queued.Disembark.Sequence, queued.Player, queued.Disembark.Target, queued.Disembark.TargetSurface);
                            if (_flight.OrderMove(_world, trans.Id, queued.Disembark.Target, finalFacing: null, landOnArrival: true, Tick))
                            {
                                _world.ClearAttackOrder(trans.Id);
                            }
                        }
                        else
                        {
                            _world.ClearPendingDisembark(trans.Id);
                            ExecuteDisembarkInternal(queued.Player, trans, queued.Disembark.Sequence, queued.Disembark.TargetSurface, queued.Disembark.Target);
                        }
                    }
                    else if (dist > 3.0f)
                    {
                        _world.SetPendingDisembark(trans.Id, queued.Disembark.Sequence, queued.Player, queued.Disembark.Target, queued.Disembark.TargetSurface);
                        MoveCommand moveCmd = new(queued.Disembark.Sequence, queued.Disembark.ClientTick, trans.Id, queued.Disembark.Target, TargetSurface: queued.Disembark.TargetSurface);
                        if (_navigation is not null)
                        {
                            if (_navigation.EnqueueCommand(queued.Player, moveCmd, _world))
                            {
                                _world.ClearAttackOrder(trans.Id);
                            }
                        }
                        else
                        {
                            if (_world.SetMoveTarget(queued.Player, trans.Id, queued.Disembark.Target))
                            {
                                _world.ClearAttackOrder(trans.Id);
                            }
                        }
                    }
                    else
                    {
                        _world.ClearPendingDisembark(trans.Id);
                        ExecuteDisembarkInternal(queued.Player, trans, queued.Disembark.Sequence, queued.Disembark.TargetSurface, queued.Disembark.Target);
                    }
                }
            }
            else if (queued.Kind == CommandKind.DeploySmoke)
            {
                _smoke?.TryExecuteDeploySmoke(queued.Player, queued.DeploySmoke, _world, Tick);
            }
            else if (queued.Kind == CommandKind.Helicopter)
            {
                if (queued.Helicopter.Kind is HelicopterOrderKind.TakeOff or HelicopterOrderKind.FastRope or HelicopterOrderKind.SlingPickup or HelicopterOrderKind.SlingDrop ||
                    (queued.Helicopter.Kind == HelicopterOrderKind.Land && queued.Helicopter.HasTarget))
                {
                    _world.ClearPendingDisembark(queued.Helicopter.Unit);
                    _world.ClearAttackOrder(queued.Helicopter.Unit);
                }
                if (queued.Helicopter.Kind == HelicopterOrderKind.SlingPickup && queued.Helicopter.TargetUnit != default)
                {
                    _navigation?.ClearUnit(queued.Helicopter.TargetUnit);
                }
                _flight?.OrderHelicopter(_world, queued.Helicopter, Tick);
            }
            else if (queued.Kind == CommandKind.FireMission)
            {
                _world.ClearBoardingOrder(queued.FireMission.Unit);
                _world.ClearPendingDisembark(queued.FireMission.Unit);
                _artillery?.TryOrderFireMission(queued.Player, queued.FireMission, _world);
            }
            else if (queued.Kind == CommandKind.Stop)
            {
                EntityId unitId = queued.Stop.Unit;
                if (_world.TryGetUnit(unitId, out UnitState unit) && unit.Owner == queued.Player &&
                    !_world.IsContained(unitId) && !_world.IsCrashing(unitId) && !_world.IsSlung(unitId))
                {
                    _world.ClearAttackOrder(unitId);
                    _world.ClearFireMission(unitId);
                    _world.ClearBoardingOrder(unitId);
                    _world.ClearPendingDisembark(unitId);
                    if (_flight is not null && _world.IsAircraft(unitId))
                    {
                        _flight.HoldPosition(_world, unitId);
                    }
                    else
                    {
                        _navigation?.ClearUnit(unitId);
                        _world.UpdateUnitNavigation(unitId, unit.Position, unit.Position, false);
                    }
                }
            }
            else if (queued.Kind == CommandKind.DeployFlares)
            {
                _guidedMissiles?.TryExecuteDeployFlares(_world, Tick, queued.DeployFlares);
            }
            else
            {
                // A valid accepted movement order cancels an active attack, fire mission, or boarding order on this unit
                _world.ClearBoardingOrder(queued.Move.Unit);
                _world.ClearPendingDisembark(queued.Move.Unit);
                _world.ClearFireMission(queued.Move.Unit);
                if (_flight is not null && _world.IsAircraft(queued.Move.Unit))
                {
                    if (_flight.OrderMove(_world, queued.Move.Unit, queued.Move.Target, queued.Move.FinalFacing, landOnArrival: false, Tick))
                    {
                        _world.ClearAttackOrder(queued.Move.Unit);
                    }
                }
                else if (_navigation is not null)
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
        ProcessPendingDisembarks();
    }

    private GroupMoveResult ExecuteMixedGroupMove(int player, GroupMoveCommand command, int aircraftCount)
    {
        ReadOnlySpan<EntityId> allUnits = command.Units.Span;
        int totalCount = allUnits.Length;
        int groundCount = totalCount - aircraftCount;

        var aircraftAssignments = new GroupMoveAssignment[aircraftCount];
        EntityId[]? groundUnits = groundCount > 0 ? new EntityId[groundCount] : null;
        int airIdx = 0;
        int gndIdx = 0;

        for (int i = 0; i < totalCount; i++)
        {
            EntityId id = allUnits[i];
            if (_world.IsAircraft(id))
            {
                Vector2 target = command.Target;
                if (aircraftCount > 1)
                {
                    float angle = (MathF.Tau * airIdx) / aircraftCount;
                    float spacing = 22.0f;
                    target += new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * spacing;
                    target = new Vector2(
                        Math.Clamp(target.X, 10f, MathF.Max(10f, _world.Map.Width - 10f)),
                        Math.Clamp(target.Y, 10f, MathF.Max(10f, _world.Map.Depth - 10f)));
                }

                if (_flight!.OrderMove(_world, id, target, command.FinalFacing, landOnArrival: false, Tick))
                {
                    _world.ClearAttackOrder(id);
                }
                aircraftAssignments[airIdx++] = new GroupMoveAssignment(id, target, 0, command.FinalFacing);
            }
            else if (groundUnits is not null)
            {
                groundUnits[gndIdx++] = id;
            }
        }

        if (groundUnits is null)
        {
            return new GroupMoveResult(
                command.Sequence,
                Tick,
                command.Target,
                command.TargetSurface,
                command.Mode,
                aircraftAssignments,
                ReadOnlyMemory<GroupMoveUnplaced>.Empty);
        }

        GroupMoveCommand groundCmd = command with { Units = groundUnits };
        GroupMoveResult groundResult;
        if (_navigation is not null)
        {
            groundResult = _navigation.ExecuteGroupMove(player, groundCmd, _world);
        }
        else
        {
            var groundPlaced = new GroupMoveAssignment[groundUnits.Length];
            for (int u = 0; u < groundUnits.Length; u++)
            {
                EntityId id = groundUnits[u];
                groundPlaced[u] = new GroupMoveAssignment(id, command.Target, command.TargetSurface, command.FinalFacing);
                _world.SetMoveTarget(player, id, command.Target);
                _world.ClearAttackOrder(id);
            }
            groundResult = new GroupMoveResult(command.Sequence, Tick, command.Target, command.TargetSurface, command.Mode, groundPlaced, ReadOnlyMemory<GroupMoveUnplaced>.Empty);
        }

        var mergedPlaced = new GroupMoveAssignment[groundResult.Placed.Length + aircraftAssignments.Length];
        groundResult.Placed.Span.CopyTo(mergedPlaced);
        aircraftAssignments.CopyTo(mergedPlaced, groundResult.Placed.Length);

        return groundResult with { Placed = mergedPlaced };
    }

    private void ProcessPendingDisembarks()
    {
        if (_world.PendingDisembarkCount == 0) return;

        int count = _world.ContainerCount;
        for (int k = 0; k < count; k++)
        {
            EntityId transId = _world.GetContainerIdAt(k);
            if (!_world.TryGetPendingDisembark(transId, out WorldState.PendingDisembarkOrder pending))
                continue;

            if (!_world.TryGetUnit(transId, out UnitState trans))
            {
                _world.ClearPendingDisembark(transId);
                continue;
            }

            if (_world.IsAircraft(transId))
            {
                if (_world.TryGetFlightState(transId, out WorldState.UnitFlightState fs))
                {
                    if (fs.Phase == FlightPhase.Crashing)
                    {
                        _world.ClearPendingDisembark(transId);
                        continue;
                    }
                    if (fs.Phase == FlightPhase.Ground && !trans.Moving)
                    {
                        _world.ClearPendingDisembark(transId);
                        ExecuteDisembarkInternal(pending.Player, trans, pending.Sequence, pending.Surface, pending.Target);
                        continue;
                    }
                    // If the helicopter reached hover and aborted landing due to blocked LZ, clear pending disembark
                    if (fs.Phase == FlightPhase.Airborne && !fs.HasMoveTarget && !fs.LandOnArrival)
                    {
                        _world.ClearPendingDisembark(transId);
                        continue;
                    }
                }
                continue;
            }

            if (trans.Moving)
            {
                // Transport still moving
                continue;
            }

            bool shouldUnload = false;
            if (_navigation is null)
            {
                shouldUnload = true;
            }
            else
            {
                bool hasActiveRoute = _navigation.HasActiveRoute(transId);
                PathRequestStatus status = _navigation.GetUnitStatus(transId);

                if (!hasActiveRoute && status != PathRequestStatus.WaitingForClearance)
                {
                    shouldUnload = true;
                }
                else if (status is PathRequestStatus.None or PathRequestStatus.NoPath or PathRequestStatus.InvalidDestination or PathRequestStatus.CapacityExceeded)
                {
                    shouldUnload = true;
                }
                else if (status == PathRequestStatus.WaitingForClearance && pending.WaitTicks >= 15)
                {
                    shouldUnload = true;
                }
            }

            if (shouldUnload)
            {
                _world.ClearPendingDisembark(transId);
                ExecuteDisembarkInternal(pending.Player, trans, pending.Sequence, pending.Surface, pending.Target);
            }
            else
            {
                _world.UpdatePendingDisembark(transId, pending with { WaitTicks = pending.WaitTicks + 1 });
            }
        }
    }

    private void ExecuteDisembarkInternal(int player, UnitState trans, uint sequence, uint targetSurface, Vector2 targetLoc)
    {
        _navigation?.ClearUnit(trans.Id);
        _world.UpdateUnitNavigation(trans.Id, trans.Position, trans.Position, false, Tick, trans.SupportSurface);

        ReadOnlySpan<EntityId> passengers = _world.GetPassengers(trans.Id);
        int totalPassengers = Math.Min(passengers.Length, _disembarkPassBuf.Length);
        if (totalPassengers > 0)
        {
            passengers.Slice(0, totalPassengers).CopyTo(_disembarkPassBuf);

            DestinationEvaluator eval = _navigation is not null
                ? _navigation.Grid.Evaluator
                : new DestinationEvaluator(new StaticGeometry(_world.Map, []));

            uint surface = targetSurface > 0 ? targetSurface : trans.SupportSurface;

            int placedCount = PassengerDistribution.Distribute(
                _world,
                eval,
                trans.Id,
                targetLoc,
                surface,
                _disembarkPassBuf.AsSpan(0, totalPassengers),
                _disembarkPosBuf.AsSpan(0, totalPassengers),
                _disembarkSuccessBuf.AsSpan(0, totalPassengers),
                _navigation?.Catalog);

            for (int p = 0; p < totalPassengers; p++)
            {
                if (_disembarkSuccessBuf[p])
                {
                    _world.Disembark(trans.Id, _disembarkPassBuf[p], _disembarkPosBuf[p], surface);
                    _navigation?.ClearUnit(_disembarkPassBuf[p]);
                }
            }

            byte disembarked = (byte)placedCount;
            byte remaining = (byte)(totalPassengers - placedCount);
            DisembarkResult result = new(
                sequence,
                Tick,
                trans.Id,
                disembarked,
                remaining,
                disembarked > 0);

            LastDisembarkResult = result;
            _pendingDisembarkResults[player - 1].Enqueue(result);
        }
    }

    public void ResetPlayer(int player)
    {
        if (player >= 1 && player <= _lastAccepted.Length)
        {
            _lastAccepted[player - 1] = 0;
            _pendingGroupMoveResults[player - 1].Clear();
            _pendingDisembarkResults[player - 1].Clear();
        }

        _navigation?.ResetPlayer(player);
        _combat?.ResetPlayer(player);
        _detection?.ResetPlayer(player);

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
        ReadOnlySpan<WreckObservation> wrecks = _detection is not null
            ? _detection.GetKnownWrecks(knowledge.Player)
            : [];
        ReadOnlySpan<SmokeCloudObservation> smokeClouds = _detection is not null
            ? _detection.GetVisibleSmokeClouds(knowledge.Player)
            : [];
        MissionObservation mission = _mission is not null
            ? _mission.GetObservation(knowledge.Player)
            : default;
        knowledge.Refresh(_world, contacts, _navigation, events, mission, wrecks, smokeClouds, Tick, _detection);
    }

    private readonly bool[] _admittedPlayers = new bool[MaxPlayers];

    public void AcknowledgeCombatEvents(int player, int count) => _combat?.AcknowledgeEvents(player, count);
    public void SetPlayerAdmitted(int player, bool admitted)
    {
        if (player >= 1 && player <= _admittedPlayers.Length)
        {
            if (!_admittedPlayers[player - 1] && admitted)
            {
                _lastAccepted[player - 1] = 0;
            }
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
