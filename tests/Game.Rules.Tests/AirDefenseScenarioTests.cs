using System.Numerics;
using DeadGround.Engine;
using DeadGround.Engine.Net;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class AirDefenseScenarioTests
{
    [Fact]
    public void ColdWarCatalog_DefinesStrela10SamAndChinookFlares()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        UnitDefinition strela = catalog.RequireDefinition("strela10");
        Assert.Equal("9K35 Strela-10", strela.Name);
        Assert.Equal("soviet", strela.Faction);
        Assert.Equal("anti_air", strela.Classification);
        Assert.Equal("sam_9m37", strela.Weapon);
        Assert.Equal(8, catalog.GetAmmoCapacity(strela));

        OpsPerceptionCatalog perc = new(catalog);
        Assert.True(perc.TryGetProfile(catalog.RequireDefinitionIndex("strela10"), out UnitPerceptionProfile strelaPerc));
        Assert.Equal(ContactClassification.AntiAir, strelaPerc.Classification);

        OpsCombatCatalog combat = new(catalog);
        Assert.True(combat.TryGetProfile(catalog.RequireDefinitionIndex("strela10"), out UnitCombatProfile strelaCombat));
        Assert.NotNull(strelaCombat.Weapon);
        Assert.True(strelaCombat.Weapon!.IsGuidedMissile);
        Assert.True(strelaCombat.Weapon.AirTargetsOnly);
        Assert.Equal(MissileGuidanceKind.Infrared, strelaCombat.Weapon.GuidanceKind);
        Assert.True(WeaponPresentation.IsSurfaceToAirMissile(strelaCombat.Weapon.PresentationId));

        UnitDefinition ch47 = catalog.RequireDefinition("ch47d");
        Assert.NotNull(ch47.Flares);
        Assert.Equal(8, ch47.Flares!.Charges);
        Assert.True(ch47.Flares.DefaultAutoFlares);

        OpsCountermeasureCatalog cm = new(catalog);
        Assert.True(cm.TryGetFlareProfile(catalog.RequireDefinitionIndex("ch47d"), out FlareProfile flareProfile));
        Assert.True(flareProfile.IsValid);
        Assert.Equal((byte)8, flareProfile.Charges);
    }

    [Fact]
    public void AirDefensePlayground_Strela10EngagesHighChinookWhileLowChinookStaysMaskedByRidge()
    {
        StaticGeometry geometry = AirDefensePlaygroundScenario.CreateStaticGeometry();
        using Simulation sim = AirDefensePlaygroundScenario.CreateSimulation(geometry, matchSeed: 1337UL);
        using LocalSession session = new(sim, player: 1);

        for (int i = 0; i < 20; i++) session.Advance(Simulation.TickSeconds);
        Assert.Equal(8, session.Replica.OwnUnits.Length);

        EntityId alphaId = session.Replica.OwnUnits[0].Id;
        EntityId bravoId = session.Replica.OwnUnits[1].Id;
        EntityId foxtrotId = session.Replica.OwnUnits[5].Id;

        int launches = 0;
        int flares = 0;
        int detonations = 0;
        int maxMissiles = 0;
        uint lastEventId = 0;

        for (int i = 0; i < 340; i++)
        {
            session.Advance(Simulation.TickSeconds);
            if (session.Replica.MissileCount > maxMissiles)
                maxMissiles = session.Replica.MissileCount;

            ReadOnlySpan<CombatEventObservation> events = session.Replica.Events;
            for (int e = 0; e < events.Length; e++)
            {
                if (events[e].EventId <= lastEventId) continue;
                lastEventId = events[e].EventId;
                if (events[e].Kind == CombatEventKind.MissileLaunched) launches++;
                else if (events[e].Kind == CombatEventKind.FlaresDeployed) flares++;
                else if (events[e].Kind == CombatEventKind.MissileDetonated) detonations++;
            }
        }

        Assert.True(launches >= 2, $"Expected Strela-10 battery to launch SAMs, got {launches}.");
        Assert.True(flares >= 1, $"Expected Chinook Alpha to deploy flares, got {flares}.");
        Assert.True(detonations >= 1, $"Expected SAM detonations, got {detonations}.");
        Assert.True(maxMissiles >= 1, $"Expected replicated active guided missiles, got max {maxMissiles}.");

        // Low-altitude NOE Chinooks (Bravo and Foxtrot) behind the ridge must remain untouched at full HP
        Assert.True(session.Replica.TryGetOwnUnit(bravoId, out OwnUnitObservation bravoObs));
        Assert.Equal((ushort)160, bravoObs.Health);
        Assert.Equal((byte)8, bravoObs.FlareCharges);
        Assert.True(session.Replica.TryGetOwnUnit(foxtrotId, out OwnUnitObservation foxtrotObs));
        Assert.Equal((ushort)160, foxtrotObs.Health);
        Assert.Equal((byte)8, foxtrotObs.FlareCharges);
        Assert.True(alphaId.Value != bravoId.Value);
    }

    [Fact]
    public void AirDefensePlayground_IsDeterministicAcrossIdenticalSeeds()
    {
        StaticGeometry geometry = AirDefensePlaygroundScenario.CreateStaticGeometry();
        using Simulation simA = AirDefensePlaygroundScenario.CreateSimulation(geometry, matchSeed: 4242UL);
        using Simulation simB = AirDefensePlaygroundScenario.CreateSimulation(geometry, matchSeed: 4242UL);

        for (int i = 0; i < 240; i++)
        {
            simA.Step();
            simB.Step();
        }

        Assert.Equal(simA.Combat!.TotalShotsFired, simB.Combat!.TotalShotsFired);
        Assert.Equal(simA.GuidedMissiles!.TotalFlaresDeployed, simB.GuidedMissiles!.TotalFlaresDeployed);
        Assert.Equal(simA.GuidedMissiles.TotalMissilesSpoofed, simB.GuidedMissiles.TotalMissilesSpoofed);
        Assert.Equal(simA.GuidedMissiles.TotalMissilesHit, simB.GuidedMissiles.TotalMissilesHit);
        Assert.Equal(simA.World.Count, simB.World.Count);
        Assert.Equal(simA.World.GuidedMissileCount, simB.World.GuidedMissileCount);
    }
}
