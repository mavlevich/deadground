using System.Numerics;
using Arch.Core;
using EcsWorld = Arch.Core.World;

namespace DeadGround.Engine;

// Arch handles never leave this storage adapter.
public sealed class WorldState : IDisposable
{
    private readonly EcsWorld _storage = EcsWorld.Create();
    private readonly Entity[] _entities;
    private readonly Dictionary<EntityId, int> _indices;
    private readonly int[] _playerTeams;
    private uint _nextId = 1;
    private bool _disposed;
    public HeightMap Map { get; }
    public int Count { get; private set; }
    public uint RemovalCount { get; private set; }
    public int Capacity => _entities.Length;
    internal EcsWorld Storage => _storage;

    public const int MaxWrecks = 128;
    private readonly WreckState[] _wrecks = new WreckState[MaxWrecks];
    private int _wreckCount;
    private uint _nextWreckId = 1;

    public int WreckCount => _wreckCount;
    public ReadOnlySpan<WreckState> Wrecks => _wrecks.AsSpan(0, _wreckCount);

    public WreckState AddWreck(
        ushort definition,
        Vector2 position,
        uint supportSurface,
        float hullHeading,
        float turretYaw,
        int destroyedTick,
        int originalOwner)
    {
        uint idVal = _nextWreckId++;
        if (_nextWreckId == 0) _nextWreckId = 1;
        WreckId id = new(idVal);
        WreckState wreck = new(
            id,
            definition,
            position,
            supportSurface,
            hullHeading,
            turretYaw,
            destroyedTick,
            originalOwner);

        if (_wreckCount < _wrecks.Length)
        {
            _wrecks[_wreckCount++] = wreck;
        }
        else
        {
            Array.Copy(_wrecks, 1, _wrecks, 0, _wrecks.Length - 1);
            _wrecks[^1] = wreck;
        }

        return wreck;
    }

    public void ClearWrecks()
    {
        _wreckCount = 0;
        _nextWreckId = 1;
    }

    public const int MaxSmokeClouds = 64;
    private readonly SmokeCloudState[] _smokeClouds = new SmokeCloudState[MaxSmokeClouds];
    private int _smokeCloudCount;
    private uint _nextSmokeCloudId = 1;

    public int SmokeCloudCount => _smokeCloudCount;
    public ReadOnlySpan<SmokeCloudState> SmokeClouds => _smokeClouds.AsSpan(0, _smokeCloudCount);

    public SmokeCloudState AddSmokeCloud(
        int owner,
        Vector2 position,
        uint supportSurface,
        float radius,
        float height,
        int createdTick,
        int expiresTick,
        ushort bloomTicks,
        ushort fadeTicks,
        float opticalDensity)
    {
        uint idVal = _nextSmokeCloudId++;
        if (_nextSmokeCloudId == 0) _nextSmokeCloudId = 1;
        SmokeCloudState cloud = new(
            new SmokeCloudId(idVal),
            owner,
            position,
            supportSurface,
            radius,
            height,
            createdTick,
            expiresTick,
            bloomTicks,
            fadeTicks,
            opticalDensity);

        if (_smokeCloudCount < _smokeClouds.Length)
        {
            _smokeClouds[_smokeCloudCount++] = cloud;
        }
        else
        {
            // Evict oldest cloud to stay bounded
            Array.Copy(_smokeClouds, 1, _smokeClouds, 0, _smokeClouds.Length - 1);
            _smokeClouds[^1] = cloud;
        }

        return cloud;
    }

    public int RemoveExpiredSmokeClouds(int tick)
    {
        int removed = 0;
        int write = 0;
        for (int i = 0; i < _smokeCloudCount; i++)
        {
            if (_smokeClouds[i].IsExpired(tick))
            {
                removed++;
            }
            else
            {
                _smokeClouds[write++] = _smokeClouds[i];
            }
        }
        _smokeCloudCount = write;
        return removed;
    }

    public void ClearSmokeClouds()
    {
        _smokeCloudCount = 0;
        _nextSmokeCloudId = 1;
    }

    private record struct Identity(EntityId Id, int Owner, ushort Definition);

    public sealed class ContainerData
    {
        public int Capacity;
        public int ReservedSeats;
        public int PassengerCount;
        public readonly EntityId[] Passengers = new EntityId[32];
    }

    public readonly record struct PendingDisembarkOrder(
        uint Sequence,
        int Player,
        Vector2 Target,
        uint Surface,
        int WaitTicks = 0);

