using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Dedicated test and playable scenario for guided surface-to-air missiles (9K35 Strela-10)
/// and helicopter infrared flare countermeasures (CH-47D Chinook M130 dispensers).
/// Features a 2048m x 2048m valley with a central ridge for terrain-masking NOE play:
/// Player 1 (US) commands a 6-helicopter CH-47D Chinook air-assault wave (4 High-altitude
/// aircraft with 2 actively maneuvering across the valley and 2 hovering, plus 2 Low-altitude
/// NOE aircraft masked behind the ridge) alongside a ridge-crest M151A2 spotter and M60A1 tank;
/// Player 2 (Soviet) deploys a 3-launcher 9K35 Strela-10 SAM battery and a BRDM-2 scout.
/// </summary>
public static class AirDefensePlaygroundScenario
{
    public const string ScenarioName = "air-defense-playground";
    public const int MapResolution = 129;
    public const float MapSpacing = 16.0f; // 128 * 16m = 2048m x 2048m
    public const float MapSize = (MapResolution - 1) * MapSpacing;
    public const float BaseHeight = 4.0f;
    public const float RidgeZ = 980.0f;
    public const float RidgeHalfWidth = 210.0f;
    public const float RidgePeakHeight = 28.0f;

    // Player 1 (US): 6 airborne CH-47D Chinooks + ridge spotter + escort tank
    public static readonly Vector2 ChinookAlphaPos = new(830.0f, 610.0f);   // High, maneuvering east
    public static readonly Vector2 ChinookBravoPos = new(960.0f, 430.0f);   // Low NOE behind ridge
    public static readonly Vector2 ChinookCharliePos = new(1220.0f, 610.0f); // High, maneuvering west
    public static readonly Vector2 ChinookDeltaPos = new(965.0f, 650.0f);   // High, hovering
    public static readonly Vector2 ChinookEchoPos = new(1090.0f, 650.0f);   // High, hovering
    public static readonly Vector2 ChinookFoxtrotPos = new(1110.0f, 430.0f); // Low NOE behind ridge

    public static readonly Vector2 RidgeScoutPos = new(1024.0f, 965.0f);
    public static readonly Vector2 RidgeTankPos = new(960.0f, 955.0f);

    // Player 2 (Soviet): 3 x 9K35 Strela-10 SAM battery + BRDM-2 scout
    public static readonly Vector2 StrelaAlphaPos = new(900.0f, 1410.0f);
    public static readonly Vector2 StrelaBravoPos = new(1024.0f, 1430.0f);
    public static readonly Vector2 StrelaCharliePos = new(1148.0f, 1410.0f);
    public static readonly Vector2 SovietScoutPos = new(1024.0f, 1340.0f);

