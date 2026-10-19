using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class VisibilityRulesTests
{
    [Fact]
    public void CatalogValidation_ValidJson_LoadsSuccessfully()
    {
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.NotNull(catalog.Detection);
        Assert.True(catalog.Detection.MemoryDurationTicks > 0);
        Assert.True(catalog.Detection.EvaluationIntervalTicks > 0);
        Assert.True(catalog.Detection.CorrelationDistanceGate > 0f);

        Assert.Equal(4, catalog.Units.Length);
        Assert.Equal("prototype_vehicle", catalog.Units[0].Id);
        Assert.Equal("recon_vehicle", catalog.Units[1].Id);
        Assert.Equal("prototype_tank", catalog.Units[2].Id);
        Assert.Equal("apc", catalog.Units[3].Id);

        foreach (UnitDefinition unit in catalog.Units)
        {
            Assert.True(unit.Speed > 0);
            Assert.True(unit.SensorRange > 0);
            Assert.True(unit.EyeHeight > 0);
            Assert.NotNull(unit.TargetPoints);
            Assert.InRange(unit.TargetPoints.Length, 1, 8);
            foreach (float pt in unit.TargetPoints)
            {
                Assert.True(pt >= 0);
            }
        }
    }

    [Theory]
    [InlineData("{\"schema_version\":1,\"units\":[{\"id\":\"\",\"speed\":5.0,\"sensor_range\":50.0,\"eye_height\":1.8,\"target_points\":[0.5]}]}")] // empty id
    [InlineData("{\"schema_version\":1,\"units\":[{\"id\":\"a\",\"speed\":-1.0,\"sensor_range\":50.0,\"eye_height\":1.8,\"target_points\":[0.5]}]}")] // negative speed
    [InlineData("{\"schema_version\":1,\"units\":[{\"id\":\"a\",\"speed\":5.0,\"sensor_range\":-50.0,\"eye_height\":1.8,\"target_points\":[0.5]}]}")] // negative range
    [InlineData("{\"schema_version\":1,\"units\":[{\"id\":\"a\",\"speed\":5.0,\"sensor_range\":50.0,\"eye_height\":-1.8,\"target_points\":[0.5]}]}")] // negative eye height
    [InlineData("{\"schema_version\":1,\"units\":[{\"id\":\"a\",\"speed\":5.0,\"sensor_range\":50.0,\"eye_height\":1.8,\"target_points\":[]}]}")] // empty target points
    [InlineData("{\"schema_version\":1,\"units\":[{\"id\":\"a\",\"speed\":5.0,\"sensor_range\":50.0,\"eye_height\":1.8,\"target_points\":[-0.5]}]}")] // negative target point
    [InlineData("{\"schema_version\":1,\"units\":[{\"id\":\"a\",\"speed\":5.0,\"sensor_range\":50.0,\"eye_height\":1.8,\"target_points\":[0.5]},{\"id\":\"a\",\"speed\":5.0,\"sensor_range\":50.0,\"eye_height\":1.8,\"target_points\":[0.5]}]}")] // duplicate id
    [InlineData("{\"schema_version\":1,\"units\":[{\"id\":\"a\",\"speed\":5.0,\"sensor_range\":50.0,\"eye_height\":1.8,\"target_points\":[0.5]}],\"detection\":{\"memory_duration_ticks\":-1}}")] // invalid tuning
    [InlineData("{\"schema_version\":1,\"units\":[{\"id\":\"a\",\"speed\":5.0,\"sensor_range\":50.0,\"eye_height\":1.8,\"target_points\":[0.5]}],\"detection\":{\"evaluation_interval_ticks\":0}}")] // invalid eval interval
    [InlineData("{\"schema_version\":1,\"units\":[{\"id\":\"a\",\"speed\":5.0,\"sensor_range\":50.0,\"eye_height\":1.8,\"target_points\":[0.5]}],\"detection\":{\"correlation_distance_gate\":-5.0}}")] // invalid gate
    public void CatalogValidation_RejectsInvalidJson(string json)
    {
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(json));
    }

    [Fact]
    public void ScenarioFactory_RespectsCatalogDetectionIntervalAndExplicitOverride()
    {
        UnitCatalog defaultCatalog = PrototypeScenario.LoadCatalog();
        Simulation defaultSim = VisibilityScenario.CreateSimulation(catalog: defaultCatalog);
        Assert.NotNull(defaultSim.Detection);
        Assert.Equal(defaultCatalog.Detection!.EvaluationIntervalTicks, defaultSim.Detection.Tuning.EvaluationIntervalTicks);

        // Explicit override to 1 (20 Hz detection)
        Simulation overrideSim = VisibilityScenario.CreateSimulation(catalog: defaultCatalog, evaluationIntervalTicks: 1);
        Assert.NotNull(overrideSim.Detection);
        Assert.Equal(1, overrideSim.Detection.Tuning.EvaluationIntervalTicks);
    }

    [Fact]
    public void VisibilityScenario_AllUnitSpawnPositionsAreOutsideOpaqueStructures()
    {
        StaticGeometry geometry = VisibilityScenario.CreateStaticGeometry();
        Vector2[] spawnPositions =
        [
            new(20f, 25f), // Side 1 Recon 1
            new(20f, 45f), // Side 1 Recon 2
            new(33f, 27f), // Side 2 Tank 1
            new(60f, 45f), // Side 2 Tank 2
            new(95f, 95f), // Side 2 Recon 3
        ];

        foreach (Vector2 pos in spawnPositions)
        {
            float groundY = geometry.HeightMap.GetHeight(pos.X, pos.Y);
            Vector3 unitPos = new(pos.X, groundY + 0.5f, pos.Y);

            foreach (OpaqueStructure structure in geometry.Structures)
            {
                bool inside = unitPos.X >= structure.MinX && unitPos.X <= structure.MaxX
                    && unitPos.Y >= structure.MinY && unitPos.Y <= structure.MaxY
                    && unitPos.Z >= structure.MinZ && unitPos.Z <= structure.MaxZ;
                Assert.False(inside,
                    $"Unit at ({pos.X}, {pos.Y}) spawned inside structure {structure.Id.Value}.");
            }
        }
    }

    [Fact]
    public void MapHash_StructureCanonicalOrder_PermutationInvariance()
    {
        HeightMap map = VisibilityScenario.CreateMap();

        OpaqueStructure s1 = new(new StructureId(1), 10, 0, 10, 20, 5, 20, isGrounded: true);
        OpaqueStructure s2 = new(new StructureId(2), 30, 0, 30, 40, 5, 40, isGrounded: true);
        OpaqueStructure s3 = new(new StructureId(3), 50, 0, 50, 60, 5, 60, isGrounded: true);

        ulong hashForward = MatchSetup.ComputeMapHash(map, [s1, s2, s3]);
        ulong hashReverse = MatchSetup.ComputeMapHash(map, [s3, s2, s1]);
        ulong hashPermuted = MatchSetup.ComputeMapHash(map, [s2, s1, s3]);

        Assert.Equal(hashForward, hashReverse);
        Assert.Equal(hashForward, hashPermuted);

        // Modifying height or position changes map hash
        OpaqueStructure s3Modified = new(new StructureId(3), 50, 0, 50, 60, 6f, 60, isGrounded: true);
        ulong hashModified = MatchSetup.ComputeMapHash(map, [s1, s2, s3Modified]);
        Assert.NotEqual(hashForward, hashModified);
    }

    [Fact]
    public void InformationIsolation_HiddenOpponentUnits_ProduceIdenticalSnapshots()
    {
        StaticGeometry geometry = VisibilityScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        // Sim A: Observer at (20, 25), enemy at (95, 95) behind hill (completely unobserved)
        Simulation simA = VisibilityScenario.CreateSimulation(geometry, catalog);

        // Sim B: Observer at (20, 25), enemy at (98, 98) behind hill (completely unobserved)
        WorldState worldB = new(geometry.HeightMap);
        worldB.CreateUnit(1, 1, new Vector2(20f, 25f), catalog.Units[1].Speed);
        worldB.CreateUnit(1, 1, new Vector2(20f, 45f), catalog.Units[1].Speed);
        worldB.CreateUnit(2, 2, new Vector2(33f, 27f), catalog.Units[2].Speed);
        worldB.CreateUnit(2, 2, new Vector2(60f, 45f), catalog.Units[2].Speed);
        worldB.CreateUnit(2, 1, new Vector2(98f, 98f), catalog.Units[1].Speed); // Shifted hidden position
        OpsRules rulesB = new(geometry, new OpsPerceptionCatalog(catalog), PrototypeScenario.CreateDetectionTuning(catalog));
        Simulation simB = new(worldB, rulesB);

        // Run both simulations for 10 ticks
        for (int i = 0; i < 10; i++)
        {
            simA.Step();
            simB.Step();
        }

        PlayerKnowledge knowledgeA = new(1);
        PlayerKnowledge knowledgeB = new(1);
        simA.UpdateKnowledge(knowledgeA);
        simB.UpdateKnowledge(knowledgeB);

        // Player 1 knowledge should be identical
        Assert.Equal(knowledgeA.OwnUnits.Length, knowledgeB.OwnUnits.Length);
        Assert.Equal(knowledgeA.ContactCount, knowledgeB.ContactCount);

        byte[] snapA = new byte[WireProtocol.MaxMessageSize];
        byte[] snapB = new byte[WireProtocol.MaxMessageSize];

        int lenA = WireProtocol.WriteSnapshot(snapA, 1, 10, knowledgeA.OwnUnits, knowledgeA.Contacts);
        int lenB = WireProtocol.WriteSnapshot(snapB, 1, 10, knowledgeB.OwnUnits, knowledgeB.Contacts);

        Assert.Equal(lenA, lenB);
        Assert.True(snapA.AsSpan(0, lenA).SequenceEqual(snapB.AsSpan(0, lenB)));
    }

    [Fact]
    public void InformationIsolation_HiddenRemovalAndStorageCompaction_ProducesIdenticalSnapshots()
    {
        StaticGeometry geometry = VisibilityScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        // Sim A: Observer at (10, 10), hidden enemy at (120, 120), visible enemies at (20, 20) and (25, 20)
        WorldState worldA = new(geometry.HeightMap);
        worldA.CreateUnit(1, 1, new Vector2(10f, 10f), 5.0f);
        worldA.CreateUnit(2, 2, new Vector2(120f, 120f), 5.0f);
        worldA.CreateUnit(2, 2, new Vector2(20f, 20f), 5.0f);
        worldA.CreateUnit(2, 2, new Vector2(25f, 20f), 5.0f);
        Simulation simA = new(worldA, new OpsRules(geometry, new OpsPerceptionCatalog(catalog), new DetectionTuning(evaluationIntervalTicks: 2)));

        // Sim B: Identical, but hidden enemy removed before step -> swaps (25, 20) into slot 1
        WorldState worldB = new(geometry.HeightMap);
        worldB.CreateUnit(1, 1, new Vector2(10f, 10f), 5.0f);
        EntityId hiddenB = worldB.CreateUnit(2, 2, new Vector2(120f, 120f), 5.0f);
        worldB.CreateUnit(2, 2, new Vector2(20f, 20f), 5.0f);
        worldB.CreateUnit(2, 2, new Vector2(25f, 20f), 5.0f);
        worldB.RemoveUnit(hiddenB);
        Simulation simB = new(worldB, new OpsRules(geometry, new OpsPerceptionCatalog(catalog), new DetectionTuning(evaluationIntervalTicks: 2)));

        simA.Step(); simA.Step();
        simB.Step(); simB.Step();

        PlayerKnowledge knowledgeA = new(1);
        PlayerKnowledge knowledgeB = new(1);
        simA.UpdateKnowledge(knowledgeA);
        simB.UpdateKnowledge(knowledgeB);

        Assert.Equal(knowledgeA.ContactCount, knowledgeB.ContactCount);
        Assert.Equal(knowledgeA.Contacts[0].Id, knowledgeB.Contacts[0].Id);
        Assert.Equal(knowledgeA.Contacts[0].Position, knowledgeB.Contacts[0].Position);
        Assert.Equal(knowledgeA.Contacts[1].Id, knowledgeB.Contacts[1].Id);
        Assert.Equal(knowledgeA.Contacts[1].Position, knowledgeB.Contacts[1].Position);

        byte[] snapA = new byte[WireProtocol.MaxMessageSize];
        byte[] snapB = new byte[WireProtocol.MaxMessageSize];
        int lenA = WireProtocol.WriteSnapshot(snapA, 1, 2, knowledgeA.OwnUnits, knowledgeA.Contacts);
        int lenB = WireProtocol.WriteSnapshot(snapB, 1, 2, knowledgeB.OwnUnits, knowledgeB.Contacts);

        Assert.Equal(lenA, lenB);
        Assert.True(snapA.AsSpan(0, lenA).SequenceEqual(snapB.AsSpan(0, lenB)));
    }

    [Fact]
    public void Protocol_SnapshotWithContacts_SerializationRoundTrip()
    {
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        OwnUnitObservation[] ownUnits =
        [
            new OwnUnitObservation(new EntityId(1), 0, new Vector2(10f, 15f), 0),
            new OwnUnitObservation(new EntityId(2), 1, new Vector2(20f, 25f), 0)
        ];

        ContactObservation[] contacts =
        [
            new ContactObservation(new ContactId(101), ContactState.Current, ContactClassification.Unknown, 0, new Vector2(35.5f, 40.25f), 40),
            new ContactObservation(new ContactId(102), ContactState.LastKnown, ContactClassification.Unknown, 0, new Vector2(50f, 60f), 35)
        ];

        int bytesWritten = WireProtocol.WriteSnapshot(buffer, 1, 42, ownUnits, contacts);
        Assert.True(bytesWritten > 0);

        ClientReplica replica = new();
        bool success = replica.TryApply(buffer.AsSpan(0, bytesWritten));

        Assert.True(success);
        Assert.Equal(42, replica.LatestTick);
        Assert.Equal(1u, replica.LatestSequence);
        Assert.Equal(2, replica.OwnUnits.Length);
        Assert.Equal(2, replica.ContactCount);

        Assert.True(replica.TryGetContact(new ContactId(101), out ContactObservation c1));
        Assert.Equal(ContactClassification.Unknown, c1.Classification);
        Assert.Equal(ContactState.Current, c1.State);
        Assert.Equal(35.5f, c1.Position.X);
        Assert.Equal(40.25f, c1.Position.Y);
        Assert.Equal(40, c1.LastSeenTick);

        Assert.True(replica.TryGetContact(new ContactId(102), out ContactObservation c2));
        Assert.Equal(ContactState.LastKnown, c2.State);
        Assert.Equal(50f, c2.Position.X);
        Assert.Equal(60f, c2.Position.Y);
        Assert.Equal(35, c2.LastSeenTick);
    }

    [Fact]
    public void Protocol_RejectsFutureLastSeenTick_AndPreservesReplica()
    {
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        ClientReplica replica = new();

        // Valid initial snapshot at tick 5
        ContactObservation[] validContacts =
        [
            new ContactObservation(new ContactId(1), ContactState.Current, ContactClassification.Unknown, 0, new Vector2(10f, 20f), 5)
        ];
        int len1 = WireProtocol.WriteSnapshot(buffer, 1, 5, [], validContacts);
        Assert.True(replica.TryApply(buffer.AsSpan(0, len1)));
        Assert.Equal(1, replica.ContactCount);

        // Corrupted snapshot at tick 6 where contact claims LastSeenTick = 1000 (> snapshot tick 6)
        ContactObservation[] invalidContacts =
        [
            new ContactObservation(new ContactId(1), ContactState.Current, ContactClassification.Unknown, 0, new Vector2(10f, 20f), 1000)
        ];
        int len2 = WireProtocol.WriteSnapshot(buffer, 2, 6, [], invalidContacts);

        // Must be rejected by replica and decoder
        Assert.False(replica.TryApply(buffer.AsSpan(0, len2)));

        // Replica history must remain preserved and uncorrupted
        Assert.Equal(5, replica.LatestTick);
        Assert.Equal(1, replica.ContactCount);
        Assert.True(replica.TryGetContact(new ContactId(1), out ContactObservation current));
        Assert.Equal(5, current.LastSeenTick);
    }

    [Fact]
    public void Protocol_ContactBearingSnapshotFragmentation_ReassemblesAndApplies()
    {
        OwnUnitObservation[] units = new OwnUnitObservation[30];
        for (int i = 0; i < units.Length; i++)
        {
            units[i] = new OwnUnitObservation(new EntityId((uint)(i + 1)), 0, new Vector2(i * 2, i * 2), 0);
        }

        ContactObservation[] contacts = new ContactObservation[30];
        for (int i = 0; i < contacts.Length; i++)
        {
            contacts[i] = new ContactObservation(new ContactId((uint)(i + 100)), ContactState.Current,
                ContactClassification.Unknown, 0, new Vector2(i * 3, i * 3), 10);
        }

        byte[] fullSnapshot = new byte[WireProtocol.MaxMessageSize];
        int totalPayload = WireProtocol.WriteSnapshot(fullSnapshot, 1, 10, units, contacts);

        int maxPayload = WireProtocol.MaxFragmentPayloadSize;
        ushort fragmentCount = (ushort)((totalPayload + maxPayload - 1) / maxPayload);
        Assert.True(fragmentCount > 1, "Snapshot should span multiple fragments.");

        byte[] reassembled = new byte[WireProtocol.MaxMessageSize];
        byte[] fragmentBuffer = new byte[1500];

        for (ushort f = 0; f < fragmentCount; f++)
        {
            int offset = f * maxPayload;
            int length = Math.Min(maxPayload, totalPayload - offset);
            ReadOnlySpan<byte> chunk = fullSnapshot.AsSpan(offset, length);

            int fragSize = WireProtocol.WriteSnapshotFragment(
                fragmentBuffer, 1, 10, f, fragmentCount, totalPayload, offset, chunk);

            Assert.True(WireProtocol.TryReadSnapshotFragment(
                fragmentBuffer.AsSpan(0, fragSize),
                out uint seq, out int tick, out ushort index, out ushort count,
                out int payloadSize, out int fragOffset, out ReadOnlySpan<byte> payload));

            payload.CopyTo(reassembled.AsSpan(fragOffset));
        }

        Assert.True(fullSnapshot.AsSpan(0, totalPayload).SequenceEqual(reassembled.AsSpan(0, totalPayload)));

        ClientReplica replica = new();
        Assert.True(replica.TryApply(reassembled.AsSpan(0, totalPayload)));
        Assert.Equal(30, replica.OwnUnits.Length);
        Assert.Equal(30, replica.ContactCount);
        Assert.True(replica.TryGetContact(new ContactId(105), out ContactObservation c5));
        Assert.Equal(new Vector2(15, 15), c5.Position);
    }

    [Fact]
    public void Protocol_RejectsInvalidSnapshotData()
    {
        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        ContactObservation[] contacts =
        [
            new ContactObservation(new ContactId(1), ContactState.Current, ContactClassification.Unknown, 0, new Vector2(10f, 20f), 1)
        ];

        int len = WireProtocol.WriteSnapshot(buffer, 1, 1, [], contacts);

        // 1. Corrupt protocol version
        byte[] corrupted = (byte[])buffer.Clone();
        corrupted[0] = 99;
        ClientReplica replica = new();
        Assert.False(replica.TryApply(corrupted.AsSpan(0, len)));

        // 2. Corrupt contact state (stateByte must be 1 or 2)
        corrupted = (byte[])buffer.Clone();
        int contactsOffset = WireProtocol.SnapshotHeaderSize;
        corrupted[contactsOffset + 4] = 99; // invalid state
        replica = new();
        Assert.False(replica.TryApply(corrupted.AsSpan(0, len)));

        // 3. Corrupt contact ID to 0
        corrupted = (byte[])buffer.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(corrupted.AsSpan(contactsOffset), 0);
        replica = new();
        Assert.False(replica.TryApply(corrupted.AsSpan(0, len)));

        // 4. Corrupt position to NaN (at offset 8 in 20-byte contact record)
        corrupted = (byte[])buffer.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(corrupted.AsSpan(contactsOffset + 8), float.NaN);
        replica = new();
        Assert.False(replica.TryApply(corrupted.AsSpan(0, len)));

        // 5. Truncated buffer
        replica = new();
        Assert.False(replica.TryApply(buffer.AsSpan(0, len - 5)));
    }

    [Fact]
    public void ClientReplica_MaintainsContactsCorrectly()
    {
        ClientReplica replica = new();
        Assert.Equal(0, replica.ContactCount);

        byte[] buffer = new byte[WireProtocol.MaxMessageSize];

        // 1. Initial snapshot with 1 Current contact
        ContactObservation[] c1 =
        [
            new ContactObservation(new ContactId(1), ContactState.Current, ContactClassification.Unknown, 0, new Vector2(10f, 20f), 1)
        ];
        int len1 = WireProtocol.WriteSnapshot(buffer, 1, 1, [], c1);
        Assert.True(replica.TryApply(buffer.AsSpan(0, len1)));

        Assert.Equal(1, replica.ContactCount);
        Assert.True(replica.TryGetContact(new ContactId(1), out ContactObservation observed1));
        Assert.Equal(ContactState.Current, observed1.State);
        Assert.Equal(new Vector2(10f, 20f), observed1.Position);

        // 2. Snapshot where contact transitioned to LastKnown
        ContactObservation[] c2 =
        [
            new ContactObservation(new ContactId(1), ContactState.LastKnown, ContactClassification.Unknown, 0, new Vector2(10f, 20f), 1)
        ];
        int len2 = WireProtocol.WriteSnapshot(buffer, 2, 2, [], c2);
        Assert.True(replica.TryApply(buffer.AsSpan(0, len2)));

        Assert.Equal(1, replica.ContactCount);
        Assert.True(replica.TryGetContact(new ContactId(1), out ContactObservation observed2));
        Assert.Equal(ContactState.LastKnown, observed2.State);

        // 3. Snapshot with no contacts (expired)
        int len3 = WireProtocol.WriteSnapshot(buffer, 3, 3, [], []);
        Assert.True(replica.TryApply(buffer.AsSpan(0, len3)));

        Assert.Equal(0, replica.ContactCount);
        Assert.False(replica.TryGetContact(new ContactId(1), out _));
    }

    private static Simulation CreateDense1000Simulation(StaticGeometry geometry)
    {
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();

        DetectionTuning tuning = new(
            memoryDurationTicks: 100,
            correlationWindowTicks: 60,
            correlationDistanceGate: 25.0f,
            evaluationIntervalTicks: 2);

        OpsRules rules = new(geometry, new OpsPerceptionCatalog(catalog), tuning);
        WorldState world = new(geometry.HeightMap);
        Simulation sim = new(world, rules);

        // 500 Side 1 observers vs 500 Side 2 tanks
        for (int i = 0; i < 500; i++)
        {
            float x = 10f + (i % 25) * 2.0f;
            float y = 10f + (i / 25) * 5.4f;
            EntityId id = world.CreateUnit(1, 1, new Vector2(x, y), 0.001f);
            Assert.True(sim.TryEnqueue(1, new MoveCommand((uint)(i + 1), 0, id, new Vector2(x + 50f, y))));
            if (i % 200 == 199) sim.Step();
        }
        sim.Step();

        for (int i = 0; i < 500; i++)
        {
            float x = 70f + (i % 25) * 2.0f;
            float y = 10f + (i / 25) * 5.4f;
            EntityId id = world.CreateUnit(2, 2, new Vector2(x, y), 0.001f);
            Assert.True(sim.TryEnqueue(2, new MoveCommand((uint)(500 + i + 1), 0, id, new Vector2(x - 50f, y))));
            if (i % 200 == 199) sim.Step();
        }
        sim.Step();

        int movingCount = 0;
        for (int i = 0; i < world.Count; i++)
        {
            if (world.GetUnitAt(i).Moving) movingCount++;
        }
        Assert.Equal(1000, movingCount);

        return sim;
    }

    [Fact]
    public void Dense1000Entities_ZeroSteadyStateAllocationsAndMeetsTickBudget()
    {
        const int size = 65;
        HeightMap map = new(size, size, 2.0f, 0.01f, new ushort[size * size]);
        StaticGeometry geometry = new(map, []);
        using Simulation sim = CreateDense1000Simulation(geometry);

        PlayerKnowledge knowledge1 = new(1);
        PlayerKnowledge knowledge2 = new(2);
        byte[] buffer1 = new byte[WireProtocol.MaxMessageSize];
        byte[] buffer2 = new byte[WireProtocol.MaxMessageSize];

        // 20 warm-up ticks
        for (int i = 0; i < 20; i++)
        {
            sim.Step();
            if (sim.Tick % 2 == 0)
            {
                sim.UpdateKnowledge(knowledge1);
                WireProtocol.WriteSnapshot(buffer1, (uint)sim.Tick, sim.Tick, knowledge1.OwnUnits, knowledge1.Contacts);
                sim.UpdateKnowledge(knowledge2);
                WireProtocol.WriteSnapshot(buffer2, (uint)sim.Tick, sim.Tick, knowledge2.OwnUnits, knowledge2.Contacts);
            }
        }

        // 100 measured ticks
        double[] tickTimesMs = new double[100];
        using NoGcAllocationWindow window = new();
        long totalAllocated = 0;

        for (int i = 0; i < 100; i++)
        {
            long startAlloc = GC.GetAllocatedBytesForCurrentThread();
            long startTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

            sim.Step();
            if (sim.Tick % 2 == 0)
            {
                sim.UpdateKnowledge(knowledge1);
                WireProtocol.WriteSnapshot(buffer1, (uint)sim.Tick, sim.Tick, knowledge1.OwnUnits, knowledge1.Contacts);
                sim.UpdateKnowledge(knowledge2);
                WireProtocol.WriteSnapshot(buffer2, (uint)sim.Tick, sim.Tick, knowledge2.OwnUnits, knowledge2.Contacts);
            }

            long elapsedTicks = System.Diagnostics.Stopwatch.GetTimestamp() - startTimestamp;
            long endAlloc = GC.GetAllocatedBytesForCurrentThread();

            totalAllocated += (endAlloc - startAlloc);
            tickTimesMs[i] = (double)elapsedTicks / System.Diagnostics.Stopwatch.Frequency * 1000.0;
        }

        Array.Sort(tickTimesMs);
        double medianMs = tickTimesMs[50];
        double p99Ms = tickTimesMs[98];

        // Steady state execution must not allocate managed memory
        Assert.True(totalAllocated == 0, window.Describe(totalAllocated));

        // Functional CI smoke gate: guarantees zero allocations in steady state
        // and verifies simulation frames progress without unbounded stalls.
        // The dedicated performance acceptance gate across all 3 scenarios (Open Map, Solid Wall, Hill)
        // enforcing p99 < 15 ms is executed via tests/Benchmarks/Program.cs --probe.
#if DEBUG
        const double targetMedianMs = 150.0;
        const double targetP99Ms = 250.0;
#else
        const double targetMedianMs = 50.0;
        const double targetP99Ms = 250.0;
#endif
        Assert.True(medianMs < targetMedianMs, $"Median tick time {medianMs:F2} ms exceeded {targetMedianMs} ms target.");
        Assert.True(p99Ms < targetP99Ms, $"P99 tick time {p99Ms:F2} ms exceeded {targetP99Ms} ms target.");
    }

    [Fact]
    public void CatalogHash_ModifyingSensorRangeChangesContentHashAndBreaksHandshake()
    {
        UnitCatalog defaultCatalog = PrototypeScenario.LoadCatalog();
        Assert.NotEqual(0UL, defaultCatalog.ContentHash);

        string json = """
        {
          "schema_version": 1,
          "detection": {
            "memory_duration_ticks": 100,
            "correlation_window_ticks": 60,
            "correlation_distance_gate": 25.0,
            "evaluation_interval_ticks": 2
          },
          "units": [
            { "id": "prototype_vehicle", "speed": 6.0, "sensor_range": 60.0, "eye_height": 1.8, "target_points": [0.7, 1.6] },
            { "id": "recon_vehicle", "speed": 7.0, "sensor_range": 99.0, "eye_height": 1.6, "target_points": [0.6, 1.4] },
            { "id": "prototype_tank", "speed": 5.0, "sensor_range": 50.0, "eye_height": 2.0, "target_points": [0.8, 1.8] }
          ]
        }
        """;

        UnitCatalog modifiedCatalog = PrototypeScenario.LoadCatalog(json);
        Assert.NotEqual(defaultCatalog.ContentHash, modifiedCatalog.ContentHash);

        StaticGeometry geometry = VisibilityScenario.CreateStaticGeometry();
        MatchSetup setupServer = VisibilityScenario.CreateMatchSetup(geometry, catalog: defaultCatalog);
        MatchSetup setupClient = VisibilityScenario.CreateMatchSetup(geometry, catalog: modifiedCatalog);

        Assert.False(setupServer.Matches(setupClient));
    }

    [Fact]
    public void UnitCatalog_LookupByName_IsInvariantToUnitDefinitionOrder()
    {
        string shuffledJson = """
        {
          "schema_version": 1,
          "units": [
            { "id": "prototype_tank", "speed": 5.0, "sensor_range": 50.0, "eye_height": 2.0, "target_points": [0.8, 1.8] },
            { "id": "recon_vehicle", "speed": 7.0, "sensor_range": 70.0, "eye_height": 1.6, "target_points": [0.6, 1.4] },
            { "id": "prototype_vehicle", "speed": 6.0, "sensor_range": 60.0, "eye_height": 1.8, "target_points": [0.7, 1.6] }
          ]
        }
        """;

        UnitCatalog shuffledCatalog = PrototypeScenario.LoadCatalog(shuffledJson);

        Assert.Equal("prototype_tank", shuffledCatalog.Units[0].Id);
        Assert.Equal("recon_vehicle", shuffledCatalog.Units[1].Id);

        UnitDefinition recon = shuffledCatalog.RequireDefinition("recon_vehicle");
        Assert.Equal("recon_vehicle", recon.Id);
        Assert.Equal(7.0f, recon.Speed);
        Assert.Equal(1, shuffledCatalog.RequireDefinitionIndex("recon_vehicle"));

        UnitDefinition tank = shuffledCatalog.RequireDefinition("prototype_tank");
        Assert.Equal("prototype_tank", tank.Id);
        Assert.Equal(5.0f, tank.Speed);
        Assert.Equal(0, shuffledCatalog.RequireDefinitionIndex("prototype_tank"));

        Assert.Throws<KeyNotFoundException>(() => shuffledCatalog.RequireDefinition("non_existent"));

        // Simulation initialization creates observers and enemies with correct definitions regardless of order
        StaticGeometry geometry = VisibilityScenario.CreateStaticGeometry();
        using Simulation sim = VisibilityScenario.CreateSimulation(geometry, shuffledCatalog);
        PlayerKnowledge k1 = new(1);
        sim.UpdateKnowledge(k1);
        Assert.Equal(2, k1.OwnUnits.Length);
        Assert.Equal(new Vector2(20f, 25f), k1.OwnUnits[0].Position);

        PlayerKnowledge k2 = new(2);
        sim.UpdateKnowledge(k2);
        Assert.Equal(3, k2.OwnUnits.Length);
    }

    [Fact]
    public void DetectionSettings_UsesConsolidatedDefaults()
    {
        DetectionSettings settings = new();
        Assert.Equal(DetectionTuning.DefaultMemoryDurationTicks, settings.MemoryDurationTicks);
        Assert.Equal(DetectionTuning.DefaultCorrelationWindowTicks, settings.CorrelationWindowTicks);
        Assert.Equal(DetectionTuning.DefaultCorrelationDistanceGate, settings.CorrelationDistanceGate);
        Assert.Equal(DetectionTuning.DefaultEvaluationIntervalTicks, settings.EvaluationIntervalTicks);
        Assert.Equal(DetectionTuning.DefaultMaxCorrelationSpeed, settings.MaxCorrelationSpeed);
        Assert.Equal(DetectionTuning.DefaultSlack, settings.Slack);
        Assert.Equal(DetectionTuning.DefaultMaxGate, settings.MaxGate);
    }
}
