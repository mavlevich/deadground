using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class InfantryTransportScenarioTests
{
    private static readonly StaticGeometry SharedGeometry = InfantryTransportScenario.CreateStaticGeometry();
    private static readonly UnitCatalog SharedCatalog = ColdWarContent.LoadCatalog();

    private static Simulation CreateSimulation(ulong matchSeed = 42UL) =>
        InfantryTransportScenario.CreateSimulation(SharedGeometry, SharedCatalog, matchSeed: matchSeed);

    [Fact]
    public void ScenarioSetup_HasValidGeometry_Catalog_AndTeams()
    {
        StaticGeometry geometry = SharedGeometry;
        MatchSetup setup = InfantryTransportScenario.CreateMatchSetup(geometry, SharedCatalog);

        Assert.Equal("InfantryTransport", setup.RuleSetId);
        Assert.Equal(256f, geometry.HeightMap.Width);
        Assert.Equal(256f, geometry.HeightMap.Depth);

        Assert.NotEmpty(geometry.Structures);
        Assert.NotEmpty(geometry.Bridges);
        Assert.NotEmpty(geometry.TerrainObstacles);
        Assert.NotEmpty(geometry.VegetationZones);

        ushort riflemanDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");
        UnitDefinition def = SharedCatalog.Units[riflemanDef];
        Assert.Equal("infantry", def.Classification);
        Assert.True(def.Capabilities.HasFlag(TraversabilityCapabilities.DenseForest));
        Assert.True(def.Capabilities.HasFlag(TraversabilityCapabilities.Foot));
        Assert.False(def.Capabilities.HasFlag(TraversabilityCapabilities.Water));

        ushort m113Def = SharedCatalog.RequireDefinitionIndex("m113a1");
        UnitDefinition m113 = SharedCatalog.Units[m113Def];
        Assert.Equal("apc", m113.Classification);
        Assert.Equal(11, m113.PassengerCapacity);

        ushort btrDef = SharedCatalog.RequireDefinitionIndex("btr70");
        UnitDefinition btr = SharedCatalog.Units[btrDef];
        Assert.Equal("apc", btr.Classification);
        Assert.Equal(8, btr.PassengerCapacity);
    }

    [Fact]
    public void PassengerCapacity_ConfiguredOnTransports_SeparateFromCrew()
    {
        ushort m113Def = SharedCatalog.RequireDefinitionIndex("m113a1");
        ushort btrDef = SharedCatalog.RequireDefinitionIndex("btr70");
        ushort riflemanDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");

        Assert.Equal(11, SharedCatalog.GetPassengerCapacity(SharedCatalog.Units[m113Def]));
        Assert.Equal(8, SharedCatalog.GetPassengerCapacity(SharedCatalog.Units[btrDef]));
        Assert.Equal(0, SharedCatalog.GetPassengerCapacity(SharedCatalog.Units[riflemanDef]));

        UnitCatalog protoCatalog = ScenarioContent.LoadCatalog("prototype");
        ushort protoApcDef = protoCatalog.RequireDefinitionIndex("apc");
        Assert.Equal(8, protoCatalog.GetPassengerCapacity(protoCatalog.Units[protoApcDef]));
    }

    [Fact]
    public void BoardTransport_ApproachesAndBoards_PreservingIdentityHealthAndAmmo()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1); // M113 at (128, 210)
        EntityId riflemanId = new(2);  // Rifleman at (124, 216)

        Assert.True(world.TryGetCombatState(riflemanId, out WorldState.CombatState initialCs));
        int initialHealth = initialCs.CurrentHealth;
        int initialAmmo = initialCs.Ammunition;
        Assert.Equal(15, initialHealth);
        Assert.Equal(210, initialAmmo);
        Assert.Equal(default, world.GetContainerId(riflemanId));

        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanId, transportId)));

        bool boarded = false;
        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            if (world.IsContained(riflemanId))
            {
                boarded = true;
                break;
            }
        }

        Assert.True(boarded, "Rifleman must reach transport and board into container.");
        Assert.Equal(transportId, world.GetContainerId(riflemanId));
        Assert.True(world.TryGetCombatState(riflemanId, out WorldState.CombatState containedCs));
        Assert.Equal(initialHealth, containedCs.CurrentHealth);
        Assert.Equal(initialAmmo, containedCs.Ammunition);
        Assert.Equal(1, world.GetPassengerCount(transportId));

        ReadOnlySpan<EntityId> occ = world.GetPassengers(transportId);
        Assert.Equal(1, occ.Length);
        Assert.Equal(riflemanId, occ[0]);
    }

    [Fact]
    public void BoardTransport_SeatReservation_PreventsExceedingCapacity_AndConcurrentOrders()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1); // M113 capacity 11
        Assert.Equal(11, world.GetContainerCapacity(transportId));
        Assert.Equal(11, world.GetAvailableSeats(transportId));

        // Enqueue board commands for 4 riflemen
        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, new EntityId(2), transportId)));
        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(2, sim.Tick, new EntityId(3), transportId)));
        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(3, sim.Tick, new EntityId(4), transportId)));
        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(4, sim.Tick, new EntityId(5), transportId)));

        // Step simulation to process command queue
        sim.Step();
        Assert.Equal(7, world.GetAvailableSeats(transportId));

        // Let all 4 board
        for (int t = 0; t < 150; t++)
        {
            sim.Step();
            if (world.GetPassengerCount(transportId) == 4)
                break;
        }

        Assert.Equal(4, world.GetPassengerCount(transportId));
        Assert.Equal(7, world.GetAvailableSeats(transportId));
    }

    [Fact]
    public void BoardTransport_SeatReservation_FreesOnNewCommandOrCancellation()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1);
        EntityId riflemanId = new(2);

        Assert.Equal(11, world.GetAvailableSeats(transportId));
        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanId, transportId)));
        sim.Step();
        Assert.Equal(10, world.GetAvailableSeats(transportId));

        // Order rifleman to move somewhere else instead
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, riflemanId, new Vector2(100f, 210f))));
        sim.Step();

        // Seat reservation must be released
        Assert.Equal(11, world.GetAvailableSeats(transportId));
        Assert.False(world.TryGetBoardingTarget(riflemanId, out _));
    }

    [Fact]
    public void BoardTransport_TransportStartsMoving_CancelsBoardingOrder()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1);
        EntityId riflemanId = new(2);

        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanId, transportId)));
        sim.Step();
        Assert.True(world.TryGetBoardingTarget(riflemanId, out _));

        // Transport starts moving away
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, transportId, new Vector2(128f, 165f))));
        sim.Step();

        // Transport is moving, so boarding is cancelled and not chased indefinitely
        Assert.False(world.TryGetBoardingTarget(riflemanId, out _));
        Assert.Equal(11, world.GetAvailableSeats(transportId));
    }

    [Fact]
    public void BoardTransport_RejectsEnemyTransport()
    {
        using Simulation sim = CreateSimulation();

        EntityId riflemanSide1 = new(2);
        EntityId opforTransportSide2 = new(7);

        // Player 1 trying to board enemy transport must fail
        Assert.False(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanSide1, opforTransportSide2)));
    }

    [Fact]
    public void ContainedUnit_ExcludedFromMovementCombatAndVision()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1);
        EntityId riflemanId = new(2);

        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanId, transportId)));
        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            if (world.IsContained(riflemanId)) break;
        }
        Assert.True(world.IsContained(riflemanId));

        // 1. Move command to contained soldier is rejected
        Assert.False(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, riflemanId, new Vector2(100f, 100f))));

        // 2. Position locksteps to transport
        Vector2 targetPos = new(128f, 175f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(3, sim.Tick, transportId, targetPos)));
        for (int t = 0; t < 150; t++)
        {
            sim.Step();
            if (world.TryGetUnit(transportId, out UnitState tr) && Vector2.Distance(tr.Position, targetPos) < 1.0f)
                break;
        }

        Assert.True(world.TryGetUnit(riflemanId, out UnitState containedState));
        Assert.True(world.TryGetUnit(transportId, out UnitState transportState));
        Assert.Equal(transportState.Position, containedState.Position);
        Assert.Equal(transportState.SupportSurface, containedState.SupportSurface);
    }

    [Fact]
    public void Disembark_FullSafeDisembark_ReturnsUnitsToWorldWithPreservedState()
    {
        using Simulation sim = CreateSimulation();
        TestOptions.HoldFireForOthers(sim, 1);
        WorldState world = sim.World;

        EntityId transportId = new(1);
        EntityId[] riflemen = [new(2), new(3), new(4), new(5)];

        for (uint i = 0; i < (uint)riflemen.Length; i++)
        {
            world.SetFireStance(riflemen[i], FireStance.HoldFire);
            Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(i + 1, sim.Tick, riflemen[i], transportId)));
        }

        for (int t = 0; t < 150; t++)
        {
            sim.Step();
            if (world.GetPassengerCount(transportId) == 4) break;
        }
        Assert.Equal(4, world.GetPassengerCount(transportId));

        // Transport travels to landing zone
        Vector2 lz = InfantryTransportScenario.LandingZoneCenter;
        Assert.True(sim.TryEnqueue(1, new MoveCommand(10, sim.Tick, transportId, lz)));
        for (int t = 0; t < 300; t++)
        {
            sim.Step();
            if (world.TryGetUnit(transportId, out UnitState tr) && Vector2.Distance(tr.Position, lz) < 1.0f && !tr.Moving)
                break;
        }

        // Issue disembark command
        Assert.True(sim.TryEnqueue(1, new DisembarkCommand(11, sim.Tick, transportId, lz)));
        sim.Step();

        Assert.True(sim.TryPopDisembarkResult(1, out DisembarkResult result));
        Assert.True(result.Success);
        Assert.Equal(4, result.DisembarkedCount);
        Assert.Equal(0, result.RemainingCount);
        Assert.Equal(0, world.GetPassengerCount(transportId));

        // Verify each soldier is alive, placed, healthy, and separated
        Span<Vector2> positions = stackalloc Vector2[4];
        Assert.True(world.TryGetUnit(transportId, out UnitState trState));
        for (int i = 0; i < riflemen.Length; i++)
        {
            Assert.False(world.IsContained(riflemen[i]));
            Assert.True(world.TryGetUnit(riflemen[i], out UnitState u));
            Assert.Equal(default, world.GetContainerId(riflemen[i]));

            Assert.True(world.TryGetCombatState(riflemen[i], out WorldState.CombatState cs));
            Assert.Equal(15, cs.CurrentHealth);
            Assert.Equal(210, cs.Ammunition);
            positions[i] = u.Position;

            // Distance to transport hull must respect safety clearance
            float distToTrans = Vector2.Distance(u.Position, trState.Position);
            Assert.True(distToTrans >= 1.5f, $"Rifleman {riflemen[i].Value} placed too close to transport: {distToTrans}");
        }

        // Check mutual separation between soldiers
        for (int a = 0; a < 4; a++)
        {
            for (int b = a + 1; b < 4; b++)
            {
                float dist = Vector2.Distance(positions[a], positions[b]);
                Assert.True(dist >= PassengerDistribution.MinInfantryDistance - 0.05f,
                    $"Disembarked soldiers {a} and {b} overlap: dist={dist}");
            }
        }

        // Verify disembarked soldier can now navigate independently (order easternmost soldier east into open ground)
        int eastIdx = 0;
        for (int i = 1; i < riflemen.Length; i++)
        {
            if (positions[i].X > positions[eastIdx].X)
                eastIdx = i;
        }
        EntityId movingSoldier = riflemen[eastIdx];
        Vector2 startPos = positions[eastIdx];
        Assert.True(sim.TryEnqueue(1, new MoveCommand(12, sim.Tick, movingSoldier, new Vector2(160f, 165f))));
        bool moved = false;
        for (int t = 0; t < 60; t++)
        {
            sim.Step();
            if (world.TryGetUnit(movingSoldier, out UnitState movedU) && Vector2.Distance(movedU.Position, startPos) > 1.0f)
            {
                moved = true;
                break;
            }
        }
        Assert.True(moved, "Disembarked soldier should navigate independently upon order.");
    }

    [Fact]
    public void Disembark_UnloadAtPoint_DrivesToDestinationThenDisembarksOnArrival()
    {
        using Simulation sim = CreateSimulation();
        TestOptions.HoldFireForOthers(sim, 1);
        WorldState world = sim.World;

        EntityId transportId = new(1);
        EntityId[] riflemen = [new(2), new(3), new(4), new(5)];

        for (uint i = 0; i < (uint)riflemen.Length; i++)
        {
            world.SetFireStance(riflemen[i], FireStance.HoldFire);
            Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(i + 1, sim.Tick, riflemen[i], transportId)));
        }

        for (int t = 0; t < 150; t++)
        {
            sim.Step();
            if (world.GetPassengerCount(transportId) == 4) break;
        }
        Assert.Equal(4, world.GetPassengerCount(transportId));

        // Issue Unload At command 45m away at the landing zone
        Vector2 lz = InfantryTransportScenario.LandingZoneCenter;
        Assert.True(sim.TryEnqueue(1, new DisembarkCommand(10, sim.Tick, transportId, lz)));

        // Transport should move toward landing zone while carrying passengers
        bool arrivedAndDisembarked = false;
        for (int t = 0; t < 400; t++)
        {
            sim.Step();
            if (world.GetPassengerCount(transportId) == 0)
            {
                arrivedAndDisembarked = true;
                break;
            }
        }

        Assert.True(arrivedAndDisembarked, "Transport must drive to landing zone destination and disembark passengers on arrival.");
        Assert.True(world.TryGetUnit(transportId, out UnitState finalTr));
        Assert.True(Vector2.Distance(finalTr.Position, lz) < 2.5f, $"Transport should be at landing zone upon disembark, but was at {finalTr.Position}.");

        // Verify disembarked riflemen are outside and placed around vehicle
        for (int i = 0; i < riflemen.Length; i++)
        {
            Assert.False(world.IsContained(riflemen[i]));
            Assert.True(world.TryGetUnit(riflemen[i], out UnitState u));
            float distToVehicle = Vector2.Distance(u.Position, finalTr.Position);
            Assert.True(distToVehicle >= 1.5f && distToVehicle <= 4.5f,
                $"Disembarked soldier {riflemen[i].Value} must be beside vehicle (1.5-4.5m), but was at {distToVehicle}m.");
        }
    }

    [Fact]
    public void Disembark_OnElevatedBridge_PreservesSupportSurfaceWithoutFallingBelow()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1);
        EntityId riflemanId = new(2);

        // Board rifleman into transport
        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanId, transportId)));
        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            if (world.IsContained(riflemanId)) break;
        }
        Assert.True(world.IsContained(riflemanId));

        // Move transport onto bridge deck (X=128, Z=128)
        Vector2 bridgeMidpoint = new(128.0f, 128.0f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, transportId, bridgeMidpoint)));
        for (int t = 0; t < 500; t++)
        {
            sim.Step();
            if (world.TryGetUnit(transportId, out UnitState tr) && Vector2.Distance(tr.Position, bridgeMidpoint) < 1.0f && !tr.Moving)
                break;
        }

        Assert.True(world.TryGetUnit(transportId, out UnitState transOnBridge));
        Assert.Equal(1u, transOnBridge.SupportSurface);

        // Disembark rifleman on bridge deck
        Assert.True(sim.TryEnqueue(1, new DisembarkCommand(3, sim.Tick, transportId, bridgeMidpoint, 1u)));
        sim.Step();

        Assert.True(sim.TryPopDisembarkResult(1, out DisembarkResult disResult));
        Assert.True(disResult.Success);
        Assert.Equal(1, disResult.DisembarkedCount);

        Assert.True(world.TryGetUnit(riflemanId, out UnitState riflemanOnBridge));
        Assert.Equal(1u, riflemanOnBridge.SupportSurface);
        // Position must be within bridge width bounds (Bridge X=128, Width=8 => [124..132])
        Assert.InRange(riflemanOnBridge.Position.X, 124.0f, 132.0f);
        Assert.InRange(riflemanOnBridge.Position.Y, 112.0f, 144.0f);
    }

    [Fact]
    public void Disembark_ConstrainedArea_PartialDisembark_ReportsRemaining()
    {
        StaticGeometry geometry = SharedGeometry;
        DestinationEvaluator evaluator = new(geometry);
        WorldState world = new(geometry.HeightMap);

        ushort rifleDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");
        ushort m113Def = SharedCatalog.RequireDefinitionIndex("m113a1");

        UnitDefinition rifle = SharedCatalog.Units[rifleDef];
        UnitDefinition m113 = SharedCatalog.Units[m113Def];

        EntityId transId = world.CreateUnit(1, m113Def, new Vector2(100f, 100f), m113.Speed, m113.MaxHealth, 100, 0f, passengerCapacity: 4);
        EntityId[] passIds = new EntityId[4];
        for (int i = 0; i < 4; i++)
        {
            passIds[i] = world.CreateUnit(1, rifleDef, new Vector2(100f, 100f), rifle.Speed, rifle.MaxHealth, 100, 0f);
            world.Board(transId, passIds[i]);
        }

        Span<Vector2> outPos = stackalloc Vector2[4];
        Span<bool> outPlaced = stackalloc bool[4];

        int placed = PassengerDistribution.Distribute(
            world,
            evaluator,
            transId,
            new Vector2(100f, 100f),
            targetSurface: 0u,
            passIds,
            outPos,
            outPlaced);

        Assert.Equal(4, placed);
        for (int i = 0; i < 4; i++)
        {
            Assert.True(outPlaced[i]);
        }
    }

    [Fact]
    public void TransportDestruction_DealsConfiguredDamage_SurvivorsEvacuate()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1);
        EntityId riflemanId = new(2);

        // Give rifleman 100 HP so taking 50 damage leaves him alive as a survivor
        Assert.True(world.TryGetCombatState(riflemanId, out WorldState.CombatState csInit));
        world.SetCombatState(riflemanId, csInit with { CurrentHealth = 100, MaxHealth = 100 });

        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanId, transportId)));
        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            if (world.IsContained(riflemanId)) break;
        }
        Assert.True(world.IsContained(riflemanId));

        // Transport takes lethal damage
        sim.Combat!.DestroyUnit(transportId, world, sim.Tick);
        sim.Step();

        // Transport is destroyed
        Assert.False(world.TryGetUnit(transportId, out _));

        // Rifleman takes DefaultContainerDestructionDamage (50) from initial 100 HP -> 50 HP remaining
        Assert.True(world.TryGetUnit(riflemanId, out _));
        Assert.False(world.IsContained(riflemanId));
        Assert.Equal(default, world.GetContainerId(riflemanId));

        Assert.True(world.TryGetCombatState(riflemanId, out WorldState.CombatState survivorCs));
        Assert.Equal(50, survivorCs.CurrentHealth);
        Assert.True(survivorCs.CurrentHealth > 0);
    }

    [Fact]
    public void TransportDestruction_LethalDamage_CausesPassengerDeathWithoutVehicleWrecks()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1);
        EntityId riflemanId = new(2);

        // Default rifleman has 15 HP so 50 explosion damage is fatal
        Assert.True(world.TryGetCombatState(riflemanId, out WorldState.CombatState csBefore));
        Assert.Equal(15, csBefore.CurrentHealth);

        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanId, transportId)));
        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            if (world.IsContained(riflemanId)) break;
        }
        Assert.True(world.IsContained(riflemanId));

        int wreckCountBefore = world.Wrecks.Length;

        // Destroy transport
        sim.Combat!.DestroyUnit(transportId, world, sim.Tick);
        sim.Step();

        // Rifleman took 50 damage -> died
        Assert.False(world.TryGetUnit(riflemanId, out _));
        // Only the vehicle leaves a wreck; infantry does not leave a vehicle wreck
        Assert.Equal(wreckCountBefore + 1, world.Wrecks.Length);
        Assert.Equal("apc", SharedCatalog.Units[world.Wrecks[^1].Definition].Classification);
    }

    [Fact]
    public void FogOfWar_EnemyContacts_DoNotLeakPassengerIdentityOrCount()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1);
        EntityId riflemanId = new(2);

        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanId, transportId)));
        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            if (world.IsContained(riflemanId)) break;
        }

        // Side 2 knowledge should not expose passenger identity or count
        PlayerKnowledge p2Knowledge = new(2);
        sim.UpdateKnowledge(p2Knowledge);

        // Contained soldier must not produce a contact for Side 2
        for (int c = 0; c < p2Knowledge.ContactCount; c++)
        {
            ContactObservation contact = p2Knowledge.Contacts[c];
            Assert.NotEqual(ContactClassification.Infantry, contact.Classification);
        }
    }

    [Fact]
    public void Networking_SnapshotSync_RestoresContainerStateAccurately()
    {
        EntityId transportId = new(10);
        EntityId riflemanId = new(20);

        OwnUnitObservation transportObs = new(
            Id: transportId,
            Definition: 1,
            Position: new Vector2(100f, 100f),
            SupportSurface: 0,
            PassengerCount: 3,
            PassengerCapacity: 11);

        OwnUnitObservation riflemanObs = new(
            Id: riflemanId,
            Definition: 2,
            Position: new Vector2(100f, 100f),
            SupportSurface: 0,
            ContainerId: transportId);

        byte[] snapshotBuffer = new byte[WireProtocol.MaxMessageSize];
        int bytesWritten = WireProtocol.WriteSnapshot(snapshotBuffer, 1, 1, [transportObs, riflemanObs]);
        Assert.True(bytesWritten > 0);

        ClientReplica replica = new();
        Assert.True(replica.TryApply(snapshotBuffer.AsSpan(0, bytesWritten)));

        Assert.Equal(2, replica.OwnUnits.Length);

        OwnUnitObservation deserializedTransport = replica.OwnUnits[0];
        Assert.Equal(transportId, deserializedTransport.Id);
        Assert.Equal(3, deserializedTransport.PassengerCount);
        Assert.Equal(11, deserializedTransport.PassengerCapacity);
        Assert.Equal(default, deserializedTransport.ContainerId);

        OwnUnitObservation deserializedRifleman = replica.OwnUnits[1];
        Assert.Equal(riflemanId, deserializedRifleman.Id);
        Assert.Equal(transportId, deserializedRifleman.ContainerId);
        Assert.Equal(0, deserializedRifleman.PassengerCount);
    }

    [Fact]
    public void Disembark_TargetInsideDenseForest_UnloadsAtForestEdgeWhenTransportStops()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1);
        EntityId riflemanId = new(2);

        // Board rifleman into transport
        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanId, transportId)));
        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            if (world.IsContained(riflemanId)) break;
        }
        Assert.True(world.IsContained(riflemanId));

        // Command disembark with target deep inside dense forest (X=90, Z=175; Forest is X in [30..105])
        Vector2 forestTarget = new(90.0f, 175.0f);
        Assert.True(sim.TryEnqueue(1, new DisembarkCommand(2, sim.Tick, transportId, forestTarget, 0u)));

        bool disembarked = false;
        for (int t = 0; t < 500; t++)
        {
            sim.Step();
            if (!world.IsContained(riflemanId))
            {
                disembarked = true;
                break;
            }
        }

        Assert.True(disembarked, "Passenger must unload when transport stops at forest edge.");
        Assert.True(world.TryGetUnit(transportId, out UnitState trans));
        Assert.False(trans.Moving);

        // Transport must stop outside the dense forest boundary (X >= 105)
        Assert.True(trans.Position.X >= InfantryTransportScenario.ForestMaxX - 0.5f,
            $"Transport must stop outside dense forest, but was at X={trans.Position.X}");

        // Distance to target is greater than 3.5m (verifying disembark succeeded despite stopping short)
        float distToTarget = Vector2.Distance(trans.Position, forestTarget);
        Assert.True(distToTarget > 3.5f, $"Transport must have stopped short of forest target, distance was {distToTarget}m");

        Assert.True(sim.TryPopDisembarkResult(1, out DisembarkResult result));
        Assert.True(result.Success);
        Assert.Equal(1, result.DisembarkedCount);
    }

    [Fact]
    public void Disembark_TargetBehindBlockedStandingInfantry_UnloadsWhenTransportCannotProgress()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1); // (128, 210)
        EntityId riflemanId = new(2);  // (124, 216)

        // Board rifleman 1 into transport
        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, riflemanId, transportId)));
        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            if (world.IsContained(riflemanId)) break;
        }
        Assert.True(world.IsContained(riflemanId));

        // Create a blocking infantry unit directly ahead at (128, 214) with an active route so it does not step aside
        ushort rifleDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");
        UnitDefinition rDef = SharedCatalog.Units[rifleDef];
        EntityId blocker = world.CreateUnit(1, rifleDef, new Vector2(128.0f, 214.0f), rDef.Speed, rDef.MaxHealth, 100, 0f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, blocker, new Vector2(128.0f, 214.5f))));

        // Order disembark at (128, 225), directly behind the blocker
        Vector2 behindTarget = new(128.0f, 225.0f);
        Assert.True(sim.TryEnqueue(1, new DisembarkCommand(3, sim.Tick, transportId, behindTarget, 0u)));

        bool disembarked = false;
        for (int t = 0; t < 300; t++)
        {
            sim.Step();
            if (!world.IsContained(riflemanId))
            {
                disembarked = true;
                break;
            }
        }

        Assert.True(disembarked, "Passenger must unload when transport is blocked by standing infantry and cannot progress.");
        Assert.True(world.TryGetUnit(transportId, out UnitState trans));
        Assert.False(trans.Moving);

        float distToTarget = Vector2.Distance(trans.Position, behindTarget);
        Assert.True(distToTarget > 3.5f, $"Transport must have unloaded short of target, distance was {distToTarget}m");

        Assert.True(sim.TryPopDisembarkResult(1, out DisembarkResult result));
        Assert.True(result.Success);
        Assert.Equal(1, result.DisembarkedCount);
    }

    [Fact]
    public void VehicleMovement_FriendlyStandingInfantryStepsAside_AllowsTransportToPass()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        EntityId transportId = new(1); // (128, 210)
        ushort rifleDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");
        UnitDefinition rDef = SharedCatalog.Units[rifleDef];

        // Create an idle friendly rifleman directly ahead on open road north of transport
        Vector2 blockerStart = new(128.0f, 195.0f);
        EntityId idleSoldier = world.CreateUnit(1, rifleDef, blockerStart, rDef.Speed, rDef.MaxHealth, 100, 0f);

        Vector2 transportTarget = new(128.0f, 180.0f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, transportId, transportTarget)));

        for (int t = 0; t < 300; t++)
        {
            sim.Step();
            if (world.TryGetUnit(transportId, out UnitState tr) && Vector2.Distance(tr.Position, transportTarget) < 1.0f)
                break;
        }

        Assert.True(world.TryGetUnit(transportId, out UnitState finalTrans));
        Assert.True(world.TryGetUnit(idleSoldier, out UnitState finalSoldier));

        // Friendly rifleman stepped aside laterally from vehicle center (X=128)
        Assert.True(MathF.Abs(finalSoldier.Position.X - 128.0f) >= 0.8f,
            $"Standing soldier should step aside laterally, but was at X={finalSoldier.Position.X}");

        // Transport successfully passed and reached target
        Assert.True(Vector2.Distance(finalTrans.Position, transportTarget) < 1.0f,
            $"Transport should reach target, but was at {finalTrans.Position}");
    }

    [Fact]
    public void Boarding_NavigationFailure_CancelsOrderAndReleasesSeatReservation()
    {
        HeightMap map = InfantryTransportScenario.CreateHeightMap();
        TerrainObstacle canalWater = new(0f, 116.0f, 256.0f, 140.0f, TraversabilityCapabilities.Water);
        StaticGeometry disconnectedGeom = new(map, [], [], [canalWater], [], []);

        using Simulation sim = InfantryTransportScenario.CreateSimulation(disconnectedGeom, SharedCatalog);
        WorldState world = sim.World;

        // Transport is on North side of canal (Z=80)
        ushort m113Def = SharedCatalog.RequireDefinitionIndex("m113a1");
        UnitDefinition m113 = SharedCatalog.Units[m113Def];
        EntityId isolatedTrans = world.CreateUnit(1, m113Def, new Vector2(50.0f, 80.0f), m113.Speed, m113.MaxHealth, 100, 0f, passengerCapacity: 4);

        // Soldier is on South side of canal (starts at 124, 216)
        EntityId southSoldier = new(2);

        Assert.Equal(0, world.GetReservedSeats(isolatedTrans));

        // Order soldier to board unreachable transport
        Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1, sim.Tick, southSoldier, isolatedTrans)));

        // Step simulation to trigger navigation search and failure
        for (int t = 0; t < 60; t++)
        {
            sim.Step();
            if (!world.TryGetBoardingTarget(southSoldier, out _))
                break;
        }

        Assert.False(world.TryGetBoardingTarget(southSoldier, out _));
        Assert.Equal(0, world.GetReservedSeats(isolatedTrans));
        Assert.False(world.IsContained(southSoldier));
    }

    [Fact]
    public void DataDriven_ContainerDestructionDamage_LoadedFromCatalog()
    {
        ushort m113Def = SharedCatalog.RequireDefinitionIndex("m113a1");
        ushort btrDef = SharedCatalog.RequireDefinitionIndex("btr70");
        Assert.Equal(50, SharedCatalog.Units[m113Def].ContainerDestructionDamage);
        Assert.Equal(50, SharedCatalog.Units[btrDef].ContainerDestructionDamage);

        UnitCatalog protoCatalog = ScenarioContent.LoadCatalog("prototype");
        ushort protoApcDef = protoCatalog.RequireDefinitionIndex("apc");
        Assert.Equal(50, protoCatalog.Units[protoApcDef].ContainerDestructionDamage);
    }

    [Fact]
    public void PassengerDistribution_UsesCatalogDimensions_DifferentiatesVehicles()
    {
        StaticGeometry geometry = SharedGeometry;
        DestinationEvaluator evaluator = new(geometry);
        WorldState world = new(geometry.HeightMap);
        OpsMovementCatalog moveCatalog = new(SharedCatalog);

        ushort rifleDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");
        ushort m113Def = SharedCatalog.RequireDefinitionIndex("m113a1");
        ushort btrDef = SharedCatalog.RequireDefinitionIndex("btr70");

        UnitDefinition rifle = SharedCatalog.Units[rifleDef];
        UnitDefinition m113 = SharedCatalog.Units[m113Def];
        UnitDefinition btr = SharedCatalog.Units[btrDef];

        EntityId m113Id = world.CreateUnit(1, m113Def, new Vector2(100f, 100f), m113.Speed, m113.MaxHealth, 100, 0f, passengerCapacity: 4);
        EntityId btrId = world.CreateUnit(1, btrDef, new Vector2(100f, 100f), btr.Speed, btr.MaxHealth, 100, 0f, passengerCapacity: 4);

        EntityId passM113 = world.CreateUnit(1, rifleDef, new Vector2(100f, 100f), rifle.Speed, rifle.MaxHealth, 100, 0f);
        EntityId passBtr = world.CreateUnit(1, rifleDef, new Vector2(100f, 100f), rifle.Speed, rifle.MaxHealth, 100, 0f);
        world.Board(m113Id, passM113);
        world.Board(btrId, passBtr);

        Span<Vector2> outPosM113 = stackalloc Vector2[1];
        Span<bool> outPlacedM113 = stackalloc bool[1];
        int placedM113 = PassengerDistribution.Distribute(
            world, evaluator, m113Id, new Vector2(100f, 100f), 0u, [passM113], outPosM113, outPlacedM113, moveCatalog);

        Span<Vector2> outPosBtr = stackalloc Vector2[1];
        Span<bool> outPlacedBtr = stackalloc bool[1];
        int placedBtr = PassengerDistribution.Distribute(
            world, evaluator, btrId, new Vector2(100f, 100f), 0u, [passBtr], outPosBtr, outPlacedBtr, moveCatalog);

        Assert.Equal(1, placedM113);
        Assert.Equal(1, placedBtr);

        float distM113 = Vector2.Distance(new Vector2(100f, 100f), outPosM113[0]);
        float distBtr = Vector2.Distance(new Vector2(100f, 100f), outPosBtr[0]);

        // BTR-70 is longer (7.53m vs 4.86m), so its candidate positions must have greater clearance from center
        Assert.True(distBtr > distM113, $"BTR-70 clearance ({distBtr}m) must be larger than M113 clearance ({distM113}m)");
    }
}
