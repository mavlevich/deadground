using DeadGround.Sandbox;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class WeaponReadinessPresentationTests
{
    [Theory]
    [InlineData(false, 20, 0, 100, WeaponReadinessState.Unarmed, 0f)]
    [InlineData(true, 0, 0, 100, WeaponReadinessState.Empty, 0f)]
    [InlineData(true, 0, 90, 100, WeaponReadinessState.Empty, 0f)]
    [InlineData(true, 3, 0, 100, WeaponReadinessState.Ready, 1f)]
    [InlineData(true, 3, 100, 100, WeaponReadinessState.Reloading, 0f)]
    [InlineData(true, 3, 25, 100, WeaponReadinessState.Reloading, 0.75f)]
    [InlineData(true, 3, 120, 100, WeaponReadinessState.Reloading, 0f)]
    [InlineData(true, 3, 20, 0, WeaponReadinessState.Reloading, 0f)]
    public void Observation_DistinguishesReadinessFromAmmunitionAndClampsProgress(
        bool armed, int ammo, int remaining, int duration, WeaponReadinessState expectedState, float expectedProgress)
    {
        WeaponReadinessPresentation presentation = WeaponReadinessPresentation.From(armed, ammo, remaining, duration);
        Assert.Equal(expectedState, presentation.State);
        Assert.Equal(expectedProgress, presentation.Progress, 5);
        Assert.NotEmpty(presentation.Label);
    }

    [Fact]
    public void LastRound_DoesNotShowReadyWhenReloadCounterIsZero()
    {
        WeaponReadinessPresentation beforeShot = WeaponReadinessPresentation.From(true, 1, 0, 80);
        WeaponReadinessPresentation afterShot = WeaponReadinessPresentation.From(true, 0, 0, 80);
        Assert.Equal("READY", beforeShot.Label);
        Assert.Equal("NO AMMO", afterShot.Label);
        Assert.Equal(0f, afterShot.Progress);
    }
}
