using System.Buffers.Binary;
using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using Xunit;

namespace Engine.Core.Tests;

public sealed class BuildingGarrisonNetworkTests
{
    [Fact]
    public void WireProtocolV33_RoundTripsEnterExitCommandsAndGarrisonObservations_AndRejectsMalformedAndV32Handshake()
    {
        Assert.Equal(33, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(33, WireProtocol.Version);
        Assert.Equal(78, WireProtocol.UnitSize);
        Assert.Equal(40, WireProtocol.ContactSize);
        Assert.Equal(19, WireProtocol.EnterBuildingSize);
        Assert.Equal(14, WireProtocol.ExitBuildingSize);
        Assert.Equal(20, WireProtocol.EnterBuildingKind);
        Assert.Equal(21, WireProtocol.ExitBuildingKind);

        // 1. EnterBuildingCommand round-trip (Interior & Roof)
        byte[] enterBuf = new byte[WireProtocol.EnterBuildingSize];
        EnterBuildingCommand enterInterior = new(42u, 100, new EntityId(7), new StructureId(101), GarrisonSlotKind.Interior);
        WireProtocol.WriteEnterBuilding(enterBuf, enterInterior);
        Assert.True(WireProtocol.TryReadEnterBuilding(enterBuf, out EnterBuildingCommand decodedInterior));
        Assert.Equal(enterInterior, decodedInterior);

        EnterBuildingCommand enterRoof = new(43u, 101, new EntityId(8), new StructureId(102), GarrisonSlotKind.Roof);
        WireProtocol.WriteEnterBuilding(enterBuf, enterRoof);
        Assert.True(WireProtocol.TryReadEnterBuilding(enterBuf, out EnterBuildingCommand decodedRoof));
        Assert.Equal(enterRoof, decodedRoof);

        // Reject invalid SlotKind (None = 0, out of range = 3), zero structureId, stale v32 version
        byte[] badEnter = (byte[])enterBuf.Clone();
        badEnter[18] = 0;
        Assert.False(WireProtocol.TryReadEnterBuilding(badEnter, out _));

        badEnter = (byte[])enterBuf.Clone();
        badEnter[18] = 3;
        Assert.False(WireProtocol.TryReadEnterBuilding(badEnter, out _));

        badEnter = (byte[])enterBuf.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(badEnter.AsSpan(14), 0);
        Assert.False(WireProtocol.TryReadEnterBuilding(badEnter, out _));

        badEnter = (byte[])enterBuf.Clone();
        badEnter[0] = 32;
        Assert.False(WireProtocol.TryReadEnterBuilding(badEnter, out _));

        // 2. ExitBuildingCommand round-trip and malformed rejection
        byte[] exitBuf = new byte[WireProtocol.ExitBuildingSize];
        ExitBuildingCommand exitCmd = new(44u, 105, new EntityId(7));
        WireProtocol.WriteExitBuilding(exitBuf, exitCmd);
        Assert.True(WireProtocol.TryReadExitBuilding(exitBuf, out ExitBuildingCommand decodedExit));
        Assert.Equal(exitCmd, decodedExit);

        byte[] badExit = (byte[])exitBuf.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badExit.AsSpan(10), 0u);
        Assert.False(WireProtocol.TryReadExitBuilding(badExit, out _));

        badExit = (byte[])exitBuf.Clone();
        badExit[0] = 32;
        Assert.False(WireProtocol.TryReadExitBuilding(badExit, out _));

        // 3. Snapshot OwnUnitObservation & ContactObservation garrison fields round-trip
        OwnUnitObservation[] ownUnits =
        [
            new OwnUnitObservation(
                Id: new EntityId(10),
                Definition: 0,
                Position: new Vector2(200f, 200f),
                SupportSurface: 0,
                Health: 100,
                GarrisonStructureId: new StructureId(101),
                GarrisonSlotKind: GarrisonSlotKind.Interior),
            new OwnUnitObservation(
                Id: new EntityId(11),
                Definition: 0,
                Position: new Vector2(214.4f, 200f),
                SupportSurface: 0,
                Health: 90,
                GarrisonStructureId: new StructureId(102),
                GarrisonSlotKind: GarrisonSlotKind.Roof),
            new OwnUnitObservation(
                Id: new EntityId(12),
                Definition: 1,
                Position: new Vector2(180f, 180f),
                SupportSurface: 0,
                Health: 150,
                GarrisonStructureId: default,
                GarrisonSlotKind: GarrisonSlotKind.None)
        ];

        ContactObservation[] contacts =
        [
            new ContactObservation(
                Id: new ContactId(501),
                State: ContactState.Current,
                Classification: ContactClassification.Infantry,
                Type: 0,
                Position: new Vector2(200f, 200f),
                LastSeenTick: 120,
                GarrisonStructureId: new StructureId(101),
                GarrisonSlotKind: GarrisonSlotKind.Interior),
            new ContactObservation(
                Id: new ContactId(502),
                State: ContactState.Current,
                Classification: ContactClassification.Infantry,
                Type: 0,
                Position: new Vector2(214.4f, 200f),
                LastSeenTick: 120,
                GarrisonStructureId: new StructureId(102),
                GarrisonSlotKind: GarrisonSlotKind.Roof)
        ];

        byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
        int len = WireProtocol.WriteSnapshot(snapBuf, sequence: 5u, tick: 120, units: ownUnits, contacts: contacts);

        ClientReplica replica = new();
        Assert.True(replica.TryApply(snapBuf.AsSpan(0, len)));
        Assert.Equal(3, replica.OwnUnits.Length);
        Assert.Equal(new StructureId(101), replica.OwnUnits[0].GarrisonStructureId);
        Assert.Equal(GarrisonSlotKind.Interior, replica.OwnUnits[0].GarrisonSlotKind);
        Assert.Equal(new StructureId(102), replica.OwnUnits[1].GarrisonStructureId);
        Assert.Equal(GarrisonSlotKind.Roof, replica.OwnUnits[1].GarrisonSlotKind);
        Assert.Equal(default, replica.OwnUnits[2].GarrisonStructureId);
        Assert.Equal(GarrisonSlotKind.None, replica.OwnUnits[2].GarrisonSlotKind);

        Assert.Equal(2, replica.ContactCount);
        Assert.Equal(new StructureId(101), replica.Contacts[0].GarrisonStructureId);
        Assert.Equal(GarrisonSlotKind.Interior, replica.Contacts[0].GarrisonSlotKind);
        Assert.Equal(new StructureId(102), replica.Contacts[1].GarrisonStructureId);
        Assert.Equal(GarrisonSlotKind.Roof, replica.Contacts[1].GarrisonSlotKind);

        // 4. Reject malformed snapshot garrison pairs (slotKind without structureId, structureId without slotKind, invalid enum)
        // Own unit 0 slot kind at offset SnapshotHeaderSize + 77
        byte[] malformed = snapBuf.AsSpan(0, len).ToArray();
        malformed[WireProtocol.SnapshotHeaderSize + 77] = 3; // invalid GarrisonSlotKind
        Assert.False(new ClientReplica().TryApply(malformed));

        malformed = snapBuf.AsSpan(0, len).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(WireProtocol.SnapshotHeaderSize + 73), 0); // structureId=0 with slotKind=Interior
        Assert.False(new ClientReplica().TryApply(malformed));

