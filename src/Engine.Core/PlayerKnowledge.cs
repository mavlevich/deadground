namespace DeadGround.Engine;

public sealed class PlayerKnowledge
{
    public const int DefaultContactCapacity = 256;
    public const int DefaultEventCapacity = 64;
    public const int DefaultWreckCapacity = 128;
    public const int DefaultSmokeCapacity = WorldState.MaxSmokeClouds;
    private readonly OwnUnitObservation[] _ownUnits;
    private readonly ContactObservation[] _contacts;
    private readonly CombatEventObservation[] _events;
    private readonly WreckObservation[] _wrecks;
    private readonly SmokeCloudObservation[] _smokeClouds;

    public int Player { get; }
    public int Count { get; private set; }
    public int ContactCount { get; private set; }
    public int EventCount { get; private set; }
    public int WreckCount { get; private set; }
    public int SmokeCloudCount { get; private set; }
    public MissionObservation Mission { get; set; }

    public ReadOnlySpan<OwnUnitObservation> OwnUnits => _ownUnits.AsSpan(0, Count);
    public ReadOnlySpan<ContactObservation> Contacts => _contacts.AsSpan(0, ContactCount);
    public ReadOnlySpan<CombatEventObservation> Events => _events.AsSpan(0, EventCount);
    public ReadOnlySpan<WreckObservation> Wrecks => _wrecks.AsSpan(0, WreckCount);
    public ReadOnlySpan<SmokeCloudObservation> SmokeClouds => _smokeClouds.AsSpan(0, SmokeCloudCount);

    public PlayerKnowledge(
        int player,
        int capacity = 1024,
        int contactCapacity = DefaultContactCapacity,
        int eventCapacity = DefaultEventCapacity,
        int wreckCapacity = DefaultWreckCapacity,
        int smokeCapacity = DefaultSmokeCapacity)
    {
        if (player < 1) throw new ArgumentOutOfRangeException(nameof(player));
        Player = player;
        _ownUnits = new OwnUnitObservation[capacity];
        _contacts = new ContactObservation[contactCapacity];
        _events = new CombatEventObservation[eventCapacity];
        _wrecks = new WreckObservation[wreckCapacity];
        _smokeClouds = new SmokeCloudObservation[smokeCapacity];
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
        MissionObservation mission = default,
        ReadOnlySpan<WreckObservation> wrecks = default,
        ReadOnlySpan<SmokeCloudObservation> smokeClouds = default)
    {
        int count = 0;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            if (unit.Owner != Player) continue;
            if (count == _ownUnits.Length) throw new InvalidOperationException("Knowledge capacity exceeded.");

            EntityId containerId = world.GetContainerId(unit.Id);
            byte passengerCount = (byte)Math.Clamp(world.GetPassengerCount(unit.Id), 0, 255);
            byte passengerCapacity = (byte)Math.Clamp(world.GetContainerCapacity(unit.Id), 0, 255);

            PathRequestStatus navStatus = containerId.Value != 0
                ? PathRequestStatus.None
                : (navigation?.GetUnitStatus(unit.Id) ?? PathRequestStatus.None);
            uint navOrderSeq = containerId.Value != 0
                ? 0
                : (navigation?.GetUnitOrderSequence(unit.Id) ?? 0);

            byte smokeCharges = 0;
            ushort smokeCooldown = 0;
            bool hasSmoke = false;
            bool smokeActive = false;
            if (world.TryGetSmokeState(unit.Id, out WorldState.UnitSmokeState smokeState) && smokeState.HasCapability)
            {
                hasSmoke = true;
                smokeCharges = (byte)Math.Clamp(smokeState.ChargesRemaining, 0, byte.MaxValue);
                smokeCooldown = (ushort)Math.Clamp(smokeState.CooldownTicksRemaining, 0, ushort.MaxValue);
                smokeActive = smokeState.ActiveEmitTicksRemaining > 0;
            }

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
                    containerId.Value != 0 ? AttackExecutionStatus.None : combat.Status,
                    containerId.Value != 0 ? default : combat.TargetContactId,
                    combat.Stance,
                    combat.TurretYaw,
                    combat.HullHeading,
                    navOrderSeq,
                    containerId,
                    passengerCount,
                    passengerCapacity,
                    smokeCharges,
                    smokeCooldown,
                    hasSmoke,
                    smokeActive);
            }
            else
            {
                _ownUnits[count++] = new OwnUnitObservation(
                    unit.Id,
                    unit.Definition,
                    unit.Position,
                    unit.SupportSurface,
                    navStatus,
                    Stance: FireStance.FireAtWill,
                    TurretYaw: 0f,
                    NavOrderSequence: navOrderSeq,
                    ContainerId: containerId,
                    PassengerCount: passengerCount,
                    PassengerCapacity: passengerCapacity,
                    SmokeCharges: smokeCharges,
                    SmokeCooldownTicks: smokeCooldown,
                    HasSmokeCapability: hasSmoke,
                    SmokeActive: smokeActive);
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

        if (wrecks.Length > _wrecks.Length)
            throw new InvalidOperationException("Wreck knowledge capacity exceeded.");

        wrecks.CopyTo(_wrecks.AsSpan(0, wrecks.Length));
        WreckCount = wrecks.Length;

        if (smokeClouds.Length > _smokeClouds.Length)
            throw new InvalidOperationException("Smoke knowledge capacity exceeded.");

        smokeClouds.CopyTo(_smokeClouds.AsSpan(0, smokeClouds.Length));
        SmokeCloudCount = smokeClouds.Length;

        Mission = mission;
    }
}
