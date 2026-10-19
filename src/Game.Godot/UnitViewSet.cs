using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

internal sealed class UnitViewSet(Node3D parent, HeightMap map, Func<ushort, int>? maxHealthProvider = null,
    Func<ushort, string?>? unitIdProvider = null, Func<string?>? factionProvider = null)
{
    internal const int ControlGroupCount = 9;

    internal Dictionary<EntityId, UnitView> Views { get; } = new();
    internal HashSet<EntityId> Selected { get; } = new();
    private readonly HashSet<EntityId>[] _controlGroups = CreateControlGroups();
    private readonly HashSet<EntityId> _present = new(WireProtocol.MaxUnits);
    private readonly List<EntityId> _removed = new(WireProtocol.MaxUnits);
    private readonly Func<ushort, int>? _maxHealthProvider = maxHealthProvider;
    private readonly Func<ushort, string?>? _unitIdProvider = unitIdProvider;
    // Own faction from the match setup; read when a view is created, after the side is assigned.
    private readonly Func<string?>? _factionProvider = factionProvider;
    private readonly UnitCatalog? _catalog;
    private float _cameraSize;
    private static readonly Lazy<UnitCatalog> DefaultCatalog = new(() => PrototypeScenario.LoadCatalog());

    public UnitViewSet(Node3D parent, HeightMap map, UnitCatalog catalog, Func<string?>? factionProvider = null)
        : this(parent, map, def => def < catalog.Units.Length ? catalog.Units[def].MaxHealth : 100,
            def => def < catalog.Units.Length ? catalog.Units[def].Id : null, factionProvider)
    {
        _catalog = catalog;
    }

    private static HashSet<EntityId>[] CreateControlGroups()
    {
        HashSet<EntityId>[] groups = new HashSet<EntityId>[ControlGroupCount];
        for (int i = 0; i < ControlGroupCount; i++)
            groups[i] = new HashSet<EntityId>();
        return groups;
    }

    internal HashSet<EntityId> GetControlGroup(int groupNumber)
    {
        if (groupNumber < 1 || groupNumber > ControlGroupCount)
            throw new ArgumentOutOfRangeException(nameof(groupNumber));
        return _controlGroups[groupNumber - 1];
    }

    internal int GetLowestControlGroup(EntityId id)
    {
        for (int i = 0; i < ControlGroupCount; i++)
        {
            if (_controlGroups[i].Contains(id))
                return i + 1;
        }
        return 0;
    }

    internal int GetSelectedControlGroup()
    {
        if (Selected.Count == 0) return 0;
        for (int i = 0; i < ControlGroupCount; i++)
        {
            HashSet<EntityId> group = _controlGroups[i];
            if (group.Count != Selected.Count) continue;
            bool allMatch = true;
            foreach (EntityId id in Selected)
            {
                if (!group.Contains(id))
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch) return i + 1;
        }
        return 0;
    }

    public static int GetDefaultMaxHealth(ushort definition)
    {
        UnitCatalog catalog = DefaultCatalog.Value;
        return definition < catalog.Units.Length ? catalog.Units[definition].MaxHealth : 100;
    }

    internal static string? GetDefaultUnitId(ushort definition)
    {
        UnitCatalog catalog = DefaultCatalog.Value;
        return definition < catalog.Units.Length ? catalog.Units[definition].Id : null;
    }

    internal static UnitCatalog GetDefaultCatalog() => DefaultCatalog.Value;

    internal static (float SensorRange, float WeaponRange) GetRanges(UnitCatalog catalog, ushort definition)
    {
        if (definition >= catalog.Units.Length) return (60f, 0f);
        UnitDefinition def = catalog.Units[definition];
        float sensorRange = def.SensorRange;
        float weaponRange = 0f;
        if (def.Weapon is not null && catalog.Weapons is not null)
        {
            foreach (WeaponData w in catalog.Weapons)
            {
                if (w.Id == def.Weapon)
                {
                    weaponRange = w.Range;
                    break;
                }
            }
        }
        return (sensorRange, weaponRange);
    }

    internal void Sync(ClientReplica replica, double renderTick)
    {
        _present.Clear();
        foreach (OwnUnitObservation unit in replica.OwnUnits)
        {
            _present.Add(unit.Id);
            if (!Views.TryGetValue(unit.Id, out UnitView? view))
            {
                string? unitId = _unitIdProvider is null ? GetDefaultUnitId(unit.Definition) : _unitIdProvider(unit.Definition);
                view = new UnitView(unitId, _factionProvider?.Invoke());
                var (sensorRange, weaponRange) = GetRanges(_catalog ?? DefaultCatalog.Value, unit.Definition);
                view.ConfigureRanges(sensorRange, weaponRange, map);
                view.UpdateLod(_cameraSize);
                Views.Add(unit.Id, view);
                parent.AddChild(view.Root);
            }
            System.Numerics.Vector2 position = replica.SamplePosition(unit.Id, renderTick);
            view.UpdatePosition(new Vector3(position.X, map.GetHeight(position.X, position.Y), position.Y));
            replica.SampleYaws(unit.Id, renderTick, out float turretYaw, out float hullHeading);
            view.SetHullHeading(hullHeading);
            view.SetTurretYaw(turretYaw);
            int maxHealth = _maxHealthProvider?.Invoke(unit.Definition) ?? GetDefaultMaxHealth(unit.Definition);
            view.UpdateHealth(unit.Health, maxHealth);
            view.SetSelected(Selected.Contains(unit.Id));
        }
        _removed.Clear();
        foreach (EntityId id in Views.Keys)
            if (!_present.Contains(id)) _removed.Add(id);
        foreach (EntityId id in _removed)
        {
            UnitView view = Views[id];
            parent.RemoveChild(view.Root);
            view.Root.QueueFree();
            Views.Remove(id);
            Selected.Remove(id);
            for (int i = 0; i < ControlGroupCount; i++)
                _controlGroups[i].Remove(id);
        }
    }

    internal void UpdateLod(float cameraSize)
    {
        _cameraSize = cameraSize;
        foreach (UnitView view in Views.Values)
            view.UpdateLod(cameraSize);
    }

    internal void UpdateSelectionVisuals()
    {
        foreach ((EntityId id, UnitView view) in Views)
            view.SetSelected(Selected.Contains(id));
    }

    internal void UpdateEffects(double delta)
    {
        foreach (UnitView view in Views.Values)
            view.UpdateEffects(delta);
    }

    internal void UpdateRecoil(double delta)
    {
        foreach (UnitView view in Views.Values)
            view.UpdateRecoil(delta);
    }

    internal void ResetRecoil()
    {
        foreach (UnitView view in Views.Values)
            view.ResetRecoil();
    }

    internal void Clear()
    {
        foreach (UnitView view in Views.Values)
        {
            parent.RemoveChild(view.Root);
            view.Root.QueueFree();
        }
        Views.Clear();
        Selected.Clear();
        for (int i = 0; i < ControlGroupCount; i++)
            _controlGroups[i].Clear();
        _present.Clear();
        _removed.Clear();
    }
}