        malformed = snapBuf.AsSpan(0, len).ToArray();
        malformed[WireProtocol.SnapshotHeaderSize + 77] = 0; // slotKind=None with structureId=101
        Assert.False(new ClientReplica().TryApply(malformed));

        // Contact 0 slot kind at offset SnapshotHeaderSize + 3*UnitSize + 39
        int contact0Offset = WireProtocol.SnapshotHeaderSize + 3 * WireProtocol.UnitSize;
        malformed = snapBuf.AsSpan(0, len).ToArray();
        malformed[contact0Offset + 39] = 3;
        Assert.False(new ClientReplica().TryApply(malformed));

        malformed = snapBuf.AsSpan(0, len).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(contact0Offset + 35), 0);
        Assert.False(new ClientReplica().TryApply(malformed));

        // 5. Legacy v32 handshake is rejected with ProtocolVersionMismatch
        HeightMap map = new(17, 17, 4f, 0.01f, new ushort[17 * 17]);
        StaticGeometry geo = new(map, []);
        MatchSetup serverSetup = new(MatchSetup.CurrentProtocolVersion, 12345UL, MatchSetup.ComputeMapHash(geo), "GarrisonV33");
        MatchSetup v32ClientSetup = serverSetup with { ProtocolVersion = 32 };

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 1);
        using Simulation sim = new(new WorldState(map), new RuleSet(new MovementSystem()));
        using SimulationHost host = new(sim, serverTransport, serverSetup);
        using ClientSession client = ClientSession.Connect("127.0.0.1", serverTransport.Port, v32ClientSetup);

        for (int i = 0; i < 150 && client.State != SessionConnectionState.Failed; i++)
        {
            host.Advance(Simulation.TickSeconds);
            client.Advance(Simulation.TickSeconds);
            Thread.Sleep(5);
        }

        Assert.Equal(SessionConnectionState.Failed, client.State);
        Assert.Contains("ProtocolVersionMismatch", client.StatusDescription);
    }
}
