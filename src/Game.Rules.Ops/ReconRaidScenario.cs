using System.Numerics;
using DeadGround.Engine;

namespace DeadGround.Rules.Ops;

public sealed class ReconRaidMissionSystem : IMissionSystem
{
    public const int DefaultTimeLimitTicks = 12 * 60 * Simulation.TickRate; // 14,400 ticks (12 minutes)
    public const ushort MissionIdentifier = 1;

    private readonly EntityId _playerReconId;
    private readonly EntityId _playerTank1Id;
    private readonly EntityId _playerTank2Id;
    private readonly EntityId _commandVehicleId;
    private readonly Vector2 _searchAreaCenter;
    private readonly float _searchAreaRadius;
    private readonly int _timeLimitTicks;

    private readonly DetectionSystem _detection;
    private readonly NavigationSystem? _navigation;
    private readonly CombatSystem? _combat;
    private readonly ReconRaidBot? _bot;
    private readonly PlayerKnowledge? _botKnowledge;
    private readonly IBotCommandSink? _botSink;

    public MissionStatus Status { get; private set; } = MissionStatus.InProgress;
    public MissionEndReason EndReason { get; private set; } = MissionEndReason.None;
    public int FinishedTick { get; private set; }
    public bool IsFinished => Status is MissionStatus.Victory or MissionStatus.Defeat;

    public ReconRaidMissionSystem(
        EntityId playerReconId,
        EntityId playerTank1Id,
        EntityId playerTank2Id,
        EntityId commandVehicleId,
        Vector2 searchAreaCenter,
        float searchAreaRadius,
        DetectionSystem detection,
        NavigationSystem? navigation = null,
        CombatSystem? combat = null,
        ReconRaidBot? bot = null,
        IBotCommandSink? botSink = null,
        int timeLimitTicks = DefaultTimeLimitTicks,
        int botCapacity = 1024)
    {
        _playerReconId = playerReconId;
        _playerTank1Id = playerTank1Id;
        _playerTank2Id = playerTank2Id;
        _commandVehicleId = commandVehicleId;
        _searchAreaCenter = searchAreaCenter;
        _searchAreaRadius = searchAreaRadius;
        _detection = detection ?? throw new ArgumentNullException(nameof(detection));
        _navigation = navigation;
        _combat = combat;
        _bot = bot;
        _botSink = botSink;
        _timeLimitTicks = timeLimitTicks;

        if (_bot is not null)
        {
            _botKnowledge = new PlayerKnowledge(2, botCapacity);
        }
    }

    public void Update(WorldState world, int tick)
    {
        // 1. Evaluate termination conditions if mission is still in progress
        if (!IsFinished)
        {
            bool cmdDestroyed = !world.TryGetUnit(_commandVehicleId, out _);
            bool reconDestroyed = !world.TryGetUnit(_playerReconId, out _);
            bool tank1Alive = world.TryGetUnit(_playerTank1Id, out _);
            bool tank2Alive = world.TryGetUnit(_playerTank2Id, out _);
            bool allArmedDestroyed = !tank1Alive && !tank2Alive;
            bool timeExpired = tick >= _timeLimitTicks;

            bool isDefeat = reconDestroyed || allArmedDestroyed || timeExpired;
            bool isVictory = cmdDestroyed && !reconDestroyed;

            // Defeat strictly takes precedence over victory if both conditions occur simultaneously
            if (isDefeat)
            {
                Status = MissionStatus.Defeat;
                if (reconDestroyed)
                    EndReason = MissionEndReason.ReconVehicleDestroyed;
                else if (allArmedDestroyed)
                    EndReason = MissionEndReason.AllArmedUnitsDestroyed;
                else
                    EndReason = MissionEndReason.TimeLimitExpired;

                FinishedTick = tick;
            }
            else if (isVictory)
            {
                Status = MissionStatus.Victory;
                EndReason = MissionEndReason.TargetDestroyed;
                FinishedTick = tick;
            }
        }

        // 2. Drive bot decisions if still in progress
        if (!IsFinished && _bot is not null && _botKnowledge is not null && _botSink is not null)
        {
            if (tick % ReconRaidBot.DecisionIntervalTicks == 0)
            {
                _botSink.UpdateKnowledge(_botKnowledge);
                _bot.Update(_botKnowledge, tick, _botSink);
            }
        }
    }

