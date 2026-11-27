using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Large 2048m x 2048m multi-zone tactical proving ground designed for testing
/// infantry, RPG-7V anti-tank combat, urban panelka destruction, armor/helicopters,
/// and forest/bridge navigation simultaneously without units wiping each other out on spawn.
/// </summary>
public static class ProvingGroundScenario
{
    public const string ScenarioName = "proving-ground";
    public const int MapResolution = 129;
    public const float MapSpacing = 16.0f; // 128 * 16m = 2048m x 2048m
    public const float MapSize = (MapResolution - 1) * MapSpacing;
    public const float BaseHeight = 2.0f;
    public const float WaterBedHeight = 0.5f;

    public const string PanelkaProfileId = "panelka_1_464";
    public const float SectionWidth = UrbanDestructionPlaygroundScenario.SectionWidth;
    public const float SectionDepth = UrbanDestructionPlaygroundScenario.SectionDepth;
    public const float BuildingHeight = UrbanDestructionPlaygroundScenario.BuildingHeight;

    // 5 isolated test zones (F1..F5 camera presets)
    public static readonly (string Name, Vector2 Focus, float CameraSize)[] Zones =
    [
        ("F1: AK-74 & Run Range", new Vector2(440f, 520f), 125f),
        ("F2: RPG-7V Anti-Tank Range", new Vector2(1480f, 640f), 170f),
        ("F3: Urban Panelka CQB", new Vector2(1024f, 1080f), 155f),
        ("F4: Armor, Heli & Grad", new Vector2(460f, 1520f), 210f),
        ("F5: Forest & Bridge", new Vector2(1560f, 1540f), 160f),
    ];

    // Zone 5 canal & bridge
    public const float CanalMinX = 1320f;
    public const float CanalMaxX = 1800f;
    public const float CanalMinZ = 1580f;
    public const float CanalMaxZ = 1604f;
    public static readonly Vector2 BridgeStart = new(1560f, 1574f);
    public static readonly Vector2 BridgeEnd = new(1560f, 1610f);
    public const float BridgeWidth = 10.0f;
    public const float BridgeHeight = 2.0f;
    public const float BridgeThickness = 0.8f;

