using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class BuildingDestructionNetworkTests
{
    private static void WaitFor(Func<bool> condition, SimulationHost host, params ClientSession[] clients)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            host.Advance(Simulation.TickSeconds);
            for (int c = 0; c < clients.Length; c++)
            {
                clients[c].Advance(Simulation.TickSeconds);
            }
            Thread.Sleep(2);
        }

        Assert.True(condition(), "Timed out waiting for expected network condition.");
    }

    [Fact]
    public void NetworkAttackGround_ReplicatesStructureCollapseToObserver_AndHidesUnobservedDestructionInFog()
    {
        HeightMap map = new(101, 101, 4f, 0.01f, new ushort[101 * 101]);
        // Tall opaque dividing wall at X=245..255, Z=40..360 separating Player 1 + building (West) from Player 2 (East)
        OpaqueStructure wall = new(new StructureId(1), minX: 245f, minY: 0f, minZ: 40f, maxX: 255f, maxY: 25f, maxZ: 360f, isGrounded: true);
        OpaqueStructure panelka = new(new StructureId(2), minX: 194f, minY: 0f, minZ: 194f, maxX: 206f, maxY: 15.5f, maxZ: 206f, isGrounded: true);
        StaticGeometry geometry = new(map, [wall, panelka]);

        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(map);

        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");

        // Player 1 T-62 faces the panelka from the West; Player 2 M60A1 sits behind the tall wall on the East
        EntityId t62 = world.CreateUnit(1, t62Idx, new Vector2(120f, 200f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def));
        EntityId m60 = world.CreateUnit(2, m60Idx, new Vector2(310f, 200f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def));

        MatchSetup setup = new(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            "UrbanDestructionNetTest",
            PlayerFactions: ["soviet", "us"]);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        using Simulation simulation = new(world, rules, matchSeed: 901);
        StructureDestructionProfile profile = new(
            FloorsTotal: 5,
            FloorHeight: 3.1f,
            RubbleHeight: 1.5f,
            MaxHealth: 180,
            StructuralArmor: 0,
            DustCloudRadius: 16f,
            DustCloudHeight: 14f,
            DustCloudDurationTicks: 180);
        Assert.NotNull(simulation.BuildingDestruction);
        Assert.True(simulation.BuildingDestruction!.RegisterStructure(world, panelka.Id, profile));
        using SimulationHost host = new(simulation, serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", serverTransport.Port, setup);
        WaitFor(() => client1.IsConnected && client1.Replica.HasSnapshots, host, client1);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", serverTransport.Port, setup);
        WaitFor(() => client2.IsConnected && client2.Replica.HasSnapshots, host, client1, client2);

        // Initially intact: neither client receives any StructureObservation records
        Assert.Equal(0, client1.Replica.StructureCount);
        Assert.Equal(0, client2.Replica.StructureCount);

        // Player 1 issues AttackGround against the ground floor of the panelka over the wire
        Assert.True(client1.AttackGround(t62, new Vector3(194f, 1.5f, 200f), panelka.Id));
        WaitFor(() => client1.LastCommandResult is { Queued: true }, host, client1, client2);

        // Wait until the building collapses on Player 1's replica
        WaitFor(
            () => client1.Replica.TryGetStructure(panelka.Id, out StructureObservation obs) && obs.FloorsStanding == 0,
            host,
            client1,
            client2);

        Assert.True(client1.Replica.TryGetStructure(panelka.Id, out StructureObservation p1Collapsed));
        Assert.Equal((byte)0, p1Collapsed.FloorsStanding);
        Assert.Equal((ushort)0, p1Collapsed.Health);

        bool p1SawCollapseEvent = false;
        foreach (CombatEventObservation ev in client1.Replica.Events)
        {
            if (ev.Kind == CombatEventKind.StructureCollapsed)
            {
                p1SawCollapseEvent = true;
                break;
            }
        }
        Assert.True(p1SawCollapseEvent, "Attacker client must receive StructureCollapsed combat event.");

        // Pump extra snapshots to ensure Player 2 would have received any leaked state
        for (int i = 0; i < 20; i++)
        {
            host.Advance(Simulation.TickSeconds);
            client1.Advance(Simulation.TickSeconds);
            client2.Advance(Simulation.TickSeconds);
            Thread.Sleep(2);
        }

        // Player 2 behind the wall MUST NOT see the damaged/collapsed building or any structure events
        Assert.Equal(0, client2.Replica.StructureCount);
        Assert.False(client2.Replica.TryGetStructure(panelka.Id, out _));
        foreach (CombatEventObservation ev in client2.Replica.Events)
        {
            Assert.NotEqual(CombatEventKind.StructureDamaged, ev.Kind);
            Assert.NotEqual(CombatEventKind.StructureFloorCollapsed, ev.Kind);
            Assert.NotEqual(CombatEventKind.StructureCollapsed, ev.Kind);
        }

        // Move Player 2 observer around the wall edge (Z=20) with clear LOS to the collapsed panelka
        world.SetPosition(m60, new Vector2(200f, 20f));
        WaitFor(
            () => client2.Replica.TryGetStructure(panelka.Id, out StructureObservation obs) && obs.FloorsStanding == 0,
            host,
            client1,
            client2);

        Assert.True(client2.Replica.TryGetStructure(panelka.Id, out StructureObservation p2Observed));
        Assert.Equal((byte)0, p2Observed.FloorsStanding);
        Assert.Equal((ushort)0, p2Observed.Health);
    }
}