    public MissionObservation GetObservation(int player)
    {
        if (player == 1)
        {
            // For player 1: resolve target contact if earned through spotting the command vehicle
            ContactId contactId = default;
            bool hasTargetContact = false;

            ReadOnlySpan<ContactObservation> contacts = _detection.GetContacts(1);
            for (int i = 0; i < contacts.Length; i++)
            {
                ref readonly ContactObservation contact = ref contacts[i];
                if (contact.Classification == ContactClassification.CommandVehicle)
                {
                    contactId = contact.Id;
                    hasTargetContact = true;
                    break;
                }
            }

            byte flags = 0;
            if (hasTargetContact) flags |= MissionObservation.FlagTargetSpotted;
            if (Status == MissionStatus.Victory || EndReason == MissionEndReason.TargetDestroyed)
                flags |= MissionObservation.FlagTargetDestroyed;

            return new MissionObservation(
                MissionIdentifier,
                Status,
                EndReason,
                _timeLimitTicks,
                FinishedTick,
                contactId,
                _searchAreaCenter,
                _searchAreaRadius,
                flags);
        }

        return new MissionObservation(
            MissionIdentifier,
            Status,
            EndReason,
            _timeLimitTicks,
            FinishedTick,
            default,
            _searchAreaCenter,
            _searchAreaRadius,
            0);
    }
}

