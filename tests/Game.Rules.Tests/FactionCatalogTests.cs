using DeadGround.Engine;
using DeadGround.Rules.Ops;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class FactionCatalogTests
{
    private const string MinimalUnits = """
        "units": [{ "id": "u", "speed": 1.0, "target_points": [1.0] }]
        """;

    [Fact]
    public void ShippedContentDefinesSovietAndUsFactions()
    {
        UnitCatalog catalog = PrototypeScenario.LoadCatalog();
        Assert.True(catalog.HasFaction("soviet"));
        Assert.True(catalog.HasFaction("us"));
        Assert.False(catalog.HasFaction("missing"));
    }

    [Fact]
    public void DuplicateOrBlankFactionIdsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(
            $$"""{ "schema_version": 1, "factions": [{ "id": "a" }, { "id": "a" }], {{MinimalUnits}} }"""));
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.LoadCatalog(
            $$"""{ "schema_version": 1, "factions": [{ "id": " " }], {{MinimalUnits}} }"""));
    }

    [Fact]
    public void ScenariosAssignSovietToPlayerOneAndUsToPlayerTwo()
    {
        MatchSetup[] setups =
        [
            PrototypeScenario.CreateMatchSetup(HeightMap.CreatePrototype()),
            VisibilityScenario.CreateMatchSetup(),
            NavigationScenario.CreateMatchSetup(),
            LargeNavigationScenario.CreateMatchSetup(),
            CombatScenario.CreateMatchSetup(),
            ReconRaidScenario.CreateMatchSetup(),
        ];
        foreach (MatchSetup setup in setups)
        {
            Assert.Equal("soviet", setup.GetPlayerFaction(1));
            Assert.Equal("us", setup.GetPlayerFaction(2));
        }
    }

    [Fact]
    public void CatalogWithoutFactionsLeavesMatchWithoutFactions()
    {
        UnitCatalog catalog = PrototypeScenario.LoadCatalog($$"""{ "schema_version": 1, {{MinimalUnits}} }""");
        Assert.Null(PrototypeScenario.CreatePlayerFactions(catalog));
    }

    [Fact]
    public void CatalogMissingADefaultFactionIsRejected()
    {
        UnitCatalog catalog = PrototypeScenario.LoadCatalog(
            $$"""{ "schema_version": 1, "factions": [{ "id": "soviet" }], {{MinimalUnits}} }""");
        Assert.Throws<InvalidOperationException>(() => PrototypeScenario.CreatePlayerFactions(catalog));
    }
}
