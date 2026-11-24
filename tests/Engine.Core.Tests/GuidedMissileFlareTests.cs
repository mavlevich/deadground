using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Tests;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class GuidedMissileFlareTests
{
    private const ushort DefHeli = 0;
    private const ushort DefSam = 1;
    private const ushort DefTank = 2;

    private sealed class TestPerceptionCatalog : IUnitPerceptionCatalog
    {
        private static readonly VisualSensorProfile Sensor = new(2600f, 2.5f);
        private static readonly TargetObservationPoints Points = new([1.0f, 2.5f]);
        private static readonly UnitPerceptionProfile HeliProfile = new(Sensor, Points, ContactClassification.Helicopter, 1.0f);
        private static readonly UnitPerceptionProfile SamProfile = new(Sensor, Points, ContactClassification.AntiAir, 1.0f);
        private static readonly UnitPerceptionProfile TankProfile = new(Sensor, Points, ContactClassification.Tank, 1.0f);

        public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile)
        {
            profile = definition switch
            {
                DefHeli => HeliProfile,
                DefSam => SamProfile,
                _ => TankProfile
            };
            return true;
        }
    }

    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile)
        {
            profile = new UnitMovementProfile(
                Speed: definition == DefHeli ? 50f : 10f,
                Radius: 1.5f,
                Capabilities: TraversabilityCapabilities.DefaultVehicle,
                TurnRate: 45f,
                ReverseSpeed: 3f,
                Acceleration: 3f,
                Deceleration: 5f,
                CanPivot: true,
                MinTurnRadius: 0f,
                CollisionRadius: 3.0f,
                Height: 2.6f);
            return true;
        }
    }

    private sealed class TestFlightCatalog : IUnitFlightCatalog
    {
        public bool TryGetFlightProfile(ushort definition, out FlightProfile profile)
        {
            if (definition == DefHeli)
            {
                profile = new FlightProfile(
                    CruiseSpeed: 55f,
                    LowAltitudeSpeed: 35f,
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
                    StartAirborne: false);
                return true;
            }
            profile = default;
            return false;
        }

        public bool TryGetCargoProfile(ushort definition, out CargoProfile cargo)
        {
            cargo = CargoProfile.NonTransportable;
            return true;
        }
    }

    private sealed class TestCombatCatalog(float irResistance = 0.25f, int samDamage = 120) : IUnitCombatCatalog
    {
        private readonly WeaponDefinition _samWeapon = new(
            Id: "test_sam",
            Range: 2000f,
            ReloadDurationTicks: 40,
            Damage: samDamage,
            AmmoCapacity: 8,
            MuzzleHeight: 2.5f,
            PresentationId: WeaponPresentation.MakeId(WeaponPresentationKind.SurfaceToAirMissile, 1),
            PenetrationNear: 35f,
            PenetrationFar: 35f,
            PenetrationNearRange: 0f,
            PenetrationFarRange: 2000f,
            RoundsPerBurst: 1,
            CanTargetAir: true,
            MaxAirTargetAltitude: 350f,
            MinRange: 180f,
            SplashRadius: 12f,
            ProjectileSpeed: 150f,
            GuidanceKind: MissileGuidanceKind.Infrared,
            AirTargetsOnly: true,
            MissileMaxTurnRateDegPerSec: 60f,
            MissileProximityFuzeRadius: 12f,
            IrCountermeasureResistance: irResistance,
            MissileMaxFlightTicks: 320,
            MissileMotorBurnTicks: 140);

        public bool TryGetProfile(ushort definition, out UnitCombatProfile profile)
        {
            if (definition == DefSam)
            {
                profile = new UnitCombatProfile(55, _samWeapon, 80f, new UnitArmor(10f, 7f, 7f), 1.5f);
                return true;
            }
            if (definition == DefHeli)
            {
                profile = new UnitCombatProfile(160, null, 0f, new UnitArmor(2f, 2f, 2f), 4.5f);
                return true;
            }
            profile = new UnitCombatProfile(100, null, 20f, new UnitArmor(200f, 80f, 45f), 1.8f);
            return true;
        }
    }

    private sealed class TestCountermeasureCatalog(float spoofProb = 0.78f, bool defaultAuto = true) : IUnitCountermeasureCatalog
    {
        public bool TryGetFlareProfile(ushort definition, out FlareProfile profile)
        {
            if (definition == DefHeli)
            {
                profile = new FlareProfile(
                    Charges: 8,
                    CooldownTicks: 80,
                    ActiveDurationTicks: 50,
                    DecoyRadius: 70f,
                    IrSpoofProbability: spoofProb,
                    DefaultAutoFlares: defaultAuto);
                return true;
            }
            profile = default;
            return false;
        }
    }

    private static (Simulation Sim, WorldState World) CreateTestHarness(
        float irSpoofProbability = 0.78f,
        float irResistance = 0.25f,
        bool defaultAutoFlares = true,
        int samDamage = 120,
        ulong seed = 7777UL)
    {
        HeightMap map = new(65, 65, 24f, 0.01f, new ushort[65 * 65]);
        StaticGeometry geometry = new(map, []);
        TestPerceptionCatalog perc = new();
        TestMovementCatalog move = new();
        TestFlightCatalog flightCat = new();
        TestCombatCatalog combatCat = new(irResistance, samDamage);
        TestCountermeasureCatalog cmCat = new(irSpoofProbability, defaultAutoFlares);

        FlightSystem flight = new(geometry, flightCat);
        NavigationSystem nav = new(new NavigationGrid(geometry, 4f), move);
        DetectionSystem det = new(geometry, perc);
        CombatSystem combat = new(geometry, combatCat, perc, det, nav, flight: flight);
        GuidedMissileSystem missiles = new(geometry, cmCat, combatCat);

        RuleSet rules = new(flight, nav, det, combat, missiles);
        WorldState world = new(map);
        Simulation sim = new(world, rules, seed);
        return (sim, world);
    }

    [Fact]
    public void SamLauncher_TracksAndHitsAirborneHelicopter_AndTriggersCrashOnLethalDamage()
    {
        (Simulation sim, WorldState world) = CreateTestHarness(irSpoofProbability: 0f, defaultAutoFlares: false, samDamage: 180);
        using (sim)
        {
            EntityId heli = world.CreateUnit(1, DefHeli, new Vector2(500f, 300f), 50f, 160, 0, MathF.PI);
            EntityId sam = world.CreateUnit(2, DefSam, new Vector2(500f, 1000f), 10f, 55, 8, 0f);
            sim.Flight!.EnsureUnitsInitialized(world);
            sim.GuidedMissiles!.EnsureUnitsInitialized(world);
            Assert.True(world.TryGetFlightState(heli, out WorldState.UnitFlightState fs));
            world.SetFlightState(heli, fs with { Phase = FlightPhase.Airborne, Altitude = 55f, DesiredBand = AltitudeBand.High });

            bool sawMissileInFlight = false;
            bool sawPitchUp = false;
            bool sawCrashing = false;

            for (int t = 0; t < 240; t++)
            {
                sim.Step();
                if (world.GuidedMissileCount > 0) sawMissileInFlight = true;
                if (world.TryGetArtilleryState(sam, out WorldState.UnitArtilleryState art) && art.GunPitchRad > 0.05f)
                    sawPitchUp = true;
                if (world.IsCrashing(heli))
                {
                    sawCrashing = true;
                    break;
                }
            }

            Assert.True(sawMissileInFlight, "SAM should have launched a guided missile in flight.");
            Assert.True(sawPitchUp, "SAM canister rack should pitch upward toward the airborne target.");
            Assert.True(sawCrashing, "Lethal SAM proximity hit should trigger helicopter crash descent.");
            Assert.True(sim.GuidedMissiles!.TotalMissilesHit >= 1);
        }
    }

    [Fact]
    public void SamLauncher_AirTargetsOnly_IgnoresGroundTankAndRespectsMinRange()
    {
        (Simulation sim, WorldState world) = CreateTestHarness(defaultAutoFlares: false);
        using (sim)
        {
            world.CreateUnit(1, DefTank, new Vector2(500f, 600f), 10f, 100, 0, MathF.PI);
            EntityId closeHeli = world.CreateUnit(1, DefHeli, new Vector2(500f, 920f), 50f, 160, 0, MathF.PI);
            EntityId sam = world.CreateUnit(2, DefSam, new Vector2(500f, 1000f), 10f, 55, 8, 0f);
            sim.Flight!.EnsureUnitsInitialized(world);
            Assert.True(world.TryGetFlightState(closeHeli, out WorldState.UnitFlightState fs));
            world.SetFlightState(closeHeli, fs with { Phase = FlightPhase.Airborne, Altitude = 55f, DesiredBand = AltitudeBand.High });

            for (int t = 0; t < 120; t++)
            {
                sim.Step();
            }

            Assert.Equal(0, sim.Combat!.TotalShotsFired);
            Assert.Equal(0, world.GuidedMissileCount);
            Assert.True(world.TryGetCombatState(sam, out WorldState.CombatState cs));
            Assert.Equal(8, cs.Ammunition);
        }
    }

    [Fact]
    public void FlareCountermeasures_DecoyIncomingInfraredSam_IntoHarmlessAirburst()
    {
        (Simulation sim, WorldState world) = CreateTestHarness(
            irSpoofProbability: 1.0f,
            irResistance: 0.0f,
            defaultAutoFlares: true,
            samDamage: 120);
        using (sim)
        {
            EntityId heli = world.CreateUnit(1, DefHeli, new Vector2(500f, 350f), 50f, 160, 0, MathF.PI);
            world.CreateUnit(2, DefSam, new Vector2(500f, 1050f), 10f, 55, 8, 0f);
            sim.Flight!.EnsureUnitsInitialized(world);
            sim.GuidedMissiles!.EnsureUnitsInitialized(world);
            Assert.True(world.TryGetFlightState(heli, out WorldState.UnitFlightState fs));
            world.SetFlightState(heli, fs with { Phase = FlightPhase.Airborne, Altitude = 55f, DesiredBand = AltitudeBand.High });

            bool sawDecoyedFlag = false;
            bool sawWarning = false;
            bool sawWarningBeforeFlares = false;
            float maxLateralVeer = 0f;
            for (int t = 0; t < 260; t++)
            {
                sim.Step();
                if ((world.GetCountermeasureFlags(heli) & CountermeasureFlags.MissileWarning) != 0)
                {
                    sawWarning = true;
                    if (sim.GuidedMissiles!.TotalFlaresDeployed == 0)
                        sawWarningBeforeFlares = true;
                }
                for (int m = 0; m < world.GuidedMissileCount; m++)
                {
                    ref readonly GuidedMissileState ms = ref world.GetGuidedMissileRef(m);
                    if (ms.Decoyed)
                    {
                        sawDecoyedFlag = true;
                        maxLateralVeer = MathF.Max(maxLateralVeer, MathF.Abs(ms.Position.X - 500f));
                    }
                }
                if (sim.GuidedMissiles!.TotalMissilesSpoofed >= 1 && sim.GuidedMissiles.TotalMissilesSelfDestructed >= 1)
                    break;
            }

            Assert.True(sawWarning, "Aircraft should raise MissileWarning when SAM approaches.");
            Assert.True(sawWarningBeforeFlares, "Auto-flares should wait until the SAM closes in rather than firing immediately at launch.");
            Assert.True(sim.GuidedMissiles!.TotalFlaresDeployed >= 1, "Auto-flares should fire against incoming SAM.");
            Assert.True(sawDecoyedFlag, "Guaranteed IR flare burst must physically decoy the missile.");
            Assert.True(maxLateralVeer >= 8f, $"Decoyed missile should visibly steer into an actual falling lateral flare, observed max lateral offset {maxLateralVeer:F1}m.");
            Assert.True(sim.GuidedMissiles.TotalMissilesSpoofed >= 1);
            Assert.True(sim.GuidedMissiles.TotalMissilesSelfDestructed >= 1);
            Assert.Equal(0, sim.GuidedMissiles.TotalMissilesHit);
            Assert.True(world.TryGetCombatState(heli, out WorldState.CombatState heliCs));
            Assert.Equal(160, heliCs.CurrentHealth);
        }
    }

    [Fact]
    public void SpoofProbability_RewardsManeuveringAndBeamingOverHovering()
    {
        Vector2 incomingNorth = new(0f, -145f);
        float hoverProb = GuidedMissileSystem.ComputeEffectiveSpoofProbability(0.78f, 0.25f, Vector2.Zero, incomingNorth);
        float inlineCruiseProb = GuidedMissileSystem.ComputeEffectiveSpoofProbability(0.78f, 0.25f, new Vector2(0f, 50f), incomingNorth);
        float beamingProb = GuidedMissileSystem.ComputeEffectiveSpoofProbability(0.78f, 0.25f, new Vector2(50f, 0f), incomingNorth);

        Assert.InRange(hoverProb, 0.38f, 0.46f);
        Assert.True(inlineCruiseProb > hoverProb + 0.18f, $"Moving aircraft ({inlineCruiseProb:F2}) should decoy better than hovering ({hoverProb:F2}).");
        Assert.True(beamingProb > inlineCruiseProb + 0.05f, $"Perpendicular beaming maneuver ({beamingProb:F2}) should beat inline flight ({inlineCruiseProb:F2}).");
        Assert.InRange(beamingProb, 0.70f, 0.80f);
    }

    [Fact]
    public void ManualDeployFlares_AndToggleAutoFlares_ValidateChargesCooldownAndAirborneState()
    {
        (Simulation sim, WorldState world) = CreateTestHarness(defaultAutoFlares: true);
        using (sim)
        {
            EntityId heli = world.CreateUnit(1, DefHeli, new Vector2(500f, 500f), 50f, 160, 0, 0f);
            sim.Flight!.EnsureUnitsInitialized(world);
            sim.GuidedMissiles!.EnsureUnitsInitialized(world);

            // Grounded helicopter cannot manually deploy flares, but can toggle AutoFlares
            Assert.False(sim.TryEnqueue(1, new DeployFlaresCommand(1, 0, heli, ToggleAuto: false)));
            Assert.True(sim.TryEnqueue(1, new DeployFlaresCommand(2, 0, heli, ToggleAuto: true)));
            sim.Step();
            Assert.Equal(CountermeasureFlags.HasFlares, world.GetCountermeasureFlags(heli));

            // Make airborne and manually fire flares
            Assert.True(world.TryGetFlightState(heli, out WorldState.UnitFlightState fs));
            world.SetFlightState(heli, fs with { Phase = FlightPhase.Airborne, Altitude = 40f, DesiredBand = AltitudeBand.High });
            Assert.True(sim.TryEnqueue(1, new DeployFlaresCommand(3, 1, heli, ToggleAuto: false)));
            sim.Step();

            Assert.Equal(1, sim.GuidedMissiles.TotalFlaresDeployed);
            Assert.True(world.TryGetCountermeasureState(heli, out WorldState.UnitCountermeasureState cm));
            Assert.Equal((byte)7, cm.ChargesRemaining);
            Assert.True(cm.CooldownTicksRemaining > 0);
            Assert.True((world.GetCountermeasureFlags(heli) & CountermeasureFlags.FlaresActive) != 0);

            // Cooldown rejects immediate second burst
            Assert.False(sim.TryEnqueue(1, new DeployFlaresCommand(4, 2, heli, ToggleAuto: false)));
        }
    }

    [Fact]
    public void WireProtocolV31_RoundTripsFlaresAndGuidedMissiles_AndRejectsV30Handshake()
    {
        Assert.Equal(31, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(31, WireProtocol.Version);
        Assert.Equal(73, WireProtocol.UnitSize);
        Assert.Equal(31, WireProtocol.MissileSize);
        Assert.Equal(15, WireProtocol.DeployFlaresSize);

        // 1. DeployFlaresCommand round-trip
        byte[] cmdBuf = new byte[WireProtocol.DeployFlaresSize];
        DeployFlaresCommand cmd = new(42, 100, new EntityId(7), ToggleAuto: true);
        WireProtocol.WriteDeployFlares(cmdBuf, cmd);
        Assert.True(WireProtocol.TryReadDeployFlares(cmdBuf, out DeployFlaresCommand decodedCmd));
        Assert.Equal(cmd, decodedCmd);

        // Reject outdated v30 packet and invalid toggle byte
        cmdBuf[0] = 30;
        Assert.False(WireProtocol.TryReadDeployFlares(cmdBuf, out _));
        cmdBuf[0] = WireProtocol.Version;
        cmdBuf[14] = 2;
        Assert.False(WireProtocol.TryReadDeployFlares(cmdBuf, out _));

        // 2. Snapshot round-trip with flare state, AntiAir contact, SAM/flare events, and active missiles
        OwnUnitObservation heli = new(
            new EntityId(7),
            DefHeli,
            new Vector2(400f, 500f),
            0,
            FlareCharges: 6,
            FlareCooldownTicks: 45,
            CountermeasureFlags: CountermeasureFlags.HasFlares | CountermeasureFlags.AutoFlares | CountermeasureFlags.MissileWarning);
        ContactObservation samContact = new(
            new ContactId(9),
            ContactState.Current,
            ContactClassification.AntiAir,
            DefSam,
            new Vector2(400f, 1100f),
            10,
            TurretYaw: 0.25f,
            HullHeading: 0f,
            GunPitchRad: 0.45f);
        CombatEventObservation ev = new(
            EventId: 101,
            ServerTick: 12,
            Kind: CombatEventKind.FlaresDeployed,
            PresentationId: DefHeli,
            Origin: new Vector3(400f, 55f, 500f),
            Target: new Vector3(400f, 55f, 510f),
            ShooterUnitId: new EntityId(7));
        GuidedMissileObservation m1 = new(1, 0x5001, new Vector3(400f, 40f, 800f), new Vector3(0f, 12f, -140f), GuidedMissileFlags.MotorBurning);
        GuidedMissileObservation m2 = new(1, 0x5001, new Vector3(400f, 42f, 772f), new Vector3(0f, 10f, -140f), GuidedMissileFlags.MotorBurning | GuidedMissileFlags.Decoyed);

        byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
        ClientReplica replica = new();

        int len1 = WireProtocol.WriteSnapshot(snapBuf, 1, 10, [heli], [samContact], [ev], default, default, default, [m1]);
        Assert.True(replica.TryApply(snapBuf.AsSpan(0, len1)));
        int len2 = WireProtocol.WriteSnapshot(snapBuf, 2, 12, [heli], [samContact], default, default, default, default, [m2]);
        Assert.True(replica.TryApply(snapBuf.AsSpan(0, len2)));

        Assert.True(replica.TryGetOwnUnit(new EntityId(7), out OwnUnitObservation decodedHeli));
        Assert.Equal((byte)6, decodedHeli.FlareCharges);
        Assert.Equal((ushort)45, decodedHeli.FlareCooldownTicks);
        Assert.Equal(CountermeasureFlags.HasFlares | CountermeasureFlags.AutoFlares | CountermeasureFlags.MissileWarning, decodedHeli.CountermeasureFlags);

        Assert.True(replica.TryGetContact(new ContactId(9), out ContactObservation decodedSam));
        Assert.Equal(ContactClassification.AntiAir, decodedSam.Classification);
        Assert.Single(replica.Missiles.ToArray());

        Assert.True(replica.SampleMissilePose(1, 11.0, out Vector3 midPos, out _, out GuidedMissileFlags midFlags));
        Assert.InRange(midPos.Z, 785.5f, 786.5f);
        Assert.True((midFlags & GuidedMissileFlags.MotorBurning) != 0);

        // 3. Handshake rejects v30 client connecting to v31 server
        HeightMap map = new(17, 17, 8f, 0.01f, new ushort[17 * 17]);
        StaticGeometry geo = new(map, []);
        MatchSetup serverSetup = new(MatchSetup.CurrentProtocolVersion, 12345UL, MatchSetup.ComputeMapHash(geo), "AirDefense");
        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        WorldState world = new(map);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using SimulationHost host = new(sim, serverTransport, serverSetup);

        MatchSetup v30ClientSetup = serverSetup with { ProtocolVersion = 30 };
        using ClientSession client = ClientSession.Connect("127.0.0.1", serverTransport.Port, v30ClientSetup);
        for (int i = 0; i < 150 && client.State != SessionConnectionState.Failed; i++)
        {
            host.Advance(Simulation.TickSeconds);
            client.Advance(Simulation.TickSeconds);
            Thread.Sleep(5);
        }

        Assert.Equal(SessionConnectionState.Failed, client.State);
        Assert.Contains("ProtocolVersionMismatch", client.StatusDescription);
    }

    [Fact]
    public void HiddenSamLauncher_ConcealsLaunchEventAndInitialFlight_ThenRevealsMissileIn3DLos()
    {
        // Helicopter has 1500m ground sensor (cannot spot Strela-10 at 1750m),
        // while SAM has 2600m sensor and 2000m weapon range.
        HeightMap map = new(129, 129, 16f, 0.01f, new ushort[129 * 129]);
        StaticGeometry geometry = new(map, []);
        ShortHeliPerceptionCatalog perc = new();
        TestMovementCatalog move = new();
        TestFlightCatalog flight = new();
        TestCombatCatalog combat = new(irResistance: 0.25f, samDamage: 30);
        TestCountermeasureCatalog cm = new(spoofProb: 0.0f, defaultAuto: false);

        NavigationSystem nav = new(new NavigationGrid(geometry, 16f), move);
        FlightSystem flightSys = new(geometry, flight);
        DetectionSystem det = new(geometry, perc, new DetectionTuning(evaluationIntervalTicks: 1));
        CombatSystem combatSys = new(geometry, combat, perc, det, nav, combatRandom: new DeterministicRandom(77), flight: flightSys);
        GuidedMissileSystem missileSys = new(geometry, cm, combat);
        WorldState world = new(map);
        using Simulation sim = new(world, new RuleSet(flightSys, nav, det, combatSys, missileSys), matchSeed: 77);

        Vector2 heliPos = new(1000f, 150f);
        Vector2 samPos = new(1000f, 1900f); // 1750m away: > 1500m heli sensor, < 2000m SAM range
        EntityId heli = world.CreateUnit(1, DefHeli, heliPos, 50f, 160, 0, MathF.PI);
        EntityId sam = world.CreateUnit(2, DefSam, samPos, 10f, 55, 8, 0f);
        flightSys.EnsureUnitsInitialized(world);
        missileSys.EnsureUnitsInitialized(world);
        Assert.True(world.TryGetFlightState(heli, out WorldState.UnitFlightState fs));
        world.SetFlightState(heli, fs with { Phase = FlightPhase.Airborne, Altitude = 55f, DesiredBand = AltitudeBand.High });

        PlayerKnowledge p1 = new(1);
        PlayerKnowledge p2 = new(2);
        int launchedTick = -1;
        bool p1SawLaunchEvent = false;
        bool p2SawLaunchEvent = false;
        bool p1SawMissileDuringConcealment = false;
        bool p2SawMissileAtLaunch = false;
        Vector3 firstRevealedPos = default;
        bool p1RevealedMissileLater = false;

        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            sim.UpdateKnowledge(p1);
            sim.UpdateKnowledge(p2);

            Assert.False(det.IsEntityObservedBy(1, sam), "SAM at 1750m must remain unspotted by 1500m helicopter.");

            ReadOnlySpan<CombatEventObservation> p1Evs = p1.Events;
            for (int e = 0; e < p1Evs.Length; e++)
            {
                if (p1Evs[e].Kind == CombatEventKind.MissileLaunched)
                    p1SawLaunchEvent = true;
            }
            sim.AcknowledgeCombatEvents(1, p1Evs.Length);

            ReadOnlySpan<CombatEventObservation> p2Evs = p2.Events;
            for (int e = 0; e < p2Evs.Length; e++)
            {
                if (p2Evs[e].Kind == CombatEventKind.MissileLaunched)
                    p2SawLaunchEvent = true;
            }
            sim.AcknowledgeCombatEvents(2, p2Evs.Length);

            if (launchedTick < 0 && world.GuidedMissileCount > 0)
            {
                launchedTick = world.GetGuidedMissileRef(0).LaunchedTick;
                if (p2.Missiles.Length > 0)
                    p2SawMissileAtLaunch = true;
            }

            if (launchedTick >= 0)
            {
                int flightAge = sim.Tick - launchedTick;
                if (flightAge < DetectionSystem.HiddenLaunchConcealmentTicks)
                {
                    if (p1.Missiles.Length > 0)
                        p1SawMissileDuringConcealment = true;
                }
                else if (!p1RevealedMissileLater && p1.Missiles.Length > 0)
                {
                    p1RevealedMissileLater = true;
                    firstRevealedPos = p1.Missiles[0].Position;
                }
            }

            if (p1RevealedMissileLater)
                break;
        }

        Assert.True(launchedTick >= 0, "Expected hidden SAM to launch a guided missile.");
        Assert.True(p2SawLaunchEvent, "SAM owner must receive MissileLaunched event.");
        Assert.True(p2SawMissileAtLaunch, "SAM owner must observe its own missile immediately.");
        Assert.False(p1SawLaunchEvent, "Unspotted SAM must not leak MissileLaunched ground origin to opponent.");
        Assert.False(p1SawMissileDuringConcealment, "Missile from unspotted SAM must remain concealed during initial launch ticks.");
        Assert.True(p1RevealedMissileLater, "Burning SAM plume must become visible once it clears launch concealment inside 3D LOS.");

        float distFromLauncher = Vector2.Distance(new Vector2(firstRevealedPos.X, firstRevealedPos.Z), samPos);
        Assert.True(distFromLauncher >= 85f, $"First revealed missile position must stay well clear of hidden launcher, got {distFromLauncher:F1}m.");
    }

    [Fact]
    public void HiddenSamBehindOpaqueCover_HidesMissileUntil3DLosClearsCover_WhileSpottedSamRevealsImmediately()
    {
        HeightMap map = new(129, 129, 16f, 0.01f, new ushort[129 * 129]);
        // Tall 60m wall standing right in front of the SAM at z=1050 so low/mid flight is occluded from z=300
        OpaqueStructure wall = new(new StructureId(99), 900f, 0f, 1040f, 1100f, 60f, 1060f, isGrounded: true);
        StaticGeometry geometry = new(map, [wall]);
        TestPerceptionCatalog perc = new();
        TestMovementCatalog move = new();
        TestFlightCatalog flight = new();
        TestCombatCatalog combat = new();
        TestCountermeasureCatalog cm = new(spoofProb: 0f, defaultAuto: false);

        NavigationSystem nav = new(new NavigationGrid(geometry, 16f), move);
        FlightSystem flightSys = new(geometry, flight);
        DetectionSystem det = new(geometry, perc, new DetectionTuning(evaluationIntervalTicks: 1));
        CombatSystem combatSys = new(geometry, combat, perc, det, nav, combatRandom: new DeterministicRandom(91), flight: flightSys);
        GuidedMissileSystem missileSys = new(geometry, cm, combat);
        WorldState world = new(map);
        using Simulation sim = new(world, new RuleSet(flightSys, nav, det, combatSys, missileSys), matchSeed: 91);

        EntityId observer = world.CreateUnit(1, DefTank, new Vector2(1000f, 300f), 10f, 100, 0, MathF.PI);
        EntityId sam = world.CreateUnit(2, DefSam, new Vector2(1000f, 1100f), 10f, 55, 8, 0f);
        sim.Step();
        Assert.False(det.IsEntityObservedBy(1, sam), "Wall must hide SAM from Player 1 observer.");

        // Spawn a hostile missile behind the wall past the concealment tick window:
        // still occluded by the 60m wall at y=15m, then visible once above the wall at y=95m.
        GuidedMissileState occludedMissile = new()
        {
            Owner = 2,
            ShooterId = sam,
            ShooterDefinition = DefSam,
            TargetId = observer,
            PresentationId = WeaponPresentation.MakeId(WeaponPresentationKind.SurfaceToAirMissile, 1),
            Guidance = MissileGuidanceKind.Infrared,
            Position = new Vector3(1000f, 15f, 1080f),
            Velocity = new Vector3(0f, 35f, -120f),
            Speed = 145f,
            LaunchedTick = 0,
            MaxFlightTicks = 200,
            MotorBurnTicks = 140
        };
        Assert.True(world.TryAddGuidedMissile(occludedMissile, out _));

        Span<GuidedMissileObservation> buf = stackalloc GuidedMissileObservation[4];
        int tickAfterConcealment = DetectionSystem.HiddenLaunchConcealmentTicks + 5;
        int hiddenCount = det.CopyVisibleGuidedMissiles(world, player: 1, currentTick: tickAfterConcealment, buf);
        Assert.Equal(0, hiddenCount);

        // Move missile above the 60m wall into clear 3D sky LOS
        ref GuidedMissileState live = ref world.GetGuidedMissileRef(0);
        live.Position = new Vector3(1000f, 95f, 1080f);
        int revealedCount = det.CopyVisibleGuidedMissiles(world, player: 1, currentTick: tickAfterConcealment, buf);
        Assert.Equal(1, revealedCount);

        // Conversely, place a Player 1 spotter behind the wall with direct LOS to the SAM:
        // once the SAM is observed, even a tick-0 missile right at the launcher is immediately visible.
        world.CreateUnit(1, DefTank, new Vector2(1000f, 1200f), 10f, 100, 0, 0f);
        sim.Step();
        Assert.True(det.IsEntityObservedBy(1, sam), "Flanking spotter must observe the SAM directly.");

        live.Position = new Vector3(1000f, 3f, 1100f);
        live.LaunchedTick = sim.Tick;
        int immediateCount = det.CopyVisibleGuidedMissiles(world, player: 1, currentTick: sim.Tick, buf);
        Assert.Equal(1, immediateCount);
    }

    private sealed class ShortHeliPerceptionCatalog : IUnitPerceptionCatalog
    {
        private static readonly VisualSensorProfile HeliSensor = new(1500f, 2.5f);
        private static readonly VisualSensorProfile SamSensor = new(2600f, 2.5f);
        private static readonly TargetObservationPoints Points = new([1.0f, 2.5f]);
        private static readonly UnitPerceptionProfile HeliProfile = new(HeliSensor, Points, ContactClassification.Helicopter, 1.0f);
        private static readonly UnitPerceptionProfile SamProfile = new(SamSensor, Points, ContactClassification.AntiAir, 1.0f);

        public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile)
        {
            profile = definition == DefHeli ? HeliProfile : SamProfile;
            return true;
        }
    }

    [Fact]
    public void GuidedMissileAndFlareLoop_HotPath_ZeroManagedAllocationsPerTick()
    {
        (Simulation sim, WorldState world) = CreateTestHarness(irSpoofProbability: 0.78f, defaultAutoFlares: true, samDamage: 30);
        using (sim)
        {
            EntityId heli = world.CreateUnit(1, DefHeli, new Vector2(500f, 350f), 50f, 160, 0, MathF.PI);
            world.CreateUnit(2, DefSam, new Vector2(500f, 1150f), 10f, 55, 8, 0f);
            sim.Flight!.EnsureUnitsInitialized(world);
            sim.GuidedMissiles!.EnsureUnitsInitialized(world);
            Assert.True(world.TryGetFlightState(heli, out WorldState.UnitFlightState fs));
            world.SetFlightState(heli, fs with { Phase = FlightPhase.Airborne, Altitude = 55f, DesiredBand = AltitudeBand.High });

            PlayerKnowledge p1Knowledge = new(1);
            PlayerKnowledge p2Knowledge = new(2);

            // Warm up detection, missile launch, knowledge visibility filtering, and close-in auto-flare paths
            for (int t = 0; t < 80; t++)
            {
                sim.Step();
                sim.UpdateKnowledge(p1Knowledge);
                sim.UpdateKnowledge(p2Knowledge);
                ReadOnlySpan<CombatEventObservation> warmupEvs = sim.Combat!.GetEvents(1);
                sim.Combat.AcknowledgeEvents(1, warmupEvs.Length);
            }

            using NoGcAllocationWindow window = new();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int t = 0; t < 90; t++)
            {
                sim.Step();
                sim.UpdateKnowledge(p1Knowledge);
                sim.UpdateKnowledge(p2Knowledge);
                ReadOnlySpan<CombatEventObservation> evs = sim.Combat!.GetEvents(1);
                sim.Combat.AcknowledgeEvents(1, evs.Length);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, window.Describe(allocated));
            Assert.True(sim.Combat!.TotalShotsFired >= 1);
            Assert.True(sim.GuidedMissiles.TotalFlaresDeployed >= 1);
        }
    }
}
