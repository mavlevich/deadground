using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

internal sealed class WreckViewSet(
    Node3D parent,
    HeightMap map,
    Func<ushort, string?>? unitIdProvider = null,
    Func<string?>? factionProvider = null,
    StaticGeometry? geometry = null)
{
    internal Dictionary<WreckId, WreckView> Views { get; } = new();
    private readonly HashSet<WreckId> _present = new(WireProtocol.MaxWrecks);
    private readonly List<WreckId> _removed = new(WireProtocol.MaxWrecks);
    private float _cameraSize;

    public WreckViewSet(
        Node3D parent,
        HeightMap map,
        DeadGround.Rules.Ops.UnitCatalog catalog,
        Func<string?>? factionProvider = null,
        StaticGeometry? geometry = null)
        : this(parent, map, def => def < catalog.Units.Length ? catalog.Units[def].Id : null, factionProvider, geometry)
    {
    }

    internal void Sync(ClientReplica replica)
    {
        _present.Clear();
        foreach (WreckObservation wreck in replica.Wrecks)
        {
            _present.Add(wreck.Id);
            if (!Views.TryGetValue(wreck.Id, out WreckView? view))
            {
                string? unitId = unitIdProvider?.Invoke(wreck.Definition) ?? UnitViewSet.GetDefaultUnitId(wreck.Definition);
                view = new WreckView(unitId, factionProvider?.Invoke());
                Views.Add(wreck.Id, view);
                parent.AddChild(view.Root);
                if (_cameraSize > 0f) view.UpdateLod(_cameraSize);
            }

            float groundY = geometry is not null
                ? geometry.GetSurfaceHeight(wreck.Position, wreck.SupportSurface)
                : map.GetHeight(wreck.Position.X, wreck.Position.Y);

            view.Update(wreck, groundY);
        }

        _removed.Clear();
        foreach (WreckId id in Views.Keys)
        {
            if (!_present.Contains(id))
                _removed.Add(id);
        }

        foreach (WreckId id in _removed)
        {
            WreckView view = Views[id];
            parent.RemoveChild(view.Root);
            view.Root.QueueFree();
            Views.Remove(id);
        }
    }

    internal void UpdateLod(float cameraSize)
    {
        _cameraSize = cameraSize;
        foreach (WreckView view in Views.Values)
        {
            view.UpdateLod(cameraSize);
        }
    }

    internal void Clear()
    {
        foreach (WreckView view in Views.Values)
        {
            parent.RemoveChild(view.Root);
            view.Root.QueueFree();
        }
        Views.Clear();
        _present.Clear();
        _removed.Clear();
    }
}
