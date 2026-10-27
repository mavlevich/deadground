using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class NavigationNetworkTests
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

    private static void WaitFor(Func<bool> condition, SimulationHost host, ClientSession c1, int maxAttempts = 150)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
        {
            Pump(host, c1, c1, iterations: 1, delayMs: 5);
        }
        Assert.True(condition());
    }

    [Fact]
    public void TwoClientsConnectOverLoopback_NavigationScenario_ServerAuthoritativeMovementAroundObstacle()
    {
        StaticGeometry geometry = NavigationScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        MatchSetup setup = NavigationScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(NavigationScenario.CreateSimulation(geometry, catalog), serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        // Wait for connection and initial snapshots
        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 3 && client2.Replica.OwnUnits.Length == 2, host, client1, client2);

        Assert.Equal(1, client1.AssignedPlayerId);
        Assert.Equal(2, client2.AssignedPlayerId);

        // Client 1 has 3 units, Client 2 has 2 units
        Assert.Equal(3, client1.Replica.OwnUnits.Length);
        Assert.Equal(2, client2.Replica.OwnUnits.Length);

        // Client 1 issues a move order for its first unit (Recon at (15, 20))
        // Target is past the standalone building (at 30..44, 30..44) to (50, 37)
        EntityId client1Unit = client1.Replica.OwnUnits[0].Id;
        Vector2 target = new(50f, 37f);
        Assert.True(client1.Move(client1Unit, target));

        // Wait for command confirmation
        WaitFor(() => client1.LastCommandResult is { Sequence: 1, Queued: true }, host, client1, client2);

        // Advance simulation and let unit navigate around the building
        for (int i = 0; i < 200; i++)
        {
            Pump(host, client1, client2, iterations: 1, delayMs: 5);
        }

        // Verify client1 replica receives authoritative position arriving at target
        bool found = false;
        Vector2 unitPos = default;
        foreach (OwnUnitObservation u in client1.Replica.OwnUnits)
        {
            if (u.Id == client1Unit)
            {
                found = true;
                unitPos = u.Position;
                break;
            }
        }
        Assert.True(found);
        Assert.True(Vector2.Distance(unitPos, target) < 0.5f,
            $"Unit did not arrive near destination: {unitPos} vs {target}");

        // Client 2 attempts to issue a command for Client 1's unit (rejected)
        Assert.True(client2.Move(client1Unit, new Vector2(10f, 10f)));
        WaitFor(() => client2.LastCommandResult is { Sequence: 1 }, host, client1, client2);
        Assert.False(client2.LastCommandResult!.Value.Queued);
        Assert.Equal(1L, client2.RejectedCommands);
    }

    [Fact]
    public void Dense1000Navigation_ZeroSteadyStateAllocationsAndMeetsTickBudget()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);

        // Central building obstacle at X in [58..70], Z in [40..88]
        OpaqueStructure building = new(
            new StructureId(1),
            minX: 58f, minY: 0f, minZ: 40f,
            maxX: 70f, maxY: 6f, maxZ: 88f,
            isGrounded: true);

        // Enclosed courtyard obstacle: 4 walls enclosing interior [92..108, 77..95]
        OpaqueStructure courtS = new(new StructureId(2), 90f, 0f, 75f, 110f, 3f, 77f, isGrounded: true);
        OpaqueStructure courtN = new(new StructureId(3), 90f, 0f, 95f, 110f, 3f, 97f, isGrounded: true);
        OpaqueStructure courtW = new(new StructureId(4), 90f, 0f, 77f, 92f, 3f, 95f, isGrounded: true);
        OpaqueStructure courtE = new(new StructureId(5), 108f, 0f, 77f, 110f, 3f, 95f, isGrounded: true);

        StaticGeometry geo = new(map, [building, courtS, courtN, courtW, courtE]);
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        NavigationTuning navTuning = new(MaxNodeExpansionsPerTick: 1000, MaxPendingRequests: 1024);
        OpsRules rules = new(geo, new OpsPerceptionCatalog(catalog), new SlowMovementCatalog(), navTuning);
        WorldState world = new(geo.HeightMap);
        using Simulation sim = new(world, rules);

        // 500 units on Side 1: moving North in West corridor X in [10..46]
        for (int i = 0; i < 500; i++)
        {
            float x = 10f + (i % 25) * 1.5f;
            float y = 10f + (i / 25) * 2.5f;
            EntityId id = world.CreateUnit(1, 1, new Vector2(x, y), 0.001f);
            bool enqueued = sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), 0, id, new Vector2(x, y + 60f)));
            Assert.True(enqueued);
            if (i % 200 == 199) sim.Step();
        }
        sim.Step();

        // 500 units on Side 2: moving South in West corridor X in [10..46]
        for (int i = 0; i < 500; i++)
        {
            float x = 10f + (i % 25) * 1.5f;
            float y = 118f - (i / 25) * 2.5f;
            EntityId id = world.CreateUnit(2, 2, new Vector2(x, y), 0.001f);
            bool enqueued = sim.TryEnqueue(2, new MoveCommand((uint)(500 + i + 1), 0, id, new Vector2(x, y - 60f)));
            Assert.True(enqueued);
            if (i % 200 == 199) sim.Step();
        }
        sim.Step();

        // Step simulation until all 1000 units have routes established and are moving
        int prepTicks = 0;
        while (prepTicks < 150)
        {
            sim.Step();
            prepTicks++;
            int moving = 0;
            for (int u = 0; u < world.Count; u++) if (world.GetUnitAt(u).Moving) moving++;
            if (moving == 1000) break;
        }

        int initialMoving = 0;
        for (int u = 0; u < world.Count; u++) if (world.GetUnitAt(u).Moving) initialMoving++;
        Assert.Equal(1000, initialMoving);

        PlayerKnowledge k1 = new(1);
        PlayerKnowledge k2 = new(2);
        byte[] b1 = new byte[WireProtocol.MaxMessageSize];
        byte[] b2 = new byte[WireProtocol.MaxMessageSize];

        uint commandSeq = 2001;

        // Warm up for 20 ticks with representative workload to JIT compile all code paths
        for (int m = 0; m < 5; m++)
        {
            EntityId uId = world.GetUnitAt(m).Id;
            sim.TryEnqueue(1, new MoveCommand(commandSeq++, 0, uId, new Vector2(15f + (m * 4f), 80f)));
        }
        EntityId warmupUnreachableId = world.GetUnitAt(500).Id;
        sim.TryEnqueue(2, new MoveCommand(commandSeq++, 0, warmupUnreachableId, new Vector2(100f, 86f)));

        for (int i = 0; i < 20; i++)
        {
            sim.Step();
            if (sim.Tick % 2 == 0)
            {
                sim.UpdateKnowledge(k1);
                WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts);
                sim.UpdateKnowledge(k2);
                WireProtocol.WriteSnapshot(b2, (uint)sim.Tick, sim.Tick, k2.OwnUnits, k2.Contacts);
            }
        }

        double[] times = new double[100];
        using NoGcAllocationWindow window = new();
        long totalAllocated = 0;
        int minMoving = 1000;

        for (int i = 0; i < 100; i++)
        {
            // At tick 0, enqueue a bounded burst of new move orders and genuine unreachable searches
            if (i == 0)
            {
                for (int m = 0; m < 10; m++)
                {
                    EntityId uId = world.GetUnitAt(10 + m).Id;
                    sim.TryEnqueue(1, new MoveCommand(commandSeq++, 0, uId, new Vector2(15f + (m * 2f), 80f)));
                }
                for (int u = 0; u < 2; u++)
                {
                    EntityId uId = world.GetUnitAt(501 + u).Id;
                    sim.TryEnqueue(2, new MoveCommand(commandSeq++, 0, uId, new Vector2(100f, 86f)));
                }
            }

            long startAlloc = GC.GetAllocatedBytesForCurrentThread();
            long start = System.Diagnostics.Stopwatch.GetTimestamp();

            sim.Step();
            if (sim.Tick % 2 == 0)
            {
                sim.UpdateKnowledge(k1);
                WireProtocol.WriteSnapshot(b1, (uint)sim.Tick, sim.Tick, k1.OwnUnits, k1.Contacts);
                sim.UpdateKnowledge(k2);
                WireProtocol.WriteSnapshot(b2, (uint)sim.Tick, sim.Tick, k2.OwnUnits, k2.Contacts);
            }

            long elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - start;
            long endAlloc = GC.GetAllocatedBytesForCurrentThread();

            totalAllocated += (endAlloc - startAlloc);
            times[i] = (double)elapsed / System.Diagnostics.Stopwatch.Frequency * 1000.0;

            int currentMoving = 0;
            for (int n = 0; n < world.Count; n++) if (world.GetUnitAt(n).Moving) currentMoving++;
            minMoving = Math.Min(minMoving, currentMoving);
        }

        Array.Sort(times);
        double medianMs = times[50];
        double p99Ms = times[98];

        Assert.True(totalAllocated == 0, window.Describe(totalAllocated));
        Assert.True(minMoving >= 980, $"Moving units dropped to {minMoving}/1000.");

