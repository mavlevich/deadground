using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Dedicated test and playable scenario for indirect-fire rocket artillery (BM-21 Grad).
/// Features a 2048m x 2048m valley split by a steep east-west central ridge blocking
/// direct line of sight, two Soviet BM-21 Grad MLRS launchers with a forward BRDM-2
/// observer overlooking a US staging camp (infantry, M151A2, M113A1, M577, M60A1,
/// and landed CH-47D Chinook) approximately 1220m away.
/// </summary>
public static class ArtilleryPlaygroundScenario
{
    public const string ScenarioName = "artillery-playground";
    public const int MapResolution = 129;
    public const float MapSpacing = 16.0f; // 128 * 16m = 2048m x 2048m
    public const float MapSize = (MapResolution - 1) * MapSpacing;
    public const float BaseHeight = 4.0f;
    public const float RidgeZ = 960.0f;
    public const float RidgeHalfWidth = 220.0f;
    public const float RidgePeakHeight = 46.0f;

    // Side 1 (Soviet): Firing battery behind the northern slope of the central ridge
    public static readonly Vector2 GradAlphaPos = new(996.0f, 360.0f);
    public static readonly Vector2 GradBravoPos = new(1052.0f, 360.0f);
    public static readonly Vector2 EscortTankPos = new(920.0f, 410.0f);
    public static readonly Vector2 ForwardObserverPos = new(1024.0f, 975.0f);

