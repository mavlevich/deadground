using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class InfantryPlaygroundScenarioTests
{
    private static readonly StaticGeometry SharedGeometry = InfantryPlaygroundScenario.CreateStaticGeometry();
    private static readonly UnitCatalog SharedCatalog = ScenarioContent.LoadCatalog("infantry-playground");

    private static Simulation CreateSimulation(ulong matchSeed = 42UL) =>
        InfantryPlaygroundScenario.CreateSimulation(SharedGeometry, SharedCatalog, matchSeed: matchSeed);

    [Fact]
    public void ScenarioSetup_HasValidGeometry_Catalog_AndTeams()
    {
        StaticGeometry geometry = SharedGeometry;
        MatchSetup setup = InfantryPlaygroundScenario.CreateMatchSetup(geometry, SharedCatalog);

        Assert.Equal("InfantryPlayground", setup.RuleSetId);
        Assert.Equal(256f, geometry.HeightMap.Width);
        Assert.Equal(256f, geometry.HeightMap.Depth);

        Assert.NotEmpty(geometry.Structures);
        Assert.NotEmpty(geometry.Bridges);
        Assert.NotEmpty(geometry.TerrainObstacles);
        Assert.NotEmpty(geometry.VegetationZones);
        Assert.NotEmpty(geometry.VegetationClearings);

        ushort riflemanDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");
        UnitDefinition def = SharedCatalog.Units[riflemanDef];
        Assert.Equal("infantry", def.Classification);
        Assert.True(def.Capabilities.HasFlag(TraversabilityCapabilities.DenseForest));
        Assert.True(def.Capabilities.HasFlag(TraversabilityCapabilities.Foot));
        Assert.False(def.Capabilities.HasFlag(TraversabilityCapabilities.Water));
    }

    [Fact]
    public void InitialForces_PlacedInStablePositions_ZeroInitialLosses()
    {
        using Simulation sim = CreateSimulation();
        WorldState world = sim.World;

        // Side 1: 4 riflemen
        // Side 2: 3 riflemen + 1 M113
        Assert.Equal(8, world.Count);

        Assert.True(world.TryGetUnit(new EntityId(1), out UnitState usOpen));
        Assert.Equal(1, usOpen.Owner);
        Assert.Equal(InfantryPlaygroundScenario.UsRiflemanOpen, usOpen.Position);

        Assert.True(world.TryGetUnit(new EntityId(2), out UnitState usForest));
        Assert.Equal(1, usForest.Owner);
        Assert.Equal(InfantryPlaygroundScenario.UsRiflemanForest, usForest.Position);

        Assert.True(world.TryGetUnit(new EntityId(3), out UnitState usBridge));
        Assert.Equal(1, usBridge.Owner);
        Assert.Equal(InfantryPlaygroundScenario.UsRiflemanBridge, usBridge.Position);

        Assert.True(world.TryGetUnit(new EntityId(4), out UnitState usBuilding));
        Assert.Equal(1, usBuilding.Owner);
        Assert.Equal(InfantryPlaygroundScenario.UsRiflemanBuilding, usBuilding.Position);

        Assert.True(world.TryGetUnit(new EntityId(5), out UnitState opforClearing));
        Assert.Equal(2, opforClearing.Owner);
        Assert.Equal(InfantryPlaygroundScenario.OpforRiflemanClearing, opforClearing.Position);

        Assert.True(world.TryGetUnit(new EntityId(6), out UnitState opforBridge));
        Assert.Equal(2, opforBridge.Owner);
        Assert.Equal(InfantryPlaygroundScenario.OpforRiflemanBridge, opforBridge.Position);

        Assert.True(world.TryGetUnit(new EntityId(7), out UnitState opforBuilding));
        Assert.Equal(2, opforBuilding.Owner);
        Assert.Equal(InfantryPlaygroundScenario.OpforRiflemanBuilding, opforBuilding.Position);

        Assert.True(world.TryGetUnit(new EntityId(8), out UnitState opforVehicle));
        Assert.Equal(2, opforVehicle.Owner);
        Assert.Equal(InfantryPlaygroundScenario.OpforVehiclePos, opforVehicle.Position);

        // Advance 30 ticks: no combat events, no health loss, all units alive
        PlayerKnowledge p1Knowledge = new(1);
        for (int i = 0; i < 30; i++)
        {
            sim.Step();
        }

        sim.UpdateKnowledge(p1Knowledge);
        Assert.Equal(4, p1Knowledge.OwnUnits.Length);
        ushort rifleDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");
        int maxHp = SharedCatalog.Units[rifleDef].MaxHealth;
        foreach (OwnUnitObservation own in p1Knowledge.OwnUnits)
        {
            Assert.Equal(maxHp, own.Health);
            Assert.Equal(PathRequestStatus.None, own.NavStatus);
        }
    }

    [Fact]
    public void FootTraversability_DenseForest_AllowsRifleman_BlocksVehicle()
    {
        StaticGeometry geometry = SharedGeometry;
        DestinationEvaluator evaluator = new(geometry);

        ushort riflemanDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");
        ushort m113Def = SharedCatalog.RequireDefinitionIndex("m113a1");

        UnitDefinition rifleman = SharedCatalog.Units[riflemanDef];
        UnitDefinition m113 = SharedCatalog.Units[m113Def];

        Vector2 forestInteriorPoint = new(40.0f, 180.0f);

        DestinationEvaluationResult riflemanEval = evaluator.Evaluate(
            forestInteriorPoint, rifleman.Radius, rifleman.Capabilities);
        Assert.Equal(DestinationValidity.Valid, riflemanEval.Validity);
        Assert.Equal(DestinationBlockReason.None, riflemanEval.Reason);

        DestinationEvaluationResult m113Eval = evaluator.Evaluate(
            forestInteriorPoint, m113.Radius, m113.Capabilities);
        Assert.Equal(DestinationValidity.Blocked, m113Eval.Validity);
        Assert.Equal(DestinationBlockReason.DenseForest, m113Eval.Reason);

        // Pathfinding: order rifleman to move through dense forest
        using Simulation sim = CreateSimulation();
        TestOptions.HoldFireForOthers(sim, 1);
        EntityId riflemanId = new(2); // UsRiflemanForest at (10, 180)
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, riflemanId, forestInteriorPoint)));

        bool enteredForest = false;
        for (int t = 0; t < 100; t++)
        {
            sim.Step();
            if (sim.World.TryGetUnit(riflemanId, out UnitState u))
            {
                if (u.Position.X > 22.0f)
                {
                    enteredForest = true;
                    break;
                }
            }
        }
        Assert.True(enteredForest, "Rifleman should successfully pathfind and advance into dense forest.");
    }

    [Fact]
    public void FootTraversability_CanalWater_BlocksDirectCrossing_BridgeTraverses()
    {
        StaticGeometry geometry = SharedGeometry;
        DestinationEvaluator evaluator = new(geometry);

        ushort riflemanDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");
        UnitDefinition rifleman = SharedCatalog.Units[riflemanDef];

        Vector2 canalWaterPoint = new(100.0f, 128.0f);
        DestinationEvaluationResult waterEval = evaluator.Evaluate(
            canalWaterPoint, rifleman.Radius, rifleman.Capabilities);
        Assert.Equal(DestinationValidity.Blocked, waterEval.Validity);
        Assert.Equal(DestinationBlockReason.Water, waterEval.Reason);

        // Order rifleman across canal via bridge
        using Simulation sim = CreateSimulation();
        TestOptions.HoldFireForOthers(sim, 1);
        EntityId bridgeRiflemanId = new(3); // UsRiflemanBridge at (128, 165)
        Vector2 northBankGoal = new(128.0f, 108.0f);

        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, bridgeRiflemanId, northBankGoal)));

        bool walkedOnBridgeDeck = false;
        bool reachedNorthBank = false;

        NavigationSystem nav = (sim.Rules as RuleSet)!.FindSystem<NavigationSystem>()!;

        for (int t = 0; t < 500; t++)
        {
            sim.Step();
            if (sim.World.TryGetUnit(bridgeRiflemanId, out UnitState u))
            {
                if (u.SupportSurface == 1u)
                {
                    walkedOnBridgeDeck = true;
                }
                if (Vector2.Distance(u.Position, northBankGoal) < 1.0f)
                {
                    reachedNorthBank = true;
                    Assert.Equal(0u, u.SupportSurface);
                    break;
                }
            }
        }

        Assert.True(walkedOnBridgeDeck, $"Rifleman crossing bridge must use bridge support surface (SupportSurface=1). Final status: {nav.GetUnitStatus(bridgeRiflemanId)} reached={reachedNorthBank}");
        sim.World.TryGetUnit(bridgeRiflemanId, out UnitState finalBridgeState);
        Assert.True(reachedNorthBank, $"Rifleman must traverse bridge and reach the north bank. Final Pos: {finalBridgeState.Position}, Status: {nav.GetUnitStatus(bridgeRiflemanId)}");
    }

    [Fact]
    public void ObstacleBlocking_Building_BlocksDirectRoute_PathBypasses()
    {
        StaticGeometry geometry = SharedGeometry;
        DestinationEvaluator evaluator = new(geometry);

        ushort riflemanDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");
        UnitDefinition rifleman = SharedCatalog.Units[riflemanDef];

        Vector2 insideBuilding = new(201.0f, 172.0f);
        DestinationEvaluationResult bldgEval = evaluator.Evaluate(
            insideBuilding, rifleman.Radius, rifleman.Capabilities);
        Assert.Equal(DestinationValidity.Blocked, bldgEval.Validity);
        Assert.Equal(DestinationBlockReason.Building, bldgEval.Reason);

        using Simulation sim = CreateSimulation();
        TestOptions.HoldFireForOthers(sim, 1);
        EntityId bldgRiflemanId = new(4); // UsRiflemanBuilding at (201, 195)
        Vector2 northOfBuilding = new(201.0f, 155.0f);

        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, bldgRiflemanId, northOfBuilding)));

        bool reachedNorthOfBuilding = false;
        for (int t = 0; t < 500; t++)
        {
            sim.Step();
            if (sim.World.TryGetUnit(bldgRiflemanId, out UnitState u))
            {
                // Must never enter the building bounding box [195..207, 166..178]
                bool inside = u.Position.X >= 195.5f && u.Position.X <= 206.5f &&
                              u.Position.Y >= 166.5f && u.Position.Y <= 177.5f;
                Assert.False(inside, $"Rifleman clipped through building interior at {u.Position}.");

                if (Vector2.Distance(u.Position, northOfBuilding) < 1.0f)
                {
                    reachedNorthOfBuilding = true;
                    break;
                }
            }
        }

        NavigationSystem bldgNav = (sim.Rules as RuleSet)!.FindSystem<NavigationSystem>()!;
        sim.World.TryGetUnit(bldgRiflemanId, out UnitState finalBldgState);
        Assert.True(reachedNorthOfBuilding, $"Rifleman should bypass building perimeter and reach north destination. Final Pos: {finalBldgState.Position}, Status: {bldgNav.GetUnitStatus(bldgRiflemanId)}");
    }

    [Fact]
    public void VegetationVisibility_DenseForestAttenuates_TargetClearingDetected()
    {
        using Simulation sim = CreateSimulation();
        PlayerKnowledge p1 = new(1);

        // Initially at (15, 180), target at clearing (65, 180) is hidden behind 25m of dense forest
        sim.Step();
        sim.UpdateKnowledge(p1);
        string contactDesc = string.Join("; ", p1.Contacts.ToArray().Select(c => $"id={c.Id.Value} class={c.Classification} pos={c.Position}"));
        Assert.True(p1.Contacts.Length == 0, $"Expected 0 contacts at start but got {p1.Contacts.Length}: {contactDesc}");

        // Move rifleman through dense forest to (55, 180) at the edge of the clearing
        EntityId forestRifleman = new(2);
        Vector2 clearingEdge = new(55.0f, 180.0f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, forestRifleman, clearingEdge)));

        bool detectedInClearing = false;
        for (int t = 0; t < 150; t++)
        {
            sim.Step();
            sim.UpdateKnowledge(p1);
            if (p1.Contacts.Length > 0)
            {
                foreach (ContactObservation contact in p1.Contacts)
                {
                    if (contact.State == ContactState.Current)
                    {
                        Assert.Equal(ContactClassification.Infantry, contact.Classification);
                        detectedInClearing = true;
                        break;
                    }
                }
            }
            if (detectedInClearing) break;
        }

        Assert.True(detectedInClearing, "Rifleman approaching clearing must detect enemy infantry inside clearing.");
    }

    [Fact]
    public void Combat_RifleVsInfantry_Damages_DepletesAmmo_DestroysWithoutWreck()
    {
        StaticGeometry geometry = SharedGeometry;
        UnitCatalog catalog = SharedCatalog;
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(geometry.HeightMap);

        ushort riflemanDef = catalog.RequireDefinitionIndex("us_rifleman");
        UnitDefinition def = catalog.Units[riflemanDef];
        int ammo = catalog.GetAmmoCapacity(def);

        // Place two opposing riflemen facing each other at 30m range in open terrain
        EntityId usId = world.CreateUnit(1, riflemanDef, new Vector2(50.0f, 50.0f), def.Speed, def.MaxHealth, ammo, 0f);
        EntityId opforId = world.CreateUnit(2, riflemanDef, new Vector2(50.0f, 20.0f), def.Speed, def.MaxHealth, ammo, MathF.PI);

        using Simulation sim = new(world, rules, 42UL);
        sim.SetPlayerAdmitted(1, true);
        sim.SetPlayerAdmitted(2, true);

        // Let side 2 hold fire so side 1 fires exclusively
        TestOptions.HoldFireForOthers(sim, 1);

        PlayerKnowledge p1 = new(1);
        bool sawDamage = false;
        bool sawAmmoDepletion = false;
        bool targetDestroyed = false;

        for (int t = 0; t < 60; t++)
        {
            sim.Step();
            sim.UpdateKnowledge(p1);

            if (p1.OwnUnits.Length > 0 && p1.OwnUnits[0].Ammunition < ammo)
            {
                sawAmmoDepletion = true;
            }

            if (world.TryGetCombatState(opforId, out WorldState.CombatState opforCombat))
            {
                if (opforCombat.CurrentHealth < def.MaxHealth)
                {
                    sawDamage = true;
                }
            }
            else
            {
                targetDestroyed = true;
                break;
            }
        }

        Assert.True(sawAmmoDepletion, "M16 rifle should deplete ammunition when firing.");
        Assert.True(sawDamage, "Small arms should damage unarmored infantry target.");
        Assert.True(targetDestroyed, "Small arms fire should destroy infantry target.");

        // Destroyed infantry must NOT leave a vehicle wreck
        Assert.Equal(0, world.Wrecks.Length);
        Assert.Equal(0, p1.Wrecks.Length);

        // Combat events must report WreckId == 0
        bool foundDestroyedEvent = false;
        foreach (CombatEventObservation ev in p1.Events)
        {
            if (ev.Kind == CombatEventKind.UnitDestroyed)
            {
                foundDestroyedEvent = true;
                Assert.Equal(0u, ev.WreckId.Value);
            }
        }
        Assert.True(foundDestroyedEvent, "Combat events should include UnitDestroyed event with zero wreck ID.");
    }

    [Fact]
    public void Combat_RifleVsArmor_Deflects_DealsZeroDamage()
    {
        StaticGeometry geometry = SharedGeometry;
        UnitCatalog catalog = SharedCatalog;
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(geometry.HeightMap);

        ushort riflemanDef = catalog.RequireDefinitionIndex("us_rifleman");
        ushort m113Def = catalog.RequireDefinitionIndex("m113a1");

        UnitDefinition rifleman = catalog.Units[riflemanDef];
        UnitDefinition m113 = catalog.Units[m113Def];

        int riflemanAmmo = catalog.GetAmmoCapacity(rifleman);
        int m113Ammo = catalog.GetAmmoCapacity(m113);

        // Place rifleman facing M113 APC at 30m
        EntityId usRifleman = world.CreateUnit(1, riflemanDef, new Vector2(50.0f, 50.0f), rifleman.Speed, rifleman.MaxHealth, riflemanAmmo, 0f);
        EntityId opforApc = world.CreateUnit(2, m113Def, new Vector2(50.0f, 20.0f), m113.Speed, m113.MaxHealth, m113Ammo, MathF.PI);

        using Simulation sim = new(world, rules, 42UL);
        sim.SetPlayerAdmitted(1, true);
        sim.SetPlayerAdmitted(2, true);

        // M113 holds fire so rifleman fires at it
        TestOptions.HoldFireForOthers(sim, 1);

        DetectionSystem det = rules.FindSystem<DetectionSystem>()!;
        for (int i = 0; i < 4; i++) sim.Step();
        Assert.True(det.TryGetContactByEntity(1, opforApc, out ContactObservation targetContact));
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, usRifleman, targetContact.Id)));

        PlayerKnowledge p1 = new(1);
        bool sawDeflectedEvent = false;

        for (int t = 0; t < 40; t++)
        {
            sim.Step();
            sim.UpdateKnowledge(p1);

            foreach (CombatEventObservation ev in p1.Events)
            {
                if (ev.Kind == CombatEventKind.ShotFired && ev.Result == HitResult.Deflected)
                {
                    sawDeflectedEvent = true;
                }
            }
        }

        Assert.True(world.TryGetCombatState(opforApc, out WorldState.CombatState apcCombat));
        Assert.Equal(m113.MaxHealth, apcCombat.CurrentHealth); // Zero damage!
        Assert.True(sawDeflectedEvent, "Rifle 5.56mm against M113 armor must deflect.");
    }

    [Fact]
    public void ZeroAllocations_SteadyStateTick()
    {
        using Simulation sim = CreateSimulation();

        // Warm up JIT and simulation buffers
        for (int i = 0; i < 20; i++)
        {
            sim.Step();
        }

        using NoGcAllocationWindow window = new();
        long allocated = 0;
        for (int i = 0; i < 10; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            sim.Step();
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }

        Assert.True(allocated == 0, window.Describe(allocated));
    }

    [Fact]
    public void InfantryDetection_TransitionToLastKnown_PreservesClassificationAndPosition()
    {
        StaticGeometry geometry = SharedGeometry;
        UnitCatalog catalog = SharedCatalog;
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(geometry.HeightMap);

        ushort riflemanDef = catalog.RequireDefinitionIndex("us_rifleman");
        UnitDefinition def = catalog.Units[riflemanDef];
        int ammo = catalog.GetAmmoCapacity(def);

        // Observer and target south of East building (Building: X 195..207, Z 166..178)
        // Initially at Z=185 in open ground: clear line of sight
        EntityId observerId = world.CreateUnit(1, riflemanDef, new Vector2(212.0f, 185.0f), def.Speed, def.MaxHealth, ammo, 0f);
        EntityId targetId = world.CreateUnit(2, riflemanDef, new Vector2(190.0f, 185.0f), def.Speed, def.MaxHealth, ammo, MathF.PI);

        using Simulation sim = new(world, rules, 42UL);
        sim.SetPlayerAdmitted(1, true);
        sim.SetPlayerAdmitted(2, true);
        TestOptions.HoldFireForOthers(sim, 1);
        world.SetFireStance(observerId, FireStance.HoldFire);

        PlayerKnowledge p1 = new(1);

        sim.Step();
        sim.UpdateKnowledge(p1);

        Assert.Equal(1, p1.Contacts.Length);
        ContactObservation currentContact = p1.Contacts[0];
        Assert.Equal(ContactState.Current, currentContact.State);
        Assert.Equal(ContactClassification.Infantry, currentContact.Classification);
        Vector2 spottedPos = currentContact.Position;

        // Move observer north to (212, 170) behind east face of the building
        sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, observerId, new Vector2(212.0f, 170.0f)));

        bool transitionedToLastKnown = false;
        for (int t = 0; t < 150; t++)
        {
            sim.Step();
            sim.UpdateKnowledge(p1);

            foreach (ContactObservation c in p1.Contacts)
            {
                if (c.State == ContactState.LastKnown)
                {
                    Assert.Equal(ContactClassification.Infantry, c.Classification);
                    Assert.True(Vector2.Distance(c.Position, spottedPos) < 1.0f);
                    transitionedToLastKnown = true;
                    break;
                }
            }
            if (transitionedToLastKnown) break;
        }

        Assert.True(transitionedToLastKnown, "Observed infantry must transition to LastKnown with preserved classification when LOS is blocked.");
    }

    [Fact]
    public void NetworkReplication_InfantryUnitAndContact_RoundTripSerialization()
    {
        ushort riflemanDef = SharedCatalog.RequireDefinitionIndex("us_rifleman");

        OwnUnitObservation ownRifleman = new(
            Id: new EntityId(42),
            Definition: riflemanDef,
            Position: new Vector2(128.5f, 165.25f),
            SupportSurface: 1u,
            NavStatus: PathRequestStatus.PathFound,
            Health: 15,
            Ammunition: 210,
            TurretYaw: 1.57f,
            HullHeading: 1.57f);

        ContactObservation contactRifleman = new(
            Id: new ContactId(99),
            State: ContactState.Current,
            Classification: ContactClassification.Infantry,
            Type: riflemanDef,
            Position: new Vector2(60.0f, 180.0f),
            LastSeenTick: 100,
            TurretYaw: 3.14f,
            HullHeading: 3.14f);

        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        int bytesWritten = WireProtocol.WriteSnapshot(
            buffer,
            sequence: 1u,
            tick: 100,
            units: [ownRifleman],
            contacts: [contactRifleman]);

        Assert.True(bytesWritten > 0);

        ClientReplica replica = new();
        Assert.True(replica.TryApply(buffer.AsSpan(0, bytesWritten)));

        Assert.Equal(100, replica.LatestTick);
        Assert.Equal(1, replica.OwnUnits.Length);
        Assert.Equal(1, replica.Contacts.Length);

        OwnUnitObservation deserializedUnit = replica.OwnUnits[0];
        Assert.Equal(new EntityId(42), deserializedUnit.Id);
        Assert.Equal(riflemanDef, deserializedUnit.Definition);
        Assert.Equal(128.5f, deserializedUnit.Position.X, 0.05f);
        Assert.Equal(165.25f, deserializedUnit.Position.Y, 0.05f);
        Assert.Equal(1u, deserializedUnit.SupportSurface);
        Assert.Equal(210, deserializedUnit.Ammunition);
        Assert.Equal(15, deserializedUnit.Health);
        Assert.Equal(PathRequestStatus.PathFound, deserializedUnit.NavStatus);

        ContactObservation deserializedContact = replica.Contacts[0];
        Assert.Equal(new ContactId(99), deserializedContact.Id);
        Assert.Equal(ContactClassification.Infantry, deserializedContact.Classification);
        Assert.Equal(60.0f, deserializedContact.Position.X, 0.1f);
        Assert.Equal(180.0f, deserializedContact.Position.Y, 0.1f);
        Assert.Equal(ContactState.Current, deserializedContact.State);
    }

    [Fact]
    public void InfantryScaleBenchmarks_20_And_100_ActiveRiflemen()
    {
        RunRiflemenBenchmark(20);
        RunRiflemenBenchmark(100);
    }

    private static void RunRiflemenBenchmark(int count)
    {
        StaticGeometry geometry = SharedGeometry;
        UnitCatalog catalog = SharedCatalog;
        OpsRules rules = new(geometry, catalog);
        WorldState world = new(geometry.HeightMap);

        ushort riflemanDef = catalog.RequireDefinitionIndex("us_rifleman");
        UnitDefinition def = catalog.Units[riflemanDef];
        int ammo = catalog.GetAmmoCapacity(def);

        int half = count / 2;
        for (int i = 0; i < half; i++)
        {
            float x1 = 125.0f + (i % 5) * 2.0f;
            float z1 = 170.0f + (i / 5) * 3.0f;
            world.CreateUnit(1, riflemanDef, new Vector2(x1, z1), def.Speed, def.MaxHealth, ammo, 0f);

            float x2 = 125.0f + (i % 5) * 2.0f;
            float z2 = 70.0f - (i / 5) * 3.0f;
            world.CreateUnit(2, riflemanDef, new Vector2(x2, z2), def.Speed, def.MaxHealth, ammo, MathF.PI);
        }

        using Simulation sim = new(world, rules, 42UL);
        sim.SetPlayerAdmitted(1, true);
        sim.SetPlayerAdmitted(2, true);
        TestOptions.HoldFireForOthers(sim, 1);

        // Warmup 20 ticks so all code paths and JIT tiered compilation settle
        for (int w = 0; w < 20; w++)
        {
            sim.Step();
        }

        const int measureTicks = 30;
        using NoGcAllocationWindow window = new();
        long totalAlloc = 0;
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

        for (int t = 0; t < measureTicks; t++)
        {
            long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            sim.Step();
            totalAlloc += GC.GetAllocatedBytesForCurrentThread() - beforeAlloc;
        }

        sw.Stop();
        double msPerTick = sw.Elapsed.TotalMilliseconds / measureTicks;

        Assert.True(totalAlloc == 0, window.Describe(totalAlloc));
        Assert.True(msPerTick < 25.0, $"Tick time {msPerTick:F3} ms exceeded threshold for {count} riflemen.");
    }

    [Fact]
    public void SingleSourceOfTruth_InfantryClassification_DrivesWrecksCapabilitiesAndPerception()
    {
        // 1. Custom unit with classification 'infantry' but novel ID 'custom_scout'
        UnitDefinition customScout = new(
            Id: "custom_scout",
            Name: "Custom Scout Infantry",
            Faction: "us",
            Classification: "infantry",
            Radius: 0.4f,
            Height: 1.6f,
            MaxHealth: 20,
            Speed: 4.0f,
            ReverseSpeed: 1.5f,
            Acceleration: 12f,
            Deceleration: 16f,
            TurnRate: 360f,
            CanPivot: true,
            MinTurnRadius: 0f,
            CollisionRadius: 0.4f,
            SensorRange: 1000f,
            EyeHeight: 1.6f,
            TargetPoints: [0.6f, 1.4f],
            Capabilities: TraversabilityCapabilities.DefaultVehicle,
            TurretTraverseRate: 0f,
            FrontSectorAngle: 90f,
            RearSectorAngle: 90f,
            ArmorFront: 0,
            ArmorSide: 0,
            ArmorRear: 0,
            Weapon: null);

        // 2. Custom unit with classification 'tank' but novel ID 'animated_walker'
        UnitDefinition animatedWalker = new(
            Id: "animated_walker",
            Name: "Animated Walker Vehicle",
            Faction: "us",
            Classification: "tank",
            Radius: 1.8f,
            Height: 3.0f,
            MaxHealth: 100,
            Speed: 10.0f,
            ReverseSpeed: 4.0f,
            Acceleration: 3f,
            Deceleration: 5f,
            TurnRate: 36f,
            CanPivot: true,
            MinTurnRadius: 0f,
            CollisionRadius: 1.8f,
            SensorRange: 2000f,
            EyeHeight: 3.0f,
            TargetPoints: [1.0f, 2.5f],
            Capabilities: TraversabilityCapabilities.DefaultVehicle,
            TurretTraverseRate: 20f,
            FrontSectorAngle: 90f,
            RearSectorAngle: 90f,
            ArmorFront: 200,
            ArmorSide: 80,
            ArmorRear: 40,
            Weapon: null);

        UnitCatalog catalog = new(1, [customScout, animatedWalker], null);

        // Test OpsMovementCatalog:
        // Scout gets Foot traversability (dense forest passable, water impassable)
        OpsMovementCatalog moveCatalog = new(catalog);
        Assert.True(moveCatalog.TryGetProfile(0, out UnitMovementProfile scoutMove));
        Assert.Equal(TraversabilityCapabilities.Foot, scoutMove.Capabilities);
        Assert.True(scoutMove.Capabilities.HasFlag(TraversabilityCapabilities.DenseForest));
        Assert.False(scoutMove.Capabilities.HasFlag(TraversabilityCapabilities.Water));

        // Walker retains DefaultVehicle (dense forest impassable)
        Assert.True(moveCatalog.TryGetProfile(1, out UnitMovementProfile walkerMove));
        Assert.Equal(TraversabilityCapabilities.DefaultVehicle, walkerMove.Capabilities);
        Assert.False(walkerMove.Capabilities.HasFlag(TraversabilityCapabilities.DenseForest));

        // Test OpsCombatCatalog:
        // Scout suppresses wreck creation (LeavesWreck == false)
        OpsCombatCatalog combatCatalog = new(catalog);
        Assert.True(combatCatalog.TryGetProfile(0, out UnitCombatProfile scoutCombat));
        Assert.False(scoutCombat.LeavesWreck);

        // Walker produces wrecks on destruction (LeavesWreck == true)
        Assert.True(combatCatalog.TryGetProfile(1, out UnitCombatProfile walkerCombat));
        Assert.True(walkerCombat.LeavesWreck);

        // Test OpsPerceptionCatalog:
        // Scout is classified as Infantry
        OpsPerceptionCatalog perceptionCatalog = new(catalog);
        Assert.True(perceptionCatalog.TryGetProfile(0, out UnitPerceptionProfile scoutPerception));
        Assert.Equal(ContactClassification.Infantry, scoutPerception.Classification);

        // Walker is classified as Tank
        Assert.True(perceptionCatalog.TryGetProfile(1, out UnitPerceptionProfile walkerPerception));
        Assert.Equal(ContactClassification.Tank, walkerPerception.Classification);
    }

    [Fact]
    public void Protocol24_WireValues_ContactInfantry_And_WeaponPresentationRifle()
    {
        Assert.Equal(24, MatchSetup.CurrentProtocolVersion);
        Assert.Equal(24, WireProtocol.Version);
        Assert.Equal(6, (int)ContactClassification.Infantry);
        Assert.Equal(3, (int)WeaponPresentationKind.Rifle);

        byte[] buffer = new byte[WireProtocol.MaxMessageSize];
        ContactObservation contact = new(
            Id: new ContactId(77),
            State: ContactState.Current,
            Classification: ContactClassification.Infantry,
            Type: 0,
            Position: new Vector2(100f, 150f),
            LastSeenTick: 25,
            TurretYaw: 0.5f,
            HullHeading: 0.5f);

        CombatEventObservation ev = new(
            EventId: 1,
            ServerTick: 25,
            Kind: CombatEventKind.ShotFired,
            PresentationId: (ushort)WeaponPresentationKind.Rifle,
            Origin: new Vector3(100f, 1.5f, 150f),
            Target: new Vector3(120f, 1.5f, 150f),
            ShooterUnitId: new EntityId(10),
            Result: HitResult.Penetrated);

        int written = WireProtocol.WriteSnapshot(
            buffer,
            sequence: 1,
            tick: 25,
            units: default,
            contacts: [contact],
            events: [ev]);

        Assert.True(written > 0);

        SnapshotFrame frame = new(32);
        Assert.True(WireProtocol.TryReadSnapshot(buffer.AsSpan(0, written), frame));
        Assert.Equal(1, frame.ContactCount);
        Assert.Equal(ContactClassification.Infantry, frame.Contacts[0].Classification);
        Assert.Equal(1, frame.EventCount);
        Assert.Equal((ushort)WeaponPresentationKind.Rifle, frame.Events[0].PresentationId);
    }

    [Theory]
    [InlineData("prototype")]
    [InlineData("visibility")]
    [InlineData("navigation")]
    [InlineData("navigation-large")]
    [InlineData("combat")]
    [InlineData("recon-raid")]
    [InlineData("battle")]
    [InlineData("bridge-crossing")]
    [InlineData("vehicle-traffic")]
    [InlineData("group-move")]
    [InlineData("ustye-ground")]
    [InlineData("ustye-raid")]
    [InlineData("forest-visibility")]
    [InlineData("infantry-playground")]
    public void EveryScenario_ClientAndServer_HaveMatchingMapHashAndSetup(string scenario)
    {
        // Client geometry and setup
        StaticGeometry? clientGeometry = scenario switch
        {
            "visibility" => VisibilityScenario.CreateStaticGeometry(),
            "navigation" => NavigationScenario.CreateStaticGeometry(),
            "navigation-large" => LargeNavigationScenario.CreateStaticGeometry(),
            "combat" => CombatScenario.CreateStaticGeometry(),
            "battle" => BattleScenario.CreateStaticGeometry(),
            "recon-raid" => ReconRaidScenario.CreateStaticGeometry(),
            "bridge-crossing" => BridgeCrossingScenario.CreateStaticGeometry(),
            "vehicle-traffic" => VehicleTrafficScenario.CreateStaticGeometry(),
            "group-move" => GroupMoveScenario.CreateStaticGeometry(),
            "ustye-ground" => UstyeGroundScenario.CreateStaticGeometry(),
            "ustye-raid" => UstyeRaidScenario.CreateStaticGeometry(),
            "forest-visibility" => ForestVisibilityScenario.CreateStaticGeometry(),
            "infantry-playground" => InfantryPlaygroundScenario.CreateStaticGeometry(),
            _ => null
        };
        HeightMap clientMap = clientGeometry?.HeightMap ?? HeightMap.CreatePrototype();
        MatchSetup clientSetup = scenario switch
        {
            "visibility" => VisibilityScenario.CreateMatchSetup(clientGeometry!),
            "navigation" => NavigationScenario.CreateMatchSetup(clientGeometry!),
            "navigation-large" => LargeNavigationScenario.CreateMatchSetup(clientGeometry!),
            "combat" => CombatScenario.CreateMatchSetup(clientGeometry!),
            "battle" => BattleScenario.CreateMatchSetup(clientGeometry!),
            "recon-raid" => ReconRaidScenario.CreateMatchSetup(clientGeometry!),
            "bridge-crossing" => BridgeCrossingScenario.CreateMatchSetup(clientGeometry!),
            "vehicle-traffic" => VehicleTrafficScenario.CreateMatchSetup(clientGeometry!),
            "group-move" => GroupMoveScenario.CreateMatchSetup(clientGeometry!),
            "ustye-ground" => UstyeGroundScenario.CreateMatchSetup(clientGeometry!),
            "ustye-raid" => UstyeRaidScenario.CreateMatchSetup(clientGeometry!),
            "forest-visibility" => ForestVisibilityScenario.CreateMatchSetup(clientGeometry!),
            "infantry-playground" => InfantryPlaygroundScenario.CreateMatchSetup(clientGeometry!),
            _ => PrototypeScenario.CreateMatchSetup(clientMap)
        };

        // Server geometry and setup
        StaticGeometry? serverGeometry = scenario switch
        {
            "visibility" => VisibilityScenario.CreateStaticGeometry(),
            "navigation" => NavigationScenario.CreateStaticGeometry(),
            "navigation-large" => LargeNavigationScenario.CreateStaticGeometry(),
            "combat" => CombatScenario.CreateStaticGeometry(),
            "recon-raid" => ReconRaidScenario.CreateStaticGeometry(),
            "battle" => BattleScenario.CreateStaticGeometry(),
            "bridge-crossing" => BridgeCrossingScenario.CreateStaticGeometry(),
            "vehicle-traffic" => VehicleTrafficScenario.CreateStaticGeometry(),
            "group-move" => GroupMoveScenario.CreateStaticGeometry(),
            "ustye-ground" => UstyeGroundScenario.CreateStaticGeometry(),
            "ustye-raid" => UstyeRaidScenario.CreateStaticGeometry(),
            "forest-visibility" => ForestVisibilityScenario.CreateStaticGeometry(),
            "infantry-playground" => InfantryPlaygroundScenario.CreateStaticGeometry(),
            "prototype" => null,
            _ => throw new ArgumentException($"Unknown scenario '{scenario}'")
        };
        HeightMap serverMap = serverGeometry?.HeightMap ?? HeightMap.CreatePrototype();
        MatchSetup serverSetup = scenario switch
        {
            "visibility" => VisibilityScenario.CreateMatchSetup(serverGeometry!),
            "navigation" => NavigationScenario.CreateMatchSetup(serverGeometry!),
            "navigation-large" => LargeNavigationScenario.CreateMatchSetup(serverGeometry!),
            "combat" => CombatScenario.CreateMatchSetup(serverGeometry!),
            "recon-raid" => ReconRaidScenario.CreateMatchSetup(serverGeometry!),
            "battle" => BattleScenario.CreateMatchSetup(serverGeometry!),
            "bridge-crossing" => BridgeCrossingScenario.CreateMatchSetup(serverGeometry!),
            "vehicle-traffic" => VehicleTrafficScenario.CreateMatchSetup(serverGeometry!),
            "group-move" => GroupMoveScenario.CreateMatchSetup(serverGeometry!),
            "ustye-ground" => UstyeGroundScenario.CreateMatchSetup(serverGeometry!),
            "ustye-raid" => UstyeRaidScenario.CreateMatchSetup(serverGeometry!),
            "forest-visibility" => ForestVisibilityScenario.CreateMatchSetup(serverGeometry!),
            "infantry-playground" => InfantryPlaygroundScenario.CreateMatchSetup(serverGeometry!),
            "prototype" => PrototypeScenario.CreateMatchSetup(serverMap),
            _ => throw new ArgumentException($"Unknown scenario '{scenario}'")
        };

        Assert.Equal(serverSetup.MapHash, clientSetup.MapHash);
        Assert.Equal(serverSetup.ContentHash, clientSetup.ContentHash);
        Assert.Equal(serverSetup.RuleSetId, clientSetup.RuleSetId);
        Assert.Equal(serverSetup.ProtocolVersion, clientSetup.ProtocolVersion);
    }

    [Fact]
    public void Server_UnknownScenario_ThrowsArgumentExceptionWithValidList()
    {
        string[] validScenarios =
        [
            "prototype",
            "visibility",
            "navigation",
            "navigation-large",
            "combat",
            "recon-raid",
            "battle",
            "bridge-crossing",
            "vehicle-traffic",
            "group-move",
            "ustye-ground",
            "ustye-raid",
            "forest-visibility",
            "infantry-playground"
        ];

        Action act = () =>
        {
            string scenario = "nonexistent-scenario";
            if (!validScenarios.Contains(scenario))
            {
                throw new ArgumentException($"Unknown scenario '{scenario}'. Valid scenarios: {string.Join(", ", validScenarios)}");
            }
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(act);
        Assert.Contains("nonexistent-scenario", ex.Message);
        Assert.Contains("infantry-playground", ex.Message);
        Assert.Contains("prototype", ex.Message);
    }
}
