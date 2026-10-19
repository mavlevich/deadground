using DeadGround.Engine;
using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

internal sealed class ContactViewSet(Node3D parent, HeightMap map, Func<string?>? hostileFactionProvider = null, Func<ushort, string?>? unitIdProvider = null)
{
    internal Dictionary<ContactId, ContactView> Views { get; } = new();
    internal HashSet<ContactId> Selected { get; } = new();
    private readonly HashSet<ContactId> _present = new(WireProtocol.MaxContacts);
    private readonly List<ContactId> _removed = new(WireProtocol.MaxContacts);

    public ContactViewSet(Node3D parent, HeightMap map, DeadGround.Rules.Ops.UnitCatalog catalog, Func<string?>? hostileFactionProvider = null)
        : this(parent, map, hostileFactionProvider, def => def < catalog.Units.Length ? catalog.Units[def].Id : null)
    {
    }

    internal void Sync(ClientReplica replica, double renderTick = 0)
    {
        _present.Clear();
        foreach (ContactObservation contact in replica.Contacts)
        {
            _present.Add(contact.Id);
            if (!Views.TryGetValue(contact.Id, out ContactView? view))
            {
                view = new ContactView(hostileFactionProvider, unitIdProvider);
                Views.Add(contact.Id, view);
                parent.AddChild(view.Root);
            }
            System.Numerics.Vector2 position = replica.SampleContactPosition(contact.Id, renderTick);
            replica.SampleContactYaws(contact.Id, renderTick, out float turretYaw, out float hullHeading);
            float groundY = map.GetHeight(position.X, position.Y);
            view.Update(contact, position, groundY, Selected.Contains(contact.Id), turretYaw, hullHeading);
        }

        _removed.Clear();
        foreach (ContactId id in Views.Keys)
        {
            if (!_present.Contains(id))
                _removed.Add(id);
        }

        foreach (ContactId id in _removed)
        {
            ContactView view = Views[id];
            parent.RemoveChild(view.Root);
            view.Root.QueueFree();
            Views.Remove(id);
            Selected.Remove(id);
        }
    }

    internal void Clear()
    {
        foreach (ContactView view in Views.Values)
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
