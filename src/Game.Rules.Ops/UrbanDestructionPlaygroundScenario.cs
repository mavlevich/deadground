using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

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

    // Authentic Soviet Series 1-464 5-story panelka proportions (14.4 m pod'ezd, 11.8 m depth, 2.75 m/floor)
    public const float SectionWidth = 14.4f;
    public const float SectionDepth = 11.8f;
    public const float FloorHeight = 2.75f;
    public const byte FloorsTotal = 5;
    public const float BuildingHeight = FloorsTotal * FloorHeight; // 13.75m
    public const float RubbleHeight = 1.65f;

    public const string PanelkaProfileId = "panelka_1_464";

    public static StructureDestructionProfile PanelkaProfile =>
        ColdWarContent.LoadCatalog().RequireBuildingProfile(PanelkaProfileId);

    // Player 1 (Soviet): 2x T-62, 1x BM-21 Grad, 1x BRDM-2 scout facing north into the courtyard
    public static readonly Vector2 SovietT62WestPos = new(482.0f, 415.0f);
    public static readonly Vector2 SovietT62EastPos = new(518.0f, 415.0f);
    public static readonly Vector2 SovietBrdm2Pos = new(500.0f, 445.0f);
    public static readonly Vector2 SovietBm21Pos = new(500.0f, 110.0f);

    // Player 2 (US): 2x M60A1 tanks concealed directly behind the 4-section central panelka (Z=512..523.8)
    public static readonly Vector2 UsM60WestPos = new(492.0f, 548.0f);
    public static readonly Vector2 UsM60EastPos = new(508.0f, 548.0f);

    private static readonly (int Id, float MinX, float MinZ, float YawRad)[] PanelkaLayout =
    [
        // Central 4-section (57.6 m) panelka blocking LOS across the courtyard (front faces south toward Player 1)
        (101, 471.2f, 512.0f, MathF.PI),
        (102, 485.6f, 512.0f, MathF.PI),
        (103, 500.0f, 512.0f, MathF.PI),
        (104, 514.4f, 512.0f, MathF.PI),

        // East 3-section (43.2 m) flanking panelka (front faces west into courtyard; rotated 90 deg -> 11.8 m X by 14.4 m Z)
        (105, 562.0f, 458.0f, -MathF.PI * 0.5f),
        (106, 562.0f, 472.4f, -MathF.PI * 0.5f),
        (107, 562.0f, 486.8f, -MathF.PI * 0.5f),

        // West 3-section (43.2 m) flanking panelka (front faces east into courtyard; rotated 90 deg -> 11.8 m X by 14.4 m Z)
        (108, 426.2f, 458.0f, MathF.PI * 0.5f),
        (109, 426.2f, 472.4f, MathF.PI * 0.5f),
        (110, 426.2f, 486.8f, MathF.PI * 0.5f),

        // Rear north 2-section (28.8 m) panelka behind OPFOR positions
        (111, 485.6f, 574.0f, MathF.PI),
        (112, 500.0f, 574.0f, MathF.PI),
    ];

    public static DestructibleBuildingSpec[] GetBuildingSpecs(UnitCatalog? catalog = null)
    {
        UnitCatalog resolvedCatalog = catalog ?? ColdWarContent.LoadCatalog();
        StructureDestructionProfile profile = resolvedCatalog.RequireBuildingProfile(PanelkaProfileId);
        BuildingGarrisonProfile garrisonProfile = resolvedCatalog.RequireBuildingGarrisonProfile(PanelkaProfileId);
        DestructibleBuildingSpec[] specs = new DestructibleBuildingSpec[PanelkaLayout.Length];
        for (int i = 0; i < PanelkaLayout.Length; i++)
        {
            specs[i] = new DestructibleBuildingSpec(
                new StructureId(PanelkaLayout[i].Id),
                profile,
                PanelkaLayout[i].YawRad,
                garrisonProfile);
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

    public static StaticGeometry CreateStaticGeometry(HeightMap? map = null, UnitCatalog? catalog = null)
    {
        map ??= CreateHeightMap();
        OpaqueStructure[] structures = new OpaqueStructure[PanelkaLayout.Length];
        for (int i = 0; i < PanelkaLayout.Length; i++)
        {
            (int id, float minX, float minZ, float yawRad) = PanelkaLayout[i];
            bool rotated90 = MathF.Abs(MathF.Sin(yawRad)) > 0.707f;
            float sizeX = rotated90 ? SectionDepth : SectionWidth;
            float sizeZ = rotated90 ? SectionWidth : SectionDepth;
            float groundY = map.GetHeight(minX + sizeX * 0.5f, minZ + sizeZ * 0.5f);
            structures[i] = new OpaqueStructure(
                new StructureId(id),
                minX,
                groundY,
                minZ,
                minX + sizeX,
                groundY + BuildingHeight,
                minZ + sizeZ,
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

        DestructibleBuildingSpec[] specs = GetBuildingSpecs(catalog);
        return new StaticGeometry(map, structures, [], [], [westPark, eastPark], [], destructibleBuildings: specs);
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

        UnitDefinition rifleDef = catalog.RequireDefinition("us_rifleman");
        ushort rifleIdx = catalog.RequireDefinitionIndex("us_rifleman");
        int rifleAmmo = catalog.GetAmmoCapacity(rifleDef);

        UnitDefinition ch47Def = catalog.RequireDefinition("ch47d");
        ushort ch47Idx = catalog.RequireDefinitionIndex("ch47d");
        int ch47Ammo = catalog.GetAmmoCapacity(ch47Def);

        // Player 1 (Soviet) facing +Z toward the central panelka block
        const float facingPlusZ = MathF.PI;
        world.CreateUnit(1, t62Idx, SovietT62WestPos, t62Def.Speed, t62Def.MaxHealth, t62Ammo, facingPlusZ);
        world.CreateUnit(1, t62Idx, SovietT62EastPos, t62Def.Speed, t62Def.MaxHealth, t62Ammo, facingPlusZ);
        world.CreateUnit(1, brdmIdx, SovietBrdm2Pos, brdmDef.Speed, brdmDef.MaxHealth, brdmAmmo, facingPlusZ);
        world.CreateUnit(1, bm21Idx, SovietBm21Pos, bm21Def.Speed, bm21Def.MaxHealth, bm21Ammo, facingPlusZ);

        // Player 1 urban assault fireteam in the courtyard ready to garrison panelka sections
        world.CreateUnit(1, rifleIdx, new Vector2(488.0f, 472.0f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingPlusZ);
        world.CreateUnit(1, rifleIdx, new Vector2(496.0f, 472.0f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingPlusZ);
        world.CreateUnit(1, rifleIdx, new Vector2(504.0f, 472.0f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingPlusZ);
        world.CreateUnit(1, rifleIdx, new Vector2(512.0f, 472.0f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingPlusZ);

        // Player 1 CH-47D assault transport staged on the south pad with 4 embarked riflemen ready for rooftop Fast-Rope
        Vector2 chinookPos = new(548.0f, 368.0f);
        EntityId chinookId = world.CreateUnit(
            1,
            ch47Idx,
            chinookPos,
            ch47Def.Speed > 0f ? ch47Def.Speed : 18.0f,
            ch47Def.MaxHealth,
            ch47Ammo,
            facingPlusZ,
            passengerCapacity: ch47Def.PassengerCapacity);
        for (int r = 0; r < 4; r++)
        {
            EntityId airAssault = world.CreateUnit(1, rifleIdx, chinookPos, rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingPlusZ);
            world.Board(chinookId, airAssault);
        }

        // Player 2 (US) behind central panelka facing -Z toward the courtyard,
        // plus a 2-man ambush fireteam pre-garrisoned inside east flanking panelka section 106 for CQB testing
        const float facingMinusZ = 0f;
        world.CreateUnit(2, m60Idx, UsM60WestPos, m60Def.Speed, m60Def.MaxHealth, m60Ammo, facingMinusZ);
        world.CreateUnit(2, m60Idx, UsM60EastPos, m60Def.Speed, m60Def.MaxHealth, m60Ammo, facingMinusZ);
        EntityId opforInf1 = world.CreateUnit(2, rifleIdx, new Vector2(567.9f, 479.6f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, MathF.PI * 0.5f);
        EntityId opforInf2 = world.CreateUnit(2, rifleIdx, new Vector2(567.9f, 479.6f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, MathF.PI * 0.5f);
        world.SetFireStance(opforInf1, FireStance.ReturnFire);
        world.SetFireStance(opforInf2, FireStance.ReturnFire);

        Simulation simulation = new(world, rules, matchSeed: matchSeed);
        if (simulation.BuildingDestruction is not null)
        {
            IReadOnlyList<DestructibleBuildingSpec> specs = geometry.DestructibleBuildings.Count > 0
                ? geometry.DestructibleBuildings
                : GetBuildingSpecs(catalog);
            for (int i = 0; i < specs.Count; i++)
            {
                StructureDestructionProfile prof = specs[i].Profile;
                simulation.BuildingDestruction.RegisterStructure(world, specs[i].Id, in prof);
            }
        }
        if (simulation.Garrison is not null)
        {
            StructureId ambushSection = new(106);
            simulation.Garrison.TryPlaceInitialGarrison(world, opforInf1, ambushSection, GarrisonSlotKind.Interior);
            simulation.Garrison.TryPlaceInitialGarrison(world, opforInf2, ambushSection, GarrisonSlotKind.Interior);
        }

        return simulation;
    }
}
