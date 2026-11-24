using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Data-driven visual and structural registration descriptor for a destructible building section.
/// Shared between server scenario setup and client presentation so neither side hardcodes model dimensions.
/// </summary>
public readonly record struct DestructibleBuildingSpec(
    StructureId Id,
    StructureDestructionProfile Profile,
    float YawRad = 0f);

/// <summary>
/// Playable urban combat and building destruction proving ground featuring modular 5-story Soviet
/// Series 1-464 panelkas arranged around a central microdistrict courtyard.
/// Player 1 (Soviet) fields 2x T-62 tanks, 1x BM-21 Grad MLRS, and 1x BRDM-2 recon vehicle
/// facing a 4-section panelka block (plus flanking 3-section and rear 2-section blocks).
/// Player 2 (US) deploys 2x M60A1 tanks concealed in fog of war behind the central panelka
/// until its sections collapse and collapse dust clears.
/// </summary>
public static class UrbanDestructionPlaygroundScenario
{
    public const string ScenarioName = "urban-destruction-playground";
    public const int MapResolution = 129;
    public const float MapSpacing = 8.0f; // 128 * 8m = 1024m x 1024m
    public const float MapSize = (MapResolution - 1) * MapSpacing;
    public const float BaseHeight = 2.0f;

    public const float SectionWidth = 12.0f;
    public const float SectionDepth = 12.0f;
    public const float FloorHeight = 3.1f;
    public const byte FloorsTotal = 5;
    public const float BuildingHeight = FloorsTotal * FloorHeight; // 15.5m
    public const float RubbleHeight = 1.5f;

    public const string PanelkaProfileId = "panelka_1_464";

    public static StructureDestructionProfile PanelkaProfile =>
        ColdWarContent.LoadCatalog().RequireBuildingProfile(PanelkaProfileId);

    // Player 1 (Soviet): 2x T-62, 1x BM-21 Grad, 1x BRDM-2 scout facing north into the courtyard
    public static readonly Vector2 SovietT62WestPos = new(482.0f, 415.0f);
    public static readonly Vector2 SovietT62EastPos = new(518.0f, 415.0f);
    public static readonly Vector2 SovietBrdm2Pos = new(500.0f, 445.0f);
    public static readonly Vector2 SovietBm21Pos = new(500.0f, 110.0f);

    // Player 2 (US): 2x M60A1 tanks concealed directly behind the 4-section central panelka (Z=512..524)
    public static readonly Vector2 UsM60WestPos = new(492.0f, 548.0f);
    public static readonly Vector2 UsM60EastPos = new(508.0f, 548.0f);

    private static readonly (int Id, float MinX, float MinZ, float YawRad)[] PanelkaLayout =
    [
        // Central 4-section (48m) panelka blocking LOS across the courtyard (front faces south toward Player 1)
        (101, 476f, 512f, MathF.PI),
        (102, 488f, 512f, MathF.PI),
        (103, 500f, 512f, MathF.PI),
        (104, 512f, 512f, MathF.PI),

        // East 3-section (36m) flanking panelka (front faces west into courtyard)
        (105, 556f, 464f, -MathF.PI * 0.5f),
        (106, 556f, 476f, -MathF.PI * 0.5f),
        (107, 556f, 488f, -MathF.PI * 0.5f),

        // West 3-section (36m) flanking panelka (front faces east into courtyard)
        (108, 432f, 464f, MathF.PI * 0.5f),
        (109, 432f, 476f, MathF.PI * 0.5f),
        (110, 432f, 488f, MathF.PI * 0.5f),

        // Rear north 2-section (24m) panelka behind OPFOR positions
        (111, 488f, 574f, MathF.PI),
        (112, 500f, 574f, MathF.PI),
    ];

    public static DestructibleBuildingSpec[] GetBuildingSpecs(UnitCatalog? catalog = null)
    {
        StructureDestructionProfile profile = (catalog ?? ColdWarContent.LoadCatalog()).RequireBuildingProfile(PanelkaProfileId);
        DestructibleBuildingSpec[] specs = new DestructibleBuildingSpec[PanelkaLayout.Length];
        for (int i = 0; i < PanelkaLayout.Length; i++)
        {
            specs[i] = new DestructibleBuildingSpec(
                new StructureId(PanelkaLayout[i].Id),
                profile,
                PanelkaLayout[i].YawRad);
        }
        return specs;
    }

    public static DestructibleStructureBinding[] GetStructureBindings(UnitCatalog? catalog = null)
    {
        DestructibleBuildingSpec[] specs = GetBuildingSpecs(catalog);
        DestructibleStructureBinding[] bindings = new DestructibleStructureBinding[specs.Length];
        for (int i = 0; i < specs.Length; i++)
        {
            bindings[i] = new DestructibleStructureBinding(specs[i].Id, specs[i].Profile);
        }
        return bindings;
    }

