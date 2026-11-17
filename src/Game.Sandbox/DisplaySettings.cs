namespace DeadGround.Sandbox;

public enum EnvironmentQuality : byte
{
    Low = 0,
    Medium = 1,
    High = 2
}

public enum RenderScalePreset : byte
{
    Auto = 0,
    Native100 = 1,
    Scale85 = 2,
    Scale75 = 3,
    Scale50 = 4
}

public enum AntiAliasingPreset : byte
{
    Auto = 0,
    Off = 1,
    Msaa2x = 2,
    Msaa4x = 3
}

/// <summary>
/// Display configuration data model with VSync, frame capping, environment profile,
/// render scale, anti-aliasing, and command-line override support. Free of any Godot dependencies.
/// </summary>
public sealed record DisplaySettings(
    bool VSyncEnabled = true,
    int MaxFps = 0,
    int BenchmarkFrames = 0,
    string EnvironmentProfile = "overcast",
    EnvironmentQuality Quality = EnvironmentQuality.High,
    bool BenchmarkQuit = false,
    string? CaptureViewsDir = null,
    RenderScalePreset RenderScale = RenderScalePreset.Native100,
    AntiAliasingPreset AntiAliasing = AntiAliasingPreset.Auto)
{
    public const float AutoRenderTargetPixels = 2560f * 1440f;
    public const float MsaaAutoCutoffPixels = 3_700_000f;
    public const float MinAutoRenderScale = 0.50f;
    public const float MaxAutoRenderScale = 1.0f;
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

    public static RenderScalePreset ParseRenderScale(string? val, RenderScalePreset defaultPreset = RenderScalePreset.Native100)
    {
        if (string.IsNullOrWhiteSpace(val)) return defaultPreset;
        string lower = val.Trim().ToLowerInvariant();
        return lower switch
        {
            "auto" => RenderScalePreset.Auto,
            "100" or "100%" or "1.0" or "1.00" or "1" or "native" or "native100" => RenderScalePreset.Native100,
            "85" or "85%" or "0.85" or "scale85" => RenderScalePreset.Scale85,
            "75" or "75%" or "0.75" or "scale75" => RenderScalePreset.Scale75,
            "50" or "50%" or "0.5" or "0.50" or "scale50" => RenderScalePreset.Scale50,
            _ => defaultPreset
        };
    }

    public static float ResolveRenderScaleFactor(RenderScalePreset preset, int windowWidth, int windowHeight)
    {
        switch (preset)
        {
            case RenderScalePreset.Native100: return 1.0f;
            case RenderScalePreset.Scale85: return 0.85f;
            case RenderScalePreset.Scale75: return 0.75f;
            case RenderScalePreset.Scale50: return 0.50f;
            case RenderScalePreset.Auto:
            default:
                if (windowWidth <= 0 || windowHeight <= 0) return 1.0f;
                float windowPixels = (float)windowWidth * windowHeight;
                return Math.Clamp(MathF.Sqrt(AutoRenderTargetPixels / windowPixels), MinAutoRenderScale, MaxAutoRenderScale);
        }
    }

    public static AntiAliasingPreset ParseAntiAliasing(string? val, AntiAliasingPreset defaultPreset = AntiAliasingPreset.Auto)
    {
        if (string.IsNullOrWhiteSpace(val)) return defaultPreset;
        string lower = val.Trim().ToLowerInvariant();
        return lower switch
        {
            "auto" => AntiAliasingPreset.Auto,
            "off" or "0" or "none" or "disabled" => AntiAliasingPreset.Off,
            "2x" or "2" or "msaa2x" => AntiAliasingPreset.Msaa2x,
            "4x" or "4" or "msaa4x" => AntiAliasingPreset.Msaa4x,
            _ => defaultPreset
        };
    }

    public static int ResolveMsaaSamples(AntiAliasingPreset preset, int windowWidth, int windowHeight, float effectiveRenderScale)
    {
        switch (preset)
        {
            case AntiAliasingPreset.Off: return 0;
            case AntiAliasingPreset.Msaa2x: return 2;
            case AntiAliasingPreset.Msaa4x: return 4;
            case AntiAliasingPreset.Auto:
            default:
                if (windowWidth <= 0 || windowHeight <= 0) return 2;
                float scale = Math.Clamp(effectiveRenderScale, MinAutoRenderScale, MaxAutoRenderScale);
                float internalPixels = (windowWidth * scale) * (windowHeight * scale);
                return internalPixels > MsaaAutoCutoffPixels ? 0 : 2;
        }
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
        RenderScalePreset renderScale = result.RenderScale;
        AntiAliasingPreset antiAliasing = result.AntiAliasing;

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
            else if (arg.Equals("--render-scale", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                renderScale = ParseRenderScale(args[i + 1], renderScale);
                i++;
            }
            else if (arg.StartsWith("--render-scale=", StringComparison.OrdinalIgnoreCase))
            {
                renderScale = ParseRenderScale(arg["--render-scale=".Length..], renderScale);
            }
            else if ((arg.Equals("--msaa", StringComparison.OrdinalIgnoreCase) ||
                      arg.Equals("--anti-aliasing", StringComparison.OrdinalIgnoreCase) ||
                      arg.Equals("--aa", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                antiAliasing = ParseAntiAliasing(args[i + 1], antiAliasing);
                i++;
            }
            else if (arg.StartsWith("--msaa=", StringComparison.OrdinalIgnoreCase))
            {
                antiAliasing = ParseAntiAliasing(arg["--msaa=".Length..], antiAliasing);
            }
            else if (arg.StartsWith("--anti-aliasing=", StringComparison.OrdinalIgnoreCase))
            {
                antiAliasing = ParseAntiAliasing(arg["--anti-aliasing=".Length..], antiAliasing);
            }
            else if (arg.StartsWith("--aa=", StringComparison.OrdinalIgnoreCase))
            {
                antiAliasing = ParseAntiAliasing(arg["--aa=".Length..], antiAliasing);
            }
        }

        return new DisplaySettings(vsync, maxFps, benchmarkFrames, profile, quality, benchmarkQuit, captureViewsDir, renderScale, antiAliasing);
    }

    private static bool HasOptionOverride(string[]? args, params string[] names)
    {
        if (args is null) return false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            for (int j = 0; j < names.Length; j++)
            {
                string name = names[j];
                if (arg.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    (arg.Length > name.Length &&
                     arg.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
                     arg[name.Length] == '='))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool HasVsyncOverride(string[]? args) => HasOptionOverride(args, "--vsync");
    public static bool HasMaxFpsOverride(string[]? args) => HasOptionOverride(args, "--max-fps");
    public static bool HasEnvironmentOverride(string[]? args) => HasOptionOverride(args, "--environment", "--env");
    public static bool HasQualityOverride(string[]? args) => HasOptionOverride(args, "--quality");
    public static bool HasRenderScaleOverride(string[]? args) => HasOptionOverride(args, "--render-scale");
    public static bool HasAntiAliasingOverride(string[]? args) => HasOptionOverride(args, "--msaa", "--anti-aliasing", "--aa");

    public static DisplaySettings ApplyUserEdit(
        DisplaySettings savedConfig,
        bool? newVsync = null,
        int? newMaxFps = null,
        string? newProfile = null,
        EnvironmentQuality? newQuality = null,
        RenderScalePreset? newRenderScale = null,
        AntiAliasingPreset? newAntiAliasing = null)
    {
        bool vsync = newVsync ?? savedConfig.VSyncEnabled;
        int maxFps = newMaxFps.HasValue ? SanitizeMaxFps(newMaxFps.Value) : savedConfig.MaxFps;
        string profile = newProfile is not null ? SanitizeProfile(newProfile) : savedConfig.EnvironmentProfile;
        EnvironmentQuality quality = newQuality ?? savedConfig.Quality;
        RenderScalePreset renderScale = newRenderScale ?? savedConfig.RenderScale;
        AntiAliasingPreset antiAliasing = newAntiAliasing ?? savedConfig.AntiAliasing;
        return savedConfig with
        {
            VSyncEnabled = vsync,
            MaxFps = maxFps,
            EnvironmentProfile = profile,
            Quality = quality,
            RenderScale = renderScale,
            AntiAliasing = antiAliasing
        };
    }
}
