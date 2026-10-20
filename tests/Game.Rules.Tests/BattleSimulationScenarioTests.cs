using System.Numerics;
using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class BattleSimulationScenarioTests
{
    [Fact]
    public void HeadlessBattleScenario_ShortDeterministicRun_ShotsHitsAndMissesOccur()
    {
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");
        using Simulation sim = BattleScenario.CreateSimulation(geometry, catalog, matchSeed: 1337UL);

        OpsRules? rules = sim.Rules as OpsRules;
        Assert.NotNull(rules);
        CombatSystem combat = rules.FindSystem<CombatSystem>()!;
        DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
        WorldState world = sim.World;

        // Position both sides across the western saddle (~250m apart) so combat begins quickly
        for (int i = 0; i < world.Count; i++)
        {
            UnitState unit = world.GetUnitAt(i);
            Vector2 pos = unit.Owner == 1
                ? new Vector2(560f, 700f + (i * 15f))
                : new Vector2(700f, 560f + ((i - 6) * 15f));
            world.SetPosition(unit.Id, pos);
            // Face enemy direction
            float desiredYaw = unit.Owner == 1 ? -MathF.PI / 4f : 3f * MathF.PI / 4f;
            world.SetTurretYaw(unit.Id, desiredYaw);
        }

        bool hasHit = false;
        bool hasMiss = false;
        int maxTicks = 400; // 20 seconds of simulation

        for (int tick = 1; tick <= maxTicks; tick++)
        {
            sim.Step();

            for (int p = 1; p <= 2; p++)
            {
                ReadOnlySpan<CombatEventObservation> events = combat.GetEvents(p);
                for (int e = 0; e < events.Length; e++)
                {
                    if (events[e].Kind == CombatEventKind.ShotFired)
                    {
                        if (events[e].Result is HitResult.Penetrated or HitResult.Deflected)
                        {
                            hasHit = true;
                        }
                        else if (events[e].Result == HitResult.Miss)
                        {
                            hasMiss = true;
                        }
                    }
                }
            }

            if (tick % 20 == 0)
            {
                for (int u = 0; u < world.Count; u++)
                {
                    UnitState unit = world.GetUnitAt(u);
                    SideContactTracker tracker = detection.GetTracker(unit.Owner);
                    if (tracker.Contacts.Length > 0)
                    {
                        ContactId bestContact = tracker.Contacts[0].Id;
                        float bestDist = float.MaxValue;
                        for (int c = 0; c < tracker.Contacts.Length; c++)
                        {
                            float d = Vector2.Distance(unit.Position, tracker.Contacts[c].Position);
                            if (d < bestDist)
                            {
                                bestDist = d;
                                bestContact = tracker.Contacts[c].Id;
                            }
                        }
                        sim.TryEnqueue(unit.Owner, new AttackCommand((uint)(10000 + tick * 50 + u), tick, unit.Id, bestContact));
                    }
                }
            }

            if (combat.TotalShotsFired > 0 && hasHit && hasMiss)
            {
                break;
            }
        }

        Assert.True(combat.TotalShotsFired > 0, "Shots should have fired within 450 ticks.");
        Assert.True(hasHit, "At least one hit (penetration or deflection) should occur.");
        Assert.True(hasMiss, "At least one miss should occur.");
    }

    [Fact]
    public void TwoSimulations_SameSeed_ProduceIdenticalCombatEvents_DifferentSeed_ChangesThem()
    {
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        UnitCatalog catalog = ScenarioContent.LoadCatalog("battle");

        List<CombatEventObservation> RunSimulation(ulong seed)
        {
            using Simulation sim = BattleScenario.CreateSimulation(geometry, catalog, matchSeed: seed);
            OpsRules rules = (OpsRules)sim.Rules;
            CombatSystem combat = rules.FindSystem<CombatSystem>()!;
            DetectionSystem detection = rules.FindSystem<DetectionSystem>()!;
            WorldState world = sim.World;

            // Position both sides close across the saddle so combat occurs
            for (int i = 0; i < world.Count; i++)
            {
                UnitState unit = world.GetUnitAt(i);
                Vector2 pos = unit.Owner == 1
                    ? new Vector2(560f, 700f + (i * 15f))
                    : new Vector2(700f, 560f + ((i - 6) * 15f));
                world.SetPosition(unit.Id, pos);
                float desiredYaw = unit.Owner == 1 ? -MathF.PI / 4f : 3f * MathF.PI / 4f;
                world.SetTurretYaw(unit.Id, desiredYaw);
            }

            List<CombatEventObservation> recordedEvents = new();
            HashSet<uint> seen = new();

            for (int tick = 1; tick <= 150; tick++)
            {
                sim.Step();

                for (int p = 1; p <= 2; p++)
                {
                    ReadOnlySpan<CombatEventObservation> events = combat.GetEvents(p);
                    for (int e = 0; e < events.Length; e++)
                    {
                        if (events[e].Kind == CombatEventKind.ShotFired && seen.Add(events[e].EventId))
                        {
                            recordedEvents.Add(events[e]);
                        }
                    }
                }

                if (tick % 20 == 0)
                {
                    for (int u = 0; u < world.Count; u++)
                    {
                        UnitState unit = world.GetUnitAt(u);
                        SideContactTracker tracker = detection.GetTracker(unit.Owner);
                        if (tracker.Contacts.Length > 0)
                        {
                            sim.TryEnqueue(unit.Owner, new AttackCommand((uint)(20000 + tick * 50 + u), tick, unit.Id, tracker.Contacts[0].Id));
                        }
                    }
                }
            }

            return recordedEvents;
        }

        var run1 = RunSimulation(4242UL);
        var run2 = RunSimulation(4242UL);
        var runDiff = RunSimulation(99999999UL);

        Assert.NotEmpty(run1);
        Assert.Equal(run1.Count, run2.Count);
        for (int i = 0; i < run1.Count; i++)
        {
            Assert.Equal(run1[i].Result, run2[i].Result);
            Assert.Equal(run1[i].ShooterUnitId, run2[i].ShooterUnitId);
            Assert.Equal(run1[i].Target, run2[i].Target);
        }

        bool anyDiff = run1.Count != runDiff.Count;
        if (!anyDiff)
        {
            for (int i = 0; i < run1.Count; i++)
            {
                if (run1[i].Result != runDiff[i].Result || run1[i].Target != runDiff[i].Target)
                {
                    anyDiff = true;
                    break;
                }
            }
        }
        Assert.True(anyDiff, "Simulations with different match seeds must produce different combat outcomes.");
    }
}