    // Side 2 (US): Staging camp ~1220m south of the BM-21 battery, completely behind the ridge
    public static readonly Vector2 TargetCampCenter = new(1024.0f, 1580.0f);
    public static readonly Vector2 TargetChinookPos = new(982.0f, 1594.0f);
    public static readonly Vector2 TargetM113Pos = new(1054.0f, 1564.0f);
    public static readonly Vector2 TargetJeepPos = new(1004.0f, 1554.0f);
    public static readonly Vector2 TargetTankPos = new(1062.0f, 1606.0f);

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
                float gentleRoll = MathF.Sin(x * 0.0045f) * MathF.Cos(z * 0.0038f) * 2.2f;
                float h = BaseHeight + ridgeLift + gentleRoll;
                heights[rowOffset + c] = (ushort)Math.Clamp((int)MathF.Round(h * 100f), 0, ushort.MaxValue);
            }
        }
        return new HeightMap(MapResolution, MapResolution, MapSpacing, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry(HeightMap? map = null)
    {
        map ??= CreateHeightMap();

        // Flanking woodlines framing the valley while keeping the artillery corridor clear
        VegetationZone westForest = VegetationZone.CreateDenseForest(
            "FOREST_ARTY_WEST",
            [
                new Vector2(460f, 1340f),
                new Vector2(720f, 1340f),
                new Vector2(720f, 1680f),
                new Vector2(460f, 1680f)
            ],
            canopyHeight: 12.0f,
            maxPenetration: 20.0f);

        VegetationZone eastForest = VegetationZone.CreateDenseForest(
            "FOREST_ARTY_EAST",
            [
                new Vector2(1320f, 1340f),
                new Vector2(1580f, 1340f),
                new Vector2(1580f, 1680f),
                new Vector2(1320f, 1680f)
            ],
            canopyHeight: 12.0f,
            maxPenetration: 20.0f);

        OpaqueStructure depotA = new(
            new StructureId(20),
            952f, 0f, 1546f,
            968f, 6.5f, 1564f,
            isGrounded: true);

        OpaqueStructure depotB = new(
            new StructureId(21),
            1082f, 0f, 1572f,
            1098f, 6.5f, 1592f,
            isGrounded: true);

        return new StaticGeometry(
            map,
            [depotA, depotB],
            [],
            [],
            [westForest, eastForest]);
    }

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        string ruleSetId = "ArtilleryPlayground")
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerFactions: ["soviet", "us"]);
    }

    public static Simulation CreateSimulation(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        NavigationTuning? navTuning = null,
        ulong matchSeed = 2024UL)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();

        // Use 2.0m cell size on the 2048m x 2048m artillery map for fast startup and low memory
        NavigationTuning effectiveNavTuning = navTuning ?? new NavigationTuning(CellSize: 2.0f);
        OpsRules rules = new(geometry, catalog, effectiveNavTuning);
        WorldState world = new(geometry.HeightMap);

        UnitDefinition bm21Def = catalog.RequireDefinition("bm21");
        ushort bm21Idx = catalog.RequireDefinitionIndex("bm21");
        int bm21Ammo = catalog.GetAmmoCapacity(bm21Def);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        int t62Ammo = catalog.GetAmmoCapacity(t62Def);

        UnitDefinition brdmDef = catalog.RequireDefinition("brdm2");
        ushort brdmIdx = catalog.RequireDefinitionIndex("brdm2");
        int brdmAmmo = catalog.GetAmmoCapacity(brdmDef);

        UnitDefinition m577Def = catalog.RequireDefinition("m577");
        ushort m577Idx = catalog.RequireDefinitionIndex("m577");

        UnitDefinition ch47Def = catalog.RequireDefinition("ch47d");
        ushort ch47Idx = catalog.RequireDefinitionIndex("ch47d");
        int ch47Ammo = catalog.GetAmmoCapacity(ch47Def);

        UnitDefinition m113Def = catalog.RequireDefinition("m113a1");
        ushort m113Idx = catalog.RequireDefinitionIndex("m113a1");
        int m113Ammo = catalog.GetAmmoCapacity(m113Def);

        UnitDefinition jeepDef = catalog.RequireDefinition("m151a2");
        ushort jeepIdx = catalog.RequireDefinitionIndex("m151a2");
        int jeepAmmo = catalog.GetAmmoCapacity(jeepDef);

        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        int m60Ammo = catalog.GetAmmoCapacity(m60Def);

        UnitDefinition rifleDef = catalog.RequireDefinition("us_rifleman");
        ushort rifleIdx = catalog.RequireDefinitionIndex("us_rifleman");
        int rifleAmmo = catalog.GetAmmoCapacity(rifleDef);

        // Side 1 (Player, Soviet): 2 BM-21 Grads + T-62 security escort + BRDM-2 forward observer on ridge
        const float facingSouth = MathF.PI;
        world.CreateUnit(1, bm21Idx, GradAlphaPos, bm21Def.Speed, bm21Def.MaxHealth, bm21Ammo, facingSouth);
        world.CreateUnit(1, bm21Idx, GradBravoPos, bm21Def.Speed, bm21Def.MaxHealth, bm21Ammo, facingSouth);
        world.CreateUnit(1, t62Idx, EscortTankPos, t62Def.Speed, t62Def.MaxHealth, t62Ammo, facingSouth);
        EntityId observerId = world.CreateUnit(1, brdmIdx, ForwardObserverPos, brdmDef.Speed, brdmDef.MaxHealth, brdmAmmo, facingSouth);
        world.SetFireStance(observerId, FireStance.HoldFire);

        // Side 2 (Opponent, US): Bivouacked staging camp in ReturnFire posture behind the ridge
        const float facingNorth = 0f;
        EntityId[] usUnits =
        [
            world.CreateUnit(2, m577Idx, TargetCampCenter, m577Def.Speed, m577Def.MaxHealth, 0, facingNorth),
            world.CreateUnit(2, ch47Idx, TargetChinookPos, ch47Def.Speed, ch47Def.MaxHealth, ch47Ammo, facingNorth, passengerCapacity: ch47Def.PassengerCapacity),
            world.CreateUnit(2, m113Idx, TargetM113Pos, m113Def.Speed, m113Def.MaxHealth, m113Ammo, facingNorth, passengerCapacity: m113Def.PassengerCapacity),
            world.CreateUnit(2, jeepIdx, TargetJeepPos, jeepDef.Speed, jeepDef.MaxHealth, jeepAmmo, facingNorth),
            world.CreateUnit(2, m60Idx, TargetTankPos, m60Def.Speed, m60Def.MaxHealth, m60Ammo, facingNorth),
            world.CreateUnit(2, rifleIdx, TargetCampCenter + new Vector2(-14f, -8f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingNorth),
            world.CreateUnit(2, rifleIdx, TargetCampCenter + new Vector2(-6f, -12f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingNorth),
            world.CreateUnit(2, rifleIdx, TargetCampCenter + new Vector2(10f, -10f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingNorth),
            world.CreateUnit(2, rifleIdx, TargetCampCenter + new Vector2(18f, 4f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingNorth),
            world.CreateUnit(2, rifleIdx, TargetCampCenter + new Vector2(-10f, 12f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingNorth),
            world.CreateUnit(2, rifleIdx, TargetCampCenter + new Vector2(6f, 14f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo, facingNorth)
        ];

        for (int i = 0; i < usUnits.Length; i++)
        {
            world.SetFireStance(usUnits[i], FireStance.ReturnFire);
        }

        return new Simulation(world, rules, matchSeed);
    }
}
