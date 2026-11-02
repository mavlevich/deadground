using System.Numerics;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class GroupDestinationDistributionTests
{
    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public TestMovementCatalog Register(
            ushort definition,
            float speed = 10.0f,
            float radius = 1.0f,
            TraversabilityCapabilities capabilities = TraversabilityCapabilities.DefaultVehicle,
            float turnRate = 45.0f,
            float reverseSpeed = 2.5f,
            float acceleration = 3.0f,
            float deceleration = 5.0f,
            bool canPivot = true,
            float minTurnRadius = 0.0f,
            float collisionRadius = 0.0f,
            float height = 2.5f)
        {
            _profiles[definition] = new UnitMovementProfile(
                speed,
                radius,
                capabilities,
                turnRate,
                reverseSpeed,
                acceleration,
                deceleration,
                canPivot,
                minTurnRadius,
                collisionRadius,
                height);
            return this;
        }

        public bool TryGetProfile(ushort definition, out UnitMovementProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private static (NavigationSystem nav, WorldState world, Simulation sim, DestinationEvaluator evaluator) CreateTestEnvironment(
        IUnitMovementCatalog catalog,
        StaticGeometry? geo = null,
        NavigationTuning? tuning = null)
    {
        const int size = 101;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
        geo ??= new StaticGeometry(map, ReadOnlySpan<OpaqueStructure>.Empty);
        NavigationGrid grid = new(geo, cellSize: 0.5f);

        NavigationSystem nav = new(grid, catalog, tuning);
        WorldState world = new(map);
        RuleSet rules = new(nav);
        Simulation sim = new(world, rules);
        return (nav, world, sim, grid.Evaluator);
    }

    [Fact]
    public void Test01_SingleUnit_DirectMoveBehavior()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.5f, collisionRadius: 2.0f);
        (_, _, _, DestinationEvaluator evaluator) = CreateTestEnvironment(catalog);

        GroupUnitInput[] units =
        [
            new(new EntityId(1), new Vector2(10f, 10f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true, 2.5f)
        ];

        GroupMoveResult result = GroupDestinationDistribution.Distribute(
            sequence: 1,
            serverTick: 1,
            units: units,
            target: new Vector2(50f, 50f),
            targetSurface: 0,
            finalFacing: null,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        Assert.Equal(1, result.Placed.Length);
        Assert.Equal(0, result.Unplaced.Length);
        Assert.Equal(new EntityId(1), result.Placed.Span[0].Unit);
        Assert.Equal(new Vector2(50f, 50f), result.Placed.Span[0].Target);
        Assert.Null(result.Placed.Span[0].FinalFacing);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(20)]
    public void Test02_MultipleIdenticalUnits_ReceiveDistinctNonOverlappingDestinations(int count)
    {
        TestMovementCatalog catalog = new();
        const float colRadius = 2.0f;
        catalog.Register(1, speed: 10f, radius: 1.5f, collisionRadius: colRadius);
        (_, _, _, DestinationEvaluator evaluator) = CreateTestEnvironment(catalog);

        var units = new GroupUnitInput[count];
        for (int i = 0; i < count; i++)
        {
            units[i] = new GroupUnitInput(
                new EntityId((uint)(i + 1)),
                new Vector2(20f + i * 4.0f, 20f),
                0f,
                1.5f,
                colRadius,
                TraversabilityCapabilities.DefaultVehicle,
                true,
                2.5f);
        }

        Vector2 target = new(100f, 100f);
        GroupMoveResult result = GroupDestinationDistribution.Distribute(
            sequence: 1,
            serverTick: 1,
            units: units,
            target: target,
            targetSurface: 0,
            finalFacing: null,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        Assert.Equal(count, result.Placed.Length);
        Assert.Equal(0, result.Unplaced.Length);

        ReadOnlySpan<GroupMoveAssignment> placed = result.Placed.Span;
        float minRequiredDistance = colRadius + colRadius + GroupDestinationDistribution.DestinationGap;

        // Verify distinctness and physical clearance condition
        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                float dist = Vector2.Distance(placed[i].Target, placed[j].Target);
                Assert.True(
                    dist >= minRequiredDistance - 0.05f,
                    $"Units {placed[i].Unit.Value} and {placed[j].Unit.Value} are too close: {dist:F2}m < {minRequiredDistance:F2}m");
            }
        }
    }

    [Fact]
    public void Test03_MixedGroup_RespectsDifferentDimensionsAndClearances()
    {
        TestMovementCatalog catalog = new();
        // Heavy tank: collision radius 3.5m
        catalog.Register(1, speed: 8f, radius: 2.0f, collisionRadius: 3.5f);
        // Light scout car: collision radius 1.5m
        catalog.Register(2, speed: 12f, radius: 1.0f, collisionRadius: 1.5f);
        (_, _, _, DestinationEvaluator evaluator) = CreateTestEnvironment(catalog);

        GroupUnitInput[] units =
        [
            new(new EntityId(1), new Vector2(30f, 30f), 0f, 2.0f, 3.5f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(2), new Vector2(35f, 30f), 0f, 2.0f, 3.5f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(3), new Vector2(40f, 30f), 0f, 1.0f, 1.5f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(4), new Vector2(45f, 30f), 0f, 1.0f, 1.5f, TraversabilityCapabilities.DefaultVehicle, true),
        ];

        GroupMoveResult result = GroupDestinationDistribution.Distribute(
            sequence: 1,
            serverTick: 1,
            units: units,
            target: new Vector2(80f, 80f),
            targetSurface: 0,
            finalFacing: null,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        Assert.Equal(4, result.Placed.Length);
        ReadOnlySpan<GroupMoveAssignment> placed = result.Placed.Span;

        for (int i = 0; i < placed.Length; i++)
        {
            float rA = units[i].CollisionRadius;
            for (int j = i + 1; j < placed.Length; j++)
            {
                float rB = units[j].CollisionRadius;
                float expectedMin = rA + rB + GroupDestinationDistribution.DestinationGap;
                float dist = Vector2.Distance(placed[i].Target, placed[j].Target);
                Assert.True(
                    dist >= expectedMin - 0.05f,
                    $"Pair ({placed[i].Unit.Value}, {placed[j].Unit.Value}) clearance violation: {dist:F2}m < {expectedMin:F2}m");
            }
        }
    }

    [Fact]
    public void Test04_ShuffledInputList_YieldsIdenticalDeterministicAssignments()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.5f, collisionRadius: 2.0f);
        (_, _, _, DestinationEvaluator evaluator) = CreateTestEnvironment(catalog);

        GroupUnitInput[] original =
        [
            new(new EntityId(10), new Vector2(20f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(20), new Vector2(25f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(30), new Vector2(30f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(40), new Vector2(35f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(50), new Vector2(40f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
        ];

        GroupUnitInput[] shuffled =
        [
            original[3],
            original[0],
            original[4],
            original[1],
            original[2],
        ];

        Vector2 target = new(80f, 80f);

        GroupMoveResult res1 = GroupDestinationDistribution.Distribute(
            sequence: 1,
            serverTick: 1,
            units: original,
            target: target,
            targetSurface: 0,
            finalFacing: 1.25f,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        GroupMoveResult res2 = GroupDestinationDistribution.Distribute(
            sequence: 1,
            serverTick: 1,
            units: shuffled,
            target: target,
            targetSurface: 0,
            finalFacing: 1.25f,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        Assert.Equal(res1.Placed.Length, res2.Placed.Length);

        // Every unit must get the exact same target position regardless of input order
        for (int i = 0; i < res1.Placed.Length; i++)
        {
            GroupMoveAssignment a1 = res1.Placed.Span[i];
            bool foundMatch = false;
            for (int j = 0; j < res2.Placed.Length; j++)
            {
                GroupMoveAssignment a2 = res2.Placed.Span[j];
                if (a1.Unit == a2.Unit)
                {
                    Assert.Equal(a1.Target.X, a2.Target.X, precision: 3);
                    Assert.Equal(a1.Target.Y, a2.Target.Y, precision: 3);
                    Assert.Equal(a1.FinalFacing, a2.FinalFacing);
                    foundMatch = true;
                    break;
                }
            }
            Assert.True(foundMatch, $"Missing assignment for unit {a1.Unit.Value}");
        }
    }

    [Fact]
    public void Test05_FormationOrientation_WorldSpaceCoordinateIndependence()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.5f, collisionRadius: 2.0f);
        (_, _, _, DestinationEvaluator evaluator) = CreateTestEnvironment(catalog);

        GroupUnitInput[] units =
        [
            new(new EntityId(1), new Vector2(20f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(2), new Vector2(24f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
        ];

        // Specify explicit facing: East (pi / 2 radians in navigation coordinates)
        float facingEast = MathF.PI / 2f;
        GroupMoveResult resultEast = GroupDestinationDistribution.Distribute(
            sequence: 1,
            serverTick: 1,
            units: units,
            target: new Vector2(80f, 80f),
            targetSurface: 0,
            finalFacing: facingEast,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        Assert.Equal(2, resultEast.Placed.Length);
        Assert.Equal(facingEast, resultEast.Placed.Span[0].FinalFacing);
        Assert.Equal(facingEast, resultEast.Placed.Span[1].FinalFacing);

        // When facing East, front is (-X) and right is North (-Y).
        // Lateral separation should align along the formation right axis.
        Vector2 delta = resultEast.Placed.Span[1].Target - resultEast.Placed.Span[0].Target;
        Assert.True(delta.Length() >= 5.0f - 0.05f);
    }

    [Fact]
    public void Test06_ReverseAndFinalFacing_PreservedPerParticipant()
    {
        TestMovementCatalog catalog = new();
        // Unit 1: reverse capable (speed 3.0)
        catalog.Register(1, speed: 10f, radius: 1.5f, reverseSpeed: 3.0f, collisionRadius: 2.0f);
        // Unit 2: no reverse gear (reverseSpeed 0)
        catalog.Register(2, speed: 10f, radius: 1.5f, reverseSpeed: 0.0f, collisionRadius: 2.0f);
        (_, _, _, DestinationEvaluator evaluator) = CreateTestEnvironment(catalog);

        GroupUnitInput[] units =
        [
            new(new EntityId(1), new Vector2(20f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true, 3.0f),
            new(new EntityId(2), new Vector2(25f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, false, 0.0f),
        ];

        float finalFacing = 0.75f;
        GroupMoveResult result = GroupDestinationDistribution.Distribute(
            sequence: 1,
            serverTick: 1,
            units: units,
            target: new Vector2(60f, 60f),
            targetSurface: 0,
            finalFacing: finalFacing,
            mode: MoveMode.Reverse,
            evaluator: evaluator);

        Assert.Equal(1, result.Placed.Length);
        Assert.Equal(1, result.Unplaced.Length);

        // Placed unit 1 receives destination and requested final facing
        Assert.Equal(new EntityId(1), result.Placed.Span[0].Unit);
        Assert.Equal(finalFacing, result.Placed.Span[0].FinalFacing);

        // Unit 2 without reverse gear receives NoReverseGear reason
        Assert.Equal(new EntityId(2), result.Unplaced.Span[0].Unit);
        Assert.Equal(DestinationBlockReason.NoReverseGear, result.Unplaced.Span[0].Reason);
    }

    [Fact]
    public void Test07_Obstacles_BuildingWaterForestAndBounds_ConstrainValidSlots()
    {
        HeightMap map = new(101, 101, 2.0f, 0.01f, new ushort[101 * 101]);
        // Solid building at (40, 40) to (60, 60)
        OpaqueStructure[] structures = [new(new StructureId(1), 40f, 0f, 40f, 60f, 10f, 60f, isGrounded: true)];
        StaticGeometry geo = new(map, structures);
        DestinationEvaluator evaluator = new(geo, cellSize: 0.5f);

        GroupUnitInput[] units =
        [
            new(new EntityId(1), new Vector2(10f, 10f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(2), new Vector2(15f, 10f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(3), new Vector2(20f, 10f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
        ];

        // Click deep inside building: must reject without drifting miles away
        GroupMoveResult blockedResult = GroupDestinationDistribution.Distribute(
            sequence: 1,
            serverTick: 1,
            units: units,
            target: new Vector2(50f, 50f),
            targetSurface: 0,
            finalFacing: null,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        Assert.Equal(0, blockedResult.Placed.Length);
        Assert.Equal(3, blockedResult.Unplaced.Length);
        Assert.Equal(DestinationBlockReason.Building, blockedResult.Unplaced.Span[0].Reason);

        // Click outside map boundary: must reject with OutsideMapBounds
        GroupMoveResult outOfBoundsResult = GroupDestinationDistribution.Distribute(
            sequence: 2,
            serverTick: 1,
            units: units,
            target: new Vector2(-10f, 50f),
            targetSurface: 0,
            finalFacing: null,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        Assert.Equal(0, outOfBoundsResult.Placed.Length);
        Assert.Equal(3, outOfBoundsResult.Unplaced.Length);
        Assert.Equal(DestinationBlockReason.OutsideMapBounds, outOfBoundsResult.Unplaced.Span[0].Reason);
    }

    [Fact]
    public void Test08_Snapping_AntiCollapseProtection()
    {
        HeightMap map = new(101, 101, 2.0f, 0.01f, new ushort[101 * 101]);
        // Building near target
        OpaqueStructure[] structures = [new(new StructureId(1), 52f, 0f, 48f, 58f, 10f, 58f, isGrounded: true)];
        StaticGeometry geo = new(map, structures);
        DestinationEvaluator evaluator = new(geo, cellSize: 0.5f);

        GroupUnitInput[] units =
        [
            new(new EntityId(1), new Vector2(20f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(2), new Vector2(25f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
            new(new EntityId(3), new Vector2(30f, 20f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
        ];

        GroupMoveResult result = GroupDestinationDistribution.Distribute(
            sequence: 1,
            serverTick: 1,
            units: units,
            target: new Vector2(50f, 50f),
            targetSurface: 0,
            finalFacing: null,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        ReadOnlySpan<GroupMoveAssignment> placed = result.Placed.Span;
        for (int i = 0; i < placed.Length; i++)
        {
            for (int j = i + 1; j < placed.Length; j++)
            {
                float dist = Vector2.Distance(placed[i].Target, placed[j].Target);
                // Ensure no two slots collapsed into overlapping positions
                Assert.True(dist >= 4.95f, $"Slots collapsed: dist {dist:F2}m < 4.95m");
            }
        }
    }

    [Fact]
    public void Test09_PartialPlacement_PreservesOldOrders()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.5f, collisionRadius: 2.0f);
        (NavigationSystem nav, WorldState world, Simulation sim, DestinationEvaluator evaluator) = CreateTestEnvironment(catalog);

        using (sim)
        {
            EntityId u1 = world.CreateUnit(1, 1, new Vector2(10f, 10f), 10f);
            EntityId u2 = world.CreateUnit(1, 1, new Vector2(15f, 10f), 10f);
            EntityId u3 = world.CreateUnit(1, 1, new Vector2(20f, 10f), 10f);

            // Give u3 an existing move order
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, u3, new Vector2(10f, 80f))));
            sim.Step();
            Assert.True(nav.HasActiveRoute(u3) || nav.GetUnitStatus(u3) is PathRequestStatus.Searching or PathRequestStatus.PathFound or PathRequestStatus.Pending);

            // Now create a group move with simulated tight constraints
            GroupUnitInput[] inputs =
            [
                new(u1, new Vector2(10f, 10f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
                new(u2, new Vector2(15f, 10f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true),
            ];

            GroupMoveResult result = GroupDestinationDistribution.Distribute(
                sequence: 2,
                serverTick: sim.Tick,
                units: inputs,
                target: new Vector2(80f, 80f),
                targetSurface: 0,
                finalFacing: null,
                mode: MoveMode.Forward,
                evaluator: evaluator);

            Assert.Equal(2, result.Placed.Length);
            // Unit 3 was not part of this group order; its route is preserved
            Assert.Equal(1u, nav.GetUnitOrderSequence(u3));
        }
    }

    [Fact]
    public void Test10_VerticalSurfaces_BridgeDeckVersusGroundUnderBridge()
    {
        HeightMap map = new(101, 101, 2.0f, 0.01f, new ushort[101 * 101]);
        BridgeStructure bridge = new(
            new StructureId(10),
            start: new Vector2(40f, 40f),
            end: new Vector2(40f, 80f),
            width: 10f,
            startHeight: 12f,
            endHeight: 12f);

        StaticGeometry geo = new(
            map,
            ReadOnlySpan<OpaqueStructure>.Empty,
            bridges: [bridge],
            terrainObstacles: []);

        DestinationEvaluator evaluator = new(geo, cellSize: 0.5f);

        // Group with bridge capability targeting the bridge deck (surface 10)
        GroupUnitInput[] bridgeUnits =
        [
            new(new EntityId(1), new Vector2(40f, 45f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.Bridge, true),
            new(new EntityId(2), new Vector2(40f, 55f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.Bridge, true),
        ];

        GroupMoveResult bridgeResult = GroupDestinationDistribution.Distribute(
            sequence: 1,
            serverTick: 1,
            units: bridgeUnits,
            target: new Vector2(40f, 60f),
            targetSurface: 10,
            finalFacing: null,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        Assert.Equal(2, bridgeResult.Placed.Length);
        Assert.Equal(10u, bridgeResult.Placed.Span[0].TargetSurface);
        Assert.Equal(10u, bridgeResult.Placed.Span[1].TargetSurface);

        // Units without bridge capability targeting the bridge deck must be rejected
        GroupUnitInput[] nonBridgeUnits =
        [
            new(new EntityId(3), new Vector2(40f, 45f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.Ground, true),
        ];

        GroupMoveResult rejectedBridge = GroupDestinationDistribution.Distribute(
            sequence: 2,
            serverTick: 1,
            units: nonBridgeUnits,
            target: new Vector2(40f, 60f),
            targetSurface: 10,
            finalFacing: null,
            mode: MoveMode.Forward,
            evaluator: evaluator);

        Assert.Equal(0, rejectedBridge.Placed.Length);
        Assert.Equal(1, rejectedBridge.Unplaced.Length);
        Assert.Equal(DestinationBlockReason.InsufficientClearance, rejectedBridge.Unplaced.Span[0].Reason);
    }

    [Fact]
    public void Test11_PacketDuplication_DoesNotTriggerReDistribution()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.5f, collisionRadius: 2.0f);
        (_, WorldState world, Simulation sim, _) = CreateTestEnvironment(catalog);

        using (sim)
        {
            EntityId u1 = world.CreateUnit(1, 1, new Vector2(20f, 20f), 10f);
            EntityId u2 = world.CreateUnit(1, 1, new Vector2(25f, 20f), 10f);

            GroupMoveCommand cmd = new(
                Sequence: 5,
                ClientTick: 1,
                Units: new EntityId[] { u1, u2 },
                Target: new Vector2(80f, 80f));

            // First enqueue succeeds
            Assert.True(sim.TryEnqueue(1, cmd));

            // Immediate duplicate with same sequence is rejected
            Assert.False(sim.TryEnqueue(1, cmd));

            // Lower sequence is rejected
            Assert.False(sim.TryEnqueue(1, cmd with { Sequence = 4 }));
        }
    }

    [Fact]
    public void Test12_SupersedingOrder_CancelsStaleDeferredAssignments()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.5f, collisionRadius: 2.0f);
        (NavigationSystem nav, WorldState world, Simulation sim, _) = CreateTestEnvironment(catalog);

        using (sim)
        {
            EntityId u1 = world.CreateUnit(1, 1, new Vector2(20f, 20f), 10f);
            EntityId u2 = world.CreateUnit(1, 1, new Vector2(25f, 20f), 10f);

            GroupMoveCommand cmd1 = new(10, 1, new EntityId[] { u1, u2 }, new Vector2(50f, 50f));
            Assert.True(sim.TryEnqueue(1, cmd1));
            sim.Step();

            Assert.Equal(10u, nav.GetUnitOrderSequence(u1));
            Assert.Equal(10u, nav.GetUnitOrderSequence(u2));

            // New superseding command
            GroupMoveCommand cmd2 = new(20, 2, new EntityId[] { u1, u2 }, new Vector2(90f, 90f));
            Assert.True(sim.TryEnqueue(1, cmd2));
            sim.Step();

            Assert.Equal(20u, nav.GetUnitOrderSequence(u1));
            Assert.Equal(20u, nav.GetUnitOrderSequence(u2));
        }
    }

    [Fact]
    public void Test13_ParticipantDeath_HandledGracefully()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.5f, collisionRadius: 2.0f);
        (NavigationSystem nav, WorldState world, Simulation sim, _) = CreateTestEnvironment(catalog);

        using (sim)
        {
            EntityId u1 = world.CreateUnit(1, 1, new Vector2(20f, 20f), 10f);
            EntityId u2 = world.CreateUnit(1, 1, new Vector2(25f, 20f), 10f);
            EntityId u3 = world.CreateUnit(1, 1, new Vector2(30f, 20f), 10f);

            GroupMoveCommand cmd = new(1, 1, new EntityId[] { u1, u2, u3 }, new Vector2(80f, 80f));
            Assert.True(sim.TryEnqueue(1, cmd));

            // Kill u2 before Step
            world.RemoveUnit(u2);

            // Simulation steps without crashing
            sim.Step();

            // Alive units receive assignments
            Assert.Equal(1u, nav.GetUnitOrderSequence(u1));
            Assert.Equal(1u, nav.GetUnitOrderSequence(u3));
        }
    }

    [Fact]
    public void Test14_LargeGroup_Over256AndMaxUnits_NotTruncated()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.5f, collisionRadius: 2.0f);
        (_, WorldState world, Simulation sim, DestinationEvaluator evaluator) = CreateTestEnvironment(catalog);

        using (sim)
        {
            const int count = 300;
            var units = new EntityId[count];
            var inputs = new GroupUnitInput[count];

            for (int i = 0; i < count; i++)
            {
                EntityId id = world.CreateUnit(1, 1, new Vector2(10f + (i % 20) * 4f, 10f + (i / 20) * 4f), 10f);
                units[i] = id;
                inputs[i] = new GroupUnitInput(id, new Vector2(50f, 50f), 0f, 1.5f, 2.0f, TraversabilityCapabilities.DefaultVehicle, true);
            }

            GroupMoveCommand cmd = new(1, 1, units, new Vector2(100f, 100f));
            Assert.True(sim.TryEnqueue(1, cmd));

            sim.Step();

            Assert.True(sim.TryPopGroupMoveResult(1, out GroupMoveResult execResult));
            Assert.Equal(count, execResult.Placed.Length);
            Assert.Equal(0, execResult.Unplaced.Length);

            // Preview does not truncate
            GroupPreviewResult preview = GroupDestinationDistribution.ComputePreview(
                inputs,
                new Vector2(100f, 100f),
                0,
                null,
                MoveMode.Forward,
                evaluator);

            Assert.Equal(count, preview.Slots.Length);
            Assert.Equal(count, preview.PlacedCount);
            Assert.Equal(0, preview.BlockedCount);
        }
    }

    [Fact]
    public void Test15_Security_ForgedMalformedOversized_RejectedSafely()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.5f, collisionRadius: 2.0f);
        (_, WorldState world, Simulation sim, _) = CreateTestEnvironment(catalog);

        using (sim)
        {
            EntityId ownUnit = world.CreateUnit(1, 1, new Vector2(20f, 20f), 10f);
            EntityId enemyUnit = world.CreateUnit(2, 2, new Vector2(50f, 50f), 10f); // Enemy owned by player 2

            // Forged command: player 1 includes player 2's unit
            GroupMoveCommand forged = new(1, 1, new EntityId[] { ownUnit, enemyUnit }, new Vector2(80f, 80f));
            Assert.False(sim.TryEnqueue(1, forged));

            // Command with duplicate UnitIds
            GroupMoveCommand duplicate = new(2, 1, new EntityId[] { ownUnit, ownUnit }, new Vector2(80f, 80f));
            Assert.False(sim.TryEnqueue(1, duplicate));

            // Command with zero EntityId
            GroupMoveCommand zeroId = new(3, 1, new EntityId[] { ownUnit, new EntityId(0) }, new Vector2(80f, 80f));
            Assert.False(sim.TryEnqueue(1, zeroId));

            // Command with empty units
            GroupMoveCommand empty = new(4, 1, ReadOnlyMemory<EntityId>.Empty, new Vector2(80f, 80f));
            Assert.False(sim.TryEnqueue(1, empty));
        }
    }

    [Fact]
    public void Test_CheckMixedSelection()
    {
        TerrainObstacle water = new(40f, 40f, 60f, 60f, TraversabilityCapabilities.Water);
        OpaqueStructure building = new(new StructureId(1), 70f, 0f, 70f, 85f, 10f, 85f, isGrounded: true);
        HeightMap map = HeightMap.CreatePrototype();
        StaticGeometry geo = new(map, [building], ReadOnlySpan<BridgeStructure>.Empty, [water]);
        DestinationEvaluator evaluator = new(geo);

        GroupUnitInput[] inputs =
        [
            new(new EntityId(101), new Vector2(20f, 20f), 0f, 1.4f, 1.4f, TraversabilityCapabilities.DefaultVehicle, true, 2.5f),
            new(new EntityId(102), new Vector2(22f, 20f), 0f, 1.4f, 1.4f, TraversabilityCapabilities.DefaultVehicle | TraversabilityCapabilities.Water, true, 2.5f)
        ];

        GroupPreviewResult preview = GroupDestinationDistribution.ComputePreview(
            inputs,
            new Vector2(50f, 50f),
            0,
            null,
            MoveMode.Forward,
            evaluator);

        Assert.Equal(2, preview.Slots.Length);
        Assert.Equal(1, preview.PlacedCount);
        Assert.Equal(1, preview.BlockedCount);
    }

    [Fact]
    public void Test19_ZeroSteadyStateAllocations()
    {
        var catalog = new TestMovementCatalog().Register(1, radius: 1.5f, collisionRadius: 2.0f);
        var (nav, world, sim, evaluator) = CreateTestEnvironment(catalog);
        var units = new GroupUnitInput[20];
        for (int i = 0; i < 20; i++)
        {
            units[i] = new GroupUnitInput(
                new EntityId((uint)(i + 1)),
                new Vector2(20f + i * 2f, 20f),
                0f, 1.5f, 2.0f,
                TraversabilityCapabilities.DefaultVehicle,
                true);
        }

        // Warmup JIT and thread-static buffers
        for (int w = 0; w < 100; w++)
        {
            GroupDestinationDistribution.Distribute(1, 1, units.AsSpan(), new Vector2(50f, 50f), 0, null, MoveMode.Forward, evaluator);
            GroupDestinationDistribution.ComputePreview(units.AsSpan(), new Vector2(50f, 50f), 0, null, MoveMode.Forward, evaluator);
        }

        long beforeDist = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            GroupDestinationDistribution.Distribute((uint)(i + 1), i, units.AsSpan(), new Vector2(50f, 50f), 0, null, MoveMode.Forward, evaluator);
        }
        long distAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeDist;

        long beforePrev = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            GroupDestinationDistribution.ComputePreview(units.AsSpan(), new Vector2(50f, 50f), 0, null, MoveMode.Forward, evaluator);
        }
        long prevAlloc = GC.GetAllocatedBytesForCurrentThread() - beforePrev;

        // Preview runs per-frame and must allocate 0 B
        Assert.Equal(0, prevAlloc);
        // Distribute allocates only the owned result array (~504 B per order) to prevent result corruption
        Assert.True(distAlloc <= 100 * 650);

        // Verify NavigationSystem.ExecuteGroupMove eliminates intermediate List and ToArray allocations
        var unitIds = new EntityId[20];
        for (int i = 0; i < 20; i++)
        {
            unitIds[i] = world.CreateUnit(1, 1, new Vector2(20f + i * 4f, 20f), 10f);
        }
        GroupMoveCommand cmd = new(100, 1, unitIds, new Vector2(50f, 50f));
        for (int w = 0; w < 100; w++)
        {
            nav.ExecuteGroupMove(1, cmd, world);
        }
        long beforeExec = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            nav.ExecuteGroupMove(1, cmd, world);
        }
        long execAlloc = GC.GetAllocatedBytesForCurrentThread() - beforeExec;
        Assert.True(execAlloc <= 100 * 650);
    }

    [Fact]
    public void Test20_PermutationInvariance_NarrowBridgeColumn_AllSixPermutationsAssignIdenticalDestinations()
    {
        HeightMap map = new(101, 101, 2.0f, 0.01f, new ushort[101 * 101]);
        BridgeStructure bridge = new(
            new StructureId(1),
            start: new Vector2(75f, 100f),
            end: new Vector2(125f, 100f),
            width: 12f,
            startHeight: 4f,
            endHeight: 4f);
        StaticGeometry geo = new(map, ReadOnlySpan<OpaqueStructure>.Empty, [bridge]);
        DestinationEvaluator evaluator = new(geo, cellSize: 0.5f);

        GroupUnitInput u1 = new(new EntityId(1), new Vector2(50f, 40f), 0f, 1.0f, 1.5f, TraversabilityCapabilities.Bridge, true);
        GroupUnitInput u2 = new(new EntityId(2), new Vector2(50.075f, 30f), 0f, 1.0f, 1.5f, TraversabilityCapabilities.Bridge, true);
        GroupUnitInput u3 = new(new EntityId(3), new Vector2(50.15f, 20f), 0f, 1.0f, 1.5f, TraversabilityCapabilities.Bridge, true);

        GroupUnitInput[][] permutations =
        [
            [u1, u2, u3],
            [u1, u3, u2],
            [u2, u1, u3],
            [u2, u3, u1],
            [u3, u1, u2],
            [u3, u2, u1],
        ];

        Vector2? targetU1 = null;
        Vector2? targetU2 = null;
        Vector2? targetU3 = null;

        for (int p = 0; p < permutations.Length; p++)
        {
            GroupMoveResult result = GroupDestinationDistribution.Distribute(
                sequence: (uint)(p + 1),
                serverTick: 1,
                units: permutations[p],
                target: new Vector2(100f, 100f),
                targetSurface: 1,
                finalFacing: null,
                mode: MoveMode.Forward,
                evaluator: evaluator);

            Assert.Equal(3, result.Placed.Length);

            Vector2 assignedU1 = Vector2.Zero;
            Vector2 assignedU2 = Vector2.Zero;
            Vector2 assignedU3 = Vector2.Zero;

            foreach (var a in result.Placed.Span)
            {
                if (a.Unit == new EntityId(1)) assignedU1 = a.Target;
                else if (a.Unit == new EntityId(2)) assignedU2 = a.Target;
                else if (a.Unit == new EntityId(3)) assignedU3 = a.Target;
            }

            if (p == 0)
            {
                targetU1 = assignedU1;
                targetU2 = assignedU2;
                targetU3 = assignedU3;
            }
            else
            {
                Assert.Equal(targetU1!.Value.X, assignedU1.X, 3);
                Assert.Equal(targetU1!.Value.Y, assignedU1.Y, 3);
                Assert.Equal(targetU2!.Value.X, assignedU2.X, 3);
                Assert.Equal(targetU2!.Value.Y, assignedU2.Y, 3);
                Assert.Equal(targetU3!.Value.X, assignedU3.X, 3);
                Assert.Equal(targetU3!.Value.Y, assignedU3.Y, 3);
            }
        }
    }
}
