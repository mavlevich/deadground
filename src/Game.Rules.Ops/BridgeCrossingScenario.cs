using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

public static class BridgeCrossingScenario
{
    public const int MapResolution = 201;
    public const float MapSpacing = 1.0f;
    public const float MapSize = (MapResolution - 1) * MapSpacing; // 200m x 200m

    public const float BankHeight = 4.0f;
    public const float RiverHeight = 0.5f;

    // Bridge spanning across the river at Z=100
    public static readonly Vector2 BridgeStart = new(75f, 100f);
    public static readonly Vector2 BridgeEnd = new(125f, 100f);
    public const float BridgeWidth = 8.0f;
    public const float BridgeHeight = 4.0f;
    public const float BridgeThickness = 0.8f;

    // Dense forest on east bank: X in [135..175], Z in [50..90]
    public const float ForestMinX = 135f;
    public const float ForestMaxX = 175f;
    public const float ForestMinZ = 50f;
    public const float ForestMaxZ = 90f;

    // River impassable water obstacle: X in [85..115], Z in [0..200]
    public const float RiverMinX = 85f;
    public const float RiverMaxX = 115f;

    public static HeightMap CreateHeightMap()
    {
        ushort[] heights = new ushort[MapResolution * MapResolution];
        for (int z = 0; z < MapResolution; z++)
        {
            for (int x = 0; x < MapResolution; x++)
            {
                float worldX = x * MapSpacing;
                float h;
                if (worldX <= 80f)
                {
                    h = BankHeight;
                }
                else if (worldX <= 90f)
                {
                    float t = (worldX - 80f) / 10f;
                    h = BankHeight * (1f - t) + RiverHeight * t;
                }
                else if (worldX <= 110f)
                {
                    h = RiverHeight;
                }
                else if (worldX <= 120f)
                {
                    float t = (worldX - 110f) / 10f;
                    h = RiverHeight * (1f - t) + BankHeight * t;
                }
                else
                {
                    h = BankHeight;
                }
                heights[z * MapResolution + x] = (ushort)Math.Clamp((int)Math.Round(h / 0.01f), 0, ushort.MaxValue);
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

        TerrainObstacle riverWater = new(
            RiverMinX, 0f, RiverMaxX, MapSize,
            TraversabilityCapabilities.Water);

        TerrainObstacle denseForest = new(
            ForestMinX, ForestMinZ, ForestMaxX, ForestMaxZ,
            TraversabilityCapabilities.DenseForest);

        return new StaticGeometry(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            [bridge],
            [riverWater, denseForest]);
    }

    private const string ScenarioCatalogJson = """
    {
      "schema_version": 1,
      "factions": [
        { "id": "soviet", "name": "Soviet Army" },
        { "id": "us", "name": "US Army" }
      ],
      "detection": {
        "memory_duration_ticks": 200,
        "correlation_window_ticks": 100,
        "correlation_distance_gate": 30.0,
        "evaluation_interval_ticks": 2,
        "max_correlation_speed": 12.0,
        "slack": 5.0,
        "max_gate": 50.0
      },
      "weapons": [
        {
          "id": "tank_gun_100mm",
          "range": 160.0,
          "reload_duration_ticks": 40,
          "damage": 100,
          "ammo_capacity": 20,
          "muzzle_height": 1.9,
          "presentation_id": "cannon_100mm",
          "presentation_kind": "cannon",
          "penetration_near": 200.0,
          "penetration_far": 150.0,
          "penetration_near_range": 0.0,
          "penetration_far_range": 160.0,
          "rounds_per_burst": 1,
          "accuracy_curve": [
            { "range": 30.0, "hit_probability": 0.95 },
            { "range": 80.0, "hit_probability": 0.85 },
            { "range": 160.0, "hit_probability": 0.70 }
          ],
          "dispersion_mils": 2.5,
          "target_movement_penalty": 0.15,
          "ranging_bonus": 0.08,
          "max_ranging_bonus": 0.24
        }
      ],
      "units": [
        {
          "id": "prototype_tank",
          "speed": 5.0,
          "sensor_range": 120.0,
          "eye_height": 2.0,
          "target_points": [0.8, 1.8],
          "radius": 1.4,
          "max_health": 120,
          "weapon": "tank_gun_100mm",
          "turret_traverse_rate": 24.0,
          "classification": "tank",
          "armor_front": 100.0,
          "armor_side": 50.0,
          "armor_rear": 30.0,
          "capabilities": "DefaultVehicle"
        },
        {
          "id": "infantry_foot",
          "speed": 4.0,
          "sensor_range": 80.0,
          "eye_height": 1.6,
          "target_points": [0.6, 1.4],
          "radius": 0.4,
          "max_health": 40,
          "classification": "infantry",
          "armor_front": 5.0,
          "armor_side": 5.0,
          "armor_rear": 5.0,
          "capabilities": "Foot"
        },
        {
          "id": "mini_vehicle",
          "speed": 6.0,
          "sensor_range": 80.0,
          "eye_height": 1.0,
          "target_points": [0.4, 0.8],
          "radius": 0.4,
          "max_health": 40,
          "classification": "recon",
          "armor_front": 8.0,
          "armor_side": 5.0,
          "armor_rear": 5.0,
          "capabilities": "DefaultVehicle"
        },
        {
          "id": "target_vehicle",
          "speed": 5.0,
          "sensor_range": 60.0,
          "eye_height": 1.8,
          "target_points": [0.7, 1.6],
          "radius": 1.4,
          "max_health": 100,
          "classification": "tank",
          "armor_front": 20.0,
          "armor_side": 10.0,
          "armor_rear": 10.0,
          "capabilities": "DefaultVehicle"
        }
      ]
    }
    """;

    private static UnitCatalog? s_catalog;

    public static UnitCatalog LoadCatalog() =>
        s_catalog ??= PrototypeScenario.LoadCatalog(ScenarioCatalogJson);

    public static Simulation CreateSimulation(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        ulong matchSeed = 42UL)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= LoadCatalog();

        WorldState world = new(geometry.HeightMap);
        OpsRules rules = new(geometry, catalog);

        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");
        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
        UnitDefinition footDef = catalog.RequireDefinition("infantry_foot");
        ushort footIdx = catalog.RequireDefinitionIndex("infantry_foot");
        UnitDefinition miniDef = catalog.RequireDefinition("mini_vehicle");
        ushort miniIdx = catalog.RequireDefinitionIndex("mini_vehicle");
        UnitDefinition targetDef = catalog.RequireDefinition("target_vehicle");
        ushort targetIdx = catalog.RequireDefinitionIndex("target_vehicle");

        int tankAmmo = catalog.GetAmmoCapacity(tankDef);

        // Player 1 (West bank, moving east toward bridge):
        // 1. Tank at (40, 100)
        world.CreateUnit(1, tankIdx, new Vector2(40f, 100f), tankDef.Speed, tankDef.MaxHealth, tankAmmo, initialTurretYaw: MathF.PI * 0.5f);
        // 2. Foot soldier (radius 0.4, Foot) at (40, 96)
        world.CreateUnit(1, footIdx, new Vector2(40f, 96f), footDef.Speed, footDef.MaxHealth, 0, initialTurretYaw: MathF.PI * 0.5f);
        // 3. Mini vehicle (radius 0.4, DefaultVehicle) at (40, 104)
        world.CreateUnit(1, miniIdx, new Vector2(40f, 104f), miniDef.Speed, miniDef.MaxHealth, 0, initialTurretYaw: MathF.PI * 0.5f);

        // Player 2 (Hostile, East bank):
        // 4. Target vehicle across the bridge at (160, 100)
        world.CreateUnit(2, targetIdx, new Vector2(160f, 100f), targetDef.Speed, targetDef.MaxHealth, 0, initialTurretYaw: -MathF.PI * 0.5f);
        // 5. Target vehicle behind/inside forest at (155, 70)
        world.CreateUnit(2, targetIdx, new Vector2(155f, 70f), targetDef.Speed, targetDef.MaxHealth, 0, initialTurretYaw: -MathF.PI * 0.5f);

        return new Simulation(world, rules, matchSeed);
    }

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        string ruleSetId = "BridgeCrossing")
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= LoadCatalog();

        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerFactions: ["soviet", "us"]);
    }
}
