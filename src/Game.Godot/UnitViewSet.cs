using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

internal sealed class UnitViewSet(Node3D parent, HeightMap map)
{
    internal Dictionary<EntityId, UnitView> Views { get; } = new();
    internal HashSet<EntityId> Selected { get; } = new();
    private readonly HashSet<EntityId> _present = new(WireProtocol.MaxUnits);
    private readonly List<EntityId> _removed = new(WireProtocol.MaxUnits);

    internal void Sync(ClientReplica replica, double renderTick)
    {
        _present.Clear();
        foreach (OwnUnitObservation unit in replica.OwnUnits)
        {
            _present.Add(unit.Id);
            if (!Views.TryGetValue(unit.Id, out UnitView? view))
            {
                view = new UnitView(unit.Definition);
                Views.Add(unit.Id, view);
                parent.AddChild(view.Root);
            }
            System.Numerics.Vector2 position = replica.SamplePosition(unit.Id, renderTick);
            view.Root.Position = new Vector3(position.X, map.GetHeight(position.X, position.Y), position.Y);
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
}
