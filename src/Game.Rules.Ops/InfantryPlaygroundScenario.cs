using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Dedicated test scenario for the first playable infantry unit (US Rifleman).
/// Features open ground, dense forest with an interior clearing, a small building obstacle,
/// a water canal with an elevated bridge crossing, and an armored vehicle for scale and armor comparison.
/// </summary>
public static class InfantryPlaygroundScenario
{
    public const string ScenarioName = "infantry-playground";
    public const int MapResolution = 65;
    public const float MapSpacing = 4.0f; // 64 * 4m = 256m x 256m
    public const float BankHeight = 2.0f;
    public const float WaterBedHeight = 0.5f;
    public const float LeveeHeight = 4.2f;

    // Canal impassable water obstacle running west to east
    public const float CanalMinZ = 116.0f;
    public const float CanalMaxZ = 140.0f;

    // Bridge spanning across canal at X=128
    public static readonly Vector2 BridgeStart = new(128.0f, 112.0f);
    public static readonly Vector2 BridgeEnd = new(128.0f, 144.0f);
    public const float BridgeWidth = 8.0f;
    public const float BridgeHeight = 2.0f;
    public const float BridgeThickness = 0.8f;

    // Dense forest surrounding an interior clearing
    public const float ForestMinX = 20.0f;
    public const float ForestMaxX = 100.0f;
    public const float ForestMinZ = 145.0f;
    public const float ForestMaxZ = 215.0f;
    public static readonly Vector2 ClearingCenter = new(60.0f, 180.0f);
    public const float ClearingRadius = 8.0f;

    // Small building obstacle (East test area)
    public const float BuildingMinX = 195.0f;
    public const float BuildingMaxX = 207.0f;
    public const float BuildingMinZ = 166.0f;
    public const float BuildingMaxZ = 178.0f;
    public const float BuildingHeight = 6.0f;

    // Checkpoint structure (North of bridge)
    public const float CheckpointMinX = 122.0f;
    public const float CheckpointMaxX = 134.0f;
    public const float CheckpointMinZ = 98.0f;
    public const float CheckpointMaxZ = 104.0f;
    public const float CheckpointHeight = 5.0f;

    // Tree separator belt between bridge road and building area
    public const float SeparatorMinX = 160.0f;
    public const float SeparatorMaxX = 175.0f;

    // Side 1 (Player, US) positions
    public static readonly Vector2 UsRiflemanOpen = new(145.0f, 175.0f);
    public static readonly Vector2 UsRiflemanForest = new(10.0f, 180.0f);
    public static readonly Vector2 UsRiflemanBridge = new(128.0f, 165.0f);
    public static readonly Vector2 UsRiflemanBuilding = new(201.0f, 195.0f);

