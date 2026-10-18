using System.Numerics;
using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

// Historical unit types and the 2 km battle (TDD section 17.1, docs/unit-sources.md).
public sealed class HistoricalUnitsTests
{
    [Fact]
    public void ColdWarCatalog_EveryTypeIsARealVehicleOfOneFaction()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        foreach (UnitDefinition unit in catalog.Units)
        {
            Assert.False(string.IsNullOrWhiteSpace(unit.Name), unit.Id);
            Assert.True(unit.Faction is not null && catalog.HasFaction(unit.Faction), unit.Id);
            Assert.NotNull(unit.Classification);
            Assert.True(unit.SpeedOffroad > 0f, unit.Id);
            Assert.Equal(unit.SpeedOffroad, unit.Speed);
            Assert.True(unit.SpeedRoad > unit.SpeedOffroad, unit.Id);
            Assert.InRange(unit.ReverseSpeed, 0.1f, unit.SpeedRoad);
        }

        foreach (string faction in new[] { "soviet", "us" })
        {
            foreach (string role in new[] { "tank", "apc", "recon" })
                Assert.Contains(catalog.Units, u => u.Faction == faction && u.Classification == role);
        }
    }

    [Theory]
    [InlineData("t62", "T-62", 13.9f, 8.3f, 1600f)]
    [InlineData("m60a1", "M60A1", 13.3f, 6.9f, 2000f)]
    public void TankData_MatchesTheSourceTable(string id, string name, float road, float offroad, float gunRange)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        UnitDefinition tank = catalog.RequireDefinition(id);
        Assert.Equal(name, tank.Name);
        Assert.Equal(road, tank.SpeedRoad);
        Assert.Equal(offroad, tank.SpeedOffroad);
        WeaponData gun = Assert.Single(catalog.Weapons!, w => w.Id == tank.Weapon);
        Assert.Equal(gunRange, gun.Range);
    }

    [Theory]
    [InlineData("\"speed\": 5.0, \"speed_road\": 10.0, \"speed_offroad\": 5.0", "both speed and speed_offroad")]
    [InlineData("\"speed_road\": 4.0, \"speed_offroad\": 5.0", "faster off-road")]
    [InlineData("\"speed_offroad\": 5.0", "no speed_road")]
    [InlineData("\"speed_road\": 10.0, \"speed_offroad\": 5.0, \"reverse_speed\": 10.0", "reverses as fast")]
    [InlineData("\"speed\": 5.0, \"faction\": \"nowhere\"", "unknown faction")]
    [InlineData("\"speed\": 5.0, \"name\": \" \"", "blank name")]
    public void InvalidPeriodData_IsRejected(string fields, string message)
    {
        string json = $$"""
            {
              "schema_version": 1,
              "factions": [{ "id": "soviet" }],
              "units": [{ "id": "x", {{fields}}, "target_points": [1.0] }]
            }
            """;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(json));
        Assert.Contains(message, error.Message);
    }

    // The client loads the catalog through ScenarioContent; it must be the one the server pins in
    // the match setup, or names and models are wrong and a remote connect fails the content check.
    [Fact]
    public void EveryScenario_ClientCatalogMatchesTheServerSetup()
    {
        Assert.Equal(ScenarioContent.LoadCatalog("battle").ContentHash, BattleScenario.CreateMatchSetup().ContentHash);
        Assert.Equal(ScenarioContent.LoadCatalog("combat").ContentHash, CombatScenario.CreateMatchSetup().ContentHash);
        Assert.Equal(ScenarioContent.LoadCatalog("recon-raid").ContentHash, ReconRaidScenario.CreateMatchSetup().ContentHash);
        Assert.Equal(ScenarioContent.LoadCatalog("prototype").ContentHash,
            PrototypeScenario.CreateMatchSetup(HeightMap.CreatePrototype()).ContentHash);
        Assert.NotEqual(ColdWarContent.LoadCatalog().ContentHash, PrototypeScenario.LoadCatalog().ContentHash);
    }

    [Fact]
    public void AmmoCapacity_ComesFromTheUnitsWeapon()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        Assert.Equal(40, catalog.GetAmmoCapacity(catalog.RequireDefinition("t62")));
        Assert.Equal(63, catalog.GetAmmoCapacity(catalog.RequireDefinition("m60a1")));
        Assert.Equal(0, catalog.GetAmmoCapacity(catalog.RequireDefinition("btr70")));
    }

    [Fact]
    public void BattleScenario_SpawnsEachFactionsOwnTypes()
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        StaticGeometry geometry = BattleScenario.CreateStaticGeometry();
        MatchSetup setup = BattleScenario.CreateMatchSetup(geometry, catalog: catalog);
        using Simulation sim = BattleScenario.CreateSimulation(geometry, catalog);

        int[] perSide = new int[3];
        for (int i = 0; i < sim.World.Count; i++)
        {
            UnitState unit = sim.World.GetUnitAt(i);
            perSide[unit.Owner]++;
            Assert.Equal(setup.GetPlayerFaction(unit.Owner), catalog.Units[unit.Definition].Faction);
            Assert.InRange(unit.Position.X, 0f, BattleScenario.MapSize);
            Assert.InRange(unit.Position.Y, 0f, BattleScenario.MapSize);
        }
        Assert.Equal(BattleScenario.UnitsPerSide, perSide[1]);
        Assert.Equal(BattleScenario.UnitsPerSide, perSide[2]);
        Assert.Equal(2000f, BattleScenario.MapSize);
    }

    [Fact]
    public void BattleScenario_RidgeHidesTheSidesAtTheStart()
    {
        using Simulation sim = BattleScenario.CreateSimulation();
        for (int i = 0; i < 40; i++) sim.Step();
        foreach (int player in new[] { 1, 2 })
        {
            PlayerKnowledge knowledge = new(player);
            sim.UpdateKnowledge(knowledge);
            Assert.Equal(BattleScenario.UnitsPerSide, knowledge.OwnUnits.Length);
            Assert.Equal(0, knowledge.ContactCount);
        }
    }

    // The prototype tank saw 50 m; a T-62 crew now sees a tank on open ground at about 2 km.
    [Theory]
    [InlineData(1900f, 1)]
    [InlineData(2100f, 0)]
    public void TankSeesAnEnemyTankAtPeriodRange(float distance, int expectedContacts)
    {
        UnitCatalog catalog = ColdWarContent.LoadCatalog();
        const int size = 301;
        HeightMap flat = new(size, size, 10f, 0.01f, new ushort[size * size]);
        StaticGeometry geometry = new(flat, []);
        WorldState world = new(flat);
        world.CreateUnit(1, catalog.RequireDefinitionIndex("t62"), new Vector2(200f, 1500f), catalog.RequireDefinition("t62").Speed);
        world.CreateUnit(2, catalog.RequireDefinitionIndex("m60a1"), new Vector2(200f + distance, 1500f), catalog.RequireDefinition("m60a1").Speed);
        using Simulation sim = new(world, new OpsRules(geometry, new OpsPerceptionCatalog(catalog), PrototypeScenario.CreateDetectionTuning(catalog)));
        for (int i = 0; i < 10; i++) sim.Step();

        PlayerKnowledge knowledge = new(1);
        sim.UpdateKnowledge(knowledge);
        Assert.Equal(expectedContacts, knowledge.ContactCount);
    }
}
