using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class SmokeNetworkTests
{
    private static void WaitFor(Func<bool> condition, SimulationHost host, ClientSession c1, int maxAttempts = 200)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
        {
            host.Advance(1d / Simulation.TickRate);
            c1.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }
        Assert.True(condition());
    }

    private static void WaitFor(Func<bool> condition, SimulationHost host, ClientSession c1, ClientSession c2, int maxAttempts = 200)
    {
        for (int i = 0; i < maxAttempts && !condition(); i++)
        {
            host.Advance(1d / Simulation.TickRate);
            c1.Advance(1d / Simulation.TickRate);
            c2.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }
        Assert.True(condition());
    }

    [Fact]
    public void ColdWarCatalog_T62HasHistoricalTdaSmoke_EarlyM60A1HasNone_AndValidationRejectsInvalidSmoke()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        UnitDefinition t62 = catalog.RequireDefinition("t62");
        UnitDefinition m60a1 = catalog.RequireDefinition("m60a1");

        Assert.NotNull(t62.Smoke);
        Assert.Equal("engine_screen", t62.Smoke.Kind);
        Assert.Equal(2, t62.Smoke.Charges);
        Assert.Equal(16.0f, t62.Smoke.CloudRadius);
        Assert.Equal(7.0f, t62.Smoke.CloudHeight);
        Assert.Equal(105.0f, t62.Smoke.CloudDurationSeconds);
        Assert.Null(m60a1.Smoke);

        OpsSmokeCatalog smokeCatalog = new(catalog);
        Assert.True(smokeCatalog.TryGetSmokeProfile(catalog.RequireDefinitionIndex("t62"), out SmokeProfile t62Profile));
        Assert.True(t62Profile.IsValid);
        Assert.Equal(SmokeDeliveryKind.EngineScreen, t62Profile.Kind);
        Assert.False(smokeCatalog.TryGetSmokeProfile(catalog.RequireDefinitionIndex("m60a1"), out _));

        // Invalid smoke kind must be rejected by catalog validation
        const string invalidKindJson = """
        {
          "schema_version": 1,
          "units": [
            {
              "id": "bad_smoke_tank",
              "speed": 8.0,
              "sensor_range": 500.0,
              "eye_height": 2.3,
              "target_points": [0.9, 2.2],
              "radius": 1.6,
              "max_health": 100,
              "smoke": {
                "kind": "orbital_fog",
                "charges": 2
              }
            }
          ]
        }
        """;
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(invalidKindJson));

        // Bloom + fade exceeding cloud duration must be rejected
        const string invalidDurationJson = """
        {
          "schema_version": 1,
          "units": [
            {
              "id": "bad_duration_tank",
              "speed": 8.0,
              "sensor_range": 500.0,
              "eye_height": 2.3,
              "target_points": [0.9, 2.2],
              "radius": 1.6,
              "max_health": 100,
              "smoke": {
                "kind": "engine_screen",
                "charges": 2,
                "emit_duration_seconds": 10.0,
                "emit_interval_seconds": 1.0,
                "cloud_duration_seconds": 10.0,
                "bloom_seconds": 6.0,
                "fade_seconds": 6.0
              }
            }
          ]
        }
        """;
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(invalidDurationJson));
    }

    [Fact]
    public void T62SmokeScreen_BlocksDetectionBothWays_PreventsEmptyLastKnownConfirm_AndBlocksDirectFire()
    {
        HeightMap map = new(101, 101, 4f, 0.01f, new ushort[101 * 101]);
        StaticGeometry geometry = new(map, []);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(map);

        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");

        // T-62 faces West (-X) so its rear exhaust (+X) faces the M60A1 to the East (+X)
        float westYaw = MathF.PI / 2f;
        float eastYaw = -MathF.PI / 2f;
        EntityId t62 = world.CreateUnit(1, t62Idx, new Vector2(120f, 200f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def), westYaw);
        EntityId m60 = world.CreateUnit(2, m60Idx, new Vector2(240f, 200f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def), eastYaw);

        using Simulation sim = new(world, rules);
        // Set HoldFire initially so neither tank destroys the other during detection setup
        Assert.True(sim.TryEnqueue(1, new SetFireStanceCommand(1, 0, t62, FireStance.HoldFire)));
        Assert.True(sim.TryEnqueue(2, new SetFireStanceCommand(1, 0, m60, FireStance.HoldFire)));

        for (int t = 0; t < 10; t++) sim.Step();

        Assert.NotNull(sim.Detection);
        Assert.True(sim.Detection.IsEntityObservedBy(2, t62), "M60A1 must initially see T-62 in open ground.");
        Assert.True(sim.Detection.IsEntityObservedBy(1, m60), "T-62 must initially see M60A1 in open ground.");

        // T-62 deploys smoke screen and drives away behind it
        Assert.True(sim.TryEnqueue(1, new DeploySmokeCommand(2, sim.Tick, t62)));
        Assert.True(sim.TryEnqueue(1, new MoveCommand(3, sim.Tick, t62, new Vector2(85f, 200f))));

        // Advance past 3s bloom (60 ticks) while T-62 pulls behind its smoke screen
        for (int t = 0; t < 80; t++) sim.Step();

        Assert.True(world.SmokeCloudCount > 0, "T-62 must have emitted smoke clouds.");

        // Both-way sight blocking: M60A1 loses Current lock on T-62, and T-62 cannot see M60A1 through its own smoke
        Assert.False(sim.Detection.IsEntityObservedBy(2, t62), "Opaque smoke screen must block M60A1 sight to T-62.");
        Assert.False(sim.Detection.IsEntityObservedBy(1, m60), "Smoke screen must block sight both ways (T-62 also blinded through smoke).");

        // M60A1 still holds LastKnown contact (not falsely confirmed empty through smoke!) before memory timeout
        ReadOnlySpan<ContactObservation> p2Contacts = sim.Detection.GetContacts(2);
        Assert.True(p2Contacts.Length == 1 && p2Contacts[0].State == ContactState.LastKnown,
            "Contact behind smoke must transition to LastKnown instead of being confirmed empty through smoke.");

        // Direct fire through smoke must be blocked (CombatSystem.HasLineOfSightToTarget returns false)
        Assert.NotNull(sim.Combat);
        Assert.True(world.TryGetUnit(m60, out UnitState m60State));
        Assert.True(world.TryGetUnit(t62, out UnitState t62State));
        OpsCombatCatalog combatCat = new(catalog);
        Assert.True(combatCat.TryGetProfile(m60Idx, out UnitCombatProfile m60Combat));
        Assert.NotNull(m60Combat.Weapon);
        Assert.False(sim.Combat.HasLineOfSightToTarget(m60State, m60Combat.Weapon, t62State, out _, out _),
            "Direct fire LOS check must be obstructed by tactical smoke.");

        // Advance past full smoke cloud lifetime (~125 seconds = 2500 ticks) -> smoke expires and both sides re-acquire sight
        for (int t = 0; t < 2600; t++) sim.Step();

        Assert.Equal(0, world.SmokeCloudCount);
        Assert.True(sim.Detection.IsEntityObservedBy(2, t62), "Sight must return once tactical smoke dissipates.");
        Assert.True(sim.Detection.IsEntityObservedBy(1, m60), "Both-way sight must return once tactical smoke dissipates.");
    }

    [Fact]
    public void NetworkSnapshot_FogOfWarHidesOccludedEnemySmoke_AndRevealsVisibleSmokeWithoutLeakingEntityId()
    {
        HeightMap map = new(101, 101, 4f, 0.01f, new ushort[101 * 101]);
        // Tall opaque wall at X=195..205, Z=0..400, height 20m completely separating West and East halves
        OpaqueStructure wall = new(new StructureId(1), minX: 195f, minY: 0f, minZ: 50f, maxX: 205f, maxY: 20f, maxZ: 350f, isGrounded: true);
        StaticGeometry geometry = new(map, [wall]);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(map);

        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");

        // Player 1 T-62 on West side behind wall; Player 2 M60A1 on East side behind wall
        EntityId t62 = world.CreateUnit(1, t62Idx, new Vector2(150f, 200f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def));
        EntityId m60 = world.CreateUnit(2, m60Idx, new Vector2(250f, 200f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def));

        MatchSetup setup = new(
            MatchSetup.CurrentProtocolVersion,
            catalog.ContentHash,
            MatchSetup.ComputeMapHash(geometry),
            "SmokeFogTest",
            PlayerFactions: ["soviet", "us"]);

        using LiteNetLibServerTransport serverTransport = LiteNetLibTransport.CreateServer(0, maxPlayers: 2);
        int port = serverTransport.Port;
        Simulation simulation = new(world, rules);
        using SimulationHost host = new(simulation, serverTransport, setup);

        using ClientSession client1 = ClientSession.Connect("127.0.0.1", port, setup);
        WaitFor(() => client1.IsConnected && client1.Replica.HasSnapshots, host, client1);
        using ClientSession client2 = ClientSession.Connect("127.0.0.1", port, setup);
        WaitFor(() => client2.IsConnected && client2.Replica.HasSnapshots, host, client1, client2);

        Assert.Equal(1, client1.AssignedPlayerId);
        Assert.Equal(2, client2.AssignedPlayerId);

        // Verify initial own-unit smoke capability replicated to client 1, and M60A1 has no smoke on client 2
        Assert.True(client1.Replica.TryGetOwnUnit(t62, out OwnUnitObservation t62Obs0));
        Assert.True(t62Obs0.HasSmokeCapability);
        Assert.Equal((byte)2, t62Obs0.SmokeCharges);
        Assert.False(t62Obs0.SmokeActive);

        Assert.True(client2.Replica.TryGetOwnUnit(m60, out OwnUnitObservation m60Obs0));
        Assert.False(m60Obs0.HasSmokeCapability);
        Assert.Equal((byte)0, m60Obs0.SmokeCharges);

        // Player 1 deploys smoke behind the opaque wall
        Assert.True(client1.DeploySmoke(t62));
        WaitFor(() => client1.Replica.SmokeCloudCount > 0, host, client1, client2);

        // Pump extra snapshots to ensure client 2 receives latest server state
        for (int i = 0; i < 15; i++)
        {
            host.Advance(1d / Simulation.TickRate);
            client1.Advance(1d / Simulation.TickRate);
            client2.Advance(1d / Simulation.TickRate);
            Thread.Sleep(5);
        }

        // Player 1 sees its own smoke and updated unit smoke state; Player 2 behind wall sees ZERO smoke and ZERO contacts
        Assert.True(client1.Replica.SmokeCloudCount >= 1);
        Assert.True(client1.Replica.TryGetOwnUnit(t62, out OwnUnitObservation t62Obs1));
        Assert.Equal((byte)1, t62Obs1.SmokeCharges);
        Assert.True(t62Obs1.SmokeActive);

        Assert.Equal(0, client2.Replica.SmokeCloudCount);
        Assert.Equal(0, client2.Replica.ContactCount);
        Assert.False(client2.Replica.TryGetOwnUnit(t62, out _));

        // Now move Player 2 observer to a clear vantage point past the wall edge (Z=20) with clear LOS to (150, 200)
        world.SetPosition(m60, new Vector2(150f, 20f));
        WaitFor(() => client2.Replica.SmokeCloudCount > 0, host, client1, client2);

        Assert.True(client2.Replica.SmokeCloudCount >= 1);
        Assert.False(client2.Replica.TryGetOwnUnit(t62, out _));
        // SmokeCloudObservation has its own SmokeCloudId, never exposing unit EntityId or owner
        SmokeCloudObservation observedCloud = client2.Replica.SmokeClouds[0];
        Assert.True(observedCloud.Id.Value > 0);
        Assert.Equal(16.0f, observedCloud.Radius);
        Assert.Equal(7.0f, observedCloud.Height);
    }

    [Fact]
    public void WireProtocol_DeploySmokeAndSmokeCloudSnapshot_RoundTripAndRejectInvalid()
    {
        Assert.Equal(28, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(28, WireProtocol.Version);

        byte[] cmdBuf = new byte[WireProtocol.DeploySmokeSize];
        DeploySmokeCommand cmd = new(77, 120, new EntityId(9), HasTarget: true, Target: new Vector2(210.5f, 340.25f), TargetSurface: 2);
        WireProtocol.WriteDeploySmoke(cmdBuf, cmd);
        Assert.True(WireProtocol.TryReadDeploySmoke(cmdBuf, out DeploySmokeCommand decodedCmd));
        Assert.Equal(cmd, decodedCmd);

        // Malformed HasTarget byte rejected
        cmdBuf[14] = 2;
        Assert.False(WireProtocol.TryReadDeploySmoke(cmdBuf, out _));

        // Snapshot round-trip with SmokeCloudObservation and OwnUnitObservation smoke fields
        OwnUnitObservation[] units =
        [
            new OwnUnitObservation(
                new EntityId(3),
                0,
                new Vector2(100f, 200f),
                0,
                SmokeCharges: 1,
                SmokeCooldownTicks: 420,
                HasSmokeCapability: true,
                SmokeActive: true)
        ];
        SmokeCloudObservation[] clouds =
        [
            new SmokeCloudObservation(
                new SmokeCloudId(5),
                new Vector2(96.5f, 200f),
                0,
                16.0f,
                7.0f,
                100,
                2200,
                60,
                300,
                0.25f)
        ];

        byte[] snapBuf = new byte[WireProtocol.MaxMessageSize];
        int written = WireProtocol.WriteSnapshot(snapBuf, 1, 120, units, smokeClouds: clouds);
        ClientReplica replica = new();
        Assert.True(replica.TryApply(snapBuf.AsSpan(0, written)));

        Assert.Equal(1, replica.OwnUnits.Length);
        Assert.True(replica.OwnUnits[0].HasSmokeCapability);
        Assert.True(replica.OwnUnits[0].SmokeActive);
        Assert.Equal((byte)1, replica.OwnUnits[0].SmokeCharges);
        Assert.Equal((ushort)420, replica.OwnUnits[0].SmokeCooldownTicks);

        Assert.Equal(1, replica.SmokeCloudCount);
        Assert.True(replica.TryGetSmokeCloud(new SmokeCloudId(5), out SmokeCloudObservation readCloud));
        Assert.Equal(clouds[0], readCloud);
    }

    [Fact]
    public void AllPlayableColdWarScenarios_WireSmokeSystemForT62Units()
    {
        static void AssertHasReadyT62Smoke(Simulation sim, string scenarioName)
        {
            Assert.True(sim.Smoke is not null, $"{scenarioName} must register SmokeSystem.");
            PlayerKnowledge knowledge = new(1, sim.Capacity);
            sim.UpdateKnowledge(knowledge);
            int smokeCapable = 0;
            foreach (OwnUnitObservation unit in knowledge.OwnUnits)
            {
                if (unit.HasSmokeCapability)
                {
                    smokeCapable++;
                    Assert.Equal((byte)2, unit.SmokeCharges);
                    Assert.False(unit.SmokeActive);
                }
            }
            Assert.True(smokeCapable > 0, $"{scenarioName} must expose at least one T-62 with HasSmokeCapability=true on tick 0.");
        }

        using (Simulation battle = BattleScenario.CreateSimulation())
            AssertHasReadyT62Smoke(battle, "BattleScenario");

        using (Simulation ustyeRaid = UstyeRaidScenario.CreateSimulation())
            AssertHasReadyT62Smoke(ustyeRaid, "UstyeRaidScenario");

        using (Simulation tankBattle = TankBattleScenario.CreateSimulation())
            AssertHasReadyT62Smoke(tankBattle, "TankBattleScenario");
    }

    [Fact]
    public void StationaryT62_OverlappingRefreshClouds_RemainVisibleToEnemy()
    {
        HeightMap map = new(129, 129, 4f, 0.01f, new ushort[129 * 129]);
        StaticGeometry geometry = new(map, []);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(map);

        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");

        // Stationary T-62 on west; hostile M60A1 observing from 160 m east with clear LOS
        EntityId t62 = world.CreateUnit(1, t62Idx, new Vector2(140f, 200f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def));
        world.CreateUnit(2, m60Idx, new Vector2(300f, 200f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def));

        using Simulation sim = new(world, rules);
        Assert.NotNull(sim.Detection);

        for (int t = 0; t < 20; t++) sim.Step();

        Assert.True(sim.TryEnqueue(1, new DeploySmokeCommand(1, sim.Tick, t62)));

        // Advance to tick ~100: 1 mature cloud emitted by stationary T-62 -> enemy sees 1 cloud
        for (int t = 0; t < 80; t++) sim.Step();
        Assert.Equal(1, world.SmokeCloudCount);
        Assert.Equal(1, sim.Detection.GetVisibleSmokeClouds(2).Length);

        // Advance past stationary refresh (200 ticks) and bloom of 2nd overlapping cloud (~tick 300):
        // world now has 2 overlapping clouds at the same spot; enemy must NEVER lose sight of the smoke screen.
        for (int t = 0; t < 200; t++)
        {
            sim.Step();
            Assert.True(sim.Detection.GetVisibleSmokeClouds(2).Length >= 1,
                $"Enemy must continuously see stationary overlapping smoke screen at tick {sim.Tick}.");
        }

        Assert.True(world.SmokeCloudCount >= 2, $"Expected >= 2 overlapping clouds after stationary refresh, got {world.SmokeCloudCount}.");
        Assert.Equal(world.SmokeCloudCount, sim.Detection.GetVisibleSmokeClouds(2).Length);
    }

    [Fact]
    public void CloseEnemySmoke_IsNotRevealedThroughBridgeDeckOrAdjacentWall()
    {
        HeightMap map = new(129, 129, 4f, 0.01f, new ushort[129 * 129]);
        // 15 m elevated bridge deck spanning X=160..240 at Z=200, plus a 20 m opaque wall at X=295..305
        BridgeStructure bridge = new(
            new StructureId(1),
            new Vector2(160f, 200f),
            new Vector2(240f, 200f),
            width: 16f,
            startHeight: 15f,
            endHeight: 15f,
            thickness: 1.0f);
        OpaqueStructure wall = new(
            new StructureId(2),
            minX: 295f,
            minY: 0f,
            minZ: 150f,
            maxX: 305f,
            maxY: 20f,
            maxZ: 250f,
            isGrounded: true);
        StaticGeometry geometry = new(map, [wall], [bridge]);
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(map);

        ushort t62Idx = catalog.RequireDefinitionIndex("t62");
        UnitDefinition t62Def = catalog.RequireDefinition("t62");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");

        // Case 1: Enemy T-62 underneath the 15 m bridge (ground surface 0), observer M60A1 directly on top of bridge deck (surface 1)
        EntityId underBridgeT62 = world.CreateUnit(1, t62Idx, new Vector2(200f, 200f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def));
        world.CreateUnit(2, m60Idx, new Vector2(200f, 200f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def), initialSupportSurface: (uint)bridge.Id.Value);

        // Case 2: Enemy T-62 directly west of opaque wall (X=291, default heading spawns smoke at (291, 203.5)),
        // observer M60A1 directly east of wall at (306, 203.5) (dist=15 m <= radius=16 m, separated by wall X=295..305).
        EntityId behindWallT62 = world.CreateUnit(1, t62Idx, new Vector2(291f, 200f), t62Def.Speed, t62Def.MaxHealth, catalog.GetAmmoCapacity(t62Def));
        EntityId nearWallM60 = world.CreateUnit(2, m60Idx, new Vector2(306f, 203.5f), m60Def.Speed, m60Def.MaxHealth, catalog.GetAmmoCapacity(m60Def));

        using Simulation sim = new(world, rules);
        Assert.NotNull(sim.Detection);

        sim.Step();
        Assert.True(sim.TryEnqueue(1, new DeploySmokeCommand(1, sim.Tick, underBridgeT62)));
        Assert.True(sim.TryEnqueue(1, new DeploySmokeCommand(2, sim.Tick, behindWallT62)));

        for (int t = 0; t < 80; t++) sim.Step();

        Assert.True(world.SmokeCloudCount >= 2, "Both enemy T-62s must have emitted smoke clouds.");
        // Neither the observer on the 15 m bridge deck nor the observer right next to the opaque wall may see occluded enemy smoke
        Assert.Equal(0, sim.Detection.GetVisibleSmokeClouds(2).Length);

        // Also check when east observer steps slightly back (dist > radius, while cloud radius mathematically crosses wall)
        world.SetPosition(nearWallM60, new Vector2(325f, 200f));
        for (int t = 0; t < 10; t++) sim.Step();
        Assert.Equal(0, sim.Detection.GetVisibleSmokeClouds(2).Length);
    }
}
