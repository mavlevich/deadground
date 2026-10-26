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

    private record struct Identity(EntityId Id, int Owner, ushort Definition);

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

    public EntityId CreateUnit(int owner, ushort definition, Vector2 position, float speed, int maxHealth = 100, int ammo = 0, float initialTurretYaw = 0f)
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
            new Position(position), new Motion(position, speed, false), combat);
        _entities[Count] = entity;
        _indices.Add(id, Count++);
        return id;
    }

    public bool RemoveUnit(EntityId id)
    {
        if (!_indices.Remove(id, out int index)) return false;
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

    internal bool UpdateUnitNavigation(EntityId id, Vector2 position, Vector2 target, bool active, int tick = -1, uint? supportSurface = null)
    {
        if (!_indices.TryGetValue(id, out int index)) return false;
        Entity entity = _entities[index];
        ref Position pos = ref _storage.Get<Position>(entity);
        ref Motion motion = ref _storage.Get<Motion>(entity);
        ref CombatState combat = ref _storage.Get<CombatState>(entity);
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
