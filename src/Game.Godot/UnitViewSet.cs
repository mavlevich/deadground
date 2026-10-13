using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Godot;

namespace DeadGround.Client;

internal sealed class UnitViewSet(Node3D parent, HeightMap map, Func<ushort, int>? maxHealthProvider = null,
    Func<ushort, string?>? unitIdProvider = null)
{
    internal Dictionary<EntityId, UnitView> Views { get; } = new();
    internal HashSet<EntityId> Selected { get; } = new();
    private readonly HashSet<EntityId> _present = new(WireProtocol.MaxUnits);
    private readonly List<EntityId> _removed = new(WireProtocol.MaxUnits);
    private readonly Func<ushort, int>? _maxHealthProvider = maxHealthProvider;
    private readonly Func<ushort, string?>? _unitIdProvider = unitIdProvider;
    private static readonly Lazy<UnitCatalog> DefaultCatalog = new(() => PrototypeScenario.LoadCatalog());

    public UnitViewSet(Node3D parent, HeightMap map, UnitCatalog catalog)
        : this(parent, map, def => def < catalog.Units.Length ? catalog.Units[def].MaxHealth : 100,
            def => def < catalog.Units.Length ? catalog.Units[def].Id : null)
    {
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

    internal void Sync(ClientReplica replica, double renderTick)
    {
        _present.Clear();
        foreach (OwnUnitObservation unit in replica.OwnUnits)
        {
            _present.Add(unit.Id);
            if (!Views.TryGetValue(unit.Id, out UnitView? view))
            {
                string? unitId = _unitIdProvider is null ? GetDefaultUnitId(unit.Definition) : _unitIdProvider(unit.Definition);
                view = new UnitView(unitId);
                Views.Add(unit.Id, view);
                parent.AddChild(view.Root);
            }
            System.Numerics.Vector2 position = replica.SamplePosition(unit.Id, renderTick);
            view.UpdatePosition(new Vector3(position.X, map.GetHeight(position.X, position.Y), position.Y));
            if (replica.TryGetContact(unit.TargetContactId, out ContactObservation target) && target.IsCurrent)
            {
                System.Numerics.Vector2 targetPosition = replica.SampleContactPosition(target.Id, renderTick);
                view.AimAt(new Vector3(targetPosition.X, 0, targetPosition.Y));
            }
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
        }
    }

    internal void UpdateRecoil(double delta)
    {
        foreach (UnitView view in Views.Values)
            view.UpdateRecoil(delta);
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
        _present.Clear();
        _removed.Clear();
    }
}