    private readonly Dictionary<EntityId, ContainerData> _containers = new();
    private readonly List<EntityId> _containerIds = new(32);
    private readonly Dictionary<EntityId, EntityId> _containedIn = new();
    private readonly Dictionary<EntityId, EntityId> _boardingOrders = new();
    private readonly Dictionary<EntityId, PendingDisembarkOrder> _pendingDisembarks = new();

    public WorldState(HeightMap map, int capacity = 1024, ReadOnlySpan<int> playerTeams = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Map = map;
        _entities = new Entity[capacity];
        _indices = new Dictionary<EntityId, int>(capacity);
        _playerTeams = new int[Simulation.MaxPlayers];
        for (int i = 0; i < _playerTeams.Length; i++)
        {
            _playerTeams[i] = MatchSetup.ResolveTeam(playerTeams, i + 1);
        }
    }

    public void SetPlayerTeam(int player, int team)
    {
        if (player >= 1 && player <= _playerTeams.Length)
        {
            _playerTeams[player - 1] = team;
        }
    }

    public int GetPlayerTeam(int player) =>
        (player >= 1 && player <= _playerTeams.Length) ? _playerTeams[player - 1] : player;

    public bool AreHostile(int playerA, int playerB) =>
        playerA != playerB && GetPlayerTeam(playerA) != GetPlayerTeam(playerB);

    public record struct CombatState(
        int CurrentHealth,
        int MaxHealth,
        int Ammunition,
        int ReloadTicksRemaining,
        bool HasAttackOrder,
        ContactId TargetContactId,
        EntityId TargetEntityId,
        AttackExecutionStatus Status,
        FireStance Stance = FireStance.FireAtWill,
        Vector2 PursuitDestination = default,
        EntityId LastTookFireAttacker = default,
        int LastTookFireTick = -1,
        int LastPursuitRequestTick = -1,
        float TurretYaw = 0f,
        float HullHeading = 0f,
        int ConsecutiveShotsOnTarget = 0,
        EntityId LastTargetEntityId = default,
        Vector2 LastTargetPosition = default,
        int LastMovedTick = -1,
        bool HasSwitchedTargetThisReload = false);

    public record struct UnitSmokeState(
        bool Initialized,
        bool HasCapability,
        int ChargesRemaining,
        int CooldownTicksRemaining,
        int ActiveEmitTicksRemaining,
        int EmitIntervalRemaining,
        bool HasLastCloud,
        Vector2 LastCloudPosition,
        int LastCloudTick);

    public EntityId CreateUnit(
        int owner,
        ushort definition,
        Vector2 position,
        float speed,
        int maxHealth = 100,
        int ammo = 0,
        float initialTurretYaw = 0f,
        uint initialSupportSurface = 0,
        int passengerCapacity = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Count == Capacity) throw new InvalidOperationException("World capacity exceeded.");
        if (owner < 1 || !Map.Contains(position) || !float.IsFinite(speed) || speed <= 0)
            throw new ArgumentException("Invalid unit definition or position.");
        EntityId id = new(_nextId);
        _nextId = checked(_nextId + 1);
        CombatState combat = new(
            maxHealth,
            maxHealth,
            ammo,
            0,
            false,
            default,
            default,
            AttackExecutionStatus.None,
            FireStance.FireAtWill,
            Vector2.Zero,
            default,
            -1,
            -1,
            initialTurretYaw,
            initialTurretYaw);
        Entity entity = _storage.Create(new Identity(id, owner, definition),
            new Position(position), new Motion(position, speed, false, initialSupportSurface), combat, default(UnitSmokeState));
        _entities[Count] = entity;
        _indices.Add(id, Count++);

        if (passengerCapacity > 0)
        {
            RegisterContainer(id, passengerCapacity);
        }

