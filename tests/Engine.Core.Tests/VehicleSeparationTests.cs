using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class VehicleSeparationTests
{
    private sealed class TestMovementCatalog : IUnitMovementCatalog
    {
        private readonly Dictionary<ushort, UnitMovementProfile> _profiles = new();

        public TestMovementCatalog Register(
            ushort definition,
            float speed,
            float radius,
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
                TraversabilityCapabilities.DefaultVehicle,
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

    private static (NavigationSystem nav, WorldState world, Simulation sim) CreateTestEnvironment(
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
        return (nav, world, sim);
    }

    [Fact]
    public void Scenario1_ParkedAlly_FollowerBrakesAndWaitsWithoutPenetration()
    {
        TestMovementCatalog catalog = new();
        // Tank: radius 1.5m, collision radius 3.5m, accel 2.0, decel 4.0
        catalog.Register(1, speed: 10f, radius: 1.5f, turnRate: 30f, acceleration: 2f, deceleration: 4f, collisionRadius: 3.5f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog);
        using (sim)
        {
            // Parked ally at (50, 50)
            EntityId leader = world.CreateUnit(1, 1, new Vector2(50f, 50f), 10f, initialTurretYaw: MathF.PI);
            // Follower at (50, 20) moving North towards (50, 70)
            EntityId follower = world.CreateUnit(1, 1, new Vector2(50f, 20f), 10f, initialTurretYaw: MathF.PI);

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, follower, new Vector2(50f, 70f))));

            float minObservedDistance = float.MaxValue;
            for (int tick = 0; tick < 200; tick++)
            {
                sim.Step();

                world.TryGetUnit(leader, out UnitState lState);
                world.TryGetUnit(follower, out UnitState fState);

                float dist = Vector2.Distance(lState.Position, fState.Position);
                minObservedDistance = MathF.Min(minObservedDistance, dist);

                // Neither vehicle may ever penetrate the combined collision radius (3.5 + 3.5 = 7.0m)
                Assert.True(dist >= 7.0f - 1e-3f, $"Penetration detected: distance {dist:F2}m < 7.0m at tick {tick}");
            }

            world.TryGetUnit(follower, out UnitState finalFollower);
            Assert.False(finalFollower.Moving);
            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(follower));
            Assert.True(nav.SeparationWaitingUnits >= 1);
            Assert.True(minObservedDistance >= 7.0f);
        }
    }

    [Fact]
    public void Scenario2_ParkedEnemy_TreatedAsSolidObstacle()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.5f, turnRate: 30f, acceleration: 2f, deceleration: 4f, collisionRadius: 3.5f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog);
        using (sim)
        {
            // Enemy parked at (50, 50) on Team 2
            EntityId enemy = world.CreateUnit(2, 1, new Vector2(50f, 50f), 8f, initialTurretYaw: MathF.PI);
            world.SetFireStance(enemy, FireStance.HoldFire);

            // Friendly unit on Team 1 at (50, 20) ordered to (50, 70)
            EntityId friendly = world.CreateUnit(1, 1, new Vector2(50f, 20f), 8f, initialTurretYaw: MathF.PI);
            world.SetFireStance(friendly, FireStance.HoldFire);

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, friendly, new Vector2(50f, 70f))));

            for (int tick = 0; tick < 150; tick++)
            {
                sim.Step();

                world.TryGetUnit(enemy, out UnitState eState);
                world.TryGetUnit(friendly, out UnitState fState);

                float dist = Vector2.Distance(eState.Position, fState.Position);
                Assert.True(dist >= 7.0f - 1e-3f, $"Friendly penetrated enemy hull: dist {dist:F2}m at tick {tick}");
            }

            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(friendly));
        }
    }

    [Fact]
    public void Scenario3_LeadStops_FollowerBrakesSmoothly()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.5f, turnRate: 45f, acceleration: 2f, deceleration: 4f, collisionRadius: 3.0f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog);
        using (sim)
        {
            // Both driving North in a column: lead at (50, 40), follower at (50, 25)
            EntityId lead = world.CreateUnit(1, 1, new Vector2(50f, 40f), 8f, initialTurretYaw: MathF.PI);
            EntityId follower = world.CreateUnit(1, 1, new Vector2(50f, 25f), 8f, initialTurretYaw: MathF.PI);

            // Lead ordered to (50, 60), follower ordered to (50, 80)
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, lead, new Vector2(50f, 60f))));
            Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, follower, new Vector2(50f, 80f))));

            // Lead reaches (50, 60) and stops around tick 60
            float prevSpeed = 0f;
            for (int tick = 0; tick < 180; tick++)
            {
                sim.Step();

                world.TryGetUnit(lead, out UnitState lState);
                world.TryGetUnit(follower, out UnitState fState);

                float dist = Vector2.Distance(lState.Position, fState.Position);
                Assert.True(dist >= 6.0f - 1e-3f, $"Separation violated: dist {dist:F2}m at tick {tick}");

                float fSpeed = nav.GetUnitSpeed(follower);
                if (fState.Moving && prevSpeed > 0f)
                {
                    float speedDrop = prevSpeed - fSpeed;
                    float maxAllowedDecel = (4f * Simulation.TickSeconds) + 1e-3f; // decel * dt
                    Assert.True(speedDrop <= maxAllowedDecel + 1e-3f,
                        $"Follower decel jerk: dropped by {speedDrop:F3} m/s in tick {tick} (limit {maxAllowedDecel:F3})");
                }
                prevSpeed = fSpeed;
            }

            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(follower));
        }
    }

    [Fact]
    public void Scenario4_LeadResumes_FollowerResumesAutomaticallyWithoutNewOrder()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.5f, turnRate: 45f, acceleration: 2f, deceleration: 4f, collisionRadius: 3.0f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog);
        using (sim)
        {
            // Lead stopped at (50, 50), follower stopped behind at (50, 42)
            EntityId lead = world.CreateUnit(1, 1, new Vector2(50f, 50f), 8f, initialTurretYaw: MathF.PI);
            EntityId follower = world.CreateUnit(1, 1, new Vector2(50f, 35f), 8f, initialTurretYaw: MathF.PI);

            // Follower ordered to (50, 80)
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, follower, new Vector2(50f, 80f))));

            // Step until follower is stopped and waiting behind lead
            for (int tick = 0; tick < 80; tick++)
            {
                sim.Step();
            }

            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(follower));

            // Lead resumes moving forward to (50, 90)
            Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, lead, new Vector2(50f, 90f))));

            // Step further: follower must automatically resume without a new order
            bool followerResumed = false;
            for (int tick = 0; tick < 100; tick++)
            {
                sim.Step();

                if (nav.GetUnitSpeed(follower) > 0.5f && nav.GetUnitStatus(follower) == PathRequestStatus.PathFound)
                {
                    followerResumed = true;
                }

                world.TryGetUnit(lead, out UnitState lState);
                world.TryGetUnit(follower, out UnitState fState);
                float dist = Vector2.Distance(lState.Position, fState.Position);
                Assert.True(dist >= 6.0f - 1e-3f, $"Penetration during column following: dist {dist:F2}m at tick {tick}");
            }

            Assert.True(followerResumed, "Follower must resume moving after lead clears path.");
        }
    }

    [Fact]
    public void Scenario5_HeadOnApproach_SweptCollisionStopsBothSafely()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 10f, radius: 1.5f, turnRate: 45f, acceleration: 3f, deceleration: 5f, collisionRadius: 3.0f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog);
        using (sim)
        {
            // Fast head-on approach: A at (50, 20) moving North to (50, 80), B at (50, 80) moving South to (50, 20)
            EntityId unitA = world.CreateUnit(1, 1, new Vector2(50f, 20f), 10f, initialTurretYaw: MathF.PI);
            EntityId unitB = world.CreateUnit(2, 1, new Vector2(50f, 80f), 10f, initialTurretYaw: 0f);

            world.SetFireStance(unitA, FireStance.HoldFire);
            world.SetFireStance(unitB, FireStance.HoldFire);

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, unitA, new Vector2(50f, 80f))));
            Assert.True(sim.TryEnqueue(2, new MoveCommand(2, sim.Tick, unitB, new Vector2(50f, 20f))));

            float minDistance = float.MaxValue;
            for (int tick = 0; tick < 150; tick++)
            {
                sim.Step();

                world.TryGetUnit(unitA, out UnitState aState);
                world.TryGetUnit(unitB, out UnitState bState);

                float dist = Vector2.Distance(aState.Position, bState.Position);
                minDistance = MathF.Min(minDistance, dist);

                Assert.True(dist >= 6.0f - 1e-3f, $"Head-on collision penetration: dist {dist:F2}m < 6.0m at tick {tick}");
            }

            Assert.True(minDistance >= 6.0f);
            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(unitA));
            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(unitB));
        }
    }

    [Fact]
    public void Scenario6_ReverseGear_DetectsObstacleBehindAndHalts()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.5f, turnRate: 45f, reverseSpeed: 4f, acceleration: 2f, deceleration: 4f, collisionRadius: 3.0f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog);
        using (sim)
        {
            // Reversing tank at (50, 50) facing North (MathF.PI)
            EntityId tank = world.CreateUnit(1, 1, new Vector2(50f, 50f), 8f, initialTurretYaw: MathF.PI);
            // Obstacle parked behind at (50, 35)
            EntityId parked = world.CreateUnit(1, 1, new Vector2(50f, 35f), 8f, initialTurretYaw: MathF.PI);

            // Reverse move command to (50, 25) (which is directly behind, through parked unit)
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, tank, new Vector2(50f, 25f), Mode: MoveMode.Reverse)));

            for (int tick = 0; tick < 120; tick++)
            {
                sim.Step();

                world.TryGetUnit(tank, out UnitState tState);
                world.TryGetUnit(parked, out UnitState pState);

                float dist = Vector2.Distance(tState.Position, pState.Position);
                Assert.True(dist >= 6.0f - 1e-3f, $"Reverse gear penetration: dist {dist:F2}m < 6.0m at tick {tick}");
            }

            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(tank));
        }
    }

    [Fact]
    public void Scenario7_ThreeVehicles_SharedDestination_QueueUpWithoutError()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.5f, turnRate: 45f, acceleration: 2f, deceleration: 4f, collisionRadius: 3.0f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog);
        using (sim)
        {
            // 3 vehicles all ordered to (50, 70)
            EntityId v1 = world.CreateUnit(1, 1, new Vector2(50f, 50f), 8f, initialTurretYaw: MathF.PI);
            EntityId v2 = world.CreateUnit(1, 1, new Vector2(50f, 35f), 8f, initialTurretYaw: MathF.PI);
            EntityId v3 = world.CreateUnit(1, 1, new Vector2(50f, 20f), 8f, initialTurretYaw: MathF.PI);

            Vector2 sharedGoal = new(50f, 70f);
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, v1, sharedGoal)));
            Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, v2, sharedGoal)));
            Assert.True(sim.TryEnqueue(1, new MoveCommand(3, sim.Tick, v3, sharedGoal)));

            for (int tick = 0; tick < 250; tick++)
            {
                sim.Step();

                world.TryGetUnit(v1, out UnitState s1);
                world.TryGetUnit(v2, out UnitState s2);
                world.TryGetUnit(v3, out UnitState s3);

                float d12 = Vector2.Distance(s1.Position, s2.Position);
                float d23 = Vector2.Distance(s2.Position, s3.Position);

                Assert.True(d12 >= 6.0f - 1e-3f, $"Queue collision 1-2: dist {d12:F2}m at tick {tick}");
                Assert.True(d23 >= 6.0f - 1e-3f, $"Queue collision 2-3: dist {d23:F2}m at tick {tick}");
            }

            // Lead vehicle arrived at destination
            Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(v1));
            // Followers queued up waiting
            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(v2));
            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(v3));
        }
    }

    [Fact]
    public void Scenario8_WaitingVehicle_ReceivesNewDestination_ReplacesOrderImmediately()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.5f, turnRate: 45f, acceleration: 2f, deceleration: 4f, collisionRadius: 3.0f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog);
        using (sim)
        {
            EntityId leader = world.CreateUnit(1, 1, new Vector2(50f, 50f), 8f, initialTurretYaw: MathF.PI);
            EntityId follower = world.CreateUnit(1, 1, new Vector2(50f, 35f), 8f, initialTurretYaw: MathF.PI);

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, follower, new Vector2(50f, 60f))));

            for (int tick = 0; tick < 80; tick++) sim.Step();
            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(follower));

            // Issue new move order to an open lateral flank (80, 40)
            Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, follower, new Vector2(80f, 40f))));
            sim.Step();

            // Status must immediately transition to searching/found, not stuck in waiting
            PathRequestStatus status = nav.GetUnitStatus(follower);
            Assert.True(status is PathRequestStatus.Searching or PathRequestStatus.PathFound);

            // Follower completes move to new target
            for (int tick = 0; tick < 200; tick++) sim.Step();

            world.TryGetUnit(follower, out UnitState fState);
            Assert.InRange(Vector2.Distance(fState.Position, new Vector2(80f, 40f)), 0f, 0.5f);
        }
    }

    [Fact]
    public void Scenario9_RemovingBlockingVehicle_WaitingVehicleResumesAutomatically()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.5f, turnRate: 45f, acceleration: 2f, deceleration: 4f, collisionRadius: 3.0f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog);
        using (sim)
        {
            EntityId blocker = world.CreateUnit(1, 1, new Vector2(50f, 50f), 8f, initialTurretYaw: MathF.PI);
            EntityId waiting = world.CreateUnit(1, 1, new Vector2(50f, 35f), 8f, initialTurretYaw: MathF.PI);

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, waiting, new Vector2(50f, 70f))));

            for (int tick = 0; tick < 80; tick++) sim.Step();
            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(waiting));

            // Move the blocker completely out of the way
            world.RemoveUnit(blocker);

            // Step: waiting vehicle must detect path clear, resume, and reach (50, 70)
            for (int tick = 0; tick < 120; tick++) sim.Step();

            world.TryGetUnit(waiting, out UnitState wState);
            Assert.InRange(Vector2.Distance(wState.Position, new Vector2(50f, 70f)), 0f, 0.5f);
            Assert.Equal(PathRequestStatus.PathFound, nav.GetUnitStatus(waiting));
        }
    }

    [Fact]
    public void Scenario10_BridgeDeck_Vs_UnderSpan_NoVerticalCollision()
    {
        const int size = 101;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);

        // Bridge spanning X=30 to X=70 along Y=50 at elevation 8m, thickness 1.5m
        BridgeStructure bridge = new(
            new StructureId(1),
            new Vector2(30f, 50f),
            new Vector2(70f, 50f),
            12f,
            8f,
            8f,
            1.5f);

        StaticGeometry geo = new(map, ReadOnlySpan<OpaqueStructure>.Empty, [bridge], ReadOnlySpan<TerrainObstacle>.Empty);
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.5f, turnRate: 45f, acceleration: 2f, deceleration: 4f, collisionRadius: 3.0f, height: 2.5f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog, geo);
        using (sim)
        {
            // Unit on bridge deck at (35, 50) moving East to (65, 50) with SupportSurface = 1
            EntityId bridgeUnit = world.CreateUnit(1, 1, new Vector2(35f, 50f), 8f, initialTurretYaw: -MathF.PI / 2f, initialSupportSurface: 1);
            // Unit on ground under bridge at (50, 35) moving North to (50, 65) with SupportSurface = 0
            EntityId groundUnit = world.CreateUnit(1, 1, new Vector2(50f, 35f), 8f, initialTurretYaw: MathF.PI, initialSupportSurface: 0);

            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, bridgeUnit, new Vector2(65f, 50f))));
            Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, groundUnit, new Vector2(50f, 65f))));

            bool passedIntersection = false;
            for (int tick = 0; tick < 120; tick++)
            {
                sim.Step();

                world.TryGetUnit(bridgeUnit, out UnitState bState);
                world.TryGetUnit(groundUnit, out UnitState gState);

                float d2D = Vector2.Distance(bState.Position, gState.Position);
                if (d2D < 2.0f)
                {
                    // They cross within 2m of each other horizontally!
                    passedIntersection = true;
                    // Neither must be waiting for clearance because their vertical spans do not overlap
                    Assert.True(bState.Moving, "Bridge unit should not stop for ground unit underneath.");
                    Assert.True(gState.Moving, "Ground unit should not stop for bridge unit above.");
                }
            }

            Assert.True(passedIntersection, "Both units should have crossed through the same 2D intersection point.");
        }
    }

    [Fact]
    public void Scenario11_WaitingPreservesState_NoReplanning_NoWatchdogTimeout()
    {
        TestMovementCatalog catalog = new();
        catalog.Register(1, speed: 8f, radius: 1.5f, turnRate: 45f, acceleration: 2f, deceleration: 4f, collisionRadius: 3.0f);

        (NavigationSystem nav, WorldState world, Simulation sim) = CreateTestEnvironment(catalog);
        using (sim)
        {
            EntityId leader = world.CreateUnit(1, 1, new Vector2(50f, 50f), 8f, initialTurretYaw: MathF.PI);
            EntityId follower = world.CreateUnit(1, 1, new Vector2(50f, 35f), 8f, initialTurretYaw: MathF.PI);

            Vector2 finalTarget = new(50f, 80f);
            Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, follower, finalTarget)));

            // Run for 500 ticks (25 seconds at 20 Hz)
            for (int tick = 0; tick < 500; tick++)
            {
                sim.Step();
            }

            // Must NOT have timed out or failed to NoPath!
            Assert.Equal(PathRequestStatus.WaitingForClearance, nav.GetUnitStatus(follower));
            Assert.True(nav.HasActiveRoute(follower));
            Assert.Equal(finalTarget, nav.GetUnitFinalTarget(follower));
        }
    }

    [Fact]
    public void Scenario12_NetworkReplication_WaitingForClearance_WireProtocol()
    {
        Assert.Equal(20, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(20, WireProtocol.Version);

        OwnUnitObservation unit = new(
            Id: new EntityId(42),
            Definition: 1,
            Position: new Vector2(50f, 50f),
            SupportSurface: 0,
            NavStatus: PathRequestStatus.WaitingForClearance,
            Health: 100,
            Ammunition: 10,
            ReloadTicksRemaining: 0,
            AttackStatus: AttackExecutionStatus.None,
            TargetContactId: default,
            Stance: FireStance.FireAtWill,
            TurretYaw: 0f,
            HullHeading: 0f,
            NavOrderSequence: 5);

        byte[] buffer = new byte[1024];
        int bytesWritten = WireProtocol.WriteSnapshot(
            buffer,
            sequence: 100,
            tick: 200,
            units: [unit],
            contacts: ReadOnlySpan<ContactObservation>.Empty,
            events: ReadOnlySpan<CombatEventObservation>.Empty);

        Assert.True(bytesWritten > 0);

        SnapshotFrame frame = new(16);
        Assert.True(WireProtocol.TryReadSnapshot(buffer.AsSpan(0, bytesWritten), frame));

        Assert.Equal(100u, frame.Sequence);
        Assert.Equal(200, frame.Tick);
        Assert.Equal(1, frame.Count);

        // Deserialized record NavStatus must be WaitingForClearance
        Assert.Equal(PathRequestStatus.WaitingForClearance, frame.Units[0].NavStatus);
    }

    [Fact]
    public void Scenario13_MultiVehicleConvergence_NoPenetration()
    {
        // Tests multi-vehicle convergence (3 and 4 vehicles at high speed converging on a single point).
        // Verifies continuous swept distance between all vehicle pairs remains >= 2 * CollisionRadius.
        foreach (int n in new[] { 3, 4 })
        {
            foreach (float radius in new[] { 1.5f, 3.0f })
            {
                TestMovementCatalog catalog = new TestMovementCatalog()
                    .Register(1, speed: 12.0f, radius: 1.0f, turnRate: 45.0f, reverseSpeed: 4.0f, acceleration: 4.0f, deceleration: 4.0f, canPivot: true, minTurnRadius: 0.0f, collisionRadius: radius);

                var (nav, world, sim) = CreateTestEnvironment(catalog);
                using (sim)
                {
                    EntityId[] ids = new EntityId[n];
                    Vector2[] previous = new Vector2[n];
                    float requiredDistance = 2f * radius;

                    for (int i = 0; i < n; i++)
                    {
                        float angle = 2f * MathF.PI * i / n;
                        Vector2 radial = new(MathF.Cos(angle), MathF.Sin(angle));
                        Vector2 start = new Vector2(64f, 64f) + radial * 35f;
                        Vector2 target = new Vector2(64f, 64f) - radial * 45f;
                        float yaw = MathF.Atan2(radial.X, radial.Y);
                        ids[i] = world.CreateUnit(1, 1, start, 12.0f, initialTurretYaw: yaw);
                        previous[i] = start;
                        sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), 0, ids[i], target));
                    }

                    float minDistance = float.MaxValue;
                    for (int t = 0; t < 200; t++)
                    {
                        sim.Step();
                        for (int a = 0; a < n; a++)
                        {
                            for (int b = a + 1; b < n; b++)
                            {
                                world.TryGetUnit(ids[a], out UnitState ua);
                                world.TryGetUnit(ids[b], out UnitState ub);
                                Vector2 rel0 = previous[a] - previous[b];
                                Vector2 rel1 = ua.Position - ub.Position;
                                Vector2 delta = rel1 - rel0;
                                float deltaSq = delta.LengthSquared();
                                float tau = deltaSq > 1e-8f ? Math.Clamp(-Vector2.Dot(rel0, delta) / deltaSq, 0f, 1f) : 0f;
                                float d = (rel0 + tau * delta).Length();
                                if (d < minDistance)
                                {
                                    minDistance = d;
                                }
                            }
                        }

                        for (int i = 0; i < n; i++)
                        {
                            world.TryGetUnit(ids[i], out UnitState u);
                            previous[i] = u.Position;
                        }
                    }

                    Assert.True(minDistance >= requiredDistance - 0.001f,
                        $"Multi-vehicle penetration detected: minDistance={minDistance} < required={requiredDistance} (n={n}, radius={radius})");
                }
            }
        }
    }

    [Fact]
    public void Scenario14_WheeledVehicleNearObstacle_NoStationaryTurn_And_CurvatureCompliance()
    {
        // Tests wheeled vehicles (CanPivot = false) facing obstacles with diagonal orders.
        // Verifies zero turning while stationary (dist <= 1e-5f => turn == 0)
        // and strict curvature compliance (turn <= dist / minTurnRadius) across all ticks.
        foreach (float gap in new[] { 6.01f, 6.5f, 7.0f, 8.0f })
        {
            const float minTurnRadius = 7.5f;
            const float collisionRadius = 3.0f;
            TestMovementCatalog catalog = new TestMovementCatalog()
                .Register(1, speed: 8.0f, radius: 1.0f, turnRate: 45.0f, reverseSpeed: 2.5f, acceleration: 4.0f, deceleration: 4.0f, canPivot: false, minTurnRadius: minTurnRadius, collisionRadius: collisionRadius);

            var (nav, world, sim) = CreateTestEnvironment(catalog);
            using (sim)
            {
                // Blocker vehicle at (50, 50)
                EntityId blocker = world.CreateUnit(1, 1, new Vector2(50f, 50f), 8.0f, initialTurretYaw: 0f);

                // Wheeled vehicle behind blocker
                Vector2 startPos = new Vector2(50f, 50f - gap);
                EntityId car = world.CreateUnit(1, 1, startPos, 8.0f, initialTurretYaw: MathF.PI);

                // Diagonal order attempting to steer around blocker
                sim.TryEnqueue(1, new MoveCommand(1, 0, car, new Vector2(80f, 70f)));

                Vector2 prevPos = startPos;
                float prevHeading = MathF.PI;

                for (int t = 0; t < 200; t++)
                {
                    sim.Step();
                    world.TryGetUnit(car, out UnitState u);
                    world.TryGetCombatState(car, out WorldState.CombatState cs);

                    float dist = Vector2.Distance(prevPos, u.Position);
                    float turn = MathF.Abs(CombatSystem.ShorterArcDelta(prevHeading, cs.HullHeading));

                    if (dist <= 1e-5f)
                    {
                        Assert.True(turn <= 1e-4f,
                            $"Wheeled vehicle turned while stationary: gap={gap}, tick={t}, turn={turn * 180f / MathF.PI:F4} deg");
                    }
                    else
                    {
                        float allowedTurn = dist / minTurnRadius;
                        float excess = turn - allowedTurn;
                        Assert.True(excess <= 1e-4f,
                            $"Wheeled curvature violation: gap={gap}, tick={t}, dist={dist:F4}, turn={turn * 180f / MathF.PI:F4} deg, allowed={allowedTurn * 180f / MathF.PI:F4} deg");
                    }

                    prevPos = u.Position;
                    prevHeading = cs.HullHeading;
                }
            }
        }
    }
}