    public static HeightMap CreateHeightMap()
    {
        ushort[] heights = new ushort[MapResolution * MapResolution];
        ushort flat = (ushort)Math.Clamp((int)MathF.Round(BaseHeight * 100f), 0, ushort.MaxValue);
        Array.Fill(heights, flat);
        return new HeightMap(MapResolution, MapResolution, MapSpacing, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry(HeightMap? map = null)
    {
        map ??= CreateHeightMap();
        OpaqueStructure[] structures = new OpaqueStructure[PanelkaLayout.Length];
        for (int i = 0; i < PanelkaLayout.Length; i++)
        {
            (int id, float minX, float minZ, _) = PanelkaLayout[i];
            float groundY = map.GetHeight(minX + SectionWidth * 0.5f, minZ + SectionDepth * 0.5f);
            structures[i] = new OpaqueStructure(
                new StructureId(id),
                minX,
                groundY,
                minZ,
                minX + SectionWidth,
                groundY + BuildingHeight,
                minZ + SectionDepth,
                isGrounded: true);
        }

        VegetationZone westPark = VegetationZone.CreateDenseForest(
            "PARK_URBAN_WEST",
            [
                new Vector2(360f, 420f),
                new Vector2(416f, 420f),
                new Vector2(416f, 540f),
                new Vector2(360f, 540f)
            ],
            canopyHeight: 9.0f,
            maxPenetration: 35.0f);

        VegetationZone eastPark = VegetationZone.CreateDenseForest(
            "PARK_URBAN_EAST",
            [
                new Vector2(584f, 420f),
                new Vector2(640f, 420f),
                new Vector2(640f, 540f),
                new Vector2(584f, 540f)
            ],
            canopyHeight: 9.0f,
            maxPenetration: 35.0f);

        return new StaticGeometry(map, structures, [], [], [westPark, eastPark]);
    }

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        string ruleSetId = "UrbanDestructionPlayground")
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();
        DestructibleStructureBinding[] bindings = GetStructureBindings(catalog);
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry, bindings),
            ruleSetId,
            PlayerFactions: ["soviet", "us"]);
    }

    public static Simulation CreateSimulation(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        NavigationTuning? navTuning = null,
        ulong matchSeed = 1464UL)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();

        NavigationTuning effectiveNavTuning = navTuning ?? new NavigationTuning(CellSize: 2.0f);
        OpsRules rules = new(geometry, catalog, effectiveNavTuning);
        WorldState world = new(geometry.HeightMap);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        int t62Ammo = catalog.GetAmmoCapacity(t62Def);

        UnitDefinition bm21Def = catalog.RequireDefinition("bm21");
        ushort bm21Idx = catalog.RequireDefinitionIndex("bm21");
        int bm21Ammo = catalog.GetAmmoCapacity(bm21Def);

        UnitDefinition brdmDef = catalog.RequireDefinition("brdm2");
        ushort brdmIdx = catalog.RequireDefinitionIndex("brdm2");
        int brdmAmmo = catalog.GetAmmoCapacity(brdmDef);

        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        int m60Ammo = catalog.GetAmmoCapacity(m60Def);

        // Player 1 (Soviet) facing +Z toward the central panelka block
        const float facingPlusZ = MathF.PI;
        world.CreateUnit(1, t62Idx, SovietT62WestPos, t62Def.Speed, t62Def.MaxHealth, t62Ammo, facingPlusZ);
        world.CreateUnit(1, t62Idx, SovietT62EastPos, t62Def.Speed, t62Def.MaxHealth, t62Ammo, facingPlusZ);
        world.CreateUnit(1, brdmIdx, SovietBrdm2Pos, brdmDef.Speed, brdmDef.MaxHealth, brdmAmmo, facingPlusZ);
        world.CreateUnit(1, bm21Idx, SovietBm21Pos, bm21Def.Speed, bm21Def.MaxHealth, bm21Ammo, facingPlusZ);

        // Player 2 (US) behind central panelka facing -Z toward the courtyard
        const float facingMinusZ = 0f;
        world.CreateUnit(2, m60Idx, UsM60WestPos, m60Def.Speed, m60Def.MaxHealth, m60Ammo, facingMinusZ);
        world.CreateUnit(2, m60Idx, UsM60EastPos, m60Def.Speed, m60Def.MaxHealth, m60Ammo, facingMinusZ);

        Simulation simulation = new(world, rules, matchSeed: matchSeed);
        if (simulation.BuildingDestruction is not null)
        {
            DestructibleBuildingSpec[] specs = GetBuildingSpecs();
            for (int i = 0; i < specs.Length; i++)
            {
                StructureDestructionProfile prof = specs[i].Profile;
                simulation.BuildingDestruction.RegisterStructure(world, specs[i].Id, in prof);
            }
        }

        return simulation;
    }
}
