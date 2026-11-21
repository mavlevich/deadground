using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class HelicopterCargoTests
{
    private const ushort DefHeli = 0;
    private const ushort DefInfantry = 1;
    private const ushort DefJeep = 2;
    private const ushort DefApc = 3;
    private const ushort DefTank = 4;

    private static FlightProfile CargoHeliProfile => new(
        CruiseSpeed: 68f,
        LowAltitudeSpeed: 42f,
        Acceleration: 12f,
        Deceleration: 14f,
        YawRateDegPerSec: 55f,
        ClimbRate: 9f,
        DescentRate: 7f,
        LowAltitude: 18f,
        HighAltitude: 55f,
        LandingClearanceRadius: 10f,
        MaxLandingSlopeDegrees: 12f,
        CrashDescentRate: 18f,
        CrashSpinRateDegPerSec: 180f,
        CrashSplashRadius: 12f,
        CrashSplashDamage: 60,
        StartAirborne: false,
        CanLoadInternalVehicles: true,
        CanFastRope: true,
        FastRopeAltitude: 18f,
        FastRopeIntervalTicks: 8,
        MaxSlingWeightKg: 12700f,
        SlingCableLength: 10f,
        SlingSpeedPenaltyAtMax: 0.45f);

    private sealed class CargoFlightCatalog : IUnitFlightCatalog
    {
        private readonly FlightProfile _profile;
        public CargoFlightCatalog(FlightProfile profile) => _profile = profile;

        public bool TryGetFlightProfile(ushort definition, out FlightProfile profile)
        {
            if (definition == DefHeli)
            {
                profile = _profile;
                return true;
            }
            profile = default;
            return false;
        }

        public bool TryGetCargoProfile(ushort definition, out CargoProfile cargo)
        {
            cargo = definition switch
            {
                DefInfantry => new CargoProfile(100f, 1, CanInternalLoad: true, CanSlingLoad: false, IsInfantry: true),
                DefJeep => new CargoProfile(1100f, 8, CanInternalLoad: true, CanSlingLoad: true, IsInfantry: false),
                DefApc => new CargoProfile(11200f, 0, CanInternalLoad: false, CanSlingLoad: true, IsInfantry: false),
                DefTank => new CargoProfile(49000f, 0, CanInternalLoad: false, CanSlingLoad: false, IsInfantry: false),
                _ => CargoProfile.NonTransportable
            };
            return true;
        }
    }

    private sealed class CargoMovementCatalog : IUnitMovementCatalog
    {
        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
        {
            profile = definition switch
            {
                DefHeli => new UnitMovementProfile(68f, 4.5f, TraversabilityCapabilities.DefaultVehicle, TurnRate: 55f, Acceleration: 12f, Deceleration: 14f, CollisionRadius: 8.5f, Height: 5.7f),
                DefInfantry => new UnitMovementProfile(4f, 0.4f, TraversabilityCapabilities.Foot, TurnRate: 360f, Acceleration: 12f, Deceleration: 16f, CollisionRadius: 0.4f, Height: 1.8f),
                DefJeep => new UnitMovementProfile(13.9f, 0.85f, TraversabilityCapabilities.DefaultVehicle, TurnRate: 60f, Acceleration: 4f, Deceleration: 6f, CanPivot: false, MinTurnRadius: 5.5f, CollisionRadius: 1.9f, Height: 1.8f),
                DefApc => new UnitMovementProfile(9.7f, 1.35f, TraversabilityCapabilities.DefaultVehicle, TurnRate: 36f, Acceleration: 2.5f, Deceleration: 4.5f, CollisionRadius: 2.8f, Height: 2.5f),
                _ => new UnitMovementProfile(8f, 1.8f, TraversabilityCapabilities.DefaultVehicle, TurnRate: 36f, Acceleration: 2.2f, Deceleration: 4.5f, CollisionRadius: 3.9f, Height: 3.3f)
            };
            return true;
        }
    }

    private static (Simulation Sim, WorldState World, FlightSystem Flight) CreateHarness(StaticGeometry? geometry = null)
    {
        HeightMap map = geometry?.HeightMap ?? new HeightMap(65, 65, 8f, 0.01f, new ushort[65 * 65]);
        geometry ??= new StaticGeometry(map, []);
        var flightCatalog = new CargoFlightCatalog(CargoHeliProfile);
        var moveCatalog = new CargoMovementCatalog();
        var nav = new NavigationSystem(new NavigationGrid(geometry, enableHierarchical: true), moveCatalog);
        var container = new ContainerSystem(nav, moveCatalog);
        var flight = new FlightSystem(geometry, flightCatalog);
        var rules = new RuleSet(flight, nav, container);
        var world = new WorldState(map);
        var sim = new Simulation(world, rules);
        return (sim, world, flight);
    }

    [Fact]
    public void InternalVehicleRampLoading_JeepBoardsCargoHeli_RejectedByApc_AndDisembarksCleanly()
    {
        (Simulation sim, WorldState world, _) = CreateHarness();
        using (sim)
        {
            EntityId heli = world.CreateUnit(1, DefHeli, new Vector2(150f, 150f), 68f, maxHealth: 160, passengerCapacity: 24);
            EntityId apc = world.CreateUnit(1, DefApc, new Vector2(200f, 150f), 10f, maxHealth: 50, passengerCapacity: 11);
            EntityId jeep = world.CreateUnit(1, DefJeep, new Vector2(150f, 165f), 14f, maxHealth: 30);
            EntityId tank = world.CreateUnit(1, DefTank, new Vector2(130f, 165f), 8f, maxHealth: 100);

            // APC cannot load vehicles internally; Tank cannot load internally anywhere
            Assert.False(sim.TryEnqueue(1, new BoardTransportCommand(1, 0, jeep, apc)));
            Assert.False(sim.TryEnqueue(1, new BoardTransportCommand(2, 0, tank, heli)));

            // Jeep drives up CH-47D rear ramp and consumes 8 of 24 cargo slots
            Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(3, 0, jeep, heli)));
            for (int i = 0; i < 80; i++) sim.Step();

            Assert.True(world.IsContained(jeep));
            Assert.Equal(heli, world.GetContainerId(jeep));
            Assert.Equal(1, world.GetPassengerCount(heli));
            Assert.Equal(8, world.GetUsedCargoSlots(heli));
            Assert.Equal(16, world.GetAvailableSeats(heli));

            // Disembark jeep onto clear ground
            Assert.True(sim.TryEnqueue(1, new DisembarkCommand(4, 0, heli, new Vector2(150f, 150f))));
            for (int i = 0; i < 10; i++) sim.Step();

            Assert.False(world.IsContained(jeep));
            Assert.Equal(0, world.GetPassengerCount(heli));
            Assert.Equal(0, world.GetUsedCargoSlots(heli));
            Assert.True(Vector2.Distance(world.GetUnitAt(2).Position, new Vector2(150f, 150f)) >= 9.0f);
        }
    }

    [Fact]
    public void FastRopeInsertion_DeploysInfantryIntoDenseForest_KeepsJeepAboard_AndRejectsWater()
    {
        HeightMap map = new(65, 65, 8f, 0.01f, new ushort[65 * 65]);
        TerrainObstacle forest = new(240f, 240f, 320f, 320f, TraversabilityCapabilities.DenseForest);
        TerrainObstacle water = new(360f, 240f, 440f, 320f, TraversabilityCapabilities.Water);
        StaticGeometry geometry = new(map, [], [], [forest, water]);

        (Simulation sim, WorldState world, FlightSystem flight) = CreateHarness(geometry);
        using (sim)
        {
            EntityId heli = world.CreateUnit(1, DefHeli, new Vector2(160f, 280f), 68f, maxHealth: 160, passengerCapacity: 24);
            EntityId inf1 = world.CreateUnit(1, DefInfantry, new Vector2(160f, 280f), 4f, maxHealth: 15);
            EntityId inf2 = world.CreateUnit(1, DefInfantry, new Vector2(160f, 280f), 4f, maxHealth: 15);
            EntityId jeep = world.CreateUnit(1, DefJeep, new Vector2(160f, 280f), 14f, maxHealth: 30);

            Assert.True(world.Board(heli, inf1, slots: 1));
            Assert.True(world.Board(heli, inf2, slots: 1));
            Assert.True(world.Board(heli, jeep, slots: 8));

            Vector2 forestDrop = new(280f, 280f);
            Vector2 waterDrop = new(400f, 280f);

            // Landing in dense forest is blocked, but fast-rope in dense forest is valid; water is blocked for both
            Assert.Equal(LandingZoneStatus.BlockedByForest, FlightSystem.EvaluateLandingZone(geometry, world, forestDrop, 10f, 12f, heli));
            Assert.Equal(FastRopeZoneStatus.Valid, FlightSystem.EvaluateFastRopeZone(geometry, forestDrop));
            Assert.Equal(FastRopeZoneStatus.BlockedByWater, FlightSystem.EvaluateFastRopeZone(geometry, waterDrop));

            // Order fast-rope into the dense forest
            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(1, 0, heli, HelicopterOrderKind.FastRope, AltitudeBand.Low, HasTarget: true, Target: forestDrop)));

            bool observedFastRopeFlag = false;
            for (int i = 0; i < 320; i++)
            {
                sim.Step();
                if ((world.GetFlightFlags(heli) & FlightStateFlags.FastRopeActive) != 0)
                    observedFastRopeFlag = true;
            }

            Assert.True(observedFastRopeFlag, "Helicopter must enter FastRopeActive state while inserting troops.");
            Assert.Equal(2, flight.TotalFastRopeInsertions);
            Assert.False(world.IsContained(inf1));
            Assert.False(world.IsContained(inf2));
            Assert.True(world.IsContained(jeep), "Internal vehicles must stay aboard during infantry fast-rope insertion.");

            // Helicopter stays safely hovering airborne over the forest after fast-roping
            Assert.True(world.TryGetFlightState(heli, out WorldState.UnitFlightState fs));
            Assert.Equal(FlightPhase.Airborne, fs.Phase);
            Assert.Equal(FlightStateFlags.None, world.GetFlightFlags(heli));
        }
    }

    [Fact]
    public void SlingLoad_LightAndHeavy_AppliesWeightPenalty_RejectsTank_AndDropsCleanly()
    {
        (Simulation sim, WorldState world, FlightSystem flight) = CreateHarness();
        using (sim)
        {
            EntityId heliLight = world.CreateUnit(1, DefHeli, new Vector2(120f, 120f), 68f, maxHealth: 160, passengerCapacity: 24);
            EntityId heliHeavy = world.CreateUnit(1, DefHeli, new Vector2(120f, 220f), 68f, maxHealth: 160, passengerCapacity: 24);
            EntityId jeep = world.CreateUnit(1, DefJeep, new Vector2(120f, 122f), 14f, maxHealth: 30);
            EntityId apc = world.CreateUnit(1, DefApc, new Vector2(120f, 222f), 10f, maxHealth: 50);
            EntityId tank = world.CreateUnit(1, DefTank, new Vector2(120f, 320f), 8f, maxHealth: 100);

            // Tank (49t) exceeds 12.7t hook capacity and is rejected
            Assert.False(sim.TryEnqueue(1, new HelicopterCommand(1, 0, heliLight, HelicopterOrderKind.SlingPickup, TargetUnit: tank)));

            // Pickup light jeep (1.1t) and heavy APC (11.2t)
            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(2, 0, heliLight, HelicopterOrderKind.SlingPickup, TargetUnit: jeep)));
            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(3, 0, heliHeavy, HelicopterOrderKind.SlingPickup, TargetUnit: apc)));

            for (int i = 0; i < 160; i++) sim.Step();

            Assert.Equal(jeep, world.GetSlingLoad(heliLight));
            Assert.Equal(heliLight, world.GetSlungBy(jeep));
            Assert.Equal(apc, world.GetSlingLoad(heliHeavy));
            Assert.Equal(heliHeavy, world.GetSlungBy(apc));
            Assert.True(world.GetUnitAltitude(jeep) > 5f);
            Assert.True(world.GetUnitAltitude(apc) > 5f);

            // Slung vehicles cannot accept independent move orders
            Assert.False(sim.TryEnqueue(1, new MoveCommand(4, 0, apc, new Vector2(300f, 220f))));

            // Fly both east by 260m and compare progress: heavy APC sling must travel noticeably slower than light jeep sling
            Assert.True(sim.TryEnqueue(1, new MoveCommand(5, 0, heliLight, new Vector2(380f, 120f))));
            Assert.True(sim.TryEnqueue(1, new MoveCommand(6, 0, heliHeavy, new Vector2(380f, 220f))));
            for (int i = 0; i < 90; i++) sim.Step();

            Assert.True(world.TryGetUnit(heliLight, out UnitState lightPos));
            Assert.True(world.TryGetUnit(heliHeavy, out UnitState heavyPos));
            Assert.True(lightPos.Position.X > heavyPos.Position.X + 20f,
                $"Light sling ({lightPos.Position.X:F1}m) must outpace heavy APC sling ({heavyPos.Position.X:F1}m).");

            // Drop the heavy APC at (380, 220)
            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(7, 0, heliHeavy, HelicopterOrderKind.SlingDrop, HasTarget: true, Target: new Vector2(380f, 220f))));
            for (int i = 0; i < 240; i++) sim.Step();

            Assert.Equal(default, world.GetSlingLoad(heliHeavy));
            Assert.False(world.IsSlung(apc));
            Assert.Equal(0f, world.GetUnitAltitude(apc));
            Assert.Equal(1, flight.TotalSlingDrops);
            Assert.True(world.TryGetUnit(apc, out UnitState droppedApc));
            Assert.True(Vector2.Distance(droppedApc.Position, new Vector2(380f, 220f)) < 3f);

            // Dropped APC can immediately accept movement orders again
            Assert.True(sim.TryEnqueue(1, new MoveCommand(8, 0, apc, new Vector2(400f, 220f))));
        }
    }

    [Fact]
    public void WireProtocol_V28_RoundTripsCargoCommandsAndSlingState_AndRejectsV27Handshake()
    {
        Assert.Equal(28, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(28, WireProtocol.Version);
        Assert.Equal(66, WireProtocol.UnitSize);
        Assert.Equal(29, WireProtocol.HelicopterSize);

        // 1. Round-trip SlingPickup command with TargetUnit
        byte[] cmdBuf = new byte[WireProtocol.HelicopterSize];
        HelicopterCommand slingCmd = new(11, 42, new EntityId(1), HelicopterOrderKind.SlingPickup, AltitudeBand.Low, HasTarget: false, Target: default, TargetUnit: new EntityId(9));
        WireProtocol.WriteHelicopter(cmdBuf, slingCmd);
        Assert.True(WireProtocol.TryReadHelicopter(cmdBuf, out HelicopterCommand decodedCmd));
        Assert.Equal(slingCmd, decodedCmd);

        // Reject outdated v27 packet
        cmdBuf[0] = 27;
        Assert.False(WireProtocol.TryReadHelicopter(cmdBuf, out _));

        // 2. Round-trip OwnUnitObservation with SlingLoadId, SlungById, and FlightFlags
        OwnUnitObservation heliObs = new(
            new EntityId(1),
            DefHeli,
            new Vector2(200f, 300f),
            0,
            Altitude: 18f,
            FlightPhase: FlightPhase.Airborne,
            AltitudeBand: AltitudeBand.Low,
            HasFlightCapability: true,
            SlingLoadId: new EntityId(5),
            SlungById: default,
            FlightFlags: FlightStateFlags.FastRopeActive);

        OwnUnitObservation slungObs = new(
            new EntityId(5),
            DefApc,
            new Vector2(200f, 300f),
            0,
            Altitude: 8f,
            SlingLoadId: default,
            SlungById: new EntityId(1),
            FlightFlags: FlightStateFlags.None);

        byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
        int len = WireProtocol.WriteSnapshot(snapBuf, 1, 20, [heliObs, slungObs]);
        ClientReplica replica = new();
        Assert.True(replica.TryApply(snapBuf.AsSpan(0, len)));
        Assert.True(replica.TryGetOwnUnit(new EntityId(1), out OwnUnitObservation rHeli));
        Assert.Equal(new EntityId(5), rHeli.SlingLoadId);
        Assert.Equal(FlightStateFlags.FastRopeActive, rHeli.FlightFlags);
        Assert.True(replica.TryGetOwnUnit(new EntityId(5), out OwnUnitObservation rSlung));
        Assert.Equal(new EntityId(1), rSlung.SlungById);
        Assert.Equal(8f, rSlung.Altitude);

        // 3. Handshake mismatch rejection for protocol v27
        MatchSetup serverSetup = new(MatchSetup.CurrentProtocolVersion, 12345UL, 67890UL, "HelicopterCargo");
        MatchSetup staleV27Setup = serverSetup with { ProtocolVersion = 27 };
        byte[] hsBuf = new byte[WireProtocol.MaxMessageSize];
        int hsLen = WireProtocol.WriteHandshakeRequest(hsBuf, staleV27Setup);
        Assert.True(WireProtocol.TryReadHandshakeRequest(hsBuf.AsSpan(0, hsLen), out MatchSetup receivedStale));
        Assert.Equal((byte)27, receivedStale.ProtocolVersion);
        Assert.NotEqual(MatchSetup.CurrentProtocolVersion, receivedStale.ProtocolVersion);
    }

    [Fact]
    public void CargoOperations_HotPath_ZeroManagedAllocationsPerTick()
    {
        (Simulation sim, WorldState world, _) = CreateHarness();
        using (sim)
        {
            EntityId heli = world.CreateUnit(1, DefHeli, new Vector2(120f, 120f), 68f, maxHealth: 160, passengerCapacity: 24);
            EntityId apc = world.CreateUnit(1, DefApc, new Vector2(120f, 122f), 10f, maxHealth: 50);

            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(1, 0, heli, HelicopterOrderKind.SlingPickup, TargetUnit: apc)));
            for (int i = 0; i < 120; i++) sim.Step();
            Assert.Equal(apc, world.GetSlingLoad(heli));

            Assert.True(sim.TryEnqueue(1, new MoveCommand(2, 0, heli, new Vector2(400f, 300f))));
            for (int i = 0; i < 10; i++) sim.Step();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 40; i++) sim.Step();
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, after - before);
        }
    }
}
