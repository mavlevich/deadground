using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Tests;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class StopCommandTests
{
    private const ushort DefTank = 0;
    private const ushort DefInfantry = 1;
    private const ushort DefApc = 2;
    private const ushort DefHeli = 3;
    private const ushort DefArtillery = 4;

    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
        {
            profile = definition switch
            {
                DefInfantry => new UnitMovementProfile(3.6f, 0.4f, TraversabilityCapabilities.Foot, TurnRate: 360f, CanPivot: true, Acceleration: 12f, Deceleration: 16f, CollisionRadius: 0.4f, Height: 1.8f),
                DefApc => new UnitMovementProfile(10.0f, 1.4f, TraversabilityCapabilities.DefaultVehicle, TurnRate: 40f, CanPivot: true, Acceleration: 3f, Deceleration: 5f, CollisionRadius: 2.8f, Height: 2.5f),
                DefArtillery => new UnitMovementProfile(9.7f, 1.5f, TraversabilityCapabilities.DefaultVehicle, TurnRate: 32f, CanPivot: false, MinTurnRadius: 10.5f, Acceleration: 2.4f, Deceleration: 4.5f, CollisionRadius: 3.9f, Height: 2.7f),
                DefHeli => new UnitMovementProfile(68f, 4.5f, TraversabilityCapabilities.DefaultVehicle, TurnRate: 55f, CanPivot: true, Acceleration: 12f, Deceleration: 14f, CollisionRadius: 8.5f, Height: 5.7f),
                _ => new UnitMovementProfile(8.3f, 1.65f, TraversabilityCapabilities.DefaultVehicle, TurnRate: 30f, CanPivot: true, Acceleration: 2f, Deceleration: 4f, CollisionRadius: 3.7f, Height: 2.4f)
            };
            return true;
        }
    }

    private sealed class TestPerceptionCatalog : IUnitPerceptionCatalog
    {
        private readonly UnitPerceptionProfile[] _profiles =
        [
            new(new VisualSensorProfile(500f, 2.0f), new TargetObservationPoints(0.8f, 2.0f), ContactClassification.Tank),
            new(new VisualSensorProfile(500f, 1.6f), new TargetObservationPoints(0.6f, 1.4f), ContactClassification.Infantry),
            new(new VisualSensorProfile(500f, 2.0f), new TargetObservationPoints(0.8f, 2.0f), ContactClassification.Apc),
            new(new VisualSensorProfile(500f, 2.5f), new TargetObservationPoints(1.5f, 4.0f), ContactClassification.Helicopter),
            new(new VisualSensorProfile(500f, 2.2f), new TargetObservationPoints(1.0f, 2.5f), ContactClassification.Artillery)
        ];

        public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile)
        {
            if (definition < _profiles.Length)
            {
                profile = _profiles[definition];
                return true;
            }
            profile = default;
            return false;
        }
    }

    private sealed class TestCombatCatalog : IUnitCombatCatalog
    {
        private static readonly WeaponDefinition TankGun = new(
            Id: "test_cannon",
            Range: 400f,
            ReloadDurationTicks: 40,
            Damage: 50,
            AmmoCapacity: 20,
            MuzzleHeight: 2.0f,
            PresentationId: 1,
            PenetrationNear: 200f,
            PenetrationFar: 180f);

        private static readonly WeaponDefinition GradRocket = new(
            Id: "test_rocket",
            Range: 1000f,
            ReloadDurationTicks: 200,
            Damage: 60,
            AmmoCapacity: 40,
            MuzzleHeight: 2.5f,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.RocketArtillery, 0),
            IsIndirectFire: true,
            MinRange: 200f,
            SalvoCount: 6,
            SalvoIntervalTicks: 10,
            DispersionRadius: 40f,
            SplashRadius: 20f,
            ProjectileSpeed: 120f,
            SetupTicks: 20);

        public bool TryGetProfile(ushort definition, out UnitCombatProfile profile)
        {
            profile = definition switch
            {
                DefTank => new UnitCombatProfile(100, TankGun, TurretTraverseRate: 30f),
                DefArtillery => new UnitCombatProfile(40, GradRocket, TurretTraverseRate: 28f),
                DefHeli => new UnitCombatProfile(160, null),
                _ => new UnitCombatProfile(50, null)
            };
            return true;
        }
    }

    private sealed class TestFlightCatalog : IUnitFlightCatalog
    {
        private static readonly FlightProfile HeliProfile = new(
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
            StartAirborne: true);

        public bool TryGetFlightProfile(ushort definition, out FlightProfile profile)
        {
            if (definition == DefHeli)
            {
                profile = HeliProfile;
                return true;
            }
            profile = default;
            return false;
        }

        public bool TryGetCargoProfile(ushort definition, out CargoProfile cargo)
        {
            cargo = definition == DefInfantry
                ? new CargoProfile(100f, 1, CanInternalLoad: true, CanSlingLoad: false, IsInfantry: true)
                : CargoProfile.NonTransportable;
            return true;
        }
    }

    private static (Simulation Sim, WorldState World, DetectionSystem Detection, ArtillerySystem Artillery) CreateHarness()
    {
        HeightMap map = new(129, 129, 8.0f, 0.01f, new ushort[129 * 129]);
        StaticGeometry geometry = new(map, []);
        TestMovementCatalog moveCat = new();
        TestPerceptionCatalog percCat = new();
        TestCombatCatalog combatCat = new();
        TestFlightCatalog flightCat = new();

        NavigationSystem nav = new(new NavigationGrid(geometry, enableHierarchical: true), moveCat);
        FlightSystem flight = new(geometry, flightCat);
        DetectionSystem detection = new(geometry, percCat, new DetectionTuning(evaluationIntervalTicks: 2));
        ContainerSystem container = new(nav, moveCat);
        CombatSystem combat = new(geometry, combatCat, percCat, detection, navigation: nav, flight: flight);
        ArtillerySystem artillery = new(geometry, combatCat, nav);

        WorldState world = new(map);
        Simulation sim = new(world, new RuleSet(flight, nav, container, detection, combat, artillery));
        return (sim, world, detection, artillery);
    }

    [Fact]
    public void WireProtocol_StopCommand_RoundTripsAndRejectsStaleProtocolVersion()
    {
        StopCommand original = new(Sequence: 42u, ClientTick: 123, Unit: new EntityId(7));
        byte[] buffer = new byte[WireProtocol.StopSize];
        WireProtocol.WriteStop(buffer, original);

        Assert.True(WireProtocol.TryReadStop(buffer, out StopCommand decoded));
        Assert.Equal(original, decoded);

        // Stale v29 packet must be rejected by v30 wire protocol
        buffer[0] = 29;
        Assert.False(WireProtocol.TryReadStop(buffer, out _));

        // Zero unit ID must be rejected
        buffer[0] = WireProtocol.Version;
        WireProtocol.WriteStop(buffer, new StopCommand(1u, 10, default));
        Assert.False(WireProtocol.TryReadStop(buffer, out _));
    }

    [Fact]
    public void StopCommand_HaltsMovingGroundUnitAndClearsAttackOrder()
    {
        (Simulation sim, WorldState world, DetectionSystem detection, _) = CreateHarness();
        using (sim)
        {
            EntityId tank = world.CreateUnit(1, DefTank, new Vector2(100f, 100f), 8.3f, maxHealth: 100, ammo: 20);
            // Place enemy at 450m (> 400m gun range, < 500m sensor range) so tank must advance to engage
            world.CreateUnit(2, DefTank, new Vector2(550f, 100f), 8.3f, maxHealth: 100, ammo: 20);

            // Detect enemy and order move + attack
            for (int i = 0; i < 4; i++) sim.Step();
            ReadOnlySpan<ContactObservation> contacts = detection.GetContacts(1);
            Assert.Equal(1, contacts.Length);

            Assert.True(sim.TryEnqueue(1, new AttackCommand(1u, 0, tank, contacts[0].Id)));
            for (int i = 0; i < 20; i++) sim.Step();
            Assert.True(world.TryGetUnit(tank, out UnitState moving));
            Assert.True(moving.Moving);
            Assert.True(world.TryGetCombatState(tank, out WorldState.CombatState csBefore));
            Assert.NotEqual(default, csBefore.TargetContactId);

            // Issue STOP command
            Assert.True(sim.TryEnqueue(1, new StopCommand(2u, 0, tank)));
            sim.Step();

            Assert.True(world.TryGetUnit(tank, out UnitState stopped));
            Assert.False(stopped.Moving);
            Vector2 posAfterStop = stopped.Position;
            Assert.True(world.TryGetCombatState(tank, out WorldState.CombatState csAfter));
            Assert.Equal(default, csAfter.TargetContactId);

            // Remains halted on subsequent ticks
            for (int i = 0; i < 15; i++) sim.Step();
            Assert.True(world.TryGetUnit(tank, out UnitState settled));
            Assert.False(settled.Moving);
            Assert.Equal(posAfterStop, settled.Position);
        }
    }

    [Fact]
    public void StopCommand_CancelsBoardingAndFreesReservedTransportSeat()
    {
        (Simulation sim, WorldState world, _, _) = CreateHarness();
        using (sim)
        {
            EntityId apc = world.CreateUnit(1, DefApc, new Vector2(200f, 100f), 10.0f, maxHealth: 50, passengerCapacity: 8);
            EntityId soldier = world.CreateUnit(1, DefInfantry, new Vector2(120f, 100f), 3.6f, maxHealth: 15);

            Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(1u, 0, soldier, apc)));
            for (int i = 0; i < 10; i++) sim.Step();
            Assert.True(world.TryGetBoardingTarget(soldier, out EntityId targetApc));
            Assert.Equal(apc, targetApc);
            Assert.True(world.GetReservedSeats(apc) > 0);

            Assert.True(sim.TryEnqueue(1, new StopCommand(2u, 0, soldier)));
            sim.Step();

            Assert.False(world.TryGetBoardingTarget(soldier, out _));
            Assert.Equal(0, world.GetReservedSeats(apc));
            Assert.True(world.TryGetUnit(soldier, out UnitState halted));
            Assert.False(halted.Moving);
        }
    }

    [Fact]
    public void StopCommand_CancelsActiveArtilleryBarrage()
    {
        (Simulation sim, WorldState world, _, _) = CreateHarness();
        using (sim)
        {
            EntityId grad = world.CreateUnit(1, DefArtillery, new Vector2(100f, 100f), 9.7f, maxHealth: 40, ammo: 40);

            Assert.True(sim.TryEnqueue(1, new FireMissionCommand(1u, 0, grad, new Vector2(600f, 100f))));
            for (int i = 0; i < 5; i++) sim.Step();
            Assert.True(world.TryGetArtilleryState(grad, out WorldState.UnitArtilleryState artBefore));
            Assert.True(artBefore.HasFireMission);

            Assert.True(sim.TryEnqueue(1, new StopCommand(2u, 0, grad)));
            sim.Step();

            Assert.True(world.TryGetArtilleryState(grad, out WorldState.UnitArtilleryState artAfter));
            Assert.False(artAfter.HasFireMission);
            Assert.Equal(0, artAfter.SalvoRemaining);
        }
    }

    [Fact]
    public void StopCommand_HaltsAirborneHelicopterIntoStableHover()
    {
        (Simulation sim, WorldState world, _, _) = CreateHarness();
        using (sim)
        {
            EntityId heli = world.CreateUnit(1, DefHeli, new Vector2(150f, 150f), 68f, maxHealth: 160, passengerCapacity: 24);
            sim.Step();

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1u, 0, heli, new Vector2(700f, 500f))));
            for (int i = 0; i < 30; i++) sim.Step();
            Assert.True(world.TryGetFlightState(heli, out WorldState.UnitFlightState flying));
            Assert.True(flying.HasMoveTarget);
            Assert.True(flying.Velocity.Length() > 5f);

            Assert.True(sim.TryEnqueue(1, new StopCommand(2u, 0, heli)));
            for (int i = 0; i < 120; i++) sim.Step();

            Assert.True(world.TryGetFlightState(heli, out WorldState.UnitFlightState hovering));
            Assert.False(hovering.HasMoveTarget);
            Assert.Equal(FlightPhase.Airborne, hovering.Phase);
            Assert.True(hovering.Velocity.Length() < 0.5f, $"Expected helicopter to settle into hover, speed={hovering.Velocity.Length():F2}");
        }
    }

    [Fact]
    public void StopCommand_RejectsNonOwnerAndContainedUnits()
    {
        (Simulation sim, WorldState world, _, _) = CreateHarness();
        using (sim)
        {
            EntityId enemyTank = world.CreateUnit(2, DefTank, new Vector2(200f, 200f), 8.3f);
            EntityId apc = world.CreateUnit(1, DefApc, new Vector2(100f, 100f), 10.0f, maxHealth: 50, passengerCapacity: 8);
            EntityId soldier = world.CreateUnit(1, DefInfantry, new Vector2(101f, 100f), 3.6f, maxHealth: 15);
            Assert.True(world.Board(apc, soldier, 1));

            Assert.False(sim.TryEnqueue(1, new StopCommand(1u, 0, enemyTank)));
            Assert.False(sim.TryEnqueue(1, new StopCommand(1u, 0, soldier)));
        }
    }

    [Fact]
    public void StopCommand_LocalSessionRoundTrip_AndZeroAllocHotPath()
    {
        (Simulation sim, WorldState world, _, _) = CreateHarness();
        EntityId tank = world.CreateUnit(1, DefTank, new Vector2(100f, 100f), 8.3f, maxHealth: 100, ammo: 20);
        using LocalSession session = new(sim, player: 1);

        Assert.True(session.Move(tank, new Vector2(400f, 100f)));
        for (int i = 0; i < 20; i++) session.Advance(0.05);

        Assert.True(session.Stop(tank));
        session.Advance(0.05);
        session.Advance(0.05);

        Assert.True(world.TryGetUnit(tank, out UnitState stopped));
        Assert.False(stopped.Moving);

        // Warmup both TryEnqueue(StopCommand) and Step() before measuring steady-state allocations
        Assert.True(sim.TryEnqueue(1, new StopCommand(999u, 0, tank)));
        for (int i = 0; i < 10; i++) sim.Step();

        using NoGcAllocationWindow window = new();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool enq = sim.TryEnqueue(1, new StopCommand(1000u, 0, tank));
        for (int i = 0; i < 20; i++) sim.Step();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(enq);
        Assert.True(allocated == 0, window.Describe(allocated));
    }
}
