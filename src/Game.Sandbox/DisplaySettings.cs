namespace DeadGround.Sandbox;

/// <summary>
/// Display configuration data model with VSync, frame capping, and command-line override support.
/// Free of any Godot dependencies for clean testing and validation.
/// </summary>
public sealed record DisplaySettings(
    bool VSyncEnabled = true,
    int MaxFps = 0,
    int BenchmarkFrames = 0)
{
    public static readonly int[] ValidFpsLimits = [0, 30, 60, 120, 144, 165, 240];

    public static int SanitizeMaxFps(int fps)
    {
        for (int i = 0; i < ValidFpsLimits.Length; i++)
        {
            if (ValidFpsLimits[i] == fps) return fps;
        }

        if (fps <= 0) return 0;
        if (fps <= 45) return 30;
        if (fps <= 90) return 60;
        if (fps <= 132) return 120;
        if (fps <= 154) return 144;
        if (fps <= 200) return 165;
        return 240;
    }

    public static DisplaySettings CreateDefault() => new();

    public DisplaySettings WithSanitizedValues() =>
        this with { MaxFps = SanitizeMaxFps(MaxFps), BenchmarkFrames = Math.Max(0, BenchmarkFrames) };

    public static DisplaySettings ParseCommandLine(string[] args, DisplaySettings? current = null)
    {
        DisplaySettings result = current ?? CreateDefault();
        if (args is null || args.Length == 0) return result;

        bool vsync = result.VSyncEnabled;
        int maxFps = result.MaxFps;
        int benchmarkFrames = result.BenchmarkFrames;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg.Equals("--vsync", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                string val = args[i + 1].ToLowerInvariant();
                if (val is "on" or "true" or "1" or "enabled")
                {
                    vsync = true;
                    i++;
                }
                else if (val is "off" or "false" or "0" or "disabled")
                {
                    vsync = false;
                    i++;
                }
            }
            else if (arg.StartsWith("--vsync=", StringComparison.OrdinalIgnoreCase))
            {
                ReadOnlySpan<char> val = arg.AsSpan("--vsync=".Length);
                if (val.Equals("on", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                    val.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                {
                    vsync = true;
                }
                else if (val.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                         val.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                         val.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                         val.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    vsync = false;
                }
            }
            else if (arg.Equals("--max-fps", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int fpsVal))
                {
                    maxFps = SanitizeMaxFps(fpsVal);
                    i++;
                }
            }
            else if (arg.StartsWith("--max-fps=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg.AsSpan("--max-fps=".Length), out int fpsVal))
                {
                    maxFps = SanitizeMaxFps(fpsVal);
                }
            }
            else if (arg.Equals("--benchmark-frames", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out int benchVal))
                {
                    benchmarkFrames = Math.Max(0, benchVal);
                    i++;
                }
            }
            else if (arg.StartsWith("--benchmark-frames=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg.AsSpan("--benchmark-frames=".Length), out int benchVal))
                {
                    benchmarkFrames = Math.Max(0, benchVal);
                }
            }
        }

        return new DisplaySettings(vsync, maxFps, benchmarkFrames);
    }

    public static bool HasVsyncOverride(string[]? args)
    {
        if (args is null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--vsync", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool HasMaxFpsOverride(string[]? args)
    {
        if (args is null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--max-fps", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static DisplaySettings ApplyUserEdit(DisplaySettings savedConfig, bool? newVsync = null, int? newMaxFps = null)
    {
        bool vsync = newVsync ?? savedConfig.VSyncEnabled;
        int maxFps = newMaxFps.HasValue ? SanitizeMaxFps(newMaxFps.Value) : savedConfig.MaxFps;
        return savedConfig with { VSyncEnabled = vsync, MaxFps = maxFps };
    }
}
