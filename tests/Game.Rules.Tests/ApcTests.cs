using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

// The BTR-70 was first mapped onto the recon unit; it is an armored personnel carrier.
public sealed class ApcTests
{
    [Fact]
    public void ApcDefinition_IsClassifiedAsApc()
    {
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        OpsPerceptionCatalog perception = new(catalog);

        ushort apc = catalog.RequireDefinitionIndex("apc");
        ushort recon = catalog.RequireDefinitionIndex("recon_vehicle");

        Assert.True(perception.TryGetProfile(apc, out UnitPerceptionProfile apcProfile));
        Assert.Equal(ContactClassification.Apc, apcProfile.Classification);
        Assert.True(perception.TryGetProfile(recon, out UnitPerceptionProfile reconProfile));
        Assert.Equal(ContactClassification.Recon, reconProfile.Classification);
        Assert.Null(catalog.RequireDefinition("apc").Weapon);
    }

    [Fact]
    public void ApcContact_RoundTripsOverTheWire()
    {
        ClientReplica replica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];
        ContactObservation contact = new(new ContactId(7), ContactState.Current, ContactClassification.Apc, 42, new Vector2(40f, 50f), 3);

        int size = WireProtocol.WriteSnapshot(bytes, 1, 3, [], [contact], []);

        Assert.True(replica.TryApply(bytes.AsSpan(0, size)));
        Assert.True(replica.TryGetContact(new ContactId(7), out ContactObservation received));
        Assert.Equal(ContactClassification.Apc, received.Classification);
        Assert.Equal(42, received.Type);
    }

    [Fact]
    public void UnknownContactClassification_IsRejected()
    {
        ClientReplica replica = new();
        byte[] bytes = new byte[WireProtocol.MaxMessageSize];
        ContactObservation contact = new(new ContactId(7), ContactState.Current, ContactClassification.Apc, 0, new Vector2(40f, 50f), 3);

        int size = WireProtocol.WriteSnapshot(bytes, 1, 3, [], [contact], []);
        bytes[WireProtocol.SnapshotHeaderSize + 5] = (byte)ContactClassification.Apc + 1;

        Assert.False(replica.TryApply(bytes.AsSpan(0, size)));
    }

    [Fact]
    public void CombatScenario_HasOneUnarmedApcPerSide()
    {
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        ushort apc = catalog.RequireDefinitionIndex("apc");
        using Simulation sim = CombatScenario.CreateSimulation(CombatScenario.CreateStaticGeometry(), catalog);

        int side1 = 0, side2 = 0;
        for (int i = 0; i < sim.World.Count; i++)
        {
            UnitState unit = sim.World.GetUnitAt(i);
            if (unit.Definition != apc) continue;
            Assert.True(sim.World.TryGetCombatState(unit.Id, out WorldState.CombatState combat));
            Assert.Equal(0, combat.Ammunition);
            if (unit.Owner == 1) side1++;
            if (unit.Owner == 2) side2++;
        }

        Assert.Equal(1, side1);
        Assert.Equal(1, side2);
    }
}
