using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class BridgeCrossingTests
{
    [Fact]
    public void Clarification1_SameRadius_DifferentCapabilities_TakeDifferentRoutesThroughForest()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        UnitDefinition footDef = catalog.RequireDefinition("infantry_foot");
        UnitDefinition miniDef = catalog.RequireDefinition("mini_vehicle");

        Assert.Equal(0.4f, footDef.Radius);
        Assert.Equal(0.4f, miniDef.Radius);

        Assert.True(footDef.Capabilities.HasFlag(TraversabilityCapabilities.DenseForest));
        Assert.False(miniDef.Capabilities.HasFlag(TraversabilityCapabilities.DenseForest));

        // Create simulation with both units starting at (130, 70), west of forest [135..175, 50..90]
        WorldState world = new(geometry.HeightMap);
        OpsRules rules = new(geometry, catalog);
        using Simulation sim = new(world, rules);

        ushort footIdx = catalog.RequireDefinitionIndex("infantry_foot");
        ushort miniIdx = catalog.RequireDefinitionIndex("mini_vehicle");

        EntityId footId = world.CreateUnit(1, footIdx, new Vector2(130f, 70f), footDef.Speed, footDef.MaxHealth);
        EntityId miniId = world.CreateUnit(1, miniIdx, new Vector2(130f, 70f), miniDef.Speed, miniDef.MaxHealth);

        // Command both units to move across the forest to (180, 70)
        Vector2 goal = new(180f, 70f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, footId, goal)));
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, miniId, goal)));

        bool footTraversedForestInterior = false;
        bool miniDetouredAroundForest = false;

        for (int tick = 0; tick < 500; tick++)
        {
            sim.Step();

            if (world.TryGetUnit(footId, out UnitState footState))
            {
                // Check if foot unit is moving through the forest interior
                if (footState.Position.X > BridgeCrossingScenario.ForestMinX &&
                    footState.Position.X < BridgeCrossingScenario.ForestMaxX)
                {
                    footTraversedForestInterior = true;
                    // Foot unit should move directly along Z ~ 70
                    Assert.InRange(footState.Position.Y, 65f, 75f);
                }
            }

            if (world.TryGetUnit(miniId, out UnitState miniState))
            {
                // Mini vehicle must NEVER enter the forest interior bounding box
                bool insideForest = miniState.Position.X > BridgeCrossingScenario.ForestMinX &&
                                    miniState.Position.X < BridgeCrossingScenario.ForestMaxX &&
                                    miniState.Position.Y > BridgeCrossingScenario.ForestMinZ &&
                                    miniState.Position.Y < BridgeCrossingScenario.ForestMaxZ;
                Assert.False(insideForest, $"Mini vehicle entered forest interior at ({miniState.Position.X:F1}, {miniState.Position.Y:F1}).");

                // Mini vehicle must route around the forest boundary (Z < 50 or Z > 90)
                if (miniState.Position.Y < BridgeCrossingScenario.ForestMinZ ||
                    miniState.Position.Y > BridgeCrossingScenario.ForestMaxZ)
                {
                    miniDetouredAroundForest = true;
                }
            }

            if (footTraversedForestInterior && miniDetouredAroundForest)
            {
                break;
            }
        }

        Assert.True(footTraversedForestInterior, "Foot unit should route directly through the dense forest.");
        Assert.True(miniDetouredAroundForest, "Mini vehicle with identical radius must detour around the dense forest.");
    }

    [Fact]
    public void Clarification2_ContactObservation_PreservesSupportSurface_InLastKnown_WithoutHiddenStatePolling()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        using Simulation sim = BridgeCrossingScenario.CreateSimulation(geometry, catalog);
        PlayerKnowledge p1Knowledge = new(1);

        // Advance simulation: tank on west bank should detect target vehicle across the river
        for (int t = 0; t < 20; t++)
        {
            sim.Step();
        }

        sim.UpdateKnowledge(p1Knowledge);
        Assert.True(p1Knowledge.Contacts.Length > 0, "Player 1 should detect hostile unit across the bridge.");

        ContactObservation detected = p1Knowledge.Contacts[0];
        Assert.Equal(ContactState.Current, detected.State);
        Assert.Equal(0u, detected.SupportSurface); // Target is on east bank ground

        // Move hostile unit onto the bridge deck (X=100, Z=100)
        WorldState world = sim.World;
        EntityId targetId = default;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 2 && u.Position.X > 150f && u.Position.Y > 90f)
            {
                targetId = u.Id;
                break;
            }
        }
        Assert.NotEqual(default, targetId);

        // Update target position onto bridge center with supportSurface: 1
        world.UpdateUnitNavigation(targetId, new Vector2(100f, 100f), new Vector2(100f, 100f), false, supportSurface: 1);

        for (int t = 0; t < 10; t++)
        {
            sim.Step();
        }

        sim.UpdateKnowledge(p1Knowledge);
        ContactObservation onBridgeContact = default;
        bool foundOnBridge = false;
        foreach (ContactObservation c in p1Knowledge.Contacts)
        {
            if (c.State == ContactState.Current && Vector2.Distance(c.Position, new Vector2(100f, 100f)) < 5.0f)
            {
                onBridgeContact = c;
                foundOnBridge = true;
                break;
            }
        }

        Assert.True(foundOnBridge, "Hostile on bridge should be observed as current contact.");
        Assert.Equal(1u, onBridgeContact.SupportSurface);

        // Move all player 1 units far away to lose contact: target should transition to LastKnown
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 1)
            {
                world.UpdateUnitNavigation(u.Id, new Vector2(5f, 5f), new Vector2(5f, 5f), false, supportSurface: 0);
            }
        }

        // Advance simulation until contact becomes LastKnown
        bool sawLastKnownWithSurface = false;
        for (int t = 0; t < 120; t++)
        {
            sim.Step();
            sim.UpdateKnowledge(p1Knowledge);
            foreach (ContactObservation c in p1Knowledge.Contacts)
            {
                if (c.State == ContactState.LastKnown && c.SupportSurface == 1u)
                {
                    sawLastKnownWithSurface = true;
                    break;
                }
            }
            if (sawLastKnownWithSurface) break;
        }

        Assert.True(sawLastKnownWithSurface, "LastKnown contact must retain observed SupportSurface == 1 without polling hidden state.");
    }

    [Fact]
    public void Clarification2_WireProtocol_Version16_RoundTripContact_PreservesSupportSurface_NoEntityIdLeak()
    {
        Assert.Equal(19, WireProtocol.Version);
        Assert.Equal(28, WireProtocol.ContactSize);

        byte[] snapshotBuffer = new byte[WireProtocol.MaxMessageSize];

        ContactObservation originalContact = new(
            Id: new ContactId(99),
            State: ContactState.Current,
            Classification: ContactClassification.Vehicle,
            Type: 1,
            Position: new Vector2(100f, 100f),
            LastSeenTick: 42,
            TurretYaw: 1.57f,
            HullHeading: 1.57f,
            SupportSurface: 1u);

        OwnUnitObservation ownUnit = new(
            Id: new EntityId(10),
            Definition: 0,
            Position: new Vector2(40f, 100f),
            SupportSurface: 0u,
            NavStatus: PathRequestStatus.PathFound,
            Health: 100,
            Ammunition: 10,
            ReloadTicksRemaining: 0,
            AttackStatus: AttackExecutionStatus.None,
            TargetContactId: default,
            Stance: FireStance.FireAtWill,
            TurretYaw: 0f,
            HullHeading: 0f);

        int written = WireProtocol.WriteSnapshot(
            snapshotBuffer,
            sequence: 1,
            tick: 50,
            units: [ownUnit],
            contacts: [originalContact],
            events: []);

        Assert.True(written > 0);

        ClientReplica replica = new();
        Assert.True(replica.TryApply(snapshotBuffer.AsSpan(0, written)));

        Assert.Equal(1, replica.ContactCount);
        Assert.True(replica.TryGetContact(new ContactId(99), out ContactObservation receivedContact));

        Assert.Equal(originalContact.Id, receivedContact.Id);
        Assert.Equal(originalContact.State, receivedContact.State);
        Assert.Equal(originalContact.Classification, receivedContact.Classification);
        Assert.Equal(originalContact.Type, receivedContact.Type);
        Assert.Equal(1u, receivedContact.SupportSurface);
        Assert.Equal(1u, replica.SampleContactSupportSurface(new ContactId(99)));

        // Ensure no EntityId leaked into ContactObservation
        Assert.Null(typeof(ContactObservation).GetProperty("EntityId"));
        Assert.Null(typeof(ContactObservation).GetProperty("UnitId"));
    }

    [Fact]
    public void Clarification3_ElevatedDeckCombat_LineOfSight_DirectFire_AndAuthoritative3DCoordinates()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        using Simulation sim = BridgeCrossingScenario.CreateSimulation(geometry, catalog);
        PlayerKnowledge p1Knowledge = new(1);

        WorldState world = sim.World;
        sim.UpdateKnowledge(p1Knowledge);
        EntityId tankId = p1Knowledge.OwnUnits[0].Id;

        // Position player tank directly on bridge deck at (100, 100, SupportSurface=1)
        world.UpdateUnitNavigation(tankId, new Vector2(100f, 100f), new Vector2(100f, 100f), false, supportSurface: 1);

        // Find target on east bank at (160, 100)
        EntityId targetId = default;
        for (int i = 0; i < world.Count; i++)
        {
            UnitState u = world.GetUnitAt(i);
            if (u.Owner == 2 && Vector2.Distance(u.Position, new Vector2(160f, 100f)) < 1.0f)
            {
                targetId = u.Id;
                break;
            }
        }
        Assert.NotEqual(default, targetId);

        // Verify line of sight from muzzle on bridge (Y = 4.0 + 1.9 = 5.9m) to target on ground (Y = 4.0 + 1.8 = 5.8m)
        Vector3 muzzle = new(100f, 5.9f, 100f);
        Vector3 targetPos = new(160f, 5.8f, 100f);
        LineOfSightResult los = geometry.CheckLineOfSight(muzzle, targetPos);
        Assert.True(los.IsClear, "Line of sight from elevated bridge to east bank target must be clear.");

        // Advance simulation for detection
        for (int t = 0; t < 10; t++) sim.Step();

        sim.UpdateKnowledge(p1Knowledge);
        Assert.True(p1Knowledge.Contacts.Length > 0);
        ContactId targetContact = p1Knowledge.Contacts[0].Id;

        // Issue attack command
        Assert.True(sim.TryEnqueue(1, new AttackCommand(1, sim.Tick, tankId, targetContact)));

        // Advance simulation until combat event fires
        CombatEventObservation combatEvent = default;
        bool fired = false;
        for (int t = 0; t < 80; t++)
        {
            sim.Step();
            sim.UpdateKnowledge(p1Knowledge);
            if (p1Knowledge.Events.Length > 0)
            {
                combatEvent = p1Knowledge.Events[0];
                fired = true;
                break;
            }
        }

        Assert.True(fired, "Tank on bridge deck should fire at detected enemy target.");
        // Verify authoritative 3D origin height is elevated deck height + muzzle (~5.9m), NOT riverbed (0.5m)
        Assert.True(combatEvent.Origin.Y >= 5.8f, $"Combat event Origin.Y ({combatEvent.Origin.Y}) should reflect elevated bridge deck.");
        Assert.InRange(combatEvent.Target.Y, 4.0f, 6.0f);
    }

    [Fact]
    public void Clarification4_EndToEndBridgeCrossing_SupportSurfaceTransitions_WaterRejection()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        using Simulation sim = BridgeCrossingScenario.CreateSimulation(geometry, catalog);
        WorldState world = sim.World;
        PlayerKnowledge p1Knowledge = new(1);
        sim.UpdateKnowledge(p1Knowledge);
        EntityId tankId = p1Knowledge.OwnUnits[0].Id;

        Assert.True(world.TryGetUnit(tankId, out UnitState initialUnit));
        Assert.Equal(new Vector2(40f, 100f), initialUnit.Position);
        Assert.Equal(0u, initialUnit.SupportSurface);

        // Command tank to cross the river to east bank (160, 100)
        Vector2 destination = new(160f, 100f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, tankId, destination)));

        bool enteredBridge = false;
        bool crossedToEastBank = false;
        float minObservedY = float.MaxValue;

        // Advance simulation and track position, SupportSurface, and height
        for (int tick = 0; tick < 600; tick++)
        {
            sim.Step();
            if (!world.TryGetUnit(tankId, out UnitState currentUnit))
            {
                break;
            }

            Vector2 currentPos = currentUnit.Position;
            uint currentSurface = currentUnit.SupportSurface;
            float currentHeight = geometry.GetSurfaceHeight(currentPos, currentSurface);

            if (currentHeight < minObservedY)
            {
                minObservedY = currentHeight;
            }

            // When unit is between X in [78..122], it should be on the bridge deck
            if (currentPos.X >= 78f && currentPos.X <= 122f)
            {
                if (currentSurface == 1u)
                {
                    enteredBridge = true;
                    // While on bridge deck, height must be BridgeHeight (4.0m), never river height (0.5m)
                    Assert.InRange(currentHeight, 3.9f, 4.1f);
                }
            }

            if (currentPos.X >= 150f)
            {
                crossedToEastBank = true;
                Assert.Equal(0u, currentSurface); // Back on ground
                break;
            }
        }

        NavigationSystem navSys = (sim.Rules as RuleSet)!.FindSystem<NavigationSystem>()!;
        world.TryGetUnit(tankId, out UnitState dbgUnit);
        Assert.True(enteredBridge, $"Tank must receive SupportSurface == 1 while traversing bridge deck. TankId={tankId.Value}, Pos={dbgUnit.Position}, Status={navSys.GetUnitStatus(tankId)}, Surface={dbgUnit.SupportSurface}");
        Assert.True(crossedToEastBank, "Tank must successfully cross bridge and reach east bank.");
        Assert.True(minObservedY >= 3.8f, $"Tank height dropped to {minObservedY}m! Tank must never drop into riverbed (0.5m).");
    }

    [Fact]
    public void BridgeRampEntryConstraint_DirectSideEntry_IsRejected()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");
        WorldState world = new(geometry.HeightMap);
        OpsRules rules = new(geometry, catalog);
        using Simulation sim = new(world, rules);

        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
        // Tank starts on south river bank at (70, 130), away from bridge ramp at (75, 100)
        EntityId tankId = world.CreateUnit(1, tankIdx, new Vector2(70f, 130f), tankDef.Speed, tankDef.MaxHealth);

        // Command tank to move to center of bridge deck at (100, 100)
        Vector2 bridgeCenter = new(100f, 100f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, tankId, bridgeCenter)));

        bool enteredThroughRamp = false;

        for (int tick = 0; tick < 600; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(tankId, out UnitState u))
            {
                // When unit enters the bridge deck (SupportSurface == 1), verify its position entered near the ramp (X ~ 75, Z ~ 100)
                if (u.SupportSurface == 1u)
                {
                    // It must have approached near BridgeCrossingScenario.BridgeStart (75, 100), not jumped in from side (X=100, Z=104)
                    Assert.True(Vector2.Distance(u.Position, BridgeCrossingScenario.BridgeStart) < 14.0f,
                        $"Unit entered bridge deck from non-ramp position ({u.Position.X:F1}, {u.Position.Y:F1}).");
                    enteredThroughRamp = true;
                    break;
                }
            }
        }

        Assert.True(enteredThroughRamp, "Unit must enter bridge through designated ramp endpoints, not from the side.");
    }

    [Fact]
    public void Bug1_RejectedOrder_DoesNotResetSupportSurface_OnBridgeDeck()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        using Simulation sim = BridgeCrossingScenario.CreateSimulation(geometry, catalog);
        WorldState world = sim.World;
        PlayerKnowledge p1Knowledge = new(1);
        sim.UpdateKnowledge(p1Knowledge);
        EntityId tankId = p1Knowledge.OwnUnits[0].Id;

        // Place tank on the bridge deck with SupportSurface = 1
        world.UpdateUnitNavigation(tankId, new Vector2(100f, 100f), new Vector2(100f, 100f), false, supportSurface: 1);

        Assert.True(world.TryGetUnit(tankId, out UnitState onBridge));
        Assert.Equal(1u, onBridge.SupportSurface);
        Assert.Equal(4.0f, geometry.GetSurfaceHeight(onBridge.Position, onBridge.SupportSurface));

        // Order move into impassable water in river off the bridge (100, 140)
        Vector2 invalidWaterGoal = new(100f, 140f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, tankId, invalidWaterGoal)));

        // Advance simulation: command is processed and rejected with InvalidDestination
        sim.Step();

        NavigationSystem nav = (sim.Rules as RuleSet)!.FindSystem<NavigationSystem>()!;
        Assert.Equal(PathRequestStatus.InvalidDestination, nav.GetUnitStatus(tankId));

        // Verify tank remained in place and SupportSurface remained 1 (did not drop to 0 or 0.5m)
        Assert.True(world.TryGetUnit(tankId, out UnitState afterRejection));
        Assert.Equal(new Vector2(100f, 100f), afterRejection.Position);
        Assert.Equal(1u, afterRejection.SupportSurface);
        Assert.Equal(4.0f, geometry.GetSurfaceHeight(afterRejection.Position, afterRejection.SupportSurface));
    }

    [Fact]
    public void Bug2_MovementUnderBridge_DryPassage_PreservesGroundSupportSurface_AndReachesDestination()
    {
        // Flat dry map 200 x 200 m, ground height 0
        ushort[] flatHeights = new ushort[BridgeCrossingScenario.MapResolution * BridgeCrossingScenario.MapResolution];
        HeightMap flatHeightMap = new(BridgeCrossingScenario.MapResolution, BridgeCrossingScenario.MapResolution, BridgeCrossingScenario.MapSpacing, 0.01f, flatHeights);

        // Bridge from [75, 100] to [125, 100], width 8 m, deck at height 4 m, thickness 0.8m
        BridgeStructure bridge = new(
            new StructureId(1),
            new Vector2(75f, 100f),
            new Vector2(125f, 100f),
            8.0f,
            4.0f,
            4.0f,
            0.8f);

        StaticGeometry dryGeometry = new(
            flatHeightMap,
            ReadOnlySpan<OpaqueStructure>.Empty,
            [bridge],
            ReadOnlySpan<TerrainObstacle>.Empty);

        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();
        UnitDefinition miniDef = catalog.RequireDefinition("mini_vehicle"); // DefaultVehicle, radius 0.4, speed 6.0

        WorldState world = new(dryGeometry.HeightMap);
        OpsRules rules = new(dryGeometry, catalog);
        using Simulation sim = new(world, rules);

        ushort miniIdx = catalog.RequireDefinitionIndex("mini_vehicle");
        // Vehicle with DefaultVehicle, radius 0.4m, starts at [100, 90], goal [100, 110], SupportSurface = 0
        Vector2 startPos = new(100f, 90f);
        Vector2 goalPos = new(100f, 110f);
        EntityId unitId = world.CreateUnit(1, miniIdx, startPos, miniDef.Speed, miniDef.MaxHealth);

        Assert.True(world.TryGetUnit(unitId, out UnitState initial));
        Assert.Equal(0u, initial.SupportSurface);
        Assert.Equal(0.0f, dryGeometry.GetSurfaceHeight(initial.Position, initial.SupportSurface));

        // Step simulation while stationary: surface must stay 0 and height 0.0m
        sim.Step();
        Assert.True(world.TryGetUnit(unitId, out UnitState stationary));
        Assert.Equal(0u, stationary.SupportSurface);
        Assert.Equal(0.0f, dryGeometry.GetSurfaceHeight(stationary.Position, stationary.SupportSurface));

        // Order move across under the bridge to [100, 110]
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, unitId, goalPos)));

        bool reachedGoal = false;
        Vector2 prevPos = startPos;
        float maxAllowedStep = miniDef.Speed * Simulation.TickSeconds + 0.05f;

        for (int tick = 0; tick < 200; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(unitId, out UnitState u))
            {
                // Displacement per tick must be bounded (no teleportation)
                float stepDist = Vector2.Distance(u.Position, prevPos);
                Assert.True(stepDist <= maxAllowedStep, $"Teleportation detected: displacement was {stepDist:F3}m (max allowed: {maxAllowedStep:F3}m) at tick {tick}.");
                prevPos = u.Position;

                // SupportSurface must remain 0 on every step (never teleport onto bridge deck)
                Assert.Equal(0u, u.SupportSurface);
                // Height must remain ground height (0.0 m) on every step
                Assert.Equal(0.0f, dryGeometry.GetSurfaceHeight(u.Position, u.SupportSurface));

                // Path must pass directly under the span (no detour to ramp ends at X=75 or X=125)
                Assert.InRange(u.Position.X, 99.0f, 101.0f);

                if (Vector2.Distance(u.Position, goalPos) <= 0.1f)
                {
                    reachedGoal = true;
                    break;
                }
            }
        }

        Assert.True(reachedGoal, $"Vehicle failed to reach destination {goalPos} under bridge within 200 ticks. Final position: {prevPos}.");
    }

    [Fact]
    public void WaterUnderBridge_RemainsImpassable_ForVehicleOnGround()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        UnitDefinition tankDef = catalog.RequireDefinition("prototype_tank");
        WorldState world = new(geometry.HeightMap);
        OpsRules rules = new(geometry, catalog);
        using Simulation sim = new(world, rules);

        ushort tankIdx = catalog.RequireDefinitionIndex("prototype_tank");
        // Tank starts on ground west of river at (80, 90), outside bridge footprint (bridge is at Z in [96..104])
        Vector2 bankPos = new(80f, 90f);
        EntityId tankId = world.CreateUnit(1, tankIdx, bankPos, tankDef.Speed, tankDef.MaxHealth);

        // Command tank to cross perpendicular through river to east bank at (120, 90)
        Vector2 waterGoal = new(120f, 90f);
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, tankId, waterGoal)));

        for (int tick = 0; tick < 50; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(tankId, out UnitState u))
            {
                // Tank must never enter the river water obstacle [85..115]
                Assert.True(u.Position.X < BridgeCrossingScenario.RiverMinX,
                    $"Tank entered impassable water at X={u.Position.X:F1}.");
            }
        }
    }

    [Fact]
    public void Bug3_BridgeDeckVolume_BlocksLineOfSight_RayFromBelowToAboveDeck()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();

        // Bridge center is at (100, 100), deck height 4.0m, thickness 0.8m, deck volume Y in [3.2..4.0]
        Vector3 underBridge = new(100f, 1.0f, 100f);
        Vector3 aboveBridge = new(100f, 6.0f, 100f);

        // Ray passing vertically through deck from below to above must be blocked
        Assert.False(geometry.IsLineOfSightClear(underBridge, aboveBridge));

        LineOfSightResult losVertical = geometry.CheckLineOfSight(underBridge, aboveBridge);
        Assert.False(losVertical.IsClear);
        Assert.Equal(ObstructionKind.Structure, losVertical.ObstructionKind);
        Assert.Equal(1, losVertical.ObstructingStructureId.Value);
        Assert.InRange(losVertical.HitPoint.Y, 3.19f, 3.21f); // Hits bottom of deck slab at 3.2m

        // Ray passing under bridge horizontally must be clear
        Vector3 underStart = new(100f, 1.0f, 90f);
        Vector3 underEnd = new(100f, 1.0f, 110f);
        Assert.True(geometry.IsLineOfSightClear(underStart, underEnd));

        // Ray passing above bridge horizontally must be clear
        Vector3 aboveStart = new(100f, 5.0f, 90f);
        Vector3 aboveEnd = new(100f, 5.0f, 110f);
        Assert.True(geometry.IsLineOfSightClear(aboveStart, aboveEnd));
    }

    [Fact]
    public void Bug4_HierarchicalNavGraph_SelectsCorrectGraphForWheeledFootProfile_ThroughForest()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        NavigationGrid grid = new(geometry, enableHierarchical: true);

        // Foot profile with radius 0.8m (MovementClass.Wheeled) and Foot capabilities
        HierarchicalNavGraph wheeledFootGraph = grid.GetHierarchicalGraph(MovementClass.Wheeled, TraversabilityCapabilities.Foot);
        Assert.NotNull(wheeledFootGraph);
        Assert.Equal(0.8f, wheeledFootGraph.ClassRadius);
        Assert.True(wheeledFootGraph.Capabilities.HasFlag(TraversabilityCapabilities.DenseForest));

        // Start west of forest at (130, 70), goal east of forest at (180, 70)
        Vector2 start = new(130f, 70f);
        Vector2 goal = new(180f, 70f);

        int startComp = wheeledFootGraph.GetComponentId(start, 0.8f);
        int targetComp = wheeledFootGraph.GetComponentId(goal, 0.8f);

        Assert.True(startComp > 0);
        Assert.True(targetComp > 0);
        Assert.Equal(startComp, targetComp); // Confirms connectivity through dense forest for wheeled foot profile
    }

    [Fact]
    public void Bug5_MissOutsideBridgeFootprint_ReceivesHeightMapGroundHeight_NotDeckHeight()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();

        // Bridge center is at (100, 100), width is 8.0m (half-width 4.0m)
        // Offset of 6.0m in lateral direction (Z=106) is outside bridge footprint
        Vector2 onBridgePoint = new(100f, 100f);
        Vector2 offBridgePoint = new(100f, 106f);

        Assert.Equal(4.0f, geometry.GetSurfaceHeight(onBridgePoint, 1u));
        // Off bridge, even when passing supportSurface = 1u, surface height must be riverbed (0.5m), NOT deck (4.0m)
        Assert.Equal(0.5f, geometry.GetSurfaceHeight(offBridgePoint, 1u));
    }

    [Fact]
    public void BridgeDeck_ToBothBanks_SeparateNewOrders_ReachDestinations()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        UnitDefinition miniDef = catalog.RequireDefinition("mini_vehicle");
        WorldState world = new(geometry.HeightMap);
        OpsRules rules = new(geometry, catalog);
        using Simulation sim = new(world, rules);

        ushort miniIdx = catalog.RequireDefinitionIndex("mini_vehicle");
        Vector2 westBank = new(40f, 100f);
        Vector2 bridgeCenter = new(100f, 100f);
        Vector2 eastBank = new(160f, 100f);
        EntityId unitId = world.CreateUnit(1, miniIdx, westBank, miniDef.Speed, miniDef.MaxHealth);

        // 1. Order vehicle to middle of bridge [100, 100]
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, unitId, bridgeCenter)));

        bool reachedBridgeCenter = false;
        for (int tick = 0; tick < 300; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(unitId, out UnitState u))
            {
                if (Vector2.Distance(u.Position, bridgeCenter) <= 0.5f && u.SupportSurface == 1u)
                {
                    reachedBridgeCenter = true;
                    break;
                }
            }
        }
        Assert.True(reachedBridgeCenter, "Vehicle must reach bridge center with SupportSurface == 1.");

        for (int t = 0; t < 10; t++) sim.Step();
        Assert.True(world.TryGetUnit(unitId, out UnitState stationaryOnDeck));
        Assert.Equal(1u, stationaryOnDeck.SupportSurface);

        // 2. Issue new order from middle of bridge to east bank [160, 100]
        NavigationSystem navSys = (sim.Rules as RuleSet)!.FindSystem<NavigationSystem>()!;
        Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, unitId, eastBank)));

        bool reachedEastBank = false;
        for (int tick = 0; tick < 300; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(unitId, out UnitState u))
            {
                if (Vector2.Distance(u.Position, eastBank) <= 0.5f)
                {
                    reachedEastBank = true;
                    Assert.Equal(0u, u.SupportSurface);
                    Assert.Equal(4.0f, geometry.GetSurfaceHeight(u.Position, u.SupportSurface));
                    break;
                }
            }
        }
        Assert.True(reachedEastBank, $"Vehicle failed to reach east bank from bridge deck. Final status: {navSys.GetUnitStatus(unitId)}");

        for (int t = 0; t < 10; t++) sim.Step();

        // 3. Issue new order to return back to bridge center [100, 100]
        Assert.True(sim.TryEnqueue(1, new MoveCommand(3, sim.Tick, unitId, bridgeCenter)));
        reachedBridgeCenter = false;
        for (int tick = 0; tick < 450; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(unitId, out UnitState u))
            {
                if (Vector2.Distance(u.Position, bridgeCenter) <= 0.5f && u.SupportSurface == 1u)
                {
                    reachedBridgeCenter = true;
                    break;
                }
            }
        }
        Assert.True(reachedBridgeCenter, "Vehicle must return to bridge center with SupportSurface == 1.");

        for (int t = 0; t < 10; t++) sim.Step();

        // 4. Issue new order from middle of bridge to west bank [40, 100]
        Assert.True(sim.TryEnqueue(1, new MoveCommand(4, sim.Tick, unitId, westBank)));
        bool reachedWestBank = false;
        for (int tick = 0; tick < 450; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(unitId, out UnitState u))
            {
                if (Vector2.Distance(u.Position, westBank) <= 0.5f)
                {
                    reachedWestBank = true;
                    Assert.Equal(0u, u.SupportSurface);
                    Assert.Equal(4.0f, geometry.GetSurfaceHeight(u.Position, u.SupportSurface));
                    break;
                }
            }
        }
        Assert.True(reachedWestBank, $"Vehicle failed to reach west bank from bridge deck. Final status: {navSys.GetUnitStatus(unitId)}");
    }

    [Fact]
    public void BridgeDeck_RetargetingDuringCrossing_TransitionsCorrectly()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        UnitDefinition miniDef = catalog.RequireDefinition("mini_vehicle");
        WorldState world = new(geometry.HeightMap);
        OpsRules rules = new(geometry, catalog);
        using Simulation sim = new(world, rules);

        ushort miniIdx = catalog.RequireDefinitionIndex("mini_vehicle");
        Vector2 westBank = new(40f, 100f);
        Vector2 eastBank = new(160f, 100f);
        EntityId unitId = world.CreateUnit(1, miniIdx, westBank, miniDef.Speed, miniDef.MaxHealth);

        // Initial command: cross bridge toward east bank
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, unitId, eastBank)));

        bool retargetedOnDeck = false;
        NavigationSystem navSys = (sim.Rules as RuleSet)!.FindSystem<NavigationSystem>()!;

        // Advance until unit is on the deck (X in [90..110] with SupportSurface == 1)
        for (int tick = 0; tick < 300; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(unitId, out UnitState u))
            {
                if (u.SupportSurface == 1u && u.Position.X >= 90f && u.Position.X <= 110f)
                {
                    // Retarget while moving along bridge deck back to west bank
                    Assert.True(sim.TryEnqueue(1, new MoveCommand(2, sim.Tick, unitId, westBank)));
                    retargetedOnDeck = true;
                    break;
                }
            }
        }
        Assert.True(retargetedOnDeck, "Unit must reach bridge deck before retargeting.");

        // Continue simulation: unit must reverse direction, exit via west ramp, and reach west bank
        bool reachedWestBank = false;
        for (int tick = 0; tick < 300; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(unitId, out UnitState u))
            {
                if (Vector2.Distance(u.Position, westBank) <= 0.5f)
                {
                    reachedWestBank = true;
                    Assert.Equal(0u, u.SupportSurface);
                    Assert.Equal(4.0f, geometry.GetSurfaceHeight(u.Position, u.SupportSurface));
                    break;
                }
            }
        }
        Assert.True(reachedWestBank, $"Vehicle failed to retarget and reach west bank. Final status: {navSys.GetUnitStatus(unitId)}");
    }

    [Fact]
    public void BridgeDeck_ReachesBankDestination_SurfaceTransitionsOneToZero()
    {
        StaticGeometry geometry = BridgeCrossingScenario.CreateStaticGeometry();
        UnitCatalog catalog = BridgeCrossingScenario.LoadCatalog();

        UnitDefinition miniDef = catalog.RequireDefinition("mini_vehicle");
        WorldState world = new(geometry.HeightMap);
        OpsRules rules = new(geometry, catalog);
        using Simulation sim = new(world, rules);

        ushort miniIdx = catalog.RequireDefinitionIndex("mini_vehicle");
        Vector2 deckPos = new(100f, 100f);
        Vector2 bankGoal = new(150f, 100f);
        EntityId unitId = world.CreateUnit(1, miniIdx, deckPos, miniDef.Speed, miniDef.MaxHealth);

        // Explicitly initialize navigation state on the bridge deck with SupportSurface = 1
        world.UpdateUnitNavigation(unitId, deckPos, deckPos, false, supportSurface: 1u);

        Assert.True(world.TryGetUnit(unitId, out UnitState initial));
        Assert.Equal(1u, initial.SupportSurface);
        Assert.Equal(4.0f, geometry.GetSurfaceHeight(initial.Position, initial.SupportSurface));

        // Issue order from bridge deck to east bank
        Assert.True(sim.TryEnqueue(1, new MoveCommand(1, sim.Tick, unitId, bankGoal)));

        bool sawTransitionToOneToZero = false;
        bool reachedDestination = false;
        float minHeight = float.MaxValue;
        NavigationSystem navSys = (sim.Rules as RuleSet)!.FindSystem<NavigationSystem>()!;

        for (int tick = 0; tick < 300; tick++)
        {
            sim.Step();
            if (world.TryGetUnit(unitId, out UnitState u))
            {
                float h = geometry.GetSurfaceHeight(u.Position, u.SupportSurface);
                if (h < minHeight) minHeight = h;

                // While on bridge structure (X in [75..125]), SupportSurface is 1
                // When past east ramp (X > 125), SupportSurface transitions to 0
                if (u.Position.X > 125.5f)
                {
                    if (u.SupportSurface == 0u)
                    {
                        sawTransitionToOneToZero = true;
                    }
                }

                if (Vector2.Distance(u.Position, bankGoal) <= 0.5f)
                {
                    reachedDestination = true;
                    Assert.Equal(0u, u.SupportSurface);
                    Assert.Equal(4.0f, geometry.GetSurfaceHeight(u.Position, u.SupportSurface));
                    break;
                }
            }
        }

        Assert.True(sawTransitionToOneToZero, "SupportSurface must transition from 1 to 0 after exiting the bridge ramp.");
        Assert.True(reachedDestination, $"Unit failed to reach bank destination. Final status: {navSys.GetUnitStatus(unitId)}");
        Assert.True(minHeight >= 3.9f, $"Unit height dropped below deck level: minHeight={minHeight}m.");
    }
}
