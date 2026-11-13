using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Dedicated test scenario for vegetation visibility attenuation and line-of-sight checks.
/// Features dense forest with an interior clearing, dual shelter belts demonstrating multi-belt
/// accumulation, sparse forest, an elevated observation ridge, and open terrain for comparison.
/// </summary>
public static class ForestVisibilityScenario
{
    public const string ScenarioName = "forest-visibility";
    public const int MapResolution = 65;
    public const float MapSpacing = 4.0f; // 64 * 4m = 256m x 256m
    public const float RidgeHeight = 18.0f;

    // Side 1 (Observer) initial positions
    public static readonly Vector2 ObserverGroundStart = new(60f, 110f);
    public static readonly Vector2 ObserverRidgeStart = new(20f, 110f);
    public static readonly Vector2 ObserverBeltsStart = new(60f, 205f);
    public static readonly Vector2 ObserverSparseStart = new(60f, 35f);
    public static readonly Vector2 ObserverOpenStart = new(60f, 160f);

    // Side 2 (Opponent targets) positions
    public static readonly Vector2 TargetEdgePos = new(88f, 75f);
    public static readonly Vector2 TargetClearingPos = new(130f, 110f);
    public static readonly Vector2 TargetBehindBeltsPos = new(150f, 205f);
    public static readonly Vector2 TargetSparsePos = new(120f, 35f);
    public static readonly Vector2 TargetOpenPos = new(180f, 160f);

