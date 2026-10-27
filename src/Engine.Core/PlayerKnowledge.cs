namespace DeadGround.Engine;

public sealed class PlayerKnowledge
{
    public const int DefaultContactCapacity = 256;
    public const int DefaultEventCapacity = 64;
    private readonly OwnUnitObservation[] _ownUnits;
    private readonly ContactObservation[] _contacts;
    private readonly CombatEventObservation[] _events;

    public int Player { get; }
    public int Count { get; private set; }
    public int ContactCount { get; private set; }
    public int EventCount { get; private set; }
    public MissionObservation Mission { get; set; }

    public ReadOnlySpan<OwnUnitObservation> OwnUnits => _ownUnits.AsSpan(0, Count);
    public ReadOnlySpan<ContactObservation> Contacts => _contacts.AsSpan(0, ContactCount);
    public ReadOnlySpan<CombatEventObservation> Events => _events.AsSpan(0, EventCount);

    public PlayerKnowledge(
        int player,
        int capacity = 1024,
        int contactCapacity = DefaultContactCapacity,
        int eventCapacity = DefaultEventCapacity)
    {
        if (player < 1) throw new ArgumentOutOfRangeException(nameof(player));
        Player = player;
        _ownUnits = new OwnUnitObservation[capacity];
        _contacts = new ContactObservation[contactCapacity];
        _events = new CombatEventObservation[eventCapacity];
    }

    internal void Refresh(WorldState world) => Refresh(world, [], null, []);

    internal void Refresh(WorldState world, ReadOnlySpan<ContactObservation> contacts) => Refresh(world, contacts, null, []);

    internal void Refresh(WorldState world, ReadOnlySpan<ContactObservation> contacts, NavigationSystem? navigation) =>
        Refresh(world, contacts, navigation, []);

    internal void Refresh(
        WorldState world,
        ReadOnlySpan<ContactObservation> contacts,
        NavigationSystem? navigation,
        ReadOnlySpan<CombatEventObservation> events,
        MissionObservation mission = default)
    {
        int count = 0;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            if (unit.Owner != Player) continue;
            if (count == _ownUnits.Length) throw new InvalidOperationException("Knowledge capacity exceeded.");
            PathRequestStatus navStatus = navigation?.GetUnitStatus(unit.Id) ?? PathRequestStatus.None;
            uint navOrderSeq = navigation?.GetUnitOrderSequence(unit.Id) ?? 0;
            if (world.TryGetCombatState(unit.Id, out WorldState.CombatState combat))
            {
                _ownUnits[count++] = new OwnUnitObservation(
                    unit.Id,
                    unit.Definition,
                    unit.Position,
                    unit.SupportSurface,
                    navStatus,
                    (ushort)Math.Clamp(combat.CurrentHealth, 0, ushort.MaxValue),
                    (ushort)Math.Clamp(combat.Ammunition, 0, ushort.MaxValue),
                    (ushort)Math.Clamp(combat.ReloadTicksRemaining, 0, ushort.MaxValue),
                    combat.Status,
                    combat.TargetContactId,
                    combat.Stance,
                    combat.TurretYaw,
                    combat.HullHeading,
                    navOrderSeq);
            }
            else
            {
                _ownUnits[count++] = new OwnUnitObservation(unit.Id, unit.Definition, unit.Position, unit.SupportSurface, navStatus, Stance: FireStance.FireAtWill, TurretYaw: 0f, NavOrderSequence: navOrderSeq);
            }
        }
        Count = count;

        if (contacts.Length > _contacts.Length)
            throw new InvalidOperationException("Contact knowledge capacity exceeded.");

        contacts.CopyTo(_contacts.AsSpan(0, contacts.Length));
        ContactCount = contacts.Length;

        if (events.Length > _events.Length)
            throw new InvalidOperationException("Event knowledge capacity exceeded.");

        events.CopyTo(_events.AsSpan(0, events.Length));
        EventCount = events.Length;

        Mission = mission;
    }
}
