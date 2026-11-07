using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using Xunit;

namespace DeadGround.Tests;

public sealed class VehicleWreckTests
{
    private sealed class TestCombatCatalog : IUnitCombatCatalog
    {
        private readonly Dictionary<ushort, UnitCombatProfile> _profiles = new();

        public TestCombatCatalog Register(ushort definition, ushort maxHealth, WeaponDefinition? weapon, float turretTraverseRate = 0f)
        {
            _profiles[definition] = new UnitCombatProfile(maxHealth, weapon, TurretTraverseRate: turretTraverseRate);
            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitCombatProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private sealed class TestPerceptionCatalog : IUnitPerceptionCatalog
    {
        private readonly Dictionary<ushort, UnitPerceptionProfile> _profiles = new();

        public TestPerceptionCatalog Register(ushort definition, VisualSensorProfile? sensor, TargetObservationPoints? points)
        {
            _profiles[definition] = new UnitPerceptionProfile(sensor, points);
            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public TestMovementCatalog Register(ushort definition, float speed, float radius, float turnRate = 360f, TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle)
        {
            _profiles[definition] = new UnitMovementProfile(speed, radius, capabilities, turnRate);
            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private static HeightMap CreateFlatMap(int size = 256, float cellSize = 1.0f)
    {
        ushort[] samples = new ushort[size * size];
        return new HeightMap(size, size, cellSize, 0.01f, samples);
    }

    private static (Simulation simulation, WorldState world, StaticGeometry geometry) CreateSetup(
        StaticGeometry? geometry = null,
        ushort shooterDef = 1,
        ushort victimDef = 2,
        ushort damage = 100)
    {
        geometry ??= new StaticGeometry(CreateFlatMap(), []);

        WeaponDefinition gun = new("tank_gun", 100f, 10, damage, 10, MuzzleHeight: 1.9f, PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0));

        TestCombatCatalog combatCat = new();
        combatCat.Register(shooterDef, 100, gun);
        combatCat.Register(victimDef, 100, gun);

        TestPerceptionCatalog perceptCat = new();
        perceptCat.Register(shooterDef,
            sensor: new VisualSensorProfile(120f, eyeHeight: 1.8f),
            points: new TargetObservationPoints(0.8f, 1.8f));
        perceptCat.Register(victimDef,
            sensor: new VisualSensorProfile(120f, eyeHeight: 1.8f),
            points: new TargetObservationPoints(0.8f, 1.8f));

        TestMovementCatalog moveCat = new();
        moveCat.Register(shooterDef, 10f, 1.5f);
        moveCat.Register(victimDef, 10f, 1.5f);

        DetectionSystem detection = new(geometry, perceptCat, new DetectionTuning(evaluationIntervalTicks: 1));
        NavigationGrid navGrid = new(geometry, cellSize: 0.5f);
        NavigationSystem nav = new(navGrid, moveCat);
        CombatSystem combat = new(geometry, combatCat, perceptCat, detection, nav);

        RuleSet rules = new(detection, nav, combat);
        WorldState world = new(geometry.HeightMap);
        world.SetPlayerTeam(1, 1);
        world.SetPlayerTeam(2, 2);

        Simulation sim = new(world, rules);
        sim.SetPlayerAdmitted(1, true);
        sim.SetPlayerAdmitted(2, true);

        return (sim, world, geometry);
    }

    [Fact]
    public void PlainSightKill_CreatesSingleWreck_WithMatchingAnglesAndDefinition()
    {
        (Simulation sim, WorldState world, _) = CreateSetup();

        // Start shooter with 0 ammo so it detects on step 1 without firing immediately
        EntityId shooter = world.CreateUnit(1, 1, new Vector2(20f, 20f), 10f, 100, 0);
        EntityId victim = world.CreateUnit(2, 2, new Vector2(40f, 20f), 10f, 100, 0);

        Assert.True(world.TryGetCombatState(victim, out WorldState.CombatState victimCs));
        world.SetCombatState(victim, victimCs with { TurretYaw = 0.78f, HullHeading = 0.78f });

        PlayerKnowledge k1 = new(1);
        PlayerKnowledge k2 = new(2);

        // Step to establish detection
        sim.Step();
        sim.UpdateKnowledge(k1);
        sim.UpdateKnowledge(k2);

        Assert.Equal(0, world.WreckCount);
        Assert.Equal(0, k1.WreckCount);
        Assert.Equal(0, k2.WreckCount);

        // Grant ammo and order attack from shooter to victim
        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState shooterCs));
        world.SetCombatState(shooter, shooterCs with { Ammunition = 10 });

        ContactId victimContact = k1.Contacts[0].Id;
        sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, shooter, victimContact));

