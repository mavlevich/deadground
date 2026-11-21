using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Dedicated test and playable scenario for rotorcraft flight mechanics (CH-47D Chinook).
/// Features a wide flat airfield pad, an impassable river, a steep central-east ridge for NOE
/// terrain masking and slope LZ rejection, dense woods, a hangar building, and opposing
/// Soviet ground forces (BTR-70 with 14.5mm AA capability and T-62 tank without air targeting).
/// </summary>
public static class HelicopterPlaygroundScenario
{
    public const string ScenarioName = "helicopter-playground";
    public const int MapResolution = 129;
    public const float MapSpacing = 8.0f; // 128 * 8m = 1024m x 1024m
    public const float MapSize = (MapResolution - 1) * MapSpacing;
    public const float BaseHeight = 2.0f;
    public const float WaterBedHeight = 0.4f;
    public const float RidgePeakHeight = 24.0f;

    // Impassable river running west-east across the middle of the map
    public const float RiverMinZ = 480.0f;
    public const float RiverMaxZ = 544.0f;

    // Dense forest south of the river (for LZ forest rejection testing)
    public const float ForestMinX = 180.0f;
    public const float ForestMaxX = 320.0f;
    public const float ForestMinZ = 620.0f;
    public const float ForestMaxZ = 760.0f;

    // Airfield hangar building near the start pad (for LZ structure rejection testing)
    public const float HangarMinX = 80.0f;
    public const float HangarMaxX = 106.0f;
    public const float HangarMinZ = 65.0f;
    public const float HangarMaxZ = 91.0f;
    public const float HangarHeight = 8.0f;

    // Steep ridge center north of the river (for slope LZ rejection and NOE masking testing)
    public static readonly Vector2 RidgeCenter = new(740.0f, 350.0f);
    public const float RidgeRadius = 70.0f;

    // Side 1 (US) start positions (safe northern staging airfield, >1040m from Soviet positions)
    public static readonly Vector2 ChinookAlphaPos = new(140.0f, 120.0f);
    public static readonly Vector2 ChinookBravoPos = new(200.0f, 120.0f);
    public static readonly Vector2 RiflemanEscort1Pos = new(120.0f, 138.0f);
    public static readonly Vector2 RiflemanEscort2Pos = new(128.0f, 138.0f);

    // Clear landing zone on the southern bank, screened from eastern Soviet units by the dense forest
    public static readonly Vector2 SouthLandingZone = new(110.0f, 690.0f);

    // Side 2 (Soviet) positions in the southeastern contested sector
    public static readonly Vector2 SovietBtrPos = new(880.0f, 910.0f);
    public static readonly Vector2 SovietTankPos = new(920.0f, 920.0f);
    public static readonly Vector2 SovietBrdmPos = new(840.0f, 940.0f);

