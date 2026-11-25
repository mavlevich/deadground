using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class ArtilleryScenarioTests
{
    [Fact]
    public void ColdWarCatalog_ContainsValidBm21GradAnd122mmRocketWeapon()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        UnitDefinition bm21 = catalog.RequireDefinition("bm21");

        Assert.Equal("BM-21 Grad", bm21.Name);
        Assert.Equal("soviet", bm21.Faction);
        Assert.Equal("artillery", bm21.Classification);
        Assert.Equal("rocket_122mm_9m22u", bm21.Weapon);
        Assert.Equal(40, catalog.GetAmmoCapacity(bm21));

        OpsCombatCatalog combatCatalog = new(catalog);
        ushort bm21Idx = catalog.RequireDefinitionIndex("bm21");
        Assert.True(combatCatalog.TryGetProfile(bm21Idx, out UnitCombatProfile profile));
        Assert.NotNull(profile.Weapon);
        Assert.True(profile.Weapon!.IsIndirectFire);
        Assert.Equal(WeaponPresentationKind.RocketArtillery, WeaponPresentation.GetKind(profile.Weapon.PresentationId));
        Assert.Equal(350f, profile.Weapon.MinRange);
        Assert.Equal(4000f, profile.Weapon.Range);
        Assert.Equal(20, profile.Weapon.SalvoCount);
        Assert.Equal(45f, profile.Weapon.DispersionRadius);
        Assert.Equal(22f, profile.Weapon.SplashRadius);

        OpsPerceptionCatalog perceptionCatalog = new(catalog);
        Assert.True(perceptionCatalog.TryGetProfile(bm21Idx, out UnitPerceptionProfile perc));
        Assert.Equal(ContactClassification.Artillery, perc.Classification);
    }

    [Fact]
    public void ArtilleryPlayground_TwoGradsBarrageOverRidgeAndDestroySpottedUsTargets()
    {
        StaticGeometry geometry = ArtilleryPlaygroundScenario.CreateStaticGeometry();
        Simulation sim = ArtilleryPlaygroundScenario.CreateSimulation(geometry, matchSeed: 1337UL);
        using LocalSession session = new(sim, player: 1);

        // Allow forward BRDM-2 observer on ridge to spot the US staging camp
        for (int i = 0; i < 20; i++)
            session.Advance(Simulation.TickSeconds);

        Assert.Equal(4, session.Replica.OwnUnits.Length);
        Assert.True(session.Replica.ContactCount >= 5, $"Expected BRDM-2 to spot US staging camp, saw {session.Replica.ContactCount} contacts.");

        int initialEnemyCount = CountPlayerUnits(sim.World, 2);
        Assert.Equal(11, initialEnemyCount);

        // Order both BM-21 Grad launchers to fire a barrage onto the US staging camp behind the ridge
        EntityId grad1 = session.Replica.OwnUnits[0].Id;
        EntityId grad2 = session.Replica.OwnUnits[1].Id;
        Assert.True(session.FireMission(grad1, ArtilleryPlaygroundScenario.TargetCampCenter));
        Assert.True(session.FireMission(grad2, ArtilleryPlaygroundScenario.TargetCampCenter));

        int launchEvents = 0;
        int impactEvents = 0;
        uint lastEvId = 0;

        for (int i = 0; i < 520; i++)
        {
            session.Advance(Simulation.TickSeconds);
            ReadOnlySpan<CombatEventObservation> evs = session.Replica.Events;
            for (int e = 0; e < evs.Length; e++)
            {
                if (evs[e].EventId <= lastEvId) continue;
                lastEvId = evs[e].EventId;
                if (evs[e].Kind == CombatEventKind.RocketLaunched) launchEvents++;
                else if (evs[e].Kind == CombatEventKind.ArtilleryImpact) impactEvents++;
            }
        }

        Assert.Equal(40, sim.Artillery!.TotalRocketsFired);
        Assert.Equal(40, sim.Artillery.TotalImpacts);
        Assert.True(launchEvents >= 30, $"Expected rocket launch events in client replica, got {launchEvents}.");
        Assert.True(impactEvents >= 30, $"Expected artillery impact events in client replica, got {impactEvents}.");

        int remainingEnemies = CountPlayerUnits(sim.World, 2);
        Assert.True(remainingEnemies < initialEnemyCount,
            $"Expected 40-rocket BM-21 Grad barrage to destroy US camp units (remaining {remainingEnemies}/{initialEnemyCount}).");
    }

    [Fact]
    public void Bm21Grad_AllRocketsLandInsideDispersionRadius_AndTwoSalvosDestroyStationaryTanks()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        const int size = 129;
        HeightMap map = new(size, size, 8f, 0.01f, new ushort[size * size]);
        StaticGeometry geometry = new(map, []);
        OpsRules rules = new(geometry, catalog, null);
        WorldState world = new(map);
        using Simulation sim = new(world, rules, matchSeed: 2121UL);

        ushort bm21Idx = catalog.RequireDefinitionIndex("bm21");
        UnitDefinition bm21Def = catalog.RequireDefinition("bm21");
        ushort m60Idx = catalog.RequireDefinitionIndex("m60a1");
        UnitDefinition m60Def = catalog.RequireDefinition("m60a1");

        Vector2 targetCenter = new(500f, 548f);
        EntityId grad = world.CreateUnit(1, bm21Idx, new Vector2(500f, 110f), bm21Def.Speed, bm21Def.MaxHealth, 40);
        EntityId tankA = world.CreateUnit(2, m60Idx, new Vector2(492f, 548f), m60Def.Speed, m60Def.MaxHealth, 0);
        EntityId tankB = world.CreateUnit(2, m60Idx, new Vector2(508f, 548f), m60Def.Speed, m60Def.MaxHealth, 0);
        world.SetFireStance(tankA, FireStance.HoldFire);
        world.SetFireStance(tankB, FireStance.HoldFire);

        float maxObservedOffset = 0f;
        int impactsObserved = 0;
        ArtillerySystem artillery = sim.Artillery!;
        Action<WorldState, int, RocketProjectileState>? prevImpact = artillery.OnRocketImpact;
        artillery.OnRocketImpact = (w, t, proj) =>
        {
            float offset = Vector2.Distance(targetCenter, new Vector2(proj.Target.X, proj.Target.Z));
            if (offset > maxObservedOffset) maxObservedOffset = offset;
            impactsObserved++;
            prevImpact?.Invoke(w, t, proj);
        };

        // Fire first 20-rocket salvo, wait for impact, then fire second 20-rocket salvo
        Assert.True(sim.TryEnqueue(1, new FireMissionCommand(1, sim.Tick, grad, targetCenter)));
        for (int i = 0; i < 420; i++) sim.Step();

        // Reload completes after 30s (600 ticks); advance and fire second salvo
        for (int i = 0; i < 420; i++) sim.Step();
        Assert.True(sim.TryEnqueue(1, new FireMissionCommand(2, sim.Tick, grad, targetCenter)));
        for (int i = 0; i < 420; i++) sim.Step();

        Assert.Equal(40, impactsObserved);
        Assert.True(maxObservedOffset <= 45.01f, $"All rockets must land inside 45m dispersion radius, got max {maxObservedOffset:F2}m.");

        int survivingTanks = CountPlayerUnits(world, 2);
        Assert.True(survivingTanks == 0, $"Expected 2 full BM-21 Grad salvos directly on 2 stationary M60A1s to destroy both, remaining={survivingTanks}.");
    }

    private static int CountPlayerUnits(WorldState world, int player)
    {
        int count = 0;
        for (int i = 0; i < world.Count; i++)
        {
            if (world.GetUnitAt(i).Owner == player)
                count++;
        }
        return count;
    }
}