        // Step simulation until victim is destroyed
        for (int i = 0; i < 5; i++)
        {
            sim.Step();
        }

        // Victim should be destroyed, leaving exactly one authoritative wreck
        Assert.False(world.TryGetUnit(victim, out _));
        Assert.Equal(1, world.WreckCount);

        WreckState wreck = world.Wrecks[0];
        Assert.True(wreck.Id.Value > 0);
        Assert.Equal((ushort)2, wreck.Definition);
        Assert.Equal(new Vector2(40f, 20f), wreck.Position);
        Assert.Equal(0u, wreck.SupportSurface);
        Assert.Equal(0.78f, wreck.HullHeading, 0.01f);
        Assert.Equal(0.78f, wreck.TurretYaw, 0.01f);
        Assert.Equal(2, wreck.OriginalOwner);

        // Both owner and plain-sight opponent receive the wreck
        sim.UpdateKnowledge(k1);
        sim.UpdateKnowledge(k2);

        Assert.Equal(1, k1.WreckCount);
        Assert.Equal(1, k2.WreckCount);
        Assert.Equal(wreck.Id, k1.Wrecks[0].Id);
        Assert.Equal(wreck.Id, k2.Wrecks[0].Id);

        // Repeated update does not duplicate
        sim.UpdateKnowledge(k1);
        Assert.Equal(1, k1.WreckCount);
    }

    [Fact]
    public void FogOfWarKill_OpponentDoesNotLearnWreckUntilScouting_ThenRetainsInFog()
    {
        (Simulation sim, WorldState world, _) = CreateSetup();

        // Victim far away (150m, beyond 120m sensor range)
        EntityId shooter = world.CreateUnit(1, 1, new Vector2(20f, 20f), 10f, 100, 0);
        EntityId victim = world.CreateUnit(2, 2, new Vector2(170f, 20f), 10f, 100, 0);

        PlayerKnowledge k1 = new(1);
        PlayerKnowledge k2 = new(2);

        sim.Step();
        sim.UpdateKnowledge(k1);
        sim.UpdateKnowledge(k2);

        Assert.Equal(0, k1.ContactCount);

        // Inflict direct damage to destroy victim without player 1 observing
        world.ApplyDamage(victim, 100, out bool destroyed);
        Assert.True(destroyed);

        WreckState wreck = world.AddWreck(2, new Vector2(170f, 20f), 0, 0f, 0f, sim.Tick, 2);
        sim.Detection!.NotifyWreckCreated(wreck, 2, victim);
        world.RemoveUnit(victim);
        sim.Detection.NotifyEntityDestroyed(victim);

        sim.UpdateKnowledge(k1);
        sim.UpdateKnowledge(k2);

        // Victim owner knows about own wreck; opponent does NOT know yet
        Assert.Equal(1, k2.WreckCount);
        Assert.Equal(0, k1.WreckCount);

        // Opponent unit moves close to wreck position
        world.SetPosition(shooter, new Vector2(140f, 20f));

        // Run simulation step so detection scans unobserved wrecks
        sim.Step();
        sim.UpdateKnowledge(k1);

        // Opponent has now scouted and discovered the wreck
        Assert.Equal(1, k1.WreckCount);
        Assert.Equal(wreck.Id, k1.Wrecks[0].Id);

        // Opponent unit moves far away back into fog of war
        world.SetPosition(shooter, new Vector2(20f, 20f));

        for (int i = 0; i < 5; i++)
        {
            sim.Step();
        }

        sim.UpdateKnowledge(k1);

        // Discovered wreck remains known in fog of war
        Assert.Equal(1, k1.WreckCount);
        Assert.Equal(wreck.Id, k1.Wrecks[0].Id);
    }

