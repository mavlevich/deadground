using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class ForestVisibilityScenarioTests
{
    private static readonly StaticGeometry SharedGeometry = ForestVisibilityScenario.CreateStaticGeometry();
    private static readonly UnitCatalog SharedCatalog = ScenarioContent.LoadCatalog("forest-visibility");

    private static Simulation CreateSimulation(ulong matchSeed = 42UL) =>
        ForestVisibilityScenario.CreateSimulation(SharedGeometry, SharedCatalog, matchSeed: matchSeed);

    [Fact]
    public void ScenarioSetup_HasValidGeometry_Catalog_AndTeams()
    {
        StaticGeometry geometry = SharedGeometry;
        MatchSetup setup = ForestVisibilityScenario.CreateMatchSetup(geometry, SharedCatalog);

        Assert.Equal("ForestVisibility", setup.RuleSetId);
        Assert.Equal(256f, geometry.HeightMap.Width);
        Assert.Equal(256f, geometry.HeightMap.Depth);
        Assert.NotEmpty(geometry.VegetationZones);
        Assert.Equal(7, geometry.VegetationZones.Count);
    }

    [Fact]
    public void InitialForces_PlacedCorrectly()
    {
        Simulation sim = CreateSimulation();
        // Side 1: 5 units across test corridors
        // Side 2: 5 target units across test corridors
        Assert.Equal(10, sim.World.Count);

        Assert.True(sim.World.TryGetUnit(new EntityId(1), out UnitState obsGround));
        Assert.Equal(1, obsGround.Owner);
        Assert.Equal(ForestVisibilityScenario.ObserverGroundStart, obsGround.Position);

        Assert.True(sim.World.TryGetUnit(new EntityId(2), out UnitState obsRidge));
        Assert.Equal(1, obsRidge.Owner);
        Assert.Equal(ForestVisibilityScenario.ObserverRidgeStart, obsRidge.Position);

        Assert.True(sim.World.TryGetUnit(new EntityId(3), out UnitState obsBelts));
        Assert.Equal(1, obsBelts.Owner);
        Assert.Equal(ForestVisibilityScenario.ObserverBeltsStart, obsBelts.Position);

        Assert.True(sim.World.TryGetUnit(new EntityId(4), out UnitState obsSparse));
        Assert.Equal(1, obsSparse.Owner);
        Assert.Equal(ForestVisibilityScenario.ObserverSparseStart, obsSparse.Position);

        Assert.True(sim.World.TryGetUnit(new EntityId(5), out UnitState obsOpen));
        Assert.Equal(1, obsOpen.Owner);
        Assert.Equal(ForestVisibilityScenario.ObserverOpenStart, obsOpen.Position);

        Assert.True(sim.World.TryGetUnit(new EntityId(6), out UnitState tgtEdge));
        Assert.Equal(2, tgtEdge.Owner);
        Assert.Equal(ForestVisibilityScenario.TargetEdgePos, tgtEdge.Position);

        Assert.True(sim.World.TryGetUnit(new EntityId(7), out UnitState tgtClearing));
        Assert.Equal(2, tgtClearing.Owner);
        Assert.Equal(ForestVisibilityScenario.TargetClearingPos, tgtClearing.Position);

        Assert.True(sim.World.TryGetUnit(new EntityId(8), out UnitState tgtBehindBelts));
        Assert.Equal(2, tgtBehindBelts.Owner);
        Assert.Equal(ForestVisibilityScenario.TargetBehindBeltsPos, tgtBehindBelts.Position);

        Assert.True(sim.World.TryGetUnit(new EntityId(9), out UnitState tgtSparse));
        Assert.Equal(2, tgtSparse.Owner);
        Assert.Equal(ForestVisibilityScenario.TargetSparsePos, tgtSparse.Position);

        Assert.True(sim.World.TryGetUnit(new EntityId(10), out UnitState tgtOpen));
        Assert.Equal(2, tgtOpen.Owner);
        Assert.Equal(ForestVisibilityScenario.TargetOpenPos, tgtOpen.Position);
    }

    [Fact]
    public void Scenario_InitialPerceptionState()
    {
        Simulation sim = CreateSimulation();
        LocalSession session = new(sim, player: 1);

        // Advance 5 ticks so perception updates and replicates to client
        for (int i = 0; i < 5; i++)
        {
            session.Advance(1.0 / Simulation.TickRate);
        }

        ReadOnlySpan<ContactObservation> contacts = session.Replica.Contacts;

        // Ground observer is at (60, 110):
        // Target 1 (edge at 88, 75): 8m into dense forest -> Detected!
        // Target 2 (clearing at 120, 110): surrounded by dense forest -> Hidden!
        // Target 3 (behind belts at 150, 205): 2 shelter belts (30m forest) -> Hidden!
        // Target 4 (sparse at 120, 35): sparse forest -> Detected!
        // Target 5 (open at 180, 160): open ground -> Detected!

        bool seesTarget1 = false;
        bool seesTarget2 = false;
        bool seesTarget3 = false;
        bool seesTarget4 = false;
        bool seesTarget5 = false;

        foreach (ContactObservation c in contacts)
        {
            Console.WriteLine($"CONTACT: Id={c.Id.Value} State={c.State} Pos={c.Position} Class={c.Classification}");
            if (Vector2.Distance(c.Position, ForestVisibilityScenario.TargetEdgePos) < 2.0f) seesTarget1 = true;
            if (Vector2.Distance(c.Position, ForestVisibilityScenario.TargetClearingPos) < 2.0f) seesTarget2 = true;
            if (Vector2.Distance(c.Position, ForestVisibilityScenario.TargetBehindBeltsPos) < 2.0f) seesTarget3 = true;
            if (Vector2.Distance(c.Position, ForestVisibilityScenario.TargetSparsePos) < 2.0f) seesTarget4 = true;
            if (Vector2.Distance(c.Position, ForestVisibilityScenario.TargetOpenPos) < 2.0f) seesTarget5 = true;
        }

        Assert.True(seesTarget1, "Target 1 at forest edge should be detected");
        Assert.False(seesTarget2, "Target 2 in clearing behind dense forest should be hidden");
        Assert.False(seesTarget3, "Target 3 behind dual shelter belts should be hidden");
        Assert.True(seesTarget4, "Target 4 in sparse forest should be detected");
        Assert.True(seesTarget5, "Target 5 on open ground should be detected");
    }

    [Fact]
    public void Advance_SpotsTargetInClearing()
    {
        Simulation sim = CreateSimulation();
        LocalSession session = new(sim, player: 1);

        // Scout at (60, 110) moves forward to (115, 110) towards clearing
        EntityId scoutId = new(1);
        session.Move(scoutId, new Vector2(115f, 110f));

        // Advance ticks until scout approaches clearing
        bool spottedInClearing = false;
        for (int i = 0; i < 150; i++)
        {
            session.Advance(1.0 / Simulation.TickRate);
            foreach (ContactObservation c in session.Replica.Contacts)
            {
                if (Vector2.Distance(c.Position, ForestVisibilityScenario.TargetClearingPos) < 2.0f)
                {
                    spottedInClearing = true;
                    Assert.Equal(ContactState.Current, c.State);
                    break;
                }
            }
            if (spottedInClearing) break;
        }

        Assert.True(spottedInClearing, "Scout advancing towards clearing should spot Target 2");
    }

    [Fact]
    public void Retreat_TransitionsToLastKnown_RetainedBehindForest()
    {
        Simulation sim = CreateSimulation();
        LocalSession session = new(sim, player: 1);
        EntityId scoutId = new(1);

        // Advance to spot target
        session.Move(scoutId, new Vector2(115f, 110f));
        for (int i = 0; i < 150; i++)
        {
            session.Advance(1.0 / Simulation.TickRate);
            bool spotted = false;
            foreach (ContactObservation c in session.Replica.Contacts)
            {
                if (Vector2.Distance(c.Position, ForestVisibilityScenario.TargetClearingPos) < 2.0f)
                {
                    spotted = true;
                    break;
                }
            }
            if (spotted) break;
        }

        // Retreat back outside the forest to (60, 110)
        session.Move(scoutId, new Vector2(60f, 110f));
        for (int i = 0; i < 150; i++)
        {
            session.Advance(1.0 / Simulation.TickRate);
        }

        // Contact should now transition to LastKnown because LOS is broken by forest
        ContactObservation? clearingContact = null;
        foreach (ContactObservation c in session.Replica.Contacts)
        {
            if (Vector2.Distance(c.Position, ForestVisibilityScenario.TargetClearingPos) < 2.0f)
            {
                clearingContact = c;
                break;
            }
        }

        Assert.NotNull(clearingContact);
        Assert.Equal(ContactState.LastKnown, clearingContact.Value.State);

        // Advance further ticks while obscured: LastKnown must NOT be confirmed empty or cleared!
        for (int i = 0; i < 50; i++)
        {
            session.Advance(1.0 / Simulation.TickRate);
        }

        bool stillRetained = false;
        foreach (ContactObservation c in session.Replica.Contacts)
        {
            if (Vector2.Distance(c.Position, ForestVisibilityScenario.TargetClearingPos) < 2.0f)
            {
                stillRetained = true;
                Assert.Equal(ContactState.LastKnown, c.State);
                break;
            }
        }
        Assert.True(stillRetained, "LastKnown behind forest must remain retained and not falsely cleared");
    }

    [Fact]
    public void BotKnowledge_NoDataLeaks()
    {
        Simulation sim = CreateSimulation();
        LocalSession session = new(sim, player: 1);

        for (int i = 0; i < 10; i++)
        {
            session.Advance(1.0 / Simulation.TickRate);
        }

        // Check that player 1 replica does not contain unobserved units
        foreach (ContactObservation c in session.Replica.Contacts)
        {
            // Neither Target 2 nor Target 3 should be in contacts
            Assert.False(Vector2.Distance(c.Position, ForestVisibilityScenario.TargetClearingPos) < 2.0f, "Hidden clearing target must not leak");
            Assert.False(Vector2.Distance(c.Position, ForestVisibilityScenario.TargetBehindBeltsPos) < 2.0f, "Hidden belt target must not leak");
        }
    }

    [Fact]
    public void FogOfWar_AgreesWithDetection()
    {
        StaticGeometry geo = SharedGeometry;
        Vector3 eye = new(ForestVisibilityScenario.ObserverGroundStart.X, 1.5f, ForestVisibilityScenario.ObserverGroundStart.Y);

        // Check target 2 (clearing): blocked in geometry
        Vector3 targetClearing = new(ForestVisibilityScenario.TargetClearingPos.X, 1.5f, ForestVisibilityScenario.TargetClearingPos.Y);
        LineOfSightResult los = geo.CheckLineOfSight(eye, targetClearing);
        Assert.False(los.IsClear);
        Assert.Equal(ObstructionKind.Vegetation, los.ObstructionKind);

        // Check IsVegetationClear directly
        bool vegClear = geo.IsVegetationClear(eye, targetClearing);
        Assert.False(vegClear);

        // Check open terrain target from open corridor observer: clear
        Vector3 eyeOpen = new(ForestVisibilityScenario.ObserverOpenStart.X, 1.5f, ForestVisibilityScenario.ObserverOpenStart.Y);
        Vector3 targetOpen = new(ForestVisibilityScenario.TargetOpenPos.X, 1.5f, ForestVisibilityScenario.TargetOpenPos.Y);
        Assert.True(geo.IsVegetationClear(eyeOpen, targetOpen));
    }

    [Fact]
    public void Ustye_RoadAndSawmillClearings_RemainClear()
    {
        StaticGeometry ustyeGeo = UstyeGroundScenario.CreateStaticGeometry();

        // 1. Road north corridor: (1600, 1000) to (1660, 1000) passes through WOOD-D1 polygon.
        // Because of the 14m road clearing corridor, line of sight along the road must be clear.
        float yA = ustyeGeo.HeightMap.GetHeight(1600f, 1000f) + 1.5f;
        float yB = ustyeGeo.HeightMap.GetHeight(1660f, 1000f) + 1.5f;
        Vector3 roadStart = new(1600f, yA, 1000f);
        Vector3 roadEnd = new(1660f, yB, 1000f);

        Assert.True(ustyeGeo.IsLineOfSightClear(roadStart, roadEnd), "Road north corridor on Ustye must not be blocked by WOOD-D1 forest.");

        // 2. Sawmill yard (TIMBER-AREA): (2450, 3560) to (2650, 3560) passes inside WOOD-D2 polygon.
        // Because of the TIMBER-AREA clearing polygon, sight across the sawmill yard must be clear.
        float yYardA = ustyeGeo.HeightMap.GetHeight(2450f, 3560f) + 1.5f;
        float yYardB = ustyeGeo.HeightMap.GetHeight(2650f, 3560f) + 1.5f;
        Vector3 yardStart = new(2450f, yYardA, 3560f);
        Vector3 yardEnd = new(2650f, yYardB, 3560f);

        LineOfSightResult yardLos = ustyeGeo.CheckLineOfSight(yardStart, yardEnd);
        Assert.True(yardLos.IsClear, $"Sawmill yard (TIMBER-AREA) must not be obstructed, but got: {yardLos}");
        Assert.True(ustyeGeo.IsVegetationClear(yardStart, yardEnd));

        // 3. Off the road into the forest: from road at (1600, 1000) to 50m north at (1600, 950).
        // Traverses > 35m of dense forest -> optical depth > 1.0 -> must be blocked!
        float yForest = ustyeGeo.HeightMap.GetHeight(1600f, 950f) + 1.5f;
        Vector3 forestPoint = new(1600f, yForest, 950f);

        Assert.False(ustyeGeo.IsLineOfSightClear(roadStart, forestPoint), "Line of sight off-road deep into WOOD-D1 must be blocked by dense vegetation.");
    }
}
