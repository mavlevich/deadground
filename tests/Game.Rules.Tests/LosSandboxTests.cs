using DeadGround.Engine;
using DeadGround.Sandbox;
using Xunit;

namespace DeadGround.Rules.Tests;

public sealed class LosSandboxTests
{
    [Fact]
    public void AllLosSandboxPresetsMatchExpectedResults()
    {
        StaticGeometry geo = LosSandboxScenario.CreateGeometry();
        IReadOnlyList<LosSandboxPreset> presets = LosSandboxScenario.CreatePresets();

        Assert.Equal(6, presets.Count);

        for (int p = 0; p < presets.Count; p++)
        {
            LosSandboxPreset preset = presets[p];
            Assert.Equal(preset.TargetPoints.Length, preset.ExpectedObstructions.Length);

            for (int t = 0; t < preset.TargetPoints.Length; t++)
            {
                LineOfSightResult result = geo.CheckLineOfSight(preset.Observer, preset.TargetPoints[t]);
                ObstructionKind expectedKind = preset.ExpectedObstructions[t];

                Assert.Equal(expectedKind, result.ObstructionKind);

                if (expectedKind == ObstructionKind.None)
                {
                    Assert.True(result.IsClear);
                }
                else
                {
                    Assert.False(result.IsClear);
                    if (expectedKind == ObstructionKind.Structure)
                    {
                        Assert.Equal(preset.ExpectedStructureIds[t], result.ObstructingStructureId);
                    }
                }
            }
        }
    }
}