#if DEBUG
        const double targetMedianMs = 150.0;
        const double targetP99Ms = 250.0;
#else
        const double targetMedianMs = 50.0;
        const double targetP99Ms = 250.0;
#endif
        Assert.True(medianMs < targetMedianMs, $"Median tick time {medianMs:F2} ms exceeded {targetMedianMs} ms target.");
        Assert.True(p99Ms < targetP99Ms, $"P99 tick time {p99Ms:F2} ms exceeded {targetP99Ms} ms target.");
    }

    [Fact]
    public void ReplacementClient_SequenceReset_OldQueuedOrderCannotSupersedeNewOrder()
    {
        const int size = 33;
        HeightMap map = new(size, size, 1f, 0.01f, new ushort[size * size]);
        OpaqueStructure wall = new(new StructureId(1), 15f, 0f, 0f, 17f, 3f, 25f, isGrounded: true);
        StaticGeometry geo = new(map, [wall]);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 6f, radius: 0.8f);
        catalog.Register(2, speed: 6f, radius: 0.8f);

        NavigationTuning tuning = new(MaxNodeExpansionsPerTick: 5);
        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map, capacity: 16);
        RuleSet rules = new(nav);

        EntityId player1Unit = world.CreateUnit(1, 1, new Vector2(5f, 5f), 6f);
        EntityId player2Unit = world.CreateUnit(2, 2, new Vector2(10f, 10f), 6f);

        Simulation sim = new(world, rules);
        MatchSetup setup = new(ProtocolVersion: WireProtocol.Version, ContentHash: 1, MapHash: 1, RuleSetId: "ops");

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(sim, serverTransport, setup);

        ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected, host, client1, client2);

        // Player 2 occupies the A* context with multi-tick search around wall
        Assert.True(client2.Move(player2Unit, new Vector2(22f, 10f)));
        WaitFor(() => client2.LastCommandResult is { Sequence: 1, Queued: true }, host, client1, client2);

        // Player 1 sends sequence 1 to detour target (26, 10), queuing behind player 2's search
        Assert.True(client1.Move(player1Unit, new Vector2(26f, 10f)));
        WaitFor(() => client1.LastCommandResult is { Sequence: 1, Queued: true }, host, client1, client2);

        // Disconnect and dispose client 1
        client1.Dispose();

        // Pump until server detects disconnect
        for (int i = 0; i < 20; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client2.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        // Connect replacement client to slot 1
        using ClientSession replacementClient1 = ClientSession.Connect("127.0.0.1", port, setup);
        for (int i = 0; i < 50 && !replacementClient1.IsConnected; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client2.Advance(1d / Simulation.TickRate);
            replacementClient1.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }
        Assert.True(replacementClient1.IsConnected);
        Assert.Equal(1, replacementClient1.AssignedPlayerId);

        // Replacement client sends sequence 1 for same unit to direct target (5, 20)
        Assert.True(replacementClient1.Move(player1Unit, new Vector2(5f, 20f)));
        for (int i = 0; i < 50 && replacementClient1.LastCommandResult is null; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client2.Advance(1d / Simulation.TickRate);
            replacementClient1.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }
        Assert.True(replacementClient1.LastCommandResult is { Queued: true });

        // Advance simulation for 140 ticks
        for (int i = 0; i < 140; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client2.Advance(1d / Simulation.TickRate);
            replacementClient1.Advance(1d / Simulation.TickRate);
            Thread.Sleep(2);
        }

        client2.Dispose();

        // Verify player 1 unit arrives at replacement destination (5, 20) and not (26, 10)
        UnitState finalState = world.GetUnitAt(0);
        Assert.True(Vector2.Distance(finalState.Position, new Vector2(5f, 20f)) < 0.2f,
            $"Unit did not arrive at (5, 20): {finalState.Position}");
        Assert.False(Vector2.Distance(finalState.Position, new Vector2(26f, 10f)) < 5.0f,
            "Unit diverted to obsolete route (26, 10).");
    }

    [Fact]
    public void NavigationOutcome_CarriedInOwnerSnapshot_EnemyReceivesNoStatus()
    {
        StaticGeometry geometry = NavigationScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        MatchSetup setup = NavigationScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(NavigationScenario.CreateSimulation(geometry, catalog), serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 3 && client2.Replica.OwnUnits.Length == 2, host, client1, client2);

        EntityId c1Unit = client1.Replica.OwnUnits[0].Id;

        // 1. Order unit deep inside the building at (37, 37) (building is at 30..44, 30..44, 7m deep > 3m snap)
        Assert.True(client1.Move(c1Unit, new Vector2(37f, 37f)));

        // Wait for snapshot containing InvalidDestination for client 1
        WaitFor(() =>
        {
            foreach (OwnUnitObservation u in client1.Replica.OwnUnits)
            {
                if (u.Id == c1Unit && u.NavStatus == PathRequestStatus.InvalidDestination) return true;
            }
            return false;
        }, host, client1, client2);

        // Verify client 1 snapshot reports InvalidDestination for its own unit
        OwnUnitObservation c1Obs = default;
        foreach (OwnUnitObservation u in client1.Replica.OwnUnits)
        {
            if (u.Id == c1Unit) { c1Obs = u; break; }
        }
        Assert.Equal(PathRequestStatus.InvalidDestination, c1Obs.NavStatus);

        // Verify client 2 (enemy) NEVER receives client 1's unit in OwnUnits
        foreach (OwnUnitObservation u in client2.Replica.OwnUnits)
        {
            Assert.NotEqual(c1Unit, u.Id);
        }

        // 2. Now order unit to a valid clear destination (50, 37)
        Assert.True(client1.Move(c1Unit, new Vector2(50f, 37f)));

        // Wait for snapshot confirming PathFound
        WaitFor(() =>
        {
            foreach (OwnUnitObservation u in client1.Replica.OwnUnits)
            {
                if (u.Id == c1Unit && u.NavStatus == PathRequestStatus.PathFound) return true;
            }
            return false;
        }, host, client1, client2);
    }

    [Fact]
    public void FinalFacing_MoveOverUdp_ExecutesTurnsAndReplicatesToReplica()
    {
        StaticGeometry geometry = NavigationScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        MatchSetup setup = NavigationScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;
        using SimulationHost host = new(NavigationScenario.CreateSimulation(geometry, catalog), serverTransport, setup);

        using ClientSession client = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client.IsConnected && client.Replica.OwnUnits.Length == 3, host, client);

        EntityId unitId = client.Replica.OwnUnits[0].Id;
        Vector2 target = new(25f, 20f);
        float desiredFacing = 0f; // North (0 rad)

        Assert.True(client.Move(unitId, target, desiredFacing));
        WaitFor(() => client.LastCommandResult is { Sequence: 1, Queued: true }, host, client);

        // Advance until unit arrives and rotates to final facing
        WaitFor(() =>
        {
            foreach (OwnUnitObservation u in client.Replica.OwnUnits)
            {
                if (u.Id == unitId)
                {
                    bool nearDest = Vector2.Distance(u.Position, target) <= 0.15f;
                    bool facingMatched = MathF.Abs(CombatSystem.ShorterArcDelta(u.HullHeading, desiredFacing)) <= 0.05f;
                    if (nearDest && facingMatched)
                    {
                        return true;
                    }
                }
            }
            return false;
        }, host, client, maxAttempts: 150);

        OwnUnitObservation finalObs = default;
        foreach (OwnUnitObservation u in client.Replica.OwnUnits)
        {
            if (u.Id == unitId) { finalObs = u; break; }
        }

        Assert.True(Vector2.Distance(finalObs.Position, target) <= 0.15f);
        Assert.True(MathF.Abs(CombatSystem.ShorterArcDelta(finalObs.HullHeading, desiredFacing)) <= 0.05f);
    }

    [Fact]
    public void FinalFacing_SimulationDeterminism_IdenticalRunsMatchExactly()
    {
        StaticGeometry geometry = NavigationScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        using Simulation sim1 = NavigationScenario.CreateSimulation(geometry, catalog);
        using Simulation sim2 = NavigationScenario.CreateSimulation(geometry, catalog);

        EntityId unit1 = sim1.World.GetUnitAt(0).Id;
        EntityId unit2 = sim2.World.GetUnitAt(0).Id;

        MoveCommand cmd1 = new(1, 0, unit1, new Vector2(40f, 25f), FinalFacing: -1.25f);
        MoveCommand cmd2 = new(1, 0, unit2, new Vector2(40f, 25f), FinalFacing: -1.25f);

        sim1.TryEnqueue(1, cmd1);
        sim2.TryEnqueue(1, cmd2);

        for (int tick = 0; tick < 100; tick++)
        {
            sim1.Step();
            sim2.Step();

            UnitState u1 = sim1.World.GetUnitAt(0);
            UnitState u2 = sim2.World.GetUnitAt(0);
            sim1.World.TryGetCombatState(unit1, out WorldState.CombatState cs1);
            sim2.World.TryGetCombatState(unit2, out WorldState.CombatState cs2);

            Assert.Equal(u1.Position, u2.Position);
            Assert.Equal(cs1.HullHeading, cs2.HullHeading);
            Assert.Equal(u1.Moving, u2.Moving);
        }
    }

    [Fact]
    public void FinalFacing_FogOfWar_FreezesLastKnownHullHeading()
    {
        StaticGeometry geometry = VisibilityScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        DetectionTuning lifecycleTuning = new(
            memoryDurationTicks: 30,
            correlationWindowTicks: 50,
            correlationDistanceGate: 30.0f,
            evaluationIntervalTicks: 2);
        OpsRules rules = new(geometry, new OpsPerceptionCatalog(catalog), lifecycleTuning);
        WorldState world = new(geometry.HeightMap);
        world.CreateUnit(1, 1, new Vector2(20f, 25f), catalog.Units[1].Speed); // Observer 1 (Recon)
        EntityId enemy = world.CreateUnit(2, 2, new Vector2(60f, 25f), 10.0f); // Enemy tank in open view at (60, 25)

        using Simulation sim = new(world, rules);

        // Step so watcher spots enemy
        for (int i = 0; i < 10; i++) sim.Step();

        PlayerKnowledge p1Knowledge = new(1);
        sim.UpdateKnowledge(p1Knowledge);

        Assert.Equal(1, p1Knowledge.Contacts.Length);
        ContactObservation seen = p1Knowledge.Contacts[0];
        Assert.Equal(ContactState.Current, seen.State);

        // Move enemy behind the 6m building to (60, 45) and specify final facing
        sim.TryEnqueue(2, new MoveCommand(1, sim.Tick, enemy, new Vector2(60f, 45f), FinalFacing: 2.5f));

        // Advance simulation until enemy is behind building and turns
        for (int i = 0; i < 60; i++) sim.Step();

        sim.UpdateKnowledge(p1Knowledge);
        Assert.Equal(1, p1Knowledge.Contacts.Length);
        ContactObservation lost = p1Knowledge.Contacts[0];

        Assert.Equal(ContactState.LastKnown, lost.State);
        // Contact heading must remain frozen from last seen observation, not leaking the unobserved turn
        Assert.False(MathF.Abs(lost.HullHeading - 2.5f) < 0.1f,
            "Server must not leak unobserved heading changes in fog of war.");
    }

    private sealed class SlowMovementCatalog : IUnitMovementCatalog
    {
        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
        {
            profile = new UnitMovementProfile(0.001f, 1.0f);
            return true;
        }
    }

    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public TestMovementCatalog Register(ushort definition, float speed, float radius)
        {
            _profiles[definition] = new UnitMovementProfile(speed, radius);
            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }
}
