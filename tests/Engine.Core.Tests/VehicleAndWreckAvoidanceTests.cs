using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Tests;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class VehicleAndWreckAvoidanceTests
{
    private const ushort TankDef = 1;
    private const ushort WheeledApcDef = 2;
    private const ushort InfantryDef = 3;

    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public TestMovementCatalog RegisterDefaults()
        {
            _profiles[TankDef] = new UnitMovementProfile(
                Speed: 9.0f,
                Radius: 1.5f,
                Capabilities: TraversabilityCapabilities.DefaultVehicle,
                TurnRate: 45.0f,
                ReverseSpeed: 3.0f,
                Acceleration: 3.0f,
                Deceleration: 5.0f,
                CanPivot: true,
                MinTurnRadius: 0.0f,
                CollisionRadius: 3.5f,
                Height: 2.5f);

            _profiles[WheeledApcDef] = new UnitMovementProfile(
                Speed: 11.0f,
                Radius: 1.2f,
                Capabilities: TraversabilityCapabilities.DefaultVehicle,
                TurnRate: 45.0f,
                ReverseSpeed: 3.5f,
                Acceleration: 3.5f,
                Deceleration: 5.0f,
                CanPivot: false,
                MinTurnRadius: 6.0f,
                CollisionRadius: 2.5f,
                Height: 2.3f);

            _profiles[InfantryDef] = new UnitMovementProfile(
                Speed: 4.5f,
                Radius: 0.45f,
                Capabilities: TraversabilityCapabilities.DefaultVehicle,
                TurnRate: 360.0f,
                ReverseSpeed: 2.0f,
                Acceleration: 12.0f,
                Deceleration: 18.0f,
                CanPivot: true,
                MinTurnRadius: 0.0f,
                CollisionRadius: 0.45f,
                Height: 1.8f);

            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private static (NavigationSystem nav, WorldState world, Simulation sim) CreateEnvironment(
        StaticGeometry? geo = null,
        NavigationTuning? tuning = null)
    {
        const int size = 101;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
        geo ??= new StaticGeometry(map, ReadOnlySpan<OpaqueStructure>.Empty);
        NavigationGrid grid = new(geo, cellSize: 0.5f);
        TestMovementCatalog catalog = new TestMovementCatalog().RegisterDefaults();

        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        Simulation sim = new(world, rules);
        return (nav, world, sim);
    }

    [Fact]
    public void Tank_OrderedBehindParkedTank_BypassesSmoothlyAndReachesDestination()
    {
        static (Vector2 finalPos, float minDist, int detours, PathRequestStatus status, long steadyAlloc, string allocDiag) RunScenario(bool measureAlloc)
        {
            (NavigationSystem nav, WorldState world, Simulation sim) = CreateEnvironment();
            using (sim)
            {
                EntityId parkedTank = world.CreateUnit(1, TankDef, new Vector2(50f, 50f), 9f, initialTurretYaw: MathF.PI);
                EntityId movingTank = world.CreateUnit(1, TankDef, new Vector2(50f, 22f), 9f, initialTurretYaw: MathF.PI);

                Vector2 goal = new(50f, 80f);
                Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, movingTank, goal)));

                // Warm up initial path search before steady-state allocation measurement
                for (int t = 0; t < 15; t++)
                {
                    sim.Step();
                }

                long maxTickAlloc = 0L;
                string allocDiag = string.Empty;
                float minDist = float.MaxValue;
                using (NoGcAllocationWindow? noGc = measureAlloc ? new NoGcAllocationWindow() : null)
                {
                    for (int t = 0; t < 380; t++)
                    {
                        long before = measureAlloc ? GC.GetAllocatedBytesForCurrentThread() : 0L;
                        sim.Step();
                        if (measureAlloc)
                        {
                            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                            if (delta > maxTickAlloc)
                            {
                                maxTickAlloc = delta;
                            }
                        }
                        world.TryGetUnit(parkedTank, out UnitState pState);
                        world.TryGetUnit(movingTank, out UnitState mState);
                        minDist = MathF.Min(minDist, Vector2.Distance(pState.Position, mState.Position));
                    }

                    if (noGc is not null)
                    {
                        allocDiag = noGc.Describe(maxTickAlloc);
                    }
                }

                world.TryGetUnit(movingTank, out UnitState finalTank);
                return (finalTank.Position, minDist, nav.StationaryDetoursInserted, nav.GetUnitStatus(movingTank), maxTickAlloc, allocDiag);
            }
        }

        // Unmeasured warmup so cold JIT compilation does not skew allocation telemetry
        _ = RunScenario(measureAlloc: false);

        (Vector2 finalPos, float minDist, int detours, PathRequestStatus status, long steadyAlloc, string allocDiag) = RunScenario(measureAlloc: true);

        Assert.True(minDist >= 7.0f - 1e-2f, $"Tank penetrated parked tank hull: minDist={minDist:F3}m < 7.0m");
        Assert.True(detours >= 1, "Expected at least one local stationary detour to be inserted for moving tank.");
        Assert.True(Vector2.Distance(finalPos, new Vector2(50f, 80f)) <= 0.75f,
            $"Moving tank stalled instead of reaching goal: finalPos={finalPos}, status={status}");
        Assert.Equal(PathRequestStatus.PathFound, status);
        Assert.True(steadyAlloc == 0L, $"Expected zero steady-state allocations: {allocDiag}");
    }

    [Fact]
    public void WheeledApc_OrderedBehindParkedTank_BypassesRespectingTurnRadius()
    {
        const float minTurnRadius = 6.0f;
        (NavigationSystem nav, WorldState world, Simulation sim) = CreateEnvironment();
        using (sim)
        {
            EntityId parkedTank = world.CreateUnit(1, TankDef, new Vector2(50f, 50f), 9f, initialTurretYaw: MathF.PI);
            Vector2 startPos = new(50f, 20f);
            EntityId apc = world.CreateUnit(1, WheeledApcDef, startPos, 11f, initialTurretYaw: MathF.PI);

            Vector2 goal = new(50f, 82f);
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, apc, goal)));

            Vector2 prevPos = startPos;
            float prevHeading = MathF.PI;
            float minDist = float.MaxValue;

            for (int t = 0; t < 400; t++)
            {
                sim.Step();
                world.TryGetUnit(parkedTank, out UnitState pState);
                world.TryGetUnit(apc, out UnitState aState);
                world.TryGetCombatState(apc, out WorldState.CombatState cs);

                float hullDist = Vector2.Distance(pState.Position, aState.Position);
                minDist = MathF.Min(minDist, hullDist);
                Assert.True(hullDist >= 6.0f - 1e-2f, $"APC penetrated parked tank: dist={hullDist:F3}m at tick {t}");

                float moved = Vector2.Distance(prevPos, aState.Position);
                float turned = MathF.Abs(CombatSystem.ShorterArcDelta(prevHeading, cs.HullHeading));
                if (moved <= 1e-5f)
                {
                    Assert.True(turned <= 1e-4f, $"Wheeled APC turned while stationary at tick {t}: turn={turned:F4} rad");
                }
                else
                {
                    float allowedTurn = moved / minTurnRadius;
                    Assert.True(turned <= allowedTurn + 1e-3f,
                        $"Wheeled APC violated minTurnRadius at tick {t}: turned={turned:F4}, allowed={allowedTurn:F4}");
                }

                prevPos = aState.Position;
                prevHeading = cs.HullHeading;
            }

            world.TryGetUnit(apc, out UnitState finalApc);
            Assert.True(nav.StationaryDetoursInserted >= 1, "Expected wheeled APC to insert stationary detour.");
            Assert.True(Vector2.Distance(finalApc.Position, goal) <= 1.5f,
                $"Wheeled APC failed to bypass parked tank and reach goal: finalPos={finalApc.Position}, status={nav.GetUnitStatus(apc)}");
        }
    }

    [Fact]
    public void TankAndInfantry_BypassDestroyedWreck_WithoutPenetration()
    {
        (NavigationSystem nav, WorldState world, Simulation sim) = CreateEnvironment();
        using (sim)
        {
            Vector2 wreckPos = new(50f, 50f);
            world.AddWreck(
                definition: TankDef,
                position: wreckPos,
                supportSurface: 0,
                hullHeading: MathF.PI * 0.5f,
                turretYaw: 0f,
                destroyedTick: 0,
                originalOwner: 2);

            EntityId tank = world.CreateUnit(1, TankDef, new Vector2(50f, 22f), 9f, initialTurretYaw: MathF.PI);
            EntityId soldier = world.CreateUnit(1, InfantryDef, new Vector2(50f, 34f), 4.5f, initialTurretYaw: MathF.PI);

            Vector2 tankGoal = new(50f, 80f);
            Vector2 soldierGoal = new(50f, 68f);

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, tank, tankGoal)));
            Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, soldier, soldierGoal)));

            float minTankWreckDist = float.MaxValue;
            float minSoldierWreckDist = float.MaxValue;

            for (int t = 0; t < 420; t++)
            {
                sim.Step();
                world.TryGetUnit(tank, out UnitState tState);
                world.TryGetUnit(soldier, out UnitState sState);

                minTankWreckDist = MathF.Min(minTankWreckDist, Vector2.Distance(tState.Position, wreckPos));
                minSoldierWreckDist = MathF.Min(minSoldierWreckDist, Vector2.Distance(sState.Position, wreckPos));
            }

            world.TryGetUnit(tank, out UnitState finalTank);
            world.TryGetUnit(soldier, out UnitState finalSoldier);

            Assert.True(minTankWreckDist >= 7.0f - 1e-2f, $"Tank penetrated wreck: minDist={minTankWreckDist:F3}m < 7.0m");
            Assert.True(minSoldierWreckDist >= 3.95f - 1e-2f, $"Infantry penetrated wreck: minDist={minSoldierWreckDist:F3}m < 3.95m");
            Assert.True(Vector2.Distance(finalTank.Position, tankGoal) <= 0.75f,
                $"Tank stalled behind wreck: finalPos={finalTank.Position}, status={nav.GetUnitStatus(tank)}");
            Assert.True(Vector2.Distance(finalSoldier.Position, soldierGoal) <= 0.6f,
                $"Infantry stalled behind wreck: finalPos={finalSoldier.Position}, status={nav.GetUnitStatus(soldier)}");
        }
    }

    [Fact]
    public void Tank_InNarrowAlleyBlockedByParkedTank_WaitsForClearanceSafely()
    {
        const int size = 101;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
        OpaqueStructure westBlock = new(new StructureId(1), new Vector3(15f, 0f, 35f), new Vector3(45f, 12f, 65f));
        OpaqueStructure eastBlock = new(new StructureId(2), new Vector3(55f, 0f, 35f), new Vector3(85f, 12f, 65f));
        StaticGeometry geo = new(map, [westBlock, eastBlock]);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateEnvironment(geo);
        using (sim)
        {
            EntityId parkedTank = world.CreateUnit(1, TankDef, new Vector2(50f, 50f), 9f, initialTurretYaw: MathF.PI);
            EntityId followerTank = world.CreateUnit(1, TankDef, new Vector2(50f, 38f), 9f, initialTurretYaw: MathF.PI);

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, followerTank, new Vector2(50f, 75f))));

            for (int t = 0; t < 200; t++)
            {
                sim.Step();
                world.TryGetUnit(parkedTank, out UnitState pState);
                world.TryGetUnit(followerTank, out UnitState fState);

                float dist = Vector2.Distance(pState.Position, fState.Position);
                Assert.True(dist >= 7.0f - 1e-2f, $"Alley tank penetrated blocker: dist={dist:F3}m at tick {t}");
                Assert.InRange(fState.Position.X, 46.0f, 54.0f);
            }

            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(followerTank));
            Assert.True(nav.HasActiveRoute(followerTank));
        }
    }

    [Fact]
    public void VehicleAndWreckAvoidance_IsDeterministicAcrossRuns()
    {
        static (Vector2 tankPos, float tankHeading, Vector2 apcPos, float apcHeading, int detours) RunOnce()
        {
            (NavigationSystem nav, WorldState world, Simulation sim) = CreateEnvironment();
            using (sim)
            {
                world.AddWreck(TankDef, new Vector2(50f, 50f), 0, 0.5f, 0f, 0, 2);
                EntityId parked = world.CreateUnit(1, TankDef, new Vector2(64f, 52f), 9f, initialTurretYaw: MathF.PI);
                _ = parked;

                EntityId tank = world.CreateUnit(1, TankDef, new Vector2(50f, 22f), 9f, initialTurretYaw: MathF.PI);
                EntityId apc = world.CreateUnit(1, WheeledApcDef, new Vector2(64f, 22f), 11f, initialTurretYaw: MathF.PI);

                Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, tank, new Vector2(50f, 80f))));
                Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, apc, new Vector2(64f, 82f))));

                for (int t = 0; t < 380; t++)
                {
                    sim.Step();
                }

                world.TryGetUnit(tank, out UnitState tState);
                world.TryGetCombatState(tank, out WorldState.CombatState tCs);
                world.TryGetUnit(apc, out UnitState aState);
                world.TryGetCombatState(apc, out WorldState.CombatState aCs);
                return (tState.Position, tCs.HullHeading, aState.Position, aCs.HullHeading, nav.StationaryDetoursInserted);
            }
        }

        var runA = RunOnce();
        var runB = RunOnce();

        Assert.Equal(runA.tankPos, runB.tankPos);
        Assert.Equal(runA.tankHeading, runB.tankHeading);
        Assert.Equal(runA.apcPos, runB.apcPos);
        Assert.Equal(runA.apcHeading, runB.apcHeading);
        Assert.Equal(runA.detours, runB.detours);
        Assert.True(runA.detours >= 2);
    }
}