    [Fact]
    public void EmptyPositionScouted_ClearsOldLastKnownMarker_AndDiscoversWreck()
    {
        (Simulation sim, WorldState world, _) = CreateSetup();

        // 1. Scout sees enemy tank
        EntityId scout = world.CreateUnit(1, 1, new Vector2(30f, 20f), 10f, 100, 0);
        EntityId victim = world.CreateUnit(2, 2, new Vector2(50f, 20f), 10f, 100, 0);

        PlayerKnowledge k1 = new(1);
        sim.Step();
        sim.UpdateKnowledge(k1);

        Assert.Equal(1, k1.ContactCount);
        Assert.Equal(ContactState.Current, k1.Contacts[0].State);

        // 2. Scout moves out of range, contact transitions to LastKnown
        world.SetPosition(scout, new Vector2(30f, 200f));
        sim.Step();
        sim.UpdateKnowledge(k1);

        Assert.Equal(1, k1.ContactCount);
        Assert.Equal(ContactState.LastKnown, k1.Contacts[0].State);
        Vector2 lkPos = k1.Contacts[0].Position;

        // 3. Enemy tank is destroyed while in fog
        world.ApplyDamage(victim, 100, out bool destroyed);
        Assert.True(destroyed);
        WreckState wreck = world.AddWreck(2, lkPos, 0, 0f, 0f, sim.Tick, 2);
        sim.Detection!.NotifyWreckCreated(wreck, 2, victim);
        world.RemoveUnit(victim);
        sim.Detection.NotifyEntityDestroyed(victim);

        // 4. Scout returns to observe the old location
        world.SetPosition(scout, new Vector2(30f, 20f));
        sim.Step();
        sim.UpdateKnowledge(k1);

        // The old LastKnown contact is confirmed empty and cleared; the wreck is now known
        Assert.Equal(0, k1.ContactCount);
        Assert.Equal(1, k1.WreckCount);
        Assert.Equal(wreck.Id, k1.Wrecks[0].Id);
    }

    [Fact]
    public void WireProtocol_SnapshotRoundTrip_PreservesWrecks()
    {
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        WreckObservation[] wrecks =
        [
            new(new WreckId(1), 2, new Vector2(100.5f, 200.25f), 0, 0.75f, -1.25f),
            new(new WreckId(5), 1, new Vector2(350f, 420f), 12, 3.14f, 0f)
        ];

        int size = WireProtocol.WriteSnapshot(
            buffer,
            sequence: 42,
            tick: 100,
            units: [],
            contacts: [],
            events: [],
            mission: default,
            wrecks: wrecks);

        Assert.True(size > WireProtocol.SnapshotHeaderSize);

        SnapshotFrame frame = new(WireProtocol.MaxUnits);
        bool read = WireProtocol.TryReadSnapshot(buffer.AsSpan(0, size), frame);

        Assert.True(read);
        Assert.Equal(42u, frame.Sequence);
        Assert.Equal(100, frame.Tick);
        Assert.Equal(2, frame.WreckCount);

        WreckObservation w0 = frame.Wrecks[0];
        Assert.Equal(1u, w0.Id.Value);
        Assert.Equal((ushort)2, w0.Definition);
        Assert.Equal(100.5f, w0.Position.X);
        Assert.Equal(200.25f, w0.Position.Y);
        Assert.Equal(0u, w0.SupportSurface);
        Assert.Equal(0.75f, w0.HullHeading, 0.001f);
        Assert.Equal(-1.25f, w0.TurretYaw, 0.001f);

        WreckObservation w1 = frame.Wrecks[1];
        Assert.Equal(5u, w1.Id.Value);
        Assert.Equal((ushort)1, w1.Definition);
        Assert.Equal(350f, w1.Position.X);
        Assert.Equal(420f, w1.Position.Y);
        Assert.Equal(12u, w1.SupportSurface);
    }

