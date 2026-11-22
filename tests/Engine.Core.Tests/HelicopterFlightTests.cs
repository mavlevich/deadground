using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Tests;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class HelicopterFlightTests
{
    private const ushort DefHeli = 0;
    private const ushort DefTank = 1;
    private const ushort DefAaApc = 2;
    private const ushort DefInfantry = 3;

    private sealed class TestFlightCatalog : IUnitFlightCatalog
    {
        private readonly FlightProfile _heliProfile;
        public TestFlightCatalog(FlightProfile? profile = null)
        {
            _heliProfile = profile ?? new FlightProfile(
                CruiseSpeed: 68f,
                LowAltitudeSpeed: 42f,
                Acceleration: 12f,
                Deceleration: 14f,
                YawRateDegPerSec: 55f,
                ClimbRate: 6f,
                DescentRate: 4.5f,
                LowAltitude: 12f,
                HighAltitude: 35f,
                LandingClearanceRadius: 10f,
                MaxLandingSlopeDegrees: 12f,
                CrashDescentRate: 14f,
                CrashSpinRateDegPerSec: 180f,
                CrashSplashRadius: 12f,
                CrashSplashDamage: 60);
        }

        public bool TryGetFlightProfile(ushort definition, out FlightProfile profile)
        {
            if (definition == DefHeli)
            {
                profile = _heliProfile;
                return true;
            }
            profile = default;
            return false;
        }
    }

    private sealed class TestPerceptionCatalog : IUnitPerceptionCatalog
    {
        private readonly UnitPerceptionProfile[] _profiles =
        [
            new(new VisualSensorProfile(1500f, 2.5f), new TargetObservationPoints([1.5f, 3.8f, 5.5f]), ContactClassification.Helicopter),
            new(new VisualSensorProfile(1500f, 2.0f), new TargetObservationPoints([1.0f, 2.0f]), ContactClassification.Tank),
            new(new VisualSensorProfile(1500f, 2.0f), new TargetObservationPoints([1.0f, 2.0f]), ContactClassification.Apc),
            new(new VisualSensorProfile(1500f, 2.0f), new TargetObservationPoints([1.0f, 2.0f]), ContactClassification.Infantry)
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

    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
        {
            profile = definition == DefInfantry
                ? new UnitMovementProfile(3.5f, 0.4f, TraversabilityCapabilities.Foot, 360f, 1.5f, 12f, 16f, true, 0f, 0.4f, 1.8f)
                : new UnitMovementProfile(10f, 1.8f, TraversabilityCapabilities.DefaultVehicle, 45f, 4f, 3f, 5f, true, 0f, 3.0f, 2.5f);
            return true;
        }
    }

    private sealed class TestCombatCatalog : IUnitCombatCatalog
    {
        private readonly WeaponDefinition _tankCannon = new(
            "cannon", 1500f, 20, 50, 40, 1.8f, WeaponPresentation.MakeId(WeaponPresentationKind.Cannon, 0),
            200f, 150f, 0f, 1500f, 1,
            [new AccuracyPoint(500f, 0.99f), new AccuracyPoint(1500f, 0.95f)],
            CanTargetAir: false, MaxAirTargetAltitude: 0f);

        private readonly WeaponDefinition _aaHmg = new(
            "aa_hmg", 1000f, 4, 40, 500, 2.0f, WeaponPresentation.MakeId(WeaponPresentationKind.MachineGun, 1),
            30f, 18f, 0f, 1000f, 5,
            [new AccuracyPoint(500f, 0.98f), new AccuracyPoint(1000f, 0.90f)],
            CanTargetAir: true, MaxAirTargetAltitude: 60f);

        private readonly WeaponDefinition _doorGun = new(
            "door_gun", 550f, 10, 6, 1200, 1.9f, WeaponPresentation.MakeId(WeaponPresentationKind.MachineGun, 2),
            9f, 4f, 0f, 550f, 5,
            [new AccuracyPoint(300f, 0.85f), new AccuracyPoint(550f, 0.50f)],
            CanTargetAir: true, MaxAirTargetAltitude: 40f);

        public bool TryGetProfile(ushort definition, out UnitCombatProfile profile)
        {
            profile = definition switch
            {
                DefHeli => new UnitCombatProfile(120, _doorGun, 120f, new UnitArmor(2f, 2f, 2f), 4.5f, true, 100),
                DefTank => new UnitCombatProfile(100, _tankCannon, 90f, new UnitArmor(200f, 80f, 45f), 1.8f, true, 50),
                DefAaApc => new UnitCombatProfile(60, _aaHmg, 90f, new UnitArmor(16f, 9f, 7f), 1.4f, true, 50),
                DefInfantry => new UnitCombatProfile(15, null, 0f, new UnitArmor(0f, 0f, 0f), 0.4f, false, 50),
                _ => new UnitCombatProfile(50, null, 0f, default, 1.0f, true, 50)
            };
            return true;
        }
    }

    private static StaticGeometry CreateTestGeometry(bool withSteepHill = false)
    {
        const int res = 101;
        const float spacing = 8f; // 800m x 800m
        ushort[] heights = new ushort[res * res];
        for (int r = 0; r < res; r++)
        {
            float z = r * spacing;
            for (int c = 0; c < res; c++)
            {
                float x = c * spacing;
                float h = 2f;
                if (withSteepHill)
                {
                    float dx = x - 500f;
                    float dz = z - 500f;
                    float d2 = dx * dx + dz * dz;
                    if (d2 < 36f * 36f)
                    {
                        float norm = 1f - d2 / (36f * 36f);
                        h += 24f * norm;
                    }
                }
                heights[r * res + c] = (ushort)MathF.Round(h * 100f);
            }
        }
        HeightMap map = new(res, res, spacing, 0.01f, heights);
        OpaqueStructure building = new(new StructureId(1), 220f, 0f, 220f, 240f, 8f, 240f, isGrounded: true);
        TerrainObstacle water = new(0f, 360f, 800f, 420f, TraversabilityCapabilities.Water);
        TerrainObstacle forest = new(600f, 200f, 700f, 300f, TraversabilityCapabilities.DenseForest);
        return new StaticGeometry(map, [building], [], [water, forest]);
    }

    private static (Simulation Sim, FlightSystem Flight, CombatSystem Combat, DetectionSystem Det) CreateSim(
        StaticGeometry? geometry = null,
        FlightProfile? flightProfile = null,
        ulong seed = 2024UL)
    {
        geometry ??= CreateTestGeometry();
        TestFlightCatalog flightCat = new(flightProfile);
        TestPerceptionCatalog percCat = new();
        TestMovementCatalog movCat = new();
        TestCombatCatalog combCat = new();

        FlightSystem flight = new(geometry, flightCat);
        NavigationSystem nav = new(new NavigationGrid(geometry, 4f, enableHierarchical: false), movCat);
        ContainerSystem container = new(nav);
        DetectionSystem det = new(geometry, percCat, new DetectionTuning(evaluationIntervalTicks: 1));
        CombatSystem combat = new(geometry, combCat, percCat, det, nav, flight: flight);
        RuleSet rules = new(flight, nav, container, det, combat);
        WorldState world = new(geometry.HeightMap);
        Simulation sim = new(world, rules, seed);
        return (sim, flight, combat, det);
    }

    [Fact]
    public void TakeOff_And_MoveOverWater_FliesAndHoversWithoutAutoLanding()
    {
        (Simulation sim, _, _, _) = CreateSim();
        using (sim)
        {
            EntityId heli = sim.World.CreateUnit(1, DefHeli, new Vector2(150f, 150f), 68f, 120, 1200, passengerCapacity: 24);
            sim.Flight!.EnsureUnitsInitialized(sim.World);

            Assert.True(sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState init));
            Assert.Equal(FlightPhase.Ground, init.Phase);
            Assert.Equal(0f, init.Altitude);

            // Right-click Move directly across impassable water (Z=360..420) to Z=520
            Vector2 dest = new(150f, 520f);
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, heli, dest)));

            for (int i = 0; i < 360; i++)
                sim.Step();

            Assert.True(sim.World.TryGetUnit(heli, out UnitState finalUnit));
            Assert.True(sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState finalState));
            Assert.True(Vector2.Distance(finalUnit.Position, dest) < 3.0f, $"Expected hover near {dest}, got {finalUnit.Position}");
            Assert.Equal(FlightPhase.Airborne, finalState.Phase);
            Assert.InRange(finalState.Altitude, 34.5f, 35.5f);
            Assert.False(finalUnit.Moving);
        }
    }

    [Fact]
    public void AltitudeBand_LowVsHigh_ControlsAltitudeAndMaxSpeed()
    {
        (Simulation sim, _, _, _) = CreateSim();
        using (sim)
        {
            EntityId heli = sim.World.CreateUnit(1, DefHeli, new Vector2(100f, 100f), 68f, 120, 1200);
            sim.Flight!.EnsureUnitsInitialized(sim.World);

            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(1, sim.Tick, heli, HelicopterOrderKind.SetAltitudeBand, AltitudeBand.Low)));
            Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, heli, new Vector2(700f, 100f))));

            float maxSpeedLow = 0f;
            for (int i = 0; i < 200; i++)
            {
                sim.Step();
                if (sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState fs))
                {
                    maxSpeedLow = MathF.Max(maxSpeedLow, fs.Velocity.Length());
                }
            }

            Assert.True(sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState lowState));
            Assert.Equal(AltitudeBand.Low, lowState.DesiredBand);
            Assert.InRange(lowState.Altitude, 11.5f, 12.5f);
            Assert.InRange(maxSpeedLow, 38f, 42.5f);

            // Switch to High altitude and fly back
            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(3, sim.Tick, heli, HelicopterOrderKind.SetAltitudeBand, AltitudeBand.High)));
            Assert.True(sim.TryEnqueue(1, new MoveCommand(4, sim.Tick, heli, new Vector2(100f, 100f))));

            float maxSpeedHigh = 0f;
            for (int i = 0; i < 240; i++)
            {
                sim.Step();
                if (sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState fs))
                {
                    maxSpeedHigh = MathF.Max(maxSpeedHigh, fs.Velocity.Length());
                }
            }

            Assert.True(sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState highState));
            Assert.Equal(AltitudeBand.High, highState.DesiredBand);
            Assert.InRange(highState.Altitude, 34.5f, 35.5f);
            Assert.True(maxSpeedHigh > 55f, $"Expected high cruise speed > 55 m/s, got {maxSpeedHigh:F1}");
        }
    }

    [Fact]
    public void LandingZoneValidation_ChecksAllObstacleTypes()
    {
        StaticGeometry geom = CreateTestGeometry(withSteepHill: true);
        WorldState world = new(geom.HeightMap);
        EntityId heli = world.CreateUnit(1, DefHeli, new Vector2(100f, 100f), 68f, 120, 1200);
        world.CreateUnit(1, DefTank, new Vector2(160f, 160f), 10f, 100, 40);
        world.AddWreck(DefTank, new Vector2(300f, 160f), 0, 0f, 0f, 1, 1);

        Assert.Equal(LandingZoneStatus.Valid, FlightSystem.EvaluateLandingZone(geom, world, new Vector2(100f, 100f), 10f, 12f, heli));
        Assert.Equal(LandingZoneStatus.OutOfBounds, FlightSystem.EvaluateLandingZone(geom, world, new Vector2(2f, 2f), 10f, 12f, heli));
        Assert.Equal(LandingZoneStatus.BlockedByWater, FlightSystem.EvaluateLandingZone(geom, world, new Vector2(150f, 390f), 10f, 12f, heli));
        Assert.Equal(LandingZoneStatus.BlockedByStructure, FlightSystem.EvaluateLandingZone(geom, world, new Vector2(230f, 230f), 10f, 12f, heli));
        Assert.Equal(LandingZoneStatus.BlockedByForest, FlightSystem.EvaluateLandingZone(geom, world, new Vector2(650f, 250f), 10f, 12f, heli));
        Assert.Equal(LandingZoneStatus.SlopeTooSteep, FlightSystem.EvaluateLandingZone(geom, world, new Vector2(512f, 512f), 10f, 12f, heli));
        Assert.Equal(LandingZoneStatus.BlockedByUnit, FlightSystem.EvaluateLandingZone(geom, world, new Vector2(162f, 160f), 10f, 12f, heli));
        Assert.Equal(LandingZoneStatus.BlockedByWreck, FlightSystem.EvaluateLandingZone(geom, world, new Vector2(302f, 160f), 10f, 12f, heli));
    }

    [Fact]
    public void LandOrder_AbortsOnWater_SucceedsOnClearLz_AndGatesBoarding()
    {
        (Simulation sim, _, _, _) = CreateSim();
        using (sim)
        {
            EntityId heli = sim.World.CreateUnit(1, DefHeli, new Vector2(150f, 150f), 68f, 120, 1200, passengerCapacity: 24);
            EntityId soldier = sim.World.CreateUnit(1, DefInfantry, new Vector2(134f, 150f), 3.5f, 15, 0);
            sim.Flight!.EnsureUnitsInitialized(sim.World);

            // Take off
            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(1, sim.Tick, heli, HelicopterOrderKind.TakeOff)));
            for (int i = 0; i < 160; i++) sim.Step();

            Assert.True(sim.World.IsAirborne(heli));
            // Boarding rejected while airborne
            Assert.False(sim.TryEnqueue(1, new BoardTransportCommand(2, sim.Tick, soldier, heli)));

            // Land on water: flies over water and aborts to hover instead of sinking
            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(3, sim.Tick, heli, HelicopterOrderKind.Land, HasTarget: true, Target: new Vector2(150f, 390f))));
            for (int i = 0; i < 240; i++) sim.Step();

            Assert.True(sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState waterState));
            Assert.Equal(FlightPhase.Airborne, waterState.Phase);
            Assert.True(waterState.Altitude > 10f);

            // Land on clear ground at (150, 150)
            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(4, sim.Tick, heli, HelicopterOrderKind.Land, HasTarget: true, Target: new Vector2(150f, 150f))));
            for (int i = 0; i < 360; i++) sim.Step();

            Assert.True(sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState groundState));
            Assert.Equal(FlightPhase.Ground, groundState.Phase);
            Assert.Equal(0f, groundState.Altitude);
            Assert.True(sim.Flight.TotalLandings >= 1);

            // Boarding succeeds once landed
            Assert.True(sim.TryEnqueue(1, new BoardTransportCommand(5, sim.Tick, soldier, heli)));
        }
    }

    [Fact]
    public void Disembark_WhileAirborne_FliesToLzLandsAndUnloadsPassengers()
    {
        (Simulation sim, _, _, _) = CreateSim();
        using (sim)
        {
            EntityId heli = sim.World.CreateUnit(1, DefHeli, new Vector2(150f, 150f), 68f, 120, 1200, passengerCapacity: 24);
            EntityId passenger = sim.World.CreateUnit(1, DefInfantry, new Vector2(150f, 150f), 3.5f, 15, 0);
            Assert.True(sim.World.Board(heli, passenger));
            sim.Flight!.EnsureUnitsInitialized(sim.World);

            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(1, sim.Tick, heli, HelicopterOrderKind.TakeOff)));
            for (int i = 0; i < 160; i++) sim.Step();
            Assert.True(sim.World.IsAirborne(heli));

            Vector2 dropLz = new(150f, 260f);
            Assert.True(sim.TryEnqueue(1, new DisembarkCommand(2, sim.Tick, heli, dropLz)));
            for (int i = 0; i < 400; i++) sim.Step();

            Assert.False(sim.World.IsAirborne(heli));
            Assert.False(sim.World.IsContained(passenger));
            Assert.Equal(0, sim.World.GetPassengerCount(heli));
            Assert.True(sim.World.TryGetUnit(passenger, out UnitState pState));
            Assert.True(Vector2.Distance(pState.Position, dropLz) < 15f);
        }
    }

    [Fact]
    public void AirspaceSeparation_PreventsTwoHelicoptersOverlappingAtSameTarget()
    {
        (Simulation sim, _, _, _) = CreateSim();
        using (sim)
        {
            EntityId h1 = sim.World.CreateUnit(1, DefHeli, new Vector2(120f, 150f), 68f, 120, 1200);
            EntityId h2 = sim.World.CreateUnit(1, DefHeli, new Vector2(180f, 150f), 68f, 120, 1200);
            sim.Flight!.EnsureUnitsInitialized(sim.World);

            Vector2 sharedTarget = new(150f, 300f);
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, h1, sharedTarget)));
            Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, h2, sharedTarget)));

            for (int i = 0; i < 320; i++) sim.Step();

            Assert.True(sim.World.TryGetUnit(h1, out UnitState u1));
            Assert.True(sim.World.TryGetUnit(h2, out UnitState u2));
            float separation = Vector2.Distance(u1.Position, u2.Position);
            Assert.True(separation >= 20f, $"Expected airspace separation >= 20m, got {separation:F1}m");
            Assert.False(u1.Moving, "First helicopter should settle into a calm hover.");
            Assert.False(u2.Moving, "Second helicopter should settle into a calm standoff hover.");
        }
    }

    [Fact]
    public void TwoHelicopters_OrderedToLandAtSamePoint_DoNotStack()
    {
        (Simulation sim, _, _, _) = CreateSim();
        using (sim)
        {
            EntityId h1 = sim.World.CreateUnit(1, DefHeli, new Vector2(120f, 150f), 68f, 120, 1200);
            EntityId h2 = sim.World.CreateUnit(1, DefHeli, new Vector2(180f, 150f), 68f, 120, 1200);
            sim.Flight!.EnsureUnitsInitialized(sim.World);

            Vector2 sharedLz = new(150f, 260f);
            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(1, sim.Tick, h1, HelicopterOrderKind.Land, HasTarget: true, Target: sharedLz)));
            Assert.True(sim.TryEnqueue(1, new HelicopterCommand(2, sim.Tick, h2, HelicopterOrderKind.Land, HasTarget: true, Target: sharedLz)));

            for (int i = 0; i < 600; i++) sim.Step();

            Assert.True(sim.World.TryGetFlightState(h1, out WorldState.UnitFlightState fs1));
            Assert.True(sim.World.TryGetFlightState(h2, out WorldState.UnitFlightState fs2));
            Assert.True(sim.World.TryGetUnit(h1, out UnitState u1));
            Assert.True(sim.World.TryGetUnit(h2, out UnitState u2));

            int landedCount = (fs1.Phase == FlightPhase.Ground ? 1 : 0) + (fs2.Phase == FlightPhase.Ground ? 1 : 0);
            Assert.True(landedCount == 1, $"Expected exactly 1 landed helicopter; got h1={fs1.Phase}@{u1.Position}(alt={fs1.Altitude:F1}), h2={fs2.Phase}@{u2.Position}(alt={fs2.Altitude:F1})");
            float separation = Vector2.Distance(u1.Position, u2.Position);
            Assert.True(separation >= 18f, $"Helicopters must not stack on the same LZ, got {separation:F1}m");
        }
    }

    [Fact]
    public void AntiAirRules_TankCannotShootAirborneHeli_HmgShootsDownAndTriggersCrashSplash()
    {
        FlightProfile airborneProfile = new(
            CruiseSpeed: 68f, LowAltitudeSpeed: 42f, Acceleration: 12f, Deceleration: 14f,
            YawRateDegPerSec: 55f, ClimbRate: 6f, DescentRate: 4.5f, LowAltitude: 12f, HighAltitude: 35f,
            CrashDescentRate: 14f, CrashSpinRateDegPerSec: 180f, CrashSplashRadius: 15f, CrashSplashDamage: 60,
            StartAirborne: true);

        // Part 1: Tank alone cannot shoot airborne helicopter
        {
            (Simulation sim, _, CombatSystem combat, _) = CreateSim(flightProfile: airborneProfile);
            using (sim)
            {
                EntityId heli = sim.World.CreateUnit(1, DefHeli, new Vector2(200f, 150f), 68f, 120, 1200);
                sim.World.SetFireStance(heli, FireStance.HoldFire);
                sim.World.CreateUnit(2, DefTank, new Vector2(500f, 150f), 10f, 100, 40);
                sim.Flight!.EnsureUnitsInitialized(sim.World);

                for (int i = 0; i < 80; i++) sim.Step();

                Assert.Equal(0, combat.TotalShotsFired);
                Assert.True(sim.World.TryGetCombatState(heli, out WorldState.CombatState hState));
                Assert.Equal(120, hState.CurrentHealth);
            }
        }

        // Part 2: AA HMG shoots down airborne helicopter -> Crashing spin-down -> Ground impact wreck + splash
        {
            (Simulation sim, FlightSystem flight, CombatSystem combat, _) = CreateSim(flightProfile: airborneProfile);
            using (sim)
            {
                EntityId heli = sim.World.CreateUnit(1, DefHeli, new Vector2(200f, 150f), 68f, 40, 1200, passengerCapacity: 24);
                sim.World.SetFireStance(heli, FireStance.HoldFire);
                EntityId passenger = sim.World.CreateUnit(1, DefInfantry, new Vector2(200f, 150f), 3.5f, 15, 0);
                sim.World.Board(heli, passenger);

                sim.World.CreateUnit(2, DefAaApc, new Vector2(400f, 150f), 10f, 60, 500);
                sim.Flight!.EnsureUnitsInitialized(sim.World);

                bool observedCrashing = false;
                bool rejectedOrderDuringCrash = false;
                Vector2 crashStartPos = default;
                EntityId bystander = default;

                for (int i = 0; i < 400; i++)
                {
                    sim.Step();
                    if (!observedCrashing && sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState fs) && fs.Phase == FlightPhase.Crashing)
                    {
                        observedCrashing = true;
                        sim.World.TryGetUnit(heli, out UnitState crashingUnit);
                        crashStartPos = crashingUnit.Position;
                        rejectedOrderDuringCrash = !sim.TryEnqueue(1, new MoveCommand(99, sim.Tick, heli, new Vector2(100f, 100f)));
                    }
                    if (observedCrashing && bystander.Value == 0 && sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState fs2) && fs2.Altitude < 5f)
                    {
                        // Place a ground bystander right under the imminent impact point to verify crash splash damage
                        sim.World.TryGetUnit(heli, out UnitState lowUnit);
                        bystander = sim.World.CreateUnit(2, DefInfantry, lowUnit.Position + new Vector2(2f, 0f), 3.5f, 15, 0);
                    }
                    if (flight.TotalCrashImpacts > 0)
                        break;
                }

                Assert.True(observedCrashing, "Airborne helicopter should enter Crashing phase when shot down.");
                Assert.True(rejectedOrderDuringCrash, "Crashing helicopter must reject new player commands.");
                Assert.Equal(1, flight.TotalCrashImpacts);
                Assert.False(sim.World.TryGetUnit(heli, out _), "Crashed helicopter should be removed on ground impact.");
                Assert.False(sim.World.TryGetUnit(passenger, out _), "Passengers aboard crashed helicopter should perish on impact.");
                Assert.True(sim.World.WreckCount >= 1, "Crash impact must leave a helicopter wreck.");
                Assert.True(combat.KillCount >= 1, "Crash impact must attribute kill to the AA attacker.");
                if (bystander.Value != 0)
                {
                    Assert.False(sim.World.TryGetUnit(bystander, out _), "Ground bystander near impact should be killed by crash splash.");
                }
            }
        }
    }

    [Fact]
    public void Determinism_IdenticalRunsProduceIdenticalFlightTelemetry()
    {
        static (Vector2 pos, float alt, float pitch, float roll, float heading, FlightPhase phase) RunOnce()
        {
            (Simulation sim, _, _, _) = CreateSim(seed: 7777UL);
            using (sim)
            {
                EntityId heli = sim.World.CreateUnit(1, DefHeli, new Vector2(150f, 150f), 68f, 120, 1200);
                sim.Flight!.EnsureUnitsInitialized(sim.World);
                sim.TryEnqueue(1, new MoveCommand(1, 0, heli, new Vector2(540f, 480f)));
                for (int i = 0; i < 120; i++) sim.Step();
                sim.TryEnqueue(1, new HelicopterCommand(2, sim.Tick, heli, HelicopterOrderKind.SetAltitudeBand, AltitudeBand.Low));
                for (int i = 0; i < 80; i++) sim.Step();

                sim.World.TryGetUnit(heli, out UnitState u);
                sim.World.TryGetFlightState(heli, out WorldState.UnitFlightState fs);
                sim.World.TryGetCombatState(heli, out WorldState.CombatState cs);
                return (u.Position, fs.Altitude, fs.PitchRad, fs.RollRad, cs.HullHeading, fs.Phase);
            }
        }

        var runA = RunOnce();
        var runB = RunOnce();
        Assert.Equal(runA, runB);
    }

    [Fact]
    public void FlightSystem_HotPath_ZeroManagedAllocationsPerTick()
    {
        (Simulation sim, _, _, _) = CreateSim();
        using (sim)
        {
            EntityId h1 = sim.World.CreateUnit(1, DefHeli, new Vector2(120f, 120f), 68f, 120, 1200);
            EntityId h2 = sim.World.CreateUnit(1, DefHeli, new Vector2(180f, 120f), 68f, 120, 1200);
            sim.Flight!.EnsureUnitsInitialized(sim.World);

            sim.TryEnqueue(1, new MoveCommand(1, 0, h1, new Vector2(600f, 600f)));
            sim.TryEnqueue(1, new MoveCommand(2, 0, h2, new Vector2(620f, 600f)));

            // Warmup through takeoff and into active airborne flight so both phases and JIT paths are hot
            for (int i = 0; i < 140; i++) sim.Step();

            using NoGcAllocationWindow window = new();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 40; i++) sim.Step();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, window.Describe(allocated));
        }
    }

    [Fact]
    public void WireProtocol_V27_RoundTripsFlightStateAndRejectsV26Handshake()
    {
        Assert.Equal(31, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(31, WireProtocol.Version);

        // 1. HelicopterCommand round-trip
        byte[] cmdBuf = new byte[WireProtocol.HelicopterSize];
        HelicopterCommand cmd = new(42, 120, new EntityId(7), HelicopterOrderKind.Land, AltitudeBand.Low, HasTarget: true, Target: new Vector2(312.5f, 640.25f));
        WireProtocol.WriteHelicopter(cmdBuf, cmd);
        Assert.True(WireProtocol.TryReadHelicopter(cmdBuf, out HelicopterCommand decodedCmd));
        Assert.Equal(cmd, decodedCmd);

        // Reject outdated v26 packet
        cmdBuf[0] = 26;
        Assert.False(WireProtocol.TryReadHelicopter(cmdBuf, out _));

        // 2. OwnUnitObservation + ContactObservation flight fields round-trip
        OwnUnitObservation ownHeli = new(
            Id: new EntityId(7),
            Definition: 8,
            Position: new Vector2(220f, 340f),
            SupportSurface: 0,
            NavStatus: PathRequestStatus.None,
            Health: 160,
            Ammunition: 1200,
            ReloadTicksRemaining: 0,
            AttackStatus: AttackExecutionStatus.None,
            TargetContactId: default,
            Stance: FireStance.FireAtWill,
            TurretYaw: 0.5f,
            HullHeading: -0.75f,
            NavOrderSequence: 0,
            ContainerId: default,
            PassengerCount: 6,
            PassengerCapacity: 24,
            Altitude: 35.0f,
            FlightPhase: FlightPhase.Airborne,
            AltitudeBand: AltitudeBand.High,
            PitchRad: -0.14f,
            RollRad: 0.22f,
            HasFlightCapability: true);

        ContactObservation enemyHeli = new(
            Id: new ContactId(99),
            State: ContactState.Current,
            Classification: ContactClassification.Helicopter,
            Type: 8,
            Position: new Vector2(510f, 620f),
            LastSeenTick: 50,
            TurretYaw: 0f,
            HullHeading: 1.2f,
            SupportSurface: 0,
            Altitude: 12.0f,
            FlightPhase: FlightPhase.Airborne);

        byte[] snapBuf = new byte[2048];
        int len = WireProtocol.WriteSnapshot(snapBuf, sequence: 10, tick: 50, units: [ownHeli], contacts: [enemyHeli]);
        ClientReplica replica = new(64);
        Assert.True(replica.TryApply(snapBuf.AsSpan(0, len)));

        Assert.True(replica.TryGetOwnUnit(ownHeli.Id, out OwnUnitObservation decOwn));
        Assert.Equal(35.0f, decOwn.Altitude);
        Assert.Equal(FlightPhase.Airborne, decOwn.FlightPhase);
        Assert.Equal(AltitudeBand.High, decOwn.AltitudeBand);
        Assert.True(decOwn.HasFlightCapability);
        Assert.InRange(decOwn.PitchRad, -0.15f, -0.13f);
        Assert.InRange(decOwn.RollRad, 0.21f, 0.23f);

        Assert.True(replica.TryGetContact(enemyHeli.Id, out ContactObservation decContact));
        Assert.Equal(ContactClassification.Helicopter, decContact.Classification);
        Assert.Equal(12.0f, decContact.Altitude);
        Assert.Equal(FlightPhase.Airborne, decContact.FlightPhase);

        // 3. Handshake mismatch rejection for protocol v26
        MatchSetup serverSetup = new(MatchSetup.CurrentProtocolVersion, 12345UL, 67890UL, "Helicopter");
        MatchSetup staleClientSetup = serverSetup with { ProtocolVersion = 26 };
        Assert.False(serverSetup.Matches(staleClientSetup));

        byte[] hsBuf = new byte[256];
        int hsLen = WireProtocol.WriteHandshakeRequest(hsBuf, staleClientSetup);
        Assert.True(WireProtocol.TryReadHandshakeRequest(hsBuf.AsSpan(0, hsLen), out MatchSetup receivedStale));
        Assert.Equal((byte)26, receivedStale.ProtocolVersion);
        Assert.NotEqual(MatchSetup.CurrentProtocolVersion, receivedStale.ProtocolVersion);
    }
}
