using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

/// <summary>
/// Operational reconnaissance raid scenario on the 5000 x 5000 m Ustye map.
/// Soviet recon element (1x BRDM-2, 2x T-62) infiltrates enemy-controlled
/// Town and Central Crossing sector to locate and destroy US command vehicle (M577)
/// defended by an armored guard tank and roving patrols.
/// </summary>
public static class UstyeRaidScenario
{
    public const int TimeLimitTicks = ReconRaidMissionSystem.DefaultTimeLimitTicks; // 12 minutes (14,400 ticks)

    public static readonly Vector2 SearchAreaCenter = new(3450f, 2700f);
    public const float SearchAreaRadius = 180.0f;

    public static readonly Vector2 PlayerBrdm2Spawn = new(2180f, 2325f);
    public static readonly Vector2 PlayerTank1Spawn = new(2160f, 2325f);
    public static readonly Vector2 PlayerTank2Spawn = new(2140f, 2325f);

    public static readonly Vector2 EnemyCommandVehicleSpawn = new(3560f, 2690f);
    public static readonly Vector2 EnemyGuardTankSpawn = new(3330f, 2690f);
    public static readonly Vector2 EnemyPatrolTankSpawn = new(3530f, 2680f);
    public static readonly Vector2 EnemyPatrolReconSpawn = new(3530f, 2672f);

    public static readonly Vector2[] PatrolTankWaypoints =
    [
        new Vector2(3530f, 2680f),
        new Vector2(3580f, 2680f),
        new Vector2(3580f, 2690f),
        new Vector2(3530f, 2690f)
    ];

    public static readonly Vector2[] PatrolReconWaypoints =
    [
        new Vector2(3530f, 2672f),
        new Vector2(3580f, 2675f),
        new Vector2(3580f, 2685f),
        new Vector2(3530f, 2685f)
    ];

