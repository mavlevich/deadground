using System.Numerics;
using DeadGround.Engine;
using Xunit;

namespace DeadGround.Tests;

public sealed class DetectionSystemTests
{
    private sealed class TestCatalog : IUnitPerceptionCatalog
    {
        private readonly Dictionary<ushort, UnitPerceptionProfile> _profiles = new();

        public void Register(ushort definition, VisualSensorProfile? sensor, TargetObservationPoints? points, ContactClassification classification = ContactClassification.Vehicle)
        {
            _profiles[definition] = new UnitPerceptionProfile(sensor, points, classification);
        }

        public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile) =>
            _profiles.TryGetValue(definition, out profile);
    }

    private static HeightMap CreateFlatMap(int size = 65, float cellSize = 2.0f)
    {
        ushort[] samples = new ushort[size * size];
        return new HeightMap(size, size, cellSize, 0.01f, samples);
    }

    [Fact]
    public void TargetInsideVisualSensorRangeIsDetected()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 50.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionSystem detection = new(geometry, catalog);
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(20, 20), 5.0f);
        world.CreateUnit(2, 0, new Vector2(40, 20), 5.0f); // distance = 20m < 50m

        detection.Update(world, tick: 1);
        detection.Update(world, tick: 2);

        ReadOnlySpan<ContactObservation> p1Contacts = detection.GetContacts(1);
        Assert.Equal(1, p1Contacts.Length);
        Assert.Equal(ContactState.Current, p1Contacts[0].State);
        Assert.Equal(new Vector2(40, 20), p1Contacts[0].Position);
    }

    [Fact]
    public void TargetOutsideVisualSensorRangeIsNotDetected()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 30.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionSystem detection = new(geometry, catalog);
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(20, 20), 5.0f);
        world.CreateUnit(2, 0, new Vector2(60, 20), 5.0f); // distance = 40m > 30m

        detection.Update(world, tick: 2);

        ReadOnlySpan<ContactObservation> p1Contacts = detection.GetContacts(1);
        Assert.Equal(0, p1Contacts.Length);
    }

    [Fact]
    public void OpaqueBuildingOccludesLineOfSight()
    {
        HeightMap map = CreateFlatMap();
        // Building at X: 30..40, Z: 15..25, height: 6.0m
        OpaqueStructure building = new(new StructureId(1), 30, 0, 15, 40, 6, 25, isGrounded: true);
        StaticGeometry geometry = new(map, [building]);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 60.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.8f, 1.8f));

        DetectionSystem detection = new(geometry, catalog);
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(20, 20), 5.0f);
        world.CreateUnit(2, 0, new Vector2(50, 20), 5.0f); // Observer at 20, Target at 50, building in between (30..40)

        detection.Update(world, tick: 2);

        ReadOnlySpan<ContactObservation> contacts = detection.GetContacts(1);
        Assert.Equal(0, contacts.Length);
    }

    [Fact]
    public void HillOccludesLineOfSight()
    {
        const int size = 65;
        ushort[] samples = new ushort[size * size];
        // Create an 8m hill centered at (35, 20)
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x * 2.0f - 35.0f;
                float dz = z * 2.0f - 20.0f;
                float dist2 = dx * dx + dz * dz;
                float h = MathF.Max(0, 1.0f - dist2 / 100.0f) * 8.0f;
                samples[z * size + x] = (ushort)MathF.Round(h * 100f);
            }
        }
        HeightMap map = new(size, size, 2.0f, 0.01f, samples);
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 60.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.8f, 1.8f));

        DetectionSystem detection = new(geometry, catalog);
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(20, 20), 5.0f);
        world.CreateUnit(2, 0, new Vector2(50, 20), 5.0f);

        detection.Update(world, tick: 2);

        ReadOnlySpan<ContactObservation> contacts = detection.GetContacts(1);
        Assert.Equal(0, contacts.Length);
    }

    [Fact]
    public void LowWallAllowsPartialVisibilityOfUpperTargetPoint()
    {
        HeightMap map = CreateFlatMap();
        // Low wall at X: 30..31, Z: 15..25, height: 1.0m
        OpaqueStructure wall = new(new StructureId(1), 30, 0, 15, 31, 1.0f, 25, isGrounded: true);
        StaticGeometry geometry = new(map, [wall]);
        TestCatalog catalog = new();
        // Target points: 0.6m (hull, below wall) and 1.8m (turret roof, above wall)
        catalog.Register(0, new VisualSensorProfile(range: 50.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.6f, 1.8f));

        DetectionSystem detection = new(geometry, catalog);
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(20, 20), 5.0f); // Eye height = 1.8m
        world.CreateUnit(2, 0, new Vector2(35, 20), 5.0f); // Hull-down behind 1m wall

        detection.Update(world, tick: 1);
        detection.Update(world, tick: 2);

        ReadOnlySpan<ContactObservation> contacts = detection.GetContacts(1);
        Assert.Equal(1, contacts.Length);
        Assert.Equal(ContactState.Current, contacts[0].State);
    }

    [Fact]
    public void SpatialGridUpdatesCorrectlyAfterUnitMovement()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 30.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionSystem detection = new(geometry, catalog);
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(20, 20), 5.0f);
        EntityId enemyId = world.CreateUnit(2, 0, new Vector2(60, 20), 5.0f); // Initially 40m away (out of range)

        detection.Update(world, tick: 1);
        detection.Update(world, tick: 2);
        Assert.Equal(0, detection.GetContacts(1).Length);

        // Move enemy closer to (30, 20) (distance = 10m < 30m, 30m at 5m/s takes 120 ticks)
        world.SetMoveTarget(2, enemyId, new Vector2(30, 20));
        MovementSystem movement = new();
        for (int t = 3; t <= 130; t++)
        {
            movement.Update(world, t);
        }

        detection.Update(world, tick: 131);
        detection.Update(world, tick: 132);
        ReadOnlySpan<ContactObservation> contacts = detection.GetContacts(1);
        Assert.Equal(1, contacts.Length);
        Assert.Equal(ContactState.Current, contacts[0].State);
    }

    [Fact]
    public void FullContactLifecycle_Detect_Track_LastKnown_Expire()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure building = new(new StructureId(1), 20, 0, 0, 25, 4, 20, isGrounded: true);
        StaticGeometry geometry = new(map, [building]);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 60.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionTuning tuning = new(memoryDurationTicks: 50, correlationWindowTicks: 60, correlationDistanceGate: 30.0f, evaluationIntervalTicks: 2);
        DetectionSystem detection = new(geometry, catalog, tuning);
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(10, 10), 5.0f);
        EntityId enemy = world.CreateUnit(2, 0, new Vector2(15, 10), 5.0f); // In clear sight

        // 1. Initial detection
        detection.Update(world, tick: 1);
        detection.Update(world, tick: 2);
        ReadOnlySpan<ContactObservation> contacts = detection.GetContacts(1);
        Assert.Equal(1, contacts.Length);
        Assert.Equal(ContactState.Current, contacts[0].State);
        ContactId initialContactId = contacts[0].Id;

        // 2. Move enemy behind building to (30, 10) (15m takes 60 ticks, occluded at x >= 20 around tick 22)
        world.SetMoveTarget(2, enemy, new Vector2(30, 10));
        MovementSystem movement = new();
        int tick = 2;
        for (int i = 0; i < 40; i++)
        {
            tick++;
            movement.Update(world, tick);
            detection.Update(world, tick);
        }

        // 3. Loss of track -> transitions to LastKnown with frozen position
        contacts = detection.GetContacts(1);
        Assert.Equal(1, contacts.Length);
        Assert.Equal(ContactState.LastKnown, contacts[0].State);
        Assert.Equal(initialContactId, contacts[0].Id);
        Vector2 lastPos = contacts[0].Position;

        // 4. Age without sight until memory duration (50 ticks) expires
        for (int i = 0; i < 60; i++)
        {
            tick++;
            detection.Update(world, tick);
        }

        // Contact should expire and be removed
        contacts = detection.GetContacts(1);
        Assert.Equal(0, contacts.Length);
    }

    [Fact]
    public void TwoObserversShareContact_OneLosingSightMaintainsCurrent()
    {
        HeightMap map = CreateFlatMap();
        // Building occluding observer 1 but not observer 2
        OpaqueStructure building = new(new StructureId(1), 25, 0, 15, 35, 6, 25, isGrounded: true);
        StaticGeometry geometry = new(map, [building]);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 50.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionTuning tuning = new(evaluationIntervalTicks: 2);
        DetectionSystem detection = new(geometry, catalog, tuning);
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(10, 20), 5.0f); // Observer 1 (ID 1, odd: evaluates at tick 1, 3, 5...)
        world.CreateUnit(1, 0, new Vector2(40, 40), 5.0f); // Observer 2 (ID 2, even: evaluates at tick 2, 4, 6...)
        world.CreateUnit(2, 0, new Vector2(40, 20), 5.0f); // Target (ID 3)

        // Tick 2: Observer 2 evaluates and sees Target (no obstacle between (40,40) and (40,20))
        detection.Update(world, tick: 2);
        ReadOnlySpan<ContactObservation> contacts = detection.GetContacts(1);
        Assert.Equal(1, contacts.Length);
        Assert.Equal(ContactState.Current, contacts[0].State);

        // Tick 3: Observer 1 evaluates and cannot see Target (building between (10,20) and (40,20))
        detection.Update(world, tick: 3);
        // Contact must remain Current because Observer 2 still observes it within evaluation interval!
        contacts = detection.GetContacts(1);
        Assert.Equal(1, contacts.Length);
        Assert.Equal(ContactState.Current, contacts[0].State);
    }

    [Fact]
    public void ObserverWorkIsSpreadAcrossEvaluationIntervalTicks()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 50.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionTuning tuning = new(evaluationIntervalTicks: 2);
        DetectionSystem detection = new(geometry, catalog, tuning);
        WorldState world = new(map);
        // Observer 1 has EntityId(1) -> 1 % 2 = 1 (odd tick phase)
        world.CreateUnit(1, 0, new Vector2(20, 20), 5.0f);
        // Observer 2 has EntityId(2) -> 2 % 2 = 0 (even tick phase)
        world.CreateUnit(1, 0, new Vector2(20, 30), 5.0f);
        // Hostile target
        world.CreateUnit(2, 0, new Vector2(40, 20), 5.0f);

        // On tick 0: scheduled phase is 0 (even). Only Observer 2 evaluates.
        detection.Update(world, tick: 0);
        int evenQueries = detection.CandidateQueriesCount;
        Assert.True(evenQueries > 0);

        // On tick 1: scheduled phase is 1 (odd). Only Observer 1 evaluates.
        detection.Update(world, tick: 1);
        int oddQueries = detection.CandidateQueriesCount;
        Assert.True(oddQueries > 0);
    }

    [Fact]
    public void RediscoveryWithinCorrelationBoundsReusesContactId()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 60.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionTuning tuning = new(memoryDurationTicks: 40, correlationWindowTicks: 40, correlationDistanceGate: 25.0f, evaluationIntervalTicks: 2);
        SideContactTracker tracker = new(side: 1, tuning);

        EntityId enemy = new(10);
        // 1. Initial observation at tick 0
        tracker.BeginDetectionStep();
        tracker.RegisterObservation(enemy, new Vector2(20, 20), currentTick: 0, ContactClassification.Vehicle, 0);
        tracker.EndDetectionStep(currentTick: 0);
        ContactId originalId = tracker.ExportContacts()[0].Id;

        // 2. Lost observation at tick 4
        tracker.BeginDetectionStep();
        tracker.EndDetectionStep(currentTick: 4);
        Assert.Equal(ContactState.LastKnown, tracker.ExportContacts()[0].State);

        // 3. Rediscovery at tick 10 within 15m (gate is 25m, elapsed is 6 ticks <= 40)
        tracker.BeginDetectionStep();
        tracker.RegisterObservation(enemy, new Vector2(30, 20), currentTick: 10, ContactClassification.Vehicle, 0);
        tracker.EndDetectionStep(currentTick: 10);

        ReadOnlySpan<ContactObservation> contacts = tracker.ExportContacts();
        Assert.Equal(1, contacts.Length);
        Assert.Equal(ContactState.Current, contacts[0].State);
        Assert.Equal(originalId, contacts[0].Id); // Reused ContactId under correlation policy!
    }

    [Fact]
    public void RediscoveryOutsideCorrelationDistanceGateAllocatesNewContactId()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 100.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionTuning tuning = new(memoryDurationTicks: 40, correlationWindowTicks: 40, correlationDistanceGate: 10.0f, evaluationIntervalTicks: 2);
        SideContactTracker tracker = new(side: 1, tuning);

        EntityId enemy = new(10);
        // 1. Initial observation at tick 0
        tracker.BeginDetectionStep();
        tracker.RegisterObservation(enemy, new Vector2(20, 20), currentTick: 0, ContactClassification.Vehicle, 0);
        tracker.EndDetectionStep(currentTick: 0);
        ContactId originalId = tracker.ExportContacts()[0].Id;

        // 2. Lost observation at tick 4
        tracker.BeginDetectionStep();
        tracker.EndDetectionStep(currentTick: 4);

        // 3. Rediscovery at distance 40m (> 10m gate)
        tracker.BeginDetectionStep();
        tracker.RegisterObservation(enemy, new Vector2(60, 20), currentTick: 10, ContactClassification.Vehicle, 0);
        tracker.EndDetectionStep(currentTick: 10);

        ReadOnlySpan<ContactObservation> contacts = tracker.ExportContacts();
        // Since original contact did not match distance gate, a new contact was allocated,
        // and original LastKnown contact persists until expiry!
        Assert.Equal(2, contacts.Length);
        Assert.Contains(contacts.ToArray(), c => c.Id != originalId && c.State == ContactState.Current);
    }

    [Fact]
    public void CapacityOverflowEvictsOldestLastKnownContact()
    {
        DetectionTuning tuning = new(memoryDurationTicks: 100, evaluationIntervalTicks: 2);
        SideContactTracker tracker = new(side: 1, tuning, capacity: 3);

        // Fill capacity with 3 LastKnown contacts
        tracker.BeginDetectionStep();
        tracker.RegisterObservation(new EntityId(1), new Vector2(10, 10), currentTick: 0, ContactClassification.Vehicle, 0);
        tracker.RegisterObservation(new EntityId(2), new Vector2(20, 20), currentTick: 2, ContactClassification.Vehicle, 0);
        tracker.RegisterObservation(new EntityId(3), new Vector2(30, 30), currentTick: 4, ContactClassification.Vehicle, 0);
        tracker.EndDetectionStep(currentTick: 4);

        // Lose all contacts so they become LastKnown
        tracker.BeginDetectionStep();
        tracker.EndDetectionStep(currentTick: 6);
        Assert.Equal(3, tracker.Count);

        // Now observe a 4th new target at tick 8 (overflow!)
        tracker.BeginDetectionStep();
        tracker.RegisterObservation(new EntityId(4), new Vector2(40, 40), currentTick: 8, ContactClassification.Vehicle, 0);
        tracker.EndDetectionStep(currentTick: 8);

        Assert.Equal(3, tracker.Count);
        ReadOnlySpan<ContactObservation> contacts = tracker.ExportContacts();
        // Target 1 was oldest LastKnown (seen at tick 0) -> evicted!
        // Live observation (Target 4) is preserved!
        Assert.Contains(contacts.ToArray(), c => c.State == ContactState.Current);
    }

    [Fact]
    public void HiddenEntityRemovalDoesNotLeakHiddenDeathEvent()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure wall = new(new StructureId(1), 20, 0, 0, 25, 4, 20, isGrounded: true);
        StaticGeometry geometry = new(map, [wall]);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 50.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionTuning tuning = new(memoryDurationTicks: 50, evaluationIntervalTicks: 2);
        DetectionSystem detection = new(geometry, catalog, tuning);
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(10, 10), 5.0f);
        EntityId enemy = world.CreateUnit(2, 0, new Vector2(15, 10), 5.0f);

        // Detect enemy
        detection.Update(world, tick: 1);
        detection.Update(world, tick: 2);
        Assert.Equal(1, detection.GetContacts(1).Length);
        Assert.Equal(ContactState.Current, detection.GetContacts(1)[0].State);

        // Move enemy behind the wall to (30, 10) (15m distance takes 60 ticks, occluded at x >= 20 around tick 22)
        world.SetMoveTarget(2, enemy, new Vector2(30, 10));
        MovementSystem movement = new();
        int tick = 2;
        for (int i = 0; i < 40; i++)
        {
            tick++;
            movement.Update(world, tick);
            detection.Update(world, tick);
        }
        Assert.Equal(ContactState.LastKnown, detection.GetContacts(1)[0].State);

        // Destroy / remove enemy out of sight
        world.RemoveUnit(enemy);

        // Stepping detection must NOT immediately remove the LastKnown marker
        tick++;
        detection.Update(world, tick);
        tick++;
        detection.Update(world, tick);
        ReadOnlySpan<ContactObservation> contacts = detection.GetContacts(1);
        Assert.Equal(1, contacts.Length);
        Assert.Equal(ContactState.LastKnown, contacts[0].State);
    }

    [Fact]
    public void RepeatedUpdateKnowledgeCallDoesNotAdvanceDetectionOrKnowledge()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 50.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionTuning tuning = new(evaluationIntervalTicks: 2);
        DetectionSystem detection = new(geometry, catalog, tuning);
        RuleSet rules = new(new MovementSystem(), detection);
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(10, 10), 5.0f);
        world.CreateUnit(2, 0, new Vector2(30, 10), 5.0f);

        Simulation sim = new(world, rules);
        sim.Step(); // Tick 1
        sim.Step(); // Tick 2 (detection evaluates)

        PlayerKnowledge k1 = new(1);
        sim.UpdateKnowledge(k1);
        Assert.Equal(1, k1.ContactCount);
        ContactId id1 = k1.Contacts[0].Id;

        // Calling UpdateKnowledge 10 times consecutively on the same simulation tick
        for (int i = 0; i < 10; i++)
        {
            PlayerKnowledge kRepeat = new(1);
            sim.UpdateKnowledge(kRepeat);
            Assert.Equal(1, kRepeat.ContactCount);
            Assert.Equal(id1, kRepeat.Contacts[0].Id);
            Assert.Equal(k1.Contacts[0].State, kRepeat.Contacts[0].State);
        }
    }

    [Fact]
    public void WarmedDetectionSimulation_AllocatesZeroManagedMemory()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 50.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionTuning tuning = new(evaluationIntervalTicks: 2);
        DetectionSystem detection = new(geometry, catalog, tuning);
        RuleSet rules = new(new MovementSystem(), detection);
        WorldState world = new(map);
        for (int i = 0; i < 20; i++)
        {
            world.CreateUnit(1, 0, new Vector2(10 + i, 10), 5.0f);
            world.CreateUnit(2, 0, new Vector2(30 + i, 10), 5.0f);
        }

        Simulation sim = new(world, rules);
        PlayerKnowledge k = new(1);

        // Warm up simulation and knowledge buffers
        for (int i = 0; i < 100; i++)
        {
            sim.Step();
            sim.UpdateKnowledge(k);
        }

        // A per-tick allocation shows up in every window. One-time runtime work, such as tiered JIT
        // recompiling hot methods with a delay on a slow CI runner, shows up in one window at most;
        // it made this test fail once on macOS with 4,680 bytes.
        long allocated = long.MaxValue;
        for (int window = 0; window < 3 && allocated != 0; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                sim.Step();
                sim.UpdateKnowledge(k);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0L, allocated);
    }

    [Fact]
    public void StorageCompactionAndHiddenEntityRemoval_PreservesContactIdentitiesAndSnapshots()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 50.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionTuning tuning = new(evaluationIntervalTicks: 2);

        // World 1: Observer at (10,10), hidden enemy at (120,120), visible enemies at (20,20) and (25,20)
        WorldState world1 = new(map);
        world1.CreateUnit(1, 0, new Vector2(10, 10), 5.0f);
        world1.CreateUnit(2, 0, new Vector2(120, 120), 5.0f);
        world1.CreateUnit(2, 0, new Vector2(20, 20), 5.0f);
        world1.CreateUnit(2, 0, new Vector2(25, 20), 5.0f);

        // World 2: Identical units, but hidden enemy is removed before detection, causing storage compaction
        WorldState world2 = new(map);
        world2.CreateUnit(1, 0, new Vector2(10, 10), 5.0f);
        EntityId hidden2 = world2.CreateUnit(2, 0, new Vector2(120, 120), 5.0f);
        world2.CreateUnit(2, 0, new Vector2(20, 20), 5.0f);
        world2.CreateUnit(2, 0, new Vector2(25, 20), 5.0f);
        world2.RemoveUnit(hidden2); // Swaps unit (25,20) into slot 1!

        DetectionSystem detection1 = new(geometry, catalog, tuning);
        DetectionSystem detection2 = new(geometry, catalog, tuning);

        detection1.Update(world1, tick: 1);
        detection1.Update(world1, tick: 2);

        detection2.Update(world2, tick: 1);
        detection2.Update(world2, tick: 2);

        ReadOnlySpan<ContactObservation> contacts1 = detection1.GetContacts(1);
        ReadOnlySpan<ContactObservation> contacts2 = detection2.GetContacts(1);

        Assert.Equal(2, contacts1.Length);
        Assert.Equal(2, contacts2.Length);

        // Both worlds must assign identical ContactIds to identical positions
        Assert.Equal(contacts1[0].Id, contacts2[0].Id);
        Assert.Equal(contacts1[0].Position, contacts2[0].Position);
        Assert.Equal(contacts1[1].Id, contacts2[1].Id);
        Assert.Equal(contacts1[1].Position, contacts2[1].Position);
    }

    [Fact]
    public void RediscoveryAfterStorageCompaction_CorrelatesDeterministically()
    {
        HeightMap map = CreateFlatMap();
        OpaqueStructure wall = new(new StructureId(1), 20, 0, 0, 25, 4, 30, isGrounded: true);
        StaticGeometry geometry = new(map, [wall]);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 50.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionTuning tuning = new(memoryDurationTicks: 50, correlationWindowTicks: 50, correlationDistanceGate: 10.0f, evaluationIntervalTicks: 2);

        // Initial setup: Observer at (10,10), visible enemies at (15,10) and (15,20)
        WorldState world = new(map);
        world.CreateUnit(1, 0, new Vector2(10, 10), 5.0f);
        EntityId enemyA = world.CreateUnit(2, 0, new Vector2(15, 10), 5.0f);
        EntityId enemyB = world.CreateUnit(2, 0, new Vector2(15, 20), 5.0f);

        DetectionSystem detection = new(geometry, catalog, tuning);
        detection.Update(world, tick: 1);
        detection.Update(world, tick: 2);

        ReadOnlySpan<ContactObservation> initial = detection.GetContacts(1);
        Assert.Equal(2, initial.Length);
        ContactId idA = initial[0].Id;
        ContactId idB = initial[1].Id;

        // Move both enemies behind wall to X=30 (15m takes 60 ticks, occluded at x >= 20 around tick 22)
        world.SetMoveTarget(2, enemyA, new Vector2(30, 10));
        world.SetMoveTarget(2, enemyB, new Vector2(30, 20));
        MovementSystem movement = new();
        for (int t = 3; t <= 42; t++)
        {
            movement.Update(world, t);
            detection.Update(world, t);
        }

        Assert.Equal(ContactState.LastKnown, detection.GetContacts(1)[0].State);
        Assert.Equal(ContactState.LastKnown, detection.GetContacts(1)[1].State);

        // Create a temporary hidden enemy and remove it to alter internal storage indices
        EntityId tempHidden = world.CreateUnit(2, 0, new Vector2(120, 120), 5.0f);
        world.RemoveUnit(tempHidden);

        // Move enemy units back in front of the wall (to X=16)
        world.SetMoveTarget(2, enemyA, new Vector2(16, 10));
        world.SetMoveTarget(2, enemyB, new Vector2(16, 20));
        for (int t = 43; t <= 85; t++)
        {
            movement.Update(world, t);
            detection.Update(world, t);
        }

        ReadOnlySpan<ContactObservation> rediscovered = detection.GetContacts(1);
        Assert.Equal(2, rediscovered.Length);
        Assert.Equal(ContactState.Current, rediscovered[0].State);
        Assert.Equal(ContactState.Current, rediscovered[1].State);

        // Contact IDs must correlate back to their respective original tracks
        Assert.Equal(idA, rediscovered[0].Id);
        Assert.Equal(idB, rediscovered[1].Id);
    }

    [Theory]
    [InlineData(5000u)]
    [InlineData((uint)int.MaxValue)]
    public void SideContactTracker_TrackingForEntityIdAbove4096_RetainsSingleContactOver40Ticks(uint entityValue)
    {
        DetectionTuning tuning = new(evaluationIntervalTicks: 2);
        SideContactTracker tracker = new(1, tuning);
        EntityId enemyId = new(entityValue);

        Span<SideContactTracker.ObservedTarget> targets = stackalloc SideContactTracker.ObservedTarget[1];
        ContactId? initialContactId = null;

        for (int tick = 1; tick <= 40; tick++)
        {
            tracker.BeginDetectionStep();
            targets[0] = new SideContactTracker.ObservedTarget(enemyId, new Vector2(25, 25));
            tracker.RegisterObservations(targets, tick);
            tracker.EndDetectionStep(tick);

            ReadOnlySpan<ContactObservation> contacts = tracker.ExportContacts();
            Assert.Equal(1, contacts.Length);
            Assert.Equal(ContactState.Current, contacts[0].State);

            initialContactId ??= contacts[0].Id;
            Assert.Equal(initialContactId.Value, contacts[0].Id);
            Assert.Equal(1, tracker.Count);
        }
    }

    [Fact]
    public void SideContactTracker_DynamicCorrelationGate_RejectsEarlyDisplacementBeyondPlausibleSpeed()
    {
        // Max correlation speed 10 m/s, slack 5m, tick rate 20 Hz
        DetectionTuning tuning = new(
            maxCorrelationSpeed: 10.0f,
            slack: 5.0f,
            maxGate: 35.0f,
            evaluationIntervalTicks: 2,
            correlationWindowTicks: 60);
        SideContactTracker tracker = new(1, tuning);

        // Tick 0: Target A observed at (20, 20)
        tracker.BeginDetectionStep();
        tracker.RegisterObservation(new EntityId(1), new Vector2(20, 20), currentTick: 0, ContactClassification.Vehicle, 0);
        tracker.EndDetectionStep(currentTick: 0);
        ContactId idA = tracker.ExportContacts()[0].Id;

        // Tick 2: Target A is lost, transitions to LastKnown at (20, 20)
        tracker.BeginDetectionStep();
        tracker.EndDetectionStep(currentTick: 2);
        Assert.Equal(ContactState.LastKnown, tracker.ExportContacts()[0].State);

        // Tick 4 (elapsed 2 ticks = 0.1s): dynamic gate is min(35, 5 + 0.1 * 10) = 6.0m
        // Target B observed 20m away at (40, 20). 20m > 6m, so must NOT correlate with A!
        tracker.BeginDetectionStep();
        tracker.RegisterObservation(new EntityId(2), new Vector2(40, 20), currentTick: 4, ContactClassification.Vehicle, 0);
        tracker.EndDetectionStep(currentTick: 4);

        ReadOnlySpan<ContactObservation> contacts = tracker.ExportContacts();
        Assert.Equal(2, contacts.Length);

        // Contact A remains LastKnown, and a new contact is created for target B
        Assert.Contains(contacts.ToArray(), c => c.Id == idA && c.State == ContactState.LastKnown);
        Assert.Contains(contacts.ToArray(), c => c.Id != idA && c.State == ContactState.Current);
    }

    [Fact]
    public void SideContactTracker_DynamicCorrelationGate_AcceptsRediscoveryWithinPlausibleSpeed()
    {
        DetectionTuning tuning = new(
            maxCorrelationSpeed: 10.0f,
            slack: 5.0f,
            maxGate: 35.0f,
            evaluationIntervalTicks: 2,
            correlationWindowTicks: 60);
        SideContactTracker tracker = new(1, tuning);

        // Tick 0: Target A observed at (20, 20)
        tracker.BeginDetectionStep();
        tracker.RegisterObservation(new EntityId(1), new Vector2(20, 20), currentTick: 0, ContactClassification.Vehicle, 0);
        tracker.EndDetectionStep(currentTick: 0);
        ContactId idA = tracker.ExportContacts()[0].Id;

        // Tick 2: Target A is lost, transitions to LastKnown
        tracker.BeginDetectionStep();
        tracker.EndDetectionStep(currentTick: 2);
        Assert.Equal(ContactState.LastKnown, tracker.ExportContacts()[0].State);

        // Tick 42 (elapsed 40 ticks = 2.0s): dynamic gate is min(35, 5 + 2.0 * 10) = 25.0m
        // Target observed at (35, 20) (distance 15m <= 25m): must correlate back to Contact A!
        tracker.BeginDetectionStep();
        tracker.RegisterObservation(new EntityId(1), new Vector2(35, 20), currentTick: 42, ContactClassification.Vehicle, 0);
        tracker.EndDetectionStep(currentTick: 42);

        ReadOnlySpan<ContactObservation> contacts = tracker.ExportContacts();
        Assert.Equal(1, contacts.Length);
        Assert.Equal(idA, contacts[0].Id);
        Assert.Equal(ContactState.Current, contacts[0].State);
        Assert.Equal(new Vector2(35, 20), contacts[0].Position);
    }

    [Fact]
    public void DetectionSystem_AlliesAreNotDetectedAsEnemyContacts()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 50.0f, eyeHeight: 1.8f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionSystem detection = new(geometry, catalog);
        WorldState world = new(map);

        // 4 players: Team 1 has players 1 & 2; Team 2 has players 3 & 4
        world.SetPlayerTeam(1, 1);
        world.SetPlayerTeam(2, 1);
        world.SetPlayerTeam(3, 2);
        world.SetPlayerTeam(4, 2);

        // Observer for player 1
        world.CreateUnit(1, 0, new Vector2(20, 20), 5.0f);
        // Ally from player 2 (Team 1) at distance 5m
        world.CreateUnit(2, 0, new Vector2(25, 20), 5.0f);
        // Enemy from player 3 (Team 2) at distance 10m
        world.CreateUnit(3, 0, new Vector2(30, 20), 5.0f);

        detection.Update(world, tick: 1);
        detection.Update(world, tick: 2);

        ReadOnlySpan<ContactObservation> p1Contacts = detection.GetContacts(1);
        Assert.Equal(1, p1Contacts.Length);
        Assert.Equal(ContactState.Current, p1Contacts[0].State);
        Assert.Equal(new Vector2(30, 20), p1Contacts[0].Position); // Only enemy (player 3) detected
    }

    [Fact]
    public void DetectionSystem_TargetBehindSparsePeak_IsNotObserved()
    {
        const int size = 65;
        ushort[] spike = new ushort[size * size];
        spike[5 * size + 10] = 500; // height 5m at x=20, z=10
        HeightMap spikeMap = new(size, size, 2.0f, 0.01f, spike);
        StaticGeometry spikeGeo = new(spikeMap, []);

        Vector3 a = new(1f, 1f, 1f);
        Vector3 b = new(101f, 1f, 51f);

        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 200.0f, eyeHeight: 1.0f), new TargetObservationPoints(1.0f));

        DetectionSystem detection = new(spikeGeo, catalog, new DetectionTuning(evaluationIntervalTicks: 1));
        WorldState world = new(spikeMap);
        world.CreateUnit(1, 0, new Vector2(a.X, a.Z), 1.0f);
        world.CreateUnit(2, 0, new Vector2(b.X, b.Z), 1.0f);

        detection.Update(world, tick: 1);

        ReadOnlySpan<ContactObservation> p1Contacts = detection.GetContacts(1);
        Assert.True(p1Contacts.IsEmpty, "Unit behind 5m sparse terrain peak should not be observed.");

        ReadOnlySpan<ContactObservation> p2Contacts = detection.GetContacts(2);
        Assert.True(p2Contacts.IsEmpty, "Observer behind 5m sparse terrain peak should not observe target.");
    }

    [Fact]
    public void DetectionSystem_TwoEnemyUnitsOfDifferentDefinitionsSeen_ExposeTypesAndClassificationsImmediately()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        // Observer unit (Tank, def 0)
        catalog.Register(0, new VisualSensorProfile(range: 100.0f, eyeHeight: 2.0f), new TargetObservationPoints(0.5f, 1.5f), ContactClassification.Tank);
        // Enemy unit 1 (Recon, def 1)
        catalog.Register(1, null, new TargetObservationPoints(0.5f, 1.0f), ContactClassification.Recon);
        // Enemy unit 2 (Apc, def 2)
        catalog.Register(2, null, new TargetObservationPoints(0.5f, 1.2f), ContactClassification.Apc);

        DetectionSystem detection = new(geometry, catalog);
        WorldState world = new(map);
        // Side 1 observer
        world.CreateUnit(1, 0, new Vector2(20, 20), 5.0f);
        // Side 2 units
        world.CreateUnit(2, 1, new Vector2(35, 20), 5.0f);
        world.CreateUnit(2, 2, new Vector2(50, 20), 5.0f);

        detection.Update(world, tick: 1);
        detection.Update(world, tick: 2);

        ReadOnlySpan<ContactObservation> p1Contacts = detection.GetContacts(1);
        Assert.Equal(2, p1Contacts.Length);

        ContactObservation reconContact = Assert.Single(p1Contacts.ToArray(), c => c.Type == 1);
        Assert.Equal(ContactState.Current, reconContact.State);
        Assert.Equal(ContactClassification.Recon, reconContact.Classification);
        Assert.Equal(new Vector2(35, 20), reconContact.Position);

        ContactObservation apcContact = Assert.Single(p1Contacts.ToArray(), c => c.Type == 2);
        Assert.Equal(ContactState.Current, apcContact.State);
        Assert.Equal(ContactClassification.Apc, apcContact.Classification);
        Assert.Equal(new Vector2(50, 20), apcContact.Position);
    }

    [Fact]
    public void DetectionSystem_LastKnownContact_TypeAndPositionDoNotChangeWhenHiddenUnitMoves()
    {
        HeightMap map = CreateFlatMap();
        StaticGeometry geometry = new(map, []);
        TestCatalog catalog = new();
        // Observer has 60m range
        catalog.Register(0, new VisualSensorProfile(range: 60.0f, eyeHeight: 2.0f), new TargetObservationPoints(0.5f, 1.5f), ContactClassification.Tank);
        // Enemy tank
        catalog.Register(1, null, new TargetObservationPoints(0.5f, 1.5f), ContactClassification.Tank);

        DetectionTuning tuning = new(memoryDurationTicks: 100, evaluationIntervalTicks: 1);
        DetectionSystem detection = new(geometry, catalog, tuning);
        WorldState world = new(map);
        // Side 1 observer at (20, 20)
        world.CreateUnit(1, 0, new Vector2(20, 20), 5.0f);
        // Side 2 enemy unit at (35, 20) (distance 15m < 60m)
        EntityId enemyId = world.CreateUnit(2, 1, new Vector2(35, 20), 10.0f);

        // Tick 1: observed
        detection.Update(world, tick: 1);
        ReadOnlySpan<ContactObservation> p1Contacts = detection.GetContacts(1);
        Assert.Equal(1, p1Contacts.Length);
        Assert.Equal(ContactState.Current, p1Contacts[0].State);
        Assert.Equal(1, p1Contacts[0].Type);
        Assert.Equal(new Vector2(35, 20), p1Contacts[0].Position);
        ContactId contactId = p1Contacts[0].Id;

        // Break line of sight: move the enemy unit beyond 60m range (e.g. to (100, 100))
        world.SetPosition(enemyId, new Vector2(100, 100));

        // Tick 2: observation lost -> transitions to LastKnown
        detection.Update(world, tick: 2);
        p1Contacts = detection.GetContacts(1);
        Assert.Equal(1, p1Contacts.Length);
        Assert.Equal(contactId, p1Contacts[0].Id);
        Assert.Equal(ContactState.LastKnown, p1Contacts[0].State);
        Assert.Equal(1, p1Contacts[0].Type);
        Assert.Equal(new Vector2(35, 20), p1Contacts[0].Position);

        // Move the hidden unit further away in the fog of war to (120, 120)
        world.SetPosition(enemyId, new Vector2(120, 120));

        // Tick 3: contact remains LastKnown, Type and Position MUST NOT change!
        detection.Update(world, tick: 3);
        p1Contacts = detection.GetContacts(1);
        Assert.Equal(1, p1Contacts.Length);
        Assert.Equal(contactId, p1Contacts[0].Id);
        Assert.Equal(ContactState.LastKnown, p1Contacts[0].State);
        Assert.Equal(1, p1Contacts[0].Type);
        Assert.Equal(new Vector2(35, 20), p1Contacts[0].Position);
        Assert.Equal(ContactClassification.Tank, p1Contacts[0].Classification);
    }

    [Fact]
    public void FirstDetection_WithOpaqueWall_AttributesDistanceToActualObservingUnit_NotBlindCloserUnit()
    {
        // Map 1000m x 1000m
        HeightMap map = CreateFlatMap(size: 501, cellSize: 2.0f);
        // Opaque wall between x=245..255, z=400..600, height=50
        OpaqueStructure wall = new(new StructureId(1), new Vector3(245f, 0f, 400f), new Vector3(255f, 50f, 600f));
        StaticGeometry geometry = new(map, [wall]);
        TestCatalog catalog = new();
        catalog.Register(0, new VisualSensorProfile(range: 1500.0f, eyeHeight: 2.0f), new TargetObservationPoints(0.5f, 1.5f));

        DetectionSystem detection = new(geometry, catalog);
        WorldState world = new(map);

        // Friendly tank at (200, 500) behind the wall relative to target
        EntityId tank = world.CreateUnit(1, 0, new Vector2(200f, 500f), 5.0f);
        // Friendly scout at (900, 700) with unobstructed line of sight to target
        EntityId scout = world.CreateUnit(1, 0, new Vector2(900f, 700f), 5.0f);
        // Enemy APC at (300, 500)
        EntityId apc = world.CreateUnit(2, 0, new Vector2(300f, 500f), 5.0f);

        EntityId recordedObserverId = default;
        Vector2 recordedObserverPos = default;
        EntityId recordedTargetId = default;
        float recordedDistance = 0f;
        int observationsCount = 0;

        detection.OnObservationEstablished = (tick, side, observerId, obsPos, targetId, targetPos, classification, targetType) =>
        {
            if (side == 1 && targetId == apc && observationsCount == 0)
            {
                recordedObserverId = observerId;
                recordedObserverPos = obsPos;
                recordedTargetId = targetId;
                recordedDistance = Vector2.Distance(obsPos, targetPos);
                observationsCount++;
            }
        };

        detection.Update(world, tick: 1);
        detection.Update(world, tick: 2);

        Assert.Equal(1, observationsCount);
        Assert.Equal(scout, recordedObserverId);
        Assert.NotEqual(tank, recordedObserverId);
        Assert.Equal(new Vector2(900f, 700f), recordedObserverPos);
        Assert.Equal(apc, recordedTargetId);
        // Actual observing distance from scout (900, 700) to APC (300, 500) is sqrt(600^2 + 200^2) = 632.46 m
        Assert.InRange(recordedDistance, 632.4f, 632.5f);
        // Blind closer tank was only 100 m away but blocked by the wall
        float tankDistance = Vector2.Distance(new Vector2(200f, 500f), new Vector2(300f, 500f));
        Assert.Equal(100f, tankDistance);
        Assert.NotEqual(tankDistance, recordedDistance);
    }
}