        return id;
    }

    public bool RemoveUnit(EntityId id)
    {
        if (!_indices.Remove(id, out int index)) return false;

        if (_boardingOrders.Remove(id, out EntityId targetTransport))
        {
            ReleaseSeatReservation(targetTransport);
        }
        _pendingDisembarks.Remove(id);
        if (_containedIn.Remove(id, out EntityId containerId))
        {
            if (_containers.TryGetValue(containerId, out ContainerData? c))
            {
                int pIdx = Array.IndexOf(c.Passengers, id, 0, c.PassengerCount);
                if (pIdx >= 0)
                {
                    for (int i = pIdx; i < c.PassengerCount - 1; i++)
                    {
                        c.Passengers[i] = c.Passengers[i + 1];
                    }
                    c.PassengerCount--;
                    c.Passengers[c.PassengerCount] = default;
                }
            }
        }
        if (_containers.Remove(id, out ContainerData? container))
        {
            _containerIds.Remove(id);
            for (int i = 0; i < container.PassengerCount; i++)
            {
                _containedIn.Remove(container.Passengers[i]);
            }
        }

        _storage.Destroy(_entities[index]);
        Count--;
        RemovalCount++;
        if (index != Count)
        {
            _entities[index] = _entities[Count];
            _indices[_storage.Get<Identity>(_entities[index]).Id] = index;
        }
        return true;
    }

    public void RegisterContainer(EntityId id, int capacity)
    {
        if (capacity > 0)
        {
            _containers[id] = new ContainerData { Capacity = capacity };
            if (!_containerIds.Contains(id)) _containerIds.Add(id);
        }
    }

    public bool IsContainer(EntityId id) => _containers.ContainsKey(id);
    public int ContainerCount => _containerIds.Count;
    public EntityId GetContainerIdAt(int index) => _containerIds[index];
    public int GetContainerCapacity(EntityId id) => _containers.TryGetValue(id, out ContainerData? c) ? c.Capacity : 0;
    public int GetPassengerCount(EntityId id) => _containers.TryGetValue(id, out ContainerData? c) ? c.PassengerCount : 0;
    public int GetReservedSeats(EntityId id) => _containers.TryGetValue(id, out ContainerData? c) ? c.ReservedSeats : 0;
    public int GetAvailableSeats(EntityId id) => _containers.TryGetValue(id, out ContainerData? c) ? Math.Max(0, c.Capacity - c.PassengerCount - c.ReservedSeats) : 0;

    public bool TryReserveSeat(EntityId transportId)
    {
        if (_containers.TryGetValue(transportId, out ContainerData? c))
        {
            if (c.PassengerCount + c.ReservedSeats < c.Capacity)
            {
                c.ReservedSeats++;
                return true;
            }
        }
        return false;
    }

    public void ReleaseSeatReservation(EntityId transportId)
    {
        if (_containers.TryGetValue(transportId, out ContainerData? c) && c.ReservedSeats > 0)
        {
            c.ReservedSeats--;
        }
    }

    public bool IsContained(EntityId unitId) => _containedIn.ContainsKey(unitId);
    public EntityId GetContainerId(EntityId unitId) => _containedIn.TryGetValue(unitId, out EntityId cId) ? cId : default;

    public ReadOnlySpan<EntityId> GetPassengers(EntityId transportId)
    {
        if (_containers.TryGetValue(transportId, out ContainerData? c))
            return c.Passengers.AsSpan(0, c.PassengerCount);
        return [];
    }

    public bool Board(EntityId transportId, EntityId passengerId)
    {
        if (!_containers.TryGetValue(transportId, out ContainerData? c)) return false;
        if (c.PassengerCount >= c.Capacity) return false;
        if (_containedIn.ContainsKey(passengerId)) return false;

        if (_boardingOrders.Remove(passengerId))
        {
            if (c.ReservedSeats > 0) c.ReservedSeats--;
        }

        c.Passengers[c.PassengerCount++] = passengerId;
        _containedIn[passengerId] = transportId;

        if (TryGetUnit(transportId, out UnitState transport))
        {
            SetPosition(passengerId, transport.Position);
            UpdateUnitNavigation(passengerId, transport.Position, transport.Position, false, supportSurface: transport.SupportSurface);
        }
        return true;
    }

    public bool Disembark(EntityId transportId, EntityId passengerId, Vector2 destination, uint supportSurface = 0)
    {
        if (!_containers.TryGetValue(transportId, out ContainerData? c)) return false;
        if (!_containedIn.TryGetValue(passengerId, out EntityId containerId) || containerId != transportId) return false;

        int idx = -1;
        for (int i = 0; i < c.PassengerCount; i++)
        {
            if (c.Passengers[i] == passengerId)
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) return false;

        for (int i = idx; i < c.PassengerCount - 1; i++)
        {
            c.Passengers[i] = c.Passengers[i + 1];
        }
        c.PassengerCount--;
        c.Passengers[c.PassengerCount] = default;
        _containedIn.Remove(passengerId);

        SetPosition(passengerId, destination);
        UpdateUnitNavigation(passengerId, destination, destination, false, supportSurface: supportSurface);
        return true;
    }

    public bool SetBoardingOrder(EntityId unitId, EntityId transportId)
    {
        _boardingOrders[unitId] = transportId;
        return true;
    }

    public bool TryGetBoardingTarget(EntityId unitId, out EntityId transportId) => _boardingOrders.TryGetValue(unitId, out transportId);

    public bool ClearBoardingOrder(EntityId unitId)
    {
        if (_boardingOrders.Remove(unitId, out EntityId transportId))
        {
            ReleaseSeatReservation(transportId);
            return true;
        }
        return false;
    }

    public int PendingDisembarkCount => _pendingDisembarks.Count;

    public void SetPendingDisembark(EntityId transportId, uint sequence, int player, Vector2 target, uint surface)
    {
        _pendingDisembarks[transportId] = new PendingDisembarkOrder(sequence, player, target, surface);
    }

    public bool TryGetPendingDisembark(EntityId transportId, out PendingDisembarkOrder order) =>
        _pendingDisembarks.TryGetValue(transportId, out order);

    public void UpdatePendingDisembark(EntityId transportId, PendingDisembarkOrder order) =>
        _pendingDisembarks[transportId] = order;

    public bool ClearPendingDisembark(EntityId transportId) =>
        _pendingDisembarks.Remove(transportId);

    public void UpdateContainedPositions()
    {
        for (int k = 0; k < _containerIds.Count; k++)
        {
            EntityId transportId = _containerIds[k];
            if (!_containers.TryGetValue(transportId, out ContainerData? container) || container.PassengerCount == 0)
                continue;
            if (!TryGetUnit(transportId, out UnitState transport))
                continue;

            for (int i = 0; i < container.PassengerCount; i++)
            {
                EntityId passengerId = container.Passengers[i];
                SetPosition(passengerId, transport.Position);
                UpdateUnitNavigation(passengerId, transport.Position, transport.Position, false, supportSurface: transport.SupportSurface);
            }
        }
    }

    public UnitState GetUnitAt(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        Entity entity = _entities[index];
        ref Identity identity = ref _storage.Get<Identity>(entity);
        ref Motion motion = ref _storage.Get<Motion>(entity);
        return new UnitState(identity.Id, identity.Owner, identity.Definition,
            _storage.Get<Position>(entity).Value, motion.Target, motion.Active, motion.SupportSurface);
    }

    public bool TryGetUnit(EntityId id, out UnitState unit)
    {
        if (_indices.TryGetValue(id, out int index)) { unit = GetUnitAt(index); return true; }
        unit = default;
        return false;
    }

    internal bool SetMoveTarget(int player, EntityId id, Vector2 target)
    {
        if (!Map.Contains(target) || !_indices.TryGetValue(id, out int index)) return false;
        Entity entity = _entities[index];
        if (_storage.Get<Identity>(entity).Owner != player) return false;
        ref Motion motion = ref _storage.Get<Motion>(entity);
        motion.Target = target;
        motion.Active = true;
        return true;
    }

    public bool SetPosition(EntityId id, Vector2 position)
    {
        if (!Map.Contains(position) || !_indices.TryGetValue(id, out int index)) return false;
        Entity entity = _entities[index];
        _storage.Get<Position>(entity).Value = position;
        return true;
    }

    internal bool UpdateUnitNavigation(EntityId id, Vector2 position, Vector2 target, bool active, int tick = -1, uint? supportSurface = null, float? hullHeading = null)
    {
        if (!_indices.TryGetValue(id, out int index)) return false;
        Entity entity = _entities[index];
        ref Position pos = ref _storage.Get<Position>(entity);
        ref Motion motion = ref _storage.Get<Motion>(entity);
        ref CombatState combat = ref _storage.Get<CombatState>(entity);

        if (hullHeading.HasValue)
        {
            TurnHullTo(ref combat, hullHeading.Value, tick);
            if (position != pos.Value && tick >= 0)
            {
                combat.LastMovedTick = tick;
                combat.ConsecutiveShotsOnTarget = 0;
            }
        }
        else
        {
            Vector2 step = position - pos.Value;
            if (step.LengthSquared() > 0.0001f)
            {
                TurnHullAlong(ref combat, step);
                if (tick >= 0)
                {
                    combat.LastMovedTick = tick;
                }
                combat.ConsecutiveShotsOnTarget = 0;
            }
        }

        pos.Value = position;
        motion.Target = target;
        motion.Active = active;
        if (supportSurface.HasValue)
        {
            motion.SupportSurface = supportSurface.Value;
        }
        return true;
    }

    public bool TryGetCombatState(EntityId id, out CombatState combat)
    {
        if (_indices.TryGetValue(id, out int index))
        {
            combat = _storage.Get<CombatState>(_entities[index]);
            return true;
        }
        combat = default;
        return false;
    }

    public bool SetCombatState(EntityId id, in CombatState combat)
    {
        if (!_indices.TryGetValue(id, out int index)) return false;
        _storage.Get<CombatState>(_entities[index]) = combat;
        return true;
    }

    public bool TryGetSmokeState(EntityId id, out UnitSmokeState smoke)
    {
        if (_indices.TryGetValue(id, out int index))
        {
            smoke = _storage.Get<UnitSmokeState>(_entities[index]);
            return true;
        }
        smoke = default;
        return false;
    }

    public bool SetSmokeState(EntityId id, in UnitSmokeState smoke)
    {
        if (!_indices.TryGetValue(id, out int index)) return false;
        _storage.Get<UnitSmokeState>(_entities[index]) = smoke;
        return true;
    }

    public bool ApplyDamage(EntityId id, int damage, out bool destroyed)
    {
        destroyed = false;
        if (!_indices.TryGetValue(id, out int index)) return false;
        ref CombatState combat = ref _storage.Get<CombatState>(_entities[index]);
        bool wasAlive = combat.CurrentHealth > 0;
        combat.CurrentHealth = Math.Max(0, combat.CurrentHealth - damage);
        destroyed = wasAlive && combat.CurrentHealth == 0;
        return true;
    }

    public bool ClearAttackOrder(EntityId id)
    {
        if (!_indices.TryGetValue(id, out int index)) return false;
        ref CombatState combat = ref _storage.Get<CombatState>(_entities[index]);
        // Only an ordered target is dropped. A target picked by auto-fire stays, so a unit driving off
        // keeps its gun on it; the combat system drops it as soon as it is no longer valid.
        if (combat.HasAttackOrder)
        {
            combat.TargetContactId = default;
            combat.TargetEntityId = default;
            combat.Status = AttackExecutionStatus.None;
        }
        combat.HasAttackOrder = false;
        combat.PursuitDestination = default;
        combat.LastPursuitRequestTick = -1;
        return true;
    }

    public bool SetFireStance(EntityId id, FireStance stance)
    {
        if (!_indices.TryGetValue(id, out int index)) return false;
        ref CombatState combat = ref _storage.Get<CombatState>(_entities[index]);
        combat.Stance = stance;
        return true;
    }

    /// <summary>
    /// Steers the hull to a specified world heading.
    /// With no target the turret rides on the hull, so a hull turn carries the gun along. With a target
    /// the gunner keeps the gun on it while the hull turns, so the gun's world bearing stays.
    /// </summary>
    internal static void TurnHullTo(ref CombatState combat, float newHullHeading, int tick = -1)
    {
        float delta = CombatSystem.ShorterArcDelta(combat.HullHeading, newHullHeading);
        if (MathF.Abs(delta) > 0.0001f)
        {
            if (combat.TargetContactId.Value == 0)
                combat.TurretYaw = CombatSystem.NormalizeYaw(combat.TurretYaw + delta);
            combat.HullHeading = CombatSystem.NormalizeYaw(newHullHeading);
            if (tick >= 0)
            {
                combat.LastMovedTick = tick;
            }
            combat.ConsecutiveShotsOnTarget = 0;
        }
    }

    /// <summary>
    /// The hull faces the way the unit just moved. Until hull turn rates land (TDD 9.5) it turns at once.
    /// With no target the turret rides on the hull, so a hull turn carries the gun along. With a target
    /// the gunner keeps the gun on it while the hull turns (a real hull turns over seconds and the
    /// gunner tracks through it), so the gun's world bearing stays.
    /// </summary>
    internal static void TurnHullAlong(ref CombatState combat, Vector2 step)
    {
        if (step.LengthSquared() < MinHullStep * MinHullStep) return;
        float hull = MathF.Atan2(-step.X, -step.Y);
        if (combat.TargetContactId.Value == 0)
            combat.TurretYaw = CombatSystem.NormalizeYaw(combat.TurretYaw + CombatSystem.ShorterArcDelta(combat.HullHeading, hull));
        combat.HullHeading = hull;
    }

    // Shorter moves (a final nudge onto the target point) do not say which way the hull faces.
    internal const float MinHullStep = 0.01f;

    public bool SetTurretYaw(EntityId id, float yaw)
    {
        if (!_indices.TryGetValue(id, out int index)) return false;
        ref CombatState combat = ref _storage.Get<CombatState>(_entities[index]);
        combat.TurretYaw = yaw;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _storage.Dispose();
    }
}