public static class ReconRaidScenario
{
    public static HeightMap CreateMap()
    {
        const int size = 257; // 256 cells * 2m = 512m
        ushort[] heights = new ushort[size * size];

        // Gentle elevation in the northeast and central ridges
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                float worldX = x * 2.0f;
                float worldZ = z * 2.0f;

                float h = 0f;
                if (worldX > 350f && worldZ > 350f)
                {
                    h += MathF.Min(2.5f, (worldX - 350f) * 0.015f + (worldZ - 350f) * 0.015f);
                }
                heights[z * size + x] = (ushort)(h / 0.01f);
            }
        }

        return new HeightMap(size, size, 2.0f, 0.01f, heights);
    }

    public static StaticGeometry CreateStaticGeometry()
    {
        HeightMap map = CreateMap();
        List<OpaqueStructure> structures = new(32);
        int structId = 1;

        // 1. Enemy Command Post Compound [410..460, 410..460]
        // North wall
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 410f, minY: 0f, minZ: 454f,
            maxX: 460f, maxY: 4.5f, maxZ: 458f,
            isGrounded: true));
        // East wall
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 456f, minY: 0f, minZ: 410f,
            maxX: 460f, maxY: 4.5f, maxZ: 458f,
            isGrounded: true));
        // South wall
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 410f, minY: 0f, minZ: 406f,
            maxX: 460f, maxY: 4.5f, maxZ: 410f,
            isGrounded: true));
        // West side is open entrance for tactical penetration

        // 2. Guard Tank Low Wall Cover
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 388f, minY: 0f, minZ: 418f,
            maxX: 390f, maxY: 1.1f, maxZ: 438f,
            isGrounded: true));

        // 3. Northwest Flank Ruins / Village [80..260, 340..460]
        // Building Block 1
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 90f, minY: 0f, minZ: 340f,
            maxX: 130f, maxY: 5.5f, maxZ: 370f,
            isGrounded: true));
        // Building Block 2
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 160f, minY: 0f, minZ: 340f,
            maxX: 200f, maxY: 5.5f, maxZ: 370f,
            isGrounded: true));
        // Building Block 3
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 230f, minY: 0f, minZ: 340f,
            maxX: 270f, maxY: 5.5f, maxZ: 370f,
            isGrounded: true));
        // Building Block 4
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 110f, minY: 0f, minZ: 410f,
            maxX: 150f, maxY: 5.5f, maxZ: 440f,
            isGrounded: true));
        // Building Block 5
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 180f, minY: 0f, minZ: 410f,
            maxX: 220f, maxY: 5.5f, maxZ: 440f,
            isGrounded: true));

        // 4. Central Open Approach Barriers and Small Building
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 240f, minY: 0f, minZ: 210f,
            maxX: 244f, maxY: 1.2f, maxZ: 250f,
            isGrounded: true));
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 266f, minY: 0f, minZ: 240f,
            maxX: 270f, maxY: 1.2f, maxZ: 280f,
            isGrounded: true));
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 285f, minY: 0f, minZ: 285f,
            maxX: 315f, maxY: 4.5f, maxZ: 315f,
            isGrounded: true));

        // 5. Southern Pass Obstacle
        structures.Add(new OpaqueStructure(
            new StructureId(structId++),
            minX: 320f, minY: 0f, minZ: 180f,
            maxX: 350f, maxY: 3.5f, maxZ: 200f,
            isGrounded: true));

        return new StaticGeometry(map, structures.ToArray());
    }

    public static Simulation CreateSimulation(
        StaticGeometry? geometry = null,
        UnitCatalog? catalog = null,
        NavigationTuning? navTuning = null,
        DetectionTuning? detTuning = null,
        int timeLimitTicks = ReconRaidMissionSystem.DefaultTimeLimitTicks,
        ulong matchSeed = 1337UL)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= PrototypeScenario.LoadCatalog();

        WorldState world = new(geometry.HeightMap);

        UnitDefinition reconDef = catalog.RequireDefinition("recon_vehicle");
        ushort reconIdx = catalog.RequireDefinitionIndex("recon_vehicle");
        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");
        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
        UnitDefinition cmdDef = catalog.RequireDefinition("prototype_vehicle");
        ushort cmdIdx = catalog.RequireDefinitionIndex("prototype_vehicle");

        int tankAmmo = catalog.GetAmmoCapacity(tankDef);

        // Side 1 (Player): 1 recon vehicle and 2 tanks in southwest corner [60..80, 60..80]
        EntityId playerReconId = world.CreateUnit(1, reconIdx, new Vector2(70f, 70f), reconDef.Speed, reconDef.MaxHealth, 0);
        EntityId playerTank1Id = world.CreateUnit(1, tankIdx, new Vector2(60f, 80f), tankDef.Speed, tankDef.MaxHealth, tankAmmo);
        EntityId playerTank2Id = world.CreateUnit(1, tankIdx, new Vector2(80f, 60f), tankDef.Speed, tankDef.MaxHealth, tankAmmo);

        // Side 2 (Enemy):
        // Command vehicle: unarmed, 80 HP, at compound [435, 432]
        EntityId cmdVehicleId = world.CreateUnit(2, cmdIdx, new Vector2(435f, 432f), cmdDef.Speed, 80, 0);

        // Guard tank: stationary guard at [396, 428]
        EntityId guardTankId = world.CreateUnit(2, tankIdx, new Vector2(396f, 428f), tankDef.Speed, tankDef.MaxHealth, tankAmmo);

        // Patrol tank: patrolling central crossroads and passes
        EntityId patrolTankId = world.CreateUnit(2, tankIdx, new Vector2(250f, 240f), tankDef.Speed, tankDef.MaxHealth, tankAmmo);

        // Patrol recon: patrolling flank approach
        EntityId patrolReconId = world.CreateUnit(2, reconIdx, new Vector2(210f, 290f), reconDef.Speed, reconDef.MaxHealth, 0);

        // Bot configuration
        Vector2[] patrolWaypoints =
        [
            new Vector2(250f, 240f),
            new Vector2(340f, 230f),
            new Vector2(360f, 320f),
            new Vector2(290f, 330f)
        ];
        Vector2 guardAnchor = new(396f, 428f);

        ReconRaidBot bot = new(
            patrolWaypoints,
            guardAnchor,
            guardTankId,
            patrolTankId,
            patrolReconId,
            catalog);

        // Create simulation systems
        OpsMovementCatalog movCatalog = new(catalog);
        OpsPerceptionCatalog perCatalog = new(catalog);
        OpsCombatCatalog comCatalog = new(catalog);

        NavigationSystem nav = new(
            new NavigationGrid(geometry, navTuning?.CellSize ?? NavigationTuning.DefaultCellSize, enableHierarchical: true),
            movCatalog,
            navTuning);

        DetectionSystem det = new(
            geometry,
            perCatalog,
            detTuning ?? PrototypeScenario.CreateDetectionTuning(catalog));

        CombatSystem combat = new(geometry, comCatalog, perCatalog, det, nav);

        Vector2 searchAreaCenter = new(420f, 420f);
        float searchAreaRadius = 70.0f;

        SimulationBotCommandSink botSink = new(null, 2);

        ReconRaidMissionSystem mission = new(
            playerReconId,
            playerTank1Id,
            playerTank2Id,
            cmdVehicleId,
            searchAreaCenter,
            searchAreaRadius,
            det,
            nav,
            combat,
            bot,
            botSink,
            timeLimitTicks,
            world.Capacity);

        OpsRules rules = new(nav, det, combat, mission);
        Simulation sim = new(world, rules, matchSeed);

        // Wire simulation reference into bot command sink
        botSink.SetSimulation(sim);

        // Admit bot player (side 2) so combat events are queued for bot decision making
        sim.SetPlayerAdmitted(2, true);

        return sim;
    }

    public static MatchSetup CreateMatchSetup(
        StaticGeometry? geometry = null,
        string ruleSetId = "ReconRaid",
        UnitCatalog? catalog = null)
    {
        geometry ??= CreateStaticGeometry();
        catalog ??= PrototypeScenario.LoadCatalog();
        return new MatchSetup(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            ruleSetId,
            PlayerFactions: PrototypeScenario.CreatePlayerFactions(catalog));
    }
}
