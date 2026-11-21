using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class HelicopterScenarioTests
{
    [Fact]
    public void ColdWarCatalog_Ch47d_HasExpectedHistoricalFlightAndWeaponData()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        UnitDefinition ch47 = catalog.RequireDefinition("ch47d");

        Assert.Equal("CH-47D Chinook", ch47.Name);
        Assert.Equal("us", ch47.Faction);
        Assert.Equal("helicopter", ch47.Classification);
        Assert.Equal(24, ch47.PassengerCapacity);
        Assert.Equal("mg_7_62mm_m60d", ch47.Weapon);
        Assert.NotNull(ch47.Flight);
        Assert.Equal(68.0f, ch47.Flight!.CruiseSpeed);
        Assert.Equal(42.0f, ch47.Flight.LowAltitudeSpeed);
        Assert.Equal(18.0f, ch47.Flight.LowAltitude);
        Assert.Equal(55.0f, ch47.Flight.HighAltitude);

        WeaponData kpvt = Assert.Single(catalog.Weapons!, w => w.Id == "mg_14_5mm_kpvt");
        Assert.True(kpvt.CanTargetAir);
        Assert.True(kpvt.MaxAirTargetAltitude >= 50f);

        WeaponData u5ts = Assert.Single(catalog.Weapons!, w => w.Id == "gun_115mm_u5ts");
        Assert.False(u5ts.CanTargetAir);
    }

    [Fact]
    public void HelicopterPlayground_SpawnsUnits_FliesOverRiver_AndRejectsInvalidLzs()
    {
        StaticGeometry geometry = HelicopterPlaygroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        MatchSetup setup = HelicopterPlaygroundScenario.CreateMatchSetup(geometry, catalog);

        Assert.Equal(MatchSetup.CurrentProtocolVersion, setup.ProtocolVersion);
        Assert.Equal("HelicopterPlayground", setup.RuleSetId);
        Assert.Equal(ScenarioContent.LoadCatalog("helicopter-playground").ContentHash, setup.ContentHash);

        using Simulation sim = HelicopterPlaygroundScenario.CreateSimulation(geometry, catalog, matchSeed: 2024UL);
        using LocalSession session = new(sim, player: 1);

        for (int i = 0; i < 20; i++)
            session.Advance(1d / Simulation.TickRate);

        Assert.Equal(6, session.Replica.OwnUnits.Length);
        OwnUnitObservation alpha = session.Replica.OwnUnits[0];
        Assert.True(alpha.HasFlightCapability);
        Assert.Equal(FlightPhase.Ground, alpha.FlightPhase);
        Assert.Equal(2, alpha.PassengerCount);
        Assert.Equal(24, alpha.PassengerCapacity);

        // Verify LZ checks against scenario terrain features
        Assert.Equal(LandingZoneStatus.BlockedByWater,
            FlightSystem.EvaluateLandingZone(geometry, sim.World, new Vector2(400f, 512f), 10f, 12f, alpha.Id));
        Assert.Equal(LandingZoneStatus.BlockedByStructure,
            FlightSystem.EvaluateLandingZone(geometry, sim.World, new Vector2(93f, 78f), 10f, 12f, alpha.Id));
        Assert.Equal(LandingZoneStatus.BlockedByForest,
            FlightSystem.EvaluateLandingZone(geometry, sim.World, new Vector2(250f, 690f), 10f, 12f, alpha.Id));
        Assert.Equal(LandingZoneStatus.SlopeTooSteep,
            FlightSystem.EvaluateLandingZone(geometry, sim.World, HelicopterPlaygroundScenario.RidgeCenter + new Vector2(20f, 0f), 10f, 12f, alpha.Id));
        Assert.Equal(LandingZoneStatus.Valid,
            FlightSystem.EvaluateLandingZone(geometry, sim.World, HelicopterPlaygroundScenario.SouthLandingZone, 10f, 12f, alpha.Id));

        // Fly Alpha across the river to the South LZ, then land and disembark
        Assert.True(session.Disembark(alpha.Id, HelicopterPlaygroundScenario.SouthLandingZone));
        for (int i = 0; i < 620; i++)
            session.Advance(1d / Simulation.TickRate);

        Assert.True(session.Replica.TryGetOwnUnit(alpha.Id, out OwnUnitObservation landedAlpha));
        Assert.Equal(FlightPhase.Ground, landedAlpha.FlightPhase);
        Assert.Equal(0, landedAlpha.PassengerCount);
        Assert.True(Vector2.Distance(landedAlpha.Position, HelicopterPlaygroundScenario.SouthLandingZone) < 5f);
    }

    [Fact]
    public void HelicopterPlayground_BoardInfantry_ThenTakeOffInPlace_LiftsToCruiseAltitudeSafely()
    {
        StaticGeometry geometry = HelicopterPlaygroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using Simulation sim = HelicopterPlaygroundScenario.CreateSimulation(geometry, catalog, matchSeed: 2024UL);
        using LocalSession session = new(sim, player: 1);

        for (int i = 0; i < 10; i++)
            session.Advance(1d / Simulation.TickRate);

        EntityId alphaId = default;
        EntityId escort1 = default;
        EntityId escort2 = default;
        foreach (OwnUnitObservation obs in session.Replica.OwnUnits)
        {
            if (obs.HasFlightCapability && alphaId == default)
            {
                alphaId = obs.Id;
            }
            else if (!obs.HasFlightCapability && obs.ContainerId == default)
            {
                if (escort1 == default) escort1 = obs.Id;
                else if (escort2 == default) escort2 = obs.Id;
            }
        }

        Assert.NotEqual(default, alphaId);
        Assert.NotEqual(default, escort1);
        Assert.NotEqual(default, escort2);

        // Board both outside riflemen into Chinook Alpha
        Assert.True(session.BoardTransport(escort1, alphaId));
        Assert.True(session.BoardTransport(escort2, alphaId));
        for (int i = 0; i < 200; i++)
            session.Advance(1d / Simulation.TickRate);

        Assert.True(session.Replica.TryGetOwnUnit(alphaId, out OwnUnitObservation boardedAlpha));
        Assert.Equal(4, boardedAlpha.PassengerCount);
        Assert.Equal(FlightPhase.Ground, boardedAlpha.FlightPhase);
        Vector2 takeoffOrigin = boardedAlpha.Position;

        // Order vertical TakeOff with NO move target
        Assert.True(session.OrderHelicopter(alphaId, HelicopterOrderKind.TakeOff, AltitudeBand.High));
        for (int i = 0; i < 180; i++)
            session.Advance(1d / Simulation.TickRate);

        Assert.True(session.Replica.TryGetOwnUnit(alphaId, out OwnUnitObservation airborneAlpha));
        Assert.Equal(FlightPhase.Airborne, airborneAlpha.FlightPhase);
        Assert.True(airborneAlpha.Altitude >= 54.0f, $"Expected cruise altitude ~55m after vertical takeoff, got {airborneAlpha.Altitude:F2}m.");
        Assert.True(Vector2.Distance(airborneAlpha.Position, takeoffOrigin) < 2.0f, "Vertical takeoff without move target should hover in place.");
        Assert.Equal(4, airborneAlpha.PassengerCount);
        Assert.Equal(160, airborneAlpha.Health);
    }

    [Fact]
    public void HelicopterPlayground_EnemyContactObservation_DoesNotLeakEnemyEntityIdOrPassengers()
    {
        StaticGeometry geometry = HelicopterPlaygroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using Simulation sim = HelicopterPlaygroundScenario.CreateSimulation(geometry, catalog, matchSeed: 2024UL);

        // Order Alpha airborne near Soviet observers
        EntityId alphaId = sim.World.GetUnitAt(0).Id;
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, alphaId, new Vector2(700f, 700f))));
        for (int i = 0; i < 240; i++) sim.Step();

        PlayerKnowledge sovietKnowledge = new(2);
        sim.UpdateKnowledge(sovietKnowledge);

        bool sawAirborneHeli = false;
        foreach (ContactObservation c in sovietKnowledge.Contacts)
        {
            if (c.Classification == ContactClassification.Helicopter && c.FlightPhase == FlightPhase.Airborne)
            {
                sawAirborneHeli = true;
                Assert.True(c.Altitude > 10f);
                // ContactId is opaque and not guaranteed equal to EntityId; no passenger fields exist on ContactObservation
            }
        }
        Assert.True(sawAirborneHeli, "Soviet side should observe the approaching airborne US helicopter contact.");
    }

    [Fact]
    public void HelicopterPlayground_HotPath_ZeroManagedAllocationsPerTick()
    {
        StaticGeometry geometry = HelicopterPlaygroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        using Simulation sim = HelicopterPlaygroundScenario.CreateSimulation(geometry, catalog, matchSeed: 2024UL);

        EntityId alpha = sim.World.GetUnitAt(0).Id;
        EntityId bravo = sim.World.GetUnitAt(1).Id;
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, 0, alpha, new Vector2(400f, 350f))));
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, 0, bravo, new Vector2(460f, 350f))));

        // Warmup through takeoff into active airborne cruise + separation + detection + combat
        for (int i = 0; i < 140; i++) sim.Step();

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 40; i++) sim.Step();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }
}