    public static HeightMap CreateHeightMap()
    {
        ushort[] heights = new ushort[MapResolution * MapResolution];
        for (int r = 0; r < MapResolution; r++)
        {
            float z = r * MapSpacing;
            int rowOffset = r * MapResolution;
            float dz = MathF.Abs(z - RidgeZ);
            float ridgeFactor = dz < RidgeHalfWidth
                ? MathF.Cos((dz / RidgeHalfWidth) * (MathF.PI * 0.5f))
                : 0f;
            float ridgeLift = RidgePeakHeight * ridgeFactor * ridgeFactor;

            for (int c = 0; c < MapResolution; c++)
            {
                float x = c * MapSpacing;
                float gentleRoll = MathF.Sin(x * 0.0042f) * MathF.Cos(z * 0.0036f) * 2.0f;
                float h = BaseHeight + ridgeLift + gentleRoll;
                heights[rowOffset + c] = (ushort)Math.Clamp((int)MathF.Round(h * 100f), 0, ushort.MaxValue);
            }
        }
        return new HeightMap(MapResolution, MapResolution, MapSpacing, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry(HeightMap? map = null)
    {
        map ??= CreateHeightMap();

        VegetationZone westForest = VegetationZone.CreateDenseForest(
            "FOREST_AD_WEST",
            [
                new Vector2(440f, 1180f),
                new Vector2(720f, 1180f),
                new Vector2(720f, 1540f),
                new Vector2(440f, 1540f)
            ],
            canopyHeight: 12.0f,
            maxPenetration: 20.0f);

        VegetationZone eastForest = VegetationZone.CreateDenseForest(
            "FOREST_AD_EAST",
            [
                new Vector2(1320f, 1180f),
                new Vector2(1600f, 1180f),
                new Vector2(1600f, 1540f),
                new Vector2(1320f, 1540f)
            ],
            canopyHeight: 12.0f,
            maxPenetration: 20.0f);

        OpaqueStructure bunkerA = new(
            new StructureId(30),
            856f, 0f, 1442f,
            872f, 5.5f, 1460f,
            isGrounded: true);

        OpaqueStructure bunkerB = new(
            new StructureId(31),
            1176f, 0f, 1442f,
            1192f, 5.5f, 1460f,
            isGrounded: true);

        return new StaticGeometry(
            map,
            [bunkerA, bunkerB],
            [],
            [],
            [westForest, eastForest]);
    }

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        string ruleSetId = "AirDefensePlayground")
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerFactions: ["us", "soviet"]);
    }

    public static Simulation CreateSimulation(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        NavigationTuning? navTuning = null,
        ulong matchSeed = 2024UL)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();

        NavigationTuning effectiveNavTuning = navTuning ?? new NavigationTuning(CellSize: 2.0f);
        OpsRules rules = new(geometry, catalog, effectiveNavTuning);
        WorldState world = new(geometry.HeightMap);

        UnitDefinition ch47Def = catalog.RequireDefinition("ch47d");
        ushort ch47Idx = catalog.RequireDefinitionIndex("ch47d");
        int ch47Ammo = catalog.GetAmmoCapacity(ch47Def);

        UnitDefinition jeepDef = catalog.RequireDefinition("m151a2");
        ushort jeepIdx = catalog.RequireDefinitionIndex("m151a2");
        int jeepAmmo = catalog.GetAmmoCapacity(jeepDef);

        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        int m60Ammo = catalog.GetAmmoCapacity(m60Def);

        UnitDefinition strelaDef = catalog.RequireDefinition("strela10");
        ushort strelaIdx = catalog.RequireDefinitionIndex("strela10");
        int strelaAmmo = catalog.GetAmmoCapacity(strelaDef);

        UnitDefinition brdmDef = catalog.RequireDefinition("brdm2");
        ushort brdmIdx = catalog.RequireDefinitionIndex("brdm2");
        int brdmAmmo = catalog.GetAmmoCapacity(brdmDef);

        // Player 1 (US): 6 airborne CH-47Ds + ridge spotter + tank escort
        const float facingSouth = MathF.PI;
        EntityId alphaId = world.CreateUnit(1, ch47Idx, ChinookAlphaPos, ch47Def.Speed, ch47Def.MaxHealth, ch47Ammo, facingSouth, passengerCapacity: ch47Def.PassengerCapacity);
        EntityId bravoId = world.CreateUnit(1, ch47Idx, ChinookBravoPos, ch47Def.Speed, ch47Def.MaxHealth, ch47Ammo, facingSouth, passengerCapacity: ch47Def.PassengerCapacity);
        EntityId charlieId = world.CreateUnit(1, ch47Idx, ChinookCharliePos, ch47Def.Speed, ch47Def.MaxHealth, ch47Ammo, facingSouth, passengerCapacity: ch47Def.PassengerCapacity);
        EntityId deltaId = world.CreateUnit(1, ch47Idx, ChinookDeltaPos, ch47Def.Speed, ch47Def.MaxHealth, ch47Ammo, facingSouth, passengerCapacity: ch47Def.PassengerCapacity);
        EntityId echoId = world.CreateUnit(1, ch47Idx, ChinookEchoPos, ch47Def.Speed, ch47Def.MaxHealth, ch47Ammo, facingSouth, passengerCapacity: ch47Def.PassengerCapacity);
        EntityId foxtrotId = world.CreateUnit(1, ch47Idx, ChinookFoxtrotPos, ch47Def.Speed, ch47Def.MaxHealth, ch47Ammo, facingSouth, passengerCapacity: ch47Def.PassengerCapacity);
        EntityId scoutId = world.CreateUnit(1, jeepIdx, RidgeScoutPos, jeepDef.Speed, jeepDef.MaxHealth, jeepAmmo, facingSouth);
        EntityId tankId = world.CreateUnit(1, m60Idx, RidgeTankPos, m60Def.Speed, m60Def.MaxHealth, m60Ammo, facingSouth);
        world.SetFireStance(scoutId, FireStance.HoldFire);
        world.SetFireStance(tankId, FireStance.ReturnFire);

        // Player 2 (Soviet): 3 x 9K35 Strela-10 SAM vehicles in FireAtWill + BRDM-2 scout in HoldFire
        const float facingNorth = 0f;
        world.CreateUnit(2, strelaIdx, StrelaAlphaPos, strelaDef.Speed, strelaDef.MaxHealth, strelaAmmo, facingNorth);
        world.CreateUnit(2, strelaIdx, StrelaBravoPos, strelaDef.Speed, strelaDef.MaxHealth, strelaAmmo, facingNorth);
        world.CreateUnit(2, strelaIdx, StrelaCharliePos, strelaDef.Speed, strelaDef.MaxHealth, strelaAmmo, facingNorth);
        EntityId sovScoutId = world.CreateUnit(2, brdmIdx, SovietScoutPos, brdmDef.Speed, brdmDef.MaxHealth, brdmAmmo, facingNorth);
        world.SetFireStance(sovScoutId, FireStance.HoldFire);

        Simulation sim = new(world, rules, matchSeed);

        if (ch47Def.Flight is not null)
        {
            float highAlt = ch47Def.Flight.HighAltitude;
            float lowAlt = ch47Def.Flight.LowAltitude;

            SetAirborne(world, alphaId, AltitudeBand.High, highAlt);
            SetAirborne(world, charlieId, AltitudeBand.High, highAlt);
            SetAirborne(world, deltaId, AltitudeBand.High, highAlt);
            SetAirborne(world, echoId, AltitudeBand.High, highAlt);
            SetAirborne(world, bravoId, AltitudeBand.Low, lowAlt);
            SetAirborne(world, foxtrotId, AltitudeBand.Low, lowAlt);

            // Give the two flank Chinooks active cross-valley maneuvering trajectories so the player
            // immediately sees moving helicopters trailing staggered flare ribbons vs hovering targets.
            sim.Flight?.OrderMove(world, alphaId, new Vector2(1320f, 690f), finalFacing: null, landOnArrival: false, 0);
            sim.Flight?.OrderMove(world, charlieId, new Vector2(720f, 690f), finalFacing: null, landOnArrival: false, 0);
        }

        return sim;
    }

    private static void SetAirborne(WorldState world, EntityId id, AltitudeBand band, float altitude)
    {
        if (world.TryGetFlightState(id, out WorldState.UnitFlightState fs))
        {
            world.SetFlightState(id, fs with
            {
                Phase = FlightPhase.Airborne,
                DesiredBand = band,
                Altitude = altitude
            });
        }
    }
}