    public static StaticGeometry CreateStaticGeometry(UstyePlanData? plan = null, HeightMap? map = null) =>
        UstyeGroundScenario.CreateStaticGeometry(plan, map);

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        string ruleSetId = "UstyeRaid",
        UnitCatalog? catalog = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerTeams: [1, 2],
            PlayerFactions: ["soviet", "us"]);
    }

    public static Simulation CreateSimulation(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        NavigationTuning? navTuning = null,
        DetectionTuning? detTuning = null,
        int timeLimitTicks = TimeLimitTicks,
        ulong matchSeed = 1337UL,
        NavigationGrid? navGrid = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= ColdWarContent.LoadCatalog();

        WorldState world = new(geometry.HeightMap, UstyeGroundScenario.WorldCapacity);

        UnitDefinition brdm2Def = catalog.RequireDefinition("brdm2");
        ushort brdm2Idx = catalog.RequireDefinitionIndex("brdm2");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");

        UnitDefinition m577Def = catalog.RequireDefinition("m577");
        ushort m577Idx = catalog.RequireDefinitionIndex("m577");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m151Def = catalog.RequireDefinition("m151a2");
        ushort m151Idx = catalog.RequireDefinitionIndex("m151a2");

        int t62Ammo = catalog.GetAmmoCapacity(t62Def);
        int brdm2Ammo = catalog.GetAmmoCapacity(brdm2Def);
        int m60Ammo = catalog.GetAmmoCapacity(m60Def);
        int m151Ammo = catalog.GetAmmoCapacity(m151Def);

        // Player (Side 1, Soviet): In Town-West courtyard behind BLOCK-WEST-2-2
        // Facing East towards central crossing avenue (landmark: X = 2520, Z = 2325)
        Vector2 playerOrientLandmark = new(2520f, 2325f);
        float playerFacingYaw = CalculateFacingYaw(PlayerTank1Spawn, playerOrientLandmark);
        EntityId playerReconId = world.CreateUnit(1, brdm2Idx, PlayerBrdm2Spawn, brdm2Def.SpeedRoad, brdm2Def.MaxHealth, brdm2Ammo, playerFacingYaw);
        EntityId playerTank1Id = world.CreateUnit(1, t62Idx, PlayerTank1Spawn, t62Def.SpeedRoad, t62Def.MaxHealth, t62Ammo, playerFacingYaw);
        EntityId playerTank2Id = world.CreateUnit(1, t62Idx, PlayerTank2Spawn, t62Def.SpeedRoad, t62Def.MaxHealth, t62Ammo, playerFacingYaw);

        // Enemy (Side 2, US):
        // Command vehicle: unarmed M577 sheltered in Depot courtyard facing courtyard entrance (North)
        Vector2 depotCourtyardCenter = new(3560f, 2650f);
        float cmdFacingYaw = CalculateFacingYaw(EnemyCommandVehicleSpawn, depotCourtyardCenter);
        EntityId cmdVehicleId = world.CreateUnit(2, m577Idx, EnemyCommandVehicleSpawn, m577Def.SpeedRoad, m577Def.MaxHealth, 0, cmdFacingYaw);

        // Guard tank: stationary guard at Depot entrance facing western road approach (West)
        Vector2 westRoadApproach = new(3100f, 2690f);
        float guardFacingYaw = CalculateFacingYaw(EnemyGuardTankSpawn, westRoadApproach);
        EntityId guardTankId = world.CreateUnit(2, m60Idx, EnemyGuardTankSpawn, m60Def.SpeedRoad, m60Def.MaxHealth, m60Ammo, guardFacingYaw);

        // Patrol tank: patrolling Town-East avenues, facing initial patrol route direction (East)
        float patrolTankYaw = CalculateFacingYaw(EnemyPatrolTankSpawn, PatrolTankWaypoints[1]);
        EntityId patrolTankId = world.CreateUnit(2, m60Idx, EnemyPatrolTankSpawn, m60Def.SpeedRoad, m60Def.MaxHealth, m60Ammo, patrolTankYaw);

        // Patrol recon: patrolling bridge exit and depot approaches, facing initial route direction
        float patrolReconYaw = CalculateFacingYaw(EnemyPatrolReconSpawn, PatrolReconWaypoints[1]);
        EntityId patrolReconId = world.CreateUnit(2, m151Idx, EnemyPatrolReconSpawn, m151Def.SpeedRoad, m151Def.MaxHealth, m151Ammo, patrolReconYaw);

        // Bot configuration
        Vector2 guardAnchor = EnemyGuardTankSpawn;
        ReconRaidBot bot = new(
            PatrolTankWaypoints,
            guardAnchor,
            guardTankId,
            patrolTankId,
            patrolReconId,
            catalog,
            reconWaypoints: PatrolReconWaypoints);

        // Create simulation systems
        OpsMovementCatalog movCatalog = new(catalog);
        OpsPerceptionCatalog perCatalog = new(catalog);
        OpsCombatCatalog comCatalog = new(catalog);

        navTuning ??= UstyeGroundScenario.CreateNavigationTuning();
        NavigationBuildLimits buildLimits = UstyeGroundScenario.CreateNavigationBuildLimits();

        navGrid ??= new NavigationGrid(
            geometry,
            navTuning.CellSize,
            enableHierarchical: true,
            buildLimits: buildLimits,
            sectorSize: navTuning.SectorSize);

        NavigationSystem nav = new(
            navGrid,
            movCatalog,
            navTuning);

        DetectionSystem det = new(
            geometry,
            perCatalog,
            detTuning ?? PrototypeScenario.CreateDetectionTuning(catalog));

        CombatSystem combat = new(geometry, comCatalog, perCatalog, det, nav);

        SimulationBotCommandSink botSink = new(null, 2);

        ReconRaidMissionSystem mission = new(
            playerReconId,
            playerTank1Id,
            playerTank2Id,
            cmdVehicleId,
            SearchAreaCenter,
            SearchAreaRadius,
            det,
            nav,
            combat,
            bot,
            botSink,
            timeLimitTicks,
            world.Capacity);

        OpsRules rules = new(nav, det, combat, mission);
        Simulation sim = new(world, rules, matchSeed);

        // Wire simulation into bot command sink
        botSink.SetSimulation(sim);

        // Admit bot player so combat events are queued for bot decision making
        sim.SetPlayerAdmitted(2, true);

        return sim;
    }

    private static float CalculateFacingYaw(Vector2 from, Vector2 targetLandmark)
    {
        Vector2 dir = targetLandmark - from;
        return MathF.Atan2(-dir.X, -dir.Y);
    }
}
