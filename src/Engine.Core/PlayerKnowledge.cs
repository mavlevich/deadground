namespace DeadGround.Engine;

public sealed class PlayerKnowledge
{
    public const int DefaultContactCapacity = 256;
    private readonly OwnUnitObservation[] _ownUnits;
    private readonly ContactObservation[] _contacts;

    public int Player { get; }
    public int Count { get; private set; }
    public int ContactCount { get; private set; }

    public ReadOnlySpan<OwnUnitObservation> OwnUnits => _ownUnits.AsSpan(0, Count);
    public ReadOnlySpan<ContactObservation> Contacts => _contacts.AsSpan(0, ContactCount);

    public PlayerKnowledge(int player, int capacity = 1024, int contactCapacity = DefaultContactCapacity)
    {
        if (player < 1) throw new ArgumentOutOfRangeException(nameof(player));
        Player = player;
        _ownUnits = new OwnUnitObservation[capacity];
        _contacts = new ContactObservation[contactCapacity];
    }

    internal void Refresh(WorldState world) => Refresh(world, [], null);

    internal void Refresh(WorldState world, ReadOnlySpan<ContactObservation> contacts) => Refresh(world, contacts, null);

    internal void Refresh(WorldState world, ReadOnlySpan<ContactObservation> contacts, NavigationSystem? navigation)
    {
        int count = 0;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            if (unit.Owner != Player) continue;
            if (count == _ownUnits.Length) throw new InvalidOperationException("Knowledge capacity exceeded.");
            PathRequestStatus navStatus = navigation?.GetUnitStatus(unit.Id) ?? PathRequestStatus.None;
            _ownUnits[count++] = new OwnUnitObservation(unit.Id, unit.Definition, unit.Position, 0, navStatus);
        }
        Count = count;

        if (contacts.Length > _contacts.Length)
            throw new InvalidOperationException("Contact knowledge capacity exceeded.");

        contacts.CopyTo(_contacts.AsSpan(0, contacts.Length));
        ContactCount = contacts.Length;
    }
}
