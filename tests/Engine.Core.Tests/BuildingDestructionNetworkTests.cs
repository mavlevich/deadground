using System.Buffers.Binary;
using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using Xunit;

namespace Engine.Core.Tests;

public sealed class BuildingDestructionNetworkTests
{
    [Fact]
    public void WireProtocolV32_RoundTripsAttackGroundStructuresAndCollapseEvents_AndRejectsInvalidAndV31Handshake()
    {
        Assert.Equal(32, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(32, WireProtocol.Version);
        Assert.Equal(56, WireProtocol.SnapshotHeaderSize);
        Assert.Equal(8, WireProtocol.StructureSize);
        Assert.Equal(256, WireProtocol.MaxStructures);
        Assert.Equal(30, WireProtocol.AttackGroundSize);
        Assert.Equal(19, WireProtocol.AttackGroundKind);

        // 1. AttackGroundCommand round-trip (structure target and pure ground target)
        byte[] cmdBuf = new byte[WireProtocol.AttackGroundSize];
        AttackGroundCommand buildingCmd = new(
            Sequence: 77,
            ClientTick: 120,
            Unit: new EntityId(14),
            TargetPoint: new Vector3(206f, 4.5f, 206f),
            TargetStructure: new StructureId(42));
        WireProtocol.WriteAttackGround(cmdBuf, buildingCmd);
        Assert.True(WireProtocol.TryReadAttackGround(cmdBuf, out AttackGroundCommand decodedBuildingCmd));
        Assert.Equal(buildingCmd, decodedBuildingCmd);

        AttackGroundCommand groundCmd = new(
            Sequence: 78,
            ClientTick: 121,
            Unit: new EntityId(14),
            TargetPoint: new Vector3(150.25f, 0f, 300.5f),
            TargetStructure: default);
        WireProtocol.WriteAttackGround(cmdBuf, groundCmd);
        Assert.True(WireProtocol.TryReadAttackGround(cmdBuf, out AttackGroundCommand decodedGroundCmd));
        Assert.Equal(groundCmd, decodedGroundCmd);

        // Reject outdated v31 packet, wrong length, zero sequence/unit, negative structure id, non-finite target
        cmdBuf[0] = 31;
        Assert.False(WireProtocol.TryReadAttackGround(cmdBuf, out _));
        cmdBuf[0] = WireProtocol.Version;
        Assert.False(WireProtocol.TryReadAttackGround(cmdBuf.AsSpan(0, WireProtocol.AttackGroundSize - 1), out _));

        WireProtocol.WriteAttackGround(cmdBuf, buildingCmd with { Sequence = 0 });
        Assert.False(WireProtocol.TryReadAttackGround(cmdBuf, out _));
        WireProtocol.WriteAttackGround(cmdBuf, buildingCmd with { ClientTick = -1 });
        Assert.False(WireProtocol.TryReadAttackGround(cmdBuf, out _));
        WireProtocol.WriteAttackGround(cmdBuf, buildingCmd with { Unit = default });
        Assert.False(WireProtocol.TryReadAttackGround(cmdBuf, out _));
        WireProtocol.WriteAttackGround(cmdBuf, buildingCmd with { TargetStructure = new StructureId(-5) });
        Assert.False(WireProtocol.TryReadAttackGround(cmdBuf, out _));
        WireProtocol.WriteAttackGround(cmdBuf, buildingCmd with { TargetPoint = new Vector3(float.NaN, 0f, 0f) });
        Assert.False(WireProtocol.TryReadAttackGround(cmdBuf, out _));

        // 2. Snapshot round-trip with StructureObservation records and all 3 structure CombatEventKinds
        StructureObservation[] structures =
        [
            new(new StructureId(10), FloorsStanding: 3, FloorBreachMask: 0b00110, Health: 240),
            new(new StructureId(11), FloorsStanding: 0, FloorBreachMask: 0, Health: 0)
        ];
        CombatEventObservation[] events =
        [
            CombatEventObservation.ForStructure(201, 10, CombatEventKind.StructureDamaged, 1, new Vector3(100f, 2f, 200f), new Vector3(206f, 4f, 206f), new StructureId(10), HitResult.Penetrated),
            CombatEventObservation.ForStructure(202, 10, CombatEventKind.StructureFloorCollapsed, 1, new Vector3(206f, 4f, 206f), new Vector3(206f, 4f, 206f), new StructureId(10), HitResult.Penetrated),
            CombatEventObservation.ForStructure(203, 10, CombatEventKind.StructureCollapsed, 1, new Vector3(206f, 1.5f, 206f), new Vector3(206f, 1.5f, 206f), new StructureId(11), HitResult.Penetrated)
        ];

        byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
        int snapLen = WireProtocol.WriteSnapshot(
            snapBuf,
            sequence: 1,
            tick: 10,
            units: default,
            contacts: default,
            events: events,
            mission: default,
            wrecks: default,
            smokeClouds: default,
            missiles: default,
            structures: structures);

        Assert.Equal(
            WireProtocol.SnapshotHeaderSize + events.Length * WireProtocol.EventSize + structures.Length * WireProtocol.StructureSize,
            snapLen);

        ClientReplica replica = new();
        Assert.True(replica.TryApply(snapBuf.AsSpan(0, snapLen)));
        Assert.Equal(2, replica.StructureCount);
        Assert.True(replica.TryGetStructure(new StructureId(10), out StructureObservation s10));
        Assert.Equal(structures[0], s10);
        Assert.True(replica.TryGetStructure(new StructureId(11), out StructureObservation s11));
        Assert.Equal(structures[1], s11);
        Assert.False(replica.TryGetStructure(new StructureId(99), out _));

        Assert.Equal(3, replica.EventCount);
        Assert.Equal(CombatEventKind.StructureDamaged, replica.Events[0].Kind);
        Assert.Equal(new StructureId(10), replica.Events[0].StructureId);
        Assert.Equal(CombatEventKind.StructureFloorCollapsed, replica.Events[1].Kind);
        Assert.Equal(new StructureId(10), replica.Events[1].StructureId);
        Assert.Equal(CombatEventKind.StructureCollapsed, replica.Events[2].Kind);
        Assert.Equal(new StructureId(11), replica.Events[2].StructureId);

        // Reject invalid structure records (non-positive id, floors > MaxSupportedFloors, breach bits above FloorsStanding, duplicate id)
        byte[] badBuf = new byte[snapLen];
        snapBuf.AsSpan(0, snapLen).CopyTo(badBuf);
        int firstStructOffset = WireProtocol.SnapshotHeaderSize + events.Length * WireProtocol.EventSize;

        // Duplicate StructureId (set second record id = 10)
        BinaryPrimitives.WriteInt32LittleEndian(badBuf.AsSpan(firstStructOffset + WireProtocol.StructureSize, 4), 10);
        Assert.False(new ClientReplica().TryApply(badBuf));

        // Zero StructureId
        snapBuf.AsSpan(0, snapLen).CopyTo(badBuf);
        BinaryPrimitives.WriteInt32LittleEndian(badBuf.AsSpan(firstStructOffset, 4), 0);
        Assert.False(new ClientReplica().TryApply(badBuf));

        // FloorsStanding > MaxSupportedFloors
        snapBuf.AsSpan(0, snapLen).CopyTo(badBuf);
        badBuf[firstStructOffset + 4] = StructureDestructionProfile.MaxSupportedFloors + 1;
        Assert.False(new ClientReplica().TryApply(badBuf));

        // FloorBreachMask bit set above FloorsStanding (FloorsStanding == 3 -> bit 3 is invalid)
        snapBuf.AsSpan(0, snapLen).CopyTo(badBuf);
        badBuf[firstStructOffset + 5] = 0b01000;
        Assert.False(new ClientReplica().TryApply(badBuf));

        // Non-zero FloorBreachMask when FloorsStanding == 0
        snapBuf.AsSpan(0, snapLen).CopyTo(badBuf);
        badBuf[firstStructOffset + WireProtocol.StructureSize + 5] = 0b00001;
        Assert.False(new ClientReplica().TryApply(badBuf));

        // Structure event with zero StructureId rejected
        snapBuf.AsSpan(0, snapLen).CopyTo(badBuf);
        BinaryPrimitives.WriteUInt32LittleEndian(badBuf.AsSpan(WireProtocol.SnapshotHeaderSize + 11, 4), 0u);
        Assert.False(new ClientReplica().TryApply(badBuf));

        // Out-of-range CombatEventKind
        snapBuf.AsSpan(0, snapLen).CopyTo(badBuf);
        badBuf[WireProtocol.SnapshotHeaderSize + 8] = (byte)CombatEventKind.StructureCollapsed + 1;
        Assert.False(new ClientReplica().TryApply(badBuf));

        // 3. Handshake rejects legacy v31 client connecting to v32 server
        HeightMap map = new(17, 17, 8f, 0.01f, new ushort[17 * 17]);
        StaticGeometry geo = new(map, []);
        MatchSetup serverSetup = new(MatchSetup.CurrentProtocolVersion, 12345UL, MatchSetup.ComputeMapHash(geo), "UrbanDestruction");
        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        WorldState world = new(map);
        using Simulation sim = new(world, new RuleSet(new MovementSystem()));
        using SimulationHost host = new(sim, serverTransport, serverSetup);

        MatchSetup v31ClientSetup = serverSetup with { ProtocolVersion = 31 };
        using ClientSession client = ClientSession.Connect("127.0.0.1", serverTransport.Port, v31ClientSetup);
        for (int i = 0; i < 150 && client.State != SessionConnectionState.Failed; i++)
        {
            host.Advance(Simulation.TickSeconds);
            client.Advance(Simulation.TickSeconds);
            Thread.Sleep(5);
        }

        Assert.Equal(SessionConnectionState.Failed, client.State);
        Assert.Contains("ProtocolVersionMismatch", client.StatusDescription);
    }

    [Fact]
    public void LocalSession_SteadyStateWithDamagedStructures_AllocatesZeroBytesPerTick()
    {
        HeightMap map = new(33, 33, 8f, 0.01f, new ushort[33 * 33]);
        OpaqueStructure building = new(new StructureId(1), 100f, 0f, 100f, 112f, 15.5f, 112f, isGrounded: true);
        StaticGeometry geometry = new(map, [building]);
        WorldState world = new(map);

        BuildingDestructionSystem destruction = new(geometry);
        StructureDestructionProfile profile = new(
            FloorsTotal: 5,
            FloorHeight: 3.1f,
            RubbleHeight: 1.5f,
            MaxHealth: 400);
        Assert.True(destruction.RegisterStructure(world, building.Id, profile));
        Assert.True(destruction.ApplyDirectHit(world, 0, building.Id, new Vector3(100f, 4f, 106f), 180, out _, out _, out _));

        using Simulation sim = new(world, new RuleSet(new MovementSystem(), destruction));
        using LocalSession session = new(sim, player: 1);

        // Warmup snapshots and buffers
        for (int i = 0; i < 10; i++)
        {
            session.Advance(Simulation.TickSeconds);
        }

        long maxAlloc = 0;
        for (int i = 0; i < 20; i++)
        {
            session.Advance(Simulation.TickSeconds);
            if (session.LastTickAllocatedBytes > maxAlloc)
                maxAlloc = session.LastTickAllocatedBytes;
        }

        Assert.Equal(0, maxAlloc);
    }

    [Fact]
    public void MaxStructures256_FragmentsAndReassemblesOverLiteNetLib_AndProfileHashMismatchRejected()
    {
        // Build a map with 256 destructible structures (> MaxFragmentPayloadSize = 960 bytes)
        HeightMap map = new(65, 65, 16f, 0.01f, new ushort[65 * 65]);
        OpaqueStructure[] structs = new OpaqueStructure[WorldState.MaxDestructibleStructures];
        DestructibleStructureBinding[] bindings = new DestructibleStructureBinding[WorldState.MaxDestructibleStructures];
        StructureDestructionProfile profile = new(
            FloorsTotal: 5,
            FloorHeight: 3.1f,
            RubbleHeight: 1.5f,
            MaxHealth: 360);

        for (int i = 0; i < structs.Length; i++)
        {
            int col = i % 16;
            int row = i / 16;
            float x = 40f + col * 36f;
            float z = 40f + row * 36f;
            StructureId id = new(i + 1);
            structs[i] = new OpaqueStructure(id, x, 0f, z, x + 12f, 15.5f, z + 12f, isGrounded: true);
            bindings[i] = new DestructibleStructureBinding(id, profile);
        }

        StaticGeometry geometry = new(map, structs);
        WorldState world = new(map);
        BuildingDestructionSystem destruction = new(geometry);
        for (int i = 0; i < structs.Length; i++)
        {
            Assert.True(destruction.RegisterStructure(world, structs[i].Id, in profile));
            // Damage every single section so all 256 are replicated in the snapshot (256 * 8 = 2048+ bytes -> multi-fragment)
            Assert.True(destruction.ApplyDirectHit(world, 0, structs[i].Id, new Vector3(structs[i].MinX + 6f, 2f, structs[i].MinZ + 6f), 40, out _, out _, out _));
        }

        ulong mapHashWithProfiles = MatchSetup.ComputeMapHash(geometry, bindings);
        Assert.NotEqual(MatchSetup.ComputeMapHash(geometry), mapHashWithProfiles);

        // Changing a single building's RubbleHeight changes MapHash and triggers MapMismatch on handshake
        DestructibleStructureBinding[] mismatchedBindings = (DestructibleStructureBinding[])bindings.Clone();
        mismatchedBindings[0] = mismatchedBindings[0] with { Profile = profile with { RubbleHeight = 2.2f } };
        ulong mismatchedMapHash = MatchSetup.ComputeMapHash(geometry, mismatchedBindings);
        Assert.NotEqual(mapHashWithProfiles, mismatchedMapHash);

        MatchSetup serverSetup = new(MatchSetup.CurrentProtocolVersion, 99999UL, mapHashWithProfiles, "MaxStruct256");
        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        DetectionSystem detection = new(geometry, new ElevatedObserverPerceptionCatalog(), new DetectionTuning(evaluationIntervalTicks: 1));
        world.CreateUnit(1, 1, new Vector2(512f, 512f), 10f, 100, 0);
        using Simulation sim = new(world, new RuleSet(new MovementSystem(), detection, destruction));
        using SimulationHost host = new(sim, serverTransport, serverSetup);

        // 1. Mismatched building profile hash is rejected at handshake
        MatchSetup badProfileClientSetup = serverSetup with { MapHash = mismatchedMapHash };
        using (ClientSession badClient = ClientSession.Connect("127.0.0.1", serverTransport.Port, badProfileClientSetup))
        {
            for (int i = 0; i < 150 && badClient.State != SessionConnectionState.Failed; i++)
            {
                host.Advance(Simulation.TickSeconds);
                badClient.Advance(Simulation.TickSeconds);
                Thread.Sleep(4);
            }
            Assert.Equal(SessionConnectionState.Failed, badClient.State);
            Assert.Contains("MapMismatch", badClient.StatusDescription);
        }

        // 2. Valid client receives fragmented 256-structure snapshot over real LiteNetLib
        using ClientSession validClient = ClientSession.Connect("127.0.0.1", serverTransport.Port, serverSetup);
        for (int i = 0; i < 200 && !(validClient.IsConnected && validClient.Replica.StructureCount == 256); i++)
        {
            host.Advance(Simulation.TickSeconds);
            validClient.Advance(Simulation.TickSeconds);
            Thread.Sleep(4);
        }

        Assert.True(validClient.IsConnected);
        Assert.Equal(256, validClient.Replica.StructureCount);
        Assert.True(validClient.Replica.TryGetStructure(new StructureId(1), out StructureObservation first));
        Assert.True(validClient.Replica.TryGetStructure(new StructureId(256), out StructureObservation last));
        Assert.Equal((ushort)320, first.Health);
        Assert.Equal((ushort)320, last.Health);
    }

    private sealed class ElevatedObserverPerceptionCatalog : IUnitPerceptionCatalog
    {
        public bool TryGetProfile(ushort definition, out UnitPerceptionProfile profile)
        {
            profile = new UnitPerceptionProfile(new VisualSensorProfile(2000f, 180f), new TargetObservationPoints([2f]));
            return true;
        }
    }
}
