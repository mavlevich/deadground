using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class BuildingGarrisonNetworkTests
{
    private static void WaitFor(Func<bool> condition, SimulationHost host, params ClientSession[] clients)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            host.Advance(Simulation.TickSeconds);
            for (int c = 0; c < clients.Length; c++)
            {
                clients[c].Advance(Simulation.TickSeconds);
            }
            Thread.Sleep(2);
        }

        Assert.True(condition(), "Timed out waiting for expected garrison network condition.");
    }

    [Fact]
    public void ColdWarContent_PanelkaGarrisonProfile_LoadsAndRegistersAll12SectionsInPlayground()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        BuildingGarrisonProfile profile = catalog.RequireBuildingGarrisonProfile(UrbanDestructionPlaygroundScenario.PanelkaProfileId);

        Assert.True(profile.IsValid);
        Assert.Equal(12, profile.Capacity);
        Assert.Equal(12, profile.FiringPorts.Length);
        Assert.Equal(4, profile.RoofPorts.Length);
        Assert.Equal(0.35f, profile.CoverDamageMultiplier);
        Assert.Equal(0.55f, profile.RubbleCoverDamageMultiplier);
        Assert.Equal(0.70f, profile.RoofCoverDamageMultiplier);
        Assert.Equal(40.0f, profile.ConcealmentSpotRange);
        Assert.Equal(60, profile.FiringRevealTicks);

        using Simulation sim = UrbanDestructionPlaygroundScenario.CreateSimulation(catalog: catalog);
        Assert.NotNull(sim.Garrison);
        Assert.Equal(12, sim.Garrison!.RegisteredBuildingCount);

        // Verify central courtyard panelka 101 (Yaw = PI) has its entrance facing south (-Z) into the courtyard
        Assert.True(sim.Garrison.TryGetEntrancePosition(new StructureId(101), out Vector2 entrance101));
        Assert.True(entrance101.Y < 512.0f, $"Central panelka 101 entrance should face south into courtyard, got {entrance101}");
    }

    [Fact]
    public void Multiplayer_LiteNetLib_GarrisonEnterFireExit_StrictFogOfWarNoEntityIdLeak()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        HeightMap map = new(65, 65, 4f, 0.01f, new ushort[65 * 65]);
        StructureDestructionProfile panelkaDest = catalog.RequireBuildingProfile(UrbanDestructionPlaygroundScenario.PanelkaProfileId);

        // Single 5-story panelka centered at (128, 128), facing south (-Z, Yaw = PI)
        OpaqueStructure panelka = new(
            new StructureId(101),
            minX: 120.8f, minY: 0f, minZ: 122.1f,
            maxX: 135.2f, maxY: 13.75f, maxZ: 133.9f,
            isGrounded: true);
        DestructibleBuildingSpec[] specs = [new DestructibleBuildingSpec(panelka.Id, panelkaDest, MathF.PI)];
        StaticGeometry geometry = new(map, [panelka], [], [], [], [], destructibleBuildings: specs);

        OpsRules rules = new(geometry, catalog);
        WorldState world = new(map);

        UnitDefinition rifleDef = catalog.RequireDefinition("us_rifleman");
        ushort rifleIdx = catalog.RequireDefinitionIndex("us_rifleman");
        int rifleAmmo = catalog.GetAmmoCapacity(rifleDef);
        UnitDefinition brdmDef = catalog.RequireDefinition("brdm2");
        ushort brdmIdx = catalog.RequireDefinitionIndex("brdm2");

        // Compute world entrance (south courtyard of panelka 101)
        BuildingGarrisonProfile garrisonProfile = catalog.RequireBuildingGarrisonProfile(UrbanDestructionPlaygroundScenario.PanelkaProfileId);
        Vector2 entrance = GarrisonMath.ComputeWorldEntrance(panelka, MathF.PI, garrisonProfile);

        // Player 1 (Soviet) unarmed scout BRDM-2 + rifleman parked 85m south (> 40m ConcealmentSpotRange)
        EntityId p1Scout = world.CreateUnit(1, brdmIdx, new Vector2(128f, 43f), brdmDef.Speed, brdmDef.MaxHealth, 0);
        EntityId p1Infantry = world.CreateUnit(1, rifleIdx, new Vector2(120f, 43f), rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo);
        world.SetFireStance(p1Infantry, FireStance.HoldFire);

        // Player 2 (US) rifleman starts directly at the panelka entrance on HoldFire
        EntityId p2Defender = world.CreateUnit(2, rifleIdx, entrance, rifleDef.Speed, rifleDef.MaxHealth, rifleAmmo);
        world.SetFireStance(p2Defender, FireStance.HoldFire);

        DestructibleStructureBinding[] bindings = [new DestructibleStructureBinding(panelka.Id, panelkaDest)];
        MatchSetup setup = new(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry, bindings),
            "GarrisonNetTest",
            PlayerFactions: ["soviet", "us"]);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        using Simulation simulation = new(world, rules, matchSeed: 2026UL);
        Assert.NotNull(simulation.BuildingDestruction);
        Assert.True(simulation.BuildingDestruction!.RegisterStructure(world, panelka.Id, in panelkaDest));
        using SimulationHost host = new(simulation, serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", serverTransport.Port, setup);
        WaitFor(() => client1.IsConnected && client1.Replica.HasSnapshots, host, client1);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", serverTransport.Port, setup);
        WaitFor(() => client2.IsConnected && client2.Replica.HasSnapshots, host, client1, client2);

        // 1. Player 2 orders defender into Panelka 101 Interior over LiteNetLib
        Assert.True(client2.EnterBuilding(p2Defender, panelka.Id, GarrisonSlotKind.Interior));
        WaitFor(
            () => client2.Replica.TryGetOwnUnit(p2Defender, out OwnUnitObservation own) &&
                  own.GarrisonStructureId == panelka.Id &&
                  own.GarrisonSlotKind == GarrisonSlotKind.Interior,
            host,
            client1,
            client2);

        // Advance several snapshots and verify Player 1 NEVER receives a Current contact for the concealed defender
        for (int i = 0; i < 15; i++)
        {
            host.Advance(Simulation.TickSeconds);
            client1.Advance(Simulation.TickSeconds);
            client2.Advance(Simulation.TickSeconds);
            Thread.Sleep(2);
        }

        // Strict FoW check: Player 1 sees NO current contact inside the building (> 40m, HoldFire)
        foreach (ContactObservation c in client1.Replica.Contacts)
        {
            Assert.NotEqual(ContactState.Current, c.State);
            Assert.Equal(default, c.GarrisonStructureId);
        }

        // Strict anti-oracle FoW check: Player 1 probing the building with EnterBuilding from 85m away
        // is NOT rejected at enqueue time (would leak hidden enemy occupancy)
        Assert.True(client1.EnterBuilding(p1Infantry, panelka.Id, GarrisonSlotKind.Interior));
        WaitFor(() => client1.LastCommandResult is { Queued: true }, host, client1, client2);
        // Immediately stop p1Infantry so it stays at 85m
        Assert.True(client1.Stop(p1Infantry));
        WaitFor(() => client1.LastCommandResult is { Sequence: 2u, Queued: true }, host, client1, client2);

        // 2. Player 2 switches defender to FireAtWill -> defender fires from elevated window at p1Infantry,
        //    breaking FoW concealment and replicating a ContactObservation with GarrisonStructureId & SlotKind
        Assert.True(client2.SetFireStance(p2Defender, FireStance.FireAtWill));
        WaitFor(
            () => HasCurrentGarrisonContact(client1.Replica, panelka.Id, GarrisonSlotKind.Interior),
            host,
            client1,
            client2);

        // Verify ContactObservation replicates garrison location with ZERO enemy EntityId exposure
        Assert.Null(typeof(ContactObservation).GetProperty("EntityId"));
        Assert.Null(typeof(ContactObservation).GetProperty("UnitId"));
        bool foundGarrisonContact = false;
        foreach (ContactObservation c in client1.Replica.Contacts)
        {
            if (c.State == ContactState.Current && c.GarrisonStructureId == panelka.Id)
            {
                foundGarrisonContact = true;
                Assert.Equal(GarrisonSlotKind.Interior, c.GarrisonSlotKind);
                Assert.Equal(ContactClassification.Infantry, c.Classification);
            }
        }
        Assert.True(foundGarrisonContact);

        // Verify no combat event delivered to Player 1 leaks Player 2's EntityId
        foreach (CombatEventObservation ev in client1.Replica.Events)
        {
            Assert.NotEqual(p2Defender, ev.ShooterUnitId);
        }

        // 3. Player 2 orders ExitBuilding over LiteNetLib -> unit leaves building and clears garrison state
        Assert.True(client2.ExitBuilding(p2Defender));
        WaitFor(
            () => client2.Replica.TryGetOwnUnit(p2Defender, out OwnUnitObservation own) &&
                  own.GarrisonStructureId == default &&
                  own.GarrisonSlotKind == GarrisonSlotKind.None,
            host,
            client1,
            client2);

        _ = p1Scout;
    }

    private static bool HasCurrentGarrisonContact(ClientReplica replica, StructureId structureId, GarrisonSlotKind slotKind)
    {
        ReadOnlySpan<ContactObservation> contacts = replica.Contacts;
        for (int i = 0; i < contacts.Length; i++)
        {
            if (contacts[i].State == ContactState.Current &&
                contacts[i].GarrisonStructureId == structureId &&
                contacts[i].GarrisonSlotKind == slotKind)
            {
                return true;
            }
        }
        return false;
    }
}