    // Zone 3 panelkas (2-section central block + 2-section east flank)
    private static readonly (int Id, float MinX, float MinZ, float YawRad)[] PanelkaLayout =
    [
        (201, 1009.6f, 1120.0f, MathF.PI),
        (202, 1024.0f, 1120.0f, MathF.PI),
        (203, 1078.0f, 1072.0f, -MathF.PI * 0.5f),
        (204, 1078.0f, 1086.4f, -MathF.PI * 0.5f),
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
        for (int r = 0; r < MapResolution; r++)
        {
            float z = r * MapSpacing;
            int rowOffset = r * MapResolution;
            for (int c = 0; c < MapResolution; c++)
            {
                float x = c * MapSpacing;
                float h = BaseHeight;
                if (x >= CanalMinX && x <= CanalMaxX && z >= CanalMinZ - 12f && z <= CanalMaxZ + 12f)
                {
                    if (z >= CanalMinZ && z <= CanalMaxZ)
                    {
                        h = WaterBedHeight;
                    }
                    else if (z < CanalMinZ)
                    {
                        float t = (z - (CanalMinZ - 12f)) / 12f;
                        h = BaseHeight * (1f - t) + WaterBedHeight * t;
                    }
                    else
                    {
                        float t = (z - CanalMaxZ) / 12f;
                        h = WaterBedHeight * (1f - t) + BaseHeight * t;
                    }
                }
                heights[rowOffset + c] = (ushort)Math.Clamp((int)MathF.Round(h * 100f), 0, ushort.MaxValue);
            }
        }
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

        BridgeStructure bridge = new(
            new StructureId(1),
            BridgeStart,
            BridgeEnd,
            BridgeWidth,
            BridgeHeight,
            BridgeHeight,
            BridgeThickness);

        TerrainObstacle canalWater = new(
            CanalMinX,
            CanalMinZ,
            CanalMaxX,
            CanalMaxZ,
            TraversabilityCapabilities.Water);

        // Zone 1 flanking shelterbelt (west of AK-74 range)
        VegetationZone rangeWoods = VegetationZone.CreateDenseForest(
            "PG_RANGE_WOODS",
            [
                new Vector2(280f, 470f),
                new Vector2(345f, 470f),
                new Vector2(345f, 750f),
                new Vector2(280f, 750f)
            ],
            canopyHeight: 11.0f,
            maxPenetration: 24.0f);

        // Zone 5 dense forest with interior clearing
        VegetationZone northEastForest = VegetationZone.CreateDenseForest(
            "PG_NE_FOREST",
            [
                new Vector2(1380f, 1450f),
                new Vector2(1505f, 1450f),
                new Vector2(1505f, 1560f),
                new Vector2(1380f, 1560f)
            ],
            canopyHeight: 12.0f,
            maxPenetration: 20.0f);

        VegetationClearing forestClearing = VegetationClearing.CreateBox(
            "PG_NE_CLEARING",
            1428f,
            1490f,
            1462f,
            1524f);

        TerrainObstacle forestW = new(1380f, 1450f, 1428f, 1560f, TraversabilityCapabilities.DenseForest);
        TerrainObstacle forestE = new(1462f, 1450f, 1505f, 1560f, TraversabilityCapabilities.DenseForest);
        TerrainObstacle forestN = new(1428f, 1450f, 1462f, 1490f, TraversabilityCapabilities.DenseForest);
        TerrainObstacle forestS = new(1428f, 1524f, 1462f, 1560f, TraversabilityCapabilities.DenseForest);

        DestructibleBuildingSpec[] specs = GetBuildingSpecs(catalog);
        return new StaticGeometry(
            map,
            structures,
            [bridge],
            [canalWater, forestW, forestE, forestN, forestS],
            [rangeWoods, northEastForest],
            [forestClearing],
            destructibleBuildings: specs);
    }

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        string ruleSetId = "ProvingGround")
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
        ulong matchSeed = 2048UL)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();

        NavigationTuning effectiveNavTuning = navTuning ?? new NavigationTuning(CellSize: 4.0f);
        OpsRules rules = new(geometry, catalog, effectiveNavTuning);
        WorldState world = new(geometry.HeightMap);

        EntityId Spawn(int owner, string unitId, float x, float z, float yaw, FireStance? stance = null)
        {
            ushort idx = catalog.RequireDefinitionIndex(unitId);
            UnitDefinition def = catalog.Units[idx];
            int ammo = catalog.GetAmmoCapacity(def);
            float speed = def.Speed > 0f ? def.Speed : 18.0f;
            EntityId id = world.CreateUnit(owner, idx, new Vector2(x, z), speed, def.MaxHealth, ammo, yaw, passengerCapacity: def.PassengerCapacity);
            if (stance.HasValue)
            {
                world.SetFireStance(id, stance.Value);
            }
            return id;
        }

        const float faceNorth = MathF.PI; // +Z
        const float faceSouth = 0f;       // -Z
        const float faceEast = -MathF.PI * 0.5f;

        // =========================================================================
        // ZONE 1 (F1): AK-74 & Infantry Movement Range (X ~ 440, Z ~ 450..850)
        // Targets sit just past 300m rifle range on spawn so nothing wastes ammo
        // before you order the squad forward or click a target; OPFOR holds fire (F6 toggles).
        // =========================================================================
        Spawn(1, "soviet_rifleman", 428f, 450f, faceNorth);
        Spawn(1, "soviet_rifleman", 436f, 450f, faceNorth);
        Spawn(1, "soviet_rifleman", 444f, 450f, faceNorth);
        Spawn(1, "soviet_rifleman", 452f, 450f, faceNorth);
        Spawn(1, "soviet_rpg", 420f, 450f, faceNorth);
        Spawn(1, "soviet_rpg", 460f, 450f, faceNorth);

        // Live HoldFire infantry target lines at 320m and 380m (step 25m north to open fire)
        Spawn(2, "us_rifleman", 432f, 770f, faceSouth, FireStance.HoldFire);
        Spawn(2, "us_rifleman", 440f, 770f, faceSouth, FireStance.HoldFire);
        Spawn(2, "us_rifleman", 448f, 770f, faceSouth, FireStance.HoldFire);
        Spawn(2, "us_rifleman", 430f, 830f, faceSouth, FireStance.HoldFire);
        Spawn(2, "us_rifleman", 440f, 830f, faceSouth, FireStance.HoldFire);
        Spawn(2, "us_rifleman", 450f, 830f, faceSouth, FireStance.HoldFire);
        // Optional close flanking target pair behind range woods (step west to engage at ~140m)
        Spawn(2, "us_rifleman", 260f, 540f, faceSouth, FireStance.HoldFire);
        Spawn(2, "us_rifleman", 260f, 550f, faceSouth, FireStance.HoldFire);

        // =========================================================================
        // ZONE 2 (F2): RPG-7V Anti-Tank Range (X ~ 1480, Z ~ 440..840)
        // Targets sit just outside 300m RPG range so rockets are preserved until you press F2 & engage!
        // =========================================================================
        Spawn(1, "soviet_rpg", 1468f, 440f, faceNorth);
        Spawn(1, "soviet_rpg", 1480f, 440f, faceNorth);
        Spawn(1, "soviet_rpg", 1492f, 440f, faceNorth);
        Spawn(1, "soviet_rifleman", 1458f, 440f, faceNorth);
        Spawn(1, "soviet_rifleman", 1502f, 440f, faceNorth);

        // Armored targets in HoldFire: M113A1 + infantry (splash test), M151A2, M60A1 front, M60A1 flank
        Spawn(2, "m113a1", 1448f, 760f, faceSouth, FireStance.HoldFire);
        Spawn(2, "us_rifleman", 1444f, 760f, faceSouth, FireStance.HoldFire);
        Spawn(2, "us_rifleman", 1452f, 760f, faceSouth, FireStance.HoldFire);
        Spawn(2, "m151a2", 1480f, 790f, faceSouth, FireStance.HoldFire);
        Spawn(2, "m60a1", 1512f, 825f, faceSouth, FireStance.HoldFire);
        Spawn(2, "m60a1", 1458f, 860f, faceEast, FireStance.HoldFire);

        // =========================================================================
        // ZONE 3 (F3): Urban Panelka 1-464 CQB & Destruction (X ~ 1024, Z ~ 1005..1160)
        // =========================================================================
        Spawn(1, "soviet_rifleman", 1012f, 1005f, faceNorth);
        Spawn(1, "soviet_rifleman", 1020f, 1005f, faceNorth);
        Spawn(1, "soviet_rifleman", 1028f, 1005f, faceNorth);
        Spawn(1, "soviet_rpg", 1036f, 1005f, faceNorth);
        Spawn(1, "soviet_rpg", 1004f, 1005f, faceNorth);
        Spawn(1, "t62", 994f, 975f, faceNorth);
        Spawn(1, "btr70", 1046f, 975f, faceNorth);

        EntityId urbanDef1 = Spawn(2, "us_rifleman", 1016.8f, 1125.9f, faceSouth, FireStance.HoldFire);
        EntityId urbanDef2 = Spawn(2, "us_rifleman", 1016.8f, 1125.9f, faceSouth, FireStance.HoldFire);
        EntityId urbanDef3 = Spawn(2, "us_rifleman", 1016.8f, 1125.9f, faceSouth, FireStance.HoldFire);
        Spawn(2, "m60a1", 1018f, 1158f, faceSouth, FireStance.HoldFire);

        // =========================================================================
        // ZONE 4 (F4): Armor, Air-Assault CH-47D, Grad & Strela-10 (X ~ 460, Z ~ 1440..1860)
        // =========================================================================
        Spawn(1, "t62", 430f, 1440f, faceNorth);
        Spawn(1, "t62", 455f, 1440f, faceNorth);
        Spawn(1, "btr70", 480f, 1440f, faceNorth);
        Spawn(1, "brdm2", 505f, 1440f, faceNorth);
        Spawn(1, "bm21", 400f, 1390f, faceNorth);
        Spawn(1, "strela10", 525f, 1390f, faceNorth);
        EntityId ch47 = Spawn(1, "ch47d", 465f, 1375f, faceNorth);
        for (int r = 0; r < 4; r++)
        {
            EntityId trooper = Spawn(1, "soviet_rifleman", 465f, 1375f, faceNorth);
            world.Board(ch47, trooper);
        }

        Spawn(2, "m60a1", 435f, 1860f, faceSouth, FireStance.HoldFire);
        Spawn(2, "m60a1", 475f, 1860f, faceSouth, FireStance.HoldFire);
        Spawn(2, "m113a1", 415f, 1830f, faceSouth, FireStance.HoldFire);
        Spawn(2, "m113a1", 495f, 1830f, faceSouth, FireStance.HoldFire);

        // =========================================================================
        // ZONE 5 (F5): Forest, Canal & Bridge Navigation (X ~ 1560, Z ~ 1500..1670)
        // =========================================================================
        Spawn(1, "soviet_rifleman", 1546f, 1515f, faceNorth);
        Spawn(1, "soviet_rifleman", 1556f, 1515f, faceNorth);
        Spawn(1, "soviet_rifleman", 1566f, 1515f, faceNorth);
        Spawn(1, "soviet_rpg", 1576f, 1515f, faceNorth);
        Spawn(1, "btr70", 1560f, 1490f, faceNorth);

        Spawn(2, "us_rifleman", 1442f, 1506f, faceSouth, FireStance.HoldFire);
        Spawn(2, "us_rifleman", 1450f, 1506f, faceSouth, FireStance.HoldFire);
        Spawn(2, "m113a1", 1560f, 1665f, faceSouth, FireStance.HoldFire);

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
            StructureId panelkaSection = new(201);
            simulation.Garrison.TryPlaceInitialGarrison(world, urbanDef1, panelkaSection, GarrisonSlotKind.Interior);
            simulation.Garrison.TryPlaceInitialGarrison(world, urbanDef2, panelkaSection, GarrisonSlotKind.Interior);
            simulation.Garrison.TryPlaceInitialGarrison(world, urbanDef3, panelkaSection, GarrisonSlotKind.Interior);
        }

        simulation.SetPlayerAdmitted(1, true);
        simulation.SetPlayerAdmitted(2, true);
        return simulation;
    }
}
