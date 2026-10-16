using DeadGround.Engine;
using Xunit;

namespace DeadGround.Tests;

public sealed class MatchSetupFactionTests
{
    [Fact]
    public void FactionsAreLookedUpByOneBasedPlayer()
    {
        MatchSetup setup = new(1, 1, 1, "Ops", PlayerFactions: ["soviet", "us"]);
        Assert.Equal("soviet", setup.GetPlayerFaction(1));
        Assert.Equal("us", setup.GetPlayerFaction(2));
        Assert.Null(setup.GetPlayerFaction(0));
        Assert.Null(setup.GetPlayerFaction(3));
        Assert.Null(new MatchSetup(1, 1, 1, "Ops").GetPlayerFaction(1));
    }

    [Fact]
    public void DifferentFactionAssignmentsDoNotMatch()
    {
        MatchSetup a = new(1, 1, 1, "Ops", PlayerFactions: ["soviet", "us"]);
        Assert.True(a.Matches(new MatchSetup(1, 1, 1, "Ops", PlayerFactions: ["soviet", "us"])));
        Assert.False(a.Matches(new MatchSetup(1, 1, 1, "Ops", PlayerFactions: ["us", "soviet"])));
        Assert.False(a.Matches(new MatchSetup(1, 1, 1, "Ops", PlayerFactions: ["soviet"])));
    }

    [Fact]
    public void MissingFactionsOnOneSideAreNotCompared()
    {
        // Same rule as PlayerTeams: the handshake does not carry factions yet.
        MatchSetup withFactions = new(1, 1, 1, "Ops", PlayerFactions: ["soviet", "us"]);
        Assert.True(withFactions.Matches(new MatchSetup(1, 1, 1, "Ops")));
        Assert.True(new MatchSetup(1, 1, 1, "Ops").Matches(withFactions));
    }

    [Fact]
    public void HostileFactionIsTheOtherSidesFaction()
    {
        MatchSetup setup = new(1, 1, 1, "Ops", PlayerFactions: ["soviet", "us"]);
        Assert.Equal("us", setup.GetHostileFaction(1));
        Assert.Equal("soviet", setup.GetHostileFaction(2));
        Assert.Null(setup.GetHostileFaction(0));
        Assert.Null(new MatchSetup(1, 1, 1, "Ops").GetHostileFaction(1));
    }

    [Fact]
    public void HostileFactionFollowsTeamsAndIsUnknownWhenMixed()
    {
        // 2v2: allies are skipped; mixed enemy factions give no single answer.
        MatchSetup sameEnemy = new(1, 1, 1, "Ops", PlayerTeams: [1, 1, 2, 2], PlayerFactions: ["soviet", "soviet", "us", "us"]);
        Assert.Equal("us", sameEnemy.GetHostileFaction(1));
        Assert.Equal("soviet", sameEnemy.GetHostileFaction(4));
        MatchSetup mixedEnemy = new(1, 1, 1, "Ops", PlayerTeams: [1, 1, 2, 2], PlayerFactions: ["soviet", "us", "us", "soviet"]);
        Assert.Null(mixedEnemy.GetHostileFaction(1));
    }

    [Fact]
    public void UnassignedTeamsResolveToThePlayerLikeTheSimulation()
    {
        // Team 0 means unassigned: the player is its own team, in WorldState and in MatchSetup.
        Assert.Equal(1, MatchSetup.ResolveTeam([0, 2], 1));
        Assert.Equal(2, MatchSetup.ResolveTeam([0, 2], 2));
        Assert.Equal(3, MatchSetup.ResolveTeam([0, 2], 3));
        WorldState world = new(HeightMap.CreatePrototype(), 4, [0, 2]);
        Assert.Equal(1, world.GetPlayerTeam(1));
        MatchSetup setup = new(1, 1, 1, "Ops", PlayerTeams: [0, 1], PlayerFactions: ["soviet", "us"]);
        // Player 1 is unassigned (own team 1) and so shares team 1 with player 2: no hostile faction.
        Assert.Null(setup.GetHostileFaction(1));
    }
}
