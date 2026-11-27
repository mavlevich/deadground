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
    Msaa4x = 3,
    Fxaa = 4
}

public enum AmbientOcclusionPreset : byte
{
    Auto = 0,
    Off = 1,
    On = 2
}

/// <summary>
/// Display configuration data model with VSync, frame capping, environment profile,
/// render scale, anti-aliasing, ambient occlusion, and command-line override support. Free of any Godot dependencies.
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
    AntiAliasingPreset AntiAliasing = AntiAliasingPreset.Auto,
    AmbientOcclusionPreset AmbientOcclusion = AmbientOcclusionPreset.Auto)
{
    public const float AutoRenderTargetPixels = 2560f * 1440f;
    public const float MsaaAutoCutoffPixels = 3_700_000f;
    public const float MinAutoRenderScale = 0.35f;
    public const float MinDiagnosticRenderScale = 0.25f;
    public const float MaxAutoRenderScale = 1.0f;
    public const long LargeOutputBilinearCutoffPixels = 4_500_000L;
    public static readonly int[] ValidFpsLimits = [0, 30, 60, 120, 144, 165, 240];
    public static readonly (string Id, string Label)[] EnvironmentProfileCatalog =
    [
        ("overcast", "Overcast Autumn (Ustye)"),
        ("cool_clear_day", "Cool Clear Day (Calibration)"),
        ("clear_afternoon", "Clear Afternoon"),
        ("amber_sunset", "Amber Sunset"),
        ("night_ops", "Night Operations"),
        ("storm_rain", "Storm & Rain")
    ];
    public static readonly string[] ValidEnvironmentProfiles =
        Array.ConvertAll(EnvironmentProfileCatalog, static entry => entry.Id);

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
        return trimmed switch
        {
            "cool_clear_day" or "cool_day" or "day" or "daylight" or "calibration" => "cool_clear_day",
            "clear_afternoon" or "clear" or "afternoon" => "clear_afternoon",
            "amber_sunset" or "sunset" or "dusk" or "evening" => "amber_sunset",
            "night_ops" or "night" or "midnight" or "moonlight" => "night_ops",
            "storm_rain" or "storm" or "rain" or "rainy" => "storm_rain",
            "baseline" => "baseline",
            _ => "overcast"
        };
    }

    public static string GetNextEnvironmentProfile(string? currentProfile)
    {
        string canonical = SanitizeProfile(currentProfile);
        for (int i = 0; i < ValidEnvironmentProfiles.Length; i++)
        {
            if (string.Equals(ValidEnvironmentProfiles[i], canonical, StringComparison.OrdinalIgnoreCase))
            {
                return ValidEnvironmentProfiles[(i + 1) % ValidEnvironmentProfiles.Length];
            }
        }
        return ValidEnvironmentProfiles[0];
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
            "fxaa" => AntiAliasingPreset.Fxaa,
            "2x" or "2" or "msaa2x" => AntiAliasingPreset.Msaa2x,
            "4x" or "4" or "msaa4x" => AntiAliasingPreset.Msaa4x,
            _ => defaultPreset
        };
    }

    public static int ResolveMsaaSamples(AntiAliasingPreset preset, int windowWidth, int windowHeight, float effectiveRenderScale)
    {
        switch (preset)
        {
            case AntiAliasingPreset.Off:
            case AntiAliasingPreset.Fxaa:
                return 0;
            case AntiAliasingPreset.Msaa2x: return 2;
            case AntiAliasingPreset.Msaa4x: return 4;
            case AntiAliasingPreset.Auto:
            default:
                if (windowWidth <= 0 || windowHeight <= 0) return 2;
                float scale = Math.Clamp(effectiveRenderScale, MinDiagnosticRenderScale, MaxAutoRenderScale);
                float internalPixels = (windowWidth * scale) * (windowHeight * scale);
                return internalPixels > MsaaAutoCutoffPixels ? 0 : 2;
        }
    }

    public static bool ResolveScreenSpaceFxaa(AntiAliasingPreset preset, int resolvedMsaaSamples) =>
        preset == AntiAliasingPreset.Fxaa || (preset == AntiAliasingPreset.Auto && resolvedMsaaSamples == 0);

    public static bool ResolveUseBilinearScaling(int windowWidth, int windowHeight, float effectiveRenderScale)
    {
        if (effectiveRenderScale >= 0.999f) return true;
        long outputPixels = (long)Math.Max(0, windowWidth) * Math.Max(0, windowHeight);
        return outputPixels >= LargeOutputBilinearCutoffPixels;
    }

    public static AmbientOcclusionPreset ParseAmbientOcclusion(string? val, AmbientOcclusionPreset defaultPreset = AmbientOcclusionPreset.Auto)
    {
        if (string.IsNullOrWhiteSpace(val)) return defaultPreset;
        string lower = val.Trim().ToLowerInvariant();
        return lower switch
        {
            "auto" => AmbientOcclusionPreset.Auto,
            "off" or "0" or "false" or "disabled" => AmbientOcclusionPreset.Off,
            "on" or "1" or "true" or "half" or "full" or "enabled" => AmbientOcclusionPreset.On,
            _ => defaultPreset
        };
    }

    public static bool ResolveSsaoEnabled(
        AmbientOcclusionPreset preset,
        EnvironmentQuality quality,
        bool profileSsaoEnabled,
        int windowWidth,
        int windowHeight)
    {
        if (!profileSsaoEnabled || quality == EnvironmentQuality.Low)
        {
            return false;
        }

        if (preset == AmbientOcclusionPreset.Off)
        {
            return false;
        }

        if (preset == AmbientOcclusionPreset.On)
        {
            return true;
        }

        // Auto disables expensive screen-space SSAO on large/HiDPI outputs (>= 4.5M output pixels)
        long outputPixels = (long)Math.Max(0, windowWidth) * Math.Max(0, windowHeight);
        return outputPixels < LargeOutputBilinearCutoffPixels;
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
        AmbientOcclusionPreset ambientOcclusion = result.AmbientOcclusion;

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
            else if (arg.Equals("--ssao", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                ambientOcclusion = ParseAmbientOcclusion(args[i + 1], ambientOcclusion);
                i++;
            }
            else if (arg.StartsWith("--ssao=", StringComparison.OrdinalIgnoreCase))
            {
                ambientOcclusion = ParseAmbientOcclusion(arg["--ssao=".Length..], ambientOcclusion);
            }
        }

        return new DisplaySettings(vsync, maxFps, benchmarkFrames, profile, quality, benchmarkQuit, captureViewsDir, renderScale, antiAliasing, ambientOcclusion);
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
    public static bool HasSsaoOverride(string[]? args) => HasOptionOverride(args, "--ssao");

    public static DisplaySettings ApplyUserEdit(
        DisplaySettings savedConfig,
        bool? newVsync = null,
        int? newMaxFps = null,
        string? newProfile = null,
        EnvironmentQuality? newQuality = null,
        RenderScalePreset? newRenderScale = null,
        AntiAliasingPreset? newAntiAliasing = null,
        AmbientOcclusionPreset? newAmbientOcclusion = null)
    {
        bool vsync = newVsync ?? savedConfig.VSyncEnabled;
        int maxFps = newMaxFps.HasValue ? SanitizeMaxFps(newMaxFps.Value) : savedConfig.MaxFps;
        string profile = newProfile is not null ? SanitizeProfile(newProfile) : savedConfig.EnvironmentProfile;
        EnvironmentQuality quality = newQuality ?? savedConfig.Quality;
        RenderScalePreset renderScale = newRenderScale ?? savedConfig.RenderScale;
        AntiAliasingPreset antiAliasing = newAntiAliasing ?? savedConfig.AntiAliasing;
        AmbientOcclusionPreset ambientOcclusion = newAmbientOcclusion ?? savedConfig.AmbientOcclusion;
        return savedConfig with
        {
            VSyncEnabled = vsync,
            MaxFps = maxFps,
            EnvironmentProfile = profile,
            Quality = quality,
            RenderScale = renderScale,
            AntiAliasing = antiAliasing,
            AmbientOcclusion = ambientOcclusion
        };
    }

    public static BenchmarkRenderOverrides ParseBenchmarkOverrides(string[]? args)
    {
        if (args is null || args.Length == 0) return new BenchmarkRenderOverrides();

        float? renderScaleOverride = null;
        string? scalingMode = null;
        string? ssaoMode = null;
        int? shadowSize = null;
        string? shadowFilter = null;
        bool? glowEnabled = null;
        string? terrainShader = null;
        float warmupSeconds = 0f;
        float benchmarkSeconds = 0f;
        int? screenIndex = null;
        bool benchmarkPan = false;

        static bool TryParseArgValue(string[] all, ref int idx, string prefixEq, string exactName, out string value)
        {
            string cur = all[idx];
            if (cur.Equals(exactName, StringComparison.OrdinalIgnoreCase) && idx + 1 < all.Length)
            {
                value = all[++idx];
                return true;
            }
            if (cur.StartsWith(prefixEq, StringComparison.OrdinalIgnoreCase))
            {
                value = cur[prefixEq.Length..];
                return true;
            }
            value = string.Empty;
            return false;
        }

        const RenderScalePreset UnmatchedPreset = (RenderScalePreset)255;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (TryParseArgValue(args, ref i, "--render-scale=", "--render-scale", out string rsVal))
            {
                if (ParseRenderScale(rsVal, UnmatchedPreset) != UnmatchedPreset)
                {
                    // Presets (including auto, 50, 75, 85, 100) clear any prior numeric override so last CLI arg wins
                    renderScaleOverride = null;
                }
                else if (float.TryParse(rsVal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) &&
                         f >= MinDiagnosticRenderScale && f <= MaxAutoRenderScale)
                {
                    renderScaleOverride = f;
                }
            }
            else if (TryParseArgValue(args, ref i, "--scaling-mode=", "--scaling-mode", out string smVal))
            {
                string norm = smVal.Trim().ToLowerInvariant();
                if (norm is "fsr" or "fsr2" or "bilinear") scalingMode = norm;
                else if (norm == "auto") scalingMode = null;
            }
            else if (TryParseArgValue(args, ref i, "--ssao=", "--ssao", out string ssaoVal))
            {
                string norm = ssaoVal.Trim().ToLowerInvariant();
                if (norm is "off" or "half" or "full") ssaoMode = norm;
                else if (norm is "auto" or "on") ssaoMode = null;
            }
            else if (TryParseArgValue(args, ref i, "--shadow-size=", "--shadow-size", out string szVal))
            {
                if (int.TryParse(szVal, out int sz) && sz >= 512 && sz <= 8192) shadowSize = sz;
            }
            else if (TryParseArgValue(args, ref i, "--shadow-filter=", "--shadow-filter", out string sfVal))
            {
                string norm = sfVal.Trim().ToLowerInvariant();
                if (norm is "hard" or "very_low" or "low" or "medium" or "high") shadowFilter = norm;
            }
            else if (TryParseArgValue(args, ref i, "--glow=", "--glow", out string gVal))
            {
                string norm = gVal.Trim().ToLowerInvariant();
                if (norm is "on" or "true" or "1") glowEnabled = true;
                else if (norm is "off" or "false" or "0") glowEnabled = false;
            }
            else if (TryParseArgValue(args, ref i, "--terrain-shader=", "--terrain-shader", out string tsVal))
            {
                string norm = tsVal.Trim().ToLowerInvariant();
                if (norm is "burley" or "lambert") terrainShader = norm;
            }
            else if (TryParseArgValue(args, ref i, "--warmup-seconds=", "--warmup-seconds", out string wsVal))
            {
                if (float.TryParse(wsVal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float ws) && ws >= 0f)
                {
                    warmupSeconds = ws;
                }
            }
            else if (TryParseArgValue(args, ref i, "--benchmark-seconds=", "--benchmark-seconds", out string bsVal))
            {
                if (float.TryParse(bsVal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float bs) && bs >= 0f)
                {
                    benchmarkSeconds = bs;
                }
            }
            else if (TryParseArgValue(args, ref i, "--screen=", "--screen", out string scVal))
            {
                if (int.TryParse(scVal, out int sc) && sc >= 0) screenIndex = sc;
            }
            else if (arg.Equals("--benchmark-pan", StringComparison.OrdinalIgnoreCase))
            {
                benchmarkPan = true;
            }
        }

        return new BenchmarkRenderOverrides(
            renderScaleOverride,
            scalingMode,
            ssaoMode,
            shadowSize,
            shadowFilter,
            glowEnabled,
            terrainShader,
            warmupSeconds,
            benchmarkSeconds,
            screenIndex,
            benchmarkPan);
    }
}

/// <summary>
/// CLI-only diagnostic overrides for reproducible GPU and AA benchmarking.
/// Not persisted to config or exposed in the UI settings panel.
/// </summary>
public sealed record BenchmarkRenderOverrides(
    float? RenderScaleOverride = null,
    string? ScalingMode = null,
    string? SsaoMode = null,
    int? ShadowSize = null,
    string? ShadowFilter = null,
    bool? GlowEnabled = null,
    string? TerrainShader = null,
    float WarmupSeconds = 0f,
    float BenchmarkSeconds = 0f,
    int? ScreenIndex = null,
    bool BenchmarkPan = false);