    [Fact]
    public void DroppedDestructionEvent_ReplicaReconstructsWrecksFromSnapshot()
    {
        ClientReplica replica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        WreckObservation[] wrecks =
        [
            new(new WreckId(10), 1, new Vector2(50f, 60f), 0, 0.5f, 0.5f)
        ];

        // Snapshot contains wreck but NO events (simulating lost UnitDestroyed packet)
        int size = WireProtocol.WriteSnapshot(
            buffer,
            sequence: 1,
            tick: 15,
            units: [],
            contacts: [],
            events: [],
            mission: default,
            wrecks: wrecks);

        bool applied = replica.TryApply(buffer.AsSpan(0, size));
        Assert.True(applied);

        Assert.Equal(1, replica.WreckCount);
        Assert.Equal(0, replica.EventCount);
        Assert.True(replica.TryGetWreck(new WreckId(10), out WreckObservation observedWreck));
        Assert.Equal(new Vector2(50f, 60f), observedWreck.Position);
    }

    [Fact]
    public void WreckStorageLimits_MaxWrecks_BoundedWithoutCrashOrOverflow()
    {
        HeightMap map = CreateFlatMap();
        using WorldState world = new(map, 32);

        // Add more than MaxWrecks (128)
        for (int i = 0; i < 150; i++)
        {
            world.AddWreck(1, new Vector2(i, i), 0, 0f, 0f, i, 1);
        }

        Assert.Equal(WorldState.MaxWrecks, world.WreckCount);
        Assert.Equal(WorldState.MaxWrecks, world.Wrecks.Length);

        // Side tracker also evicts oldest and clamps to DefaultCapacity
        SideWreckTracker tracker = new();
        for (uint i = 1; i <= 150; i++)
        {
            tracker.TryAdd(new WreckObservation(new WreckId(i), 1, new Vector2(i, i), 0, 0f, 0f));
        }

        Assert.Equal(SideWreckTracker.DefaultCapacity, tracker.Count);
        Assert.True(tracker.IsKnown(new WreckId(150)));
        Assert.False(tracker.IsKnown(new WreckId(1))); // Evicted

        // Wire protocol serialization of maximum capacity remains bounded
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        int size = WireProtocol.WriteSnapshot(
            buffer,
            sequence: 1,
            tick: 1,
            units: [],
            contacts: [],
            events: [],
            mission: default,
            wrecks: tracker.KnownWrecks);

        Assert.True(size <= WireProtocol.MaxMessageSize);
    }

    [Fact]
    public void BridgeKill_WreckSupportSurfaceAndElevationMatchDeck()
    {
        HeightMap map = CreateFlatMap();
        BridgeStructure bridge = new(
            new StructureId(1),
            new Vector2(50f, 20f),
            new Vector2(50f, 80f),
            width: 8f,
            startHeight: 15.5f,
            endHeight: 15.5f,
            thickness: 1.0f);

        StaticGeometry geometry = new(map, structures: [], bridges: [bridge]);
        (Simulation sim, WorldState world, _) = CreateSetup(geometry);

        // Victim on bridge deck (bridge ID 1)
        EntityId victim = world.CreateUnit(1, 2, new Vector2(50f, 50f), 10f, 100, 10, initialSupportSurface: 1);

        world.ApplyDamage(victim, 100, out bool destroyed);
        Assert.True(destroyed);

        WreckState wreck = world.AddWreck(2, new Vector2(50f, 50f), 1, 0f, 0f, sim.Tick, 2);
        sim.Detection!.NotifyWreckCreated(wreck, 2, victim);

        Assert.Equal(1u, wreck.SupportSurface);
        float surfaceHeight = geometry.GetSurfaceHeight(wreck.Position, wreck.SupportSurface);
        Assert.Equal(15.5f, surfaceHeight, 0.01f);
    }
}