    public static HeightMap CreateHeightMap()
    {
        ushort[] heights = new ushort[MapResolution * MapResolution];

        for (int r = 0; r < MapResolution; r++)
        {
            int rowOffset = r * MapResolution;
            for (int c = 0; c < MapResolution; c++)
            {
                float x = c * MapSpacing;
                float h = 0f;

                // Elevated western ridge for testing over-canopy observation
                if (x <= 24f)
                {
                    h = RidgeHeight;
                }
                else if (x < 48f)
                {
                    // Smooth cosine hermite drop to flat ground
                    float t = (x - 24f) / 24f;
                    float blend = 0.5f * (1.0f + MathF.Cos(t * MathF.PI));
                    h = RidgeHeight * blend;
                }

                heights[rowOffset + c] = (ushort)Math.Clamp((int)MathF.Round(h * 100f), 0, ushort.MaxValue);
            }
        }

        return new HeightMap(MapResolution, MapResolution, MapSpacing, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry(HeightMap? map = null)
    {
        map ??= CreateHeightMap();

        List<VegetationZone> vegetation =
        [
            // 1. Dense forest walls surrounding an interior clearing (40m x 30m)
            // Western wall (width 30m, depth 92m)
            VegetationZone.CreateDenseForest(
                "WOOD-DENSE-W",
                [
                    new Vector2(80f, 65f),
                    new Vector2(110f, 65f),
                    new Vector2(110f, 157f),
                    new Vector2(80f, 157f)
                ],
                canopyHeight: 12.0f,
                maxPenetration: 25.0f),

            // Northern wall (depth 30m)
            VegetationZone.CreateDenseForest(
                "WOOD-DENSE-N",
                [
                    new Vector2(110f, 65f),
                    new Vector2(180f, 65f),
                    new Vector2(180f, 95f),
                    new Vector2(110f, 95f)
                ],
                canopyHeight: 12.0f,
                maxPenetration: 25.0f),

            // Southern wall (depth 32m)
            VegetationZone.CreateDenseForest(
                "WOOD-DENSE-S",
                [
                    new Vector2(110f, 125f),
                    new Vector2(180f, 125f),
                    new Vector2(180f, 157f),
                    new Vector2(110f, 157f)
                ],
                canopyHeight: 12.0f,
                maxPenetration: 25.0f),

            // Eastern wall (width 30m)
            VegetationZone.CreateDenseForest(
                "WOOD-DENSE-E",
                [
                    new Vector2(150f, 95f),
                    new Vector2(180f, 95f),
                    new Vector2(180f, 125f),
                    new Vector2(150f, 125f)
                ],
                canopyHeight: 12.0f,
                maxPenetration: 25.0f),

            // 2. Shelter belt 1 (width 15m)
            VegetationZone.CreateDenseForest(
                "WOOD-BELT1",
                [
                    new Vector2(90f, 165f),
                    new Vector2(105f, 165f),
                    new Vector2(105f, 240f),
                    new Vector2(90f, 240f)
                ],
                canopyHeight: 12.0f,
                maxPenetration: 25.0f),

            // 3. Shelter belt 2 (width 15m, separated by 15m open corridor)
            VegetationZone.CreateDenseForest(
                "WOOD-BELT2",
                [
                    new Vector2(120f, 165f),
                    new Vector2(135f, 165f),
                    new Vector2(135f, 240f),
                    new Vector2(120f, 240f)
                ],
                canopyHeight: 12.0f,
                maxPenetration: 25.0f),

            // 4. Sparse forest zone (depth up to 75m detectable)
            VegetationZone.CreateSparseForest(
                "WOOD-SPARSE",
                [
                    new Vector2(80f, 15f),
                    new Vector2(170f, 15f),
                    new Vector2(170f, 55f),
                    new Vector2(80f, 55f)
                ],
                canopyHeight: 8.0f,
                maxPenetration: 75.0f)
        ];

        return new StaticGeometry(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            ReadOnlySpan<BridgeStructure>.Empty,
            ReadOnlySpan<TerrainObstacle>.Empty,
            vegetation.ToArray());
    }

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        string ruleSetId = "ForestVisibility")
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

        ushort brdmDef = catalog.RequireDefinitionIndex("brdm2");
        ushort t62Def = catalog.RequireDefinitionIndex("t62");
        ushort m113Def = catalog.RequireDefinitionIndex("m113a1");
        ushort m60Def = catalog.RequireDefinitionIndex("m60a1");

        UnitDefinition brdm = catalog.Units[brdmDef];
        UnitDefinition t62 = catalog.Units[t62Def];
        UnitDefinition m113 = catalog.Units[m113Def];
        UnitDefinition m60 = catalog.Units[m60Def];

        // Side 1: Soviet reconnaissance forces across test corridors
        world.CreateUnit(1, brdmDef, ObserverGroundStart, brdm.Speed, brdm.MaxHealth, catalog.GetAmmoCapacity(brdm), 0f);
        world.CreateUnit(1, t62Def, ObserverRidgeStart, t62.Speed, t62.MaxHealth, catalog.GetAmmoCapacity(t62), 0f);
        world.CreateUnit(1, brdmDef, ObserverBeltsStart, brdm.Speed, brdm.MaxHealth, catalog.GetAmmoCapacity(brdm), 0f);
        world.CreateUnit(1, brdmDef, ObserverSparseStart, brdm.Speed, brdm.MaxHealth, catalog.GetAmmoCapacity(brdm), 0f);
        world.CreateUnit(1, brdmDef, ObserverOpenStart, brdm.Speed, brdm.MaxHealth, catalog.GetAmmoCapacity(brdm), 0f);

        // Side 2: US opponent targets positioned across test regions
        // Target 1: at dense forest edge (8m depth)
        world.CreateUnit(2, m113Def, TargetEdgePos, m113.Speed, m113.MaxHealth, 0, MathF.PI);

        // Target 2: inside interior clearing
        world.CreateUnit(2, m60Def, TargetClearingPos, m60.Speed, m60.MaxHealth, 0, MathF.PI);

        // Target 3: behind both shelter belts
        world.CreateUnit(2, m113Def, TargetBehindBeltsPos, m113.Speed, m113.MaxHealth, 0, MathF.PI);

        // Target 4: inside sparse forest (40m depth)
        world.CreateUnit(2, m113Def, TargetSparsePos, m113.Speed, m113.MaxHealth, 0, MathF.PI);

        // Target 5: on open terrain for baseline comparison
        world.CreateUnit(2, m60Def, TargetOpenPos, m60.Speed, m60.MaxHealth, 0, MathF.PI);

        Simulation simulation = new(world, rules, matchSeed);
        simulation.SetPlayerAdmitted(1, true);
        simulation.SetPlayerAdmitted(2, true);
        return simulation;
    }
}
