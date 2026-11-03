using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class UstyeGroundScenarioTests
{
    private static void Pump(SimulationHost host, ClientSession c1, ClientSession c2, int iterations = 1, int delayMs = 5)
    {
        for (int i = 0; i < iterations; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            c1.Advance(1d / Simulation.TickRate);
            c2.Advance(1d / Simulation.TickRate);
            if (delayMs > 0) Thread.Sleep(delayMs);
        }
    }

    private static void WaitFor(Func<bool> condition, SimulationHost host, ClientSession c1, ClientSession c2, int maxAttempts = 150)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
        {
            Pump(host, c1, c2, iterations: 1, delayMs: 5);
        }
        Assert.True(condition(), "Condition was not met within maximum attempts.");
    }

    [Fact]
    public void Test01_GeometryAndLayoutHashReproducibility_DeterministicAndCountsMatch()
    {
        UstyePlanData plan = UstyePlanData.Load();
        Assert.NotNull(plan);
        Assert.Equal(5000f, plan.ExtentX);
        Assert.Equal(5000f, plan.ExtentZ);
        Assert.Equal(3, plan.Crossings.Length);

        StaticGeometry geo1 = UstyeGroundScenario.CreateStaticGeometry(plan);
        StaticGeometry geo2 = UstyeGroundScenario.CreateStaticGeometry(plan);

        ulong hash1 = MatchSetup.ComputeMapHash(geo1);
        ulong hash2 = MatchSetup.ComputeMapHash(geo2);

        Assert.Equal(hash1, hash2);
        Assert.NotEqual(0UL, hash1);

        Assert.Equal(3, geo1.Bridges.Count);
        Assert.Equal(23, geo1.Structures.Count);
        Assert.Equal(590, geo1.TerrainObstacles.Count);

        Assert.Equal(1, geo1.Bridges[0].Id.Value); // CROSS-N
        Assert.Equal(2, geo1.Bridges[1].Id.Value); // CROSS-C
        Assert.Equal(3, geo1.Bridges[2].Id.Value); // CROSS-S

        Assert.Equal(8f, geo1.Bridges[0].Width);
        Assert.Equal(14f, geo1.Bridges[1].Width);
        Assert.Equal(6f, geo1.Bridges[2].Width);
    }

    [Fact]
    public void Test02_WaterImpassabilityOutsideCrossings_BlocksMovement()
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        DestinationEvaluator evaluator = new(geometry);

        // 1. Point in river channel outside bridges (Z = 2300, river X around 2550)
        Vector2 riverWaterPoint = new(2550f, 2300f);
        DestinationBlockReason reasonRiver = evaluator.DiagnoseBlockReason(riverWaterPoint, 1.5f, TraversabilityCapabilities.None, supportSurface: 0u);
        Assert.Equal(DestinationBlockReason.Water, reasonRiver);

        // 2. Point on dry land (West bank at Z = 2300, X = 2400)
        Vector2 westBankLand = new(2400f, 2300f);
        Assert.True(evaluator.IsPointClear(westBankLand, 1.5f, TraversabilityCapabilities.None, supportSurface: 0u),
            "Dry terrain on river bank must be clear on surface 0.");

        // 3. Point on bridge deck CROSS-C (Z = 2450, X = 2650, surface = 2)
        Vector2 bridgeCenter = new(2650f, 2450f);
        Assert.True(evaluator.IsPointClear(bridgeCenter, 1.5f, TraversabilityCapabilities.DefaultVehicle, supportSurface: 2u),
            "Bridge deck must be clear on surface 2.");

        // 4. Point under bridge deck CROSS-C on ground/water (surface = 0)
        DestinationBlockReason reasonUnderBridge = evaluator.DiagnoseBlockReason(bridgeCenter, 1.5f, TraversabilityCapabilities.None, supportSurface: 0u);
        Assert.Equal(DestinationBlockReason.Water, reasonUnderBridge);
    }

    [Fact]
    public void Test03_ThreeBridgesCrossing_UnitsTraverseDecksInBothDirections()
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();

        NavigationTuning navTuning = UstyeGroundScenario.CreateNavigationTuning();
        NavigationBuildLimits buildLimits = UstyeGroundScenario.CreateNavigationBuildLimits();

        WorldState world = new(geometry.HeightMap, 16);
        OpsRules rules = new(geometry, catalog, navTuning, buildLimits: buildLimits);
        using Simulation sim = new(world, rules);
        NavigationSystem navSys = (rules as RuleSet)!.FindSystem<NavigationSystem>()!;

        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");

        uint sequence = 1;
        foreach (BridgeStructure bridge in geometry.Bridges)
        {
            foreach (bool reverseDirection in new[] { false, true })
            {
                Vector2 start = (reverseDirection ? bridge.End : bridge.Start) + (reverseDirection ? bridge.Direction : -bridge.Direction) * 30f;
                Vector2 goal = (reverseDirection ? bridge.Start : bridge.End) + (reverseDirection ? -bridge.Direction : bridge.Direction) * 30f;
                Vector2 direction = Vector2.Normalize(goal - start);
                EntityId id = world.CreateUnit(1, t62Idx, start, t62Def.Speed, t62Def.MaxHealth, 0, MathF.Atan2(-direction.X, -direction.Y));
                world.SetFireStance(id, FireStance.HoldFire);

                bool queued = sim.TryEnqueue(1, new MoveCommand(sequence++, sim.Tick, id, goal));
                Assert.True(queued);

                bool seenDeck = false;
                for (int ticks = 0; ticks < 2400; ticks++)
                {
                    sim.Step();
                    world.TryGetUnit(id, out UnitState u);
                    seenDeck |= u.SupportSurface == (uint)bridge.Id.Value;
                    if (!u.Moving && Vector2.Distance(u.Position, goal) < 0.2f) break;
                }

                world.TryGetUnit(id, out UnitState end);
                Assert.True(seenDeck, $"Unit did not traverse deck of bridge {bridge.Id.Value} (reverse={reverseDirection}).");
                Assert.True(Vector2.Distance(end.Position, goal) < 0.2f,
                    $"Unit did not reach goal across bridge {bridge.Id.Value} (dist={Vector2.Distance(end.Position, goal):F2}).");
                Assert.Equal(PathRequestStatus.PathFound, navSys.GetUnitStatus(id));

                world.RemoveUnit(id);
                sim.Step();
            }
        }
    }

    [Fact]
    public void Test04_LongRangeHierarchicalRouting_OppositeBanks()
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();

        NavigationTuning navTuning = UstyeGroundScenario.CreateNavigationTuning();
        NavigationBuildLimits buildLimits = UstyeGroundScenario.CreateNavigationBuildLimits();

        WorldState world = new(geometry.HeightMap, 16);
        OpsRules rules = new(geometry, catalog, navTuning, buildLimits: buildLimits);
        using Simulation sim = new(world, rules);
        NavigationSystem navSys = (rules as RuleSet)!.FindSystem<NavigationSystem>()!;

        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");

        // Long multi-chunk route across the city and central bridge CROSS-C (1800m)
        Vector2 westCityPos = new(1500f, 2450f);
        Vector2 eastRiverPos = new(3300f, 2450f);

        EntityId tank = world.CreateUnit(1, t62Idx, westCityPos, t62Def.Speed);
        world.SetFireStance(tank, FireStance.HoldFire);

        Assert.True(sim.TryEnqueue(1, new MoveCommand(1u, sim.Tick, tank, eastRiverPos)));

        bool arrived = false;
        for (int tick = 0; tick < 6000; tick++)
        {
            sim.Step();
            world.TryGetUnit(tank, out UnitState u);
            if (!u.Moving && Vector2.Distance(u.Position, eastRiverPos) < 0.2f)
            {
                arrived = true;
                break;
            }
            PathRequestStatus status = navSys.GetUnitStatus(tank);
            if (tick > 50 && status is PathRequestStatus.NoPath or PathRequestStatus.InvalidDestination or PathRequestStatus.CapacityExceeded)
                break;
        }

        world.TryGetUnit(tank, out UnitState end);
        Assert.True(arrived, $"Tank failed to arrive at destination {eastRiverPos}. Final pos={end.Position}, dist={Vector2.Distance(end.Position, eastRiverPos):F2}");
        Assert.Equal(PathRequestStatus.PathFound, navSys.GetUnitStatus(tank));
    }

    [Fact]
    public void Test05_BuildingMovementAndLineOfSightOcclusion()
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();

        // Building BLOCK-WEST-1-1: min = (2040, 2160), max = (2120, 2178), height = 15m
        // Soviet scout at (2080, 2200), US scout at (2080, 2130) -> separated across width of building
        Vector2 sovietPos = new(2080f, 2200f);
        Vector2 usPos = new(2080f, 2130f);

        // 1. Verify building blocks direct line of sight between Soviet and US scouts
        float sovietH = geometry.HeightMap.GetHeight(sovietPos.X, sovietPos.Y) + 1.8f;
        float usH = geometry.HeightMap.GetHeight(usPos.X, usPos.Y) + 1.8f;

        Vector3 rayStart = new(sovietPos.X, sovietH, sovietPos.Y);
        Vector3 rayEnd = new(usPos.X, usH, usPos.Y);

        bool hasLosBlocked = geometry.IsLineOfSightClear(rayStart, rayEnd);
        Assert.False(hasLosBlocked, "Building BLOCK-WEST-1-1 must occlude direct line of sight.");

        // 2. Clear line of sight past the building east edge (X = 2140 > 2120)
        Vector2 clearSovietPos = new(2140f, 2200f);
        Vector2 clearUsPos = new(2140f, 2130f);
        float clearSovietH = geometry.HeightMap.GetHeight(clearSovietPos.X, clearSovietPos.Y) + 1.8f;
        float clearUsH = geometry.HeightMap.GetHeight(clearUsPos.X, clearUsPos.Y) + 1.8f;

        bool hasLosClear = geometry.IsLineOfSightClear(
            new Vector3(clearSovietPos.X, clearSovietH, clearSovietPos.Y),
            new Vector3(clearUsPos.X, clearUsH, clearUsPos.Y));
        Assert.True(hasLosClear, "Line of sight in clear street corridor east of building must be open.");
    }

    [Fact]
    public void Test06_GroupMoveOrders_SovietSquad_AssignsSixDistinctDestinations()
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();

        using Simulation sim = UstyeGroundScenario.CreateSimulation(geometry, catalog, setupIndex: 0);
        for (int i = 0; i < sim.World.Count; i++)
            sim.World.SetFireStance(sim.World.GetUnitAt(i).Id, FireStance.HoldFire);
        using LocalSession session = new(sim, player: 1);

        for (int i = 0; i < 5; i++) session.Advance(1d / Simulation.TickRate);
        Assert.Equal(6, session.Replica.OwnUnits.Length);

        EntityId[] squad = new EntityId[6];
        for (int i = 0; i < 6; i++) squad[i] = session.Replica.OwnUnits[i].Id;

        Vector2 groupTarget = new(2550f, 2450f); // On central bridge approach
        Assert.True(session.GroupMove(squad, groupTarget));

        for (int i = 0; i < 20; i++)
        {
            session.Advance(1d / Simulation.TickRate);
            if (session.LastGroupMoveResult is { Placed.Length: 6 }) break;
        }

        Assert.NotNull(session.LastGroupMoveResult);
        GroupMoveResult result = session.LastGroupMoveResult.Value;
        Assert.Equal(6, result.Placed.Length);

        // Verify distinct destination coordinates
        for (int i = 0; i < result.Placed.Length; i++)
        {
            for (int j = i + 1; j < result.Placed.Length; j++)
            {
                float dist = Vector2.Distance(result.Placed.Span[i].Target, result.Placed.Span[j].Target);
                Assert.True(dist >= 2.0f, $"Destinations {i} and {j} are too close: {dist:F2}m.");
            }
        }
    }

    [Fact]
    public void Test07_LiteNetLib_TwoClients_HiddenContactIsolation()
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        MatchSetup setup = UstyeGroundScenario.CreateMatchSetup(geometry, catalog);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;

        // Setup 3: ReconLOS - Soviet and US forces occluded by BLOCK-WEST-1-1
        Simulation serverSim = UstyeGroundScenario.CreateSimulation(geometry, catalog, setupIndex: 3);
        for (int i = 0; i < serverSim.World.Count; i++)
            serverSim.World.SetFireStance(serverSim.World.GetUnitAt(i).Id, FireStance.HoldFire);
        using SimulationHost host = new(serverSim, serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 6 && client2.Replica.OwnUnits.Length == 6, host, client1, client2);

        Assert.Equal(1, client1.AssignedPlayerId);
        Assert.Equal(2, client2.AssignedPlayerId);

        // Advance simulation for initial detection evaluation
        Pump(host, client1, client2, iterations: 15, delayMs: 5);

        // Because BLOCK-WEST-1-1 completely blocks LOS, neither side should have contacts yet
        Assert.Equal(0, client1.Replica.ContactCount);
        Assert.Equal(0, client2.Replica.ContactCount);

        // Client 1 moves scout (unit 0, BRDM-2 at 2080, 2200) east to open corridor (2140, 2200)
        EntityId scout1 = client1.Replica.OwnUnits[0].Id;
        Assert.True(client1.Move(scout1, new Vector2(2140f, 2200f)));

        // Client 2 moves scout (unit 0, M151 at 2080, 2130) east to open corridor (2140, 2130)
        EntityId scout2 = client2.Replica.OwnUnits[0].Id;
        Assert.True(client2.Move(scout2, new Vector2(2140f, 2130f)));

        // Advance simulation until scouts reach the open corridor and gain LOS to each other
        WaitFor(() => client1.Replica.ContactCount > 0 && client2.Replica.ContactCount > 0,
            host, client1, client2, maxAttempts: 150);

        Assert.True(client1.Replica.ContactCount > 0, "Client 1 scout should detect enemy after rounding the building corner.");
        Assert.True(client2.Replica.ContactCount > 0, "Client 2 scout should detect enemy after rounding the building corner.");
        ContactObservation detected = client1.Replica.Contacts[0];
        Assert.Equal(ContactState.Current, detected.State);
    }

    [Fact]
    public void Test08_CombatEngagement_UnitsFireWeapons()
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        UnitCatalog catalog = ColdWarContent.LoadCatalog();

        // Setup 2: CityHeadOn has opposing units facing each other in open corridors
        using Simulation sim = UstyeGroundScenario.CreateSimulation(geometry, catalog, setupIndex: 2);

        int initialTotalAmmo = 0;
        for (int i = 0; i < sim.World.Count; i++)
        {
            if (sim.World.TryGetCombatState(sim.World.GetUnitAt(i).Id, out WorldState.CombatState cs))
            {
                initialTotalAmmo += cs.Ammunition;
            }
        }
        Assert.True(initialTotalAmmo > 0, "Units must spawn with non-zero ammunition.");

        bool shotsFired = false;
        for (int tick = 0; tick < 200; tick++)
        {
            sim.Step();
            int currentTotalAmmo = 0;
            for (int i = 0; i < sim.World.Count; i++)
            {
                if (sim.World.TryGetCombatState(sim.World.GetUnitAt(i).Id, out WorldState.CombatState cs))
                {
                    currentTotalAmmo += cs.Ammunition;
                }
            }
            if (currentTotalAmmo < initialTotalAmmo)
            {
                shotsFired = true;
                break;
            }
        }
        Assert.True(shotsFired, "Units in direct combat range must fire weapons and expend ammunition.");
    }

    [Fact]
    public void Test09_WaterBoundary_DenseEdgeChecks()
    {
        StaticGeometry geometry = UstyeGroundScenario.CreateStaticGeometry();
        DestinationEvaluator evaluator = new(geometry);

        // Explicit reviewer probe points near map boundaries
        Vector2 p1 = new(4000f, 4997.8f);
        Vector2 p2 = new(4997.8f, 4000f);

        Assert.False(evaluator.IsPointClear(p1, 1.65f, TraversabilityCapabilities.DefaultVehicle, 0u),
            "Border point (4000, 4997.8) must be blocked by water.");
        Assert.False(evaluator.IsPointClear(p2, 1.65f, TraversabilityCapabilities.DefaultVehicle, 0u),
            "Border point (4997.8, 4000) must be blocked by water.");

        // Dense sampling along south border (Z from 4995 to 5000, X in bay region 3300..5000)
        // and east border (X from 4995 to 5000, Z in bay region 3300..5000)
        int checkedPoints = 0;
        for (float x = 3400f; x <= 5000f; x += 10f)
        {
            for (float z = 4995f; z <= 5000f; z += 1f)
            {
                float h = geometry.HeightMap.GetHeight(x, z);
                if (h < 2.0f) // Submerged terrain under water level (water level ~ 2.0m)
                {
                    Vector2 pt = new(x, z);
                    bool clear = evaluator.IsPointClear(pt, 1.65f, TraversabilityCapabilities.DefaultVehicle, 0u);
                    Assert.False(clear, $"Water edge point {pt} (height={h:F2}m) must be impassable.");
                    checkedPoints++;
                }
            }
        }

        for (float z = 3400f; z <= 5000f; z += 10f)
        {
            for (float x = 4995f; x <= 5000f; x += 1f)
            {
                float h = geometry.HeightMap.GetHeight(x, z);
                if (h < 2.0f)
                {
                    Vector2 pt = new(x, z);
                    bool clear = evaluator.IsPointClear(pt, 1.65f, TraversabilityCapabilities.DefaultVehicle, 0u);
                    Assert.False(clear, $"Water edge point {pt} (height={h:F2}m) must be impassable.");
                    checkedPoints++;
                }
            }
        }

        Assert.True(checkedPoints > 0, "Dense boundary test must sample water points.");
    }

    [Fact]
    public void Test10_UnderBridgeGroundDetour_HierarchicalGlobalPlanning()
    {
        // 300x300m map with 50m sectors (6x6 sectors)
        const int cols = 151;
        const int rows = 151;
        const float cellSize = 2.0f;
        HeightMap heightMap = new(cols, rows, cellSize, 0.01f, new ushort[cols * rows]);

        // Wall blocking direct X=150 path from Z=80 to Z=300 (touching north border)
        OpaqueStructure wall = new(
            new StructureId(101),
            new Vector3(146f, 0f, 80f),
            new Vector3(154f, 15f, 300f));

        // Elevated bridge crossing the ground detour corridor at Z=50 from X=130 to X=170 at 10m height
        BridgeStructure bridge = new(
            new StructureId(1),
            start: new Vector2(130f, 50f),
            end: new Vector2(170f, 50f),
            width: 10f,
            startHeight: 10f,
            endHeight: 10f,
            thickness: 0.8f);

        StaticGeometry geometry = new(
            heightMap,
            structures: [wall],
            bridges: [bridge],
            terrainObstacles: []);

        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        NavigationTuning navTuning = new()
        {
            SectorSize = 50f
        };

        OpsRules rules = new(geometry, catalog, navTuning);
        WorldState world = new(heightMap, 16);
        using Simulation sim = new(world, rules);

        NavigationSystem nav = (rules as RuleSet)!.FindSystem<NavigationSystem>()!;

        Vector2 start = new(50f, 150f);
        Vector2 goal = new(250f, 150f);

        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        EntityId tank = world.CreateUnit(1, t62Idx, start, t62Def.SpeedRoad);
        world.SetFireStance(tank, FireStance.HoldFire);

        Assert.True(sim.TryEnqueue(1, new MoveCommand(1u, sim.Tick, tank, goal)));

        bool arrived = false;
        int finalTick = 0;
        for (int tick = 0; tick < 1500; tick++)
        {
            sim.Step();
            finalTick = tick;
            world.TryGetUnit(tank, out UnitState u);
            PathRequestStatus status = nav.GetUnitStatus(tank);
            Assert.NotEqual(PathRequestStatus.NoPath, status);
            if (!u.Moving && Vector2.Distance(u.Position, goal) < 0.2f)
            {
                arrived = true;
                break;
            }
        }

        world.TryGetUnit(tank, out UnitState endUnit);
        Assert.True(arrived, $"Vehicle must detour under elevated bridge and arrive at goal. finalTick={finalTick} pos={endUnit.Position} moving={endUnit.Moving} dist={Vector2.Distance(endUnit.Position, goal):F2} status={nav.GetUnitStatus(tank)}");
        Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(tank));
    }
}