    // Side 2 (Opponent) positions
    public static readonly Vector2 OpforRiflemanClearing = new(60.0f, 180.0f);
    public static readonly Vector2 OpforRiflemanBridge = new(128.0f, 85.0f);
    public static readonly Vector2 OpforRiflemanBuilding = new(201.0f, 150.0f);
    public static readonly Vector2 OpforVehiclePos = new(145.0f, 75.0f);

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
                float h = BankHeight;
                if (z >= CanalMinZ - 8.0f && z <= CanalMaxZ + 8.0f)
                {
                    if (z >= CanalMinZ && z <= CanalMaxZ)
                    {
                        h = WaterBedHeight;
                    }
                    else if (z < CanalMinZ)
                    {
                        float t = (z - (CanalMinZ - 8.0f)) / 8.0f;
                        h = BankHeight * (1.0f - t) + WaterBedHeight * t;
                    }
                    else
                    {
                        float t = (z - CanalMaxZ) / 8.0f;
                        h = WaterBedHeight * (1.0f - t) + BankHeight * t;
                    }
                }
                else if (z >= 96.0f && z <= 116.0f)
                {
                    float xFactor = 0f;
                    if (x < 120.0f)
                    {
                        xFactor = 1.0f;
                    }
                    else if (x < 124.0f)
                    {
                        xFactor = (124.0f - x) / 4.0f;
                    }
                    else if (x > 136.0f)
                    {
                        xFactor = 1.0f;
                    }
                    else if (x > 132.0f)
                    {
                        xFactor = (x - 132.0f) / 4.0f;
                    }

                    if (xFactor > 0f)
                    {
                        float zFactor;
                        if (z < 102.0f)
                        {
                            zFactor = (z - 96.0f) / 6.0f;
                        }
                        else if (z <= 110.0f)
                        {
                            zFactor = 1.0f;
                        }
                        else
                        {
                            zFactor = (116.0f - z) / 6.0f;
                        }
                        h += (LeveeHeight - BankHeight) * xFactor * zFactor;
                    }
                }
                heights[rowOffset + c] = (ushort)Math.Clamp((int)MathF.Round(h * 100f), 0, ushort.MaxValue);
            }
        }
        return new HeightMap(MapResolution, MapResolution, MapSpacing, 0.01f, heights);
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

        OpaqueStructure checkpoint = new(
            new StructureId(11),
            CheckpointMinX, 0f, CheckpointMinZ,
            CheckpointMaxX, CheckpointHeight, CheckpointMaxZ);

        TerrainObstacle canalWater = new(
            0f, CanalMinZ, 256f, CanalMaxZ,
            TraversabilityCapabilities.Water);

        TerrainObstacle forestW = new(ForestMinX, ForestMinZ, ClearingCenter.X - ClearingRadius, ForestMaxZ, TraversabilityCapabilities.DenseForest);
        TerrainObstacle forestE = new(ClearingCenter.X + ClearingRadius, ForestMinZ, ForestMaxX, ForestMaxZ, TraversabilityCapabilities.DenseForest);
        TerrainObstacle forestN = new(ClearingCenter.X - ClearingRadius, ForestMinZ, ClearingCenter.X + ClearingRadius, ClearingCenter.Y - ClearingRadius, TraversabilityCapabilities.DenseForest);
        TerrainObstacle forestS = new(ClearingCenter.X - ClearingRadius, ClearingCenter.Y + ClearingRadius, ClearingCenter.X + ClearingRadius, ForestMaxZ, TraversabilityCapabilities.DenseForest);
        TerrainObstacle separatorObstacle = new(SeparatorMinX, ForestMinZ, SeparatorMaxX, ForestMaxZ, TraversabilityCapabilities.DenseForest);

        VegetationClearing clearing = VegetationClearing.CreateBox(
            "CLEARING_PLAYGROUND",
            ClearingCenter.X - ClearingRadius,
            ClearingCenter.Y - ClearingRadius,
            ClearingCenter.X + ClearingRadius,
            ClearingCenter.Y + ClearingRadius);

        VegetationZone forestZone = VegetationZone.CreateDenseForest(
            "FOREST_PLAYGROUND",
            [
                new Vector2(ForestMinX, ForestMinZ),
                new Vector2(ForestMaxX, ForestMinZ),
                new Vector2(ForestMaxX, ForestMaxZ),
                new Vector2(ForestMinX, ForestMaxZ)
            ],
            canopyHeight: 12.0f,
            maxPenetration: 20.0f);

        VegetationZone separatorZone = VegetationZone.CreateDenseForest(
            "WOOD_SEPARATOR",
            [
                new Vector2(SeparatorMinX, ForestMinZ),
                new Vector2(SeparatorMaxX, ForestMinZ),
                new Vector2(SeparatorMaxX, ForestMaxZ),
                new Vector2(SeparatorMinX, ForestMaxZ)
            ],
            canopyHeight: 12.0f,
            maxPenetration: 10.0f);

        return new StaticGeometry(
            map,
            [building, checkpoint],
            [bridge],
            [canalWater, forestW, forestE, forestN, forestS, separatorObstacle],
            [forestZone, separatorZone],
            [clearing]);
    }

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        string ruleSetId = "InfantryPlayground")
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

        UnitDefinition rifleman = catalog.Units[riflemanDef];
        UnitDefinition m113 = catalog.Units[m113Def];

        int riflemanAmmo = catalog.GetAmmoCapacity(rifleman);
        int m113Ammo = catalog.GetAmmoCapacity(m113);

        // Side 1: 4 US riflemen positioned across test features (facing north)
        world.CreateUnit(1, riflemanDef, UsRiflemanOpen, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, 0f);
        world.CreateUnit(1, riflemanDef, UsRiflemanForest, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, 0f);
        world.CreateUnit(1, riflemanDef, UsRiflemanBridge, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, 0f);
        world.CreateUnit(1, riflemanDef, UsRiflemanBuilding, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, 0f);

        // Side 2: 3 opponent riflemen and 1 vehicle for comparison (facing south)
        world.CreateUnit(2, riflemanDef, OpforRiflemanClearing, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, MathF.PI);
        world.CreateUnit(2, riflemanDef, OpforRiflemanBridge, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, MathF.PI);
        world.CreateUnit(2, riflemanDef, OpforRiflemanBuilding, rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, MathF.PI);
        world.CreateUnit(2, m113Def, OpforVehiclePos, m113.Speed, m113.MaxHealth, m113Ammo, MathF.PI);

        Simulation simulation = new(world, rules, matchSeed);
        simulation.SetPlayerAdmitted(1, true);
        simulation.SetPlayerAdmitted(2, true);
        return simulation;
    }
}
