using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class CombatNetworkTests
{
    private static void Pump(SimulationHost host, ClientSession c1, ClientSession c2, int iterations = 1, int delayMs = 5)
    {
        for (int i = 0; i < iterations; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            c1.Advance(1d / Simulation.TickRate);
            c2.Advance(1d / Simulation.TickRate);
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
    }

    private static void WaitFor(Func<bool> condition, SimulationHost host, ClientSession c1, ClientSession c2, int maxAttempts = 150)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
        {
            Pump(host, c1, c2, iterations: 1, delayMs: 5);
        }
        Assert.True(condition());
    }

    [Fact]
    public void TwoClientsConnectOverLoopback_CombatScenario_DetectAttackDamageAndDestroy()
    {
        StaticGeometry geometry = CombatScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        MatchSetup setup = CombatScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        // This test follows one explicit attack order. Auto-fire is the default, so without HoldFire
        // every tank in range fires on its own and the shot and damage checks below race against it.
        Simulation simulation = CombatScenario.CreateSimulation(geometry, catalog);
        for (int i = 0; i < simulation.World.Count; i++)
            simulation.World.SetFireStance(simulation.World.GetUnitAt(i).Id, FireStance.HoldFire);
        using SimulationHost host = new(simulation, serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        // Wait for both to connect and receive initial snapshots
        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 4 && client2.Replica.OwnUnits.Length == 4, host, client1, client2);

        Assert.Equal(1, client1.AssignedPlayerId);
        Assert.Equal(2, client2.AssignedPlayerId);

        // Advance ticks so recon/tanks detect opposing side over the low wall
        WaitFor(() => client1.Replica.ContactCount > 0, host, client1, client2);

        // Client 1 finds contact for the enemy tank behind low wall
        ContactObservation targetContact = default;
        foreach (ContactObservation c in client1.Replica.Contacts)
        {
            if (c.State == ContactState.Current)
            {
                targetContact = c;
                break;
            }
        }
        Assert.NotEqual(0u, targetContact.Id.Value);

        // Client 1 issues attack with its second tank (Unit 2, tank at (12, 25))
        EntityId client1Tank = client1.Replica.OwnUnits[1].Id;
        Assert.True(client1.Attack(client1Tank, targetContact.Id));

        // Advance simulation: tank fires shot 1
        WaitFor(() =>
        {
            if (client1.Replica.TryGetOwnUnit(client1Tank, out OwnUnitObservation own) && own.Ammunition < 10)
                return true;
            return false;
        }, host, client1, client2);

        // Verify Client 1 received ShotFired event
        bool sawShotFired = false;
        foreach (CombatEventObservation ev in client1.Replica.Events)
        {
            if (ev.Kind == CombatEventKind.ShotFired) sawShotFired = true;
        }
        Assert.True(sawShotFired);

        // Client 2's target tank was damaged from 100 to 50
        WaitFor(() =>
        {
            foreach (OwnUnitObservation u in client2.Replica.OwnUnits)
            {
                if (u.Health <= 50) return true;
            }
            return false;
        }, host, client1, client2);

        // Wait for reload (40 ticks = 2.0s) and second shot to destroy target
        WaitFor(() => client2.Replica.OwnUnits.Length == 3, host, client1, client2, maxAttempts: 200);

        // Victim was cleanly removed and client 2 count decreased to 3
        Assert.Equal(3, client2.Replica.OwnUnits.Length);
    }

    [Fact]
    public void ForeignUnitAttack_IsRejected()
    {
        StaticGeometry geometry = CombatScenario.CreateStaticGeometry();
        MatchSetup setup = CombatScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(CombatScenario.CreateSimulation(geometry), serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 4 && client2.Replica.OwnUnits.Length == 4, host, client1, client2);

        // Client 1 tries to issue an attack command using Client 2's unit ID
        EntityId foreignUnit = client2.Replica.OwnUnits[0].Id;
        ContactId dummyContact = new(1);

        Assert.True(client1.Attack(foreignUnit, dummyContact)); // client transport queues byte packet
        Pump(host, client1, client2, iterations: 5);

        // Server rejects foreign unit command: last command result has Queued = false
        Assert.NotNull(client1.LastCommandResult);
        Assert.False(client1.LastCommandResult!.Value.Queued);
    }

    [Fact]
    public void UnknownOrOpponentContactId_IsRejected()
    {
        StaticGeometry geometry = CombatScenario.CreateStaticGeometry();
        MatchSetup setup = CombatScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(CombatScenario.CreateSimulation(geometry), serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 4, host, client1, client2);

        EntityId ownUnit = client1.Replica.OwnUnits[0].Id;

        // Non-existent contact ID
        client1.Attack(ownUnit, new ContactId(9999));
        Pump(host, client1, client2, iterations: 5);

        Assert.NotNull(client1.LastCommandResult);
        Assert.False(client1.LastCommandResult!.Value.Queued);
    }

    [Fact]
    public void DuplicateAttackCommand_IsRejected()
    {
        StaticGeometry geometry = CombatScenario.CreateStaticGeometry();
        MatchSetup setup = CombatScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using Simulation sim = CombatScenario.CreateSimulation(geometry);
        using SimulationHost host = new(sim, serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.ContactCount > 0, host, client1, client2);

        EntityId ownUnit = client1.Replica.OwnUnits[1].Id;
        ContactId target = client1.Replica.Contacts[0].Id;

        // Send two attack commands directly through simulation with same sequence number
        AttackCommand cmd1 = new(10, sim.Tick, ownUnit, target);
        AttackCommand cmd2 = new(10, sim.Tick, ownUnit, target); // Duplicate sequence

        Assert.True(sim.TryEnqueue(1, cmd1));
        Assert.False(sim.TryEnqueue(1, cmd2)); // Rejected as duplicate
    }

    [Fact]
    public void SequencingAcrossMoveAndAttackCommands_PreservesExactOrder()
    {
        StaticGeometry geometry = CombatScenario.CreateStaticGeometry();
        MatchSetup setup = CombatScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using Simulation sim = CombatScenario.CreateSimulation(geometry);
        using SimulationHost host = new(sim, serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.ContactCount > 0, host, client1, client2);

        EntityId ownUnit = client1.Replica.OwnUnits[1].Id;
        ContactId target = client1.Replica.Contacts[0].Id;

        // Move command seq=5
        MoveCommand move = new(5, sim.Tick, ownUnit, new Vector2(12f, 26f));
        Assert.True(sim.TryEnqueue(1, move));

        // Attack command with seq=5 (duplicate/stale) -> rejected
        AttackCommand staleAttack = new(5, sim.Tick, ownUnit, target);
        Assert.False(sim.TryEnqueue(1, staleAttack));

        // Attack command with seq=4 (older) -> rejected
        AttackCommand olderAttack = new(4, sim.Tick, ownUnit, target);
        Assert.False(sim.TryEnqueue(1, olderAttack));

        // Attack command with seq=6 (newer) -> accepted
        AttackCommand newerAttack = new(6, sim.Tick, ownUnit, target);
        Assert.True(sim.TryEnqueue(1, newerAttack));
    }

    [Fact]
    public void ConnectionSlotReplacement_SafelyResetsAndPreservesPendingWork()
    {
        StaticGeometry geometry = CombatScenario.CreateStaticGeometry();
        MatchSetup setup = CombatScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(CombatScenario.CreateSimulation(geometry), serverTransport, setup);

        ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 4, host, client1, client2);

        // Client 1 disconnects
        client1.Dispose();
        for (int i = 0; i < 20 && host.ConnectedPlayerCount > 1; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client2.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        // Reconnect new client in slot 1
        using ClientSession client1Reconnected = ClientSession.Connect("127.0.0.1", port, setup);
        WaitFor(() => client1Reconnected.IsConnected && client1Reconnected.Replica.OwnUnits.Length == 4,
            host, client1Reconnected, client2);

        Assert.Equal(1, client1Reconnected.AssignedPlayerId);

        // Sequence stream starts cleanly at 1 for reconnected client
        EntityId tank = client1Reconnected.Replica.OwnUnits[0].Id;
        Assert.True(client1Reconnected.Move(tank, new Vector2(16f, 26f)));
        Pump(host, client1Reconnected, client2, iterations: 5);

        Assert.NotNull(client1Reconnected.LastCommandResult);
        Assert.True(client1Reconnected.LastCommandResult!.Value.Queued);
    }

    [Fact]
    public void RecipientFiltering_HiddenShooterOmitsShotEvent_UnobservedDeathOmitsDestroyedEvent()
    {
        StaticGeometry geometry = CombatScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        // 3 player setup: Player 1 and 2 engage; Player 3 is far away and observes neither
        WorldState world = new(geometry.HeightMap, playerTeams: [1, 2, 3]);
        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");

        EntityId p1Tank = world.CreateUnit(1, tankIdx, new Vector2(12f, 25f), tankDef.Speed, 100, 10, initialTurretYaw: -MathF.PI / 2f);
        EntityId p2Tank = world.CreateUnit(2, tankIdx, new Vector2(33f, 25f), tankDef.Speed, 50, 10); // 50 HP (destroyed in 1 hit)
        EntityId p3Tank = world.CreateUnit(3, tankIdx, new Vector2(100f, 100f), tankDef.Speed, 100, 10); // Isolated far away
        world.SetFireStance(p1Tank, FireStance.HoldFire);
        world.SetFireStance(p2Tank, FireStance.HoldFire);

        OpsRules rules = new(geometry, catalog);
        using Simulation simulation = new(world, rules);

        for (int i = 0; i < 4; i++) simulation.Step();

        // Player 1 observes Player 2
        Assert.True(simulation.Detection!.TryGetContactByEntity(1, p2Tank, out ContactObservation p2Contact));
        // Player 3 observes NEITHER Player 1 nor Player 2
        Assert.False(simulation.Detection!.TryGetContactByEntity(3, p1Tank, out _));
        Assert.False(simulation.Detection!.TryGetContactByEntity(3, p2Tank, out _));

        // Player 1 fires and destroys Player 2 tank
        Assert.True(simulation.TryEnqueue(1, new AttackCommand(1, simulation.Tick, p1Tank, p2Contact.Id)));
        simulation.Step();

        // Check combat event replication to Player 3
        CombatEventObservation[] buffer = new CombatEventObservation[64];
        ReadOnlySpan<CombatEventObservation> p3Events = simulation.Combat!.DrainEvents(3, buffer);

        // Player 3 must NOT receive ShotFired or UnitDestroyed because it observed neither unit
        Assert.Equal(0, p3Events.Length);

        // Player 1, however, receives both ShotFired and UnitDestroyed
        ReadOnlySpan<CombatEventObservation> p1Events = simulation.Combat!.DrainEvents(1, buffer);
        Assert.True(p1Events.Length >= 2);
    }

    [Fact]
    public void EventLossAndDuplication_AuthoritativeStateRemainsConsistent()
    {
        ClientReplica replica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];

        OwnUnitObservation own = new(
            new EntityId(1), 2, new Vector2(10f, 10f), 0,
            Health: 50, Ammunition: 9, ReloadTicksRemaining: 38,
            AttackStatus: AttackExecutionStatus.Aiming, TargetContactId: new ContactId(101));

        CombatEventObservation ev = new(
            EventId: 10, ServerTick: 2, Kind: CombatEventKind.ShotFired, PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            Origin: new Vector3(10f, 0f, 10f), Target: new Vector3(30f, 0f, 10f));

        // 1. Initial snapshot with event
        int size1 = WireProtocol.WriteSnapshot(bytes, 1, 2, [own], [], [ev]);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size1)));
        Assert.Equal(1, replica.EventCount);
        Assert.Equal(10u, replica.Events[0].EventId);

        // 2. Duplicate snapshot packet rejected by replica sequence check
        Assert.False(replica.TryApply(bytes.AsSpan(0, size1)));

        // 3. Packet loss: next packet seq=3 received, authoritatively correct
        OwnUnitObservation ownUpdated = own with { ReloadTicksRemaining = 37 };
        int size3 = WireProtocol.WriteSnapshot(bytes, 3, 3, [ownUpdated], [], []);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size3)));
        Assert.Equal(37, replica.OwnUnits[0].ReloadTicksRemaining);
        // Events accumulated in ring buffer are preserved across subsequent snapshots
        Assert.Equal(1, replica.EventCount);
        Assert.Equal(10u, replica.Events[0].EventId);
        Assert.Equal(0, replica.LatestSnapshotEventCount);
    }

    [Fact]
    public void CombatEvents_AccumulatedAcrossMultipleSnapshots_WithDeduplicationAndRingBuffer()
    {
        ClientReplica replica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];

        CombatEventObservation ev1 = new(
            EventId: 101, ServerTick: 1, Kind: CombatEventKind.ShotFired, PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            Origin: new Vector3(10f, 0f, 10f), Target: new Vector3(30f, 0f, 10f));
        CombatEventObservation ev2 = new(
            EventId: 102, ServerTick: 1, Kind: CombatEventKind.TookFire, PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            Origin: new Vector3(10f, 0f, 10f), Target: new Vector3(30f, 0f, 10f));
        CombatEventObservation ev3 = new(
            EventId: 103, ServerTick: 2, Kind: CombatEventKind.UnitDestroyed, PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            Origin: default, Target: new Vector3(30f, 0f, 10f));
        CombatEventObservation ev4 = new(
            EventId: 104, ServerTick: 3, Kind: CombatEventKind.ShotFired, PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            Origin: new Vector3(12f, 0f, 10f), Target: new Vector3(32f, 0f, 10f));

        // Snapshot 1: ev1 and ev2
        int size1 = WireProtocol.WriteSnapshot(bytes, 1, 1, [], [], [ev1, ev2]);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size1)));
        Assert.Equal(2, replica.EventCount);
        Assert.Equal(101u, replica.Events[0].EventId);
        Assert.Equal(102u, replica.Events[1].EventId);

        // Snapshot 2: ev3 only (e.g. frame stall or network burst delivers second snapshot before client frame processes events)
        int size2 = WireProtocol.WriteSnapshot(bytes, 2, 2, [], [], [ev3]);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size2)));
        Assert.Equal(3, replica.EventCount);
        Assert.Equal(101u, replica.Events[0].EventId);
        Assert.Equal(102u, replica.Events[1].EventId);
        Assert.Equal(103u, replica.Events[2].EventId);

        // Snapshot 3: duplicate ev2 and new ev4
        int size3 = WireProtocol.WriteSnapshot(bytes, 3, 3, [], [], [ev2, ev4]);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size3)));
        // ev2 is deduplicated, only ev4 added -> total 4 events
        Assert.Equal(4, replica.EventCount);
        Assert.Equal(101u, replica.Events[0].EventId);
        Assert.Equal(102u, replica.Events[1].EventId);
        Assert.Equal(103u, replica.Events[2].EventId);
        Assert.Equal(104u, replica.Events[3].EventId);

        // Snapshot 4: empty events does not discard accumulated events
        int size4 = WireProtocol.WriteSnapshot(bytes, 4, 4, [], [], []);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size4)));
        Assert.Equal(4, replica.EventCount);
        Assert.Equal(0, replica.LatestSnapshotEventCount);
    }

    [Fact]
    public void MalformedPacketsAndFullEventBuffers_HandledSafely()
    {
        ClientReplica replica = new();

        // Truncated packet
        byte[] junk = [0x01, 0x02, 0x03];
        Assert.False(replica.TryApply(junk));

        // Zero-length packet
        Assert.False(replica.TryApply([]));

        // Random garbage packet
        byte[] garbage = new byte[128];
        new Random(42).NextBytes(garbage);
        Assert.False(replica.TryApply(garbage));

        // Overfull event buffer in WireProtocol (exceeding max snapshot events)
        CombatEventObservation[] manyEvents = new CombatEventObservation[100];
        for (int i = 0; i < manyEvents.Length; i++)
        {
            manyEvents[i] = new CombatEventObservation(
                (uint)(i + 1), 1, CombatEventKind.ShotFired, 1, new Vector3(0, 0, 0), new Vector3(10, 0, 10));
        }

        byte[] msg = new byte[WireProtocol.MaxMessageSize];
        // Must clamp or serialize safely up to buffer limit without crashing
        int size = WireProtocol.WriteSnapshot(msg, 1, 1, [], [], manyEvents.AsSpan(0, 64));
        Assert.True(size > 0);
        Assert.True(replica.TryApply(msg.AsSpan(0, size)));
        Assert.Equal(64, replica.EventCount);
    }

    [Fact]
    public void CatalogValidation_RejectsValuesBeyondProtocolFieldCapacity()
    {
        // 1. MaxHealth exceeding ushort.MaxValue (65536) must throw
        string overflowHealthJson = """
        {
          "schema_version": 1,
          "weapons": [],
          "units": [
            {
              "id": "tank_overflow",
              "speed": 5.0,
              "sensor_range": 50.0,
              "eye_height": 2.0,
              "target_points": [1.0],
              "radius": 1.4,
              "max_health": 65536
            }
          ]
        }
        """;
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(overflowHealthJson));

        // 2. AmmoCapacity exceeding ushort.MaxValue (65536) must throw
        string overflowAmmoJson = """
        {
          "schema_version": 1,
          "weapons": [
            {
              "id": "gun_overflow",
              "range": 50.0,
              "reload_duration_ticks": 10,
              "damage": 50,
              "ammo_capacity": 65536,
              "muzzle_height": 1.8,
              "presentation_id": "cannon"
            }
          ],
          "units": [
            {
              "id": "tank_overflow_ammo",
              "speed": 5.0,
              "sensor_range": 50.0,
              "eye_height": 2.0,
              "target_points": [1.0],
              "radius": 1.4,
              "max_health": 100,
              "weapon": "gun_overflow"
            }
          ]
        }
        """;
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(overflowAmmoJson));

        // 3. Non-finite reload seconds must throw
        string nonFiniteReloadJson = """
        {
          "schema_version": 1,
          "weapons": [
            {
              "id": "gun_nan_reload",
              "range": 50.0,
              "reload_duration_ticks": 0,
              "reload_seconds": 1e40,
              "damage": 50,
              "ammo_capacity": 10,
              "muzzle_height": 1.8,
              "presentation_id": "cannon"
            }
          ],
          "units": []
        }
        """;
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(nonFiniteReloadJson));

        // 4. Boundary valid values (65535 HP, 255 ammo, 65535 reload ticks) must pass
        string boundaryValidJson = """
        {
          "schema_version": 1,
          "weapons": [
            {
              "id": "gun_boundary",
              "range": 50.0,
              "reload_duration_ticks": 65535,
              "damage": 50,
              "ammo_capacity": 255,
              "muzzle_height": 1.8,
              "presentation_id": "cannon",
              "presentation_kind": "cannon",
              "penetration_near": 100.0,
              "penetration_far": 50.0
            }
          ],
          "units": [
            {
              "id": "tank_boundary",
              "speed": 5.0,
              "sensor_range": 50.0,
              "eye_height": 2.0,
              "target_points": [1.0],
              "radius": 1.4,
              "max_health": 65535,
              "weapon": "gun_boundary"
            }
          ]
        }
        """;
        UnitCatalog validCatalog = PrototypeScenario.LoadCatalog(boundaryValidJson);
        Assert.Equal(65535, validCatalog.Units[0].MaxHealth);
    }

    [Fact]
    public void WireProtocol_Version16_PreservesHitResultAndEventProperties()
    {
        Assert.Equal(MatchSetup.CurrentProtocolVersion, WireProtocol.Version);
        Assert.Equal(32, WireProtocol.MissionSize);
        Assert.Equal(48, WireProtocol.SnapshotHeaderSize);
        Assert.Equal(39, WireProtocol.UnitSize);
        Assert.Equal(28, WireProtocol.ContactSize);
        Assert.Equal(40, WireProtocol.EventSize);

        byte[] bytes = new byte[WireProtocol.MaxMessageSize];

        foreach (HitResult result in new[] { HitResult.None, HitResult.Penetrated, HitResult.Deflected, HitResult.Miss })
        {
            ClientReplica replica = new();
            CombatEventObservation ev = new(
                EventId: 42,
                ServerTick: 15,
                Kind: CombatEventKind.ShotFired,
                PresentationId: 7,
                Origin: new Vector3(15.5f, 2.1f, 30.2f),
                Target: new Vector3(45.8f, 1.75f, 60.1f),
                ShooterUnitId: new EntityId(101),
                Result: result);

            int size = WireProtocol.WriteSnapshot(bytes, 1, 15, [], [], [ev]);
            Assert.True(replica.TryApply(bytes.AsSpan(0, size)));
            Assert.Equal(1, replica.EventCount);

            CombatEventObservation deserialized = replica.Events[0];
            Assert.Equal(42u, deserialized.EventId);
            Assert.Equal(15, deserialized.ServerTick);
            Assert.Equal(CombatEventKind.ShotFired, deserialized.Kind);
            Assert.Equal((ushort)7, deserialized.PresentationId);
            Assert.Equal(new EntityId(101), deserialized.ShooterUnitId);
            Assert.Equal(new Vector3(15.5f, 2.1f, 30.2f), deserialized.Origin);
            Assert.Equal(new Vector3(45.8f, 1.75f, 60.1f), deserialized.Target);
            Assert.Equal(result, deserialized.Result);
        }
    }

    [Fact]
    public void Ammunition_Supports1000Rounds_ValidationAndWireProtocolRoundTrip()
    {
        string machineGunJson = """
        {
          "schema_version": 1,
          "weapons": [
            {
              "id": "heavy_mg",
              "range": 50.0,
              "reload_duration_ticks": 10,
              "damage": 5,
              "ammo_capacity": 1000,
              "muzzle_height": 1.2,
              "presentation_id": "mg",
              "presentation_kind": "machine_gun",
              "penetration_near": 30.0,
              "penetration_far": 15.0
            }
          ],
          "units": [
            {
              "id": "mg_truck",
              "speed": 6.0,
              "sensor_range": 50.0,
              "eye_height": 1.8,
              "target_points": [1.0],
              "radius": 1.2,
              "max_health": 80,
              "weapon": "heavy_mg"
            }
          ]
        }
        """;
        UnitCatalog catalog = PrototypeScenario.LoadCatalog(machineGunJson);
        Assert.Equal(1000, catalog.Weapons![0].AmmoCapacity);

        // Verify wire protocol serializes and deserializes ammo 1000 without truncation
        ClientReplica replica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];

        OwnUnitObservation own = new(
            Id: new EntityId(10),
            Definition: 0,
            Position: new Vector2(15f, 20f),
            SupportSurface: 0,
            NavStatus: PathRequestStatus.None,
            Health: 80,
            Ammunition: 1000,
            ReloadTicksRemaining: 0,
            AttackStatus: AttackExecutionStatus.None,
            TargetContactId: default);

        int size = WireProtocol.WriteSnapshot(bytes, 1, 10, [own]);
        Assert.True(replica.TryApply(bytes.AsSpan(0, size)));
        Assert.Equal(1, replica.OwnUnits.Length);
        Assert.Equal((ushort)1000, replica.OwnUnits[0].Ammunition);
    }

    private sealed class MockFailingTransport : IServerTransport
    {
        private readonly IServerTransport _inner;
        public bool FailSend { get; set; }

        public MockFailingTransport(IServerTransport inner) => _inner = inner;

        public int ConnectedPlayerCount => _inner.ConnectedPlayerCount;
        public int InitialAdmittedPlayer => _inner.InitialAdmittedPlayer;
        public long GetPlayerConnectionId(int player) => _inner.GetPlayerConnectionId(player);
        public bool IsPlayerConnected(int player) => _inner.IsPlayerConnected(player);
        public void DisconnectPlayer(int player, ReadOnlySpan<byte> message = default) => _inner.DisconnectPlayer(player, message);
        public void Poll() => _inner.Poll();
        public bool TryReceive(out int player, out long connectionId, Span<byte> destination, out int length) =>
            _inner.TryReceive(out player, out connectionId, destination, out length);
        public bool TrySend(int player, ReadOnlySpan<byte> message, bool reliable = true)
        {
            if (FailSend) return false;
            return _inner.TrySend(player, message, reliable);
        }
        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public void PublishPlayer_FailedSendPreservesEventsForNextSnapshot()
    {
        StaticGeometry geometry = CombatScenario.CreateStaticGeometry();
        MatchSetup setup = CombatScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport realServer = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = realServer.Port;
        MockFailingTransport wrappedServer = new(realServer);
        using SimulationHost host = new(CombatScenario.CreateSimulation(geometry), wrappedServer, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 4 && client2.Replica.OwnUnits.Length == 4, host, client1, client2);

        WaitFor(() => client1.Replica.ContactCount > 0, host, client1, client2);

        ContactObservation targetContact = default;
        foreach (ContactObservation c in client1.Replica.Contacts)
        {
            if (c.State == ContactState.Current)
            {
                targetContact = c;
                break;
            }
        }
        Assert.NotEqual(0u, targetContact.Id.Value);

        EntityId client1Tank = client1.Replica.OwnUnits[1].Id;
        Assert.True(client1.Attack(client1Tank, targetContact.Id));

        // Advance 1 tick to fire shot
        host.Advance(1d / Simulation.TickRate);

        // Fail snapshot sends
        wrappedServer.FailSend = true;
        host.Advance(1d / Simulation.TickRate);

        // Re-enable snapshot sends
        wrappedServer.FailSend = false;
        WaitFor(() =>
        {
            foreach (CombatEventObservation ev in client1.Replica.Events)
            {
                if (ev.Kind == CombatEventKind.ShotFired) return true;
            }
            return false;
        }, host, client1, client2);
    }

    [Fact]
    public void ResetPlayer_ClearsEventQueue_ReconnectGetsNoStaleEvents()
    {
        StaticGeometry geometry = CombatScenario.CreateStaticGeometry();
        MatchSetup setup = CombatScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(CombatScenario.CreateSimulation(geometry), serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        ClientSession? client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 4 && client2.Replica.OwnUnits.Length == 4, host, client1, client2);

        // Client 2 disconnects
        client2.Dispose();
        client2 = null;

        // Advance several ticks so server detects disconnect and resets player 2
        for (int i = 0; i < 20; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client1.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        // Advance simulation with combat actions while player 2 is disconnected
        WaitFor(() => client1.Replica.ContactCount > 0, host, client1, client1);
        EntityId client1Tank = client1.Replica.OwnUnits[1].Id;
        ContactId targetContact = client1.Replica.Contacts[0].Id;
        client1.Attack(client1Tank, targetContact);

        for (int i = 0; i < 20; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client1.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        // Reconnect as player 2
        using ClientSession client2New = ClientSession.Connect("127.0.0.1", port, setup);
        WaitFor(() => client2New.IsConnected && client2New.Replica.OwnUnits.Length > 0, host, client1, client2New);

        // Reconnected client receives fresh state without stale historical events from disconnect period
        Assert.Equal(0, client2New.Replica.EventCount);
    }

    [Fact]
    public void TookFire_SnapshotRoundTrip_AcceptedByClientReplica_AndInvalidKindRejected()
    {
        ClientReplica victimReplica = new();
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        EntityId victimId = new(10);
        OwnUnitObservation victimUnit = new(
            victimId,
            Definition: 1,
            Position: new System.Numerics.Vector2(45f, 10f),
            SupportSurface: 0,
            NavStatus: PathRequestStatus.None,
            Health: 50, // Updated health after shot
            Ammunition: 10,
            ReloadTicksRemaining: 0,
            AttackStatus: AttackExecutionStatus.None,
            TargetContactId: default);

        CombatEventObservation tookFireEvent = new(
            EventId: 100,
            ServerTick: 42,
            Kind: CombatEventKind.TookFire,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            Origin: new System.Numerics.Vector3(0.5f, 0f, 0.866f), // Coarse quantized incoming bearing
            Target: new System.Numerics.Vector3(45f, 1.8f, 10f),   // Impact hit point
            ShooterUnitId: default);                               // Hidden shooter ID

        // 1. Serialization of TookFire snapshot succeeds and client replica accepts it
        int size = WireProtocol.WriteSnapshot(buffer, 1, 42, [victimUnit], [], [tookFireEvent]);
        Assert.True(victimReplica.TryApply(buffer.AsSpan(0, size)));

        // 2. Replica state is correctly updated
        Assert.Equal(1, victimReplica.OwnUnits.Length);
        Assert.Equal(50, victimReplica.OwnUnits[0].Health);
        Assert.Equal(1, victimReplica.EventCount);
        CombatEventObservation received = victimReplica.Events[0];
        Assert.Equal(100u, received.EventId);
        Assert.Equal(CombatEventKind.TookFire, received.Kind);
        Assert.Equal(tookFireEvent.Origin, received.Origin);
        Assert.Equal(tookFireEvent.Target, received.Target);
        Assert.Equal(default, received.ShooterUnitId);

        // 3. Undefined/invalid event kind (e.g. 99) must be rejected by TryReadSnapshot
        byte[] invalidBuffer = new byte[size];
        buffer.AsSpan(0, size).CopyTo(invalidBuffer);
        int eventKindOffset = WireProtocol.SnapshotHeaderSize + WireProtocol.UnitSize + 8;
        invalidBuffer[eventKindOffset] = 99; // Invalid kind
        ClientReplica badReplica = new();
        Assert.False(badReplica.TryApply(invalidBuffer));
    }

    [Fact]
    public void WireProtocol_SetFireStance_RoundTrip_AndValidation()
    {
        Assert.Equal(20, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(20, WireProtocol.Version);

        byte[] buffer = new byte[32];
        SetFireStanceCommand cmd = new(42, 10, new EntityId(5), FireStance.ReturnFire);

        WireProtocol.WriteSetFireStance(buffer, cmd);
        Assert.True(WireProtocol.TryReadSetFireStance(buffer.AsSpan(0, WireProtocol.SetFireStanceSize), out SetFireStanceCommand readCmd));
        Assert.Equal(42u, readCmd.Sequence);
        Assert.Equal(10, readCmd.ClientTick);
        Assert.Equal(5u, readCmd.Unit.Value);
        Assert.Equal(FireStance.ReturnFire, readCmd.Stance);

        // Out of range stance byte rejected
        byte[] invalidStanceBuffer = new byte[WireProtocol.SetFireStanceSize];
        buffer.AsSpan(0, WireProtocol.SetFireStanceSize).CopyTo(invalidStanceBuffer);
        invalidStanceBuffer[14] = 3; // > HoldFire (2)
        Assert.False(WireProtocol.TryReadSetFireStance(invalidStanceBuffer, out _));

        invalidStanceBuffer[14] = 255;
        Assert.False(WireProtocol.TryReadSetFireStance(invalidStanceBuffer, out _));

        // Sequence == 0 rejected
        byte[] invalidSeqBuffer = new byte[WireProtocol.SetFireStanceSize];
        buffer.AsSpan(0, WireProtocol.SetFireStanceSize).CopyTo(invalidSeqBuffer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(invalidSeqBuffer.AsSpan(2), 0);
        Assert.False(WireProtocol.TryReadSetFireStance(invalidSeqBuffer, out _));

        // Unit == 0 rejected
        byte[] invalidUnitBuffer = new byte[WireProtocol.SetFireStanceSize];
        buffer.AsSpan(0, WireProtocol.SetFireStanceSize).CopyTo(invalidUnitBuffer);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(invalidUnitBuffer.AsSpan(10), 0);
        Assert.False(WireProtocol.TryReadSetFireStance(invalidUnitBuffer, out _));

        // Snapshot round-trip preserves Stance in OwnUnitObservation
        OwnUnitObservation unit = new(
            new EntityId(5),
            Definition: 1,
            Position: new Vector2(10f, 10f),
            SupportSurface: 0,
            NavStatus: PathRequestStatus.None,
            Health: 100,
            Ammunition: 10,
            ReloadTicksRemaining: 0,
            AttackStatus: AttackExecutionStatus.None,
            TargetContactId: default,
            Stance: FireStance.HoldFire);

        byte[] snapBuffer = new byte[WireProtocol.MaxMessageSize];
        int snapSize = WireProtocol.WriteSnapshot(snapBuffer, 1, 10, [unit], [], []);
        ClientReplica replica = new();
        Assert.True(replica.TryApply(snapBuffer.AsSpan(0, snapSize)));
        Assert.Equal(1, replica.OwnUnits.Length);
        Assert.Equal(FireStance.HoldFire, replica.OwnUnits[0].Stance);
    }

    [Fact]
    public void AutoFire_TargetChoice_PrefersTankOverApcAtSimilarRange()
    {
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        const int size = 65;
        HeightMap map = new(size, size, 2f, 0.01f, new ushort[size * size]);
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");
        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
        UnitDefinition apcDef = catalog.RequireDefinition("apc");
        ushort apcIdx = catalog.RequireDefinitionIndex("apc");
        int ammo = catalog.GetAmmoCapacity(tankDef);

        // Player 1 armed tank at (10, 10) with default FireAtWill facing east toward enemy
        EntityId shooter = world.CreateUnit(1, tankIdx, new Vector2(10f, 10f), tankDef.Speed, tankDef.MaxHealth, ammo, initialTurretYaw: -MathF.PI / 2f);
        world.SetFireStance(shooter, FireStance.HoldFire); // hold during warmup

        // Player 2 tank at (35, 10) (dist 25m) and APC at (34, 10) (dist 24m, slightly closer!)
        EntityId enemyTank = world.CreateUnit(2, tankIdx, new Vector2(35f, 10f), tankDef.Speed, tankDef.MaxHealth, ammo);
        EntityId enemyApc = world.CreateUnit(2, apcIdx, new Vector2(34f, 10f), apcDef.Speed, apcDef.MaxHealth, 0);
        world.SetFireStance(enemyTank, FireStance.HoldFire);
        world.SetFireStance(enemyApc, FireStance.HoldFire);

        // Warmup detection so contacts exist
        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
        Assert.True(detection.TryGetContactByEntity(1, enemyTank, out ContactObservation cTank));
        Assert.True(detection.TryGetContactByEntity(1, enemyApc, out ContactObservation cApc));

        // Switch shooter to FireAtWill: auto-fire engages
        world.SetFireStance(shooter, FireStance.FireAtWill);
        sim.Step();

        // Shooter chooses the tank even though the APC was closer
        Assert.True(world.TryGetCombatState(shooter, out WorldState.CombatState cs));
        Assert.Equal(cTank.Id, cs.TargetContactId);
        Assert.Equal(enemyTank, cs.TargetEntityId);
        world.TryGetCombatState(enemyTank, out WorldState.CombatState enemyTankCs);
        Assert.True(enemyTankCs.CurrentHealth < tankDef.MaxHealth, "Enemy tank must have taken damage.");
        world.TryGetCombatState(enemyApc, out WorldState.CombatState enemyApcCs);
        Assert.Equal(apcDef.MaxHealth, enemyApcCs.CurrentHealth); // APC untouched
    }

    [Fact]
    public void CuedFire_T62HitsTargetOnlyBtrDetects_BlockedWithoutOwnLineOfSight()
    {
        UnitCatalog baseCat = ColdWarContent.LoadCatalog();
        // Create catalog where T-62 has restricted sensor range (400m), but full weapon range (1600m).
        // BTR-70 has sensor range 1500m.
        UnitDefinition baseT62 = baseCat.RequireDefinition("t62");
        UnitDefinition restrictedT62 = baseT62 with { SensorRange = 400f };

        UnitDefinition[] modifiedUnits = (UnitDefinition[])baseCat.Units.Clone();
        int t62Idx = baseCat.RequireDefinitionIndex("t62");
        modifiedUnits[t62Idx] = restrictedT62;
        UnitCatalog testCatalog = new(
            baseCat.SchemaVersion,
            modifiedUnits,
            baseCat.Detection,
            baseCat.Weapons,
            baseCat.Factions);

        const int size = 201;
        HeightMap map = new(size, size, 10f, 0.01f, new ushort[size * size]); // 2000m x 2000m flat map
        StaticGeometry geo = new(map, []);
        OpsRules rules = new(geo, testCatalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules);

        UnitDefinition t62Def = testCatalog.RequireDefinition("t62");
        ushort t62DefIdx = testCatalog.RequireDefinitionIndex("t62");
        UnitDefinition btrDef = testCatalog.RequireDefinition("btr70");
        ushort btrDefIdx = testCatalog.RequireDefinitionIndex("btr70");
        UnitDefinition m60Def = testCatalog.RequireDefinition("m60a1");
        ushort m60DefIdx = testCatalog.RequireDefinitionIndex("m60a1");

        // Player 1: T-62 at (200, 500) facing east, BTR-70 at (800, 500)
        EntityId t62 = world.CreateUnit(1, t62DefIdx, new Vector2(200f, 500f), t62Def.Speed, t62Def.MaxHealth, testCatalog.GetAmmoCapacity(t62Def), initialTurretYaw: -MathF.PI / 2f);
        EntityId btr = world.CreateUnit(1, btrDefIdx, new Vector2(800f, 500f), btrDef.Speed, btrDef.MaxHealth, 0);
        world.SetFireStance(t62, FireStance.HoldFire);
        world.SetFireStance(btr, FireStance.HoldFire);

        // Player 2: Target M60A1 at (1000, 500)
        // Distance to T-62 is 800m (> T-62's sensor range 400m, but <= weapon range 1600m).
        // Distance to BTR-70 is 200m (<= BTR-70's sensor range 1500m).
        EntityId targetM60 = world.CreateUnit(2, m60DefIdx, new Vector2(1000f, 500f), m60Def.Speed, m60Def.MaxHealth, testCatalog.GetAmmoCapacity(m60Def));
        world.SetFireStance(targetM60, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) sim.Step();

        DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
        // Target is spotted by Player 1 (via BTR-70)
        Assert.True(detection.TryGetContactByEntity(1, targetM60, out ContactObservation contact));
        Assert.Equal(ContactState.Current, contact.State);

        // T-62 issues attack order on contact spotted only by BTR-70
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, t62, contact.Id)));
        sim.Step();

        // T-62 fires cued shot and damages the target
        world.TryGetCombatState(t62, out WorldState.CombatState t62Cs);
        Assert.Equal(testCatalog.GetAmmoCapacity(t62Def) - 1, t62Cs.Ammunition);
        world.TryGetCombatState(targetM60, out WorldState.CombatState m60Cs);
        Assert.True(m60Cs.CurrentHealth < m60Def.MaxHealth, "Target must take damage from cued fire.");

        // Part 2: If line of sight is obstructed by a wall between T-62 and target, cued fire is blocked
        OpaqueStructure wall = new(
            new StructureId(1), minX: 490f, minY: 0f, minZ: 490f, maxX: 510f, maxY: 6f, maxZ: 510f, isGrounded: true);
        StaticGeometry blockedGeo = new(map, [wall]);
        OpsRules blockedRules = new(blockedGeo, testCatalog, null);
        WorldState blockedWorld = new(map);
        using Simulation blockedSim = new(blockedWorld, blockedRules);

        EntityId blockedT62 = blockedWorld.CreateUnit(1, t62DefIdx, new Vector2(200f, 500f), t62Def.Speed, t62Def.MaxHealth, testCatalog.GetAmmoCapacity(t62Def));
        // BTR-70 placed to the side at (800, 480) with clear view to target
        EntityId blockedBtr = blockedWorld.CreateUnit(1, btrDefIdx, new Vector2(800f, 480f), btrDef.Speed, btrDef.MaxHealth, 0);
        EntityId blockedTarget = blockedWorld.CreateUnit(2, m60DefIdx, new Vector2(1000f, 500f), m60Def.Speed, m60Def.MaxHealth, testCatalog.GetAmmoCapacity(m60Def));
        blockedWorld.SetFireStance(blockedT62, FireStance.HoldFire);
        blockedWorld.SetFireStance(blockedBtr, FireStance.HoldFire);
        blockedWorld.SetFireStance(blockedTarget, FireStance.HoldFire);

        for (int i = 0; i < 4; i++) blockedSim.Step();

        DetectionSystem blockedDetection = blockedRules.FindSystem<DetectionSystem>()!;
        Assert.True(blockedDetection.TryGetContactByEntity(1, blockedTarget, out ContactObservation blockedContact));

        // T-62 attack order is accepted, but cannot fire because direct LOS is obstructed
        Assert.True(blockedSim.TryEnqueue(1, new AttackCommand(1, blockedSim.Tick, blockedT62, blockedContact.Id)));
        blockedSim.Step();

        blockedWorld.TryGetCombatState(blockedT62, out WorldState.CombatState csBlocked);
        Assert.Equal(testCatalog.GetAmmoCapacity(t62Def), csBlocked.Ammunition); // Holds fire!
        Assert.Equal(AttackExecutionStatus.Obstructed, csBlocked.Status);
    }
}
