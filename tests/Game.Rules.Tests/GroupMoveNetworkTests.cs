using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class GroupMoveNetworkTests
{
    private static Simulation CreateEmptySimulation(StaticGeometry geometry, UnitCatalog catalog)
    {
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(geometry.HeightMap, 64);
        return new Simulation(world, rules);
    }

    private static void Pump(SimulationHost host, ClientSession c1, int iterations = 1, int delayMs = 5)
    {
        for (int i = 0; i < iterations; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            c1.Advance(1d / Simulation.TickRate);
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
    }

    private static void WaitFor(Func<bool> condition, SimulationHost host, ClientSession c1, int maxAttempts = 150)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
        {
            Pump(host, c1, iterations: 1, delayMs: 5);
        }
        Assert.True(condition(), "Condition was not met within maximum attempts.");
    }

    [Fact]
    public void Test16_LocalAndClientSessionParity_ProduceIdenticalAssignments()
    {
        StaticGeometry geometry = NavigationScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        MatchSetup setup = NavigationScenario.CreateMatchSetup(geometry);

        Vector2 target = new(80f, 80f);
        Vector2[] initialPositions =
        [
            new(20f, 20f),
            new(25f, 20f),
            new(30f, 20f),
            new(35f, 20f)
        ];

        // 1. Run with LocalSession
        GroupMoveResult localResult;
        {
            Simulation sim = CreateEmptySimulation(geometry, catalog);
            EntityId[] localUnits = new EntityId[4];
            for (int i = 0; i < 4; i++)
            {
                localUnits[i] = sim.World.CreateUnit(1, 1, initialPositions[i], 10f);
                sim.World.SetFireStance(localUnits[i], FireStance.HoldFire);
            }

            using LocalSession localSession = new(sim, player: 1);
            Assert.True(localSession.GroupMove(localUnits, target));

            for (int i = 0; i < 20; i++)
            {
                localSession.Advance(1d / Simulation.TickRate);
                if (localSession.LastGroupMoveResult is { Placed.Length: 4 }) break;
            }

            Assert.NotNull(localSession.LastGroupMoveResult);
            localResult = localSession.LastGroupMoveResult.Value;
        }

        // 2. Run with dedicated server and ClientSession over UDP
        GroupMoveResult networkResult;
        {
            using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
            int port = serverTransport.Port;

            Simulation sim = CreateEmptySimulation(geometry, catalog);
            EntityId[] netUnits = new EntityId[4];
            for (int i = 0; i < 4; i++)
            {
                netUnits[i] = sim.World.CreateUnit(1, 1, initialPositions[i], 10f);
                sim.World.SetFireStance(netUnits[i], FireStance.HoldFire);
            }

            using SimulationHost host = new(sim, serverTransport, setup);
            using ClientSession client = ClientSession.Connect("127.0.0.1", port, setup);

            WaitFor(() => client.IsConnected && client.Replica.OwnUnits.Length == 4, host, client);

            Assert.True(client.GroupMove(netUnits, target));

            WaitFor(() => client.LastGroupMoveResult is { Placed.Length: 4 }, host, client);
            networkResult = client.LastGroupMoveResult!.Value;
        }

        // 3. Compare results for parity
        Assert.Equal(localResult.Placed.Length, networkResult.Placed.Length);
        Assert.Equal(localResult.Unplaced.Length, networkResult.Unplaced.Length);
        Assert.Equal(localResult.Target, networkResult.Target);
        Assert.Equal(localResult.TargetSurface, networkResult.TargetSurface);

        for (int i = 0; i < localResult.Placed.Length; i++)
        {
            GroupMoveAssignment localA = localResult.Placed.Span[i];
            GroupMoveAssignment netA = networkResult.Placed.Span[i];

            Assert.Equal(localA.Unit, netA.Unit);
            Assert.Equal(localA.Target.X, netA.Target.X, 0.001f);
            Assert.Equal(localA.Target.Y, netA.Target.Y, 0.001f);
            Assert.Equal(localA.TargetSurface, netA.TargetSurface);
            Assert.Equal(localA.FinalFacing, netA.FinalFacing);
        }
    }

    [Fact]
    public void Test17_PreviewAndAuthoritativeResponseCorrelation_MatchesSequenceAndSlots()
    {
        StaticGeometry geometry = NavigationScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        MatchSetup setup = NavigationScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        int port = serverTransport.Port;

        Simulation sim = CreateEmptySimulation(geometry, catalog);
        EntityId[] units = new EntityId[3];
        Vector2[] positions = [new(20f, 25f), new(25f, 25f), new(30f, 25f)];
        for (int i = 0; i < 3; i++)
        {
            units[i] = sim.World.CreateUnit(1, 1, positions[i], 10f);
            sim.World.SetFireStance(units[i], FireStance.HoldFire);
        }

        using SimulationHost host = new(sim, serverTransport, setup);
        using ClientSession client = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client.IsConnected && client.Replica.OwnUnits.Length == 3, host, client);

        DestinationEvaluator evaluator = new(geometry);
        OpsMovementCatalog movementCatalog = new(catalog);
        Assert.True(movementCatalog.TryGetProfile(1, out UnitMovementProfile profile));

        GroupUnitInput[] inputs =
        [
            new(units[0], positions[0], 0f, profile.Radius, profile.EffectiveCollisionRadius, profile.Capabilities, profile.ReverseSpeed > 0f, profile.ReverseSpeed),
            new(units[1], positions[1], 0f, profile.Radius, profile.EffectiveCollisionRadius, profile.Capabilities, profile.ReverseSpeed > 0f, profile.ReverseSpeed),
            new(units[2], positions[2], 0f, profile.Radius, profile.EffectiveCollisionRadius, profile.Capabilities, profile.ReverseSpeed > 0f, profile.ReverseSpeed)
        ];

        Vector2 target = new(65f, 65f);
        GroupPreviewResult preview = GroupDestinationDistribution.ComputePreview(
            inputs,
            target,
            targetSurface: 0,
            finalFacing: null,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        Assert.Equal(3, preview.PlacedCount);
        Assert.Equal(0, preview.BlockedCount);

        Assert.True(client.GroupMove(units, target));
        uint seq = client.LastSubmittedCommandSequence;

        WaitFor(() => client.LastGroupMoveResult is { Sequence: var s } && s == seq, host, client);

        GroupMoveResult authoritative = client.LastGroupMoveResult!.Value;
        Assert.Equal(seq, authoritative.Sequence);
        Assert.Equal(3, authoritative.Placed.Length);

        // Verify each placed unit in authoritative response matches preview position
        for (int i = 0; i < authoritative.Placed.Length; i++)
        {
            GroupMoveAssignment authSlot = authoritative.Placed.Span[i];
            bool foundInPreview = false;
            for (int j = 0; j < preview.Slots.Length; j++)
            {
                GroupPreviewSlot prevSlot = preview.Slots.Span[j];
                if (prevSlot.UnitId == authSlot.Unit)
                {
                    foundInPreview = true;
                    Assert.Equal(prevSlot.Target.X, authSlot.Target.X, 0.001f);
                    Assert.Equal(prevSlot.Target.Y, authSlot.Target.Y, 0.001f);
                    break;
                }
            }
            Assert.True(foundInPreview, $"Unit {authSlot.Unit} not found in preview slots.");
        }
    }

    [Fact]
    public void Test18_EndToEndMovement_TanksOccupyDistinctPositionsWithoutCollision()
    {
        StaticGeometry geometry = NavigationScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        Simulation sim = CreateEmptySimulation(geometry, catalog);
        const int tankCount = 10;
        EntityId[] tanks = new EntityId[tankCount];

        ushort tankDefIdx = catalog.RequireDefinitionIndex("prototype_tank");
        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");

        // Place 10 tanks in 2 rows of 5 on open field (X in [15..27], Y in [15..20])
        for (int i = 0; i < tankCount; i++)
        {
            float x = 15f + (i % 5) * 3f;
            float y = 15f + (i / 5) * 4f;
            tanks[i] = sim.World.CreateUnit(1, tankDefIdx, new Vector2(x, y), tankDef.Speed);
            sim.World.SetFireStance(tanks[i], FireStance.HoldFire);
        }

        using LocalSession session = new(sim, player: 1);

        // Move North along clear corridor (no obstacles between Y=15 and Y=65 in X=[15..30])
        Vector2 target = new(20f, 65f);
        Assert.True(session.GroupMove(tanks, target));

        // Advance simulation until all units arrive
        for (int step = 0; step < 500; step++)
        {
            session.Advance(1d / Simulation.TickRate);
        }

        // Verify all 10 tanks arrived at distinct positions
        Vector2[] finalPositions = new Vector2[tankCount];
        for (int i = 0; i < tankCount; i++)
        {
            finalPositions[i] = session.Replica.OwnUnits[i].Position;
        }

        // Assert no two tanks violate collision radius (radius = 1.4m for tank in catalog, min distance = 2.8m - tolerance)
        for (int i = 0; i < tankCount; i++)
        {
            for (int j = i + 1; j < tankCount; j++)
            {
                float dist = Vector2.Distance(finalPositions[i], finalPositions[j]);
                Assert.True(dist >= 2.5f,
                    $"Tanks {i} and {j} overlap at end of group move: dist={dist:F2}m (positions: {finalPositions[i]} vs {finalPositions[j]})");
            }
        }

        // Verify group arrived near target
        Vector2 centroid = Vector2.Zero;
        for (int i = 0; i < tankCount; i++) centroid += finalPositions[i];
        centroid /= tankCount;

        Assert.True(Vector2.Distance(centroid, target) < 15.0f,
            $"Centroid {centroid} is too far from target {target}");
    }

    [Fact]
    public void Test_TwoPlayers_SimultaneousGroupMove_IndependentResults()
    {
        StaticGeometry geometry = NavigationScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        Simulation sim = CreateEmptySimulation(geometry, catalog);
        ushort tankDefIdx = catalog.RequireDefinitionIndex("prototype_tank");
        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");

        EntityId[] p1Units = new EntityId[3];
        for (int i = 0; i < 3; i++)
        {
            p1Units[i] = sim.World.CreateUnit(1, tankDefIdx, new Vector2(20f + i * 4f, 20f), tankDef.Speed);
            sim.World.SetFireStance(p1Units[i], FireStance.HoldFire);
        }

        EntityId[] p2Units = new EntityId[3];
        for (int i = 0; i < 3; i++)
        {
            p2Units[i] = sim.World.CreateUnit(2, tankDefIdx, new Vector2(60f + i * 4f, 60f), tankDef.Speed);
            sim.World.SetFireStance(p2Units[i], FireStance.HoldFire);
        }

        Vector2 p1Target = new(30f, 30f);
        Vector2 p2Target = new(80f, 80f);

        GroupMoveCommand p1Cmd = new(101, sim.Tick, p1Units, p1Target);
        GroupMoveCommand p2Cmd = new(102, sim.Tick, p2Units, p2Target);

        Assert.True(sim.TryEnqueue(1, p1Cmd));
        Assert.True(sim.TryEnqueue(2, p2Cmd));

        // Step simulation: processes both player commands in the same tick
        sim.Step();

        Assert.True(sim.TryPopGroupMoveResult(1, out GroupMoveResult p1Result));
        Assert.True(sim.TryPopGroupMoveResult(2, out GroupMoveResult p2Result));

        Assert.Equal(p1Target, p1Result.Target);
        Assert.Equal(3, p1Result.Placed.Length);
        for (int i = 0; i < p1Result.Placed.Length; i++)
        {
            GroupMoveAssignment a = p1Result.Placed.Span[i];
            Assert.Contains(a.Unit, p1Units);
            Assert.True(Vector2.Distance(a.Target, p1Target) < 15f);
        }

        Assert.Equal(p2Target, p2Result.Target);
        Assert.Equal(3, p2Result.Placed.Length);
        for (int i = 0; i < p2Result.Placed.Length; i++)
        {
            GroupMoveAssignment a = p2Result.Placed.Span[i];
            Assert.Contains(a.Unit, p2Units);
            Assert.True(Vector2.Distance(a.Target, p2Target) < 15f);
        }

        // Save assignments snapshot from player 1
        GroupMoveAssignment[] p1Snapshot = p1Result.Placed.ToArray();

        // Enqueue another group command in subsequent tick
        Vector2 p1NextTarget = new(45f, 45f);
        GroupMoveCommand p1NextCmd = new(103, sim.Tick, p1Units, p1NextTarget);
        Assert.True(sim.TryEnqueue(1, p1NextCmd));
        sim.Step();

        // Verify the original result memory was not mutated or overwritten
        for (int i = 0; i < p1Snapshot.Length; i++)
        {
            Assert.Equal(p1Snapshot[i].Unit, p1Result.Placed.Span[i].Unit);
            Assert.Equal(p1Snapshot[i].Target, p1Result.Placed.Span[i].Target);
        }
    }

    [Fact]
    public void Test_GroupMoveToBridge_VehiclesArriveOnBridgeDeckWithSupportSurfaceOne()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        Simulation sim = CreateEmptySimulation(geometry, catalog);
        ushort tankDefIdx = catalog.RequireDefinitionIndex("prototype_tank");
        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");

        // Two tanks start on west bank ground (X=50, Z in [98, 102], SupportSurface = 0)
        EntityId tank1 = sim.World.CreateUnit(1, tankDefIdx, new Vector2(50f, 98f), tankDef.Speed);
        EntityId tank2 = sim.World.CreateUnit(1, tankDefIdx, new Vector2(50f, 102f), tankDef.Speed);
        sim.World.SetFireStance(tank1, FireStance.HoldFire);
        sim.World.SetFireStance(tank2, FireStance.HoldFire);

        Assert.True(sim.World.TryGetUnit(tank1, out UnitState init1));
        Assert.True(sim.World.TryGetUnit(tank2, out UnitState init2));
        Assert.Equal(0u, init1.SupportSurface);
        Assert.Equal(0u, init2.SupportSurface);

        // Group move order targeting the bridge deck (surface 1)
        Vector2 bridgeGoal = new(100f, 100f);
        GroupMoveCommand cmd = new(
            Sequence: 1,
            ClientTick: sim.Tick,
            Units: new EntityId[] { tank1, tank2 },
            Target: bridgeGoal,
            TargetSurface: 1u,
            FinalFacing: null,
            Mode: MoveMode.Forward);

        Assert.True(sim.TryEnqueue(1, cmd));

        // Advance simulation until both vehicles arrive on the bridge deck
        bool bothArrived = false;
        for (int step = 0; step < 400; step++)
        {
            sim.Step();
            if (sim.World.TryGetUnit(tank1, out UnitState u1) && sim.World.TryGetUnit(tank2, out UnitState u2))
            {
                if (u1.SupportSurface == 1u && u2.SupportSurface == 1u &&
                    Vector2.Distance(u1.Position, bridgeGoal) < 15f &&
                    Vector2.Distance(u2.Position, bridgeGoal) < 15f &&
                    !u1.Moving && !u2.Moving)
                {
                    bothArrived = true;
                    break;
                }
            }
        }

        Assert.True(bothArrived, "Both tanks must arrive on bridge deck.");
        Assert.True(sim.World.TryGetUnit(tank1, out UnitState final1));
        Assert.True(sim.World.TryGetUnit(tank2, out UnitState final2));

        Assert.Equal(1u, final1.SupportSurface);
        Assert.Equal(1u, final2.SupportSurface);
        Assert.True(geometry.GetSurfaceHeight(final1.Position, final1.SupportSurface) >= 3.9f);
        Assert.True(geometry.GetSurfaceHeight(final2.Position, final2.SupportSurface) >= 3.9f);
    }
}