    public static HeightMap CreateHeightMap()
    {
        ushort[] heights = new ushort[MapResolution * MapResolution];
        for (int r = 0; r < MapResolution; r++)
        {
            float z = r * MapSpacing;
            int rowOffset = r * MapResolution;
            for (int c = 0; c < MapResolution; c++)
            {
                float x = c * MapSpacing;
                float h = BaseHeight;

                // River channel
                if (z >= RiverMinZ - 16.0f && z <= RiverMaxZ + 16.0f)
                {
                    if (z >= RiverMinZ && z <= RiverMaxZ)
                    {
                        h = WaterBedHeight;
                    }
                    else if (z < RiverMinZ)
                    {
                        float t = (z - (RiverMinZ - 16.0f)) / 16.0f;
                        h = BaseHeight * (1.0f - t) + WaterBedHeight * t;
                    }
                    else
                    {
                        float t = (z - RiverMaxZ) / 16.0f;
                        h = WaterBedHeight * (1.0f - t) + BaseHeight * t;
                    }
                }

                // Steep conical ridge on the east flank
                float dx = x - RidgeCenter.X;
                float dz = z - RidgeCenter.Y;
                float distSq = dx * dx + dz * dz;
                float radiusSq = RidgeRadius * RidgeRadius;
                if (distSq < radiusSq)
                {
                    float norm = 1.0f - distSq / radiusSq;
                    h += RidgePeakHeight * norm * norm;
                }

                heights[rowOffset + c] = (ushort)Math.Clamp((int)MathF.Round(h * 100f), 0, ushort.MaxValue);
            }
        }
        return new HeightMap(MapResolution, MapResolution, MapSpacing, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry(HeightMap? map = null)
    {
        map ??= CreateHeightMap();

        OpaqueStructure hangar = new(
            new StructureId(10),
            HangarMinX, 0f, HangarMinZ,
            HangarMaxX, HangarHeight, HangarMaxZ,
            isGrounded: true);

        TerrainObstacle river = new(
            0f, RiverMinZ, MapSize, RiverMaxZ,
            TraversabilityCapabilities.Water);

        TerrainObstacle forest = new(
            ForestMinX, ForestMinZ, ForestMaxX, ForestMaxZ,
            TraversabilityCapabilities.DenseForest);

        VegetationZone forestVeg = VegetationZone.CreateDenseForest(
            "FOREST_HELI_PLAYGROUND",
            [
                new Vector2(ForestMinX, ForestMinZ),
                new Vector2(ForestMaxX, ForestMinZ),
                new Vector2(ForestMaxX, ForestMaxZ),
                new Vector2(ForestMinX, ForestMaxZ)
            ],
            canopyHeight: 11.0f,
            maxPenetration: 20.0f);

        return new StaticGeometry(
            map,
            [hangar],
            [],
            [river, forest],
            [forestVeg]);
    }

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        string ruleSetId = "HelicopterPlayground")
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

        OpsRules rules = new(geometry, catalog, navTuning);
        WorldState world = new(geometry.HeightMap);

        UnitDefinition ch47Def = catalog.RequireDefinition("ch47d");
        ushort ch47Idx = catalog.RequireDefinitionIndex("ch47d");
        int ch47Ammo = catalog.GetAmmoCapacity(ch47Def);

        UnitDefinition rifleDef = catalog.RequireDefinition("us_rifleman");
        ushort rifleIdx = catalog.RequireDefinitionIndex("us_rifleman");
        int rifleAmmo = catalog.GetAmmoCapacity(rifleDef);

        UnitDefinition btrDef = catalog.RequireDefinition("btr70");
        ushort btrIdx = catalog.RequireDefinitionIndex("btr70");
        int btrAmmo = catalog.GetAmmoCapacity(btrDef);

        UnitDefinition tankDef = catalog.RequireDefinition("t62");
        ushort tankIdx = catalog.RequireDefinitionIndex("t62");
        int tankAmmo = catalog.GetAmmoCapacity(tankDef);

        UnitDefinition brdmDef = catalog.RequireDefinition("brdm2");
        ushort brdmIdx = catalog.RequireDefinitionIndex("brdm2");
        int brdmAmmo = catalog.GetAmmoCapacity(brdmDef);

        // Side 1 (Player, US): 2 CH-47D Chinooks + 4 US Riflemen (2 embarked in Alpha, 2 outside)
        const float facingSouth = MathF.PI;
        EntityId alphaId = world.CreateUnit(1, ch47Idx, ChinookAlphaPos, ch47Def.Speed, ch47Def.MaxHealth, ch47Ammo, facingSouth, passengerCapacity: ch47Def.PassengerCapacity);
        world.CreateUnit(1, ch47Idx, ChinookBravoPos, ch47Def.Speed, ch47Def.MaxHealth, ch47Ammo, facingSouth, passengerCapacity: ch47Def.PassengerCapacity);

        EntityId embarked1 = world.CreateUnit(1, rifleIdx, ChinookAlphaPos, rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingSouth);
        EntityId embarked2 = world.CreateUnit(1, rifleIdx, ChinookAlphaPos, rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingSouth);
        world.Board(alphaId, embarked1);
        world.Board(alphaId, embarked2);

        world.CreateUnit(1, rifleIdx, RiflemanEscort1Pos, rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingSouth);
        world.CreateUnit(1, rifleIdx, RiflemanEscort2Pos, rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingSouth);

        // Side 2 (Opponent, Soviet): BTR-70 (AA threat), T-62 (ground only), BRDM-2 (AA threat)
        const float facingNorth = 0f;
        world.CreateUnit(2, btrIdx, SovietBtrPos, btrDef.Speed, btrDef.MaxHealth, btrAmmo, facingNorth, passengerCapacity: btrDef.PassengerCapacity);
        world.CreateUnit(2, tankIdx, SovietTankPos, tankDef.Speed, tankDef.MaxHealth, tankAmmo, facingNorth);
        world.CreateUnit(2, brdmIdx, SovietBrdmPos, brdmDef.Speed, brdmDef.MaxHealth, brdmAmmo, facingNorth);

        return new Simulation(world, rules, matchSeed);
    }
}
