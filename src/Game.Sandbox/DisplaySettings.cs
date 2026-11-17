namespace DeadGround.Sandbox;

public enum EnvironmentQuality : byte
{
    Low = 0,
    Medium = 1,
    High = 2
}

/// <summary>
/// Display configuration data model with VSync, frame capping, environment profile,
/// and command-line override support. Free of any Godot dependencies for clean testing and validation.
/// </summary>
public sealed record DisplaySettings(
    bool VSyncEnabled = true,
    int MaxFps = 0,
    int BenchmarkFrames = 0,
    string EnvironmentProfile = "overcast",
    EnvironmentQuality Quality = EnvironmentQuality.High,
    bool BenchmarkQuit = false,
    string? CaptureViewsDir = null)
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

    public static string SanitizeProfile(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return "overcast";
        string trimmed = profile.Trim().ToLowerInvariant();
        if (trimmed is "clear_afternoon" or "clear" or "afternoon") return "clear_afternoon";
        if (trimmed is "baseline") return "baseline";
        return "overcast";
    }

    public static EnvironmentQuality ParseQuality(string? val, EnvironmentQuality defaultQuality = EnvironmentQuality.High)
    {
        if (string.IsNullOrWhiteSpace(val)) return defaultQuality;
        string lower = val.Trim().ToLowerInvariant();
        return lower switch
        {
            "low" or "0" or "fast" => EnvironmentQuality.Low,
            "medium" or "med" or "1" => EnvironmentQuality.Medium,
            "high" or "2" or "ultra" => EnvironmentQuality.High,
            _ => defaultQuality
        };
    }

    public static DisplaySettings CreateDefault() => new();

    public DisplaySettings WithSanitizedValues() =>
        this with
        {
            MaxFps = SanitizeMaxFps(MaxFps),
            BenchmarkFrames = Math.Max(0, BenchmarkFrames),
            EnvironmentProfile = SanitizeProfile(EnvironmentProfile)
        };

    public static DisplaySettings ParseCommandLine(string[] args, DisplaySettings? current = null)
    {
        DisplaySettings result = current ?? CreateDefault();
        if (args is null || args.Length == 0) return result;

        bool vsync = result.VSyncEnabled;
        int maxFps = result.MaxFps;
        int benchmarkFrames = result.BenchmarkFrames;
        string profile = result.EnvironmentProfile;
        EnvironmentQuality quality = result.Quality;
        bool benchmarkQuit = result.BenchmarkQuit;
        string? captureViewsDir = result.CaptureViewsDir;

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
            else if (arg.Equals("--benchmark-quit", StringComparison.OrdinalIgnoreCase))
            {
                benchmarkQuit = true;
            }
            else if ((arg.Equals("--environment", StringComparison.OrdinalIgnoreCase) ||
                      arg.Equals("--env", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                profile = SanitizeProfile(args[i + 1]);
                i++;
            }
            else if (arg.StartsWith("--environment=", StringComparison.OrdinalIgnoreCase))
            {
                profile = SanitizeProfile(arg["--environment=".Length..]);
            }
            else if (arg.StartsWith("--env=", StringComparison.OrdinalIgnoreCase))
            {
                profile = SanitizeProfile(arg["--env=".Length..]);
            }
            else if (arg.Equals("--quality", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                quality = ParseQuality(args[i + 1], quality);
                i++;
            }
            else if (arg.StartsWith("--quality=", StringComparison.OrdinalIgnoreCase))
            {
                quality = ParseQuality(arg["--quality=".Length..], quality);
            }
            else if (arg.Equals("--capture-views", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                captureViewsDir = args[i + 1];
                i++;
            }
            else if (arg.StartsWith("--capture-views=", StringComparison.OrdinalIgnoreCase))
            {
                captureViewsDir = arg["--capture-views=".Length..];
            }
        }

        return new DisplaySettings(vsync, maxFps, benchmarkFrames, profile, quality, benchmarkQuit, captureViewsDir);
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

    public static bool HasEnvironmentOverride(string[]? args)
    {
        if (args is null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--environment", StringComparison.OrdinalIgnoreCase) ||
                args[i].StartsWith("--environment=", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--env", StringComparison.OrdinalIgnoreCase) ||
                args[i].StartsWith("--env=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static bool HasQualityOverride(string[]? args)
    {
        if (args is null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--quality", StringComparison.OrdinalIgnoreCase) ||
                args[i].StartsWith("--quality=", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static DisplaySettings ApplyUserEdit(
        DisplaySettings savedConfig,
        bool? newVsync = null,
        int? newMaxFps = null,
        string? newProfile = null,
        EnvironmentQuality? newQuality = null)
    {
        bool vsync = newVsync ?? savedConfig.VSyncEnabled;
        int maxFps = newMaxFps.HasValue ? SanitizeMaxFps(newMaxFps.Value) : savedConfig.MaxFps;
        string profile = newProfile is not null ? SanitizeProfile(newProfile) : savedConfig.EnvironmentProfile;
        EnvironmentQuality quality = newQuality ?? savedConfig.Quality;
        return savedConfig with { VSyncEnabled = vsync, MaxFps = maxFps, EnvironmentProfile = profile, Quality = quality };
    }
}
