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
            _playerTeams[i] = (i < playerTeams.Length && playerTeams[i] > 0) ? playerTeams[i] : i + 1;
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

    public EntityId CreateUnit(int owner, ushort definition, Vector2 position, float speed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Count == Capacity) throw new InvalidOperationException("World capacity exceeded.");
        if (owner < 1 || !Map.Contains(position) || !float.IsFinite(speed) || speed <= 0)
            throw new ArgumentException("Invalid unit definition or position.");
        EntityId id = new(_nextId);
        _nextId = checked(_nextId + 1);
        Entity entity = _storage.Create(new Identity(id, owner, definition),
            new Position(position), new Motion(position, speed, false));
        _entities[Count] = entity;
        _indices.Add(id, Count++);
        return id;
    }

    public bool RemoveUnit(EntityId id)
    {
        if (!_indices.Remove(id, out int index)) return false;
        _storage.Destroy(_entities[index]);
        Count--;
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
            _storage.Get<Position>(entity).Value, motion.Target, motion.Active);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _storage.Dispose();
    }
}
