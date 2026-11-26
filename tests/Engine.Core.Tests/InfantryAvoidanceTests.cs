using System.Numerics;
using DeadGround.Engine;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class InfantryAvoidanceTests
{
    private sealed class AvoidanceCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public void Register(ushort def, UnitMovementProfile profile) => _profiles[def] = profile;

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private const ushort DefTank = 1;
    private const ushort DefInfantry = 2;

    private static AvoidanceCatalog CreateCatalog()
    {
        AvoidanceCatalog catalog = new();
        catalog.Register(DefTank, new UnitMovementProfile(
            Speed: 10f,
            Radius: 1.5f,
            ReverseSpeed: 3f,
            TurnRate: 35f,
            CanPivot: true,
            MinTurnRadius: 0f,
            Acceleration: 2f,
            Deceleration: 4f,
            CollisionRadius: 3.2f,
            Height: 2.6f,
            Capabilities: TraversabilityCapabilities.DefaultVehicle));
        catalog.Register(DefInfantry, new UnitMovementProfile(
            Speed: 4.0f,
            Radius: 0.4f,
            ReverseSpeed: 1.5f,
            TurnRate: 360f,
            CanPivot: true,
            MinTurnRadius: 0f,
            Acceleration: 12f,
            Deceleration: 16f,
            CollisionRadius: 0.4f,
            Height: 1.8f,
            Capabilities: TraversabilityCapabilities.Foot));
        return catalog;
    }

    private static (NavigationSystem nav, WorldState world, Simulation sim) CreateHarness(
        OpaqueStructure[]? structures = null)
    {
        HeightMap map = new(65, 65, 2.0f, 0.01f, new ushort[65 * 65]);
        StaticGeometry geo = new(map, structures ?? []);
        NavigationGrid grid = new(geo, cellSize: 2.0f);
        NavigationSystem nav = new(grid, CreateCatalog());
        WorldState world = new(map);
        RuleSet rules = new(nav);
        Simulation sim = new(world, rules);
        return (nav, world, sim);
    }

    [Fact]
    public void Infantry_OrderedBehindParkedTank_BypassesTankSmoothlyAndReachesDestination()
    {
        (NavigationSystem nav, WorldState world, Simulation sim) = CreateHarness();
        using (sim)
        {
            // Parked tank directly between infantry start and destination
            EntityId tank = world.CreateUnit(1, DefTank, new Vector2(60f, 60f), 10f, initialTurretYaw: MathF.PI);
            EntityId soldier = world.CreateUnit(1, DefInfantry, new Vector2(60f, 44f), 4f, initialTurretYaw: MathF.PI);
            Vector2 goal = new(60f, 76f);

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1u, sim.Tick, soldier, goal)));

            float minDistance = float.MaxValue;
            long maxSteadyAlloc = 0;
            for (int t = 0; t < 180; t++)
            {
                long before = t >= 15 ? GC.GetAllocatedBytesForCurrentThread() : 0;
                sim.Step();
                if (t >= 15)
                {
                    long delta = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (delta > maxSteadyAlloc) maxSteadyAlloc = delta;
                }

                world.TryGetUnit(tank, out UnitState tankState);
                world.TryGetUnit(soldier, out UnitState solState);
                float dist = Vector2.Distance(tankState.Position, solState.Position);
                minDistance = MathF.Min(minDistance, dist);
                Assert.True(dist >= 3.6f - 0.05f, $"Infantry penetrated parked tank at tick {t}: dist={dist:F2}m");
            }

            world.TryGetUnit(soldier, out UnitState finalSol);
            float distToGoal = Vector2.Distance(finalSol.Position, goal);
            Assert.True(distToGoal <= 0.5f, $"Infantry must bypass parked tank and reach destination (60,76), ended at {finalSol.Position} (dist={distToGoal:F2}m, status={nav.GetUnitStatus(soldier)}).");
            Assert.True(nav.StationaryDetoursInserted >= 1, "Expected at least one local detour around the parked tank.");
            Assert.Equal(0L, maxSteadyAlloc);
        }
    }

    [Fact]
    public void InfantrySquad_OrderedBehindParkedTank_FlowsAroundBothSidesAndReachesTargets()
    {
        (NavigationSystem nav, WorldState world, Simulation sim) = CreateHarness();
        using (sim)
        {
            EntityId tank = world.CreateUnit(1, DefTank, new Vector2(60f, 60f), 10f, initialTurretYaw: MathF.PI);
            EntityId[] squad =
            [
                world.CreateUnit(1, DefInfantry, new Vector2(58.5f, 44f), 4f, initialTurretYaw: MathF.PI),
                world.CreateUnit(1, DefInfantry, new Vector2(59.6f, 44f), 4f, initialTurretYaw: MathF.PI),
                world.CreateUnit(1, DefInfantry, new Vector2(60.4f, 44f), 4f, initialTurretYaw: MathF.PI),
                world.CreateUnit(1, DefInfantry, new Vector2(61.5f, 44f), 4f, initialTurretYaw: MathF.PI),
            ];
            Vector2[] goals =
            [
                new(57f, 76f),
                new(59f, 76f),
                new(61f, 76f),
                new(63f, 76f),
            ];

            for (int i = 0; i < squad.Length; i++)
            {
                Assert.True(sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), sim.Tick, squad[i], goals[i])));
            }

            bool passedLeft = false;
            bool passedRight = false;
            for (int t = 0; t < 240; t++)
            {
                sim.Step();
                world.TryGetUnit(tank, out UnitState tankState);
                for (int i = 0; i < squad.Length; i++)
                {
                    world.TryGetUnit(squad[i], out UnitState s);
                    if (MathF.Abs(s.Position.Y - tankState.Position.Y) < 3.0f)
                    {
                        if (s.Position.X < tankState.Position.X - 3.0f) passedLeft = true;
                        if (s.Position.X > tankState.Position.X + 3.0f) passedRight = true;
                    }
                }
            }

            for (int i = 0; i < squad.Length; i++)
            {
                world.TryGetUnit(squad[i], out UnitState finalState);
                float dist = Vector2.Distance(finalState.Position, goals[i]);
                Assert.True(dist <= 0.8f, $"Squad member {i} stalled instead of bypassing tank: pos={finalState.Position}, goal={goals[i]}, dist={dist:F2}m.");
            }
            Assert.True(passedLeft && passedRight, "Infantry squad should naturally split around both sides of the parked tank.");
        }
    }

    [Fact]
    public void Infantry_OrderedBehindTwoSideBySideTanks_FindsClearFlankAndReachesDestination()
    {
        (NavigationSystem nav, WorldState world, Simulation sim) = CreateHarness();
        using (sim)
        {
            world.CreateUnit(1, DefTank, new Vector2(56.5f, 60f), 10f, initialTurretYaw: MathF.PI);
            world.CreateUnit(1, DefTank, new Vector2(63.5f, 60f), 10f, initialTurretYaw: MathF.PI);
            EntityId soldier = world.CreateUnit(1, DefInfantry, new Vector2(60f, 44f), 4f, initialTurretYaw: MathF.PI);
            Vector2 goal = new(60f, 78f);

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1u, sim.Tick, soldier, goal)));

            for (int t = 0; t < 260; t++) sim.Step();

            world.TryGetUnit(soldier, out UnitState finalSol);
            float dist = Vector2.Distance(finalSol.Position, goal);
            Assert.True(dist <= 0.8f, $"Infantry must bypass two side-by-side parked tanks, ended at {finalSol.Position} (dist={dist:F2}m).");
            Assert.True(nav.StationaryDetoursInserted >= 1);
        }
    }

    [Fact]
    public void Infantry_StationaryAvoidance_IsDeterministicAcrossRuns()
    {
        ulong RunOnce()
        {
            (NavigationSystem _, WorldState world, Simulation sim) = CreateHarness();
            using (sim)
            {
                world.CreateUnit(1, DefTank, new Vector2(60f, 60f), 10f, initialTurretYaw: MathF.PI);
                EntityId s1 = world.CreateUnit(1, DefInfantry, new Vector2(60f, 45f), 4f, initialTurretYaw: MathF.PI);
                EntityId s2 = world.CreateUnit(1, DefInfantry, new Vector2(60f, 43f), 4f, initialTurretYaw: MathF.PI);
                sim.TryEnqueue(1, new MoveCommand(1u, 0, s1, new Vector2(60f, 75f)));
                sim.TryEnqueue(1, new MoveCommand(2u, 0, s2, new Vector2(60f, 77f)));
                for (int t = 0; t < 200; t++) sim.Step();
                return world.ComputeStateHash();
            }
        }

        Assert.Equal(RunOnce(), RunOnce());
    }
}
