using DeadGround.Engine;
using DeadGround.Sandbox;
using Godot;

namespace DeadGround.Client;

/// <summary>
/// Regression checks for line-of-sight geometry and presets, executable in headless Godot mode.
/// </summary>
internal static class LosSandboxRegressionTests
{
    internal static bool Run()
    {
        try
        {
            StaticGeometry geometry = LosSandboxScenario.CreateGeometry();
            IReadOnlyList<LosSandboxPreset> presets = LosSandboxScenario.CreatePresets();

            if (presets.Count != 6)
            {
                throw new InvalidOperationException($"Expected 6 sandbox presets, found {presets.Count}.");
            }

            for (int p = 0; p < presets.Count; p++)
            {
                LosSandboxPreset preset = presets[p];
                if (preset.TargetPoints.Length != preset.ExpectedObstructions.Length)
                {
                    throw new InvalidOperationException(
                        $"Preset {preset.Id} ({preset.Name}) has mismatched target points and expectations.");
                }

                for (int t = 0; t < preset.TargetPoints.Length; t++)
                {
                    LineOfSightResult result = geometry.CheckLineOfSight(preset.Observer, preset.TargetPoints[t]);

                    if (result.ObstructionKind != preset.ExpectedObstructions[t])
                    {
                        throw new InvalidOperationException(
                            $"Preset {preset.Id} ({preset.Name}) target {t} ({preset.TargetLabels[t]}): " +
                            $"expected {preset.ExpectedObstructions[t]} but got {result.ObstructionKind} at hit={result.HitPoint}.");
                    }

                    if (preset.ExpectedStructureIds[t] is { } expectedStructureId &&
                        result.ObstructingStructureId != expectedStructureId)
                    {
                        throw new InvalidOperationException(
                            $"Preset {preset.Id} ({preset.Name}) target {t}: " +
                            $"expected structure {expectedStructureId} but got {result.ObstructingStructureId}.");
                    }

                    if (result.IsClear && result.RayFraction < 1.0f)
                    {
                        throw new InvalidOperationException(
                            $"Preset {preset.Id} target {t} marked clear but ray fraction is {result.RayFraction}.");
                    }

                    if (!result.IsClear && (result.RayFraction < 0f || result.RayFraction >= 1.0f))
                    {
                        throw new InvalidOperationException(
                            $"Preset {preset.Id} target {t} marked obstructed but ray fraction is {result.RayFraction}.");
                    }
                }
            }

            GD.Print("LOS_SANDBOX_REGRESSIONS_OK all_scenarios_verified");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError(exception.ToString());
            return false;
        }
    }
}
