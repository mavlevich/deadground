using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class VisibilityNetworkTests
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
        Assert.True(condition());
    }

    [Fact]
    public void TwoClientsConnectOverLoopback_VisibilityScenario_ConnectsAndReplicatesContacts()
    {
        StaticGeometry geometry = VisibilityScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        MatchSetup setup = VisibilityScenario.CreateMatchSetup(geometry);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(VisibilityScenario.CreateSimulation(geometry, catalog), serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        // Wait for connection and initial snapshots
        WaitFor(() => client1.IsConnected && client2.IsConnected &&
            client1.Replica.OwnUnits.Length == 2 && client2.Replica.OwnUnits.Length == 3, host, client1, client2);

        Assert.Equal(1, client1.AssignedPlayerId);
        Assert.Equal(2, client2.AssignedPlayerId);

        // Side 1 has 2 recon units, Side 2 has 3 units
        Assert.Equal(2, client1.Replica.OwnUnits.Length);
        Assert.Equal(3, client2.Replica.OwnUnits.Length);

        // Advance simulation so detection schedule executes (evaluationIntervalTicks = 2)
        Pump(host, client1, client2, iterations: 10, delayMs: 5);

        // Client 1 should observe Enemy 1 (behind low wall) over the 1.0m wall via upper observation point
        // while Enemy 2 (behind 6m building) and Enemy 3 (behind 8m hill) remain unobserved.
        WaitFor(() => client1.Replica.ContactCount >= 1, host, client1, client2);

        Assert.Equal(1, client1.Replica.ContactCount);
        ContactObservation c1Contact = client1.Replica.Contacts[0];
        Assert.Equal(ContactState.Current, c1Contact.State);
        Assert.Equal(ContactClassification.Unknown, c1Contact.Classification);

        // The observed contact position matches enemy 1 around (33, 27)
        Assert.InRange(c1Contact.Position.X, 32f, 34f);
        Assert.InRange(c1Contact.Position.Y, 26f, 28f);

        // Verify Client 1 cannot command enemy contact or enemy entity
        EntityId enemyUnit = client2.Replica.OwnUnits[0].Id;
        Assert.True(client1.Move(enemyUnit, new Vector2(50, 50)));
        WaitFor(() => client1.LastCommandResult is { Sequence: 1 }, host, client1, client2);
        Assert.False(client1.LastCommandResult!.Value.Queued);
        Assert.Equal(1L, client1.RejectedCommands);
    }

    [Fact]
    public void ReplicatedContact_FullLifecycle_FromCurrentToLastKnownToRediscoveryAndExpiry()
    {
        StaticGeometry geometry = VisibilityScenario.CreateStaticGeometry();
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        MatchSetup setup = VisibilityScenario.CreateMatchSetup(geometry);

        // Tuning with 30-tick memory duration and 50-tick correlation window
        DetectionTuning lifecycleTuning = new(
            memoryDurationTicks: 30,
            correlationWindowTicks: 50,
            correlationDistanceGate: 30.0f,
            evaluationIntervalTicks: 2);
        OpsRules rules = new(geometry, new OpsPerceptionCatalog(catalog), lifecycleTuning);
        WorldState world = new(geometry.HeightMap);
        world.CreateUnit(1, 1, new Vector2(20f, 25f), catalog.Units[1].Speed); // Observer 1 (Recon)
        EntityId enemy = world.CreateUnit(2, 2, new Vector2(60f, 25f), 10.0f); // Enemy tank in open view at (60, 25)

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        using SimulationHost host = new(new Simulation(world, rules), serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);

        WaitFor(() => client1.IsConnected && client2.IsConnected && client1.Replica.OwnUnits.Length == 1,
            host, client1, client2);

        // 1. Initial discovery: client 1 receives Current contact
        WaitFor(() => client1.Replica.ContactCount == 1 && client1.Replica.Contacts[0].State == ContactState.Current,
            host, client1, client2);

        ContactId contactId = client1.Replica.Contacts[0].Id;
        Assert.Equal(ContactState.Current, client1.Replica.Contacts[0].State);

        // 2. Move enemy behind the 6m building to (60, 45) -> becomes LastKnown
        Assert.True(client2.Move(enemy, new Vector2(60f, 45f)));

        WaitFor(() => client1.Replica.ContactCount == 1 && client1.Replica.Contacts[0].State == ContactState.LastKnown,
            host, client1, client2, maxAttempts: 200);

        ContactObservation lastKnown = client1.Replica.Contacts[0];
        Assert.Equal(contactId, lastKnown.Id);
        Assert.Equal(ContactState.LastKnown, lastKnown.State);

        // 3. Move enemy back into open view at (60, 25) -> rediscovery correlates to the identical ContactId
        Assert.True(client2.Move(enemy, new Vector2(60f, 25f)));

        WaitFor(() => client1.Replica.ContactCount == 1 && client1.Replica.Contacts[0].State == ContactState.Current,
            host, client1, client2, maxAttempts: 200);

        ContactObservation rediscovered = client1.Replica.Contacts[0];
        Assert.Equal(contactId, rediscovered.Id);
        Assert.Equal(ContactState.Current, rediscovered.State);

        // 4. Move enemy behind the building again to (60, 45) -> becomes LastKnown then expires
        Assert.True(client2.Move(enemy, new Vector2(60f, 45f)));

        WaitFor(() => client1.Replica.ContactCount == 1 && client1.Replica.Contacts[0].State == ContactState.LastKnown,
            host, client1, client2, maxAttempts: 200);

        // Wait until memory duration (30 ticks) expires and contact is removed
        WaitFor(() => client1.Replica.ContactCount == 0, host, client1, client2, maxAttempts: 200);

        Assert.Equal(0, client1.Replica.ContactCount);
        Assert.False(client1.Replica.TryGetContact(contactId, out _));
    }
}
