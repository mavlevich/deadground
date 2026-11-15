using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Dedicated test scenario for infantry transport boarding, conveyance, and disembarking.
/// Features a starting staging area with a transport and infantry, an approach road to a forest,
/// an intermediate landing zone, a building obstacle chokepoint, a canal with an elevated bridge,
/// and opposing forces on the northern side.
/// </summary>
public static class InfantryTransportScenario
{
    public const string ScenarioName = "infantry-transport";
    public const int MapResolution = 65;
    public const float MapSpacing = 4.0f; // 64 * 4m = 256m x 256m
    public const float BankHeight = 2.0f;
    public const float WaterBedHeight = 0.5f;
    public const float LeveeHeight = 4.2f;

    // Canal impassable water obstacle running west to east
    public const float CanalMinZ = 116.0f;
    public const float CanalMaxZ = 140.0f;

    // Elevated bridge spanning across canal at X=128
    public static readonly Vector2 BridgeStart = new(128.0f, 112.0f);
    public static readonly Vector2 BridgeEnd = new(128.0f, 144.0f);
    public const float BridgeWidth = 8.0f;
    public const float BridgeHeight = 2.0f;
    public const float BridgeThickness = 0.8f;

    // Dense forest surrounding an intermediate landing clearing
    public const float ForestMinX = 30.0f;
    public const float ForestMaxX = 105.0f;
    public const float ForestMinZ = 150.0f;
    public const float ForestMaxZ = 200.0f;

    // Intermediate landing zone south of the canal and bridge
    public static readonly Vector2 LandingZoneCenter = new(128.0f, 165.0f);
    public const float LandingZoneRadius = 10.0f;

    // Building obstacle chokepoint east of the road approach
    public const float BuildingMinX = 142.0f;
    public const float BuildingMaxX = 154.0f;
    public const float BuildingMinZ = 150.0f;
    public const float BuildingMaxZ = 162.0f;
    public const float BuildingHeight = 6.0f;

    // Side 1 (Player, US) starting positions
    public static readonly Vector2 UsTransportPos = new(128.0f, 210.0f);
    public static readonly Vector2 UsRiflemanPos1 = new(124.0f, 216.0f);
    public static readonly Vector2 UsRiflemanPos2 = new(127.0f, 217.0f);
    public static readonly Vector2 UsRiflemanPos3 = new(129.0f, 217.0f);
    public static readonly Vector2 UsRiflemanPos4 = new(132.0f, 216.0f);

    // Side 2 (Opponent) positions north of the canal
    public static readonly Vector2 OpforRiflemanPos = new(128.0f, 85.0f);
    public static readonly Vector2 OpforTransportPos = new(142.0f, 75.0f);

    public static HeightMap CreateHeightMap()
    {
        return InfantryPlaygroundScenario.CreateHeightMap();
    }

    public static StaticGeometry CreateStaticGeometry(HeightMap? map = null)
    {
        map ??= CreateHeightMap();

        BridgeStructure bridge = new(
            new StructureId(1),
            BridgeStart,
            BridgeEnd,
            BridgeWidth,
            BridgeHeight,
            BridgeHeight,
            BridgeThickness);

        OpaqueStructure building = new(
            new StructureId(10),
            BuildingMinX, 0f, BuildingMinZ,
            BuildingMaxX, BuildingHeight, BuildingMaxZ);

        TerrainObstacle canalWater = new(
            0f, CanalMinZ, 256f, CanalMaxZ,
            TraversabilityCapabilities.Water);

        TerrainObstacle forestObstacle = new(
            ForestMinX, ForestMinZ, ForestMaxX, ForestMaxZ,
            TraversabilityCapabilities.DenseForest);

        VegetationZone forestZone = VegetationZone.CreateDenseForest(
            "FOREST_TRANSPORT_WEST",
            [
                new Vector2(ForestMinX, ForestMinZ),
                new Vector2(ForestMaxX, ForestMinZ),
                new Vector2(ForestMaxX, ForestMaxZ),
                new Vector2(ForestMinX, ForestMaxZ)
            ],
            canopyHeight: 12.0f,
            maxPenetration: 20.0f);

        return new StaticGeometry(
            map,
            [building],
            [bridge],
            [canalWater, forestObstacle],
            [forestZone],
            []);
    }

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        string ruleSetId = "InfantryTransport")
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();

        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerFactions: PrototypeScenario.CreatePlayerFactions(catalog));
    }

    public static Simulation CreateSimulation(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        ulong matchSeed = 42UL)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();

        OpsRules rules = new(geometry, catalog);
        WorldState world = new(geometry.HeightMap);

        ushort riflemanDef = catalog.RequireDefinitionIndex("us_rifleman");
        ushort m113Def = catalog.RequireDefinitionIndex("m113a1");
        ushort btrDef = catalog.RequireDefinitionIndex("btr70");

        UnitDefinition rifleman = catalog.Units[riflemanDef];
        UnitDefinition m113 = catalog.Units[m113Def];
        UnitDefinition btr = catalog.Units[btrDef];

        int riflemanAmmo = catalog.GetAmmoCapacity(rifleman);
        int m113Ammo = catalog.GetAmmoCapacity(m113);
        int btrAmmo = catalog.GetAmmoCapacity(btr);

        // Side 1: 1 US M113A1 transport and 4 US riflemen (facing north)
        world.CreateUnit(1, m113Def, UsTransportPos, m113.Speed, m113.MaxHealth, m113Ammo, 0f, passengerCapacity: catalog.GetPassengerCapacity(m113));
        world.CreateUnit(1, riflemanDef, UsRiflemanPos1, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, 0f);
        world.CreateUnit(1, riflemanDef, UsRiflemanPos2, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, 0f);
        world.CreateUnit(1, riflemanDef, UsRiflemanPos3, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, 0f);
        world.CreateUnit(1, riflemanDef, UsRiflemanPos4, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, 0f);

        // Side 2: 1 opponent rifleman and 1 BTR-70 transport (facing south)
        world.CreateUnit(2, riflemanDef, OpforRiflemanPos, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, MathF.PI);
        world.CreateUnit(2, btrDef, OpforTransportPos, btr.Speed, btr.MaxHealth, btrAmmo, MathF.PI, passengerCapacity: catalog.GetPassengerCapacity(btr));

        Simulation simulation = new(world, rules, matchSeed);
        simulation.SetPlayerAdmitted(1, true);
        simulation.SetPlayerAdmitted(2, true);
        return simulation;
    }
}
